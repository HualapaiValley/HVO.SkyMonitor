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
}
