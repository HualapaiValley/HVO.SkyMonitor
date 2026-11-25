using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public interface ICenteredViewPresentationReader
{
    ValueTask<CameraAgentLayeredPresentationResult> ReadAsync(
        string baseId, string revisionId, CancellationToken cancellationToken = default);

    ValueTask<CameraAgentArtifactContentResult> OpenPreviewAsync(
        string baseId, string revisionId, CancellationToken cancellationToken = default);

    ValueTask<CameraAgentArtifactContentResult> OpenBasePreviewAsync(
        string baseId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.NotFound));
}

public static class CenteredViewLinks
{
    private const string Route = "/api/v1/operations/gallery/centered";

    public static string Preview(string baseId, string revisionId) => $"{Route}/{baseId}/{revisionId}/preview?inline=true";
    public static string Presentation(string baseId, string revisionId) => $"{Route}/{baseId}/{revisionId}/presentation.svg";
    public static string Base(Guid referenceCaptureId, string baseId) =>
        $"/api/v1/operations/gallery/{referenceCaptureId:D}/views/{baseId}";
    public static string Revision(Guid referenceCaptureId, string baseId, string revisionId) =>
        $"/api/v1/operations/gallery/{referenceCaptureId:D}/views/{baseId}/{revisionId}";
    public static string Archive(Guid referenceCaptureId, string baseId, string revisionId) =>
        $"/gallery/{referenceCaptureId:D}?base={Uri.EscapeDataString(baseId)}&revision={Uri.EscapeDataString(revisionId)}";
    public static string ArchiveSelection(Guid referenceCaptureId, Guid selectionId) =>
        $"/gallery/{referenceCaptureId:D}?selection={selectionId:D}";

    public static bool IsIdentity(string? value) =>
        value is { Length: 64 } && value.All(static character => char.IsAsciiDigit(character) || character is >= 'A' and <= 'F');
}

internal sealed class CenteredViewPresentationReader : ICenteredViewPresentationReader
{
    internal static readonly TimeSpan DefaultOptionalLayerReadTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DefaultOverallOptionalLayersReadTimeout = TimeSpan.FromMilliseconds(3500);
    internal const int DefaultMaximumConcurrentLayerReads = 4;

    private readonly ICenteredViewReadIndex _index;
    private readonly ICameraAgentArtifactService _artifacts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _optionalLayerReadTimeout;
    private readonly TimeSpan _overallOptionalLayersReadTimeout;
    private readonly int _maximumConcurrentLayerReads;
    private readonly int _maximumSourcePayloadBytes;
    private readonly Func<int, byte[]> _allocateBuffer;
    private readonly LayerReadGate _layerReadGate;

    public CenteredViewPresentationReader(
        ICenteredViewReadIndex index,
        ICameraAgentArtifactService artifacts,
        TimeProvider? timeProvider = null)
        : this(
            index,
            artifacts,
            timeProvider ?? TimeProvider.System,
            DefaultOptionalLayerReadTimeout,
            DefaultMaximumConcurrentLayerReads,
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
            DefaultOverallOptionalLayersReadTimeout)
    {
    }

    internal CenteredViewPresentationReader(
        ICenteredViewReadIndex index,
        ICameraAgentArtifactService artifacts,
        TimeProvider timeProvider,
        TimeSpan optionalLayerReadTimeout,
        int maximumConcurrentLayerReads = DefaultMaximumConcurrentLayerReads,
        int maximumSourcePayloadBytes = CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
        TimeSpan? overallOptionalLayersReadTimeout = null,
        Func<int, byte[]>? bufferAllocator = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumConcurrentLayerReads, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSourcePayloadBytes, 0);
        var resolvedOverallTimeout = overallOptionalLayersReadTimeout
            ?? (optionalLayerReadTimeout == Timeout.InfiniteTimeSpan || optionalLayerReadTimeout > DefaultOverallOptionalLayersReadTimeout
                ? optionalLayerReadTimeout
                : DefaultOverallOptionalLayersReadTimeout);
        if (resolvedOverallTimeout != Timeout.InfiniteTimeSpan && resolvedOverallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(overallOptionalLayersReadTimeout));
        }
        _index = index;
        _artifacts = artifacts;
        _timeProvider = timeProvider;
        _optionalLayerReadTimeout = optionalLayerReadTimeout;
        _overallOptionalLayersReadTimeout = resolvedOverallTimeout;
        _maximumConcurrentLayerReads = maximumConcurrentLayerReads;
        _maximumSourcePayloadBytes = maximumSourcePayloadBytes;
        _allocateBuffer = bufferAllocator ?? (static length => new byte[length]);
        _layerReadGate = new LayerReadGate(maximumConcurrentLayerReads);
    }

    internal int PendingLayerReadWaiterCount => _layerReadGate.PendingWaiterCount;

    internal int AvailableLayerReadPermits => _layerReadGate.AvailablePermits;

    public async ValueTask<CameraAgentArtifactContentResult> OpenPreviewAsync(
        string baseId, string revisionId, CancellationToken cancellationToken = default)
    {
        var view = await ReadViewAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);
        if (view is null)
            return new(CameraAgentArtifactReadStatus.NotFound);

        return await OpenBoundPreviewAsync(view.Base, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CameraAgentArtifactContentResult> OpenBasePreviewAsync(
        string baseId, CancellationToken cancellationToken = default)
    {
        var viewBase = CenteredViewLinks.IsIdentity(baseId)
            ? await _index.ReadBaseAsync(baseId, cancellationToken).ConfigureAwait(false)
            : null;
        if (viewBase is null)
            return new(CameraAgentArtifactReadStatus.NotFound);

        return await OpenBoundPreviewAsync(viewBase, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CameraAgentLayeredPresentationResult> ReadAsync(
        string baseId, string revisionId, CancellationToken cancellationToken = default)
    {
        using var overallDeadline = new CancellationTokenSource(_overallOptionalLayersReadTimeout, _timeProvider);
        var view = await ReadViewAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);
        if (view is null)
            return new(CameraAgentLayeredPresentationStatus.NotRetained);

        var manifest = view.Revision.Manifest;
        try
        {
            LayeredPresentationJson.Validate(manifest);
            if (manifest.BaseProduct != view.Base.Definition.Preview.Product)
                return new(CameraAgentLayeredPresentationStatus.Malformed);
            var preview = await OpenBoundPreviewAsync(view.Base, cancellationToken).ConfigureAwait(false);
            if (preview.Content is not { } previewContent)
                return new(preview.Status == CameraAgentArtifactReadStatus.Conflict
                    ? CameraAgentLayeredPresentationStatus.Malformed : CameraAgentLayeredPresentationStatus.Unavailable);
            await previewContent.DisposeAsync().ConfigureAwait(false);

            var referenceCaptureId = view.Base.Definition.Reference.Capture.CaptureId;
            var validLayers = new List<PresentationLayerV1>(manifest.Layers.Count);
            var payloads = new List<PresentationLayerPayloadV1>(manifest.Layers.Count);
            if (manifest.Layers.Count > 0 && !overallDeadline.IsCancellationRequested)
            {
                using var overallBounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overallDeadline.Token);
                var overallToken = overallBounded.Token;
                var budget = new PayloadBudget(_maximumSourcePayloadBytes);
                var results = new PresentationLayerPayloadV1?[manifest.Layers.Count];
                var workerCount = Math.Min(manifest.Layers.Count, _maximumConcurrentLayerReads);
                var nextIndex = -1;
                var workers = new Task[workerCount];
                for (var worker = 0; worker < workerCount; worker++)
                {
                    workers[worker] = RunLayerWorkerAsync();
                }

                await Task.WhenAll(workers).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                for (var index = 0; index < results.Length; index++)
                {
                    if (results[index] is not { } payload)
                    {
                        continue;
                    }
                    validLayers.Add(manifest.Layers[index]);
                    payloads.Add(payload);
                }

                async Task RunLayerWorkerAsync()
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (overallDeadline.IsCancellationRequested)
                        {
                            return;
                        }
                        var index = Interlocked.Increment(ref nextIndex);
                        if (index >= manifest.Layers.Count)
                        {
                            return;
                        }
                        results[index] = await TryReadOptionalLayerAsync(
                            manifest.Layers[index],
                            manifest,
                            referenceCaptureId,
                            budget,
                            overallToken,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            var effectiveManifest = validLayers.Count == manifest.Layers.Count
                ? manifest
                : manifest with { Layers = validLayers.AsReadOnly() };
            var rendered = CameraAgentLayeredPresentationService.Render(
                referenceCaptureId, effectiveManifest, payloads, view.Base.Definition.Preview.PayloadSha256);
            if (rendered.Status == CameraAgentLayeredPresentationStatus.Found || validLayers.Count == 0)
            {
                return rendered;
            }

            return CameraAgentLayeredPresentationService.Render(
                referenceCaptureId, manifest with { Layers = [] }, [], view.Base.Definition.Preview.PayloadSha256);
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or InvalidDataException)
        {
            return new(CameraAgentLayeredPresentationStatus.Malformed);
        }
        catch (IOException)
        {
            return new(CameraAgentLayeredPresentationStatus.Unavailable);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "Timed-out optional layer opens and reads are detached with best-effort stream disposal, budget release, and concurrency permit release so healthy sibling layers are never blocked and concurrency never exceeds the cap.")]
    private async ValueTask<PresentationLayerPayloadV1?> TryReadOptionalLayerAsync(
        PresentationLayerV1 layer,
        OverlayManifestV1 manifest,
        Guid referenceCaptureId,
        PayloadBudget budget,
        CancellationToken overallToken,
        CancellationToken callerCancellationToken)
    {
        if (layer.CoordinateSpace != PresentationCoordinateSpace.ScenePixels)
            return null;

        if (!await _layerReadGate.TryAcquireAsync(overallToken).ConfigureAwait(false))
        {
            callerCancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        var permitOwned = true;
        using var layerDeadline = new CancellationTokenSource(_optionalLayerReadTimeout, _timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(overallToken, layerDeadline.Token);
        var layerToken = bounded.Token;
        CameraAgentArtifactContentStream? content = null;
        var reservedBytes = 0;
        var committed = false;
        try
        {
            // Offload invoking OpenContentAsync onto the thread pool so even a synchronous stall
            // before returning a Task is bounded by layerToken and never blocks healthy siblings.
            var openTask = Task.Run(
                () => _artifacts.OpenContentAsync(layer.SourceProduct.ArtifactId, layerToken).AsTask(),
                CancellationToken.None);
            CameraAgentArtifactContentResult opened;
            try
            {
                opened = await openTask.WaitAsync(layerToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                permitOwned = false;
                _ = DisposeLateOpenedContentAsync(openTask, _layerReadGate);
                throw;
            }
            content = opened.Content;
            if (content is null || opened.Status != CameraAgentArtifactReadStatus.Found)
                return null;
            if (content.MediaType != layer.SourceProduct.MediaType || content.Role != FrameArtifactRole.Metadata ||
                content.CaptureId != referenceCaptureId)
                return null;
            if (content.ByteLength is < 1 or > PresentationLayerPayloadJson.MaximumPayloadBytes)
                return null;
            var byteLength = checked((int)content.ByteLength);
            if (!budget.TryReserve(byteLength))
                return null;
            reservedBytes = byteLength;

            var bytes = _allocateBuffer(byteLength);
            var stream = content;
            var readTask = Task.Run(
                () => stream.ReadExactlyAsync(bytes, layerToken).AsTask(),
                CancellationToken.None);
            try
            {
                await readTask.WaitAsync(layerToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var detachedStream = content;
                var detachedBytes = reservedBytes;
                content = null;
                reservedBytes = 0;
                permitOwned = false;
                _ = DisposeAfterReadAsync(readTask, detachedStream, budget, detachedBytes, _layerReadGate);
                throw;
            }
            var payload = PresentationLayerPayloadJson.Parse(bytes).Payload;
            if (payload is null || payload.ContentIdentitySha256 != layer.SourceProduct.ProductIdentitySha256 ||
                payload.WidthPixels != manifest.BaseProduct.Compatibility.WidthPixels ||
                payload.HeightPixels != manifest.BaseProduct.Compatibility.HeightPixels)
                return null;
            payload.ValidateStructure();
            if (!SceneStylePresentation.MatchesRetainedLayer(payload, layer))
                return null;
            committed = true;
            return payload;
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException or InvalidDataException or JsonException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (!committed && reservedBytes > 0)
                {
                    budget.Release(reservedBytes);
                }
                if (content is not null)
                {
                    var disposedInTime = await TryDisposeOptionalLayerContentAsync(
                        content,
                        _layerReadGate,
                        layerToken).ConfigureAwait(false);
                    if (!disposedInTime)
                    {
                        permitOwned = false;
                    }
                }
            }
            finally
            {
                if (permitOwned)
                {
                    _layerReadGate.Release();
                }
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort disposal of an optional layer stream must never discard an otherwise valid base or layer result or surface unobserved task exceptions.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "Stalled optional layer stream disposal is detached while retaining the concurrency gate permit until disposal actually completes.")]
    private static async ValueTask<bool> TryDisposeOptionalLayerContentAsync(
        CameraAgentArtifactContentStream content,
        LayerReadGate gate,
        CancellationToken layerToken)
    {
        var disposeTask = Task.Run(() => content.DisposeAsync().AsTask(), CancellationToken.None);
        try
        {
            await disposeTask.WaitAsync(layerToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (layerToken.IsCancellationRequested)
        {
            _ = ReleaseGateAfterDisposeAsync(disposeTask, gate);
            return false;
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            return true;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort detached disposal of an optional layer stream must never surface unobserved task exceptions.")]
    private static async Task ReleaseGateAfterDisposeAsync(
        Task disposeTask,
        LayerReadGate gate)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            // Detached optional layer disposal already degraded cleanly.
        }
        finally
        {
            gate.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort disposal of a timed-out optional layer stream must never surface unobserved task exceptions.")]
    internal static Task DisposeLateOpenedContentAsync(
        Task<CameraAgentArtifactContentResult> openTask,
        LayerReadGate gate) =>
        Task.Run(
            async () =>
            {
                try
                {
                    var result = await openTask.ConfigureAwait(false);
                    if (result.Content is { } content)
                    {
                        await Task.Run(() => content.DisposeAsync().AsTask(), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
                {
                    // Timed-out optional layer open already degraded cleanly.
                }
                finally
                {
                    gate.Release();
                }
            },
            CancellationToken.None);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort disposal and budget release after a timed-out layer read must never surface unobserved task exceptions.")]
    internal static Task DisposeAfterReadAsync(
        Task readTask,
        CameraAgentArtifactContentStream content,
        PayloadBudget budget,
        int reservedBytes,
        LayerReadGate gate) =>
        Task.Run(
            async () =>
            {
                try
                {
                    await readTask.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
                {
                    // Timed-out optional layer read already degraded cleanly.
                }
                finally
                {
                    try
                    {
                        await Task.Run(() => content.DisposeAsync().AsTask(), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
                    {
                    }
                    finally
                    {
                        budget.Release(reservedBytes);
                        gate.Release();
                    }
                }
            },
            CancellationToken.None);

    internal sealed class LayerReadGate(int maximumPermits)
    {
        private readonly Lock _gate = new();
        private readonly LinkedList<Waiter> _waiters = new();
        private int _available = maximumPermits;

        public int AvailablePermits
        {
            get
            {
                lock (_gate)
                {
                    return _available;
                }
            }
        }

        public int PendingWaiterCount
        {
            get
            {
                lock (_gate)
                {
                    return _waiters.Count;
                }
            }
        }

        public ValueTask<bool> TryAcquireAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(false);
            }

            Waiter waiter;
            lock (_gate)
            {
                if (_available > 0)
                {
                    _available--;
                    return ValueTask.FromResult(true);
                }
                waiter = new Waiter(this);
                waiter.SetNodeLocked(_waiters.AddLast(waiter));
            }

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(
                    static state => ((Waiter)state!).Cancel(),
                    waiter);
                waiter.AttachRegistration(registration);
            }

            return new ValueTask<bool>(waiter.Task);
        }

        public void Release()
        {
            Waiter? toGrant = null;
            CancellationTokenRegistration registrationToDispose = default;
            lock (_gate)
            {
                while (_waiters.First is { } first)
                {
                    _waiters.RemoveFirst();
                    var candidate = first.Value;
                    candidate.ClearNodeLocked();
                    if (candidate.TryGrantLocked(out registrationToDispose))
                    {
                        toGrant = candidate;
                        break;
                    }
                }

                if (toGrant is null)
                {
                    _available++;
                }
            }

            registrationToDispose.Dispose();
            toGrant?.Complete(granted: true);
        }

        private void RemoveLocked(LinkedListNode<Waiter> node)
        {
            if (node.List == _waiters)
            {
                _waiters.Remove(node);
            }
        }

        private sealed class Waiter(LayerReadGate owner)
        {
            private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private LinkedListNode<Waiter>? _node;
            private CancellationTokenRegistration _registration;
            private bool _completed;

            public Task<bool> Task => _tcs.Task;

            public void SetNodeLocked(LinkedListNode<Waiter> node) => _node = node;

            public void ClearNodeLocked() => _node = null;

            public void AttachRegistration(CancellationTokenRegistration registration)
            {
                var disposeNow = false;
                lock (owner._gate)
                {
                    if (_completed)
                    {
                        disposeNow = true;
                    }
                    else
                    {
                        _registration = registration;
                    }
                }
                if (disposeNow)
                {
                    registration.Dispose();
                }
            }

            public bool TryGrantLocked(out CancellationTokenRegistration registration)
            {
                if (_completed)
                {
                    registration = default;
                    return false;
                }
                _completed = true;
                registration = _registration;
                _registration = default;
                return true;
            }

            public void Cancel()
            {
                CancellationTokenRegistration registration;
                lock (owner._gate)
                {
                    if (_completed)
                    {
                        return;
                    }
                    _completed = true;
                    if (_node is { } node)
                    {
                        owner.RemoveLocked(node);
                        _node = null;
                    }
                    registration = _registration;
                    _registration = default;
                }
                registration.Dispose();
                _tcs.TrySetResult(false);
            }

            public void Complete(bool granted) => _tcs.TrySetResult(granted);
        }
    }

    internal sealed class PayloadBudget(int maximumBytes)
    {
        private int _remaining = maximumBytes;

        public int RemainingBytes => Volatile.Read(ref _remaining);

        public bool TryReserve(int bytes)
        {
            if (bytes <= 0)
            {
                return false;
            }
            while (true)
            {
                var current = Volatile.Read(ref _remaining);
                if (bytes > current)
                {
                    return false;
                }
                if (Interlocked.CompareExchange(ref _remaining, current - bytes, current) == current)
                {
                    return true;
                }
            }
        }

        public void Release(int bytes)
        {
            if (bytes > 0)
            {
                Interlocked.Add(ref _remaining, bytes);
            }
        }
    }

    private ValueTask<CenteredView?> ReadViewAsync(string baseId, string revisionId, CancellationToken cancellationToken)
        => CenteredViewLinks.IsIdentity(baseId) && CenteredViewLinks.IsIdentity(revisionId)
            ? _index.ReadRevisionAsync(baseId, revisionId, cancellationToken) : ValueTask.FromResult<CenteredView?>(null);

    private async ValueTask<CameraAgentArtifactContentResult> OpenBoundPreviewAsync(CenteredViewBase viewBase, CancellationToken token)
    {
        var preview = viewBase.Definition.Preview;
        var opened = await _artifacts.OpenContentAsync(preview.Product.ArtifactId, token).ConfigureAwait(false);
        if (opened.Content is not { } content)
            return opened;
        if (content.CaptureId == viewBase.Definition.Reference.Capture.CaptureId && content.Role == FrameArtifactRole.Preview &&
            content.MediaType == preview.Product.MediaType && content.ChecksumSha256 == preview.PayloadSha256 &&
            content.EncodedWidth == preview.Transform.PreviewWidth && content.EncodedHeight == preview.Transform.PreviewHeight)
            return opened;

        await content.DisposeAsync().ConfigureAwait(false);
        return new(CameraAgentArtifactReadStatus.Conflict);
    }
}
