using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class DeliveryPageTests
{
    private static readonly DateTimeOffset Now = OperatorUiTestData.Now;

    [TestMethod]
    public void Render_ShowsStatusOutboxPolicyAndLanes()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => Success(WithArtifactOutbox(pending: 5, retry: 1, quarantine: 1));
        service.DeliveryHandler = _ => Records(
            Record(42, ArtifactOutboxStatus.Retry, reason: "LogicHostUnavailable"),
            Record(40, ArtifactOutboxStatus.Quarantined, reason: "PayloadRejected"),
            Record(41, ArtifactOutboxStatus.Acknowledged, acknowledgedUtc: Now.AddMinutes(-3)));

        var cut = context.Render<DeliveryPage>();

        cut.WaitForElement(".delivery-outbox-table");
        Assert.AreEqual("Delivery", cut.Find("h1").TextContent.Trim());
        Assert.AreEqual("Enabled", Rail(cut, "Mode").Strong);
        Assert.AreEqual("Captures and environment", Rail(cut, "Mode").Small);
        Assert.AreEqual("Available", Rail(cut, "LogicHost").Strong);
        Assert.AreEqual(("4", "1 retrying / 1 quarantined"), Rail(cut, "Waiting"));
        Assert.AreEqual(("3 min ago", "Capture #41"), Rail(cut, "Last acknowledgment"));
        Assert.AreEqual("4 active / 1 quarantined", cut.Find(".delivery-outbox .ops-panel-heading > span").TextContent);

        var rows = cut.FindAll(".delivery-outbox-table tbody tr");
        Assert.HasCount(3, rows);
        StringAssert.Contains(rows[0].TextContent, "Retry at 23 Jul 2026 12:05:00 +00:00 (UTC (site time zone unavailable))", StringComparison.Ordinal);
        StringAssert.Contains(rows[0].TextContent, "Logic Host Unavailable", StringComparison.Ordinal);
        Assert.IsTrue(rows[0].ClassList.Contains("current-row"));
        Assert.AreEqual("/operations/quarantine", rows[1].QuerySelector("a")!.GetAttribute("href"));
        Assert.AreEqual("Acknowledged", rows[2].QuerySelector(".state-chip")!.TextContent);
        // One storage alias means the alias column adds nothing, so it is not shown.
        Assert.IsFalse(cut.Find(".delivery-outbox-table").TextContent.Contains("captures", StringComparison.Ordinal));

        Assert.AreEqual("Enabled", Fact(cut, ".delivery-policy", "Capture artifacts"));
        Assert.AreEqual("Unlimited", Fact(cut, ".delivery-policy", "Bandwidth limit"));
        Assert.AreEqual("10s to 5 min", Fact(cut, ".delivery-policy", "Retry backoff"));
        Assert.AreEqual("10 every 10s", Fact(cut, ".delivery-policy", "Batch"));

        var lanes = cut.FindAll(".delivery-lane-table tbody tr").Select(static row => row.QuerySelector("strong")!.TextContent).ToArray();
        string[] expectedLanes = ["Capture artifacts", "Environment", "Heartbeat", "Execution evidence"];
        CollectionAssert.AreEqual(expectedLanes, lanes);
        Assert.AreEqual("4", Cells(cut, "Capture artifacts")[2]);
        Assert.AreEqual("None", Cells(cut, "Environment")[5]);
    }

    [TestMethod]
    public void Records_FromSeveralStorageAliases_AreGroupedUnderEachLocation()
    {
        using var context = CreateContext(out var service);
        service.DeliveryHandler = _ => Records(
            Record(1, ArtifactOutboxStatus.Pending),
            Record(2, ArtifactOutboxStatus.Retry),
            Record(3, ArtifactOutboxStatus.Pending) with { StorageAlias = "archive" });

        var cut = context.Render<DeliveryPage>();

        cut.WaitForElement(".delivery-outbox-table");
        var groups = cut.FindAll(".delivery-outbox-table tbody");
        Assert.HasCount(2, groups);
        Assert.AreEqual("captures", groups[0].QuerySelector("th[scope='rowgroup']")!.TextContent);
        Assert.AreEqual(2, groups[0].QuerySelectorAll("tr:not(.delivery-location)").Length);
        Assert.AreEqual("archive", groups[1].QuerySelector("th[scope='rowgroup']")!.TextContent);
        Assert.AreEqual(1, groups[1].QuerySelectorAll("tr:not(.delivery-location)").Length);
        StringAssert.Contains(cut.Find(".delivery-footnote").TextContent, "up to 25 per location", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Records_FromOneStorageAlias_NeedNoLocationHeading()
    {
        using var context = CreateContext(out var service);
        service.DeliveryHandler = _ => Records(Record(1, ArtifactOutboxStatus.Pending), Record(2, ArtifactOutboxStatus.Pending));

        var cut = context.Render<DeliveryPage>();

        cut.WaitForElement(".delivery-outbox-table");
        Assert.IsEmpty(cut.FindAll(".delivery-location"));
        Assert.AreEqual(
            "Unfinished work first, newest queued first, then the newest completed records, up to 25.",
            cut.Find(".delivery-footnote").TextContent);
    }

    [TestMethod]
    public void RetryEligible_IsDisabledAndSaysWhereRecoveryHappens()
    {
        using var context = CreateContext(out _);

        var cut = context.Render<DeliveryPage>();

        var button = cut.Find("#delivery-retry");
        Assert.IsTrue(button.HasAttribute("disabled"));
        Assert.AreEqual("delivery-retry-reason", button.GetAttribute("aria-describedby"));
        Assert.AreEqual(DeliveryPage.RetryUnavailableReason, cut.Find("#delivery-retry-reason").TextContent);
    }

    [TestMethod]
    public void PolicyDialog_ShowsConfiguredBoundsAndReturnsFocus()
    {
        using var context = CreateContext(out _);
        var cut = context.Render<DeliveryPage>();
        cut.WaitForElement(".delivery-policy .ops-facts");

        cut.Find("#delivery-policy").Click();

        var text = cut.Find("dialog.delivery-dialog").TextContent;
        StringAssert.Contains(text, "5 min", StringComparison.Ordinal);
        StringAssert.Contains(text, "100 items", StringComparison.Ordinal);
        StringAssert.Contains(text, "50 items", StringComparison.Ordinal);
        Assert.AreEqual("10", Fact(cut, "dialog.delivery-dialog", "Attempts before quarantine"));
        Assert.AreEqual(1, context.JSInterop.Invocations.Count(static invocation => invocation.Identifier == "showModal"));

        cut.Find("#delivery-policy-close").Click();

        Assert.IsEmpty(cut.FindAll("dialog.delivery-dialog"));
        cut.WaitForAssertion(() => Assert.AreEqual(1, context.JSInterop.Invocations.Count(static invocation =>
            invocation.Identifier == "focusById" && Equals(invocation.Arguments[0], "delivery-policy"))));
    }

    [TestMethod]
    public void PolicyUnavailable_DisablesTheDialogWithTheReason()
    {
        using var context = CreateContext(out var service);
        service.SystemHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentSystemStatus>.Failure(OperatorUiResultKind.Unavailable, "Configuration is not loaded."));

        var cut = context.Render<DeliveryPage>();

        cut.WaitForElement(".delivery-lane-table");
        var button = cut.Find("#delivery-policy");
        Assert.IsTrue(button.HasAttribute("disabled"));
        Assert.AreEqual("Configuration is not loaded.", cut.Find("#delivery-policy-reason").TextContent);
        StringAssert.Contains(cut.Find(".delivery-policy [role='alert']").TextContent, "Configuration is not loaded.", StringComparison.Ordinal);
        Assert.AreEqual("Export policy unavailable", Rail(cut, "Mode").Small);
    }

    [TestMethod]
    public void CentralIntegrationDisabled_SaysTheAgentRunsStandalone()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => Success(OperatorUiTestData.Operations(centralIntegration: "Disabled"));

        var cut = context.Render<DeliveryPage>();

        cut.WaitForElement(".delivery-disabled");
        StringAssert.Contains(cut.Find(".delivery-disabled").TextContent, "local capture, processing and retention are unaffected", StringComparison.Ordinal);
        Assert.AreEqual("Disabled", Rail(cut, "Mode").Strong);
        Assert.AreEqual(("Not used", "Standalone agent"), Rail(cut, "LogicHost"));
        StringAssert.Contains(cut.Find(".delivery-empty").TextContent, "Central integration is disabled", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UnreadQueues_ShowUnknownCountsRatherThanZero()
    {
        using var context = CreateContext(out var service);
        var view = OperatorUiTestData.Operations();
        var summary = view.Summary;
        service.OperationsHandler = _ => Success(view with
        {
            Summary = summary with
            {
                ArtifactOutbox = summary.ArtifactOutbox with { ObservedUtc = null, Freshness = OperationsFreshness.Unknown },
                EnvironmentalDelivery = summary.EnvironmentalDelivery with { ObservedUtc = null, Freshness = OperationsFreshness.Unknown },
            },
        });

        var cut = context.Render<DeliveryPage>();

        cut.WaitForElement(".delivery-lane-table");
        Assert.AreEqual(("Unknown", "Outbox not read yet"), Rail(cut, "Waiting"));
        Assert.AreEqual("Not read yet", cut.Find(".delivery-outbox .ops-panel-heading > span").TextContent);
        string[] unread = ["Unknown", "Unknown", "Unknown", "Not read yet"];
        CollectionAssert.AreEqual(unread, Cells(cut, "Environment")[2..]);
        CollectionAssert.AreEqual(unread, Cells(cut, "Capture artifacts")[2..]);
        // A queue that has been read still reports its real zero.
        string[] empty = ["0", "0", "0", "None"];
        CollectionAssert.AreEqual(empty, Cells(cut, "Heartbeat")[2..]);
    }

    [TestMethod]
    public void RecordsUnavailable_SaysSoInsideTheOutboxPanel()
    {
        using var context = CreateContext(out var service);
        service.DeliveryHandler = _ => ValueTask.FromResult(
            OperatorUiResult<IReadOnlyList<CameraAgentDeliveryRecord>>.Failure(OperatorUiResultKind.Unavailable, "The outbox is locked."));

        var cut = context.Render<DeliveryPage>();

        cut.WaitForElement(".delivery-outbox [role='alert']");
        Assert.AreEqual("The outbox is locked.", cut.Find(".delivery-outbox [role='alert']").TextContent);
        Assert.AreEqual(("None recorded", "Outbox records unavailable"), Rail(cut, "Last acknowledgment"));
        Assert.IsNotNull(cut.Find(".delivery-lane-table"));
    }

    [TestMethod]
    public void FailedRefresh_KeepsLastValidSnapshotAndSaysWhy()
    {
        using var context = CreateContext(out var service);
        var cut = context.Render<DeliveryPage>();
        cut.WaitForElement(".delivery-lane-table");
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "The durable stores are locked."));

        cut.Find("#delivery-refresh").Click();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find(".ops-note-banner[role='alert']").TextContent;
            StringAssert.Contains(alert, "Showing the last valid snapshot.", StringComparison.Ordinal);
            StringAssert.Contains(alert, "The durable stores are locked.", StringComparison.Ordinal);
            Assert.IsNotNull(cut.Find(".delivery-lane-table"));
        });
    }

    [TestMethod]
    public void InitialFailure_RendersTheErrorWithRefreshAvailable()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "Nothing yet."));

        var cut = context.Render<DeliveryPage>();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Nothing yet.", StringComparison.Ordinal));
        Assert.IsFalse(cut.Find("#delivery-refresh").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(".delivery-lane-table"));
    }

    [TestMethod]
    [DataRow("operations")]
    [DataRow("records")]
    [DataRow("system")]
    public void AnUnauthorizedRead_NavigatesToAccessDenied(string read)
    {
        using var context = CreateContext(out var service);
        switch (read)
        {
            case "operations":
                service.OperationsHandler = _ => ValueTask.FromResult(
                    OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
                break;
            case "records":
                service.DeliveryHandler = _ => ValueTask.FromResult(
                    OperatorUiResult<IReadOnlyList<CameraAgentDeliveryRecord>>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
                break;
            default:
                service.SystemHandler = _ => ValueTask.FromResult(
                    OperatorUiResult<CameraAgentSystemStatus>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
                break;
        }

        _ = context.Render<DeliveryPage>();

        Assert.IsTrue(context.Services.GetRequiredService<NavigationManager>().Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
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

    private static ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentDeliveryRecord>>> Records(params CameraAgentDeliveryRecord[] records)
        => ValueTask.FromResult(OperatorUiResult<IReadOnlyList<CameraAgentDeliveryRecord>>.Success(records));

    private static CameraAgentOperationsView WithArtifactOutbox(long pending, long retry, long quarantine)
    {
        var view = OperatorUiTestData.Operations();
        var summary = view.Summary;
        return view with
        {
            Summary = summary with
            {
                ArtifactOutbox = summary.ArtifactOutbox with
                {
                    Value = summary.ArtifactOutbox.Value with { PendingCount = pending, RetryCount = retry, QuarantineCount = quarantine },
                },
            },
        };
    }

    private static CameraAgentDeliveryRecord Record(
        long sequence, ArtifactOutboxStatus status, DateTimeOffset? acknowledgedUtc = null, string? reason = null)
        => new("captures", new ArtifactOutboxDeliveryRecord(
            sequence, FrameArtifactRole.Preview, "image/png", 2048, status,
            status == ArtifactOutboxStatus.Pending ? 0 : 2,
            Now.AddMinutes(-10), Now.AddMinutes(-1), Now.AddMinutes(5), acknowledgedUtc, reason));

    private static (string Strong, string Small) Rail(IRenderedComponent<DeliveryPage> cut, string label)
    {
        var item = cut.FindAll(".ops-status-rail > div").Single(div => div.QuerySelector("span")?.TextContent == label);
        return (item.QuerySelector("strong")!.TextContent, item.QuerySelector("small")!.TextContent);
    }

    private static string[] Cells(IRenderedComponent<DeliveryPage> cut, string lane)
        => cut.FindAll(".delivery-lane-table tbody tr")
            .Single(row => row.QuerySelector("strong")?.TextContent == lane)
            .QuerySelectorAll("td")
            .Select(static cell => cell.TextContent.Trim())
            .ToArray();

    private static string Fact(IRenderedComponent<DeliveryPage> cut, string scope, string term)
    {
        var fact = cut.FindAll($"{scope} .ops-facts > div")
            .First(item => item.QuerySelector("dt")?.TextContent == term);
        return fact.QuerySelector("dd")!.TextContent;
    }
}
