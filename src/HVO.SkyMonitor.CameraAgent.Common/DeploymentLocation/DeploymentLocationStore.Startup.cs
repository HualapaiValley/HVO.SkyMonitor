using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStore
{
    internal const int RecentMaximumEntries = 400_000;
    internal const int RecentMaximumCaptures = 20_000;
    private const long RecentMaximumBytes = 4L * 1024 * 1024 * 1024;
    private const int RecentMaximumFileBytes = 8 * 1024 * 1024;

    private bool HasAnyRetainedStateBounded(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(_options.RawIngressRoot);
        if (!Directory.Exists(root)) return false;
        var count = 0;
        if (File.Exists(Path.Combine(root, ".location", RetainedIntegrityLedger.DatabaseName)) ||
            File.Exists(Path.Combine(root, ".location", RetainedIntegrityLedger.CheckpointName))) return true;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 64) return true;
            RawIngressFileStore.EnsureNoSymbolicLinks(root, entry);
            if (Path.GetFileName(entry) == ".location") continue;
            if (!Directory.Exists(entry) || Directory.EnumerateFileSystemEntries(entry).Any()) return true;
        }
        return false;
    }

    private bool ValidateRecentRetainedEvidence(DeploymentLocationHistory history, RetainedIntegrityLedger? ledger,
        CancellationToken cancellationToken)
    {
        // Without a registered authority, in-band publications cannot maintain
        // expectations. Keep capture held while the recovery host stays available.
        if (ledger is null) return false;
        using var activity = DeploymentLocationTelemetry.AttributionActivitySource.StartActivity("deployment-location.validate-startup-recent");
        var root = Path.GetFullPath(_options.RawIngressRoot);
        var now = _timeProvider.GetUtcNow();
        var cutoff = now.AddHours(-48);
        var entries = 0;
        long bytesRead = 0;
        var rawCaptures = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var area in new[] { "frames", "derived" })
            {
                for (var day = cutoff.Date; day <= now.Date; day = day.AddDays(1))
                {
                    var relative = Path.Combine(area, day.Year.ToString("D4", CultureInfo.InvariantCulture),
                        day.Month.ToString("D2", CultureInfo.InvariantCulture), day.Day.ToString("D2", CultureInfo.InvariantCulture));
                    var directory = Path.Combine(root, relative);
                    RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
                    var prefix = "F|" + relative + Path.DirectorySeparatorChar;
                    var expected = ledger?.ReadRecentExpected(prefix, RecentMaximumEntries - entries, cancellationToken)
                        ?? new Dictionary<string, RetainedIntegrityLedger.Entry>(StringComparer.Ordinal);
                    var pending = new Stack<(string Path, int Depth)>();
                    if (Directory.Exists(directory)) pending.Push((directory, 0));
                    while (pending.TryPop(out var current))
                    {
                        foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (++entries > RecentMaximumEntries) return false;
                            RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
                            if (Directory.Exists(path))
                            {
                                if (current.Depth >= 8) return false;
                                pending.Push((path, current.Depth + 1));
                                continue;
                            }
                            if (!path.EndsWith(".json", StringComparison.Ordinal) || !InRecentWindow(path, cutoff)) continue;
                            using var stream = File.OpenRead(path);
                            if (stream.Length > RecentMaximumFileBytes || bytesRead + stream.Length > RecentMaximumBytes) return false;
                            var json = new byte[checked((int)stream.Length)];
                            stream.ReadExactly(json);
                            if (stream.ReadByte() != -1) return false;
                            bytesRead += json.Length;
                            BeforeRetainedManifestValidationForTest?.Invoke();
                            var parsed = ValidateRecentJson(history, json, requireManifest: area == "frames");
                            var kind = parsed.Kind;
                            if (parsed.CaptureId is not null) rawCaptures.Add(parsed.CaptureId);
                            var key = RetainedIntegrityLedger.FileKey(root, path);
                            if (kind == 3) return false;
                            var known = expected.Remove(key, out var old);
                            if (ledger is { HasExpectedEvidence: true })
                            {
                                var digest = SHA256.HashData(json);
                                var matchesExpected = known && old is { Kind: not 0 } && digest.AsSpan().SequenceEqual(old.Digest);
                                if (!matchesExpected && !ledger.MatchesRecent(key, digest, cancellationToken)) return false;
                            }
                        }
                    }
                    if (expected.Values.Any(entry => entry.Kind != 0 && InRecentWindow(entry.Key, cutoff) &&
                        !ledger!.MatchesRecent(entry.Key, null, cancellationToken))) return false;
                }
            }
            return ValidateRecentJournal(root, history, ledger, cutoff, rawCaptures, ref bytesRead, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
            CryptographicException or SqliteException or JsonException or FormatException or InvalidCastException or OverflowException)
        {
            DeploymentLocationLog.RetainedIntegrityUnqualified(_logger);
            return false;
        }
        finally { activity?.SetTag("startup.recent.entries", entries); activity?.SetTag("startup.recent.bytes", bytesRead); }
    }

    private static bool InRecentWindow(string path, DateTimeOffset cutoff)
    {
        var name = Path.GetFileName(path);
        return name.Length < 24 || !DateTimeOffset.TryParseExact(name.AsSpan(0, 24), "yyyy-MM-dd_HH-mm-ss.fff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) || timestamp >= cutoff;
    }

    private static (int Kind, string? CaptureId, long? Exposure) ValidateRecentJson(DeploymentLocationHistory history, byte[] json, bool requireManifest)
    {
        var (manifest, other) = ParseRetainedManifest(json, requireManifest, strict: false);
        if (manifest is null) return (other ? 2 : 3, null, null);
        if (manifest.Descriptor.Location is { } provenance)
        {
            var snapshot = history.Snapshots.SingleOrDefault(item => item.LocationId == provenance.LocationId && item.Version == provenance.Version);
            if (snapshot is null || snapshot.ToProvenance() != provenance ||
                !IsEffective(history, snapshot, manifest.Descriptor.Timing.ExposureStartedUtc)) return (3, null, null);
        }
        return (1, manifest.Descriptor.Artifact.Role == HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw
            ? manifest.Descriptor.Capture.CaptureId.ToString("N") : null,
            manifest.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds());
    }

    private static bool ValidateRecentJournal(string root, DeploymentLocationHistory history, RetainedIntegrityLedger? ledger,
        DateTimeOffset cutoff, HashSet<string> rawCaptures, ref long bytesRead, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        if (!File.Exists(path)) return rawCaptures.Count == 0;
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='raw_captures';";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return rawCaptures.Count == 0;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_raw_captures_gallery_time';";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) return false;
        command.CommandText = "PRAGMA index_info(ix_raw_captures_gallery_time);";
        using (var columns = command.ExecuteReader())
        {
            foreach (var expected in new[] { "exposure_started_unix_ms", "capture_sequence", "raw_capture_row_id" })
                if (!columns.Read() || columns.IsDBNull(2) || columns.GetString(2) != expected) return false;
            if (columns.Read()) return false;
        }
        command.CommandText = "SELECT capture_id,manifest_json,exposure_started_unix_ms FROM raw_captures INDEXED BY ix_raw_captures_gallery_time WHERE exposure_started_unix_ms >= $cutoff ORDER BY exposure_started_unix_ms DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", RecentMaximumCaptures + 1);
        using var rows = command.ExecuteReader();
        var count = 0;
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > RecentMaximumCaptures) return false;
            if (rows.GetFieldType(1) != typeof(byte[])) return false;
            if (rows.GetBytes(1, 0, null, 0, 0) > RecentMaximumFileBytes) return false;
            var json = (byte[])rows.GetValue(1);
            if (json.Length > RecentMaximumFileBytes || (bytesRead += json.Length) > RecentMaximumBytes) return false;
            var parsed = ValidateRecentJson(history, json, requireManifest: true);
            if (!Guid.TryParse(rows.GetString(0), out var capture) || parsed.Kind != 1 ||
                parsed.CaptureId != capture.ToString("N") || parsed.Exposure != rows.GetInt64(2)) return false;
            rawCaptures.Remove(capture.ToString("N"));
            if (
                ledger is { HasExpectedEvidence: true } && !ledger.MatchesRecent(
                    RetainedIntegrityLedger.JournalKey(rows.GetString(0)), SHA256.HashData(json), cancellationToken)) return false;
        }
        return rawCaptures.Count == 0;
    }

    private static byte[] AuthorityStamp(params string[] paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(File.Exists(path) ? [(byte)1] : [(byte)0]);
            if (File.Exists(path)) hash.AppendData(File.ReadAllBytes(path));
        }
        return hash.GetHashAndReset();
    }
}
