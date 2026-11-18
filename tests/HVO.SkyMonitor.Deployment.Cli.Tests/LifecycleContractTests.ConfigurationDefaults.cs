using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The upgrade leg of Refs #1779, ported from the 2026-11-10 acceptance audit's reproducer (F3, finding B,
/// <c>LifecycleContractTests.F3Upgrade.cs</c>). <c>cameraagent upgrade</c> keeps an instance's configuration on disk and
/// its configuration identity in the records; adoption of newer defaults happens on the next re-run
/// (<see cref="InstallerFlowTests"/>). Every <c>docker</c> call is answered by <see cref="FakeRunner"/>.
/// </summary>
public sealed partial class LifecycleContractTests
{
    /// <summary>The image an upgrade moved an installed root to, as the fake daemon reports it.</summary>
    internal sealed record UpgradedImage(string Reference, string ImageId, Dictionary<string, string> Labels);

    /// <summary>Ported unchanged: the audit's control, which this issue keeps (out of scope: upgrade adopting defaults).</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task F3_B_Upgrade_KeepsTheRetainedConfigurationIdentity()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var before = fixture.Manifest;
        var resultBefore = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);

        var upgrade = await UpgradeForLimitsAsync(fixture);

        var after = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var resultAfter = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        Assert.AreEqual("completed", upgrade.Outcome);
        Assert.AreNotEqual(before.Image.ImageId, after.Image.ImageId, "the upgrade changed the image");
        Assert.AreEqual(before.Image, after.PreviousImage);
        // The upgrade carries the configuration identity over unchanged: it never regenerates the configuration.
        Assert.AreEqual(before.ConfigurationSha256, after.ConfigurationSha256);
        Assert.AreEqual(resultBefore.ConfigurationSha256, resultAfter.ConfigurationSha256);
        Assert.AreEqual(before.RigProfileSha256, after.RigProfileSha256);
        Assert.AreEqual(before.RigProfileVersion, after.RigProfileVersion);
        Assert.AreEqual(before.ScheduleSha256, after.ScheduleSha256);
    }

    /// <summary>
    /// Ported from <c>F3_B_Upgrade_SeededRootWithTheBaselineInstaller</c>: upgrades the completed installation in
    /// <paramref name="root"/>, which the real installer flow made, to a candidate image with this installer's lifecycle
    /// manager, and returns the candidate so the re-run can name it. The candidate declares configuration defaults
    /// <paramref name="configurationDefaults"/>, or none. The upgrade keeps the configuration identity and the file.
    /// </summary>
    internal static async Task<UpgradedImage> UpgradeInstalledRootAsync(string root, Guid instanceId, string? configurationDefaults)
    {
        var paths = InstallationPaths.Create(root, instanceId, ProductionCatalog.DefaultCatalogId);
        var before = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
        var configurationBefore = await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "camera-module.json"));
        var uid = NativeLinux.getuid();
        var gid = NativeLinux.getgid();
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        var candidateImageId = $"sha256:{new string('d', 64)}";
        var runner = new FakeRunner(before.DockerDaemon) { CatalogContract = before.Image.CatalogContract!, Cpus = 4 };
        if (configurationDefaults is not null)
        {
            runner.CandidateLabels[CameraConfigurationDefaults.ImageLabel] = configurationDefaults;
        }
        runner.ConfigureRuntime(paths, before.Image.ImmutableReference, before.Image.ImageId, candidateReference, candidateImageId, uid, gid);
        var request = new LifecycleRequest(LifecycleOperationKind.Upgrade, instanceId, root, dryRun: false, resume: false, json: false)
        {
            AllowTestProductRoot = true,
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

        var upgrade = await CameraAgentLifecycleManager.ExecuteAsync(
            request, runner, _ => new InstalledRootLifecycleClient(), _ => new FakeOwnerClient(before.ApplicationIdentity), uid, gid,
            CancellationToken.None);

        var after = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual("completed", upgrade.Outcome);
        Assert.AreEqual(candidateImageId, after.Image.ImageId);
        Assert.AreEqual(before.ConfigurationSha256, after.ConfigurationSha256, "the upgrade keeps the configuration identity");
        Assert.AreEqual(configurationBefore, await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "camera-module.json")),
            "the upgrade does not rewrite camera-module.json");
        return new UpgradedImage(candidateReference, candidateImageId, runner.CandidateImageLabels());
    }

    /// <summary>The CameraAgent lifecycle API as <see cref="FakeLifecycleClient"/> answers it, without its fixed token.</summary>
    private sealed class InstalledRootLifecycleClient : ICameraAgentLifecycleClient
    {
        public Task<LifecycleContinuity> PauseAndDrainAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(new LifecycleContinuity("Paused", 42, 100, 0, 0, 0, 0, 0, 0, 0, 0));

        public Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(new LifecycleContinuity("Paused", 42, 100, 0, 0, 0, 0, 0, 0, 0, 0));

        public Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(new LifecycleContinuity("Paused", 2, 1812, 0, 0, 0, 0, 0, 0, 0, 0, ReplayCatalogIndependent: true));

        public Task<LifecycleResumeReceipt> ResumeRecoveryAsync(
            Guid operationId, Guid commandId, long expectedVersion, string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(new LifecycleResumeReceipt("Running", expectedVersion + 1, true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
}
