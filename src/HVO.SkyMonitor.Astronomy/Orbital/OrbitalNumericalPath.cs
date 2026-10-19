namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// Quartic interpolation geometry. Hulls bound the fitted polynomial; the orbit remainder is a
/// numerical estimate, not a formal interval certificate for the point-valued SGP4 implementation.
/// </summary>
internal static class OrbitalNumericalPath
{
    internal const string EstimatorVersion = "quartic-bernstein-numerical-remainder-v1";
    internal const double RemainderMultiplier = 8;
    internal const double CartesianFloorKilometers = 1e-6;

    internal static EnuVector[] Fit(IReadOnlyList<EnuVector> y)
    {
        ArgumentNullException.ThrowIfNull(y);
        if (y.Count != 5) throw new ArgumentException("Five uniform quartic nodes are required.", nameof(y));
        return [y[0], (y[0] * -13 + y[1] * 48 - y[2] * 36 + y[3] * 16 - y[4] * 3) / 12,
            (y[0] * 13 - y[1] * 64 + y[2] * 120 - y[3] * 64 + y[4] * 13) / 18,
            (y[0] * -3 + y[1] * 16 - y[2] * 36 + y[3] * 48 - y[4] * 13) / 12, y[4]];
    }

    internal static EnuVector Evaluate(IReadOnlyList<EnuVector> controls, double fraction)
    {
        if (controls.Count != 5 || !double.IsFinite(fraction) || fraction is < 0 or > 1)
            throw new ArgumentException("Invalid quartic evaluation.");
        Span<EnuVector> work = stackalloc EnuVector[5];
        for (var i = 0; i < 5; i++) work[i] = controls[i];
        for (var degree = 4; degree > 0; degree--)
            for (var i = 0; i < degree; i++) work[i] = work[i] * (1 - fraction) + work[i + 1] * fraction;
        return work[0];
    }

    /// <summary>Nine actual uniform samples; even nodes fit, odd nodes independently estimate remainder.</summary>
    internal static OrbitalDirectionTube Enclose(IReadOnlyList<OrbitalTopocentricState> samples)
    {
        if (samples.Count != 9) throw new ArgumentException("Nine uniform probes are required.", nameof(samples));
        var positions = samples.Select(static p => p.UnitDirection * p.RangeKilometers).ToArray();
        var controls = Fit([positions[0], positions[2], positions[4], positions[6], positions[8]]);
        var residual = 0d;
        for (var i = 1; i < 8; i += 2)
            residual = Math.Max(residual, (positions[i] - Evaluate(controls, i / 8d)).Length);
        var remainder = Math.Max(CartesianFloorKilometers, RemainderMultiplier * residual);
        var center = positions[4];
        var radius = controls.Max(p => (p - center).Length) + remainder;
        var norm = center.Length;
        var angle = radius < norm ? Math.Asin(radius / norm) : Math.PI;
        return new(CameraBasis.ToHorizontal(center / norm), angle, remainder, norm - radius);
    }

    internal static DateTimeOffset At(DateTimeOffset start, DateTimeOffset end, int numerator, int denominator)
        => start.AddTicks((long)((end.UtcTicks - start.UtcTicks) * ((double)numerator / denominator)));
}

internal sealed record OrbitalDirectionTube(AltAzPoint Center, double RadiusRadians,
    double CartesianRemainderKilometers, double MinimumRangeKilometers);
