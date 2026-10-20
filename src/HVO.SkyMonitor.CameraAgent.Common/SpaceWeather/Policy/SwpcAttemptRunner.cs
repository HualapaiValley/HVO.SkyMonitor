using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;

/// <summary>P8/P10: one refused or attempted refresh and its committed view and local attention.</summary>
public sealed record SwpcRunResult(SwpcProduct Product, SwpcAttemptTrigger Trigger,
    SwpcRefusal? Refusal, Guid? AttemptId, SwpcFetchOutcome? Outcome, string ReasonCode,
    ImmutableArray<string> Diagnostics, ImmutableArray<SwpcAttentionEvent> Attention,
    SwpcStoreView View, SwpcWriteStatus? WriteStatus);

/// <summary>P9: a no-op NotHalted result, or the store's explicit resume result.</summary>
public sealed record SwpcResumeResult(bool NotHalted, SwpcWriteResult? Write, SwpcStoreView View);

/// <summary>
/// P10: single-flight, single-attempt orchestration with instance-owned recovery.
/// No logging, durable state, notice merge or freshness computation belongs here.
/// Dispose only after all operations have finished; the store and client remain caller-owned.
/// </summary>
public sealed class SwpcAttemptRunner : IDisposable
{
    private const int MaximumConflictRetries = 3;
    private const int JournalPageSize = 256;
    private readonly ISwpcStore store;
    private readonly ISwpcClient client;
    private readonly TimeProvider clock;
    private readonly ISwpcJitter jitter;
    private readonly Func<Guid> newId;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool settled;

    /// <summary>Uses injected storage, transport, time, jitter and optional immutable attempt identities.</summary>
    public SwpcAttemptRunner(ISwpcStore store, ISwpcClient client, TimeProvider timeProvider,
        ISwpcJitter jitter, Func<Guid>? idFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(jitter);
        this.store = store;
        this.client = client;
        clock = timeProvider;
        this.jitter = jitter;
        newId = idFactory ?? Guid.NewGuid;
    }

    /// <summary>
    /// P10: settles retained incomplete entries once per instance, before any run or resume.
    /// Reads at most 50,000/page-size + 1 pages, stopping when the health count is found
    /// or the journal ends. Settlements preserve the checkpoint and use settlement time
    /// for both AtUtc and EndedUtc (P1), with Preserved validators and no response facts.
    /// </summary>
    public async ValueTask SettleIncompleteAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await SettleCoreAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Runs P3 admission, one exchange, P4–P6 classification and P7 completion.
    /// Begin and completion each allow three conflict retries after the initial write.
    /// P2 samples once after admission and before Begin so invalid jitter writes nothing;
    /// the same sample survives revision retries. P1 uses admission t, exchange r and
    /// a separate clock reading at each completion submission. P8 unions S2 and store
    /// diagnostics ordinally and reports notice attention before provider attention.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every failed completion must attempt settlement; failed cleanup preserves the original exception.")]
    public async ValueTask<SwpcRunResult> RunAsync(SwpcProduct product, SwpcAttemptTrigger trigger,
        string runKey, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await SettleCoreAsync(ct).ConfigureAwait(false);
            var id = newId();
            SwpcAttemptStart? start = null;
            SwpcStoreView view;
            double? sample = null;
            for (var retry = 0; ; retry++)
            {
                ct.ThrowIfCancellationRequested();
                var t = Now();
                view = await store.ReadAsync(t, ct).ConfigureAwait(false);
                var admission = SwpcPolicy.Admit(view, product, trigger, runKey, id, t);
                if (admission.Refusal is { } refusal)
                    return Result(view, refusal, null, null, RefusalToken(refusal), null, [], []);
                sample ??= jitter.NextFraction(product);
                SwpcJitter.Validate(sample.Value);
                start = admission.Start!;
                var begin = await store.BeginAttemptAsync(start, ct).ConfigureAwait(false);
                view = begin.View;
                if (begin.Status == SwpcWriteStatus.Conflict && retry < MaximumConflictRetries) continue;
                if (begin.Status != SwpcWriteStatus.Applied)
                    return Result(view, null, id, null, begin.ReasonCode ?? (begin.Status switch
                    {
                        SwpcWriteStatus.Replayed => "replayed",
                        SwpcWriteStatus.NotFound => "not-found",
                        _ => "conflict"
                    }), begin.Status, begin.Diagnostics, begin.Attention);
                break;
            }
            SwpcHttpResponse response;
            SwpcAttemptCompletion completion;
            SwpcCommitProjection projection;
            try
            {
                ct.ThrowIfCancellationRequested();
                response = await client.FetchAsync(product, new(start.SentValidators.ETag, start.SentValidators.LastModified), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                completion = SwpcPolicy.Classify(start, view.Products[product], response, Now());
                projection = SwpcProjection.Create(product, start.StartedUtc, response.RetryAfter, sample!.Value);
            }
            catch
            {
                await TryInterruptAsync(start).ConfigureAwait(false);
                throw;
            }
            try
            {
                SwpcCommitEffects? appliedEffects = null;
                // This observer records only the pure factory's output for the result;
                // no captured observation participates in the projection's decision.
                SwpcCommitEffects Observe(SwpcCommitContext context) => appliedEffects = projection(context);
                for (var retry = 0; ; retry++)
                {
                    ct.ThrowIfCancellationRequested();
                    completion = completion with { ExpectedRevision = view.Revision, AtUtc = Now() };
                    appliedEffects = null;
                    var write = await store.CompleteAttemptAsync(completion, Observe, ct).ConfigureAwait(false);
                    if (write.Status == SwpcWriteStatus.Conflict)
                    {
                        if (retry == MaximumConflictRetries) throw new SwpcStorageException("Completion revision conflicts exhausted.");
                        view = await store.ReadAsync(Now(), ct).ConfigureAwait(false);
                        continue;
                    }
                    var diagnostics = write.Diagnostics.AsEnumerable();
                    if (completion.Outcome == SwpcFetchOutcome.Rejected || completion.Outcome == SwpcFetchOutcome.StaleSnapshot)
                        diagnostics = diagnostics.Append(completion.ReasonCode);
                    var attention = write.Status == SwpcWriteStatus.Applied && appliedEffects is not null
                        ? (appliedEffects.NoticeAttention?.Events ?? []).AddRange(appliedEffects.ProviderAttention)
                        : write.Attention;
                    return Result(write.View, null, id, completion.Outcome, completion.ReasonCode, write.Status,
                        [.. diagnostics], attention);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await TryInterruptAsync(start).ConfigureAwait(false);
                throw;
            }
            catch (Exception original)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
                    var settlement = await SettleAttemptAsync(start.AttemptId, product, start.StartedUtc,
                        "storage-failure", timeout.Token).ConfigureAwait(false);
                    if (settlement.Status is SwpcWriteStatus.Applied or SwpcWriteStatus.Replayed)
                        return Result(settlement.View, null, id, null, "storage-failure", settlement.Status, settlement.Diagnostics, settlement.Attention);
                }
                catch { /* Preserve the original completion failure and its stack. */ }
                ExceptionDispatchInfo.Capture(original).Throw();
                throw;
            }

            SwpcRunResult Result(SwpcStoreView resultView, SwpcRefusal? refusal, Guid? attemptId,
                SwpcFetchOutcome? outcome, string reason, SwpcWriteStatus? status,
                ImmutableArray<string> diagnostics, ImmutableArray<SwpcAttentionEvent> attention) =>
                new(product, trigger, refusal, attemptId, outcome, reason,
                    [.. diagnostics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], attention, resultView, status);
        }
        finally { gate.Release(); }
    }

    /// <summary>P9/P10: serializes explicit resume with runs and automatic startup recovery.</summary>
    public async ValueTask<SwpcResumeResult> ResumeAsync(SwpcProduct? product, Guid commandId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SettleCoreAsync(ct).ConfigureAwait(false);
            var now = Now();
            var view = await store.ReadAsync(now, ct).ConfigureAwait(false);
            var command = SwpcPolicy.Resume(view, product, commandId, now);
            if (command is null) return new(true, null, view);
            var result = await store.ChangeHaltAsync(command, ct).ConfigureAwait(false);
            return new(false, result, result.View);
        }
        finally { gate.Release(); }
    }

    /// <summary>Releases the instance-owned serialization gate after operations have finished.</summary>
    public void Dispose() => gate.Dispose();

    private DateTimeOffset Now() => clock.GetUtcNow().ToUniversalTime();

    private async ValueTask SettleCoreAsync(CancellationToken ct)
    {
        if (settled) return;
        var health = await store.ReadHealthAsync(Now(), ct).ConfigureAwait(false);
        var remaining = health.IncompleteAttempts;
        long? cursor = null;
        for (var page = 0; remaining > 0 && page < 50_000 / JournalPageSize + 1; page++)
        {
            var entries = await store.ReadDiagnosticsAsync(new(Now(), null, cursor, JournalPageSize), ct).ConfigureAwait(false);
            foreach (var entry in entries.Attempts)
            {
                if (entry.EndedUtc is not null) continue;
                var write = await SettleAttemptAsync(entry.AttemptId, entry.Product, entry.StartedUtc, "interrupted", ct).ConfigureAwait(false);
                if (write.Status is not (SwpcWriteStatus.Applied or SwpcWriteStatus.Replayed))
                    throw new SwpcStorageException("Startup settlement was not committed.");
                if (--remaining == 0) break;
            }
            if (entries.NextBeforeSequence is null) break;
            cursor = entries.NextBeforeSequence;
        }
        settled = true;
    }

    private async ValueTask<SwpcWriteResult> SettleAttemptAsync(Guid id, SwpcProduct product,
        DateTimeOffset startedUtc, string reason, CancellationToken ct)
    {
        var view = await store.ReadAsync(Now(), ct).ConfigureAwait(false);
        var now = Now();
        var completion = new SwpcAttemptCompletion(id, view.Revision, now, now, null, null, null, reason,
            0, SwpcValidatorOutcome.Preserved, null);
        return await store.CompleteAttemptAsync(completion, SwpcProjection.Create(product, startedUtc, null, 0), ct).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Settlement failure must not replace the original caller cancellation or client exception.")]
    private async ValueTask TryInterruptAsync(SwpcAttemptStart start)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            await SettleAttemptAsync(start.AttemptId, start.Product, start.StartedUtc, "interrupted", timeout.Token).ConfigureAwait(false);
        }
        catch { /* Cancellation/client failures retain the original exception even if settlement fails. */ }
    }

    private static string RefusalToken(SwpcRefusal refusal) => refusal switch
    {
        SwpcRefusal.Halted => "halted",
        SwpcRefusal.Backoff => "backoff",
        SwpcRefusal.MinInterval => "min-interval",
        SwpcRefusal.NotDue => "not-due",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal))
    };
}
