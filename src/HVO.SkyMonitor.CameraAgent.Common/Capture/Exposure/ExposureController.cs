using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Exposure;

/// <summary>Immutable exposure-control result with the selected setpoint and decision reason.</summary>
public sealed record ExposureDecision(CaptureSetpoint Setpoint, CaptureControlDecisionReason Reason);

/// <summary>
/// Deterministic bounded feedback controller. It first adjusts exposure and
/// leaves gain unchanged, avoiding oscillation from two simultaneous controls.
/// </summary>
public static class ExposureController
{
    /// <summary>Gets the configured starting setpoint for a solar regime.</summary>
    public static CaptureSetpoint Initial(PipelineExposureProfile profile, CaptureSolarRegime regime)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var envelope = profile.Envelope;
        var defaults = envelope is null
            ? regime == CaptureSolarRegime.Night
                ? new ExposureDefaults(profile.NightExposure, profile.NightGain)
                : new ExposureDefaults(profile.DayExposure, profile.DayGain)
            : Defaults(envelope, regime);
        return Clamp(defaults, envelope);
    }

    /// <summary>Gets the exact starting setpoint declared by a validated schedule profile.</summary>
    public static CaptureSetpoint Initial(CaptureScheduleSetpointProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new CaptureSetpoint(profile.Exposure, profile.Gain, null, profile.TargetFps);
    }

    /// <summary>Calculates the next setpoint from the active controls and a host measurement.</summary>
    public static ExposureDecision Next(
        PipelineExposureProfile profile,
        CaptureSetpoint active,
        double? normalizedBrightness,
        CaptureSolarRegime regime,
        bool adjustExposure,
        bool adjustGain,
        bool saturationRejected = false,
        bool regimeChanged = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(active);
        var envelope = profile.Envelope;
        if (envelope is null || (!adjustExposure && !adjustGain))
        {
            return new ExposureDecision(active, CaptureControlDecisionReason.NoSample);
        }

        if (regimeChanged)
        {
            var regimeSetpoint = Initial(profile, regime);
            return new ExposureDecision(
                active with
                {
                    Exposure = adjustExposure ? regimeSetpoint.Exposure : active.Exposure,
                    Gain = adjustGain ? regimeSetpoint.Gain : active.Gain
                },
                CaptureControlDecisionReason.SolarRegimeChanged);
        }

        var bounded = ClampControlled(active, envelope, adjustExposure, adjustGain);
        if (bounded.Exposure != active.Exposure || bounded.Gain != active.Gain)
        {
            return new ExposureDecision(bounded, CaptureControlDecisionReason.EnvelopeClamped);
        }

        if (saturationRejected)
        {
            return new ExposureDecision(
                Adjust(active, envelope, increase: false, adjustExposure, adjustGain, normalizedBrightness: null).Setpoint,
                CaptureControlDecisionReason.SaturationRejected);
        }
        if (normalizedBrightness is null || !double.IsFinite(normalizedBrightness.Value))
        {
            return new ExposureDecision(active, CaptureControlDecisionReason.NoSample);
        }
        if (Math.Abs(normalizedBrightness.Value - envelope.TargetAduLevel) <= (envelope.Hysteresis ?? 0.05))
        {
            return new ExposureDecision(active, CaptureControlDecisionReason.WithinHysteresis);
        }

        return Adjust(
            active,
            envelope,
            normalizedBrightness.Value < envelope.TargetAduLevel,
            adjustExposure,
            adjustGain,
            normalizedBrightness.Value);
    }

    private static ExposureDecision Adjust(
        CaptureSetpoint active,
        ExposureEnvelope envelope,
        bool increase,
        bool adjustExposure,
        bool adjustGain,
        double? normalizedBrightness)
    {
        var exposureFirst = envelope.Preference == ExposureGainPreference.ExposureFirst;
        if (exposureFirst)
        {
            if (adjustExposure && TryAdjustExposure(
                    active, envelope, increase, normalizedBrightness, out var exposure))
            {
                return new ExposureDecision(active with { Exposure = exposure }, CaptureControlDecisionReason.ExposureAdjusted);
            }
            if (adjustGain && TryAdjustGain(active, envelope, increase, normalizedBrightness, out var gain))
            {
                return new ExposureDecision(active with { Gain = gain }, CaptureControlDecisionReason.GainAdjusted);
            }
        }
        else
        {
            if (adjustGain && TryAdjustGain(active, envelope, increase, normalizedBrightness, out var gain))
            {
                return new ExposureDecision(active with { Gain = gain }, CaptureControlDecisionReason.GainAdjusted);
            }
            if (adjustExposure && TryAdjustExposure(
                    active, envelope, increase, normalizedBrightness, out var exposure))
            {
                return new ExposureDecision(active with { Exposure = exposure }, CaptureControlDecisionReason.ExposureAdjusted);
            }
        }

        return new ExposureDecision(
            active,
            increase ? CaptureControlDecisionReason.UpperLimitReached : CaptureControlDecisionReason.LowerLimitReached);
    }

    private static bool TryAdjustExposure(
        CaptureSetpoint active,
        ExposureEnvelope envelope,
        bool increase,
        double? normalizedBrightness,
        out TimeSpan exposure)
    {
        var current = active.Exposure.Ticks;
        var factor = envelope.AdjustmentFactor ?? 1.25;
        var scale = normalizedBrightness is > 0
            ? Math.Clamp(envelope.TargetAduLevel / normalizedBrightness.Value, 1 / factor, factor)
            : increase ? factor : 1 / factor;
        var candidate = increase
            ? (long)Math.Ceiling(current * scale)
            : (long)Math.Floor(current * scale);
        if (increase && candidate <= current && current < envelope.MaxExposure.Ticks)
        {
            candidate = current + 1;
        }
        candidate = Math.Clamp(candidate, envelope.MinExposure.Ticks, envelope.MaxExposure.Ticks);
        exposure = TimeSpan.FromTicks(candidate);
        return candidate != current;
    }

    private static bool TryAdjustGain(
        CaptureSetpoint active,
        ExposureEnvelope envelope,
        bool increase,
        double? normalizedBrightness,
        out double gain)
    {
        var step = envelope.GainStep ?? 10;
        var desired = normalizedBrightness is > 0 && active.Gain > 0
            ? active.Gain * envelope.TargetAduLevel / normalizedBrightness.Value
            : active.Gain + (increase ? step : -step);
        var delta = Math.Clamp(desired - active.Gain, -step, step);
        gain = Math.Clamp(
            active.Gain + delta,
            envelope.MinGain,
            envelope.MaxGain);
        return gain != active.Gain;
    }

    private static CaptureSetpoint ClampControlled(
        CaptureSetpoint active,
        ExposureEnvelope envelope,
        bool adjustExposure,
        bool adjustGain)
        => active with
        {
            Exposure = adjustExposure
                ? TimeSpan.FromTicks(Math.Clamp(
                    active.Exposure.Ticks,
                    envelope.MinExposure.Ticks,
                    envelope.MaxExposure.Ticks))
                : active.Exposure,
            Gain = adjustGain
                ? Math.Clamp(active.Gain, envelope.MinGain, envelope.MaxGain)
                : active.Gain
        };

    private static ExposureDefaults Defaults(ExposureEnvelope envelope, CaptureSolarRegime regime)
        => regime switch
        {
            CaptureSolarRegime.Day => envelope.DayDefaults,
            CaptureSolarRegime.Twilight => envelope.TwilightDefaults ?? new ExposureDefaults(
                TimeSpan.FromTicks((envelope.DayDefaults.Exposure.Ticks + envelope.NightDefaults.Exposure.Ticks) / 2),
                (envelope.DayDefaults.Gain + envelope.NightDefaults.Gain) / 2),
            CaptureSolarRegime.Night => envelope.NightDefaults,
            _ => throw new ArgumentOutOfRangeException(nameof(regime))
        };

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
