using System;

namespace HVO.SkyMonitor.AgentCore;

public sealed record CameraRigConfig(
    SensorProfile Sensor,
    OpticsProfile Optics,
    RigOrientation Orientation,
    PipelineExposureProfile Pipeline,
    CameraControlPolicy? ControlPolicy = null);

public sealed record SensorProfile(
    string Name,
    int WidthPixels,
    int HeightPixels,
    double PixelSizeMicrons,
    SensorColorMode ColorMode,
    CameraPixelFormat PixelFormat);

public enum SensorColorMode
{
    Mono,
    Color
}

public sealed record OpticsProfile(
    string ProjectionModel,
    double FocalLengthMillimeters,
    double FieldOfViewDegrees,
    double RollDegrees);

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
    ExposureEnvelope? Envelope = null);

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
