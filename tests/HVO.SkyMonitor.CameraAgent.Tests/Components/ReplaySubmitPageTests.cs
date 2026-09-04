using Bunit;
using Bunit.JSInterop;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ReplaySubmitPageTests
{
    private static readonly Guid CaptureId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly string[] ForbiddenActions =
        ["upload", "publish", "promote", "set as current", "preference"];

    [TestMethod]
    public void Render_ShowsTheFreezeSummaryEligibleRevisionsAndRunnerFacts()
    {
        using var context = Configure(new ReplayUiService(), out _);

        var cut = Render(context);

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Inputs this request will freeze", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("revision-active", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Configured replay execution facts", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Runner facts", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("not yet been observed", StringComparison.Ordinal));
        });
        Assert.HasCount(1, cut.FindAll("#replay-revision"));
    }

    [TestMethod]
    public void Render_ExposesNoUploadPublishOrPromotionControl()
    {
        using var context = Configure(new ReplayUiService(), out _);

        var cut = Render(context);

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Submit replay request", StringComparison.Ordinal)));
        foreach (var action in cut.FindAll("button, a").Select(static element => element.TextContent))
        {
            foreach (var forbidden in ForbiddenActions)
            {
                Assert.IsFalse(action.Contains(forbidden, StringComparison.OrdinalIgnoreCase), action);
            }
        }
    }

    [TestMethod]
    public void Render_WhenAuthorizationIsRevoked_NavigatesToAccessDenied()
    {
        using var context = Configure(
            new ReplayUiService
            {
                CandidateResult = OperatorUiResult<ReplayCandidateView>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.")
            },
            out _);

        _ = Render(context);

        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>()
            .Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_WhenTheFreezeSummaryIsUnavailable_ShowsTheSanitizedFailure()
    {
        using var context = Configure(
            new ReplayUiService
            {
                CandidateResult = OperatorUiResult<ReplayCandidateView>.Failure(
                OperatorUiResultKind.Unavailable, "The replay freeze summary is unavailable.")
            },
            out _);

        var cut = Render(context);

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Replay candidate unavailable", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("The replay freeze summary is unavailable.", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void Submit_ConfirmsThroughANativeDialogAndRestoresTriggerFocus()
    {
        using var context = Configure(new ReplayUiService(), out _);
        var cut = Render(context);
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("#replay-submit-trigger")));

        cut.Find("#replay-submit-trigger").Click();

        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("dialog")));
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations
            .Any(static invocation => invocation.Identifier == "showModal")));
        cut.FindAll("dialog button").First(static button =>
            button.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations
            .Any(static invocation => invocation.Identifier == "focusById")));
    }

    [TestMethod]
    public void Submit_WhenAccepted_ReportsTheAcceptedOutcomeWithAProgressLink()
    {
        using var context = Configure(new ReplayUiService(), out var service);
        var cut = Render(context);
        ConfirmSubmit(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Replay request accepted", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains(
                $"/operations/pipeline/replays/{ReplayUiTestData.ExecutionId:D}", StringComparison.Ordinal));
        });
        Assert.HasCount(1, service.Submissions);
    }

    [TestMethod]
    public void Submit_WhenTheDurableRequestAlreadyExists_ReportsTheExistingRequestOutcome()
    {
        using var context = Configure(
            new ReplayUiService { Outcome = ReplaySubmissionOutcome.ExistingRequestReturned }, out _);
        var cut = Render(context);
        ConfirmSubmit(cut);

        cut.WaitForAssertion(() => Assert.IsTrue(
            cut.Markup.Contains("Existing replay request returned", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Submit_WhenTheQueueIsAtCapacity_RetriesWithTheSameIdempotencyKey()
    {
        using var context = Configure(
            new ReplayUiService
            {
                SubmitResult = OperatorUiResult<ReplaySubmissionView>.Failure(
                    OperatorUiResultKind.Unavailable, CameraAgentReplayUiService.CapacityMessage)
            },
            out var service);
        var cut = Render(context);

        ConfirmSubmit(cut);
        cut.WaitForAssertion(() => Assert.IsTrue(
            cut.Markup.Contains(CameraAgentReplayUiService.CapacityMessage, StringComparison.Ordinal)));
        ConfirmSubmit(cut);

        cut.WaitForAssertion(() => Assert.HasCount(2, service.Submissions));
        Assert.AreEqual(service.Submissions[0].Key, service.Submissions[1].Key);
        Assert.AreEqual("revision-active", service.Submissions[0].RevisionId);
    }

    [TestMethod]
    public void Submit_WhenDurableStateConflicts_ShowsTheConflictAndTakesANewKeyOnRetry()
    {
        using var context = Configure(
            new ReplayUiService
            {
                SubmitResult = OperatorUiResult<ReplaySubmissionView>.Failure(
                    OperatorUiResultKind.Conflict, CameraAgentReplayUiService.ConflictMessage)
            },
            out var service);
        var cut = Render(context);

        ConfirmSubmit(cut);
        cut.WaitForAssertion(() => Assert.IsTrue(
            cut.Markup.Contains(CameraAgentReplayUiService.ConflictMessage, StringComparison.Ordinal)));
        ConfirmSubmit(cut);

        cut.WaitForAssertion(() => Assert.HasCount(2, service.Submissions));
        Assert.AreNotEqual(service.Submissions[0].Key, service.Submissions[1].Key);
    }

    private static void ConfirmSubmit(IRenderedComponent<ReplaySubmitPage> cut)
    {
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("#replay-submit-trigger")));
        cut.Find("#replay-submit-trigger").Click();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("dialog")));
        cut.FindAll("dialog button").First(static button =>
            button.TextContent.Contains("Confirm replay request", StringComparison.Ordinal)).Click();
    }

    private static IRenderedComponent<ReplaySubmitPage> Render(BunitContext context)
    {
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("captureId", CaptureId.ToString("D")));
        return context.Render<ReplaySubmitPage>();
    }

    private static BunitContext Configure(ReplayUiService service, out ReplayUiService configured)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentReplayUiService>(service);
        context.Services.AddSingleton(new CameraAgentReplayRunnerFactsProjection(
            Options.Create(new CameraAgentHostOptions())));
        configured = service;
        return context;
    }

    private sealed class ReplayUiService : ICameraAgentReplayUiService
    {
        internal List<(string Key, string RevisionId)> Submissions { get; } = [];

        internal OperatorUiResult<ReplayCandidateView>? CandidateResult { get; init; }

        internal OperatorUiResult<ReplaySubmissionView>? SubmitResult { get; init; }

        internal ReplaySubmissionOutcome Outcome { get; init; } = ReplaySubmissionOutcome.Accepted;

        public ValueTask<OperatorUiResult<ReplayCandidateView>> GetReplayCandidateAsync(
            Guid captureId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(CandidateResult
                ?? OperatorUiResult<ReplayCandidateView>.Success(ReplayUiTestData.Candidate(captureId)));

        public ValueTask<OperatorUiResult<ReplaySubmissionView>> SubmitReplayAsync(
            Guid captureId,
            string revisionId,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
        {
            Submissions.Add((idempotencyKey, revisionId));
            return ValueTask.FromResult(SubmitResult
                ?? OperatorUiResult<ReplaySubmissionView>.Success(new ReplaySubmissionView(
                    Outcome,
                    ReplayUiTestData.Execution("Pending", terminal: false))));
        }

        public ValueTask<OperatorUiResult<ReplayExecutionView>> GetReplayExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<ReplayExecutionView>> CancelReplayAsync(
            Guid executionId,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
