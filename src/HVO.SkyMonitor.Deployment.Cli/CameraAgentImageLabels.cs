using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The installation identity a CameraAgent image declares through its labels: its contracts and the persisted-state
/// boundaries the installer compares to decide whether an upgrade declares a state migration
/// (<see cref="CameraAgentStateMigration.DeclaredChanges"/>). Every image the installer inspects is read through here.
/// </summary>
internal static class CameraAgentImageLabels
{
    /// <summary>The labels whose values are persisted-state boundaries.</summary>
    public static IReadOnlyList<string> StateBoundaryLabels { get; } =
    [
        "io.hvo.skymonitor.state-compatibility",
        "io.hvo.skymonitor.minimum-compatible-revision",
        "io.hvo.skymonitor.identity-migration",
        "io.hvo.skymonitor.raw-ingress-schema",
        "io.hvo.skymonitor.raw-ingress-migration",
        "io.hvo.skymonitor.catalog-manifest-version"
    ];

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
            UpgradeCompatibility: Label(labels, "io.hvo.skymonitor.state-compatibility"),
            SourceRevision: Label(labels, "org.opencontainers.image.revision"),
            Component: Label(labels, "io.hvo.skymonitor.component"),
            ConfigurationContract: Label(labels, "io.hvo.skymonitor.configuration-contract"),
            CatalogContract: Label(labels, "io.hvo.skymonitor.catalog-contract"),
            ReplayRunnerContract: Label(labels, "io.hvo.skymonitor.replay-runner-contract"),
            MinimumCompatibleRevision: Label(labels, "io.hvo.skymonitor.minimum-compatible-revision"),
            IdentityMigration: Label(labels, "io.hvo.skymonitor.identity-migration"),
            RawIngressSchema: Label(labels, "io.hvo.skymonitor.raw-ingress-schema"),
            CatalogManifestVersion: Label(labels, "io.hvo.skymonitor.catalog-manifest-version"),
            RawIngressMigration: Label(labels, "io.hvo.skymonitor.raw-ingress-migration"));

    /// <summary>
    /// The label's value, or null when it is absent. The Docker API types labels as strings; a value of any other JSON
    /// kind reads as absent, which leaves the image undeclared (and so refused, or treated as declaring a migration)
    /// rather than throwing on a recovery path.
    /// </summary>
    internal static string? Label(JsonElement labels, string name)
        => labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
