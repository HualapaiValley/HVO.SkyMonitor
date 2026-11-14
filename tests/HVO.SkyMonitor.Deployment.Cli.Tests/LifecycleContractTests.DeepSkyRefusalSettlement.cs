using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    /// <summary>
    /// A deep-sky selection refused before any mutation is settled as a terminal refusal, the way the star catalog path
    /// settles one, so its journal no longer reads as running and no longer fences runtime control (#1824 L4, R9-N1).
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task DeepSkySelectionRefusedBeforeMutationIsSettledAndReadmitsRuntimeControl()
    {
        using var fixture = await DeepSkyFixtureAsync();
        var before = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var lifecycle = new FakeLifecycleClient { RejectRecoveryRead = true };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => DeepSkySettlingTransitionAsync(
            fixture, DeepSkyIdentity(fixture), lifecycle, new FakeOwnerClient(fixture.ApplicationIdentity)));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsNotNull(retained);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.IsFalse(retained.MutationStarted);
        Assert.IsTrue(CameraAgentLifecycleManager.IsRefused(retained));
        Assert.AreEqual(0, lifecycle.PauseCount, "A refusal before mutation never pauses capture.");
        var after = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(before.DeepSkyCatalog, after.DeepSkyCatalog, "Nothing was mutated, so the selection is unchanged.");
        using var lease = await CameraAgentLifecycleManager.AcquireControlDeploymentAsync(fixture.Paths, CancellationToken.None);
    }

    /// <summary>A failure after mutation keeps its restored lifecycle failure; settlement never relabels it a refusal.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task DeepSkySelectionFailedAfterMutationKeepsItsRestoredFailure()
    {
        using var fixture = await DeepSkyFixtureAsync();
        var lifecycle = new FakeLifecycleClient { DrainedProcessingPending = 3 };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => DeepSkySettlingTransitionAsync(
            fixture, DeepSkyIdentity(fixture), lifecycle, new FakeOwnerClient(fixture.ApplicationIdentity)));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsNotNull(retained);
        Assert.AreEqual(InstallationStatus.Failed, retained.Status);
        Assert.AreEqual("lifecycle-failed", retained.FailureCode);
        Assert.IsFalse(CameraAgentLifecycleManager.IsRefused(retained));
        Assert.AreEqual(1, lifecycle.PauseCount, "The failure happened after capture was paused for the mutation.");
    }

    /// <summary>Cancellation is not a refusal: the journal stays running for the ordinary resume path.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task DeepSkySelectionCancelledBeforeMutationIsNotSettledAsRefused()
    {
        using var fixture = await DeepSkyFixtureAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => DeepSkySettlingTransitionAsync(
            fixture, DeepSkyIdentity(fixture), new FakeLifecycleClient(), new FakeOwnerClient(fixture.ApplicationIdentity),
            transitionCancellation: cancelled.Token));

        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsNotNull(retained);
        Assert.AreEqual(InstallationStatus.Running, retained.Status);
        Assert.IsFalse(CameraAgentLifecycleManager.IsRefused(retained));
    }

    private static async Task<LifecycleResult> DeepSkySettlingTransitionAsync(LifecycleFixture fixture,
        CatalogInstallationIdentity? candidate, FakeLifecycleClient lifecycle, FakeOwnerClient owner,
        CancellationToken transitionCancellation = default)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var request = fixture.Request(LifecycleOperationKind.CatalogSelect)
            with
        { CatalogFamily = "deep-sky", CatalogVersion = candidate!.PackageVersion };
        var operation = await CameraAgentLifecycleManager.BeginAsync(request, fixture.Paths, request.Operation!.Value, manifest,
            CancellationToken.None);
        return await DeepSkyCatalogSelection.TransitionSettlingRefusalAsync(request, fixture.Paths, manifest, result,
            CameraAgentLifecycleManager.ComposeFrom(fixture.Paths, manifest, result), new DockerClient(fixture.Runner),
            _ => lifecycle, _ => owner, "lifecycle-token", "verification-token", candidate, operation, transitionCancellation);
    }
}
