namespace HVO.SkyMonitor.Astronomy;

internal sealed record OrbitalProjectedSample(OrbitalTrackSample Sample, OrbitalClipReason? ClipReason);

/// <summary>Uses existing optical and emitted-image transforms; no orbital-specific projection formulas.</summary>
internal sealed class OrbitalSampleProjector
{
    private readonly OrbitalGeometryFacts geometry;
    private readonly IImageProjector bounded;
    private readonly IImageProjector unbounded;

    internal OrbitalSampleProjector(OrbitalGeometryFacts geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        geometry.Validate();
        this.geometry = geometry;
        bounded = ProjectorFactory.Create(geometry.Projection);
        unbounded = ProjectorFactory.Create(geometry.Projection with { EnforceSensorBounds = false });
    }

    internal double RemainingPixelScaleBound => 1d / Math.Min(geometry.ImageTransform.BinX, geometry.ImageTransform.BinY);

    internal OrbitalProjectedSample Project(DateTimeOffset utc, OrbitalTopocentricState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        OrbitalContract.Require(OrbitalContract.Utc(utc), "sample-utc");
        var horizontal = state.Horizontal;
        OrbitalContract.Require(double.IsFinite(horizontal.AltitudeDegrees) && horizontal.AltitudeDegrees is >= -90 and <= 90 &&
            double.IsFinite(horizontal.AzimuthDegrees) && horizontal.AzimuthDegrees is >= 0 and < 360 &&
            double.IsFinite(state.RangeKilometers) && state.RangeKilometers > 0 &&
            double.IsFinite(state.RangeRateKilometersPerSecond) && double.IsFinite(state.AngularSpeedDegreesPerSecond) &&
            state.AngularSpeedDegreesPerSecond >= 0, "topocentric-sample");
        PixelPoint? pixel = null;
        OrbitalClipReason? clip;
        if (geometry.HorizonPolicy == HorizonPolicy.GeometricHorizon && horizontal.AltitudeDegrees < 0)
            clip = OrbitalClipReason.Horizon;
        else if (bounded.Project(horizontal) is not { } native)
            clip = unbounded.Project(horizontal) is null ? OrbitalClipReason.Aperture : OrbitalClipReason.Sensor;
        else if (!InCrop(native))
            clip = OrbitalClipReason.Sensor;
        else
        {
            pixel = ProjectedSceneImageTransform.Apply(geometry.ImageTransform, native);
            clip = null;
        }
        return new(new(utc, horizontal, pixel, state.RangeKilometers, state.RangeRateKilometersPerSecond,
            state.AngularSpeedDegreesPerSecond, OrbitalIllumination.Unknown), clip);
    }

    private bool InCrop(PixelPoint point)
    {
        var t = geometry.ImageTransform;
        return point.X >= t.CropX && point.X <= (long)t.CropX + t.CropWidth &&
            point.Y >= t.CropY && point.Y <= (long)t.CropY + t.CropHeight;
    }
}
