// =============================================================================
// infra/Program.cs — Infraestructura Pulumi para Doctors API en Azure
// =============================================================================
//
// Este archivo define TODA la infraestructura Azure usando Pulumi (IaC).
// Cada sección crea un recurso Azure y documenta QUÉ hace y POR QUÉ.
//
// ╔═══════════════════════════════════════════════════════════════════════════╗
// ║  ARQUITECTURA (orden de creación)                                       ║
// ║                                                                         ║
// ║  1. Resource Group        ← Contenedor lógico de todos los recursos     ║
// ║  2. Log Analytics         ← Centraliza logs de diagnóstico              ║
// ║  3. Container Registry    ← Almacena imágenes Docker (ACR)              ║
// ║  4. SQL Server + DB       ← Base de datos serverless (auto-pausa)       ║
// ║  5. Firewall Rules        ← Controla acceso al SQL Server               ║
// ║  6. Container App Env     ← Entorno donde corren los containers         ║
// ║  7. Key Vault             ← Almacena secrets (RBAC, no AccessPolicies)  ║
// ║  8. KV Secret             ← Connection string de SQL                    ║
// ║  9. Container App         ← Tu API .NET corriendo en containers         ║
// ║ 10. Firewall Rule (CA)    ← Permite al Container App hablar con SQL     ║
// ║ 11. RBAC Role Assignments ← AcrPull + KeyVaultSecretsUser              ║
// ║ 12. Diagnostic Settings   ← Logs de SQL y KV → Log Analytics            ║
// ╚═══════════════════════════════════════════════════════════════════════════╝
//
// COSTO ESTIMADO (ambiente dev):
//   Idle:  ~$10/mes  (SQL serverless pausado, ACR Basic, KV Standard)
//   Activo: ~$15-20/mes (con tráfico moderado)
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
    var env = config.Require("env");                    // "dev" | "prod"
    var sqlAdmin = config.Require("sqlAdmin");           // Usuario admin del SQL Server
    var sqlPassword = config.RequireSecret("sqlPassword"); // Password del SQL (encriptado en state)
    var tenantId = config.Require("tenantId");           // Azure AD Tenant ID
    var location = config.Get("location") ?? "eastus";  // Región Azure (default: eastus)
    var clientConfig = AzureNative.Authorization.GetClientConfig.Invoke();
    var subscriptionId = clientConfig.Apply(c => c.SubscriptionId); // Para RBAC role definitions
    var imageTag = config.Get("imageTag") ?? "latest";  // Tag de la imagen Docker a desplegar

    // =========================================================================
    // TAGS — Identifican recursos para cost tracking y organización
    // =========================================================================
    //
    // Se aplican a TODOS los recursos. Azure Cost Management filtra por tags.
    //
    var tags = new Dictionary<string, string>
    {
        { "environment", "dev" },                        // Ambiente (dev/staging/prod)
        { "project", "azure-minimal-api-net" },          // Nombre del proyecto
        { "managed-by", "pulumi" }                       // Quién gestiona el recurso
    };

    // =========================================================================
    // 1. RESOURCE GROUP — Contenedor lógico de todos los recursos
    // =========================================================================
    //
    // Todos los recursos viven dentro de este grupo. Al borrarlo, se borra todo.
    // Naming: rg-{service}-{env}
    //
    var resourceGroup = new AzureNative.Resources.ResourceGroup($"rg-doctors-api-{env}", new()
    {
        ResourceGroupName = $"rg-doctors-api-{env}",
        Location = location,
        Tags = tags,
    });

    // =========================================================================
    // 2. LOG ANALYTICS WORKSPACE — Centraliza logs de diagnóstico
    // =========================================================================
    //
    // Recibe logs de SQL, Key Vault y Container App.
    // SKU PerGB2018 = pay-per-use (pagas por GB ingerido, ~$2-5/mes en dev).
    //
    var logAnalytics = new AzureNative.OperationalInsights.Workspace($"law-doctors-api-{env}", new()
    {
        WorkspaceName = $"law-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Sku = new AzureNative.OperationalInsights.Inputs.WorkspaceSkuArgs
        {
            Name = AzureNative.OperationalInsights.WorkspaceSkuNameEnum.PerGB2018,
        },
        Tags = tags,
    });

    // =========================================================================
    // 3. CONTAINER REGISTRY (ACR) — Almacena imágenes Docker
    // =========================================================================
    //
    // Tier Basic: ~$5/mes (suficiente para dev, sin vulnerability scanning).
    //
    // AdminUserEnabled: true (temporal — permite pull con username/password).
    // TODO: Cambiar a false después del primer deploy exitoso y confiar en AcrPull role.
    //
    // Convención de nombres: sin guiones (acrdoctorsapidev, no acr-doctors-api-dev)
    //
    var containerRegistry = new AzureNative.ContainerRegistry.Registry($"acrdoctorsapi{env}", new()
    {
        RegistryName = $"acrdoctorsapi{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Sku = new AzureNative.ContainerRegistry.Inputs.SkuArgs
        {
            Name = AzureNative.ContainerRegistry.SkuName.Basic,
        },
        AdminUserEnabled = true,
        Tags = tags,
    });

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

    // =========================================================================
    // 6. SQL DATABASE — Base de datos real (Serverless)
    // =========================================================================
    //
    // Tier: GeneralPurpose Serverless (GP_S_Gen5_2)
    //   - Auto-pausa después de 60 min de inactividad → $0 cuando no hay tráfico
    //   - MinCapacity: 0.5 vCores (mínimo cuando está activo)
    //   - MaxSizeBytes: 2 GB (suficiente para dev/demo)
    //
    // Cuando la API conecta, la DB "despierta" automáticamente (~30 segundos).
    //
    var sqlDatabase = new AzureNative.Sql.Database($"sqldb-doctors-{env}", new()
    {
        DatabaseName = $"sqldb-doctors-{env}",
        ServerName = sqlServer.Name,
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Sku = new AzureNative.Sql.Inputs.SkuArgs
        {
            Name = "GP_S_Gen5_2",
            Tier = "GeneralPurpose",
        },
        AutoPauseDelay = 60,
        MinCapacity = 0.5,
        MaxSizeBytes = 2147483648,
        Tags = tags,
    });

    // =========================================================================
    // 7. CONTAINER APP ENVIRONMENT — Entorno donde corren los containers
    // =========================================================================
    //
    // El CAE agrupa Container Apps y comparte configuración de red y logging.
    //
    // IMPORTANTE: Environment StaticIp (4.242.120.1) es la IP de INGRESS
    // (lo que entra). La IP de OUTBOUND (lo que sale, ej: SQL) es DIFERENTE
    // y se obtiene del Container App.OutboundIpAddresses.
    //
    var containerAppEnvironment = new AzureNative.App.ManagedEnvironment($"cae-doctors-api-{env}", new()
    {
        EnvironmentName = $"cae-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Tags = tags,
    });

    // =========================================================================
    // 8. KEY VAULT — Almacena secrets (RBAC mode)
    // =========================================================================
    //
    // EnableRbacAuthorization = true → usa Azure RBAC en vez de AccessPolicies.
    // Esto permite que el Managed Identity del Container App lea secrets
    // sin necesidad de AccessPolicies manuales.
    //
    // SKU Standard: ~$3/mes, suficiente para dev.
    //
    var keyVault = new AzureNative.KeyVault.Vault($"kv-doctors-api-{env}", new()
    {
        VaultName = $"kv-doctors-api-{env}",
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
        },
        Tags = tags,
    });

    // =========================================================================
    // 9. SQL CONNECTION STRING → KEY VAULT SECRET
    // =========================================================================
    //
    // La connection string se construye con Outputs de Pulumi (sqlServer.Name, etc).
    // Se guarda como secret en Key Vault para centralizar gestión de credenciales.
    //
    // El Container App referencia este secret via SecretRef (no como plaintext env var).
    //
    // Encrypt=True + Trust Server Certificate=False = obligatorio para Azure SQL.
    //
    var sqlConnectionString = Output.Format($"Server=tcp:{sqlServer.Name}.database.windows.net;Database={sqlDatabase.Name};User Id={sqlAdmin};Password={sqlPassword};Encrypt=True;Trust Server Certificate=False;");
    var sqlConnectionStringSecret = new AzureNative.KeyVault.Secret($"sql-conn-{env}", new()
    {
        ResourceGroupName = resourceGroup.Name,
        VaultName = keyVault.Name,
        SecretName = "sql-connection-string",
        Properties = new AzureNative.KeyVault.Inputs.SecretPropertiesArgs
        {
            Value = sqlConnectionString,
        },
    });

    // =========================================================================
    // 10. REGISTRY CREDENTIALS — Credenciales temporales del ACR
    // =========================================================================
    //
    // TEMPORAL: Se usa admin/password del ACR para el primer deploy.
    // Después del primer deploy exitoso, se cambia a Managed Identity (AcrPull).
    //
    // La llamada a ListRegistryCredentials lee las credenciales admin del ACR
    // y las pasa al Container App para poder hacer pull de imágenes.
    //
    var registryCredentials = Output.Tuple(containerRegistry.Name, resourceGroup.Name).Apply(args =>
    {
        var (acrName, rgName) = args;
        var creds = AzureNative.ContainerRegistry.ListRegistryCredentials.Invoke(new()
        {
            RegistryName = acrName,
            ResourceGroupName = rgName,
        });
        return creds;
    });

    // =========================================================================
    // 11. CONTAINER APP — Tu API .NET corriendo en containers
    // =========================================================================
    //
    // Identity: SystemAssigned → Azure crea un Managed Identity automáticamente.
    //   Este identity se usa para:
    //   - Pull de imágenes del ACR (vía AcrPull role)
    //   - Leer secrets del Key Vault (vía KeyVaultSecretsUser role)
    //
    // Ingress: External = true → la API es accesible desde internet.
    //   TargetPort 8080 = puerto interno del container (definido en Dockerfile).
    //
    // Secrets:
    //   - sql-connection-string: connection string (Value directo por ahora)
    //   - registry-password: credencial del ACR (TEMPORAL)
    //
    // Registries: bloque para autenticar con ACR (TEMPORAL — se quita con AcrPull).
    //
    // Env:
    //   - ASPNETCORE_ENVIRONMENT=Development: activa Swagger, errores detallados
    //   - ConnectionStrings__DefaultConnection: referencia al secret del SQL
    //
    // Scale: 1-5 réplicas. En dev, con 1 réplica es suficiente.
    //
    var containerApp = new AzureNative.App.ContainerApp($"ca-doctors-api-{env}", new()
    {
        ContainerAppName = $"ca-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        ManagedEnvironmentId = containerAppEnvironment.Id,
        Identity = new AzureNative.App.Inputs.ManagedServiceIdentityArgs
        {
            Type = "SystemAssigned",
        },
        Configuration = new AzureNative.App.Inputs.ConfigurationArgs
        {
            Ingress = new AzureNative.App.Inputs.IngressArgs
            {
                External = true,
                TargetPort = 8080,
            },
            Secrets = new[]
            {
                new AzureNative.App.Inputs.SecretArgs
                {
                    Name = "sql-connection-string",
                    Value = sqlConnectionString,
                },
                // TODO: Quitar registry-password después de cambiar a AcrPull
                new AzureNative.App.Inputs.SecretArgs
                {
                    Name = "registry-password",
                    Value = registryCredentials.Apply(c => c.Passwords[0].Value!),
                },
            },
            // TODO: Quitar bloque Registries después de cambiar a AcrPull
            Registries = new[]
            {
                new AzureNative.App.Inputs.RegistryCredentialsArgs
                {
                    Server = Output.Format($"{containerRegistry.Name}.azurecr.io"),
                    Username = registryCredentials.Apply(c => c.Username!),
                    PasswordSecretRef = "registry-password",
                },
            },
        },
        Template = new AzureNative.App.Inputs.TemplateArgs
        {
            Containers = new[]
            {
                new AzureNative.App.Inputs.ContainerArgs
                {
                    Name = "doctors-api",
                    // Imagen: {acr}.azurecr.io/doctors-api:{tag}
                    // El tag viene de Pulumi config (default: "latest")
                    Image = Output.Format($"{containerRegistry.Name}.azurecr.io/doctors-api:{imageTag}"),
                    Resources = new AzureNative.App.Inputs.ContainerResourcesArgs
                    {
                        Cpu = 0.25,      // 0.25 vCPU — mínimo para Container Apps
                        Memory = "0.5Gi", // 512 MB — suficiente para .NET 10 minimal API
                    },
                    Env = new[]
                    {
                        new AzureNative.App.Inputs.EnvironmentVarArgs
                        {
                            Name = "ASPNETCORE_ENVIRONMENT",
                            Value = "Development",
                        },
                        new AzureNative.App.Inputs.EnvironmentVarArgs
                        {
                            Name = "ConnectionStrings__DefaultConnection",
                            // SecretRef referencia el secret definido arriba.
                            // El valor real viene del secret "sql-connection-string",
                            // no aparece como plaintext en variables de entorno.
                            SecretRef = "sql-connection-string",
                        },
                    },
                },
            },
            Scale = new AzureNative.App.Inputs.ScaleArgs
            {
                MinReplicas = 1,
                MaxReplicas = 5,
            },
        },
        Tags = tags,
    });

    // =========================================================================
    // 12. FIREWALL RULE: AllowContainerApp — IP outbound del Container App
    // =========================================================================
    //
    // PROBLEMA: El alias "AllowAzureServices" (0.0.0.0) NO cubre Container Apps.
    // Container Apps usa IPs outbound dinámicas que son DIFERENTES a la IP
    // estática del Environment (que es solo para ingress).
    //
    // SOLUCIÓN: Crear un firewall rule con la IP outbound real del Container App.
    // containerApp.OutboundIpAddresses es un array de IPs (usualmente 1).
    //
    // NOTA: Este firewall rule DEBE ir DESPUÉS del Container App porque depende
    // de containerApp.OutboundIpAddresses (output del Container App).
    //
    // Para producción: usar VNet + Private Endpoint en vez de firewall rules.
    //
    var caFirewallRule = new AzureNative.Sql.FirewallRule($"fw-allow-ca-{env}", new()
    {
        FirewallRuleName = "AllowContainerApp",
        ServerName = sqlServer.Name,
        ResourceGroupName = resourceGroup.Name,
        StartIpAddress = containerApp.OutboundIpAddresses.Apply(ips => ips[0]),
        EndIpAddress = containerApp.OutboundIpAddresses.Apply(ips => ips[0]),
    });

    // =========================================================================
    // 13. RBAC ROLE ASSIGNMENTS — Permisos del Managed Identity
    // =========================================================================
    //
    // AcrPull (7f951dda-4ed3-4680-a7ca-43fe172d538d):
    //   Permite al Container App hacer pull de imágenes del ACR sin admin credentials.
    //
    // KeyVaultSecretsUser (4633458b-17de-408a-b874-0445c86b69e6):
    //   Permite al Container App leer secrets del Key Vault.
    //
    // PrincipalType = "ServicePrincipal" porque Managed Identities se tratan
    // como Service Principals internamente en Azure RBAC.
    //
    var acrPullRole = new AzureNative.Authorization.RoleAssignment($"acrpull-{env}", new()
    {
        PrincipalId = containerApp.Identity.Apply(i => i!.PrincipalId!),
        PrincipalType = "ServicePrincipal",
        RoleDefinitionId = Output.Format($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d"),
        Scope = containerRegistry.Id,
    });

    var kvSecretsUserRole = new AzureNative.Authorization.RoleAssignment($"kvsecretsuser-{env}", new()
    {
        PrincipalId = containerApp.Identity.Apply(i => i!.PrincipalId!),
        PrincipalType = "ServicePrincipal",
        RoleDefinitionId = Output.Format($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6"),
        Scope = keyVault.Id,
    });

    // =========================================================================
    // 14. DIAGNOSTIC SETTINGS — Logs → Log Analytics
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
    var sqlDiag = new AzureNative.Insights.DiagnosticSetting($"diag-sql-{env}", new()
    {
        ResourceUri = sqlDatabase.Id,
        WorkspaceId = logAnalytics.Id,
        Logs = new[]
        {
            new AzureNative.Insights.Inputs.LogSettingsArgs { Enabled = true, Category = "SQLInsights" },
            new AzureNative.Insights.Inputs.LogSettingsArgs { Enabled = true, Category = "AutomaticTuning" },
            new AzureNative.Insights.Inputs.LogSettingsArgs { Enabled = true, Category = "QueryStoreRuntimeStatistics" },
            new AzureNative.Insights.Inputs.LogSettingsArgs { Enabled = true, Category = "QueryStoreWaitStatistics" },
            new AzureNative.Insights.Inputs.LogSettingsArgs { Enabled = true, Category = "Errors" },
        },
        Metrics = new[]
        {
            new AzureNative.Insights.Inputs.MetricSettingsArgs { Enabled = true, Category = "AllMetrics" },
        },
    });

    var kvDiag = new AzureNative.Insights.DiagnosticSetting($"diag-kv-{env}", new()
    {
        ResourceUri = keyVault.Id,
        WorkspaceId = logAnalytics.Id,
        Logs = new[]
        {
            new AzureNative.Insights.Inputs.LogSettingsArgs { Enabled = true, Category = "AuditEvent" },
            new AzureNative.Insights.Inputs.LogSettingsArgs { Enabled = true, Category = "AzurePolicyEvaluationDetails" },
        },
        Metrics = new[]
        {
            new AzureNative.Insights.Inputs.MetricSettingsArgs { Enabled = true, Category = "AllMetrics" },
        },
    });

    // =========================================================================
    // OUTPUTS — Valores que devuelve el stack después del deploy
    // =========================================================================
    //
    // pulumi stack output → muestra estos valores
    //
    return new Dictionary<string, object?>
    {
        ["resourceGroupName"] = resourceGroup.Name,
        ["containerAppPrincipalId"] = containerApp.Identity.Apply(i => i!.PrincipalId!),
        ["containerAppUrl"] = Output.Format($"https://{containerApp.LatestRevisionFqdn}"),
    };
});
