using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class MeasuredAstrometryPerformanceTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task CallerCancellationAndWorkBudgetRejectionArePrompt()
    {
        var (cancellation, rejection) = await MeasuredAstrometryRecipeTests
            .VerifyCallerCancellationAndWorkBudgetRejectionAsync().ConfigureAwait(false);
        Assert.IsLessThan(TimeSpan.FromSeconds(3), cancellation);
        Assert.IsLessThan(TimeSpan.FromSeconds(3), rejection);
    }
}
