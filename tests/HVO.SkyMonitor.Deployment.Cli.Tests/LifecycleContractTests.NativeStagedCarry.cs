using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("commit")]
    [DataRow("rollback")]
    [DataRow("dispose")]
    public async Task UpgradeAsync_NativeStagedCarryIoFailure_RestoresTheSnapshot(string operation)
    {
        using var fixture = await NativeCarryFixtureAsync();
        using var fault = new NativeSqliteIoFault(fixture.Paths.StateRoot);
        using var error = new StringWriter();
        byte[]? beforeCarry = null;
        CameraAgentStateCounters.CarryStep = step =>
        {
            var live = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore);
            if (step == $"apply:{FleetStore}")
                beforeCarry = File.ReadAllBytes(Directory.GetFiles(Path.GetDirectoryName(live)!, ".fleet-status.db.restore-*.tmp").Single());
            if (step != $"apply-commit:{FleetStore}") return;
            var staged = Directory.GetFiles(Path.GetDirectoryName(live)!, ".fleet-status.db.restore-*.tmp").Single();
            Assert.IsNotNull(beforeCarry);
            CollectionAssert.AreNotEqual(beforeCarry, File.ReadAllBytes(staged), "SQLite did not spill dirty pages before the fault.");
            Assert.IsTrue(File.Exists(staged + "-journal"), "SQLite created no real rollback journal.");
            fault.Arm(operation);
            // Select the rollback/disposal path after SQLite has spilled real dirty pages. The error under test
            // then comes from the native rollback pwrite/close, rather than from this control-flow exception.
            if (operation != "commit") throw new IOException("Enter rollback with spilled pages.");
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

        Assert.IsGreaterThan(0, fault.WriteFailures,
            "SQLite never attempted the intended failing native operation.");
        if (operation == "dispose") Assert.IsGreaterThan(0, fault.CloseFailures, "The native close did not fail.");
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId, error.ToString());
        Assert.IsTrue(fixture.Runner.Running);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsNotNull(journal.StateSnapshotRestoredUtc);
        await AssertLiveFleetStoreIsTheSnapshotsAsync(fixture, journal.StateSnapshot!);
        StringAssert.Contains(error.ToString(), $"could not carry the fleet-report-sequence counter of '{FleetStore}'", StringComparison.Ordinal);
        Assert.AreEqual(9, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db",
            $"SELECT last_sequence FROM raw_capture_sequences WHERE agent_id = '{AgentId}';"));
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_NativeMidCopyFailure_LeavesRestorePendingAndResumeCompletesIt()
    {
        const ulong limit = 128 * 1024;
        using var fixture = await NativeCarryFixtureAsync();
        using var fault = new NativeSqliteIoFault(fixture.Paths.StateRoot);
        NativeFileSizeLimit? fileSizeLimit = null;
        CameraAgentStateCounters.CarryStep = step =>
        {
            if (step == $"apply-commit:{FleetStore}") fault.Arm("commit");
        };
        CameraAgentStateSnapshot.StepCompleted = step =>
        {
            if (step == $"restore-recopy:{FleetStore}") fileSizeLimit = new NativeFileSizeLimit(limit);
        };
        using var error = new StringWriter();
        InstallerException failure;
        try
        {
            failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
                fixture.Uid, fixture.Gid, CancellationToken.None, error: error));
        }
        finally
        {
            fileSizeLimit?.Dispose();
            CameraAgentStateCounters.CarryStep = static _ => { };
            ResetCrashHooks();
        }
        Assert.IsNotNull(fileSizeLimit, "The pure recopy fault was never armed.");
        Assert.IsGreaterThan(0, fault.WriteFailures);
        var live = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore);
        var partial = Directory.GetFiles(Path.GetDirectoryName(live)!, ".fleet-status.db.restore-*.tmp").Single();
        Assert.AreEqual((long)limit, new FileInfo(partial).Length, "The copy did not write its first block before failing.");
        Assert.IsGreaterThan((long)limit, new FileInfo(live).Length);
        StringAssert.Contains(failure.Message, "file length was too large", StringComparison.OrdinalIgnoreCase);
        var pending = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsTrue(pending.MutationStarted);
        Assert.IsNotNull(pending.StateSnapshot);
        Assert.IsNull(pending.StateSnapshotRestoredUtc);
        await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, pending.StateSnapshot, CancellationToken.None);
        fault.Arm("disabled");
        long? carried = null;
        var restoreRecorded = false;
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            if (state.StateSnapshot == pending.StateSnapshot && state.StateSnapshotRestoredUtc is not null)
                restoreRecorded = true;
        };
        CameraAgentStateSnapshot.StepCompleted = step =>
        {
            if (step == "restore-directories-synced")
                carried = ReadCounter(fixture.Paths, FleetStore, "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;");
        };
        try
        {
            var result = await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);
            Assert.AreEqual("completed", result.Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        Assert.AreEqual(20L, carried);
        Assert.IsTrue(restoreRecorded, "The resumed restore never durably recorded its completion before retrying the upgrade.");
        foreach (var suffix in CameraAgentStateLayout.SqliteSidecarSuffixes)
            Assert.IsFalse(File.Exists(live + suffix));
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(live)!, ".*.restore-*"));
    }

    private static async Task<LifecycleFixture> NativeCarryFixtureAsync()
    {
        var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        // Exceed SQLite's default page cache so rollback must restore pages through native pwrite.
        ExecuteSql(CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore), """
            CREATE TABLE native_fault_pages (id INTEGER PRIMARY KEY, payload BLOB NOT NULL);
            WITH RECURSIVE rows(id) AS (SELECT 1 UNION ALL SELECT id + 1 FROM rows WHERE id < 2048)
            INSERT INTO native_fault_pages SELECT id, zeroblob(4096) FROM rows;
            CREATE TRIGGER native_fault_spill AFTER UPDATE OF next_sequence ON fleet_status_metadata
            BEGIN UPDATE native_fault_pages SET payload = randomblob(4096); END;
            """);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            AdvanceFleet(fixture.Paths, toNextSequence: 20);
            ExecuteSql(RawIngressPath(fixture.Paths), $"UPDATE raw_capture_sequences SET last_sequence = 9 WHERE agent_id = '{AgentId}';");
        };
        return fixture;
    }
}
