using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class PipelineSummaryPageTests
{
    private const string ActiveId = "profile-00000002-ABCDEF123456";
    private const string ToggleReason = "operator pipeline step toggle";
    private const string RawInput = "$raw";
    private static readonly DateTimeOffset Now = ProcessingExecutionPagesTests.Now;
    private static readonly string[] EdgeStageKeys = ["detector-input", "frame-window", "causal-scan", "persist-candidate", "centered-window", "assess"];

    [TestMethod]
    public void Render_DrawsConfiguredGraphAndInspectsFirstStepWithoutOptionValues()
    {
        var runId = Guid.NewGuid();
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())),
            executions: [ProcessingExecutionPagesTests.Execution(runId, ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Completed)]);

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.AreEqual("Pipeline", cut.Find("h1").TextContent.Trim());
        Assert.Contains("Processing / configured dependency graph", cut.Markup);
        Assert.Contains("Revision 2 is valid", cut.Find(".ops-profile-banner").TextContent);
        Assert.Contains("3 configured steps / 1 required / 3 in the effective graph", cut.Find(".ops-profile-banner").TextContent);
        Assert.Contains("Explicit dependencies", cut.Find(".ops-profile-banner").TextContent);
        Assert.Contains("180.00s", cut.Find(".ops-profile-banner").TextContent);

        var nodes = cut.FindAll(".ops-pipeline-node.step");
        Assert.HasCount(3, nodes);
        Assert.AreEqual("true", nodes[0].GetAttribute("aria-pressed"));
        Assert.AreEqual("grid-column:1;grid-row:1", cut.Find("#pipeline-source-raw").GetAttribute("style"));
        Assert.AreEqual("grid-column:2;grid-row:1", nodes[0].GetAttribute("style"));
        Assert.AreEqual("grid-column:3;grid-row:1", nodes[2].GetAttribute("style"));
        Assert.HasCount(3, cut.FindAll("path.pipeline-edge"), "The raw frame feeds Preview and Telemetry; Preview feeds Thumbnail.");
        Assert.IsEmpty(cut.FindAll("path.pipeline-edge.transient"), "Detection is off, so no lane edge is drawn.");

        Assert.AreEqual("Preview", cut.Find("#pipeline-step-title").TextContent.Trim());
        var facts = cut.Find(".ops-node-facts").TextContent;
        Assert.Contains("Preview / preview", facts);
        Assert.Contains("Raw frame", facts);
        Assert.Contains("Preview / display", facts);
        Assert.Contains("2 set, values not shown", facts);
        Assert.Contains("encoded-preview", facts);
        Assert.DoesNotContain("SERIAL-0042", cut.Markup, "Option values can identify the device and are never rendered.");
        Assert.DoesNotContain("/opt/sdk", cut.Markup);

        Assert.AreEqual("5 / 5 passed", cut.Find(".ops-panel-heading > span").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll(".ops-validation-item.failed"));
        Assert.AreEqual($"/operations/pipeline/executions/{runId:D}", cut.Find("a#pipeline-latest-run").GetAttribute("href"));
        Assert.AreEqual("/operations/pipeline/graphs/new", cut.Find("#pipeline-create-draft").GetAttribute("href"));
        Assert.AreEqual("Turn off step", cut.Find("#pipeline-step-toggle").TextContent.Trim());
        cut.Find("#pipeline-node-1").Click();
        Assert.IsFalse(cut.Find("#pipeline-step-toggle").HasAttribute("disabled"));
    }

    [TestMethod]
    public void SelectingAStep_UpdatesInspectorEdgeAndStepQuery()
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())));
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<PipelineSummaryPage>();
        cut.WaitForElement(".ops-pipeline-graph");

        cut.Find("#pipeline-node-2").Click();

        Assert.AreEqual("Thumbnail", cut.Find("#pipeline-step-title").TextContent.Trim());
        Assert.AreEqual("true", cut.Find("#pipeline-node-2").GetAttribute("aria-pressed"));
        Assert.AreEqual("false", cut.Find("#pipeline-node-0").GetAttribute("aria-pressed"));
        Assert.HasCount(1, cut.FindAll("path.pipeline-edge.selected"), "Only Preview to Thumbnail touches the selected step.");
        Assert.Contains("Optional step", cut.Find(".ops-pipeline-inspector .eyebrow").TextContent);
        Assert.Contains("Preview", cut.Find(".ops-node-facts").TextContent);
        Assert.Contains("Module defaults", cut.Find(".ops-node-facts").TextContent);
        Assert.EndsWith("step=Thumbnail", navigation.Uri);
    }

    [TestMethod]
    public void StepQuery_OpensThatStepInTheInspector()
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline?step=telemetry");

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.AreEqual("Telemetry", cut.Find("#pipeline-step-title").TextContent.Trim());
        Assert.AreEqual("true", cut.Find("#pipeline-node-1").GetAttribute("aria-pressed"));
        Assert.Contains("No artifact (infrastructure step)", cut.Find(".ops-node-facts").TextContent);
    }

    [TestMethod]
    public void Validation_ReportsDanglingDependencyAndStepOnAfterAStepOff()
    {
        var preview = Node("Preview", enabled: false, required: true, order: 10, [RawInput]);
        var thumbnail = Node("Thumbnail", enabled: true, required: false, order: 20, ["Preview"]);
        var telemetry = Node("Telemetry", enabled: true, required: false, order: 30, ["Missing"]);
        var plan = Plan([preview, thumbnail, telemetry], [thumbnail, telemetry]);
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(plan)));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.Contains("Revision 2 has validation problems", cut.Find(".ops-profile-banner").TextContent);
        Assert.AreEqual("3 / 5 passed", cut.Find(".ops-panel-heading > span").TextContent.Trim());
        var failed = cut.FindAll(".ops-validation-item.failed").Select(static item => item.TextContent).ToArray();
        Assert.HasCount(2, failed);
        Assert.Contains("Telemetry depends on Missing, which is not configured.", failed[0]);
        Assert.Contains("Thumbnail is on but depends on a step that is off.", failed[1]);
        Assert.Contains("Failed:", failed[0]);
        Assert.Contains("off", cut.Find("#pipeline-node-0").GetAttribute("class")!);
        Assert.Contains("off", cut.Find("path.pipeline-edge").GetAttribute("class")!);
        Assert.Contains("This step is off.", cut.Find(".ops-pipeline-inspector").TextContent);
        Assert.Contains("None while the step is off", cut.Find(".ops-node-facts").TextContent);
        Assert.AreEqual("Turn on step", cut.Find("#pipeline-step-toggle").TextContent.Trim());
    }

    [TestMethod]
    public void LegacyProfileWithoutSteps_ShowsDefaultStepsAsInferredAndCannotToggle()
    {
        var preview = Node("Preview", enabled: true, required: true, order: 10, [RawInput]);
        var thumbnail = Node("Thumbnail", enabled: true, required: false, order: 20, ["Preview"]);
        var plan = Plan([], [preview, thumbnail], legacy: true);
        using var context = CreateContext(new PipelineScheduleService(SchedulePageTests.State(), Pipeline(plan, canToggle: false)));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.HasCount(2, cut.FindAll(".ops-pipeline-node.step"));
        Assert.HasCount(2, cut.FindAll("path.pipeline-edge"));
        Assert.Contains("Inferred (legacy)", cut.Find(".ops-profile-banner").TextContent);
        Assert.AreEqual("2 / 2 passed", cut.Find(".ops-panel-heading > span").TextContent.Trim());
        var neutral = cut.FindAll(".ops-validation-item.neutral").Select(static item => item.TextContent).ToArray();
        Assert.HasCount(2, neutral);
        Assert.Contains("No steps are configured, so CameraAgent runs its default steps.", neutral[0]);
        Assert.Contains("Dependencies are inferred when CameraAgent starts", neutral[1]);
        AssertToggleBlocked(cut, "This revision uses the legacy profile schema, which does not support turning steps on or off.");
    }

    [TestMethod]
    public void LegacyStepWithoutDeclaredDependencies_ShowsTheInferredInputs()
    {
        var desired = Node("Thumbnail", enabled: true, required: false, order: 20, null);
        var effective = desired with { Dependencies = [RawInput] };
        var plan = Plan([desired], [effective], legacy: true);
        using var context = CreateContext(new PipelineScheduleService(SchedulePageTests.State(), Pipeline(plan, canToggle: false)));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.Contains("Raw frame (inferred)", cut.Find(".ops-node-facts").TextContent);
        Assert.Contains("Dependencies are inferred when CameraAgent starts", cut.Find(".ops-validation-item.neutral").TextContent);
    }

    [TestMethod]
    public void NamedGraphLive_SaysTheConfiguredGraphIsNotRunning()
    {
        var graphs = new ProcessingExecutionPagesTests.GraphUiService
        {
            Registry = new ProcessingGraphRegistryState(ProcessingGraphRegistryMode.Named, "graph-rev-7", "basic-1", 3,
            [
                new ProcessingGraphRevisionState("graph-rev-7", "Night stack", "r7", ProcessingGraphRevisionLifecycle.Active,
                    new string('A', 64), new string('B', 64), new string('C', 64), Now, Now, Now, null),
            ]),
            Executions = Executions(),
        };
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())), graphs: graphs);

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".pipeline-named");
        Assert.Contains("Live processing runs a named graph.", cut.Find(".pipeline-named").TextContent);
        Assert.AreEqual("/operations/pipeline/graphs/graph-rev-7", cut.Find(".pipeline-named a").GetAttribute("href"));
        Assert.Contains("Night stack r7", cut.Find(".pipeline-named").TextContent);
    }

    [TestMethod]
    [DataRow("schedule-unavailable", "Schedule state is unavailable, so steps cannot be changed. Refresh to try again.")]
    [DataRow("revision-changed", "The active revision changed while this page was open. Refresh before changing steps.")]
    [DataRow("schedule-draft", "Draft revision 3 is waiting in Schedule. Apply it there first; saving a step change would replace it.")]
    [DataRow("rig-unavailable", "Named rig state is unavailable, so steps cannot be changed safely. Refresh to try again.")]
    [DataRow("rig-pending", "A named rig is waiting for CameraAgent to restart. Restart or cancel it first.")]
    [DataRow("legacy-schema", "This revision uses the legacy profile schema, which does not support turning steps on or off.")]
    [DataRow("cannot-toggle", "This revision uses the legacy profile schema, which does not support turning steps on or off.")]
    public void Toggle_IsBlockedWithTheReasonTheOperatorCanActOn(string scenario, string reason)
    {
        var state = CurrentState();
        var rig = OperatorUiResult<NamedRigUiCatalog>.Success(new(new NamedRigSelection(null, null, 1), [], null, null));
        var canToggle = true;
        var service = new PipelineScheduleService(state, null);
        switch (scenario)
        {
            case "schedule-unavailable":
                service.StateResult = OperatorUiResult<CaptureScheduleOperatorState>.Failure(OperatorUiResultKind.Unavailable, "Store offline.");
                break;
            case "revision-changed":
                service.State = state with { ActiveRevision = state.ActiveRevision with { RevisionId = "profile-00000003" } };
                break;
            case "schedule-draft":
                service.State = state with { PendingRevision = state.ActiveRevision with { RevisionId = "rev-3", RevisionNumber = 3 } };
                break;
            case "rig-unavailable":
                rig = OperatorUiResult<NamedRigUiCatalog>.Failure(OperatorUiResultKind.Unavailable, "Rig store offline.");
                break;
            case "rig-pending":
                rig = OperatorUiResult<NamedRigUiCatalog>.Success(new(new NamedRigSelection("rig-v1", "rig-v2", 2), [], null, null));
                break;
            case "legacy-schema":
                service.State = SchedulePageTests.State();
                break;
            case "cannot-toggle":
                canToggle = false;
                break;
        }
        service.Pipeline = Pipeline(ExplicitPlan(), canToggle);
        using var context = CreateContext(service, rig: rig);

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        AssertToggleBlocked(cut, reason);
        Assert.IsEmpty(service.Toggles);
    }

    [TestMethod]
    [DataRow("dependent-on", "#pipeline-node-0", "Thumbnail depends on this step. Turn it off first.")]
    [DataRow("dependents-on", "#pipeline-node-0", "Telemetry and Thumbnail depend on this step. Turn them off first.")]
    [DataRow("dependency-off", "#pipeline-node-2", "This step depends on Preview, which is off. Turn it on first.")]
    public void Toggle_IsBlockedByTheStepsThatWouldBreakTheDependencyRule(string scenario, string nodeSelector, string reason)
    {
        var nodes = ExplicitPlan().DesiredNodes.Select(node => (scenario, node.Id) switch
        {
            ("dependents-on", "Telemetry") => node with { Dependencies = ["Preview"] },
            ("dependency-off", "Preview" or "Thumbnail") => node with { Enabled = false },
            _ => node,
        }).ToArray();
        var service = ToggleService();
        service.Pipeline = Pipeline(Plan(nodes, nodes.Where(static node => node.Enabled).ToArray()));
        using var context = CreateContext(service);
        var cut = context.Render<PipelineSummaryPage>();
        cut.WaitForElement(".ops-pipeline-graph");

        cut.Find(nodeSelector).Click();

        AssertToggleBlocked(cut, reason);
        Assert.IsEmpty(service.Toggles);
    }

    [TestMethod]
    public void ViewingTheDraft_ShowsItsGraphAndBlocksToggling()
    {
        var active = ExplicitPlan();
        var draftNodes = active.DesiredNodes.Select(static node => node.Id == "Telemetry" ? node with { Enabled = false } : node).ToArray();
        var draft = Plan(draftNodes, draftNodes.Where(static node => node.Enabled).ToArray());
        var pipeline = new CameraAgentPipelineOperatorState(
            new CameraAgentPipelineRevisionPlan(ActiveId, 2, new string('A', 64), true, active),
            new CameraAgentPipelineRevisionPlan("rev-3", 3, new string('B', 64), true, draft));
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), pipeline));
        var cut = context.Render<PipelineSummaryPage>();
        cut.WaitForElement("#pipeline-show-draft");
        Assert.Contains("Revision 3, not applied", cut.Find(".ops-profile-banner").TextContent);
        Assert.AreEqual("true", cut.Find("#pipeline-show-active").GetAttribute("aria-pressed"));

        cut.Find("#pipeline-show-draft").Click();

        Assert.AreEqual("true", cut.Find("#pipeline-show-draft").GetAttribute("aria-pressed"));
        Assert.Contains("Revision 3 is valid", cut.Find(".ops-profile-banner").TextContent);
        Assert.Contains("off", cut.Find("#pipeline-node-1").GetAttribute("class")!);
        AssertToggleBlocked(cut, "You are viewing the draft. Steps are turned on or off in the active revision.");
    }

    [TestMethod]
    public void Toggle_PreviewsTheChangeThenSavesAndAppliesItInOneStep()
    {
        var service = ToggleService();
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");

        var dialog = cut.Find("dialog.pipeline-dialog");
        Assert.AreEqual("Turn off Telemetry", cut.Find("#pipeline-toggle-heading").TextContent.Trim());
        Assert.Contains("3 to 2 steps", dialog.TextContent);
        Assert.Contains("Leaves the graph", dialog.TextContent);
        Assert.DoesNotContain("This is a required step.", dialog.TextContent);
        var toggle = Assert.ContainsSingle(service.Toggles);
        Assert.AreEqual(("Telemetry", false, ActiveId), (toggle.NodeId, toggle.Enabled, toggle.Basis));
        Assert.Contains(LocalCaptureProfileDefinition.CurrentSchemaVersion, toggle.ProfileJson);
        Assert.AreEqual("Turn off and apply", cut.Find("#pipeline-toggle-apply").TextContent.Trim());

        cut.Find("#pipeline-toggle-apply").Click();

        cut.WaitForElement(".pipeline-message");
        var stage = Assert.ContainsSingle(service.Stages);
        Assert.AreEqual(("staged-profile", ActiveId, 4L, ToggleReason), (stage.ProfileJson, stage.Basis, stage.ExpectedVersion, stage.Reason));
        var activation = Assert.ContainsSingle(service.Activations);
        Assert.AreEqual(("rev-3", 5L, ToggleReason), (activation.RevisionId, activation.ExpectedVersion, activation.Reason));
        Assert.AreNotEqual(stage.Key, activation.Key);
        Assert.AreEqual("status", cut.Find(".pipeline-message").GetAttribute("role"));
        Assert.Contains("Revision 3 is active: Telemetry is off.", cut.Find(".pipeline-message").TextContent);
        Assert.IsEmpty(cut.FindAll("dialog.pipeline-dialog"));
        Assert.AreEqual(2, service.PipelineReads, "The page reloads so it shows the revision that is now active.");
    }

    [TestMethod]
    public void Toggle_StageOutcomeUnknown_RetryReusesTheSameStageCommand()
    {
        var service = ToggleService();
        service.StageResults.Enqueue(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(OperatorUiResultKind.Unavailable, "Timed out."));
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");

        cut.Find("#pipeline-toggle-apply").Click();
        Assert.Contains("Timed out.", cut.Find("dialog.pipeline-dialog [role=alert]").TextContent);
        Assert.IsEmpty(service.Activations);
        cut.Find("#pipeline-toggle-apply").Click();

        cut.WaitForElement(".pipeline-message");
        Assert.HasCount(2, service.Stages);
        Assert.AreEqual(service.Stages[0].Key, service.Stages[1].Key);
        Assert.AreEqual(service.Stages[0].ExpectedVersion, service.Stages[1].ExpectedVersion);
        Assert.ContainsSingle(service.Activations);
    }

    [TestMethod]
    public void Toggle_ApplyOutcomeUnknown_RetryRepeatsOnlyTheApplyWithTheSameKey()
    {
        var service = ToggleService();
        service.ActivationResults.Enqueue(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(OperatorUiResultKind.Unavailable, "Timed out."));
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");

        cut.Find("#pipeline-toggle-apply").Click();

        Assert.Contains("Saved as draft revision 3, but applying it did not complete (Timed out.). Try again to apply it.",
            cut.Find("dialog.pipeline-dialog [role=alert]").TextContent);
        Assert.AreEqual("Apply revision 3", cut.Find("#pipeline-toggle-apply").TextContent.Trim());

        cut.Find("#pipeline-toggle-apply").Click();

        cut.WaitForElement(".pipeline-message");
        Assert.ContainsSingle(service.Stages);
        Assert.HasCount(2, service.Activations);
        Assert.AreEqual(service.Activations[0], service.Activations[1]);
        Assert.Contains("Revision 3 is active", cut.Find(".pipeline-message").TextContent);
    }

    [TestMethod]
    public void Toggle_StageOutcomeUnknown_ReopeningTheSameStepReusesTheStageCommand()
    {
        var service = ToggleService();
        service.StageResults.Enqueue(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(OperatorUiResultKind.Unavailable, "Timed out."));
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");
        cut.Find("#pipeline-toggle-apply").Click();
        cut.Find("dialog.pipeline-dialog footer .button.secondary").Click();

        cut.Find("#pipeline-node-1").Click();
        cut.Find("#pipeline-step-toggle").Click();
        cut.WaitForElement("#pipeline-toggle-apply:not([disabled])");

        Assert.Contains("The last attempt did not report whether it saved.", cut.Find("dialog.pipeline-dialog [role=alert]").TextContent);
        cut.Find("#pipeline-toggle-apply").Click();

        cut.WaitForElement(".pipeline-message");
        Assert.HasCount(2, service.Stages);
        Assert.AreEqual(service.Stages[0].Key, service.Stages[1].Key);
        Assert.AreEqual(service.Stages[0].ExpectedVersion, service.Stages[1].ExpectedVersion);
        Assert.ContainsSingle(service.Activations);
    }

    [TestMethod]
    public void Toggle_ApplyOutcomeUnknown_ReopeningTheSameStepRepeatsOnlyTheApply()
    {
        var service = ToggleService();
        service.ActivationResults.Enqueue(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(OperatorUiResultKind.Unavailable, "Timed out."));
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");
        cut.Find("#pipeline-toggle-apply").Click();
        cut.Find("dialog.pipeline-dialog footer .button.secondary").Click();

        cut.Find("#pipeline-node-1").Click();
        cut.Find("#pipeline-step-toggle").Click();
        cut.WaitForElement("#pipeline-toggle-apply:not([disabled])");

        Assert.Contains("Draft revision 3 was saved, but applying it did not complete.", cut.Find("dialog.pipeline-dialog [role=alert]").TextContent);
        Assert.AreEqual("Apply revision 3", cut.Find("#pipeline-toggle-apply").TextContent.Trim());
        cut.Find("#pipeline-toggle-apply").Click();

        cut.WaitForElement(".pipeline-message");
        Assert.ContainsSingle(service.Stages);
        Assert.HasCount(2, service.Activations);
        Assert.AreEqual(service.Activations[0], service.Activations[1]);
    }

    [TestMethod]
    public void Toggle_OutcomeUnknown_ADifferentStepStartsANewCommand()
    {
        var service = ToggleService();
        service.StageResults.Enqueue(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(OperatorUiResultKind.Unavailable, "Timed out."));
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");
        cut.Find("#pipeline-toggle-apply").Click();
        cut.Find("dialog.pipeline-dialog footer .button.secondary").Click();

        cut.Find("#pipeline-node-2").Click();
        cut.Find("#pipeline-step-toggle").Click();
        cut.WaitForElement("#pipeline-toggle-apply:not([disabled])");

        Assert.IsEmpty(cut.FindAll("dialog.pipeline-dialog [role=alert]"));
        Assert.AreEqual("Turn off and apply", cut.Find("#pipeline-toggle-apply").TextContent.Trim());
        cut.Find("#pipeline-toggle-apply").Click();

        cut.WaitForElement(".pipeline-message");
        Assert.HasCount(2, service.Stages);
        Assert.AreNotEqual(service.Stages[0].Key, service.Stages[1].Key);
    }

    [TestMethod]
    public void Toggle_PreviewFromAnEarlierOpening_DoesNotReachTheReopenedDialog()
    {
        var service = ToggleService();
        var first = new TaskCompletionSource<OperatorUiResult<CameraAgentPipelineProfilePreview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<OperatorUiResult<CameraAgentPipelineProfilePreview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Queue<TaskCompletionSource<OperatorUiResult<CameraAgentPipelineProfilePreview>>>([first, second]);
        var succeed = service.Toggle!;
        service.ToggleAsync = _ => new(pending.Dequeue().Task);
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");
        cut.Find("dialog.pipeline-dialog footer .button.secondary").Click();
        cut.Find("#pipeline-node-1").Click();
        cut.Find("#pipeline-step-toggle").Click();
        cut.WaitForElement("dialog.pipeline-dialog");

        first.SetResult(OperatorUiResult<CameraAgentPipelineProfilePreview>.Failure(OperatorUiResultKind.Invalid, "Stale preview."));

        cut.WaitForAssertion(() => Assert.Contains("Compiling the updated graph.", cut.Find("dialog.pipeline-dialog [role=status]").TextContent));
        Assert.IsEmpty(cut.FindAll("dialog.pipeline-dialog [role=alert]"));
        Assert.IsTrue(cut.Find("#pipeline-toggle-apply").HasAttribute("disabled"));

        second.SetResult(succeed(service.Toggles[^1]));

        cut.WaitForElement("#pipeline-toggle-apply:not([disabled])");
        Assert.IsEmpty(cut.FindAll("dialog.pipeline-dialog [role=alert]"));
        Assert.Contains("3 to 2 steps", cut.Find("dialog.pipeline-dialog .ops-facts").TextContent);
    }

    [TestMethod]
    public void Toggle_ApplyRejected_SaysTheDraftWasSavedButNotApplied()
    {
        var service = ToggleService();
        service.ActivationResults.Enqueue(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(OperatorUiResultKind.Invalid, "Capture is outside its window"));
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");

        cut.Find("#pipeline-toggle-apply").Click();

        cut.WaitForElement(".pipeline-message");
        Assert.AreEqual("alert", cut.Find(".pipeline-message").GetAttribute("role"));
        Assert.Contains("Saved as draft revision 3 but not applied: Capture is outside its window. Review and apply it from Schedule.",
            cut.Find(".pipeline-message").TextContent);
        Assert.IsEmpty(cut.FindAll("dialog.pipeline-dialog"));
    }

    [TestMethod]
    public void Toggle_ScheduleChangedWhileOpen_AsksForARefreshAndDoesNotApply()
    {
        var service = ToggleService();
        service.StageResults.Enqueue(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(OperatorUiResultKind.Conflict, "Version mismatch."));
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");

        cut.Find("#pipeline-toggle-apply").Click();

        Assert.Contains("The schedule changed while this dialog was open. Close it and refresh to start from the current revision.",
            cut.Find("dialog.pipeline-dialog [role=alert]").TextContent);
        Assert.IsEmpty(service.Activations);
    }

    [TestMethod]
    public void Toggle_StepAlreadyInThatState_FinishesWithoutApplying()
    {
        var service = ToggleService();
        service.StageResults.Enqueue(OperatorUiResult<CaptureScheduleStoreSnapshot>.Success(
            new CaptureScheduleStoreSnapshot(CurrentState().ActiveRevision, null, 5, null, Now)));
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-1");

        cut.Find("#pipeline-toggle-apply").Click();

        cut.WaitForElement(".pipeline-message");
        Assert.Contains("Telemetry is already off in the active revision.", cut.Find(".pipeline-message").TextContent);
        Assert.IsEmpty(service.Activations);
    }

    [TestMethod]
    public void Toggle_PreviewRejected_ShowsWhyAndKeepsApplyDisabled()
    {
        var nodes = ExplicitPlan().DesiredNodes.Select(static node => node.Id == "Thumbnail" ? node with { Enabled = false } : node).ToArray();
        var service = ToggleService();
        service.Pipeline = Pipeline(Plan(nodes, nodes.Where(static node => node.Enabled).ToArray()));
        service.Toggle = static _ => OperatorUiResult<CameraAgentPipelineProfilePreview>.Failure(
            OperatorUiResultKind.Invalid, "The profile no longer compiles.");
        using var context = CreateContext(service);
        var cut = OpenToggle(context, "#pipeline-node-0");

        var dialog = cut.Find("dialog.pipeline-dialog");
        Assert.Contains("The profile no longer compiles.", dialog.QuerySelector("[role=alert]")!.TextContent);
        Assert.Contains("This is a required step.", dialog.TextContent);
        Assert.IsTrue(cut.Find("#pipeline-toggle-apply").HasAttribute("disabled"));

        cut.Find("dialog.pipeline-dialog footer .button.secondary").Click();

        Assert.IsEmpty(cut.FindAll("dialog.pipeline-dialog"));
        Assert.IsEmpty(service.Stages);
    }

    [TestMethod]
    public void Render_WhenUnauthorized_NavigatesToAccessDenied()
    {
        var service = new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan()))
        {
            PipelineResult = OperatorUiResult<CameraAgentPipelineOperatorState>.Failure(OperatorUiResultKind.Unauthorized, "Denied."),
        };
        using var context = CreateContext(service);
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        _ = context.Render<PipelineSummaryPage>();

        Assert.EndsWith("/Account/AccessDenied", navigation.Uri);
    }

    [TestMethod]
    public void ReadFailure_ShowsTheReasonAndTryAgainReloads()
    {
        var service = new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan()))
        {
            PipelineResult = OperatorUiResult<CameraAgentPipelineOperatorState>.Failure(OperatorUiResultKind.Unavailable, "Profile store offline."),
        };
        using var context = CreateContext(service);
        var cut = context.Render<PipelineSummaryPage>();
        cut.WaitForElement(".pipeline-error");
        Assert.Contains("Profile store offline.", cut.Find(".pipeline-error").TextContent);

        service.PipelineResult = null;
        cut.Find(".pipeline-error button").Click();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.IsEmpty(cut.FindAll(".pipeline-error"));
    }

    [TestMethod]
    public void LatestRun_WithoutRunsOrExecutions_IsDisabledWithTheReason()
    {
        using var noRuns = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())));
        var cut = noRuns.Render<PipelineSummaryPage>();
        cut.WaitForElement(".ops-pipeline-graph");
        Assert.IsTrue(cut.Find("button#pipeline-latest-run").HasAttribute("disabled"));
        Assert.AreEqual("No live pipeline run has been recorded yet.", cut.Find("#pipeline-latest-run-reason").TextContent.Trim());
        Assert.Contains("No runs yet", cut.Find(".ops-profile-banner").TextContent);

        var graphs = new ProcessingExecutionPagesTests.GraphUiService
        {
            Registry = BasicRegistry(),
            Failure = OperatorUiResult<CameraAgentProcessingExecutionsView>.Failure(OperatorUiResultKind.Unavailable, "Execution store offline."),
        };
        using var unreadable = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())), graphs: graphs);
        var failed = unreadable.Render<PipelineSummaryPage>();
        failed.WaitForElement(".ops-pipeline-graph");
        Assert.AreEqual("Processing executions could not be read.", failed.Find("#pipeline-latest-run-reason").TextContent.Trim());
        Assert.Contains("Unavailable", failed.Find(".ops-profile-banner").TextContent);
    }

    private static void AssertToggleBlocked(IRenderedComponent<PipelineSummaryPage> cut, string reason)
    {
        var toggle = cut.Find("#pipeline-step-toggle");
        Assert.IsTrue(toggle.HasAttribute("disabled"));
        Assert.AreEqual(reason, toggle.GetAttribute("title"));
        Assert.AreEqual("pipeline-step-toggle-reason", toggle.GetAttribute("aria-describedby"));
        Assert.AreEqual(reason, cut.Find("#pipeline-step-toggle-reason").TextContent.Trim());
    }

    [TestMethod]
    public void Graph_DrawsAPortOnEverySideAnEdgeTouches()
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        var raw = cut.Find("#pipeline-source-raw");
        Assert.IsNotNull(raw.QuerySelector(".node-port.out"));
        Assert.IsNull(raw.QuerySelector(".node-port.in"));
        var preview = cut.Find("#pipeline-node-0");
        Assert.IsNotNull(preview.QuerySelector(".node-port.in"), "Preview reads the raw frame.");
        Assert.IsNotNull(preview.QuerySelector(".node-port.out"), "Thumbnail reads Preview.");
        var telemetry = cut.Find("#pipeline-node-1");
        Assert.IsNotNull(telemetry.QuerySelector(".node-port.in"));
        Assert.IsNull(telemetry.QuerySelector(".node-port.out"), "Nothing reads Telemetry.");
        Assert.IsNotNull(cut.Find("#pipeline-node-2").QuerySelector(".node-port.in"));
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Off, "Transient detection is off", DisplayName = "Off")]
    [DataRow(TransientOperatingMode.Central, "Central detection", DisplayName = "Central")]
    public void Lane_WithoutLocalStages_SaysWhyAndLinksToTransients(TransientOperatingMode mode, string title)
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())), transients: Lane(mode));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        var note = cut.Find(".ops-pipeline-lane-note");
        Assert.Contains(title, note.TextContent);
        Assert.AreEqual("/operations/transients", note.QuerySelector("a")!.GetAttribute("href"));
        Assert.IsEmpty(cut.FindAll("[id^='pipeline-stage-']"));
        Assert.IsEmpty(cut.FindAll("path.pipeline-edge.transient"));
        Assert.IsNull(cut.Find("#pipeline-source-raw").QuerySelector(".node-port.in"));
    }

    [TestMethod]
    public void Lane_Unreadable_SaysSoAndStillDrawsTheSteps()
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())),
            transients: OperatorUiResult<TransientOperationsView>.Failure(OperatorUiResultKind.Unavailable, "Store offline at /var/lib/agent."));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.HasCount(3, cut.FindAll(".ops-pipeline-node.step"));
        Assert.Contains("Meteor detection state could not be read.", cut.Find(".ops-pipeline-lane-note").TextContent);
        Assert.Contains("State unavailable", cut.Find(".ops-pipeline-lane-title").TextContent);
        Assert.DoesNotContain("/var/lib/agent", cut.Markup);
    }

    [TestMethod]
    public void Lane_WhenUnauthorized_NavigatesToAccessDenied()
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())),
            transients: OperatorUiResult<TransientOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "Denied."));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        _ = context.Render<PipelineSummaryPage>();

        Assert.EndsWith("/Account/AccessDenied", navigation.Uri);
    }

    [TestMethod]
    public void Lane_Edge_DrawsTheLocalStagesBelowTheStepsAndInspectsOne()
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())),
            transients: Lane(TransientOperatingMode.Edge, pendingFrames: 3, pendingCandidates: 1));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        CollectionAssert.AreEqual(
            EdgeStageKeys,
            cut.FindAll("[id^='pipeline-stage-']").Select(static stage => stage.Id!["pipeline-stage-".Length..]).ToArray());
        Assert.Contains("--pipeline-row-template:4.25rem 4.25rem 1.5rem 4.25rem 4.25rem", cut.Find(".ops-pipeline-graph").GetAttribute("style")!);
        Assert.AreEqual("grid-column:2;grid-row:4", cut.Find("#pipeline-stage-detector-input").GetAttribute("style"));
        Assert.AreEqual("grid-column:2;grid-row:5", cut.Find("#pipeline-stage-frame-window").GetAttribute("style"));
        Assert.AreEqual("grid-column:3;grid-row:4", cut.Find("#pipeline-stage-causal-scan").GetAttribute("style"));
        Assert.AreEqual("grid-column:6;grid-row:4", cut.Find("#pipeline-stage-assess").GetAttribute("style"));
        Assert.HasCount(7, cut.FindAll("path.pipeline-edge.transient"));
        Assert.Contains("Edge detector healthy", cut.Find(".ops-pipeline-lane-title").TextContent);
        Assert.IsEmpty(cut.FindAll(".ops-pipeline-lane-note"));
        Assert.IsNotNull(cut.Find("#pipeline-stage-assess").QuerySelector(".node-port.in"));
        Assert.IsNull(cut.Find("#pipeline-stage-assess").QuerySelector(".node-port.out"), "The lane ends at the local event.");

        cut.Find("#pipeline-stage-causal-scan").Click();

        Assert.AreEqual("Causal candidate scan", cut.Find("#pipeline-step-title").TextContent.Trim());
        Assert.AreEqual("Meteor detection / this agent", cut.Find(".ops-pipeline-inspector .eyebrow").TextContent.Trim());
        var facts = cut.Find(".ops-node-facts").TextContent;
        Assert.Contains("Linear detector input, Durable frame window", facts);
        Assert.Contains("3 frames waiting", facts);
        Assert.Contains("CameraAgent transient worker", facts);
        Assert.AreEqual("true", cut.Find("#pipeline-stage-causal-scan").GetAttribute("aria-pressed"));
        Assert.AreEqual("false", cut.Find("#pipeline-node-0").GetAttribute("aria-pressed"));
        Assert.HasCount(3, cut.FindAll("path.pipeline-edge.transient.selected"));
        Assert.IsEmpty(cut.FindAll("#pipeline-step-toggle"), "A lane stage is not part of the revision and cannot be toggled here.");
        Assert.EndsWith("stage=causal-scan", navigation.Uri);
        Assert.DoesNotContain("step=", navigation.Uri);

        cut.Find("#pipeline-stage-persist-candidate").Click();
        Assert.Contains("1 candidate waiting", cut.Find(".ops-node-facts").TextContent);

        cut.Find("#pipeline-node-2").Click();
        Assert.AreEqual("Thumbnail", cut.Find("#pipeline-step-title").TextContent.Trim());
        Assert.DoesNotContain("stage=", navigation.Uri);
        Assert.IsEmpty(cut.FindAll("path.pipeline-edge.transient.selected"));
    }

    [TestMethod]
    public void Lane_Hybrid_RelaysToCentralStagesThisAgentDoesNotMeasure()
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())),
            transients: Lane(TransientOperatingMode.Hybrid, delivering: 2, retrying: 1));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.IsNotNull(cut.Find("#pipeline-stage-relay"));
        Assert.IsEmpty(cut.FindAll("#pipeline-stage-assess"));
        Assert.Contains("central", cut.Find("#pipeline-stage-central-validation").GetAttribute("class")!);
        Assert.DoesNotContain("central", cut.Find("#pipeline-stage-relay").GetAttribute("class")!);

        cut.Find("#pipeline-stage-relay").Click();
        Assert.Contains("2 candidates waiting, 1 retrying", cut.Find(".ops-node-facts").TextContent);

        cut.Find("#pipeline-stage-central-validation").Click();
        Assert.AreEqual("Meteor detection / LogicHost", cut.Find(".ops-pipeline-inspector .eyebrow").TextContent.Trim());
        var facts = cut.Find(".ops-node-facts").TextContent;
        Assert.Contains("Not measured on this agent", facts);
        Assert.Contains("LogicHost", facts);
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Edge, "assess", DisplayName = "Edge")]
    [DataRow(TransientOperatingMode.Hybrid, "relay", DisplayName = "Hybrid")]
    public void Lane_EmptyExplicitPlan_StillDrawsTheRawFrameAndTheLane(TransientOperatingMode mode, string stage)
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(Plan([], []))),
            transients: Lane(mode, pendingFrames: 2));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.AreEqual("This revision configures no processing steps.", cut.Find("#pipeline-empty").TextContent.Trim());
        Assert.AreEqual("grid-column:2 / -1;grid-row:1", cut.Find("#pipeline-empty").GetAttribute("style"));
        Assert.IsEmpty(cut.FindAll(".ops-pipeline-node.step"));
        Assert.IsNotNull(cut.Find($"#pipeline-stage-{stage}"));
        Assert.IsNotEmpty(cut.FindAll("path.pipeline-edge.transient"));
        Assert.IsNotNull(cut.Find("#pipeline-source-raw").QuerySelector(".node-port.out"), "The lane reads the raw frame.");
        Assert.IsNotEmpty(cut.FindAll("#pipeline-lane-list li"), "The text alternative lists the lane too.");
        Assert.Contains("Select the raw frame or a meteor detection stage", cut.Find(".ops-pipeline-inspector").TextContent);

        cut.Find("#pipeline-source-raw").Click();

        Assert.AreEqual("Meteor detection", cut.Find(".ops-node-facts dd").TextContent.Trim());
    }

    [TestMethod]
    [DataRow(TransientOperatingMode.Off, DisplayName = "Off")]
    [DataRow(TransientOperatingMode.Central, DisplayName = "Central")]
    public void Lane_EmptyExplicitPlan_WithoutLocalStages_OffersOnlyTheRawFrame(TransientOperatingMode mode)
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(Plan([], []))),
            transients: Lane(mode));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.IsEmpty(cut.FindAll("[id^='pipeline-stage-']"));
        var hint = cut.Find(".ops-pipeline-inspector").TextContent;
        Assert.Contains("Select the raw frame to inspect it.", hint);
        Assert.DoesNotContain("meteor detection stage", hint);
    }

    [TestMethod]
    public void Lane_DegradedWorker_ShowsOnlyTheStateTitleNotItsReason()
    {
        using var context = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())),
            transients: Lane(TransientOperatingMode.Edge, availability: TransientWorkerAvailability.Degraded, reason: "store-at-/srv/agent/transients"));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement(".ops-pipeline-graph");
        Assert.Contains("Edge detector degraded", cut.Find(".ops-pipeline-lane-title").TextContent);
        Assert.DoesNotContain("/srv/agent", cut.Markup);
    }

    [TestMethod]
    public void StageQuery_OpensTheRawFrameOrAStageTheModeDraws()
    {
        using var edge = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())),
            transients: Lane(TransientOperatingMode.Edge));
        edge.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline?stage=raw-frame");

        var raw = edge.Render<PipelineSummaryPage>();

        raw.WaitForElement(".ops-pipeline-graph");
        Assert.AreEqual("Raw frame", raw.Find("#pipeline-step-title").TextContent.Trim());
        Assert.AreEqual("true", raw.Find("#pipeline-source-raw").GetAttribute("aria-pressed"));
        Assert.Contains("Preview, Telemetry, Meteor detection", raw.Find(".ops-node-facts").TextContent);
        Assert.HasCount(4, raw.FindAll("path.pipeline-edge.selected"), "Two steps and two lane stages read the raw frame.");

        using var off = CreateContext(new PipelineScheduleService(CurrentState(), Pipeline(ExplicitPlan())));
        off.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline?stage=assess");

        var fallback = off.Render<PipelineSummaryPage>();

        fallback.WaitForElement(".ops-pipeline-graph");
        Assert.AreEqual("Preview", fallback.Find("#pipeline-step-title").TextContent.Trim(), "Off draws no assess stage, so the first step is shown.");
    }

    private static IRenderedComponent<PipelineSummaryPage> OpenToggle(BunitContext context, string nodeSelector)
    {
        var cut = context.Render<PipelineSummaryPage>();
        cut.WaitForElement(".ops-pipeline-graph");
        cut.Find(nodeSelector).Click();
        cut.Find("#pipeline-step-toggle").Click();
        cut.WaitForElement("dialog.pipeline-dialog");
        return cut;
    }

    private static BunitContext CreateContext(
        PipelineScheduleService schedule,
        ProcessingExecutionPagesTests.GraphUiService? graphs = null,
        OperatorUiResult<NamedRigUiCatalog>? rig = null,
        ProcessingGraphExecutionState[]? executions = null,
        OperatorUiResult<TransientOperationsView>? transients = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(schedule);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(graphs ?? new ProcessingExecutionPagesTests.GraphUiService
        {
            Registry = BasicRegistry(),
            Executions = Executions(executions ?? []),
        });
        var rigService = new Mock<ICameraAgentNamedRigUiService>();
        rigService.Setup(service => service.GetAsync(It.IsAny<CancellationToken>())).Returns(() => ValueTask.FromResult(
            rig ?? OperatorUiResult<NamedRigUiCatalog>.Success(new(new NamedRigSelection(null, null, 1), [], null, null))));
        context.Services.AddSingleton(rigService.Object);
        context.Services.AddSingleton<ICameraAgentTransientOperationsUiService>(new TransientOperationsPageTests.TestTransientOperationsUiService(
            transients ?? OperatorUiResult<TransientOperationsView>.Success(TransientOperationsPageTests.View(TransientOperatingMode.Off))));
        return context;
    }

    private static OperatorUiResult<TransientOperationsView> Lane(
        TransientOperatingMode mode, long pendingFrames = 0, long pendingCandidates = 0, long delivering = 0, int retrying = 0,
        TransientWorkerAvailability availability = TransientWorkerAvailability.Healthy, string reason = "ready")
    {
        var view = TransientOperationsPageTests.View(mode);
        return OperatorUiResult<TransientOperationsView>.Success(view with
        {
            Worker = view.Worker with
            {
                Availability = availability,
                Reason = reason,
                PendingFrames = pendingFrames,
                PendingCandidates = pendingCandidates,
            },
            Delivery = view.Delivery with { PendingCount = delivering, RetryingCount = retrying },
        });
    }

    private static PipelineScheduleService ToggleService()
    {
        var active = ExplicitPlan();
        var after = active.EffectiveNodes.Where(static node => node.Id != "Telemetry").ToArray();
        var service = new PipelineScheduleService(CurrentState(), Pipeline(active))
        {
            Toggle = request => OperatorUiResult<CameraAgentPipelineProfilePreview>.Success(new(
                request.Basis, "staged-profile", Plan(active.DesiredNodes, after))),
        };
        return service;
    }

    private static OperatorUiResult<CaptureScheduleStoreSnapshot> Staged()
    {
        var state = CurrentState();
        var staged = state.ActiveRevision with { RevisionId = "rev-3", RevisionNumber = 3 };
        return OperatorUiResult<CaptureScheduleStoreSnapshot>.Success(new(state.ActiveRevision, staged, 5, null, Now));
    }

    private static OperatorUiResult<CaptureScheduleStoreSnapshot> Activated()
    {
        var staged = CurrentState().ActiveRevision with { RevisionId = "rev-3", RevisionNumber = 3 };
        return OperatorUiResult<CaptureScheduleStoreSnapshot>.Success(new(staged, null, 6, null, Now));
    }

    private static CaptureScheduleOperatorState CurrentState()
    {
        var state = SchedulePageTests.State();
        return state with
        {
            ActiveRevision = state.ActiveRevision with
            {
                Profile = state.ActiveRevision.Profile with { SchemaVersion = LocalCaptureProfileDefinition.CurrentSchemaVersion },
            },
        };
    }

    private static CameraAgentPipelineOperatorState Pipeline(CaptureProcessingPlanPreview plan, bool canToggle = true)
        => new(new CameraAgentPipelineRevisionPlan(ActiveId, 2, new string('A', 64), canToggle, plan), null);

    private static CaptureProcessingPlanPreview ExplicitPlan()
    {
        using var options = JsonDocument.Parse("""{"deviceSerial":"SERIAL-0042","sdkPath":"/opt/sdk"}""");
        var preview = Node("Preview", enabled: true, required: true, order: 10, [RawInput]) with
        {
            Options = options.RootElement.Clone(),
            RecipeName = "encoded-preview",
            OutputRole = FrameArtifactRole.Preview,
            OutputVariant = "display",
        };
        var telemetry = Node("Telemetry", enabled: true, required: false, order: 20, [RawInput]);
        var thumbnail = Node("Thumbnail", enabled: true, required: false, order: 30, ["Preview"]) with
        {
            OutputRole = FrameArtifactRole.Preview,
            OutputVariant = "thumbnail",
        };
        CaptureProcessingPlanNode[] nodes = [preview, telemetry, thumbnail];
        return Plan(nodes, nodes);
    }

    private static CaptureProcessingPlanNode Node(string id, bool enabled, bool required, int order, IReadOnlyList<string>? dependencies)
        => new(id, Alias(id), enabled, required, order, null, dependencies, null, null, null);

    private static string Alias(string id) => id switch
    {
        "Preview" => "preview",
        "Telemetry" => "telemetry",
        "Thumbnail" => "thumbnail",
        _ => id,
    };

    private static CaptureProcessingPlanPreview Plan(
        IReadOnlyList<CaptureProcessingPlanNode> desired, IReadOnlyList<CaptureProcessingPlanNode> effective, bool legacy = false)
        => new(
            legacy ? CapturePipelineSchemaVersions.LegacyV1 : CapturePipelineSchemaVersions.ExplicitV2,
            legacy ? CapturePipelineDependencyPolicy.LegacyInference : CapturePipelineDependencyPolicy.RejectEnabledDependent,
            new string('D', 64),
            new string('E', 64),
            desired,
            effective);

    private static ProcessingGraphRegistryState BasicRegistry()
        => new(ProcessingGraphRegistryMode.ConfiguredBasic, "basic-1", "basic-1", 1, []);

    private static CameraAgentProcessingExecutionsView Executions(params ProcessingGraphExecutionState[] live) => new(
        Now, CameraAgentProcessingExecutionProjection.MaximumPerClass,
        live.Select(CameraAgentProcessingExecutionProjection.Summarize).ToArray(), []);

    /// <summary>
    /// A schedule service that records every command so the one-step apply can be checked end to end. Queued results
    /// are returned first; once they run out, staging saves revision 3 and applying it succeeds.
    /// </summary>
    private sealed class PipelineScheduleService(CaptureScheduleOperatorState state, CameraAgentPipelineOperatorState? pipeline)
        : ICameraAgentScheduleUiService
    {
        internal CaptureScheduleOperatorState State { get; set; } = state;
        internal OperatorUiResult<CaptureScheduleOperatorState>? StateResult { get; set; }
        internal CameraAgentPipelineOperatorState? Pipeline { get; set; } = pipeline;
        internal OperatorUiResult<CameraAgentPipelineOperatorState>? PipelineResult { get; set; }
        internal int PipelineReads { get; private set; }
        internal Func<(string ProfileJson, string Basis, string NodeId, bool Enabled), OperatorUiResult<CameraAgentPipelineProfilePreview>>? Toggle { get; set; }
        internal Func<(string ProfileJson, string Basis, string NodeId, bool Enabled), ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>>>? ToggleAsync { get; set; }
        internal List<(string ProfileJson, string Basis, string NodeId, bool Enabled)> Toggles { get; } = [];
        internal Queue<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageResults { get; } = [];
        internal List<(string ProfileJson, string Basis, long ExpectedVersion, string Key, string? Reason)> Stages { get; } = [];
        internal Queue<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivationResults { get; } = [];
        internal List<(string RevisionId, long ExpectedVersion, string Key, string? Reason)> Activations { get; } = [];

        public ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(StateResult ?? OperatorUiResult<CaptureScheduleOperatorState>.Success(State));

        public ValueTask<OperatorUiResult<CameraAgentPipelineOperatorState>> GetPipelineAsync(CancellationToken cancellationToken)
        {
            PipelineReads++;
            return ValueTask.FromResult(PipelineResult ?? OperatorUiResult<CameraAgentPipelineOperatorState>.Success(Pipeline!));
        }

        public ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>> TogglePipelineAsync(
            string profileJson, string basisRevisionId, string nodeId, bool enabled, CancellationToken cancellationToken)
        {
            var request = (profileJson, basisRevisionId, nodeId, enabled);
            Toggles.Add(request);
            if (ToggleAsync is { } pending)
            {
                return pending(request);
            }
            return ValueTask.FromResult(Toggle?.Invoke(request) ?? throw new NotSupportedException());
        }

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
            string profileJson, string basisRevisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
        {
            Stages.Add((profileJson, basisRevisionId, expectedVersion, idempotencyKey, reason));
            return ValueTask.FromResult(StageResults.TryDequeue(out var result) ? result : Staged());
        }

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
        {
            Activations.Add((revisionId, expectedVersion, idempotencyKey, reason));
            return ValueTask.FromResult(ActivationResults.TryDequeue(out var result) ? result : Activated());
        }

        public ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
            string profileJson, string basisRevisionId, int dayCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> RollbackAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
            CaptureScheduleOverride scheduleOverride, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
            string overrideId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
