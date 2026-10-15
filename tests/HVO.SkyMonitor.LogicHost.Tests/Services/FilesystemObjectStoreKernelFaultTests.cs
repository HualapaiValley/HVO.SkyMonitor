namespace HVO.SkyMonitor.LogicHost.Tests.Services;

[TestClass]
[TestCategory("Manual")]
public sealed class FilesystemObjectStoreKernelFaultTests
{
    [TestMethod]
    public async Task DeleteRetryWithRealDirectoryFsyncFailure()
    {
        var fixture = new FilesystemObjectStoreFaultTests();
        fixture.Initialize();
        try { await fixture.VerifyDeleteKernelRetryAsync(); }
        finally { fixture.Cleanup(); }
    }
}
