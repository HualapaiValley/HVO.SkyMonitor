using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>One UTC-relative state in a deterministic virtual cloud scenario.</summary>
public sealed record VirtualCloudKeyframe
{
    public double OffsetSeconds { get; init; }
    public double Coverage { get; init; }
    public double MaximumOpacity { get; init; }
    public double ScatterFraction { get; init; }

    internal void Validate()
    {
        if (!double.IsFinite(OffsetSeconds) ||
            !UnitInterval(Coverage) || !UnitInterval(MaximumOpacity) || !UnitInterval(ScatterFraction))
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualCloudKeyframe));
        }
    }

    private static bool UnitInterval(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}

/// <summary>Versioned inputs for a seeded cloud field with temporal motion and transitions.</summary>
public sealed record VirtualCloudScenarioDefinition
{
    public const string CurrentSchemaVersion = "virtual-cloud-scenario-v1";
    public const string CurrentAlgorithmVersion = "virtual-cloud-value-field-v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ScenarioId { get; init; } = "cloud-scenario";
    public string ScenarioVersion { get; init; } = "1";
    public int Seed { get; init; } = 2025;
    public DateTimeOffset EpochUtc { get; init; } = DateTimeOffset.UnixEpoch;
    public double SpatialFrequency { get; init; } = 3;
    public double DriftEastCellsPerSecond { get; init; }
    public double DriftNorthCellsPerSecond { get; init; }
    public double EvolutionCellsPerSecond { get; init; }
    public int Octaves { get; init; } = 3;
    public double EdgeSoftness { get; init; } = 0.15;
    public double HorizonFadeDegrees { get; init; } = 5;
    public int TemporalSampleCount { get; init; } = 4;
    public IReadOnlyList<VirtualCloudKeyframe> Keyframes { get; init; } =
        [new() { Coverage = 0, MaximumOpacity = 0 }];

    /// <summary>Validates bounded identities, numeric parameters, and strictly ordered keyframes.</summary>
    public void Validate()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            !BoundedIdentity(ScenarioId, 128) || !BoundedIdentity(ScenarioVersion, 64) ||
            EpochUtc == default || EpochUtc.Offset != TimeSpan.Zero ||
            !double.IsFinite(SpatialFrequency) || SpatialFrequency is < 0.1 or > 64 ||
            !BoundedRate(DriftEastCellsPerSecond) || !BoundedRate(DriftNorthCellsPerSecond) ||
            !BoundedRate(EvolutionCellsPerSecond) || Octaves is < 1 or > 6 ||
            !double.IsFinite(EdgeSoftness) || EdgeSoftness is <= 0 or > 1 ||
            !double.IsFinite(HorizonFadeDegrees) || HorizonFadeDegrees is < 0 or > 30 ||
            TemporalSampleCount is < 1 or > 16 || Keyframes is null || Keyframes.Count is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualCloudScenarioDefinition));
        }

        var previousOffset = double.NegativeInfinity;
        foreach (var keyframe in Keyframes)
        {
            ArgumentNullException.ThrowIfNull(keyframe);
            keyframe.Validate();
            if (keyframe.OffsetSeconds <= previousOffset)
            {
                throw new ArgumentException("Cloud keyframes must be strictly ordered by offset.", nameof(Keyframes));
            }
            previousOffset = keyframe.OffsetSeconds;
        }
    }

    /// <summary>Returns a canonical SHA-256 identity for every field-defining parameter.</summary>
    public string ComputeParametersSha256()
    {
        Validate();
        return CaptureContractJson.ComputeCanonicalJsonSha256(this);
    }

    /// <summary>Returns an opaque identity derived from every scenario parameter except caller labeling.</summary>
    public string ComputeCanonicalScenarioId()
    {
        Validate();
        var hash = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            SchemaVersion,
            ScenarioVersion,
            Seed,
            EpochUtc,
            SpatialFrequency,
            DriftEastCellsPerSecond,
            DriftNorthCellsPerSecond,
            EvolutionCellsPerSecond,
            Octaves,
            EdgeSoftness,
            HorizonFadeDegrees,
            TemporalSampleCount,
            Keyframes
        });
        return $"scn-{hash[..24]}";
    }

    private static bool BoundedIdentity(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim();

    private static bool BoundedRate(double value) => double.IsFinite(value) && Math.Abs(value) <= 10;
}

/// <summary>Integrated cloud transmission and background scatter for one sky direction.</summary>
public readonly record struct VirtualCloudEffect(double Opacity, double Transmission, double Scatter)
{
    public static VirtualCloudEffect Clear => new(0, 1, 0);
}

/// <summary>Immutable deterministic evaluator for one virtual cloud scenario.</summary>
public sealed class VirtualCloudField
{
    private const int CoverageAltitudeBands = 12;
    private const int CoverageAzimuthSamples = 24;
    private readonly VirtualCloudScenarioDefinition _definition;

    public VirtualCloudField(VirtualCloudScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        _definition = definition;
    }

    public VirtualCloudScenarioDefinition Definition => _definition;

    /// <summary>Evaluates one instantaneous horizontal direction.</summary>
    public VirtualCloudEffect Evaluate(AltAzPoint direction, DateTimeOffset utc)
    {
        ValidateDirectionAndTime(direction, utc);
        if (direction.AltitudeDegrees < 0)
        {
            return VirtualCloudEffect.Clear;
        }

        var state = ResolveState(utc);
        if (state.Coverage <= 0 || state.MaximumOpacity <= 0)
        {
            return VirtualCloudEffect.Clear;
        }

        var altitude = DegreesToRadians(direction.AltitudeDegrees);
        var azimuth = DegreesToRadians(direction.AzimuthDegrees);
        var seconds = (utc - _definition.EpochUtc).TotalSeconds;
        var east = Math.Cos(altitude) * Math.Sin(azimuth);
        var north = Math.Cos(altitude) * Math.Cos(azimuth);
        var x = east * _definition.SpatialFrequency + seconds * _definition.DriftEastCellsPerSecond;
        var y = north * _definition.SpatialFrequency + seconds * _definition.DriftNorthCellsPerSecond;
        var evolution = seconds * _definition.EvolutionCellsPerSecond;
        var noise = FractalNoise(x, y, evolution);
        var threshold = 1 - state.Coverage;
        var opacity = state.MaximumOpacity * SmoothStep(
            threshold - _definition.EdgeSoftness / 2,
            threshold + _definition.EdgeSoftness / 2,
            noise);
        if (_definition.HorizonFadeDegrees > 0)
        {
            opacity *= SmoothStep(0, _definition.HorizonFadeDegrees, direction.AltitudeDegrees);
        }
        opacity = Math.Clamp(opacity, 0, 1);
        return new VirtualCloudEffect(opacity, 1 - opacity, opacity * state.ScatterFraction);
    }

    /// <summary>Integrates one direction over a logical exposure using fixed midpoint samples.</summary>
    public VirtualCloudEffect Integrate(AltAzPoint direction, DateTimeOffset startUtc, TimeSpan duration)
    {
        ValidateInterval(startUtc, duration);
        var opacity = 0d;
        var transmission = 0d;
        var scatter = 0d;
        var samples = _definition.TemporalSampleCount;
        for (var sample = 0; sample < samples; sample++)
        {
            var offsetTicks = duration == TimeSpan.Zero
                ? 0
                : checked((long)(duration.Ticks * ((sample + 0.5) / samples)));
            var effect = Evaluate(direction, startUtc.AddTicks(offsetTicks));
            opacity += effect.Opacity;
            transmission += effect.Transmission;
            scatter += effect.Scatter;
        }
        return new VirtualCloudEffect(opacity / samples, transmission / samples, scatter / samples);
    }

    /// <summary>Computes deterministic equal-area sky-dome coverage for a logical exposure.</summary>
    public double ComputeSkyCoverage(DateTimeOffset startUtc, TimeSpan duration)
    {
        ValidateInterval(startUtc, duration);
        var midpointUtc = startUtc + TimeSpan.FromTicks(duration.Ticks / 2);
        var cloudyThreshold = ResolveState(midpointUtc).MaximumOpacity * 0.5;
        if (cloudyThreshold <= 0)
        {
            return 0;
        }
        var cloudy = 0;
        var total = CoverageAltitudeBands * CoverageAzimuthSamples;
        for (var altitudeBand = 0; altitudeBand < CoverageAltitudeBands; altitudeBand++)
        {
            var sinAltitude = (altitudeBand + 0.5) / CoverageAltitudeBands;
            var altitude = Math.Asin(sinAltitude) * 180 / Math.PI;
            for (var azimuthSample = 0; azimuthSample < CoverageAzimuthSamples; azimuthSample++)
            {
                var azimuth = (azimuthSample + 0.5) * 360 / CoverageAzimuthSamples;
                if (Integrate(new AltAzPoint(altitude, azimuth), startUtc, duration).Opacity >= cloudyThreshold)
                {
                    cloudy++;
                }
            }
        }
        return cloudy / (double)total;
    }

    private (double Coverage, double MaximumOpacity, double ScatterFraction) ResolveState(DateTimeOffset utc)
    {
        var offset = (utc - _definition.EpochUtc).TotalSeconds;
        var keyframes = _definition.Keyframes;
        if (offset <= keyframes[0].OffsetSeconds)
        {
            return State(keyframes[0]);
        }
        for (var index = 1; index < keyframes.Count; index++)
        {
            var upper = keyframes[index];
            if (offset <= upper.OffsetSeconds)
            {
                var lower = keyframes[index - 1];
                var fraction = (offset - lower.OffsetSeconds) / (upper.OffsetSeconds - lower.OffsetSeconds);
                return (
                    Lerp(lower.Coverage, upper.Coverage, fraction),
                    Lerp(lower.MaximumOpacity, upper.MaximumOpacity, fraction),
                    Lerp(lower.ScatterFraction, upper.ScatterFraction, fraction));
            }
        }
        return State(keyframes[^1]);
    }

    private double FractalNoise(double x, double y, double evolution)
    {
        var sum = 0d;
        var amplitude = 1d;
        var amplitudeSum = 0d;
        for (var octave = 0; octave < _definition.Octaves; octave++)
        {
            sum += ValueNoise(x, y, evolution, octave) * amplitude;
            amplitudeSum += amplitude;
            x *= 2;
            y *= 2;
            evolution *= 2;
            amplitude *= 0.5;
        }
        return sum / amplitudeSum;
    }

    private double ValueNoise(double x, double y, double evolution, int octave)
    {
        var floorX = Math.Floor(x);
        var floorY = Math.Floor(y);
        var floorEvolution = Math.Floor(evolution);
        if (floorX is < long.MinValue or >= long.MaxValue ||
            floorY is < long.MinValue or >= long.MaxValue ||
            floorEvolution is < long.MinValue or >= long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Cloud motion exceeded the supported coordinate range.");
        }
        var ix = (long)floorX;
        var iy = (long)floorY;
        var iz = (long)floorEvolution;
        var fx = SmoothCurve(x - floorX);
        var fy = SmoothCurve(y - floorY);
        var fz = SmoothCurve(evolution - floorEvolution);
        var lowerNear = Lerp(HashValue(ix, iy, iz, octave), HashValue(ix + 1, iy, iz, octave), fx);
        var upperNear = Lerp(HashValue(ix, iy + 1, iz, octave), HashValue(ix + 1, iy + 1, iz, octave), fx);
        var lowerFar = Lerp(HashValue(ix, iy, iz + 1, octave), HashValue(ix + 1, iy, iz + 1, octave), fx);
        var upperFar = Lerp(HashValue(ix, iy + 1, iz + 1, octave), HashValue(ix + 1, iy + 1, iz + 1, octave), fx);
        return Lerp(Lerp(lowerNear, upperNear, fy), Lerp(lowerFar, upperFar, fy), fz);
    }

    private double HashValue(long x, long y, long evolution, int octave)
    {
        var value = unchecked((ulong)(uint)_definition.Seed) + 0x9E3779B97F4A7C15UL;
        value = Mix(value ^ unchecked((ulong)x));
        value = Mix(value ^ unchecked((ulong)y));
        value = Mix(value ^ unchecked((ulong)evolution));
        value = Mix(value ^ unchecked((ulong)octave));
        return (value >> 11) * (1d / (1UL << 53));
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static void ValidateDirectionAndTime(AltAzPoint direction, DateTimeOffset utc)
    {
        if (!double.IsFinite(direction.AltitudeDegrees) || direction.AltitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(direction.AzimuthDegrees) || utc == default || utc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }
    }

    private static void ValidateInterval(DateTimeOffset startUtc, TimeSpan duration)
    {
        if (startUtc == default || startUtc.Offset != TimeSpan.Zero || duration < TimeSpan.Zero || duration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        _ = startUtc + duration;
    }

    private static (double, double, double) State(VirtualCloudKeyframe keyframe)
        => (keyframe.Coverage, keyframe.MaximumOpacity, keyframe.ScatterFraction);

    private static double SmoothCurve(double value) => value * value * (3 - 2 * value);

    private static double SmoothStep(double lower, double upper, double value)
    {
        if (upper <= lower)
        {
            return value >= upper ? 1 : 0;
        }
        var fraction = Math.Clamp((value - lower) / (upper - lower), 0, 1);
        return SmoothCurve(fraction);
    }

    private static double Lerp(double lower, double upper, double fraction) => lower + (upper - lower) * fraction;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}

/// <summary>Cloud field and logical exposure interval supplied to a scene renderer.</summary>
public sealed record VirtualCloudRenderContext(
    VirtualCloudField Field,
    DateTimeOffset IntegrationStartUtc,
    TimeSpan IntegrationDuration)
{
    internal bool RequiresEvaluation
    {
        get
        {
            var keyframes = Field.Definition.Keyframes;
            for (var index = 0; index < keyframes.Count; index++)
            {
                if (keyframes[index].Coverage > 0 && keyframes[index].MaximumOpacity > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Field);
        _ = Field.Integrate(new AltAzPoint(90, 0), IntegrationStartUtc, IntegrationDuration);
    }
}
