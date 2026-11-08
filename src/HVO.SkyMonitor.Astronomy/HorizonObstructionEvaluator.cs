using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

public enum HorizonObstructionEligibility { Clear, Obstructed, Unknown, OutsideDomain, Incompatible }

/// <summary>One frozen angular profile, shared by prediction and pixel eligibility.</summary>
public sealed class HorizonObstructionEvaluator
{
    public const string AlgorithmVersion = "angular-horizon-linear-wrap-v1";
    public HorizonObstructionProfile Profile { get; }
    public bool IsCompatible { get; }
    public ProjectionContext Projection { get; }
    public string CalibrationVersion { get; }
    public string GeometryIdentitySha256 { get; }

    public HorizonObstructionEvaluator(HorizonObstructionProfile profile, CameraRigConfig rig, ObserverLocation observer)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rig);
        observer.Validate();
        Profile = profile;
        CalibrationVersion = rig.Optics.CalibrationVersion;
        Projection = RigProjectionContextFactory.Create(rig);
        GeometryIdentitySha256 = HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig);
        IsCompatible = profile.GeometryIdentitySha256 == GeometryIdentitySha256 &&
            profile.SiteIdentitySha256 == HorizonObstructionProfile.ComputeSiteIdentitySha256(
                observer.LatitudeDegrees, observer.LongitudeDegrees, observer.ElevationMeters);
    }

    public HorizonObstructionEligibility Evaluate(AltAzPoint direction)
    {
        if (!double.IsFinite(direction.AzimuthDegrees) || !double.IsFinite(direction.AltitudeDegrees) ||
            direction.AltitudeDegrees is < -90 or > 90) return HorizonObstructionEligibility.OutsideDomain;
        if (!IsCompatible) return HorizonObstructionEligibility.Incompatible;
        if (direction.AltitudeDegrees < 0) return HorizonObstructionEligibility.Obstructed;
        var elevation = HorizonElevation(direction.AzimuthDegrees);
        return elevation is null ? HorizonObstructionEligibility.Unknown :
            direction.AltitudeDegrees <= elevation ? HorizonObstructionEligibility.Obstructed : HorizonObstructionEligibility.Clear;
    }

    /// <summary>
    /// Tests the displayed linear-altitude, shortest-azimuth segment at every horizon knot and interval midpoint.
    /// This does not assert the physical trajectory between prediction samples.
    /// </summary>
    public bool IsSegmentClear(AltAzPoint from, AltAzPoint to)
    {
        if (Evaluate(from) != HorizonObstructionEligibility.Clear || Evaluate(to) != HorizonObstructionEligibility.Clear)
            return false;
        var delta = (to.AzimuthDegrees - from.AzimuthDegrees) % 360;
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;
        if (delta == 0) return true;
        var cuts = new List<double>(Profile.Samples.Count + 2) { 0, 1 };
        foreach (var sample in Profile.Samples)
        {
            var distance = (sample.AzimuthDegrees - from.AzimuthDegrees) % 360;
            if (delta > 0 && distance < 0) distance += 360;
            if (delta < 0 && distance > 0) distance -= 360;
            var fraction = distance / delta;
            if (fraction > 0 && fraction < 1)
            {
                var altitude = from.AltitudeDegrees + fraction * (to.AltitudeDegrees - from.AltitudeDegrees);
                if (sample.ElevationDegrees is not { } elevation || altitude <= elevation) return false;
                cuts.Add(fraction);
            }
        }
        cuts.Sort();
        for (var i = 0; i < cuts.Count - 1; i++)
        {
            if (!ClearAt(cuts[i]) || !ClearAt((cuts[i] + cuts[i + 1]) / 2)) return false;
        }
        return true;

        bool ClearAt(double fraction) => Evaluate(new(
            from.AltitudeDegrees + fraction * (to.AltitudeDegrees - from.AltitudeDegrees),
            from.AzimuthDegrees + fraction * delta)) == HorizonObstructionEligibility.Clear;
    }

    /// <summary>Returns null across unsupported gaps. Binary search bounds per-pixel work independently of sample count.</summary>
    public double? HorizonElevation(double azimuthDegrees)
    {
        if (!IsCompatible || !double.IsFinite(azimuthDegrees)) return null;
        var az = azimuthDegrees % 360;
        if (az < 0) az += 360;
        var samples = Profile.Samples;
        var low = 0;
        var high = samples.Count - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].AzimuthDegrees == az) return samples[mid].ElevationDegrees;
            if (samples[mid].AzimuthDegrees < az) low = mid + 1;
            else high = mid - 1;
        }
        if (samples.Count < 2) return null;
        var left = samples[high < 0 ? samples.Count - 1 : high];
        var right = samples[low == samples.Count ? 0 : low];
        var gap = right.AzimuthDegrees - left.AzimuthDegrees;
        if (gap < 0) gap += 360;
        if (gap > Profile.MaximumInterpolationGapDegrees || left.ElevationDegrees is not { } a ||
            right.ElevationDegrees is not { } b) return null;
        var distance = az - left.AzimuthDegrees;
        if (distance < 0) distance += 360;
        return a + (b - a) * (distance / gap);
    }
}
