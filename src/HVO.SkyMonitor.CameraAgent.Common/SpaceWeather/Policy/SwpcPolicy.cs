using System.Collections.Immutable;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;

/// <summary>The first admission rule that prevented an exchange (owner decision P3).</summary>
public enum SwpcRefusal
{
    /// <summary>A provider or product halt is in effect.</summary>
    Halted,
    /// <summary>A manual refresh is still in backoff.</summary>
    Backoff,
    /// <summary>A manual refresh is within minimum spacing.</summary>
    MinInterval,
    /// <summary>The scheduler fired before the permitted time.</summary>
    NotDue
}

/// <summary>A refusal without side effects, or the durable admission to submit.</summary>
public sealed record SwpcAdmission(SwpcRefusal? Refusal, SwpcAttemptStart? Start);

/// <summary>Pure admission, response classification and resume policy, without storage or clocks.</summary>
public static class SwpcPolicy
{
    /// <summary>
    /// Applies P3 in order: halt, scheduled due time, manual backoff, manual spacing.
    /// P1: admission time is t, the LastAttemptUtc anchor. P3/Q2: a start ends the
    /// prior backoff and checkpoints at least t + base, without jitter, for crash recovery.
    /// </summary>
    public static SwpcAdmission Admit(SwpcStoreView view, SwpcProduct product, SwpcAttemptTrigger trigger,
        string runKey, Guid attemptId, DateTimeOffset startedUtc)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!Enum.IsDefined(trigger)) throw new ArgumentOutOfRangeException(nameof(trigger));
        var interval = SwpcSource.BaseInterval(product);
        var stored = view.Products[product];
        var checkpoint = stored.Checkpoint;
        SwpcRefusal? refusal = null;
        if (view.ProviderHalt is not null || stored.ProductHalt is not null) refusal = SwpcRefusal.Halted;
        else if (trigger == SwpcAttemptTrigger.Scheduled && startedUtc < checkpoint.NextAttemptNotBeforeUtc)
            refusal = SwpcRefusal.NotDue;
        else if (trigger == SwpcAttemptTrigger.Manual && checkpoint.InBackoff && startedUtc < checkpoint.NextAttemptNotBeforeUtc)
            refusal = SwpcRefusal.Backoff;
        else if (trigger == SwpcAttemptTrigger.Manual && startedUtc < stored.LastAttemptUtc + SwpcSource.MinimumAttemptSpacing)
            refusal = SwpcRefusal.MinInterval;
        return refusal is not null ? new(refusal, null) : new(null,
            new(attemptId, view.Revision, product, runKey, trigger, startedUtc, stored.Validators,
                checkpoint with { InBackoff = false, NextAttemptNotBeforeUtc = Later(checkpoint.NextAttemptNotBeforeUtc, startedUtc + interval) }));
    }

    /// <summary>
    /// Implements P4–P6 in first-match order. P1: r is response completion, never
    /// earlier than admission; submission time is independent. P5/T1 probes the
    /// public S1 parser with an empty body so S1 remains the sole media-type rule.
    /// Candidates retain exact decoded bytes and S1's hash and content stamp.
    /// </summary>
    public static SwpcAttemptCompletion Classify(SwpcAttemptStart start, SwpcStoredProduct stored,
        SwpcHttpResponse response, DateTimeOffset submittedUtc)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(response);
        if (response.Product != start.Product || response.CompletedUtc < start.StartedUtc)
            throw new ArgumentException("The response must match the admitted exchange.", nameof(response));
        SwpcFetchOutcome outcome;
        string reason;
        SwpcSnapshot? candidate = null;
        var status = response.StatusCode;
        if (response.Failure == SwpcTransportFailure.Timeout) (outcome, reason) = (SwpcFetchOutcome.TransientFailure, "timeout");
        else if (response.Failure == SwpcTransportFailure.NetworkError) (outcome, reason) = (SwpcFetchOutcome.TransientFailure, "network-error");
        else if (status >= 500) (outcome, reason) = (SwpcFetchOutcome.TransientFailure, "server-error");
        else if (status < 300 && status != 200) (outcome, reason) = (SwpcFetchOutcome.TransientFailure, "unexpected-status");
        else if (status == 304) (outcome, reason) = response.ValidatorsSent
            ? (SwpcFetchOutcome.NotModified, "not-modified") : (SwpcFetchOutcome.TransientFailure, "not-modified-unsolicited");
        else if (status == 429) (outcome, reason) = (stored.Checkpoint.ConsecutiveRateLimited >= SwpcSource.RateLimitedHaltCount - 1
            ? SwpcFetchOutcome.Halted : SwpcFetchOutcome.RateLimited, "rate-limited");
        else if (status == 403) (outcome, reason) = (SwpcFetchOutcome.Halted, "forbidden");
        else if (status < 400 && status != 200) (outcome, reason) = (SwpcFetchOutcome.Halted, "endpoint-moved");
        else if (status is >= 400 and < 500) (outcome, reason) = (SwpcFetchOutcome.Halted,
            status switch { 404 => "not-found", 410 => "gone", _ => "client-error" });
        else
        {
            SwpcParsedBody? parsed = null;
            var rejection = response.Oversize ? "oversize" : response.BodyUndecodable
                ? Rejection(Parse(start.Product, [], response.MediaType)) == "content-type" ? "content-type" : "encoding"
                : Rejection(parsed = Parse(start.Product, response.Body.Span, response.MediaType));
            if (rejection is not null) (outcome, reason) = (SwpcFetchOutcome.Rejected, "body-rejected:" + rejection);
            else
            {
                var (hash, stamp) = SnapshotIdentity(parsed!);
                candidate = new(start.Product, SwpcSource.RequestUri(start.Product).AbsolutePath,
                    start.StartedUtc, response.CompletedUtc, 200, response.MediaType!, response.Body.Length,
                    hash, stamp, new(response.EntityTag, response.LastModified), [.. response.Body.Span], parsed!);
                outcome = SwpcSnapshotComparison.Classify(stored.LastKnownGood, candidate);
                reason = OutcomeToken(outcome);
            }
        }
        var validatorOutcome = outcome is SwpcFetchOutcome.Updated or SwpcFetchOutcome.Unchanged ? SwpcValidatorOutcome.Replaced
            : outcome == SwpcFetchOutcome.NotModified ? SwpcValidatorOutcome.NotModified
            : !response.ValidatorsSent ? SwpcValidatorOutcome.NotSent : SwpcValidatorOutcome.Preserved;
        return new(start.AttemptId, start.ExpectedRevision, submittedUtc, response.CompletedUtc,
            status.HasValue ? response.CompletedUtc : null, outcome, status, reason,
            status == 200 ? response.Body.Length : 0, validatorOutcome, candidate);
    }

    /// <summary>
    /// P9: clears only the addressed halt, or returns null for NotHalted. Checkpoints
    /// cover only resumed products without a remaining halt. Resume takes the maximum
    /// of now, existing next, Retry-After and last attempt + 60 seconds, clears only
    /// RetryNotBeforeUtc, and preserves counters and InBackoff. No jitter is applied.
    /// </summary>
    public static SwpcHaltCommand? Resume(SwpcStoreView view, SwpcProduct? product, Guid commandId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(view);
        if ((product is { } p ? view.Products[p].ProductHalt : view.ProviderHalt) is null) return null;
        var checkpoints = ImmutableDictionary.CreateBuilder<SwpcProduct, SwpcFetchCheckpoint>();
        foreach (var (key, stored) in view.Products)
        {
            if (product is { } target && key != target) continue;
            if (product is null ? stored.ProductHalt is not null : view.ProviderHalt is not null) continue;
            var checkpoint = stored.Checkpoint;
            var next = Later(Later(Later(nowUtc, checkpoint.NextAttemptNotBeforeUtc), checkpoint.RetryNotBeforeUtc),
                stored.LastAttemptUtc + SwpcSource.MinimumAttemptSpacing);
            checkpoints[key] = checkpoint with { NextAttemptNotBeforeUtc = next, RetryNotBeforeUtc = null };
        }
        return new(commandId, view.Revision, nowUtc, new(product, null), checkpoints.ToImmutable());
    }

    /// <summary>The stable lower-case contract spelling of an outcome.</summary>
    public static string OutcomeToken(SwpcFetchOutcome outcome) => outcome switch
    {
        SwpcFetchOutcome.Updated => "updated",
        SwpcFetchOutcome.Unchanged => "unchanged",
        SwpcFetchOutcome.StaleSnapshot => "stale-snapshot",
        SwpcFetchOutcome.NotModified => "not-modified",
        SwpcFetchOutcome.Rejected => "rejected",
        SwpcFetchOutcome.TransientFailure => "transient-failure",
        SwpcFetchOutcome.RateLimited => "rate-limited",
        SwpcFetchOutcome.Halted => "halted",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    internal static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null || a >= b ? a : b;

    private static SwpcParsedBody Parse(SwpcProduct product, ReadOnlySpan<byte> body, string? mediaType) => product switch
    {
        SwpcProduct.Alerts => new SwpcAlertsBody(SwpcAlertsParser.Parse(body, mediaType)),
        SwpcProduct.Scales => new SwpcScalesBody(SwpcScalesParser.Parse(body, mediaType)),
        SwpcProduct.KpForecast => new SwpcKpBody(SwpcKpForecastParser.Parse(body, mediaType)),
        SwpcProduct.ThreeDayForecast => new SwpcThreeDayBody(SwpcThreeDayForecastParser.Parse(body, mediaType)),
        _ => throw new ArgumentOutOfRangeException(nameof(product))
    };

    private static string? Rejection(SwpcParsedBody body) => body switch
    {
        SwpcAlertsBody a => a.Value.RejectionReason,
        SwpcScalesBody s => s.Value.RejectionReason,
        SwpcKpBody k => k.Value.RejectionReason,
        SwpcThreeDayBody t => t.Value.RejectionReason,
        _ => throw new ArgumentOutOfRangeException(nameof(body))
    };

    private static (string Hash, DateTimeOffset? Stamp) SnapshotIdentity(SwpcParsedBody body) => body switch
    {
        SwpcAlertsBody a => (a.Value.SnapshotSha256!, a.Value.StampUtc),
        SwpcScalesBody s => (s.Value.SnapshotSha256!, s.Value.StampUtc),
        SwpcKpBody k => (k.Value.SnapshotSha256!, k.Value.StampUtc),
        SwpcThreeDayBody t => (t.Value.SnapshotSha256!, t.Value.StampUtc),
        _ => throw new ArgumentOutOfRangeException(nameof(body))
    };
}
