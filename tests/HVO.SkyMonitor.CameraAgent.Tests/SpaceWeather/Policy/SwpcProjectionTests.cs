using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcProjectionTests
{
    [TestMethod]
    [DataRow(SwpcFetchOutcome.Updated)]
    [DataRow(SwpcFetchOutcome.Unchanged)]
    [DataRow(SwpcFetchOutcome.StaleSnapshot)]
    [DataRow(SwpcFetchOutcome.NotModified)]
    [DataRow(SwpcFetchOutcome.Rejected)]
    public void BaseSchedulesResetExpectedCountersAndRetainRetryTime(SwpcFetchOutcome outcome)
    {
        var t = SwpcTestData.Now;
        var context = Context(outcome, new(t, t.AddHours(1), true, 4, 2, 8, null));
        var effects = SwpcProjection.Create(SwpcProduct.Alerts, t, null, 0)(context);
        Assert.AreEqual(new SwpcFetchCheckpoint(context.Attempt.EndedUtc.AddSeconds(120), t.AddHours(1), false,
            0, 0, outcome == SwpcFetchOutcome.Rejected ? 9 : 0, null), effects.ProductCheckpoint);
        Assert.IsNull(effects.HaltChange);
        Assert.IsNull(effects.NoticeAttention);
        Assert.IsTrue(effects.ProviderAttention.IsEmpty);
    }

    [TestMethod]
    [DataRow(SwpcProduct.Alerts, 240, 480, 960, 1800, 1800)]
    [DataRow(SwpcProduct.Scales, 600, 1200, 1800, 1800, 1800)]
    [DataRow(SwpcProduct.KpForecast, 1800, 1800, 1800, 1800, 1800)]
    [DataRow(SwpcProduct.ThreeDayForecast, 1800, 1800, 1800, 1800, 1800)]
    public void TransientSeriesUsesEachBaseAndCap(SwpcProduct product, int a, int b, int c, int d, int e)
    {
        var checkpoint = new SwpcFetchCheckpoint(null, SwpcTestData.Now.AddHours(1), false, 0, 2, 5, null);
        foreach (var seconds in new[] { a, b, c, d, e })
        {
            var context = Context(SwpcFetchOutcome.TransientFailure, checkpoint, product);
            var effects = SwpcProjection.Create(product, SwpcTestData.Now, null, 0)(context);
            Assert.AreEqual(context.Attempt.EndedUtc.AddSeconds(seconds), effects.ProductCheckpoint.NextAttemptNotBeforeUtc);
            Assert.IsTrue(seconds >= 60);
            Assert.AreEqual(checkpoint.ConsecutiveTransientFailures + 1, effects.ProductCheckpoint.ConsecutiveTransientFailures);
            Assert.AreEqual(2, effects.ProductCheckpoint.ConsecutiveRateLimited);
            Assert.AreEqual(5, effects.ProductCheckpoint.ConsecutiveRejected);
            Assert.AreEqual(checkpoint.RetryNotBeforeUtc, effects.ProductCheckpoint.RetryNotBeforeUtc);
            Assert.IsTrue(effects.ProductCheckpoint.InBackoff);
            checkpoint = effects.ProductCheckpoint;
        }
    }

    [TestMethod]
    public void RateLimitSequenceHaltsProviderOnThirdAndRetryAfterUsesCompletionTime()
    {
        var t = SwpcTestData.Now;
        var checkpoint = new SwpcFetchCheckpoint(null, null, false, 7, 0, 4, null);
        for (var count = 1; count <= 3; count++)
        {
            var context = Context(count == 3 ? SwpcFetchOutcome.Halted : SwpcFetchOutcome.RateLimited, checkpoint)
                with
            { Attempt = Context(SwpcFetchOutcome.RateLimited, checkpoint).Attempt with { Outcome = count == 3 ? SwpcFetchOutcome.Halted : SwpcFetchOutcome.RateLimited, ReasonCode = "rate-limited" } };
            var effects = SwpcProjection.Create(SwpcProduct.Alerts, t, "Tue, 03 Feb 2026 01:02:00 GMT", 0)(context);
            var next = t.AddMinutes(2);
            Assert.AreEqual(count, effects.ProductCheckpoint.ConsecutiveRateLimited);
            Assert.AreEqual(count == 3 ? null : next, effects.ProductCheckpoint.NextAttemptNotBeforeUtc);
            Assert.AreEqual(count == 3 ? next : null, effects.ProductCheckpoint.RetryNotBeforeUtc);
            Assert.AreEqual(7, effects.ProductCheckpoint.ConsecutiveTransientFailures);
            Assert.AreEqual(4, effects.ProductCheckpoint.ConsecutiveRejected);
            Assert.IsTrue(effects.ProductCheckpoint.InBackoff);
            Assert.AreEqual(count == 3 ? new SwpcHaltChange(null, new("rate-limited", context.Attempt.EndedUtc)) : null, effects.HaltChange);
            checkpoint = effects.ProductCheckpoint;
        }
    }

    [TestMethod]
    [DataRow("forbidden", true)]
    [DataRow("endpoint-moved", false)]
    [DataRow("not-found", false)]
    [DataRow("gone", false)]
    [DataRow("client-error", false)]
    public void HaltsKeepCountersAndUseExchangeTime(string reason, bool provider)
    {
        var checkpoint = new SwpcFetchCheckpoint(SwpcTestData.Now, SwpcTestData.Now.AddHours(1), false, 5, 2, 9, null);
        var context = Context(SwpcFetchOutcome.Halted, checkpoint);
        context = context with { Attempt = context.Attempt with { ReasonCode = reason } };
        var effects = SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, 0)(context);
        Assert.AreEqual(checkpoint with { NextAttemptNotBeforeUtc = null }, effects.ProductCheckpoint);
        Assert.AreEqual(new SwpcHaltChange(provider ? null : SwpcProduct.Alerts, new(reason, context.Attempt.EndedUtc)), effects.HaltChange);
    }

    [TestMethod]
    public void FormatDriftRaisesExactlyOnThirdAndOnlyAgainAfterSuccess()
    {
        var checkpoint = PolicyData.Stored().Checkpoint;
        var project = SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, 0);
        foreach (var expectedCount in new[] { 1, 2, 3, 4, 0, 1, 2, 3 })
        {
            var outcome = expectedCount == 0 ? SwpcFetchOutcome.NotModified : SwpcFetchOutcome.Rejected;
            var effects = project(Context(outcome, checkpoint));
            Assert.AreEqual(expectedCount, effects.ProductCheckpoint.ConsecutiveRejected);
            CollectionAssert.AreEqual(expectedCount == 3 ? new[] { new SwpcAttentionEvent("provider/alerts/format-drift", "new", null) } : [], effects.ProviderAttention.ToArray());
            checkpoint = effects.ProductCheckpoint;
        }
    }

    [TestMethod]
    public void RecordDriftEpisodeOpensAdvancesClosesAndReopensOnlyForUpdated()
    {
        var t = SwpcTestData.Now;
        var checkpoint = PolicyData.Stored().Checkpoint;
        var project = SwpcProjection.Create(SwpcProduct.Alerts, t, null, 0);
        var rows = new[] {
            (SwpcFetchOutcome.Updated, true, 0, (int?)0, true),
            (SwpcFetchOutcome.Updated, false, 0, (int?)0, false),
            (SwpcFetchOutcome.Updated, true, 1, (int?)1, false),
            (SwpcFetchOutcome.Unchanged, false, 2, (int?)1, false),
            (SwpcFetchOutcome.StaleSnapshot, true, 3, (int?)1, false),
            (SwpcFetchOutcome.Updated, false, 2, (int?)null, false),
            (SwpcFetchOutcome.Updated, true, 3, (int?)3, true) };
        foreach (var (outcome, drifted, minute, stamp, raised) in rows)
        {
            var context = Context(outcome, checkpoint);
            var candidate = PolicyData.Classify(PolicyData.Response()).Candidate!;
            var body = ((SwpcAlertsBody)candidate.Parsed).Value with
            {
                StampUtc = t.AddMinutes(minute),
                Rejections = drifted ? [new(1, "record-schema")] : []
            };
            context = context with { Attempt = context.Attempt with { Candidate = candidate with { Parsed = new SwpcAlertsBody(body), InFileStampUtc = body.StampUtc } } };
            var effects = project(context);
            Assert.AreEqual(stamp is { } minutes ? t.AddMinutes(minutes) : null, effects.ProductCheckpoint.RecordDriftStampUtc);
            Assert.AreEqual(raised ? 1 : 0, effects.ProviderAttention.Length);
            if (raised) Assert.AreEqual(new SwpcAttentionEvent("provider/alerts/record-drift", "new", null), effects.ProviderAttention.Single());
            checkpoint = effects.ProductCheckpoint;
        }
        var repeated = PolicyData.Alerts.AsSpan()[1..^1].ToArray();
        var bytes = "["u8.ToArray().Concat(repeated).Concat(","u8.ToArray()).Concat(repeated).Concat("]"u8.ToArray()).ToArray();
        var parsed = SwpcAlertsParser.Parse(bytes, "application/json");
        Assert.AreEqual(2, parsed.Records.Length);
        Assert.IsTrue(parsed.Rejections.IsEmpty, "Repeats are diagnostics, not record rejections.");
        Assert.IsTrue(SwpcAlertsParser.Diagnostics(parsed).Any(d => d.StartsWith("duplicate-record-in-body:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NoticeAttentionUsesAfterMergeTouchedRanksAndCompletionClock()
    {
        var context = Context(SwpcFetchOutcome.Updated, PolicyData.Stored().Checkpoint);
        var record = SwpcAlertsParser.Parse(PolicyData.Alerts, "application/json").Records.Single();
        var groups = SwpcGroupBuilder.Build([new(record, 1)]);
        var touched = ImmutableHashSet.Create(record.Node);
        var ranks = ImmutableDictionary<SwpcMessageNode, int>.Empty;
        var after = context.AfterMerge with { Notices = context.AfterMerge.Notices with { Groups = groups, RaisedRanks = ranks } };
        context = context with { AfterMerge = after, Touched = touched };
        var actual = SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, 0)(context).NoticeAttention!;
        var expected = SwpcAttentionEvaluator.Evaluate(new(groups, touched, ranks, context.Attempt.EndedUtc));
        CollectionAssert.AreEqual(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.AreEqual(1, actual.Events.Length);
        CollectionAssert.AreEquivalent(expected.RaisedRanks.ToArray(), actual.RaisedRanks.ToArray());
        Assert.IsNull(SwpcProjection.Create(SwpcProduct.Scales, SwpcTestData.Now, null, 0)(context).NoticeAttention);
        Assert.IsNull(SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, 0)(context with { Touched = [] }).NoticeAttention);
        var raised = ranks.Add(record.Node, 0);
        var alreadyRaised = context with { AfterMerge = after with { Notices = after.Notices with { RaisedRanks = raised } } };
        Assert.IsTrue(SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, 0)(alreadyRaised).NoticeAttention!.Events.IsEmpty);
        var muchLater = context with { Attempt = context.Attempt with { EndedUtc = context.Attempt.EndedUtc.AddDays(2) } };
        Assert.IsTrue(SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, 0)(muchLater).NoticeAttention!.Events.IsEmpty);
    }

    [TestMethod]
    [DataRow("interrupted")]
    [DataRow("storage-failure")]
    public void NullOutcomePreservesIdentityAndProjectionIsDeterministic(string reason)
    {
        var context = Context(null, new(SwpcTestData.Now, SwpcTestData.Now, true, 2, 3, 4, SwpcTestData.Now));
        context = context with { Attempt = context.Attempt with { ReasonCode = reason } };
        var project = SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, 0);
        var effects = project(context);
        Assert.AreSame(context.Before.Products[SwpcProduct.Alerts].Checkpoint, effects.ProductCheckpoint);
        Assert.IsNull(effects.HaltChange);
        Assert.IsNull(effects.NoticeAttention);
        Assert.IsTrue(effects.ProviderAttention.IsEmpty);
        foreach (var outcome in Enum.GetValues<SwpcFetchOutcome>())
        {
            var valid = Context(outcome, PolicyData.Stored().Checkpoint);
            Assert.AreEqual(JsonSerializer.Serialize(project(valid)), JsonSerializer.Serialize(project(valid)));
        }
    }

    [TestMethod]
    [DataRow(-0.2)]
    [DataRow(0.0)]
    [DataRow(0.2)]
    public void JitterAffectsOnlyBaseScheduleAndHonoursSpacing(double fraction)
    {
        var t = SwpcTestData.Now;
        foreach (var product in Enum.GetValues<SwpcProduct>())
        {
            var project = SwpcProjection.Create(product, t, "90", fraction);
            foreach (var outcome in new[] { SwpcFetchOutcome.Updated, SwpcFetchOutcome.Rejected, SwpcFetchOutcome.NotModified })
            {
                var context = Context(outcome, PolicyData.Stored().Checkpoint, product);
                var next = project(context).ProductCheckpoint.NextAttemptNotBeforeUtc;
                Assert.AreEqual(context.Attempt.EndedUtc + SwpcSource.BaseInterval(product) * (1 + fraction), next);
                Assert.IsTrue(next >= t.AddSeconds(60));
            }
            var limited = Context(SwpcFetchOutcome.RateLimited, PolicyData.Stored().Checkpoint, product);
            Assert.AreEqual(limited.Attempt.EndedUtc.AddSeconds(90), project(limited).ProductCheckpoint.NextAttemptNotBeforeUtc);
            var transient = Context(SwpcFetchOutcome.TransientFailure, PolicyData.Stored().Checkpoint, product);
            Assert.AreEqual(SwpcProjection.Create(product, t, "90", 0)(transient).ProductCheckpoint, project(transient).ProductCheckpoint);
        }
        // Exercise the defensive clamp independently of the production r >= t invariant.
        var clamp = SwpcProjection.Create(SwpcProduct.Alerts, t.AddSeconds(200), null, fraction);
        Assert.AreEqual(t.AddSeconds(260), clamp(Context(SwpcFetchOutcome.Updated, PolicyData.Stored().Checkpoint)).ProductCheckpoint.NextAttemptNotBeforeUtc);
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(-0.20001)]
    [DataRow(0.20001)]
    public void InvalidJitterIsRejected(double fraction) => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, fraction));

    [TestMethod]
    public void IntegerLimitCountersNeverWrapOrOverflowTheBackoff()
    {
        var checkpoint = new SwpcFetchCheckpoint(null, null, false, int.MaxValue, int.MaxValue, int.MaxValue, null);
        var project = SwpcProjection.Create(SwpcProduct.Alerts, SwpcTestData.Now, null, 0);
        var transient = Context(SwpcFetchOutcome.TransientFailure, checkpoint);
        var effects = project(transient);
        Assert.AreEqual(int.MaxValue, effects.ProductCheckpoint.ConsecutiveTransientFailures);
        Assert.AreEqual(transient.Attempt.EndedUtc.AddSeconds(1800), effects.ProductCheckpoint.NextAttemptNotBeforeUtc);
        Assert.AreEqual(int.MaxValue, project(Context(SwpcFetchOutcome.Rejected, checkpoint)).ProductCheckpoint.ConsecutiveRejected);
        var rate = Context(SwpcFetchOutcome.Halted, checkpoint);
        rate = rate with { Attempt = rate.Attempt with { ReasonCode = "rate-limited" } };
        Assert.AreEqual(int.MaxValue, project(rate).ProductCheckpoint.ConsecutiveRateLimited);
    }

    [TestMethod]
    public void UniformJitterUsesInjectedRandomAndStaysInRange()
    {
        var expected = new Random(1398);
        var jitter = new SwpcUniformJitter(new Random(1398));
        for (var i = 0; i < 1000; i++)
        {
            var actual = jitter.NextFraction(SwpcProduct.Alerts);
            Assert.AreEqual((expected.NextDouble() * 2 - 1) * SwpcJitter.JitterFraction, actual);
            Assert.IsTrue(actual >= -0.2 && actual <= 0.2);
            Assert.AreEqual(0d, SwpcJitter.None.NextFraction(SwpcProduct.Alerts));
        }
    }

    internal static SwpcCommitContext Context(SwpcFetchOutcome? outcome, SwpcFetchCheckpoint checkpoint, SwpcProduct product = SwpcProduct.Alerts)
    {
        var view = SwpcPolicyTestStoreDouble.EmptyView();
        view = view with { Products = view.Products.SetItem(product, view.Products[product] with { Checkpoint = checkpoint }) };
        var attempt = PolicyData.Classify(PolicyData.Response()) with
        {
            Outcome = outcome,
            Candidate = null,
            ReasonCode = outcome == SwpcFetchOutcome.Rejected ? "body-rejected:schema" : outcome == SwpcFetchOutcome.Halted ? "forbidden" : "test"
        };
        return new(attempt, view, view, []);
    }
}
