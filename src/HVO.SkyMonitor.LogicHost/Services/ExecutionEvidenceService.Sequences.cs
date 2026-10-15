using System.Collections.Immutable;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class ExecutionEvidenceService
{
    private async Task AddRangeAsync(ImportedExecutionEvidenceOrigin origin, long sequence, CancellationToken cancellationToken)
    {
        var ranges = _db.Set<ImportedExecutionEvidenceRange>();
        var left = await ranges.Where(r => r.OriginId == origin.Id && r.ToSequence == sequence - 1)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var right = sequence == long.MaxValue ? null : await ranges.Where(r => r.OriginId == origin.Id && r.FromSequence == sequence + 1)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var end = right?.ToSequence ?? sequence;
        if (right is not null) { ranges.Remove(right); }
        if (left is not null) { left.ToSequence = end; }
        else { ranges.Add(new ImportedExecutionEvidenceRange { OriginId = origin.Id, FromSequence = sequence, ToSequence = end }); }
        if ((left?.FromSequence ?? sequence) == 1) { origin.ContiguousThroughSequence = end; }
    }

    private async Task<ExecutionEvidenceFeedbackV1> FeedbackAsync(long originId, ImmutableArray<ExecutionEvidenceFactV1> facts,
        CancellationToken cancellationToken)
    {
        var origin = await _db.Set<ImportedExecutionEvidenceOrigin>().AsNoTracking().SingleAsync(o => o.Id == originId, cancellationToken).ConfigureAwait(false);
        var accepted = await _db.Set<ImportedExecutionEvidenceRange>().AsNoTracking()
            .Where(r => r.OriginId == originId && r.FromSequence > origin.ContiguousThroughSequence)
            .OrderBy(r => r.FromSequence).Take(GraphExecutionEvidenceLimits.MaximumMissingRanges + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var gaps = ImmutableArray.CreateBuilder<ExecutionEvidenceSequenceRangeV1>();
        var truncated = false;
        var remainingUnits = (long)ExecutionEvidenceOptions.MaximumBatchUnits;
        // The producer's resync helper uses the shared 256-unit ceiling. Constrain the missing ranges
        // themselves to our advertised 64-unit bound, including when a single gap is much larger.
        bool AddGap(long fromSequence, long toSequence)
        {
            if (gaps.Count == GraphExecutionEvidenceLimits.MaximumMissingRanges || remainingUnits == 0)
            {
                truncated = true;
                return false;
            }
            var length = toSequence - fromSequence + 1;
            var count = Math.Min(length, remainingUnits);
            gaps.Add(new(ExecutionEvidenceSequenceRangeV1.CurrentSchemaVersion, fromSequence, fromSequence + (count - 1)));
            remainingUnits -= count;
            if (count == length) { return true; }
            truncated = true;
            return false;
        }
        if (origin.ContiguousThroughSequence < long.MaxValue)
        {
            var next = origin.ContiguousThroughSequence + 1;
            foreach (var range in accepted)
            {
                if (range.FromSequence > next)
                {
                    if (!AddGap(next, range.FromSequence - 1)) { break; }
                }
                if (range.ToSequence == long.MaxValue) { next = long.MaxValue; break; }
                next = range.ToSequence + 1;
            }
            if (!truncated && next <= origin.HighestSeenSequence &&
                (accepted.Count == 0 || accepted[^1].ToSequence != long.MaxValue))
            {
                _ = AddGap(next, origin.HighestSeenSequence);
            }
        }
        return new(ExecutionEvidenceFeedbackV1.CurrentSchemaVersion, origin.IdentitySha256, timeProvider.GetUtcNow(),
            origin.ContiguousThroughSequence, gaps.ToImmutable(), facts,
            new(ExecutionEvidenceRetentionV1.CurrentSchemaVersion, origin.ContiguousThroughSequence, DateTimeOffset.MaxValue,
                _options.MaximumUnitsPerBinding), truncated);
    }
}
