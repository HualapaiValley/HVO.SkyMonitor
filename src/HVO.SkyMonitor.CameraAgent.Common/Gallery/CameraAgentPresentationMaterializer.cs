using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public enum CameraAgentPresentationMaterializationStatus
{
    Saved,
    Unavailable,
    Invalid,
    Conflict
}

public sealed record CameraAgentPresentationMaterializationReceipt(
    Guid CaptureId,
    Guid ArtifactId,
    string OutputIdentitySha256,
    string ChecksumSha256,
    long ByteLength,
    bool Replayed);

public sealed record CameraAgentPresentationMaterializationResult(
    CameraAgentPresentationMaterializationStatus Status,
    CameraAgentPresentationMaterializationReceipt? Receipt = null,
    string? Reason = null);

public interface ICameraAgentPresentationMaterializer
{
    ValueTask<CameraAgentPresentationMaterializationResult> SaveAsync(
        Guid captureId,
        IReadOnlyList<string> enabledLayerIdentitySha256,
        string actor,
        CancellationToken cancellationToken);
}

internal sealed class CameraAgentPresentationMaterializer(
    SqliteCaptureProcessingStore processingStore,
    CaptureProcessingPersistence persistence) : ICameraAgentPresentationMaterializer
{
    public async ValueTask<CameraAgentPresentationMaterializationResult> SaveAsync(
        Guid captureId,
        IReadOnlyList<string> enabledLayerIdentitySha256,
        string actor,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty || enabledLayerIdentitySha256 is null ||
            enabledLayerIdentitySha256.Count > LayeredPresentationJson.MaximumLayerCount ||
            enabledLayerIdentitySha256.Distinct(StringComparer.Ordinal).Count() != enabledLayerIdentitySha256.Count ||
            enabledLayerIdentitySha256.Any(static identity => identity is not { Length: 64 } ||
                identity.Any(static character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
        {
            return new(CameraAgentPresentationMaterializationStatus.Invalid,
                Reason: "The selected presentation stack is invalid.");
        }

        try
        {
            var manifests = await processingStore.ReadCaptureProductsAsync(
                captureId, OverlayManifestV1.CurrentSchemaVersion, 2, cancellationToken).ConfigureAwait(false);
            if (manifests.Count == 0)
            {
                return new(CameraAgentPresentationMaterializationStatus.Unavailable,
                    Reason: "Structured layers were not retained for this capture.");
            }
            if (manifests.Count != 1 || manifests[0].AvailabilityState != "Available")
            {
                return new(CameraAgentPresentationMaterializationStatus.Conflict,
                    Reason: "The retained layer manifest is ambiguous or unavailable.");
            }
            var saved = await persistence.MaterializePresentationAsync(
                captureId, manifests[0].ArtifactId, enabledLayerIdentitySha256, actor, cancellationToken)
                .ConfigureAwait(false);
            return new(CameraAgentPresentationMaterializationStatus.Saved, new(
                captureId, saved.ArtifactId, saved.OutputIdentitySha256, saved.ChecksumSha256,
                saved.ByteLength, saved.Replayed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new(CameraAgentPresentationMaterializationStatus.Invalid,
                Reason: "The selected presentation stack is invalid.");
        }
        catch (InvalidDataException)
        {
            return new(CameraAgentPresentationMaterializationStatus.Conflict,
                Reason: "The retained presentation evidence conflicts with the requested stack.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(CameraAgentPresentationMaterializationStatus.Unavailable,
                Reason: "The presentation stack could not be saved.");
        }
    }
}
