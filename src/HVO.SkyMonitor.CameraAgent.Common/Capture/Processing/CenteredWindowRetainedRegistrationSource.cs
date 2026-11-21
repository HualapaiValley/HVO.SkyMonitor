using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CenteredWindowRetainedRegistrationSource(
    IExposureProductReader exposures,
    RawCaptureIngress ingress,
    CaptureProcessingPersistence persistence) : ICenteredWindowRegistrationSource
{
    private readonly CenteredStackRetainedInputReader _inputs = new(exposures, async (exposure, cancellationToken) =>
    {
        var raw = await ingress.ReadCenteredRawAsync(exposure.Key, cancellationToken).ConfigureAwait(false);
        if (raw is not { IsCommitted: true } ||
            CaptureContractJson.ComputeDescriptorSha256(raw.Manifest.Descriptor) != CaptureContractJson.ComputeDescriptorSha256(exposure.Raw))
            return null;
        return ResolveGeometry(raw);
    });

    public ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(
        ExposureKey key, ProcessingArtifact linear, CancellationToken cancellationToken) =>
        _inputs.ReadLinearAsync(key, linear, cancellationToken);

    public ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken) =>
        exposures.ReadExposureAsync(key, cancellationToken);

    internal static CenteredStackMeasurementGeometry ResolveGeometry(CenteredRawEvidence raw)
    {
        var descriptor = raw.Manifest.Descriptor;
        var rig = raw.Run.Configuration.Rig;
        var native = RigProjectionContextFactory.CreateNative(rig);
        ProjectionContext? aperture = descriptor.Layout.Readout is { } readout
            ? readout.NativeWidth == native.WidthPixels && readout.NativeHeight == native.HeightPixels
                ? RigProjectionContextFactory.CreateReadoutView(native, readout) : null
            : native.WidthPixels == descriptor.Layout.Width && native.HeightPixels == descriptor.Layout.Height ? native : null;
        return new(rig.Sensor.PixelSizeMicrons > 0 ? rig.Sensor.PixelSizeMicrons : null, aperture);
    }

    public async ValueTask<ProcessingArtifact?> ReadArtifactAsync(
        ExposureKey key, CenteredStackArtifactPin pin, CancellationToken cancellationToken)
    {
        var found = await ingress.ReadCenteredRawAsync(key, cancellationToken).ConfigureAwait(false);
        if (found is not { IsCommitted: true })
            return null;
        var raw = found.Manifest.Descriptor;
        if (raw.Artifact.ArtifactId != pin.ArtifactId)
            return await persistence.ReadCenteredRetainedProductAsync(key, pin, cancellationToken).ConfigureAwait(false);

        var header = CenteredWindowArtifactHeader.FromRaw(raw);
        if (CenteredStackArtifactPin.FromHeader(CenteredStackArtifactHeader.FromArtifact(header), raw.Artifact.ChecksumSha256) != pin ||
            raw.Layout.ByteLength > CenteredWindowRegistrationResult.MaximumRetainedInputBytes)
            throw new InvalidDataException("The retained raw registration header differs from its admitted pin.");
        var frame = await ingress.ReadCenteredFrameAsync(found, cancellationToken).ConfigureAwait(false);
        var artifact = header with { Payload = frame.PixelData };
        if (CenteredStackArtifactPin.From(artifact) != pin)
            throw new InvalidDataException("The retained raw registration pixels differ from their admitted pin.");
        return artifact;
    }
}
