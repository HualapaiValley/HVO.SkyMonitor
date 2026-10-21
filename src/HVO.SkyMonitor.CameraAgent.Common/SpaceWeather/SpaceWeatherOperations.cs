using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

/// <summary>
/// The owner operations contract. Commands share the host gate; status and notices use ungated timed
/// reads. A result includes current status when storage can be read. No provider URL or validator escapes.
/// </summary>
public sealed class SpaceWeatherOperations
{
    /// <summary>The bounded operator-command disposition.</summary>
    public enum Disposition { Accepted, Applied, Replayed, Conflict, Invalid, NotFound, Unavailable }
    /// <summary>UTC round-trip halt identity, suitable for echoing exactly in a resume request.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "These DTOs belong to the single operations contract exposed to the host boundary.")]
    public sealed record HaltStatus(string ReasonCode, string SinceUtc);
    /// <summary>One product's redacted policy and freshness state.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "These DTOs belong to the single operations contract exposed to the host boundary.")]
    public sealed record ProductStatus(string Product, bool Configured, string? LastOutcome, string Freshness, DateTimeOffset? LastSuccessUtc,
        DateTimeOffset? InFileStampUtc, DateTimeOffset? NextAttemptNotBeforeUtc, DateTimeOffset? RetryNotBeforeUtc,
        int ConsecutiveTransientFailures, int ConsecutiveRateLimited, int ConsecutiveRejected, HaltStatus? Halt);
    /// <summary>The local read model, including storage and reserved automation availability.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "These DTOs belong to the single operations contract exposed to the host boundary.")]
    public sealed record Status(DateTimeOffset ObservedUtc, bool Enabled, bool StoreAvailable, long Revision,
        IReadOnlyList<ProductStatus> Products, HaltStatus? ProviderHalt, bool DefinitionExists, bool DefinitionEnabled, string Credit);
    /// <summary>The current result; a retry need not reproduce its original response.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "These DTOs belong to the single operations contract exposed to the host boundary.")]
    public sealed record CommandResult(Disposition Disposition, string Outcome, string ReasonCode, Status Status);
    /// <summary>One lifecycle projection, independent of acknowledgement.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "These DTOs belong to the single operations contract exposed to the host boundary.")]
    public sealed record Notice(string Group, string Family, string? Level, string State, DateTimeOffset FromUtc,
        DateTimeOffset UntilUtc, string? EndReason, string LatestIdentity, int VersionCount, bool Corrected, bool Orphan,
        SwpcAcknowledgement? Acknowledgement, bool MayBeOutOfDate);
    /// <summary>Exclusive ordinal cursor over retained notice identities.</summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "These DTOs belong to the single operations contract exposed to the host boundary.")]
    public sealed record NoticePage(IReadOnlyList<Notice> Notices, string? NextCursor);

    public const string Credit = "Space weather data: NOAA Space Weather Prediction Center (SWPC). Displayed by HVO SkyMonitor; not endorsed by NOAA/NWS. Local relevance is derived by HVO SkyMonitor and is not an official forecast.";
    private readonly ISwpcStore store;
    private readonly SwpcAttemptRunner runner;
    private readonly SpaceWeatherAvailability availability;
    private readonly SpaceWeatherWriteGate gate;
    private readonly SpaceWeatherRefreshJob job;
    private readonly ILocalAutomationStore automation;
    private readonly IOptions<CameraAgentHostOptions> options;
    private readonly TimeProvider clock;
    private readonly ILogger<SpaceWeatherOperations> logger;

    internal SpaceWeatherOperations(ISwpcStore store, SwpcAttemptRunner runner, SpaceWeatherAvailability availability,
        SpaceWeatherWriteGate gate, SpaceWeatherRefreshJob job, ILocalAutomationStore automation,
        IOptions<CameraAgentHostOptions> options, TimeProvider clock, ILogger<SpaceWeatherOperations> logger)
    {
        this.store = store; this.runner = runner; this.availability = availability; this.gate = gate; this.job = job;
        this.automation = automation; this.options = options; this.clock = clock; this.logger = logger;
    }

    /// <summary>Reads even while disabled; storage failures return the unavailable read model.</summary>
    public async ValueTask<Status> GetStatusAsync(CancellationToken ct)
    {
        var now = Now();
        var definitions = await automation.GetStateAsync(ct).ConfigureAwait(false);
        var definition = FindDefinition(definitions);
        if (!await availability.EnsureAvailableAsync(ct).ConfigureAwait(false)) return BuildStatus(null, now, definition);
        try { return BuildStatus(await store.ReadAsync(now, ct).ConfigureAwait(false), now, definition); }
        catch (Exception exception) when (exception is SwpcStorageException or InvalidDataException)
        { availability.RecordFailure(exception); return BuildStatus(null, now, definition); }
    }

    /// <summary>
    /// Pages pending, active, recent or history notices; null state selects all retained groups.
    /// Each call projects the current view. Concurrent merge, expiry or purge can change membership
    /// or group identity between pages; the exclusive ordinal cursor is not a retained snapshot.
    /// </summary>
    public async ValueTask<NoticePage> GetNoticesAsync(string? state, string? cursor, int limit = 25, CancellationToken ct = default)
    {
        if (limit is < 1 or > 50 || state is not (null or "pending" or "active" or "recent" or "history") ||
            cursor is not null && !LocalAutomationDefinitionValidator.IsText(cursor, 32))
            throw new ArgumentException("space-weather.invalid-paging");
        if (!await availability.EnsureAvailableAsync(ct).ConfigureAwait(false)) throw new SwpcStorageException("Space-weather store unavailable.");
        var now = Now();
        var view = await store.ReadAsync(now, ct).ConfigureAwait(false);
        var notices = view.Notices.Groups.OrderBy(static group => group.Identity, StringComparer.Ordinal)
            .Where(group => cursor is null || StringComparer.Ordinal.Compare(group.Identity, cursor) > 0)
            .Select(group => Project(group, view, now)).Where(notice => state is null || notice.State == state)
            .Take(limit + 1).ToArray();
        return new(notices.Take(limit).ToArray(), notices.Length > limit ? notices[limit - 1].Group : null);
    }

    /// <summary>Queues through the automation store's string-key replay seam; never performs a provider attempt.</summary>
    public async ValueTask<CommandResult> RefreshAsync(string idempotencyKey, string actor, CancellationToken ct)
    {
        if (!ValidKey(idempotencyKey)) return await ResultAsync(Disposition.Invalid, "space-weather.invalid-idempotency-key", ct).ConfigureAwait(false);
        if (!LocalAutomationDefinitionValidator.IsText(actor, LocalAutomationContract.MaximumActorLength))
            return await ResultAsync(Disposition.Invalid, "space-weather.invalid-command", ct).ConfigureAwait(false);
        var definition = FindDefinition(await automation.GetStateAsync(ct).ConfigureAwait(false));
        if (definition is not null && automation is ILocalAutomationOccurrenceStore replayStore &&
            await replayStore.FindRunNowReplayAsync(Request(definition), ct).ConfigureAwait(false) is { } replay)
            return await MapRefreshAsync(replay.Status, replay.ReasonCode, ct).ConfigureAwait(false);
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.StoreAvailable) return new(Disposition.Unavailable, "unavailable", SpaceWeatherAvailability.StoreUnavailableReasonCode, status);
        if (!status.Enabled) return new(Disposition.Conflict, "rejected", "space-weather.disabled", status);
        if (definition is null) return new(Disposition.Conflict, "rejected", "space-weather.definition-missing", status);
        if (!definition.Definition.Enabled) return new(Disposition.Conflict, "rejected", "space-weather.definition-disabled", status);
        if (status.ProviderHalt is not null) return new(Disposition.Conflict, "rejected", "space-weather.halted", status);
        if (automation is not ILocalAutomationOccurrenceStore occurrences)
            return new(Disposition.Unavailable, "unavailable", LocalAutomationContract.RunNowUnavailableReasonCode, status);
        var queued = await occurrences.RunNowAsync(Request(definition), ct).ConfigureAwait(false);
        return await MapRefreshAsync(queued.Status, queued.ReasonCode, ct).ConfigureAwait(false);

        LocalAutomationRunNowRequest Request(LocalAutomationDefinitionState d) => new(d.Definition.DefinitionId, d.Version,
            idempotencyKey, actor, "space-weather.refresh");
    }

    /// <summary>
    /// Clears only the named halt instance, comparing exact UTC ticks under the host command gate.
    /// A key is not applied twice while its receipt is retained: 35 days and at most the 10,000 most
    /// recent halt/acknowledgement receipts. An old expected halt never clears a later halt, even after eviction.
    /// Revision conflicts write no receipt and may be retried with the same key. There is no internal retry.
    /// </summary>
    public async ValueTask<CommandResult> ResumeAsync(string? scope, string? product, DateTimeOffset? expectedHaltSinceUtc,
        string idempotencyKey, CancellationToken ct)
    {
        if (!ValidKey(idempotencyKey)) return await RejectAsync(Disposition.Invalid, "space-weather.invalid-idempotency-key", ct).ConfigureAwait(false);
        if (expectedHaltSinceUtc is null || expectedHaltSinceUtc.Value.Offset != TimeSpan.Zero)
            return await RejectAsync(Disposition.Invalid, "space-weather.resume-invalid-halt-time", ct).ConfigureAwait(false);
        SwpcProduct? target = Enum.GetValues<SwpcProduct>().Cast<SwpcProduct?>().FirstOrDefault(p => p!.Value.Token() == product);
        if (!(scope == "provider" && product is null || scope == "product" && target is { } p && options.Value.SpaceWeather.Products.Contains(p)))
            return await RejectAsync(Disposition.Invalid, "space-weather.resume-invalid-scope", ct).ConfigureAwait(false);
        if (scope == "provider") target = null;
        if (!await gate.TryEnterAsync(clock, ct).ConfigureAwait(false))
            return await RejectAsync(Disposition.Unavailable, "space-weather.busy", ct).ConfigureAwait(false);
        try
        {
            if (!await availability.EnsureAvailableAsync(ct).ConfigureAwait(false))
                return await RejectAsync(Disposition.Unavailable, SpaceWeatherAvailability.StoreUnavailableReasonCode, ct).ConfigureAwait(false);
            var view = await store.ReadAsync(Now(), ct).ConfigureAwait(false);
            var halt = target is { } selected ? view.Products[selected].ProductHalt : view.ProviderHalt;
            if (halt is null) return await ResultAsync(Disposition.Applied, "not-halted", ct).ConfigureAwait(false);
            if (halt.SinceUtc.UtcTicks != expectedHaltSinceUtc.Value.UtcTicks)
                return await RejectAsync(Disposition.Conflict, "space-weather.resume-halt-changed", ct).ConfigureAwait(false);
            await gate.ObserveStartupAsync(store, clock, logger, ct).ConfigureAwait(false);
            var result = await runner.ResumeAsync(target, CommandId("resume", idempotencyKey), ct).ConfigureAwait(false);
            if (result.NotHalted) return await ResultAsync(Disposition.Applied, "not-halted", ct).ConfigureAwait(false);
            var (disposition, reason) = MapWrite(result.Write!, "resumed");
            if (disposition == Disposition.Applied)
            {
                SpaceWeatherLog.Resumed(logger, scope!, target?.Token() ?? "none");
                await job.PublishStateAsync(ct).ConfigureAwait(false);
            }
            return await CommandResultAsync(disposition, reason, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SwpcStorageException or InvalidDataException)
        {
            availability.RecordFailure(exception);
            return await RejectAsync(Disposition.Unavailable, SpaceWeatherAvailability.StoreUnavailableReasonCode, ct).ConfigureAwait(false);
        }
        finally { gate.Exit(); }
    }

    /// <summary>
    /// Records server time and authenticated actor on a retained group. A key is not applied twice while
    /// its receipt is retained: 35 days and at most the 10,000 most recent halt/acknowledgement receipts.
    /// A later retry may return key-reused rather than the original response. Revision conflicts write no
    /// receipt and may be retried with the same key; no internal retry or durable command cache is added.
    /// </summary>
    public async ValueTask<CommandResult> AcknowledgeAsync(string? group, string actor, string idempotencyKey, CancellationToken ct)
    {
        if (!ValidKey(idempotencyKey)) return await RejectAsync(Disposition.Invalid, "space-weather.invalid-idempotency-key", ct).ConfigureAwait(false);
        if (!LocalAutomationDefinitionValidator.IsText(group, 32) || !LocalAutomationDefinitionValidator.IsText(actor, 128))
            return await RejectAsync(Disposition.Invalid, "space-weather.invalid-command", ct).ConfigureAwait(false);
        if (!await gate.TryEnterAsync(clock, ct).ConfigureAwait(false))
            return await RejectAsync(Disposition.Unavailable, "space-weather.busy", ct).ConfigureAwait(false);
        try
        {
            if (!await availability.EnsureAvailableAsync(ct).ConfigureAwait(false))
                return await RejectAsync(Disposition.Unavailable, SpaceWeatherAvailability.StoreUnavailableReasonCode, ct).ConfigureAwait(false);
            var now = Now();
            var view = await store.ReadAsync(now, ct).ConfigureAwait(false);
            var result = await store.AcknowledgeAsync(new(CommandId("acknowledge", idempotencyKey), view.Revision,
                now, group!, new(actor, now)), ct).ConfigureAwait(false);
            var (disposition, reason) = MapWrite(result, "acknowledged");
            if (disposition == Disposition.Applied)
            {
                SpaceWeatherLog.Acknowledged(logger, group!);
                await job.PublishStateAsync(ct).ConfigureAwait(false);
            }
            return await CommandResultAsync(disposition, reason, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SwpcStorageException or InvalidDataException)
        {
            availability.RecordFailure(exception);
            return await RejectAsync(Disposition.Unavailable, SpaceWeatherAvailability.StoreUnavailableReasonCode, ct).ConfigureAwait(false);
        }
        finally { gate.Exit(); }
    }

    /// <summary>Records the host boundary's antiforgery rejection without request content.</summary>
    public void RecordAntiforgeryRejection(bool resumeOrAcknowledge)
    {
        SpaceWeatherLog.Antiforgery(logger);
        if (resumeOrAcknowledge) SpaceWeatherLog.Rejected(logger, "space-weather.antiforgery-invalid");
    }

    /// <summary>Records a rejected resume/acknowledge header with the fixed key reason only.</summary>
    public void RecordInvalidKeyRejection() => SpaceWeatherLog.Rejected(logger, "space-weather.invalid-idempotency-key");

    /// <summary>Records a rejected resume/acknowledge actor without disclosing identity.</summary>
    public void RecordInvalidActorRejection() => SpaceWeatherLog.Rejected(logger, "space-weather.invalid-command");

    /// <summary>Shares the unique generated storage event with the host boundary; only type is logged.</summary>
    public void RecordBoundaryFailure(Exception exception, bool resumeOrAcknowledge = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        SpaceWeatherLog.Storage(logger, exception.GetType().Name);
        if (resumeOrAcknowledge) SpaceWeatherLog.Rejected(logger, SpaceWeatherAvailability.StoreUnavailableReasonCode);
    }

    /// <summary>D1: first 128 SHA-256 bits with RFC 9562 UUIDv8 version/variant bits and big-endian Guid layout.</summary>
    internal static Guid CommandId(string kind, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("hvo.skymonitor/space-weather/" + kind + "\n" + key));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    internal static bool ValidKey(string? key) => LocalAutomationDefinitionValidator.IsText(key, LocalAutomationContract.MaximumIdempotencyKeyLength);
    internal static (Disposition Disposition, string Reason) MapWrite(SwpcWriteResult write, string applied) => write.Status switch
    {
        SwpcWriteStatus.Applied => (Disposition.Applied, applied),
        SwpcWriteStatus.Replayed => (Disposition.Replayed, "replayed"),
        SwpcWriteStatus.NotFound => (Disposition.NotFound, "space-weather.unknown-notice"),
        _ => (Disposition.Conflict, write.ReasonCode == "space-weather.idempotency-conflict"
            ? "space-weather.idempotency-key-reused" : "space-weather.revision-conflict")
    };

    private async ValueTask<CommandResult> MapRefreshAsync(LocalAutomationCommandStatus status, string? code, CancellationToken ct)
    {
        var (disposition, reason) = status switch
        {
            LocalAutomationCommandStatus.Applied => (Disposition.Accepted, "space-weather.refresh-queued"),
            LocalAutomationCommandStatus.Replayed or LocalAutomationCommandStatus.Unchanged => (Disposition.Replayed, "space-weather.refresh-replayed"),
            LocalAutomationCommandStatus.NotFound => (Disposition.Conflict, "space-weather.definition-missing"),
            LocalAutomationCommandStatus.Invalid => (Disposition.Invalid, code ?? LocalAutomationContract.InvalidCommandReasonCode),
            _ => (Disposition.Conflict, code ?? LocalAutomationContract.RunNowUnavailableReasonCode)
        };
        if (disposition is Disposition.Accepted or Disposition.Replayed) SpaceWeatherLog.Refresh(logger, disposition.ToString());
        return await ResultAsync(disposition, reason, ct).ConfigureAwait(false);
    }

    private ValueTask<CommandResult> CommandResultAsync(Disposition disposition, string reason, CancellationToken ct)
        => disposition is Disposition.Conflict or Disposition.Invalid or Disposition.NotFound or Disposition.Unavailable
            ? RejectAsync(disposition, reason, ct) : ResultAsync(disposition, reason, ct);
    private ValueTask<CommandResult> RejectAsync(Disposition disposition, string reason, CancellationToken ct)
    { SpaceWeatherLog.Rejected(logger, reason); return ResultAsync(disposition, reason, ct); }
    private async ValueTask<CommandResult> ResultAsync(Disposition disposition, string reason, CancellationToken ct)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.StoreAvailable && reason == "space-weather.busy") reason = SpaceWeatherAvailability.StoreUnavailableReasonCode;
        return new(disposition, disposition == Disposition.Accepted ? "queued" : disposition == Disposition.Replayed ? "replayed" : reason.StartsWith("space-weather.", StringComparison.Ordinal) ? "rejected" : reason, reason, status);
    }
    private Status BuildStatus(SwpcStoreView? view, DateTimeOffset now, LocalAutomationDefinitionState? definition)
        => new(now, options.Value.SpaceWeather.Enabled, view is not null, view?.Revision ?? 0,
            view is null ? [] : Enum.GetValues<SwpcProduct>().Select(product =>
            {
                var stored = view.Products[product]; var checkpoint = stored.Checkpoint;
                return new ProductStatus(product.Token(), options.Value.SpaceWeather.Products.Contains(product), stored.LastOutcome is { } outcome ? SwpcPolicy.OutcomeToken(outcome) : null,
                    FreshnessToken(stored.Freshness), stored.LastSuccessUtc, stored.LastKnownGood?.InFileStampUtc,
                    checkpoint.NextAttemptNotBeforeUtc, checkpoint.RetryNotBeforeUtc, checkpoint.ConsecutiveTransientFailures,
                    checkpoint.ConsecutiveRateLimited, checkpoint.ConsecutiveRejected, Halt(stored.ProductHalt));
            }).ToArray(), Halt(view?.ProviderHalt), definition is not null, definition?.Definition.Enabled ?? false, Credit);
    private static string FreshnessToken(SwpcFreshness freshness) => freshness switch
    {
        SwpcFreshness.None => "none",
        SwpcFreshness.Fresh => "fresh",
        SwpcFreshness.Stale => "stale",
        SwpcFreshness.Outdated => "outdated",
        _ => throw new ArgumentOutOfRangeException(nameof(freshness))
    };
    private static HaltStatus? Halt(SwpcHalt? halt) => halt is null ? null : new(halt.ReasonCode, halt.SinceUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    private static LocalAutomationDefinitionState? FindDefinition(LocalAutomationOperatorState state) => state.Definitions.SingleOrDefault(static d =>
        d.Definition.DefinitionId == SpaceWeatherAutomationAdapter.DefinitionId && d.Definition.TaskKind == LocalAutomationTaskKind.SpaceWeatherRefresh);
    private static Notice Project(SwpcNoticeGroup group, SwpcStoreView view, DateTimeOffset now)
    {
        var window = SwpcNoticeLifecycle.Evaluate(group, view.Notices.Groups, now);
        return new(group.Identity, group.Family.Token(), group.Level, window.State.Token(), window.FromUtc, window.UntilUtc,
            window.EndReason?.Token(), group.Latest.Identity, group.Versions.Length, group.Latest.Header.Corrected, group.Orphan,
            SwpcGroupBuilder.Acknowledgement(group, view.Notices.Acknowledgements),
            window.State is SwpcNoticeState.Pending or SwpcNoticeState.Active && view.Products[SwpcProduct.Alerts].Freshness != SwpcFreshness.Fresh);
    }
    private DateTimeOffset Now() => clock.GetUtcNow().ToUniversalTime();
}
