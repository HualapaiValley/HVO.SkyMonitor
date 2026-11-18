using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    public async Task MissingRequiredLedgerKeepsCaptureHeldAcrossOrdinaryAndBackgroundLoading()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using (var previous = CreateStore())
            _ = await previous.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        File.Delete(Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName));
        using var restarted = CreateStore();
        _ = await restarted.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(restarted.RecentRetainedEvidenceReady);
        _ = await restarted.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(restarted.RecentRetainedEvidenceReady);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RecentRawSidecarWithoutItsJournalRowCannotAdmitCapture()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using (var previous = CreateStore())
        {
            var active = await previous.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            var (path, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
            var recent = Path.Combine(_options.Value.RawIngressRoot, "frames", "2026", "07", "23", "Raw", "recent.json");
            Directory.CreateDirectory(Path.GetDirectoryName(recent)!);
            RetainedFileMutations.Move(path, recent);
        }
        using var restarted = CreateStore();
        _ = await restarted.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(restarted.RecentRetainedEvidenceReady);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnexpiredPeriodicRefreshKeepsQualificationUntilActualDriftIsObserved()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddHours(23);
        store.BeforeRetainedManifestValidationForTest = () => Assert.IsFalse(store.RequiresRetainedIntegrityScrub);
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await File.AppendAllTextAsync(path, " ").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsTrue(store.RequiresRetainedIntegrityScrub);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecentStartupInspectsCurrentBytesButNeverGrantsWholeCorpusTrust(bool tamper)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var path = Path.Combine(_options.Value.RawIngressRoot, "derived", "2026", "07", "23", "Metadata", "recent.json");
        using (var previous = CreateStore())
        {
            _ = await previous.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await RetainedFileMutations.WriteAllTextAsync(path, "{}", CancellationToken.None).ConfigureAwait(false);
        }
        if (tamper) await File.AppendAllTextAsync(path, " ").ConfigureAwait(false);
        using var restarted = CreateStore();
        var visited = 0;
        restarted.BeforeRetainedManifestValidationForTest = () => visited++;
        _ = await restarted.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, visited);
        Assert.AreEqual(!tamper, restarted.RecentRetainedEvidenceReady);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        if (tamper)
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                _ = await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        else
            _ = await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OlderTamperIsFoundByBackgroundVerificationBeforeAnyLifecycleQualification()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var path = Path.Combine(_options.Value.RawIngressRoot, "derived", "2026", "07", "01", "Metadata", "older.json");
        using (var previous = CreateStore())
        {
            _ = await previous.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await RetainedFileMutations.WriteAllTextAsync(path, "{}", CancellationToken.None).ConfigureAwait(false);
        }
        await File.AppendAllTextAsync(path, " ").ConfigureAwait(false);
        using var restarted = CreateStore();
        var visited = 0;
        restarted.BeforeRetainedManifestValidationForTest = () => visited++;
        _ = await restarted.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, visited);
        Assert.IsTrue(restarted.RecentRetainedEvidenceReady);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        _ = await restarted.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsGreaterThan(0, visited);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task BackgroundCorpusWalkDoesNotOwnTheConfigurationGate()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var gate = (SemaphoreSlim)typeof(ProtectedDeploymentLocationStore).GetField("_gate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
        store.BeforeRetainedManifestValidationForTest = () =>
        {
            Assert.IsTrue(gate.Wait(0), "The corpus walk must not hold the configuration gate.");
            gate.Release();
        };
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ColdOrdinaryStartupDoesNotWalkNonRecentRetainedHistory()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using (var previous = CreateStore())
        {
            var active = await previous.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        }
        using var restarted = CreateStore();
        var visited = 0;
        restarted.BeforeRetainedManifestValidationForTest = () => visited++;
        _ = await restarted.LoadCurrentAsync(seed, verification: false, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, visited, "Cold ordinary loading must not walk the older/unknown-layout corpus before serving the host.");
    }

    [TestMethod]
    public async Task WarmVerificationRefusesDeletedLedgerEvenWhenItsSqliteHandleIsOpen()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        File.Delete(Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName));
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FocusRetentionCanPruneItsOwnNewlyPublishedOlderRecordWithoutDrift()
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
        _ = await focus.SaveAsync(Record("newer", Now), CancellationToken.None).ConfigureAwait(false);
        _ = await focus.SaveAsync(Record("older", Now.AddHours(-1)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(_options.Value.RawIngressRoot,
            ManualFocusSessionStore.DirectoryName), "*.json").Count());
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task NestedDeleteStillRefusesBytesThatDifferFromItsDeclaredInFlightWrite()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        _ = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var path = Path.Combine(_options.Value.RawIngressRoot, "inflight.json");
        using var write = RetainedIntegrityLedger.BeginWrite(path, "{}"u8.ToArray());
        await File.WriteAllTextAsync(path, "{\"unexpected\":true}").ConfigureAwait(false);
        Assert.ThrowsExactly<InvalidDataException>(() => RetainedFileMutations.Delete(path));
        Assert.IsTrue(File.Exists(path));
    }
}
