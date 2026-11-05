using System.Data;
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
        await using var connection = await ConnectAsync(builder.ConnectionString, write, CancellationToken.None).ConfigureAwait(false);
        await WhileObservedAsync(operation, token => SampleAsync(connection, write, token,
            retry => ConnectAsync(builder.ConnectionString, write, retry)), write).ConfigureAwait(false);
    }

    private static async Task<SqlConnection?> ConnectAsync(string connectionString, Action<string> write, CancellationToken stop)
    {
        var connection = new SqlConnection(connectionString);
        var clock = Stopwatch.StartNew();
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(stop);
            opening.CancelAfter(TimeSpan.FromSeconds(2));
            await connection.OpenAsync(opening.Token).ConfigureAwait(false);
            write($"SQL observer connected: elapsedMs={clock.ElapsedMilliseconds} session={connection.ServerProcessId}");
            return connection;
        }
        catch (SqlException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            write($"SQL observer connection unavailable: number={exception.Number}");
            return null;
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            write("SQL observer connection timed out");
            return null;
        }
        catch (InvalidOperationException) when (connection.State != ConnectionState.Open)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            write("SQL observer connection closed during setup");
            return null;
        }
    }

    internal static async Task WhileObservedAsync(
        Func<Task> operation, Func<CancellationToken, Task> sample, Action<string> write)
    {
        using var stop = new CancellationTokenSource();
        var sampling = sample(stop.Token);
        try { await operation().ConfigureAwait(false); }
        finally
        {
            await DrainDiagnosticAsync(stop.CancelAsync(), write).ConfigureAwait(false);
            await DrainDiagnosticAsync(sampling, write).ConfigureAwait(false);
        }
    }

    // Passive evidence must never replace the subject outcome, including if reporting itself fails.
    private static async Task DrainDiagnosticAsync(Task diagnostic, Action<string> write)
    {
        var report = diagnostic.ContinueWith(completed =>
        {
            if (completed.Exception is { } error)
            {
                write($"SQL observer stopped unexpectedly: type={error.GetBaseException().GetType().Name}");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await report.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
    }

    internal static async Task SampleAsync(SqlConnection? connection, Action<string> write, CancellationToken stop,
        Func<CancellationToken, Task<SqlConnection?>>? reconnect = null, TimeSpan? samplingDelay = null)
    {
        SqlConnection? ownedConnection = null;
        var clock = Stopwatch.StartNew();
        try
        {
            for (var sample = 0; sample < 3; sample++)
            {
                if (connection?.State != ConnectionState.Open && reconnect is null)
                {
                    write("SQL observer disconnected; sampling stopped");
                    return;
                }
                var delay = samplingDelay ?? TimeSpan.FromSeconds(sample == 0 ? 5 : 8);
                var due = clock.Elapsed + delay;
                await Task.Delay(delay, stop).ConfigureAwait(false);
                using var process = Process.GetCurrentProcess();
                write($"SQL sample runtime: sample={sample + 1} elapsedMs={clock.ElapsedMilliseconds} schedulingLagMs={(clock.Elapsed - due).TotalMilliseconds:F0} threads={ThreadPool.ThreadCount} pendingWork={ThreadPool.PendingWorkItemCount} managedBytes={GC.GetTotalMemory(false)} workingSetBytes={process.WorkingSet64}");
                if (connection?.State != ConnectionState.Open)
                {
                    if (reconnect is null) { write("SQL observer disconnected; sampling stopped"); return; }
                    if (ownedConnection is not null) { await ownedConnection.DisposeAsync().ConfigureAwait(false); }
                    connection = await reconnect(stop).ConfigureAwait(false);
                    ownedConnection = connection;
                    if (connection is null) { write($"SQL sample {sample + 1}: connection unavailable"); continue; }
                }
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
                catch (SqlException exception)
                {
                    write($"SQL observer command unavailable: number={exception.Number}");
                    if (connection.State != ConnectionState.Open && reconnect is null) { return; }
                }
                catch (InvalidOperationException) when (connection.State != ConnectionState.Open)
                {
                    write(reconnect is null ? "SQL observer disconnected; sampling stopped" : "SQL observer disconnected; retry at next sample");
                    if (reconnect is null) { return; }
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { write("SQL observer command timed out"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (ownedConnection is not null) { await ownedConnection.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
