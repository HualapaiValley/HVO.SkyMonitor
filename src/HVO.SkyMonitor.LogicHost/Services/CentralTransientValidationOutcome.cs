using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class CentralTransientValidationOutcome
{
    public static async Task RecordNeedsReviewAsync(
        ApplicationDbContext dbContext,
        Guid jobId,
        string reasonCode,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        var job = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Include(item => item.InputRequirements)
            .Include(item => item.Inputs)
            .SingleAsync(item => item.Id == jobId, cancellationToken).ConfigureAwait(false);
        await RecordNeedsReviewAsync(dbContext, job, reasonCode, recordedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task RecordNeedsReviewAsync(
        ApplicationDbContext dbContext,
        CentralDerivativeJob job,
        string reasonCode,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        await using var ownedTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false)
            : null;
        var agentId = await dbContext.CentralTransientValidationJobs.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == job.Id)
            .Select(item => item.AgentId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (agentId is null)
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        await AcquireEventSerializationLockAsync(dbContext, agentId, cancellationToken).ConfigureAwait(false);
        var validation = await dbContext.CentralTransientValidationJobs
            .Include(item => item.IdentitySlots)
            .Include(item => item.OutcomeVersions)
            .Include(item => item.ExtractionReceipt)!.ThenInclude(item => item!.Sources)
            .SingleOrDefaultAsync(item => item.CentralDerivativeJobId == job.Id, cancellationToken)
            .ConfigureAwait(false);
        if (validation is null)
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var evidence = validation.CommittedAtUtc.HasValue && validation.ExtractionReceipt is { } receipt
            ? CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
            {
                schemaVersion = "central-transient-validation-outcome-v1",
                state = TransientEventState.NeedsReview,
                reasonCode,
                job.RequestIdentitySha256,
                validation.ExecutionOptionsIdentitySha256,
                receipt.ExtractionIdentitySha256,
                receipt.CanonicalReceiptSha256,
                sources = receipt.Sources.OrderBy(item => item.Ordinal).Select(item => new
                {
                    item.Ordinal,
                    item.CentralArtifactId,
                    item.ArtifactId,
                    item.ArtifactChecksumSha256,
                    item.DetectorInputIdentitySha256
                })
            })).GetRawText()
            : CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
            {
                schemaVersion = "central-transient-validation-outcome-v1",
                state = TransientEventState.NeedsReview,
                reasonCode,
                job.RequestIdentitySha256,
                job.InputSetIdentitySha256,
                sources = job.Inputs.OrderBy(item => item.Ordinal).Select(item => new
                {
                    item.Ordinal,
                    item.CentralArtifactId,
                    item.CaptureSequence,
                    item.CompatibilitySha256,
                    item.ByteLength
                }),
                requirements = job.InputRequirements.OrderBy(item => item.Ordinal).Select(item => new
                {
                    item.Ordinal,
                    item.SequenceOffset,
                    item.IsRequired,
                    item.ResolutionState,
                    item.ResolutionReasonCode,
                    item.ExpectedCaptureSequence
                })
            })).GetRawText();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        if (await dbContext.CentralTransientValidationOutcomeVersions.AsNoTracking().AnyAsync(item =>
                item.CentralDerivativeJobId == validation.CentralDerivativeJobId &&
                item.EvidenceIdentitySha256 == identity, cancellationToken).ConfigureAwait(false))
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (!validation.OutcomeRecordedAtUtc.HasValue)
        {
            validation.OutcomeState = TransientEventState.NeedsReview;
            validation.OutcomeReasonCode = reasonCode;
            validation.OutcomeEvidenceJson = evidence;
            validation.OutcomeEvidenceIdentitySha256 = identity;
            validation.OutcomeRecordedAtUtc = recordedAtUtc;
        }
        else if (validation.CommittedAtUtc.HasValue)
        {
            await AppendNeedsReviewEventVersionsAsync(
                dbContext, validation, recordedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        var version = validation.OutcomeVersions.Count == 0
            ? 1
            : validation.OutcomeVersions.Max(item => item.Version) + 1;
        foreach (var slot in validation.IdentitySlots.Where(item =>
                     item.State == CentralTransientValidationIdentitySlotState.Reserved))
        {
            slot.State = CentralTransientValidationIdentitySlotState.Unused;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [CentralTransientValidationOutcomeVersions]
                ([Id], [CentralDerivativeJobId], [Version], [State], [ReasonCode], [EvidenceJson],
                 [EvidenceIdentitySha256], [RecordedAtUtc])
            VALUES ({Guid.NewGuid()}, {validation.CentralDerivativeJobId}, {version},
                    {TransientEventState.NeedsReview.ToString()}, {reasonCode}, {evidence}, {identity}, {recordedAtUtc})
            """, cancellationToken).ConfigureAwait(false);
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static Task<int> AcquireEventSerializationLockAsync(
        ApplicationDbContext dbContext,
        string agentId,
        CancellationToken cancellationToken)
    {
        var agentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId)));
        var resource = $"hvo:transient-association:{agentHash}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000;
            IF @result < 0
                THROW 51008, 'Could not acquire the transient event serialization lock.', 1;
            """, cancellationToken);
    }

    private static async Task AppendNeedsReviewEventVersionsAsync(
        ApplicationDbContext dbContext,
        CentralTransientValidationJob validation,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var eventRecordIds = validation.IdentitySlots
            .Where(item => item.State == CentralTransientValidationIdentitySlotState.Committed &&
                item.CentralTransientEventId.HasValue)
            .Select(item => item.CentralTransientEventId!.Value)
            .Distinct()
            .ToArray();
        foreach (var eventRecordId in eventRecordIds)
        {
            var latest = await dbContext.CentralTransientEventVersions.AsNoTracking()
                .Where(item => item.CentralTransientEventId == eventRecordId)
                .OrderByDescending(item => item.Version)
                .FirstAsync(cancellationToken).ConfigureAwait(false);
            var parsed = TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(latest.CanonicalEventJson));
            var prior = parsed.Value ?? throw new CentralDerivativeJobStateException(
                $"Persisted transient event failed canonical parsing: {parsed.Validation.ReasonCode}.");
            var createdUtc = recordedAtUtc > prior.VersionCreatedUtc
                ? recordedAtUtc
                : prior.VersionCreatedUtc.AddTicks(1);
            var next = prior with
            {
                EventVersionId = Guid.NewGuid(),
                Version = prior.Version + 1,
                PreviousEventVersionId = prior.EventVersionId,
                PreviousVersionCreatedUtc = prior.VersionCreatedUtc,
                State = TransientEventState.NeedsReview,
                VersionCreatedUtc = createdUtc
            };
            var canonical = TransientContractJson.Serialize(next);
            var version = new CentralTransientEventVersionRecord
            {
                EventVersionId = next.EventVersionId,
                CentralTransientEventId = eventRecordId,
                Version = next.Version,
                PreviousVersionNumber = prior.Version,
                PreviousEventVersionId = prior.EventVersionId,
                PreviousVersionCreatedUtc = prior.VersionCreatedUtc,
                State = next.State,
                VersionCreatedUtc = next.VersionCreatedUtc,
                FirstObservedUtc = next.FirstObservedUtc,
                LastObservedUtc = next.LastObservedUtc,
                SchemaVersion = next.SchemaVersion,
                CanonicalEventJson = Encoding.UTF8.GetString(canonical),
                CanonicalEventSha256 = Convert.ToHexString(SHA256.HashData(canonical)),
                CanonicalEventByteLength = canonical.Length
            };
            dbContext.CentralTransientEventVersions.Add(version);
            dbContext.AddRange(next.Observations.Select(observation =>
                new CentralTransientEventVersionObservation
                {
                    CentralTransientEventId = eventRecordId,
                    EventVersionId = next.EventVersionId,
                    Ordinal = observation.Ordinal,
                    ObservationId = observation.ObservationId
                }));
            dbContext.AddRange(next.Assessments.Select((assessment, ordinal) =>
                new CentralTransientEventVersionAssessment
                {
                    CentralTransientEventId = eventRecordId,
                    EventVersionId = next.EventVersionId,
                    Ordinal = ordinal,
                    AssessmentId = assessment.AssessmentId
                }));
        }
    }
}
