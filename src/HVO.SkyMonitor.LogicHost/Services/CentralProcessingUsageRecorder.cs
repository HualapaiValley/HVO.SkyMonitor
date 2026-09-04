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
internal sealed record CentralProcessingUsageSignal(Guid ObservatoryId, string ResourceClass, string Outcome, long InputBytes, long OutputBytes);

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
    /// Takes up to <paramref name="limit"/> usage rows that no replica has signaled yet, marking them signaled in the
    /// same statement, so committed usage rows feed the completion and byte metrics once across any number of
    /// LogicHost replicas (READPAST lets concurrent replicas take disjoint rows without blocking). Callers run it in
    /// a transaction they commit only after emitting the metrics, so an interrupted pass leaves the rows for the next.
    /// </summary>
    public static async Task<IReadOnlyList<CentralProcessingUsageSignal>> TakeUnsignaledAsync(
        ApplicationDbContext dbContext,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var connection = dbContext.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            opened = true;
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE TOP(@limit) usage SET [SignaledAtUtc] = SYSDATETIMEOFFSET()
                OUTPUT inserted.[ObservatoryId], inserted.[ResourceClass], inserted.[Outcome], inserted.[InputBytes], inserted.[OutputBytes]
                FROM [CentralProcessingUsageRecords] AS usage WITH (READPAST)
                WHERE usage.[SignaledAtUtc] IS NULL
                """;
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.Add(new SqlParameter("@limit", limit));
            var signals = new List<CentralProcessingUsageSignal>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                signals.Add(new CentralProcessingUsageSignal(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
            return signals;
        }
        finally
        {
            if (opened)
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
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
