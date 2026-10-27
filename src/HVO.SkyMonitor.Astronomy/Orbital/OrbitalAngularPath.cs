namespace HVO.SkyMonitor.Astronomy;

/// <summary>Cartesian polynomial hull plus an empirical orbit remainder, expressed against time-synchronous slerp.</summary>
internal static class OrbitalAngularPath
{
    internal static double Angle(EnuVector a, EnuVector b) => Math.Atan2(EnuVector.Cross(a, b).Length, EnuVector.Dot(a, b));
    // Match the existing host-neutral shortest-arc interpolator's antipodal exclusion.
    internal static bool CanInterpolate(EnuVector a, EnuVector b) => EnuVector.Dot(a, b) >= -.999999999999;

    internal static double Estimate(IReadOnlyList<OrbitalTopocentricState> probes)
    {
        if (probes.Count != 9) throw new ArgumentException("Nine uniform probes are required.", nameof(probes));
        var positions = probes.Select(static p => p.UnitDirection * p.RangeKilometers).ToArray();
        var controls = OrbitalNumericalPath.Fit([positions[0], positions[2], positions[4], positions[6], positions[8]]);
        var angle = Angle(probes[0].UnitDirection, probes[8].UnitDirection);
        if (!double.IsFinite(angle) || !CanInterpolate(probes[0].UnitDirection, probes[8].UnitDirection)) return double.PositiveInfinity;
        var deviation = 0d; var residual = 0d;
        for (var i = 0; i < 5; i++)
            deviation = Math.Max(deviation, (controls[i] - (positions[0] * (1 - i / 4d) + positions[8] * (i / 4d))).Length);
        for (var i = 1; i < 8; i += 2)
            residual = Math.Max(residual, (positions[i] - OrbitalNumericalPath.Evaluate(controls, i / 8d)).Length);
        var r0 = probes[0].RangeKilometers; var r1 = probes[8].RangeKilometers;
        // Q(t)=((1-t)r0+t*r1)*slerp(u0,u1,t). Its second derivative has norm at most
        // 2*|r1-r0|*theta + max(r0,r1)*theta^2. Q and the linear Cartesian chord agree at both ends,
        // so their difference is bounded by max|Q''|/8. The quartic-minus-chord Bernstein hull
        // bounds the other fitted contribution. Only the odd-node orbit remainder is empirical.
        var spherical = (2 * Math.Abs(r1 - r0) * angle + Math.Max(r0, r1) * angle * angle) / 8;
        var remainder = Math.Max(OrbitalNumericalPath.CartesianFloorKilometers, OrbitalNumericalPath.RemainderMultiplier * residual);
        // Uniform UTC probes truncate to ticks. Charge one tick of reported direction/range motion
        // at every interpolation node, inflated with the same factor as the empirical remainder.
        var rounding = 8 * TimeSpan.FromTicks(1).TotalSeconds * probes.Max(static p =>
            p.RangeKilometers * p.AngularSpeedDegreesPerSecond * (Math.PI / 180) + Math.Abs(p.RangeRateKilometersPerSecond));
        var ratio = (deviation + spherical + remainder + rounding) / Math.Min(r0, r1);
        return ratio < 1 ? Math.Asin(ratio) * (180 / Math.PI) + 1e-10 : double.PositiveInfinity;
    }
}
