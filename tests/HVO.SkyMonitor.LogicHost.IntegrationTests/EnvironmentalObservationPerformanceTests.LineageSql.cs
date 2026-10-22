using System.Data.Common;
using System.Diagnostics;
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

        private void Record(DbCommand command)
        {
            CommandCount++;
            if (command.CommandText.Contains("FROM [EnvironmentalObservationSources]", StringComparison.Ordinal)) SourceLookups++;
            if (command.CommandText.Contains("FROM [EnvironmentalObservations]", StringComparison.Ordinal)) ObservationLookups++;
            if (capture) Commands.Add(new(command.CommandText, command.Parameters.Cast<SqlParameter>().Select(CopyLineageParameter).ToArray()));
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            Record(command);
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

    private static SqlParameter CopyLineageParameter(SqlParameter parameter) => new(parameter.ParameterName, parameter.SqlDbType, parameter.Size)
    {
        Value = parameter.Value, Precision = parameter.Precision, Scale = parameter.Scale,
        Direction = parameter.Direction, IsNullable = parameter.IsNullable
    };

    private static async Task<object> ReplayLineageCommandsAsync(string connectionString, List<LineageCommand> commands, string output)
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
                        NodeId = (int?)node.Attribute("NodeId"), Logical = (string?)node.Attribute("LogicalOp"),
                        Physical = (string?)node.Attribute("PhysicalOp"), EstimatedRows = (double?)node.Attribute("EstimateRows"),
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
                    Ordinal = evidence.Count, Sql = captured.Text,
                    Parameters = captured.Parameters.Select(parameter => new
                    {
                        parameter.ParameterName, Type = parameter.SqlDbType.ToString(), parameter.Size,
                        parameter.Precision, parameter.Scale, Value = parameter.Value is DBNull ? null : parameter.Value
                    }),
                    ReturnedRows = returnedRows, LogicalReads = reads, ServerCpuMilliseconds = cpu,
                    Operators = operators, IoMessages = messages.ToString(), ActualPlans = plans
                });
            }
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        Assert.IsTrue(plansCount >= 514, "Capture actual plans for both own identity and all 512 lineage SELECTs.");
        var path = Path.Combine(output, "sql-replay.json.gz");
        await using (var stream = File.Create(path))
        await using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize))
        {
            await JsonSerializer.SerializeAsync(gzip, evidence, EvidenceJsonOptions).ConfigureAwait(false);
        }
        return new { Commands = commands.Count, ActualPlans = plansCount, LogicalReads = totalReads,
            ActualRowsRead = totalRowsRead, ServerCpuMilliseconds = serverCpu, Artifact = "sql-replay.json.gz",
            Boundary = "One untimed 256-reference production command sequence; exact parameters, all writes rolled back" };
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
