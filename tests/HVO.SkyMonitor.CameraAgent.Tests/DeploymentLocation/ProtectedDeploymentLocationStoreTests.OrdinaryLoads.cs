using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The scrub is released and drained before its synchronization event is disposed.")]
    public async Task OrdinaryLoadDuringRunningScrubWaitsInsteadOfFailingAfterOneSecond()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRetainedManifestValidationForTest = () =>
        {
            entered.TrySetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var scrub = Task.Run(async () => await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Task<DeploymentLocationSnapshot>? ordinary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            ordinary = store.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(1250)).ConfigureAwait(false);
            Assert.IsFalse(ordinary.IsCompleted, "Ordinary loading must wait for the scrub, rather than escape as a pending-verification error.");
        }
        finally
        {
            release.Set();
            await scrub.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (ordinary is not null) _ = await ordinary.ConfigureAwait(false);
        }
        Assert.AreEqual(active, await ordinary!.ConfigureAwait(false));
    }

    [TestMethod]
    public async Task OrdinaryLoadDuringManagedWriteWaitsInsteadOfFailingAfterOneSecond()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            var path = Path.Combine(_options.Value.RawIngressRoot, "blocked-writer.json");
            using var mutation = RetainedIntegrityLedger.BeginWrite(path, "{}"u8.ToArray());
            await File.WriteAllTextAsync(path, "{}").ConfigureAwait(false);
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });
        Task<DeploymentLocationSnapshot>? ordinary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            ordinary = store.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(1250)).ConfigureAwait(false);
            Assert.IsFalse(ordinary.IsCompleted, "An ordinary load must wait for managed publication to finish.");
        }
        finally
        {
            release.TrySetResult();
            await writer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (ordinary is not null) _ = await ordinary.ConfigureAwait(false);
        }
        Assert.AreEqual(active, await ordinary!.ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryGateAndManagedWriteWaitsHonorCallerCancellation(bool managedWriteBarrier)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var owner = managedWriteBarrier
            ? typeof(ProtectedDeploymentLocationStore).GetField("_integrity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!
            : store;
        var gate = (SemaphoreSlim)owner.GetType().GetField(managedWriteBarrier ? "_barrier" : "_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                _ = await store.LoadCurrentAsync(seed, verification: false, () => true, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    [TestMethod]
    [DataRow("invalid-json")]
    [DataRow("drift")]
    [DataRow("missing-ledger")]
    [DataRow("damaged-ledger")]
    public async Task OrdinaryLoadsOfUnqualifiedStateDoNotRepeatFullRetainedScans(string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var initial = CreateStore();
        var active = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (retained, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        initial.Dispose();
        if (fault == "invalid-json")
        {
            var path = Path.Combine(_options.Value.RawIngressRoot, "quarantine", "torn.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{").ConfigureAwait(false);
        }
        else if (fault == "drift") await File.AppendAllTextAsync(retained, " ").ConfigureAwait(false);
        else
        {
            var ledger = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
            if (fault == "missing-ledger") File.Delete(ledger);
            else await File.WriteAllTextAsync(ledger, "not a SQLite database").ConfigureAwait(false);
        }
        using var store = CreateStore();
        Assert.AreEqual(active, await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        var validations = 0;
        store.BeforeRetainedManifestValidationForTest = () => validations++;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.AreEqual(active, await store.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        Assert.AreEqual(0, validations, "Unqualified ordinary reads must not repeat the linear scrub inline.");
    }
}
