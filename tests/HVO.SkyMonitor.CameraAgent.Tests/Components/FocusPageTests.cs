using System.Reflection;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class FocusPageTests
{
    private const string Owner = "owner-1";
    private static readonly DateTimeOffset Started = new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
    private static readonly string[] ProcedureSteps = ["Choose a field", "Start previews", "Adjust by hand", "Secure & verify"];
    private static readonly string[] HistoryColumns = ["Session", "Best HFD", "Target", "Conditions", "Result"];
    private static readonly string[] ReasonCodes =
    [
        ManualFocusReasonCodes.StoppedByOperator, ManualFocusReasonCodes.SafetyTimeout, ManualFocusReasonCodes.ObserverLost,
        ManualFocusReasonCodes.HostStopping, ManualFocusReasonCodes.CameraWithdrawn, ManualFocusReasonCodes.AdmissionUnavailable,
        ManualFocusReasonCodes.RepeatedFailures, ManualFocusReasonCodes.SampleDeadlineExceeded,
        ManualFocusReasonCodes.PreviewUnmeasurable, ManualFocusReasonCodes.LoopFailed
    ];

    private static readonly CameraSimulatedFocusModel Model =
        new("virtual-defocus-gaussian-quadrature-v1", "simulated steps", 0, 1000, 560, new string('a', 64));

    private static readonly CameraFocusPreviewFidelity Simulated =
        new("virtual-sky-simulated", false, "VirtualSky blur is simulated and does not qualify a physical lens.");

    private static readonly ManualFocusSessionAvailability Available = new(true, "Ready.", "VirtualSky", Simulated, Model);

    [TestMethod]
    public void Route_IsTheProtectedOperationsFocusPath()
    {
        var routes = typeof(FocusPage).GetCustomAttributes<RouteAttribute>().Select(static route => route.Template).ToArray();
        Assert.HasCount(1, routes);
        Assert.AreEqual("/operations/focus", routes[0]);
        Assert.AreEqual(OperationsSectionCatalog.Get("focus").Href, routes[0]);
        Assert.AreEqual(
            CameraAgentAuthorizationPolicyNames.OperationsReadV1,
            typeof(FocusPage).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [TestMethod]
    public void Render_PortsThePrototypeLayoutBoundaryAndActionPositions()
    {
        var service = new FakeFocusService(Status(ManualFocusSessionSnapshot.Idle));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();
        var section = OperationsSectionCatalog.Get("focus");

        Assert.AreEqual("Focus", cut.Find("h1#focus-heading").TextContent.Trim());
        Assert.AreEqual(section.Eyebrow, cut.Find(".ops-page-heading .eyebrow").TextContent.Trim());
        var actions = cut.Find(".ops-page-actions");
        Assert.AreEqual("Current sky", actions.QuerySelector("a.button")!.TextContent.Trim());
        Assert.AreEqual("/", actions.QuerySelector("a.button")!.GetAttribute("href"));
        Assert.AreEqual("Start focus session", actions.QuerySelector("#focus-start")!.TextContent.Trim());
        Assert.IsFalse(actions.QuerySelector("#focus-start")!.HasAttribute("disabled"));
        Assert.AreEqual(
            "Manual focus only. Loosen, adjust, and secure the lens at the camera. CameraAgent measures image sharpness but does not move hardware.",
            Normalize(cut.Find(".focus-boundary").TextContent));
        StringAssert.Contains(cut.Find(".focus-viewer-toolbar").TextContent,
            "Preview is temporary and is not admitted to the normal capture pipeline.", StringComparison.Ordinal);
        Assert.AreEqual("Select region", cut.Find(".focus-viewer-toolbar #focus-select-region").TextContent.Trim());
        Assert.AreEqual("Ready to measure", cut.Find("#focus-session-status").TextContent.Trim());
        Assert.AreEqual("No focus session is active", cut.Find("#focus-session-detail").TextContent.Trim());
        Assert.AreEqual("Idle", cut.Find(".focus-session-state .state-chip").TextContent.Trim());
        Assert.AreEqual("Apply to next exposure", cut.Find(".focus-inspector #focus-sample").TextContent.Trim());
        Assert.AreEqual("1.000s", cut.Find("#focus-exposure").GetAttribute("value"));
        Assert.AreEqual("110", cut.Find("#focus-gain").GetAttribute("value"));
        CollectionAssert.AreEqual(
            ProcedureSteps,
            cut.FindAll(".ops-step strong").Select(static step => step.TextContent.Trim()).ToArray());
        var history = cut.Find("#focus-history-heading").Closest("section")!;
        Assert.IsNotNull(history.QuerySelector("header #focus-export"));
        CollectionAssert.AreEqual(HistoryColumns,
            history.QuerySelectorAll("thead th").Select(static column => column.TextContent.Trim()).ToArray());
        Assert.AreEqual("No focus sessions are retained on this CameraAgent.", history.QuerySelector("tbody tr")!.TextContent.Trim());
        Assert.IsEmpty(cut.FindAll("img"));
        Assert.StartsWith("— no sample", cut.Find("#focus-metric").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll(".focus-trend circle"));
        StringAssert.Contains(cut.Find(".focus-simulated legend").TextContent, "not a motor", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".focus-fidelity").TextContent, Simulated.Limitation, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Start_SendsTheFormSettingsAndBecomesEndSession()
    {
        var service = new FakeFocusService(Status(ManualFocusSessionSnapshot.Idle));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        cut.Find("#focus-exposure").Input("500ms");
        cut.Find("#focus-gain").Input("40");
        cut.Find("#focus-safety-limit").Change("5");
        cut.Find("#focus-start").Click();

        var request = service.StartRequest!;
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), request.Settings.Exposure);
        Assert.AreEqual(40d, request.Settings.Gain);
        Assert.AreEqual(Model.DefaultPosition, request.SimulatedFocusPosition);
        Assert.AreEqual(TimeSpan.FromMinutes(5), request.SafetyTimeout);
        Assert.IsNull(request.Target);
        Assert.AreEqual("End session", cut.Find("#focus-start").TextContent.Trim());
        Assert.AreEqual("Live", cut.Find(".focus-session-state .state-chip").TextContent.Trim());
        StringAssert.Contains(cut.Find(".focus-viewer-toolbar").TextContent, "Leaving this page ends the session within 30 seconds.",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void Unavailable_DisablesStartWithTheOwnerReason()
    {
        var service = new FakeFocusService(Status(ManualFocusSessionSnapshot.Idle) with
        {
            Availability = ManualFocusSessionAvailability.Unavailable(ManualFocusReasonCodes.NoModule)
        });
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        var start = cut.Find("#focus-start");
        Assert.IsTrue(start.HasAttribute("disabled"));
        Assert.AreEqual("focus-unavailable-reason", start.GetAttribute("aria-describedby"));
        Assert.AreEqual(ManualFocusReasonCodes.NoModule, cut.Find("#focus-unavailable-reason").TextContent.Trim());
        Assert.AreEqual("Unavailable", cut.Find(".focus-session-state .state-chip").TextContent.Trim());
        Assert.IsTrue(cut.Find("#focus-exposure").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("#focus-sample").HasAttribute("disabled"));
    }

    [TestMethod]
    public void ReadOnlyOperator_SeesTheLiveSessionButCannotControlOrHeartbeatIt()
    {
        var service = new FakeFocusService(Status(Running(Sample(1, 4.2)), images: Images(1)) with { CanControl = false, IsOwner = false });
        using var context = CreateContext(service, out var time);
        var cut = context.Render<FocusPage>();

        time.Tick();

        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StatusReads));
        Assert.AreEqual(0, service.ObserveCount);
        Assert.IsTrue(cut.Find("#focus-start").HasAttribute("disabled"));
        Assert.AreEqual("End session", cut.Find("#focus-start").TextContent.Trim());
        StringAssert.Contains(cut.Find("#focus-unavailable-reason").TextContent, "can view focus sessions", StringComparison.Ordinal);
        Assert.IsTrue(cut.Find("#focus-exposure").HasAttribute("disabled"));
        Assert.AreEqual("4.20 px", cut.Find("#focus-metric").TextContent.Trim());
    }

    [TestMethod]
    public void RunningUnderAnotherOwner_CannotBeEndedOrObserved()
    {
        var service = new FakeFocusService(Status(Running(Sample(1, 4.2)) with { OwnerId = "owner-2" }) with { IsOwner = false });
        using var context = CreateContext(service, out var time);
        var cut = context.Render<FocusPage>();

        time.Tick();

        cut.WaitForAssertion(() => Assert.AreEqual(2, service.StatusReads));
        Assert.AreEqual(0, service.ObserveCount);
        Assert.IsTrue(cut.Find("#focus-start").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find("#focus-unavailable-reason").TextContent, "Another operator owns", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RunningOwner_HeartbeatsEachPollAndShowsMeasuredStarTrendAndSamples()
    {
        var history = new[] { Sample(1, 6.0), Sample(2, 3.0), Sample(3, 4.5) };
        var service = new FakeFocusService(Status(Running(history), images: Images(3)));
        using var context = CreateContext(service, out var time);
        var cut = context.Render<FocusPage>();

        time.Tick();
        cut.WaitForAssertion(() => Assert.AreEqual(2, service.ObserveCount));
        time.Tick();
        cut.WaitForAssertion(() => Assert.AreEqual(3, service.ObserveCount));

        var star = cut.Find(".focus-star img");
        Assert.StartsWith("data:image/jpeg;base64,", star.GetAttribute("src"));
        // The centroid 242.5,152.4 sits in the 64-pixel crop at 210,120.
        Assert.AreEqual("left:50.781%;top:50.625%", cut.Find(".focus-star-frame .focus-reticle").GetAttribute("style"));
        StringAssert.Contains(cut.Find(".focus-star .focus-target-label").TextContent, "Target 242.5, 152.4 / automatic / HFD 4.50 px",
            StringComparison.Ordinal);
        Assert.AreEqual("4.50 px", cut.Find("#focus-metric").TextContent.Trim());
        StringAssert.Contains(cut.Find("#focus-best").TextContent, "3.00 px HFD", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("#focus-best").TextContent, "sample 2", StringComparison.Ordinal);
        Assert.AreEqual("M6.0 10.0 L120.0 80.0 L234.0 45.0", cut.Find(".focus-trend path.line").GetAttribute("d"));
        Assert.AreEqual("80.0", cut.Find(".focus-trend circle.best").GetAttribute("cy"));
        Assert.AreEqual("234.0", cut.Find(".focus-trend circle.latest").GetAttribute("cx"));
        var rows = cut.FindAll(".focus-samples-table tbody tr");
        Assert.HasCount(3, rows);
        StringAssert.StartsWith(rows[0].TextContent.Trim(), "#3", StringComparison.Ordinal);

        cut.Find("#focus-metric-kind").Change(FocusPage.MetricFwhm);

        Assert.AreEqual("4.05 px", cut.Find("#focus-metric").TextContent.Trim());
        StringAssert.Contains(cut.Find(".focus-score > span").TextContent, "FWHM", StringComparison.Ordinal);
    }

    [TestMethod]
    public void InvalidSamples_StateTheirReasonAndCarryNoWidth()
    {
        var saturated = Sample(2, 0, FocusStarStatus.Saturated);
        var service = new FakeFocusService(Status(Running(Sample(1, 3.0), saturated), images: Images(2)));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        Assert.StartsWith("— not measured", cut.Find("#focus-metric").TextContent.Trim());
        var detail = cut.Find("#focus-sample-detail");
        Assert.IsTrue(detail.ClassList.Contains("invalid"));
        StringAssert.Contains(detail.TextContent, "7 pixels are clipped", StringComparison.Ordinal);
        StringAssert.Contains(detail.TextContent, "Reduce exposure or gain", StringComparison.Ordinal);
        var latestRow = cut.FindAll(".focus-samples-table tbody tr")[0];
        StringAssert.Contains(latestRow.TextContent, "Saturated", StringComparison.Ordinal);
        Assert.AreEqual("—", latestRow.QuerySelectorAll("td")[2].TextContent.Trim());
        Assert.HasCount(1, cut.FindAll(".focus-trend circle.latest"));
    }

    [TestMethod]
    public void ApplyAndNudge_AdjustOnlyTheNextExposure()
    {
        var service = new FakeFocusService(Status(Running(Sample(1, 3.0)), images: Images(1)));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();
        Assert.IsTrue(cut.Find("#focus-sample").HasAttribute("disabled"));

        cut.Find("#focus-exposure").Input("2.5");
        cut.Find("#focus-gain").Input("75");
        Assert.IsFalse(cut.Find("#focus-sample").HasAttribute("disabled"));
        cut.Find("#focus-sample").Click();

        Assert.HasCount(1, service.Adjustments);
        Assert.AreEqual(new ManualFocusPreviewSettings(TimeSpan.FromSeconds(2.5), 75), service.Adjustments[0].Settings);
        Assert.IsNull(service.Adjustments[0].SimulatedFocusPosition);
        Assert.IsTrue(cut.Find("#focus-sample").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find(".focus-message").TextContent, "next exposure", StringComparison.Ordinal);

        cut.FindAll(".focus-simulated-row button")[2].Click();

        Assert.HasCount(2, service.Adjustments);
        Assert.AreEqual(570d, service.Adjustments[1].SimulatedFocusPosition);
        Assert.IsNull(service.Adjustments[1].Settings);
        Assert.AreEqual("570", cut.Find("#focus-position").GetAttribute("value"));
    }

    [TestMethod]
    public void InvalidSettings_AreRejectedBeforeAnyCommand()
    {
        var service = new FakeFocusService(Status(Running(Sample(1, 3.0))));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        cut.Find("#focus-exposure").Input("two seconds");
        cut.Find("#focus-sample").Click();
        StringAssert.Contains(cut.Find(".focus-message[role=alert]").TextContent, "Enter a preview exposure", StringComparison.Ordinal);

        cut.Find("#focus-exposure").Input("61s");
        cut.Find("#focus-sample").Click();
        cut.Find("#focus-exposure").Input("1s");
        cut.Find("#focus-gain").Input("1001");
        cut.Find("#focus-sample").Click();

        StringAssert.Contains(cut.Find(".focus-message[role=alert]").TextContent, "Enter a preview gain", StringComparison.Ordinal);
        Assert.IsEmpty(service.Adjustments);
    }

    [TestMethod]
    public async Task FieldView_RetargetsByCatalogStarAndByExactPointerPixel()
    {
        var service = new FakeFocusService(Status(Running(Sample(1, 3.0)), images: Images(1)));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        await cut.Find("#focus-select-region").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        var field = cut.Find(".focus-field img");
        Assert.StartsWith("data:image/jpeg;base64,", field.GetAttribute("src"));
        var vega = cut.Find(".focus-catalog-star");
        Assert.AreEqual("left:50.000%;top:50.000%", vega.GetAttribute("style"));
        StringAssert.Contains(vega.GetAttribute("aria-label")!, "Vega, magnitude 0.0", StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.AreEqual(1,
            context.JSInterop.Invocations.Count(static invocation => invocation.Identifier == "bindPicker")));
        await vega.ClickAsync(new MouseEventArgs()).ConfigureAwait(false);

        Assert.AreEqual(new PixelPoint(320, 240), service.Adjustments.Single().Target);
        Assert.IsEmpty(cut.FindAll(".focus-field"));

        await cut.Find("#focus-select-region").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("#focus-automatic-target").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.Find("#focus-select-region").ClickAsync(new MouseEventArgs()).ConfigureAwait(false);
        await cut.InvokeAsync(() => cut.Instance.PickAsync(0.25, 0.75)).ConfigureAwait(false);

        Assert.HasCount(3, service.Adjustments);
        Assert.IsTrue(service.Adjustments[1].ResetToAutomaticTarget);
        Assert.AreEqual(new PixelPoint(160, 360), service.Adjustments[2].Target);
    }

    [TestMethod]
    public void EndedOwner_SavesTheResultThenExportsTheVerifiedRecord()
    {
        var service = new FakeFocusService(Status(Running(Sample(1, 3.0), Sample(2, 2.5))));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        cut.Find("#focus-start").Click();
        Assert.AreEqual(1, service.StopCount);
        Assert.AreEqual("Save session result", cut.Find("#focus-start").TextContent.Trim());
        Assert.AreEqual("Discard", cut.Find("#focus-discard").TextContent.Trim());
        StringAssert.Contains(cut.Find("#focus-session-detail").TextContent, "Ended by the operator. Result is in memory only.",
            StringComparison.Ordinal);
        Assert.AreEqual("BUTTON", cut.Find("#focus-export").TagName);

        cut.Find("#focus-start").Click();

        Assert.AreEqual(1, service.SaveCount);
        Assert.AreEqual("Start new session", cut.Find("#focus-start").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll("#focus-discard"));
        var export = cut.Find("#focus-export");
        Assert.AreEqual("A", export.TagName);
        Assert.AreEqual("/api/v1/operations/focus-sessions/20261002T040500Z-session-1/export", export.GetAttribute("href"));
        Assert.IsTrue(export.HasAttribute("download"));
        var row = cut.Find(".focus-history-table tbody tr.selected");
        StringAssert.Contains(row.TextContent, "2.50 px", StringComparison.Ordinal);
        StringAssert.Contains(row.TextContent, "1.000s / gain 110 / position 560, virtual-sky-simulated preview", StringComparison.Ordinal);
        StringAssert.Contains(row.TextContent, "Saved", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".focus-message").TextContent, "saved as 20261002T040500Z-session-1", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EndedOwner_DiscardRetainsNothingAndOffersANewSession()
    {
        var service = new FakeFocusService(Status(Ended(ManualFocusReasonCodes.SafetyTimeout, Sample(1, 3.0))));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();
        Assert.AreEqual("Timed out", cut.Find(".focus-session-state .state-chip").TextContent.Trim());
        StringAssert.Contains(cut.Find("#focus-session-detail").TextContent, "The safety limit ended the session.", StringComparison.Ordinal);

        cut.Find("#focus-discard").Click();

        Assert.AreEqual(1, service.DiscardCount);
        Assert.AreEqual("Start new session", cut.Find("#focus-start").TextContent.Trim());
        Assert.IsFalse(cut.Find("#focus-start").HasAttribute("disabled"));
        Assert.AreEqual("The session was discarded; nothing was retained.", cut.Find(".focus-samples-table tbody tr").TextContent.Trim());
    }

    [TestMethod]
    public void EndedWithoutRetention_CannotSaveAndSaysResultsStayInMemory()
    {
        var service = new FakeFocusService(Status(Ended(ManualFocusReasonCodes.ObserverLost, Sample(1, 3.0))) with { RetentionAvailable = false });
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        Assert.AreEqual("Start new session", cut.Find("#focus-start").TextContent.Trim());
        StringAssert.Contains(cut.Find("#focus-session-detail").TextContent, "page closed, connection lost, or access removed",
            StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("#focus-history-note").TextContent, "retention is not configured", StringComparison.Ordinal);
        Assert.AreEqual("Focus session retention is not configured on this CameraAgent.",
            cut.Find(".focus-history-table tbody tr").TextContent.Trim());
    }

    [TestMethod]
    public void History_FlagsChecksumFailuresAndNeverExportsThem()
    {
        var service = new FakeFocusService(Status(ManualFocusSessionSnapshot.Idle));
        service.Saved.Add(Record("20261002T030000Z-bad", verified: false));
        service.Saved.Add(Record("20261002T020000Z-good", verified: true));
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        var rows = cut.FindAll(".focus-history-table tbody tr");
        StringAssert.Contains(rows[0].TextContent, "Checksum failed", StringComparison.Ordinal);
        Assert.IsTrue(rows[0].QuerySelector("button.focus-record")!.HasAttribute("disabled"));
        Assert.AreEqual("/api/v1/operations/focus-sessions/20261002T020000Z-good/export", cut.Find("#focus-export").GetAttribute("href"));
    }

    [TestMethod]
    public void CommandConflict_IsShownAsAnAlertWithoutChangingTheSession()
    {
        var service = new FakeFocusService(Status(Running(Sample(1, 3.0))))
        {
            CommandFailure = OperatorUiResult<ManualFocusSessionSnapshot>.Failure(OperatorUiResultKind.Conflict, "Only the session owner can do that.")
        };
        using var context = CreateContext(service, out _);
        var cut = context.Render<FocusPage>();

        cut.Find("#focus-start").Click();

        Assert.AreEqual("Only the session owner can do that.", cut.Find(".focus-message[role=alert]").TextContent.Trim());
        Assert.AreEqual("End session", cut.Find("#focus-start").TextContent.Trim());
    }

    [TestMethod]
    public void UnauthorizedStatus_NavigatesToAccessDenied()
    {
        var service = new FakeFocusService(null);
        using var context = CreateContext(service, out _);

        _ = context.Render<FocusPage>();

        Assert.EndsWith("/Account/AccessDenied", context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.AreEqual(0, service.SavedReads);
    }

    [TestMethod]
    public void RevokedHeartbeat_StopsPollingAndNavigatesToAccessDenied()
    {
        var service = new FakeFocusService(Status(Running(Sample(1, 3.0)))) { ObserveKind = OperatorUiResultKind.Unauthorized };
        using var context = CreateContext(service, out var time);
        var cut = context.Render<FocusPage>();

        cut.WaitForAssertion(() => Assert.EndsWith("/Account/AccessDenied", context.Services.GetRequiredService<NavigationManager>().Uri));
        time.Tick();

        Assert.AreEqual(1, service.StatusReads);
        Assert.IsEmpty(cut.FindAll("img"));
    }

    [TestMethod]
    [DataRow("1.000s", 1000d)]
    [DataRow("1.5", 1500d)]
    [DataRow(" 500ms ", 500d)]
    [DataRow("0.001S", 1d)]
    [DataRow("2MS", 2d)]
    public void TryParseExposure_AcceptsSecondsAndMilliseconds(string text, double milliseconds)
    {
        Assert.IsTrue(FocusPage.TryParseExposure(text, out var exposure));
        Assert.AreEqual(milliseconds, exposure.TotalMilliseconds, 1e-9);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("s")]
    [DataRow("-1s")]
    [DataRow("0")]
    [DataRow("NaN")]
    [DataRow("1e300")]
    public void TryParseExposure_RejectsMalformedValues(string text)
        => Assert.IsFalse(FocusPage.TryParseExposure(text, out _));

    [TestMethod]
    public void EndText_GivesEveryReasonCodeItsOwnExplanation()
    {
        var texts = ReasonCodes.Select(FocusPage.EndText).ToArray();

        Assert.HasCount(ReasonCodes.Length, texts.Distinct(StringComparer.Ordinal));
        Assert.IsFalse(texts.Contains(FocusPage.EndText(null)));
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static BunitContext CreateContext(FakeFocusService service, out ManualTimeProvider time)
    {
        var context = new BunitContext();
        context.JSInterop.SetupModule("./Components/Pages/FocusPage.razor.js").SetupVoid("bindPicker", _ => true).SetVoidResult();
        time = new ManualTimeProvider();
        context.Services.AddSingleton<ICameraAgentFocusUiService>(service);
        context.Services.AddSingleton<TimeProvider>(time);
        return context;
    }

    private static FocusUiStatus Status(ManualFocusSessionSnapshot session, ManualFocusPreviewImages? images = null)
        => new(session, images, Available, ManualFocusSessionLimits.Default, RetentionAvailable: true, CanControl: true,
            IsOwner: session.OwnerId == Owner);

    private static ManualFocusSessionSnapshot Running(params ManualFocusSample[] history)
        => new("session-1", ManualFocusSessionState.Running, Owner, Started, null, Started.AddMinutes(15),
            new(TimeSpan.FromSeconds(1), 110), Model.DefaultPosition, null, ManualFocusTargetSource.Automatic, history.Length, 100, history,
            history.Where(static sample => sample.Measurement.Status == FocusStarStatus.Valid)
                .MinBy(static sample => sample.Measurement.HalfFluxDiameterPixels),
            null, null, "VirtualSky", Simulated, Model, ManualFocusRetentionState.InMemoryOnly, null);

    private static ManualFocusSessionSnapshot Ended(string reason, params ManualFocusSample[] history)
        => Running(history) with
        {
            State = reason == ManualFocusReasonCodes.StoppedByOperator ? ManualFocusSessionState.Stopped : ManualFocusSessionState.TimedOut,
            EndedUtc = Started.AddMinutes(5),
            EndReason = reason
        };

    private static ManualFocusSample Sample(long sequence, double hfd, FocusStarStatus status = FocusStarStatus.Valid)
    {
        var valid = status == FocusStarStatus.Valid;
        var measurement = new FocusStarMeasurement(status, valid ? FocusStarReasonCodes.Valid : FocusStarReasonCodes.Saturated,
            FocusTestCentroid, valid ? hfd / 2 : null, valid ? hfd * 0.9 : null, 19_500, 900, 100, 4, 220,
            valid ? 0 : 7, 400, new string('b', 64));
        var provenance = new ManualFocusSampleProvenance(Started.AddSeconds(sequence), 640, 480, CameraPixelFormat.Mono16, new string('c', 64),
            null, "VirtualSky", 1, Simulated.Kind, false, Model.ModelId, Model.ParametersSha256, 210, 120, 64, 64, "sampler-v1",
            FocusStarMeasurement.MetricDefinition, FocusStarMeasurement.Units, FocusStarMeasurer.AlgorithmVersion, new string('b', 64),
            new string('d', 64));
        return new(sequence, Started.AddSeconds(sequence), new(TimeSpan.FromSeconds(1), 110), Model.DefaultPosition,
            ManualFocusTargetSource.Automatic, measurement, provenance);
    }

    private static readonly PixelPoint FocusTestCentroid = new(242.5, 152.4);

    private static ManualFocusPreviewImages Images(long sequence)
        => new(sequence, [0xFF, 0xD8, 0x01], 160, 120, 4, 640, 480, [0xFF, 0xD8, 0x02], 210, 120, 64, 64,
            [new ManualFocusCatalogStar("hip-91262", "Vega", 0.03, new PixelPoint(320, 240))]);

    private static ManualFocusSessionRecordSummary Record(string id, bool verified)
        => new(id, verified, verified ? new string('e', 64) : null, 2048, verified ? Started.AddMinutes(5) : null, Owner,
            verified ? ManualFocusSessionState.Stopped : null, 2, verified ? 2.5 : null, "VirtualSky", Simulated.Kind, false,
            verified ? new(TimeSpan.FromSeconds(1), 110) : null, verified ? ManualFocusTargetSource.Automatic : null,
            verified ? Model.DefaultPosition : null);

    private sealed class FakeFocusService(FocusUiStatus? status) : ICameraAgentFocusUiService
    {
        private FocusUiStatus? _status = status;

        public List<ManualFocusSessionRecordSummary> Saved { get; } = [];

        public List<ManualFocusAdjustment> Adjustments { get; } = [];

        public ManualFocusSessionRequest? StartRequest { get; private set; }

        public OperatorUiResult<ManualFocusSessionSnapshot>? CommandFailure { get; init; }

        public OperatorUiResultKind ObserveKind { get; init; } = OperatorUiResultKind.Success;

        public int StatusReads { get; private set; }

        public int SavedReads { get; private set; }

        public int ObserveCount { get; private set; }

        public int StopCount { get; private set; }

        public int SaveCount { get; private set; }

        public int DiscardCount { get; private set; }

        public ValueTask<OperatorUiResult<FocusUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
        {
            StatusReads++;
            return ValueTask.FromResult(_status is null
                ? OperatorUiResult<FocusUiStatus>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.")
                : OperatorUiResult<FocusUiStatus>.Success(_status));
        }

        public ValueTask<OperatorUiResult<IReadOnlyList<ManualFocusSessionRecordSummary>>> GetSavedSessionsAsync(CancellationToken cancellationToken)
        {
            SavedReads++;
            return ValueTask.FromResult(OperatorUiResult<IReadOnlyList<ManualFocusSessionRecordSummary>>.Success(Saved.ToArray()));
        }

        public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> StartAsync(ManualFocusSessionRequest request, CancellationToken cancellationToken)
        {
            StartRequest = request;
            return Command(Running() with { Settings = request.Settings, SimulatedFocusPosition = request.SimulatedFocusPosition });
        }

        public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> AdjustAsync(
            string sessionId, ManualFocusAdjustment adjustment, CancellationToken cancellationToken)
        {
            Adjustments.Add(adjustment);
            var session = _status!.Session;
            return Command(session with
            {
                Settings = adjustment.Settings ?? session.Settings,
                SimulatedFocusPosition = adjustment.SimulatedFocusPosition ?? session.SimulatedFocusPosition,
                Target = adjustment.Target ?? session.Target
            });
        }

        public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> ObserveAsync(string sessionId, CancellationToken cancellationToken)
        {
            ObserveCount++;
            if (ObserveKind != OperatorUiResultKind.Success)
            {
                _status = null;
                return ValueTask.FromResult(OperatorUiResult<ManualFocusSessionSnapshot>.Failure(ObserveKind, "Authorization is required."));
            }
            return ValueTask.FromResult(OperatorUiResult<ManualFocusSessionSnapshot>.Success(_status!.Session));
        }

        public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            StopCount++;
            return Command(_status!.Session with
            {
                State = ManualFocusSessionState.Stopped,
                EndedUtc = Started.AddMinutes(5),
                EndReason = ManualFocusReasonCodes.StoppedByOperator
            });
        }

        public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> SaveAsync(string sessionId, CancellationToken cancellationToken)
        {
            SaveCount++;
            const string recordId = "20261002T040500Z-session-1";
            Saved.Insert(0, Record(recordId, verified: true));
            return Command(_status!.Session with { Retention = ManualFocusRetentionState.Saved, SavedRecordId = recordId });
        }

        public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> DiscardAsync(string sessionId, CancellationToken cancellationToken)
        {
            DiscardCount++;
            return Command(_status!.Session with { Retention = ManualFocusRetentionState.Discarded, History = [], Best = null });
        }

        private ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> Command(ManualFocusSessionSnapshot session)
        {
            if (CommandFailure is { } failure)
            {
                return ValueTask.FromResult(failure);
            }
            _status = _status! with { Session = session, IsOwner = true };
            return ValueTask.FromResult(OperatorUiResult<ManualFocusSessionSnapshot>.Success(session));
        }
    }

    /// <summary>Drives the page poll deterministically: a timer fires only when the test ticks it.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => Started;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_timers)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void Tick()
        {
            ManualTimer[] timers;
            lock (_timers)
            {
                timers = [.. _timers];
            }
            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
