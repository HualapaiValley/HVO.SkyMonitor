using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
[TestProperty(CameraAgentTestAreas.PropertyName, CameraAgentTestAreas.Ui)]
public sealed class AutomationRunProductsTests
{
    [TestMethod]
    public void RetainedWindowWithDifferentTimeZoneRulesShowsOnlyItsExactUtcTime()
    {
        using var fixture = new AutomationWizardFixture("America/Denver");
        var window = fixture.Planner.ResolveWindows(new DateOnly(2026, 11, 1),
            LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.SunriseDay)).Single();
        var known = PredefinedAutomationRunControls.WindowTime(window.StartUtc, window);
        Assert.Contains("-07:00 / UTC", known);
        var oldRules = window with
        {
            ReportingPeriod = window.ReportingPeriod with { TimeZoneRulesSha256 = new string('0', 64) }
        };
        var retained = PredefinedAutomationRunControls.WindowTime(oldRules.StartUtc, oldRules);
        Assert.StartsWith("UTC ", retained);
        Assert.Contains("retained local offset unavailable", retained);
    }

    [TestMethod]
    public void HourlyAndDailyLinksMatchOnlyTheOccurrenceThatRequestedTheVideo()
    {
        using var fixture = new AutomationWizardFixture();
        var date = new DateOnly(2026, 10, 12);
        var hourly = Occurrence("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour);
        var daily = Occurrence("daily", LocalAutomationSourceWindowKind.SunriseDay);
        var anotherHourly = Occurrence("another", LocalAutomationSourceWindowKind.CompletedCivilHour);
        var hourlyId = Guid.NewGuid();
        var dailyId = Guid.NewGuid();
        var view = new CameraAgentNightlyDayView(new(date, [], []), null)
        {
            TimeLapses = new(date,
                [Job(hourly, null, hourlyId), Job(daily, null, dailyId),
                    Job(daily, Guid.NewGuid(), Guid.NewGuid()), Job(anotherHourly, null, Guid.NewGuid())], [])
        };
        Assert.AreEqual(($"/archive/products/time-lapse/{hourlyId:D}", "Hourly time-lapse"),
            AutomationRunProducts.ProductsFor(view, hourly).Single());
        Assert.AreEqual(($"/archive/products/time-lapse/{dailyId:D}", "Daily time-lapse"),
            AutomationRunProducts.ProductsFor(view, daily).Single());
        Assert.IsEmpty(AutomationRunProducts.ProductsFor(view, Occurrence("unpublished", LocalAutomationSourceWindowKind.SunriseDay)));

        LocalAutomationOccurrence Occurrence(string id, LocalAutomationSourceWindowKind kind)
        {
            var policy = LocalAutomationWindowTests.Policy(kind);
            var definition = LocalAutomationWindowTests.Definition(id, policy) with
            { TaskKind = LocalAutomationTaskKind.TimeLapseGeneration };
            var entry = new LocalAutomationRunnerEntry(definition, 1, LocalAutomationContract.ComputeRevisionSha256(definition), null, null);
            return LocalAutomationWindowPlanner.CreateOccurrence(entry, fixture.Planner.ResolveWindows(date, policy)[0]);
        }

        static CameraAgentTimeLapseJob Job(LocalAutomationOccurrence occurrence, Guid? parent, Guid productId)
            => new(Guid.NewGuid(), occurrence, occurrence.SourceWindow!, parent, null!, CameraAgentTimeLapseState.Produced,
                occurrence.ScheduledForUtc, null, productId);
    }
}
