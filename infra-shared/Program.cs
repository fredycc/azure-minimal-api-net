// =============================================================================
using Pulumi;
using AzureNative = Pulumi.AzureNative;

return await Pulumi.Deployment.RunAsync(() =>
{
    var config = new Pulumi.Config();
    var location = config.Get("location") ?? "eastus";

    var tags = new Dictionary<string, string>
    {
        { "project", "azure-minimal-api-net" },
        { "managed-by", "pulumi" },
        { "component", "shared" }
    };

    // =========================================================================
    // 1. SHARED RESOURCE GROUP
    // =========================================================================
    var resourceGroup = new AzureNative.Resources.ResourceGroup("rg-core-shared", new()
    {
        ResourceGroupName = "rg-core-shared",
        Location = location,
        Tags = tags,
    });

    // =========================================================================
    // 2. CONTAINER REGISTRY (acrfcoremain)
    // =========================================================================
    // Shared across all environments (dev/qa/prod). Basic SKU is sufficient.
    var acr = new AzureNative.ContainerRegistry.Registry("acrfcoremain", new()
    {
        RegistryName = "acrfcoremain",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Sku = new AzureNative.ContainerRegistry.Inputs.SkuArgs
        {
            Name = AzureNative.ContainerRegistry.SkuName.Basic,
        },
        AdminUserEnabled = false,
        Tags = tags,
    });

    // =========================================================================
    // 3. LOG ANALYTICS WORKSPACE
    // =========================================================================
    // Shared LAW for diagnostics across all environments.
    var logAnalytics = new AzureNative.OperationalInsights.Workspace("law-core-main", new()
    {
        WorkspaceName = "law-core-main",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Sku = new AzureNative.OperationalInsights.Inputs.WorkspaceSkuArgs
        {
            Name = AzureNative.OperationalInsights.WorkspaceSkuNameEnum.PerGB2018,
        },
        Tags = tags,
    });

    // =========================================================================
    // 4. SHARED CONTAINER APP ENVIRONMENT (cae-core-main)
    // =========================================================================
    // Azure restricts Container App Environments in some subscriptions.
    // Sharing one across environments saves costs and avoids limits.
    
    // Obtenemos las credenciales (SharedKey) del Log Analytics Workspace para asociarlas al CAE.
    var sharedKey = Output.Tuple(resourceGroup.Name, logAnalytics.Name).Apply(names =>
    {
        var rgName = names.Item1;
        var lawName = names.Item2;
        var keys = AzureNative.OperationalInsights.GetSharedKeys.Invoke(new AzureNative.OperationalInsights.GetSharedKeysInvokeArgs
        {
            ResourceGroupName = rgName,
            WorkspaceName = lawName,
        });
        return keys.Apply(k => k.PrimarySharedKey ?? string.Empty);
    });

    var cae = new AzureNative.App.ManagedEnvironment("cae-core-main", new()
    {
        EnvironmentName = "cae-core-main",
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        AppLogsConfiguration = new AzureNative.App.Inputs.AppLogsConfigurationArgs
        {
            Destination = "log-analytics",
            LogAnalyticsConfiguration = new AzureNative.App.Inputs.LogAnalyticsConfigurationArgs
            {
                CustomerId = logAnalytics.CustomerId,
                SharedKey = sharedKey,
            }
        },
        Tags = tags,
    });

    // =========================================================================
    // OUTPUTS - Used by environment stacks via StackReference
    // =========================================================================
    return new Dictionary<string, object?>
    {
        ["acrName"] = acr.Name,
        ["acrId"] = acr.Id,                    // Required for RBAC AcrPull
        ["acrLoginServer"] = Output.Format($"{acr.Name}.azurecr.io"),
        ["logAnalyticsWorkspaceId"] = logAnalytics.Id,
        ["caeId"] = cae.Id,
        ["resourceGroupName"] = resourceGroup.Name,
        ["location"] = location,
    };
});