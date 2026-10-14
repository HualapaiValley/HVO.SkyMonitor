using HVO.SkyMonitor.CameraAgent.Common.Options;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>Which live count on this agent shows a lane stage's backlog.</summary>
internal enum MeteorLaneMeasure
{
    None,
    PendingFrames,
    PendingCandidates,
    Delivery,
}

/// <summary>
/// One stage of the meteor detection lane. Its column and row are relative to the lane, whose first column sits right
/// of the raw frame; an input is another stage's key or <see cref="MeteorDetectionLane.RawFrameKey"/>.
/// </summary>
internal sealed record MeteorLaneStage(
    string Key,
    string Title,
    string Caption,
    int Column,
    int Row,
    IReadOnlyList<string> Inputs,
    string Outputs,
    bool Central,
    MeteorLaneMeasure Measure,
    string Description);

/// <summary>
/// The detector lane each transient mode runs beside the processing graph. It reads the raw frame from durable ingress
/// on its own, so the processing steps never delay it. Edge finishes every candidate on this agent; Hybrid relays it
/// and LogicHost finishes it; Off and Central run no stage here. This describes what the worker does in each mode;
/// the live state comes from the worker and delivery snapshots.
/// </summary>
internal static class MeteorDetectionLane
{
    internal const string RawFrameKey = "raw-frame";

    private static readonly MeteorLaneStage DetectorInput = new(
        "detector-input", "Linear detector input", "Calibration and masks", 0, 0, [RawFrameKey],
        "Linear luminance and masks", false, MeteorLaneMeasure.PendingFrames,
        "Builds the detector's linear luminance input from the raw frame, with image-circle, horizon, obstruction, bad-pixel and star masks.");

    private static readonly MeteorLaneStage FrameWindow = new(
        "frame-window", "Durable frame window", "N-2, N-1, N", 0, 1, [RawFrameKey],
        "Ordered source frames", false, MeteorLaneMeasure.PendingFrames,
        "Resolves the two frames before each new frame by capture sequence from the durable runtime store, so the scan survives a restart.");

    private static readonly MeteorLaneStage CausalScan = new(
        "causal-scan", "Causal candidate scan", "N-2, N-1 → N", 1, 0, [DetectorInput.Key, FrameWindow.Key],
        "Causal extraction receipt", false, MeteorLaneMeasure.PendingFrames,
        "Builds a background from N-2 and N-1 and extracts bounded candidates from N. A frame with no candidate completes here.");

    private static readonly MeteorLaneStage PersistCandidate = new(
        "persist-candidate", "Persist candidate", "Restart-safe journal", 2, 0, [CausalScan.Key],
        "Provisional candidate", false, MeteorLaneMeasure.PendingCandidates,
        "Allocates a stable candidate identity and journals its geometry, measured features, lineage and causal receipt before anything else happens to it.");

    private static readonly MeteorLaneStage CenteredWindow = new(
        "centered-window", "Resolve centered window", "N-2 … N+2", 3, 0, [PersistCandidate.Key],
        "Five-frame window", false, MeteorLaneMeasure.PendingCandidates,
        "Waits for frames N+1 and N+2, then loads the exact five-frame window centered on the candidate. If they do not arrive before the candidate timeout, the event is kept for review.");

    private static readonly MeteorLaneStage Assess = new(
        "assess", "Assess and finalize", "Local event and receipt", 4, 0, [CenteredWindow.Key],
        "Event version and finalization receipt", false, MeteorLaneMeasure.PendingCandidates,
        "Repeats extraction on the centered window, matches it to the causal candidate, classifies it and records the event with a finalization receipt. Meteor, satellite and aircraft tracks are validated; an unclear result is marked for review.");

    private static readonly MeteorLaneStage Relay = new(
        "relay", "Relay candidate", "Durable outbox", 3, 0, [PersistCandidate.Key],
        "Central acceptance receipt", false, MeteorLaneMeasure.Delivery,
        "Submits the causal candidate and its N-2, N-1, N source references to LogicHost through a durable outbox. Acceptance means LogicHost has taken the work, not that it has validated the event.");

    private static readonly MeteorLaneStage CentralWindow = new(
        "central-window", "Resolve centered window", "N-2 … N+2 on LogicHost", 4, 0, [Relay.Key],
        "Five-frame window", true, MeteorLaneMeasure.None,
        "LogicHost resolves N+1 and N+2 by capture sequence and freezes the exact five-frame window. This agent does not run this stage.");

    private static readonly MeteorLaneStage CentralValidation = new(
        "central-validation", "Validate event", "Centered extraction and assessment", 5, 0, [CentralWindow.Key],
        "Event version and evidence", true, MeteorLaneMeasure.None,
        "LogicHost repeats extraction on the centered window, matches the causal candidate and records the assessed event. This agent does not run this stage.");

    private static readonly MeteorLaneStage[] EdgeStages = [DetectorInput, FrameWindow, CausalScan, PersistCandidate, CenteredWindow, Assess];

    private static readonly MeteorLaneStage[] HybridStages = [DetectorInput, FrameWindow, CausalScan, PersistCandidate, Relay, CentralWindow, CentralValidation];

    internal static IReadOnlyList<MeteorLaneStage> Stages(TransientOperatingMode mode) => mode switch
    {
        TransientOperatingMode.Edge => EdgeStages,
        TransientOperatingMode.Hybrid => HybridStages,
        _ => [],
    };
}
