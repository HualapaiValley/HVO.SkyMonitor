using System.Collections.Immutable;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcRunnerTests
{
    [TestMethod]
    public async Task AdmissionAndCompletionUseDistinctTimeAnchorsAndSampleOnce()
    {
        using var h = new Harness();
        var t = h.Clock.GetUtcNow();
        h.Client.Fetch = (product, _, _) =>
        {
            h.Clock.Advance(TimeSpan.FromSeconds(15));
            var r = h.Clock.GetUtcNow();
            var response = new SwpcHttpResponse(product, t.AddSeconds(2), r, SwpcTransportFailure.None, 200,
                mediaType: "application/json", body: PolicyData.Alerts);
            h.Clock.Advance(TimeSpan.FromSeconds(3));
            return ValueTask.FromResult(response);
        };
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "anchors").ConfigureAwait(false);
        var begin = h.Store.Begins.Single();
        var completion = h.Store.Completions.Single();
        Assert.AreEqual(t, begin.StartedUtc);
        Assert.AreEqual(t.AddSeconds(120), begin.CheckpointAfterStart.NextAttemptNotBeforeUtc);
        Assert.AreEqual(t.AddSeconds(15), completion.EndedUtc);
        Assert.AreEqual(t.AddSeconds(15), completion.RetrievedUtc);
        Assert.AreEqual(t.AddSeconds(18), completion.AtUtc);
        Assert.AreEqual(t, completion.Candidate!.FetchStartedUtc);
        Assert.AreEqual(t.AddSeconds(135), result.View.Products[SwpcProduct.Alerts].Checkpoint.NextAttemptNotBeforeUtc);
        Assert.AreEqual(t, result.View.Products[SwpcProduct.Alerts].LastAttemptUtc);
        Assert.AreEqual(1, h.Jitter.Samples);
    }

    [TestMethod]
    public async Task ConcurrentRunsAndResumeShareOneFlightGateAndWaitingHonoursCancellation()
    {
        using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        h.Client.Fetch = async (product, _, ct) =>
        {
            active++;
            maximum = Math.Max(maximum, active);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            active--;
            return new(product, h.Clock.GetUtcNow(), h.Clock.GetUtcNow(), SwpcTransportFailure.None, 500);
        };
        var first = h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "first").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var second = h.Runner.RunAsync(SwpcProduct.Scales, SwpcAttemptTrigger.Scheduled, "second").AsTask();
        var resume = h.Runner.ResumeAsync(null, Guid.NewGuid()).AsTask();
        using var cancelled = new CancellationTokenSource();
        var waiting = h.Runner.RunAsync(SwpcProduct.KpForecast, SwpcAttemptTrigger.Manual, "cancelled", cancelled.Token).AsTask();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting).ConfigureAwait(false);
        Assert.AreEqual(1, h.Store.Begins.Count);
        Assert.AreEqual(1, h.Client.Calls);
        Assert.IsFalse(resume.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);
        Assert.IsTrue((await resume.ConfigureAwait(false)).NotHalted);
        Assert.AreEqual(1, maximum);
        Assert.AreEqual(2, h.Store.Begins.Count);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task BeginConflictRetriesAreBoundedAndNeverSendBeforeApplied(int conflicts)
    {
        using var h = new Harness();
        h.Store.BeginConflicts = conflicts;
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "begin-conflict").ConfigureAwait(false);
        Assert.AreEqual(Math.Min(conflicts + 1, 4), h.Store.Begins.Count);
        Assert.AreEqual(conflicts == 4 ? SwpcWriteStatus.Conflict : SwpcWriteStatus.Applied, result.WriteStatus);
        Assert.AreEqual(conflicts == 4 ? 0 : 1, h.Client.Calls);
        Assert.AreEqual(1, h.Store.Begins.Select(b => b.AttemptId).Distinct().Count());
        Assert.AreEqual(1, h.Jitter.Samples);
    }

    [TestMethod]
    [DataRow(SwpcWriteStatus.Replayed)]
    [DataRow(SwpcWriteStatus.NotFound)]
    public async Task NonAppliedBeginSendsNothing(SwpcWriteStatus status)
    {
        using var h = new Harness();
        h.Store.BeginStatus = status;
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "not-applied").ConfigureAwait(false);
        Assert.AreEqual(status, result.WriteStatus);
        Assert.AreEqual(status == SwpcWriteStatus.Replayed ? "replayed" : "not-found", result.ReasonCode);
        Assert.AreEqual(0, h.Client.Calls);
        Assert.AreEqual(0, h.Store.Completions.Count);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task CompletionRetriesKeepExchangeAndSampleAndSettleExhaustion(int conflicts)
    {
        using var h = new Harness();
        h.Store.CompleteConflicts = conflicts;
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "complete-conflict").ConfigureAwait(false);
        Assert.AreEqual(conflicts + 1, h.Store.Completions.Count);
        Assert.AreEqual(conflicts == 4 ? "storage-failure" : "updated", result.ReasonCode);
        Assert.AreEqual(conflicts == 4 ? null : SwpcFetchOutcome.Updated, result.Outcome);
        Assert.IsFalse(h.Store.Incomplete);
        Assert.AreEqual(1, h.Client.Calls);
        Assert.AreEqual(1, h.Jitter.Samples);
        var attempted = h.Store.Completions.Where(c => c.Outcome is not null).ToArray();
        Assert.AreEqual(1, attempted.Select(c => c.Candidate).Distinct().Count());
        Assert.AreEqual(1, attempted.Select(c => c.AttemptId).Distinct().Count());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletionFailureSettlesStorageFailureAndKeepsAdmissionCheckpoint(bool projection)
    {
        using var h = new Harness();
        var original = new InvalidOperationException("test-projection");
        if (projection) h.Store.ProjectionFailure = original;
        else h.Store.CompletionFailure = c => c.Outcome is not null ? new SwpcStorageException() : null;
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "storage").ConfigureAwait(false);
        Assert.AreEqual("storage-failure", result.ReasonCode);
        Assert.IsNull(result.Outcome);
        Assert.IsFalse(h.Store.Incomplete);
        var settlement = h.Store.Completions.Last();
        AssertSettlement(settlement, "storage-failure");
        var stored = result.View.Products[SwpcProduct.Alerts];
        Assert.AreSame(h.Store.Begins.Single().CheckpointAfterStart, stored.Checkpoint);
        Assert.IsNull(stored.LastKnownGood);
        Assert.IsNull(stored.LastOutcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedStorageSettlementPropagatesOriginalFailureAndLeavesIncomplete(bool settlementConflict)
    {
        using var h = new Harness();
        var original = new SwpcStorageException("original");
        h.Store.CompletionFailure = c => c.Outcome is not null ? original : settlementConflict ? null : new IOException("settlement");
        if (settlementConflict) h.Store.CompleteConflicts = 1;
        var thrown = await Assert.ThrowsExactlyAsync<SwpcStorageException>(() => h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "failure").AsTask()).ConfigureAwait(false);
        Assert.AreSame(original, thrown);
        Assert.IsTrue(h.Store.Incomplete);
        Assert.AreEqual(2, h.Store.Completions.Count);
    }

    [TestMethod]
    public async Task ExhaustedCompletionAndSettlementConflictsLeaveAttemptRecoverable()
    {
        using var h = new Harness();
        h.Store.CompleteConflicts = 99;
        await Assert.ThrowsExactlyAsync<SwpcStorageException>(() => h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "exhausted").AsTask()).ConfigureAwait(false);
        Assert.AreEqual(5, h.Store.Completions.Count);
        Assert.AreEqual(1, h.Client.Calls);
        Assert.IsTrue(h.Store.Incomplete);
    }

    [TestMethod]
    public async Task CancellationBeforeBeginDoesNotWriteOrReadHealth()
    {
        using var h = new Harness();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "cancel", cancelled.Token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(0, h.Store.Begins.Count);
        Assert.AreEqual(0, h.Store.Completions.Count);
        Assert.AreEqual(0, h.Store.HealthReads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationAfterBeginUsesIndependentSettlementAndRethrowsOriginal(bool settlementFails)
    {
        using var h = new Harness();
        using var cancelled = new CancellationTokenSource();
        var original = new OperationCanceledException(cancelled.Token);
        h.Client.Fetch = async (_, _, _) =>
        {
            await cancelled.CancelAsync().ConfigureAwait(false);
            throw original;
        };
        if (settlementFails) h.Store.CompletionFailure = _ => new SwpcStorageException();
        var thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "cancel-after", cancelled.Token).AsTask()).ConfigureAwait(false);
        Assert.AreSame(original, thrown);
        AssertSettlement(h.Store.Completions.Single(), "interrupted");
        Assert.AreEqual(settlementFails, h.Store.Incomplete);
    }

    [TestMethod]
    public async Task UnexpectedClientExceptionIsSettledThenRethrownByIdentity()
    {
        using var h = new Harness();
        var original = new InvalidOperationException("unexpected");
        h.Client.Fetch = (_, _, _) => throw original;
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "unexpected").AsTask()).ConfigureAwait(false);
        Assert.AreSame(original, thrown);
        AssertSettlement(h.Store.Completions.Single(), "interrupted");
        Assert.IsFalse(h.Store.Incomplete);
    }

    [TestMethod]
    public async Task RecoveryPagesUntilIncompleteAndRunsOnlyOnceBeforeResumeOrRun()
    {
        using var h = new Harness();
        var start = PolicyData.Start();
        await h.Store.BeginAttemptAsync(start, default).ConfigureAwait(false);
        var incomplete = h.Store.Journal.Single();
        h.Store.DiagnosticsPage = query => query.BeforeSequence is null
            ? new([incomplete with { Sequence = 500, EndedUtc = h.Clock.GetUtcNow() }], 500)
            : new([incomplete], null);
        await h.Runner.ResumeAsync(null, Guid.NewGuid()).ConfigureAwait(false);
        Assert.AreEqual(2, h.Store.Queries.Count);
        Assert.IsNull(h.Store.Queries[0].BeforeSequence);
        Assert.AreEqual(500L, h.Store.Queries[1].BeforeSequence);
        AssertSettlement(h.Store.Completions.Single(), "interrupted");
        Assert.IsFalse(h.Store.Incomplete);
        await h.Runner.SettleIncompleteAsync().ConfigureAwait(false);
        await h.Runner.RunAsync(SwpcProduct.Scales, SwpcAttemptTrigger.Manual, "after-recovery").ConfigureAwait(false);
        Assert.AreEqual(1, h.Store.HealthReads);
    }

    [TestMethod]
    public async Task FirstRunAutomaticallySettlesIncompleteAndFastPathNeverReadsJournal()
    {
        using var h = new Harness();
        var start = PolicyData.Start();
        await h.Store.BeginAttemptAsync(start, default).ConfigureAwait(false);
        await h.Runner.RunAsync(SwpcProduct.Scales, SwpcAttemptTrigger.Scheduled, "automatic").ConfigureAwait(false);
        AssertSettlement(h.Store.Completions[0], "interrupted");
        Assert.AreEqual(1, h.Store.Queries.Count);
        using var fast = new Harness();
        await fast.Runner.SettleIncompleteAsync().ConfigureAwait(false);
        await fast.Runner.SettleIncompleteAsync().ConfigureAwait(false);
        Assert.AreEqual(1, fast.Store.HealthReads);
        Assert.AreEqual(0, fast.Store.Queries.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryStopsAtJournalEndOrBoundedPageLimit(bool endless)
    {
        using var h = new Harness();
        h.Store.HealthIncompleteOverride = 1;
        h.Store.DiagnosticsPage = query => new([], endless ? (query.BeforeSequence ?? 50_001) - 1 : null);
        await h.Runner.SettleIncompleteAsync().ConfigureAwait(false);
        Assert.AreEqual(endless ? 50_000 / 256 + 1 : 1, h.Store.Queries.Count);
        Assert.IsTrue(h.Store.Queries.All(q => q.Limit == 256));
    }

    [TestMethod]
    public async Task HaltRefusalAndNotHaltedResumeWriteNothingAndDoNotSampleJitter()
    {
        using var h = new Harness();
        Assert.IsTrue((await h.Runner.ResumeAsync(null, Guid.NewGuid()).ConfigureAwait(false)).NotHalted);
        h.Store.View = h.Store.View with { ProviderHalt = new("forbidden", h.Clock.GetUtcNow()) };
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "refused").ConfigureAwait(false);
        Assert.AreEqual(SwpcRefusal.Halted, result.Refusal);
        Assert.AreEqual(0, h.Store.Begins.Count);
        Assert.AreEqual(0, h.Store.Completions.Count);
        Assert.AreEqual(0, h.Store.Halts.Count);
        Assert.AreEqual(0, h.Jitter.Samples);
        Assert.AreEqual(0, h.Client.Calls);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(-0.21)]
    [DataRow(0.21)]
    public async Task InvalidJitterThrowsBeforeBeginOrAnyOtherWrite(double fraction)
    {
        using var h = new Harness();
        h.Jitter.Fraction = fraction;
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "bad-jitter").AsTask()).ConfigureAwait(false);
        Assert.AreEqual(0, h.Store.Begins.Count);
        Assert.AreEqual(0, h.Store.Completions.Count);
        Assert.AreEqual(0, h.Store.Halts.Count);
        Assert.AreEqual(0, h.Client.Calls);
    }

    [TestMethod]
    public async Task DiagnosticUnionIsOrdinalAndDeduplicatedWhetherStoreEmitsS2Code()
    {
        foreach (var code in new[] { "body-rejected:suspect-empty", "stale-snapshot" })
            foreach (var duplicate in new[] { false, true })
            {
                using var h = new Harness();
                if (code == "stale-snapshot")
                {
                    var newer = PolicyData.Classify(PolicyData.Response()).Candidate!;
                    h.Store.View = h.Store.View with
                    {
                        Products = h.Store.View.Products.SetItem(SwpcProduct.Alerts,
                        PolicyData.Stored() with { LastKnownGood = newer with { Sha256 = "newer", InFileStampUtc = newer.InFileStampUtc!.Value.AddHours(1) } })
                    };
                }
                else h.Client.Response = new(SwpcProduct.Alerts, SwpcTestData.Now, SwpcTestData.Now, SwpcTransportFailure.None, 200, mediaType: "application/json", body: "[]"u8.ToArray());
                h.Store.StoreDiagnostics = duplicate ? ["z-code", code, "a-code", code] : ["z-code", "a-code"];
                var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "diagnostics").ConfigureAwait(false);
                CollectionAssert.AreEqual(new[] { "a-code", code, "z-code" }.Order(StringComparer.Ordinal).ToArray(), result.Diagnostics.ToArray());
            }
    }

    [TestMethod]
    public async Task AppliedAttentionUsesProjectionEffectsAndReplayUsesStoredAttention()
    {
        using var h = new Harness();
        var record = SwpcAlertsParser.Parse(PolicyData.Alerts, "application/json").Records.Single();
        var groups = SwpcGroupBuilder.Build([new(record, 1)]);
        h.Store.AfterMerge = h.Store.View with { Notices = h.Store.View.Notices with { Groups = groups } };
        h.Store.Touched = ImmutableHashSet.Create(record.Node);
        h.Store.AppliedAttentionOverride = [new("provider/alerts/store-only", "new", null)];
        // A rejected third body can still exercise the store-supplied touched context for ordering.
        var stored = h.Store.View.Products[SwpcProduct.Alerts];
        h.Store.View = h.Store.View with
        {
            Products = h.Store.View.Products.SetItem(SwpcProduct.Alerts,
            stored with { Checkpoint = stored.Checkpoint with { ConsecutiveRejected = 2 } })
        };
        h.Client.Response = new(SwpcProduct.Alerts, SwpcTestData.Now, SwpcTestData.Now, SwpcTransportFailure.None, 200, mediaType: "application/json", body: "[]"u8.ToArray());
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "attention").ConfigureAwait(false);
        Assert.AreEqual(2, result.Attention.Length);
        Assert.AreEqual("ALTZZZ/1", result.Attention[0].Group);
        Assert.AreEqual("provider/alerts/format-drift", result.Attention[1].Group);
        using var replay = new Harness();
        replay.Store.CompleteStatus = SwpcWriteStatus.Replayed;
        replay.Store.ReplayAttention = [new("provider/alerts/format-drift", "new", null)];
        var replayed = await replay.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "replayed").ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Replayed, replayed.WriteStatus);
        CollectionAssert.AreEqual(replay.Store.ReplayAttention.ToArray(), replayed.Attention.ToArray());
    }

    [TestMethod]
    public async Task BeginConflictRereadsAndReadmitsChangedHaltWithoutSending()
    {
        using var h = new Harness();
        h.Store.BeforeBegin = _ => h.Store.View = h.Store.View with
        {
            Revision = h.Store.View.Revision + 1,
            ProviderHalt = new("forbidden", h.Clock.GetUtcNow())
        };
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "readmit").ConfigureAwait(false);
        Assert.AreEqual(SwpcRefusal.Halted, result.Refusal);
        Assert.AreEqual(1, h.Store.Begins.Count);
        Assert.AreEqual(2, h.Store.Reads.Count);
        Assert.AreEqual(0, h.Client.Calls);
    }

    [TestMethod]
    public async Task CompletionConflictUsesFreshRevisionAndProjectionCheckpoint()
    {
        using var h = new Harness();
        h.Client.Response = PolicyData.Response(500, completedAtStart: true);
        h.Store.BeforeCompleteAsync = (request, _) =>
        {
            if (h.Store.Completions.Count == 1)
            {
                var stored = h.Store.View.Products[SwpcProduct.Alerts];
                h.Store.View = h.Store.View with
                {
                    Revision = h.Store.View.Revision + 1,
                    Products = h.Store.View.Products.SetItem(SwpcProduct.Alerts, stored with
                    { Checkpoint = stored.Checkpoint with { ConsecutiveTransientFailures = 2 } })
                };
            }
            return ValueTask.CompletedTask;
        };
        var result = await h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "fresh-context").ConfigureAwait(false);
        Assert.AreEqual(2, h.Store.Completions.Count);
        Assert.AreEqual(h.Store.Completions[1].ExpectedRevision, h.Store.Completions[0].ExpectedRevision + 1);
        Assert.AreEqual(3, result.View.Products[SwpcProduct.Alerts].Checkpoint.ConsecutiveTransientFailures);
        Assert.AreEqual(h.Client.Response.CompletedUtc.AddSeconds(960), result.View.Products[SwpcProduct.Alerts].Checkpoint.NextAttemptNotBeforeUtc);
        Assert.AreEqual(1, h.Client.Calls);
        Assert.AreEqual(1, h.Jitter.Samples);
    }

    [TestMethod]
    public async Task CompletionCancellationSettlesInterruptedWithIndependentToken()
    {
        using var h = new Harness();
        using var caller = new CancellationTokenSource();
        var original = new OperationCanceledException(caller.Token);
        h.Store.BeforeCompleteAsync = async (request, token) =>
        {
            if (request.Outcome is not null)
            {
                await caller.CancelAsync().ConfigureAwait(false);
                throw original;
            }
            Assert.IsFalse(token.IsCancellationRequested);
            Assert.AreNotEqual(caller.Token, token);
        };
        var thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "cancel-completion", caller.Token).AsTask()).ConfigureAwait(false);
        Assert.AreSame(original, thrown);
        AssertSettlement(h.Store.Completions.Last(), "interrupted");
        Assert.IsFalse(h.Store.Incomplete);
    }

    [TestMethod]
    public async Task InterruptedSettlementTimeoutUsesInjectedClockAndPreservesClientException()
    {
        using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new InvalidOperationException("client");
        h.Client.Fetch = (_, _, _) => throw original;
        h.Store.BeforeCompleteAsync = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        };
        var pending = h.Runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "settlement-timeout").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(pending.IsCompleted);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pending).ConfigureAwait(false);
        Assert.AreSame(original, thrown);
        Assert.IsTrue(h.Store.Incomplete);
    }

    [TestMethod]
    public async Task ReplayedBeginNeverRepeatsTheExchange()
    {
        using var store = new SwpcPolicyTestStoreDouble();
        var client = new PolicyStubClient { Response = PolicyData.Response(completedAtStart: true) };
        var clock = new ManualTimeProvider(SwpcTestData.Now);
        var id = Guid.NewGuid();
        using var runner = new SwpcAttemptRunner(store, client, clock, SwpcJitter.None, () => id);
        await runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "same-run").ConfigureAwait(false);
        clock.Advance(TimeSpan.FromMinutes(3));
        var replay = await runner.RunAsync(SwpcProduct.Alerts, SwpcAttemptTrigger.Scheduled, "same-run").ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Replayed, replay.WriteStatus);
        Assert.AreEqual(1, client.Calls);
        Assert.AreEqual(1, store.Completions.Count);
        Assert.AreEqual(2, store.Begins.Count);
    }

    private static void AssertSettlement(SwpcAttemptCompletion completion, string reason)
    {
        Assert.AreEqual(reason, completion.ReasonCode);
        Assert.IsNull(completion.Outcome);
        Assert.IsNull(completion.Candidate);
        Assert.IsNull(completion.RetrievedUtc);
        Assert.IsNull(completion.HttpStatus);
        Assert.AreEqual(0L, completion.BytesRead);
        Assert.AreEqual(SwpcValidatorOutcome.Preserved, completion.ValidatorOutcome);
        Assert.AreEqual(completion.AtUtc, completion.EndedUtc);
    }

    private sealed class Harness : IDisposable
    {
        public SwpcPolicyTestStoreDouble Store { get; } = new();
        public PolicyStubClient Client { get; } = new() { Response = PolicyData.Response(completedAtStart: true) };
        public ManualTimeProvider Clock { get; } = new(SwpcTestData.Now);
        public CountingJitter Jitter { get; } = new();
        public SwpcAttemptRunner Runner { get; }
        public Harness() => Runner = new(Store, Client, Clock, Jitter);
        public void Dispose() { Runner.Dispose(); Store.Dispose(); }
    }

    private sealed class CountingJitter : ISwpcJitter
    {
        public int Samples { get; private set; }
        public double Fraction { get; set; }
        public double NextFraction(SwpcProduct product) { Samples++; return Fraction; }
    }
}
