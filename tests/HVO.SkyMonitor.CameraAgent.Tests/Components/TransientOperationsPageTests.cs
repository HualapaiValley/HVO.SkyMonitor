using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class TransientOperationsPageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 4, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void HybridLaneRendersEachOutcomeHonestlyAndLinksTheEventRun()
    {
        var candidateCapture = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var candidate = Candidate("Validated");
        var view = View(TransientOperatingMode.Hybrid) with
        {
            RecentOutcomes =
            [
                Outcome(46, workState: "pending", frameState: null),
                Outcome(45, workState: "pending", frameState: "retry_wait", attempts: 2),
                Outcome(44, workState: "candidate_persisted", frameState: "history", succeeded: true, candidates: 2,
                    completed: 1, captureId: candidateCapture),
                Outcome(43, workState: "completed", frameState: "completed", succeeded: true,
                    reason: TransientCandidateExtractionReasonCodes.NoCandidate),
                Outcome(42, workState: "quarantined", frameState: "quarantined", reason: "worker-evidence-corrupt"),
                Outcome(41, workState: "completed", frameState: "history", reason: "causal-context-pending"),
            ],
            RecentCandidates = [candidate],
            LatestEventRun = new TransientLatestEventRun(candidateCapture, 44, executionId),
        };
        using var context = CreateContext(new TestTransientOperationsUiService(view));

        var cut = context.Render<TransientOperationsPage>();

        cut.WaitForAssertion(() => Assert.AreEqual("Hybrid detector healthy", cut.Find("#transients-state-title").TextContent));
        var rows = cut.FindAll(".transients-outcome-table tbody tr");
        Assert.HasCount(6, rows);
        StringAssert.Contains(rows[0].TextContent, "Waiting for detector", StringComparison.Ordinal);
        StringAssert.Contains(rows[1].TextContent, "After attempt 2", StringComparison.Ordinal);
        Assert.AreEqual("Retrying", rows[1].QuerySelector(".state-chip")!.TextContent);
        StringAssert.Contains(rows[2].TextContent, "2 causal candidates", StringComparison.Ordinal);
        StringAssert.Contains(rows[2].TextContent, "1 of 2 complete", StringComparison.Ordinal);
        Assert.AreEqual("current-row", rows[2].GetAttribute("class"));
        StringAssert.Contains(rows[3].TextContent, "No candidate", StringComparison.Ordinal);
        Assert.AreEqual("state-chip success", rows[3].QuerySelector(".state-chip")!.GetAttribute("class"));
        StringAssert.Contains(rows[4].TextContent, "Detector failed", StringComparison.Ordinal);
        Assert.AreEqual("worker-evidence-corrupt", rows[4].QuerySelector("small.failure")!.TextContent);
        Assert.AreEqual("state-chip failure", rows[4].QuerySelector(".state-chip")!.GetAttribute("class"));
        StringAssert.Contains(rows[5].TextContent, "No prior context", StringComparison.Ordinal);
        Assert.AreEqual($"/gallery/{candidateCapture:D}", rows[2].QuerySelector("a")!.GetAttribute("href"));
        Assert.AreEqual($"/operations/pipeline/executions/{executionId:D}", cut.Find("a#transients-event-run").GetAttribute("href"));
        Assert.AreEqual($"/transients/{candidate.CandidateId:D}", cut.Find(".transients-latest dd a").GetAttribute("href"));
        Assert.AreEqual("state-chip success", cut.Find(".transients-latest .state-chip").GetAttribute("class"));
        Assert.AreEqual("Delivering", cut.Find(".transients-handoff .state-chip").TextContent);
        StringAssert.Contains(cut.Find(".transients-handoff").TextContent, "Last acknowledged", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UnavailableDeliveryIsNeverShownAsNothingToDeliver()
    {
        var delivery = new TransientCandidateDeliverySnapshot(
            TransientCandidateDeliveryAvailability.Unhealthy, "logic-host-unreachable", 4, 4, 0, 0,
            Now.AddMinutes(-12), null, Now.AddSeconds(-5), Now, Now);
        using var context = CreateContext(new TestTransientOperationsUiService(
            View(TransientOperatingMode.Hybrid) with { Delivery = delivery }));

        var cut = context.Render<TransientOperationsPage>();

        cut.WaitForAssertion(() => Assert.AreEqual("Unavailable", cut.Find(".transients-handoff .state-chip").TextContent));
        var handoff = cut.Find(".transients-handoff").TextContent;
        StringAssert.Contains(handoff, "Candidates wait durably; none are lost.", StringComparison.Ordinal);
        StringAssert.Contains(handoff, "12m ago", StringComparison.Ordinal);
        StringAssert.Contains(handoff, "Never", StringComparison.Ordinal);
        Assert.DoesNotContain("Not exported", handoff);
    }

    [TestMethod]
    public void OffAndCentralModesSayThisAgentExtractsNothing()
    {
        var off = View(TransientOperatingMode.Off) with
        {
            Worker = new TransientWorkerSnapshot(TransientWorkerAvailability.Disabled, "mode-disabled", 0, 0, Now),
            RecentOutcomes = [],
            RecentCandidates = [],
        };
        using (var context = CreateContext(new TestTransientOperationsUiService(off)))
        {
            var cut = context.Render<TransientOperationsPage>();

            cut.WaitForAssertion(() => Assert.AreEqual("Transient detection is off", cut.Find("#transients-state-title").TextContent));
            Assert.AreEqual("Not exported", cut.Find(".transients-handoff .state-chip").TextContent);
            StringAssert.Contains(cut.Find(".transients-outcomes").TextContent, "Detection is off.", StringComparison.Ordinal);
            var eventRun = cut.Find("button#transients-event-run");
            Assert.IsTrue(eventRun.HasAttribute("disabled"));
            Assert.AreEqual("No recent capture produced a causal candidate.", eventRun.GetAttribute("title"));
            Assert.AreEqual("transients-event-run-reason", eventRun.GetAttribute("aria-describedby"));
        }

        using (var context = CreateContext(new TestTransientOperationsUiService(off with { Mode = TransientOperatingMode.Central })))
        {
            var cut = context.Render<TransientOperationsPage>();

            cut.WaitForAssertion(() => Assert.AreEqual("Central detection", cut.Find("#transients-state-title").TextContent));
            StringAssert.Contains(cut.Find(".transients-handoff").TextContent, "This agent delivers captures, not candidates.", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".status-icon.failure"));
        }
    }

    [TestMethod]
    public void DegradedWorkerLinksToTheTransientQuarantine()
    {
        using var context = CreateContext(new TestTransientOperationsUiService(View(TransientOperatingMode.Edge) with
        {
            Worker = new TransientWorkerSnapshot(TransientWorkerAvailability.Degraded, "quarantined-work", 1, 0, Now),
        }));

        var cut = context.Render<TransientOperationsPage>();

        cut.WaitForAssertion(() => Assert.AreEqual("Edge detector degraded", cut.Find("#transients-state-title").TextContent));
        Assert.AreEqual("/operations/quarantine?kind=TransientRuntime", cut.Find(".transients-state small a").GetAttribute("href"));
        StringAssert.Contains(cut.Find(".transients-handoff").TextContent, "assesses them locally", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EachUnavailableReadIsReportedWithoutHidingTheOthers()
    {
        var candidate = Candidate("NeedsReview");
        using var context = CreateContext(new TestTransientOperationsUiService(View(TransientOperatingMode.Edge) with
        {
            RecentOutcomes = null,
            OutcomesUnavailable = "Recent detector outcomes are temporarily unavailable.",
            RecentCandidates = [candidate],
        }));

        var cut = context.Render<TransientOperationsPage>();

        cut.WaitForAssertion(() => Assert.AreEqual(
            "Recent detector outcomes are temporarily unavailable.", cut.Find(".transients-outcomes [role=alert]").TextContent));
        Assert.HasCount(1, cut.FindAll(".transients-candidate-table tbody tr"));
        Assert.AreEqual("state-chip warning", cut.Find(".transients-candidate-table .state-chip").GetAttribute("class"));
        Assert.AreEqual("Edge detector healthy", cut.Find("#transients-state-title").TextContent);
    }

    [TestMethod]
    public void AFailedReadShowsTheMessageAndUnauthorizedNavigatesAway()
    {
        using (var context = CreateContext(new TestTransientOperationsUiService(
                   OperatorUiResult<TransientOperationsView>.Failure(OperatorUiResultKind.Unavailable, "The detector lane is temporarily unavailable."))))
        {
            var cut = context.Render<TransientOperationsPage>();

            cut.WaitForAssertion(() => StringAssert.Contains(
                cut.Find("[role=alert]").TextContent, "The detector lane is temporarily unavailable.", StringComparison.Ordinal));
            Assert.IsTrue(cut.Find("#transients-profile").HasAttribute("disabled"));
        }

        using (var context = CreateContext(new TestTransientOperationsUiService(
                   OperatorUiResult<TransientOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required."))))
        {
            var navigation = context.Services.GetRequiredService<NavigationManager>();

            context.Render<TransientOperationsPage>();

            Assert.EndsWith("/Account/AccessDenied", navigation.Uri);
        }
    }

    [TestMethod]
    public void DetectorProfileDialogShowsTheVersionedThresholdsAndLaneBounds()
    {
        using var context = CreateContext(new TestTransientOperationsUiService(View(TransientOperatingMode.Edge)));
        var cut = context.Render<TransientOperationsPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#transients-profile").HasAttribute("disabled")));

        cut.Find("#transients-profile").Click();

        var dialog = cut.Find("dialog.transients-dialog");
        Assert.AreEqual("edge-v1", dialog.QuerySelector("#transients-profile-heading")!.TextContent);
        var text = dialog.TextContent;
        StringAssert.Contains(text, "50 ADU", StringComparison.Ordinal);
        StringAssert.Contains(text, "Up to 32", StringComparison.Ordinal);
        StringAssert.Contains(text, "N-2..N+2", StringComparison.Ordinal);
        StringAssert.Contains(text, "adjacent-candidate-association-v1", StringComparison.Ordinal);
        StringAssert.Contains(text, "Optional lane", StringComparison.Ordinal);
        StringAssert.Contains(text, "1 s to 60 s", StringComparison.Ordinal);
        Assert.AreEqual(1, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "showModal"));

        cut.Find("#transients-profile-close").Click();

        Assert.IsEmpty(cut.FindAll("dialog.transients-dialog"));
    }

    private static BunitContext CreateContext(ICameraAgentTransientOperationsUiService service)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(service);
        return context;
    }

    private static TransientOperationsView View(TransientOperatingMode mode)
    {
        var detection = new TransientDetectionOptions { Mode = mode };
        return new(
            Now,
            mode,
            false,
            true,
            new TransientDetectorProfileView(
                CameraAgentTransientOperationsUiService.ExtractionProfileName,
                TransientCandidateExtractionProfiles.EdgeV1,
                detection.CandidateTimeoutMinutes,
                detection.MaximumAttempts,
                detection.RetryInitialDelaySeconds,
                detection.RetryMaximumDelaySeconds,
                detection.MaximumAdjacentStartIntervalSeconds,
                detection.StarMaximumMagnitude,
                detection.StarMaximumResults,
                detection.Association.AlgorithmVersion,
                detection.Association.MaximumStartIntervalSeconds,
                detection.Association.MaximumEndpointGapPixels,
                detection.Association.MinimumAbsolutePrincipalAxisAlignment),
            new TransientWorkerSnapshot(TransientWorkerAvailability.Healthy, "ready", 0, 0, Now),
            new TransientCandidateDeliverySnapshot(
                TransientCandidateDeliveryAvailability.Healthy, "ready", 0, 0, 0, 0, null, Now.AddSeconds(-4), Now, Now, Now),
            [],
            null,
            [],
            null,
            null);
    }

    private static TransientCaptureOutcome Outcome(
        long sequence,
        string workState,
        string? frameState,
        bool succeeded = false,
        string? reason = null,
        int attempts = 1,
        int candidates = 0,
        int completed = 0,
        Guid? captureId = null) => new(
            captureId ?? Guid.NewGuid(), sequence, Now.AddMinutes(-sequence), workState, frameState,
            frameState is null ? null : succeeded, reason, attempts, candidates, completed, 0, Now);

    private static CameraAgentTransientOperatorCandidate Candidate(string eventState) => new(
        Guid.NewGuid(), Guid.NewGuid(), "persisted", eventState, "finalized", Now, Now,
        "Available", "Available", "Available", "Available");

    private sealed class TestTransientOperationsUiService(OperatorUiResult<TransientOperationsView> result)
        : ICameraAgentTransientOperationsUiService
    {
        public TestTransientOperationsUiService(TransientOperationsView view)
            : this(OperatorUiResult<TransientOperationsView>.Success(view))
        {
        }

        public ValueTask<OperatorUiResult<TransientOperationsView>> GetOverviewAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(result);
    }
}
