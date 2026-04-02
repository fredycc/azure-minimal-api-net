using Pulumi;
using AzureNative = Pulumi.AzureNative;

return await Pulumi.Deployment.RunAsync(() =>
{
    var config = new Pulumi.Config();
    var env = config.Require("env");
    var sqlAdmin = config.Require("sqlAdmin");
    var sqlPassword = config.RequireSecret("sqlPassword");
    var tenantId = config.Require("tenantId");
    var location = config.Get("location") ?? "eastus";

    // Resource Group
    var resourceGroup = new AzureNative.Resources.ResourceGroup($"rg-doctors-api-{env}", new()
    {
        ResourceGroupName = $"rg-doctors-api-{env}",
        Location = location,
    });

    // Log Analytics Workspace
    var logAnalytics = new AzureNative.OperationalInsights.Workspace($"law-doctors-api-{env}", new()
    {
        WorkspaceName = $"law-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Sku = new AzureNative.OperationalInsights.Inputs.WorkspaceSkuArgs
        {
            Name = AzureNative.OperationalInsights.WorkspaceSkuNameEnum.PerGB2018,
        },
    });

    // Container Registry
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
    });

    // SQL Server — instancia que aloja las bases de datos (NO es una BD por sí mismo)
    // Jerarquía: Server → Database → ConnectionString en el Container App
    var sqlServer = new AzureNative.Sql.Server($"sql-doctors-api-{env}", new()
    {
        ServerName = $"sql-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        AdministratorLogin = sqlAdmin,
        AdministratorLoginPassword = sqlPassword,
        Version = "12.0",
    });

    // Allow Azure services (Container App) to access SQL Server
    var firewallRule = new AzureNative.Sql.FirewallRule($"fw-allow-azure-{env}", new()
    {
        FirewallRuleName = "AllowAzureServices",
        ServerName = sqlServer.Name,
        ResourceGroupName = resourceGroup.Name,
        StartIpAddress = "0.0.0.0",
        EndIpAddress = "0.0.0.0",
    });

    // SQL Database — base de datos real, vive DENTRO del sqlServer creado arriba.
    // ServerName = sqlServer.Name vincula esta BD al servidor. Una sola BD en un solo servidor.
    // Tier Serverless: auto-pausa después de 60min idle → $0 cuando no hay tráfico
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
        AutoPauseDelay = 60,        // Pausa después de 60 min de inactividad
        MinCapacity = 0.5,          // Mínimo 0.5 vCores cuando activo
        MaxSizeBytes = 2147483648,  // 2 GB — suficiente para dev/demo
    });

    // Container App Environment
    var containerAppEnvironment = new AzureNative.App.ManagedEnvironment($"cae-doctors-api-{env}", new()
    {
        EnvironmentName = $"cae-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        AppLogsConfiguration = new AzureNative.App.Inputs.AppLogsConfigurationArgs
        {
            Destination = "log-analytics",
            LogAnalyticsConfiguration = new AzureNative.App.Inputs.LogAnalyticsConfigurationArgs
            {
                CustomerId = logAnalytics.CustomerId,
                SharedKey = Output.Tuple(resourceGroup.Name, logAnalytics.Name).Apply(args =>
                {
                    var (rgName, wsName) = args;
                    var keys = AzureNative.OperationalInsights.GetSharedKeys.Invoke(new()
                    {
                        ResourceGroupName = rgName,
                        WorkspaceName = wsName,
                    });
                    return keys.Apply(k => k.PrimarySharedKey!);
                }),
            },
        },
    });

    // Key Vault
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
            AccessPolicies = new[]
            {
                new AzureNative.KeyVault.Inputs.AccessPolicyEntryArgs
                {
                    TenantId = tenantId,
                    ObjectId = Output.Create("00000000-0000-0000-0000-000000000000"), // Replace with managed identity
                    Permissions = new AzureNative.KeyVault.Inputs.PermissionsArgs
                    {
                        Secrets =
                        {
                            AzureNative.KeyVault.SecretPermissions.Get,
                            AzureNative.KeyVault.SecretPermissions.Set,
                        },
                    },
                },
            },
        },
    });

    // Container App
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

    var containerApp = new AzureNative.App.ContainerApp($"ca-doctors-api-{env}", new()
    {
        ContainerAppName = $"ca-doctors-api-{env}",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        ManagedEnvironmentId = containerAppEnvironment.Id,
        Configuration = new AzureNative.App.Inputs.ConfigurationArgs
        {
            Ingress = new AzureNative.App.Inputs.IngressArgs
            {
                External = true,
                TargetPort = 8080,
            },
            Registries = new[]
            {
                new AzureNative.App.Inputs.RegistryCredentialsArgs
                {
                    Server = Output.Format($"{containerRegistry.Name}.azurecr.io"),
                    Username = registryCredentials.Apply(c => c.Username!),
                    PasswordSecretRef = "registry-password",
                },
            },
            Secrets = new[]
            {
                new AzureNative.App.Inputs.SecretArgs
                {
                    Name = "registry-password",
                    Value = registryCredentials.Apply(c => c.Passwords[0].Value!),
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
                    Image = Output.Format($"{containerRegistry.Name}.azurecr.io/doctors-api:latest"),
                    Resources = new AzureNative.App.Inputs.ContainerResourcesArgs
                    {
                        Cpu = 0.25,
                        Memory = "0.5Gi",
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
                            Value = Output.Format($"Server=tcp:{sqlServer.Name}.database.windows.net;Database={sqlDatabase.Name};User Id={sqlAdmin};Password={sqlPassword};Encrypt=True;Trust Server Certificate=False;"),
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
    });

    return new Dictionary<string, object?>
    {
        ["resourceGroupName"] = resourceGroup.Name,
        ["containerAppUrl"] = Output.Format($"https://{containerApp.Configuration.Apply(c => c!.Ingress!.Fqdn!)}"),
    };
});
