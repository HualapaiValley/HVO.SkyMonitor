using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(null)]
    [DataRow("LegacyScalarSolar")]
    [DataRow("SolarDriven")]
    [DataRow("ControlledNight")]
    public async Task InstallAsync_LegacyResume_RejectsChangedLightingAndPreservesOmittedCompatibility(string? mode)
    {
        var (request, root) = ResourceRequest(RequireProductionCatalogBundle(), Guid.NewGuid());
        var instanceId = request.InstanceId!.Value;
        var paths = InstallationPaths.Create(root, instanceId, ProductionCatalog.DefaultCatalogId);
        var runner = new InstallerProcessRunner(request.ImageReference, root, instanceId);
        var owner = new FailingOnceOwnerClient();
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallResourceRequestAsync(request, runner, owner));
            Assert.AreEqual("injected owner authentication failure", failure.Message);
            var failed = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(paths.StatePath),
                DeploymentJsonContext.Default.InstallationState)!;
            Assert.AreEqual(InstallationStatus.Failed, failed.Status);
            Assert.AreEqual(InstallationPhase.OwnerBootstrap, failed.Phase);

            // Model an interrupted installation written before replay profiles and explicit illumination existed.
            var legacy = CameraAgentInstaller.ComputeLegacyRequestSha256(request, instanceId);
            await SafeFileSystem.WriteJsonAtomicAsync(paths.StatePath, failed with { RequestSha256 = legacy },
                DeploymentJsonContext.Default.InstallationState, CancellationToken.None);
            var configurationPath = Path.Combine(paths.ConfigRoot, "camera-module.json");
            var configurationBefore = await File.ReadAllTextAsync(configurationPath);
            using (var configuration = JsonDocument.Parse(configurationBefore))
                Assert.IsFalse(configuration.RootElement.GetProperty("module").GetProperty("options").TryGetProperty("illuminationMode", out _));
            var composeUps = runner.ComposeUpCount;

            if (mode is not null)
            {
                var before = FileSystemActivity.Snapshot(root);
                var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallResourceRequestAsync(
                    request with { Resume = true, IlluminationMode = mode }, runner, owner));

                Assert.IsTrue(refusal.IsRefusal, refusal.Message);
                StringAssert.Contains(refusal.Message, "different immutable inputs", StringComparison.Ordinal);
                FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root),
                    "a rejected legacy resume changed files or metadata");
                Assert.AreEqual(composeUps, runner.ComposeUpCount);
                Assert.AreEqual(configurationBefore, await File.ReadAllTextAsync(configurationPath));
            }

            // Refusal must not poison the still-supported legacy resume with its original omitted lighting.
            var resumed = await InstallResourceRequestAsync(request with { Resume = true }, runner, owner);
            Assert.AreEqual(InstallationOutcome.Installed, resumed.Outcome);
            Assert.AreEqual(composeUps + 2, runner.ComposeUpCount);
            Assert.AreEqual(configurationBefore, await File.ReadAllTextAsync(configurationPath));
            var completed = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(paths.StatePath),
                DeploymentJsonContext.Default.InstallationState)!;
            Assert.AreEqual(InstallationStatus.Completed, completed.Status);
            Assert.AreEqual(legacy, completed.RequestSha256);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }
}
