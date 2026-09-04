using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
            recorded += await dbContext.Database.ExecuteSqlRawAsync(
                    InsertSql("AND attempt.[CentralDerivativeJobId] = @jobId AND attempt.[AttemptNumber] = @attemptNumber", string.Empty),
                    [Classes(entitlements), new SqlParameter("@jobId", jobId), new SqlParameter("@attemptNumber", attemptNumber)],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return recorded;
    }

    /// <summary>Records usage for terminal attempts that have no usage row yet, oldest first, up to <paramref name="limit"/> rows.</summary>
    public static async Task<int> RecordMissingAsync(
        ApplicationDbContext dbContext,
        CentralProcessingEntitlementOptions? entitlements,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return await dbContext.Database.ExecuteSqlRawAsync(
                InsertSql(string.Empty, "TOP(@limit)"),
                [Classes(entitlements), new SqlParameter("@limit", limit)],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static SqlParameter Classes(CentralProcessingEntitlementOptions? entitlements)
        => new("@classes", System.Data.SqlDbType.NVarChar, -1)
        {
            Value = (entitlements ?? new CentralProcessingEntitlementOptions()).CreateRecipeClassesJson()
        };

    // A lease whose end time was recorded before this claim waited on locks can precede its own lease start; the
    // usage end is clamped to the lease acquisition so the check constraint holds and durations never go negative.
    private static string InsertSql(string targetFilter, string top)
        => $"""
            INSERT INTO [CentralProcessingUsageRecords]
                ([Id], [ObservatoryId], [DevicePublicId], [CentralDerivativeJobId], [AttemptNumber], [RecipeName],
                 [ResourceClass], [WorkerId], [Outcome], [ReasonCode], [LeaseAcquiredAtUtc], [EndedAtUtc],
                 [InputBytes], [OutputBytes], [RecipeDurationTicks], [RecordedAtUtc])
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
            ORDER BY attempt.[EndedAtUtc], attempt.[CentralDerivativeJobId], attempt.[AttemptNumber]
            """;
}
