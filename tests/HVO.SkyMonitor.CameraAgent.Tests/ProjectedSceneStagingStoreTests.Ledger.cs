using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class ProjectedSceneStagingStoreTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task NativeStagePublicationCompletionDeletionAndReconciliationRemainQualifiedAcrossRestart()
    {
        var root = CreateRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
            });
            var protector = new DataProtectionDeploymentLocationProtector(
                DataProtectionProvider.Create(Directory.CreateDirectory(Path.Combine(root, "keys"))));
            var seed = new DeploymentLocationSeed("native-stage-ledger", "synthetic", null, null, null,
                new ObservatoryLocation(35, -115, 1000, "America/Phoenix"));
            using var locations = new ProtectedDeploymentLocationStore(options, protector, TimeProvider.System,
                NullLogger<ProtectedDeploymentLocationStore>.Instance);
            var identity = await locations.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            using var stages = CreateStore(root);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            var sceneId = new string('A', 64);
            var first = new string('1', 64);
            await stages.StageAsync(first, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(identity, await locations.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(identity, await locations.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
            await stages.MarkCompletedAsync(first, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(identity, await locations.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(identity, await locations.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
            await stages.DeleteCompletedAsync(first, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(identity, await locations.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));

            var second = new string('2', 64);
            await stages.StageAsync(second, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            await stages.DeleteAsync(second, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(identity, await locations.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
            var third = new string('3', 64);
            await stages.StageAsync(third, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            var reconciled = await stages.ReconcileAsync(new HashSet<string>(StringComparer.Ordinal), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, reconciled.Deleted);
            Assert.AreEqual(identity, await locations.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));

            var retained = new string('4', 64);
            await stages.StageAsync(retained, sceneId, scene, CancellationToken.None).ConfigureAwait(false);
            locations.Dispose();
            using var restarted = new ProtectedDeploymentLocationStore(options, protector, TimeProvider.System,
                NullLogger<ProtectedDeploymentLocationStore>.Instance);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(identity, await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(identity, await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNotNull(await stages.ReadAsync(retained, CancellationToken.None).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
