using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum EnvironmentalObservationIngestDisposition
{
    Accepted,
    Duplicate
}

internal static class EnvironmentalObservationLockNames
{
    public const string Retention = "environmental-observation-retention";
}

internal sealed record EnvironmentalObservationIngestResult(
    Guid RecordId,
    EnvironmentalObservationIngestDisposition Disposition,
    DateTimeOffset ReceivedAtUtc,
    string ContentSha256);

internal sealed class EnvironmentalObservationConflictException : InvalidOperationException
{
    public EnvironmentalObservationConflictException()
    {
    }

    public EnvironmentalObservationConflictException(string message) : base(message)
    {
    }

    public EnvironmentalObservationConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

internal interface IEnvironmentalObservationIngestService
{
    Task<EnvironmentalObservationIngestResult> IngestAsync(
        EnvironmentalObservationV1 observation,
        CancellationToken cancellationToken = default);
}

internal sealed class EnvironmentalObservationIngestService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<EnvironmentalObservationOptions> options,
    EnvironmentalObservationTelemetry telemetry,
    ILogger<EnvironmentalObservationIngestService> logger) : IEnvironmentalObservationIngestService
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    private static readonly Action<ILogger, EnvironmentalObservationKind, EnvironmentalObservationSourceKind, EnvironmentalObservationIngestDisposition, Exception?> Ingested =
        LoggerMessage.Define<EnvironmentalObservationKind, EnvironmentalObservationSourceKind, EnvironmentalObservationIngestDisposition>(
            LogLevel.Information,
            new EventId(2500, nameof(Ingested)),
            "Environmental {ObservationKind} observation from {SourceKind} evidence was {Disposition}");
    private static readonly Action<ILogger, EnvironmentalObservationKind, EnvironmentalObservationSourceKind, Exception?> Conflict =
        LoggerMessage.Define<EnvironmentalObservationKind, EnvironmentalObservationSourceKind>(
            LogLevel.Warning,
            new EventId(2502, nameof(Conflict)),
            "Environmental {ObservationKind} observation from {SourceKind} evidence conflicted with durable identity");

    public async Task<EnvironmentalObservationIngestResult> IngestAsync(
        EnvironmentalObservationV1 observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var validation = EnvironmentalObservationJson.Validate(observation);
        if (!validation.IsValid)
        {
            telemetry.RecordValidation("contract");
            throw new ArgumentException(
                $"Invalid environmental observation ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(observation));
        }

        var targetExists = await dbContext.Observatories
            .AsNoTracking()
            .AnyAsync(site => site.Id == observation.Target.SiteId, cancellationToken)
            .ConfigureAwait(false);
        if (!targetExists)
        {
            telemetry.RecordConflict("site");
            throw new EnvironmentalObservationConflictException("The environmental observation site does not exist.");
        }
        if (observation.Target.AgentId is { } agentId)
        {
            var agentBound = await dbContext.DeviceRegistrations
                .AsNoTracking()
                .AnyAsync(
                    registration => registration.ObservatoryId == observation.Target.SiteId &&
                        registration.DevicePublicId == agentId,
                    cancellationToken)
                .ConfigureAwait(false);
            agentBound = agentBound || await dbContext.DeviceRigProfiles
                .AsNoTracking()
                .AnyAsync(
                    profile => profile.ObservatoryId == observation.Target.SiteId &&
                        profile.DevicePublicId == agentId,
                    cancellationToken)
                .ConfigureAwait(false) || await dbContext.CentralFrames
                .AsNoTracking()
                .AnyAsync(
                    frame => frame.ObservatoryId == observation.Target.SiteId &&
                        frame.DevicePublicId == agentId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!agentBound)
            {
                telemetry.RecordConflict("agent");
                throw new EnvironmentalObservationConflictException(
                    "The environmental observation agent is not historically bound to the specified site.");
            }
            if (observation.Target.RigId is { } rigId)
            {
                var rigBound = dbContext.Database.IsRelational()
                    ? await dbContext.CentralFrames
                        .AsNoTracking()
                        .AnyAsync(
                            frame => frame.ObservatoryId == observation.Target.SiteId &&
                                frame.DevicePublicId == agentId &&
                                EF.Functions.Collate(frame.RigId!, BinaryCollation) == rigId,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await dbContext.CentralFrames
                        .AsNoTracking()
                        .AnyAsync(
                            frame => frame.ObservatoryId == observation.Target.SiteId &&
                                frame.DevicePublicId == agentId && frame.RigId == rigId,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (!rigBound)
                {
                    telemetry.RecordConflict("rig");
                    throw new EnvironmentalObservationConflictException(
                        "The environmental observation rig is not historically bound to the specified site and agent.");
                }
            }
        }

        var now = timeProvider.GetUtcNow();
        var payload = EnvironmentalObservationJson.Serialize(observation);
        var payloadSha256 = EnvironmentalObservationJson.ComputeContentSha256(observation);
        var sourceIdentitySha256 = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation);
        var sourceContentSha256 = EnvironmentalObservationJson.ComputeSourceContentSha256(observation);
        var diagnostic = ClockDiagnostic(observation.ObservedAtUtc, now);
        var started = timeProvider.GetTimestamp();
        using var activity = EnvironmentalObservationTelemetry.ActivitySource.StartActivity("environment.ingest");
        activity?.SetTag("environment.observation_kind", observation.Value.Kind.ToString());
        activity?.SetTag("environment.source_kind", observation.Source.Kind.ToString());
        IDbContextTransaction? transaction = null;
        var retentionLockHeld = false;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var lockResource = $"environment-source:{sourceIdentitySha256}";
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    DECLARE @sourceResult int;
                    EXEC @sourceResult = sys.sp_getapplock
                        @Resource = {lockResource},
                        @LockMode = 'Exclusive',
                        @LockOwner = 'Transaction',
                        @LockTimeout = 10000;
                    IF @sourceResult < 0
                        THROW 51006, 'Could not acquire the environmental source lock.', 1;
                    """, cancellationToken).ConfigureAwait(false);
            }

            if (observation.Source.Kind == EnvironmentalObservationSourceKind.Derived)
            {
                await AcquireRetentionLockAsync().ConfigureAwait(false);
            }
            var source = await dbContext.EnvironmentalObservationSources
                .SingleOrDefaultAsync(item => item.IdentitySha256 == sourceIdentitySha256, cancellationToken)
                .ConfigureAwait(false);
            if (source is not null && !string.Equals(source.ContentSha256, sourceContentSha256, StringComparison.Ordinal))
            {
                telemetry.RecordConflict("source");
                Conflict(logger, observation.Value.Kind, observation.Source.Kind, null);
                throw new EnvironmentalObservationConflictException(
                    "A different environmental source descriptor already exists for this source identity.");
            }
            var sourceIsNew = source is null;
            if (sourceIsNew)
            {
                source = CreateSource(observation, sourceIdentitySha256, sourceContentSha256, now);
            }
            var resolvedSource = source ?? throw new InvalidOperationException("Environmental source resolution failed.");

            var existing = await dbContext.EnvironmentalObservations
                .SingleOrDefaultAsync(
                    item => item.SourceRecordId == resolvedSource.Id && item.ObservationId == observation.ObservationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && !retentionLockHeld)
            {
                await AcquireRetentionLockAsync().ConfigureAwait(false);
                existing = await dbContext.EnvironmentalObservations
                    .SingleOrDefaultAsync(
                        item => item.SourceRecordId == resolvedSource.Id && item.ObservationId == observation.ObservationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            if (existing is not null)
            {
                if (!string.Equals(existing.PayloadSha256, payloadSha256, StringComparison.Ordinal))
                {
                    telemetry.RecordConflict("observation");
                    Conflict(logger, observation.Value.Kind, observation.Source.Kind, null);
                    throw new EnvironmentalObservationConflictException(
                        "A different payload already exists for this environmental observation identity.");
                }
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                return Complete(existing.Id, EnvironmentalObservationIngestDisposition.Duplicate, existing.ReceivedAtUtc);
            }

            var record = CreateObservation(observation, resolvedSource.Id, payloadSha256, now, diagnostic);
            for (var ordinal = 0; ordinal < observation.Lineage.Count; ordinal++)
            {
                var reference = observation.Lineage[ordinal];
                var referencedSourceIdentity = reference.SourceIdentitySha256.ToUpperInvariant();
                var referencedSource = await dbContext.EnvironmentalObservationSources
                    .SingleOrDefaultAsync(
                        item => item.IdentitySha256 == referencedSourceIdentity,
                        cancellationToken)
                    .ConfigureAwait(false);
                var referencedObservation = referencedSource is null
                    ? null
                    : await dbContext.EnvironmentalObservations.SingleOrDefaultAsync(
                        item => item.SourceRecordId == referencedSource.Id &&
                            item.ObservationId == reference.ObservationId,
                        cancellationToken).ConfigureAwait(false);
                if (referencedObservation is null)
                {
                    telemetry.RecordConflict("lineage");
                    throw new EnvironmentalObservationConflictException(
                        "A referenced environmental source observation does not exist.");
                }
                record.Lineage.Add(new EnvironmentalObservationLineageRecord
                {
                    DerivedObservationRecordId = record.Id,
                    Ordinal = ordinal,
                    SourceObservationRecordId = referencedObservation.Id
                });
            }
            if (sourceIsNew)
            {
                dbContext.EnvironmentalObservationSources.Add(resolvedSource);
            }
            dbContext.EnvironmentalObservations.Add(record);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return Complete(record.Id, EnvironmentalObservationIngestDisposition.Accepted, record.ReceivedAtUtc);
        }
        catch (EnvironmentalObservationConflictException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (DbUpdateException exception) when (dbContext.Database.IsRelational())
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
            var winnerSource = await dbContext.EnvironmentalObservationSources
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.IdentitySha256 == sourceIdentitySha256, cancellationToken)
                .ConfigureAwait(false);
            if (winnerSource is null || !string.Equals(winnerSource.ContentSha256, sourceContentSha256, StringComparison.Ordinal))
            {
                RecordFailure(exception);
                throw;
            }
            var winner = await dbContext.EnvironmentalObservations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.SourceRecordId == winnerSource.Id && item.ObservationId == observation.ObservationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (winner is null)
            {
                RecordFailure(exception);
                throw;
            }
            if (!string.Equals(winner.PayloadSha256, payloadSha256, StringComparison.Ordinal))
            {
                telemetry.RecordConflict("observation");
                Conflict(logger, observation.Value.Kind, observation.Source.Kind, null);
                throw new EnvironmentalObservationConflictException(
                    "A different payload won the concurrent environmental observation identity.");
            }
            return Complete(winner.Id, EnvironmentalObservationIngestDisposition.Duplicate, winner.ReceivedAtUtc);
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("environment.outcome", "canceled");
            throw;
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }

        EnvironmentalObservationIngestResult Complete(
            Guid recordId,
            EnvironmentalObservationIngestDisposition disposition,
            DateTimeOffset receivedAtUtc)
        {
            var duration = timeProvider.GetElapsedTime(started);
            telemetry.RecordIngest(
                disposition,
                observation.Value.Kind,
                observation.Source.Kind,
                payload.Length,
                (observation.ObservedAtUtc - now).TotalSeconds,
                diagnostic,
                duration);
            Ingested(logger, observation.Value.Kind, observation.Source.Kind, disposition, null);
            activity?.SetTag("environment.disposition", disposition.ToString());
            return new(recordId, disposition, receivedAtUtc, payloadSha256);
        }

        void RecordFailure(Exception exception)
        {
            telemetry.RecordIngestFailure();
            activity?.SetTag("environment.outcome", "failed");
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
        }

        async Task AcquireRetentionLockAsync()
        {
            if (!dbContext.Database.IsRelational())
            {
                retentionLockHeld = true;
                return;
            }
            var lockResource = EnvironmentalObservationLockNames.Retention;
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = {lockResource},
                    @LockMode = 'Shared',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 10000;
                IF @result < 0
                    THROW 51007, 'Could not acquire the environmental retention lock.', 1;
                """, cancellationToken).ConfigureAwait(false);
            retentionLockHeld = true;
        }
    }

    private EnvironmentalClockDiagnostic ClockDiagnostic(DateTimeOffset observedAtUtc, DateTimeOffset receivedAtUtc)
    {
        var offset = observedAtUtc - receivedAtUtc;
        var tolerance = TimeSpan.FromSeconds(options.Value.ClockToleranceSeconds);
        return offset > tolerance ? EnvironmentalClockDiagnostic.ClockAhead :
            offset < -tolerance ? EnvironmentalClockDiagnostic.ClockBehindOrDeliveryDelayed :
            EnvironmentalClockDiagnostic.WithinTolerance;
    }

    private static EnvironmentalObservationSourceRecord CreateSource(
        EnvironmentalObservationV1 observation,
        string identitySha256,
        string contentSha256,
        DateTimeOffset now)
    {
        var provenance = observation.Source.Provenance;
        return new EnvironmentalObservationSourceRecord
        {
            IdentitySha256 = identitySha256,
            ContentSha256 = contentSha256,
            SiteId = observation.Target.SiteId,
            AgentId = observation.Target.AgentId,
            RigId = observation.Target.RigId,
            Provider = observation.Source.Provider,
            SourceId = observation.Source.SourceId,
            Version = observation.Source.Version,
            Kind = observation.Source.Kind,
            MethodName = provenance.Method.Name,
            MethodVersion = provenance.Method.Version,
            ParametersJson = JsonSerializer.Serialize(CaptureContractJson.Canonicalize(provenance.Parameters)),
            ParametersSha256 = provenance.ParametersSha256.ToUpperInvariant(),
            CreatedAtUtc = now
        };
    }

    private static EnvironmentalObservationRecord CreateObservation(
        EnvironmentalObservationV1 observation,
        Guid sourceRecordId,
        string payloadSha256,
        DateTimeOffset now,
        EnvironmentalClockDiagnostic diagnostic)
    {
        var value = observation.Value;
        return new EnvironmentalObservationRecord
        {
            SourceRecordId = sourceRecordId,
            SiteId = observation.Target.SiteId,
            AgentId = observation.Target.AgentId,
            RigId = observation.Target.RigId,
            SourceKind = observation.Source.Kind,
            SourceIdentitySha256 = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation),
            ObservationId = observation.ObservationId,
            SchemaVersion = observation.SchemaVersion,
            Kind = value.Kind,
            Unit = value.Unit,
            NumericValue = value.NumericValue,
            BooleanValue = value.BooleanValue,
            Quality = value.Quality,
            Uncertainty = value.Uncertainty,
            SubmittedNumericValue = value.SubmittedNumericValue,
            SubmittedUnit = value.SubmittedUnit,
            ObservedAtUtc = observation.ObservedAtUtc,
            ObservedFromUtc = observation.ObservedFromUtc,
            ObservedThroughUtc = observation.ObservedThroughUtc,
            ValidFromUtc = observation.ValidFromUtc,
            ValidThroughUtc = observation.ValidThroughUtc,
            StaleAfterUtc = observation.StaleAfterUtc,
            ReceivedAtUtc = now,
            ApparentClockOffsetSeconds = (observation.ObservedAtUtc - now).TotalSeconds,
            ClockDiagnostic = diagnostic,
            PayloadSha256 = payloadSha256
        };
    }
}
