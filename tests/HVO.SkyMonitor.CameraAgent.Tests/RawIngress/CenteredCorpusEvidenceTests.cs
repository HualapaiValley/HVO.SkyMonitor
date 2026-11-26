using HVO.SkyMonitor.CameraAgent.Tests.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Manual")]
[TestProperty("Area", "RawIngress")]
public sealed class CenteredCorpusEvidenceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CenteredRetainedNovemberMonoAndOddOriginBayerPassExactRawAdmission()
    {
        var corpus = Environment.GetEnvironmentVariable("HVO_CAPTURE_1919_CORPUS");
        if (string.IsNullOrEmpty(corpus))
            Assert.Inconclusive("Set HVO_CAPTURE_1919_CORPUS to the directory containing the documented #1919 fixtures.");
        await RawCaptureIngressTests.RunCenteredRetainedCorpusAsync(corpus!).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CenteredRetainedNovemberMonoPassEndToEndCenteredQualificationAsync()
    {
        var corpus = Environment.GetEnvironmentVariable("HVO_CAPTURE_1919_CORPUS");
        if (string.IsNullOrEmpty(corpus))
            Assert.Inconclusive("Set HVO_CAPTURE_1919_CORPUS to the directory containing the documented #1919 fixtures.");
        await CenteredPipelineQualificationTests.RunRetainedNovemberMonoEndToEndAsync(
            corpus!, TestContext.CancellationToken).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CenteredRetainedOddOriginBayerGeometryPassEndToEndCenteredQualificationAsync()
    {
        var corpus = Environment.GetEnvironmentVariable("HVO_CAPTURE_1919_CORPUS")
            ?? Environment.GetEnvironmentVariable("HVO_REGISTRATION_GEOMETRY");
        if (string.IsNullOrEmpty(corpus))
            Assert.Inconclusive("Set HVO_CAPTURE_1919_CORPUS (or HVO_REGISTRATION_GEOMETRY) to the directory containing the geometry oracle fixtures.");
        await CenteredPipelineQualificationTests.RunRetainedBayerGeometryEndToEndAsync(
            corpus!, TestContext.CancellationToken).ConfigureAwait(false);
    }
}
