using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcAttemptTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swpc-attempt-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private SqliteSwpcStore Open() => new(SwpcStoreTests.Settings(_root), NullLogger<SqliteSwpcStore>.Instance);
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static SwpcCommitEffects Project(SwpcCommitContext context) => new(SwpcStoreTests.Empty, null,
        SwpcAttentionEvaluator.Evaluate(new(context.AfterMerge.Notices.Groups, context.Touched, context.Before.Notices.RaisedRanks, context.Attempt.AtUtc)), []);
    private static async Task<(SwpcAttemptStart Request, SwpcWriteResult Result)> Begin(SqliteSwpcStore store, SwpcValidators? validators = null)
    {
        var state = await store.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false);
        var request = new SwpcAttemptStart(Guid.NewGuid(), state.Revision, SwpcProduct.Alerts, "fixture", SwpcAttemptTrigger.Scheduled, Now,
            validators ?? new(null, null), SwpcStoreTests.Empty);
        return (request, await store.BeginAttemptAsync(request, CancellationToken.None).ConfigureAwait(false));
    }
    private static SwpcAttemptCompletion Completion(SwpcAttemptStart start, SwpcWriteResult begin, SwpcSnapshot? snapshot = null,
        SwpcFetchOutcome? outcome = SwpcFetchOutcome.Updated) => new(start.AttemptId, begin.View.Revision, Now, Now.AddSeconds(1), Now,
            outcome, snapshot is null ? null : 200, outcome is null ? "interrupted" : "updated", snapshot?.DecodedByteCount ?? 0, outcome is null ? SwpcValidatorOutcome.Preserved : SwpcValidatorOutcome.NotSent, snapshot);

    [TestMethod]
    public async Task BeginIsDurableBeforeIoAndReopenDoesNotInventAHaltOrOutcome()
    {
        SwpcAttemptStart request;
        using (var store = Open())
        {
            var begun = await Begin(store).ConfigureAwait(false); request = begun.Request;
            Assert.AreEqual(SwpcWriteStatus.Applied, begun.Result.Status);
            Assert.AreEqual(SwpcWriteStatus.Replayed, (await store.BeginAttemptAsync(request, CancellationToken.None).ConfigureAwait(false)).Status);
            var journal = await store.ReadDiagnosticsAsync(new(Now, null, null, 10), CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, journal.Attempts); Assert.IsNull(journal.Attempts[0].EndedUtc);
            Assert.AreEqual(Now, begun.Result.View.Products[SwpcProduct.Alerts].LastAttemptUtc);
        }
        using var reopened = Open(); var view = await reopened.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(view.ProviderHalt); Assert.IsNull(view.Products[SwpcProduct.Alerts].LastOutcome);
        Assert.AreEqual(1, (await reopened.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false)).IncompleteAttempts);
        var settled = await reopened.CompleteAttemptAsync(new(request.AttemptId, view.Revision, Now, Now.AddSeconds(2), null,
            null, null, "interrupted", 0, SwpcValidatorOutcome.Preserved, null), Project, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, settled.Status); Assert.IsNull(settled.View.Products[SwpcProduct.Alerts].LastOutcome);
        Assert.AreEqual(0, (await reopened.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false)).IncompleteAttempts);
    }

    [TestMethod]
    public async Task SnapshotJournalCheckpointAndRaisedRanksCommitTogetherAndReplayIsSilent()
    {
        using var store = Open(); var begun = await Begin(store).ConfigureAwait(false);
        var snapshot = SwpcStoreTests.Alerts(SwpcStoreTests.Message());
        var request = Completion(begun.Request, begun.Result, snapshot);
        var completed = await store.CompleteAttemptAsync(request, Project, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, completed.Status); Assert.AreEqual(1, completed.AddedVersions);
        Assert.HasCount(1, completed.Attention); Assert.HasCount(1, completed.View.Notices.RaisedRanks);
        var replay = await store.CompleteAttemptAsync(request, _ => throw new InvalidOperationException("must not repeat"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Replayed, replay.Status); Assert.IsEmpty(replay.Attention);
        Assert.HasCount(1, replay.View.Notices.Versions);
        var journal = await store.ReadDiagnosticsAsync(new(Now, SwpcProduct.Alerts, null, 1), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, journal.Attempts); Assert.AreEqual(snapshot.DecodedByteCount, journal.Attempts[0].BytesRead);
        Assert.AreEqual(Now.AddSeconds(1), journal.Attempts[0].EndedUtc);
        Assert.IsFalse(JsonSerializer.Serialize(journal).Contains("Synthetic", StringComparison.Ordinal));
        var conflicting = await store.CompleteAttemptAsync(request with { BytesRead = 123 }, Project, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Conflict, conflicting.Status);
    }

    [TestMethod]
    public async Task ProjectionFailureRollsBackVersionsRawSnapshotRanksCheckpointAndJournalCompletion()
    {
        using var store = Open(); var begun = await Begin(store).ConfigureAwait(false);
        var request = Completion(begun.Request, begun.Result, SwpcStoreTests.Alerts(SwpcStoreTests.Message()));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.CompleteAttemptAsync(request, _ => throw new InvalidOperationException("injected mid-commit failure"), CancellationToken.None).AsTask()).ConfigureAwait(false);
        var view = await store.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(view.Notices.Versions); Assert.IsEmpty(view.Notices.RaisedRanks); Assert.IsNull(view.Products[SwpcProduct.Alerts].LastKnownGood);
        var health = await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, health.RawBodyBytes); Assert.AreEqual(0L, health.SnapshotCount); Assert.AreEqual(1, health.IncompleteAttempts);
        Assert.AreEqual(SwpcWriteStatus.Applied, (await store.CompleteAttemptAsync(request, Project, CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task CancellationAfterProjectionRollsBackTheEntireCommit()
    {
        using var store = Open(); var begun = await Begin(store).ConfigureAwait(false); using var cancelled = new CancellationTokenSource();
        var request = Completion(begun.Request, begun.Result, SwpcStoreTests.Alerts(SwpcStoreTests.Message()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.CompleteAttemptAsync(request, context =>
        { var effects = Project(context); cancelled.Cancel(); return effects; }, cancelled.Token).AsTask()).ConfigureAwait(false);
        Assert.IsEmpty((await store.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false)).Notices.Versions);
        Assert.AreEqual(1, (await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false)).IncompleteAttempts);
    }

    [TestMethod]
    public async Task NonSnapshotOutcomeAndHaltArePurePersistenceAndResumeKeepsCallerCounters()
    {
        using var store = Open(); var begun = await Begin(store).ConfigureAwait(false);
        var checkpoint = new SwpcFetchCheckpoint(null, Now.AddHours(1), true, 2, 3, 1, Now);
        var result = await store.CompleteAttemptAsync(Completion(begun.Request, begun.Result, outcome: SwpcFetchOutcome.Halted) with
        { ReasonCode = "rate-limited", HttpStatus = 429, RetrievedUtc = null }, _ => new(checkpoint, new(null, new("rate-limited", Now)), null, []), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result.View.ProviderHalt); Assert.AreEqual(checkpoint, result.View.Products[SwpcProduct.Alerts].Checkpoint);
        var resume = await store.ChangeHaltAsync(new(Guid.NewGuid(), result.View.Revision, Now, new(null, null),
            ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(checkpoint, resume.View.Products[SwpcProduct.Alerts].Checkpoint);
        Assert.IsNull(resume.View.ProviderHalt); Assert.IsNull(resume.View.Products[SwpcProduct.Alerts].LastKnownGood);
    }

    [TestMethod]
    public async Task UnchangedAnd304AdvanceRetrievalButKeepSnapshotAndOriginalVersion()
    {
        using var store = Open(); var first = await Begin(store).ConfigureAwait(false);
        var snapshot = SwpcStoreTests.Alerts(SwpcStoreTests.Message());
        await store.CompleteAttemptAsync(Completion(first.Request, first.Result, snapshot), Project, CancellationToken.None).ConfigureAwait(false);
        var next = await Begin(store, snapshot.ResponseValidators).ConfigureAwait(false);
        var changed = snapshot with { ResponseValidators = new("\"churn\"", null), RetrievedUtc = Now.AddSeconds(1) };
        var unchanged = await store.CompleteAttemptAsync(Completion(next.Request, next.Result, changed, SwpcFetchOutcome.Unchanged) with
        { RetrievedUtc = changed.RetrievedUtc, ValidatorOutcome = SwpcValidatorOutcome.Replaced }, Project, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("\"churn\"", unchanged.View.Products[SwpcProduct.Alerts].Validators.ETag);
        Assert.HasCount(1, unchanged.View.Notices.Versions); Assert.IsEmpty(unchanged.Diagnostics); Assert.IsEmpty(unchanged.Attention);
        var third = await Begin(store, unchanged.View.Products[SwpcProduct.Alerts].Validators).ConfigureAwait(false);
        var notModified = await store.CompleteAttemptAsync(Completion(third.Request, third.Result, outcome: SwpcFetchOutcome.NotModified) with
        { HttpStatus = 304, ReasonCode = "not-modified", RetrievedUtc = Now.AddSeconds(2), EndedUtc = Now.AddSeconds(2), ValidatorOutcome = SwpcValidatorOutcome.NotModified }, Project, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(Now.AddSeconds(2), notModified.View.Products[SwpcProduct.Alerts].LastSuccessUtc);
        Assert.AreEqual("\"churn\"", notModified.View.Products[SwpcProduct.Alerts].Validators.ETag);
        Assert.AreEqual(snapshot.Sha256, notModified.View.Products[SwpcProduct.Alerts].LastKnownGood!.Sha256);
    }

    [TestMethod]
    public async Task ConcurrentStartsHaveOnlyOneDurableWinner()
    {
        using var store = Open(); var state = await store.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false);
        var requests = Enumerable.Range(0, 2).Select(_ => store.BeginAttemptAsync(new(Guid.NewGuid(), state.Revision, SwpcProduct.Alerts,
            "concurrent", SwpcAttemptTrigger.Manual, Now, new(null, null), SwpcStoreTests.Empty), CancellationToken.None).AsTask()).ToArray();
        var results = await Task.WhenAll(requests).ConfigureAwait(false);
        Assert.AreEqual(1, results.Count(r => r.Status == SwpcWriteStatus.Applied));
        Assert.AreEqual(1, results.Count(r => r.Status == SwpcWriteStatus.Conflict));
        Assert.AreEqual(1L, (await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false)).AttemptCount);
    }

    [TestMethod]
    public async Task InvalidCandidateAndUnsafeReasonLeaveNoPartialState()
    {
        using var store = Open(); var begun = await Begin(store).ConfigureAwait(false); var snapshot = SwpcStoreTests.Alerts(SwpcStoreTests.Message());
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAttemptAsync(Completion(begun.Request, begun.Result, snapshot with { Sha256 = new string('0', 64) }), Project, CancellationToken.None).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAttemptAsync(Completion(begun.Request, begun.Result, outcome: SwpcFetchOutcome.TransientFailure) with { ReasonCode = "https://secret.invalid/token" }, Project, CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(1, (await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false)).IncompleteAttempts);
        Assert.IsEmpty((await store.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false)).Notices.Versions);
    }
}
