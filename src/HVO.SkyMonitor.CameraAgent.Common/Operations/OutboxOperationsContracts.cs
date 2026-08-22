using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Operations;

public enum OutboxOperationAction
{
    Replay,
    Abandon
}

public enum OutboxOperationDisposition
{
    Applied,
    Duplicate
}

public sealed class OutboxOperationCollisionException : InvalidOperationException
{
    public OutboxOperationCollisionException()
    {
    }

    public OutboxOperationCollisionException(string message)
        : base(message)
    {
    }

    public OutboxOperationCollisionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record ArtifactOutboxOperationsCursor(long RecordId);

public sealed record ArtifactOutboxOperationsRecord(
    string RecordKey,
    ArtifactOutboxManifestKind ManifestKind,
    ArtifactOutboxStatus Status,
    int AttemptCount,
    long? PayloadBytes,
    string? MediaType,
    FrameArtifactRole? Role,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset NextAttemptUtc,
    string? ReasonCode,
    bool CanReplay,
    bool CanAbandon,
    ArtifactOutboxOperationsCursor Cursor);

public sealed record ArtifactOutboxOperationsPage(
    IReadOnlyList<ArtifactOutboxOperationsRecord> Items,
    ArtifactOutboxOperationsCursor? NextCursor);

public sealed record EnvironmentalOutboxOperationsCursor(long RecordId);

public sealed record EnvironmentalOutboxOperationsRecord(
    long RecordId,
    string Status,
    int AttemptCount,
    int PayloadBytes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? ReasonCode,
    bool CanReplay,
    bool CanAbandon,
    EnvironmentalOutboxOperationsCursor Cursor);

public sealed record EnvironmentalOutboxOperationsPage(
    IReadOnlyList<EnvironmentalOutboxOperationsRecord> Items,
    EnvironmentalOutboxOperationsCursor? NextCursor);

public sealed record OutboxOperationsAuditCursor(long Sequence);

public sealed record OutboxOperationsAuditRecord(
    long Sequence,
    string Action,
    string ActorKind,
    string ReasonCode,
    DateTimeOffset OccurredUtc);

public sealed record OutboxOperationsAuditPage(
    IReadOnlyList<OutboxOperationsAuditRecord> Items,
    OutboxOperationsAuditCursor? NextCursor);

public sealed record CameraAgentStorageLocation(string Alias, string Root);

public interface ICameraAgentStorageResolver
{
    ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetStorageLocationsAsync(CancellationToken cancellationToken)
        => GetUploadLocationsAsync(cancellationToken);

    ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetUploadLocationsAsync(CancellationToken cancellationToken);

    ValueTask<CameraAgentStorageLocation?> ResolveAliasAsync(string storageAlias, CancellationToken cancellationToken);
}

public sealed class CameraAgentStorageResolver(
    ICameraAgentConfigurationAccessor configurationAccessor,
    IOptions<CameraAgentHostOptions> hostOptions) : ICameraAgentStorageResolver
{
    public async ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetStorageLocationsAsync(
        CancellationToken cancellationToken)
    {
        var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(configuration, hostOptions.Value);
    }

    public async ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetUploadLocationsAsync(
        CancellationToken cancellationToken)
    {
        var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return ResolveLocations(
            ArtifactOutboxDrainService.ResolveStorageRoots(configuration, hostOptions.Value),
            hostOptions.Value.RawIngressRoot);
    }

    public async ValueTask<CameraAgentStorageLocation?> ResolveAliasAsync(
        string storageAlias,
        CancellationToken cancellationToken)
        => (await GetUploadLocationsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(location => string.Equals(location.Alias, storageAlias, StringComparison.Ordinal));

    public static IReadOnlyList<CameraAgentStorageLocation> Resolve(
        CameraModuleConfig configuration,
        CameraAgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        return ResolveLocations(
            ArtifactOutboxDrainService.ResolveLocalStorageRoots(configuration, options),
            options.RawIngressRoot);
    }

    private static List<CameraAgentStorageLocation> ResolveLocations(
        IReadOnlyList<string> roots,
        string rawIngressRoot)
    {
        var rawRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawIngressRoot));
        var locations = new List<CameraAgentStorageLocation>(roots.Count);
        var nextStorage = 1;
        foreach (var configuredRoot in roots)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
            var alias = PathsEqual(root, rawRoot) ? "raw-ingress" : $"storage-{nextStorage++}";
            locations.Add(new CameraAgentStorageLocation(alias, root));
        }

        return locations;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public static class OutboxOperationsReasonCodes
{
    private static readonly HashSet<string> ReplayCodes = new(StringComparer.Ordinal)
    {
        "configuration-corrected",
        "evidence-restored",
        "upstream-recovered"
    };

    private static readonly HashSet<string> AbandonCodes = new(StringComparer.Ordinal)
    {
        "invalid-source",
        "irrecoverable-evidence",
        "operator-approved-loss"
    };

    public static bool IsAllowed(OutboxOperationAction action, string? reasonCode)
        => reasonCode is not null && action switch
        {
            OutboxOperationAction.Replay => ReplayCodes.Contains(reasonCode),
            OutboxOperationAction.Abandon => AbandonCodes.Contains(reasonCode),
            _ => false
        };

    public static string Sanitize(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 64 ||
            reasonCode[0] is < 'a' or > 'z' ||
            reasonCode.Any(static character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            return "unspecified";
        }

        return reasonCode;
    }
}
