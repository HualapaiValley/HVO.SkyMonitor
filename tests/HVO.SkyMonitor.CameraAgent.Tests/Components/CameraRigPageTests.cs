using System.Security.Claims;
using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraRigPageTests
{
    [TestMethod]
    public void FreshVirtualSkyInventory_CreatesStarterAndOpensTypedCameraEditor()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { VirtualOnly = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement("[aria-label='Add ZWO camera']");
        cut.Find("[aria-label='Add ZWO camera'] button").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("asi676mc", service.StarterTemplate);
            Assert.AreEqual("starter-v1", cut.FindAll("[aria-label='Equipment editor'] select")[1].GetAttribute("value"));
            StringAssert.Contains(cut.Markup, "Basis expected model: ASI676MC", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("libraryPathEnvironmentVariable", StringComparison.Ordinal));
        });
        Assert.IsEmpty(cut.FindAll("[aria-label='Add ZWO camera']"));
    }

    [TestMethod]
    public void VirtualSkyStarter_CustomCameraDuplicate_SelectsSavedCameraForComposition()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { VirtualOnly = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("[aria-label='Add ZWO camera'] button").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("starter-v1", cut.FindAll("[aria-label='Rig composer'] select")[1].GetAttribute("value")));
        cut.FindAll("[aria-label='Equipment editor'] select")[2].Change(true);
        cut.FindAll("[aria-label='Equipment editor'] label")
            .Single(l => l.TextContent.StartsWith("Equipment name", StringComparison.Ordinal))
            .QuerySelector("input")!.Change("Custom ASI120MM Mini");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Custom ZWO expected model", StringComparison.Ordinal));
        cut.FindAll("[aria-label='Equipment editor'] label")
            .Single(l => l.TextContent.StartsWith("Custom ZWO expected model", StringComparison.Ordinal))
            .QuerySelector("input")!.Change("ASI120MM Mini");
        cut.FindAll("[aria-label='Equipment editor'] label")
            .Single(l => l.TextContent.Contains("unvalidated camera model", StringComparison.Ordinal))
            .QuerySelector("input")!.Change(true);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create duplicate equipment", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("camera-v3", cut.FindAll("[aria-label='Rig composer'] select")[1].GetAttribute("value"));
            StringAssert.Contains(cut.Find("[aria-label='Rig composer']").TextContent, "Camera: Custom ASI120MM Mini", StringComparison.Ordinal);
        });
        cut.FindAll("button").Single(b => b.TextContent == "Compose immutable revision").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("camera-v3", service.ComposedCameraId));
    }

    [TestMethod]
    public void Render_UsesNamedRigInventoryWithoutScheduleEditor()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement("[aria-label='Rig composer']");
        StringAssert.Contains(cut.Markup, "Pending restart", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "Compare &amp; stage", StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains("Night gain", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Capture interval", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Render_ShowsSelectionFactsProvenanceAndCollapsedAdvancedReadout()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("camera-v1");
        var advanced = cut.Find(".camera-advanced");
        Assert.IsFalse(advanced.HasAttribute("open"));
        cut.FindAll("[aria-label='Equipment editor'] label")
            .Single(l => l.TextContent.Contains("Sensor readout", StringComparison.Ordinal))
            .QuerySelector("input")!.Change(true);
        advanced = cut.Find(".camera-advanced");
        StringAssert.Contains(advanced.TextContent, "ROI width", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("[aria-label='Rig selection']").TextContent, "Installed rig / revision 1", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".camera-compare").TextContent, "Readout:", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".camera-provenance").TextContent, "older-v3", StringComparison.Ordinal);
        cut.Find("[aria-label='Rig preview and selection'] select").Change("rig-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".camera-provenance").TextContent,
            "installed-schedule", StringComparison.Ordinal));
        Assert.IsNotEmpty(cut.FindAll("[aria-label='Rig revision history'] li"));
    }

    [TestMethod]
    public void PendingFailure_RefreshShowsFailureAndCancellationWithoutInternalDetails()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { PendingId = "rig-v1" };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        Assert.IsEmpty(cut.FindAll("[aria-label='Rig runtime failure']"));
        service.PendingFailure = "Pending rig failed to initialize.";
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        var alert = cut.Find("[aria-label='Rig runtime failure']");
        StringAssert.Contains(alert.TextContent, "Cancel the pending restart", StringComparison.Ordinal);
        StringAssert.Contains(alert.TextContent, "correct the hardware and restart", StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains("pending-command-secret", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Unvalidated at runtime. Restart required", StringComparison.Ordinal));
        cut.FindAll("button").Single(b => b.TextContent == "Cancel pending restart").Click();
        Assert.AreEqual(1, service.CancelCount);
    }

    [TestMethod]
    public void ActiveFailure_RefreshShowsCaptureUnavailableThenClears()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { ActiveFailure = "Active camera is unavailable for capture." };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        StringAssert.Contains(cut.Find("[aria-label='Rig runtime failure']").TextContent,
            "Check the camera hardware and restart", StringComparison.Ordinal);
        service.ActiveFailure = null;
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        Assert.IsEmpty(cut.FindAll("[aria-label='Rig runtime failure']"));
    }

    [TestMethod]
    public void UnauthorizedRuntimeRead_DoesNotShowFailure()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { Unauthorized = true, PendingFailure = "Pending rig failed to initialize." };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("[aria-label='Rig runtime failure']"));
    }

    [TestMethod]
    public async Task ServiceGet_UnauthorizedOwnerCannotReadRuntimeStatus()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(service => service.AuthorizeAsync(principal, null,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1)).ReturnsAsync(AuthorizationResult.Failed());
        var service = new CameraAgentNamedRigUiService(
            new FixedAuthenticationStateProvider(principal), authorization.Object, null!, null!,
            NullLogger<CameraAgentNamedRigUiService>.Instance);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsNull(result.Value);
        authorization.Verify(value => value.AuthorizeAsync(principal, null,
            CameraAgentAuthorizationPolicyNames.OperationsReadV1), Times.Once);
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    [TestMethod]
    public void Preview_RequiresExplicitAcknowledgement_BeforeStaging()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Runtime verified: no", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.StageCount));
        Assert.IsTrue(service.Acknowledged);
        Assert.AreEqual("schedule-v1", service.StagedScheduleId);
        Assert.AreEqual(service.ScheduleHash, service.StagedScheduleHash);
        StringAssert.Contains(cut.Markup, "Runtime compatibility is not yet verified", StringComparison.Ordinal);
    }

    [TestMethod]
    public void PreviewFailure_CannotBeAcknowledgedOrStaged()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { Valid = false };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Preview failed", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.AreEqual(0, service.StageCount);
    }

    [TestMethod]
    public void ScheduleChangesAfterPreview_RequiresAnotherPreview()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        service.ScheduleId = "schedule-v2";
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Preview again", StringComparison.Ordinal));
        Assert.AreEqual(0, service.StageCount);
    }

    [TestMethod]
    public async Task Stage_DuringFreshPreview_IsSingleFlight()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true }).ConfigureAwait(false);
        service.DeferredPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cut.Find("button.btn-primary").ClickAsync(new MouseEventArgs());
        Assert.IsFalse(first.IsCompleted);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        await cut.Find("button.btn-primary").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        service.DeferredPreview.SetResult(OperatorUiResult<NamedRigPreview>.Success(
            new(service.ScheduleId, service.ScheduleHash, "rig-v1", true, null, false)));
        await first.ConfigureAwait(false);
        Assert.AreEqual(1, service.StageCount);
    }

    [TestMethod]
    public async Task Stage_AcknowledgementChangesDuringFreshPreview_DoesNotSend()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true }).ConfigureAwait(false);
        service.DeferredPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = cut.Find("button.btn-primary").ClickAsync(new MouseEventArgs());
        Assert.IsFalse(stage.IsCompleted);
        await cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false }).ConfigureAwait(false);
        service.DeferredPreview.SetResult(OperatorUiResult<NamedRigPreview>.Success(
            new(service.ScheduleId, service.ScheduleHash, "rig-v1", true, null, false)));
        await stage.ConfigureAwait(false);
        Assert.AreEqual(0, service.StageCount);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task Stage_RevisionChangesDuringFreshPreview_DoesNotSend()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true }).ConfigureAwait(false);
        service.DeferredPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = cut.Find("button.btn-primary").ClickAsync(new MouseEventArgs());
        Assert.IsFalse(stage.IsCompleted);
        await cut.Find("[aria-label='Rig preview and selection'] select").ChangeAsync(new ChangeEventArgs { Value = "older-v3" }).ConfigureAwait(false);
        service.DeferredPreview.SetResult(OperatorUiResult<NamedRigPreview>.Success(
            new(service.ScheduleId, service.ScheduleHash, "rig-v1", true, null, false)));
        await stage.ConfigureAwait(false);
        Assert.AreEqual(0, service.StageCount);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public void HistorySelector_LoadsOlderRevisionsAndCanPreviewThem()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement("[aria-label='Rig preview and selection'] option[value='older-v3']");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Load older revisions", StringComparison.Ordinal)).Click();
        cut.WaitForElement("[aria-label='Rig preview and selection'] option[value='older-v1']");
        cut.Find("[aria-label='Rig preview and selection'] select").Change("older-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Selected</h3>", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "Camera: Historical sensor", StringComparison.Ordinal);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Contract preview passed", StringComparison.Ordinal));
        Assert.AreEqual(2, service.HistoryCalls);
        Assert.IsFalse(cut.Markup.Contains("ASI_SDK_PATH", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SwitchingProfile_FromHistoricalRevision_SelectsNewProfileRevisionAndName()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("[aria-label='Rig preview and selection'] select").Change("older-v3");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "ASI120MM (unvalidated model)", StringComparison.Ordinal));
        cut.Find("[aria-label='Rig composer'] select").Change("other");
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("other-v2", cut.Find("[aria-label='Rig preview and selection'] select").GetAttribute("value"));
            Assert.AreEqual("Other rig", cut.Find("[aria-label='Rig composer'] label input:not([type])").GetAttribute("value"));
            Assert.IsFalse(cut.Find("[aria-label='Rig preview and selection'] article:last-child").TextContent.Contains("ASI120MM", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task DelayedHistory_AfterRapidProfileSwitches_DoesNotReplaceLatestSelection()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        var delayed = service.DeferHistory("other");
        var other = cut.Find("[aria-label='Rig composer'] select").ChangeAsync(new ChangeEventArgs { Value = "other" });
        Assert.IsFalse(other.IsCompleted);
        await cut.Find("[aria-label='Rig composer'] select").ChangeAsync(new ChangeEventArgs { Value = "rig" }).ConfigureAwait(false);
        delayed.SetResult(OperatorUiResult<NamedRigHistoryPage>.Success(new(1,
            [new("foreign-v9", "other", 9, "camera-v2", "optics-v1", "mount-v1", null)], null)));
        await other.ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("rig", cut.Find("[aria-label='Rig composer'] select").GetAttribute("value"));
            Assert.AreEqual("older-v3", cut.Find("[aria-label='Rig preview and selection'] select").GetAttribute("value"));
            Assert.IsFalse(cut.Markup.Contains("foreign-v9", StringComparison.Ordinal));
        });
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Load older revisions", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        cut.WaitForElement("[aria-label='Rig preview and selection'] option[value='older-v1']");
    }

    [TestMethod]
    public async Task DelayedHistory_AfterNewerRequestForSameProfile_DoesNotReplaceNewerPage()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        var delayed = service.DeferHistory("rig");
        var older = cut.FindAll("button").Single(b => b.TextContent.Contains("Load older revisions", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());
        Assert.IsFalse(older.IsCompleted);
        await cut.FindAll("button").Single(b => b.TextContent == "Refresh").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        delayed.SetResult(OperatorUiResult<NamedRigHistoryPage>.Success(new(3,
            [new("obsolete-v1", "rig", 1, "camera-v1", "optics-v1", "mount-v1", null)], null)));
        await older.ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.IsFalse(cut.Markup.Contains("obsolete-v1", StringComparison.Ordinal));
            Assert.IsNotEmpty(cut.FindAll("button").Where(b => b.TextContent.Contains("Load older revisions", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void CreatedProfile_CanBeRenamedWithoutCreatingAnotherProfile()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        var composer = cut.Find("[aria-label='Rig composer']");
        composer.QuerySelector("input[type=checkbox]")!.Change(true);
        composer.QuerySelector("label input:not([type])")!.Change("New rig");
        cut.FindAll("button").Single(b => b.TextContent == "Save rig name").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("new", cut.Find("[aria-label='Rig composer'] select").GetAttribute("value")));
        Assert.IsFalse(cut.Find("[aria-label='Rig composer'] input[type=checkbox]").HasAttribute("checked"));
        cut.Find("[aria-label='Rig composer'] label input:not([type])").Change("Renamed rig");
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        Assert.AreEqual("Renamed rig", cut.Find("[aria-label='Rig composer'] label input:not([type])").GetAttribute("value"));
        cut.FindAll("button").Single(b => b.TextContent == "Save rig name").Click();
        Assert.AreEqual("new", service.SavedProfileId);
        Assert.AreEqual("Renamed rig", service.SavedProfileName);
    }

    [TestMethod]
    public void Refresh_RestoresSelectedRevisionBeyondFirstHundred_WithoutExposingSecrets()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { DeepHistory = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        for (var page = 0; page < 2; page++)
            cut.FindAll("button").Single(b => b.TextContent.Contains("Load older revisions", StringComparison.Ordinal)).Click();
        cut.Find("[aria-label='Rig preview and selection'] select").Change("older-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Camera: Historical sensor", StringComparison.Ordinal));
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("older-v1", cut.Find("[aria-label='Rig preview and selection'] select").GetAttribute("value"));
            StringAssert.Contains(cut.Markup, "Camera: Historical sensor", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("ASI_SDK_PATH", StringComparison.Ordinal));
        });
        Assert.AreEqual(6, service.HistoryCalls);
    }

    [TestMethod]
    public void PreviewResult_AfterRevisionChanges_CannotEnableStage()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { DeferredPreview = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.Find("[aria-label='Rig preview and selection'] select").Change("older-v3");
        service.DeferredPreview.SetResult(OperatorUiResult<NamedRigPreview>.Success(
            new("schedule-v1", service.ScheduleHash, "rig-v1", true, null, false)));
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Markup.Contains("Contract preview passed", StringComparison.Ordinal)));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public void UncertainStage_RefreshAndScheduleChange_RetryOrAbandon()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { StageUnavailable = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.StageCount));
        var key = service.StageKey;
        var version = service.StageVersion;
        var hash = service.StagedScheduleHash;
        service.ScheduleId = "schedule-v2";
        service.ScheduleHash = new('B', 64);
        service.Version = 2;
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        StringAssert.Contains(cut.Markup, "uncertain outcome", StringComparison.Ordinal);
        Assert.AreEqual(1, service.StageCount);
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StageCount));
        Assert.AreEqual(key, service.StageKey);
        Assert.AreEqual(version, service.StageVersion);
        Assert.AreEqual("schedule-v1", service.StagedScheduleId);
        Assert.AreEqual(hash, service.StagedScheduleHash);
        cut.FindAll("button").Single(b => b.TextContent == "Abandon stage retry").Click();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(3, service.StageCount));
        Assert.AreNotEqual(key, service.StageKey);
        Assert.AreEqual("schedule-v2", service.StagedScheduleId);
        Assert.AreEqual(service.ScheduleHash, service.StagedScheduleHash);
    }

    [TestMethod]
    public void UncertainSuccessfulStage_PendingRefresh_AllowsOnlyOriginalRequestReplay()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { StageUnavailable = true, StageCommittedOnUnavailable = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.StageCount));
        var key = service.StageKey;
        var version = service.StageVersion;
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled")));
        Assert.AreEqual("Retry same stage request", cut.Find("button.btn-primary").TextContent);
        service.StageUnavailable = false;
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StageCount));
        Assert.AreEqual(key, service.StageKey);
        Assert.AreEqual(version, service.StageVersion);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.AreEqual("Confirm restart-only stage", cut.Find("button.btn-primary").TextContent);
    }

    [TestMethod]
    public void UncertainStage_CommittedThenCancelled_ReplayDoesNotClaimCurrentStage()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { StageUnavailable = true, StageCommittedOnUnavailable = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("button.btn-primary").Click();
        var key = service.StageKey;
        service.PendingId = null;
        service.Version++;
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        service.StageUnavailable = false;
        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StageCount));
        Assert.AreEqual(key, service.StageKey);
        StringAssert.Contains(cut.Find("[role='status'].camera-banner").TextContent,
            "current selection does not match its receipt", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find("[role='status'].camera-banner").TextContent.Contains("Rig staged for restart", StringComparison.Ordinal));
        StringAssert.Contains(cut.Find("[aria-label='Rig selection']").TextContent, "No restart staged", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("[aria-label='Rig selection']").TextContent, "Installed rig", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UncertainCancel_Refresh_RetriesIdenticalKeyAndVersion()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { PendingId = "rig-v1", CancelUnavailable = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Cancel pending", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.CancelCount));
        var key = service.CancelKey;
        service.Version = 2;
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        Assert.AreEqual(1, service.CancelCount);
        StringAssert.Contains(cut.Markup, "uncertain outcome", StringComparison.Ordinal);
        cut.FindAll("button").Single(b => b.TextContent == "Retry same cancel request").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.CancelCount));
        Assert.AreEqual(key, service.CancelKey);
        Assert.AreEqual(1L, service.CancelVersion);
        cut.FindAll("button").Single(b => b.TextContent == "Abandon cancel retry").Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Cancel pending", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.AreEqual(3, service.CancelCount));
        Assert.AreNotEqual(key, service.CancelKey);
        Assert.AreEqual(2L, service.CancelVersion);
    }

    [TestMethod]
    public void UncertainCancel_CommittedThenRestaged_ReplayDoesNotClaimPendingCancelled()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { PendingId = "rig-v1", CancelUnavailable = true, CancelCommittedOnUnavailable = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent == "Cancel pending restart").Click();
        var key = service.CancelKey;
        service.PendingId = "rig-v1";
        service.Version++;
        cut.FindAll("button").Single(b => b.TextContent == "Refresh").Click();
        service.CancelUnavailable = false;
        cut.FindAll("button").Single(b => b.TextContent == "Retry same cancel request").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.CancelCount));
        Assert.AreEqual(key, service.CancelKey);
        Assert.AreEqual(1L, service.CancelVersion);
        StringAssert.Contains(cut.Find("[role='status'].camera-banner").TextContent,
            "current selection does not match its receipt", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find("[role='status'].camera-banner").TextContent.Contains("Pending restart cancelled", StringComparison.Ordinal));
        StringAssert.Contains(cut.Find("[aria-label='Rig selection']").TextContent, "Pending restart", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find("[aria-label='Rig selection']").TextContent.Contains("No restart staged", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EquipmentEditor_UsesBasisAndRejectsInvalidTypedField()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("[aria-label='Equipment editor'] select")[0].Change("optics");
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("optics-v1");
        cut.WaitForElement("[aria-label='Equipment editor'] input[value='50']");
        cut.FindAll("[aria-label='Equipment editor'] label")
            .Single(l => l.TextContent.StartsWith("Focal length", StringComparison.Ordinal)).QuerySelector("input")!.Change("wide");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create duplicate equipment", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Focal length", StringComparison.Ordinal));
        Assert.AreEqual(0, service.SaveCount);
    }

    [TestMethod]
    public void InstalledEquipment_OnlyAllowsDuplicate_AndSubmitsNewDefinition()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("[aria-label='Equipment editor'] select")[0].Change("optics");
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("optics-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Installed basis is immutable", StringComparison.Ordinal));
        var mode = cut.FindAll("[aria-label='Equipment editor'] select")[2];
        Assert.AreEqual("true", mode.QuerySelector("option")!.GetAttribute("value"));
        Assert.AreEqual(1, mode.QuerySelectorAll("option").Length);
        Assert.AreEqual("Installed optics copy", cut.Find("[aria-label='Equipment editor'] label input[maxlength='128']").GetAttribute("value"));
        Assert.AreEqual(0, service.SaveCount);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create duplicate equipment", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.SaveCount));
        Assert.AreEqual("Installed optics copy", service.SavedName);
        Assert.IsNull(service.SavedDefinitionId);
        Assert.IsNull(service.SavedExpectedRevisionId);
    }

    [TestMethod]
    public void InstalledEquipment_ExistingDuplicateName_RequiresDifferentName()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("[aria-label='Equipment editor'] select")[0].Change("optics");
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("optics-v1");
        cut.Find("[aria-label='Equipment editor'] label input[maxlength='128']").Change("Installed optics");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create duplicate equipment", StringComparison.Ordinal)).Click();
        StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Enter a different name", StringComparison.Ordinal);
        Assert.AreEqual(0, service.SaveCount);
    }

    [TestMethod]
    public void InstalledEquipment_ProposedNameSkipsExistingCopy_WithoutSavingOnLoad()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { ExistingOpticsCopy = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("[aria-label='Equipment editor'] select")[0].Change("optics");
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("optics-v1");
        Assert.AreEqual("Installed optics copy 2", cut.Find("[aria-label='Equipment editor'] label input[maxlength='128']").GetAttribute("value"));
        Assert.AreEqual(0, service.SaveCount);
    }

    [TestMethod]
    public void InstalledEquipment_ConcurrentNameConflict_ShowsActionableMessage()
    {
        using var context = new BunitContext();
        var service = new FakeRigService { SaveConflict = true };
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("[aria-label='Equipment editor'] select")[0].Change("optics");
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("optics-v1");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create duplicate equipment", StringComparison.Ordinal)).Click();
        StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Refresh the inventory and choose a different name", StringComparison.Ordinal);
        Assert.AreEqual(1, service.SaveCount);
    }

    [TestMethod]
    public void DetailMetadata_ControlsRevisionAndShowsExactCameraModels()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Model: ASI676MC (model not flagged unvalidated)", StringComparison.Ordinal));
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("camera-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Basis expected model: ASI676MC", StringComparison.Ordinal));
        Assert.AreEqual(1, cut.FindAll("[aria-label='Equipment editor'] select")[2].QuerySelectorAll("option").Length);
        Assert.IsFalse(cut.Markup.Contains("libraryPathEnvironmentVariable", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void NonInstalledOlderRevision_CannotBeRevised()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("camera-v2");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Only the latest editable revision", StringComparison.Ordinal));
        Assert.AreEqual(1, cut.FindAll("[aria-label='Equipment editor'] select")[2].QuerySelectorAll("option").Length);
    }

    [TestMethod]
    public void SelectingHistoricalRig_ShowsItsExactUnvalidatedModel()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.Find("[aria-label='Rig preview and selection'] select").Change("older-v3");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Model: ASI120MM (unvalidated model)", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "Model: ASI676MC (model not flagged unvalidated)", StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains("libraryPathEnvironmentVariable", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DelayedCatalogCamera_AfterSelectingHistory_DoesNotReplaceSelectedModel()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        var staleResult = await service.GetEquipmentAsync("camera-v1", CancellationToken.None).ConfigureAwait(false);
        var delayed = service.DeferEquipment("camera-v1");
        var selection = cut.Find("[aria-label='Rig preview and selection'] select").ChangeAsync(new ChangeEventArgs { Value = "rig-v1" });
        Assert.IsFalse(selection.IsCompleted);
        await cut.Find("[aria-label='Rig preview and selection'] select").ChangeAsync(new ChangeEventArgs { Value = "older-v3" }).ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[aria-label='Rig preview and selection'] article:last-child").TextContent,
            "ASI120MM (unvalidated model)", StringComparison.Ordinal));
        delayed.SetResult(staleResult);
        await selection.ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[aria-label='Rig preview and selection'] article:last-child").TextContent,
            "ASI120MM (unvalidated model)", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DelayedHistoricalCamera_AfterRefresh_DoesNotReplaceRefreshedModel()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        var cameraResult = await service.GetEquipmentAsync("camera-v2", CancellationToken.None).ConfigureAwait(false);
        var staleResult = await service.GetEquipmentAsync("camera-v1", CancellationToken.None).ConfigureAwait(false);
        var delayed = service.DeferEquipment("camera-v2");
        var selection = cut.Find("[aria-label='Rig preview and selection'] select").ChangeAsync(new ChangeEventArgs { Value = "older-v3" });
        Assert.IsFalse(selection.IsCompleted);
        var refreshed = service.DeferEquipment("camera-v2");
        var refresh = cut.FindAll("button").Single(b => b.TextContent == "Refresh").ClickAsync(new MouseEventArgs());
        refreshed.SetResult(cameraResult);
        await refresh.ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[aria-label='Rig preview and selection'] article:last-child").TextContent,
            "ASI120MM (unvalidated model)", StringComparison.Ordinal));
        delayed.SetResult(staleResult);
        await selection.ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[aria-label='Rig preview and selection'] article:last-child").TextContent,
            "ASI120MM (unvalidated model)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EditingComposerSelection_InvalidatesAcknowledgedPreview()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Preview against", StringComparison.Ordinal)).Click();
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.FindAll("[aria-label='Rig composer'] select")[1].Change("optics-v1");
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsFalse(cut.Markup.Contains("Contract preview passed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CustomZwoDuplicate_RequiresRiskAcknowledgementAndSubmitsModelFlag()
    {
        using var context = new BunitContext();
        var service = new FakeRigService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        var cut = context.Render<CameraRigPage>();
        cut.FindAll("[aria-label='Equipment editor'] select")[1].Change("camera-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Custom ZWO expected model", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "ASI120MM Mini", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "SDK V1.41", StringComparison.Ordinal);
        cut.FindAll("[aria-label='Equipment editor'] label")
            .Single(l => l.TextContent.StartsWith("Custom ZWO expected model", StringComparison.Ordinal))
            .QuerySelector("input")!.Change("ASI120MM Mini");
        StringAssert.Contains(cut.Markup, "Expected model for this duplicate: ASI120MM Mini", StringComparison.Ordinal);
        var save = cut.FindAll("button").Single(b => b.TextContent.Contains("Create duplicate equipment", StringComparison.Ordinal));
        save.Click();
        Assert.AreEqual(0, service.SaveCount);
        StringAssert.Contains(cut.Find("[role='alert']").TextContent, "acknowledgement", StringComparison.Ordinal);
        cut.FindAll("[aria-label='Equipment editor'] label")
            .Single(l => l.TextContent.Contains("unvalidated camera model", StringComparison.Ordinal))
            .QuerySelector("input")!.Change(true);
        save.Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.SaveCount));
        Assert.IsNull(service.SavedDefinitionId);
        Assert.AreEqual("camera-v1", service.SavedBasisRevisionId);
        Assert.AreEqual("ASI120MM Mini", service.SavedDefinition.GetProperty("expectedModel").GetString());
        Assert.IsTrue(service.SavedDefinition.GetProperty("useUnvalidatedCameraAtOwnRisk").GetBoolean());
        Assert.IsFalse(service.SavedDefinition.GetProperty("module").TryGetProperty("options", out _));
    }

    [TestMethod]
    public void UnauthorizedRead_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(new FakeRigService { Unauthorized = true });
        _ = context.Render<CameraRigPage>();
        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    private sealed class FakeRigService : ICameraAgentNamedRigUiService
    {
        private readonly NamedRigRevision _rig;
        private readonly NamedRigInventory _inventory;
        internal bool VirtualOnly { get; set; }
        internal bool ExistingOpticsCopy { get; set; }
        internal bool SaveConflict { get; set; }
        internal string? StarterTemplate { get; private set; }
        internal bool Valid { get; set; } = true;
        internal string ScheduleId { get; set; } = "schedule-v1";
        internal string ScheduleHash { get; set; } = new('A', 64);
        internal string? StagedScheduleId { get; private set; }
        internal string? StagedScheduleHash { get; private set; }
        internal int HistoryCalls { get; private set; }
        internal bool DeepHistory { get; set; }
        internal bool Unauthorized { get; set; }
        internal bool Acknowledged { get; private set; }
        internal int StageCount { get; private set; }
        internal bool StageUnavailable { get; set; }
        internal bool StageCommittedOnUnavailable { get; set; }
        internal bool CancelUnavailable { get; set; }
        internal bool CancelCommittedOnUnavailable { get; set; }
        internal string? PendingId { get; set; }
        internal string? PendingFailure { get; set; }
        internal string? ActiveFailure { get; set; }
        internal long Version { get; set; } = 1;
        internal int CancelCount { get; private set; }
        internal string? CancelKey { get; private set; }
        internal long CancelVersion { get; private set; }
        internal string? StageKey { get; private set; }
        internal long StageVersion { get; private set; }
        internal TaskCompletionSource<OperatorUiResult<NamedRigPreview>>? DeferredPreview { get; set; }
        internal string? SavedProfileId { get; private set; }
        internal string? SavedProfileName { get; private set; }
        private readonly List<NamedRigProfile> _profiles = [new("rig", "Installed rig"), new("other", "Other rig")];
        internal int SaveCount { get; private set; }
        internal string? SavedDefinitionId { get; private set; }
        internal string? SavedName { get; private set; }
        internal string? SavedExpectedRevisionId { get; private set; }
        internal string? SavedBasisRevisionId { get; private set; }
        internal JsonElement SavedDefinition { get; private set; }
        internal string? ComposedCameraId { get; private set; }
        private readonly Dictionary<string, Queue<TaskCompletionSource<OperatorUiResult<NamedEquipmentDetail>>>> _deferredEquipment = [];
        private readonly Dictionary<string, Queue<TaskCompletionSource<OperatorUiResult<NamedRigHistoryPage>>>> _deferredHistory = [];
        private NamedEquipmentDefinition? _savedEquipment;
        private readonly Dictionary<string, NamedRigStageReceipt> _stageReceipts = [];
        private readonly Dictionary<string, NamedRigStageReceipt> _cancelReceipts = [];

        internal TaskCompletionSource<OperatorUiResult<NamedEquipmentDetail>> DeferEquipment(string revisionId)
        {
            var deferred = new TaskCompletionSource<OperatorUiResult<NamedEquipmentDetail>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_deferredEquipment.TryGetValue(revisionId, out var queue))
                _deferredEquipment[revisionId] = queue = new();
            queue.Enqueue(deferred);
            return deferred;
        }

        internal TaskCompletionSource<OperatorUiResult<NamedRigHistoryPage>> DeferHistory(string profileId)
        {
            var deferred = new TaskCompletionSource<OperatorUiResult<NamedRigHistoryPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_deferredHistory.TryGetValue(profileId, out var queue))
                _deferredHistory[profileId] = queue = new();
            queue.Enqueue(deferred);
            return deferred;
        }

        internal FakeRigService()
        {
            var profile = SchedulePageTests.Profile();
            _rig = new NamedRigRevision("rig-v1", "rig", 1, "camera-v1", "optics-v1", "mount-v1",
                profile.Module, profile.Rig, "installed-schedule");
            _inventory = new NamedRigInventory(_profiles,
                 [new NamedEquipmentDefinition("camera", "camera", "Installed camera", 1, "camera-v1", true, false),
                  new NamedEquipmentDefinition("optics", "optics", "Installed optics", 1, "optics-v1", true, false),
                  new NamedEquipmentDefinition("mount", "mount", "Installed mount", 1, "mount-v1", true, false),
                  new NamedEquipmentDefinition("camera-v2", "camera", "Older camera", 1, "camera-v2", false, false)]);
        }

        public ValueTask<OperatorUiResult<NamedRigUiCatalog>> GetAsync(CancellationToken token)
            => ValueTask.FromResult(Unauthorized
                ? OperatorUiResult<NamedRigUiCatalog>.Failure(OperatorUiResultKind.Unauthorized, "Denied")
                : OperatorUiResult<NamedRigUiCatalog>.Success(new(new("rig-v1", PendingId, Version),
                    [_rig, _rig with { RevisionId = "other-v2", ProfileId = "other", RevisionNumber = 2 }],
                    PendingFailure, ActiveFailure)));
        public ValueTask<OperatorUiResult<NamedRigInventory>> GetInventoryAsync(CancellationToken token)
            => ValueTask.FromResult(OperatorUiResult<NamedRigInventory>.Success(VirtualOnly
                ? new(_inventory.Profiles, StarterTemplate is null
                    ? [new NamedEquipmentDefinition("virtual", "camera", "Virtual camera", 1, "camera-v1", true, false),
                       .. _inventory.Equipment.Where(e => e.Kind != "camera")]
                    : [.. _inventory.Equipment, new NamedEquipmentDefinition("starter", "camera", "ASI676MC camera", 1, "starter-v1"), .. (_savedEquipment is null ? [] : new[] { _savedEquipment })])
                : new(_inventory.Profiles, [.. _inventory.Equipment,
                    .. (ExistingOpticsCopy ? new[] { new NamedEquipmentDefinition("optics-copy", "optics", "Installed optics copy", 1, "optics-copy-v1") } : []),
                    .. (_savedEquipment is null ? [] : new[] { _savedEquipment })])));
        public ValueTask<OperatorUiResult<NamedRigHistoryPage>> GetHistoryAsync(string profileId, int limit,
            long? beforeRevisionNumber, long? version, CancellationToken token)
        {
            HistoryCalls++;
            if (_deferredHistory.TryGetValue(profileId, out var queue) && queue.Count > 0)
                return new(queue.Dequeue().Task);
            if (profileId != "rig")
                return ValueTask.FromResult(OperatorUiResult<NamedRigHistoryPage>.Success(new(1, [], null)));
            if (DeepHistory)
            {
                var first = beforeRevisionNumber is null ? 150 : beforeRevisionNumber.Value - 1;
                return ValueTask.FromResult(OperatorUiResult<NamedRigHistoryPage>.Success(new(3,
                    Enumerable.Range(0, 50).Select(offset => new NamedRigHistoryEntry(
                        first - offset == 1 ? "older-v1" : $"older-v{first - offset}", profileId,
                    first - offset, "camera-v1", "optics-v1", "mount-v1", null)).ToArray(),
                    first == 50 ? null : first - 49)));
            }
            return ValueTask.FromResult(OperatorUiResult<NamedRigHistoryPage>.Success(beforeRevisionNumber is null
                ? new(3, [new("older-v3", profileId, 3, "camera-v2", "optics-v1", "mount-v1", null)], 3)
                : new(3, [new("older-v1", profileId, 1, "camera-v1", "optics-v1", "mount-v1", null)], null)));
        }
        public ValueTask<OperatorUiResult<NamedEquipmentDetail>> GetEquipmentAsync(string revisionId, CancellationToken token)
        {
            if (_deferredEquipment.TryGetValue(revisionId, out var queue) && queue.Count > 0)
                return new(queue.Dequeue().Task);
            return ValueTask.FromResult(OperatorUiResult<NamedEquipmentDetail>.Success(revisionId == _savedEquipment?.RevisionId
                ? new(_savedEquipment.DefinitionId, _savedEquipment.Kind, _savedEquipment.DisplayName, 2,
                    revisionId, SavedDefinition, false, true)
                : revisionId == "starter-v1"
                ? new("starter", "camera", "ASI676MC camera", 1, revisionId,
                    new
                    {
                        module = new { type = "ZwoAsi" },
                        sensor = _rig.Rig.Sensor,
                        readout = _rig.Rig.Readout,
                        expectedModel = "ASI676MC",
                        useUnvalidatedCameraAtOwnRisk = false
                    }, false, true)
                : revisionId == "camera-v1"
                ? new("camera", "camera", "Installed camera", 1, revisionId,
                      new
                      {
                          module = new { type = VirtualOnly ? "VirtualSky" : "ZwoAsi" },
                          sensor = _rig.Rig.Sensor with { Name = "Historical sensor" },
                          readout = _rig.Rig.Readout,
                          expectedModel = "ASI676MC",
                          useUnvalidatedCameraAtOwnRisk = false
                      }, true, false)
                : revisionId == "camera-v2"
                    ? new("camera-v2", "camera", "New camera", 1, revisionId,
                         new
                         {
                             module = new { type = "ZwoAsi" },
                             sensor = _rig.Rig.Sensor,
                             readout = _rig.Rig.Readout,
                             expectedModel = "ASI120MM",
                             useUnvalidatedCameraAtOwnRisk = true
                         }, false, false)
                : revisionId == "optics-v2"
                     ? new("optics", "optics", "Installed optics", 2, revisionId, _rig.Rig.Optics, true, false)
                 : new("optics", "optics", "Installed optics", 1, revisionId, _rig.Rig.Optics, true, false)));
        }
        public ValueTask<OperatorUiResult<NamedEquipmentDefinition>> SaveEquipmentAsync(string? definitionId, string kind,
            string name, JsonElement definition, string? basisRevisionId, string? expectedRevisionId, CancellationToken token)
        {
            SaveCount++;
            SavedDefinitionId = definitionId;
            SavedName = name;
            SavedExpectedRevisionId = expectedRevisionId;
            SavedBasisRevisionId = basisRevisionId;
            SavedDefinition = definition;
            if (SaveConflict)
                return ValueTask.FromResult(OperatorUiResult<NamedEquipmentDefinition>.Failure(
                    OperatorUiResultKind.Conflict, "A rig or equipment name already exists."));
            _savedEquipment = new(kind, kind, name, 2, kind == "camera" ? "camera-v3" : "optics-v2");
            return ValueTask.FromResult(OperatorUiResult<NamedEquipmentDefinition>.Success(_savedEquipment));
        }
        public ValueTask<OperatorUiResult<NamedEquipmentDefinition>> CreateZwoStarterAsync(string templateId, string name,
            CancellationToken token)
        {
            StarterTemplate = templateId;
            return ValueTask.FromResult(OperatorUiResult<NamedEquipmentDefinition>.Success(
                new("starter", "camera", name, 1, "starter-v1")));
        }
        public ValueTask<OperatorUiResult<NamedRigProfile>> SaveProfileAsync(string? profileId, string name, CancellationToken token)
        {
            SavedProfileId = profileId;
            SavedProfileName = name;
            var saved = new NamedRigProfile(profileId ?? "new", name);
            _profiles.RemoveAll(p => p.ProfileId == saved.ProfileId);
            _profiles.Add(saved);
            return ValueTask.FromResult(OperatorUiResult<NamedRigProfile>.Success(saved));
        }
        public ValueTask<OperatorUiResult<NamedRigRevision>> ComposeAsync(string profileId, string cameraId, string opticsId,
            string mountId, CancellationToken token)
        {
            ComposedCameraId = cameraId;
            return ValueTask.FromResult(OperatorUiResult<NamedRigRevision>.Success(_rig));
        }
        public ValueTask<OperatorUiResult<NamedRigPreview>> PreviewAsync(string revisionId, CancellationToken token)
            => DeferredPreview is { } deferred ? new(deferred.Task) : ValueTask.FromResult(
                OperatorUiResult<NamedRigPreview>.Success(new(ScheduleId, ScheduleHash, revisionId,
                    Valid, Valid ? null : "rig.readout", false)));
        public ValueTask<OperatorUiResult<NamedRigStageReceipt>> StageAsync(string revisionId, long version, string key,
            bool acknowledgeUnvalidated, string expectedScheduleRevisionId, string expectedScheduleProfileSha256,
            CancellationToken token)
        {
            StageCount++;
            StageKey = key;
            StageVersion = version;
            Acknowledged = acknowledgeUnvalidated;
            StagedScheduleId = expectedScheduleRevisionId;
            StagedScheduleHash = expectedScheduleProfileSha256;
            if (_stageReceipts.TryGetValue(key, out var previous))
                return ValueTask.FromResult(OperatorUiResult<NamedRigStageReceipt>.Success(previous));
            if (StageUnavailable)
            {
                if (StageCommittedOnUnavailable)
                {
                    PendingId = revisionId;
                    Version = version + 1;
                    _stageReceipts[key] = new("receipt", revisionId, Version, "restart_required", acknowledgeUnvalidated);
                }
                return ValueTask.FromResult(OperatorUiResult<NamedRigStageReceipt>.Failure(
                    OperatorUiResultKind.Unavailable, "Transport unavailable"));
            }
            PendingId = revisionId;
            Version = version + 1;
            var receipt = new NamedRigStageReceipt("receipt", revisionId, Version, "restart_required", acknowledgeUnvalidated);
            _stageReceipts[key] = receipt;
            return ValueTask.FromResult(OperatorUiResult<NamedRigStageReceipt>.Success(receipt));
        }
        public ValueTask<OperatorUiResult<NamedRigStageReceipt>> CancelAsync(string revisionId, long version, string key,
            CancellationToken token)
        {
            CancelCount++;
            CancelKey = key;
            CancelVersion = version;
            if (_cancelReceipts.TryGetValue(key, out var previous))
                return ValueTask.FromResult(OperatorUiResult<NamedRigStageReceipt>.Success(previous));
            if (CancelUnavailable && !CancelCommittedOnUnavailable)
                return ValueTask.FromResult(OperatorUiResult<NamedRigStageReceipt>.Failure(
                    OperatorUiResultKind.Unavailable, "Transport unavailable"));
            PendingId = null;
            Version = version + 1;
            var receipt = new NamedRigStageReceipt("receipt", revisionId, Version, "cancelled", true);
            _cancelReceipts[key] = receipt;
            return ValueTask.FromResult(CancelUnavailable
                ? OperatorUiResult<NamedRigStageReceipt>.Failure(OperatorUiResultKind.Unavailable, "Transport unavailable")
                : OperatorUiResult<NamedRigStageReceipt>.Success(receipt));
        }
    }
}
