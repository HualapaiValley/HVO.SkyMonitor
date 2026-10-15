using System.Collections.Immutable;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class ExecutionEvidenceService
{
    public async Task<ExecutionEvidenceBindingView?> GetBindingAsync(Guid bindingId, string userId, Guid? observatoryScope,
        CancellationToken cancellationToken = default)
    {
        var binding = await AuthorizedBindingAsync(bindingId, userId, observatoryScope, cancellationToken).ConfigureAwait(false);
        return binding is null ? null : View(binding);
    }

    public async Task<ExecutionEvidenceStatusView?> GetStatusAsync(Guid bindingId, string userId, Guid? observatoryScope,
        CancellationToken cancellationToken = default)
    {
        if (await AuthorizedBindingAsync(bindingId, userId, observatoryScope, cancellationToken).ConfigureAwait(false) is null) { return null; }
        var usage = await _db.Set<ExecutionEvidenceBindingUsage>().AsNoTracking().SingleAsync(u => u.BindingId == bindingId, cancellationToken).ConfigureAwait(false);
        return new(bindingId, usage.StoredBytes, usage.UnitCount, usage.OriginCount, usage.ConflictCount, usage.ObservationCount,
            usage.StoredBytes >= _options.MaximumStoredBytesPerBinding || usage.UnitCount >= _options.MaximumUnitsPerBinding ||
            usage.OriginCount >= _options.MaximumOriginsPerBinding || usage.ConflictCount >= _options.MaximumConflictsPerBinding ||
            usage.ObservationCount >= _options.MaximumObservationsPerBinding,
            _options.MaximumStoredBytesPerBinding, _options.MaximumUnitsPerBinding, _options.MaximumOriginsPerBinding,
            _options.MaximumConflictsPerBinding, _options.MaximumObservationsPerBinding);
    }

    public async Task<IReadOnlyList<ExecutionEvidenceOriginView>?> ListOriginsAsync(Guid bindingId, string userId, Guid? observatoryScope,
        long afterId = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        ValidatePage(afterId, take);
        if (await AuthorizedBindingAsync(bindingId, userId, observatoryScope, cancellationToken).ConfigureAwait(false) is null) { return null; }
        return await _db.Set<ImportedExecutionEvidenceOrigin>().AsNoTracking().Where(o => o.BindingId == bindingId && o.Id > afterId)
            .OrderBy(o => o.Id).Take(take).Select(o => new ExecutionEvidenceOriginView(o.Id, o.IdentitySha256, o.BootSessionId,
                o.SoftwareVersion, o.FirstReceivedAtUtc, o.HighestSeenSequence, o.ContiguousThroughSequence))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExecutionEvidenceUnitView>?> ListUnitsAsync(Guid bindingId, string userId, Guid? observatoryScope,
        long afterId = 0, int take = 50, string? originIdentitySha256 = null, string? kind = null, CancellationToken cancellationToken = default)
    {
        ValidatePage(afterId, take);
        if (originIdentitySha256 is not null && !ExecutionEvidencePlanValidator.IsHash(originIdentitySha256) ||
            kind is not null && (kind != nameof(ExecutionEvidenceBodyKind.GraphRevision) && kind != nameof(ExecutionEvidenceBodyKind.GraphExecution) &&
                kind != nameof(ExecutionEvidenceBodyKind.ArtifactAvailability)))
        { throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.InvalidIdentity); }
        if (await AuthorizedBindingAsync(bindingId, userId, observatoryScope, cancellationToken).ConfigureAwait(false) is null) { return null; }
        var query = _db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().Where(u => u.BindingId == bindingId && u.Id > afterId);
        if (originIdentitySha256 is not null)
        {
            var originId = await _db.Set<ImportedExecutionEvidenceOrigin>().Where(o => o.BindingId == bindingId && o.IdentitySha256 == originIdentitySha256)
                .Select(o => (long?)o.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (originId is null) { return []; }
            query = query.Where(u => u.OriginId == originId);
        }
        if (kind is not null) { query = query.Where(u => u.Kind == kind); }
        return await query.OrderBy(u => u.Id).Take(take).Select(u => new ExecutionEvidenceUnitView(u.Id, u.OriginId, u.OriginSequence,
            u.EvidenceId, u.PayloadSha256, u.Kind, u.RevisionId, u.ExecutionId, u.CaptureId, u.ProducedAtUtc, u.ReceivedAtUtc,
            u.Accepted, u.RejectionReasonCode)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetUnitAsync(Guid bindingId, long unitId, string userId, Guid? observatoryScope,
        CancellationToken cancellationToken = default)
    {
        if (await AuthorizedBindingAsync(bindingId, userId, observatoryScope, cancellationToken).ConfigureAwait(false) is null) { return null; }
        return await _db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().Where(u => u.BindingId == bindingId && u.Id == unitId)
            .Select(u => u.CanonicalBytes).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionEvidenceFeedbackV1?> GetFeedbackAsync(Guid bindingId, string originIdentitySha256, string userId,
        Guid? observatoryScope, long afterSequence = 0, int take = 64, CancellationToken cancellationToken = default)
    {
        ValidatePage(afterSequence, take, ExecutionEvidenceOptions.MaximumBatchUnits);
        if (!ExecutionEvidencePlanValidator.IsHash(originIdentitySha256)) { throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.InvalidOrigin); }
        if (await AuthorizedBindingAsync(bindingId, userId, observatoryScope, cancellationToken).ConfigureAwait(false) is null) { return null; }
        var originId = await _db.Set<ImportedExecutionEvidenceOrigin>().Where(o => o.BindingId == bindingId && o.IdentitySha256 == originIdentitySha256)
            .Select(o => (long?)o.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (originId is null) { return null; }
        // Receipts need only the indexed identity/time projection, never the potentially multi-megabyte body.
        var units = await _db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking().Where(u => u.OriginId == originId && u.OriginSequence > afterSequence)
            .OrderBy(u => u.OriginSequence).Take(take).Select(u => new
            {
                u.EvidenceId, u.OriginSequence, u.PayloadSha256, u.ReceivedAtUtc, u.Accepted, u.RejectionReasonCode, u.RejectionFieldPath
            }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var facts = ImmutableArray.CreateBuilder<ExecutionEvidenceFactV1>();
        foreach (var unit in units)
        {
            var kinds = unit.Accepted ? new[] { ExecutionEvidenceFactKind.Received, ExecutionEvidenceFactKind.Validated,
                ExecutionEvidenceFactKind.Accepted, ExecutionEvidenceFactKind.Acknowledged } : [ExecutionEvidenceFactKind.Received, ExecutionEvidenceFactKind.Rejected];
            foreach (var kind in kinds)
            {
                facts.Add(new(ExecutionEvidenceFactV1.CurrentSchemaVersion, kind, unit.EvidenceId, unit.OriginSequence,
                    unit.PayloadSha256, unit.ReceivedAtUtc, kind != ExecutionEvidenceFactKind.Rejected,
                    kind == ExecutionEvidenceFactKind.Rejected ? unit.RejectionReasonCode : null,
                    kind == ExecutionEvidenceFactKind.Rejected ? unit.RejectionFieldPath : null));
            }
        }
        return await FeedbackAsync(originId.Value, facts.ToImmutable(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExecutionEvidenceObservationView>?> ListObservationsAsync(Guid bindingId, string userId, Guid? observatoryScope,
        long afterId = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        ValidatePage(afterId, take);
        if (await AuthorizedBindingAsync(bindingId, userId, observatoryScope, cancellationToken).ConfigureAwait(false) is null) { return null; }
        return await _db.Set<ImportedExecutionEvidenceObservation>().AsNoTracking().Where(o => o.BindingId == bindingId && o.Id > afterId)
            .OrderBy(o => o.Id).Take(take).Select(o => new ExecutionEvidenceObservationView(o.Id, o.ArtifactReferenceId, o.State,
                o.ReasonCode, o.ObservedAtUtc, o.ObjectGeneration)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<ExecutionEvidenceBinding?> AuthorizedBindingAsync(Guid bindingId, string userId, Guid? observatoryScope, CancellationToken cancellationToken)
        => _db.Set<ExecutionEvidenceBinding>().AsNoTracking().Where(b => b.Id == bindingId &&
            (observatoryScope == null || observatoryScope == b.ObservatoryId) &&
            ObservatoryMembershipAccess.ForUser(_db, userId).Any(m => m.ObservatoryId == b.ObservatoryId))
            .SingleOrDefaultAsync(cancellationToken);

    private static void ValidatePage(long cursor, int take, int maximum = 100)
    {
        if (cursor < 0 || take < 1 || take > maximum) { throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.LimitExceeded); }
    }
}
