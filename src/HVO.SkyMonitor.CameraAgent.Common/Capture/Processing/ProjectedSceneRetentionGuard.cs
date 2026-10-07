using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Protects a canonical scene while retained capture files still depend on its geometry.</summary>
internal sealed class ProjectedSceneRetentionGuard(
    string canonicalRoot, SqliteCaptureProcessingStore store,
    int maximumSidecars = 131072, long maximumBytes = 1024L * 1024 * 1024, int maximumCandidates = 512)
{
    private int _candidates;
    private long _readBytes;
    private int _sidecars;
    private int _entries;
    private const long MaximumSidecarBytes = 16 * 1024 * 1024;
    private static readonly string[] Roles = Enum.GetNames<FrameArtifactRole>();
    private readonly Dictionary<(string Root, DateOnly Date), HashSet<(string AgentId, Guid CaptureId)>> _archiveDates = [];

    // This cursor only postpones earlier candidates. A later pass must independently prove absence
    // of every consumer before deletion; persisted progress never serves as absence evidence.
    internal async ValueTask<(long? Timestamp, string? Identity)> ReadCursorAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePhysicalPath(canonicalRoot, "journal/scene-retention-cursor.json");
        if (!Exists(path, directory: false)) return (null, null);
        var bytes = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
        var cursor = JsonSerializer.Deserialize<SceneRetentionCursor>(bytes)
            ?? throw new InvalidDataException("Scene retention progress is invalid.");
        if (cursor.Identity.Length != 64) throw new InvalidDataException("Scene retention progress identity is invalid.");
        return (cursor.Timestamp, cursor.Identity);
    }

    internal async ValueTask SaveCursorAsync(long? timestamp, string? identity, CancellationToken cancellationToken)
    {
        if (timestamp is null || identity is null) return;
        var path = ResolvePhysicalPath(canonicalRoot, "journal/scene-retention-cursor.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        RawIngressFileStore.EnsureNoSymbolicLinks(canonicalRoot, temporary);
        await File.WriteAllBytesAsync(temporary,
            JsonSerializer.SerializeToUtf8Bytes(new SceneRetentionCursor(timestamp.Value, identity)), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    internal void CompletePass()
        => File.Delete(ResolvePhysicalPath(canonicalRoot, "journal/scene-retention-cursor.json"));

    internal async ValueTask<bool> HasRetainedConsumerAsync(
        DurableProcessingEvidence candidate, CancellationToken cancellationToken)
    {
        if (candidate.Role != FrameArtifactRole.Metadata) return false;
        var product = DurableProcessingProductManifestJson.Parse(candidate.EvidenceJson);
        if (!SceneProvenance.IsRetainedProjectedSceneSchemaVersion(product.ProductSchemaVersion)) return false;
        var inventory = await store.ReadSceneConsumersAsync(candidate.CaptureId, candidate.ArtifactId, AccountBytes, cancellationToken)
            .ConfigureAwait(false);
        if (inventory is null) return true; // Missing source identity cannot authorize deletion.
        if (inventory.Raw.Scene?.RequiresProjectedScene != true) return false;
        if (++_candidates > Math.Clamp(maximumCandidates, 1, 512))
            throw new InvalidDataException("Scene consumer discovery reached its candidate budget; retention resumes next pass.");
        var raw = inventory.Raw.Descriptor;
        if (raw.Capture.CaptureId != candidate.CaptureId ||
            product.Artifact.SourceArtifactIds.Count != 1 ||
            product.Artifact.SourceArtifactIds[0] != raw.Artifact.ArtifactId)
            throw new InvalidDataException("The retained scene is not bound to its capture's raw artifact.");
        foreach (var relativePath in inventory.CanonicalPayloadPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Exists(ResolvePhysicalPath(canonicalRoot, relativePath), directory: false)) return true;
        }
        // Every supported new live/replay publication records its graph revision before execution. Without that
        // history, retain the scene rather than infer that a removed archive destination never existed.
        if (!inventory.HasRecordedHistory) return true;
        var dates = new[]
        {
            DateOnly.FromDateTime(raw.Timing.ExposureStartedUtc.UtcDateTime),
            DateOnly.FromDateTime(raw.Timing.ReadoutCompletedUtc.UtcDateTime)
        }.Distinct();
        foreach (var root in inventory.ArchiveRoots)
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(canonicalRoot),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            foreach (var date in dates)
            {
                if (!_archiveDates.TryGetValue((root, date), out var captures))
                {
                    if (_archiveDates.Count >= 4096)
                        throw new InvalidDataException("Scene archive discovery reached its date budget; retention resumes next pass.");
                    captures = await ReadArchiveDateAsync(root, date, cancellationToken).ConfigureAwait(false);
                    _archiveDates.Add((root, date), captures);
                }
                if (captures.Contains((raw.Capture.AgentId, candidate.CaptureId))) return true;
            }
        }
        return false;
    }

    private async Task<HashSet<(string AgentId, Guid CaptureId)>> ReadArchiveDateAsync(
        string root, DateOnly date, CancellationToken cancellationToken)
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(root, root);
        var captures = new HashSet<(string AgentId, Guid CaptureId)>();
        foreach (var kind in new[] { "frames", "derived" })
        {
            var day = Path.Combine(kind, date.Year.ToString("D4", CultureInfo.InvariantCulture),
                date.Month.ToString("D2", CultureInfo.InvariantCulture), date.Day.ToString("D2", CultureInfo.InvariantCulture));
            foreach (var role in Roles)
            {
                var directory = ResolvePhysicalPath(root, Path.Combine(day, role));
                if (!Exists(directory, directory: true)) continue;
                foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++_entries > 262144)
                        throw new InvalidDataException("Scene archive discovery exceeds its directory-entry budget; retention is deferred.");
                    if (!path.EndsWith(kind == "frames" ? ".json" : ".manifest.json", StringComparison.Ordinal)) continue;
                    if (++_sidecars > Math.Clamp(maximumSidecars, 1, 131072) || new FileInfo(path).Length > MaximumSidecarBytes)
                        throw new InvalidDataException("Scene archive discovery exceeds its safety bound; scene retention is deferred.");
                    RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
                    var bytes = await ReadBoundedFileAsync(path, cancellationToken).ConfigureAwait(false);
                    CaptureIdentityDescriptor capture;
                    string payload;
                    if (kind == "frames")
                    {
                        using var json = JsonDocument.Parse(bytes);
                        if (!json.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetString() != ArtifactManifestV2.CurrentSchemaVersion)
                            continue; // Pre-v2 files cannot be consumers of the new compact contract.
                        var parsed = CaptureContractJson.ParseManifest(bytes);
                        var manifest = parsed.IsValid ? parsed.Document?.Manifest : null;
                        if (manifest is null) throw new InvalidDataException("A retained archive manifest is invalid.");
                        capture = manifest.Descriptor.Capture;
                        payload = manifest.RelativeArtifactPath;
                    }
                    else
                    {
                        var manifest = DurableProcessingProductManifestJson.Parse(bytes);
                        capture = manifest.Capture;
                        payload = manifest.RelativeArtifactPath;
                    }
                    if (Exists(ResolvePhysicalPath(root, payload), directory: false)) captures.Add((capture.AgentId, capture.CaptureId));
                }
            }
        }
        return captures;
    }

    private async Task<byte[]> ReadBoundedFileAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var ownedStream = stream.ConfigureAwait(false);
        if (stream.Length > MaximumSidecarBytes)
            throw new InvalidDataException("A scene consumer sidecar exceeds its byte budget.");
        AccountBytes(stream.Length);
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private sealed record SceneRetentionCursor(long Timestamp, string Identity);

    private void AccountBytes(long bytes)
    {
        if (bytes < 0 || bytes > MaximumSidecarBytes || _readBytes > Math.Clamp(maximumBytes, 1, 1024L * 1024 * 1024) - bytes)
            throw new InvalidDataException("Scene consumer discovery exceeds its byte budget; scene retention is deferred.");
        _readBytes += bytes;
    }

    private static bool Exists(string path, bool directory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directory)
                throw new InvalidDataException("Scene consumer evidence has an unsafe filesystem type.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // Access, device and I/O errors are not evidence of absence.
    }

    private static string ResolvePhysicalPath(string root, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("A scene consumer path escapes its recorded storage root.");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        return path;
    }
}
