using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalPageTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SuccessRendersFreshSourceHistoryAttemptsAndLoadsOlderPage()
    {
        using var context = new BunitContext();
        var service = new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Success(new(
                true,
                Epoch,
                2,
                512,
                0,
                [new EnvironmentalUiSource(
                    "virtual-rain", EnvironmentalObservationKind.RainState, true, "Fresh",
                    EnvironmentalAcquisitionDisposition.Produced, "produced", Epoch, 2, Epoch.AddSeconds(30), 0)],
                [new EnvironmentalAcquisitionAttemptRecord(
                    1, "virtual-rain", EnvironmentalObservationKind.RainState, true,
                    EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionDisposition.Produced,
                    "produced", Guid.NewGuid(), null, null, Epoch, Epoch.AddMilliseconds(10))])),
            History = cursor => OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new(
                [new EnvironmentalUiObservation(
                    Guid.NewGuid(), cursor is null ? "virtual-rain" : "older-rain",
                    EnvironmentalObservationKind.RainState, EnvironmentalObservationUnit.Boolean,
                    null, true, EnvironmentalObservationQuality.Good, null, Epoch, Epoch.AddSeconds(45))],
                cursor is null ? "older-cursor" : null))
        };
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(service);

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "virtual-rain", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Fresh", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "True", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "produced", StringComparison.Ordinal);
        });
        cut.FindAll("button").Single(button => button.TextContent.Contains("Older", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("older-cursor", service.LastCursor);
            StringAssert.Contains(cut.Markup, "older-rain", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void UnauthorizedStatusNavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.")
        });

        var cut = context.Render<EnvironmentalPage>();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => StringAssert.EndsWith(
            navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FailureRendersOnlySanitizedAlert()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new TestEnvironmentalUiService
        {
            Status = OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental status is unavailable.")
        });

        var cut = context.Render<EnvironmentalPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("alert", cut.Find("[role=alert]").GetAttribute("role"));
            StringAssert.Contains(cut.Markup, "Environmental status is unavailable.", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("/tmp/", StringComparison.Ordinal));
        });
    }

    private sealed class TestEnvironmentalUiService : ICameraAgentEnvironmentalUiService
    {
        public OperatorUiResult<EnvironmentalUiStatus> Status { get; init; } =
            OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental status is unavailable.");
        public Func<string?, OperatorUiResult<EnvironmentalUiHistoryPage>> History { get; init; } =
            _ => OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new([], null));
        public string? LastCursor { get; private set; }

        public ValueTask<OperatorUiResult<EnvironmentalUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Status);

        public ValueTask<OperatorUiResult<EnvironmentalUiHistoryPage>> GetHistoryAsync(
            EnvironmentalObservationKind? kind,
            int pageSize,
            string? cursor,
            CancellationToken cancellationToken)
        {
            LastCursor = cursor;
            return ValueTask.FromResult(History(cursor));
        }
    }
}
