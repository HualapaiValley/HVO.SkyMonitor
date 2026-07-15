using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>
/// Represents a capture cycle reported by a camera module. Modules submit the
/// raw <see cref="CaptureResult"/> plus timing metadata so the host can store,
/// publish, and record telemetry independent of the acquisition loop.
/// </summary>
public sealed record CaptureLoopSubmission(
    CaptureRequest Request,
    CaptureResult Result,
    DateTimeOffset CaptureStartedUtc,
    TimeSpan EffectiveInterval,
    TimeSpan LoopDuration)
{
    /// <summary>Gets the optional acquisition-critical evidence for this capture cycle.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureCycleEvidence? CycleEvidence { get; init; }
}

/// <summary>
/// Host-facing context that modules (or module runners) use to hand frames back
/// to the agent host. The host owns storage, telemetry, and downstream fan-out.
/// </summary>
public interface ICaptureHostContext
{
    CameraModuleConfig Configuration { get; }

    ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken);
}
