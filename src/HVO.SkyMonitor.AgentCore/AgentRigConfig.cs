using System;

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
    TimeSpan? CaptureFailureMaximumDelay = null);

public sealed record ExposureEnvelope(
    TimeSpan MinExposure,
    TimeSpan MaxExposure,
    double MinGain,
    double MaxGain,
    ExposureDefaults DayDefaults,
    ExposureDefaults NightDefaults,
    double TargetAduLevel);

public sealed record ExposureDefaults(
    TimeSpan Exposure,
    double Gain);

public sealed record CameraControlPolicy
{
    public TemperatureControlDirective Temperature { get; init; } = new();

    public CameraFeatureDirective AutoGain { get; init; }
        = CameraFeatureDirective.Unspecified;

    public CameraFeatureDirective AutoExposure { get; init; }
        = CameraFeatureDirective.Unspecified;
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
