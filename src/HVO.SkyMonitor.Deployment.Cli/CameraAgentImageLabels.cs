using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Reads the installation identity a CameraAgent image declares through its OCI labels.</summary>
internal static class CameraAgentImageLabels
{
    public const string FirstBootContractLabel = "io.hvo.skymonitor.first-boot-contract";

    public static ImageInstallationIdentity Identity(
        string source,
        string reference,
        string imageId,
        string architecture,
        string? archiveSha256,
        JsonElement labels)
        => new(
            source,
            reference,
            imageId,
            architecture,
            archiveSha256,
            StateContract: Label(labels, "io.hvo.skymonitor.state-contract"),
            SourceRevision: Label(labels, "org.opencontainers.image.revision"),
            Component: Label(labels, "io.hvo.skymonitor.component"),
            ConfigurationContract: Label(labels, "io.hvo.skymonitor.configuration-contract"),
            CatalogContract: Label(labels, "io.hvo.skymonitor.catalog-contract"),
            ReplayRunnerContract: Label(labels, "io.hvo.skymonitor.replay-runner-contract"),
            IdentityMigration: Label(labels, "io.hvo.skymonitor.identity-migration"),
            RawIngressSchema: Label(labels, "io.hvo.skymonitor.raw-ingress-schema"),
            CatalogManifestVersion: Label(labels, "io.hvo.skymonitor.catalog-manifest-version"),
            FirstBootContract: Label(labels, FirstBootContractLabel));

    /// <summary>
    /// The label's value, or null when it is absent. The Docker API types labels as strings; a value of any other JSON
    /// kind reads as absent.
    /// </summary>
    internal static string? Label(JsonElement labels, string name)
        => labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
