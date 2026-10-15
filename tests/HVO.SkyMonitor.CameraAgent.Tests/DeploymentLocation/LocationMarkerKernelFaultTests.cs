namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

[TestClass]
[TestCategory("Manual")]
public sealed class LocationMarkerKernelFaultTests
{
    [TestMethod]
    public async Task LocationMarkerRetryWithRealFsyncFailure()
    {
        var fixture = new ProtectedDeploymentLocationStoreTests();
        fixture.Initialize();
        try { await fixture.VerifyLocationMarkerRetryAsync(kernelFault: true).ConfigureAwait(false); }
        finally { fixture.Cleanup(); }
    }
}
