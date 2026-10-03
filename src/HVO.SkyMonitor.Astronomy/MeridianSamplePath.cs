namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// One sample on the local meridian. <see cref="MeridianAngleDegrees"/> runs from 0 at the north horizon through 90 at
/// the zenith to 180 at the south horizon; <see cref="Pixel"/> is <see langword="null"/> where the calibrated aperture or
/// sensor does not image that direction.
/// </summary>
public readonly record struct MeridianSample(
    double MeridianAngleDegrees,
    AltAzPoint Direction,
    PixelPoint? Pixel);

/// <summary>
/// Projects the north-zenith-south meridian through a calibrated rig projection at uniform angular spacing, so a keogram
/// row is a fixed sky direction rather than an image column that is only a meridian for a north-up, centred lens.
/// </summary>
public static class MeridianSamplePath
{
    public const string AlgorithmVersion = "meridian-north-zenith-south-v1";
    public const int MinimumSampleCount = 2;
    public const int MaximumSampleCount = 8192;

    /// <summary>Creates <paramref name="sampleCount"/> samples with both horizons as inclusive endpoints.</summary>
    public static IReadOnlyList<MeridianSample> Create(ProjectionContext projection, int sampleCount)
    {
        projection.Validate();
        if (sampleCount is < MinimumSampleCount or > MaximumSampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        var projector = ProjectorFactory.Create(projection);
        var samples = new MeridianSample[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var meridianAngle = 180d * index / (sampleCount - 1);
            var direction = ToDirection(meridianAngle);
            samples[index] = new MeridianSample(meridianAngle, direction, projector.Project(direction));
        }
        return samples;
    }

    /// <summary>
    /// Returns a sample count giving roughly one output row per source pixel along the imaged meridian, bounded to the
    /// supported range. Directions the projection does not image contribute nothing to the length.
    /// </summary>
    public static int RecommendedSampleCount(ProjectionContext projection)
    {
        const int Probe = 1025;
        var probe = Create(projection, Probe);
        var length = 0d;
        for (var index = 1; index < probe.Count; index++)
        {
            if (probe[index - 1].Pixel is { } previous && probe[index].Pixel is { } current)
            {
                length += Math.Sqrt(Square(current.X - previous.X) + Square(current.Y - previous.Y));
            }
        }
        var imagedFraction = probe.Count(static sample => sample.Pixel is not null) / (double)Probe;
        if (length <= 0 || imagedFraction <= 0)
        {
            return MinimumSampleCount;
        }

        // Scale the imaged length to the full 180 degrees so unimaged rows keep the same angular spacing.
        var rows = (int)Math.Ceiling(length / imagedFraction) + 1;
        return Math.Clamp(rows, MinimumSampleCount, MaximumSampleCount);
    }

    private static AltAzPoint ToDirection(double meridianAngleDegrees) => meridianAngleDegrees <= 90
        ? new AltAzPoint(meridianAngleDegrees, 0)
        : new AltAzPoint(180 - meridianAngleDegrees, 180);

    private static double Square(double value) => value * value;
}
