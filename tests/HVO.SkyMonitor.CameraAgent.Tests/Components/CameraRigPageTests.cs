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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        var service = new FakeRigService { VirtualOnly = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement("#rig-add-zwo").Click();
        Assert.AreEqual("Add ZWO camera", cut.Find("#rig-dialog-heading").TextContent);
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("asi676mc", service.StarterTemplate);
            Assert.AreEqual("Edit camera", cut.Find("#rig-dialog-heading").TextContent);
            Assert.AreEqual("starter-v1", cut.FindAll("dialog select")[0].GetAttribute("value"));
            StringAssert.Contains(cut.Find("dialog").TextContent, "Basis expected model: ASI676MC", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("dialog .rig-message[role='status']").TextContent, "ZWO camera created", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("libraryPathEnvironmentVariable", StringComparison.Ordinal));
        });
        Assert.IsEmpty(cut.FindAll("#rig-add-zwo"));
        Assert.AreEqual("starter-v1", cut.Find("#rig-camera-select").GetAttribute("value"));
    }

    [TestMethod]
    public void VirtualSkyStarter_CustomCameraDuplicate_SelectsSavedCameraForComposition()
    {
        var service = new FakeRigService { VirtualOnly = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-add-zwo").Click();
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => Assert.AreEqual("starter-v1", cut.Find("#rig-camera-select").GetAttribute("value")));
        EditorSelect(cut, "Save mode").Change("duplicate");
        EditorInput(cut, "Equipment name").Change("Custom ASI120MM Mini");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("dialog").TextContent, "Custom ZWO expected model", StringComparison.Ordinal));
        EditorInput(cut, "Custom ZWO expected model").Change("ASI120MM Mini");
        DialogCheck(cut, "unvalidated camera model").Change(true);
        Assert.AreEqual("Create duplicate equipment", DialogPrimary(cut).TextContent);
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            Assert.AreEqual("camera-v3", cut.Find("#rig-camera-select").GetAttribute("value"));
            Assert.AreEqual("Custom ASI120MM Mini (v2)", cut.Find("#rig-camera-select option[value='camera-v3']").TextContent);
            StringAssert.Contains(cut.Find(".rig-message[role='status']").TextContent, "Equipment revision saved", StringComparison.Ordinal);
        });
        Assert.IsTrue(FocusRequested(context, "rig-camera-select"));
        ClickButton(cut, "Compose immutable revision");
        cut.WaitForAssertion(() => Assert.AreEqual("camera-v3", service.ComposedCameraId));
    }

    [TestMethod]
    public void Render_UsesNamedRigInventoryWithoutScheduleEditor()
    {
        using var context = CreateContext(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement("[aria-label='Rig composer']");
        Assert.AreEqual("None", SelectionFact(cut, "Pending restart"));
        Assert.AreEqual("Restart", SelectionFact(cut, "Apply boundary"));
        Assert.AreEqual("Capture schedule", cut.Find("[aria-label='Rig selection'] a[href='/operations/schedule']").TextContent);
        StringAssert.Contains(cut.Markup, "Compare &amp; stage", StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains("Night gain", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Capture interval", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll("dialog"));
        var module = cut.Find("[aria-labelledby='rig-module-heading']").TextContent;
        StringAssert.Contains(module, "Held in module options; not shown", StringComparison.Ordinal);
        StringAssert.Contains(module, "Not reported", StringComparison.Ordinal);
        Assert.IsNotNull(cut.Find("[aria-labelledby='rig-optics-heading'] .ops-geometry-visual[role='img']"));
        StringAssert.Contains(cut.Find("[aria-labelledby='rig-capabilities-heading']").TextContent,
            "A capability inventory is not reported", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_ShowsSelectionFactsProvenanceAndCollapsedAdvancedReadout()
    {
        using var context = CreateContext(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-camera-edit").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("camera-v1", cut.FindAll("dialog select")[0].GetAttribute("value")));
        Assert.IsFalse(cut.Find(".rig-advanced").HasAttribute("open"));
        DialogCheck(cut, "Sensor readout").Change(true);
        StringAssert.Contains(cut.Find(".rig-advanced").TextContent, "ROI width", StringComparison.Ordinal);
        ClickButton(cut, "Cancel");
        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsTrue(FocusRequested(context, "rig-camera-edit"));
        StringAssert.Contains(cut.Find("[aria-label='Rig selection'] strong").TextContent, "Installed rig revision 1 is active", StringComparison.Ordinal);
        CollectionAssert.Contains(cut.FindAll(".rig-compare tbody th").Select(th => th.TextContent).ToList(), "Readout");
        StringAssert.Contains(cut.Find(".rig-provenance").TextContent, "older-v3", StringComparison.Ordinal);
        cut.Find("#rig-revision-select").Change("rig-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".rig-provenance").TextContent,
            "installed-schedule", StringComparison.Ordinal));
        Assert.IsNotEmpty(cut.FindAll("[aria-label='Rig revision history'] tbody tr"));
    }

    [TestMethod]
    public void PendingFailure_RefreshShowsFailureAndCancellationWithoutInternalDetails()
    {
        var service = new FakeRigService { PendingId = "rig-v1" };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        Assert.IsEmpty(cut.FindAll("[aria-label='Rig runtime failure']"));
        Assert.AreEqual("Revision 1", SelectionFact(cut, "Pending restart"));
        StringAssert.Contains(cut.Find("[aria-label='Pending restart']").TextContent, "unvalidated at runtime", StringComparison.Ordinal);
        service.PendingFailure = "Pending rig failed to initialize.";
        ClickButton(cut, "Refresh");
        var alert = cut.Find("[aria-label='Rig runtime failure']");
        StringAssert.Contains(alert.TextContent, "Cancel the pending restart", StringComparison.Ordinal);
        StringAssert.Contains(alert.TextContent, "correct the hardware and restart", StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains("pending-command-secret", StringComparison.Ordinal));
        Assert.IsFalse(cut.Find("[aria-label='Pending restart']").TextContent.Contains("unvalidated at runtime", StringComparison.Ordinal));
        ClickButton(cut, "Cancel pending restart");
        Assert.AreEqual(1, service.CancelCount);
    }

    [TestMethod]
    public void ActiveFailure_RefreshShowsCaptureUnavailableThenClears()
    {
        var service = new FakeRigService { ActiveFailure = "Active camera is unavailable for capture." };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        StringAssert.Contains(cut.Find("[aria-label='Rig runtime failure']").TextContent,
            "Check the camera hardware and restart", StringComparison.Ordinal);
        service.ActiveFailure = null;
        ClickButton(cut, "Refresh");
        Assert.IsEmpty(cut.FindAll("[aria-label='Rig runtime failure']"));
    }

    [TestMethod]
    public void UnauthorizedRuntimeRead_DoesNotShowFailure()
    {
        using var context = CreateContext(new FakeRigService { Unauthorized = true, PendingFailure = "Pending rig failed to initialize." });
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
            Mock.Of<IHostApplicationLifetime>(), new ConfigurationBuilder().Build(), TimeProvider.System,
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
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Runtime verified: no", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("#rig-stage").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.StageCount));
        Assert.IsTrue(service.Acknowledged);
        Assert.AreEqual("schedule-v1", service.StagedScheduleId);
        Assert.AreEqual(service.ScheduleHash, service.StagedScheduleHash);
        StringAssert.Contains(cut.Markup, "Runtime compatibility is not yet verified", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Stage_SuccessfulCommandButRefreshFails_DoesNotClaimStagedSelection(bool failInventory)
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        if (failInventory) service.FailNextInventory = true;
        else service.FailNextGet = true;

        cut.Find("#rig-stage").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, service.StageCount);
            var banner = cut.Find("[role='alert'].rig-message").TextContent;
            StringAssert.Contains(banner, "current selection could not be verified", StringComparison.Ordinal);
            Assert.IsFalse(banner.Contains("refreshed", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(banner.Contains("Rig staged for restart", StringComparison.Ordinal));
            Assert.IsFalse(banner.Contains("Pending restart cancelled", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Cancel_SuccessfulCommandButRefreshFails_DoesNotClaimCancelledSelection(bool failInventory)
    {
        var service = new FakeRigService { PendingId = "rig-v1" };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        if (failInventory) service.FailNextInventory = true;
        else service.FailNextGet = true;

        ClickButton(cut, "Cancel pending restart");

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, service.CancelCount);
            var banner = cut.Find("[role='alert'].rig-message").TextContent;
            StringAssert.Contains(banner, "current selection could not be verified", StringComparison.Ordinal);
            Assert.IsFalse(banner.Contains("refreshed", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(banner.Contains("Pending restart cancelled", StringComparison.Ordinal));
            Assert.IsFalse(banner.Contains("Rig staged for restart", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void PreviewFailure_CannotBeAcknowledgedOrStaged()
    {
        var service = new FakeRigService { Valid = false };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Preview failed", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("[aria-label='Rig preview and selection'] input[type=checkbox]"));
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
        Assert.AreEqual(0, service.StageCount);
    }

    [TestMethod]
    public void ScheduleChangesAfterPreview_RequiresAnotherPreview()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        service.ScheduleId = "schedule-v2";
        cut.Find("#rig-stage").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[role='alert'].rig-message").TextContent, "Preview again", StringComparison.Ordinal));
        Assert.AreEqual(0, service.StageCount);
    }

    [TestMethod]
    public async Task Stage_DuringFreshPreview_IsSingleFlight()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        await PreviewButton(cut).ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true }).ConfigureAwait(false);
        service.DeferredPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cut.Find("#rig-stage").ClickAsync(new MouseEventArgs());
        Assert.IsFalse(first.IsCompleted);
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
        await cut.Find("#rig-stage").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        service.DeferredPreview.SetResult(OperatorUiResult<NamedRigPreview>.Success(
            new(service.ScheduleId, service.ScheduleHash, "older-v3", true, null, false)));
        await first.ConfigureAwait(false);
        Assert.AreEqual(1, service.StageCount);
    }

    [TestMethod]
    public async Task Stage_AcknowledgementChangesDuringFreshPreview_DoesNotSend()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        await PreviewButton(cut).ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true }).ConfigureAwait(false);
        service.DeferredPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = cut.Find("#rig-stage").ClickAsync(new MouseEventArgs());
        Assert.IsFalse(stage.IsCompleted);
        await cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false }).ConfigureAwait(false);
        service.DeferredPreview.SetResult(OperatorUiResult<NamedRigPreview>.Success(
            new(service.ScheduleId, service.ScheduleHash, "older-v3", true, null, false)));
        await stage.ConfigureAwait(false);
        Assert.AreEqual(0, service.StageCount);
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
    }

    [TestMethod]
    public async Task Stage_RevisionChangesDuringFreshPreview_DoesNotSend()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        await PreviewButton(cut).ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true }).ConfigureAwait(false);
        service.DeferredPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = cut.Find("#rig-stage").ClickAsync(new MouseEventArgs());
        Assert.IsFalse(stage.IsCompleted);
        await cut.Find("#rig-revision-select").ChangeAsync(new ChangeEventArgs { Value = "rig-v1" }).ConfigureAwait(false);
        service.DeferredPreview.SetResult(OperatorUiResult<NamedRigPreview>.Success(
            new(service.ScheduleId, service.ScheduleHash, "older-v3", true, null, false)));
        await stage.ConfigureAwait(false);
        Assert.AreEqual(0, service.StageCount);
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
    }

    [TestMethod]
    public void HistorySelector_LoadsOlderRevisionsAndCanPreviewThem()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.WaitForElement("#rig-revision-select option[value='older-v3']");
        ClickButton(cut, "Load older revisions");
        cut.WaitForElement("#rig-revision-select option[value='older-v1']");
        cut.Find("#rig-revision-select").Change("older-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Selected</th>", StringComparison.Ordinal));
        StringAssert.Contains(CompareValue(cut, "Sensor", "selected"), "Historical sensor", StringComparison.Ordinal);
        ClickPreview(cut);
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Contract preview passed", StringComparison.Ordinal));
        Assert.AreEqual(2, service.HistoryCalls);
        Assert.IsFalse(cut.Markup.Contains("ASI_SDK_PATH", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HistoryInspect_SelectsRevisionAndReturnsFocusToSelector()
    {
        using var context = CreateContext(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        ClickButton(cut, "Load older revisions");
        cut.WaitForElement("[aria-label='Inspect revision 1']");
        Assert.HasCount(2, cut.FindAll("[aria-label='Rig revision history'] tbody tr"));
        StringAssert.Contains(cut.Find("[aria-label='Rig revision history'] tbody tr").TextContent, "Composed", StringComparison.Ordinal);
        cut.Find("[aria-label='Inspect revision 1']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("older-v1", cut.Find("#rig-revision-select").GetAttribute("value"));
            StringAssert.Contains(CompareValue(cut, "Sensor", "selected"), "Historical sensor", StringComparison.Ordinal);
            Assert.IsTrue(FocusRequested(context, "rig-revision-select"));
        });
    }

    [TestMethod]
    public void SwitchingProfile_FromHistoricalRevision_SelectsNewProfileRevisionAndName()
    {
        using var context = CreateContext(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-revision-select").Change("older-v3");
        cut.WaitForAssertion(() => StringAssert.Contains(CompareValue(cut, "Model", "selected"), "ASI120MM (unvalidated model)", StringComparison.Ordinal));
        cut.Find("#rig-profile-select").Change("other");
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("other-v2", cut.Find("#rig-revision-select").GetAttribute("value"));
            StringAssert.Contains(cut.Find("[aria-label='Rig revision history']").TextContent, "Loaded revisions of Other rig", StringComparison.Ordinal);
            Assert.IsFalse(CompareValue(cut, "Model", "selected").Contains("ASI120MM", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task DelayedHistory_AfterRapidProfileSwitches_DoesNotReplaceLatestSelection()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        var delayed = service.DeferHistory("other");
        var other = cut.Find("#rig-profile-select").ChangeAsync(new ChangeEventArgs { Value = "other" });
        Assert.IsFalse(other.IsCompleted);
        await cut.Find("#rig-profile-select").ChangeAsync(new ChangeEventArgs { Value = "rig" }).ConfigureAwait(false);
        delayed.SetResult(OperatorUiResult<NamedRigHistoryPage>.Success(new(1,
            [new("foreign-v9", "other", 9, "camera-v2", "optics-v1", "mount-v1", null)], null)));
        await other.ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("rig", cut.Find("#rig-profile-select").GetAttribute("value"));
            Assert.AreEqual("older-v3", cut.Find("#rig-revision-select").GetAttribute("value"));
            Assert.IsFalse(cut.Markup.Contains("foreign-v9", StringComparison.Ordinal));
        });
        await cut.FindAll("button").Single(b => b.TextContent == "Load older revisions").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        cut.WaitForElement("#rig-revision-select option[value='older-v1']");
    }

    [TestMethod]
    public async Task DelayedHistory_AfterNewerRequestForSameProfile_DoesNotReplaceNewerPage()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        var delayed = service.DeferHistory("rig");
        var older = cut.FindAll("button").Single(b => b.TextContent == "Load older revisions").ClickAsync(new MouseEventArgs());
        Assert.IsFalse(older.IsCompleted);
        await cut.FindAll("button").Single(b => b.TextContent == "Refresh").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        delayed.SetResult(OperatorUiResult<NamedRigHistoryPage>.Success(new(3,
            [new("obsolete-v1", "rig", 1, "camera-v1", "optics-v1", "mount-v1", null)], null)));
        await older.ConfigureAwait(false);
        cut.WaitForAssertion(() =>
        {
            Assert.IsFalse(cut.Markup.Contains("obsolete-v1", StringComparison.Ordinal));
            Assert.IsNotEmpty(cut.FindAll("button").Where(b => b.TextContent == "Load older revisions"));
        });
    }

    [TestMethod]
    public void CreatedProfile_CanBeRenamedWithoutCreatingAnotherProfile()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-profile-new").Click();
        Assert.AreEqual("New named rig", cut.Find("#rig-dialog-heading").TextContent);
        Assert.IsTrue(DialogPrimary(cut).HasAttribute("disabled"));
        cut.Find("#rig-profile-name").Input("New rig");
        Assert.IsFalse(DialogPrimary(cut).HasAttribute("disabled"));
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => Assert.AreEqual("new", cut.Find("#rig-profile-select").GetAttribute("value")));
        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsNull(service.SavedProfileId);
        StringAssert.Contains(cut.Find(".rig-message[role='status']").TextContent, "Rig name saved", StringComparison.Ordinal);
        Assert.IsTrue(FocusRequested(context, "rig-profile-select"));

        cut.Find("#rig-profile-rename").Click();
        Assert.AreEqual("Rename rig", cut.Find("#rig-dialog-heading").TextContent);
        Assert.AreEqual("New rig", cut.Find("#rig-profile-name").GetAttribute("value"));
        cut.Find("#rig-profile-name").Input("Renamed rig");
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => Assert.AreEqual("Renamed rig", service.SavedProfileName));
        Assert.AreEqual("new", service.SavedProfileId);
        Assert.HasCount(3, cut.FindAll("#rig-profile-select option:not([value=''])"));
        Assert.AreEqual("Renamed rig", cut.Find("#rig-profile-select option[value='new']").TextContent);
    }

    [TestMethod]
    public async Task RigDialog_StaysOpenUntilAnInFlightSaveSettlesAsync()
    {
        var service = new FakeRigService { DeferredProfileSave = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        await cut.Find("#rig-profile-new").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("#rig-profile-name").InputAsync(new ChangeEventArgs { Value = "New rig" }).ConfigureAwait(false);

        var save = DialogPrimary(cut).ClickAsync(new MouseEventArgs());
        Assert.IsFalse(save.IsCompleted);
        Assert.IsTrue(cut.Find("dialog .dialog-header .icon-button").HasAttribute("disabled"));
        Assert.IsTrue(cut.FindAll("dialog footer button").Single(b => b.TextContent == "Cancel").HasAttribute("disabled"));
        // Escape raises the native cancel event, which the page routes to its dismiss handler.
        await cut.Find("dialog").TriggerEventAsync("oncancel", EventArgs.Empty).ConfigureAwait(false);
        Assert.HasCount(1, cut.FindAll("dialog"));
        Assert.AreEqual("New named rig", cut.Find("#rig-dialog-heading").TextContent);

        service.DeferredProfileSave.SetResult();
        await save.ConfigureAwait(false);

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        StringAssert.Contains(cut.Find(".rig-message[role='status']").TextContent, "Rig name saved", StringComparison.Ordinal);
        Assert.AreEqual("new", cut.Find("#rig-profile-select").GetAttribute("value"));
    }

    [TestMethod]
    public void InstalledRigName_IsFixedAndCancelRestoresFocus()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-profile-rename").Click();
        Assert.AreEqual("Rename rig", cut.Find("#rig-dialog-heading").TextContent);
        Assert.IsTrue(cut.Find("dialog fieldset").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find("dialog .dialog-note").TextContent, "This name is fixed.", StringComparison.Ordinal);
        Assert.IsTrue(DialogPrimary(cut).HasAttribute("disabled"));
        ClickButton(cut, "Cancel");
        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsTrue(FocusRequested(context, "rig-profile-rename"));
        Assert.IsNull(service.SavedProfileName);
    }

    [TestMethod]
    public void Refresh_RestoresSelectedRevisionBeyondFirstHundred_WithoutExposingSecrets()
    {
        var service = new FakeRigService { DeepHistory = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        for (var page = 0; page < 2; page++)
            ClickButton(cut, "Load older revisions");
        cut.Find("#rig-revision-select").Change("older-v1");
        cut.WaitForAssertion(() => StringAssert.Contains(CompareValue(cut, "Sensor", "selected"), "Historical sensor", StringComparison.Ordinal));
        ClickButton(cut, "Refresh");
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("older-v1", cut.Find("#rig-revision-select").GetAttribute("value"));
            StringAssert.Contains(CompareValue(cut, "Sensor", "selected"), "Historical sensor", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("ASI_SDK_PATH", StringComparison.Ordinal));
        });
        Assert.AreEqual(6, service.HistoryCalls);
    }

    [TestMethod]
    public void PreviewResult_AfterRevisionChanges_CannotEnableStage()
    {
        var service = new FakeRigService { DeferredPreview = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.Find("#rig-revision-select").Change("rig-v1");
        service.DeferredPreview.SetResult(OperatorUiResult<NamedRigPreview>.Success(
            new("schedule-v1", service.ScheduleHash, "older-v3", true, null, false)));
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Markup.Contains("Contract preview passed", StringComparison.Ordinal)));
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
    }

    [TestMethod]
    public void UncertainStage_RefreshAndScheduleChange_RetryOrAbandon()
    {
        var service = new FakeRigService { StageUnavailable = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("#rig-stage").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.StageCount));
        var key = service.StageKey;
        var version = service.StageVersion;
        var hash = service.StagedScheduleHash;
        service.ScheduleId = "schedule-v2";
        service.ScheduleHash = new('B', 64);
        service.Version = 2;
        ClickButton(cut, "Refresh");
        StringAssert.Contains(cut.Markup, "uncertain outcome", StringComparison.Ordinal);
        Assert.AreEqual(1, service.StageCount);
        cut.Find("#rig-stage").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StageCount));
        Assert.AreEqual(key, service.StageKey);
        Assert.AreEqual(version, service.StageVersion);
        Assert.AreEqual("schedule-v1", service.StagedScheduleId);
        Assert.AreEqual(hash, service.StagedScheduleHash);
        ClickButton(cut, "Abandon stage retry");
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
        ClickPreview(cut);
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("#rig-stage").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(3, service.StageCount));
        Assert.AreNotEqual(key, service.StageKey);
        Assert.AreEqual("schedule-v2", service.StagedScheduleId);
        Assert.AreEqual(service.ScheduleHash, service.StagedScheduleHash);
    }

    [TestMethod]
    public void UncertainSuccessfulStage_PendingRefresh_AllowsOnlyOriginalRequestReplay()
    {
        var service = new FakeRigService { StageUnavailable = true, StageCommittedOnUnavailable = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("#rig-stage").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.StageCount));
        var key = service.StageKey;
        var version = service.StageVersion;
        ClickButton(cut, "Refresh");
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#rig-stage").HasAttribute("disabled")));
        Assert.AreEqual("Retry same stage request", cut.Find("#rig-stage").TextContent);
        service.StageUnavailable = false;
        cut.Find("#rig-stage").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StageCount));
        Assert.AreEqual(key, service.StageKey);
        Assert.AreEqual(version, service.StageVersion);
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
        Assert.AreEqual("Confirm restart-only stage", cut.Find("#rig-stage").TextContent);
    }

    [TestMethod]
    public void UncertainStage_CommittedThenCancelled_ReplayDoesNotClaimCurrentStage()
    {
        var service = new FakeRigService { StageUnavailable = true, StageCommittedOnUnavailable = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        cut.Find("#rig-stage").Click();
        var key = service.StageKey;
        service.PendingId = null;
        service.Version++;
        ClickButton(cut, "Refresh");
        service.StageUnavailable = false;
        cut.Find("#rig-stage").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StageCount));
        Assert.AreEqual(key, service.StageKey);
        var banner = cut.Find("[role='status'].rig-message").TextContent;
        StringAssert.Contains(banner, "current selection does not match its receipt", StringComparison.Ordinal);
        Assert.IsFalse(banner.Contains("Rig staged for restart", StringComparison.Ordinal));
        Assert.AreEqual("None", SelectionFact(cut, "Pending restart"));
        StringAssert.Contains(cut.Find("[aria-label='Rig selection'] strong").TextContent, "Installed rig revision 1 is active", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UncertainCancel_Refresh_RetriesIdenticalKeyAndVersion()
    {
        var service = new FakeRigService { PendingId = "rig-v1", CancelUnavailable = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickButton(cut, "Cancel pending restart");
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.CancelCount));
        var key = service.CancelKey;
        service.Version = 2;
        ClickButton(cut, "Refresh");
        Assert.AreEqual(1, service.CancelCount);
        StringAssert.Contains(cut.Markup, "uncertain outcome", StringComparison.Ordinal);
        ClickButton(cut, "Retry same cancel request");
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.CancelCount));
        Assert.AreEqual(key, service.CancelKey);
        Assert.AreEqual(1L, service.CancelVersion);
        ClickButton(cut, "Abandon cancel retry");
        ClickButton(cut, "Cancel pending restart");
        cut.WaitForAssertion(() => Assert.AreEqual(3, service.CancelCount));
        Assert.AreNotEqual(key, service.CancelKey);
        Assert.AreEqual(2L, service.CancelVersion);
    }

    [TestMethod]
    public void UncertainCancel_CommittedThenRestaged_ReplayDoesNotClaimPendingCancelled()
    {
        var service = new FakeRigService { PendingId = "rig-v1", CancelUnavailable = true, CancelCommittedOnUnavailable = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        ClickButton(cut, "Cancel pending restart");
        var key = service.CancelKey;
        service.PendingId = "rig-v1";
        service.Version++;
        ClickButton(cut, "Refresh");
        service.CancelUnavailable = false;
        ClickButton(cut, "Retry same cancel request");
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.CancelCount));
        Assert.AreEqual(key, service.CancelKey);
        Assert.AreEqual(1L, service.CancelVersion);
        var banner = cut.Find("[role='status'].rig-message").TextContent;
        StringAssert.Contains(banner, "current selection does not match its receipt", StringComparison.Ordinal);
        Assert.IsFalse(banner.Contains("Pending restart cancelled", StringComparison.Ordinal));
        Assert.AreEqual("Revision 1", SelectionFact(cut, "Pending restart"));
        Assert.IsNotNull(cut.Find("[aria-label='Pending restart']"));
    }

    [TestMethod]
    public void EquipmentEditor_UsesBasisAndRejectsInvalidTypedField()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-optics-edit").Click();
        cut.WaitForElement("dialog input[value='50']");
        Assert.AreEqual("Edit optics", cut.Find("#rig-dialog-heading").TextContent);
        EditorInput(cut, "Focal length").Change("wide");
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("dialog .rig-message[role='alert']").TextContent, "Focal length", StringComparison.Ordinal));
        Assert.HasCount(1, cut.FindAll(".rig-message"));
        Assert.AreEqual(0, service.SaveCount);
    }

    [TestMethod]
    public void InstalledEquipment_OnlyAllowsDuplicate_AndSubmitsNewDefinition()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-optics-edit").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("dialog").TextContent, "Installed basis is immutable", StringComparison.Ordinal));
        var mode = cut.FindAll("dialog select")[1];
        Assert.AreEqual("duplicate", mode.GetAttribute("value"));
        Assert.AreEqual("duplicate", mode.QuerySelector("option")!.GetAttribute("value"));
        Assert.AreEqual(1, mode.QuerySelectorAll("option").Length);
        Assert.AreEqual("Installed optics copy", EditorInput(cut, "Equipment name").GetAttribute("value"));
        var projection = EditorSelect(cut, "Projection model");
        CollectionAssert.IsSubsetOf(Enum.GetNames<HVO.SkyMonitor.Astronomy.ProjectionModel>(),
            projection.QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToArray());
        Assert.AreEqual("Stereographic Fisheye", projection.QuerySelector("option[value='StereographicFisheye']")!.TextContent);
        projection.Change("StereographicFisheye");
        Assert.AreEqual(0, service.SaveCount);
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.SaveCount));
        Assert.AreEqual("Installed optics copy", service.SavedName);
        Assert.AreEqual("StereographicFisheye", service.SavedDefinition.GetProperty("projectionModel").GetString());
        Assert.IsNull(service.SavedDefinitionId);
        Assert.IsNull(service.SavedExpectedRevisionId);
        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            Assert.AreEqual("optics-v2", cut.Find("#rig-optics-select").GetAttribute("value"));
            Assert.IsTrue(FocusRequested(context, "rig-optics-select"));
        });
    }

    [TestMethod]
    public void NewEquipment_AlwaysDuplicatesEvenFromRevisableBasis()
    {
        var service = new FakeRigService { VirtualOnly = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-add-zwo").Click();
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => Assert.AreEqual("starter-v1", cut.Find("#rig-camera-select").GetAttribute("value")));
        ClickButton(cut, "Cancel");
        cut.Find("#rig-camera-new").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("ASI676MC camera copy", EditorInput(cut, "Equipment name").GetAttribute("value")));
        Assert.AreEqual("New camera", cut.Find("#rig-dialog-heading").TextContent);
        Assert.AreEqual("starter-v1", EditorSelect(cut, "Start from").GetAttribute("value"));
        Assert.IsFalse(cut.FindAll("dialog label").Any(l => l.TextContent.StartsWith("Save mode", StringComparison.Ordinal)));
        Assert.AreEqual("Create duplicate equipment", DialogPrimary(cut).TextContent);
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.SaveCount));
        Assert.IsNull(service.SavedDefinitionId);
        Assert.IsNull(service.SavedExpectedRevisionId);
        Assert.AreEqual("starter-v1", service.SavedBasisRevisionId);
        Assert.AreEqual("ASI676MC camera copy", service.SavedName);
    }

    [TestMethod]
    public void RevisableEquipment_SaveModeShowsSelectedModeAndSwapsProposedName()
    {
        var service = new FakeRigService { VirtualOnly = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-add-zwo").Click();
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => Assert.AreEqual("starter-v1", cut.Find("#rig-camera-select").GetAttribute("value")));
        ClickButton(cut, "Cancel");
        cut.Find("#rig-camera-edit").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("ASI676MC camera", EditorInput(cut, "Equipment name").GetAttribute("value")));
        Assert.AreEqual("revise", EditorSelect(cut, "Save mode").GetAttribute("value"));
        Assert.HasCount(2, EditorSelect(cut, "Save mode").QuerySelectorAll("option"));

        EditorSelect(cut, "Save mode").Change("duplicate");
        Assert.AreEqual("duplicate", EditorSelect(cut, "Save mode").GetAttribute("value"));
        Assert.AreEqual("ASI676MC camera copy", EditorInput(cut, "Equipment name").GetAttribute("value"));
        EditorSelect(cut, "Save mode").Change("revise");
        Assert.AreEqual("ASI676MC camera", EditorInput(cut, "Equipment name").GetAttribute("value"));

        EditorInput(cut, "Equipment name").Change("Roof camera");
        EditorSelect(cut, "Save mode").Change("duplicate");
        Assert.AreEqual("Roof camera", EditorInput(cut, "Equipment name").GetAttribute("value"));
        EditorSelect(cut, "Save mode").Change("revise");
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.SaveCount));
        Assert.AreEqual("starter", service.SavedDefinitionId);
        Assert.AreEqual("starter-v1", service.SavedExpectedRevisionId);
        Assert.AreEqual("Roof camera", service.SavedName);
    }

    [TestMethod]
    public void InstalledEquipment_ExistingDuplicateName_RequiresDifferentName()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-optics-edit").Click();
        cut.WaitForElement("dialog input[value='50']");
        EditorInput(cut, "Equipment name").Change("Installed optics");
        DialogPrimary(cut).Click();
        StringAssert.Contains(cut.Find("dialog [role='alert']").TextContent, "Enter a different name", StringComparison.Ordinal);
        Assert.AreEqual(0, service.SaveCount);
    }

    [TestMethod]
    public void InstalledEquipment_ProposedNameSkipsExistingCopy_WithoutSavingOnLoad()
    {
        var service = new FakeRigService { ExistingOpticsCopy = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-optics-edit").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("Installed optics copy 2", EditorInput(cut, "Equipment name").GetAttribute("value")));
        Assert.AreEqual(0, service.SaveCount);
    }

    [TestMethod]
    public void InstalledEquipment_ConcurrentNameConflict_ShowsActionableMessage()
    {
        var service = new FakeRigService { SaveConflict = true };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-optics-edit").Click();
        cut.WaitForElement("dialog input[value='50']");
        DialogPrimary(cut).Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("dialog [role='alert']").TextContent,
            "Refresh the inventory and choose a different name", StringComparison.Ordinal));
        Assert.AreEqual(1, service.SaveCount);
    }

    [TestMethod]
    public void DetailMetadata_ControlsRevisionAndShowsExactCameraModels()
    {
        using var context = CreateContext(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[aria-labelledby='rig-module-heading']").TextContent,
            "ASI676MC (model not flagged unvalidated)", StringComparison.Ordinal));
        cut.Find("#rig-camera-edit").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("dialog").TextContent, "Basis expected model: ASI676MC", StringComparison.Ordinal));
        Assert.AreEqual(1, cut.FindAll("dialog select")[1].QuerySelectorAll("option").Length);
        Assert.IsFalse(cut.Markup.Contains("libraryPathEnvironmentVariable", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void NonInstalledOlderRevision_CannotBeRevised()
    {
        using var context = CreateContext(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-camera-select").Change("camera-v2");
        cut.Find("#rig-camera-edit").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("dialog").TextContent, "Only the latest editable revision", StringComparison.Ordinal));
        Assert.AreEqual("camera-v2", cut.FindAll("dialog select")[0].GetAttribute("value"));
        Assert.AreEqual(1, cut.FindAll("dialog select")[1].QuerySelectorAll("option").Length);
    }

    [TestMethod]
    public void SelectingHistoricalRig_ShowsItsExactUnvalidatedModel()
    {
        using var context = CreateContext(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-revision-select").Change("older-v3");
        cut.WaitForAssertion(() => StringAssert.Contains(CompareValue(cut, "Model", "selected"), "ASI120MM (unvalidated model)", StringComparison.Ordinal));
        StringAssert.Contains(CompareValue(cut, "Model", "active"), "ASI676MC (model not flagged unvalidated)", StringComparison.Ordinal);
        StringAssert.Contains(CompareValue(cut, "Model", "selected"), "(differs from active)", StringComparison.Ordinal);
        Assert.IsFalse(cut.Markup.Contains("libraryPathEnvironmentVariable", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DelayedCatalogCamera_AfterSelectingHistory_DoesNotReplaceSelectedModel()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        var staleResult = await service.GetEquipmentAsync("camera-v1", CancellationToken.None).ConfigureAwait(false);
        var delayed = service.DeferEquipment("camera-v1");
        var selection = cut.Find("#rig-revision-select").ChangeAsync(new ChangeEventArgs { Value = "rig-v1" });
        Assert.IsFalse(selection.IsCompleted);
        await cut.Find("#rig-revision-select").ChangeAsync(new ChangeEventArgs { Value = "older-v3" }).ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(CompareValue(cut, "Model", "selected"),
            "ASI120MM (unvalidated model)", StringComparison.Ordinal));
        delayed.SetResult(staleResult);
        await selection.ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(CompareValue(cut, "Model", "selected"),
            "ASI120MM (unvalidated model)", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DelayedHistoricalCamera_AfterRefresh_DoesNotReplaceRefreshedModel()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        var cameraResult = await service.GetEquipmentAsync("camera-v2", CancellationToken.None).ConfigureAwait(false);
        var staleResult = await service.GetEquipmentAsync("camera-v1", CancellationToken.None).ConfigureAwait(false);
        var delayed = service.DeferEquipment("camera-v2");
        var selection = cut.Find("#rig-revision-select").ChangeAsync(new ChangeEventArgs { Value = "older-v3" });
        Assert.IsFalse(selection.IsCompleted);
        var refreshed = service.DeferEquipment("camera-v2");
        var refresh = cut.FindAll("button").Single(b => b.TextContent == "Refresh").ClickAsync(new MouseEventArgs());
        refreshed.SetResult(cameraResult);
        await refresh.ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(CompareValue(cut, "Model", "selected"),
            "ASI120MM (unvalidated model)", StringComparison.Ordinal));
        delayed.SetResult(staleResult);
        await selection.ConfigureAwait(false);
        cut.WaitForAssertion(() => StringAssert.Contains(CompareValue(cut, "Model", "selected"),
            "ASI120MM (unvalidated model)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EditingComposerSelection_InvalidatesAcknowledgedPreview()
    {
        using var context = CreateContext(new FakeRigService());
        var cut = context.Render<CameraRigPage>();
        ClickPreview(cut);
        cut.Find("[aria-label='Rig preview and selection'] input[type=checkbox]").Change(true);
        Assert.IsFalse(cut.Find("#rig-stage").HasAttribute("disabled"));
        cut.Find("#rig-camera-select").Change("camera-v2");
        Assert.IsTrue(cut.Find("#rig-stage").HasAttribute("disabled"));
        Assert.IsFalse(cut.Markup.Contains("Contract preview passed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CustomZwoDuplicate_RequiresRiskAcknowledgementAndSubmitsModelFlag()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-camera-edit").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("dialog").TextContent, "Custom ZWO expected model", StringComparison.Ordinal));
        StringAssert.Contains(cut.Markup, "ASI120MM Mini", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "SDK V1.41", StringComparison.Ordinal);
        EditorInput(cut, "Custom ZWO expected model").Change("ASI120MM Mini");
        StringAssert.Contains(cut.Markup, "Expected model for this duplicate: ASI120MM Mini", StringComparison.Ordinal);
        DialogPrimary(cut).Click();
        Assert.AreEqual(0, service.SaveCount);
        StringAssert.Contains(cut.Find("dialog [role='alert']").TextContent, "acknowledgement", StringComparison.Ordinal);
        DialogCheck(cut, "unvalidated camera model").Change(true);
        DialogPrimary(cut).Click();
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
        using var context = CreateContext(new FakeRigService { Unauthorized = true });
        _ = context.Render<CameraRigPage>();
        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EditActiveRig_StagesTheWholeChainInOneRequestAndOffersRestart()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-edit-active").Click();
        Assert.AreEqual("Edit active rig", cut.Find("#rig-dialog-heading").TextContent);
        Assert.IsFalse(cut.Find("#rig-edit-flip").HasAttribute("checked"));
        Assert.AreEqual("10", cut.Find("#rig-edit-fov").GetAttribute("value"));
        Assert.AreEqual("50", cut.Find("#rig-edit-focal").GetAttribute("value"));
        Assert.AreEqual("90", cut.Find("#rig-edit-altitude").GetAttribute("value"));
        StringAssert.Contains(cut.Find("#rig-edit-flip-help").TextContent, "East appears on the left", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("dialog input[type=checkbox]").Where(i => i.Id != "rig-edit-flip"));
        Assert.IsFalse(cut.Markup.Contains("libraryPathEnvironmentVariable", StringComparison.OrdinalIgnoreCase));

        cut.Find("#rig-edit-flip").Change(true);
        cut.Find("#rig-edit-azimuth").Change("180");
        Assert.AreEqual("Stage for restart", DialogPrimary(cut).TextContent);
        DialogPrimary(cut).Click();

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        Assert.AreEqual(1, service.EditCount);
        Assert.AreEqual(0, service.StageCount);
        Assert.AreEqual(new ActiveRigEditRequest("rig-v1", 1, true, 10, 50, 90, 180, 0), service.EditRequest);
        StringAssert.Contains(cut.Find(".rig-message[role='status']").TextContent,
            "Rig change staged. It applies when CameraAgent restarts.", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("[aria-label='Active rig edit steps']").TextContent, "Staged rig revision r2 for restart.",
            StringComparison.Ordinal);
        Assert.AreEqual("rig-v2", cut.Find("#rig-revision-select").GetAttribute("value"));
        Assert.AreEqual("Revision 2", SelectionFact(cut, "Pending restart"));
        var pending = cut.Find("[aria-label='Pending restart']");
        Assert.AreEqual("Restart now", pending.QuerySelector("button.restart-now")!.TextContent);
        Assert.IsTrue(cut.Find("#rig-edit-active").HasAttribute("disabled"));
    }

    [TestMethod]
    public void EditActiveRig_Unchanged_ReportsThatNothingWasRecorded()
    {
        var service = new FakeRigService { EditOutcome = new(ActiveRigEditStatus.NoChanges, [], []) };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-edit-active").Click();
        DialogPrimary(cut).Click();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".rig-message[role='status']").TextContent,
            "Nothing changed; no revisions were recorded.", StringComparison.Ordinal));
        Assert.AreEqual(1, service.EditCount);
        Assert.IsEmpty(cut.FindAll("[aria-label='Active rig edit steps']"));
        Assert.IsEmpty(cut.FindAll("[aria-label='Pending restart']"));
        Assert.IsFalse(cut.Find("#rig-edit-active").HasAttribute("disabled"));
    }

    [TestMethod]
    public void EditActiveRig_PreviewFailure_ShowsWhatWasRecordedAndWhatDidNotHappen()
    {
        var service = new FakeRigService
        {
            EditOutcome = new(ActiveRigEditStatus.Failed,
                ["Saved optics \"Installed optics copy\" revision 2.", "Composed rig revision r2."],
                ["Nothing was staged; the active rig and schedule are unchanged."],
                "Preview failed: rig.readout", "rig-v2")
        };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-edit-active").Click();
        cut.Find("#rig-edit-fov").Change("120");
        DialogPrimary(cut).Click();

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        var alert = cut.Find(".rig-message[role='alert']").TextContent;
        StringAssert.Contains(alert, "The rig edit stopped: Preview failed: rig.readout", StringComparison.Ordinal);
        StringAssert.Contains(alert, "selected in Compare & stage", StringComparison.Ordinal);
        var steps = cut.Find("[aria-label='Active rig edit steps']").TextContent;
        StringAssert.Contains(steps, "Composed rig revision r2.", StringComparison.Ordinal);
        StringAssert.Contains(steps, "Did not happen", StringComparison.Ordinal);
        StringAssert.Contains(steps, "Nothing was staged; the active rig and schedule are unchanged.", StringComparison.Ordinal);
        Assert.AreEqual("rig-v2", cut.Find("#rig-revision-select").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll("[aria-label='Pending restart']"));
        Assert.AreEqual(0, service.StageCount);
    }

    [TestMethod]
    public void EditActiveRig_InvalidValue_StaysOpenWithoutCallingTheService()
    {
        var service = new FakeRigService();
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("#rig-edit-active").Click();
        cut.Find("#rig-edit-fov").Change("0");
        DialogPrimary(cut).Click();

        StringAssert.Contains(cut.Find("dialog [role='alert']").TextContent, "Field of view", StringComparison.Ordinal);
        Assert.AreEqual(0, service.EditCount);
        Assert.AreEqual("Edit active rig", cut.Find("#rig-dialog-heading").TextContent);
    }

    [TestMethod]
    public void PendingRig_RestartNow_ShowsRestartingState()
    {
        var service = new FakeRigService { PendingId = "other-v2" };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();
        cut.Find("[aria-label='Pending restart'] button.restart-now").Click();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[aria-label='Pending restart'] [role='status']").TextContent,
            "Restarting CameraAgent", StringComparison.Ordinal));
        Assert.AreEqual(1, service.RestartCount);
        Assert.IsEmpty(cut.FindAll("button.restart-now"));
    }

    [TestMethod]
    public void PendingRig_UnsupervisedHost_HidesRestartAndExplainsManualRestart()
    {
        var service = new FakeRigService { PendingId = "other-v2", RestartStatus = new(false, true, false) };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();

        Assert.IsEmpty(cut.FindAll("button.restart-now"));
        StringAssert.Contains(cut.Find("[aria-label='Pending restart']").TextContent, "Restart CameraAgent manually",
            StringComparison.Ordinal);
        Assert.AreEqual(0, service.RestartCount);
    }

    [TestMethod]
    public void PendingRig_ReadOnlyOperator_IsNotOfferedRestart()
    {
        var service = new FakeRigService { PendingId = "other-v2", RestartStatus = new(true, false, false) };
        using var context = CreateContext(service);
        var cut = context.Render<CameraRigPage>();

        Assert.IsEmpty(cut.FindAll("button.restart-now"));
        StringAssert.Contains(cut.Find("[aria-label='Pending restart']").TextContent, "change rights are required",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void ComposerSelectors_DefaultToTheActiveRigsEquipment()
    {
        using var context = CreateContext(new FakeRigService { ActiveCameraListedSecond = true });
        var cut = context.Render<CameraRigPage>();

        Assert.AreEqual("camera-v2", cut.FindAll("#rig-camera-select option")[0].GetAttribute("value"));
        Assert.AreEqual("rig", cut.Find("#rig-profile-select").GetAttribute("value"));
        Assert.AreEqual("camera-v1", cut.Find("#rig-camera-select").GetAttribute("value"));
        Assert.AreEqual("optics-v1", cut.Find("#rig-optics-select").GetAttribute("value"));
        Assert.AreEqual("mount-v1", cut.Find("#rig-mount-select").GetAttribute("value"));
    }

    private static BunitContext CreateContext(FakeRigService service)
    {
        var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./Components/Pages/CameraRigPage.razor.js");
        module.SetupVoid("showModal", _ => true).SetVoidResult();
        module.SetupVoid("focusById", _ => true).SetVoidResult();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(service);
        return context;
    }

    private static bool FocusRequested(BunitContext context, string id)
        => context.JSInterop.Invocations.Any(i => i.Identifier == "focusById" && Equals(i.Arguments[0], id));

    private static void ClickButton(IRenderedComponent<CameraRigPage> cut, string text)
        => cut.FindAll("button").Single(b => b.TextContent == text).Click();

    private static AngleSharp.Dom.IElement PreviewButton(IRenderedComponent<CameraRigPage> cut)
        => cut.FindAll("button").Single(b => b.TextContent == "Preview against active schedule");

    private static void ClickPreview(IRenderedComponent<CameraRigPage> cut) => PreviewButton(cut).Click();

    private static AngleSharp.Dom.IElement DialogPrimary(IRenderedComponent<CameraRigPage> cut)
        => cut.Find("dialog footer .button.primary");

    private static AngleSharp.Dom.IElement EditorInput(IRenderedComponent<CameraRigPage> cut, string labelPrefix)
        => cut.FindAll("dialog label").Single(l => l.TextContent.StartsWith(labelPrefix, StringComparison.Ordinal)).QuerySelector("input")!;

    private static AngleSharp.Dom.IElement EditorSelect(IRenderedComponent<CameraRigPage> cut, string labelPrefix)
        => cut.FindAll("dialog label").Single(l => l.TextContent.StartsWith(labelPrefix, StringComparison.Ordinal)).QuerySelector("select")!;

    private static AngleSharp.Dom.IElement DialogCheck(IRenderedComponent<CameraRigPage> cut, string text)
        => cut.FindAll("dialog label.rig-check").Single(l => l.TextContent.Contains(text, StringComparison.Ordinal)).QuerySelector("input")!;

    private static string CompareValue(IRenderedComponent<CameraRigPage> cut, string label, string column)
        => cut.FindAll(".rig-compare tbody tr").Single(r => r.QuerySelector("th")!.TextContent == label)
            .QuerySelector($"td.rig-{column}")!.TextContent;

    private static string SelectionFact(IRenderedComponent<CameraRigPage> cut, string term)
        => cut.FindAll("[aria-label='Rig selection'] dl > div").Single(d => d.QuerySelector("dt")!.TextContent == term)
            .QuerySelector("dd")!.TextContent;

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
        internal bool FailNextGet { get; set; }
        internal bool FailNextInventory { get; set; }
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
        internal TaskCompletionSource? DeferredProfileSave { get; set; }
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
        internal bool ActiveCameraListedSecond { get; set; }
        internal int EditCount { get; private set; }
        internal ActiveRigEditRequest? EditRequest { get; private set; }
        internal ActiveRigEditOutcome? EditOutcome { get; set; }
        internal CameraAgentRestartStatus RestartStatus { get; set; } = new(true, true, false);
        internal int RestartCount { get; private set; }
        private NamedRigRevision? _composedRig;
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
        {
            if (FailNextGet)
            {
                FailNextGet = false;
                return ValueTask.FromResult(OperatorUiResult<NamedRigUiCatalog>.Failure(
                    OperatorUiResultKind.Unavailable, "Catalog unavailable"));
            }
            return ValueTask.FromResult(Unauthorized
                ? OperatorUiResult<NamedRigUiCatalog>.Failure(OperatorUiResultKind.Unauthorized, "Denied")
                : OperatorUiResult<NamedRigUiCatalog>.Success(new(new("rig-v1", PendingId, Version),
                    [_rig, _rig with { RevisionId = "other-v2", ProfileId = "other", RevisionNumber = 2 },
                     .. (_composedRig is null ? [] : new[] { _composedRig })],
                    PendingFailure, ActiveFailure)));
        }
        public ValueTask<OperatorUiResult<NamedRigInventory>> GetInventoryAsync(CancellationToken token)
        {
            if (FailNextInventory)
            {
                FailNextInventory = false;
                return ValueTask.FromResult(OperatorUiResult<NamedRigInventory>.Failure(
                    OperatorUiResultKind.Unavailable, "Inventory unavailable"));
            }
            return ValueTask.FromResult(OperatorUiResult<NamedRigInventory>.Success(VirtualOnly
                ? new(_inventory.Profiles, StarterTemplate is null
                    ? [new NamedEquipmentDefinition("virtual", "camera", "Virtual camera", 1, "camera-v1", true, false),
                       .. _inventory.Equipment.Where(e => e.Kind != "camera")]
                    : [.. _inventory.Equipment, new NamedEquipmentDefinition("starter", "camera", "ASI676MC camera", 1, "starter-v1"), .. (_savedEquipment is null ? [] : new[] { _savedEquipment })])
                : new(_inventory.Profiles, [.. (ActiveCameraListedSecond ? _inventory.Equipment.OrderByDescending(e => e.RevisionId == "camera-v2").ToArray() : _inventory.Equipment),
                    .. (ExistingOpticsCopy ? new[] { new NamedEquipmentDefinition("optics-copy", "optics", "Installed optics copy", 1, "optics-copy-v1") } : []),
                    .. (_savedEquipment is null ? [] : new[] { _savedEquipment })])));
        }
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
            var result = OperatorUiResult<NamedRigProfile>.Success(saved);
            return DeferredProfileSave is { } deferred ? AfterAsync(deferred.Task, result) : ValueTask.FromResult(result);
        }

        private static async ValueTask<T> AfterAsync<T>(Task gate, T value)
        {
            await gate.ConfigureAwait(false);
            return value;
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
        public ValueTask<OperatorUiResult<ActiveRigEditOutcome>> ApplyActiveRigEditAsync(ActiveRigEditRequest request,
            CancellationToken token)
        {
            EditCount++;
            EditRequest = request;
            var outcome = EditOutcome ?? new ActiveRigEditOutcome(ActiveRigEditStatus.Staged,
                ["Created optics \"Installed optics copy\" from \"Installed optics\", which is installed and read-only.",
                 "Composed rig revision r2.", "Staged rig revision r2 for restart."], [], ComposedRevisionId: "rig-v2");
            if (outcome.ComposedRevisionId is { } composed)
            {
                _composedRig = _rig with
                {
                    RevisionId = composed,
                    RevisionNumber = 2,
                    OpticsRevisionId = "optics-copy-v1",
                    SourceScheduleRevisionId = null,
                    Rig = _rig.Rig with { Optics = _rig.Rig.Optics with { HorizontalFlip = request.HorizontalFlip } }
                };
            }
            if (outcome.Status == ActiveRigEditStatus.Staged)
            {
                PendingId = outcome.ComposedRevisionId;
                Version = request.SelectionVersion + 1;
            }
            return ValueTask.FromResult(OperatorUiResult<ActiveRigEditOutcome>.Success(outcome));
        }
        public ValueTask<OperatorUiResult<CameraAgentRestartStatus>> GetRestartStatusAsync(CancellationToken token)
            => ValueTask.FromResult(OperatorUiResult<CameraAgentRestartStatus>.Success(RestartStatus));
        public ValueTask<OperatorUiResult<CameraAgentRestartDisposition>> RequestRestartAsync(CancellationToken token)
        {
            RestartCount++;
            return ValueTask.FromResult(RestartStatus.CanRequest
                ? OperatorUiResult<CameraAgentRestartDisposition>.Success(RestartStatus.Supervised
                    ? CameraAgentRestartDisposition.Scheduled : CameraAgentRestartDisposition.Unsupervised)
                : OperatorUiResult<CameraAgentRestartDisposition>.Failure(OperatorUiResultKind.Unauthorized, "Denied"));
        }
    }
}
