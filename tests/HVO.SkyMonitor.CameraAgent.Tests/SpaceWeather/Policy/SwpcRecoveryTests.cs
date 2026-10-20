using System.Collections.Immutable;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcRecoveryTests
{
    [TestMethod]
    public async Task MissingIncompleteFailsClosedAndRetriesBeforeAnyRunOrResume()
    {
        using var store = await StartedStoreAsync(2).ConfigureAwait(false);
        var client = new PolicyStubClient { Response = PolicyData.Response(completedAtStart: true) };
        using var runner = Runner(store, client);
        store.DiagnosticsPage = query => query.BeforeSequence is null
            ? new([store.Journal[1]], 2)
            : new([], null);

        await AssertRecoveryFailureAsync(() => runner.SettleIncompleteAsync().AsTask(), "space-weather.recovery-incomplete").ConfigureAwait(false);
        await AssertRecoveryFailureAsync(() => runner.RunAsync(SwpcProduct.Scales, SwpcAttemptTrigger.Manual, "blocked").AsTask(), "space-weather.recovery-incomplete").ConfigureAwait(false);
        await AssertRecoveryFailureAsync(() => runner.ResumeAsync(null, Guid.NewGuid()).AsTask(), "space-weather.recovery-incomplete").ConfigureAwait(false);
        Assert.AreEqual(3, store.HealthReads);
        Assert.AreEqual(6, store.Queries.Count);
        Assert.AreEqual(1, store.Begins.Count);
        Assert.AreEqual(0, store.Completions.Count);
        Assert.AreEqual(0, store.Halts.Count);
        Assert.AreEqual(0, client.Calls);
        Assert.IsTrue(store.Incomplete);

        store.DiagnosticsPage = null;
        var result = await runner.RunAsync(SwpcProduct.Scales, SwpcAttemptTrigger.Manual, "recovered").ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, result.WriteStatus);
        Assert.AreEqual("interrupted", store.Completions[0].ReasonCode);
        Assert.IsFalse(store.Incomplete);
        Assert.AreEqual(4, store.HealthReads);
        Assert.AreEqual(7, store.Queries.Count);
        Assert.AreEqual(2, store.Begins.Count);
        Assert.AreEqual(1, client.Calls);
        await runner.SettleIncompleteAsync().ConfigureAwait(false);
        Assert.AreEqual(4, store.HealthReads);
    }

    [TestMethod]
    [DataRow(15, 5)]
    [DataRow(600, 200)]
    public async Task ShortNonFinalPagesSettleEvenBeyondOldFixedBound(int retained, int expectedCalls)
    {
        using var store = await StartedStoreAsync(retained).ConfigureAwait(false);
        using var runner = Runner(store);
        store.DiagnosticsPage = query =>
        {
            var entries = store.Journal.Where(e => query.BeforeSequence is null || e.Sequence < query.BeforeSequence)
                .OrderByDescending(e => e.Sequence).Take(3).ToImmutableArray();
            return new(entries, entries[^1].Sequence > 1 ? entries[^1].Sequence : null);
        };
        await runner.SettleIncompleteAsync().ConfigureAwait(false);
        Assert.AreEqual(expectedCalls, store.Queries.Count);
        Assert.IsTrue(store.Queries.All(q => q.Limit == 200));
        Assert.AreEqual("interrupted", store.Completions.Single().ReasonCode);
        Assert.IsFalse(store.Incomplete);
        await runner.SettleIncompleteAsync().ConfigureAwait(false);
        await runner.ResumeAsync(null, Guid.NewGuid()).ConfigureAwait(false);
        Assert.AreEqual(1, store.HealthReads);
        Assert.AreEqual(expectedCalls, store.Queries.Count);
    }

    [TestMethod]
    [DataRow(200, 1)]
    [DataRow(201, 2)]
    [DataRow(400, 2)]
    [DataRow(401, 3)]
    public async Task DefaultPagingSettlesAtStorePageSizeBoundaries(int retained, int expectedCalls)
    {
        using var store = await StartedStoreAsync(retained).ConfigureAwait(false);
        using var runner = Runner(store);
        await runner.SettleIncompleteAsync().ConfigureAwait(false);
        Assert.AreEqual(expectedCalls, store.Queries.Count);
        Assert.IsTrue(store.Queries.All(q => q.Limit == 200));
        Assert.IsNull(store.Queries[0].BeforeSequence);
        for (var page = 1; page < expectedCalls; page++)
            Assert.AreEqual((long)retained - page * 200 + 1, store.Queries[page].BeforeSequence);
        Assert.AreEqual("interrupted", store.Completions.Single().ReasonCode);
        Assert.IsFalse(store.Incomplete);
        Assert.AreEqual(1, store.HealthReads);

        // Recovery stops once sequence 1 settles; repeat its requests to pin each
        // returned continuation, including the final cursor that recovery ignores.
        var queries = store.Queries.ToArray();
        foreach (var query in queries)
        {
            var page = await store.ReadDiagnosticsAsync(query, default).ConfigureAwait(false);
            var minimum = page.Attempts[^1].Sequence;
            Assert.AreEqual(minimum > 1 ? (long?)minimum : null, page.NextBeforeSequence);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DiagnosticsDoubleEnforcesStoreRequestBoundsBeforeOverrides(bool useOverride)
    {
        using var store = new SwpcPolicyTestStoreDouble();
        var overrideCalls = 0;
        if (useOverride)
            store.DiagnosticsPage = _ =>
            {
                overrideCalls++;
                return new([], null);
            };
        SwpcDiagnosticsQuery[] invalid =
        [
            new(SwpcTestData.Now, null, null, 0),
            new(SwpcTestData.Now, null, null, 201),
            new(SwpcTestData.Now, null, 0, 200)
        ];
        foreach (var query in invalid)
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.ReadDiagnosticsAsync(query, default).AsTask()).ConfigureAwait(false);
            Assert.AreSame(query, store.Queries[^1]);
        }
        Assert.AreEqual(3, store.Queries.Count);
        Assert.AreEqual(3, store.Calls.Count(c => c == "ReadDiagnostics"));
        Assert.AreEqual(0, overrideCalls);
        foreach (var limit in new[] { 1, 200 })
        {
            var query = new SwpcDiagnosticsQuery(SwpcTestData.Now, null, null, limit);
            var result = await store.ReadDiagnosticsAsync(query, default).ConfigureAwait(false);
            Assert.IsTrue(result.Attempts.IsEmpty);
            Assert.IsNull(result.NextBeforeSequence);
            Assert.AreSame(query, store.Queries[^1]);
        }
        Assert.AreEqual(5, store.Queries.Count);
        Assert.AreEqual(useOverride ? 2 : 0, overrideCalls);
    }

    [TestMethod]
    [DataRow("equal-cursor", 2)]
    [DataRow("higher-cursor", 2)]
    [DataRow("above-page-minimum", 1)]
    [DataRow("empty-non-final", 1)]
    [DataRow("ascending", 1)]
    [DataRow("unsorted", 1)]
    [DataRow("duplicate-sequence", 1)]
    [DataRow("at-cursor", 2)]
    [DataRow("above-cursor", 2)]
    [DataRow("oversized", 1)]
    [DataRow("continuation-below-one", 1)]
    public async Task InvalidPagingFailsBeforeAnyEntryIsSettled(string violation, int expectedCalls)
    {
        using var store = await StartedStoreAsync(300).ConfigureAwait(false);
        using var runner = Runner(store);
        var incomplete = store.Journal[0];
        var complete = store.Journal[1];
        store.DiagnosticsPage = query =>
        {
            if (expectedCalls == 2 && query.BeforeSequence is null)
                return new([complete with { Sequence = 100 }], 100);
            return violation switch
            {
                "equal-cursor" => new([complete with { Sequence = 99 }], 100),
                "higher-cursor" => new([complete with { Sequence = 99 }], 101),
                "above-page-minimum" => new([complete with { Sequence = 100 }, incomplete with { Sequence = 90 }], 95),
                "empty-non-final" => new([], 100),
                "ascending" => new([incomplete with { Sequence = 90 }, complete with { Sequence = 100 }], null),
                "unsorted" => new([incomplete with { Sequence = 100 }, complete with { Sequence = 80 }, complete with { Sequence = 90 }], null),
                "duplicate-sequence" => new([incomplete with { Sequence = 100 }, complete with { Sequence = 100 }], null),
                "at-cursor" => new([incomplete with { Sequence = 100 }], null),
                "above-cursor" => new([incomplete with { Sequence = 101 }], null),
                "oversized" => new(Enumerable.Range(0, 201).Select(i => (i == 0 ? incomplete : complete) with { Sequence = 500 - i }).ToImmutableArray(), null),
                "continuation-below-one" => new([complete with { Sequence = 5 }, incomplete with { Sequence = 3 }], 0),
                _ => throw new AssertFailedException("Unknown paging violation.")
            };
        };
        await AssertRecoveryFailureAsync(() => runner.SettleIncompleteAsync().AsTask(), "space-weather.journal-paging").ConfigureAwait(false);
        Assert.AreEqual(expectedCalls, store.Queries.Count);
        Assert.AreEqual(0, store.Completions.Count, "The entire page must be validated before settling its first entry.");
        Assert.IsTrue(store.Incomplete);
    }

    [TestMethod]
    public async Task ContinuationBelowOneNeverBecomesRejectedStoreRequest()
    {
        using var store = await StartedStoreAsync(2).ConfigureAwait(false);
        using var runner = Runner(store);
        store.DiagnosticsPage = _ => new([store.Journal[1] with { Sequence = 5 }], 0);
        await AssertRecoveryFailureAsync(() => runner.SettleIncompleteAsync().AsTask(), "space-weather.journal-paging").ConfigureAwait(false);
        Assert.AreEqual(1, store.Queries.Count);
        Assert.AreEqual(0, store.Completions.Count);
        Assert.IsTrue(store.Incomplete);
    }

    [TestMethod]
    public async Task InconsistentHealthFailsWithoutPaging()
    {
        using var store = await StartedStoreAsync(1).ConfigureAwait(false);
        using var runner = Runner(store);
        store.HealthIncompleteOverride = 2;
        await AssertRecoveryFailureAsync(() => runner.SettleIncompleteAsync().AsTask(), "space-weather.journal-paging").ConfigureAwait(false);
        Assert.AreEqual(1, store.HealthReads);
        Assert.AreEqual(0, store.Queries.Count);
        Assert.AreEqual(0, store.Completions.Count);
    }

    [TestMethod]
    public async Task EndlessValidPagesStopOnFirstEntryBeyondRetainedCount()
    {
        const int retained = 4;
        using var store = await StartedStoreAsync(retained).ConfigureAwait(false);
        using var runner = Runner(store);
        store.DiagnosticsPage = query =>
        {
            var sequence = (query.BeforeSequence ?? 100) - 1;
            return new([store.Journal[1] with { Sequence = sequence }], sequence);
        };
        await AssertRecoveryFailureAsync(() => runner.SettleIncompleteAsync().AsTask(), "space-weather.journal-paging").ConfigureAwait(false);
        Assert.AreEqual(retained + 1, store.Queries.Count);
        Assert.AreEqual(0, store.Completions.Count);
        Assert.IsTrue(store.Incomplete);
    }

    [TestMethod]
    [DataRow(SwpcWriteStatus.Conflict)]
    [DataRow(SwpcWriteStatus.NotFound)]
    public async Task UncommittedRecoveryPropagatesAndNextCallStartsFresh(SwpcWriteStatus status)
    {
        using var store = await StartedStoreAsync(1).ConfigureAwait(false);
        using var runner = Runner(store);
        store.CompleteStatus = status;
        await Assert.ThrowsExactlyAsync<SwpcStorageException>(() => runner.SettleIncompleteAsync().AsTask()).ConfigureAwait(false);
        Assert.IsTrue(store.Incomplete);
        store.CompleteStatus = null;
        await runner.SettleIncompleteAsync().ConfigureAwait(false);
        Assert.IsFalse(store.Incomplete);
        Assert.AreEqual(2, store.HealthReads);
        Assert.AreEqual(2, store.Queries.Count);
    }

    private static async Task AssertRecoveryFailureAsync(Func<Task> action, string reason)
    {
        var exception = await Assert.ThrowsExactlyAsync<SwpcStorageException>(action).ConfigureAwait(false);
        Assert.AreEqual(reason, exception.ReasonCode);
        Assert.AreEqual(reason == "space-weather.journal-paging"
            ? "Space-weather journal paging is inconsistent."
            : "Space-weather startup recovery is incomplete.", exception.Message);
    }

    private static SwpcAttemptRunner Runner(SwpcPolicyTestStoreDouble store, PolicyStubClient? client = null) =>
        new(store, client ?? new PolicyStubClient(), new ManualTimeProvider(SwpcTestData.Now), SwpcJitter.None);

    private static async Task<SwpcPolicyTestStoreDouble> StartedStoreAsync(int retained)
    {
        var store = new SwpcPolicyTestStoreDouble();
        await store.BeginAttemptAsync(PolicyData.Start(), default).ConfigureAwait(false);
        var incomplete = store.Journal.Single();
        for (var sequence = 2; sequence <= retained; sequence++)
            store.Journal.Add(incomplete with { Sequence = sequence, AttemptId = Guid.NewGuid(), EndedUtc = SwpcTestData.Now });
        return store;
    }
}
