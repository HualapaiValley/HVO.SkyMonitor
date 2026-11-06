using System.Collections.Immutable;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;

/// <summary>P7: pure checkpoint, halt and attention effects; storage owns all merging and freshness.</summary>
public static class SwpcProjection
{
    /// <summary>
    /// Creates a bounded deterministic projection over immutable captured values. P1: r
    /// (Attempt.EndedUtc) anchors every post-exchange time, Retry-After and attention;
    /// t anchors the minimum-spacing floor. P2: one prevalidated jitter sample affects
    /// base schedules only. A null outcome preserves the Before checkpoint by identity.
    /// Counters saturate at the seam's integer limit; capped exponentiation stays bounded.
    /// </summary>
    public static SwpcCommitProjection Create(SwpcProduct product, DateTimeOffset startedUtc, string? retryAfter, double jitter)
    {
        SwpcJitter.Validate(jitter);
        var interval = SwpcSource.BaseInterval(product);
        return context =>
        {
            var checkpoint = context.Before.Products[product].Checkpoint;
            var attempt = context.Attempt;
            if (attempt.Outcome is null) return new(checkpoint, null, null, []);
            var r = attempt.EndedUtc;
            var baseNext = SwpcPolicy.Later(r + interval * (1 + jitter), startedUtc + SwpcSource.MinimumAttemptSpacing);
            SwpcHaltChange? halt = null;
            var providerAttention = ImmutableArray.CreateBuilder<SwpcAttentionEvent>();
            switch (attempt.Outcome)
            {
                case SwpcFetchOutcome.TransientFailure:
                    var n = Increment(checkpoint.ConsecutiveTransientFailures);
                    var delay = Math.Min(SwpcSource.TransientBackoffCap.TotalSeconds,
                        Math.Max(SwpcSource.MinimumAttemptSpacing.TotalSeconds, interval.TotalSeconds * Math.Pow(2, Math.Min(n, 10))));
                    checkpoint = checkpoint with { ConsecutiveTransientFailures = n, InBackoff = true, NextAttemptNotBeforeUtc = r.AddSeconds(delay) };
                    break;
                case SwpcFetchOutcome.RateLimited:
                case SwpcFetchOutcome.Halted when attempt.ReasonCode == "rate-limited":
                    var halting = attempt.Outcome == SwpcFetchOutcome.Halted;
                    var retry = r + SwpcRetryAfter.Resolve(retryAfter, r);
                    checkpoint = checkpoint with
                    {
                        ConsecutiveRateLimited = Increment(checkpoint.ConsecutiveRateLimited),
                        InBackoff = true,
                        NextAttemptNotBeforeUtc = halting ? null : retry,
                        RetryNotBeforeUtc = halting ? retry : checkpoint.RetryNotBeforeUtc
                    };
                    if (halting) halt = new(null, new("rate-limited", r));
                    break;
                case SwpcFetchOutcome.Halted:
                    halt = new(attempt.ReasonCode == "forbidden" ? null : product, new(attempt.ReasonCode, r));
                    checkpoint = checkpoint with { NextAttemptNotBeforeUtc = null };
                    break;
                case SwpcFetchOutcome.Rejected:
                    var rejected = Increment(checkpoint.ConsecutiveRejected);
                    checkpoint = checkpoint with
                    {
                        ConsecutiveRejected = rejected,
                        ConsecutiveTransientFailures = 0,
                        ConsecutiveRateLimited = 0,
                        InBackoff = false,
                        NextAttemptNotBeforeUtc = baseNext
                    };
                    if (rejected == SwpcSource.RejectedAttentionCount)
                        providerAttention.Add(new("provider/" + product.Token() + "/format-drift", "new", null));
                    break;
                default:
                    checkpoint = checkpoint with
                    {
                        ConsecutiveTransientFailures = 0,
                        ConsecutiveRateLimited = 0,
                        ConsecutiveRejected = 0,
                        InBackoff = false,
                        NextAttemptNotBeforeUtc = baseNext
                    };
                    break;
            }
            if (product == SwpcProduct.Alerts && attempt.Outcome == SwpcFetchOutcome.Updated
                && attempt.Candidate?.Parsed is SwpcAlertsBody body)
            {
                // S1's Rejections contains only failed records; repeated valid records stay in Records.
                var stamp = body.Value.StampUtc;
                var previous = checkpoint.RecordDriftStampUtc;
                if (!body.Value.Rejections.IsEmpty)
                {
                    if (previous is null) providerAttention.Add(new("provider/alerts/record-drift", "new", null));
                    if (previous is null || stamp > previous) checkpoint = checkpoint with { RecordDriftStampUtc = stamp };
                }
                else if (previous is not null && stamp > previous) checkpoint = checkpoint with { RecordDriftStampUtc = null };
            }
            var noticeAttention = product == SwpcProduct.Alerts && !context.Touched.IsEmpty
                ? SwpcAttentionEvaluator.Evaluate(new(context.AfterMerge.Notices.Groups, context.Touched,
                    context.AfterMerge.Notices.RaisedRanks, r)) : null;
            return new(checkpoint, halt, noticeAttention, providerAttention.ToImmutable());
        };
    }

    private static int Increment(int count) => count == int.MaxValue ? count : count + 1;
}
