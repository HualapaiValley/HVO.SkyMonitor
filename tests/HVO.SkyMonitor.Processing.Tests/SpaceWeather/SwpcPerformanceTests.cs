using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Processing.Tests.SpaceWeather;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class SwpcPerformanceTests
{
    [TestMethod]
    [Timeout(60_000)]
    public void AdversarialProbabilityRowRejectsPromptlyWithoutTimeout() =>
        Assert.IsLessThan(TimeSpan.FromSeconds(2), SwpcCorrectionTests.VerifyAdversarialProbabilityRow());

    [TestMethod]
    [Timeout(60_000)]
    public void MaximumWatchEvaluationIsBounded() =>
        Assert.IsLessThan(TimeSpan.FromSeconds(10), SwpcCorrectionTests.VerifyMaximumWatchEvaluation());

    [TestMethod]
    [Timeout(60_000)]
    public void UnterminatedEscapedStringIsBounded() =>
        Assert.IsLessThan(TimeSpan.FromSeconds(5), SwpcFactBoundaryTests.VerifyUnterminatedEscapedString());

    [TestMethod]
    public void OversizeMessageIsRejectedBeforeMaterialization() =>
        Assert.IsLessThan(131_072L, SwpcCorrectionTests.VerifyOversizeMessageRejection());

    [TestMethod]
    public void RecordCountIsRejectedBeforeDom() =>
        Assert.IsLessThan(4096L, SwpcCorrectionTests.VerifyRecordCountRejection());
}
