using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcAttemptContractTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swpc-contract-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private SqliteSwpcStore Open(ILogger<SqliteSwpcStore>? logger = null)
        => new(SwpcStoreTests.Settings(_root), logger ?? NullLogger<SqliteSwpcStore>.Instance);
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static SwpcCommitEffects Preserve(SwpcCommitContext context)
        => new(context.Before.Products[SwpcProduct.Alerts].Checkpoint, null, null, []);

    private static async Task<(SwpcAttemptStart Start, SwpcWriteResult Result)> Begin(
        SqliteSwpcStore store, DateTimeOffset? at = null, SwpcFetchCheckpoint? checkpoint = null)
    {
        var time = at ?? Now;
        var view = await store.ReadAsync(time, CancellationToken.None).ConfigureAwait(false);
        var request = new SwpcAttemptStart(Guid.NewGuid(), view.Revision, SwpcProduct.Alerts, "contract-test",
            SwpcAttemptTrigger.Scheduled, time, view.Products[SwpcProduct.Alerts].Validators,
            checkpoint ?? view.Products[SwpcProduct.Alerts].Checkpoint);
        return (request, await store.BeginAttemptAsync(request, CancellationToken.None).ConfigureAwait(false));
    }
    private static SwpcAttemptCompletion Completion((SwpcAttemptStart Start, SwpcWriteResult Result) begun, SwpcSnapshot? snapshot = null)
        => new(begun.Start.AttemptId, begun.Result.View.Revision, begun.Start.StartedUtc.AddSeconds(1),
            begun.Start.StartedUtc.AddSeconds(1), snapshot?.RetrievedUtc, snapshot is null ? null : SwpcFetchOutcome.Updated,
            snapshot is null ? null : 200, snapshot is null ? "interrupted" : "updated",
            snapshot?.DecodedByteCount ?? 0, snapshot is null ? SwpcValidatorOutcome.Preserved : SwpcValidatorOutcome.Replaced, snapshot);

    [TestMethod]
    [DataRow(true, "io")]
    [DataRow(false, "io")]
    [DataRow(true, "sqlite")]
    [DataRow(false, "sqlite")]
    [DataRow(true, "denied")]
    [DataRow(false, "denied")]
    [DataRow(true, "projection")]
    [DataRow(false, "projection")]
    public async Task ProjectionFaultPropagatesOriginalExceptionAndRollsBackEveryEffect(bool candidate, string kind)
    {
        var logger = new CaptureLogger();
        using var store = Open(logger);
        var begun = await Begin(store).ConfigureAwait(false);
        var request = Completion(begun, candidate ? SwpcStoreTests.Alerts(SwpcStoreTests.Message()) : null);
        const string marker = "projection-text-must-never-be-logged";
        Exception original = kind switch
        {
            "io" => new IOException(marker),
            "sqlite" => new SqliteException(marker, 1),
            "denied" => new UnauthorizedAccessException(marker),
            _ => new InvalidOperationException(marker)
        };
        var caught = await Assert.ThrowsAsync<Exception>(() =>
            store.CompleteAttemptAsync(request, _ => throw original, CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.AreSame(original, caught);
        var health = await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(begun.Result.View.Revision, health.Revision);
        Assert.AreEqual(1, health.IncompleteAttempts);
        Assert.AreEqual(0L, health.RawBodyBytes);
        Assert.AreEqual(0L, health.MessageVersionCount);
        Assert.AreEqual(0L, health.SnapshotCount);
        Assert.IsFalse(logger.Entries.Any(e => e.Text.Contains(marker, StringComparison.Ordinal)));
        Assert.IsTrue(logger.Entries.Any(e => e.Id is >= 7810 and <= 7824 && e.Text.Contains(original.GetType().Name, StringComparison.Ordinal)));
        Assert.IsTrue(logger.Entries.All(e => e.Exception is null));
        Assert.AreEqual(SwpcWriteStatus.Applied, (await store.CompleteAttemptAsync(request, Preserve, CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task CandidateLessCompletionProjectsTheUnchangedViewAndPersistsCallerEffects()
    {
        using var store = Open();
        var begun = await Begin(store).ConfigureAwait(false);
        var checkpoint = new SwpcFetchCheckpoint(Now.AddHours(1), Now.AddMinutes(20), true, 3, 2, 1, Now);
        var calls = 0;
        var request = Completion(begun) with { Outcome = SwpcFetchOutcome.TransientFailure, ReasonCode = "transport-failure" };
        var result = await store.CompleteAttemptAsync(request, context =>
        {
            calls++;
            Assert.AreEqual(context.Before, context.AfterMerge);
            Assert.IsEmpty(context.Touched);
            return new(checkpoint, new(null, new("test-halt", Now)), null, [new("provider", "format-drift", null)]);
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(checkpoint, result.View.Products[SwpcProduct.Alerts].Checkpoint);
        Assert.AreEqual("test-halt", result.View.ProviderHalt!.ReasonCode);
        Assert.HasCount(1, result.Attention);
    }

    [TestMethod]
    [DataRow("interrupted")]
    [DataRow("storage-failure")]
    public async Task ExplicitSettlementPreservesEveryDurablePolicyField(string reason)
    {
        var checkpoint = new SwpcFetchCheckpoint(Now.AddMinutes(2), Now.AddHours(1), true, 2, 3, 4, Now.AddDays(-1));
        SwpcAttemptCompletion completion;
        string before;
        using (var store = Open())
        {
            var first = await Begin(store).ConfigureAwait(false);
            var snapshot = SwpcStoreTests.Alerts(SwpcStoreTests.Message());
            var saved = await store.CompleteAttemptAsync(Completion(first, snapshot), Preserve, CancellationToken.None).ConfigureAwait(false);
            await store.ChangeHaltAsync(new(Guid.NewGuid(), saved.View.Revision, Now,
                new(null, new("test-halt", Now)), ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty), CancellationToken.None).ConfigureAwait(false);
            var begun = await Begin(store, checkpoint: checkpoint).ConfigureAwait(false);
            before = JsonSerializer.Serialize(begun.Result.View.Products[SwpcProduct.Alerts]);
            completion = Completion(begun) with { ReasonCode = reason };
        }
        using var reopened = Open();
        var calls = 0;
        var settled = await reopened.CompleteAttemptAsync(completion, context =>
        {
            calls++;
            Assert.AreEqual(context.Before, context.AfterMerge);
            Assert.IsEmpty(context.Touched);
            return Preserve(context);
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(before, JsonSerializer.Serialize(settled.View.Products[SwpcProduct.Alerts]));
        Assert.AreEqual("test-halt", settled.View.ProviderHalt!.ReasonCode);
        Assert.AreEqual(0, (await reopened.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false)).IncompleteAttempts);
        var replay = await reopened.CompleteAttemptAsync(completion, _ => throw new InvalidOperationException(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Replayed, replay.Status);
        Assert.IsEmpty(replay.Attention);
    }

    [TestMethod]
    public async Task CompletedJournalExpiresFromStartButIncompleteJournalNeverExpires()
    {
        using var store = Open();
        var old = await Begin(store, Now.AddDays(-35)).ConfigureAwait(false);
        var completed = Completion(old);
        await store.CompleteAttemptAsync(completed, Preserve, CancellationToken.None).ConfigureAwait(false);
        var incomplete = await Begin(store, Now.AddDays(-90)).ConfigureAwait(false);
        var health = await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, health.AttemptCount);
        Assert.AreEqual(1, health.IncompleteAttempts);
        Assert.AreEqual(incomplete.Start.StartedUtc, health.OldestRetainedAttemptStartedUtc);
        var missing = await store.CompleteAttemptAsync(completed, _ => throw new InvalidOperationException(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.NotFound, missing.Status);
    }

    [TestMethod]
    public async Task JournalCapPrunesOldestCompletedRowsAndKeepsIncompleteIdentity()
    {
        using var store = Open();
        var incomplete = await Begin(store, Now.AddDays(-2)).ConfigureAwait(false);
        await store.ExecuteAsync(null, (db, tx) =>
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO swpc_attempts(id,product,started,start,start_fingerprint,completion,completion_fingerprint,added,diagnostics) VALUES($id,0,$t,$s,$f,$c,$f,0,'[]');";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var time = command.Parameters.Add("$t", SqliteType.Integer);
            var start = command.Parameters.Add("$s", SqliteType.Text);
            var complete = command.Parameters.Add("$c", SqliteType.Text);
            command.Parameters.AddWithValue("$f", new string('A', 64));
            for (var index = 0; index < 50003; index++)
            {
                var stamp = Now.AddDays(-1).AddTicks(index);
                var request = incomplete.Start with { AttemptId = Guid.NewGuid(), StartedUtc = stamp };
                id.Value = request.AttemptId.ToString("N"); time.Value = stamp.UtcTicks;
                start.Value = JsonSerializer.Serialize(request);
                complete.Value = JsonSerializer.Serialize(Completion((request, incomplete.Result)));
                command.ExecuteNonQuery();
            }
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        var revision = incomplete.Result.View.Revision;
        var health = await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(50001L, health.AttemptCount);
        Assert.AreEqual(1, health.IncompleteAttempts);
        Assert.IsTrue(health.Revision > revision);
        var page = await store.ReadDiagnosticsAsync(new(Now, null, null, 200), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(200, page.Attempts);
        Assert.IsNotNull(page.NextBeforeSequence);
        using var database = Database(); using var check = database.CreateCommand();
        check.CommandText = "SELECT min(started) FROM swpc_attempts WHERE completion IS NOT NULL;";
        Assert.AreEqual(Now.AddDays(-1).AddTicks(3).UtcTicks, await check.ExecuteScalarAsync().ConfigureAwait(false));
        Assert.AreEqual(SwpcWriteStatus.Replayed, (await store.BeginAttemptAsync(incomplete.Start, CancellationToken.None).ConfigureAwait(false)).Status);
        Assert.AreEqual(50000L, health.ReasonCounts["interrupted"]);
        var completed = Completion(incomplete) with { ExpectedRevision = health.Revision, AtUtc = Now, EndedUtc = Now };
        Assert.AreEqual(SwpcWriteStatus.Applied, (await store.CompleteAttemptAsync(completed, Preserve, CancellationToken.None).ConfigureAwait(false)).Status);
        var after = await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(50000L, after.AttemptCount);
        Assert.AreEqual(0, after.IncompleteAttempts);
        Assert.AreEqual(SwpcWriteStatus.NotFound, (await store.CompleteAttemptAsync(completed,
            _ => throw new InvalidOperationException(), CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task CommandIdentitiesHaveThirtyFiveDayLifetimeAndTenThousandCap()
    {
        using var store = Open();
        var view = await store.ReadAsync(Now.AddDays(-35), CancellationToken.None).ConfigureAwait(false);
        var command = new SwpcHaltCommand(Guid.NewGuid(), view.Revision, Now.AddDays(-35),
            new(null, new("test-halt", Now.AddDays(-35))), ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty);
        await store.ChangeHaltAsync(command, CancellationToken.None).ConfigureAwait(false);
        await store.ExecuteAsync(null, (db, tx) =>
        {
            using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO swpc_receipts(kind,id,created,fingerprint,added,diagnostics) VALUES('ack',$id,$t,$f,0,'[]');";
            var id = insert.Parameters.Add("$id", SqliteType.Text); var time = insert.Parameters.Add("$t", SqliteType.Integer);
            insert.Parameters.AddWithValue("$f", new string('A', 64));
            for (var index = 0; index < 10003; index++)
            { id.Value = Guid.NewGuid().ToString("N"); time.Value = Now.AddDays(-1).AddTicks(index).UtcTicks; insert.ExecuteNonQuery(); }
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        var purged = await store.ApplyRetentionAsync(Now, CancellationToken.None).ConfigureAwait(false);
        using var database = Database(); using var check = database.CreateCommand();
        check.CommandText = "SELECT count(*) FROM swpc_receipts;";
        Assert.AreEqual(10000L, await check.ExecuteScalarAsync().ConfigureAwait(false));
        check.CommandText = "SELECT min(created) FROM swpc_receipts;";
        Assert.AreEqual(Now.AddDays(-1).AddTicks(3).UtcTicks, await check.ExecuteScalarAsync().ConfigureAwait(false));
        var added = await store.ChangeHaltAsync(new(Guid.NewGuid(), purged.Revision, Now, new(null, null),
            ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, added.Status);
        check.CommandText = "SELECT count(*) FROM swpc_receipts;";
        Assert.AreEqual(10000L, await check.ExecuteScalarAsync().ConfigureAwait(false), "The command commit enforces the hard cap.");
        check.CommandText = "SELECT min(created) FROM swpc_receipts;";
        Assert.AreEqual(Now.AddDays(-1).AddTicks(4).UtcTicks, await check.ExecuteScalarAsync().ConfigureAwait(false));
        Assert.AreEqual(SwpcWriteStatus.Conflict, (await store.ChangeHaltAsync(command, CancellationToken.None).ConfigureAwait(false)).Status);
    }

    private SqliteConnection Database()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "space-weather.db"), Pooling = false }.ToString());
        db.Open(); return db;
    }
    private sealed class CaptureLogger : ILogger<SqliteSwpcStore>
    {
        public List<(int Id, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((eventId.Id, formatter(state, exception), exception));
    }
}
