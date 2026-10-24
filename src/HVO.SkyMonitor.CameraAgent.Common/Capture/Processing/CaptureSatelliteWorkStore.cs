using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record CaptureSatelliteWork(string Key, string Holder, string NodeId, string Variant,
    string RawPayloadPath, string RawSidecarPath, CaptureSatelliteInputV1 Input);

/// <summary>
/// Intent precedes reference acquisition; reference precedes product publication. A restart reuses exact inputs and
/// the first completed result. Existing retention sweeps reclaim the intent and release its idempotent reference.
/// </summary>
internal sealed class CaptureSatelliteWorkStore(IOptions<CameraAgentHostOptions> options,
    SqliteCaptureProcessingStore processing, IOrbitalElementsStore orbital, TimeProvider? timeProvider = null)
{
    private readonly string root = Path.GetFullPath(options.Value.RawIngressRoot);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private const int MaximumWorkEntries = 131072;
    private const int MaximumHeaderBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal Action? BeforeHeaderPublicationForTest { get; set; }
    private sealed record Header(string Key, string NodeId, string Variant, string RawPayloadPath, string RawSidecarPath,
        Guid CaptureId, Guid RawArtifactId, string InputSha256, string? ManifestSha256);

    internal async ValueTask<CaptureSatelliteWork> PrepareAsync(CaptureProcessingContext context, string nodeId, string variant,
        Func<ValueTask<CaptureSatelliteInputV1>> resolve, CancellationToken cancellationToken)
    {
        var lifecycle = StorageLifecycleLock.ForRoot(root);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await PrepareCoreAsync(context, nodeId, variant, resolve, cancellationToken).ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async ValueTask<CaptureSatelliteWork> PrepareCoreAsync(CaptureProcessingContext context, string nodeId, string variant,
        Func<ValueTask<CaptureSatelliteInputV1>> resolve, CancellationToken cancellationToken)
    {
        var raw = context.RawCapture ?? throw new InvalidDataException("Satellite inputs require committed raw evidence.");
        var key = WorkKey(raw.Manifest.Descriptor.Capture.CaptureId, nodeId);
        var directory = DirectoryFor(key);
        var headerPath = Path.Combine(directory, "header.json");
        if (File.Exists(headerPath)) return await ReadAsync(headerPath, cancellationToken).ConfigureAwait(false);
        var inputPath = Path.Combine(directory, "input.json");
        var input = File.Exists(inputPath)
            ? CaptureSatelliteProducts.ParseInput(await ReadBoundedAsync(inputPath,
                CaptureSatelliteProducts.MaximumInputBytes, cancellationToken).ConfigureAwait(false))
            : await resolve().ConfigureAwait(false);
        var bytes = CaptureSatelliteProducts.SerializeInput(input);
        var manifest = input.Plan is null ? null : CaptureSatelliteProducts.ReadSnapshotManifest(input)?.ManifestSha256;
        var header = new Header(key, nodeId, variant, raw.Manifest.RelativeArtifactPath,
            Path.ChangeExtension(raw.Manifest.RelativeArtifactPath, ".json"), input.Descriptor.Capture.CaptureId,
            input.Descriptor.Artifact.ArtifactId, ProcessingIdentity.ComputePayloadSha256(bytes), manifest);
        CameraAgentFileSystem.EnsureStorageRootDurable(root);
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        RawIngressFileStore.SyncDirectoryHierarchy(root, directory);
        await CaptureProcessingPersistence.WriteAtomicallyAsync(root, Path.Combine(directory, "input.json"), bytes, null,
            cancellationToken).ConfigureAwait(false);
        BeforeHeaderPublicationForTest?.Invoke();
        await CaptureProcessingPersistence.WriteAtomicallyAsync(root, headerPath, JsonSerializer.SerializeToUtf8Bytes(header),
            null, cancellationToken).ConfigureAwait(false);
        return Work(header, input);
    }

    internal async ValueTask<ReadOnlyMemory<byte>?> ReadOrPredictAsync(CaptureSatelliteWork work, ProcessingArtifact raw,
        HVO.SkyMonitor.Astronomy.OrbitalSnapshotReadResult? exactSnapshot, CancellationToken cancellationToken)
    {
        if (work.Input.Plan is null) return null;
        var path = Path.Combine(DirectoryFor(work.Key), "result.json");
        if (File.Exists(path))
        {
            var bytes = await ReadBoundedAsync(path, CaptureOrbitalPredictionJson.MaximumOutputBytes, cancellationToken).ConfigureAwait(false);
            _ = CaptureOrbitalPredictionJson.Parse(bytes, CaptureSatelliteProducts.Restore(work.Input, exactSnapshot));
            return bytes;
        }
        var result = await CaptureSatelliteProducts.PredictAsync(work.Input, raw, work.Variant, cancellationToken, exactSnapshot: exactSnapshot).ConfigureAwait(false)
            ?? throw new InvalidDataException("Prepared satellite plan produced no result.");
        await CaptureProcessingPersistence.WriteAtomicallyAsync(root, path, result.Payload, null, cancellationToken).ConfigureAwait(false);
        return result.Payload;
    }

    internal async ValueTask<CaptureSatelliteWork> PinAsync(CaptureSatelliteWork work, CancellationToken cancellationToken)
    {
        var path = Path.Combine(DirectoryFor(work.Key), "publication-input.json");
        var frozen = File.Exists(path);
        var input = frozen ? CaptureSatelliteProducts.ParseInput(await ReadBoundedAsync(path,
            CaptureSatelliteProducts.MaximumInputBytes, cancellationToken).ConfigureAwait(false)) : work.Input;
        if (input.Plan is not null && CaptureSatelliteProducts.ReadSnapshotManifest(input) is { } manifest &&
            !await orbital.AddReferenceAsync(manifest.ManifestSha256, work.Holder, cancellationToken).ConfigureAwait(false))
        {
            if (frozen) throw new InvalidDataException("A previously pinned satellite snapshot is no longer retained.");
            input = input with { Plan = null, UnavailableReason = "SnapshotRetiredBeforePin" };
        }
        await CaptureProcessingPersistence.WriteAtomicallyAsync(root, path, CaptureSatelliteProducts.SerializeInput(input),
            null, cancellationToken).ConfigureAwait(false);
        return work with { Input = input };
    }

    // All scan work is bounded, reads small headers only, and runs under the existing retention/lifecycle barrier.
    // Raw is held while prediction products exist. Support products are held while another node/archive needs them.
    // A subsequent sweep removes raw, then releases the source reference; interruptions only postpone reclamation.
    internal async ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(string storageRoot,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetFullPath(storageRoot), root, StringComparison.Ordinal)) return [];
        var directory = Path.Combine(root, "journal", "capture-satellites");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        if (!Directory.Exists(directory)) return [];
        var holds = new List<ProcessingRetentionHold>();
        var count = 0;
        var guard = new ProjectedSceneRetentionGuard(root, processing);
        foreach (var entry in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaximumWorkEntries) throw new InvalidDataException("Satellite retention discovery exceeds its entry bound.");
            var path = Path.Combine(entry, "header.json");
            RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
            if (!File.Exists(path)) continue; // Interrupted pre-intent preparation acquired no reference.
            var header = await ReadHeaderAsync(path, cancellationToken).ConfigureAwait(false);
            var inventory = await processing.ReadSatelliteConsumersAsync(header.CaptureId, header.NodeId, cancellationToken).ConfigureAwait(false);
            var archiveHeld = await guard.HasArchivedCaptureAsync(header.CaptureId, cancellationToken).ConfigureAwait(false);
            var support = inventory.Where(static p => p.Satellite).Where(p => p.Unavailable || Exists(p.PayloadRelativePath)).ToArray();
            // A canonical scene is independent support, and its existing guard waits for raw to expire.
            // Treating it as a satellite consumer would form scene -> raw -> satellite -> scene holds.
            var external = inventory.Any(p => !p.Satellite && !p.ProjectedScene && (p.Unavailable || Exists(p.PayloadRelativePath)));
            if (support.Length > 0 || archiveHeld)
                holds.Add(new(header.RawArtifactId, header.RawPayloadPath, header.RawSidecarPath));
            if (external || archiveHeld)
                holds.AddRange(support.Where(p => Exists(p.PayloadRelativePath)).Select(static p =>
                    new ProcessingRetentionHold(p.ArtifactId, p.PayloadRelativePath, p.SidecarRelativePath)));
        }
        return holds;
    }

    internal async ValueTask ReconcileAsync(string storageRoot, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetFullPath(storageRoot), root, StringComparison.Ordinal)) return;
        var directory = Path.Combine(root, "journal", "capture-satellites");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        if (!Directory.Exists(directory)) return;
        var guard = new ProjectedSceneRetentionGuard(root, processing);
        var count = 0;
        var orphanReads = 0;
        long orphanBytes = 0;
        foreach (var entry in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaximumWorkEntries) throw new InvalidDataException("Satellite reference discovery exceeds its entry bound.");
            var path = Path.Combine(entry, "header.json");
            RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
            if (!File.Exists(path))
            {
                if (!Directory.EnumerateFileSystemEntries(entry).Any())
                {
                    Directory.Delete(entry);
                    RawIngressFileStore.SyncDirectory(directory);
                    continue;
                }
                if (clock.GetUtcNow().UtcDateTime - Directory.GetLastWriteTimeUtc(entry) < TimeSpan.FromDays(1) ||
                    orphanReads++ >= 16) continue;
                var inputPath = Path.Combine(entry, "input.json");
                RawIngressFileStore.EnsureNoSymbolicLinks(root, inputPath);
                if (File.Exists(inputPath))
                {
                    AccountOrphanBytes(new FileInfo(inputPath).Length);
                    var orphan = CaptureSatelliteProducts.ParseInput(await ReadBoundedAsync(inputPath,
                        CaptureSatelliteProducts.MaximumInputBytes, cancellationToken).ConfigureAwait(false));
                    var inventory = await processing.ReadSceneConsumersAsync(orphan.Descriptor.Capture.CaptureId, Guid.Empty,
                        AccountOrphanBytes, cancellationToken).ConfigureAwait(false);
                    if (inventory is null || inventory.CanonicalPayloadPaths.Any(Exists) ||
                        await guard.HasArchivedCaptureAsync(orphan.Descriptor.Capture.CaptureId, cancellationToken).ConfigureAwait(false)) continue;
                    var consumers = await processing.ReadSatelliteConsumersAsync(orphan.Descriptor.Capture.CaptureId,
                        string.Empty, cancellationToken).ConfigureAwait(false);
                    if (consumers.Any(static p => p.Unavailable)) continue;
                }
                // No header means reference acquisition never began, or cleanup already released it.
                foreach (var file in Directory.EnumerateFiles(entry))
                {
                    RawIngressFileStore.EnsureNoSymbolicLinks(root, file);
                    File.Delete(file);
                }
                Directory.Delete(entry);
                RawIngressFileStore.SyncDirectory(directory);
                continue;
            }
            var header = await ReadHeaderAsync(path, cancellationToken).ConfigureAwait(false);
            if (Exists(header.RawPayloadPath) ||
                (await processing.ReadSatelliteConsumersAsync(header.CaptureId, header.NodeId, cancellationToken).ConfigureAwait(false))
                    .Any(p => p.Unavailable || Exists(p.PayloadRelativePath)) ||
                await guard.HasArchivedCaptureAsync(header.CaptureId, cancellationToken).ConfigureAwait(false)) continue;
            if (header.ManifestSha256 is { } manifest)
                await orbital.RemoveReferenceAsync(manifest, Holder(header.Key), cancellationToken).ConfigureAwait(false);
            // Removal is idempotent. A crash before directory removal repeats it; no referenced product remains.
            foreach (var file in Directory.EnumerateFiles(entry).Where(file => !string.Equals(file, path, StringComparison.Ordinal)))
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(root, file);
                File.Delete(file);
            }
            // Keep the small reference intent until every large file is gone, so interrupted cleanup resumes.
            File.Delete(path);
            Directory.Delete(entry);
            RawIngressFileStore.SyncDirectory(directory);
        }

        void AccountOrphanBytes(long bytes)
        {
            if (bytes < 0 || bytes > 64L * 1024 * 1024 - orphanBytes)
                throw new InvalidDataException("Satellite orphan discovery exceeds its byte bound.");
            orphanBytes += bytes;
        }
    }

    private async ValueTask<CaptureSatelliteWork> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(path, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(Path.Combine(DirectoryFor(header.Key), "input.json"),
            CaptureSatelliteProducts.MaximumInputBytes, cancellationToken).ConfigureAwait(false);
        if (ProcessingIdentity.ComputePayloadSha256(bytes) != header.InputSha256)
            throw new InvalidDataException("Retained satellite input checksum differs from its intent.");
        var input = CaptureSatelliteProducts.ParseInput(bytes);
        if (input.Descriptor.Capture.CaptureId != header.CaptureId || input.Descriptor.Artifact.ArtifactId != header.RawArtifactId ||
            (input.Plan is null ? null : CaptureSatelliteProducts.ReadSnapshotManifest(input)?.ManifestSha256) != header.ManifestSha256)
            throw new InvalidDataException("Retained satellite intent differs from its evidence.");
        return Work(header, input);
    }

    private async ValueTask<Header> ReadHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var header = JsonSerializer.Deserialize<Header>(await ReadBoundedAsync(path, MaximumHeaderBytes, cancellationToken).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Satellite retention intent is invalid.");
        if (!HVO.SkyMonitor.Astronomy.OrbitalContract.IsSha256(header.Key) ||
            !string.Equals(Path.GetDirectoryName(path), DirectoryFor(header.Key), StringComparison.Ordinal) ||
            header.CaptureId == Guid.Empty || header.RawArtifactId == Guid.Empty || header.NodeId.Length is < 1 or > 128 ||
            header.Variant.Length is < 1 or > 96)
            throw new InvalidDataException("Satellite retention intent identity is invalid.");
        if (header.Key != WorkKey(header.CaptureId, header.NodeId) ||
            !await processing.VerifySatelliteRawAsync(header.CaptureId, header.RawArtifactId, header.RawPayloadPath,
                header.RawSidecarPath, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Satellite retention intent differs from its durable raw identity.");
        _ = Resolve(header.RawPayloadPath); _ = Resolve(header.RawSidecarPath);
        return header;
    }

    private static CaptureSatelliteWork Work(Header h, CaptureSatelliteInputV1 input)
        => new(h.Key, Holder(h.Key), h.NodeId, h.Variant, h.RawPayloadPath, h.RawSidecarPath, input);
    private static string Holder(string key) => "capture-satellite:" + key;
    private static string WorkKey(Guid captureId, string nodeId) => ProcessingIdentity.ComputePayloadSha256(
        Encoding.UTF8.GetBytes($"capture-satellite-work-v1\0{captureId:N}\0{nodeId}"));
    private string DirectoryFor(string key) => Path.Combine(root, "journal", "capture-satellites", key);
    private string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Satellite intent path escapes its storage root.");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        return path;
    }
    private bool Exists(string relative)
    {
        try { return (File.GetAttributes(Resolve(relative)) & FileAttributes.Directory) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private async ValueTask<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length is <= 0 || stream.Length > maximum) throw new InvalidDataException("Satellite evidence exceeds its byte bound.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Satellite evidence changed while reading.");
        return bytes;
    }
}
