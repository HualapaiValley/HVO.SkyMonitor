using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraRigPageTests
{
    [TestMethod]
    public void Render_ShowsRevisionsReadOnlyFactsAndTypedFields()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new RigUiService(SchedulePageTests.State()));

        var cut = context.Render<CameraRigPage>();

        cut.WaitForElement(".camera-form");
        Assert.AreEqual("Camera & rig", cut.Find("h1").TextContent.Trim());
        Assert.IsTrue(cut.Markup.Contains("Revision 2", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("2 x 2 px", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Optics and orientation", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("no metered exposure envelope", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll("textarea"));
        Assert.IsNotNull(cut.Find("a[href='/operations/schedule']"));
    }

    [TestMethod]
    public void ValidateThenSave_StagesTheTypedDraftWithOnlyTheEditedFieldChanged()
    {
        using var context = new BunitContext();
        var service = new RigUiService(SchedulePageTests.State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement(".camera-form");

        var nightGain = cut.FindAll("label").Single(label => label.TextContent.StartsWith("Night gain", StringComparison.Ordinal)).QuerySelector("input")!;
        nightGain.Change("3.5");
        cut.Find("button[type='submit']").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("The draft is valid", StringComparison.Ordinal)));
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));

        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => Assert.HasCount(1, service.StageCommands));
        var staged = CameraAgentScheduleUiService.ParseProfile(service.StageCommands[0].ProfileJson);
        Assert.AreEqual(3.5, staged.Rig.Pipeline.NightGain);
        Assert.AreEqual(SchedulePageTests.Profile().Rig.Pipeline.DayGain, staged.Rig.Pipeline.DayGain);
        Assert.AreEqual(SchedulePageTests.State().ActiveRevision.RevisionId, service.StageCommands[0].BasisRevisionId);
        Assert.AreEqual(1, service.PreviewCalls);
        Assert.AreEqual(1, service.PipelinePreviewCalls);
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Draft saved", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void InvalidValue_BlocksValidationWithLabelledMessage()
    {
        using var context = new BunitContext();
        var service = new RigUiService(SchedulePageTests.State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement(".camera-form");

        cut.FindAll("label").Single(label => label.TextContent.StartsWith("Focal length", StringComparison.Ordinal)).QuerySelector("input")!.Change("wide");
        cut.Find("button[type='submit']").Click();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Focal length must be a number.", StringComparison.Ordinal));
        Assert.AreEqual(0, service.PreviewCalls);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Edit_AfterValidation_RequiresValidationAgain()
    {
        using var context = new BunitContext();
        var service = new RigUiService(SchedulePageTests.State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement(".camera-form");
        cut.Find("button[type='submit']").Click();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled")));

        cut.FindAll("label").Single(label => label.TextContent.StartsWith("Day gain", StringComparison.Ordinal)).QuerySelector("input")!.Change("2");

        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Render_WhenUnauthorized_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(null));

        _ = context.Render<CameraRigPage>();

        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    private sealed class RigUiService(CaptureScheduleOperatorState state) : ICameraAgentScheduleUiService
    {
        internal List<(string ProfileJson, string BasisRevisionId, string Key, long ExpectedVersion)> StageCommands { get; } = [];
        internal int PreviewCalls { get; private set; }
        internal int PipelinePreviewCalls { get; private set; }

        public ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CaptureScheduleOperatorState>.Success(state));

        public ValueTask<OperatorUiResult<CameraAgentPipelineOperatorState>> GetPipelineAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
            string profileJson, string basisRevisionId, int dayCount, CancellationToken cancellationToken)
        {
            PreviewCalls++;
            _ = CameraAgentScheduleUiService.ParseProfile(profileJson);
            return ValueTask.FromResult(OperatorUiResult<CaptureSchedulePreview>.Success(state.Preview));
        }

        public ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>> PreviewPipelineAsync(
            string profileJson, string basisRevisionId, CancellationToken cancellationToken)
        {
            PipelinePreviewCalls++;
            var plan = new CaptureProcessingPlanPreview(
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent,
                new string('D', 64),
                new string('E', 64),
                [],
                []);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineProfilePreview>.Success(new(basisRevisionId, profileJson, plan)));
        }

        public ValueTask<OperatorUiResult<CameraAgentPipelineProfilePreview>> TogglePipelineAsync(
            string profileJson, string basisRevisionId, string nodeId, bool enabled, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
            string profileJson, string basisRevisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
        {
            StageCommands.Add((profileJson, basisRevisionId, idempotencyKey, expectedVersion));
            return ValueTask.FromResult(OperatorUiResult<CaptureScheduleStoreSnapshot>.Success(
                new CaptureScheduleStoreSnapshot(state.ActiveRevision, state.PendingRevision, state.StateVersion + 1, null, DateTimeOffset.UtcNow)));
        }

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> RollbackAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
            CaptureScheduleOverride scheduleOverride, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
            string overrideId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
