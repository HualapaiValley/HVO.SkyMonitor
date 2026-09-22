using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingExecutionPagesTests
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RunningId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CompletedId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CaptureId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static void ConfigureTransientRun(BunitContext context)
    {
        context.Services.AddSingleton(Options.Create(new CameraAgentHostOptions()));
        context.Services.AddSingleton(Mock.Of<ITransientRuntimeManagement>());
    }

    [TestMethod]
    public void ExecutionsPage_SeparatesLiveAndReplayAndMarksActiveWork()
    {
        using var context = new BunitContext();
        var service = new GraphUiService { Executions = View(live: [Execution(RunningId, ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Running)], replay: [Execution(CompletedId, ProcessingGraphExecutionClass.Replay, ProcessingGraphExecutionStatus.Completed)]) };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));

        var cut = context.Render<ProcessingExecutionsPage>();

        cut.WaitForElement(".execution-table");
        Assert.AreEqual("Processing executions", cut.Find("h1").TextContent.Trim());
        var sections = cut.FindAll(".execution-class");
        Assert.HasCount(2, sections);
        StringAssert.Contains(sections[0].TextContent, "Live executions", StringComparison.Ordinal);
        StringAssert.Contains(sections[0].TextContent, "Running", StringComparison.Ordinal);
        StringAssert.Contains(sections[1].TextContent, "Replay executions", StringComparison.Ordinal);
        StringAssert.Contains(sections[1].TextContent, "Completed", StringComparison.Ordinal);
        Assert.HasCount(1, cut.FindAll(".execution-row--active"));
        Assert.IsNotNull(cut.Find($"a[href='/operations/pipeline/executions/{RunningId}']"));
        Assert.IsTrue(cut.Markup.Contains("refreshing every 5 s while work is active", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("lease", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ExecutionsPage_WithoutActiveWork_DoesNotAnnouncePolling()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new GraphUiService { Executions = View(live: [], replay: [Execution(CompletedId, ProcessingGraphExecutionClass.Replay, ProcessingGraphExecutionStatus.Failed)]) });
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));

        var cut = context.Render<ProcessingExecutionsPage>();

        cut.WaitForElement(".execution-table");
        Assert.IsFalse(cut.Markup.Contains("refreshing every 5 s", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("No live executions are recorded", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExecutionsPage_FailedRefresh_KeepsLastValidData()
    {
        using var context = new BunitContext();
        var service = new GraphUiService { Executions = View(live: [Execution(RunningId, ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Pending)], replay: []) };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        var cut = context.Render<ProcessingExecutionsPage>();
        cut.WaitForElement(".execution-table");
        service.Failure = OperatorUiResult<CameraAgentProcessingExecutionsView>.Failure(OperatorUiResultKind.Unavailable, "The journal is locked.");

        cut.FindAll("button").Single(static button => button.TextContent.Trim() == "Refresh").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Showing last valid data", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("The journal is locked.", StringComparison.Ordinal));
            Assert.IsNotNull(cut.Find(".execution-table"));
        });
    }

    [TestMethod]
    public async Task ExecutionsPage_Disposal_StopsPollingAsync()
    {
        using var context = new BunitContext();
        var service = new GraphUiService { Executions = View(live: [Execution(RunningId, ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Running)], replay: []) };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        var cut = context.Render<ProcessingExecutionsPage>();
        cut.WaitForElement(".execution-table");
        var reads = service.ExecutionReads;

        await cut.Instance.DisposeAsync().ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);

        Assert.AreEqual(reads, service.ExecutionReads);
    }

    [TestMethod]
    public void DetailPage_ShowsIdentityNodesInputsAttemptsAndOutputsWithoutLeaseOwners()
    {
        using var context = new BunitContext();
        ConfigureTransientRun(context);
        var detail = new CameraAgentProcessingExecutionDetailView(
            Now,
            CameraAgentProcessingExecutionProjection.Summarize(Execution(CompletedId, ProcessingGraphExecutionClass.Replay, ProcessingGraphExecutionStatus.Completed)),
            new string('S', 64),
            new string('L', 64),
            [
                new CameraAgentProcessingNodeView(
                    "preview", true, new string('P', 64), "Completed", null, 1, Now.AddSeconds(-10), Now.AddSeconds(-4),
                    [new ProcessingGraphExecutionInputState(0, 0, ProcessingGraphExecutionInputKind.RawCapture, CaptureId, Guid.NewGuid(), new string('D', 64), new string('E', 64), null)],
                    [new CameraAgentProcessingNodeAttemptView(1, Now.AddSeconds(-10), Now.AddSeconds(-4), "Completed", "Succeeded", null, TimeSpan.FromSeconds(6), "InProcess")],
                    [new ProcessingGraphExecutionOutputState(0, new string('O', 64), Guid.NewGuid(), FrameArtifactRole.Preview, "display", "Available", null, true)]),
                new CameraAgentProcessingNodeView("telemetry", false, new string('T', 64), "Failed", "sensitive-tool crashed", 2, Now.AddSeconds(-3), Now, [], [], [])
            ]);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new GraphUiService { Detail = detail });

        var cut = context.Render<ProcessingExecutionDetailPage>(parameters => parameters.Add(static page => page.ExecutionId, CompletedId));

        cut.WaitForElement(".node-list");
        StringAssert.Contains(cut.Find("h1").TextContent, "Replay pipeline run", StringComparison.Ordinal);
        Assert.HasCount(2, cut.FindAll(".run-diagram__node"));
        StringAssert.Contains(cut.Find(".transient-band").TextContent, "Disabled", StringComparison.Ordinal);
        Assert.HasCount(2, cut.FindAll(".node"));
        Assert.IsTrue(cut.Markup.Contains(new string('O', 64), StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Succeeded", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("sensitive-tool crashed", StringComparison.Ordinal));
        Assert.IsNotNull(cut.Find($"a[href='/gallery/{CaptureId}']"));
        Assert.IsNotNull(cut.Find($"a[href^='/api/v1/operations/processing-graphs/executions/{CompletedId}/outputs/']"));
        Assert.IsFalse(cut.Markup.Contains("lease", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(cut.FindAll("button").Any(static button => button.TextContent.Contains("Publish", StringComparison.Ordinal) || button.TextContent.Contains("Upload", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RunDiagram_DrawsFrozenOptionalEdgeAndSelectsNodes()
    {
        using var context = new BunitContext();
        string? selected = null;
        var nodes = new CameraAgentProcessingNodeView[]
        {
            new("source", true, "plan", "Completed", null, 1, Now, Now, [], [], []),
            new("preview", false, "plan", "Skipped", null, 0, null, null, [], [], [])
            {
                Dependencies = [new HVO.SkyMonitor.Processing.ProcessingGraphDependencyDefinition("source", Required: false),
                    new HVO.SkyMonitor.Processing.ProcessingGraphDependencyDefinition("$raw")]
            }
        };
        var cut = context.Render<ExecutionRunDiagram>(parameters => parameters
            .Add(component => component.Nodes, nodes)
            .Add(component => component.NodeSelected, id => selected = id));

        Assert.HasCount(2, cut.FindAll(".run-diagram__node"));
        Assert.HasCount(1, cut.FindAll(".run-diagram__edge--optional"));
        Assert.HasCount(0, cut.FindAll(".run-diagram__edge:not(.run-diagram__edge--optional)"));
        Assert.IsTrue(cut.FindAll(".run-diagram__node")[1].ClassList.Contains("run-diagram__node--skipped"));
        cut.FindAll(".run-diagram__node")[1].KeyDown("Enter");
        Assert.AreEqual("preview", selected);
        cut.Find("button[aria-label='Zoom in']").Click();
        StringAssert.Contains(cut.Find(".run-diagram").GetAttribute("style")!, "width:", StringComparison.Ordinal);
    }

    [TestMethod]
    public void DetailPage_NotFound_ShowsInfoWithoutRetry()
    {
        using var context = new BunitContext();
        ConfigureTransientRun(context);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new GraphUiService { DetailFailure = OperatorUiResult<CameraAgentProcessingExecutionDetailView>.Failure(OperatorUiResultKind.NotFound, "The execution was not found.") });

        var cut = context.Render<ProcessingExecutionDetailPage>(parameters => parameters.Add(static page => page.ExecutionId, Guid.NewGuid()));

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Execution not found", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll("[role='alert']"));
            Assert.IsFalse(cut.FindAll("button").Any(static button => button.TextContent.Contains("Try again", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void Pages_WhenUnauthorized_NavigateToAccessDenied()
    {
        using var context = new BunitContext();
        ConfigureTransientRun(context);
        var unauthorized = new GraphUiService
        {
            Failure = OperatorUiResult<CameraAgentProcessingExecutionsView>.Failure(OperatorUiResultKind.Unauthorized, "denied"),
            DetailFailure = OperatorUiResult<CameraAgentProcessingExecutionDetailView>.Failure(OperatorUiResultKind.Unauthorized, "denied")
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(unauthorized);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        var list = context.Render<ProcessingExecutionsPage>();
        list.WaitForAssertion(() => Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
        navigation.NavigateTo("/operations/pipeline/executions");
        _ = context.Render<ProcessingExecutionDetailPage>(parameters => parameters.Add(static page => page.ExecutionId, Guid.NewGuid()));

        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    internal static ProcessingGraphExecutionState Execution(Guid id, ProcessingGraphExecutionClass executionClass, ProcessingGraphExecutionStatus status) => new(
        id, executionClass, status, CaptureId, Guid.NewGuid(), "graph-rev-1", new string('G', 64), new string('S', 64), new string('L', 64),
        executionClass == ProcessingGraphExecutionClass.Live ? "live-capture" : "operator", null, 0,
        Now.AddMinutes(-5), Now.AddMinutes(-5), Now.AddMinutes(55), Now.AddHours(24),
        status == ProcessingGraphExecutionStatus.Pending ? null : Now.AddMinutes(-4),
        status is ProcessingGraphExecutionStatus.Completed or ProcessingGraphExecutionStatus.Failed ? Now.AddMinutes(-1) : null,
        status == ProcessingGraphExecutionStatus.Failed ? "node failed" : null, false, 1);

    private static CameraAgentProcessingExecutionsView View(ProcessingGraphExecutionState[] live, ProcessingGraphExecutionState[] replay) => new(
        Now, CameraAgentProcessingExecutionProjection.MaximumPerClass,
        live.Select(CameraAgentProcessingExecutionProjection.Summarize).ToArray(),
        replay.Select(CameraAgentProcessingExecutionProjection.Summarize).ToArray());

    internal sealed class GraphUiService : ICameraAgentProcessingGraphUiService
    {
        public CameraAgentProcessingExecutionsView? Executions { get; set; }
        public OperatorUiResult<CameraAgentProcessingExecutionsView>? Failure { get; set; }
        public CameraAgentProcessingExecutionDetailView? Detail { get; set; }
        public OperatorUiResult<CameraAgentProcessingExecutionDetailView>? DetailFailure { get; set; }
        public int ExecutionReads { get; private set; }

        public ValueTask<OperatorUiResult<CameraAgentProcessingExecutionsView>> GetExecutionsAsync(int maximumPerClass, CancellationToken cancellationToken)
        {
            ExecutionReads++;
            return ValueTask.FromResult(Failure ?? OperatorUiResult<CameraAgentProcessingExecutionsView>.Success(Executions!));
        }

        public ValueTask<OperatorUiResult<CameraAgentProcessingExecutionDetailView>> GetExecutionDetailAsync(Guid executionId, CancellationToken cancellationToken)
            => ValueTask.FromResult(DetailFailure ?? OperatorUiResult<CameraAgentProcessingExecutionDetailView>.Success(Detail!));

        public ValueTask<OperatorUiResult<CameraAgentLiveRunLink>> GetLiveExecutionIdAsync(Guid captureId, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CameraAgentLiveRunLink>.Failure(OperatorUiResultKind.NotFound, "No live run."));

        public IReadOnlyList<string> StepAliases { get; set; } = ["preview", "telemetry", "calibration"];
        public ProcessingGraphRegistryState? Registry { get; set; }
        public Func<CapturePipelineConfig, OperatorUiResult<CaptureProcessingPlanPreview>>? Preview { get; set; }
        public Func<(string Name, string Revision, CapturePipelineConfig Pipeline, string Key, string? Reason), OperatorUiResult<ProcessingGraphRevisionState>>? Create { get; set; }
        public List<(string Name, string Revision, CapturePipelineConfig Pipeline, string Key, string? Reason)> Created { get; } = [];
        public OperatorUiResult<ProcessingGraphRegistryState>? RegistryFailure { get; set; }
        public CameraAgentProcessingGraphRevisionDetail? RevisionDetail { get; set; }
        public OperatorUiResult<CameraAgentProcessingGraphRevisionDetail>? RevisionDetailFailure { get; set; }
        public Func<string, OperatorUiResult<ProcessingGraphRegistryState>>? RegistryMutation { get; set; }
        public Func<string, OperatorUiResult<ProcessingGraphRevisionState>>? RevisionMutation { get; set; }
        public List<(string Action, string RevisionId, long? ExpectedVersion, string Key, string? Reason)> Commands { get; } = [];

        public ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> GetRegistryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(RegistryFailure ?? OperatorUiResult<ProcessingGraphRegistryState>.Success(Registry!));

        public ValueTask<OperatorUiResult<CameraAgentProcessingGraphRevisionDetail>> GetRevisionDetailAsync(string revisionId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RevisionDetailFailure ?? OperatorUiResult<CameraAgentProcessingGraphRevisionDetail>.Success(RevisionDetail!));

        public ValueTask<OperatorUiResult<CaptureProcessingPlanPreview>> PreviewAsync(CapturePipelineConfig pipeline, CancellationToken cancellationToken)
            => ValueTask.FromResult(Preview?.Invoke(pipeline) ?? throw new NotSupportedException());

        public ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> ActivateAsync(string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => RegistryCommand("activate", revisionId, expectedVersion, idempotencyKey, reason);

        public ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> RollbackAsync(string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => RegistryCommand("rollback", revisionId, expectedVersion, idempotencyKey, reason);

        public ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> RetireAsync(string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => RegistryCommand("retire", revisionId, expectedVersion, idempotencyKey, reason);

        public ValueTask<OperatorUiResult<ProcessingGraphRevisionState>> ValidateAsync(string revisionId, string idempotencyKey, string? reason, CancellationToken cancellationToken)
        {
            Commands.Add(("validate", revisionId, null, idempotencyKey, reason));
            return ValueTask.FromResult(RevisionMutation?.Invoke(revisionId) ?? OperatorUiResult<ProcessingGraphRevisionState>.Success(Registry!.Revisions.Single(revision => revision.RevisionId == revisionId)));
        }

        public ValueTask<OperatorUiResult<ProcessingGraphRevisionState>> CreateRevisionAsync(string name, string revision, CapturePipelineConfig pipeline, string idempotencyKey, string? reason, CancellationToken cancellationToken)
        {
            Created.Add((name, revision, pipeline, idempotencyKey, reason));
            return ValueTask.FromResult(Create?.Invoke((name, revision, pipeline, idempotencyKey, reason)) ?? throw new NotSupportedException());
        }

        private ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> RegistryCommand(string action, string revisionId, long expectedVersion, string key, string? reason)
        {
            Commands.Add((action, revisionId, expectedVersion, key, reason));
            return ValueTask.FromResult(RegistryMutation?.Invoke(revisionId) ?? OperatorUiResult<ProcessingGraphRegistryState>.Success(Registry!));
        }
    }

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
