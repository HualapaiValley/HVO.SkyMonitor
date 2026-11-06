using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

/// <summary>Passive, independently bounded SQL observations for disposable integration-test SQL instances.</summary>
public static class SqlDiagnosticObservation
{
    internal static readonly IReadOnlyDictionary<string, string> Queries = new Dictionary<string, string>
    {
        ["permissions"] = "SELECT @@SPID AS observer_spid, HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER STATE') AS view_state, HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER PERFORMANCE STATE') AS view_performance;",
        ["requests"] = "SELECT TOP (64) session_id,connection_id,blocking_session_id,status,wait_type,wait_time,wait_resource,cpu_time,logical_reads,reads,writes,total_elapsed_time,open_transaction_count,query_hash,query_plan_hash FROM sys.dm_exec_requests WHERE session_id>50 AND session_id<>@@SPID AND status<>'background';",
        ["sessions"] = "SELECT TOP (128) session_id,CASE WHEN program_name = N'HVO.SkyMonitor.QualificationObserver' THEN 1 ELSE 0 END AS is_observer,status,open_transaction_count,last_request_start_time,last_request_end_time,cpu_time,logical_reads,reads,writes FROM sys.dm_exec_sessions WHERE is_user_process=1 AND session_id<>@@SPID;",
        ["grants"] = "SELECT TOP (64) session_id,requested_memory_kb,granted_memory_kb,required_memory_kb,used_memory_kb,wait_time_ms FROM sys.dm_exec_query_memory_grants;",
        ["locks"] = "SELECT TOP (256) request_session_id,resource_type,resource_database_id,resource_associated_entity_id,RTRIM(resource_description) AS resource_description,request_mode,request_status,request_owner_type FROM sys.dm_tran_locks WHERE request_session_id>50 AND (resource_type='APPLICATION' OR request_status<>'GRANT');"
    };

    public static Task WhileAsync(Func<Task> operation, string connectionString, Action<string> write,
        int maximumSamples = 4, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSamples, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumSamples, 300);
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = false, ConnectTimeout = 2, ConnectRetryCount = 0,
            ApplicationName = "HVO.SkyMonitor.QualificationObserver"
        };
        return WhileObservedAsync(operation, token => SampleAsync(
            (name, sample, stop) => ReadQueryAsync(builder.ConnectionString, name, sample, write, stop),
            write, token, maximumSamples, interval), write);
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Independent passive observations must not suppress other sources or replace the subject outcome; only exception types are reported.")]
    internal static async Task SampleAsync(Func<string, int, CancellationToken, Task> read,
        Action<string> write, CancellationToken stop, int maximumSamples = 4, TimeSpan? interval = null)
    {
        using var observationLimit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        observationLimit.CancelAfter(TimeSpan.FromMinutes(5));
        stop = observationLimit.Token;
        var clock = Stopwatch.StartNew();
        try
        {
            for (var sample = 0; sample < maximumSamples; sample++)
            {
                // First cycle preflights all sources immediately; later cycles observe the running subject.
                var delay = sample == 0 ? TimeSpan.Zero : interval ?? TimeSpan.FromSeconds(sample == 1 ? 5 : 8);
                var due = clock.Elapsed + delay;
                if (sample > 0) await Task.Delay(delay, stop).ConfigureAwait(false);
                var schedulingLag = sample == 0 ? 0 : (clock.Elapsed - due).TotalMilliseconds;
                using var process = Process.GetCurrentProcess();
                write($"SQL sample runtime: sample={sample + 1} elapsedMs={clock.ElapsedMilliseconds} schedulingLagMs={schedulingLag:F0} threads={ThreadPool.ThreadCount} pendingWork={ThreadPool.PendingWorkItemCount} managedBytes={GC.GetTotalMemory(false)} workingSetBytes={process.WorkingSet64}");
                await Task.WhenAll(Queries.Keys.Select(name => ObserveQueryAsync(name, sample + 1))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

        async Task ObserveQueryAsync(string name, int sample)
        {
            try { await read(name, sample, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception)
            {
                // One observation source cannot suppress the other sources or a subsequent cycle.
                write($"SQL observer query failed: source={name} sample={sample} type={exception.GetType().Name}");
            }
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Queries are selected solely from the fixed internal read-only diagnostic catalog; no external SQL is accepted.")]
    internal static async Task ReadQueryAsync(string connectionString, string name, int sample,
        Action<string> write, CancellationToken stop)
    {
        var clock = Stopwatch.StartNew();
        var stage = "open";
        try
        {
            // Each source/cycle owns a fresh unpooled session. A timeout cannot poison the next sample.
            await using var connection = new SqlConnection(connectionString);
            using (var opening = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                opening.CancelAfter(TimeSpan.FromSeconds(2));
                await connection.OpenAsync(opening.Token).ConfigureAwait(false);
            }
            stage = "execute";
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = Queries[name];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            stage = "read";
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>();
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = await reader.IsDBNullAsync(index, deadline.Token).ConfigureAwait(false) ? null : reader.GetValue(index);
                    row.Add(reader.GetName(index), value is byte[] hash ? Convert.ToHexString(hash) : value);
                }
                rows.Add(row);
            }
            write($"SQL wait observation: source={name} sample={sample} elapsedMs={clock.ElapsedMilliseconds} rows={JsonSerializer.Serialize(rows)}");
        }
        catch (SqlException exception)
        {
            write($"SQL observer unavailable: source={name} sample={sample} stage={stage} elapsedMs={clock.ElapsedMilliseconds} number={exception.Number}");
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            write($"SQL observer timed out: source={name} sample={sample} stage={stage} elapsedMs={clock.ElapsedMilliseconds}");
        }
    }
}
