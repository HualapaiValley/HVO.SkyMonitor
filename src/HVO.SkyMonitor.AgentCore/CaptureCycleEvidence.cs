using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Identifies why a camera module call began.</summary>
public enum CaptureStartReason
{
    /// <summary>The first capture in a runner lifetime began.</summary>
    Initial,

    /// <summary>The minimum-start deadline was reached.</summary>
    DeadlineReached,

    /// <summary>Acquisition-critical work completed after the minimum-start deadline.</summary>
    DeadlineOverrun,

    /// <summary>Continuous-mode acquisition-critical work completed.</summary>
    ContinuousReady,

    /// <summary>A capture began after failure recovery.</summary>
    FailureRecovery
}

/// <summary>Identifies the solar-altitude regime used for a control decision.</summary>
public enum CaptureSolarRegime
{
    /// <summary>The day control policy applies.</summary>
    Day,

    /// <summary>The twilight control policy applies.</summary>
    Twilight,

    /// <summary>The night control policy applies.</summary>
    Night
}

/// <summary>Identifies the outcome of host-side capture metering.</summary>
public enum CaptureMeteringOutcome
{
    /// <summary>An eligible normalized level was measured.</summary>
    Measured,

    /// <summary>No frame was available to meter.</summary>
    NoFrame,

    /// <summary>The frame format did not support host metering.</summary>
    UnsupportedFormat,

    /// <summary>No samples satisfied the configured region, mask, or CFA selection.</summary>
    NoEligibleSamples,

    /// <summary>Saturation prevented a usable level from being accepted.</summary>
    SaturationRejected
}

/// <summary>Identifies the reason for a capture control decision.</summary>
public enum CaptureControlDecisionReason
{
    /// <summary>Automatic exposure and gain control were disabled.</summary>
    Disabled,

    /// <summary>The camera or camera module owned automatic control.</summary>
    CameraNative,

    /// <summary>No usable host-metering sample was available.</summary>
    NoSample,

    /// <summary>Saturation prevented a host-metered adjustment.</summary>
    SaturationRejected,

    /// <summary>The measured level was within the configured hysteresis band.</summary>
    WithinHysteresis,

    /// <summary>Exposure was adjusted for the next capture.</summary>
    ExposureAdjusted,

    /// <summary>Gain was adjusted for the next capture.</summary>
    GainAdjusted,

    /// <summary>Exposure and gain were adjusted for the next capture.</summary>
    ExposureAndGainAdjusted,

    /// <summary>The lower exposure and gain limits prevented further adjustment.</summary>
    LowerLimitReached,

    /// <summary>The upper exposure and gain limits prevented further adjustment.</summary>
    UpperLimitReached,

    /// <summary>Active camera controls were brought back inside the configured envelope.</summary>
    EnvelopeClamped,

    /// <summary>A solar-regime transition selected the new regime's configured defaults.</summary>
    SolarRegimeChanged,

    /// <summary>The module failed to apply the decided controls before ingress.</summary>
    SetpointApplicationFailed
}

/// <summary>Records bounded host-side metering work and its result.</summary>
public sealed record CaptureMeteringEvidence(
    [property: JsonRequired] DateTimeOffset StartedUtc,
    [property: JsonRequired] DateTimeOffset CompletedUtc,
    [property: JsonRequired] long ConsideredSampleCount,
    [property: JsonRequired] long AcceptedSampleCount,
    [property: JsonRequired] long SaturatedSampleCount,
    [property: JsonRequired] long ScannedBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? NormalizedLevel,
    [property: JsonRequired] CaptureMeteringOutcome Outcome);

/// <summary>Records the active controls and the bounded decision for the next capture.</summary>
public sealed record CaptureControlDecisionEvidence(
    [property: JsonRequired] DateTimeOffset StartedUtc,
    [property: JsonRequired] DateTimeOffset CompletedUtc,
    [property: JsonRequired] TimeSpan ActiveExposure,
    [property: JsonRequired] double ActiveGain,
    [property: JsonRequired] TimeSpan DecidedExposure,
    [property: JsonRequired] double DecidedGain,
    [property: JsonRequired] CaptureControlDecisionReason Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? SetpointAppliedUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ActiveTargetFps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DecidedTargetFps = null);

/// <summary>Records acquisition-critical cadence, ownership, metering, decision, and handoff facts.</summary>
public sealed record CaptureCycleEvidence(
    [property: JsonRequired] CaptureCadenceMode CadenceMode,
    [property: JsonRequired] CaptureStartReason StartReason,
    [property: JsonRequired] AutomaticControlOwnership ExposureControl,
    [property: JsonRequired] AutomaticControlOwnership GainControl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CaptureSolarRegime? SolarRegime,
    [property: JsonRequired] DateTimeOffset ModuleCallStartedUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeSpan? ObservedInterExposureGap,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CaptureMeteringEvidence? Metering,
    [property: JsonRequired] CaptureControlDecisionEvidence Decision,
    [property: JsonRequired] DateTimeOffset IngressHandoffStartedUtc)
{
    /// <summary>Gets monotonic actual-start lateness relative to the requested deadline.</summary>
    [JsonRequired]
    public TimeSpan MonotonicStartJitter { get; init; }
}
