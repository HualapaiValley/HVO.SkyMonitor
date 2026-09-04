using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>
/// Writes the auditable <c>CentralProcessingUsageRecords</c> row for terminal derivative attempts (#429). The insert
/// is idempotent per attempt (a unique index and a NOT EXISTS guard) and runs on the caller's connection, so a caller
/// inside a transaction records usage atomically with the attempt's terminal update. Every direct terminalization
/// site calls it, <see cref="CentralProcessingUsageInterceptor"/> covers attempts terminalized through tracked
/// entities, and <see cref="RecordMissingAsync"/> is the periodic safety net for any path that slipped through.
/// </summary>
internal static class CentralProcessingUsageRecorder
{
    public static Task<int> RecordAsync(
        ApplicationDbContext dbContext,
        CentralProcessingEntitlementOptions? entitlements,
        Guid jobId,
        int attemptNumber,
        CancellationToken cancellationToken)
        => RecordAsync(dbContext, entitlements, [(jobId, attemptNumber)], cancellationToken);

    /// <summary>
    /// Records each attempt with an index seek on (job, attempt). One statement per attempt keeps the plan a seek:
    /// a set-based join against a table-valued parameter can scan the attempts table and block on (or deadlock with)
    /// a claimer's uncommitted attempt row, whereas a seek touches only the caller's own attempt.
    /// </summary>
    public static async Task<int> RecordAsync(
        ApplicationDbContext dbContext,
        CentralProcessingEntitlementOptions? entitlements,
        IReadOnlyCollection<(Guid JobId, int AttemptNumber)> attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(attempts);
        var recorded = 0;
        foreach (var (jobId, attemptNumber) in attempts.Distinct())
        {
            recorded += await ExecuteRecordAsync(dbContext,
                    InsertSql("AND attempt.[CentralDerivativeJobId] = @jobId AND attempt.[AttemptNumber] = @attemptNumber", string.Empty),
                    [Classes(entitlements), new SqlParameter("@jobId", jobId), new SqlParameter("@attemptNumber", attemptNumber)],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return recorded;
    }

    /// <summary>
    /// Records usage for terminal attempts that have no usage row yet, oldest first, up to <paramref name="limit"/>
    /// rows. With a <paramref name="window"/> only attempts that ended within it are examined (an index range on
    /// <c>EndedAtUtc</c>), which keeps the periodic safety-net sweep bounded on a long-lived installation; without a
    /// window the whole history is repaired.
    /// </summary>
    public static async Task<int> RecordMissingAsync(
        ApplicationDbContext dbContext,
        CentralProcessingEntitlementOptions? entitlements,
        int limit,
        TimeSpan? window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var since = window is { } span && span > TimeSpan.Zero ? DateTimeOffset.UtcNow - span : DateTimeOffset.MinValue;
        return await ExecuteRecordAsync(dbContext,
                InsertSql("AND attempt.[EndedAtUtc] > @since", "TOP(@limit)"),
                [Classes(entitlements), new SqlParameter("@limit", limit), new SqlParameter("@since", since)],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the record batch (ledger insert plus rollup merge) on the caller's connection and transaction and returns
    /// the number of usage rows written. Without a caller transaction (the SaveChanges interceptor after its own
    /// save, the periodic sweep) it opens one of its own, so the ledger row and its rollup delta are always committed
    /// or rolled back together; a ledger row without its delta would otherwise be skipped by every later retry.
    /// </summary>
    private static async Task<int> ExecuteRecordAsync(
        ApplicationDbContext dbContext, string sql, SqlParameter[] parameters, CancellationToken cancellationToken)
    {
        var ownTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        await using (ownTransaction)
        {
            var connection = dbContext.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The text is a constant template; every runtime value is a SqlParameter.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandTimeout = dbContext.Database.GetCommandTimeout() ?? command.CommandTimeout;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var recorded = result is int count ? count : 0;
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return recorded;
        }
    }

    private static SqlParameter Classes(CentralProcessingEntitlementOptions? entitlements)
        => new("@classes", System.Data.SqlDbType.NVarChar, -1)
        {
            Value = (entitlements ?? new CentralProcessingEntitlementOptions()).CreateRecipeClassesJson()
        };

    // A lease whose end time was recorded before this claim waited on locks can precede its own lease start; the
    // usage end is clamped to the lease acquisition so the check constraint holds and durations never go negative.
    // The rollup is maintained in the same batch (and therefore the caller's transaction) as the usage rows it
    // summarizes, so the metrics read from it are exact and never require aggregating the ledger.
    private static string InsertSql(string targetFilter, string top)
        => $"""
            DECLARE @recorded TABLE ([ObservatoryId] uniqueidentifier, [ResourceClass] nvarchar(64), [Outcome] nvarchar(32), [InputBytes] bigint, [OutputBytes] bigint);
            INSERT INTO [CentralProcessingUsageRecords]
                ([Id], [ObservatoryId], [DevicePublicId], [CentralDerivativeJobId], [AttemptNumber], [RecipeName],
                 [ResourceClass], [WorkerId], [Outcome], [ReasonCode], [LeaseAcquiredAtUtc], [EndedAtUtc],
                 [InputBytes], [OutputBytes], [RecipeDurationTicks], [RecordedAtUtc])
            OUTPUT inserted.[ObservatoryId], inserted.[ResourceClass], inserted.[Outcome], inserted.[InputBytes], inserted.[OutputBytes] INTO @recorded
            SELECT {top} NEWID(), frame.[ObservatoryId], frame.[DevicePublicId], attempt.[CentralDerivativeJobId], attempt.[AttemptNumber],
                   job.[RecipeName], COALESCE(rc.[cls], N'image'), attempt.[WorkerId], attempt.[Outcome], LEFT(attempt.[ReasonCode], 256),
                   attempt.[LeaseAcquiredAtUtc],
                   CASE WHEN COALESCE(attempt.[EndedAtUtc], SYSDATETIMEOFFSET()) < attempt.[LeaseAcquiredAtUtc]
                        THEN attempt.[LeaseAcquiredAtUtc]
                        ELSE COALESCE(attempt.[EndedAtUtc], SYSDATETIMEOFFSET()) END,
                   attempt.[InputBytes], attempt.[OutputBytes], attempt.[RecipeDurationTicks], SYSDATETIMEOFFSET()
            FROM [CentralDerivativeJobAttempts] AS attempt
            INNER JOIN [CentralDerivativeJobs] AS job ON job.[Id] = attempt.[CentralDerivativeJobId]
            INNER JOIN [CentralArtifacts] AS source ON source.[Id] = job.[SourceCentralArtifactId]
            INNER JOIN [CentralFrames] AS frame ON frame.[Id] = source.[CentralFrameId]
            LEFT JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls') AS rc ON rc.[r] = job.[RecipeName]
            WHERE attempt.[Outcome] <> N'Leased' {targetFilter}
              AND NOT EXISTS (SELECT 1 FROM [CentralProcessingUsageRecords] AS existing
                              WHERE existing.[CentralDerivativeJobId] = attempt.[CentralDerivativeJobId]
                                AND existing.[AttemptNumber] = attempt.[AttemptNumber])
            ORDER BY attempt.[EndedAtUtc], attempt.[CentralDerivativeJobId], attempt.[AttemptNumber];
            MERGE [CentralProcessingUsageRollups] WITH (HOLDLOCK) AS rollup
            USING (SELECT [ObservatoryId], [ResourceClass], [Outcome], COUNT(*) AS [Attempts], SUM([InputBytes]) AS [InputBytes], SUM([OutputBytes]) AS [OutputBytes]
                   FROM @recorded GROUP BY [ObservatoryId], [ResourceClass], [Outcome]) AS delta
                ON rollup.[ObservatoryId] = delta.[ObservatoryId] AND rollup.[ResourceClass] = delta.[ResourceClass] AND rollup.[Outcome] = delta.[Outcome]
            WHEN MATCHED THEN UPDATE SET
                [Attempts] = rollup.[Attempts] + delta.[Attempts],
                [InputBytes] = rollup.[InputBytes] + delta.[InputBytes],
                [OutputBytes] = rollup.[OutputBytes] + delta.[OutputBytes],
                [UpdatedAtUtc] = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN INSERT ([ObservatoryId], [ResourceClass], [Outcome], [Attempts], [InputBytes], [OutputBytes], [UpdatedAtUtc])
                VALUES (delta.[ObservatoryId], delta.[ResourceClass], delta.[Outcome], delta.[Attempts], delta.[InputBytes], delta.[OutputBytes], SYSDATETIMEOFFSET());
            SELECT COUNT(*) FROM @recorded;
            """;
}
