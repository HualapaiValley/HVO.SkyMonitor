using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>
/// Covers the versioned durable local automation contract: immutable revisions, expected-version
/// conflicts, idempotent replay and its retention window, bounded run history, restart recovery, and
/// the rollback-safe separation from the raw ingress journal.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-05T00:00:00Z", CultureInfo.InvariantCulture);

    private string _root = null!;
    private MutableTimeProvider _timeProvider = null!;
    private StubAutomationTaskRegistry _registry = null!;
    private StubCaptureSequenceSource _captureSequence = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-automation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _timeProvider = new MutableTimeProvider(Now);
        _registry = new StubAutomationTaskRegistry();
        _captureSequence = new StubCaptureSequenceSource();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_CreatesItsOwnDatabaseAndLeavesTheRawIngressJournalUntouched()
    {
        using var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        var path = Path.Combine(_root, ".automation", "local-automations.db");
        Assert.IsTrue(File.Exists(path), "The automation store owns its own database file.");
        // Nothing is written into the raw ingress journal, so a rollback to a baseline image that
        // pins that journal's schema version still opens it and simply ignores this file.
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "journal")));

        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(
            (long)LocalAutomationContract.CurrentSchemaVersion,
            Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task InitializeAsync_RefusesANewerSchemaVersion()
    {
        using (var store = CreateStore())
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var path = Path.Combine(_root, ".automation", "local-automations.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 9;";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await reopened.InitializeAsync(CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task GetStateAsync_CreatesTheSchemaWithoutAnExplicitInitializeCall()
    {
        // An operator read can arrive before the runner's hosted start; it must see a valid store
        // rather than a missing-table failure.
        using var store = CreateStore();

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0L, state.StoreVersion);
        Assert.IsEmpty(state.Definitions);
        Assert.IsTrue(File.Exists(Path.Combine(_root, ".automation", "local-automations.db")));
    }

    [TestMethod]
    public async Task SaveAsync_RecordsAnImmutableRevisionWithAStableRevisionHash()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);

        var result = await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Applied, result.Status);
        var definition = result.State.Definitions.Single();
        Assert.AreEqual(1L, definition.Version);
        Assert.AreEqual(64, definition.RevisionSha256.Length);
        Assert.AreEqual(
            SqliteLocalAutomationStore.ComputeRevisionSha256(definition.Definition), definition.RevisionSha256);
        Assert.AreEqual(1, definition.History.Count);
        Assert.IsFalse(definition.History[0].Removed);
        Assert.AreEqual("owner", definition.Actor);
        // The next occurrence is one whole interval after the epoch, never at the instant of the save.
        Assert.AreEqual(Now.AddSeconds(3600), definition.NextRunUtc);
        Assert.AreEqual(definition.Definition.DefinitionId, result.State.Calendar.Single().DefinitionId);
    }

    [TestMethod]
    public async Task SaveAsync_RejectsAnUnregisteredTargetAndAnUnregisteredTriggerCombination()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);

        var unknownTarget = await store
            .SaveAsync(SaveRequest(target: "not-registered"), CancellationToken.None).ConfigureAwait(false);
        _registry.Triggers.Remove(LocalAutomationTriggerKind.CaptureRelative);
        var unknownTrigger = await store.SaveAsync(
            SaveRequest(trigger: LocalAutomationTriggerKind.CaptureRelative, interval: 5, key: "k2"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Invalid, unknownTarget.Status);
        Assert.AreEqual(LocalAutomationContract.UnregisteredTargetReasonCode, unknownTarget.ReasonCode);
        Assert.AreEqual(LocalAutomationCommandStatus.Invalid, unknownTrigger.Status);
        Assert.AreEqual(LocalAutomationContract.UnregisteredCombinationReasonCode, unknownTrigger.ReasonCode);
        Assert.IsEmpty(unknownTrigger.State.Definitions);
    }

    [TestMethod]
    public async Task SaveAsync_RejectsAnIntervalOutsideTheContractBounds()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);

        var result = await store.SaveAsync(
            SaveRequest(interval: LocalAutomationContract.MinimumPeriodicIntervalSeconds - 1),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Invalid, result.Status);
        Assert.AreEqual(LocalAutomationContract.InvalidCommandReasonCode, result.ReasonCode);
        Assert.AreEqual("definition.triggerInterval", result.FieldPath);
    }

    [TestMethod]
    public async Task SaveAsync_EnforcesTheExpectedVersionAndReplaysAnIdenticalCommand()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        var stale = await store.SaveAsync(
            SaveRequest(name: "Renamed", expectedVersion: 0, key: "stale"), CancellationToken.None)
            .ConfigureAwait(false);
        var replay = await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
        var divergent = await store.SaveAsync(
            SaveRequest(name: "Different"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, stale.Status);
        Assert.AreEqual(LocalAutomationContract.ExpectedVersionConflictReasonCode, stale.ReasonCode);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed, replay.Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, divergent.Status);
        Assert.AreEqual(LocalAutomationContract.IdempotencyKeyConflictReasonCode, divergent.ReasonCode);
        Assert.AreEqual(1L, replay.State.Definitions.Single().Version);
    }

    [TestMethod]
    public async Task SaveAsync_ReportsUnchangedWhenTheStoredDefinitionAlreadyMatches()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        var result = await store.SaveAsync(
            SaveRequest(expectedVersion: 1, key: "second"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Unchanged, result.Status);
        Assert.AreEqual(1L, result.State.Definitions.Single().Version);
        Assert.AreEqual(1, result.State.Definitions.Single().History.Count);
    }

    [TestMethod]
    public async Task SaveAsync_RetiresTheOldestRevisionsBeyondTheRetentionBound()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
        for (var version = 1; version <= LocalAutomationContract.MaximumRetainedRevisions + 5; version++)
        {
            var result = await store.SaveAsync(
                SaveRequest(
                    name: string.Create(CultureInfo.InvariantCulture, $"Revision {version}"),
                    expectedVersion: version,
                    key: string.Create(CultureInfo.InvariantCulture, $"key-{version}")),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, result.Status);
        }

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var definition = state.Definitions.Single();
        // Storage retains the full window; the projection deliberately returns a smaller page of it.
        Assert.AreEqual(
            (long)LocalAutomationContract.MaximumRetainedRevisions,
            await CountAsync("automation_definition_revisions").ConfigureAwait(false));
        Assert.AreEqual(LocalAutomationContract.MaximumProjectedRevisions, definition.History.Count);
        Assert.AreEqual(definition.Version, definition.History[0].Version);
    }

    [TestMethod]
    public async Task SaveAsync_ForgetsAnIdempotencyKeyOlderThanTheReplayWindow()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        _timeProvider.Advance(LocalAutomationContract.IdempotencyReplayWindow + TimeSpan.FromMinutes(1));
        // A later command prunes the ledger, so the original key is no longer replayable.
        await store.SaveAsync(
            SaveRequest(name: "Later", expectedVersion: 1, key: "later"), CancellationToken.None)
            .ConfigureAwait(false);
        var replay = await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, replay.Status);
        Assert.AreEqual(LocalAutomationContract.ExpectedVersionConflictReasonCode, replay.ReasonCode);
    }

    [TestMethod]
    public async Task SaveAsync_RefusesMoreThanTheBoundedNumberOfDefinitions()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        for (var index = 0; index < LocalAutomationContract.MaximumDefinitions; index++)
        {
            var created = await store.SaveAsync(
                SaveRequest(
                    definitionId: string.Create(CultureInfo.InvariantCulture, $"automation-{index}"),
                    key: string.Create(CultureInfo.InvariantCulture, $"create-{index}")),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, created.Status);
        }

        var overflow = await store.SaveAsync(
            SaveRequest(definitionId: "automation-overflow", key: "overflow"), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Invalid, overflow.Status);
        Assert.AreEqual(LocalAutomationContract.DefinitionLimitReasonCode, overflow.ReasonCode);
    }

    [TestMethod]
    public async Task SaveAsync_RecreatesARemovedDefinitionIdOnAContinuingRevisionLine()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
        await store.RemoveAsync(
            new LocalAutomationRemoveRequest("sky-temperature", 1, "remove", "owner", null),
            CancellationToken.None).ConfigureAwait(false);

        // "Remove it, then create it again under the same name" is the obvious operator gesture. The
        // revision line is immutable and outlives the live row, so it must continue rather than restart.
        var recreated = await store.SaveAsync(
            SaveRequest(key: "recreate"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Applied, recreated.Status);
        var definition = recreated.State.Definitions.Single();
        Assert.AreEqual(3L, definition.Version);
        Assert.AreEqual(3L, definition.History[0].Version);
        Assert.IsTrue(definition.History[1].Removed);
    }

    [TestMethod]
    public async Task RemoveAsync_BoundsTheRevisionsAndRunsOfDefinitionsThatNoLongerExist()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        for (var index = 0; index < 40; index++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"orphan-{index}");
            var created = await store.SaveAsync(
                SaveRequest(definitionId: id, key: string.Create(CultureInfo.InvariantCulture, $"c-{index}")),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, created.Status);
            var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(candidate => candidate.Definition.DefinitionId == id);
            await store.TryBeginRunAsync(
                entry,
                string.Create(CultureInfo.InvariantCulture, $"run-{index}"),
                Now.AddSeconds(3600),
                null,
                CancellationToken.None).ConfigureAwait(false);
            await store.CompleteRunAsync(
                string.Create(CultureInfo.InvariantCulture, $"run-{index}"),
                LocalAutomationRunOutcome.Succeeded,
                "ok",
                CancellationToken.None).ConfigureAwait(false);
            await store.RemoveAsync(
                new LocalAutomationRemoveRequest(
                    id, 1, string.Create(CultureInfo.InvariantCulture, $"r-{index}"), "owner", null),
                CancellationToken.None).ConfigureAwait(false);
        }

        // Removal frees the definition slot, so the definition cap cannot bound the orphan tail.
        Assert.IsEmpty((await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Definitions);
        Assert.IsLessThanOrEqualTo(
            (long)LocalAutomationContract.MaximumRetainedOrphanRevisions,
            await CountAsync("automation_definition_revisions").ConfigureAwait(false));
        Assert.IsLessThanOrEqualTo(
            (long)LocalAutomationContract.MaximumRetainedOrphanRuns,
            await CountAsync("automation_runs").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CompleteRunAsync_RecordsTheRealOutcomeAfterAnotherInstanceSettledTheRun()
    {
        using var owner = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await owner.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
        var entry = (await owner.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        await owner.TryBeginRunAsync(entry, "run-1", Now.AddSeconds(3600), null, CancellationToken.None)
            .ConfigureAwait(false);

        using (var second = CreateStore())
        {
            await second.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await owner.CompleteRunAsync(
            "run-1", LocalAutomationRunOutcome.Succeeded, "Acquired an observation.", CancellationToken.None)
            .ConfigureAwait(false);

        // A second instance settling a run this instance is still executing must not replace the real
        // outcome: completion is authoritative for the claimant that holds the run.
        var run = (await owner.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome);
    }

    [TestMethod]
    public async Task GetStateAsync_ReadsTheCaptureSequenceOnlyForACaptureRelativeDefinition()
    {
        _captureSequence.Sequence = 77;
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        var periodicOnly = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var readsAfterPeriodic = _captureSequence.Reads;
        await store.SaveAsync(
            SaveRequest(
                definitionId: "capture-relative",
                trigger: LocalAutomationTriggerKind.CaptureRelative,
                interval: 10,
                key: "capture"),
            CancellationToken.None).ConfigureAwait(false);
        var withCaptureRelative = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);

        // The acquisition journal is another database; an operator read must not open it for nothing.
        Assert.AreEqual(0, readsAfterPeriodic);
        Assert.IsNull(periodicOnly.ObservedCaptureSequence);
        Assert.AreEqual(77L, withCaptureRelative.ObservedCaptureSequence);
    }

    [TestMethod]
    public async Task SaveAsync_ReAnchorsProgressWhenADisabledDefinitionIsEnabledAgain()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(enabled: false), CancellationToken.None).ConfigureAwait(false);
        _timeProvider.Advance(TimeSpan.FromSeconds(3600 * 100));

        var enabled = await store.SaveAsync(
            SaveRequest(expectedVersion: 1, key: "enable"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Applied, enabled.Status);
        // Without re-anchoring, every boundary since the epoch would look like a missed occurrence.
        var expected = Now.AddSeconds(3600 * 101);
        Assert.AreEqual(expected, enabled.State.Definitions.Single().NextRunUtc);
    }

    [TestMethod]
    public async Task GetStateAsync_ReportsTheLastRunOfADefinitionOutsideTheProjectedRunPage()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(definitionId: "quiet"), CancellationToken.None).ConfigureAwait(false);
        await store.SaveAsync(
            SaveRequest(definitionId: "busy", key: "busy"), CancellationToken.None).ConfigureAwait(false);
        var entries = await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false);
        var quiet = entries.Single(entry => entry.Definition.DefinitionId == "quiet");
        var busy = entries.Single(entry => entry.Definition.DefinitionId == "busy");
        await store.TryBeginRunAsync(quiet, "quiet-1", Now.AddSeconds(3600), null, CancellationToken.None)
            .ConfigureAwait(false);
        await store.CompleteRunAsync(
            "quiet-1", LocalAutomationRunOutcome.Succeeded, "ok", CancellationToken.None).ConfigureAwait(false);
        for (var index = 0; index < LocalAutomationContract.MaximumProjectedRuns + 5; index++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"busy-{index}");
            await store.TryBeginRunAsync(busy, key, Now.AddSeconds(3600 * (index + 2)), null, CancellationToken.None)
                .ConfigureAwait(false);
            await store.CompleteRunAsync(key, LocalAutomationRunOutcome.Succeeded, "ok", CancellationToken.None)
                .ConfigureAwait(false);
        }

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);

        // The projected page is the newest runs across all definitions; a quiet definition falls out of it
        // and must still report its own last run rather than "never ran".
        Assert.IsFalse(state.Runs.Any(run => run.DefinitionId == "quiet"));
        Assert.IsNotNull(state.Definitions.Single(item => item.Definition.DefinitionId == "quiet").LastRun);
    }

    [TestMethod]
    public async Task GetStateAsync_BoundsTheProjectedRevisionsOfOneDefinition()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
        for (var version = 1; version <= LocalAutomationContract.MaximumProjectedRevisions + 5; version++)
        {
            await store.SaveAsync(
                SaveRequest(
                    name: string.Create(CultureInfo.InvariantCulture, $"Revision {version}"),
                    expectedVersion: version,
                    key: string.Create(CultureInfo.InvariantCulture, $"key-{version}")),
                CancellationToken.None).ConfigureAwait(false);
        }

        var definition = (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false))
            .Definitions.Single();

        Assert.AreEqual(LocalAutomationContract.MaximumProjectedRevisions, definition.History.Count);
        Assert.AreEqual(definition.Version, definition.History[0].Version);
    }

    [TestMethod]
    public async Task InitializeAsync_RefusesADriftedSchemaAndAnUnversionedDatabase()
    {
        using (var store = CreateStore())
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var path = Path.Combine(_root, ".automation", "local-automations.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE drifted (id INTEGER PRIMARY KEY) STRICT;";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();

        using var drifted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await drifted.InitializeAsync(CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        var unversionedRoot = Path.Combine(_root, "unversioned");
        Directory.CreateDirectory(Path.Combine(unversionedRoot, ".automation"));
        var unversionedPath = Path.Combine(unversionedRoot, ".automation", "local-automations.db");
        using (var connection = new SqliteConnection($"Data Source={unversionedPath};Pooling=False"))
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE stranger (id INTEGER PRIMARY KEY) STRICT;";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();

        using var unversioned = CreateStore(unversionedRoot);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await unversioned.InitializeAsync(CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RemoveAsync_RetainsRecordedHistoryAndRefusesAStaleVersion()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);

        var stale = await store.RemoveAsync(
            new LocalAutomationRemoveRequest("sky-temperature", 5, "remove-stale", "owner", null),
            CancellationToken.None).ConfigureAwait(false);
        var removed = await store.RemoveAsync(
            new LocalAutomationRemoveRequest("sky-temperature", 1, "remove", "owner", "retired"),
            CancellationToken.None).ConfigureAwait(false);
        var missing = await store.RemoveAsync(
            new LocalAutomationRemoveRequest("sky-temperature", 2, "remove-again", "owner", null),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, stale.Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, removed.Status);
        Assert.IsEmpty(removed.State.Definitions);
        Assert.AreEqual(LocalAutomationCommandStatus.NotFound, missing.Status);
        Assert.AreEqual(LocalAutomationContract.UnknownDefinitionReasonCode, missing.ReasonCode);
    }

    [TestMethod]
    public async Task TryBeginRunAsync_IsIdempotentPerOccurrenceAndAdvancesProgressWithTheClaim()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
        var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        var occurrence = Now.AddSeconds(3600);

        var first = await store.TryBeginRunAsync(entry, "run-1", occurrence, null, CancellationToken.None)
            .ConfigureAwait(false);
        var second = await store.TryBeginRunAsync(entry, "run-1", occurrence, null, CancellationToken.None)
            .ConfigureAwait(false);
        await store.CompleteRunAsync(
            "run-1", LocalAutomationRunOutcome.Succeeded, "Acquired an observation.", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(first);
        Assert.IsFalse(second, "The same occurrence must be recorded exactly once.");
        var advanced = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        Assert.AreEqual(occurrence, advanced.LastOccurrenceUtc);
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var run = state.Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome);
        Assert.AreEqual(occurrence, run.ScheduledForUtc);
        Assert.AreEqual(entry.RevisionSha256, run.RevisionSha256);
        Assert.AreEqual(occurrence.AddSeconds(3600), state.Definitions.Single().NextRunUtc);
    }

    [TestMethod]
    public async Task InitializeAsync_SettlesARunTheProcessLeftClaimed()
    {
        using (var store = await CreateInitializedStoreAsync().ConfigureAwait(false))
        {
            await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
            var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
            await store.TryBeginRunAsync(entry, "run-1", Now.AddSeconds(3600), null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();

        using var restarted = CreateStore();
        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        var state = await restarted.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var run = state.Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, run.Outcome);
        Assert.IsNotNull(run.CompletedAtUtc);
        // The occurrence progress advanced with the claim, so the restart does not repeat it.
        var entryAfter = (await restarted.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        Assert.AreEqual(Now.AddSeconds(3600), entryAfter.LastOccurrenceUtc);
    }

    [TestMethod]
    public async Task CompleteRunAsync_BoundsTheRetainedRunHistoryPerDefinition()
    {
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
        var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        for (var index = 0; index < LocalAutomationContract.MaximumRetainedRuns + 10; index++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"run-{index}");
            await store.TryBeginRunAsync(entry, key, Now.AddSeconds(3600 * (index + 1)), null, CancellationToken.None)
                .ConfigureAwait(false);
            await store.CompleteRunAsync(key, LocalAutomationRunOutcome.Succeeded, "ok", CancellationToken.None)
                .ConfigureAwait(false);
        }

        Assert.AreEqual(
            (long)LocalAutomationContract.MaximumRetainedRuns, await CountRunsAsync().ConfigureAwait(false));
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationContract.MaximumProjectedRuns, state.Runs.Count);
    }

    [TestMethod]
    public async Task GetStateAsync_ProjectsTheCaptureRelativeNextSequenceAndObservedSequence()
    {
        _captureSequence.Sequence = 120;
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(
            SaveRequest(trigger: LocalAutomationTriggerKind.CaptureRelative, interval: 10),
            CancellationToken.None).ConfigureAwait(false);

        var beforeBaseline = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        await store.SetCaptureBaselineAsync("sky-temperature", 120, CancellationToken.None).ConfigureAwait(false);
        var afterBaseline = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(120L, beforeBaseline.ObservedCaptureSequence);
        Assert.IsNull(beforeBaseline.Definitions.Single().NextRunCaptureSequence);
        Assert.IsNull(beforeBaseline.Definitions.Single().NextRunUtc);
        Assert.IsEmpty(beforeBaseline.Calendar);
        Assert.AreEqual(130L, afterBaseline.Definitions.Single().NextRunCaptureSequence);
    }

    [TestMethod]
    public async Task GetStateAsync_ReportsTheObservedSequenceAsUnknownWhenTheReaderFails()
    {
        _captureSequence.Throw = new IOException("unavailable");
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(state.ObservedCaptureSequence);
        Assert.IsEmpty(state.Definitions);
    }

    [TestMethod]
    public async Task SaveAsync_RebaselinesProgressWhenTheTriggerKindChanges()
    {
        _captureSequence.Sequence = 50;
        using var store = await CreateInitializedStoreAsync().ConfigureAwait(false);
        await store.SaveAsync(SaveRequest(), CancellationToken.None).ConfigureAwait(false);
        var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        await store.TryBeginRunAsync(entry, "run-1", Now.AddSeconds(3600), null, CancellationToken.None)
            .ConfigureAwait(false);

        var changed = await store.SaveAsync(
            SaveRequest(
                trigger: LocalAutomationTriggerKind.CaptureRelative,
                interval: 10,
                expectedVersion: 1,
                key: "retrigger"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationCommandStatus.Applied, changed.Status);
        var runner = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        Assert.IsNull(runner.LastOccurrenceUtc);
        Assert.IsNull(runner.LastCaptureSequence);
        // The recorded run survives the redefinition; only the progress dimension is rebaselined.
        Assert.AreEqual(1, changed.State.Runs.Count);
    }

    private Task<long> CountRunsAsync() => CountAsync("automation_runs");

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The table name comes from this test class, never from input.")]
    private async Task<long> CountAsync(string table)
    {
        var path = Path.Combine(_root, ".automation", "local-automations.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static LocalAutomationSaveRequest SaveRequest(
        string definitionId = "sky-temperature",
        string name = "Sky temperature",
        bool enabled = true,
        string target = "virtual-sky-temperature",
        LocalAutomationTriggerKind trigger = LocalAutomationTriggerKind.Periodic,
        int interval = 3600,
        long expectedVersion = 0,
        string key = "command-1")
        => new(
            definitionId,
            name,
            enabled,
            LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
            target,
            trigger,
            interval,
            expectedVersion,
            key,
            "owner",
            null);

    private async Task<SqliteLocalAutomationStore> CreateInitializedStoreAsync()
    {
        var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        return store;
    }

    private SqliteLocalAutomationStore CreateStore(string? root = null)
        => new(
            _registry,
            _captureSequence,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root ?? _root }),
            _timeProvider,
            NullLogger<SqliteLocalAutomationStore>.Instance);
}
