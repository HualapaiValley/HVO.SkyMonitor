using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

/// <summary>Bounded test-only sampling; never logs SQL, parameter values, credentials or object payloads.</summary>
internal static class SqlWaitEvidence
{
    internal static async Task WhileAsync(Func<Task> operation, string connectionString, Action<string> write)
    {
        using var stop = new CancellationTokenSource();
        var sampling = SampleAsync(connectionString, write, stop.Token);
        try { await operation().ConfigureAwait(false); }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await sampling.ConfigureAwait(false);
        }
    }

    private static async Task SampleAsync(string connectionString, Action<string> write, CancellationToken stop)
    {
        try
        {
            // Three samples at most, each with a private connection and a two-second bound.
            for (var sample = 0; sample < 3; sample++)
            {
                await Task.Delay(TimeSpan.FromSeconds(sample == 0 ? 5 : 8), stop).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectTimeout = 2 };
                    await using var connection = new SqlConnection(builder.ConnectionString);
                    await connection.OpenAsync(timeout.Token).ConfigureAwait(false);
                    await using var command = connection.CreateCommand();
                    command.CommandTimeout = 2;
                    command.CommandText = """
                        SELECT TOP (32) s.session_id, s.open_transaction_count,
                            r.blocking_session_id, r.wait_type, r.wait_time,
                            r.cpu_time, r.logical_reads, r.total_elapsed_time,
                            HASHBYTES('SHA2_256', t.text) AS command_hash
                        FROM sys.dm_exec_sessions s
                        LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
                        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                        WHERE s.is_user_process = 1 AND s.session_id <> @@SPID
                            AND (r.database_id = DB_ID() OR s.open_transaction_count > 0)
                        ORDER BY r.blocking_session_id DESC, r.total_elapsed_time DESC, s.session_id
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
                catch (SqlException exception) { write($"SQL wait sample unavailable: number={exception.Number}"); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { write("SQL wait sample timed out"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
