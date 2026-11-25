using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    internal async ValueTask<(CenteredViewArtifact Pin, ProcessingArtifact Artifact)?> TryReadMeasuredDeepSkyAsync(
        Guid captureId, string outputIdentity, CancellationToken cancellationToken)
    {
        var artifactId = ProcessingIdentity.CreateArtifactId(outputIdentity);
        var output = await _store.ReadOutputByArtifactIdAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }
        if (output.Capture.CaptureId != captureId || output.OutputIdentitySha256 != outputIdentity ||
            output.AvailabilityState != "Available")
        {
            throw new InvalidDataException("A committed assessment conflicts with the selected centered stack.");
        }
        var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
        return (new(artifactId, outputIdentity, output.Artifact.ChecksumSha256), CreateProcessingArtifact(output, restored));
    }
}
