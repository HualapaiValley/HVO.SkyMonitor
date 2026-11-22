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
}

public static class CenteredViewLinks
{
    private const string Route = "/api/v1/operations/gallery/centered";

    public static string Preview(string baseId, string revisionId) => $"{Route}/{baseId}/{revisionId}/preview?inline=true";
    public static string Presentation(string baseId, string revisionId) => $"{Route}/{baseId}/{revisionId}/presentation.svg";
    public static string Revision(Guid referenceCaptureId, string baseId, string revisionId) =>
        $"/api/v1/operations/gallery/{referenceCaptureId:D}/views/{baseId}/{revisionId}";

    public static bool IsIdentity(string? value) =>
        value is { Length: 64 } && value.All(static character => char.IsAsciiDigit(character) || character is >= 'A' and <= 'F');
}

internal sealed class CenteredViewPresentationReader(
    ICenteredViewReadIndex index, ICameraAgentArtifactService artifacts) : ICenteredViewPresentationReader
{
    public async ValueTask<CameraAgentArtifactContentResult> OpenPreviewAsync(
        string baseId, string revisionId, CancellationToken cancellationToken = default)
    {
        var view = await ReadViewAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);
        if (view is null)
            return new(CameraAgentArtifactReadStatus.NotFound);

        return await OpenBoundPreviewAsync(view, cancellationToken).ConfigureAwait(false);
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
            var preview = await OpenBoundPreviewAsync(view, cancellationToken).ConfigureAwait(false);
            if (preview.Content is not { } previewContent)
                return new(preview.Status == CameraAgentArtifactReadStatus.Conflict
                    ? CameraAgentLayeredPresentationStatus.Malformed : CameraAgentLayeredPresentationStatus.Unavailable);
            await previewContent.DisposeAsync().ConfigureAwait(false);

            var budget = CameraAgentLayeredPresentationService.MaximumSourcePayloadBytes;
            var payloads = new List<PresentationLayerPayloadV1>(manifest.Layers.Count);
            foreach (var layer in manifest.Layers)
            {
                var opened = await artifacts.OpenContentAsync(layer.SourceProduct.ArtifactId, cancellationToken).ConfigureAwait(false);
                if (opened.Content is not { } content || opened.Status != CameraAgentArtifactReadStatus.Found)
                    return new(CameraAgentLayeredPresentationStatus.Unavailable);
                await using var lease = content.ConfigureAwait(false);
                if (content.MediaType != layer.SourceProduct.MediaType || content.Role != FrameArtifactRole.Metadata ||
                    content.CaptureId != view.Base.Definition.Reference.Capture.CaptureId)
                    return new(CameraAgentLayeredPresentationStatus.Malformed);
                if (content.ByteLength is < 1 or > PresentationLayerPayloadJson.MaximumPayloadBytes || content.ByteLength > budget)
                    return new(CameraAgentLayeredPresentationStatus.TooLarge);
                var bytes = new byte[checked((int)content.ByteLength)];
                await content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                budget -= bytes.Length;
                var payload = PresentationLayerPayloadJson.Parse(bytes).Payload;
                if (payload is null || payload.ContentIdentitySha256 != layer.SourceProduct.ProductIdentitySha256 ||
                    payload.WidthPixels != manifest.BaseProduct.Compatibility.WidthPixels ||
                    payload.HeightPixels != manifest.BaseProduct.Compatibility.HeightPixels)
                    return new(CameraAgentLayeredPresentationStatus.Malformed);
                payloads.Add(payload);
            }
            return CameraAgentLayeredPresentationService.Render(view.Base.Definition.Reference.Capture.CaptureId,
                manifest, payloads, view.Base.Definition.Preview.PayloadSha256);
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

    private ValueTask<CenteredView?> ReadViewAsync(string baseId, string revisionId, CancellationToken cancellationToken)
        => CenteredViewLinks.IsIdentity(baseId) && CenteredViewLinks.IsIdentity(revisionId)
            ? index.ReadRevisionAsync(baseId, revisionId, cancellationToken) : ValueTask.FromResult<CenteredView?>(null);

    private async ValueTask<CameraAgentArtifactContentResult> OpenBoundPreviewAsync(CenteredView view, CancellationToken token)
    {
        var preview = view.Base.Definition.Preview;
        var opened = await artifacts.OpenContentAsync(preview.Product.ArtifactId, token).ConfigureAwait(false);
        if (opened.Content is not { } content)
            return opened;
        if (content.CaptureId == view.Base.Definition.Reference.Capture.CaptureId && content.Role == FrameArtifactRole.Preview &&
            content.MediaType == preview.Product.MediaType && content.ChecksumSha256 == preview.PayloadSha256 &&
            content.EncodedWidth == preview.Transform.PreviewWidth && content.EncodedHeight == preview.Transform.PreviewHeight)
            return opened;

        await content.DisposeAsync().ConfigureAwait(false);
        return new(CameraAgentArtifactReadStatus.Conflict);
    }
}
