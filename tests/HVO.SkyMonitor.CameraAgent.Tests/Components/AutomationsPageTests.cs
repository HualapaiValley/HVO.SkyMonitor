using Bunit;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class AutomationsPageTests
{
    [TestMethod]
    public void Render_ListsRegisteredScheduleAndSourceTasksWithoutCustomDefinitions()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new EnvironmentalUiService(Status()));

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Automations", cut.Find("h1").TextContent.Trim());
            Assert.IsTrue(cut.Markup.Contains("Capture schedule", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Next scheduled activity", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("virtual-sky-temperature", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("on demand available", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Trigger kinds", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("On Demand", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Custom definitions", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("records nothing new", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll("[role='alert']"));
            Assert.IsNotNull(cut.Find("a[href='/operations/schedule']"));
            Assert.IsNotNull(cut.Find("a[href='/operations/environment']"));
        });
    }

    [TestMethod]
    public void Render_WhenOneSourceFails_ShowsPartialDataNotice()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new EnvironmentalUiService(null));

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("Showing partial data", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("environmental store is offline", StringComparison.Ordinal));
            Assert.IsTrue(cut.Markup.Contains("Capture schedule", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll("#automation-environment"));
        });
    }

    private static EnvironmentalUiStatus Status() => new(
        true,
        DateTimeOffset.Parse("2026-09-04T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        12,
        4096,
        0,
        [
            new EnvironmentalUiSource(
                "virtual-sky-temperature",
                EnvironmentalObservationKind.AirTemperature,
                true,
                true,
                "Fresh",
                null,
                null,
                DateTimeOffset.Parse("2026-09-04T02:59:00Z", System.Globalization.CultureInfo.InvariantCulture),
                60,
                DateTimeOffset.Parse("2026-09-04T03:05:00Z", System.Globalization.CultureInfo.InvariantCulture),
                0)
        ],
        []);

    private sealed class EnvironmentalUiService(EnvironmentalUiStatus? status) : ICameraAgentEnvironmentalUiService
    {
        public ValueTask<OperatorUiResult<EnvironmentalUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(status is null
                ? OperatorUiResult<EnvironmentalUiStatus>.Failure(OperatorUiResultKind.Unavailable, "The environmental store is offline.")
                : OperatorUiResult<EnvironmentalUiStatus>.Success(status));

        public ValueTask<OperatorUiResult<EnvironmentalUiHistoryPage>> GetHistoryAsync(
            EnvironmentalObservationKind? kind, int pageSize, string? cursor, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>> AcquireAsync(
            string sourceId, string idempotencyKey, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
