namespace HVO.SkyMonitor.AgentCore;

/// <summary>
/// Optional camera-module capability: acquire one bounded manual-focus preview frame. A preview is not a capture: the
/// host never admits, publishes, stages, or stores it as one, and the module must not advance anything a normal
/// capture advances (sequence counters, scenario timelines, scene stores, staged artifacts). The requested setpoint is
/// scoped to the preview; when the call returns, faults, or is cancelled the module's control state must be what it was
/// before the call, so the next normal capture is unaffected.
/// </summary>
public interface ICameraFocusPreviewCapture
{
    /// <summary>
    /// Stable statement of what a preview from this module can and cannot prove about optical focus. It is shown to
    /// the operator and recorded in every sample's provenance.
    /// </summary>
    CameraFocusPreviewFidelity FocusPreviewFidelity { get; }

    /// <summary>The declared simulated focus model, or null when the module has no simulated focus control.</summary>
    CameraSimulatedFocusModel? SimulatedFocus { get; }

    /// <summary>Captures one preview frame using <see cref="CameraFocusPreviewRequest.Capture"/>'s setpoint.</summary>
    Task<CaptureResult> CaptureFocusPreviewAsync(CameraFocusPreviewRequest request, CancellationToken cancellationToken);
}

/// <summary>One preview request.</summary>
/// <param name="Capture">Still-mode request carrying the preview-scoped setpoint.</param>
/// <param name="SimulatedFocusPosition">
/// Simulated focus position in <see cref="CameraSimulatedFocusModel.Units"/>; must be null when the module declares no
/// simulated focus model and within its declared range otherwise.
/// </param>
public sealed record CameraFocusPreviewRequest(CaptureRequest Capture, double? SimulatedFocusPosition = null);

/// <summary>What a module's preview represents. A virtual preview never qualifies physical focus.</summary>
/// <param name="Kind">Stable fidelity identifier, for example <c>virtual-simulated-defocus</c>.</param>
/// <param name="QualifiesPhysicalFocus">Whether the measured image can qualify a physical lens adjustment.</param>
/// <param name="Limitation">Operator-facing limitation statement.</param>
public sealed record CameraFocusPreviewFidelity(string Kind, bool QualifiesPhysicalFocus, string Limitation);

/// <summary>
/// A declared simulated focus control. Positions are honest simulated units, never a motor or physical scale; the model
/// identity and parameters are recorded with every preview so the rendered blur is reproducible.
/// </summary>
/// <param name="ModelId">Stable model identifier.</param>
/// <param name="Units">Operator-facing unit label.</param>
/// <param name="MinimumPosition">Inclusive lower bound.</param>
/// <param name="MaximumPosition">Inclusive upper bound.</param>
/// <param name="DefaultPosition">Position used when a session starts without one.</param>
/// <param name="ParametersSha256">Content identity of every model parameter, including the declared best focus.</param>
public sealed record CameraSimulatedFocusModel(
    string ModelId,
    string Units,
    double MinimumPosition,
    double MaximumPosition,
    double DefaultPosition,
    string ParametersSha256)
{
    public bool Contains(double position)
        => double.IsFinite(position) && position >= MinimumPosition && position <= MaximumPosition;
}
