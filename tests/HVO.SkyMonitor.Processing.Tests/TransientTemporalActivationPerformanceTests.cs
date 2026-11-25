using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientTemporalActivationPerformanceTests
{
    [TestMethod]
    public void DisabledActivationDoesNotAllocatePerCall() =>
        Assert.AreEqual(0, TransientTemporalBackgroundTests.MeasureDisabledActivationAllocations());
}
