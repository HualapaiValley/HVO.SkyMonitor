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
internal sealed record CentralProcessingUsageTotal(Guid ObservatoryId, string ResourceClass, string Outcome, long Attempts, long InputBytes, long OutputBytes);

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
        return await dbContext.Database.ExecuteSqlRawAsync(
                InsertSql("AND attempt.[EndedAtUtc] > @since", "TOP(@limit)"),
                [Classes(entitlements), new SqlParameter("@limit", limit), new SqlParameter("@since", since)],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Aggregates committed usage rows by observatory, class, and outcome: every row when <paramref name="since"/>
    /// is null, otherwise the rows recorded in (<paramref name="since"/>, <paramref name="until"/>]. The worker feeds
    /// the cumulative completion and byte counters from these totals, so the counters are a durable global fact
    /// that survives restarts and unscraped intervals and reads the same on every replica.
    /// </summary>
    public static async Task<IReadOnlyList<CentralProcessingUsageTotal>> AggregateAsync(
        ApplicationDbContext dbContext,
        DateTimeOffset? since,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var query = dbContext.CentralProcessingUsageRecords.AsNoTracking().Where(record => record.RecordedAtUtc <= until);
        if (since is { } from)
        {
            query = query.Where(record => record.RecordedAtUtc > from);
        }
        return await query
            .GroupBy(record => new { record.ObservatoryId, record.ResourceClass, record.Outcome })
            .Select(group => new CentralProcessingUsageTotal(
                group.Key.ObservatoryId,
                group.Key.ResourceClass,
                group.Key.Outcome.ToString(),
                group.LongCount(),
                group.Sum(record => record.InputBytes),
                group.Sum(record => record.OutputBytes)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
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
