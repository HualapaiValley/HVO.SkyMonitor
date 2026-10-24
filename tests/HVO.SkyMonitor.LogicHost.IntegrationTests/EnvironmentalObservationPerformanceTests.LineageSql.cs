using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class EnvironmentalObservationPerformanceTests
{
    private sealed record LineageCommand(string Text, SqlParameter[] Parameters);

    private sealed class LineageProbe(bool capture) : DbCommandInterceptor
    {
        internal List<LineageCommand> Commands { get; } = [];
        internal int CommandCount { get; private set; }
        internal int SourceLookups { get; private set; }
        internal int ObservationLookups { get; private set; }
        internal long TransactionStarted { get; set; }
        internal long RetentionAcquired { get; private set; }
        internal double TransactionMilliseconds { get; set; }
        internal double RetentionHeldMilliseconds { get; set; }
        internal double SourceLockMilliseconds { get; private set; }
        internal double RetentionAcquireMilliseconds { get; private set; }
        internal LineageTransactions Transactions { get; } = new();

        internal void Finish()
        {
            if (TransactionStarted != 0)
                TransactionMilliseconds = Stopwatch.GetElapsedTime(TransactionStarted).TotalMilliseconds;
            if (RetentionAcquired != 0)
                RetentionHeldMilliseconds = Stopwatch.GetElapsedTime(RetentionAcquired).TotalMilliseconds;
        }

        private void Record(DbCommand command)
        {
            CommandCount++;
            if (command.CommandText.Contains("FROM [EnvironmentalObservationSources]", StringComparison.Ordinal)) SourceLookups++;
            if (command.CommandText.Contains("FROM [EnvironmentalObservations]", StringComparison.Ordinal)) ObservationLookups++;
            if (capture) Commands.Add(new(command.CommandText, command.Parameters.Cast<SqlParameter>().Select(CopyLineageParameter).ToArray()));
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal))
            {
                if (command.Parameters.Cast<SqlParameter>().Any(parameter =>
                    Equals(parameter.Value, "environmental-observation-retention")))
                {
                    RetentionAcquired = Stopwatch.GetTimestamp();
                    RetentionAcquireMilliseconds = eventData.Duration.TotalMilliseconds;
                }
                else SourceLockMilliseconds = eventData.Duration.TotalMilliseconds;
            }
            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal))
            {
                if (command.Parameters.Cast<SqlParameter>().Any(parameter =>
                    Equals(parameter.Value, "environmental-observation-retention")))
                    RetentionAcquireMilliseconds = eventData.Duration.TotalMilliseconds;
                else SourceLockMilliseconds = eventData.Duration.TotalMilliseconds;
            }
            return Task.CompletedTask;
        }

        internal sealed class LineageTransactions : DbTransactionInterceptor
        {
            internal LineageProbe? Owner { get; set; }

            public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
                TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
            {
                Owner!.TransactionStarted = Stopwatch.GetTimestamp();
                return ValueTask.FromResult(result);
            }

            public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
                CancellationToken cancellationToken = default)
            {
                Owner!.TransactionMilliseconds = Stopwatch.GetElapsedTime(Owner.TransactionStarted).TotalMilliseconds;
                if (Owner.RetentionAcquired != 0)
                    Owner.RetentionHeldMilliseconds = Stopwatch.GetElapsedTime(Owner.RetentionAcquired).TotalMilliseconds;
                return Task.CompletedTask;
            }
        }
    }

    private static async Task<object> ReadLineageDeadlocksAsync(string connectionString, string output)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(nvarchar(max), CAST(target.target_data AS xml).query('RingBufferTarget/event[@name="xml_deadlock_report"]'))
            FROM sys.dm_xe_session_targets AS target
            JOIN sys.dm_xe_sessions AS session ON session.address=target.event_session_address
            WHERE session.name=N'system_health' AND target.target_name=N'ring_buffer';
            """;
        var xml = (string?)await command.ExecuteScalarAsync().ConfigureAwait(false) ?? string.Empty;
        var document = XDocument.Parse("<events>" + xml + "</events>");
        // The disposable server contains synthetic inputs, but XE also includes the physical client identity.
        foreach (var attribute in document.Descendants().Attributes().Where(attribute =>
            attribute.Name.LocalName is "hostname" or "hostpid" or "loginname")) attribute.Value = "redacted";
        await File.WriteAllTextAsync(Path.Combine(output, "deadlocks.xml"), document.ToString()).ConfigureAwait(false);
        return new
        {
            Reports = document.Descendants("deadlock").Count(),
            Artifact = "deadlocks.xml",
            Source = "Disposable fixture server system_health ring_buffer; asynchronous/bounded event retention may omit a graph",
            SanitizedAttributes = new[] { "hostname", "hostpid", "loginname" }
        };
    }

    private static SqlParameter CopyLineageParameter(SqlParameter parameter) => new(parameter.ParameterName, parameter.SqlDbType, parameter.Size)
    {
        Value = parameter.Value,
        Precision = parameter.Precision,
        Scale = parameter.Scale,
        Direction = parameter.Direction,
        IsNullable = parameter.IsNullable
    };

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Replays only captured production EF command text with its original typed parameters in a disposable database.")]
    private static async Task<object> ReplayLineageCommandsAsync(string connectionString, List<LineageCommand> commands, string output, int minimumSelectPlans = 514, string artifact = "sql-replay.json.gz")
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        await using (var settings = connection.CreateCommand())
        {
            settings.Transaction = transaction;
            settings.CommandText = "SET STATISTICS XML ON; SET STATISTICS IO ON;";
            await settings.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var evidence = new List<object>();
        long totalReads = 0;
        long totalRowsRead = 0;
        double serverCpu = 0;
        var plansCount = 0;
        try
        {
            foreach (var captured in commands)
            {
                messages.Clear();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = captured.Text;
                command.Parameters.AddRange(captured.Parameters.Select(CopyLineageParameter).ToArray());
                var plans = new List<string>();
                long returnedRows = 0;
                await using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    do
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            if (reader.FieldCount == 1 && reader.GetValue(0) is string xml &&
                                xml.Contains("<ShowPlanXML", StringComparison.Ordinal)) plans.Add(xml);
                            else returnedRows++;
                        }
                    } while (await reader.NextResultAsync().ConfigureAwait(false));
                }
                var documents = plans.Select(XDocument.Parse).ToArray();
                var operators = documents.SelectMany(plan => plan.Descendants().Where(node => node.Name.LocalName == "RelOp"))
                    .Select(node => new
                    {
                        NodeId = (int?)node.Attribute("NodeId"),
                        Logical = (string?)node.Attribute("LogicalOp"),
                        Physical = (string?)node.Attribute("PhysicalOp"),
                        EstimatedRows = (double?)node.Attribute("EstimateRows"),
                        ActualRows = node.Elements().Where(item => item.Name.LocalName == "RunTimeInformation")
                            .SelectMany(item => item.Elements()).Sum(item => (long?)item.Attribute("ActualRows") ?? 0),
                        ActualRowsRead = node.Elements().Where(item => item.Name.LocalName == "RunTimeInformation")
                            .SelectMany(item => item.Elements()).Sum(item => (long?)item.Attribute("ActualRowsRead") ?? 0)
                    }).ToArray();
                var reads = Regex.Matches(messages.ToString(), @"logical reads (\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                var cpu = documents.SelectMany(plan => plan.Descendants().Where(node => node.Name.LocalName == "QueryTimeStats"))
                    .Sum(node => (double?)node.Attribute("CpuTime") ?? 0);
                totalReads += reads;
                totalRowsRead += operators.Sum(item => item.ActualRowsRead);
                serverCpu += cpu;
                plansCount += plans.Count;
                evidence.Add(new
                {
                    Ordinal = evidence.Count,
                    Sql = captured.Text,
                    Parameters = captured.Parameters.Select(parameter => new
                    {
                        parameter.ParameterName,
                        Type = parameter.SqlDbType.ToString(),
                        parameter.Size,
                        parameter.Precision,
                        parameter.Scale,
                        Value = parameter.Value is DBNull ? null : parameter.Value
                    }),
                    ReturnedRows = returnedRows,
                    LogicalReads = reads,
                    ServerCpuMilliseconds = cpu,
                    Operators = operators,
                    IoMessages = messages.ToString(),
                    ActualPlans = plans
                });
            }
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        Assert.IsTrue(plansCount >= minimumSelectPlans, "Capture actual plans for own identity and every lineage SELECT.");
        var path = Path.Combine(output, artifact);
        await using (var stream = File.Create(path))
        await using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize))
        {
            await JsonSerializer.SerializeAsync(gzip, evidence, EvidenceJsonOptions).ConfigureAwait(false);
        }
        return new
        {
            Commands = commands.Count,
            ActualPlans = plansCount,
            LogicalReads = totalReads,
            ActualRowsRead = totalRowsRead,
            ServerCpuMilliseconds = serverCpu,
            Artifact = artifact,
            MaximumParameters = commands.Max(command => command.Parameters.Length),
            RpcParameterOverheadAllowance = 3,
            Boundary = "One untimed production command sequence; exact parameters, all writes rolled back"
        };
    }

    private sealed record LineageStorage(long LeafInserts, long LeafUpdates, long LeafDeletes,
        long LogPhysicalBytesWritten, long LogFileBytes, long LogUsedBytes)
    {
        internal LineageStorage Delta(LineageStorage before) => new(LeafInserts - before.LeafInserts,
            LeafUpdates - before.LeafUpdates, LeafDeletes - before.LeafDeletes,
            LogPhysicalBytesWritten - before.LogPhysicalBytesWritten, LogFileBytes - before.LogFileBytes, LogUsedBytes - before.LogUsedBytes);
    }

    private static async Task<LineageStorage> ReadLineageStorageAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE((SELECT SUM(leaf_insert_count) FROM sys.dm_db_index_operational_stats(DB_ID(),NULL,NULL,NULL)),0),
                COALESCE((SELECT SUM(leaf_update_count) FROM sys.dm_db_index_operational_stats(DB_ID(),NULL,NULL,NULL)),0),
                COALESCE((SELECT SUM(leaf_delete_count) FROM sys.dm_db_index_operational_stats(DB_ID(),NULL,NULL,NULL)),0),
                (SELECT SUM(stats.num_of_bytes_written) FROM sys.dm_io_virtual_file_stats(DB_ID(),NULL) stats
                    JOIN sys.database_files files ON stats.file_id=files.file_id WHERE files.type=1),
                (SELECT SUM(CONVERT(bigint,size)*8192) FROM sys.database_files WHERE type=1),
                (SELECT used_log_space_in_bytes FROM sys.dm_db_log_space_usage);
            """;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
    }
}
