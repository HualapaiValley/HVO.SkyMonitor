using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentAutomationPreviewTests
{
    [TestMethod]
    public async Task Preview_DeniesUnauthorizedReadsBeforeConsultingTheRegistry()
    {
        using var fixture = new AutomationWizardFixture { Authorized = false };
        fixture.Registry.Throw = true;
        var result = await fixture.Service.PreviewWindowAsync(AutomationWizardFixture.Request(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
    }

    [TestMethod]
    public async Task Preview_RejectsUnavailableTasksAndMismatchedTargetSelection()
    {
        using var fixture = new AutomationWizardFixture();
        var request = AutomationWizardFixture.Request() with { TaskTarget = "star-trail" };
        var invalid = await fixture.Service.PreviewWindowAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Invalid, invalid.Kind);
        Assert.Contains("source selection", invalid.Message!, StringComparison.Ordinal);
        fixture.Registry.Available = false;
        var unavailable = await fixture.Service.PreviewWindowAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Invalid, unavailable.Kind);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(86401)]
    public async Task Preview_RejectsInvalidSettleAllowance(int seconds)
    {
        using var fixture = new AutomationWizardFixture();
        var request = AutomationWizardFixture.Request();
        var result = await fixture.Service.PreviewWindowAsync(request with
        { SourceWindow = request.SourceWindow! with { ProcessingSettleAllowance = TimeSpan.FromSeconds(seconds) } },
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
    }

    [TestMethod]
    [DataRow("2026-11-01T06:30:00Z", "2026-11-01T07:00:00Z", -6)]
    [DataRow("2026-11-01T07:30:00Z", "2026-11-01T08:00:00Z", -6)]
    [DataRow("2026-11-01T08:30:00Z", "2026-11-01T09:00:00Z", -7)]
    [DataRow("2026-03-08T08:30:00Z", "2026-03-08T09:00:00Z", -7)]
    public async Task Preview_CivilHoursUseActualDstBoundaries(string now, string end, int startOffset)
    {
        using var fixture = new AutomationWizardFixture("America/Denver");
        fixture.Clock.Advance(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture) - fixture.Clock.GetUtcNow());
        var request = AutomationWizardFixture.Request(kind: LocalAutomationSourceWindowKind.CompletedCivilHour);
        request = request with { SourceWindow = request.SourceWindow! with { ProcessingSettleAllowance = TimeSpan.Zero } };
        var result = await fixture.Service.PreviewWindowAsync(request, CancellationToken.None).ConfigureAwait(false);
        var window = result.Value!.NextWindow!;
        Assert.AreEqual(DateTimeOffset.Parse(end, System.Globalization.CultureInfo.InvariantCulture), window.EndUtc);
        Assert.AreEqual(TimeSpan.FromHours(startOffset), TimeZoneInfo.ConvertTime(window.StartUtc,
            TimeZoneInfo.FindSystemTimeZoneById("America/Denver")).Offset);
        Assert.AreEqual(window.EndUtc, window.EarliestFinalUtc);
        Assert.IsTrue(window.IsValid());
    }

    [TestMethod]
    public async Task Preview_NoSunriseHasNoInventedNextRun()
    {
        using var fixture = new AutomationWizardFixture("Arctic/Longyearbyen", 78.2232, 15.6469);
        fixture.Clock.Advance(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero) - fixture.Clock.GetUtcNow());
        var result = await fixture.Service.PreviewWindowAsync(AutomationWizardFixture.Request(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Value!.NextWindow);
        Assert.IsNotNull(result.Value.UnavailableReasonCode);
    }

    [TestMethod]
    public async Task Preview_SanitizesFailuresAndPropagatesCancellation()
    {
        using var fixture = new AutomationWizardFixture();
        fixture.Registry.Throw = true;
        var result = await fixture.Service.PreviewWindowAsync(AutomationWizardFixture.Request(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.DoesNotContain("private path", result.Message!);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await fixture.Service.PreviewWindowAsync(AutomationWizardFixture.Request(), cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }
}
