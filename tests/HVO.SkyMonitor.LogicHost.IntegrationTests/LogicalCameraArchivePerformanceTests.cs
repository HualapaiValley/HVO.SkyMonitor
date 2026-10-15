using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class LogicalCameraArchivePerformanceTests
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };
    [TestMethod]
    [Timeout(600_000)]
    public async Task SqlServer_W3MArchiveAndCurrentSelectionRecordIndexedBounds()
    {
        var evidence = new List<object>();
        foreach (var size in new[] { 1_000, 10_000 })
        {
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var seedDb = services.GetRequiredService<ApplicationDbContext>();
            var dataset = await LogicalCameraArchiveIntegrationTests.SeedAsync(services, size).ConfigureAwait(false);
            _ = await LogicalCameraArchiveIntegrationTests.AddPreviewAsync(services, dataset.Frames[0], true).ConfigureAwait(false);
            seedDb.ChangeTracker.Clear();
            var connectionString = seedDb.Database.GetConnectionString()!;
            var capture = new QueryCapture();
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(connectionString).AddInterceptors(capture).Options);
            var old = new NetworkOperationsReadService(db);
            var service = new LogicalCameraArchiveReadService(db, services.GetRequiredService<IDataProtectionProvider>(), old,
                ActivatorUtilities.CreateInstance<CentralArtifactRetrievalService>(services, db),
                services.GetRequiredService<ICentralArtifactObjectReader>());
            var principal = LogicalCameraArchiveIntegrationTests.Principal(dataset.Owner);
            var requests = new Dictionary<string, Func<Task<object>>>
            {
                ["nearest-baseline-unfiltered"] = async () => await old.ListCapturesAsync(dataset.Owner, dataset.Observatory, dataset.Camera, 50, null).ConfigureAwait(false),
                ["archive-unfiltered"] = async () => await service.ListCapturesAsync(principal, dataset.Observatory, dataset.Camera, new(), 50, null).ConfigureAwait(false),
                ["archive-product-state"] = async () => await service.ListCapturesAsync(principal, dataset.Observatory, dataset.Camera,
                    new(Product: FrameArtifactRole.Preview, ObjectState: CentralArtifactObjectState.Expired), 50, null).ConfigureAwait(false),
                ["archive-exact-missing"] = async () => await service.ListCapturesAsync(principal, dataset.Observatory, dataset.Camera, new(Text: "999999999"), 50, null).ConfigureAwait(false),
                ["current-verified"] = async () => (await service.SelectCurrentAsync(principal, dataset.Observatory, dataset.Camera, null).ConfigureAwait(false))!
            };
            foreach (var (name, request) in requests)
            {
                capture.Enabled = false;
                for (var warm = 0; warm < 5; warm++) _ = await request().ConfigureAwait(false);
                var times = new List<double>();
                var allocations = GC.GetTotalAllocatedBytes(true);
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var rss = process.WorkingSet64;
                var bytes = 0;
                for (var run = 0; run < 30; run++)
                {
                    var start = Stopwatch.GetTimestamp();
                    var result = await request().ConfigureAwait(false);
                    bytes = JsonSerializer.SerializeToUtf8Bytes(result).Length;
                    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                var allocated = GC.GetTotalAllocatedBytes(true) - allocations;
                process.Refresh();
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                capture.Commands.Clear();
                capture.Enabled = true;
                var observed = await request().ConfigureAwait(false);
                capture.Enabled = false;
                capture.Commands.Count.Should().BeLessThanOrEqualTo(name == "current-verified" ? 8 : 3);
                var plans = new List<Plan>();
                foreach (var command in capture.Commands) plans.Add(await ExplainAsync(connectionString, command).ConfigureAwait(false));
                plans.Max(item => item.ResultRows).Should().BeLessThanOrEqualTo(51);
                plans.SelectMany(item => item.Indexes).Should().Contain("IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id");
                if (observed is CameraArchivePage page) page.Items.Count.Should().BeLessThanOrEqualTo(50);
                if (observed is CameraCurrentSelection current)
                {
                    current.Status.Should().Be("found");
                    current.Capture!.CaptureId.Should().Be(dataset.Frames[0].Id);
                    current.ExaminedCaptures.Should().Be(1);
                }
                times.Sort();
                evidence.Add(new
                {
                    Name = name,
                    HistoryRows = size,
                    Warmup = 5,
                    Operations = 30,
                    Concurrency = 1,
                    MedianMs = times[15],
                    P95Ms = times[28],
                    MaxMs = times[^1],
                    CpuMs = cpuMs,
                    AllocatedBytesPerOperation = allocated / 30,
                    WorkingSetBefore = rss,
                    WorkingSetAfter = process.WorkingSet64,
                    ResponseBytes = bytes,
                    SqlCommands = capture.Commands.Count,
                    Plans = plans,
                    Dataset = new { dataset.Observatory, dataset.Camera, FirstCapture = dataset.Frames[0].Id }
                });
            }
        }
        var path = Environment.GetEnvironmentVariable("HVO_ISSUE1236_EVIDENCE") ?? "TestResults/issue-1236-performance.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Source = Environment.GetEnvironmentVariable("HVO_ISSUE1236_REVISION"),
            Utc = DateTimeOffset.UtcNow,
            Workload = "W3M: 1K/10K metadata captures, two retained installations, two expired and one present Preview",
            Method = "5 warmup + 30 sequential end-to-end service/serialization operations; SQL plans from replay of exact parameterized production commands; process-wide allocations include fixture background work",
            Browser = "N/A: backend prerequisite, no UI changes",
            Results = evidence
        }, EvidenceJson)).ConfigureAwait(false);
    }

    private static async Task<Plan> ExplainAsync(string connectionString, Command command)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        await using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "SET STATISTICS XML ON; SET STATISTICS IO ON;";
            await settings.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await using var replay = connection.CreateCommand();
#pragma warning disable CA2100 // Captured EF parameterized production command, never user SQL.
        replay.CommandText = command.Text;
#pragma warning restore CA2100
        replay.Parameters.AddRange(command.Parameters.Select(Copy).ToArray());
        await using var reader = await replay.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = 0;
        var first = true;
        var plans = new List<XDocument>();
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (first) rows++;
                if (!first && reader.FieldCount == 1 && reader.GetValue(0) is string xml && xml.Contains("ShowPlanXML", StringComparison.Ordinal))
                    plans.Add(XDocument.Parse(xml));
            }
            first = false;
        } while (await reader.NextResultAsync().ConfigureAwait(false));
        plans.Should().NotBeEmpty();
        var indexes = plans.SelectMany(plan => plan.Descendants().Where(item => item.Name.LocalName == "Object")
            .Select(item => ((string?)item.Attribute("Index") ?? "").Trim('[', ']'))).Where(item => item.Length > 0).Distinct().ToArray();
        var reads = Regex.Matches(messages.ToString(), "logical reads ([0-9]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Sum(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        var accessedRows = plans.SelectMany(plan => plan.Descendants().Where(item => item.Name.LocalName == "RunTimeCountersPerThread"))
            .Sum(item => (long?)item.Attribute("ActualRowsRead") ?? 0);
        return new(rows, accessedRows, reads, indexes);
    }
    private static SqlParameter Copy(SqlParameter item) => new(item.ParameterName, item.SqlDbType, item.Size)
    { Value = item.Value, Precision = item.Precision, Scale = item.Scale };
    private sealed record Plan(int ResultRows, long ActualRowsRead, long LogicalReads, string[] Indexes);
    private sealed record Command(string Text, SqlParameter[] Parameters);
    private sealed class QueryCapture : DbCommandInterceptor
    {
        internal bool Enabled { get; set; }
        internal List<Command> Commands { get; } = [];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Enabled) Commands.Add(new(command.CommandText, command.Parameters.Cast<SqlParameter>().Select(Copy).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}
