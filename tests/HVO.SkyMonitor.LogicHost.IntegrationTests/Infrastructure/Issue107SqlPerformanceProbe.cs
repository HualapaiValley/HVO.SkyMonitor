using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal static partial class Issue107SqlPerformanceProbe
{
    internal static async Task<SqlPerformanceEvidence> MeasureAsync(
        IServiceProvider services,
        Issue107PerformanceDataset dataset)
    {
        await using var setupScope = services.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ownerId = await setupDb.Users.Where(user => user.Email == TestUsers.Operator.Email)
            .Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var logicalCameraId = dataset.TenThousandSparseCameraId;
        var connectionString = setupDb.Database.GetConnectionString()
            ?? throw new InvalidOperationException("The issue #107 SQL probe requires SQL Server.");

        var scenarios = new[]
        {
            new Scenario("1k-first", 1_000, dataset.ThousandObservatoryId, null, 0),
            new Scenario("1k-middle-500", 1_000, dataset.ThousandObservatoryId, null, 500),
            new Scenario("1k-late-900", 1_000, dataset.ThousandObservatoryId, null, 900),
            new Scenario("10k-first", 10_000, dataset.TenThousandObservatoryId, null, 0),
            new Scenario("10k-middle-5000", 10_000, dataset.TenThousandObservatoryId, null, 5_000),
            new Scenario("10k-late-9000", 10_000, dataset.TenThousandObservatoryId, null, 9_000),
            new Scenario("10k-filtered-camera-first", 10_000, dataset.TenThousandObservatoryId, logicalCameraId, 0),
            new Scenario("10k-filtered-camera-middle", 10_000, dataset.TenThousandObservatoryId, logicalCameraId, 250),
            new Scenario("10k-filtered-camera-late", 10_000, dataset.TenThousandObservatoryId, logicalCameraId, 450)
        };
        var pages = new List<SqlPageEvidence>(scenarios.Length);
        foreach (var scenario in scenarios)
        {
            var cursor = await CreateCursorAsync(setupDb, scenario)
                .ConfigureAwait(false);
            pages.Add(await MeasurePageAsync(
                    connectionString, ownerId, scenario, cursor)
                .ConfigureAwait(false));
        }

        pages.Should().OnlyContain(page => page.SqlCommands <= 3);
        pages.Should().OnlyContain(page => page.ProjectedRootRows <= 51);
        pages.Should().OnlyContain(page => page.ItemsReturned <= 50 && page.StableExpectedItems);
        pages.Should().OnlyContain(page => page.UsesBoundedCaptureIndex);
        var thousandReads = pages.Where(page => page.HistoryRows == 1_000).Max(page => page.LogicalReads);
        var tenThousandReads = pages.Where(page => page.HistoryRows == 10_000).Max(page => page.LogicalReads);
        tenThousandReads.Should().BeLessThanOrEqualTo(thousandReads * 3,
            "10K archive paging must remain indexed rather than scale with history length");

        var thousandScale = await MeasureScaleAsync(
            connectionString, ownerId, dataset.ThousandObservatoryId).ConfigureAwait(false);
        var tenThousandScale = await MeasureScaleAsync(
            connectionString, ownerId, dataset.TenThousandObservatoryId).ConfigureAwait(false);
        tenThousandScale.P95Milliseconds.Should().BeLessThanOrEqualTo(
            Math.Max(thousandScale.P95Milliseconds * 2, thousandScale.P95Milliseconds + 50));
        tenThousandScale.AllocatedBytesPerOperation.Should().BeLessThanOrEqualTo(
            Math.Max(
                thousandScale.AllocatedBytesPerOperation * 2,
                thousandScale.AllocatedBytesPerOperation + 1024 * 1024));
        var detailSqlCommands = await MeasureDetailAsync(
            connectionString, ownerId, dataset.TenThousandCaptureId).ConfigureAwait(false);
        detailSqlCommands.Should().BeLessThanOrEqualTo(12);

        return new(pages, thousandScale, tenThousandScale, tenThousandReads, thousandReads, detailSqlCommands);
    }

    private static async Task<SqlPageEvidence> MeasurePageAsync(
        string connectionString,
        string ownerId,
        Scenario scenario,
        string? cursor)
    {
        var interceptor = new CommandCaptureInterceptor();
        var options = CreateOptions(connectionString, interceptor);
        await using var db = new ApplicationDbContext(options);
        var service = new NetworkOperationsReadService(db);
        var page = await service.ListCapturesAsync(
            ownerId, scenario.ObservatoryId, scenario.LogicalCameraId, 50, cursor).ConfigureAwait(false);
        var productionCommands = interceptor.Commands.ToArray();
        productionCommands.Should().HaveCountLessThanOrEqualTo(3);
        var expectedIds = await ExpectedIdsAsync(db, scenario, cursor).ConfigureAwait(false);
        page.Items.Select(item => item.CaptureId).Should().Equal(expectedIds.Take(50));
        page.Items.Select(item => item.CaptureId).Should().OnlyHaveUniqueItems();
        var plans = new List<PlanEvidence>(productionCommands.Length);
        foreach (var command in productionCommands)
        {
            plans.Add(await ReadPlanAndIoAsync(connectionString, command).ConfigureAwait(false));
        }
        var indexes = plans.SelectMany(plan => plan.Indexes).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var requiredIndex = scenario.LogicalCameraId is null
            ? "IX_CentralFrames_ObservatoryId_CapturedAtUtc_Id"
            : "IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id";
        var usesBoundedIndex = indexes.Any(index => index.Contains(requiredIndex, StringComparison.Ordinal));
        return new(
            scenario.Name,
            scenario.HistoryRows,
            scenario.Offset,
            scenario.LogicalCameraId is not null,
            productionCommands.Length,
            plans.Max(plan => plan.RootRows),
            page.Items.Count,
            plans.Sum(plan => plan.LogicalReads),
            Hash(string.Join('|', productionCommands.Select(command => command.NormalizedCommandSha256)
                .Order(StringComparer.Ordinal))),
            Hash(string.Join('|', plans.Select(plan => plan.PlanSha256).Order(StringComparer.Ordinal))),
            indexes,
            usesBoundedIndex,
            true);
    }

    private static async Task<string?> CreateCursorAsync(
        ApplicationDbContext db,
        Scenario scenario)
    {
        if (scenario.Offset == 0) return null;
        var query = db.CentralFrames.AsNoTracking()
            .Where(frame => frame.ObservatoryId == scenario.ObservatoryId);
        if (scenario.LogicalCameraId is not null)
        {
            query = query.Where(frame => frame.LogicalCameraInstallation != null
                && frame.LogicalCameraInstallation.LogicalCameraId == scenario.LogicalCameraId);
        }
        var preceding = await query
            .OrderByDescending(frame => frame.CapturedAtUtc).ThenByDescending(frame => frame.Id)
            .Skip(scenario.Offset - 1)
            .Select(frame => new { frame.CapturedAtUtc, CaptureId = frame.Id })
            .FirstAsync().ConfigureAwait(false);
        return WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(preceding));
    }

    private static async Task<Guid[]> ExpectedIdsAsync(
        ApplicationDbContext db,
        Scenario scenario,
        string? cursor)
    {
        var query = db.CentralFrames.AsNoTracking().Where(frame => frame.ObservatoryId == scenario.ObservatoryId);
        if (scenario.LogicalCameraId is not null)
        {
            query = query.Where(frame => frame.LogicalCameraInstallation != null
                && frame.LogicalCameraInstallation.LogicalCameraId == scenario.LogicalCameraId);
        }
        if (cursor is not null)
        {
            var value = JsonSerializer.Deserialize<CaptureCursor>(WebEncoders.Base64UrlDecode(cursor))!;
            query = query.Where(frame => frame.CapturedAtUtc < value.CapturedAtUtc
                || frame.CapturedAtUtc == value.CapturedAtUtc && frame.Id.CompareTo(value.CaptureId) < 0);
        }
        return await query.OrderByDescending(frame => frame.CapturedAtUtc).ThenByDescending(frame => frame.Id)
            .Take(51).Select(frame => frame.Id).ToArrayAsync().ConfigureAwait(false);
    }

    private static async Task<SqlScaleEvidence> MeasureScaleAsync(
        string connectionString,
        string ownerId,
        Guid observatoryId)
    {
        for (var index = 0; index < 5; index++)
        {
            await ReadFirstPageAsync(connectionString, ownerId, observatoryId).ConfigureAwait(false);
        }
        var latencies = new double[30];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var index = 0; index < latencies.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            await ReadFirstPageAsync(connectionString, ownerId, observatoryId).ConfigureAwait(false);
            latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(latencies);
        return new(5, latencies.Length, Percentile(latencies, 0.5), Percentile(latencies, 0.95),
            allocated / latencies.Length);
    }

    private static async Task ReadFirstPageAsync(string connectionString, string ownerId, Guid observatoryId)
    {
        await using var db = new ApplicationDbContext(CreateOptions(connectionString));
        var page = await new NetworkOperationsReadService(db)
            .ListCapturesAsync(ownerId, observatoryId, null, 50, null).ConfigureAwait(false);
        page.Items.Should().HaveCount(50);
    }

    private static async Task<int> MeasureDetailAsync(
        string connectionString,
        string ownerId,
        Guid captureId)
    {
        var interceptor = new CommandCaptureInterceptor();
        await using var db = new ApplicationDbContext(CreateOptions(connectionString, interceptor));
        var service = new NetworkOperationsReadService(db);
        var detail = await service.GetCaptureAsync(ownerId, captureId).ConfigureAwait(false);
        var trace = await service.GetCaptureTraceAsync(ownerId, captureId).ConfigureAwait(false);
        detail.Should().NotBeNull();
        trace.Should().NotBeNull();
        return interceptor.Commands.Count;
    }

    private static DbContextOptions<ApplicationDbContext> CreateOptions(
        string connectionString,
        DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private static async Task<PlanEvidence> ReadPlanAndIoAsync(
        string connectionString,
        CommandSnapshot snapshot)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var plans = new List<string>();
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        await ExecuteNonQueryAsync(connection, "SET STATISTICS XML ON;").ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "SET STATISTICS IO ON;").ConfigureAwait(false);
        var rootRows = 0;
        try
        {
            await using var command = snapshot.CreateCommand(connection);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var resultIndex = 0;
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (resultIndex == 0) rootRows++;
                    for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    {
                        if (!await reader.IsDBNullAsync(ordinal).ConfigureAwait(false))
                        {
                            var value = Convert.ToString(
                                reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
                            if (value?.Contains("ShowPlanXML", StringComparison.Ordinal) == true)
                            {
                                plans.Add(value);
                            }
                        }
                    }
                }
                resultIndex++;
            }
            while (await reader.NextResultAsync().ConfigureAwait(false));
        }
        finally
        {
            await ExecuteNonQueryAsync(connection, "SET STATISTICS IO OFF;").ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "SET STATISTICS XML OFF;").ConfigureAwait(false);
        }
        plans.Should().NotBeEmpty();
        var logicalReads = LogicalReadsRegex().Matches(messages.ToString())
            .Select(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Sum();
        var planXml = string.Join('\n', plans);
        var indexes = plans.SelectMany(plan => XDocument.Parse(plan).Descendants()
                .Where(element => element.Name.LocalName == "Object")
                .Select(element => ((string?)element.Attribute("Index") ?? string.Empty).Trim('[', ']')))
            .Where(index => index.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(rootRows, logicalReads,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planXml))), indexes);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Callers provide only fixed SET statements declared in this test harness.
        command.CommandText = commandText;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static double Percentile(double[] ordered, double percentile)
        => ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * percentile) - 1)];

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex(@"logical reads (?<reads>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogicalReadsRegex();

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        internal ConcurrentBag<CommandSnapshot> Commands { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(CommandSnapshot.Create(command));
            return ValueTask.FromResult(result);
        }
    }

    private sealed record CommandSnapshot(string CommandText, SqlParameter[] Parameters, string NormalizedCommandSha256)
    {
        internal static CommandSnapshot Create(DbCommand command)
        {
            var normalized = string.Join(' ', command.CommandText.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return new(command.CommandText,
                command.Parameters.Cast<SqlParameter>().Select(CopyParameter).ToArray(),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
        }

        internal SqlCommand CreateCommand(SqlConnection connection)
        {
            var command = connection.CreateCommand();
#pragma warning disable CA2100 // The command is captured from EF Core's parameterized production query.
            command.CommandText = CommandText;
#pragma warning restore CA2100
            command.Parameters.AddRange(Parameters.Select(CopyParameter).ToArray());
            return command;
        }

        private static SqlParameter CopyParameter(SqlParameter source)
            => new(source.ParameterName, source.SqlDbType, source.Size)
            {
                Direction = source.Direction,
                IsNullable = source.IsNullable,
                Precision = source.Precision,
                Scale = source.Scale,
                Value = source.Value
            };
    }

    private sealed record Scenario(
        string Name,
        int HistoryRows,
        Guid ObservatoryId,
        Guid? LogicalCameraId,
        int Offset);

    private sealed record CaptureCursor(DateTimeOffset CapturedAtUtc, Guid CaptureId);
    private sealed record PlanEvidence(int RootRows, long LogicalReads, string PlanSha256, IReadOnlyList<string> Indexes);
}

internal sealed record SqlPerformanceEvidence(
    IReadOnlyList<SqlPageEvidence> Pages,
    SqlScaleEvidence ThousandRows,
    SqlScaleEvidence TenThousandRows,
    long TenThousandMaximumLogicalReads,
    long ThousandMaximumLogicalReads,
    int DetailSqlCommands);

internal sealed record SqlPageEvidence(
    string Name,
    int HistoryRows,
    int Offset,
    bool LogicalCameraFiltered,
    int SqlCommands,
    int ProjectedRootRows,
    int ItemsReturned,
    long LogicalReads,
    string NormalizedCommandSha256,
    string PlanSha256,
    IReadOnlyList<string> Indexes,
    bool UsesBoundedCaptureIndex,
    bool StableExpectedItems);

internal sealed record SqlScaleEvidence(
    int Warmups,
    int Samples,
    double MedianMilliseconds,
    double P95Milliseconds,
    long AllocatedBytesPerOperation);
