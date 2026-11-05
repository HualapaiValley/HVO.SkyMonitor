using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

/// <summary>Bounded test-only sampling; never logs SQL, parameter values, credentials or object payloads.</summary>
internal static class SqlWaitEvidence
{
    internal static async Task WhileAsync(Func<Task> operation, string connectionString, Action<string> write)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ConnectTimeout = 2,
            ApplicationName = "HVO.SkyMonitor.QualificationObserver"
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        // Establish the observer before invoking the delegate. Already-running requests retain
        // their own deadlines. The old combined connection/read deadline hid which phase failed.
        var openingClock = Stopwatch.StartNew();
        try
        {
            using var opening = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connection.OpenAsync(opening.Token).ConfigureAwait(false);
            write($"SQL observer connected: elapsedMs={openingClock.ElapsedMilliseconds} session={connection.ServerProcessId}");
        }
        catch (SqlException exception)
        {
            write($"SQL observer connection unavailable: number={exception.Number}");
            await operation().ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            write("SQL observer connection timed out");
            await operation().ConfigureAwait(false);
            return;
        }
        using var stop = new CancellationTokenSource();
        var sampling = SampleAsync(connection, write, stop.Token);
        try { await operation().ConfigureAwait(false); }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await sampling.ConfigureAwait(false);
        }
    }

    private static async Task SampleAsync(SqlConnection connection, Action<string> write, CancellationToken stop)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            for (var sample = 0; sample < 3; sample++)
            {
                var delay = TimeSpan.FromSeconds(sample == 0 ? 5 : 8);
                var due = clock.Elapsed + delay;
                await Task.Delay(delay, stop).ConfigureAwait(false);
                using var process = Process.GetCurrentProcess();
                write($"SQL sample runtime: sample={sample + 1} elapsedMs={clock.ElapsedMilliseconds} schedulingLagMs={(clock.Elapsed - due).TotalMilliseconds:F0} threads={ThreadPool.ThreadCount} pendingWork={ThreadPool.PendingWorkItemCount} managedBytes={GC.GetTotalMemory(false)} workingSetBytes={process.WorkingSet64}");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandTimeout = 2;
                    // Avoid SQL-text retrieval/hashing; connection IDs correlate directly with SqlClient errors.
                    command.CommandText = """
                        SELECT TOP (32) s.session_id, COALESCE(r.connection_id, c.connection_id) AS connection_id,
                            s.open_transaction_count, s.last_request_start_time, s.last_request_end_time,
                            r.blocking_session_id, r.wait_type, r.wait_time, r.cpu_time,
                            r.logical_reads, r.total_elapsed_time, r.query_hash, r.query_plan_hash,
                            g.requested_memory_kb, g.granted_memory_kb, r.row_count
                        FROM sys.dm_exec_sessions s
                        LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
                        LEFT JOIN sys.dm_exec_connections c ON s.session_id = c.session_id
                        LEFT JOIN sys.dm_exec_query_memory_grants g ON s.session_id = g.session_id
                        WHERE s.session_id <> @@SPID AND s.is_user_process = 1
                            AND (r.database_id = DB_ID() OR s.open_transaction_count > 0)
                        ORDER BY CASE WHEN s.open_transaction_count > 0 THEN 0 ELSE 1 END,
                            r.total_elapsed_time DESC, s.session_id
                        """;
                    var rows = new List<object>();
                    await using var reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
                    while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                    {
                        var row = new Dictionary<string, object?>();
                        for (var index = 0; index < reader.FieldCount; index++)
                        {
                            var value = await reader.IsDBNullAsync(index, timeout.Token).ConfigureAwait(false) ? null : reader.GetValue(index);
                            row.Add(reader.GetName(index), value is byte[] hash ? Convert.ToHexString(hash) : value);
                        }
                        rows.Add(row);
                    }
                    write($"SQL wait sample {sample + 1}: {JsonSerializer.Serialize(rows)}");
                }
                catch (SqlException exception) { write($"SQL observer command unavailable: number={exception.Number}"); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { write("SQL observer command timed out"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
