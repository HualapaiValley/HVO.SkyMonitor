using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>Reads the exact qualified product disposition and its admitted measurement geometry.</summary>
public sealed class CenteredStackRetainedInputReader(
    IExposureProductReader exposures,
    Func<ExposureInputV1, CancellationToken, ValueTask<CenteredStackMeasurementGeometry?>> readGeometry) : ICenteredStackInputReader
{
    private readonly IExposureProductReader _exposures = exposures ?? throw new ArgumentNullException(nameof(exposures));
    private readonly Func<ExposureInputV1, CancellationToken, ValueTask<CenteredStackMeasurementGeometry?>> _readGeometry =
        readGeometry ?? throw new ArgumentNullException(nameof(readGeometry));

    public async ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(
        ExposureKey key, ProcessingArtifact linear, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(linear);
        var committed = await _exposures.ReadLinearAsync(key, cancellationToken).ConfigureAwait(false);
        if (committed is null || committed.Disposition == LinearExposureDisposition.Pending || committed.Revision is null)
        {
            return null;
        }
        if (committed.Exposure.Key != key || committed.Provenance is not { } provenance ||
            committed.Product is not { } product || product.Exposure != key || product.ArtifactId != linear.ArtifactId ||
            committed.Disposition != provenance.Disposition ||
            committed.Disposition is not (LinearExposureDisposition.Calibrated or LinearExposureDisposition.QualifiedNormalizationOnly) ||
            product.OutputIdentitySha256 != provenance.OutputIdentitySha256 || product.PayloadSha256 != provenance.LinearPayloadSha256 ||
            product.Slot.Variant != linear.Variant)
        {
            throw new InvalidDataException("The committed linear result does not match the frozen stack input.");
        }
        var geometry = await _readGeometry(committed.Exposure, cancellationToken).ConfigureAwait(false);
        return geometry is null ? null :
            new(new(committed.Exposure, linear, provenance), geometry);
    }
}
