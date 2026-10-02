using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>Lifecycle of one manual focus session. Every state is explicit; there is no implicit success.</summary>
public enum ManualFocusSessionState
{
    Idle,
    Active,
    Completed,
    Cancelled,
    TimedOut,
    Faulted
}

/// <summary>What happens to the in-memory samples when a session ends.</summary>
public enum ManualFocusSessionDisposition
{
    /// <summary>Keep the measured samples on the completed session snapshot.</summary>
    Retained,

    /// <summary>Drop the samples; nothing is retained.</summary>
    Discarded
}

/// <summary>A bounded, temporary preview exposure and gain. It is never written to the active rig profile.</summary>
public sealed record ManualFocusPreviewSettings(TimeSpan Exposure, double Gain);

/// <summary>A rectangular region of interest in frame pixel-edge coordinates.</summary>
public sealed record ManualFocusRegion(int X, int Y, int Width, int Height);

/// <summary>Declared bounds for a manual focus session. They are validated before a session starts.</summary>
public sealed record ManualFocusSessionLimits(
    TimeSpan MinimumExposure,
    TimeSpan MaximumExposure,
    double MaximumGain,
    int MaximumSamples,
    TimeSpan MinimumSessionTimeout,
    TimeSpan MaximumSessionTimeout)
{
    public static ManualFocusSessionLimits Default { get; } = new(
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromSeconds(60),
        1000,
        32,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(30));
}

/// <summary>One authorized request to begin a manual focus session.</summary>
public sealed record ManualFocusSessionRequest(
    ManualFocusPreviewSettings Settings,
    ManualFocusRegion? Region,
    TimeSpan SessionTimeout,
    int MinimumAcceptedSources = 3);

/// <summary>Whether the manual focus session capability can run on this CameraAgent, and why not when it cannot.</summary>
public sealed record ManualFocusSessionAvailability(bool Available, string Reason)
{
    public static ManualFocusSessionAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>One bounded preview frame returned by a preview frame source. It is measured in memory and discarded.</summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Justification = "The frame is a short-lived, owned transfer buffer that is consumed by span-based analysis and never retained.")]
public sealed record ManualFocusPreviewFrame(
    string FrameId,
    int Width,
    int Height,
    double[] Pixels,
    bool[] ValidMask,
    bool[] SaturatedMask,
    string? AgentId = null,
    string? RigId = null);

/// <summary>
/// Source of bounded temporary preview frames. A real implementation exists only where the configured camera can
/// produce a preview without corrupting normal acquisition; otherwise availability is false with a reason.
/// </summary>
public interface IManualFocusPreviewFrameSource
{
    ManualFocusSessionAvailability GetAvailability();

    ValueTask<ManualFocusPreviewFrame> AcquireAsync(
        ManualFocusPreviewSettings settings,
        ManualFocusRegion? region,
        CancellationToken cancellationToken);
}

/// <summary>Runs an action while holding exclusive camera acquisition, so normal capture cannot interleave.</summary>
public interface IManualFocusExclusiveAcquisition
{
    Task<T> ExecuteExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);
}

/// <summary>Provenance carried with every measured sample so a value can be reproduced and compared honestly.</summary>
public sealed record ManualFocusSampleProvenance(
    string FrameId,
    string? AgentId,
    string? RigId,
    int FrameWidth,
    int FrameHeight,
    ManualFocusRegion? Region,
    string MetricDefinition,
    string MetricUnits,
    string MetricAlgorithmVersion,
    string MetricSettingsIdentitySha256,
    string PreviewSettingsIdentitySha256);

/// <summary>One image-derived sample with its measurement and provenance. No sample exists without a measurement.</summary>
public sealed record ManualFocusSample(
    int Index,
    DateTimeOffset MeasuredUtc,
    FocusSharpnessMeasurement Measurement,
    ManualFocusSampleProvenance Provenance);

/// <summary>Immutable session view. Samples are empty unless the session completed and was retained.</summary>
public sealed record ManualFocusSessionSnapshot(
    string SessionId,
    ManualFocusSessionState State,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    DateTimeOffset DeadlineUtc,
    ManualFocusPreviewSettings Settings,
    ManualFocusRegion? Region,
    int MinimumAcceptedSources,
    IReadOnlyList<ManualFocusSample> Samples,
    string? FailureReason,
    string? EndReason)
{
    public static ManualFocusSessionSnapshot Idle { get; } = new(
        string.Empty,
        ManualFocusSessionState.Idle,
        default,
        null,
        default,
        new(TimeSpan.Zero, 0),
        null,
        3,
        [],
        null,
        null);
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

/// <summary>Stable reason codes for session termination and unavailable capability reporting.</summary>
public static class ManualFocusReasonCodes
{
    public const string Retained = "retained";
    public const string Discarded = "discarded";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed-out";
    public const string MeasurementFailed = "measurement-failed";
    public const string CameraBusy = "camera-busy";

    /// <summary>Durable session retention/export is deliberately not implemented in this slice.</summary>
    public const string RetentionUnavailable =
        "Durable manual focus session retention and evidence export are not implemented; sessions are in memory only.";

    /// <summary>No live preview frame source can produce an honest focus preview on this CameraAgent.</summary>
    public const string NoLivePreviewSource =
        "No live focus preview frame source is registered on this CameraAgent, and the virtual camera does not model optical defocus, so a session cannot qualify real focus.";
}
