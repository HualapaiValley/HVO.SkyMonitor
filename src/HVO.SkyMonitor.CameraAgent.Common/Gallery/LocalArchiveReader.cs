using System.Runtime.CompilerServices;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>Reads existing FileStorage indexes under the configured local Storage policy.</summary>
internal sealed class LocalArchiveReader(
    IFrameStorageService storage,
    ICameraAgentConfigurationAccessor configuration,
    IOptions<CameraAgentHostOptions> options)
{
    internal async ValueTask<IndexedArchiveFrame?> FindAsync(
        SqliteConnection journal, Guid artifactId, CancellationToken cancellationToken)
    {
        await foreach (var frame in ReadAsync(journal, null, artifactId, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
            return frame;
        return null;
    }

    internal async ValueTask<IReadOnlyList<IndexedArchiveFrame>> ReadPageAsync(
        SqliteConnection journal,
        int limit,
        FrameArtifactRole? role,
        string? recipe,
        long? from,
        long? to,
        long? cursorTime,
        string? cursorIdentity,
        CancellationToken cancellationToken)
    {
        var frames = new List<IndexedArchiveFrame>(limit);
        var ids = new HashSet<Guid>();
        var upper = cursorTime is { } before && (to is null || before < to) ? before : to;
        // A day index can straddle UTC midnight. Once past both possible index dates,
        // older files cannot affect the bounded page.
        bool CanFillPage(DateOnly day) =>
            frames.Count < limit || DayStart(day) + 2 * MillisecondsPerDay > SortTime(frames[^1]);
        await foreach (var frame in ReadAsync(journal, role, null, from, upper, CanFillPage, cancellationToken)
            .ConfigureAwait(false))
        {
            var manifest = frame.Manifest;
            var time = SortTime(frame);
            if (time < from || time > upper ||
                (time == cursorTime && string.CompareOrdinal(frame.DescriptorIdentity, cursorIdentity) >= 0) ||
                (recipe is not null && RecipeIdentity(frame) != recipe) ||
                !ids.Add(manifest.Descriptor.Artifact.ArtifactId))
                continue;

            frames.Add(frame);
            frames.Sort(static (left, right) =>
            {
                var byTime = SortTime(right).CompareTo(SortTime(left));
                return byTime != 0 ? byTime : string.CompareOrdinal(right.DescriptorIdentity, left.DescriptorIdentity);
            });
            if (frames.Count > limit)
            {
                ids.Remove(frames[^1].Manifest.Descriptor.Artifact.ArtifactId);
                frames.RemoveAt(frames.Count - 1);
            }
        }
        return frames;
    }

    internal static long SortTime(IndexedArchiveFrame frame)
        => frame.Manifest.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds();

    internal static string RecipeIdentity(IndexedArchiveFrame frame)
        => ProcessingIdentity.CreateRecipeIdentity(frame.Manifest.Descriptor.Artifact.Recipe).IdentitySha256;

    private const long MillisecondsPerDay = 86_400_000;

    private async IAsyncEnumerable<IndexedArchiveFrame> ReadAsync(
        SqliteConnection journal, FrameArtifactRole? role, Guid? artifactId, long? from = null, long? to = null,
        Func<DateOnly, bool>? canFillPage = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!configuration.IsConfigured || storage is not FileSystemFrameStorageService filesystem ||
            role == FrameArtifactRole.Raw)
            yield break;

        var host = options.Value;
        var config = await configuration.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var indexes = CameraAgentStorageResolver.Resolve(config, host)
            .Where(location => !StorageRootIdentity.AreEqual(location.Root, host.RawIngressRoot))
            .SelectMany(location => filesystem.GetArchiveIndexDates(location.Root)
                .Select(day => (location.Root, Day: day)))
            .Where(index => !((DayStart(index.Day) + 2 * MillisecondsPerDay <= from) ||
                (DayStart(index.Day) - MillisecondsPerDay > to)))
            .OrderByDescending(static index => index.Day);

        using var authority = journal.CreateCommand();
        authority.CommandText = """
            SELECT EXISTS (SELECT 1 FROM processing_outputs WHERE artifact_id = $artifact)
                OR EXISTS (SELECT 1 FROM raw_captures WHERE raw_artifact_id = $artifact
                            OR ($standalone = 1 AND capture_id = $capture));
            """;
        authority.Parameters.Add("$artifact", SqliteType.Text);
        authority.Parameters.Add("$capture", SqliteType.Text);
        authority.Parameters.Add("$standalone", SqliteType.Integer);

        foreach (var (root, day) in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canFillPage?.Invoke(day) == false)
                yield break;
            foreach (var frame in filesystem.ReadArchiveIndex(root, day, role, artifactId, cancellationToken))
            {
                // A detached file cannot mask a journaled artifact or stand in for publication of a known capture.
                authority.Parameters["$artifact"].Value = frame.Manifest.Descriptor.Artifact.ArtifactId.ToString("N");
                authority.Parameters["$capture"].Value = frame.Manifest.Descriptor.Capture.CaptureId.ToString("N");
                authority.Parameters["$standalone"].Value = frame.Standalone ? 1 : 0;
                if (await authority.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 0L)
                    yield return frame;
            }
        }
    }

    private static long DayStart(DateOnly day)
        => (day.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber) * MillisecondsPerDay;
}
