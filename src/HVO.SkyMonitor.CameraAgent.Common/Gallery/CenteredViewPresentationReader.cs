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

    private readonly ICenteredViewReadIndex _index;
    private readonly ICameraAgentArtifactService _artifacts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _optionalLayerReadTimeout;

    public CenteredViewPresentationReader(
        ICenteredViewReadIndex index,
        ICameraAgentArtifactService artifacts,
        TimeProvider? timeProvider = null)
        : this(index, artifacts, timeProvider ?? TimeProvider.System, DefaultOptionalLayerReadTimeout)
    {
    }

    internal CenteredViewPresentationReader(
        ICenteredViewReadIndex index,
        ICameraAgentArtifactService artifacts,
        TimeProvider timeProvider,
        TimeSpan optionalLayerReadTimeout)
    {
        _index = index;
        _artifacts = artifacts;
        _timeProvider = timeProvider;
        _optionalLayerReadTimeout = optionalLayerReadTimeout;
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

            var budget = CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes;
            var referenceCaptureId = view.Base.Definition.Reference.Capture.CaptureId;
            var validLayers = new List<PresentationLayerV1>(manifest.Layers.Count);
            var payloads = new List<PresentationLayerPayloadV1>(manifest.Layers.Count);
            if (manifest.Layers.Count > 0)
            {
                var layerTasks = new Task<(PresentationLayerPayloadV1? Payload, int ConsumedBytes)>[manifest.Layers.Count];
                for (var index = 0; index < manifest.Layers.Count; index++)
                {
                    var layer = manifest.Layers[index];
                    layerTasks[index] = Task.Run(
                        () => TryReadOptionalLayerAsync(
                            layer,
                            manifest,
                            referenceCaptureId,
                            CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes,
                            cancellationToken).AsTask(),
                        CancellationToken.None);
                }

                var results = await Task.WhenAll(layerTasks).ConfigureAwait(false);
                for (var index = 0; index < results.Length; index++)
                {
                    var (payload, consumedBytes) = results[index];
                    if (payload is null || consumedBytes > budget)
                    {
                        continue;
                    }
                    budget -= consumedBytes;
                    validLayers.Add(manifest.Layers[index]);
                    payloads.Add(payload);
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "Timed-out optional layer open is detached with best-effort stream disposal so healthy sibling layers are never blocked.")]
    private async ValueTask<(PresentationLayerPayloadV1? Payload, int ConsumedBytes)> TryReadOptionalLayerAsync(
        PresentationLayerV1 layer,
        OverlayManifestV1 manifest,
        Guid referenceCaptureId,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var layerDeadline = new CancellationTokenSource(_optionalLayerReadTimeout, _timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, layerDeadline.Token);
        var layerToken = bounded.Token;
        try
        {
            if (layer.CoordinateSpace != PresentationCoordinateSpace.ScenePixels)
                return (null, 0);

            var openTask = _artifacts.OpenContentAsync(layer.SourceProduct.ArtifactId, layerToken).AsTask();
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
            if (opened.Content is not { } content || opened.Status != CameraAgentArtifactReadStatus.Found)
                return (null, 0);
            await using var lease = content.ConfigureAwait(false);
            if (content.MediaType != layer.SourceProduct.MediaType || content.Role != FrameArtifactRole.Metadata ||
                content.CaptureId != referenceCaptureId)
                return (null, 0);
            if (content.ByteLength is < 1 or > PresentationLayerPayloadJson.MaximumPayloadBytes || content.ByteLength > maximumBytes)
                return (null, 0);
            var bytes = new byte[checked((int)content.ByteLength)];
            await content.ReadExactlyAsync(bytes, layerToken).AsTask().WaitAsync(layerToken).ConfigureAwait(false);
            var payload = PresentationLayerPayloadJson.Parse(bytes).Payload;
            if (payload is null || payload.ContentIdentitySha256 != layer.SourceProduct.ProductIdentitySha256 ||
                payload.WidthPixels != manifest.BaseProduct.Compatibility.WidthPixels ||
                payload.HeightPixels != manifest.BaseProduct.Compatibility.HeightPixels)
                return (null, 0);
            payload.ValidateStructure();
            if (!SceneStylePresentation.MatchesRetainedLayer(payload, layer))
                return (null, 0);
            return (payload, bytes.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException or InvalidDataException or JsonException)
        {
            return (null, 0);
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
