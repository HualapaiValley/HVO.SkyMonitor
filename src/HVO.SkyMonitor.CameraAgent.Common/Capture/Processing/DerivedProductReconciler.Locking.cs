using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class DerivedProductReconciler
{
    private async ValueTask<ReconciliationUnit> EnterUnitAsync(CancellationToken cancellationToken)
    {
        var unit = new ReconciliationUnit(this);
        await unit.EnterAsync(cancellationToken).ConfigureAwait(false);
        return unit;
    }

    private async ValueTask<(bool Current, Dictionary<string, bool> Value)> ObserveOperationCopiesAsync(
        ProcessingLifecycleOperation operation, ReconciliationUnit unit, CancellationToken cancellationToken)
    {
        var pairs = new List<(string Source, string Destination)>();
        var paths = new List<string>();
        if (operation.Phase == "planned")
        {
            var destinationRoot = ResolveRoot(operation.DestinationRelativePath);
            foreach (var relative in new[] { operation.SourceRelativePath, operation.CompanionRelativePath })
            {
                if (relative is null) continue;
                var source = ResolveRoot(relative);
                var destination = Path.Combine(destinationRoot, Path.GetFileName(source));
                paths.Add(source);
                paths.Add(destination);
                if (File.Exists(source) && File.Exists(destination)) pairs.Add((source, destination));
            }
        }
        if (pairs.Count == 0) return (true, new Dictionary<string, bool>(StringComparer.Ordinal));
        // Observe before starting any moves. Once revalidated, the entire durable mutation stays locked.
        return await unit.ObserveAsync(async () =>
        {
            var compared = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var pair in pairs)
                compared[pair.Source] = await FilesEqualAsync(pair.Source, pair.Destination, cancellationToken).ConfigureAwait(false);
            return compared;
        }, paths.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> IsCurrentRowAsync(DurableProcessingEvidence row, CancellationToken cancellationToken)
    {
        return await _store.IsCurrentProcessingEvidenceAsync(row, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string[]> ObservationPathsAsync(string sidecar, CancellationToken cancellationToken)
    {
        // Discovery may enumerate malformed siblings, but never while the storage lock is owned.
        try
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, sidecar);
            var bytes = await ReadObservedBytesAsync(sidecar, cancellationToken).ConfigureAwait(false);
            string? relative;
            if (sidecar.EndsWith(".manifest.json", StringComparison.Ordinal))
            {
                try { relative = DurableProcessingProductManifestJson.Parse(bytes).RelativeArtifactPath; }
                catch (InvalidDataException)
                {
                    return FindSingleModernPayload(sidecar) is { } sibling ? [sidecar, sibling] : [sidecar];
                }
            }
            else
            {
                relative = HVO.SkyMonitor.AgentCore.CaptureContractJson.ParseManifest(bytes).Document?.Manifest?.RelativeArtifactPath;
            }
            var extension = sidecar.EndsWith(".manifest.json", StringComparison.Ordinal) ? ".manifest.json" : ".json";
            if (relative is not null && SqliteCaptureProcessingStore.IsCanonicalRelativePath(relative) &&
                relative.StartsWith("derived/", StringComparison.Ordinal) &&
                string.Equals(Relative(sidecar), Path.ChangeExtension(relative, extension), StringComparison.Ordinal))
                return [sidecar, ResolveDerived(relative)];
        }
        catch (IOException) { }
        return [sidecar];
    }

    private sealed class ReconciliationUnit(DerivedProductReconciler owner) : IDisposable
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
            Justification = "The per-root registry owns this shared gate; a unit borrows it and releases only its acquisition.")]
        private readonly SemaphoreSlim _gate = StorageLifecycleLock.ForRoot(owner._root);
        private bool _held;

        internal async ValueTask EnterAsync(CancellationToken cancellationToken)
        {
            await StorageLifecycleLock.WaitAsync(_gate, "reconciliation", owner._logger, cancellationToken, owner._timeProvider).ConfigureAwait(false);
            _held = true;
        }

        internal async ValueTask<(bool Current, T Value)> ObserveAsync<T>(
            Func<ValueTask<T>> observe, string[] paths, CancellationToken cancellationToken)
        {
            var identities = paths.Select(path => owner.Identity(path)).ToArray();
            _held = false;
            _gate.Release();
            T value;
            try
            {
                value = await observe().ConfigureAwait(false);
                if (owner._observationCompleted is not null) await owner._observationCompleted(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A concurrent retention/publication can remove a file while it is being read.
                await EnterAsync(cancellationToken).ConfigureAwait(false);
                if (!identities.SequenceEqual(paths.Select(path => owner.Identity(path)))) return (false, default!);
                throw;
            }
            await EnterAsync(cancellationToken).ConfigureAwait(false);
            return (identities.SequenceEqual(paths.Select(path => owner.Identity(path))), value);
        }

        public void Dispose()
        {
            if (_held) _gate.Release();
            _held = false;
        }
    }

    private static async Task<byte[]> ReadObservedBytesAsync(string path, CancellationToken cancellationToken)
    {
        // Retention/publication may unlink or atomically replace the path while this handle pins the observed bytes.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
    }

    private FileIdentity Identity(string path)
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        var info = new FileInfo(path);
        return info.Exists ? new(true, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) : default;
    }

    private readonly record struct FileIdentity(bool Exists, long Length, DateTime ModifiedUtc, DateTime CreatedUtc);
}
