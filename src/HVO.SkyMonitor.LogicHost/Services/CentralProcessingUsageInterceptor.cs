using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>
/// Records processing usage for derivative attempts that reach a terminal outcome through tracked entities (graph
/// cancellation, source invalidation, location quarantine) in the same <c>SaveChanges</c> unit of work (#429). Sites
/// that terminalize attempts with bulk updates call <see cref="CentralProcessingUsageRecorder"/> directly.
/// </summary>
internal sealed class CentralProcessingUsageInterceptor(IOptions<CentralProcessingEntitlementOptions>? entitlementOptions = null)
    : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<(Guid JobId, int AttemptNumber)>> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is ApplicationDbContext dbContext && TakePending(dbContext) is { Count: > 0 } attempts)
        {
            CentralProcessingUsageRecorder.RecordAsync(dbContext, entitlementOptions?.Value, attempts, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ApplicationDbContext dbContext && TakePending(dbContext) is { Count: > 0 } attempts)
        {
            await CentralProcessingUsageRecorder.RecordAsync(dbContext, entitlementOptions?.Value, attempts, cancellationToken)
                .ConfigureAwait(false);
        }
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null)
        {
            _pending.Remove(eventData.Context);
        }
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }
        List<(Guid, int)>? attempts = null;
        foreach (var entry in context.ChangeTracker.Entries<CentralDerivativeJobAttempt>())
        {
            var terminal = entry.Entity.Outcome != CentralDerivativeAttemptOutcome.Leased
                && (entry.State == EntityState.Added
                    || (entry.State == EntityState.Modified && entry.Property(attempt => attempt.Outcome).IsModified));
            if (terminal)
            {
                (attempts ??= []).Add((entry.Entity.CentralDerivativeJobId, entry.Entity.AttemptNumber));
            }
        }
        if (attempts is not null)
        {
            _pending.AddOrUpdate(context, attempts);
        }
    }

    private List<(Guid JobId, int AttemptNumber)>? TakePending(DbContext context)
    {
        if (_pending.TryGetValue(context, out var attempts))
        {
            _pending.Remove(context);
            return attempts;
        }
        return null;
    }
}
