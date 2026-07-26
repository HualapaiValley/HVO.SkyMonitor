using System.Diagnostics;
using System.Threading.Channels;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalObservationDeliveryWakeup
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void Signal() => _channel.Writer.TryWrite(true);

    internal async ValueTask WaitAsync(TimeSpan pollInterval, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(pollInterval, timeProvider, timeout.Token);
        var wake = _channel.Reader.ReadAsync(timeout.Token).AsTask();
        var completed = await Task.WhenAny(delay, wake).ConfigureAwait(false);
        await timeout.CancelAsync().ConfigureAwait(false);
        if (completed == wake)
        {
            await wake.ConfigureAwait(false);
        }
    }
}

public enum EnvironmentalObservationDeliveryAvailability
{
    Initializing,
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record EnvironmentalObservationDeliveryStateSnapshot(
    EnvironmentalObservationDeliveryAvailability Availability,
    string Reason,
    EnvironmentalObservationOutboxSnapshot? Outbox,
    DateTimeOffset? LastAcknowledgedUtc);

public sealed class EnvironmentalObservationDeliveryState
{
    private EnvironmentalObservationDeliveryStateSnapshot _snapshot = new(
        EnvironmentalObservationDeliveryAvailability.Initializing,
        "initializing",
        null,
        null);

    public EnvironmentalObservationDeliveryStateSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Update(
        EnvironmentalObservationOutboxSnapshot outbox,
        EnvironmentalObservationDeliveryAvailability availability,
        string reason,
        DateTimeOffset? acknowledgedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        Volatile.Write(ref _snapshot, new(
            availability,
            Bound(reason),
            outbox,
            acknowledgedUtc ?? Snapshot.LastAcknowledgedUtc));
    }

    public void Fail(string reason)
    {
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with
        {
            Availability = EnvironmentalObservationDeliveryAvailability.Unhealthy,
            Reason = Bound(reason)
        });
    }

    public void Acknowledge(DateTimeOffset acknowledgedUtc)
    {
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with { LastAcknowledgedUtc = acknowledgedUtc });
    }

    private static string Bound(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return reason.Length <= 128 ? reason : reason[..128];
    }
}

public sealed class EnvironmentalObservationDeliveryService(
    IEnvironmentalObservationTransport transport,
    IEnvironmentalObservationTargetResolver targetResolver,
    IEnvironmentalObservationOutbox outbox,
    EnvironmentalObservationDeliveryWakeup wakeup,
    EnvironmentalObservationDeliveryState state,
    EnvironmentalObservationDeliveryTelemetry telemetry,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    ILogger<EnvironmentalObservationDeliveryService> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> Settled = LoggerMessage.Define<string>(
        LogLevel.Debug,
        new EventId(2510, nameof(Settled)),
        "Environmental delivery settled durable work with outcome {Outcome}");
    private static readonly Action<ILogger, string, long, Exception?> Retrying = LoggerMessage.Define<string, long>(
        LogLevel.Warning,
        new EventId(2511, nameof(Retrying)),
        "Environmental delivery scheduled bounded retry because {Reason} after {DelayMilliseconds} ms");
    private static readonly Action<ILogger, string, Exception?> OutboxUnavailable = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(2512, nameof(OutboxUnavailable)),
        "Environmental delivery durable outbox is unavailable because {ExceptionType}");
    private static readonly Action<ILogger, Exception?> LeaseLost = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2513, nameof(LeaseLost)),
        "Environmental delivery lease expired or changed before settlement");
    private readonly string _owner = string.Concat(Environment.MachineName, ":", Environment.ProcessId, ":", Guid.NewGuid().ToString("N"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return;
        }

        var delivery = options.Value.EnvironmentalDelivery;
        var root = options.Value.RawIngressRoot;
        var refreshInterval = TimeSpan.FromSeconds(delivery.PollIntervalSeconds);
        var lastRefresh = timeProvider.GetTimestamp();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = 0;
                if (delivery.Enabled)
                {
                    processed = await DrainBatchAsync(root, delivery, stoppingToken).ConfigureAwait(false);
                }
                if (processed < delivery.BatchSize || timeProvider.GetElapsedTime(lastRefresh) >= refreshInterval)
                {
                    var snapshot = await outbox.GetSnapshotAsync(root, stoppingToken).ConfigureAwait(false);
                    var capacityExhausted = snapshot.StoredCount >= delivery.MaximumPendingCount ||
                        snapshot.StoredBytes >= delivery.MaximumPendingBytes;
                    var availability = capacityExhausted
                        ? EnvironmentalObservationDeliveryAvailability.Unhealthy
                        : DetermineAvailability(snapshot);
                    state.Update(
                        snapshot,
                        availability,
                        capacityExhausted ? "capacity-exhausted" : delivery.Enabled ? "ready" : "disabled");
                    lastRefresh = timeProvider.GetTimestamp();
                }
                if (delivery.Enabled && processed == delivery.BatchSize)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (EnvironmentalObservationLeaseLostException exception)
            {
                var snapshot = await outbox.GetSnapshotAsync(root, stoppingToken).ConfigureAwait(false);
                state.Update(snapshot, EnvironmentalObservationDeliveryAvailability.Degraded, "lease-lost");
                LeaseLost(logger, exception);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or
                System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                state.Fail("durable-outbox-unavailable");
                OutboxUnavailable(logger, exception.GetType().Name, null);
            }
            await wakeup.WaitAsync(refreshInterval, timeProvider, stoppingToken)
                .ConfigureAwait(false);
        }
    }

    internal async ValueTask<int> DrainBatchAsync(
        string root,
        EnvironmentalObservationDeliveryOptions optionsValue,
        CancellationToken cancellationToken)
    {
        if (outbox is IEnvironmentalObservationProjectionStore projection)
        {
            var target = await targetResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
            if (target is not null && target.ObservatoryId != Guid.Empty && target.DevicePublicId != Guid.Empty)
            {
                _ = await projection.AssignUnprojectedAsync(
                    root, target, optionsValue.BatchSize, cancellationToken).ConfigureAwait(false);
            }
            _ = await projection.ProjectWaitingAsync(root, optionsValue.BatchSize, cancellationToken)
                .ConfigureAwait(false);
        }
        for (var index = 0; index < optionsValue.BatchSize; index++)
        {
            var claimStarted = timeProvider.GetTimestamp();
            var lease = await outbox.ClaimAsync(
                root,
                _owner,
                TimeSpan.FromSeconds(optionsValue.LeaseSeconds),
                cancellationToken).ConfigureAwait(false);
            telemetry.RecordClaim(timeProvider.GetElapsedTime(claimStarted));
            if (lease is null)
            {
                return index;
            }
            EnvironmentalObservationTransportResult result;
            var started = timeProvider.GetTimestamp();
            using var activity = EnvironmentalObservationDeliveryTelemetry.ActivitySource.StartActivity("environment.deliver");
            activity?.AddEvent(new ActivityEvent("claim"));
            activity?.AddEvent(new ActivityEvent("send"));
            try
            {
                result = await transport.SendAsync(lease.Record.Observation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                result = new(EnvironmentalObservationTransportDisposition.Retry, "transport-unavailable");
                RecordException(activity, exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                result = new(EnvironmentalObservationTransportDisposition.Retry, "request-timeout");
                RecordException(activity, exception);
            }
            activity?.SetTag("environment.outcome", result.Disposition.ToString());
            var reason = EnvironmentalObservationDeliveryTelemetry.NormalizeReason(result.Reason);
            activity?.SetTag("environment.reason", reason);
            telemetry.RecordSend(result, timeProvider.GetElapsedTime(started));
            if (result.Disposition == EnvironmentalObservationTransportDisposition.Acknowledged)
            {
                var acknowledgementStarted = timeProvider.GetTimestamp();
                await outbox.AcknowledgeAsync(
                    root,
                    lease,
                    result.Acknowledgement ?? throw new InvalidDataException("Acknowledged delivery omitted acknowledgement evidence."),
                    cancellationToken).ConfigureAwait(false);
                telemetry.RecordAcknowledgement(
                    timeProvider.GetElapsedTime(acknowledgementStarted),
                    lease.Record.AttemptCount > 1);
                activity?.SetStatus(ActivityStatusCode.Ok);
                var outcome = result.Acknowledgement!.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted
                    ? "accepted"
                    : "duplicate";
                telemetry.RecordSettlement(outcome, outcome);
                activity?.AddEvent(new ActivityEvent(outcome));
                state.Acknowledge(timeProvider.GetUtcNow());
                Settled(logger, outcome, null);
                continue;
            }
            if (result.Disposition == EnvironmentalObservationTransportDisposition.Quarantine)
            {
                await outbox.QuarantineAsync(root, lease, reason, cancellationToken).ConfigureAwait(false);
                telemetry.RecordSettlement("quarantine", reason);
                activity?.SetStatus(ActivityStatusCode.Error, reason);
                activity?.AddEvent(new ActivityEvent("quarantine"));
                Settled(logger, "quarantined", null);
                continue;
            }
            if (result.Disposition == EnvironmentalObservationTransportDisposition.Terminal)
            {
                await outbox.TerminalAsync(root, lease, reason, cancellationToken).ConfigureAwait(false);
                telemetry.RecordSettlement(reason == "http-conflict" ? "conflict" : "terminal", reason);
                activity?.SetStatus(ActivityStatusCode.Error, reason);
                activity?.AddEvent(new ActivityEvent(reason == "http-conflict" ? "conflict" : "terminal"));
                Settled(logger, "terminal", null);
                continue;
            }
            if (lease.Record.AttemptCount >= optionsValue.MaximumAttempts)
            {
                await outbox.TerminalAsync(root, lease, "maximum-attempts-exceeded", cancellationToken).ConfigureAwait(false);
                telemetry.RecordSettlement("terminal", "maximum-attempts-exceeded");
                activity?.SetStatus(ActivityStatusCode.Error, "maximum-attempts-exceeded");
                activity?.AddEvent(new ActivityEvent("terminal"));
                Settled(logger, "terminal", null);
                continue;
            }
            var maximum = TimeSpan.FromSeconds(optionsValue.RetryMaximumDelaySeconds);
            var suggested = result.RetryAfter ?? CalculateRetryDelay(
                lease.Record.AttemptCount,
                TimeSpan.FromSeconds(optionsValue.RetryInitialDelaySeconds),
                maximum);
            var minimum = TimeSpan.FromSeconds(optionsValue.RetryInitialDelaySeconds);
            var delay = TimeSpan.FromTicks(Math.Clamp(suggested.Ticks, minimum.Ticks, maximum.Ticks));
            await outbox.RetryAsync(root, lease, timeProvider.GetUtcNow() + delay, reason, cancellationToken)
                .ConfigureAwait(false);
            activity?.AddEvent(new ActivityEvent("retry-scheduled"));
            if (result.Disposition == EnvironmentalObservationTransportDisposition.AuthenticationBlocked)
            {
                state.Update(
                    await outbox.GetSnapshotAsync(root, cancellationToken).ConfigureAwait(false),
                    EnvironmentalObservationDeliveryAvailability.Degraded,
                    "authentication-blocked");
                activity?.SetStatus(ActivityStatusCode.Error, reason);
                Retrying(logger, reason, (long)delay.TotalMilliseconds, null);
                return index + 1;
            }
            activity?.SetStatus(ActivityStatusCode.Error, reason);
            Retrying(logger, reason, (long)delay.TotalMilliseconds, null);
        }
        return optionsValue.BatchSize;
    }

    internal static TimeSpan CalculateRetryDelay(int attempt, TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var multiplier = 1L << Math.Min(attempt - 1, 30);
        var ticks = initialDelay.Ticks > maximumDelay.Ticks / multiplier
            ? maximumDelay.Ticks
            : initialDelay.Ticks * multiplier;
        return TimeSpan.FromTicks(Math.Min(ticks, maximumDelay.Ticks));
    }

    internal static EnvironmentalObservationDeliveryAvailability DetermineAvailability(
        EnvironmentalObservationOutboxSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.QuarantineCount > 0 || snapshot.TerminalCount > 0
            ? EnvironmentalObservationDeliveryAvailability.Unhealthy
            : snapshot.RetryCount > 0 || snapshot.PendingCount > 0
                ? EnvironmentalObservationDeliveryAvailability.Degraded
                : EnvironmentalObservationDeliveryAvailability.Healthy;
    }

    private static void RecordException(Activity? activity, Exception exception)
    {
        activity?.AddEvent(new ActivityEvent(
            "exception",
            tags: new ActivityTagsCollection { { "exception.type", exception.GetType().FullName } }));
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
    }
}
