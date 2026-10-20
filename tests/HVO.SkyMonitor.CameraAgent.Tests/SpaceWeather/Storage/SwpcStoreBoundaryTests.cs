using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcStoreBoundaryTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swpc-boundary-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private SqliteSwpcStore Open() => new(SwpcStoreTests.Settings(_root), NullLogger<SqliteSwpcStore>.Instance);
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static SwpcCommitEffects Preserve(SwpcCommitContext context) => new(context.Before.Products[SwpcProduct.Alerts].Checkpoint, null, null, []);
    private static async Task<(SwpcAttemptStart Start, SwpcWriteResult Result)> Begin(SqliteSwpcStore store, DateTimeOffset? at = null, SwpcValidators? sent = null)
    {
        var time = at ?? Now;
        var view = await store.ReadAsync(time, CancellationToken.None).ConfigureAwait(false);
        var request = new SwpcAttemptStart(Guid.NewGuid(), view.Revision, SwpcProduct.Alerts, "boundary", SwpcAttemptTrigger.Manual,
            time, sent ?? view.Products[SwpcProduct.Alerts].Validators, view.Products[SwpcProduct.Alerts].Checkpoint);
        return (request, await store.BeginAttemptAsync(request, CancellationToken.None).ConfigureAwait(false));
    }
    private static SwpcAttemptCompletion Completion((SwpcAttemptStart Start, SwpcWriteResult Result) begun, SwpcSnapshot? snapshot = null)
        => new(begun.Start.AttemptId, begun.Result.View.Revision, begun.Start.StartedUtc.AddSeconds(1), begun.Start.StartedUtc.AddSeconds(1),
            snapshot?.RetrievedUtc, snapshot is null ? null : SwpcFetchOutcome.Updated, snapshot is null ? null : 200,
            snapshot is null ? "interrupted" : "updated", snapshot?.DecodedByteCount ?? 0,
            snapshot is null ? SwpcValidatorOutcome.Preserved : SwpcValidatorOutcome.Replaced, snapshot);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OutcomeDisagreementReturnsConflictWithoutInvokingProjection(bool alreadyStored)
    {
        using var store = Open();
        var snapshot = SwpcStoreTests.Alerts(SwpcStoreTests.Message());
        if (alreadyStored)
        {
            var seed = await Begin(store).ConfigureAwait(false);
            await store.CompleteAttemptAsync(Completion(seed, snapshot), Preserve, CancellationToken.None).ConfigureAwait(false);
        }
        var begun = await Begin(store).ConfigureAwait(false);
        var result = await store.CompleteAttemptAsync(Completion(begun, snapshot) with
        { Outcome = alreadyStored ? SwpcFetchOutcome.Updated : SwpcFetchOutcome.Unchanged },
            _ => throw new InvalidOperationException("A conflict must not project."), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Conflict, result.Status);
        Assert.AreEqual(begun.Result.View.Revision, result.View.Revision);
        Assert.AreEqual(alreadyStored ? 1 : 0, result.View.Notices.Versions.Length);
        Assert.AreEqual(1, (await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false)).IncompleteAttempts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NotModifiedRequiresAnActuallySentRecordedValidator(bool unrelatedValidator)
    {
        using var store = Open();
        var seed = await Begin(store).ConfigureAwait(false);
        await store.CompleteAttemptAsync(Completion(seed, SwpcStoreTests.Alerts(SwpcStoreTests.Message())), Preserve, CancellationToken.None).ConfigureAwait(false);
        var begun = await Begin(store, sent: new(unrelatedValidator ? "\"unrelated\"" : null, null)).ConfigureAwait(false);
        var request = Completion(begun) with { Outcome = SwpcFetchOutcome.NotModified, HttpStatus = 304, RetrievedUtc = Now, ReasonCode = "not-modified" };
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAttemptAsync(request, Preserve, CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false)).IncompleteAttempts);
    }

    [TestMethod]
    [DataRow("reason")]
    [DataRow("bytes")]
    [DataRow("validator")]
    [DataRow("http")]
    [DataRow("retrieved")]
    [DataRow("candidate")]
    public async Task InvalidSettlementCannotChangeTheAttemptOrPolicy(string field)
    {
        using var store = Open();
        var begun = await Begin(store).ConfigureAwait(false);
        var request = Completion(begun);
        request = field switch
        {
            "reason" => request with { ReasonCode = "unknown-outcome" },
            "bytes" => request with { BytesRead = 1 },
            "validator" => request with { ValidatorOutcome = SwpcValidatorOutcome.NotSent },
            "http" => request with { HttpStatus = 503 },
            "retrieved" => request with { RetrievedUtc = Now },
            _ => request with { Candidate = SwpcStoreTests.Alerts(SwpcStoreTests.Message()) }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAttemptAsync(request, Preserve, CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(begun.Result.View.Revision, (await store.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false)).Revision);
    }

    [TestMethod]
    [DataRow("checkpoint")]
    [DataRow("halt")]
    [DataRow("attention")]
    public async Task SettlementRejectsProjectionPolicyChanges(string field)
    {
        using var store = Open();
        var begun = await Begin(store).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAttemptAsync(Completion(begun), context =>
        {
            var effects = Preserve(context);
            return field switch
            {
                "checkpoint" => effects with { ProductCheckpoint = effects.ProductCheckpoint with { ConsecutiveRejected = 1 } },
                "halt" => effects with { HaltChange = new(null, new("unknown", Now)) },
                _ => effects with { ProviderAttention = [new("provider", "unknown", null)] }
            };
        }, CancellationToken.None).AsTask()).ConfigureAwait(false);
        var health = await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(begun.Result.View.Revision, health.Revision);
        Assert.AreEqual(1, health.IncompleteAttempts);
        Assert.IsNull(health.ProviderHalt);
    }

    [TestMethod]
    public async Task ProjectionFailureRollsBackThePurgeAsWellAsTheMerge()
    {
        using var store = Open();
        var old = Now.AddDays(-36);
        var initial = await store.ReadAsync(old, CancellationToken.None).ConfigureAwait(false);
        await store.ChangeHaltAsync(new(Guid.NewGuid(), initial.Revision, old, new(null, null),
            ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty), CancellationToken.None).ConfigureAwait(false);
        var begun = await Begin(store, old).ConfigureAwait(false);
        // Account for the mandatory purge revision so projection is reached in this transaction.
        var request = Completion(begun) with { ExpectedRevision = begun.Result.View.Revision + 1, AtUtc = Now, EndedUtc = Now };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.CompleteAttemptAsync(request,
            _ => throw new InvalidOperationException("rollback"), CancellationToken.None).AsTask()).ConfigureAwait(false);
        using var database = Database(); using var check = database.CreateCommand();
        check.CommandText = "SELECT count(*) FROM swpc_receipts;";
        Assert.AreEqual(1L, await check.ExecuteScalarAsync().ConfigureAwait(false), "The expired identity must survive a rolled-back purge.");
        check.CommandText = "SELECT revision FROM swpc_meta;";
        Assert.AreEqual(begun.Result.View.Revision, await check.ExecuteScalarAsync().ConfigureAwait(false));
        check.CommandText = "SELECT count(*) FROM swpc_attempts WHERE completion IS NULL;";
        Assert.AreEqual(1L, await check.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InvalidProjectedCheckpointRollsBackAcceptedBytesAndMessages()
    {
        using var store = Open();
        var begun = await Begin(store).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAttemptAsync(Completion(begun, SwpcStoreTests.Alerts(SwpcStoreTests.Message())),
            _ => new(SwpcStoreTests.Empty with { ConsecutiveRejected = -1 }, null, null, []), CancellationToken.None).AsTask()).ConfigureAwait(false);
        var health = await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, health.MessageVersionCount); Assert.AreEqual(0L, health.RawBodyBytes); Assert.AreEqual(1, health.IncompleteAttempts);
    }

    [TestMethod]
    public async Task StaleFixtureUsesSharedClassifierAndIgnoresStoredSourceRevisions()
    {
        using var scenario = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "space-weather", "swpc-v1", "scenarios", "s11-stale-cdn-out-of-order.json")).ConfigureAwait(false));
        var steps = scenario.RootElement.GetProperty("steps");
        using var store = Open();
        SwpcWriteResult? latest = null;
        SwpcSnapshot? initial = null;
        foreach (var step in steps.EnumerateArray().Take(2))
        {
            var at = step.GetProperty("atUtc").GetDateTimeOffset();
            var snapshot = SwpcStoreTests.Alerts(step.GetProperty("response").GetProperty("body").GetString()!, at);
            var begun = await Begin(store, at).ConfigureAwait(false);
            var outcome = SwpcSnapshotComparison.Classify(initial, snapshot);
            latest = await store.CompleteAttemptAsync(Completion(begun, snapshot) with { Outcome = outcome }, Preserve, CancellationToken.None).ConfigureAwait(false);
            initial ??= snapshot;
        }
        Assert.IsNotNull(latest); Assert.IsNotNull(initial);
        Assert.AreEqual(initial.Sha256, latest.View.Products[SwpcProduct.Alerts].LastKnownGood!.Sha256);
        Assert.AreEqual(initial.ResponseValidators, latest.View.Products[SwpcProduct.Alerts].Validators);
        Assert.AreEqual(initial.RetrievedUtc, latest.View.Products[SwpcProduct.Alerts].LastSuccessUtc);
        Assert.HasCount(2, latest.Diagnostics);
        Assert.AreEqual("stale-revision-ignored:ALTTP4/719/2026-10-08T16:44:00Z", latest.Diagnostics[0]);
        Assert.AreEqual("stale-snapshot", latest.Diagnostics[1]);
        Assert.AreEqual(0, latest.AddedVersions);
        Assert.AreEqual(3, latest.View.Notices.Versions.Length);
    }

    [TestMethod]
    public async Task SemanticReplaysIgnoreRevisionAndJournalNeverDuplicatesTheCandidateBody()
    {
        using var store = Open();
        var begun = await Begin(store).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Conflict, (await store.BeginAttemptAsync(begun.Start with { RunKey = "changed" }, CancellationToken.None).ConfigureAwait(false)).Status);
        var request = Completion(begun, SwpcStoreTests.Alerts(SwpcStoreTests.Message(detail: "journal-body-marker")));
        var applied = await store.CompleteAttemptAsync(request, Preserve, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Replayed, (await store.CompleteAttemptAsync(request with { ExpectedRevision = applied.View.Revision },
            _ => throw new InvalidOperationException(), CancellationToken.None).ConfigureAwait(false)).Status);
        using var database = Database(); using var check = database.CreateCommand();
        check.CommandText = "SELECT completion FROM swpc_attempts;";
        var journal = (string)(await check.ExecuteScalarAsync().ConfigureAwait(false))!;
        Assert.IsFalse(journal.Contains("journal-body-marker", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(journal);
        Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("Candidate").ValueKind);
    }

    [TestMethod]
    public async Task FirstSeenExpiredRecordSurvivesItsCommitAndSequencesNeverResetAfterPurge()
    {
        using (var store = Open())
        {
            var begun = await Begin(store).ConfigureAwait(false);
            var oldBody = SwpcStoreTests.Message().Replace("2026-10-20", "2026-09-01", StringComparison.Ordinal)
                .Replace("2026 Oct 20", "2026 Sep 01", StringComparison.Ordinal);
            var saved = await store.CompleteAttemptAsync(Completion(begun, SwpcStoreTests.Alerts(oldBody)), Preserve, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, saved.View.Notices.Versions, "There is no second notice purge after merging.");
            Assert.IsEmpty((await store.ReadAsync(Now.AddSeconds(2), CancellationToken.None).ConfigureAwait(false)).Notices.Versions);
        }
        using var reopened = Open();
        var next = await Begin(reopened, Now.AddSeconds(3)).ConfigureAwait(false);
        var result = await reopened.CompleteAttemptAsync(Completion(next, SwpcStoreTests.Alerts(SwpcStoreTests.Message(2), Now.AddSeconds(3))),
            Preserve, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, result.View.Notices.Versions);
        Assert.AreEqual(2L, result.View.Notices.Versions[0].Version.IngestSequence);
    }

    private SqliteConnection Database()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "space-weather.db"), Pooling = false }.ToString());
        db.Open(); return db;
    }
}
