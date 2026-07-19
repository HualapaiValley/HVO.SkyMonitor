using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>One UTC-relative sky-track state in a deterministic transient scenario.</summary>
public sealed record VirtualTransientSkyKeyframe
{
    public double OffsetSeconds { get; init; }
    public double AltitudeDegrees { get; init; }
    public double AzimuthDegrees { get; init; }
    public double Magnitude { get; init; }
    public double AngularWidthDegrees { get; init; } = 0.1;
    public double RedWeight { get; init; } = 1;
    public double GreenWeight { get; init; } = 1;
    public double BlueWeight { get; init; } = 1;

    internal void Validate()
    {
        if (!double.IsFinite(OffsetSeconds) || Math.Abs(OffsetSeconds) > 86_400 ||
            !double.IsFinite(AltitudeDegrees) || AltitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(AzimuthDegrees) || AzimuthDegrees is < 0 or >= 360 ||
            !double.IsFinite(Magnitude) || Magnitude is < -30 or > 30 ||
            !double.IsFinite(AngularWidthDegrees) || AngularWidthDegrees is <= 0 or > 2 ||
            !BoundedWeight(RedWeight) || !BoundedWeight(GreenWeight) || !BoundedWeight(BlueWeight) ||
            RedWeight + GreenWeight + BlueWeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualTransientSkyKeyframe));
        }
    }

    private static bool BoundedWeight(double value) => double.IsFinite(value) && value is >= 0 and <= 10;
}

/// <summary>A generic projected sky signal with a bounded, cadence-neutral UTC timeline.</summary>
public sealed record VirtualTransientSkyTrack
{
    public string PrimitiveId { get; init; } = "p-001";
    public IReadOnlyList<VirtualTransientSkyKeyframe> Keyframes { get; init; } =
        [new(), new() { OffsetSeconds = 1 }];

    internal void Validate()
    {
        ValidateIdentity(PrimitiveId);
        if (Keyframes is null || Keyframes.Count is < 2 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(Keyframes));
        }

        var previousOffset = double.NegativeInfinity;
        VirtualTransientSkyKeyframe? previous = null;
        foreach (var keyframe in Keyframes)
        {
            ArgumentNullException.ThrowIfNull(keyframe);
            keyframe.Validate();
            if (keyframe.OffsetSeconds <= previousOffset)
            {
                throw new ArgumentException("Sky keyframes must be strictly ordered.", nameof(Keyframes));
            }
            if (previous is not null)
            {
                var dot = EnuVector.Dot(
                    CameraBasis.FromHorizontal(new AltAzPoint(previous.AltitudeDegrees, previous.AzimuthDegrees)),
                    CameraBasis.FromHorizontal(new AltAzPoint(keyframe.AltitudeDegrees, keyframe.AzimuthDegrees)));
                if (dot < -0.999999999999)
                {
                    throw new ArgumentException("Consecutive sky keyframes cannot be antipodal.", nameof(Keyframes));
                }
            }
            previousOffset = keyframe.OffsetSeconds;
            previous = keyframe;
        }
    }

    internal static void ValidateIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value != value.Trim())
        {
            throw new ArgumentException("Primitive identities must be bounded non-empty values.", nameof(value));
        }
    }
}

/// <summary>One UTC-relative sensor-plane charge state in a deterministic transient scenario.</summary>
public sealed record VirtualTransientSensorKeyframe
{
    public double OffsetSeconds { get; init; }
    public double PixelX { get; init; }
    public double PixelY { get; init; }
    public double ElectronsPerSecond { get; init; }
    public double SigmaPixels { get; init; } = 0.25;

    internal void Validate()
    {
        if (!double.IsFinite(OffsetSeconds) || Math.Abs(OffsetSeconds) > 86_400 ||
            !double.IsFinite(PixelX) || PixelX < 0 || PixelX > 100_000 ||
            !double.IsFinite(PixelY) || PixelY < 0 || PixelY > 100_000 ||
            !double.IsFinite(ElectronsPerSecond) || ElectronsPerSecond is < 0 or > 1_000_000_000 ||
            !double.IsFinite(SigmaPixels) || SigmaPixels is < 0.1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualTransientSensorKeyframe));
        }
    }
}

/// <summary>A generic sensor-plane charge signal that bypasses sky projection and optics.</summary>
public sealed record VirtualTransientSensorTrack
{
    public string PrimitiveId { get; init; } = "s-001";
    public IReadOnlyList<VirtualTransientSensorKeyframe> Keyframes { get; init; } =
        [new(), new() { OffsetSeconds = 1 }];

    internal void Validate()
    {
        VirtualTransientSkyTrack.ValidateIdentity(PrimitiveId);
        if (Keyframes is null || Keyframes.Count is < 2 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(Keyframes));
        }

        var previousOffset = double.NegativeInfinity;
        foreach (var keyframe in Keyframes)
        {
            ArgumentNullException.ThrowIfNull(keyframe);
            keyframe.Validate();
            if (keyframe.OffsetSeconds <= previousOffset)
            {
                throw new ArgumentException("Sensor keyframes must be strictly ordered.", nameof(Keyframes));
            }
            previousOffset = keyframe.OffsetSeconds;
        }
    }
}

/// <summary>Versioned generic rendering primitives for deterministic virtual transient evidence.</summary>
public sealed record VirtualTransientScenarioDefinition
{
    public const string CurrentSchemaVersion = "virtual-transient-scenario-v1";
    public const string CurrentAlgorithmVersion = "virtual-transient-raster-v1";
    public const int MaximumPrimitiveCount = 128;
    public const int MaximumKeyframeCount = 1024;

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ScenarioId { get; init; } = "transient-scenario";
    public string ScenarioVersion { get; init; } = "1";
    public int Seed { get; init; } = 2025;
    public DateTimeOffset EpochUtc { get; init; } = DateTimeOffset.UnixEpoch;
    public int TemporalSampleCount { get; init; } = 16;
    public IReadOnlyList<VirtualTransientSkyTrack> SkyTracks { get; init; } = Array.Empty<VirtualTransientSkyTrack>();
    public IReadOnlyList<VirtualTransientSensorTrack> SensorTracks { get; init; } = Array.Empty<VirtualTransientSensorTrack>();

    /// <summary>Validates bounded identities, timelines, and primitive collections.</summary>
    public void Validate()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(ScenarioId) || ScenarioId.Length > 128 || ScenarioId != ScenarioId.Trim() ||
            string.IsNullOrWhiteSpace(ScenarioVersion) || ScenarioVersion.Length > 64 ||
            !ScenarioVersion.All(static character => character is >= '0' and <= '9') ||
            EpochUtc == default || EpochUtc.Offset != TimeSpan.Zero ||
            TemporalSampleCount is < 1 or > 64 || SkyTracks is null || SensorTracks is null ||
            SkyTracks.Count > 64 || SensorTracks.Count > MaximumPrimitiveCount ||
            SkyTracks.Count + SensorTracks.Count > MaximumPrimitiveCount)
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualTransientScenarioDefinition));
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var keyframeCount = 0;
        foreach (var track in SkyTracks)
        {
            ArgumentNullException.ThrowIfNull(track);
            track.Validate();
            keyframeCount = checked(keyframeCount + track.Keyframes.Count);
            if (!identities.Add(track.PrimitiveId))
            {
                throw new ArgumentException("Transient primitive identities must be unique.", nameof(SkyTracks));
            }
        }
        foreach (var track in SensorTracks)
        {
            ArgumentNullException.ThrowIfNull(track);
            track.Validate();
            keyframeCount = checked(keyframeCount + track.Keyframes.Count);
            if (!identities.Add(track.PrimitiveId))
            {
                throw new ArgumentException("Transient primitive identities must be unique.", nameof(SensorTracks));
            }
        }
        if (keyframeCount > MaximumKeyframeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualTransientScenarioDefinition),
                $"Transient scenarios support at most {MaximumKeyframeCount} keyframes.");
        }

        foreach (var offset in SkyTracks.SelectMany(static track => track.Keyframes)
                     .Select(static keyframe => keyframe.OffsetSeconds)
                     .Concat(SensorTracks.SelectMany(static track => track.Keyframes)
                         .Select(static keyframe => keyframe.OffsetSeconds)))
        {
            _ = EpochUtc + TimeSpan.FromSeconds(offset);
        }
    }

    /// <summary>Validates sensor-plane coordinates against one configured sensor.</summary>
    public void ValidateSensorBounds(int width, int height)
    {
        Validate();
        if (width <= 0 || height <= 0 || SensorTracks.SelectMany(static track => track.Keyframes)
            .Any(keyframe => keyframe.PixelX >= width || keyframe.PixelY >= height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Sensor-track coordinates must lie inside the configured sensor.");
        }
    }

    /// <summary>Returns a canonical SHA-256 identity for every scenario parameter.</summary>
    public string ComputeParametersSha256()
    {
        Validate();
        return CaptureContractJson.ComputeCanonicalJsonSha256(this);
    }

    /// <summary>Returns an opaque identity derived from all rendering parameters except caller labeling.</summary>
    public string ComputeCanonicalScenarioId()
    {
        Validate();
        var hash = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            SchemaVersion,
            ScenarioVersion,
            Seed,
            EpochUtc,
            TemporalSampleCount,
            SkyTracks,
            SensorTracks
        });
        return $"scn-{hash[..24]}";
    }
}

/// <summary>Immutable validated transient scenario used by frame renderers.</summary>
public sealed class VirtualTransientScenario
{
    private readonly VirtualTransientScenarioDefinition _definition;

    public VirtualTransientScenario(VirtualTransientScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        _definition = definition with
        {
            SkyTracks = Array.AsReadOnly(definition.SkyTracks.Select(static track => track with
            {
                Keyframes = Array.AsReadOnly(track.Keyframes.ToArray())
            }).ToArray()),
            SensorTracks = Array.AsReadOnly(definition.SensorTracks.Select(static track => track with
            {
                Keyframes = Array.AsReadOnly(track.Keyframes.ToArray())
            }).ToArray())
        };
    }

    public VirtualTransientScenarioDefinition Definition => _definition;
}

/// <summary>Transient scenario and logical exposure interval supplied to a scene renderer.</summary>
public sealed record VirtualTransientRenderContext(
    VirtualTransientScenario Scenario,
    DateTimeOffset IntegrationStartUtc,
    TimeSpan IntegrationDuration)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Scenario);
        if (IntegrationStartUtc == default || IntegrationStartUtc.Offset != TimeSpan.Zero ||
            IntegrationDuration < TimeSpan.Zero || IntegrationDuration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(IntegrationDuration));
        }
        _ = IntegrationStartUtc + IntegrationDuration;
    }
}
