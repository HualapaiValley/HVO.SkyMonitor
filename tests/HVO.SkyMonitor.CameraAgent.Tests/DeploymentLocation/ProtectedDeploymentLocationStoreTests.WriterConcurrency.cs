using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    public async Task NestedManagedDeleteCompletesWhileAnotherFlowStartsAWrite()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (retained, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var outer = Path.Combine(_options.Value.RawIngressRoot, "outer.json");
        await WithConcurrentManagedWriterAsync(async () =>
        {
            using var mutation = RetainedIntegrityLedger.BeginWrite(outer, "{}"u8.ToArray());
            await File.WriteAllTextAsync(outer, "{}").ConfigureAwait(false);
            RetainedFileMutations.Delete(retained);
        }).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(retained));
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ManualFocusPruningWithOneRecordCompletesAlongsideATrackedWriter()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        using var focus = new ManualFocusSessionStore(_options.Value.RawIngressRoot, maximumRecords: 1);
        static ManualFocusSessionRecord Record(string id, DateTimeOffset now)
            => ManualFocusSessionRecord.Create(ManualFocusSessionSnapshot.Idle with
            {
                SessionId = id,
                State = ManualFocusSessionState.Stopped,
                OwnerId = "synthetic",
                StartedUtc = now.AddMinutes(-5),
                EndedUtc = now.AddMinutes(-1),
                EndReason = ManualFocusReasonCodes.StoppedByOperator,
                TotalSamples = 4,
                HistoryCapacity = 100,
                ModuleType = "VirtualSky"
            }, "synthetic", now);
        var first = await focus.SaveAsync(Record("first", Now), CancellationToken.None).ConfigureAwait(false);
        await WithConcurrentManagedWriterAsync(async () =>
            _ = await focus.SaveAsync(Record("second", Now.AddMinutes(1)), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.Combine(_options.Value.RawIngressRoot, ManualFocusSessionStore.DirectoryName, first.RecordId + ".json")));
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(_options.Value.RawIngressRoot, ManualFocusSessionStore.DirectoryName), "*.json").Count());
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The independent writer is drained in finally before the coordination event is disposed.")]
    private async Task WithConcurrentManagedWriterAsync(Func<Task> nestedWrite)
    {
        using var otherBlocked = new ManualResetEventSlim();
        var startOther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Created before flow A opens a scope, so flow B inherits neither ambient lease.
        var writer = Task.Run(async () =>
        {
            await startOther.Task.ConfigureAwait(false);
            await RetainedFileMutations.WriteAllBytesAsync(Path.Combine(_options.Value.RawIngressRoot, "concurrent.json"), "{}"u8.ToArray()).ConfigureAwait(false);
        });
        RetainedIntegrityLedger.RegistrationBlockedForTest = otherBlocked.Set;
        RetainedIntegrityLedger.BeforeNestedMutationForTest = () =>
        {
            startOther.TrySetResult();
            Assert.IsTrue(otherBlocked.Wait(TimeSpan.FromSeconds(5)),
                "The competing writer must wait before owning the global gate, while the active flow can re-enter it.");
        };
        try { await nestedWrite().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        finally
        {
            RetainedIntegrityLedger.BeforeNestedMutationForTest = null;
            RetainedIntegrityLedger.RegistrationBlockedForTest = null;
            startOther.TrySetResult();
            await writer.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task OrdinaryLoadsStayQualifiedAndOnlyExplicitScrubsInspectRetainedHistory()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        store.BeforeRetainedManifestValidationForTest = () => Assert.Fail("An unchanged normal load must not scrub.");
        for (var i = 0; i < 3; i++)
            Assert.AreEqual(active, await store.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        var observed = 0;
        store.BeforeRetainedManifestValidationForTest = () => observed++;
        Assert.AreEqual(active, await store.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false));
        Assert.IsGreaterThan(0, observed);
        _timeProvider.UtcNow = Now.AddMinutes(1);
        var changed = seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } };
        Assert.AreEqual(2L, (await store.LoadCurrentAsync(changed, verification: false, () => true, CancellationToken.None).ConfigureAwait(false)).Version);
    }

    [TestMethod]
    public async Task CancellationAfterDurableLocationChangeDoesNotLeaveMemoryAtTheOldVersion()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddMinutes(1);
        using var cancellation = new CancellationTokenSource();
        var changed = seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } };
        store.BeforeStateDirectorySyncForTest = directory =>
        {
            var marker = Path.Combine(_options.Value.RawIngressRoot, ".deployment-location.v1.identity");
            if (directory != _options.Value.RawIngressRoot || !File.Exists(marker)) return;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(marker));
            if (document.RootElement.GetProperty("version").GetInt64() == 2) cancellation.Cancel();
        };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.InitializeAsync(changed, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(2L, store.Active!.Version);
        var committed = store.Active;
        store.Dispose();
        using var restarted = CreateStore();
        Assert.AreEqual(committed, await restarted.FullVerifyAsync(changed, CancellationToken.None).ConfigureAwait(false));
    }
}
