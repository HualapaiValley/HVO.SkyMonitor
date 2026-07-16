using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Fleet;

public sealed record FleetAgentEndpoint(
    Guid AgentInstanceId,
    string DeviceId,
    string DeviceKey,
    string HeartbeatEndpoint,
    int HeartbeatIntervalSeconds);

public enum FleetDeliveryDisposition
{
    Acknowledged,
    Retry,
    Quarantine,
    Blocked
}

public sealed record FleetDeliveryResult(
    FleetDeliveryDisposition Disposition,
    string Reason,
    FleetHeartbeatAcknowledgement? Acknowledgement = null,
    TimeSpan? RetryAfter = null);

public interface IFleetHeartbeatTransport
{
    ValueTask<FleetAgentEndpoint?> GetEndpointAsync(CancellationToken cancellationToken);
    ValueTask<FleetDeliveryResult> SendAsync(
        FleetAgentEndpoint endpoint,
        FleetStatusReportV1 report,
        CancellationToken cancellationToken);
}

public sealed record FleetHeartbeatStateSnapshot(
    FleetAvailability Availability,
    string Reason,
    FleetStatusOutboxSnapshot? Outbox,
    DateTimeOffset? LastAcknowledgedUtc);

public sealed class FleetHeartbeatState
{
    private FleetHeartbeatStateSnapshot _snapshot = new(FleetAvailability.Initializing, "initializing", null, null);

    public FleetHeartbeatStateSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Update(FleetStatusOutboxSnapshot outbox, FleetAvailability availability, string reason, DateTimeOffset? acknowledgedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Volatile.Write(ref _snapshot, new FleetHeartbeatStateSnapshot(
            availability,
            reason.Length <= 128 ? reason : reason[..128],
            outbox,
            acknowledgedUtc ?? Snapshot.LastAcknowledgedUtc));
    }

    public void Fail(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var snapshot = Snapshot;
        Volatile.Write(ref _snapshot, snapshot with
        {
            Availability = FleetAvailability.Unavailable,
            Reason = reason.Length <= 128 ? reason : reason[..128]
        });
    }
}

public sealed class FleetHeartbeatService(
    IFleetHeartbeatTransport transport,
    IFleetStatusOutbox outbox,
    FleetStatusCollector collector,
    FleetHeartbeatState state,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    FleetHeartbeatTelemetry telemetry,
    ILogger<FleetHeartbeatService> logger) : BackgroundService
{
    private static readonly Action<ILogger, long, bool, Exception?> ReportQueued = LoggerMessage.Define<long, bool>(
        LogLevel.Debug, new EventId(2301, nameof(ReportQueued)),
        "Fleet status sequence {Sequence} queued; transition={Transition}");
    private static readonly Action<ILogger, Exception?> QueueFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(2302, nameof(QueueFailed)),
        "Fleet heartbeat durable queue operation failed");
    private static readonly Action<ILogger, long, string, Exception?> ReportQuarantined = LoggerMessage.Define<long, string>(
        LogLevel.Warning, new EventId(2303, nameof(ReportQuarantined)),
        "Fleet status sequence {Sequence} quarantined because {Reason}");
    private static readonly Action<ILogger, long, long, string, Exception?> RetryScheduled = LoggerMessage.Define<long, long, string>(
        LogLevel.Warning, new EventId(2304, nameof(RetryScheduled)),
        "Fleet status sequence {Sequence} retry scheduled after {DelayMilliseconds} ms because {Reason}");
    private readonly Guid _bootSessionId = Guid.NewGuid();
    private readonly DateTimeOffset _processStartedAtUtc = timeProvider.GetUtcNow();
    private readonly string _owner = string.Concat(Environment.MachineName, ":", Environment.ProcessId, ":", Guid.NewGuid().ToString("N"));
    private string? _lastFingerprint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = options.Value.RawIngressRoot;
        while (!stoppingToken.IsCancellationRequested)
        {
            FleetAgentEndpoint? endpoint = null;
            try
            {
                endpoint = await transport.GetEndpointAsync(stoppingToken).ConfigureAwait(false);
                if (endpoint is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), timeProvider, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                await DrainBatchAsync(root, endpoint, 100, stoppingToken).ConfigureAwait(false);
                var before = await outbox.GetSnapshotAsync(root, stoppingToken).ConfigureAwait(false);
                FleetStatusReportV1? candidate = null;
                using (FleetHeartbeatTelemetry.ActivitySource.StartActivity("fleet.collect"))
                {
                    candidate = await collector.CollectAsync(
                        endpoint.AgentInstanceId, _bootSessionId, 1, _processStartedAtUtc, before, stoppingToken).ConfigureAwait(false);
                }
                var fingerprint = FleetStatusCollector.ComputeStateFingerprint(candidate);
                var transition = !string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal);
                var enqueueStarted = timeProvider.GetTimestamp();
                FleetStatusReportV1 report;
                using (FleetHeartbeatTelemetry.ActivitySource.StartActivity("fleet.enqueue"))
                {
                    report = await outbox.EnqueueAsync(
                        root,
                        endpoint.AgentInstanceId,
                        sequence => candidate with { Sequence = sequence },
                        fingerprint,
                        transition,
                        stoppingToken).ConfigureAwait(false);
                }
                telemetry.RecordQueued(FleetContractJson.Serialize(report).Length, transition, timeProvider.GetElapsedTime(enqueueStarted));
                _lastFingerprint = fingerprint;
                await DrainBatchAsync(root, endpoint, 100, stoppingToken).ConfigureAwait(false);
                var snapshot = await outbox.GetSnapshotAsync(root, stoppingToken).ConfigureAwait(false);
                var (availability, reason) = ResolveAvailability(snapshot, state.Snapshot);
                state.Update(snapshot, availability, reason);
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    ReportQueued(logger, report.Sequence, transition, null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (FleetStatusOutboxCapacityException exception)
            {
                state.Fail("capacity-exhausted");
                QueueFailed(logger, exception);
                try
                {
                    if (endpoint is not null)
                    {
                        await DrainBatchAsync(root, endpoint, 100, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception recoveryException) when (recoveryException is IOException or InvalidDataException or
                    InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                {
                    state.Fail("durable-queue-unavailable");
                    QueueFailed(logger, recoveryException);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or
                System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                state.Fail("durable-queue-unavailable");
                QueueFailed(logger, exception);
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(endpoint?.HeartbeatIntervalSeconds ?? 10, 10, 3600)),
                timeProvider,
                stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask DrainBatchAsync(
        string root,
        FleetAgentEndpoint endpoint,
        int maximumReports,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < maximumReports; index++)
        {
            if (!await DrainOneAsync(root, endpoint, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async ValueTask<bool> DrainOneAsync(string root, FleetAgentEndpoint endpoint, CancellationToken cancellationToken)
    {
        var lease = await outbox.ClaimAsync(root, _owner, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }
        FleetDeliveryResult result;
        try
        {
            var started = timeProvider.GetTimestamp();
            using (FleetHeartbeatTelemetry.ActivitySource.StartActivity("fleet.send"))
            {
                result = await transport.SendAsync(endpoint, lease.Record.Report, cancellationToken).ConfigureAwait(false);
            }
            telemetry.RecordDelivery(result.Disposition, timeProvider.GetElapsedTime(started));
        }
        catch (HttpRequestException exception)
        {
            result = new FleetDeliveryResult(FleetDeliveryDisposition.Retry, exception.GetType().Name);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new FleetDeliveryResult(FleetDeliveryDisposition.Retry, "request-timeout");
        }
        if (result.Disposition == FleetDeliveryDisposition.Acknowledged)
        {
            var acknowledgement = result.Acknowledgement
                ?? throw new InvalidDataException("Successful fleet delivery omitted its acknowledgement.");
            await outbox.AcknowledgeAsync(root, lease, acknowledgement, cancellationToken).ConfigureAwait(false);
            state.Update(
                await outbox.GetSnapshotAsync(root, cancellationToken).ConfigureAwait(false),
                FleetAvailability.Available,
                "acknowledged",
                timeProvider.GetUtcNow());
            return true;
        }
        if (result.Disposition == FleetDeliveryDisposition.Quarantine)
        {
            await outbox.QuarantineAsync(root, lease, result.Reason, cancellationToken).ConfigureAwait(false);
            ReportQuarantined(logger, lease.Record.Report.Sequence, result.Reason, null);
            return true;
        }
        var configuredDelay = result.RetryAfter ?? CalculateRetryDelay(lease.Record.AttemptCount);
        var delay = TimeSpan.FromTicks(Math.Min(configuredDelay.Ticks, TimeSpan.FromMinutes(5).Ticks));
        await outbox.RetryAsync(root, lease, timeProvider.GetUtcNow() + delay, result.Reason, cancellationToken).ConfigureAwait(false);
        if (result.Disposition == FleetDeliveryDisposition.Blocked)
        {
            state.Fail("credentials-blocked");
        }
        RetryScheduled(
            logger,
            lease.Record.Report.Sequence,
            (long)delay.TotalMilliseconds,
            result.Reason,
            null);
        return false;
    }

    private static TimeSpan CalculateRetryDelay(int attempt)
        => TimeSpan.FromSeconds(Math.Min(300, 5 * (1L << Math.Min(Math.Max(attempt - 1, 0), 6))));

    internal static (FleetAvailability Availability, string Reason) ResolveAvailability(
        FleetStatusOutboxSnapshot snapshot,
        FleetHeartbeatStateSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(current);
        if (snapshot.BlockedCount > 0 || current.Availability == FleetAvailability.Unavailable &&
            string.Equals(current.Reason, "credentials-blocked", StringComparison.Ordinal) &&
            snapshot.RetryCount > 0)
        {
            return (FleetAvailability.Unavailable, "credentials-blocked");
        }
        return snapshot.QuarantineCount > 0 || snapshot.OverflowCount > 0
            ? (FleetAvailability.Unavailable, snapshot.QuarantineCount > 0 ? "quarantined" : "overflow-evidence")
            : snapshot.RetryCount > 0
                ? (FleetAvailability.Degraded, "backlogged")
                : (FleetAvailability.Available, "ready");
    }
}
