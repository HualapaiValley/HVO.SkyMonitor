using System.Globalization;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Restore handles candidate databases with malformed schedule profiles or counters. The fixed checkpoint inventory
/// works independently of schedule profiles. For malformed counters recovery carries the marks already recorded.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string ProductsProfile = "{\"processingSteps\":[]}";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("text")]
    [DataRow("null")]
    [DataRow("non-utf8")]
    [DataRow("malformed-json")]
    public async Task UpgradeAsync_FailedCandidateThatRewroteAScheduleProfile_RecoversOntoItsSnapshot(string shape)
    {
        using var fixture = await CanonicalProfileFixtureAsync();
        RewriteProfileOnFirstCandidateStart(fixture, shape);
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture), fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        AssertRecoveredOntoTheSnapshotsProfile(fixture.Paths, canonical: true);
        Assert.DoesNotContain("could not read the storage roots", error.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("text")]
    [DataRow("null")]
    [DataRow("non-utf8")]
    [DataRow("malformed-json")]
    public async Task InterruptedUpgradeWhoseCandidateRewroteAScheduleProfile_RecoversOntoItsSnapshot(string shape)
    {
        using var fixture = await CanonicalProfileFixtureAsync();
        RewriteProfileOnFirstCandidateStart(fixture, shape);
        var request = UpgradeRequest(fixture);
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        // The installer loses power once the candidate is verified on its rewritten journal.
        RestoreCrashImage(fixture, images.First(static image => image.Step == "journal:CandidateVerified"));
        Assert.IsFalse(ColumnExists(RawIngressPath(fixture.Paths), "capture_schedule_revisions", "profile_sha256"));
        var restoredJournalOnResume = false;
        CameraAgentStateSnapshot.StepCompleted = step =>
        {
            if (step == "restore-directories-synced") restoredJournalOnResume = ColumnExists(RawIngressPath(fixture.Paths), "capture_schedule_revisions", "profile_sha256");
        };
        using var error = new StringWriter();
        LifecycleResult result;
        try
        {
            result = await CameraAgentLifecycleManager.ExecuteAsync(
                request with { Resume = true },
                fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                fixture.Uid, fixture.Gid, CancellationToken.None, error: error);
        }
        finally
        {
            ResetCrashHooks();
        }

        Assert.IsTrue(restoredJournalOnResume, "The recovery did not put the snapshot's journal back.");
        Assert.DoesNotContain("could not read the storage roots", error.ToString(), StringComparison.Ordinal);
        // The retry runs the candidate again on the restored stores, and this time it leaves the profile alone.
        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        AssertRecoveredOntoTheSnapshotsProfile(fixture.Paths, canonical: true);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("text-value")]
    [DataRow("null-value")]
    [DataRow("real-value")]
    [DataRow("out-of-range")]
    [DataRow("text-key")]
    public async Task UpgradeAsync_FailedCandidateThatLeftACounterInAnotherShape_RecoversOntoItsSnapshot(string shape)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, environmentRecordId: 10);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            ExecuteSql(RawIngressPath(fixture.Paths), RewriteControlStateSql(shape));
            // A store the candidate left readable is still carried.
            AdvanceEnvironmentalJournal(fixture.Paths, recordId: 20);
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        // The snapshot's control state is back in its own shape, carried no further than the journal recorded it.
        Assert.AreEqual(3, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db",
            "SELECT version FROM capture_control_state WHERE state_key = 1 AND typeof(version) = 'integer';"));
        Assert.AreEqual(20, ReadCounter(fixture.Paths, EnvironmentStore, EnvironmentalSequence));
        StringAssert.Contains(error.ToString(), "could not read the counters of 'raw/journal/raw-ingress.db'", StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), shape switch
        {
            "text-value" => "holds a value stored as text in version",
            "null-value" => "holds a value stored as null in version",
            "real-value" => "holds a value stored as real in version",
            "out-of-range" => "holds a value outside the range a restore can carry in version",
            _ => "holds a key stored as text in state_key"
        }, StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateWhoseLatestAutomationRevisionIsNotJson_NeverCarriesItIntoTheRestoredStore()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var automation = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(automation, FixtureDump(AutomationStore));
        SaveDefinition(automation, "kept", 1);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            // The candidate creates a definition whose revision the previous release could not read back.
            SaveDefinition(automation, "created", 1);
            ExecuteSql(automation, "UPDATE automation_definition_revisions SET definition_json = 'not json' WHERE definition_id = 'created';");
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(1, ReadCounter(fixture.Paths, AutomationStore, "SELECT version FROM automation_definitions WHERE definition_id = 'kept';"));
        Assert.AreEqual(0, ReadCounter(fixture.Paths, AutomationStore,
            "SELECT COUNT(*) FROM automation_definition_revisions WHERE definition_json = 'not json';"));
        StringAssert.Contains(error.ToString(), $"could not read the counters of '{AutomationStore}'", StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), "a revision that is not well-formed JSON", StringComparison.Ordinal);
    }

    private static async Task<LifecycleFixture> CanonicalProfileFixtureAsync()
    {
        var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, recoveryState: true,
            installedImage: static image => CurrentStateContract(image) with { RawIngressSchema = "15" });
        fixture.Runner.CandidateRawIngressSchema = "15";
        fixture.Runner.CandidateLabels["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        WriteCanonicalV15RawIngressJournal(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths),
            "INSERT INTO capture_control_state(state_key, state, version, updated_unix_ms) VALUES(1, 'running', 3, 0);" +
            "INSERT INTO capture_schedule_revisions(revision_id, revision_number, profile_json, profile_sha256, schedule_sha256, source, actor, reason, created_unix_ms) " +
            $"VALUES('r1', 1, CAST('{ProductsProfile}' AS BLOB), '{new string('a', 64)}', '{new string('b', 64)}', 'api', 'owner', NULL, 0);");
        WriteIdentityLineage(fixture.Paths);
        ExecuteSql(CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, EnvironmentStore),
            EnvironmentalFixtureSchema + "INSERT INTO environmental_observation_journal VALUES(1);");
        // An installed instance's Compose files are owner-only, as the installer writes them; restore-only checks that.
        foreach (var file in ComposeFileNames.Select(name => Path.Combine(fixture.Paths.ConfigRoot, "compose", name)))
            SafeFileSystem.WriteTextAtomic(file, await File.ReadAllTextAsync(file));
        return fixture;
    }

    private static void RewriteProfileOnFirstCandidateStart(LifecycleFixture fixture, string shape)
    {
        var candidateStarts = 0;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId || candidateStarts++ > 0) return;
            ExecuteSql(RawIngressPath(fixture.Paths), RewriteProfileSql(shape));
            AdvanceEnvironmentalJournal(fixture.Paths, recordId: 3);
        };
    }

    /// <summary>The journal's only schedule revision, rebuilt as a migration that changed the profile's storage would leave it.</summary>
    private static string RewriteProfileSql(string shape)
        => "PRAGMA foreign_keys = OFF; DROP TABLE capture_schedule_revisions;" +
           "CREATE TABLE capture_schedule_revisions(revision_id TEXT PRIMARY KEY, revision_number INTEGER NOT NULL UNIQUE, profile_json);" +
           $"INSERT INTO capture_schedule_revisions VALUES('r1', 1, {ProfileValue(shape)});";

    private static string ProfileValue(string shape)
    {
        byte[] notUtf8 = [.. "{\"processingSteps\":[{\"options\":{\"storageRoot\":\"/app/data/archive/"u8, 0xFF, 0xFE, .. "\"}}]}"u8];
        return shape switch
        {
            "text" => $"'{ProductsProfile}'",
            "null" => "NULL",
            "non-utf8" => $"X'{Convert.ToHexString(notUtf8)}'",
            "malformed-json" => $"CAST('{ProductsProfile[..^3]}' AS BLOB)",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown profile shape.")
        };
    }

    /// <summary>The capture-control row a candidate left in <paramref name="shape"/>.</summary>
    private static string RewriteControlStateSql(string shape)
    {
        const string loose = "DROP TABLE capture_control_state; CREATE TABLE capture_control_state(state_key {0} PRIMARY KEY, state TEXT, version, updated_unix_ms INTEGER);" +
                             "INSERT INTO capture_control_state VALUES({1}, 'paused', {2}, 0);";
        return shape switch
        {
            "text-value" => string.Format(CultureInfo.InvariantCulture, loose, "INTEGER", "1", "'seven'"),
            "null-value" => string.Format(CultureInfo.InvariantCulture, loose, "INTEGER", "1", "NULL"),
            "real-value" => string.Format(CultureInfo.InvariantCulture, loose, "INTEGER", "1", "7.5"),
            "out-of-range" => "UPDATE capture_control_state SET version = 9223372036854775807 WHERE state_key = 1;",
            "text-key" => string.Format(CultureInfo.InvariantCulture, loose, "TEXT", "'main'", "7"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown counter shape.")
        };
    }

    /// <summary>
    /// The journal has the snapshot's revision and the environmental journal carries the later record-ID floor.
    /// </summary>
    private static void AssertRecoveredOntoTheSnapshotsProfile(InstallationPaths paths, bool canonical)
    {
        Assert.AreEqual(1, ReadCounter(paths, "raw/journal/raw-ingress.db",
            $"SELECT COUNT(*) FROM capture_schedule_revisions WHERE typeof(profile_json) = 'blob' AND CAST(profile_json AS TEXT) = '{ProductsProfile}';"));
        if (canonical) Assert.IsTrue(ColumnExists(RawIngressPath(paths), "capture_schedule_revisions", "profile_sha256"));
        Assert.AreEqual(1, ReadCounter(paths, EnvironmentStore, "SELECT COUNT(*) FROM environmental_observation_journal;"));
        Assert.AreEqual(3, ReadCounter(paths, EnvironmentStore, EnvironmentalSequence));
    }
}
