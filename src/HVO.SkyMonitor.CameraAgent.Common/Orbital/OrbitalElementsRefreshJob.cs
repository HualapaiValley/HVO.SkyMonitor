using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// Runs one orbital-element refresh occurrence: provider admission, one bounded request, normalization, and either
/// immutable publication with atomic activation or a durable halt, transport failure or quarantine record.
/// </summary>
/// <remarks>
/// <para>
/// Provider policy (halt, two-hour floor, single flight) is enforced by the store, which commits the attempt row and
/// the floor before any request is made. This job never retries a request. A successful run applies retention on a
/// best-effort basis and never fails because of it.
/// </para>
/// <para>
/// Liveness: once an attempt is admitted it is closed with a terminal record on every path while this process keeps
/// running, including cancellation and unexpected exceptions; otherwise the store's single-flight rule would refuse
/// every later attempt until a restart. Closing uses its own short deadline. A halt is never downgraded to another
/// outcome: if a halt cannot be recorded the attempt stays in flight, which still refuses further requests, and
/// restart reconciliation settles it as interrupted.
/// </para>
/// <para>Every execution detail is <c>{reason code}: {bounded text}</c> and carries no secret, path or header value.</para>
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The single-flight gate never materializes a wait handle and lives for the host's lifetime.")]
public sealed partial class OrbitalElementsRefreshJob
{
    /// <summary>The largest floor shortfall the job waits out precisely instead of skipping the occurrence.</summary>
    public static readonly TimeSpan MaximumPrecisionWait = TimeSpan.FromSeconds(120);

    /// <summary>A run-now occurrence queued longer ago than this is skipped rather than surprising the operator.</summary>
    public static readonly TimeSpan ManualRequestLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The deadline for closing an admitted attempt after cancellation or an unexpected failure.</summary>
    internal static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    internal const string DisabledReasonCode = "orbital.disabled";
    internal const string ManualRequestExpiredReasonCode = "orbital.manual-request-expired";
    internal const string ProviderHaltedReasonCode = "orbital.provider-halted";
    internal const string FloorNotElapsedReasonCode = "orbital.floor-not-elapsed";
    internal const string AttemptInFlightReasonCode = "orbital.attempt-in-flight";
    internal const string ClientErrorReasonCode = "orbital.client-error";
    internal const string InvalidHttpStatusReasonCode = "orbital.invalid-http-status";
    internal const string CandidateRejectedReasonCode = "orbital.candidate-rejected";
    internal const string AttemptCancelledReasonCode = "orbital.attempt-cancelled";
    internal const string InternalErrorReasonCode = "orbital.internal-error";
    internal const string ActivatedReasonCode = "orbital.activated";
    internal const string UnchangedPayloadReasonCode = "orbital.unchanged-payload";

    private readonly IOrbitalElementsStore _store;
    private readonly IOrbitalElementsProviderClient _client;
    private readonly IOptions<CameraAgentHostOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly OrbitalElementsTelemetry? _telemetry;
    private readonly ILogger<OrbitalElementsRefreshJob> _logger;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "No wait handle is ever materialized; the job lives for the host's lifetime.")]
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _activity;

    public OrbitalElementsRefreshJob(
        IOrbitalElementsStore store,
        IOrbitalElementsProviderClient client,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        OrbitalElementsTelemetry? telemetry,
        ILogger<OrbitalElementsRefreshJob> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _client = client;
        _options = options;
        _timeProvider = timeProvider;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Runs one occurrence under the job's single-flight lock. Caller cancellation is rethrown after any admitted
    /// attempt has been closed; every other outcome is returned as a bounded automation execution.
    /// </summary>
    public async ValueTask<LocalAutomationExecution> ExecuteAsync(
        string runKey,
        OrbitalAttemptTrigger trigger,
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _activity);
        try
        {
            return await ExecuteCoreAsync(runKey, trigger, scheduledForUtc, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Increment(ref _activity);
            _gate.Release();
        }
    }

    /// <summary>
    /// An activity generation that is odd while an occurrence is executing and advances on every start and finish. A
    /// durable in-flight attempt observed between two equal, even generations was left unsettled by a failed close,
    /// because only this process can own the store root and no occurrence ran across the read.
    /// </summary>
    internal long ActivityGeneration => Interlocked.Read(ref _activity);

    /// <summary>Whether an in-flight attempt read between <paramref name="before"/> and <paramref name="after"/> is unsettled.</summary>
    internal static bool IsIdleAcross(long before, long after) => before == after && (before & 1) == 0;

    private async ValueTask<LocalAutomationExecution> ExecuteCoreAsync(
        string runKey,
        OrbitalAttemptTrigger trigger,
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.OrbitalElements.Enabled)
        {
            return Skipped(DisabledReasonCode, "orbital elements refresh is disabled by configuration");
        }
        if (trigger == OrbitalAttemptTrigger.Manual && _timeProvider.GetUtcNow() - scheduledForUtc > ManualRequestLifetime)
        {
            return Skipped(ManualRequestExpiredReasonCode, string.Create(CultureInfo.InvariantCulture,
                $"the manual refresh requested at {scheduledForUtc.UtcDateTime:O} is older than 15 minutes"));
        }

        OrbitalAttemptAdmission admission;
        try
        {
            admission = await AdmitAsync(runKey, trigger, cancellationToken).ConfigureAwait(false);
        }
        catch (OrbitalStorageException exception)
        {
            AdmissionFailed(_logger, exception.ReasonCode, exception);
            return Failed(exception.ReasonCode, "the orbital store could not admit the attempt; no request was made");
        }
        catch (InvalidDataException exception)
        {
            AdmissionFailed(_logger, OrbitalElementsAvailability.IntegrityFailureReasonCode, exception);
            return Failed(OrbitalElementsAvailability.IntegrityFailureReasonCode,
                "the orbital store failed verification; no request was made");
        }

        switch (admission.Status)
        {
            case OrbitalAdmissionStatus.Halted:
                return Skipped(ProviderHaltedReasonCode, admission.Halt is { } halt
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"provider halted since {halt.SinceUtc.UtcDateTime:O} after HTTP {halt.HttpStatusCode}; an operator resume is required")
                    : "provider halted; an operator resume is required");
            case OrbitalAdmissionStatus.FloorNotElapsed:
                return Skipped(FloorNotElapsedReasonCode, admission.NextPermittedAttemptUtc is { } next
                    ? string.Create(CultureInfo.InvariantCulture, $"next permitted attempt {next.UtcDateTime:O}")
                    : "the provider attempt floor has not elapsed");
            case OrbitalAdmissionStatus.AttemptInFlight:
                return Skipped(AttemptInFlightReasonCode, "another orbital attempt is in flight");
            case OrbitalAdmissionStatus.Admitted when admission.AttemptId is { } attemptId:
                return await RunAdmittedAsync(attemptId, trigger, cancellationToken).ConfigureAwait(false);
            default:
                return Failed(InternalErrorReasonCode, "the orbital store returned an unusable admission");
        }
    }

    /// <summary>
    /// Admits once and, when the floor is short by at most <see cref="MaximumPrecisionWait"/>, waits exactly that
    /// long on the injected clock and admits once more. A periodic boundary that slipped by a poll interval then
    /// still runs instead of losing a whole period.
    /// </summary>
    private async ValueTask<OrbitalAttemptAdmission> AdmitAsync(
        string runKey, OrbitalAttemptTrigger trigger, CancellationToken cancellationToken)
    {
        var admission = await _store.TryBeginAttemptAsync(runKey, trigger, cancellationToken).ConfigureAwait(false);
        if (admission.Status != OrbitalAdmissionStatus.FloorNotElapsed ||
            admission.NextPermittedAttemptUtc is not { } next)
        {
            return admission;
        }
        var shortfall = next - _timeProvider.GetUtcNow();
        if (shortfall > MaximumPrecisionWait)
        {
            return admission;
        }
        if (shortfall > TimeSpan.Zero)
        {
            var wait = RoundUpToMillisecond(shortfall);
            PrecisionWait(_logger, (long)wait.TotalMilliseconds);
            await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        return await _store.TryBeginAttemptAsync(runKey, trigger, cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "An admitted attempt must be closed on every path; the failure is classified and recorded.")]
    private async ValueTask<LocalAutomationExecution> RunAdmittedAsync(
        string attemptId, OrbitalAttemptTrigger trigger, CancellationToken cancellationToken)
    {
        AttemptStarted(_logger, attemptId, trigger.ToString());
        var context = new AttemptContext(attemptId);
        LocalAutomationExecution execution;
        try
        {
            execution = await RunAttemptAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var code = cancelled
                ? AttemptCancelledReasonCode
                : exception switch
                {
                    OrbitalStorageException storage => storage.ReasonCode,
                    InvalidDataException => OrbitalElementsAvailability.IntegrityFailureReasonCode,
                    _ => InternalErrorReasonCode
                };
            var detail = cancelled
                ? "the attempt was cancelled before it completed"
                : string.Concat("exception=", exception.GetType().Name);
            if (!cancelled)
            {
                AttemptFaulted(_logger, attemptId, code, exception);
            }
            // The store itself settles cancellation and storage faults raised inside quarantine and publication.
            OrbitalAttemptOutcome? settledByStore = cancelled
                ? OrbitalAttemptOutcome.Interrupted
                : exception is OrbitalStorageException ? OrbitalAttemptOutcome.StorageFailed : null;
            await CloseAsync(attemptId, context.Pending, code, detail, settledByStore).ConfigureAwait(false);
            if (cancelled)
            {
                throw;
            }
            execution = Failed(code, exception is OrbitalStorageException
                ? "orbital storage could not complete durably; the last-known-good snapshot remains active"
                : string.Concat("the attempt failed unexpectedly and was closed; ", detail));
        }
        await ObserveBestEffortAsync(cancellationToken).ConfigureAwait(false);
        return execution;
    }

    private async ValueTask<LocalAutomationExecution> RunAttemptAsync(AttemptContext context, CancellationToken cancellationToken)
    {
        var attemptId = context.AttemptId;
        var fetch = await FetchAsync(cancellationToken).ConfigureAwait(false);
        switch (fetch.Disposition)
        {
            case OrbitalFetchDisposition.HttpStatusHalt when fetch.HttpStatusCode is { } status && IsHaltStatus(status):
                {
                    var reason = string.Create(CultureInfo.InvariantCulture, $"provider returned HTTP {status}; {fetch.Detail}");
                    context.Pending = PendingSettlement.Halt(status, reason);
                    // Not cancellable by design: a lost halt would permit another request to a refusing provider.
                    await _store.RecordHaltAsync(attemptId, status, reason, CancellationToken.None).ConfigureAwait(false);
                    return Settled(OrbitalAttemptOutcome.ProviderHalted, Failed(
                        string.Create(CultureInfo.InvariantCulture, $"orbital.provider-http-{status}"),
                        string.Create(CultureInfo.InvariantCulture,
                            $"provider returned HTTP {status}; refresh is halted provider-wide until an operator resumes it")));
                }
            case OrbitalFetchDisposition.HttpStatusHalt:
                context.Pending = PendingSettlement.Quarantine(ReadOnlyMemory<byte>.Empty, InvalidHttpStatusReasonCode,
                    string.Concat("status=", fetch.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none"));
                return await QuarantineAsync(context, cancellationToken).ConfigureAwait(false);
            case OrbitalFetchDisposition.TransportFailure or OrbitalFetchDisposition.TimedOut:
                context.Pending = PendingSettlement.Transport(fetch.ReasonCode, fetch.Detail);
                await _store.RecordTransportFailureAsync(attemptId, fetch.ReasonCode, fetch.Detail, cancellationToken)
                    .ConfigureAwait(false);
                return Settled(OrbitalAttemptOutcome.TransportFailed, Failed(fetch.ReasonCode, string.Concat(
                    fetch.Disposition == OrbitalFetchDisposition.TimedOut ? "the provider request timed out" : "the provider request failed",
                    "; the last-known-good snapshot is kept and no retry is made before the next permitted attempt; ",
                    fetch.Detail)));
            case OrbitalFetchDisposition.ResponseRejected:
                context.Pending = PendingSettlement.Quarantine(ReadOnlyMemory<byte>.Empty, fetch.ReasonCode, fetch.Detail);
                return await QuarantineAsync(context, cancellationToken).ConfigureAwait(false);
            case OrbitalFetchDisposition.Retrieved when fetch.RawBytes is { } raw:
                return await NormalizeAndPublishAsync(context, fetch, raw, cancellationToken).ConfigureAwait(false);
            default:
                context.Pending = PendingSettlement.Quarantine(ReadOnlyMemory<byte>.Empty, ClientErrorReasonCode,
                    string.Concat("disposition=", fetch.Disposition.ToString(), " without-raw-bytes"));
                return await QuarantineAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<OrbitalFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        var startedUtc = _timeProvider.GetUtcNow();
        OrbitalFetchResult fetch;
        try
        {
            fetch = await _client.FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A client defect is not provider evidence: it closes the attempt as a transport failure.
            ClientFaulted(_logger, exception);
            fetch = new OrbitalFetchResult(OrbitalFetchDisposition.TransportFailure, null, startedUtc, _timeProvider.GetUtcNow(),
                null, null, 0, null, null, null, ClientErrorReasonCode, string.Concat("exception=", exception.GetType().Name));
        }
        _telemetry?.RecordFetch(fetch.Disposition, _timeProvider.GetElapsedTime(started),
            fetch.Disposition == OrbitalFetchDisposition.Retrieved ? fetch.RawBytes?.LongLength : null);
        return fetch;
    }

    private async ValueTask<LocalAutomationExecution> NormalizeAndPublishAsync(
        AttemptContext context, OrbitalFetchResult fetch, byte[] raw, CancellationToken cancellationToken)
    {
        var candidate = CelesTrakGpNormalizer.CreateCandidate(fetch);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.Snapshot is not { } snapshot)
        {
            context.Pending = PendingSettlement.Quarantine(raw,
                candidate.RejectionReasonCode ?? CandidateRejectedReasonCode, candidate.Detail);
            return await QuarantineAsync(context, cancellationToken).ConfigureAwait(false);
        }
        OrbitalPublication publication;
        try
        {
            publication = await _store.PublishAndActivateAsync(context.AttemptId, snapshot, raw, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            // The candidate did not bind to its own bytes; the store left the attempt in flight for this record.
            context.Pending = PendingSettlement.Quarantine(raw, CandidateRejectedReasonCode, string.Concat(
                "exception=", exception.GetType().Name, "; parameter=", exception.ParamName ?? "none"));
            return await QuarantineAsync(context, cancellationToken).ConfigureAwait(false);
        }
        var outcome = publication.PayloadUnchanged ? OrbitalAttemptOutcome.UnchangedPayload : OrbitalAttemptOutcome.Activated;
        var execution = Settled(outcome, Succeeded(
            publication.PayloadUnchanged ? UnchangedPayloadReasonCode : ActivatedReasonCode,
            string.Create(CultureInfo.InvariantCulture,
                $"manifest={publication.ManifestSha256} payload={publication.PayloadSha256} records={publication.RecordCount} objects={publication.ObjectCount}")));
        await ApplyRetentionBestEffortAsync(cancellationToken).ConfigureAwait(false);
        return execution;
    }

    private async ValueTask<LocalAutomationExecution> QuarantineAsync(AttemptContext context, CancellationToken cancellationToken)
    {
        var quarantine = context.Pending;
        await _store.RecordQuarantineAsync(context.AttemptId, quarantine.RawBytes, null, quarantine.ReasonCode!,
            quarantine.Detail!, cancellationToken).ConfigureAwait(false);
        return Settled(OrbitalAttemptOutcome.Quarantined, Failed(quarantine.ReasonCode!, string.Concat(
            "the provider response was quarantined and never activated; the last-known-good snapshot is kept; ",
            quarantine.Detail)));
    }


    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Retention is best-effort after a durable activation and must never fail the run.")]
    private async ValueTask ApplyRetentionBestEffortAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retention = await _store.ApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
            RetentionApplied(_logger, retention.Retired, retention.FilesDeleted, retention.Protected, retention.Refused);
        }
        catch (Exception exception)
        {
            RetentionFailed(_logger, exception.GetType().Name, exception);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Publishing a gauge snapshot is best-effort and must never change a run's outcome.")]
    private async ValueTask ObserveBestEffortAsync(CancellationToken cancellationToken)
    {
        if (_telemetry is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            _telemetry.Observe(await _store.GetStateAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            ObserveFailed(_logger, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Records a terminal outcome for an admitted attempt that has not reached one, under its own deadline. The
    /// pending record is retried first; a halt is never replaced by another outcome. An attempt that is already
    /// terminal (the store settles cancellation and storage faults during quarantine and publication) is left as is.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Closing is best-effort after a failure; every failure is logged and restart reconciliation remains.")]
    private async ValueTask CloseAsync(
        string attemptId,
        PendingSettlement pending,
        string fallbackCode,
        string fallbackDetail,
        OrbitalAttemptOutcome? alreadySettledOutcome)
    {
        using var deadline = new CancellationTokenSource(CloseTimeout, _timeProvider);
        var fallback = PendingSettlement.Transport(fallbackCode, fallbackDetail);
        PendingSettlement[] attempts = pending.Kind switch
        {
            PendingKind.Halt => [pending],
            PendingKind.Quarantine or PendingKind.Transport => [pending, fallback],
            _ => [fallback]
        };
        foreach (var settlement in attempts)
        {
            try
            {
                var outcome = await SettleAsync(attemptId, settlement, deadline.Token).ConfigureAwait(false);
                AttemptClosed(_logger, attemptId, outcome.ToString(), settlement.ReasonCode ?? fallbackCode);
                _telemetry?.RecordAttempt(outcome);
                return;
            }
            catch (InvalidOperationException invalid) when (invalid is not ObjectDisposedException)
            {
                // The store already recorded a terminal outcome (or no longer knows the attempt); nothing to close.
                if (alreadySettledOutcome is { } settled)
                {
                    _telemetry?.RecordAttempt(settled);
                }
                return;
            }
            catch (Exception exception)
            {
                CloseFailed(_logger, attemptId, settlement.Kind.ToString(), exception);
                if (exception is ObjectDisposedException)
                {
                    return;
                }
            }
        }
        AttemptLeftInFlight(_logger, attemptId);
    }

    private async ValueTask<OrbitalAttemptOutcome> SettleAsync(
        string attemptId, PendingSettlement settlement, CancellationToken cancellationToken)
    {
        switch (settlement.Kind)
        {
            case PendingKind.Halt:
                await _store.RecordHaltAsync(attemptId, settlement.HttpStatusCode, settlement.Detail!, CancellationToken.None)
                    .ConfigureAwait(false);
                return OrbitalAttemptOutcome.ProviderHalted;
            case PendingKind.Quarantine:
                await _store.RecordQuarantineAsync(attemptId, settlement.RawBytes, null, settlement.ReasonCode!,
                    settlement.Detail!, cancellationToken).ConfigureAwait(false);
                return OrbitalAttemptOutcome.Quarantined;
            default:
                await _store.RecordTransportFailureAsync(attemptId, settlement.ReasonCode!, settlement.Detail!,
                    cancellationToken).ConfigureAwait(false);
                return OrbitalAttemptOutcome.TransportFailed;
        }
    }

    private LocalAutomationExecution Settled(OrbitalAttemptOutcome outcome, LocalAutomationExecution execution)
    {
        _telemetry?.RecordAttempt(outcome);
        AttemptSettled(_logger, outcome.ToString(), execution.Outcome.ToString());
        return execution;
    }

    internal static bool IsHaltStatus(int status) => status is >= 100 and <= 599 and not 200;

    internal static TimeSpan RoundUpToMillisecond(TimeSpan value)
    {
        var remainder = value.Ticks % TimeSpan.TicksPerMillisecond;
        return remainder == 0 ? value : TimeSpan.FromTicks(value.Ticks - remainder + TimeSpan.TicksPerMillisecond);
    }

    private static LocalAutomationExecution Skipped(string code, string text)
        => new(LocalAutomationRunOutcome.Skipped, Describe(code, text));

    private static LocalAutomationExecution Failed(string code, string text)
        => new(LocalAutomationRunOutcome.Failed, Describe(code, text));

    private static LocalAutomationExecution Succeeded(string code, string text)
        => new(LocalAutomationRunOutcome.Succeeded, Describe(code, text));

    /// <summary><c>{code}: {text}</c>, control characters replaced, trimmed, and bounded to the run-detail limit.</summary>
    internal static string Describe(string code, string text)
    {
        var value = SqliteOrbitalElementsStore.BoundText(string.Concat(code, ": ", text)).Trim();
        var maximum = LocalAutomationContract.MaximumDetailLength;
        if (value.Length > maximum)
        {
            value = value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)];
        }
        return value;
    }

    private enum PendingKind { None, Halt, Transport, Quarantine }

    /// <summary>The admitted attempt and the terminal record it was about to make when a failure interrupted it.</summary>
    private sealed class AttemptContext(string attemptId)
    {
        public string AttemptId { get; } = attemptId;

        public PendingSettlement Pending { get; set; }
    }

    /// <summary>The terminal record an admitted attempt was about to make, retried if the first try fails.</summary>
    private readonly record struct PendingSettlement(
        PendingKind Kind, int HttpStatusCode, ReadOnlyMemory<byte> RawBytes, string? ReasonCode, string? Detail)
    {
        public static PendingSettlement None => default;

        public static PendingSettlement Halt(int status, string reason)
            => new(PendingKind.Halt, status, ReadOnlyMemory<byte>.Empty,
                string.Create(CultureInfo.InvariantCulture, $"orbital.provider-http-{status}"), reason);

        public static PendingSettlement Transport(string reasonCode, string detail)
            => new(PendingKind.Transport, 0, ReadOnlyMemory<byte>.Empty, reasonCode, detail);

        public static PendingSettlement Quarantine(ReadOnlyMemory<byte> raw, string reasonCode, string detail)
            => new(PendingKind.Quarantine, 0, raw, reasonCode, detail);
    }

    [LoggerMessage(7740, LogLevel.Information, "Orbital elements attempt {AttemptId} admitted ({Trigger}).")]
    private static partial void AttemptStarted(ILogger logger, string attemptId, string trigger);

    [LoggerMessage(7741, LogLevel.Information, "Orbital elements attempt settled as {AttemptOutcome}; run {RunOutcome}.")]
    private static partial void AttemptSettled(ILogger logger, string attemptOutcome, string runOutcome);

    [LoggerMessage(7742, LogLevel.Information,
        "Orbital elements attempt floor is {WaitMilliseconds} ms away; waiting precisely before admitting once more.")]
    private static partial void PrecisionWait(ILogger logger, long waitMilliseconds);

    [LoggerMessage(7743, LogLevel.Warning, "Orbital elements store could not admit an attempt ({ReasonCode}); no request was made.")]
    private static partial void AdmissionFailed(ILogger logger, string reasonCode, Exception exception);

    [LoggerMessage(7744, LogLevel.Warning, "Orbital elements provider client threw instead of returning a disposition.")]
    private static partial void ClientFaulted(ILogger logger, Exception exception);

    [LoggerMessage(7745, LogLevel.Warning, "Orbital elements attempt {AttemptId} failed ({ReasonCode}); closing it.")]
    private static partial void AttemptFaulted(ILogger logger, string attemptId, string reasonCode, Exception exception);

    [LoggerMessage(7746, LogLevel.Information, "Orbital elements attempt {AttemptId} closed as {AttemptOutcome} ({ReasonCode}).")]
    private static partial void AttemptClosed(ILogger logger, string attemptId, string attemptOutcome, string reasonCode);

    [LoggerMessage(7747, LogLevel.Error, "Orbital elements attempt {AttemptId} could not be closed with its {Settlement} record.")]
    private static partial void CloseFailed(ILogger logger, string attemptId, string settlement, Exception exception);

    [LoggerMessage(7748, LogLevel.Error,
        "Orbital elements attempt {AttemptId} remains in flight; requests stay refused until it is repaired or the host restarts.")]
    private static partial void AttemptLeftInFlight(ILogger logger, string attemptId);

    [LoggerMessage(7749, LogLevel.Information,
        "Orbital elements retention retired {Retired} snapshots, deleted {FilesDeleted} files, protected {Protected} and refused {Refused}.")]
    private static partial void RetentionApplied(ILogger logger, int retired, int filesDeleted, int @protected, int refused);

    [LoggerMessage(7750, LogLevel.Warning, "Orbital elements retention did not complete ({ExceptionType}); the activation stands.")]
    private static partial void RetentionFailed(ILogger logger, string exceptionType, Exception exception);

    [LoggerMessage(7751, LogLevel.Debug, "Orbital elements gauge snapshot was not refreshed ({ExceptionType}).")]
    private static partial void ObserveFailed(ILogger logger, string exceptionType);
}
