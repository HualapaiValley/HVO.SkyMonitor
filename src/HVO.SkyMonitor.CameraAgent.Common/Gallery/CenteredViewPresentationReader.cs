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
    internal const int DefaultMaximumConcurrentLayerReads = 4;

    private readonly ICenteredViewReadIndex _index;
    private readonly ICameraAgentArtifactService _artifacts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _optionalLayerReadTimeout;
    private readonly int _maximumConcurrentLayerReads;
    private readonly int _maximumSourcePayloadBytes;

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
            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes)
    {
    }

    internal CenteredViewPresentationReader(
        ICenteredViewReadIndex index,
        ICameraAgentArtifactService artifacts,
        TimeProvider timeProvider,
        TimeSpan optionalLayerReadTimeout,
        int maximumConcurrentLayerReads = DefaultMaximumConcurrentLayerReads,
        int maximumSourcePayloadBytes = CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumConcurrentLayerReads, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSourcePayloadBytes, 0);
        _index = index;
        _artifacts = artifacts;
        _timeProvider = timeProvider;
        _optionalLayerReadTimeout = optionalLayerReadTimeout;
        _maximumConcurrentLayerReads = maximumConcurrentLayerReads;
        _maximumSourcePayloadBytes = maximumSourcePayloadBytes;
    }

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
            if (manifest.Layers.Count > 0)
            {
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
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "Timed-out optional layer opens and reads are detached with best-effort stream disposal so healthy sibling layers are never blocked.")]
    private async ValueTask<PresentationLayerPayloadV1?> TryReadOptionalLayerAsync(
        PresentationLayerV1 layer,
        OverlayManifestV1 manifest,
        Guid referenceCaptureId,
        PayloadBudget budget,
        CancellationToken cancellationToken)
    {
        using var layerDeadline = new CancellationTokenSource(_optionalLayerReadTimeout, _timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, layerDeadline.Token);
        var layerToken = bounded.Token;
        CameraAgentArtifactContentStream? content = null;
        var reservedBytes = 0;
        var committed = false;
        try
        {
            if (layer.CoordinateSpace != PresentationCoordinateSpace.ScenePixels)
                return null;

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
                _ = DisposeLateOpenedContentAsync(openTask);
                throw;
            }
            if (opened.Content is not { } openedContent || opened.Status != CameraAgentArtifactReadStatus.Found)
                return null;
            content = openedContent;
            if (content.MediaType != layer.SourceProduct.MediaType || content.Role != FrameArtifactRole.Metadata ||
                content.CaptureId != referenceCaptureId)
                return null;
            if (content.ByteLength is < 1 or > PresentationLayerPayloadJson.MaximumPayloadBytes)
                return null;
            var byteLength = checked((int)content.ByteLength);
            if (!budget.TryReserve(byteLength))
                return null;
            reservedBytes = byteLength;

            var bytes = new byte[byteLength];
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
                _ = DisposeAfterReadAsync(readTask, detachedStream, budget, detachedBytes);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            if (content is not null)
            {
                await content.DisposeAsync().ConfigureAwait(false);
            }
            if (!committed && reservedBytes > 0)
            {
                budget.Release(reservedBytes);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort disposal of a timed-out optional layer stream must never surface unobserved task exceptions.")]
    private static async Task DisposeLateOpenedContentAsync(Task<CameraAgentArtifactContentResult> openTask)
    {
        try
        {
            var result = await openTask.ConfigureAwait(false);
            if (result.Content is { } content)
            {
                await content.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Timed-out optional layer open already degraded cleanly.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort disposal and budget release after a timed-out layer read must never surface unobserved task exceptions.")]
    private static async Task DisposeAfterReadAsync(
        Task readTask,
        CameraAgentArtifactContentStream content,
        PayloadBudget budget,
        int reservedBytes)
    {
        try
        {
            await readTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed-out optional layer read already degraded cleanly.
        }
        finally
        {
            try
            {
                await content.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            budget.Release(reservedBytes);
        }
    }

    private sealed class PayloadBudget(int maximumBytes)
    {
        private int _remaining = maximumBytes;

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
