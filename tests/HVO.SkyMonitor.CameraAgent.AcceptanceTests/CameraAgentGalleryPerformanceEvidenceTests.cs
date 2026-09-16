using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CameraAgentGalleryPerformanceEvidenceTests
{
    [TestMethod]
    public async Task InconclusiveLaunchWithdrawsOnlySelectedEvidenceAndRethrows()
    {
        var root = Directory.CreateTempSubdirectory("hvo-gallery-evidence-");
        var previousLabel = Environment.GetEnvironmentVariable("HVO_GALLERY_EVIDENCE_LABEL");
        try
        {
            Environment.SetEnvironmentVariable("HVO_GALLERY_EVIDENCE_LABEL", "baseline");
            var baseline = Directory.CreateDirectory(Path.Combine(root.FullName, "TestResults", "issue-441", "baseline"));
            var candidate = Directory.CreateDirectory(Path.Combine(root.FullName, "TestResults", "issue-441", "candidate"));
            var selected = Path.Combine(baseline.FullName, "cameraagent-gallery-performance.json");
            var opposite = Path.Combine(candidate.FullName, "cameraagent-gallery-performance.json");
            await File.WriteAllTextAsync(selected, "selected").ConfigureAwait(false);
            await File.WriteAllTextAsync(opposite, "opposite").ConfigureAwait(false);

            var failure = await Assert.ThrowsExactlyAsync<AssertInconclusiveException>(() =>
                CameraAgentGalleryPerformanceTests.RunOrWithdrawStaleEvidenceAsync<object>(
                    static () => throw new AssertInconclusiveException("browser unavailable"),
                    () => CameraAgentGalleryPerformanceTests.WithdrawStaleEvidence(root.FullName))).ConfigureAwait(false);

            StringAssert.Contains(failure.Message, "browser unavailable", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(selected));
            Assert.IsTrue(File.Exists(opposite));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HVO_GALLERY_EVIDENCE_LABEL", previousLabel);
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task SuccessfulLaunchDoesNotWithdrawEvidence()
    {
        var withdrawn = false;

        var result = await CameraAgentGalleryPerformanceTests.RunOrWithdrawStaleEvidenceAsync(
            static () => Task.FromResult("browser"),
            () => withdrawn = true).ConfigureAwait(false);

        Assert.AreEqual("browser", result);
        Assert.IsFalse(withdrawn);
    }
}
