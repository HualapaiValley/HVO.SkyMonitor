namespace HVO.SkyMonitor.Astronomy;

internal sealed record OrbitalProjectedSample(OrbitalTrackSample Sample, OrbitalClipReason? ClipReason);
internal sealed record OrbitalProjectionCell(bool Drawable, OrbitalClipReason? Excluded, double PixelsPerRadian);

/// <summary>Uses existing optical and emitted-image transforms; no orbital-specific projection formulas.</summary>
internal sealed class OrbitalSampleProjector
{
    private readonly OrbitalGeometryFacts geometry;
    private readonly IImageProjector bounded;
    private readonly IImageProjector unbounded;
    private readonly IImageProjector chart;

    internal OrbitalSampleProjector(OrbitalGeometryFacts geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        geometry.Validate();
        this.geometry = geometry;
        bounded = ProjectorFactory.Create(geometry.Projection);
        unbounded = ProjectorFactory.Create(geometry.Projection with { EnforceSensorBounds = false });
        var p = geometry.Projection;
        // The chart removes aperture clipping, but keeps the existing optical family's supported domain.
        var maximum = RadialDistortion.MaximumDistortedRadius(p.Model, p.RadialDistortionK1);
        chart = ProjectorFactory.Create(p with
        {
            EnforceSensorBounds = false,
            ImageCircleRadiusPixels = p.Aperture == ProjectionAperture.Circular
                ? p.FocalLengthXPixels * (double.IsFinite(maximum) ? maximum : 1e12) : null
        });
    }

    internal double RemainingPixelScaleBound => 1d / Math.Min(geometry.ImageTransform.BinX, geometry.ImageTransform.BinY);

    internal PixelPoint? ProjectChart(AltAzPoint horizontal) => chart.Project(horizontal);

    /// <summary>
    /// Classifies an entire direction tube, including cells with no drawable probes. A mixed result
    /// requires refinement; sampled exclusion alone never authorizes dropping a cell.
    /// </summary>
    internal OrbitalProjectionCell Classify(OrbitalDirectionTube tube)
    {
        if (!(tube.MinimumRangeKilometers > 0) || !double.IsFinite(tube.RadiusRadians))
            return new(false, null, double.PositiveInfinity);
        var altitude = tube.Center.AltitudeDegrees * Math.PI / 180;
        var horizonInside = geometry.HorizonPolicy != HorizonPolicy.GeometricHorizon || altitude > tube.RadiusRadians;
        if (geometry.HorizonPolicy == HorizonPolicy.GeometricHorizon && altitude + tube.RadiusRadians < 0)
            return new(false, OrbitalClipReason.Horizon, 0);
        var p = geometry.Projection;
        var k = ProjectionSamplingBounds.MaximumPixelsPerRadian(p, tube.Center, tube.RadiusRadians);
        if (k == 0) return new(false, OrbitalClipReason.Aperture, 0);
        if (!double.IsFinite(k) || unbounded.Project(tube.Center) is not { } center)
            return new(false, null, k);
        var radius = k * tube.RadiusRadians + 1e-8;
        var apertureInside = true;
        if (p.Aperture == ProjectionAperture.Circular)
        {
            var distance = Math.Sqrt(Math.Pow(center.X - p.PrincipalPointX, 2) + Math.Pow(center.Y - p.PrincipalPointY, 2));
            apertureInside = distance + radius < p.ImageCircleRadiusPixels!.Value;
        }
        // Stay away from family singularities even where the radial mapping remains finite.
        var camera = CameraBasis.Create(p.BoresightAltitudeDegrees, p.BoresightAzimuthDegrees)
            .ToCamera(CameraBasis.FromHorizontal(tube.Center));
        var domain = p.Model is ProjectionModel.Perspective or ProjectionModel.OrthographicFisheye ? Math.PI / 2 : Math.PI;
        apertureInside &= Math.Acos(Math.Clamp(camera.Up, -1, 1)) + tube.RadiusRadians < domain;
        if (!apertureInside) return new(false, null, k);
        var t = geometry.ImageTransform;
        if (center.X + radius < t.CropX || center.X - radius > (long)t.CropX + t.CropWidth ||
            center.Y + radius < t.CropY || center.Y - radius > (long)t.CropY + t.CropHeight)
            return new(false, OrbitalClipReason.Sensor, k);
        var cropInside = center.X - radius > t.CropX && center.X + radius < (long)t.CropX + t.CropWidth &&
            center.Y - radius > t.CropY && center.Y + radius < (long)t.CropY + t.CropHeight;
        return new(horizonInside && cropInside, null, k);
    }

    internal double? BoundaryCoordinate(OrbitalTopocentricState state, OrbitalClipReason reason)
    {
        if (reason == OrbitalClipReason.Horizon) return state.UnitDirection.Up * state.RangeKilometers;
        if (ProjectChart(state.Horizontal) is not { } point) return null;
        var p = geometry.Projection;
        if (reason == OrbitalClipReason.Aperture && p.ImageCircleRadiusPixels is { } radius)
            return radius - Math.Sqrt(Math.Pow(point.X - p.PrincipalPointX, 2) + Math.Pow(point.Y - p.PrincipalPointY, 2));
        if (reason != OrbitalClipReason.Sensor) return null;
        var t = geometry.ImageTransform;
        return Math.Min(Math.Min(point.X - t.CropX, (long)t.CropX + t.CropWidth - point.X),
            Math.Min(point.Y - t.CropY, (long)t.CropY + t.CropHeight - point.Y));
    }

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
