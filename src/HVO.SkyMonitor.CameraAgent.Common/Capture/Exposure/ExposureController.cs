using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Exposure;

/// <summary>Explains why an exposure controller selected a new capture setpoint.</summary>
public enum ExposureAdjustmentReason
{
    InitialDefault,
    WithinTolerance,
    TooDark,
    TooBright
}

/// <summary>Immutable exposure-control result with the selected setpoint and decision reason.</summary>
public sealed record ExposureDecision(CaptureSetpoint Setpoint, ExposureAdjustmentReason Reason);

/// <summary>
/// Deterministic bounded feedback controller. It first adjusts exposure and
/// leaves gain unchanged, avoiding oscillation from two simultaneous controls.
/// </summary>
public static class ExposureController
{
    /// <summary>Calculates the next setpoint from an optional prior normalized brightness measurement.</summary>
    public static ExposureDecision Next(PipelineExposureProfile profile, double? normalizedBrightness, bool night)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var envelope = profile.Envelope;
        var defaults = envelope is null
            ? new ExposureDefaults(night ? profile.NightExposure : profile.DayExposure, night ? profile.NightGain : profile.DayGain)
            : night ? envelope.NightDefaults : envelope.DayDefaults;
        var setpoint = Clamp(defaults, envelope);
        if (normalizedBrightness is null || !double.IsFinite(normalizedBrightness.Value))
        {
            return new ExposureDecision(setpoint, ExposureAdjustmentReason.InitialDefault);
        }

        var target = envelope?.TargetAduLevel ?? 0.65d;
        const double tolerance = 0.05d;
        if (Math.Abs(normalizedBrightness.Value - target) <= tolerance || envelope is null)
        {
            return new ExposureDecision(setpoint, ExposureAdjustmentReason.WithinTolerance);
        }

        var multiplier = normalizedBrightness.Value < target ? 1.25d : 0.8d;
        var adjusted = setpoint with { Exposure = TimeSpan.FromTicks((long)(setpoint.Exposure.Ticks * multiplier)) };
        return new ExposureDecision(Clamp(new ExposureDefaults(adjusted.Exposure, adjusted.Gain), envelope),
            normalizedBrightness.Value < target ? ExposureAdjustmentReason.TooDark : ExposureAdjustmentReason.TooBright);
    }

    private static CaptureSetpoint Clamp(ExposureDefaults defaults, ExposureEnvelope? envelope)
    {
        if (envelope is null)
        {
            return new CaptureSetpoint(defaults.Exposure, defaults.Gain, null, null);
        }

        return new CaptureSetpoint(
            TimeSpan.FromTicks(Math.Clamp(defaults.Exposure.Ticks, envelope.MinExposure.Ticks, envelope.MaxExposure.Ticks)),
            Math.Clamp(defaults.Gain, envelope.MinGain, envelope.MaxGain), null, null);
    }
}
