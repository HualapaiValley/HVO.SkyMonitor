using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class PipelineSummaryPageTests
{
    [TestMethod]
    public void Render_ShowsActiveRevisionGraphAndNoPendingNotice()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement("#pipeline-active");

        Assert.AreEqual("Pipeline summary", cut.Find("h1").TextContent.Trim());
        Assert.AreEqual("Revision 2", cut.Find("#pipeline-active").TextContent.Trim());
        Assert.IsTrue(cut.Markup.Contains("Desired graph", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Effective graph", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Preview / required / enabled", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("No pending revision", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("#pipeline-pending"));
        Assert.IsFalse(cut.FindAll("button").Any(static button => button.TextContent.Contains("Disable", StringComparison.Ordinal)));
        Assert.IsNotNull(cut.Find("a[href='/operations/schedule']"));
    }

    [TestMethod]
    public void Render_WithPendingRevision_ShowsBothGraphsAndToggleAvailability()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new PendingPipelineService(SchedulePageTests.State()));

        var cut = context.Render<PipelineSummaryPage>();

        cut.WaitForElement("#pipeline-pending");
        Assert.AreEqual("Revision 2", cut.Find("#pipeline-active").TextContent.Trim());
        Assert.AreEqual("Revision 3", cut.Find("#pipeline-pending").TextContent.Trim());
        Assert.IsFalse(cut.Markup.Contains("No pending revision", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Available from the capture schedule", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Requires a cameraagent-local-profile-v2 revision", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Telemetry / required / disabled", StringComparison.Ordinal));
        Assert.IsNotNull(cut.Find(".pipeline-node--disabled"));
    }

    [TestMethod]
    public void Render_WhenUnauthorized_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(null));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        _ = context.Render<PipelineSummaryPage>();

        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    private sealed class PendingPipelineService(HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleOperatorState state)
        : SchedulePageTests.ScheduleUiService(state)
    {
        private readonly HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleOperatorState _state = state;

        public override ValueTask<OperatorUiResult<CameraAgentPipelineOperatorState>> GetPipelineAsync(CancellationToken cancellationToken)
        {
            var enabled = new HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.CaptureProcessingPlanNode(
                "Preview", "Preview", true, true, 10, null, ["$raw"], "encoded-preview", HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview, "display");
            var disabled = new HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.CaptureProcessingPlanNode(
                "Telemetry", "Telemetry", false, true, 20, null, ["$raw"], null, null, null);
            HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.CaptureProcessingPlanPreview Plan(params HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.CaptureProcessingPlanNode[] desired) => new(
                HVO.SkyMonitor.AgentCore.CapturePipelineSchemaVersions.ExplicitV2,
                HVO.SkyMonitor.AgentCore.CapturePipelineDependencyPolicy.RejectEnabledDependent,
                new string('D', 64),
                new string('E', 64),
                desired,
                desired.Where(static node => node.Enabled).ToArray());
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineOperatorState>.Success(new(
                new CameraAgentPipelineRevisionPlan(_state.ActiveRevision.RevisionId, _state.ActiveRevision.RevisionNumber, _state.ActiveRevision.ProfileSha256, false, Plan(enabled)),
                new CameraAgentPipelineRevisionPlan("rev-3", 3, new string('B', 64), true, Plan(enabled, disabled)))));
        }
    }
}
