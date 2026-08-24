using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal static class CatalogReferenceStore
{
    public static async Task PinHistoricalAsync(
        InstallationPaths paths,
        CatalogInstallationIdentity catalog,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var versionRoot = Path.Combine(paths.CatalogReferencesRoot, catalog.PackageVersion, "historical");
        SafeFileSystem.CreateOwnerDirectory(paths.CatalogReferencesRoot);
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(paths.CatalogReferencesRoot, catalog.PackageVersion));
        SafeFileSystem.CreateOwnerDirectory(versionRoot);
        var pin = new CatalogReferencePin(
            1,
            catalog.CatalogId,
            catalog.PackageVersion,
            paths.InstanceRoot.Split(Path.DirectorySeparatorChar).Last(),
            operationId,
            catalog.ManifestSha256,
            catalog.DatabaseSha256,
            DateTimeOffset.UtcNow);
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(versionRoot, $"cameraagent-{pin.InstanceId}.json"),
            pin,
            CatalogReferenceJsonContext.Default.CatalogReferencePin,
            cancellationToken).ConfigureAwait(false);
    }

    internal sealed record CatalogReferencePin(
        int SchemaVersion,
        string CatalogId,
        string PackageVersion,
        string InstanceId,
        Guid OperationId,
        string ManifestSha256,
        string DatabaseSha256,
        DateTimeOffset CreatedUtc);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(CatalogReferenceStore.CatalogReferencePin))]
internal sealed partial class CatalogReferenceJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
