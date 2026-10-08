using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class StoragePageTests
{
    private static readonly DateTimeOffset Now = OperatorUiTestData.Now;

    [TestMethod]
    public void Render_ShowsCapacityPolicyClassesAndLanesWithoutPaths()
    {
        using var context = CreateContext(out _);

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-capacity");
        Assert.AreEqual("Storage & retention", cut.Find("h1").TextContent.Trim());
        Assert.AreEqual("50%", cut.Find(".storage-capacity .ops-donut span").TextContent);
        Assert.AreEqual("--used: 50%", cut.Find(".storage-capacity .ops-donut").GetAttribute("style"));
        Assert.AreEqual("Healthy", cut.Find(".storage-capacity .state-chip").TextContent);
        Assert.AreEqual("Below 10% free", Fact(cut, ".storage-capacity", "Pressure starts"));
        Assert.AreEqual("At 15% free", Fact(cut, ".storage-capacity", "Recovers"));
        Assert.AreEqual("3 days", Fact(cut, ".storage-capacity", "Retention now"));
        var classes = cut.Find(".storage-class-table").TextContent;
        StringAssert.Contains(classes, "Captures & products", StringComparison.Ordinal);
        StringAssert.Contains(classes, "Disk-pressure history", StringComparison.Ordinal);
        StringAssert.Contains(classes, "31 days", StringComparison.Ordinal);
        StringAssert.Contains(classes, "Raw ingress reserve", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".storage-lane-table").TextContent, "Standard", StringComparison.Ordinal);
        Assert.IsNotNull(cut.Find(".storage-holds a[href='/operations/quarantine']"));
        Assert.IsEmpty(cut.FindAll(".storage-pressure"));
        // A storage alias is an identity, not a path; roots and paths are never rendered.
        Assert.IsFalse(cut.Markup.Contains("/var/", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("raw-ingress", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_UnderPressure_ShowsBannerShortenedRetentionAndCriticalLane()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => Success(OperatorUiTestData.Operations(lanePressure: 2, storagePressure: true));

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-pressure");
        Assert.AreEqual("Under pressure", cut.Find(".storage-capacity .state-chip").TextContent);
        Assert.AreEqual("3 days, shortened by disk pressure", Fact(cut, ".storage-capacity", "Retention now"));
        StringAssert.Contains(cut.Find(".storage-class-table").TextContent, "Active", StringComparison.Ordinal);
        var laneChip = cut.Find(".storage-lane-table tbody tr:last-child .state-chip");
        Assert.AreEqual("Critical", laneChip.TextContent);
        StringAssert.Contains(laneChip.ClassName, "failure", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_WhenCapacityProbeFailed_SaysUnknownInsteadOfAPercentage()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => Success(WithStorage(new OperationsStorageState("captures", 0, 0, false, 30, false)));

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-capacity");
        Assert.AreEqual("Unknown", cut.Find(".storage-capacity .state-chip").TextContent);
        Assert.IsNotNull(cut.Find(".storage-capacity .ops-donut.unknown"));
        Assert.IsFalse(cut.Find(".storage-capacity .ops-donut").TextContent.Contains('%', StringComparison.Ordinal));
        StringAssert.Contains(Fact(cut, ".storage-capacity", "Capacity"), "Unknown", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".storage-capacity dt").Where(static dt => dt.TextContent == "Used"));
    }

    [TestMethod]
    public void Render_WithoutReportedStorage_ExplainsTheFirstSweepRecordsIt()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => Success(WithStorage());

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-capacity");
        StringAssert.Contains(cut.Find(".storage-capacity .storage-empty").TextContent, "first retention sweep", StringComparison.Ordinal);
        Assert.AreEqual("Unknown", cut.Find(".storage-capacity .state-chip").TextContent);
    }

    [TestMethod]
    public void Holds_ListQuarantineOnceAndExcludeItFromWaitingWork()
    {
        using var context = CreateContext(out var service);
        var view = OperatorUiTestData.Operations();
        var summary = view.Summary;
        var laneState = summary.CaptureLanes.Value with { PendingCount = 12, QuarantineCount = 2 };
        service.OperationsHandler = _ => Success(view with
        {
            Summary = summary with
            {
                RawIngress = summary.RawIngress with { Value = summary.RawIngress.Value with { PendingCount = 3, QuarantineCount = 1, OldestPendingUtc = Now.AddMinutes(-5) } },
                CaptureLanes = summary.CaptureLanes with { Value = laneState },
                ArtifactOutbox = summary.ArtifactOutbox with { Value = summary.ArtifactOutbox.Value with { PendingCount = 5, QuarantineCount = 1, OldestPendingUtc = Now.AddMinutes(-20) } },
            },
        });

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-holds");
        Assert.AreEqual("3", Fact(cut, ".storage-holds", "Raw captures held"));
        Assert.AreEqual("10", Fact(cut, ".storage-holds", "Capture lanes"));
        Assert.AreEqual("4", Fact(cut, ".storage-holds", "Delivery"));
        Assert.AreEqual("4", Fact(cut, ".storage-holds", "Quarantine"));
        Assert.AreEqual("23 Jul 2026 11:40:00 +00:00 (UTC (site time zone unavailable)), Delivery", Fact(cut, ".storage-holds", "Oldest held evidence"));
    }

    [TestMethod]
    public void Holds_WhenLaneWorkPinsRawCaptures_SayTheFiguresOverlap()
    {
        // Two held raw captures are pinned by the same two lane items, one of them quarantined. The rows count the
        // same evidence from different sides, so the panel must not read as a breakdown of disjoint waiting work.
        using var context = CreateContext(out var service);
        var view = OperatorUiTestData.Operations();
        var summary = view.Summary;
        service.OperationsHandler = _ => Success(view with
        {
            Summary = summary with
            {
                RawIngress = summary.RawIngress with { Value = summary.RawIngress.Value with { PendingCount = 2, QuarantineCount = 0 } },
                CaptureLanes = summary.CaptureLanes with { Value = summary.CaptureLanes.Value with { PendingCount = 2, QuarantineCount = 1 } },
                ArtifactOutbox = summary.ArtifactOutbox with { Value = summary.ArtifactOutbox.Value with { PendingCount = 0, QuarantineCount = 0 } },
            },
        });

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-holds");
        Assert.AreEqual("2", Fact(cut, ".storage-holds", "Raw captures held"));
        Assert.AreEqual("1", Fact(cut, ".storage-holds", "Capture lanes"));
        Assert.AreEqual("1", Fact(cut, ".storage-holds", "Quarantine"));
        Assert.IsEmpty(cut.FindAll(".storage-holds dt").Where(static term => term.TextContent == "Raw ingress"));
        StringAssert.Contains(
            cut.Find(".storage-holds .ops-panel-heading p").TextContent,
            "the figures overlap and are not a total",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void Reconciliation_BeforeAnyPass_SaysNothingHasRunYet()
    {
        using var context = CreateContext(out _);

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-reconciliation");
        var items = cut.FindAll(".storage-reconciliation .ops-validation-item");
        Assert.HasCount(2, items);
        Assert.IsTrue(items.All(static item => item.ClassList.Contains("neutral")));
        StringAssert.Contains(items[0].TextContent, "no reconciliation has completed", StringComparison.Ordinal);
        StringAssert.Contains(items[1].TextContent, "within 5 minutes", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".storage-reconciliation .ops-panel-heading > span"));
    }

    [TestMethod]
    public void Reconciliation_ShowsRecordedCountsAndFlagsProblems()
    {
        using var context = CreateContext(out var service);
        service.ReconciliationHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentStorageReconciliation>.Success(new(
            new RawIngressReconciliationReport(Now.AddHours(-2), 40, 2, 1, 1, 0, 0),
            new DerivedProductReconciliationReport(Now.AddMinutes(-3), true, 120, 0, 3, 0, 0))));

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-reconciliation .ops-validation-item");
        var items = cut.FindAll(".storage-reconciliation .ops-validation-item");
        Assert.HasCount(3, items);
        StringAssert.Contains(items[0].TextContent, "40 frames inspected, 2 recovered, 1 cleaned up", StringComparison.Ordinal);
        Assert.IsTrue(items[0].ClassList.Contains("failed"));
        StringAssert.Contains(items[1].TextContent, "1 quarantined and 0 missing", StringComparison.Ordinal);
        StringAssert.Contains(items[2].TextContent, "120 products inspected", StringComparison.Ordinal);
        Assert.IsFalse(items[2].ClassList.Contains("failed"));
        Assert.AreEqual("Passed:", items[2].QuerySelector(".visually-hidden")!.TextContent);
        Assert.AreEqual("Last 23 Jul 2026 11:57:00 +00:00 (UTC (site time zone unavailable))", cut.Find(".storage-reconciliation .ops-panel-heading > span").TextContent);
    }

    [TestMethod]
    public void Reconciliation_WhenDerivedCheckFailed_SaysSoAndWhenItRetries()
    {
        using var context = CreateContext(out var service);
        service.ReconciliationHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentStorageReconciliation>.Success(new(
            null, new DerivedProductReconciliationReport(Now.AddMinutes(-1), false, 0, 0, 0, 0, 0))));

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-reconciliation .ops-validation-item.failed");
        StringAssert.Contains(cut.Find(".storage-reconciliation .ops-validation-item.failed").TextContent,
            "failed. It runs again within 5 minutes", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RunReconciliation_IsDisabledWithTheAutomaticSchedule()
    {
        using var context = CreateContext(out _);

        var cut = context.Render<StoragePage>();

        var button = cut.Find("#storage-reconcile");
        Assert.IsTrue(button.HasAttribute("disabled"));
        Assert.AreEqual("storage-reconcile-reason", button.GetAttribute("aria-describedby"));
        Assert.AreEqual(StoragePage.ReconcileUnavailableReason, cut.Find("#storage-reconcile-reason").TextContent);
    }

    [TestMethod]
    public void PolicyDialog_ShowsConfiguredBoundsAndCloses()
    {
        using var context = CreateContext(out _);
        var cut = context.Render<StoragePage>();
        cut.WaitForElement(".storage-capacity");

        cut.Find("#storage-policy").Click();

        var dialog = cut.Find("dialog.storage-dialog");
        var text = dialog.TextContent;
        StringAssert.Contains(text, "Every 30 minutes", StringComparison.Ordinal);
        StringAssert.Contains(text, "Below 10% free", StringComparison.Ordinal);
        StringAssert.Contains(text, "At 15% free", StringComparison.Ordinal);
        StringAssert.Contains(text, "1 day, or less", StringComparison.Ordinal);
        StringAssert.Contains(text, "100,000 observations", StringComparison.Ordinal);
        StringAssert.Contains(text, "never removes evidence", StringComparison.Ordinal);
        Assert.AreEqual(1, context.JSInterop.Invocations.Count(static invocation => invocation.Identifier == "showModal"));

        cut.Find("#storage-policy-close").Click();

        Assert.IsEmpty(cut.FindAll("dialog.storage-dialog"));
        cut.WaitForAssertion(() => Assert.AreEqual(1, context.JSInterop.Invocations.Count(static invocation =>
            invocation.Identifier == "focusById" && Equals(invocation.Arguments[0], "storage-policy"))));
    }

    [TestMethod]
    public void PolicyUnavailable_DisablesTheDialogWithTheReason()
    {
        using var context = CreateContext(out var service);
        service.SystemHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentSystemStatus>.Failure(OperatorUiResultKind.Unavailable, "Configuration is not loaded."));

        var cut = context.Render<StoragePage>();

        cut.WaitForElement(".storage-capacity");
        var button = cut.Find("#storage-policy");
        Assert.IsTrue(button.HasAttribute("disabled"));
        Assert.AreEqual("Configuration is not loaded.", cut.Find("#storage-policy-reason").TextContent);
        Assert.IsEmpty(cut.FindAll(".storage-capacity dt").Where(static dt => dt.TextContent == "Pressure starts"));
    }

    [TestMethod]
    public void FailedRefresh_KeepsLastValidSnapshotAndSaysWhy()
    {
        using var context = CreateContext(out var service);
        var cut = context.Render<StoragePage>();
        cut.WaitForElement(".storage-capacity");
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "The durable stores are locked."));

        cut.Find("#storage-refresh").Click();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Showing the last valid snapshot.", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("[role='alert']").TextContent, "The durable stores are locked.", StringComparison.Ordinal);
            Assert.IsNotNull(cut.Find(".storage-capacity"));
        });
    }

    [TestMethod]
    public void InitialFailure_RendersTheErrorWithRefreshAvailable()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "Nothing yet."));

        var cut = context.Render<StoragePage>();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Nothing yet.", StringComparison.Ordinal));
        Assert.IsFalse(cut.Find("#storage-refresh").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Render_WhenUnauthorized_NavigatesToAccessDenied()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "denied"));

        _ = context.Render<StoragePage>();

        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UsedPercent_IsNullUnlessTheProbeMeasuredCapacity()
    {
        Assert.AreEqual(25, StoragePage.UsedPercent(new OperationsStorageState("a", 400, 300, false, 1, true)));
        Assert.AreEqual(100, StoragePage.UsedPercent(new OperationsStorageState("a", 400, -5, false, 1, true)));
        Assert.IsNull(StoragePage.UsedPercent(new OperationsStorageState("a", 400, 300, false, 1, false)));
        Assert.IsNull(StoragePage.UsedPercent(new OperationsStorageState("a", 0, 0, false, 1, true)));
    }

    private static BunitContext CreateContext(out TestOperatorUiService service)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        return context;
    }

    private static ValueTask<OperatorUiResult<CameraAgentOperationsView>> Success(CameraAgentOperationsView view)
        => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(view));

    private static CameraAgentOperationsView WithStorage(params OperationsStorageState[] storage)
    {
        var view = OperatorUiTestData.Operations();
        return view with { Summary = view.Summary with { Storage = view.Summary.Storage with { Value = storage } } };
    }

    private static string Fact(IRenderedComponent<StoragePage> cut, string scope, string term)
    {
        var fact = cut.FindAll($"{scope} .ops-facts > div")
            .Single(item => item.QuerySelector("dt")?.TextContent == term);
        return fact.QuerySelector("dd")!.TextContent;
    }
}
