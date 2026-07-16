using System.Data;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IDeviceHeartbeatService
{
    Task<FleetHeartbeatAcknowledgement> RecordHeartbeatAsync(
        string deviceId,
        string deviceKey,
        FleetStatusReportV1 report,
        CancellationToken cancellationToken = default);
}

internal sealed class DeviceHeartbeatService(
    IDeviceCredentialValidator credentialValidator,
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<FleetStatusOptions> options,
    FleetStatusTelemetry telemetry,
    ILogger<DeviceHeartbeatService> logger) : IDeviceHeartbeatService
{
    private static readonly Action<ILogger, Guid, long, long, Exception?> SequenceGap = LoggerMessage.Define<Guid, long, long>(
        LogLevel.Warning, new EventId(2401, nameof(SequenceGap)),
        "Fleet heartbeat sequence gap for registration {RegistrationId}: expected {ExpectedSequence}, received {ActualSequence}");
    private static readonly Action<ILogger, Guid, Guid, Exception?> BootSessionChanged = LoggerMessage.Define<Guid, Guid>(
        LogLevel.Information, new EventId(2402, nameof(BootSessionChanged)),
        "Fleet heartbeat boot session changed for registration {RegistrationId} to {BootSessionId}");
    private static readonly Action<ILogger, Guid, FleetHealth, Exception?> HealthChanged = LoggerMessage.Define<Guid, FleetHealth>(
        LogLevel.Information, new EventId(2403, nameof(HealthChanged)),
        "Fleet reported health changed for registration {RegistrationId} to {ReportedHealth}");
    private static readonly Action<ILogger, int, Exception?> DeadlockRetry = LoggerMessage.Define<int>(
        LogLevel.Warning, new EventId(2406, nameof(DeadlockRetry)),
        "Fleet heartbeat transaction deadlocked; retrying bounded attempt {Attempt}");

    public async Task<FleetHeartbeatAcknowledgement> RecordHeartbeatAsync(
        string deviceId,
        string deviceKey,
        FleetStatusReportV1 report,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RecordHeartbeatOnceAsync(deviceId, deviceKey, report, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < 4 && IsDeadlock(exception))
            {
                dbContext.ChangeTracker.Clear();
                DeadlockRetry(logger, attempt, exception);
                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<FleetHeartbeatAcknowledgement> RecordHeartbeatOnceAsync(
        string deviceId,
        string deviceKey,
        FleetStatusReportV1 report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var validation = FleetContractJson.Validate(report);
        if (!validation.IsValid)
        {
            throw new FleetHeartbeatConflictException($"Invalid fleet status report ({validation.ReasonCode}:{validation.FieldPath}).");
        }
        var registration = await credentialValidator.ValidateAsync(deviceId, deviceKey, cancellationToken).ConfigureAwait(false);
        if (registration.DevicePublicId is null)
        {
            throw new DeviceRegistrationException("Device is not fully activated. Complete bootstrap before sending heartbeats.");
        }
        if (registration.DevicePublicId.Value != report.AgentInstanceId)
        {
            throw new FleetHeartbeatConflictException("The fleet report identity does not match the authenticated registration.");
        }

        var now = timeProvider.GetUtcNow();
        var clockDiagnostic = ClockDiagnostic(report.ObservedAtUtc, now);
        var payloadSha256 = FleetContractJson.ComputeSha256(report);
        var payload = FleetContractJson.Serialize(report);
        var payloadJson = Encoding.UTF8.GetString(payload);
        var fingerprint = ComputeStatusFingerprint(report);
        var started = timeProvider.GetTimestamp();
        using var activity = FleetStatusTelemetry.ActivitySource.StartActivity("fleet.ingest");
        IDbContextTransaction? transaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
                var lockResource = $"fleet-heartbeat:{registration.Id:N}";
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock
                        @Resource = {lockResource},
                        @LockMode = 'Exclusive',
                        @LockOwner = 'Transaction',
                        @LockTimeout = 10000;
                    IF @result < 0
                        THROW 51005, 'Could not acquire the fleet heartbeat registration lock.', 1;
                    """, cancellationToken).ConfigureAwait(false);
                var lockedRegistration = await dbContext.DeviceRegistrations
                    .FromSqlInterpolated($"SELECT * FROM [DeviceRegistrations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {registration.Id}")
                    .AsNoTracking()
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
                dbContext.Entry(registration).State = EntityState.Detached;
                dbContext.DeviceRegistrations.Attach(lockedRegistration);
                registration = await credentialValidator.ValidateAsync(deviceId, deviceKey, cancellationToken).ConfigureAwait(false);
            }

            var existing = await dbContext.DeviceHeartbeatRecords
                .SingleOrDefaultAsync(record =>
                    record.RegistrationId == registration.Id &&
                    record.AgentInstanceId == report.AgentInstanceId &&
                    record.Sequence == report.Sequence,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.PayloadSha256, payloadSha256, StringComparison.Ordinal))
                {
                    throw new FleetHeartbeatConflictException("A different payload already exists for this fleet sequence.");
                }
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                telemetry.RecordIngest(FleetHeartbeatDisposition.Duplicate, payload.Length, (report.ObservedAtUtc - now).TotalSeconds, clockDiagnostic, timeProvider.GetElapsedTime(started));
                return Acknowledgement(report, FleetHeartbeatDisposition.Duplicate, now);
            }

            var current = await dbContext.DeviceFleetStates
                .SingleOrDefaultAsync(state => state.RegistrationId == registration.Id, cancellationToken)
                .ConfigureAwait(false);
            if (current is not null && current.AgentInstanceId != report.AgentInstanceId)
            {
                throw new FleetHeartbeatConflictException("The registration is already bound to a different agent instance.");
            }
            if (current is not null && report.Sequence == current.Sequence)
            {
                if (!string.Equals(current.CurrentPayloadSha256, payloadSha256, StringComparison.Ordinal))
                {
                    throw new FleetHeartbeatConflictException("A different payload is current for this fleet sequence.");
                }
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                telemetry.RecordIngest(FleetHeartbeatDisposition.Duplicate, payload.Length, (report.ObservedAtUtc - now).TotalSeconds, clockDiagnostic, timeProvider.GetElapsedTime(started));
                return Acknowledgement(report, FleetHeartbeatDisposition.Duplicate, now);
            }
            var advances = current is null || report.Sequence > current.Sequence;
            var significant = current is null ||
                !string.Equals(current.StatusFingerprint, fingerprint, StringComparison.Ordinal) ||
                current.BootSessionId != report.BootSessionId ||
                report.Sequence > current.Sequence + 1;
            dbContext.DeviceHeartbeatRecords.Add(new DeviceHeartbeatRecord
            {
                RegistrationId = registration.Id,
                AgentInstanceId = report.AgentInstanceId,
                BootSessionId = report.BootSessionId,
                Sequence = report.Sequence,
                ObservedAtUtc = report.ObservedAtUtc,
                ReceivedAtUtc = now,
                PayloadSha256 = payloadSha256,
                StatusFingerprint = fingerprint,
                ReportedHealth = report.OverallHealth,
                ClockDiagnostic = clockDiagnostic,
                AdvancedCurrent = advances,
                IsSignificantSnapshot = significant,
                SnapshotJson = significant ? payloadJson : null
            });

            if (advances)
            {
                var expectedSequence = current is null ? 1 : current.Sequence + 1;
                var gap = report.Sequence > expectedSequence;
                var sessionChanged = current is not null && current.BootSessionId != report.BootSessionId;
                var healthChanged = current is not null && current.ReportedHealth != report.OverallHealth;
                if (current is null)
                {
                    current = new DeviceFleetState { RegistrationId = registration.Id };
                    dbContext.DeviceFleetStates.Add(current);
                }
                ApplyCurrent(current, report, payloadJson, fingerprint, now, clockDiagnostic, gap, sessionChanged);
                registration.LastSeenUtc = now;
                if (gap)
                {
                    SequenceGap(logger, registration.Id, expectedSequence, report.Sequence, null);
                }
                if (sessionChanged)
                {
                    BootSessionChanged(logger, registration.Id, report.BootSessionId, null);
                }
                if (healthChanged)
                {
                    HealthChanged(logger, registration.Id, report.OverallHealth, null);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            var disposition = advances ? FleetHeartbeatDisposition.Advanced : FleetHeartbeatDisposition.Historical;
            telemetry.RecordIngest(disposition, payload.Length, (report.ObservedAtUtc - now).TotalSeconds, clockDiagnostic, timeProvider.GetElapsedTime(started));
            return Acknowledgement(
                report,
                disposition,
                now);
        }
        catch (DbUpdateException) when (dbContext.Database.IsRelational())
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
            var winner = await dbContext.DeviceHeartbeatRecords
                .AsNoTracking()
                .SingleOrDefaultAsync(record =>
                    record.RegistrationId == registration.Id &&
                    record.AgentInstanceId == report.AgentInstanceId &&
                    record.Sequence == report.Sequence,
                    cancellationToken)
                .ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }
            if (!string.Equals(winner.PayloadSha256, payloadSha256, StringComparison.Ordinal))
            {
                throw new FleetHeartbeatConflictException("A different payload won the concurrent fleet sequence.");
            }
            telemetry.RecordIngest(FleetHeartbeatDisposition.Duplicate, payload.Length, (report.ObservedAtUtc - now).TotalSeconds, clockDiagnostic, timeProvider.GetElapsedTime(started));
            return Acknowledgement(report, FleetHeartbeatDisposition.Duplicate, now);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void ApplyCurrent(
        DeviceFleetState current,
        FleetStatusReportV1 report,
        string payloadJson,
        string fingerprint,
        DateTimeOffset now,
        FleetClockDiagnostic clockDiagnostic,
        bool gap,
        bool sessionChanged)
    {
        current.AgentInstanceId = report.AgentInstanceId;
        current.BootSessionId = report.BootSessionId;
        current.Sequence = report.Sequence;
        current.ObservedAtUtc = report.ObservedAtUtc;
        current.ReceivedAtUtc = now;
        current.ApparentClockOffsetSeconds = (report.ObservedAtUtc - now).TotalSeconds;
        current.ClockDiagnostic = clockDiagnostic;
        current.ReportedHealth = report.OverallHealth;
        current.HasStoragePressure = report.Storage.Any(static storage => storage.IsUnderPressure);
        current.HasRequiredLaneFailure = report.Lanes.Any(static lane =>
            lane.Required && (lane.PressureLevel >= 2 || lane.QuarantineCount > 0));
        current.HasQuarantine = report.Ingress.QuarantineCount > 0 || report.Processing.QuarantineCount > 0 ||
            report.ArtifactOutbox.QuarantineCount > 0 || report.HeartbeatOutbox.QuarantineCount > 0 ||
            report.Lanes.Any(static lane => lane.QuarantineCount > 0);
        current.SoftwareVersion = report.SoftwareVersion;
        current.ConfigurationSha256 = report.Configuration.ConfigurationSha256;
        current.StatusFingerprint = fingerprint;
        current.CurrentPayloadSha256 = FleetContractJson.ComputeSha256(report);
        current.SnapshotJson = payloadJson;
        if (gap)
        {
            current.LastSequenceGapUtc = now;
            current.SequenceGapCount++;
        }
        if (sessionChanged)
        {
            current.LastBootSessionChangeUtc = now;
            current.BootSessionChangeCount++;
        }
    }

    private FleetClockDiagnostic ClockDiagnostic(DateTimeOffset observedAtUtc, DateTimeOffset receivedAtUtc)
    {
        var offset = observedAtUtc - receivedAtUtc;
        var tolerance = TimeSpan.FromSeconds(options.Value.ClockToleranceSeconds);
        return offset > tolerance ? FleetClockDiagnostic.ClockAhead :
            offset < -tolerance ? FleetClockDiagnostic.ClockBehindOrDeliveryDelayed :
            FleetClockDiagnostic.WithinTolerance;
    }

    private FleetHeartbeatAcknowledgement Acknowledgement(
        FleetStatusReportV1 report,
        FleetHeartbeatDisposition disposition,
        DateTimeOffset now)
        => new(
            report.AgentInstanceId,
            report.BootSessionId,
            report.Sequence,
            disposition,
            now,
            options.Value.RecommendedHeartbeatSeconds);

    private static string ComputeStatusFingerprint(FleetStatusReportV1 report)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            report.Configuration.ConfigurationSha256,
            report.Capture.Availability,
            report.Capture.Reason,
            report.OverallHealth,
            Checks = report.HealthChecks.Select(static check => new { check.Name, check.Status, check.Reason }),
            Lanes = report.Lanes.Select(static lane => new { lane.Name, lane.Required, lane.PressureLevel, HasQuarantine = lane.QuarantineCount > 0 }),
            Storage = report.Storage.Select(static storage => new { storage.Name, storage.IsUnderPressure, HasFailure = storage.FailureReason is not null })
        });

    private static bool IsDeadlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 1205 })
            {
                return true;
            }
        }
        return false;
    }
}

internal sealed class FleetHeartbeatConflictException : InvalidOperationException
{
    public FleetHeartbeatConflictException()
    {
    }

    public FleetHeartbeatConflictException(string message) : base(message)
    {
    }

    public FleetHeartbeatConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
