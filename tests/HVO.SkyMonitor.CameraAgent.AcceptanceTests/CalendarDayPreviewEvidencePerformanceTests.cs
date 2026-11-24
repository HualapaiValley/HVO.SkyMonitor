using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CalendarDayPreviewEvidencePerformanceTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task BoundedAwaitGivesUpWithinFiveSeconds() =>
        Assert.IsLessThan(TimeSpan.FromSeconds(5), await CalendarDayPreviewEvidenceTests.VerifyBoundedAwaitAsync().ConfigureAwait(false));

    [TestMethod]
    [DataRow("capture")]
    [DataRow("writer")]
    [DataRow("async-writer")]
    public async Task CallerReturnsBeforeStalledAttemptSettles(string stall) =>
        Assert.IsLessThan(TimeSpan.FromSeconds(3), await CalendarDayPreviewEvidenceTests.VerifyStalledAttemptsAsync(stall).ConfigureAwait(false));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerReturnsPromptlyWithThrowingCancellationCallbacks(bool stalls) =>
        Assert.IsLessThan(TimeSpan.FromSeconds(3), await CalendarDayPreviewEvidenceTests.VerifyThrowingCancellationCallbacksAsync(stalls).ConfigureAwait(false));

    [TestMethod]
    public async Task CallerReturnsBeforeBlockedAttachmentSettles() =>
        Assert.IsLessThan(TimeSpan.FromSeconds(3), await CalendarDayPreviewEvidenceTests.VerifyBlockedAttachmentAsync().ConfigureAwait(false));
}
