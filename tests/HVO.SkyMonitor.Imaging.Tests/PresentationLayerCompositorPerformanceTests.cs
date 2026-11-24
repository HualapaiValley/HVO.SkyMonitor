namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class PresentationLayerCompositorPerformanceTests
{
    [TestMethod]
    public void CompositionBudgetsRejectBeforeOutputAllocation()
    {
        var (oversized, tooManyLayers) = PresentationLayerCompositorTests.VerifyCompositionBudgets();
        Assert.IsLessThan(1_000_000L, oversized);
        Assert.IsLessThan(1_000_000L, tooManyLayers, "Rejected stack must not allocate its 38MB output.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationAtEndOfAdmissionPrecedesOutputAllocation(bool promoteToRgb)
    {
        var allocated = PresentationLayerCompositorTests.VerifyCancellationAtEndOfAdmission(promoteToRgb);
        Assert.IsLessThan(1_000_000L, allocated, "Admission cancellation must precede the 4/12 MiB output allocation and any base copy.");
    }
}
