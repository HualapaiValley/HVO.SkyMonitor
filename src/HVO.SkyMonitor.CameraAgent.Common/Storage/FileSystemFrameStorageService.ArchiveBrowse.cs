using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

public sealed partial class FileSystemFrameStorageService
{
    internal IReadOnlyList<DateOnly> GetArchiveIndexDates(string storageRoot)
    {
        var index = Path.Combine(storageRoot, "index");
        try
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(storageRoot, index);
            if (!Directory.Exists(index))
                return [];

            var dates = new List<DateOnly>();
            foreach (var path in Directory.EnumerateFiles(index, "frames_*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("frames_", StringComparison.Ordinal) &&
                    DateOnly.TryParseExact(name.AsSpan(7), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    dates.Add(date);
            }
            return dates;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.FrameBrowseEntrySkipped(index);
            return [];
        }
    }

    internal IEnumerable<IndexedArchiveFrame> ReadArchiveIndex(
        string storageRoot, DateOnly date, FrameArtifactRole? role, Guid? artifactId, CancellationToken cancellationToken)
    {
        var index = GetIndexPath(storageRoot, date);
        using var stream = OpenArchiveIndex(storageRoot, index);
        if (stream is null)
            yield break;

        using var journal = OpenArchiveBrowseJournal(storageRoot, index, out var journalReadable);
        using var visibility = journal is null ? null : TryCreateArchiveVisibility(journal, index);
        if (!journalReadable || journal is not null && visibility is null)
            yield break;

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadMetadata(line, out var indexed) || indexed.Role == FrameArtifactRole.Raw ||
                (artifactId is { } id && indexed.ArtifactId != id))
                continue;

            var captureDay = DateOnly.FromDateTime(indexed.TimestampUtc.UtcDateTime);
            if (Math.Abs(captureDay.DayNumber - date.DayNumber) > 1 || !IsValid(indexed, captureDay, role))
                continue;

            IndexedArchiveFrame? frame;
            try
            {
                frame = TryReadArchiveFrame(storageRoot, indexed, visibility);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                _logger.FrameBrowseEntrySkipped(index);
                continue;
            }
            if (frame is not null)
                yield return frame;
        }
    }

    private SqliteConnection? OpenArchiveBrowseJournal(string root, string index, out bool readable)
    {
        try
        {
            readable = true;
            return OpenCenteredBrowseJournal(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            _logger.FrameBrowseEntrySkipped(index);
            readable = false;
            return null;
        }
    }

    private SqliteCommand? TryCreateArchiveVisibility(SqliteConnection journal, string index)
    {
        try
        {
            return CreateBrowseVisibility(journal);
        }
        catch (SqliteException)
        {
            _logger.FrameBrowseEntrySkipped(index);
            return null;
        }
    }

    private FileStream? OpenArchiveIndex(string root, string path)
    {
        try
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.FrameBrowseEntrySkipped(path);
            return null;
        }
    }

    private IndexedArchiveFrame? TryReadArchiveFrame(string root, StoredFrameMetadata indexed, SqliteCommand? visibility)
    {
        var payload = GetPayloadPath(root, indexed);
        var sidecar = Path.ChangeExtension(payload, ".json");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, payload);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, sidecar);
        var info = new FileInfo(sidecar);
        if (!info.Exists || info.Length is < 1 or > 4L * 1024 * 1024 || !File.Exists(payload) ||
            !TryReadMetadataFile(sidecar, out var retained, out var binding) || binding is null ||
            !Matches(indexed, retained) || !MatchesVersionedBinding(root, payload, binding) ||
            !IsPublishedForCompatibility(root, payload, visibility, binding, indexed.ArtifactId, out var standalone))
            return null;

        return new(root, payload, binding.Manifest, standalone);
    }
}

internal sealed record IndexedArchiveFrame(
    string StorageRoot, string PayloadPath, ArtifactManifestV2 Manifest, bool Standalone)
{
    internal string DescriptorIdentity { get; } = Manifest.IdempotencyKey;
}
