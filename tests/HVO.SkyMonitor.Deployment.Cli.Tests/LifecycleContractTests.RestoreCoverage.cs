using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Correction round 2 of #1314 (PR #1340): a restore syncs every directory it relies on before it records that it is
/// complete, even one an interrupted attempt created (R11); every recovery authenticates the snapshot it will restore
/// before it changes any container (R12).
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ARetriedRestoreSyncsADirectoryAnInterruptedAttemptCreatedBeforeRecordingCompletion()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var automation = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        var automationDirectory = Path.GetDirectoryName(automation)!;
        ExecuteSql(automation, "CREATE TABLE automation_definitions(definition_id TEXT PRIMARY KEY, version INTEGER NOT NULL);");
        // The candidate removes the store's directory, so its recovery has to create it again.
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) Directory.Delete(automationDirectory, recursive: true);
        };
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
                fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));
        }
        finally
        {
            ResetCrashHooks();
        }
        // The power is lost just after a restore attempt recreated the directory and before it synced its parent.
        RestoreCrashImage(fixture, images.First(static image => image.Step == "journal:Restoring"));
        Assert.IsFalse(Directory.Exists(automationDirectory));
        SafeFileSystem.CreateRuntimeDirectory(automationDirectory, fixture.Uid, fixture.Gid);
        fixture.Runner.OnComposeUp = null;
        var flushed = new List<string>();
        string[]? atCompletion = null;
        NativeLinux.DirectoryFlushed = path => flushed.Add(Path.TrimEndingDirectorySeparator(path));
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            if (state.StateSnapshotRestoredUtc is not null && atCompletion is null) atCompletion = [.. flushed.Take(flushed.Count - 1)];
        };
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
                error: TextWriter.Null)).Outcome);
        }
        finally
        {
            NativeLinux.DirectoryFlushed = static _ => { };
            CameraAgentLifecycleManager.JournalRecorded = static _ => { };
        }

        Assert.IsNotNull(atCompletion);
        CollectionAssert.IsSubsetOf(Ancestors(automationDirectory, fixture.Paths.ProductRoot), atCompletion,
            $"Synced before the restore was recorded complete: {string.Join(", ", atCompletion)}");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateRecoveryAuthenticatesItsSnapshotBeforeStoppingTheCandidate()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var stopsAtCandidate = -1;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            stopsAtCandidate = fixture.Runner.ComposeStopCount;
            // The snapshot is damaged while the candidate runs, before the candidate fails its verification.
            var journal = CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None)
                .GetAwaiter().GetResult()!;
            File.Delete(Path.Combine(CameraAgentStateSnapshot.SnapshotRoot(
                fixture.Paths, journal.OperationId, journal.StateSnapshot!.SnapshotId), "manifest.json"));
        };

        var failed = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));

        StringAssert.Contains(failed.Message + failed.InnerException?.Message, "missing its manifest", StringComparison.Ordinal);
        Assert.AreEqual(stopsAtCandidate, fixture.Runner.ComposeStopCount, "Recovery stopped the candidate before it found it could not restore.");
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateThatLeftTheJournalUnreadable_StillRecoversAndSaysWhatItCouldNotRead()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            var journal = RawIngressPath(fixture.Paths);
            foreach (var suffix in CameraAgentStateLayout.SqliteSidecarSuffixes) File.Delete(journal + suffix);
            File.WriteAllBytes(journal, new byte[8192]);
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        // The original runs again on its restored journal.
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        Assert.IsTrue(TableExists(RawIngressPath(fixture.Paths), RawSentinelTable));
        StringAssert.Contains(error.ToString(), "could not read", StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), "raw/journal/raw-ingress.db", StringComparison.Ordinal);
    }

}
