using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class ProjectedSceneStagingStoreTests
{
    [TestMethod]
    [DataRow("complete")]
    [DataRow("delete")]
    [DataRow("delete-completed")]
    public async Task StageNamespaceRetryMustRepeatFailedDirectoryBarrier(string operation)
        => await VerifyStagingRetryAsync(operation, kernelFault: false).ConfigureAwait(false);

    internal static async Task VerifyStagingRetryAsync(string operation, bool kernelFault)
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Requires the Linux descriptor-relative staging path.");
        var control = kernelFault ? DurableCaptureProcessingTests.RequireKernelRetryControl() : null;
        var root = CreateRoot();
        try
        {
            using var store = CreateStore(root);
            var key = new string('C', 64);
            var otherKey = new string('D', 64);
            var scene = await CreateSceneAsync(FixtureUtc, 10).ConfigureAwait(false);
            await store.StageAsync(key, new string('A', 64), scene, CancellationToken.None).ConfigureAwait(false);
            await store.StageAsync(otherKey, new string('B', 64), scene, CancellationToken.None).ConfigureAwait(false);
            if (operation == "delete-completed") await store.MarkCompletedAsync(key, CancellationToken.None).ConfigureAwait(false);
            var attempts = 0;
            var fail = true;
            store.BeforeDirectorySyncForTest = directory =>
            {
                attempts++;
                if (!fail) return;
                if (control is null) throw new IOException("Injected staging directory fsync EIO.", 5);
                if (attempts == 1) DurableCaptureProcessingTests.ArmKernelRetryFault(control, directory);
            };
            async Task ApplyAsync()
            {
                if (operation == "complete") await store.MarkCompletedAsync(key, CancellationToken.None).ConfigureAwait(false);
                else if (operation == "delete") await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
                else await store.DeleteCompletedAsync(key, CancellationToken.None).ConfigureAwait(false);
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var failure = await Assert.ThrowsExactlyAsync<IOException>(ApplyAsync).ConfigureAwait(false);
                Assert.AreEqual(5, failure.HResult & 0xffff);
            }
            Assert.AreEqual(2, attempts);
            if (control is not null) DurableCaptureProcessingTests.WaitForKernelController(control + ".released");
            fail = false;
            await ApplyAsync().ConfigureAwait(false);
            Assert.AreEqual(3, attempts);
            Assert.IsNotNull(await store.ReadAsync(otherKey, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(await store.ReadAsync(key, CancellationToken.None).ConfigureAwait(false));
            var completed = Path.Combine(root, "staging", "projected-scenes", key + ".completed.json");
            Assert.AreEqual(operation == "complete", File.Exists(completed));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

[TestClass]
[TestCategory("Manual")]
public sealed class ProjectedSceneStagingKernelFaultTests
{
    [TestMethod]
    public async Task StageCompletionRetryWithRealFsyncFailure()
        => await ProjectedSceneStagingStoreTests.VerifyStagingRetryAsync("complete", kernelFault: true).ConfigureAwait(false);
    [TestMethod]
    public async Task StageDeletionRetryWithRealFsyncFailure()
        => await ProjectedSceneStagingStoreTests.VerifyStagingRetryAsync("delete", kernelFault: true).ConfigureAwait(false);
    [TestMethod]
    public async Task CompletedStageDeletionRetryWithRealFsyncFailure()
        => await ProjectedSceneStagingStoreTests.VerifyStagingRetryAsync("delete-completed", kernelFault: true).ConfigureAwait(false);
}
