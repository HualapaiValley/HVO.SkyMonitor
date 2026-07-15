using System;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

public sealed record CameraRigConfig(
    SensorProfile Sensor,
    OpticsProfile Optics,
    RigOrientation Orientation,
    PipelineExposureProfile Pipeline,
    CameraControlPolicy? ControlPolicy = null,
    string ProfileVersion = "unversioned");

public sealed record SensorProfile(
    string Name,
    int WidthPixels,
    int HeightPixels,
    double PixelSizeMicrons,
    SensorColorMode ColorMode,
    CameraPixelFormat PixelFormat,
    SensorResponseMode ResponseMode = SensorResponseMode.Unspecified,
    int? StrideBytes = null,
    SampleByteOrder ByteOrder = SampleByteOrder.LittleEndian,
    string SensorRecipeVersion = "unspecified");

public enum SensorColorMode
{
    Mono,
    Color
}

/// <summary>Identifies the simulated sensor response without implying physical-camera fidelity.</summary>
public enum SensorResponseMode
{
    /// <summary>No response model was specified.</summary>
    Unspecified,

    /// <summary>Linear monochrome compatibility response.</summary>
    Monochrome,

    /// <summary>Rendered RGB compatibility response; this is not Bayer raw output.</summary>
    RenderedRgb,

    /// <summary>Linear RGGB color-filter-array samples without demosaicing.</summary>
    BayerRaw
}

/// <summary>Byte order used for multi-byte samples in tightly packed or explicitly strided rows.</summary>
public enum SampleByteOrder
{
    /// <summary>Least-significant byte first.</summary>
    LittleEndian,

    /// <summary>Most-significant byte first.</summary>
    BigEndian
}

public sealed record OpticsProfile(
    string ProjectionModel,
    double FocalLengthMillimeters,
    double FieldOfViewDegrees,
    double RollDegrees,
    LensKind LensKind = LensKind.Unspecified,
    double? PrincipalPointX = null,
    double? PrincipalPointY = null,
    double? ImageCircleRadiusPixels = null,
    double? FocalLengthXPixels = null,
    double? FocalLengthYPixels = null,
    double? VerticalFieldOfViewDegrees = null,
    bool HorizontalFlip = false,
    SensorCrop? Crop = null,
    string CalibrationVersion = "unspecified");

/// <summary>Physical optical family used to select a shared projection implementation.</summary>
public enum LensKind
{
    /// <summary>The lens family was not specified.</summary>
    Unspecified,

    /// <summary>A radial fisheye lens.</summary>
    Fisheye,

    /// <summary>A rectilinear photographic lens.</summary>
    Rectilinear,

    /// <summary>A telescope represented by rectilinear calibrated intrinsics.</summary>
    Telescope
}

/// <summary>A sensor crop in continuous pixel-edge coordinates.</summary>
public sealed record SensorCrop(int X, int Y, int Width, int Height);

public sealed record RigOrientation(
    double BoresightAltitudeDegrees,
    double BoresightAzimuthDegrees,
    double RollAdjustmentDegrees);

public sealed record ObservatoryLocation(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId);

public sealed record PipelineExposureProfile(
    TimeSpan CaptureInterval,
    TimeSpan DayExposure,
    TimeSpan NightExposure,
    double DayGain,
    double NightGain,
    ExposureEnvelope? Envelope = null,
    TimeSpan? CaptureFailureInitialDelay = null,
    TimeSpan? CaptureFailureMaximumDelay = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    CaptureCadenceMode CadenceMode = CaptureCadenceMode.MinimumStartInterval);

/// <summary>Determines when the next capture may begin after an acquisition cycle.</summary>
public enum CaptureCadenceMode
{
    /// <summary>Starts no earlier than the configured interval from the preceding actual start.</summary>
    MinimumStartInterval,

    /// <summary>Starts after acquisition-critical work completes without an interval delay.</summary>
    Continuous
}

public sealed record ExposureEnvelope(
    TimeSpan MinExposure,
    TimeSpan MaxExposure,
    double MinGain,
    double MaxGain,
    ExposureDefaults DayDefaults,
    ExposureDefaults NightDefaults,
    double TargetAduLevel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExposureDefaults? TwilightDefaults = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    ExposureGainPreference Preference = ExposureGainPreference.ExposureFirst,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Hysteresis = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? AdjustmentFactor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? GainStep = null);

/// <summary>Specifies which control is adjusted first by host-metered exposure control.</summary>
public enum ExposureGainPreference
{
    /// <summary>Adjusts exposure before gain.</summary>
    ExposureFirst,

    /// <summary>Adjusts gain before exposure.</summary>
    GainFirst
}

public sealed record ExposureDefaults(
    TimeSpan Exposure,
    double Gain);

public sealed record CameraControlPolicy
{
    /// <summary>Gets the ownership policy for automatic exposure control.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public AutomaticControlOwnership ExposureControl { get; init; }
        = AutomaticControlOwnership.Unspecified;

    /// <summary>Gets the ownership policy for automatic gain control.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public AutomaticControlOwnership GainControl { get; init; }
        = AutomaticControlOwnership.Unspecified;

    /// <summary>Gets the sparse metering policy used by host-metered controls.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureMeteringPolicy? Metering { get; init; }

    /// <summary>Gets the solar-altitude policy used to select day, twilight, or night defaults.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureSolarRegimePolicy? SolarRegimes { get; init; }

    public TemperatureControlDirective Temperature { get; init; } = new();

    public CameraFeatureDirective AutoGain { get; init; }
        = CameraFeatureDirective.Unspecified;

    public CameraFeatureDirective AutoExposure { get; init; }
        = CameraFeatureDirective.Unspecified;
}

/// <summary>Identifies the component that owns an automatic camera control.</summary>
public enum AutomaticControlOwnership
{
    /// <summary>Defers ownership resolution to the host.</summary>
    Unspecified,

    /// <summary>Disables automatic control.</summary>
    Disabled,

    /// <summary>Delegates automatic control to the camera or camera module.</summary>
    CameraNative,

    /// <summary>Uses host-side image metering and bounded setpoint decisions.</summary>
    HostMetered
}

/// <summary>Configures transport-neutral sparse image metering.</summary>
public sealed record CaptureMeteringPolicy
{
    /// <summary>Gets the horizontal distance in pixels between sampled locations.</summary>
    public int XStride { get; init; } = 16;

    /// <summary>Gets the vertical distance in pixels between sampled locations.</summary>
    public int YStride { get; init; } = 16;

    /// <summary>Gets the optional sensor region eligible for metering.</summary>
    public SensorCrop? Region { get; init; }

    /// <summary>Gets rectangular mask regions excluded before reading sensor samples.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SensorCrop>? ExcludedRegions { get; init; }

    /// <summary>Gets a value indicating whether samples outside the calibrated image circle are excluded.</summary>
    public bool UseImageCircle { get; init; } = true;

    /// <summary>Gets the normalized level at or above which a sample is considered saturated.</summary>
    public double SaturationFraction { get; init; } = 0.98;

    /// <summary>Gets the color-filter-array photosites eligible for metering.</summary>
    public CaptureMeteringCfaSelection CfaSelection { get; init; } = CaptureMeteringCfaSelection.Green;
}

/// <summary>Identifies RGGB color-filter-array photosites eligible for metering.</summary>
[Flags]
public enum CaptureMeteringCfaSelection
{
    /// <summary>Selects no photosites.</summary>
    None = 0,

    /// <summary>Selects red photosites.</summary>
    Red = 1,

    /// <summary>Selects green photosites on red rows.</summary>
    GreenOnRedRow = 2,

    /// <summary>Selects green photosites on blue rows.</summary>
    GreenOnBlueRow = 4,

    /// <summary>Selects blue photosites.</summary>
    Blue = 8,

    /// <summary>Selects both green photosite positions.</summary>
    Green = GreenOnRedRow | GreenOnBlueRow,

    /// <summary>Selects every photosite position.</summary>
    All = Red | Green | Blue
}

/// <summary>Defines solar-altitude boundaries for capture control regimes.</summary>
public sealed record CaptureSolarRegimePolicy
{
    /// <summary>Gets the altitude in degrees at or above which the day regime applies.</summary>
    public double DayAltitudeThresholdDegrees { get; init; }

    /// <summary>Gets the altitude in degrees below which the night regime applies.</summary>
    public double NightAltitudeThresholdDegrees { get; init; } = -12;
}

public sealed record TemperatureControlDirective
{
    public TemperatureControlMode Mode { get; init; }
        = TemperatureControlMode.Unspecified;

    public double? TargetC { get; init; }
}

public enum TemperatureControlMode
{
    Unspecified,
    Disabled,
    Target
}

public enum CameraFeatureDirective
{
    Unspecified,
    Disabled,
    Enabled
}
