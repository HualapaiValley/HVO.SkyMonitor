using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class HealthPageTests
{
    [TestMethod]
    public void Health_GroupsChecksByScopeAndLinksEachToItsPage()
    {
        using var context = new BunitContext();
        Configure(context, out _, out _);

        var cut = context.Render<HealthPage>();

        cut.WaitForAssertion(() =>
        {
            var rail = cut.Find(".ops-status-rail");
            StringAssert.Contains(rail.TextContent, "Degraded", StringComparison.Ordinal);
            StringAssert.Contains(rail.TextContent, "1 of 5 checks need attention", StringComparison.Ordinal);
            StringAssert.Contains(rail.TextContent, "Heartbeat acknowledged 2s ago", StringComparison.Ordinal);
            Assert.AreEqual("Degraded", Chip(cut, "health-processing-heading"));
            Assert.AreEqual("Healthy", Chip(cut, "health-acquisition-heading"));
            Assert.AreEqual("Healthy", Chip(cut, "health-host-heading"));
            var processing = Row(cut, "Capture processing");
            Assert.AreEqual("Investigate", processing.QuerySelector("a")!.TextContent);
            Assert.AreEqual("/operations/pipeline", processing.QuerySelector("a")!.GetAttribute("href"));
            Assert.AreEqual("1.5 s", processing.Children[3].TextContent);
            Assert.AreEqual("View", Row(cut, "Camera configuration").QuerySelector("a")!.TextContent);
            StringAssert.Contains(Row(cut, "Host process").TextContent, "Passing", StringComparison.Ordinal);
            StringAssert.Contains(Row(cut, "Environmental acquisition").TextContent, "Switched off by configuration", StringComparison.Ordinal);
            Assert.IsNull(Row(cut, "Environmental acquisition").QuerySelector("a"));
            StringAssert.Contains(cut.Markup, "256.0 MiB", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "3.3% avg", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "2h 5m", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("form"));
        });
    }

    [TestMethod]
    public void Standalone_ShowsCentralChecksAsNotUsed()
    {
        using var context = new BunitContext();
        Configure(context, out var system, out _);
        system.HealthHandler = _ => ValueTask.FromResult(
            OperatorUiResult<SystemHealthView>.Success(TestSystemUiService.Health(centralEnabled: false)));

        var cut = context.Render<HealthPage>();

        cut.WaitForAssertion(() =>
        {
            var rail = cut.Find(".ops-status-rail");
            StringAssert.Contains(rail.TextContent, "Not used", StringComparison.Ordinal);
            StringAssert.Contains(rail.TextContent, "Standalone agent", StringComparison.Ordinal);
            var delivery = Row(cut, "Artifact delivery");
            StringAssert.Contains(delivery.TextContent, "Not used", StringComparison.Ordinal);
            StringAssert.Contains(delivery.TextContent, "Standalone agent", StringComparison.Ordinal);
            Assert.IsNull(delivery.QuerySelector("a"));
        });
    }

    [TestMethod]
    public void RefreshFailure_KeepsTheLastChecksAndSaysWhy()
    {
        using var context = new BunitContext();
        Configure(context, out var system, out _);
        var cut = context.Render<HealthPage>();
        cut.WaitForAssertion(() => Row(cut, "Capture processing"));
        system.HealthHandler = _ => ValueTask.FromResult(OperatorUiResult<SystemHealthView>.Failure(
            OperatorUiResultKind.Unavailable, "The health checks did not finish within 15 seconds."));

        cut.Find("#health-refresh").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, system.HealthReads);
            var alert = cut.Find(".ops-note-banner[role='alert']");
            StringAssert.Contains(alert.TextContent, "Showing the last completed checks.", StringComparison.Ordinal);
            StringAssert.Contains(alert.TextContent, "did not finish within 15 seconds", StringComparison.Ordinal);
            Assert.IsNotNull(Row(cut, "Capture processing"));
        });
    }

    [TestMethod]
    public void InitialFailure_RendersAnAlertWithoutAnyChecks()
    {
        using var context = new BunitContext();
        Configure(context, out var system, out _);
        system.HealthHandler = _ => ValueTask.FromResult(OperatorUiResult<SystemHealthView>.Failure(
            OperatorUiResultKind.Unavailable, "The health checks could not be run."));

        var cut = context.Render<HealthPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Health checks unavailable.", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("table"));
        });
    }

    [TestMethod]
    public void SummaryUnavailable_KeepsTheChecksAndMarksTheCaptureFactsUnknown()
    {
        using var context = new BunitContext();
        Configure(context, out _, out var operations);
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Failure(
            OperatorUiResultKind.Unavailable, "The operations summary is unavailable."));

        var cut = context.Render<HealthPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Capture and lane facts are unavailable.", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".ops-status-rail").TextContent, "Unknown", StringComparison.Ordinal);
            Assert.IsNotNull(Row(cut, "Capture processing"));
        });
    }

    [TestMethod]
    public void Unauthorized_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        Configure(context, out var system, out _);
        system.HealthHandler = _ => ValueTask.FromResult(OperatorUiResult<SystemHealthView>.Failure(
            OperatorUiResultKind.Unauthorized, "Denied."));

        context.Render<HealthPage>();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(-5d, "0s")]
    [DataRow(45d, "45s")]
    [DataRow(1800d, "30 min")]
    [DataRow(18_000d, "5 h")]
    [DataRow(259_200d, "3 d")]
    public void FormatAge_ScalesItsUnit(double seconds, string expected)
        => Assert.AreEqual(expected, HealthPage.FormatAge(TimeSpan.FromSeconds(seconds)));

    [TestMethod]
    [DataRow(250d, "250 ms")]
    [DataRow(1500d, "1.5 s")]
    [DataRow(90_000d, "1.5 min")]
    [DataRow(double.NaN, "Unavailable")]
    public void FormatDuration_ScalesMilliseconds(double milliseconds, string expected)
        => Assert.AreEqual(expected, HealthPage.FormatDuration(milliseconds));

    [TestMethod]
    [DataRow(93_600d, "1d 2h")]
    [DataRow(7_500d, "2h 5m")]
    [DataRow(184d, "3m 4s")]
    [DataRow(-1d, "0m 0s")]
    public void FormatDuration_ScalesSpans(double seconds, string expected)
        => Assert.AreEqual(expected, HealthPage.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [TestMethod]
    public void ClockDrift_ShowsTheMeasuredDriftAndWhenItWasMeasured()
    {
        using var context = new BunitContext();
        Configure(context, out var system, out _);
        system.HealthHandler = _ => ValueTask.FromResult(OperatorUiResult<SystemHealthView>.Success(
            TestSystemUiService.Health() with
            {
                Clock = new SystemClockFact(
                    ClockSyncStatus.InTolerance, TimeSpan.FromMilliseconds(12), OperatorUiTestData.Now.AddMinutes(-4))
            }));

        var cut = context.Render<HealthPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("+12 ms", cut.Find("#health-clock-drift").TextContent);
            Assert.AreEqual(
                "This agent's clock minus network time, measured 4 min ago",
                cut.Find("#health-clock-drift").GetAttribute("title"));
        });
    }

    [TestMethod]
    public void ClockDrift_WithoutAMeasurement_SaysWhy()
    {
        using var context = new BunitContext();
        Configure(context, out var system, out _);
        var cut = context.Render<HealthPage>();

        cut.WaitForAssertion(() => Assert.AreEqual("Unknown", cut.Find("#health-clock-drift").TextContent));

        system.HealthHandler = _ => ValueTask.FromResult(OperatorUiResult<SystemHealthView>.Success(
            TestSystemUiService.Health() with { Clock = new SystemClockFact(ClockSyncStatus.NotMeasured, null, null) }));
        cut.Find("#health-refresh").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Not measured", cut.Find("#health-clock-drift").TextContent);
            Assert.AreEqual("This agent's clock minus network time", cut.Find("#health-clock-drift").GetAttribute("title"));
        });
    }

    internal static void Configure(BunitContext context, out TestSystemUiService system, out TestOperatorUiService operations)
    {
        system = new TestSystemUiService();
        operations = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentSystemUiService>(system);
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(operations);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
    }

    private static string Chip(IRenderedComponent<HealthPage> cut, string headingId)
        => cut.Find($"section[aria-labelledby='{headingId}'] .state-chip").TextContent;

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<HealthPage> cut, string label)
        => cut.FindAll(".health-checks tbody tr").Single(row => row.QuerySelector("strong")!.TextContent == label);
}
