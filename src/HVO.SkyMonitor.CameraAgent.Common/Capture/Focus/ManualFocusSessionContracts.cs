using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>Lifecycle of one manual focus session. Every terminal state carries an explicit end reason.</summary>
public enum ManualFocusSessionState
{
    Idle,

    /// <summary>The preview loop is taking back-to-back exposures until the operator stops it.</summary>
    Running,

    /// <summary>The operator stopped the loop.</summary>
    Stopped,

    /// <summary>The labelled safety timeout or the owner-observation timeout ended the loop.</summary>
    TimedOut,

    /// <summary>The camera became unavailable or repeated acquisitions failed.</summary>
    Faulted
}

/// <summary>Where an ended session's bounded history lives. Nothing is durable until it is saved.</summary>
public enum ManualFocusRetentionState
{
    /// <summary>The history exists only in CameraAgent memory and is lost on restart or when a new session starts.</summary>
    InMemoryOnly,

    /// <summary>The history was written as an immutable, checksummed session record.</summary>
    Saved,

    /// <summary>The operator discarded the history; nothing was retained.</summary>
    Discarded
}

/// <summary>How the current target was chosen.</summary>
public enum ManualFocusTargetSource
{
    /// <summary>The brightest isolated unsaturated star near the frame centre.</summary>
    Automatic,

    /// <summary>The star nearest a pixel the operator picked on the preview or from the projected catalog list.</summary>
    Operator
}

/// <summary>A bounded, temporary preview exposure and gain. It is never written to the active rig profile.</summary>
public sealed record ManualFocusPreviewSettings(TimeSpan Exposure, double Gain);

/// <summary>
/// Declared bounds for a manual focus session, validated before a session starts or changes.
/// <see cref="MinimumSamplePeriod"/> paces a camera that returns sooner than an operator can act on a sample, such as
/// VirtualSky, which renders without waiting out its exposure.
/// </summary>
public sealed record ManualFocusSessionLimits(
    TimeSpan MinimumExposure,
    TimeSpan MaximumExposure,
    double MaximumGain,
    int HistoryCapacity,
    TimeSpan MinimumSafetyTimeout,
    TimeSpan MaximumSafetyTimeout,
    TimeSpan DefaultSafetyTimeout,
    TimeSpan SampleDeadlineGrace,
    TimeSpan ObserverTimeout,
    int MaximumConsecutiveFailures,
    TimeSpan StopWaitTimeout,
    TimeSpan MinimumSamplePeriod)
{
    public static ManualFocusSessionLimits Default { get; } = new(
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromSeconds(60),
        1000,
        100,
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        3,
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMilliseconds(500));
}

/// <summary>One authorized request to begin a continuous manual focus loop.</summary>
/// <param name="Settings">Temporary preview exposure and gain.</param>
/// <param name="SimulatedFocusPosition">Starting simulated focus position, when the camera declares a simulated model.</param>
/// <param name="Target">Optional source-frame pixel to search near; the centroid is always measured from pixels.</param>
/// <param name="SafetyTimeout">Labelled hard limit after which the loop stops even if nobody presses Stop.</param>
public sealed record ManualFocusSessionRequest(
    ManualFocusPreviewSettings Settings,
    double? SimulatedFocusPosition = null,
    PixelPoint? Target = null,
    TimeSpan? SafetyTimeout = null);

/// <summary>A change applied from the next exposure on. Null members are unchanged.</summary>
public sealed record ManualFocusAdjustment(
    ManualFocusPreviewSettings? Settings = null,
    double? SimulatedFocusPosition = null,
    PixelPoint? Target = null,
    bool ResetToAutomaticTarget = false);

/// <summary>Whether a manual focus session can run on this CameraAgent, and why not when it cannot.</summary>
public sealed record ManualFocusSessionAvailability(
    bool Available,
    string Reason,
    string? ModuleType = null,
    CameraFocusPreviewFidelity? Fidelity = null,
    CameraSimulatedFocusModel? SimulatedFocus = null)
{
    public static ManualFocusSessionAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>One preview acquired through the capture owner's module. It is measured and dropped, never admitted.</summary>
public sealed record ManualFocusPreview(
    CameraFrame Frame,
    string ModuleType,
    long ModuleGeneration,
    CameraFocusPreviewFidelity Fidelity,
    CameraSimulatedFocusModel? SimulatedFocus,
    DateTimeOffset RequestedUtc,
    DateTimeOffset CompletedUtc,
    MeteringImageCircle? ImageCircle = null);

/// <summary>Source of bounded temporary preview frames through the configured camera owner.</summary>
public interface IManualFocusPreviewSource
{
    ManualFocusSessionAvailability GetAvailability();

    /// <exception cref="ManualFocusSessionUnavailableException">No module can produce a preview now.</exception>
    Task<ManualFocusPreview> AcquireAsync(
        ManualFocusPreviewSettings settings,
        double? simulatedFocusPosition,
        CancellationToken cancellationToken);
}

/// <summary>A star the projected catalog places inside the preview, offered as a target hint. Never a measurement.</summary>
public sealed record ManualFocusCatalogStar(string Id, string? Name, double Magnitude, PixelPoint Pixel);

/// <summary>Provenance carried with every sample so a value can be reproduced and compared honestly.</summary>
public sealed record ManualFocusSampleProvenance(
    DateTimeOffset FrameTimestampUtc,
    int FrameWidth,
    int FrameHeight,
    CameraPixelFormat PixelFormat,
    string FrameSha256,
    string? SceneId,
    string ModuleType,
    long ModuleGeneration,
    string FidelityKind,
    bool QualifiesPhysicalFocus,
    string? SimulatedFocusModelId,
    string? SimulatedFocusParametersSha256,
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight,
    string SamplerAlgorithmVersion,
    string MetricDefinition,
    string MetricUnits,
    string MetricAlgorithmVersion,
    string MetricSettingsIdentitySha256,
    string PreviewSettingsIdentitySha256);

/// <summary>One image-derived sample. Invalid measurements are samples too; they never carry a width.</summary>
public sealed record ManualFocusSample(
    long Sequence,
    DateTimeOffset MeasuredUtc,
    ManualFocusPreviewSettings Settings,
    double? SimulatedFocusPosition,
    ManualFocusTargetSource TargetSource,
    FocusStarMeasurement Measurement,
    ManualFocusSampleProvenance Provenance);

/// <summary>The latest display images. Display stretches only; measurements always use the linear samples.</summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Justification = "Immutable encoded image bytes handed to the UI; the coordinator never mutates them after publication.")]
public sealed record ManualFocusPreviewImages(
    long Sequence,
    byte[] OverviewJpeg,
    int OverviewWidth,
    int OverviewHeight,
    int OverviewBinFactor,
    int FrameWidth,
    int FrameHeight,
    byte[]? StarJpeg,
    int StarWindowX,
    int StarWindowY,
    int StarWindowWidth,
    int StarWindowHeight,
    IReadOnlyList<ManualFocusCatalogStar> CatalogStars);

/// <summary>Immutable session view.</summary>
public sealed record ManualFocusSessionSnapshot(
    string SessionId,
    ManualFocusSessionState State,
    string? OwnerId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    DateTimeOffset SafetyDeadlineUtc,
    ManualFocusPreviewSettings Settings,
    double? SimulatedFocusPosition,
    PixelPoint? Target,
    ManualFocusTargetSource TargetSource,
    long TotalSamples,
    int HistoryCapacity,
    IReadOnlyList<ManualFocusSample> History,
    ManualFocusSample? Best,
    string? LastFailure,
    string? EndReason,
    string? ModuleType,
    CameraFocusPreviewFidelity? Fidelity,
    CameraSimulatedFocusModel? SimulatedFocus,
    ManualFocusRetentionState Retention,
    string? SavedRecordId)
{
    public static ManualFocusSessionSnapshot Idle { get; } = new(
        string.Empty, ManualFocusSessionState.Idle, null, default, null, default, new(TimeSpan.Zero, 0), null, null,
        ManualFocusTargetSource.Automatic, 0, 0, [], null, null, null, null, null, null,
        ManualFocusRetentionState.InMemoryOnly, null);

    public ManualFocusSample? Latest => History.Count == 0 ? null : History[^1];

    public bool IsRunning => State == ManualFocusSessionState.Running;
}

public sealed class ManualFocusSessionValidationException : InvalidOperationException
{
    public ManualFocusSessionValidationException()
    {
    }

    public ManualFocusSessionValidationException(string message) : base(message)
    {
    }

    public ManualFocusSessionValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class ManualFocusSessionConflictException : InvalidOperationException
{
    public ManualFocusSessionConflictException()
    {
    }

    public ManualFocusSessionConflictException(string message) : base(message)
    {
    }

    public ManualFocusSessionConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class ManualFocusSessionUnavailableException : InvalidOperationException
{
    public ManualFocusSessionUnavailableException()
    {
    }

    public ManualFocusSessionUnavailableException(string reasonCode, string message) : base(message)
    {
        ReasonCode = reasonCode;
    }

    /// <summary>Stable <see cref="ManualFocusReasonCodes"/> value recorded as a session end reason.</summary>
    public string ReasonCode { get; } = ManualFocusReasonCodes.CameraWithdrawn;

    public ManualFocusSessionUnavailableException(string message) : base(message)
    {
    }

    public ManualFocusSessionUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class ManualFocusSessionStateException : InvalidOperationException
{
    public ManualFocusSessionStateException()
    {
    }

    public ManualFocusSessionStateException(string message) : base(message)
    {
    }

    public ManualFocusSessionStateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Stable reason codes for session termination, and operator texts for unavailable capability reporting. A session's
/// <see cref="ManualFocusSessionSnapshot.EndReason"/> is always one of the codes; the detail is in
/// <see cref="ManualFocusSessionSnapshot.LastFailure"/>.
/// </summary>
public static class ManualFocusReasonCodes
{
    public const string StoppedByOperator = "stopped-by-operator";
    public const string SafetyTimeout = "safety-timeout";
    public const string ObserverLost = "owner-not-observing";
    public const string HostStopping = "host-stopping";
    public const string CameraWithdrawn = "camera-withdrawn";
    public const string AdmissionUnavailable = "capture-admission-unavailable";
    public const string RepeatedFailures = "repeated-acquisition-failures";
    public const string SampleDeadlineExceeded = "sample-deadline-exceeded";
    public const string PreviewUnmeasurable = "preview-unmeasurable";
    public const string LoopFailed = "loop-failed";

    public const string NoModule =
        "The camera module is not running, so no focus preview can be taken. Wait for capture to start, then try again.";

    public const string AdmissionFailClosed =
        "Capture admission is unavailable after a durable publication failure; focus previews are refused until verified restart recovery.";

    public const string CameraWithdrawnMessage =
        "The camera module was withdrawn by its owner (configuration change, restart, or shutdown).";

    public static string ModuleWithoutPreview(string moduleType)
        => $"The configured camera module '{moduleType}' does not provide bounded focus previews, so a manual focus session cannot start.";
}
