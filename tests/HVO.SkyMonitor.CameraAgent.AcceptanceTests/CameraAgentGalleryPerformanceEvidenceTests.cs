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
        var directory = Directory.CreateTempSubdirectory("hvo-gallery-evidence-");
        try
        {
            var selected = Path.Combine(directory.FullName, "selected.json");
            var opposite = Path.Combine(directory.FullName, "opposite.json");
            await File.WriteAllTextAsync(selected, "selected").ConfigureAwait(false);
            await File.WriteAllTextAsync(opposite, "opposite").ConfigureAwait(false);

            var failure = await Assert.ThrowsExactlyAsync<AssertInconclusiveException>(() =>
                CameraAgentGalleryPerformanceTests.RunOrWithdrawStaleEvidenceAsync<object>(
                    static () => throw new AssertInconclusiveException("browser unavailable"),
                    () => File.Delete(selected))).ConfigureAwait(false);

            StringAssert.Contains(failure.Message, "browser unavailable", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(selected));
            Assert.IsTrue(File.Exists(opposite));
        }
        finally
        {
            directory.Delete(recursive: true);
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
