using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The release records an instance keeps. <c>image-distribution.json</c> is the record of the release its running image
/// came from, which the upgrade notes and the older-version refusal read. The record of the image the instance ran before
/// is kept beside it, so a rollback to that image restores what is installed and an upgrade after the rollback is still
/// refused when it goes back. Both records live in one file, <c>image-distribution.records.json</c>, written atomically,
/// so an interrupted settlement can lose neither; <c>image-distribution.json</c> is derived from it. Each record names
/// the image ID it describes, and only a record of the image the instance runs is ever current.
/// </summary>
internal static class ReleaseRecords
{
    public const string CurrentFileName = AcquiredImage.EvidenceFileName;

    public const string RecordsFileName = "image-distribution.records.json";

    /// <summary>
    /// Makes the records follow the image the instance now runs, once that image is committed. An image a release names
    /// gets <paramref name="release"/>'s record. Any other image, such as a rollback target, gets the record kept for it,
    /// when there is one, and otherwise none. The record of the image the instance ran before stays beside it. Running it
    /// again gives the same records, so <c>--resume</c> completes an interrupted settlement.
    /// </summary>
    public static async Task SettleAsync(
        InstallationPaths paths,
        AcquiredImage? release,
        ImageInstallationIdentity running,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(running);
        var currentPath = Path.Combine(paths.DeploymentStateRoot, CurrentFileName);
        var recordsPath = Path.Combine(paths.DeploymentStateRoot, RecordsFileName);
        var current = Valid(await ReadAsync(currentPath, DeploymentJsonContext.Default.CameraAgentImageReleaseEvidence, cancellationToken)
            .ConfigureAwait(false));
        var kept = (await ReadAsync(recordsPath, DeploymentJsonContext.Default.CameraAgentImageReleaseRecords, cancellationToken)
                .ConfigureAwait(false))?.Records.Select(Valid).OfType<CameraAgentImageReleaseEvidence>() ?? [];
        var known = kept.Concat(current is null ? [] : [current])
            .GroupBy(static record => record.ImageId, StringComparer.Ordinal)
            .Select(static records => records.OrderByDescending(static record => record.RecordedUtc).First())
            .ToArray();
        var next = release?.ToEvidence(running) ??
                   known.FirstOrDefault(record => string.Equals(record.ImageId, running.ImageId, StringComparison.Ordinal));
        // The record of the image the instance ran before: the one current until this change, when it had one, or else
        // the one already kept beside the current record.
        var previous = current is not null && !string.Equals(current.ImageId, running.ImageId, StringComparison.Ordinal)
            ? current
            : known.Where(record => !string.Equals(record.ImageId, running.ImageId, StringComparison.Ordinal))
                .OrderByDescending(static record => record.RecordedUtc)
                .FirstOrDefault();
        CameraAgentImageReleaseEvidence[] records = [.. new[] { next, previous }.OfType<CameraAgentImageReleaseEvidence>()];

        // The pair is written first: it holds both records, so an interruption before the current record is rewritten
        // leaves everything a later settlement needs.
        if (records.Length > 0)
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                    recordsPath,
                    new CameraAgentImageReleaseRecords(DeploymentSchemaVersions.ImageReleaseRecords, records),
                    DeploymentJsonContext.Default.CameraAgentImageReleaseRecords,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            File.Delete(recordsPath);
        }
        if (next is not null)
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                    currentPath, next, DeploymentJsonContext.Default.CameraAgentImageReleaseEvidence, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            File.Delete(currentPath);
        }
    }

    /// <summary>
    /// The release version <paramref name="imageId"/> came from, from the records this instance keeps (its running image
    /// and the one it ran before), or <see langword="null"/> when no release names it. These records own the installed
    /// version: the machine install record (<see cref="InstallRecordStore"/>) reads its versions here and keeps none of
    /// its own (#1315).
    /// </summary>
    public static async Task<string?> VersionOfAsync(InstallationPaths paths, string imageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(imageId);
        var current = Valid(await ReadAsync(
                Path.Combine(paths.DeploymentStateRoot, CurrentFileName), DeploymentJsonContext.Default.CameraAgentImageReleaseEvidence,
                cancellationToken)
            .ConfigureAwait(false));
        var kept = (await ReadAsync(
                    Path.Combine(paths.DeploymentStateRoot, RecordsFileName), DeploymentJsonContext.Default.CameraAgentImageReleaseRecords,
                    cancellationToken)
                .ConfigureAwait(false))?.Records.Select(Valid).OfType<CameraAgentImageReleaseEvidence>() ?? [];
        return kept.Concat(current is null ? [] : [current])
            .Where(record => string.Equals(record.ImageId, imageId, StringComparison.Ordinal))
            .OrderByDescending(static record => record.RecordedUtc)
            .FirstOrDefault()?.ReleaseVersion;
    }

    /// <summary>A record this installer wrote and can use; anything else, an older schema included, counts as none.</summary>
    private static CameraAgentImageReleaseEvidence? Valid(CameraAgentImageReleaseEvidence? record)
        => record is { SchemaVersion: DeploymentSchemaVersions.ImageReleaseEvidence } &&
           record.ImageId is { Length: 71 } imageId && imageId.StartsWith("sha256:", StringComparison.Ordinal) &&
           ProductVersion.IsValid(record.ReleaseVersion)
            ? record
            : null;

    /// <summary>
    /// The record or records in <paramref name="path"/>, or <see langword="null"/> when there are none. They are this
    /// installer's own evidence: one that cannot be read is treated as absent, which leaves the next upgrade without a
    /// recorded version, as before records were kept, rather than blocking a committed operation from completing.
    /// </summary>
    private static async Task<T?> ReadAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The release records kept for an instance (schema <see cref="DeploymentSchemaVersions.ImageReleaseRecords"/>): the
/// record of its running image's release, when it has one, and the record of the image it ran before.
/// </summary>
internal sealed record CameraAgentImageReleaseRecords(
    int SchemaVersion,
    IReadOnlyList<CameraAgentImageReleaseEvidence> Records);
