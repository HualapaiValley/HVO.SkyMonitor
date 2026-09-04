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
public sealed class ReplayExecutionPageTests
{
    private static readonly string[] ForbiddenActions =
        ["upload", "publish", "promote", "set as current", "preference"];

    [TestMethod]
    public void Render_ShowsProgressAttemptsNodeStateAndTheSourceCaptureLink()
    {
        using var context = Configure(new ReplayUiService(
            ReplayUiTestData.Execution("Running", terminal: false, withNodes: true)), out _);

        var cut = Render(context);

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Running (in progress)", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("preview-node", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Attempt 1", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Runner facts", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains(
                $"/gallery/{ReplayUiTestData.CaptureId:D}", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void Render_LinksOutputContentOnlyThroughTheExecutionScopedRoute()
    {
        using var context = Configure(new ReplayUiService(
            ReplayUiTestData.Execution("Completed", terminal: true, withNodes: true)), out _);

        var cut = Render(context);

        cut.WaitForAssertion(() =>
        {
            var expected =
                $"/api/v1/operations/processing-graphs/executions/{ReplayUiTestData.ExecutionId:D}" +
                $"/outputs/{ReplayUiTestData.OutputArtifactId:D}/content";
            Assert.IsTrue(cut.Markup.Contains(expected, StringComparison.Ordinal), cut.Markup);
            Assert.IsFalse(
                cut.Markup.Contains($"/api/v1/artifacts/{ReplayUiTestData.OutputArtifactId:D}", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void Render_ForATerminalExecution_HidesCancellationAndExposesNoPublishControl()
    {
        using var context = Configure(new ReplayUiService(
            ReplayUiTestData.Execution("Completed", terminal: true, withNodes: true)), out _);

        var cut = Render(context);

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Completed (terminal)", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll("#replay-cancel-trigger"));
        });
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
            new ReplayUiService(null)
            {
                ReadResult = OperatorUiResult<ReplayExecutionView>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required.")
            },
            out _);

        _ = Render(context);

        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>()
            .Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_WhenTheExecutionIsMissing_ShowsTheSanitizedFailure()
    {
        using var context = Configure(
            new ReplayUiService(null)
            {
                ReadResult = OperatorUiResult<ReplayExecutionView>.Failure(
                    OperatorUiResultKind.NotFound, "The requested replay execution was not found.")
            },
            out _);

        var cut = Render(context);

        cut.WaitForAssertion(() => Assert.IsTrue(
            cut.Markup.Contains("Replay execution not found", StringComparison.Ordinal)));
        Assert.IsEmpty(cut.FindAll("[role='alert']"));
        Assert.IsFalse(cut.FindAll("button").Any(static button => button.TextContent.Contains("Try again", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Cancel_ConfirmsThroughANativeDialogAndRestoresTriggerFocus()
    {
        using var context = Configure(new ReplayUiService(
            ReplayUiTestData.Execution("Running", terminal: false, withNodes: true)), out _);
        var cut = Render(context);
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("#replay-cancel-trigger")));

        cut.Find("#replay-cancel-trigger").Click();

        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("dialog")));
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations
            .Any(static invocation => invocation.Identifier == "showModal")));
        cut.FindAll("dialog button").First(static button =>
            button.TextContent.Contains("Keep running", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations
            .Any(static invocation => invocation.Identifier == "focusById")));
    }

    [TestMethod]
    public void Cancel_RetryAfterAnUnavailableResponseReusesTheSameIdempotencyKey()
    {
        using var context = Configure(
            new ReplayUiService(ReplayUiTestData.Execution("Running", terminal: false, withNodes: true))
            {
                CancelResult = OperatorUiResult<ReplayExecutionView>.Failure(
                    OperatorUiResultKind.Unavailable, "The replay cancellation could not be completed.")
            },
            out var service);
        var cut = Render(context);

        ConfirmCancel(cut);
        cut.WaitForAssertion(() => Assert.IsTrue(
            cut.Markup.Contains("The replay cancellation could not be completed.", StringComparison.Ordinal)));
        ConfirmCancel(cut);

        cut.WaitForAssertion(() => Assert.HasCount(2, service.CancellationKeys));
        Assert.AreEqual(service.CancellationKeys[0], service.CancellationKeys[1]);
    }

    [TestMethod]
    public void Cancel_WhenAccepted_ShowsTheRecordedCancellationAndStopsOfferingIt()
    {
        using var context = Configure(
            new ReplayUiService(ReplayUiTestData.Execution("Running", terminal: false, withNodes: true))
            {
                CancelResult = OperatorUiResult<ReplayExecutionView>.Success(
                    ReplayUiTestData.Execution("Cancelled", terminal: true, cancellationRequested: true))
            },
            out _);
        var cut = Render(context);

        ConfirmCancel(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains(
                "Cancellation was recorded against the durable replay request.", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll("#replay-cancel-trigger"));
        });
    }

    [TestMethod]
    public void PollInterval_NeverRunsFasterThanFiveSeconds()
        => Assert.IsTrue(ReplayExecutionPage.PollInterval >= TimeSpan.FromSeconds(5));

    private static void ConfirmCancel(IRenderedComponent<ReplayExecutionPage> cut)
    {
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("#replay-cancel-trigger")));
        cut.Find("#replay-cancel-trigger").Click();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("dialog")));
        cut.FindAll("dialog button").First(static button =>
            button.TextContent.Contains("Confirm cancellation", StringComparison.Ordinal)).Click();
    }

    private static IRenderedComponent<ReplayExecutionPage> Render(BunitContext context)
        => context.Render<ReplayExecutionPage>(parameters =>
            parameters.Add(page => page.ExecutionId, ReplayUiTestData.ExecutionId));

    private static BunitContext Configure(ReplayUiService service, out ReplayUiService configured)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentReplayUiService>(service);
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton(new CameraAgentReplayRunnerFactsProjection(
            Options.Create(new CameraAgentHostOptions())));
        configured = service;
        return context;
    }

    private sealed class ReplayUiService(ReplayExecutionView? execution) : ICameraAgentReplayUiService
    {
        internal List<string> CancellationKeys { get; } = [];

        internal OperatorUiResult<ReplayExecutionView>? ReadResult { get; init; }

        internal OperatorUiResult<ReplayExecutionView>? CancelResult { get; init; }

        public ValueTask<OperatorUiResult<ReplayCandidateView>> GetReplayCandidateAsync(
            Guid captureId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<ReplaySubmissionView>> SubmitReplayAsync(
            Guid captureId,
            string revisionId,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<ReplayExecutionView>> GetReplayExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(ReadResult
                ?? OperatorUiResult<ReplayExecutionView>.Success(execution!));

        public ValueTask<OperatorUiResult<ReplayExecutionView>> CancelReplayAsync(
            Guid executionId,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
        {
            CancellationKeys.Add(idempotencyKey);
            return ValueTask.FromResult(CancelResult
                ?? OperatorUiResult<ReplayExecutionView>.Success(
                    ReplayUiTestData.Execution("Cancelled", terminal: true, cancellationRequested: true)));
        }
    }
}
