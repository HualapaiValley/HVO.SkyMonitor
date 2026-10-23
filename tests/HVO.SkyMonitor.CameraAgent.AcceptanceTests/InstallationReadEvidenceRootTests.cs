using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured disposal would hide the strongly typed fixture.")]
public sealed class InstallationReadEvidenceRootTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void RelativeEvidenceRootRequiresShortAbsoluteDirectory()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            InstallationReadAttributionFixture.ValidateEvidenceRoot("relative-output"));
        StringAssert.Contains(exception.Message, "short absolute directory", StringComparison.Ordinal);
        Assert.IsFalse(exception.ToString().Contains("relative-output", StringComparison.Ordinal));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void MultibyteEvidenceRootUsesEncodedSocketPathLimit()
    {
        var output = Path.GetPathRoot(Path.GetTempPath()) + new string('\u00e9', 40);
        Assert.IsTrue(Path.IsPathFullyQualified(output));
        Assert.IsTrue(output.Length <= 52, "Exercise a short character count that still exceeds the socket byte limit.");
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            InstallationReadAttributionFixture.ValidateEvidenceRoot(output));
        StringAssert.Contains(exception.Message, "platform limit", StringComparison.Ordinal);
        Assert.IsFalse(exception.ToString().Contains(output, StringComparison.Ordinal));
    }

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

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ShortAbsoluteRootDisposesHostAndPreservesEvidence()
    {
        var temporaryRoot = Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath();
        var output = Path.Combine(temporaryRoot, $"iv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var report = Path.Combine(output, "installation-read-attribution.json");
        var trx = Path.Combine(output, "regression.trx");
        try
        {
            await File.WriteAllTextAsync(report, "retained report").ConfigureAwait(false);
            await File.WriteAllTextAsync(trx, "retained trx").ConfigureAwait(false);
            string hostRoot;
            string socketPath;
            await using (var fixture = await InstallationReadAttributionFixture.CreateAsync(output, solar: false).ConfigureAwait(false))
            {
                hostRoot = fixture.Host.Root;
                socketPath = fixture.Host.RecoverySocketPath;
                Assert.IsTrue(Directory.Exists(hostRoot));
                using var client = fixture.CreateClient();
                var identity = await fixture.ReadAsync(client).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentKestrelFixture.AgentId, identity.GetProperty("agentId").GetString());
            }
            Assert.IsFalse(Directory.Exists(hostRoot), "Disposal must remove only the generated host root.");
            Assert.IsFalse(File.Exists(socketPath));
            Assert.AreEqual(0, Directory.GetDirectories(output).Length);
            Assert.AreEqual("retained report", await File.ReadAllTextAsync(report).ConfigureAwait(false));
            Assert.AreEqual("retained trx", await File.ReadAllTextAsync(trx).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}
