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
    // OUTPUTS — Used by environment stacks via StackReference
    // =========================================================================
    return new Dictionary<string, object?>
    {
        ["acrName"] = acr.Name,
        ["acrId"] = acr.Id,                    // Required for RBAC AcrPull
        ["acrLoginServer"] = Output.Format($"{acr.Name}.azurecr.io"),
        ["logAnalyticsWorkspaceId"] = logAnalytics.Id,
        ["resourceGroupName"] = resourceGroup.Name,
    };
});
