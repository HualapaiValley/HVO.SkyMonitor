using HVO.SkyMonitor.CameraAgent.Common.Options;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>Which live count on this agent shows a lane stage's backlog.</summary>
internal enum MeteorLaneMeasure
{
    None,
    PendingFrames,
    PendingCandidates,
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
    MeteorLaneMeasure Measure,
    string Description);

/// <summary>
/// The detector lane each transient mode runs beside the processing graph. It reads the raw frame from durable ingress
/// independently of display processing and assesses candidates locally.
/// </summary>
internal static class MeteorDetectionLane
{
    internal const string RawFrameKey = "raw-frame";

    private static readonly MeteorLaneStage DetectorInput = new(
        "detector-input", "Linear detector input", "Calibration and masks", 0, 0, [RawFrameKey],
        "Linear luminance and masks", MeteorLaneMeasure.PendingFrames,
        "Builds the detector's linear luminance input from the raw frame, with image-circle, horizon, obstruction, bad-pixel and star masks.");

    private static readonly MeteorLaneStage FrameWindow = new(
        "frame-window", "Durable frame window", "N-2, N-1, N", 0, 1, [RawFrameKey],
        "Ordered source frames", MeteorLaneMeasure.PendingFrames,
        "Resolves the two frames before each new frame by capture sequence from the durable runtime store, so the scan survives a restart.");

    private static readonly MeteorLaneStage CausalScan = new(
        "causal-scan", "Causal candidate scan", "N-2, N-1 → N", 1, 0, [DetectorInput.Key, FrameWindow.Key],
        "Causal extraction receipt", MeteorLaneMeasure.PendingFrames,
        "Builds a background from N-2 and N-1 and extracts bounded candidates from N. A frame with no candidate completes here.");

    private static readonly MeteorLaneStage PersistCandidate = new(
        "persist-candidate", "Persist candidate", "Restart-safe journal", 2, 0, [CausalScan.Key],
        "Provisional candidate", MeteorLaneMeasure.PendingCandidates,
        "Allocates a stable candidate identity and journals its geometry, measured features, lineage and causal receipt before anything else happens to it.");

    private static readonly MeteorLaneStage CenteredWindow = new(
        "centered-window", "Resolve centered window", "N-2 … N+2", 3, 0, [PersistCandidate.Key],
        "Five-frame window", MeteorLaneMeasure.PendingCandidates,
        "Waits for frames N+1 and N+2, then loads the exact five-frame window centered on the candidate. If they do not arrive before the candidate timeout, the event is kept for review.");

    private static readonly MeteorLaneStage Assess = new(
        "assess", "Assess and finalize", "Local event and receipt", 4, 0, [CenteredWindow.Key],
        "Event version and finalization receipt", MeteorLaneMeasure.PendingCandidates,
        "Repeats extraction on the centered window, matches it to the causal candidate, classifies it and records the event with a finalization receipt. Meteor, satellite and aircraft tracks are validated; an unclear result is marked for review.");

    private static readonly MeteorLaneStage[] EdgeStages = [DetectorInput, FrameWindow, CausalScan, PersistCandidate, CenteredWindow, Assess];

    internal static IReadOnlyList<MeteorLaneStage> Stages(TransientOperatingMode mode)
        => mode == TransientOperatingMode.Edge ? EdgeStages : [];

}
