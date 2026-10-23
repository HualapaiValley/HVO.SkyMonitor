using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Tests.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2025:Do not pass disposables into unawaited tasks", Justification = "Delayed reads are completed before disposing the renderer.")]
public sealed class CentralOperationsWorkspaceTests
{
    private static readonly Guid Observatory = Guid.NewGuid();
    private static readonly Guid Camera = Guid.NewGuid();
    private static readonly Guid Execution = Guid.NewGuid();
    private static readonly Guid Revision = Guid.NewGuid();
    private static readonly CentralOperationsQuery Selection = new() { Execution = Execution, Request = "retained-request-key" };

    [TestMethod]
    public void ExactCentralExecutionShowsFrozenIdentityAndReceivedHistorySeparation()
    {
        using var context = Context(out var read, out _, out _);
        var cut = Render(context);
        cut.Markup.Should().Contain("Exact central execution").And.Contain(Execution.ToString())
            .And.Contain("source-sha256").And.Contain("definition-sha256").And.Contain("central-plan-sha256")
            .And.Contain("separate central work").And.Contain("remain immutable").And.Contain("retained-request-key");
        cut.FindAll("img").Should().BeEmpty();
        read.Requests.Should().ContainSingle().Which.Should().Be((Observatory, Camera, Selection));
        cut.Markup.Should().NotContain("LeaseToken").And.NotContain("LeaseOwner");
    }

    [TestMethod]
    [DataRow("Viewer", false)]
    [DataRow("Manager", true)]
    [DataRow("Owner", true)]
    public void RecoveryAffordancesFollowRoleWithReplayOwnerCancellationException(string roleName, bool recovery)
    {
        var role = Enum.Parse<ObservatoryMembershipRole>(roleName);
        using var context = Context(out var read, out _, out _);
        read.Result = Snapshot(role);
        var cut = Render(context);
        cut.FindAll("button").Any(b => b.TextContent.Contains("Replay these", StringComparison.Ordinal)).Should().Be(recovery);
        cut.FindAll("button").Any(b => b.TextContent.Contains("execution cancellation", StringComparison.Ordinal)).Should().BeTrue("the Viewer owns this replay");
        read.Result = read.Result with { Execution = read.Result.Execution! with { Execution = read.Result.Execution.Execution with { ActorId = "another-user" } } };
        cut.Render();
        cut.FindAll("button").Any(b => b.TextContent.Contains("execution cancellation", StringComparison.Ordinal)).Should().Be(recovery);
    }

    [TestMethod]
    public async Task ReplayResubmissionRetainsExactRequestKeyAndDoesNotReplaceInspectedExecution()
    {
        using var context = Context(out _, out var mutations, out _);
        var cut = Render(context);
        var button = cut.FindAll("button").Single(b => b.TextContent == "Replay these exact sources and revision");
        await button.ClickAsync(new());
        await cut.FindAll("button").Single(b => b.TextContent == "Replay these exact sources and revision").ClickAsync(new());
        mutations.Commands.Should().HaveCount(2).And.OnlyContain(c => c.IdempotencyKey == "retained-request-key" && c.Selection.Execution == Execution);
        cut.Markup.Should().Contain(Execution.ToString()).And.Contain("recorded-action");
    }

    [TestMethod]
    public void EmptyCaptureWindowWithContinuationDoesNotClaimHistoryIsComplete()
    {
        using var context = Context(out var read, out _, out _);
        read.Result = new(ObservatoryMembershipRole.Viewer, false, Jobs: new([], "opaque-next", 50));
        var cut = Render(context, new() { View = "jobs" });
        cut.Markup.Should().Contain("No jobs in this bounded capture window").And.Contain("Next jobs page").And.Contain("50");
        read.Requests.Should().ContainSingle();
    }

    [TestMethod]
    public void DerivedStagingAndStoredFactsKeepSeparateTimesAndReasons()
    {
        using var context = Context(out var read, out _, out _);
        var proposal = new ProcessingGraphDeliveryProposalView(Guid.NewGuid(), Guid.NewGuid(), Revision, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), null, "capability-hash", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1));
        var fact = new ProcessingGraphDeliveryFactView(Guid.NewGuid(), proposal.Id, "RolledBack", "Device", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(2), "operator-rollback", "local-revision", "definition", "shared-plan", "local-plan");
        read.Result = new(ObservatoryMembershipRole.Viewer, false,
            Proposals: new([new(proposal, fact with { Kind = "Accepted" }, fact, fact, new("Staged", "derived-basis"))], null),
            Proposal: proposal, Facts: new([fact], "next-facts"));
        var cut = Render(context, new() { View = "delivery", Proposal = proposal.Id });
        cut.Markup.Should().Contain("Staged (derived; staging timestamp is not recorded)").And.Contain("RolledBack")
            .And.Contain("operator-rollback").And.Contain("Occurred 1970-01-01 00:00:00Z").And.Contain("Recorded 1970-01-01 02:00:00Z")
            .And.Contain("Next facts page").And.Contain("Heartbeat does not establish delivery");
        cut.FindAll("button").Should().ContainSingle();
    }

    [TestMethod]
    [DataRow("Viewer", false)]
    [DataRow("Manager", false)]
    [DataRow("Owner", true)]
    public void PublicationTargetsOneEligibleArtifactIndependentlyOfMaterialization(string roleName, bool canRelease)
    {
        var role = Enum.Parse<ObservatoryMembershipRole>(roleName);
        using var context = Context(out var read, out _, out _);
        var eligible = Artifact(true);
        var unavailable = Artifact(false);
        read.Result = new(role, false, Artifacts: new([eligible, unavailable], null));
        var cut = Render(context, new() { View = "artifacts", Capture = Guid.NewGuid() });
        cut.FindAll("button").Count(b => b.TextContent.StartsWith("Release artifact", StringComparison.Ordinal)).Should().Be(canRelease ? 1 : 0);
        cut.Markup.Should().Contain(eligible.ArtifactId.ToString()).And.Contain(unavailable.ArtifactId.ToString())
            .And.Contain("Materialization and public release are independent");
    }

    [TestMethod]
    public async Task PendingReadCannotRestoreOldScopeAfterDisconnectionAndReconnectRechecks()
    {
        using var context = Context(out var read, out _, out _);
        var cut = Render(context);
        var original = read.Result;
        var pending = new TaskCompletionSource<CentralOperationsSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        read.Load = _ => pending.Task;
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        await cut.InvokeAsync(cut.Instance.SuspendAsync);
        cut.FindAll(".central-protected").Should().BeEmpty();
        pending.SetResult(original);
        await refresh;
        cut.FindAll(".central-protected").Should().BeEmpty();
        read.Load = null;
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.Markup.Should().Contain(Execution.ToString());
    }

    [TestMethod]
    public async Task BrowserCredentialLossClearsFactsAndPreventsMutationDespiteCircuitPrincipal()
    {
        var session = true;
        using var context = Context(out _, out var mutations, out _, () => session);
        var cut = Render(context);
        session = false;
        await cut.FindAll("button").Single(b => b.TextContent == "Replay these exact sources and revision").ClickAsync(new());
        cut.FindAll(".central-protected").Should().BeEmpty();
        mutations.Commands.Should().BeEmpty();
        cut.Markup.Should().Contain("Session unavailable");
    }

    [TestMethod]
    public async Task AuthenticationLossClearsProtectedFactsAndActionOutcome()
    {
        using var context = Context(out _, out var mutations, out var auth);
        var cut = Render(context);
        await cut.FindAll("button").Single(b => b.TextContent == "Replay these exact sources and revision").ClickAsync(new());
        mutations.Commands.Should().ContainSingle();
        await cut.InvokeAsync(() => auth.Change(new ClaimsPrincipal()));
        cut.Markup.Should().NotContain(Execution.ToString()).And.NotContain("recorded-action");
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("failure")]
    [DataRow("denied")]
    public async Task FailedMalformedAndDeniedReadsClearEarlierEvidenceAndOfferRecovery(string state)
    {
        using var context = Context(out var read, out _, out _);
        var cut = Render(context);
        read.Load = _ => state switch
        {
            "invalid" => throw new ArgumentException("private-cursor-detail"),
            "failure" => throw new IOException("private-storage-detail"),
            _ => Task.FromResult<CentralOperationsSnapshot?>(null)
        };
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.FindAll(".central-protected").Should().BeEmpty();
        cut.Markup.Should().NotContain("private-").And.Contain("Start from the first page");
    }

    [TestMethod]
    public async Task RealJobInspectorRendersIndependentChildContinuationsAndSameJobVersusSuccessorLanguage()
    {
        await using var world = await CameraOperationsReadServiceTests.World.CreateAsync(ObservatoryMembershipRole.Manager);
        var snapshot = await CentralOperationsWorkspaceServiceTests.Read(world).ReadAsync(CentralOperationsWorkspaceServiceTests.Member(),
            world.A.Observatory, world.A.Camera, new() { View = "jobs", Job = world.A.Job });
        using var context = Context(out var read, out _, out _);
        var detail = snapshot!.Job!;
        read.Result = snapshot with
        {
            Job = detail with
            {
                Job = detail.Job with { Status = CentralDerivativeJobStatus.TerminalFailure, GraphExecutionId = null },
                Attempts = detail.Attempts with { NextCursor = "attempt-next" },
                Inputs = detail.Inputs with { NextCursor = "input-next" },
                Outputs = detail.Outputs with { NextCursor = "output-next" }
            }
        };
        var cut = Render(context, new() { View = "jobs", Job = world.A.Job });
        cut.Markup.Should().Contain("Requeue same job for a new attempt").And.Contain("Request successor reprocessing")
            .And.Contain("Next attempts page").And.Contain("Next inputs page").And.Contain("Next outputs page")
            .And.NotContain(CameraOperationsReadServiceTests.World.LeaseToken.ToString());
        cut.FindAll(".central-attempts li").Should().HaveCount(2);
        cut.FindAll(".central-inputs li").Should().HaveCount(2);
    }

    [TestMethod]
    public async Task EventReviewUsesRenderedAssessmentStrongEtagAndStableActionKey()
    {
        using var context = Context(out var read, out var mutations, out _);
        var summary = new CentralTransientEventSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "agent", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, HVO.SkyMonitor.Processing.TransientEventState.Provisional,
            CentralTransientReviewState.NeedsReview, Guid.NewGuid(), HVO.SkyMonitor.Processing.TransientClassification.Meteor, null, 500_000, "\"AAAAAAAAAAE=\"");
        read.Result = new(ObservatoryMembershipRole.Manager, false, Event: new(summary, null!), CanReviewEvent: true);
        var cut = Render(context, new() { View = "events", Event = summary.CentralTransientEventId });
        await cut.FindAll("button").Single(b => b.TextContent == "Confirm exact event assessment").ClickAsync(new());
        await cut.FindAll("button").Single(b => b.TextContent == "Confirm exact event assessment").ClickAsync(new());
        mutations.Commands.Should().HaveCount(2);
        mutations.Commands[0].ETag.Should().Be(summary.ETag);
        mutations.Commands[0].Assessment.Should().Be(summary.ActiveAssessmentId);
        mutations.Commands[0].IdempotencyKey.Should().Be(mutations.Commands[1].IdempotencyKey);
    }

    [TestMethod]
    public async Task GraphNodeRecoveryLinksExactExecutionInsteadOfOfferingIndependentRequeue()
    {
        await using var world = await CameraOperationsReadServiceTests.World.CreateAsync(ObservatoryMembershipRole.Manager);
        var snapshot = await CentralOperationsWorkspaceServiceTests.Read(world).ReadAsync(CentralOperationsWorkspaceServiceTests.Member(),
            world.A.Observatory, world.A.Camera, new() { View = "jobs", Job = world.A.Job });
        using var context = Context(out var read, out _, out _);
        read.Result = snapshot! with { Job = snapshot!.Job! with { Job = snapshot.Job.Job with { Status = CentralDerivativeJobStatus.TerminalFailure } } };
        var cut = Render(context, new() { View = "jobs", Job = world.A.Job });
        cut.Markup.Should().NotContain("Requeue same job for a new attempt");
        cut.FindAll("a").Single(a => a.TextContent == "Inspect the exact central graph execution for replay").GetAttribute("href")
            .Should().Contain("view=executions").And.Contain($"execution={world.A.Execution:D}");
    }

    [TestMethod]
    public async Task SuspendedViewCannotDispatchAnActionWaitingForAuthentication()
    {
        using var context = Context(out _, out var mutations, out var auth);
        var cut = Render(context);
        var pending = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        auth.Pending = pending.Task;
        var click = cut.FindAll("button").Single(b => b.TextContent == "Replay these exact sources and revision").ClickAsync(new());
        await cut.InvokeAsync(cut.Instance.SuspendAsync);
        pending.SetResult(new(CentralOperationsWorkspaceServiceTests.Member()));
        await click;
        mutations.Commands.Should().BeEmpty();
        cut.FindAll(".central-protected").Should().BeEmpty();
    }

    private static OperationsArtifactSummary Artifact(bool eligible) => new(Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Preview,
        "default", "v1", "image/png", 42, "exact-sha256", eligible ? CentralArtifactObjectState.Available : CentralArtifactObjectState.Quarantined,
        CentralReconstructionState.Complete, eligible ? null : "object-conflict", DateTimeOffset.UnixEpoch, "/unused", "Central", 1,
        "preview", "1", "options-hash", "2", eligible, false);
    private static IRenderedComponent<CentralOperationsWorkspace> Render(BunitContext context, CentralOperationsQuery? selection = null)
        => context.Render<CentralOperationsWorkspace>(p => p.Add(c => c.ObservatoryId, Observatory).Add(c => c.CameraId, Camera).Add(c => c.Query, selection ?? Selection));
    private static BunitContext Context(out Read read, out Mutations mutations, out Authentication auth, Func<bool>? session = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./Components/Shared/CentralOperationsWorkspace.razor.js");
        module.Setup<bool>("hasSession", _ => session?.Invoke() is not false).SetResult(true);
        module.Setup<bool>("hasSession", _ => session?.Invoke() is false).SetResult(false);
        read = new Read(); mutations = new Mutations(); auth = new Authentication();
        context.Services.AddSingleton<ICentralOperationsWorkspaceService>(read);
        context.Services.AddSingleton<ICentralOperationsWorkspaceMutationService>(mutations);
        context.Services.AddSingleton<AuthenticationStateProvider>(auth);
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddLogging();
        context.Renderer.SetRendererInfo(new RendererInfo("Server", true));
        return context;
    }
    private static CentralOperationsSnapshot Snapshot(ObservatoryMembershipRole role = ObservatoryMembershipRole.Owner)
    {
        var summary = new CentralProcessingGraphExecutionSummary(Execution, "Replay", "Pending", Revision, Observatory, Camera,
            Guid.NewGuid(), Guid.NewGuid(), "Manual", "member", "test-reason", 1, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        return new(role, false, Executions: new([summary], "next-executions"), Execution: new(summary, Guid.NewGuid(), null,
            "definition-sha256", "central-plan-sha256", "anchor-sha256", 1, 0, 0, 0, null, null, null, null, null,
            [new(0, "raw", summary.AnchorSourceArtifactId, "source-sha256", 12, DateTimeOffset.UnixEpoch)], new([], null)));
    }
    private sealed class Read : ICentralOperationsWorkspaceService
    {
        internal CentralOperationsSnapshot? Result { get; set; } = Snapshot();
        internal Func<CentralOperationsQuery, Task<CentralOperationsSnapshot?>>? Load { get; set; }
        internal List<(Guid Observatory, Guid Camera, CentralOperationsQuery Query)> Requests { get; } = [];
        public Task<CentralOperationsSnapshot?> ReadAsync(ClaimsPrincipal principal, Guid observatory, Guid camera, CentralOperationsQuery query, CancellationToken cancellationToken = default)
        { Requests.Add((observatory, camera, query)); return Load?.Invoke(query) ?? Task.FromResult(Result); }
    }
    private sealed class Mutations : ICentralOperationsWorkspaceMutationService
    {
        internal List<CentralWorkspaceCommand> Commands { get; } = [];
        public Task<string> ExecuteAsync(ClaimsPrincipal principal, Guid observatory, Guid camera, CentralWorkspaceCommand command, CancellationToken cancellationToken = default)
        { Commands.Add(command); return Task.FromResult("recorded-action"); }
    }
    private sealed class Authentication : AuthenticationStateProvider
    {
        private Task<AuthenticationState> current = Task.FromResult(new AuthenticationState(CentralOperationsWorkspaceServiceTests.Member()));
        internal Task<AuthenticationState>? Pending { get; set; }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Pending ?? current;
        internal void Change(ClaimsPrincipal principal) { current = Task.FromResult(new AuthenticationState(principal)); NotifyAuthenticationStateChanged(current); }
    }
}
