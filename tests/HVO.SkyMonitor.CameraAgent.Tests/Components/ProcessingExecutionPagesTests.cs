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
        context.Services.AddSingleton<ICameraAgentTransientUiService>(new RecordedTransientUiService());
    }

    private sealed class RecordedTransientUiService : ICameraAgentTransientUiService
    {
        public IReadOnlyList<TransientStageEvent> Events { get; init; } = [];

        public ValueTask<OperatorUiResult<TransientCaptureStageView>> GetCaptureStagesAsync(
            Guid captureId, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<TransientCaptureStageView>.Success(new(captureId, Events)));

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorPage>> GetPageAsync(
            CameraAgentTransientOperatorQuery query, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorDetail>> GetCandidateAsync(
            Guid candidateId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
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
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());

        var cut = context.Render<ProcessingExecutionDetailPage>(parameters => parameters.Add(static page => page.ExecutionId, CompletedId));

        cut.WaitForElement(".node-list");
        StringAssert.Contains(cut.Find("h1").TextContent, "Capture #42", StringComparison.Ordinal);
        Assert.HasCount(2, cut.FindAll(".run-diagram__node"));
        StringAssert.Contains(cut.Find(".run-diagram__transient").TextContent, "Local detector disabled", StringComparison.Ordinal);
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
    public void DetailPage_FailedRunDoesNotUseSuccessfulTitleGlyph()
    {
        using var context = new BunitContext();
        ConfigureTransientRun(context);
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());
        var failure = CameraAgentProcessingExecutionProjection.Summarize(
            Execution(CompletedId, ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Failed));
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new GraphUiService
        {
            Detail = new CameraAgentProcessingExecutionDetailView(Now, failure, "shared", "local", [])
        });

        var cut = context.Render<ProcessingExecutionDetailPage>(parameters => parameters.Add(page => page.ExecutionId, CompletedId));

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Find(".run-title__glyph").ClassList.Contains("run-title__glyph--failed"));
            Assert.IsFalse(cut.Find(".run-title__glyph").ClassList.Contains("run-title__glyph--completed"));
        });
    }

    [TestMethod]
    public void DetailPage_RendersJournalBeforeOptionalRecentRunsFinish()
    {
        using var context = new BunitContext();
        ConfigureTransientRun(context);
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());
        var waiting = new TaskCompletionSource<OperatorUiResult<CameraAgentProcessingExecutionsView>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = CameraAgentProcessingExecutionProjection.Summarize(
            Execution(CompletedId, ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Completed));
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new GraphUiService
        {
            Detail = new CameraAgentProcessingExecutionDetailView(Now, completed, "shared", "local", []),
            PendingExecutions = waiting.Task
        });

        var cut = context.Render<ProcessingExecutionDetailPage>(parameters => parameters.Add(page => page.ExecutionId, CompletedId));

        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find(".graph-card"));
            StringAssert.Contains(cut.Find(".run-workspace__rail").TextContent, "Recent captures are unavailable", StringComparison.Ordinal);
        });
        waiting.SetResult(OperatorUiResult<CameraAgentProcessingExecutionsView>.Success(View([], [])));
    }

    [TestMethod]
    public void DetailPage_TransientBandShowsOnlyAuthorizedRecordedMilestones()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton(Options.Create(new CameraAgentHostOptions
        {
            TransientDetection = new TransientDetectionOptions { Mode = TransientOperatingMode.Hybrid }
        }));
        var captured = Now.AddMinutes(-1);
        context.Services.AddSingleton<ICameraAgentTransientUiService>(new RecordedTransientUiService
        {
            Events =
            [
                new TransientStageEvent("frame-staged", null, "pending", "transient_capture_work", captured),
                new TransientStageEvent("causal-scan", null, "succeeded", "transient_worker_frames", captured.AddSeconds(2)),
                new TransientStageEvent("relay-pending", Guid.NewGuid(), "HandoffPending", "transient_candidates", captured.AddSeconds(3))
            ]
        });
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());
        var completed = CameraAgentProcessingExecutionProjection.Summarize(
            Execution(CompletedId, ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Completed));
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new GraphUiService
        {
            Detail = new CameraAgentProcessingExecutionDetailView(Now, completed, "shared", "local", [])
        });

        var cut = context.Render<ProcessingExecutionDetailPage>(parameters => parameters.Add(page => page.ExecutionId, CompletedId));

        cut.WaitForAssertion(() =>
        {
            var stages = cut.FindAll(".run-diagram__transient-node");
            Assert.HasCount(3, stages);
            StringAssert.Contains(stages[0].TextContent, "Durable frame window", StringComparison.Ordinal);
            StringAssert.Contains(stages[1].TextContent, "Causal candidate scan", StringComparison.Ordinal);
            StringAssert.Contains(stages[2].TextContent, "Relay queued", StringComparison.Ordinal);
            Assert.HasCount(2, cut.FindAll(".run-diagram__transient-label"));
            Assert.IsFalse(cut.Find(".run-diagram__transient").TextContent.Contains("Acknowledged", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void RunDiagram_ConnectsOnlyEvidencedPredecessorsWithinTheirCandidate()
    {
        using var context = new BunitContext();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var events = new TransientStageEvent[]
        {
            new("frame-staged", null, "pending", "transient_capture_work", Now),
            new("causal-scan", null, "not-succeeded", "transient_worker_frames", Now.AddSeconds(1)),
            new("candidate-allocated", first, "pending", "transient_worker_candidates", Now.AddSeconds(2)),
            new("relay-pending", first, "HandoffPending", "transient_candidates", Now.AddSeconds(3)),
            new("candidate-allocated", second, "pending", "transient_worker_candidates", Now.AddSeconds(4))
        };

        var cut = context.Render<ExecutionRunDiagram>(parameters => parameters
            .Add(component => component.Nodes, [])
            .Add(component => component.TransientEnabled, true)
            .Add(component => component.TransientEvents, events));

        Assert.HasCount(5, cut.FindAll(".run-diagram__transient-node"));
        Assert.HasCount(3, cut.FindAll(".run-diagram__transient-label"));
        Assert.HasCount(1, cut.FindAll(".run-diagram__transient-edge"));
        Assert.IsTrue(cut.FindAll(".run-diagram__transient-node")[1].ClassList.Contains("run-diagram__transient-node--attention"));
        StringAssert.Contains(cut.Markup, "No processing nodes were recorded", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RunDiagram_OrdersCandidateRowsByPersistedSlotAndDescribesEvents()
    {
        using var context = new BunitContext();
        var lowerGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var higherGuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var events = new TransientStageEvent[]
        {
            new("frame-staged", null, "pending", "transient_capture_work", Now),
            new("causal-scan", null, "succeeded", "transient_worker_frames", Now.AddSeconds(1)),
            new("candidate-allocated", lowerGuid, "pending", "transient_worker_candidates", Now.AddSeconds(2)) { SlotOrdinal = 1 },
            new("candidate-allocated", higherGuid, "pending", "transient_worker_candidates", Now.AddSeconds(3)) { SlotOrdinal = 0 }
        };

        var cut = context.Render<ExecutionRunDiagram>(parameters => parameters
            .Add(component => component.Nodes, [])
            .Add(component => component.TransientEnabled, true)
            .Add(component => component.TransientEvents, events));

        var labels = cut.FindAll(".run-diagram__transient-label").Select(static item => item.TextContent).ToArray();
        CollectionAssert.AreEqual(new[] { "Capture", $"Candidate {higherGuid:D}", $"Candidate {lowerGuid:D}" }, labels);
        Assert.HasCount(3, cut.FindAll(".run-diagram__transient-edge"));
        var description = cut.Find(".run-diagram desc").TextContent;
        StringAssert.Contains(description, $"Candidate {higherGuid:D}: Candidate allocated pending", StringComparison.Ordinal);
        StringAssert.Contains(description, "Causal candidate scan succeeded", StringComparison.Ordinal);
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
        Assert.HasCount(1, cut.FindAll(".run-diagram__edge:not(.run-diagram__edge--optional)"));
        Assert.IsTrue(cut.FindAll(".run-diagram__node")[1].ClassList.Contains("run-diagram__node--skipped"));
        Assert.HasCount(1, cut.FindAll(".run-diagram__cloud-placeholder"));
        Assert.HasCount(1, cut.FindAll(".run-diagram__transient-empty"));
        Assert.IsEmpty(cut.FindAll(".run-diagram__transient-edge"));
        Assert.AreEqual("Fit (min 85%)", cut.Find(".run-diagram__zoom").TextContent);
        StringAssert.Contains(cut.Find(".run-diagram").GetAttribute("style")!, "width: max(100%,", StringComparison.Ordinal);
        cut.FindAll(".run-diagram__node")[1].KeyDown("Enter");
        Assert.AreEqual("preview", selected);
        cut.Render(parameters => parameters.Add(component => component.SelectedNodeId, "preview"));
        Assert.HasCount(2, cut.FindAll(".run-diagram__edge--highlighted"));
        cut.Find("button[aria-label='Zoom in']").Click();
        Assert.AreEqual("110%", cut.Find(".run-diagram__zoom").TextContent);
        Assert.IsFalse(cut.Find(".run-diagram").GetAttribute("style")!.Contains("max(100%", StringComparison.Ordinal));
        cut.Find("button[aria-label='Fit graph']").Click();
        Assert.AreEqual("Fit (min 85%)", cut.Find(".run-diagram__zoom").TextContent);
        StringAssert.Contains(cut.Find(".run-diagram").GetAttribute("style")!, "width: max(100%,", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RunDiagram_KeepsCloudPlaceholderWhenStageHasNoProduct()
    {
        using var context = new BunitContext();
        var nodes = new CameraAgentProcessingNodeView[]
        {
            new("cloud-assessment", false, "plan", "Completed", null, 1, Now, Now, [], [], [])
        };
        var cut = context.Render<ExecutionRunDiagram>(parameters => parameters.Add(component => component.Nodes, nodes));

        Assert.HasCount(1, cut.FindAll(".run-diagram__cloud-placeholder"));
        Assert.HasCount(1, cut.FindAll(".run-diagram__node"));
    }

    [TestMethod]
    public void RunDiagram_OnlyRecordedAssessmentProductRemovesCloudPlaceholder()
    {
        using var context = new BunitContext();
        var weather = new ProcessingGraphExecutionOutputState(0, new string('A', 64), Guid.NewGuid(),
            FrameArtifactRole.Preview, "weather-cloud-overlay-v1", "Available", null, false);
        var assessment = weather with { Role = FrameArtifactRole.Metadata, Variant = "cloud-assessment-v1" };
        var nodes = new CameraAgentProcessingNodeView[]
        {
            new("cloud-assessment", false, "plan", "Completed", null, 1, Now, Now, [], [], [weather])
        };
        var cut = context.Render<ExecutionRunDiagram>(parameters => parameters.Add(component => component.Nodes, nodes));

        Assert.HasCount(1, cut.FindAll(".run-diagram__cloud-placeholder"));
        nodes[0] = nodes[0] with { Outputs = [assessment] };
        cut.Render(parameters => parameters.Add(component => component.Nodes, nodes));
        Assert.IsEmpty(cut.FindAll(".run-diagram__cloud-placeholder"));
    }

    [TestMethod]
    public void DetailPage_NotFound_ShowsInfoWithoutRetry()
    {
        using var context = new BunitContext();
        ConfigureTransientRun(context);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new GraphUiService { DetailFailure = OperatorUiResult<CameraAgentProcessingExecutionDetailView>.Failure(OperatorUiResultKind.NotFound, "The execution was not found.") });
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());

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
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());
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
        public Task<OperatorUiResult<CameraAgentProcessingExecutionsView>>? PendingExecutions { get; set; }

        public async ValueTask<OperatorUiResult<CameraAgentProcessingExecutionsView>> GetExecutionsAsync(int maximumPerClass, CancellationToken cancellationToken)
        {
            ExecutionReads++;
            return PendingExecutions is { } pending
                ? await pending.ConfigureAwait(false)
                : Failure ?? OperatorUiResult<CameraAgentProcessingExecutionsView>.Success(Executions!);
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
