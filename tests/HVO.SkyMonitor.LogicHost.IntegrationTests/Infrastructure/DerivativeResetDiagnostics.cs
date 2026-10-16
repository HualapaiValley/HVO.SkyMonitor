using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

/// <summary>One explicitly selected reset per test process; never changes reset SQL or its timeout.</summary>
internal static class DerivativeResetDiagnostics
{
    private static readonly ConcurrentDictionary<string, int> Occurrences = new(StringComparer.Ordinal);
    private static int _claimed;
    internal const string ContainerCounters = "cat /sys/fs/cgroup/cpu.stat /sys/fs/cgroup/memory.events /sys/fs/cgroup/io.stat; "
        + "printf 'memory.current '; cat /sys/fs/cgroup/memory.current; printf 'memory.swap.current '; cat /sys/fs/cgroup/memory.swap.current";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Opt-in diagnostics must preserve the original reset exception; only closed error codes are retained.")]
    internal static async Task RunAsync(ApplicationDbContext db, string site, Func<Task> reset,
        Func<CancellationToken, Task<(string Id, long? ExitCode, string Counters)>> container)
    {
        var directory = Environment.GetEnvironmentVariable("HVO_RESET_DIAGNOSTICS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            await reset().ConfigureAwait(false);
            return;
        }
        var occurrence = Occurrences.AddOrUpdate(site, 1, (_, current) => current + 1);
        var selection = string.Create(CultureInfo.InvariantCulture, $"{site}:{occurrence}");
        if (!string.Equals(Environment.GetEnvironmentVariable("HVO_RESET_DIAGNOSTICS_SELECT"), selection, StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            await reset().ConfigureAwait(false);
            return;
        }

        using var deadline = new CancellationTokenSource(DerivativeResetEvidence.Window);
        var samples = new List<object>();
        var errors = new List<object>();
        var plans = new List<object>();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        SqlConnection? observer = null;
        Task? sampling = null;
        var finished = 0;
        var session = 0;
        long? targetRows = null;
        var started = DateTimeOffset.UtcNow;
        DateTimeOffset? ended = null;
        var succeeded = false;
        int? resetSqlNumber = null;
        try
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false, ConnectTimeout = 2 };
                if (opened) { await db.Database.OpenConnectionAsync(deadline.Token).ConfigureAwait(false); }
                session = connection.ServerProcessId;
                observer = new SqlConnection(builder.ConnectionString);
                await observer.OpenAsync(deadline.Token).ConfigureAwait(false);
                using var count = observer.CreateCommand();
                count.CommandTimeout = 2;
                count.CommandText = "SET LOCK_TIMEOUT 1000; SELECT COUNT_BIG(*) FROM dbo.CentralDerivativeJobs WITH (READUNCOMMITTED) WHERE Status <> @completed";
                count.Parameters.AddWithValue("@completed", "Completed");
                targetRows = Convert.ToInt64(await count.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false), CultureInfo.InvariantCulture);
                sampling = DerivativeResetEvidence.SampleAsync(async token =>
                {
                    try
                    {
                        var utc = DateTimeOffset.UtcNow;
                        var sessions = await ReadSessionsAsync(observer, session, token).ConfigureAwait(false);
                        var resources = await container(token).ConfigureAwait(false);
                        samples.Add(new { utc, sessions, host = DerivativeResetEvidence.HostSnapshot(),
                            containerId = resources.Id, containerExit = resources.ExitCode,
                            containerCounters = DerivativeResetEvidence.NumericLines(resources.Counters) });
                    }
                    catch (Exception error) { errors.Add(Error(error)); }
                }, () => Volatile.Read(ref finished) != 0, TimeProvider.System, deadline.Token);
            }
            catch (Exception error) { errors.Add(Error(error)); }

            started = DateTimeOffset.UtcNow;
            try
            {
                await reset().ConfigureAwait(false);
                succeeded = true;
            }
            catch (SqlException error)
            {
                resetSqlNumber = error.Number;
                throw;
            }
            finally
            {
                ended = DateTimeOffset.UtcNow;
                Volatile.Write(ref finished, 1);
                try
                {
                    if (sampling is not null) { await sampling.ConfigureAwait(false); }
                    if (observer is not null && observer.State == ConnectionState.Open && !deadline.IsCancellationRequested)
                    {
                        plans.AddRange(await ReadPlansAsync(observer, deadline.Token).ConfigureAwait(false));
                    }
                }
                catch (Exception error) { errors.Add(Error(error)); }
            }
        }
        finally
        {
            try
            {
                await P5TrialDiagnostics.WriteAsync(directory, "derivative-reset.json", new
                {
                    schemaVersion = 1, selection, processId = Environment.ProcessId,
                    session, started, ended, succeeded, resetSqlNumber, commandTimeoutSeconds = db.Database.GetCommandTimeout() ?? 30,
                    targetRowsReadUncommitted = targetRows,
                    maximumSamples = DerivativeResetEvidence.MaximumSamples, samples, plans, errors,
                    ownerAttribution = "Session/process and transaction lifetime observations only; worker ownership requires separate stack correlation."
                }, DerivativeResetEvidence.MaximumBytes).ConfigureAwait(false);
            }
            catch (Exception error) { Console.WriteLine($"Reset diagnostic retention failed: {Error(error)}"); }
            try
            {
                if (observer is not null) { await observer.DisposeAsync().ConfigureAwait(false); }
                if (opened) { await db.Database.CloseConnectionAsync().ConfigureAwait(false); }
            }
            catch (Exception error) { Console.WriteLine($"Reset diagnostic disposal failed: {Error(error)}"); }
        }
    }

    private static object Error(Exception error) => new
    {
        kind = error is SqlException ? "sql" : error is OperationCanceledException ? "deadline" : "collector",
        sqlNumber = error is SqlException sql ? sql.Number : (int?)null
    };

    private static async Task<List<object>> ReadSessionsAsync(SqlConnection connection, int resetSession, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = """
            SELECT TOP (64) s.session_id, s.login_time, s.host_process_id, s.open_transaction_count,
                s.cpu_time AS session_cpu_ms, s.reads AS session_reads, s.writes AS session_writes, s.logical_reads AS session_logical_reads, r.blocking_session_id, r.wait_type,
                r.wait_time, r.wait_resource, r.cpu_time AS request_cpu_ms, r.reads AS request_reads, r.writes AS request_writes, r.logical_reads AS request_logical_reads,
                r.query_hash, r.query_plan_hash, r.start_time,
                (SELECT MIN(t.transaction_begin_time) FROM sys.dm_tran_session_transactions st
                 JOIN sys.dm_tran_active_transactions t ON st.transaction_id = t.transaction_id
                 WHERE st.session_id = s.session_id) AS oldest_transaction,
                HASHBYTES('SHA2_256', txt.text) AS command_hash,
                c.connection_id
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
            LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
            OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) txt
            WHERE s.is_user_process = 1 AND s.session_id <> @@SPID
            ORDER BY CASE WHEN s.session_id = @reset THEN 0 WHEN s.open_transaction_count > 0 THEN 1 ELSE 2 END, s.session_id
            """;
        command.Parameters.AddWithValue("@reset", resetSession);
        var result = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = await reader.IsDBNullAsync(index, token).ConfigureAwait(false) ? null : reader.GetValue(index);
                values.Add(reader.GetName(index), value switch
                {
                    byte[] bytes => Convert.ToHexString(bytes),
                    string text when index == 11 => SafeWaitResource(text),
                    string text when index == 9 && text.Length <= 64 && text.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') => text,
                    string => "redacted",
                    _ => value
                });
            }
            result.Add(values);
        }
        return result;
    }

    private static string SafeWaitResource(string value)
    {
        var prefix = value.Split(':', 2)[0];
        return prefix is "KEY" or "PAGE" or "RID" or "OBJECT" or "DATABASE"
            && value.Length > prefix.Length && value.Length <= 128 && value[(prefix.Length + 1)..].All(character => char.IsAsciiHexDigit(character) || character is ' ' or ':' or '(' or ')')
            ? value : "sha256:" + DerivativeResetEvidence.Hash(value);
    }

    private static async Task<List<object>> ReadPlansAsync(SqlConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = """
            WITH candidates AS (
                SELECT q.query_hash, q.query_plan_hash, q.plan_handle, q.last_execution_time,
                    HASHBYTES('SHA2_256', t.text) AS command_hash,
                    CASE WHEN t.objectid = OBJECT_ID('dbo.TR_CentralDerivativeJobs_GraphIdentityImmutable') THEN 1 ELSE 0 END AS is_trigger,
                    ROW_NUMBER() OVER (PARTITION BY CASE WHEN t.objectid = OBJECT_ID('dbo.TR_CentralDerivativeJobs_GraphIdentityImmutable')
                        THEN 1 ELSE 0 END ORDER BY q.last_execution_time DESC) AS candidate_rank
                FROM sys.dm_exec_query_stats q
                CROSS APPLY sys.dm_exec_sql_text(q.sql_handle) t
                WHERE ((CHARINDEX('WHERE [c].[Status] <> N''Completed''', t.text) > 0
                        AND CHARINDEX('[c].[LeaseExpiresAtUtc] = NULL', t.text) > 0
                        AND t.text LIKE '%UPDATE%' AND t.text NOT LIKE '%sys.dm_exec_query_stats%')
                    OR t.objectid = OBJECT_ID('dbo.TR_CentralDerivativeJobs_GraphIdentityImmutable'))
            )
            SELECT q.query_hash, q.query_plan_hash, q.command_hash, CONVERT(nvarchar(max), p.query_plan), q.last_execution_time, q.is_trigger
            FROM candidates q
            CROSS APPLY sys.dm_exec_query_plan(q.plan_handle) p
            WHERE q.candidate_rank <= 2 AND p.dbid = DB_ID()
                AND DATALENGTH(CONVERT(nvarchar(max), p.query_plan)) <= 1048576
            ORDER BY q.is_trigger, q.last_execution_time DESC
            """;
        var result = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            result.Add(new
            {
                kind = "cached-estimated-projection-not-actual", source = reader.GetInt32(5) == 1 ? "graph-identity-trigger" : "reset-sql-shape", queryHash = Convert.ToHexString((byte[])reader[0]),
                planHash = Convert.ToHexString((byte[])reader[1]), commandHash = Convert.ToHexString((byte[])reader[2]),
                projection = DerivativeResetEvidence.ProjectPlan(reader.GetString(3)).ToString(), lastExecution = reader.GetDateTime(4)
            });
        }
        return result;
    }
}
