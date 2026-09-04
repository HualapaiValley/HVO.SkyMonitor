using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

internal static class ReplayUiTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    internal static readonly Guid ExecutionId = Guid.Parse("00000000-0000-0000-0000-0000000000ee");
    internal static readonly Guid CaptureId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    internal static readonly Guid ArtifactId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    internal static readonly Guid OutputArtifactId = Guid.Parse("00000000-0000-0000-0000-000000000202");

    internal static ReplayCandidateView Candidate(Guid? captureId = null) => new(
        captureId ?? CaptureId,
        "agent-test",
        "rig-test",
        42,
        Now.AddSeconds(-5),
        Now.AddSeconds(-4),
        "durable",
        "Available",
        true,
        new ReplayCandidateArtifactView(
            ArtifactId, "Raw", null, new string('A', 64), 2048, "Available", "Held", "Available"),
        "Named",
        "revision-active",
        7,
        [
            new ReplayCandidateRevisionView(
                "revision-active", "nightly", "r2", "Active", new string('B', 64), true),
            new ReplayCandidateRevisionView(
                "revision-validated", "nightly", "r3", "Validated", new string('C', 64), false)
        ],
        new ReplayCapacityFactsView("InProcess", 1, 1000, 3600, 86400));

    internal static ReplayExecutionView Execution(
        string status,
        bool terminal,
        bool withNodes = false,
        bool cancellationRequested = false) => new(
        ExecutionId,
        "Replay",
        status,
        terminal,
        CaptureId,
        ArtifactId,
        "revision-active",
        new string('A', 64),
        new string('B', 64),
        new string('C', 64),
        "operator",
        null,
        0,
        1,
        cancellationRequested,
        Now,
        Now,
        Now.AddHours(1),
        Now.AddDays(1),
        Now,
        terminal ? Now.AddMinutes(1) : null,
        null,
        withNodes
            ?
            [
                new ReplayExecutionNodeView(
                    "preview-node",
                    true,
                    terminal ? "Completed" : "Running",
                    null,
                    1,
                    Now,
                    terminal ? Now.AddSeconds(3) : null,
                    [new ReplayExecutionAttemptView(
                        1,
                        terminal ? "Completed" : "Running",
                        terminal ? "Produced" : null,
                        null,
                        Now,
                        terminal ? Now.AddSeconds(3) : null,
                        terminal ? TimeSpan.FromSeconds(3) : null)],
                    [new ReplayExecutionInputView(
                        0, 0, "RawCapture", CaptureId, ArtifactId, new string('D', 64), new string('E', 64))],
                    [new ReplayExecutionOutputView(
                        0, OutputArtifactId, "Preview", "display", new string('F', 64), "Available", null)])
            ]
            : []);
}
