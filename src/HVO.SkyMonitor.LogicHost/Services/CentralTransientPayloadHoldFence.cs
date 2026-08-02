using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralTransientPayloadHoldTarget(
    CentralTransientPayloadReleaseItemKind Kind,
    Guid RecordId,
    string StorageReference,
    byte[] RowVersion,
    CentralArtifactObjectState ObjectState);

internal sealed class CentralTransientPayloadHoldScope(
    IReadOnlyList<CentralTransientPayloadHoldTarget> targets,
    IReadOnlyList<CentralObjectApplicationLock> locks) : IAsyncDisposable
{
    private bool disposed;

    internal IReadOnlyList<CentralTransientPayloadHoldTarget> Targets { get; } = targets;

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        for (var index = locks.Count - 1; index >= 0; index--)
        {
            await locks[index].DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal static class CentralTransientPayloadHoldFence
{
    internal static async Task<IReadOnlyList<CentralTransientPayloadHoldTarget>> ReadArtifactsAsync(
        ApplicationDbContext dbContext,
        IEnumerable<Guid> recordIds,
        CancellationToken cancellationToken)
    {
        var ids = recordIds.Distinct().ToArray();
        return await dbContext.CentralArtifacts.AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .Select(item => new CentralTransientPayloadHoldTarget(
                CentralTransientPayloadReleaseItemKind.SourceArtifact,
                item.Id,
                item.StorageReference,
                item.RowVersion,
                item.ObjectState))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CentralTransientPayloadHoldTarget?> ReadArtifactAsync(
        ApplicationDbContext dbContext,
        Guid recordId,
        CancellationToken cancellationToken)
        => (await ReadArtifactsAsync(dbContext, [recordId], cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();

    internal static async Task<CentralTransientPayloadHoldTarget?> ReadDerivativeAsync(
        ApplicationDbContext dbContext,
        Guid derivativeId,
        CancellationToken cancellationToken)
        => await dbContext.CentralTransientDerivatives.AsNoTracking()
            .Where(item => item.DerivativeId == derivativeId)
            .Select(item => new CentralTransientPayloadHoldTarget(
                CentralTransientPayloadReleaseItemKind.Derivative,
                item.OutputIntentId,
                item.OutputIntent!.StorageReference,
                item.OutputIntent.RowVersion,
                item.OutputIntent.ObjectState))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    internal static async Task<CentralTransientPayloadHoldScope> AcquireAsync(
        ApplicationDbContext dbContext,
        IReadOnlyList<CentralTransientPayloadHoldTarget> targets,
        CancellationToken cancellationToken)
    {
        if (targets.Any(item => string.IsNullOrWhiteSpace(item.StorageReference)))
        {
            throw new CentralTransientPayloadHoldRejectedException("transient-retention.hold-target-invalid");
        }
        var locks = new List<CentralObjectApplicationLock>();
        try
        {
            foreach (var storageReference in targets.Select(item => item.StorageReference)
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                locks.Add(await CentralObjectApplicationLock.AcquireAsync(
                    dbContext, storageReference, cancellationToken).ConfigureAwait(false));
            }
            return new(targets, locks);
        }
        catch
        {
            for (var index = locks.Count - 1; index >= 0; index--)
            {
                await locks[index].DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    internal static async Task ValidateAsync(
        ApplicationDbContext dbContext,
        IReadOnlyList<CentralTransientPayloadHoldTarget> targets,
        CancellationToken cancellationToken)
    {
        foreach (var expected in targets.OrderBy(item => item.Kind).ThenBy(item => item.RecordId))
        {
            CentralTransientPayloadHoldTarget? current;
            if (expected.Kind == CentralTransientPayloadReleaseItemKind.SourceArtifact)
            {
                if (await CentralArtifactRetentionLock.AcquireAsync(
                        dbContext, expected.RecordId, cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CentralTransientPayloadHoldRejectedException("transient-retention.hold-target-missing");
                }
                current = await dbContext.CentralArtifacts.AsNoTracking()
                    .Where(item => item.Id == expected.RecordId)
                    .Select(item => new CentralTransientPayloadHoldTarget(
                        CentralTransientPayloadReleaseItemKind.SourceArtifact,
                        item.Id,
                        item.StorageReference,
                        item.RowVersion,
                        item.ObjectState))
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                current = await dbContext.CentralTransientDerivativeOutputIntents.FromSqlInterpolated($"""
                        SELECT * FROM [CentralTransientDerivativeOutputIntents] WITH (UPDLOCK, HOLDLOCK)
                        WHERE [Id] = {expected.RecordId}
                        """)
                    .AsNoTracking()
                    .Select(item => new CentralTransientPayloadHoldTarget(
                        CentralTransientPayloadReleaseItemKind.Derivative,
                        item.Id,
                        item.StorageReference,
                        item.RowVersion,
                        item.ObjectState))
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
            if (current is null || current.ObjectState != CentralArtifactObjectState.Available ||
                !string.Equals(current.StorageReference, expected.StorageReference, StringComparison.Ordinal) ||
                !current.RowVersion.AsSpan().SequenceEqual(expected.RowVersion))
            {
                throw new CentralTransientPayloadHoldRejectedException("transient-retention.hold-target-changed");
            }
            if (await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking().AnyAsync(item =>
                    item.Kind == expected.Kind && item.RecordId == expected.RecordId && item.RequestedAtUtc != null &&
                    (item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
                         item.Release!.State == CentralTransientPayloadReleaseState.Pending ||
                     item.Outcome == CentralTransientPayloadReleaseItemOutcome.Failed &&
                         item.FailureReasonCode == "transient-retention.delete-outcome-ambiguous"),
                    cancellationToken).ConfigureAwait(false))
            {
                throw new CentralTransientPayloadHoldRejectedException("transient-retention.release-attempt-won");
            }
        }
    }
}

internal sealed class CentralTransientPayloadHoldRejectedException : InvalidOperationException
{
    public CentralTransientPayloadHoldRejectedException()
        : this("transient-retention.hold-rejected")
    {
    }

    public CentralTransientPayloadHoldRejectedException(string reasonCode)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public CentralTransientPayloadHoldRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = message;
    }

    internal string ReasonCode { get; }
}
