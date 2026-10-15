namespace HVO.SkyMonitor.Deployment;

internal sealed record InstallationPaths(
    string ProductRoot,
    string InstanceRoot,
    string ConfigRoot,
    string StateRoot,
    string DeploymentStateRoot,
    string ManifestPath,
    string ApplicationIdentityPath,
    string StatePath,
    string ResultPath,
    string CatalogRoot,
    string OperationsRoot,
    string LifecycleStatePath,
    string BackupsRoot,
    string CatalogReferencesRoot)
{
    public static InstallationPaths Create(string productRoot, Guid instanceId, string catalogId)
    {
        var canonicalProductRoot = Path.GetFullPath(productRoot);
        var id = instanceId.ToString("D");
        var instanceRoot = Path.Combine(canonicalProductRoot, "cameraagents", id);
        var configRoot = Path.Combine(instanceRoot, "config");
        var stateRoot = Path.Combine(instanceRoot, "state");
        var deploymentStateRoot = Path.Combine(stateRoot, "deployment");
        var operationsRoot = Path.Combine(canonicalProductRoot, "operations");
        return new InstallationPaths(
            canonicalProductRoot,
            instanceRoot,
            configRoot,
            stateRoot,
            deploymentStateRoot,
            Path.Combine(instanceRoot, "instance-manifest.json"),
            Path.Combine(instanceRoot, "application-identity.json"),
            Path.Combine(deploymentStateRoot, "installation-state.json"),
            Path.Combine(deploymentStateRoot, "installation-result.json"),
            Path.Combine(canonicalProductRoot, "catalogs", catalogId),
            operationsRoot,
            Path.Combine(operationsRoot, $"cameraagent-{id}.lifecycle.json"),
            Path.Combine(operationsRoot, "backups", "cameraagents", id),
            Path.Combine(operationsRoot, "catalog-references", catalogId));
    }

    /// <summary>
    /// The same instance paths bound to another approved catalog's side-by-side root (issue #521). Each catalog ID
    /// owns its own installed versions, lineage binding and historical reference pins.
    /// </summary>
    public InstallationPaths WithCatalog(string catalogId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        return this with
        {
            CatalogRoot = Path.Combine(ProductRoot, "catalogs", catalogId),
            CatalogReferencesRoot = Path.Combine(OperationsRoot, "catalog-references", catalogId)
        };
    }

    /// <summary>The installed catalog root of every approved specification under this product root.</summary>
    public IReadOnlyList<InstallationPaths> ForEveryApprovedCatalog()
        => ProductionCatalog.Specifications.All.Select(item => WithCatalog(item.CatalogId)).ToArray();
}
