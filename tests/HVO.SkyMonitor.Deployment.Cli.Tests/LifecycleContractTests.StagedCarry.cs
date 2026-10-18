using System.Security.Cryptography;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Cap-exception correction round 2 of #1314 (PR #1340), R21: a restore carries its counters into the staged copy of a
/// store before that copy replaces the live one, so the live store is only ever the snapshot's bytes or a verified
/// carried copy installed by rename. A carry that fails on the staged copy, however it fails, leaves the live store
/// byte for byte the snapshot's and the restore completes; a pure copy that fails leaves the restore pending for
/// <c>--resume</c>, which then completes it.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("apply-verify", "-journal", "commit", DisplayName = "commit I/O failure leaving partial pages and a hot journal")]
    [DataRow("apply-verify", "-journal", "rollback", DisplayName = "rollback failure after partial pages and a hot journal")]
    [DataRow("apply-verify", "-wal", "dispose", DisplayName = "close failure leaving partial pages and a write-ahead log")]
    [DataRow("apply-closed", "-journal", null, DisplayName = "a clean close that still leaves a hot journal")]
    [DataRow("apply-closed", "", null, DisplayName = "a closed copy that no longer passes its integrity check")]
    public async Task UpgradeAsync_ACarryThatFailsOnTheStagedCopy_LeavesTheLiveStoreByteForByteTheSnapshot(string step, string sidecar, string? fault)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            AdvanceFleet(fixture.Paths, toNextSequence: 20);
            ExecuteSql(RawIngressPath(fixture.Paths), $"UPDATE raw_capture_sequences SET last_sequence = 9 WHERE agent_id = '{AgentId}';");
        };
        using var error = new StringWriter();
        CameraAgentStateCounters.CarryStep = current =>
        {
            if (current != $"{step}:{FleetStore}") return;
            DamageCarriedFleetStore(fixture.Paths, sidecar);
            switch (fault)
            {
                case "commit": throw new IOException("disk I/O error");
                case "rollback": throw new SqliteException("cannot rollback - no transaction is active", 1);
                case "dispose": throw new SqliteException("disk I/O error", 10);
            }
        };
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
                fixture.Uid, fixture.Gid, CancellationToken.None, error: error));
        }
        finally
        {
            CameraAgentStateCounters.CarryStep = static _ => { };
        }

        // The restore completed, degraded: the original runs on a fleet store that is exactly the snapshot's.
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId, error.ToString());
        Assert.IsTrue(fixture.Runner.Running);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsNotNull(journal.StateSnapshotRestoredUtc);
        await AssertLiveFleetStoreIsTheSnapshotsAsync(fixture, journal.StateSnapshot!);
        StringAssert.Contains(error.ToString(), $"could not carry the counters of '{FleetStore}'", StringComparison.Ordinal);
        // Another store's carry is untouched by the fault.
        Assert.AreEqual(9, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", $"SELECT last_sequence FROM raw_capture_sequences WHERE agent_id = '{AgentId}';"));
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_APureCopyThatFailsAfterAFailedCarry_LeavesTheRestorePendingAndResumeCompletesIt()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) AdvanceFleet(fixture.Paths, toNextSequence: 20);
        };
        // The carry fails on the staged copy, and then so does the pure copy that replaces it.
        CameraAgentStateCounters.CarryStep = current =>
        {
            if (current != $"apply-verify:{FleetStore}") return;
            DamageCarriedFleetStore(fixture.Paths, "-journal");
            throw new IOException("disk I/O error");
        };
        CameraAgentStateSnapshot.StepCompleted = current =>
        {
            if (current == $"restore-recopy:{FleetStore}") throw new IOException("injected failure of the pure copy");
        };
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
                fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));
        }
        finally
        {
            CameraAgentStateCounters.CarryStep = static _ => { };
            ResetCrashHooks();
        }

        // Nothing recorded the restore complete, so --resume still owes it.
        var pending = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsTrue(pending.MutationStarted);
        Assert.IsNotNull(pending.StateSnapshot);
        Assert.IsNull(pending.StateSnapshotRestoredUtc, "A restore whose pure copy failed was recorded complete.");

        long? carriedOnResume = null;
        CameraAgentStateSnapshot.StepCompleted = current =>
        {
            if (current == "restore-directories-synced" && carriedOnResume is null)
                carriedOnResume = ReadCounter(fixture.Paths, FleetStore, "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;");
        };
        LifecycleResult result;
        try
        {
            result = await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
                error: TextWriter.Null);
        }
        finally
        {
            ResetCrashHooks();
        }

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(20L, carriedOnResume, "The resumed restore did not put the snapshot back with its counters carried.");
    }

    /// <summary>
    /// What a failed commit, rollback or close can leave of the file being carried: part of a page written and a rollback
    /// journal or write-ahead log beside it. The file being carried is the restore's staged copy when there is one, and
    /// the live store otherwise.
    /// </summary>
    private static void DamageCarriedFleetStore(InstallationPaths paths, string sidecar)
    {
        var live = CameraAgentStateSnapshot.StorePath(paths.StateRoot, FleetStore);
        var carried = Directory.GetFiles(Path.GetDirectoryName(live)!, $".{Path.GetFileName(live)}.restore-*.tmp").SingleOrDefault() ?? live;
        using (var stream = new FileStream(carried, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Seek(Math.Min(4096, stream.Length - 64), SeekOrigin.Begin);
            stream.Write(Enumerable.Repeat((byte)0x5A, 64).ToArray());
            stream.Flush(flushToDisk: true);
        }
        if (sidecar.Length > 0) File.WriteAllBytes(carried + sidecar, Enumerable.Repeat((byte)0x01, 512).ToArray());
    }

    private static async Task AssertLiveFleetStoreIsTheSnapshotsAsync(LifecycleFixture fixture, LifecycleStateSnapshot snapshot)
    {
        var manifest = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, snapshot, CancellationToken.None);
        var recorded = manifest.Databases.Single(static database => database.RelativePath == FleetStore);
        var live = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore);
        Assert.AreEqual(recorded.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(live))),
            "The live fleet store is not byte for byte the snapshot's.");
        foreach (var suffix in CameraAgentStateLayout.SqliteSidecarSuffixes)
            Assert.IsFalse(File.Exists(live + suffix), $"The restore left '{suffix}' beside the live fleet store.");
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(live)!, ".*.restore-*"), "A staged copy was left behind.");
    }
}
