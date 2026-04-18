// =============================================================================
// infra/Program.cs — Infraestructura Pulumi para Doctors API en Azure
// =============================================================================
//
// Este archivo define TODA la infraestructura Azure usando Pulumi (IaC).
// Cada sección crea un recurso Azure y documenta QUÉ hace y POR QUÉ.
//
// ╔═══════════════════════════════════════════════════════════════════════════╗
// ║  ARQUITECTURA (orden de creación)                                       ║
// ║  TWO-TIER MULTI-STAGE DESIGN:                                           ║
// ║    infra-shared (main) → ACR + LAW + CAE + RG (shared across envs)      ║
// ║    infra-{env}         → RG, SQL, KV, ContainerApp (per env)            ║
// ║                                                                         ║
// ║  1. Resource Group (per-env)                                            ║
// ║  2. SQL Server + DB (serverless + auto-pause 60min for non-prod)        ║
// ║  5. Firewall Rules        ← Controla acceso al SQL Server               ║
// ║  6. Container App Env     ← Entorno donde corren los containers         ║
// ║  7. Key Vault             ← Almacena secrets (RBAC, no AccessPolicies)  ║
// ║  8. KV Secret: SQL Conn   ← Connection string (nombre dinámico)         ║
// ║  9. KV Secret: JWT Key    ← Signing key para tokens                     ║
// ║ 10. Container App         ← Tu API .NET corriendo en containers         ║
// ║ 11. RBAC AcrPull          ← Container App → ACR (sin admin creds)      ║
// ║ 12. RBAC KV Secrets User  ← Container App → Key Vault                  ║
// ║ 13. KV Admin Role         ← Usuario Pulumi → Key Vault                  ║
// ║ 14. Firewall Rule (CA)    ← Permite al Container App hablar con SQL     ║
// ║ 15. Diagnostic Settings   ← Logs de SQL y KV → Log Analytics            ║
// ╚═══════════════════════════════════════════════════════════════════════════╝
//
// COSTO ESTIMADO (multi-stage):
//   Shared (always on): ~$15-20/mes (ACR Basic + LAW + CAE)
//   Per-env dev/qa:     ~$8-15/mes (SQL serverless 60min auto-pause)
//   Prod:               ~$25+/mes (no auto-pause)
//   Total idle:         ~$20-35/mes
//
// SEGURIDAD:
//   - Managed Identity en Container App (sin passwords hardcodeadas)
//   - AcrPull role para pull de imágenes (RBAC, no admin credentials)
//   - Key Vault con RBAC habilitado (no AccessPolicies legacy)
//   - Connection string como secret en Container App
//   - DiagnosticSettings para auditoría de SQL y Key Vault
//   - Tags consistentes para cost tracking
//
using Pulumi;
using AzureNative = Pulumi.AzureNative;

return await Pulumi.Deployment.RunAsync(() =>
{
    // =========================================================================
    // CONFIGURACIÓN — Lee valores del stack (Pulumi.dev.yaml / Pulumi.prod.yaml)
    // =========================================================================
    //
    // Pulumi config set env dev
    // Pulumi config set sqlAdmin doctorsadmin
    // Pulumi config set sqlPassword --secret "TuPassword123!"
    // Pulumi config set tenantId "tu-tenant-id-de-azure"
    // Pulumi config set location westus2
    // Pulumi config set imageTag dev-0.1.0
    //
    var config = new Pulumi.Config();
    var env = config.Require("env");                    // "dev" | "qa" | "prod"
    var sqlAdmin = config.Require("sqlAdmin");           // Usuario admin del SQL Server
    var sqlPassword = config.RequireSecret("sqlPassword"); // Password del SQL (encriptado en state)
    var tenantId = config.Require("tenantId");           // Azure AD Tenant ID
    var location = config.Get("location") ?? "eastus";  // Región Azure (default: eastus)
    var costMode = config.Get("costMode") ?? "nano";    // Modo de costo: nano, mini, normal, full

    // =========================================================================
    // SHARED INFRA REFERENCE (StackReference to infra-shared/main)
    // =========================================================================
    // This is the core of the multi-stage strategy: shared ACR + LAW.
    var sharedRef = new StackReference("organization/azure-minimal-api-net-shared/main");
    var acrName = sharedRef.GetOutput("acrName");
    var acrId = sharedRef.GetOutput("acrId").Apply(x => x!.ToString()!);
    var acrLoginServer = sharedRef.GetOutput("acrLoginServer");
    var lawWorkspaceId = sharedRef.GetOutput("logAnalyticsWorkspaceId").Apply(x => x!.ToString()!);
    var containerAppEnvironmentId = sharedRef.GetOutput("caeId").Apply(x => x!.ToString()!);

    var clientConfig = AzureNative.Authorization.GetClientConfig.Invoke();
    var subscriptionId = clientConfig.Apply(c => c.SubscriptionId); // Para RBAC role definitions

    // =========================================================================
    // TAGS — Identifican recursos para cost tracking y organización
    // =========================================================================
    //
    // Se aplican a TODOS los recursos. Azure Cost Management filtra por tags.
    //
    var tags = new Dictionary<string, string>
    {
        { "environment", env },
        { "project", "azure-minimal-api-net" },
        { "managed-by", "pulumi-user" }
    };

    // =========================================================================
    // COST CONFIG — Configuración de costos basada en costMode
    // =========================================================================
    //
    // Modos disponibles:
    //   nano:   Costos ultra-bajos. Para testing máximo. ~$3-5/mes
    //   mini:   Costos mínimos para dev activo. ~$5-8/mes
    //   normal: Configuración balanced. ~$10-15/mes
    //   full:   Máxima capacidad para producción. ~$25-40/mes
    //
    // Para cambiar el modo: pulumi config set costMode nano --cwd infra
    //
    // Parámetros por modo:
    //
    // | Modo    | CA Min | CA Max | CPU    | RAM     | Diag | SQL SKU        | DB Size |
    // |---------|--------|--------|--------|---------|------|----------------|---------|
    // | nano    | 0      | 1      | 0.25   | 0.5Gi   | No   | GP_S_Gen5_1    | 1 GB    |
    // | mini    | 0      | 2      | 0.25   | 0.5Gi   | No   | GP_S_Gen5_1    | 2 GB    |
    // | normal  | 1      | 5      | 0.25   | 0.5Gi   | Sí   | GP_S_Gen5_2    | 2 GB    |
    // | full    | 2      | 10     | 0.5    | 1Gi     | Sí   | GP_S_Gen5_2    | 5 GB    |
    //
    // Detalle de unidades:
    //   - CPU: vCores (0.25 = 1/4 de vCore, mínimo para Container Apps)
    //   - RAM: Gi = Gibibytes (0.5Gi ≈ 537 MB, 1Gi ≈ 1.07 GB)
    //   - DB Size: GB = Gigabytes (1 GB = 1,073,741,824 bytes)
    //   - CA Min/Max: réplicas del Container App (0 = scale-to-zero cuando no hay tráfico)
    //   - SQL SKU: GP_S_Gen5 = General Purpose Serverless, Gen5 hardware
    //     El número al final (1, 2) son los vCores máximos del tier
    //
    string sqlTier;
    long sqlMaxSize;
    int caMinReplicas;
    int caMaxReplicas;
    double caCpu;
    string caMemory;
    bool enableDiagnostics;
    const long GB = 1073741824L;

    (caMinReplicas, caMaxReplicas, caCpu, caMemory, enableDiagnostics, sqlTier, sqlMaxSize) = costMode switch
    {
        "nano"   => (0, 1, 0.25, "0.5Gi", false, "GP_S_Gen5_1", 1L * GB),           // Min válido: 0.25 CPU / 0.5Gi
        "mini"   => (0, 2, 0.25, "0.5Gi", false, "GP_S_Gen5_1", 2L * GB),           // Mismo que nano con más DB
        "normal" => (1, 5, 0.25, "0.5Gi", true,  "GP_S_Gen5_2", 2L * GB),
        "full"   => (2, 10, 0.5, "1Gi",   true,  "GP_S_Gen5_2", 5L * GB),
        _        => (0, 1, 0.25, "0.5Gi", false, "GP_S_Gen5_1", 1L * GB),           // Default: nano
    };

    // Mostrar configuración de costo en consola
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  COST MODE: {costMode.ToUpper()}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  Container App  → MinReplicas: {caMinReplicas}, MaxReplicas: {caMaxReplicas}, CPU: {caCpu}, Memory: {caMemory}");
    Console.WriteLine($"  SQL Database   → SKU: {sqlTier}, MaxSize: {sqlMaxSize / GB}GB");
    Console.WriteLine($"  Diagnostics    → {(enableDiagnostics ? "✅ Enabled" : "❌ Disabled")}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════════");
    Console.WriteLine();

    // SQL Serverless config (siempre optimizado para costos)
    // Per spec: 60 minutes for non-prod (dev, qa). Prod stays always on (null = no auto-pause).
    var sqlAutoPauseDelay = env == "prod" ? (int?)null : 60;
    

    // =========================================================================
    // 1. RESOURCE GROUP — Contenedor lógico de todos los recursos
    // =========================================================================
    //
    // Todos los recursos viven dentro de este grupo. Al borrarlo, se borra todo.
    // Naming: rg-{service}-{env}
    //
    var resourceGroup = new AzureNative.Resources.ResourceGroup($"rg-doctors-{env}", new()
    {
        ResourceGroupName = $"rg-doctors-{env}",
        Location = location,
        Tags = tags,
    });

    // Note: LOG ANALYTICS and CONTAINER REGISTRY are now in infra-shared.
    // They are referenced via StackReference (see top of file).
    //


    // =========================================================================
    // 4. SQL SERVER — Instancia que aloja las bases de datos
    // =========================================================================
    //
    // ADVERTENCIA: SQL Server != Base de datos.
    // Jerarquía: SQL Server → Database → ConnectionString
    //
    // El Server es el "host". La Database vive DENTRO del Server.
    // La ConnectionString apunta al Server y selecciona la Database.
    //
    var sqlServer = new AzureNative.Sql.Server($"sql-doctors-api-{env}", new()
    {
        ServerName = $"sql-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        AdministratorLogin = sqlAdmin,
        AdministratorLoginPassword = sqlPassword,
        Version = "12.0",
        Tags = tags,
    });

    // =========================================================================
    // 5. FIREWALL RULE: AllowAzureServices — Acceso desde servicios Azure
    // =========================================================================
    //
    // 0.0.0.0 → 0.0.0.0 = alias de Azure que permite servicios Azure internos.
    //
    // ADVERTENCIA: Este alias NO cubre Container Apps.
    // Container Apps usa IPs outbound dinámicas diferentes.
    // Por eso necesitamos el segundo firewall rule (AllowContainerApp) más abajo.
    //
    // DEV: Se mantiene por si acaso. Para producción, quitar y usar VNet + Private Endpoint.
    //
    var firewallRule = new AzureNative.Sql.FirewallRule($"fw-allow-azure-{env}", new()
    {
        FirewallRuleName = "AllowAzureServices",
        ServerName = sqlServer.Name,
        ResourceGroupName = resourceGroup.Name,
        StartIpAddress = "0.0.0.0",
        EndIpAddress = "0.0.0.0",
    });

    
    var sqlDatabase = new AzureNative.Sql.Database($"sqldb-doctors-{env}", new()
    {
        DatabaseName = $"sqldb-doctors-{env}",
        ServerName = sqlServer.Name,
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Sku = new AzureNative.Sql.Inputs.SkuArgs
        {
            Name = sqlTier,
            Tier = "GeneralPurpose",
        },
        AutoPauseDelay = sqlAutoPauseDelay,
        MinCapacity = 0.5,
        MaxSizeBytes = sqlMaxSize,
        Tags = tags,
    });


    // =========================================================================
    // 7. CONTAINER APP ENVIRONMENT — Referencia al CAE compartido
    // =========================================================================
    //
    // Compartimos un solo Container App Environment (cae-core-main) desde
    // el stack infra-shared. Esto optimiza recursos y nos evita el límite
    // de 1 CAE por suscripción que tienen algunas regiones de Azure.
    //
    // El ID de este CAE ya se obtuvo de sharedRef al inicio del script.
    //

    // =========================================================================
    // 8. KEY VAULT — Almacena secrets (RBAC mode)
    // =========================================================================
    //
    // EnableRbacAuthorization = true → usa Azure RBAC en vez de AccessPolicies.
    // Esto permite que el Managed Identity del Container App lea secrets
    // sin necesidad de AccessPolicies manuales.
    //
    // PROBLEMA: Key Vault tiene soft-delete habilitado por defecto (no se puede
    // desactivar). Al hacer `pulumi destroy && pulumi up`, el vault anterior
    // sigue como "soft-deleted" y el recreate falla con 409 Conflict.
    //
    // SOLUCIÓN: Un LocalCommand que purga el vault soft-deleted ANTES de crearlo.
    //   - Primero busca si existe en soft-deleted state
    //   - Si existe → lo purga (eliminación permanente)
    //   - Si no existe → continúa sin error (idempotente)
    //
    // SKU Standard: ~$3/mes, suficiente para dev.
    //
    // NOTA: El purge del Key Vault soft-deleted se maneja en destroy-all.ps1
    // para evitar errores 409 al recrear el vault.
    //
    var vaultName = $"kv-doctors-api-{env}";

    var keyVault = new AzureNative.KeyVault.Vault($"kv-doctors-api-{env}", new()
    {
        VaultName = vaultName,
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Properties = new AzureNative.KeyVault.Inputs.VaultPropertiesArgs
        {
            TenantId = tenantId,
            Sku = new AzureNative.KeyVault.Inputs.SkuArgs
            {
                Family = AzureNative.KeyVault.SkuFamily.A,
                Name = AzureNative.KeyVault.SkuName.Standard,
            },
            EnableRbacAuthorization = true,
            EnableSoftDelete = true,
        },
        Tags = tags,
    });

    // =========================================================================
    // 9. SQL CONNECTION STRING → KEY VAULT SECRET (nombre dinámico)
    // =========================================================================
    //
    // La connection string se construye con Outputs de Pulumi (sqlServer.Name, etc).
    // Se guarda como secret en Key Vault para centralizar gestión de credenciales.
    //
    // NOTA SOBRE DUPLICACIÓN: La connection string existe en DOS lugares:
    //   1. Key Vault (este secret) → centralized management + audit via DiagnosticSettings
    //   2. Container App secret (más abajo) → runtime delivery al container via SecretRef
    //
    // Esto es intencional: el container lee de env vars (no de KV directamente).
    // Para eliminar la duplicación, la app debería usar Azure.Identity + Key Vault SDK
    // en runtime (cambio de código, fuera del scope de infra).
    //
    // Encrypt=True + Trust Server Certificate=False = obligatorio para Azure SQL.
    //
    var sqlConnectionString = Output.Format($"Server=tcp:{sqlServer.Name}.database.windows.net;Database={sqlDatabase.Name};User Id={sqlAdmin};Password={sqlPassword};Encrypt=True;Trust Server Certificate=False;");
    var sqlSecretName = Output.Format($"sql-conn-{env}-{location}");
    var sqlConnectionStringSecret = new AzureNative.KeyVault.Secret($"sql-conn-{env}", new()
    {
        ResourceGroupName = resourceGroup.Name,
        VaultName = keyVault.Name,
        SecretName = sqlSecretName,
        Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
        {
            Value = sqlConnectionString,
        },
    });

    // =========================================================================
    // 10. JWT SIGNING KEY → KEY VAULT SECRET
    // =========================================================================
    //
    // The signing key for JWT token validation in production.
    // In dev, the key comes from appsettings.Development.json.
    //
    var jwtSigningKeySecret = new AzureNative.KeyVault.Secret($"jwt-signing-key-{env}", new()
    {
        ResourceGroupName = resourceGroup.Name,
        VaultName = keyVault.Name,
        SecretName = $"jwt-signing-key-{env}",
        Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
        {
            Value = config.RequireSecret("jwtSigningKey"),
        },
    });

    // =========================================================================
    // 11. CONTAINER APP — Tu API .NET corriendo en containers
    // =========================================================================
    //
    // PROBLEMA ORIGINAL: La ContainerApp se creaba via deploy.ps1 porque en el
    // primer pulumi up la imagen Docker aún no existe en el ACR.
    //
    // SOLUCIÓN: Se crea con una imagen placeholder pública (siempre disponible)
    // y MinReplicas=0. deploy.ps1 luego actualiza la imagen tras el build+push.
    //
    // Los secrets (connection string, JWT key) se almacenan como Container App secrets
    // para que el container los lea via env vars con SecretRef.
    // Estos mismos valores también existen en Key Vault (sección 9/9b) para
    // centralized management y audit. Ver nota de duplicación en sección 9.
    // IgnoreChanges en la imagen → Pulumi no revierte los updates de deploy.ps1.
    //
    var containerApp = new AzureNative.App.ContainerApp($"ca-doctors-api-{env}", new()
    {
        ContainerAppName = $"ca-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        EnvironmentId = containerAppEnvironmentId,
        Identity = new AzureNative.App.Inputs.ManagedServiceIdentityArgs
        {
            Type = AzureNative.App.ManagedServiceIdentityType.SystemAssigned,
        },
        Configuration = new AzureNative.App.Inputs.ConfigurationArgs
        {
            Secrets = new[]
            {
                new AzureNative.App.Inputs.SecretArgs
                {
                    Name = "sql-connection-string",
                    Value = sqlConnectionString,
                },
                new AzureNative.App.Inputs.SecretArgs
                {
                    Name = "jwt-signing-key",
                    Value = config.RequireSecret("jwtSigningKey"),
                },
            },
            Ingress = new AzureNative.App.Inputs.IngressArgs
            {
                External = true,
                TargetPort = 8080,
                Transport = "auto",
            },
        },
        Template = new AzureNative.App.Inputs.TemplateArgs
        {
            Containers = new[]
            {
                new AzureNative.App.Inputs.ContainerArgs
                {
                    Name = "doctors-api",
                    Image = "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest",
                    Env = new[]
                    {
                        new AzureNative.App.Inputs.EnvironmentVarArgs { Name = "ASPNETCORE_ENVIRONMENT", Value = (env == "qa" || env == "prod") ? "Production" : "Development" },
                        new AzureNative.App.Inputs.EnvironmentVarArgs { Name = "ConnectionStrings__DefaultConnection", SecretRef = "sql-connection-string" },
                        new AzureNative.App.Inputs.EnvironmentVarArgs { Name = "JwtSettings__SigningKey", SecretRef = "jwt-signing-key" },
                    },
                    Resources = new AzureNative.App.Inputs.ContainerResourcesArgs
                    {
                        Cpu = caCpu,
                        Memory = caMemory,
                    },
                },
            },
            Scale = new AzureNative.App.Inputs.ScaleArgs
            {
                MinReplicas = caMinReplicas,
                MaxReplicas = caMaxReplicas,
            },
        },
    }, new CustomResourceOptions
    {
        IgnoreChanges = { "template.containers[0].image" },
        DependsOn = { sqlConnectionStringSecret, jwtSigningKeySecret },
    });

    // =========================================================================
    // 12. RBAC: AcrPull para Container App → ACR
    // =========================================================================
    //
    // Sin este rol, el Container App no puede hacer pull de imágenes del ACR
    // (necesario porque AdminUserEnabled = false).
    //
    var acrPullRole = new AzureNative.Authorization.RoleAssignment($"acr-pull-{env}", new()
    {
        PrincipalId = containerApp.Identity.Apply(i => i!.PrincipalId),
        // "ServicePrincipal" es correcto para SystemAssigned Managed Identity en Azure RBAC
        PrincipalType = "ServicePrincipal",
        RoleDefinitionId = Output.Format($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d"),
        Scope = acrId,  // Now from shared stack (full resource ID required for RBAC)
    });

    // =========================================================================
    // 13. RBAC: KeyVaultSecretsUser para Container App → Key Vault
    // =========================================================================
    //
    // Permite que el Container App lea secrets del Key Vault usando su
    // Managed Identity (sin AccessPolicies legacy).
    //
    var kvSecretsUser = new AzureNative.Authorization.RoleAssignment($"kv-user-{env}", new()
    {
        PrincipalId = containerApp.Identity.Apply(i => i!.PrincipalId),
        // "ServicePrincipal" es correcto para SystemAssigned Managed Identity en Azure RBAC
        PrincipalType = "ServicePrincipal",
        RoleDefinitionId = Output.Format($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6"),
        Scope = keyVault.Id,
    });

    // =========================================================================
    // 14. RBAC: Key Vault Secrets Officer para el usuario que corre Pulumi
    // =========================================================================
    //
    // Sin este rol, el usuario no puede crear/borrar secrets en el Key Vault
    // (porque EnableRbacAuthorization = true).
    // Esto permite que Pulumi gestione secrets sin errores 403 Forbidden.
    //
    var kvSecretsOfficer = new AzureNative.Authorization.RoleAssignment($"kvadmin-{env}", new()
    {
        PrincipalId = clientConfig.Apply(c => c.ObjectId),
        PrincipalType = "User",
        RoleDefinitionId = Output.Format($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/b86a8fe4-44ce-4948-aee5-eccb2c155cd7"),
        Scope = keyVault.Id,
    });

    // =========================================================================
    // 15. FIREWALL RULE: AllowContainerApp — IP outbound del Container App
    // =========================================================================
    //
    // El alias "AllowAzureServices" (0.0.0.0) NO cubre Container Apps.
    // Se crea una regla con la IP outbound real del Container App.
    //
    // Usa recurso nativo Pulumi (no LocalCommand) para:
    //   - Ser cross-platform (no depende de PowerShell)
    //   - Evitar injection risk con string interpolation
    //   - Integrarse con el lifecycle de Pulumi (create/update/delete automático)
    //
    var containerAppOutboundIp = containerApp.OutboundIpAddresses.Apply(ips =>
    {
        return ips.IsDefaultOrEmpty ? "0.0.0.0" : ips[0];
    });

    var containerAppFirewallRule = new AzureNative.Sql.FirewallRule($"fw-ca-ip-{env}", new()
    {
        FirewallRuleName = "AllowContainerApp",
        ServerName = sqlServer.Name,
        ResourceGroupName = resourceGroup.Name,
        StartIpAddress = containerAppOutboundIp,
        EndIpAddress = containerAppOutboundIp,
    });

    // =========================================================================
    // 16. DIAGNOSTIC SETTINGS — Logs → Log Analytics (solo si enableDiagnostics=true)
    // =========================================================================
    //
    // SQL Database:
    //   Los categories de logs están a nivel DATABASE, no a nivel SERVER.
    //   Categorías: SQLInsights, AutomaticTuning, QueryStore*,
    //               Errors, DatabaseWaitStatistics, Timeouts, Blocks, Deadlocks
    //
    // Key Vault:
    //   AuditEvent = log de operaciones de secrets (get, set, delete).
    //
    // Los logs llegan al Log Analytics workspace y se pueden consultar con KQL.
    //
    if (enableDiagnostics)
    {
        var sqlDiag = new AzureNative.Monitor.DiagnosticSetting($"diag-sql-{env}", new()
        {
            // Nombre explícito → evita que Azure genere un nombre con sufijo aleatorio
            // que causa conflictos 409 al recrear la infra
            Name = $"diag-sql-{env}",
            ResourceUri = sqlDatabase.Id,
            WorkspaceId = lawWorkspaceId,
            Logs = new[]
            {
                new AzureNative.Monitor.Inputs.LogSettingsArgs { Enabled = true, Category = "SQLInsights" },
                new AzureNative.Monitor.Inputs.LogSettingsArgs { Enabled = true, Category = "AutomaticTuning" },
                new AzureNative.Monitor.Inputs.LogSettingsArgs { Enabled = true, Category = "QueryStoreRuntimeStatistics" },
                new AzureNative.Monitor.Inputs.LogSettingsArgs { Enabled = true, Category = "QueryStoreWaitStatistics" },
                new AzureNative.Monitor.Inputs.LogSettingsArgs { Enabled = true, Category = "Errors" },
            },
            Metrics = new[]
            {
                new AzureNative.Monitor.Inputs.MetricSettingsArgs { Enabled = true, Category = "AllMetrics" },
            },
        });

        var kvDiag = new AzureNative.Monitor.DiagnosticSetting($"diag-kv-{env}", new()
        {
            Name = $"diag-kv-{env}",
            ResourceUri = keyVault.Id,
            WorkspaceId = lawWorkspaceId,
            Logs = new[]
            {
                new AzureNative.Monitor.Inputs.LogSettingsArgs { Enabled = true, Category = "AuditEvent" },
                new AzureNative.Monitor.Inputs.LogSettingsArgs { Enabled = true, Category = "AzurePolicyEvaluationDetails" },
            },
            Metrics = new[]
            {
                new AzureNative.Monitor.Inputs.MetricSettingsArgs { Enabled = true, Category = "AllMetrics" },
            },
        });
    }

    // =========================================================================
    // OUTPUTS — Valores que devuelve el stack después del deploy
    // =========================================================================
    //
    // pulumi stack output → muestra estos valores
    //
    return new Dictionary<string, object?>
    {
        ["resourceGroupName"] = resourceGroup.Name,
        ["containerAppName"] = containerApp.Name,
        // acrName and others now come from shared stack via StackReference
    };
});
