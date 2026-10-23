using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured disposal would hide the strongly typed fixture.")]
public sealed class InstallationReadEvidenceRootTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task OverlongAbsoluteRootFailsBeforeHostCreationAndPreservesEvidence()
    {
        var callerRoot = Directory.CreateTempSubdirectory("hvo-iv-");
        var output = Path.Combine(callerRoot.FullName, new string('x', 64));
        Directory.CreateDirectory(output);
        var report = Path.Combine(output, "installation-read-attribution.json");
        var trx = Path.Combine(output, "regression.trx");
        await File.WriteAllTextAsync(report, "retained report").ConfigureAwait(false);
        await File.WriteAllTextAsync(trx, "retained trx").ConfigureAwait(false);
        try
        {
            Assert.IsTrue(Path.IsPathFullyQualified(output));
            Assert.IsGreaterThan(52, output.Length);
            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            {
                await using var fixture = await InstallationReadAttributionFixture.CreateAsync(output, solar: false).ConfigureAwait(false);
            }).ConfigureAwait(false);
            StringAssert.Contains(exception.Message, "short absolute directory", StringComparison.Ordinal);
            StringAssert.Contains(exception.Message, "hvo scratch", StringComparison.Ordinal);
            Assert.IsFalse(exception.ToString().Contains(output, StringComparison.Ordinal), "The diagnostic must not disclose the caller's path.");
            Assert.AreEqual(0, Directory.GetDirectories(output).Length, "Preflight must reject the root before creating a disposable host.");
            Assert.AreEqual("retained report", await File.ReadAllTextAsync(report).ConfigureAwait(false));
            Assert.AreEqual("retained trx", await File.ReadAllTextAsync(trx).ConfigureAwait(false));
        }
        finally
        {
            callerRoot.Delete(recursive: true);
        }
    }
}
