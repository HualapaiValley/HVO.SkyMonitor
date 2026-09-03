using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class CentralProcessingGraphExpansionSeal
{
    internal static async Task SealAsync(
        ApplicationDbContext dbContext,
        Guid executionId,
        DateTimeOffset expandedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        if (dbContext.Database.IsSqlServer())
        {
            _ = await dbContext.Database.SqlQuery<int>($"""
                    SELECT CAST(1 AS int) AS [Value]
                    FROM [CentralProcessingGraphExecutions] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {executionId}
                    """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        dbContext.ChangeTracker.Clear();
        var execution = await dbContext.CentralProcessingGraphExecutions.SingleOrDefaultAsync(
            item => item.Id == executionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The processing graph execution does not exist.");
        var sourceCount = await dbContext.CentralProcessingGraphExecutionSources.CountAsync(
            item => item.ExecutionId == executionId, cancellationToken).ConfigureAwait(false);
        var nodeCount = await dbContext.CentralDerivativeJobs.CountAsync(
            item => item.GraphExecutionId == executionId, cancellationToken).ConfigureAwait(false);
        var dependencyCount = await dbContext.CentralDerivativeJobDependencies.CountAsync(
            item => item.ExecutionId == executionId, cancellationToken).ConfigureAwait(false);
        var outputCount = await dbContext.CentralDerivativeJobOutputs.CountAsync(
            item => item.Job!.GraphExecutionId == executionId, cancellationToken).ConfigureAwait(false);
        if (sourceCount != execution.ExpectedSourceCount || nodeCount != execution.ExpectedNodeCount ||
            dependencyCount != execution.ExpectedDependencyCount || outputCount != execution.ExpectedOutputCount)
        {
            throw new InvalidOperationException("The processing graph expansion does not match its frozen counts.");
        }
        if (execution.ExpandedAtUtc is not null)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(expandedAtUtc, execution.CreatedAtUtc);
        execution.ExpandedAtUtc = expandedAtUtc;
        execution.UpdatedAtUtc = expandedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
