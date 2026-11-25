namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Manual")]
[TestProperty("Area", "RawIngress")]
public sealed class CenteredCorpusEvidenceTests
{
    [TestMethod]
    public async Task CenteredRetainedNovemberMonoAndOddOriginBayerPassExactRawAdmission()
    {
        var corpus = Environment.GetEnvironmentVariable("HVO_CAPTURE_1919_CORPUS");
        if (string.IsNullOrEmpty(corpus))
            Assert.Inconclusive("Set HVO_CAPTURE_1919_CORPUS to the directory containing the documented #1919 fixtures.");
        await RawCaptureIngressTests.RunCenteredRetainedCorpusAsync(corpus!).ConfigureAwait(false);
    }
}
