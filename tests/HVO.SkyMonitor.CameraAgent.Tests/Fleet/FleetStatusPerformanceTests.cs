using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class FleetStatusPerformanceTests
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task EdgeOutageCoalescingAndCatchUpRecordsPerformanceEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fleet-performance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var instance = Guid.NewGuid();
        var boot = Guid.NewGuid();
        var latencies = new List<double>();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var rssBefore = process.WorkingSet64;
        try
        {
            using var outbox = new SqliteFleetStatusOutbox(time);
            await MeasureEnqueueAsync(transition: false, "normal").ConfigureAwait(false);
            var outageLease = await outbox.ClaimAsync(root, "outage-worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(outageLease);
            await outbox.RetryAsync(root, outageLease, now.AddHours(24), "central-offline", CancellationToken.None).ConfigureAwait(false);
            for (var index = 0; index < 6; index++)
            {
                time.Advance(TimeSpan.FromHours(1));
                await MeasureEnqueueAsync(transition: true, $"transition-{index}").ConfigureAwait(false);
            }
            for (var index = 0; index < 96; index++)
            {
                time.Advance(TimeSpan.FromMinutes(15));
                await MeasureEnqueueAsync(transition: false, "stable").ConfigureAwait(false);
            }
            var outage = await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            outage.PendingCount.Should().Be(8, "96 routine checkpoints coalesce while six transitions remain immutable");
            time.Advance(TimeSpan.FromHours(24));
            var drainStarted = Stopwatch.GetTimestamp();
            var delivered = new List<long>();
            while (await outbox.ClaimAsync(root, "recovery-worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false) is { } lease)
            {
                delivered.Add(lease.Record.Report.Sequence);
                await outbox.AcknowledgeAsync(
                    root,
                    lease,
                    new FleetHeartbeatAcknowledgement(
                        instance, lease.Record.Report.BootSessionId, lease.Record.Report.Sequence,
                        FleetHeartbeatDisposition.Advanced, time.GetUtcNow(), 60),
                    CancellationToken.None).ConfigureAwait(false);
            }
            var drainDuration = Stopwatch.GetElapsedTime(drainStarted);
            delivered.Should().BeInAscendingOrder().And.HaveCount(8);
            (await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).PendingCount.Should().Be(0);
            SqliteConnection.ClearAllPools();
            var databaseBytes = new FileInfo(Path.Combine(root, ".fleet", "fleet-status.db")).Length;
            process.Refresh();
            var evidence = new
            {
                workload = "24-hour-edge-outage",
                reportsObserved = 103,
                significantTransitions = 6,
                retainedRecords = outage.PendingCount,
                payloadBytes = FleetContractJson.Serialize(FleetStatusTestData.CreateReport(instance)).Length,
                sqliteDatabaseBytes = databaseBytes,
                enqueueLatencyMs = Percentiles(latencies),
                catchUpDurationMs = drainDuration.TotalMilliseconds,
                catchUpReportsPerSecond = delivered.Count / drainDuration.TotalSeconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                workingSetBeforeBytes = rssBefore,
                workingSetAfterBytes = process.WorkingSet64,
                correctness = new { ordered = true, backlogDrained = true, transitionsPreserved = true }
            };
            await WriteEvidenceAsync("edge-fleet-performance.json", evidence).ConfigureAwait(false);

            async ValueTask MeasureEnqueueAsync(bool transition, string fingerprint)
            {
                var started = Stopwatch.GetTimestamp();
                await outbox.EnqueueAsync(
                    root,
                    instance,
                    sequence => FleetStatusTestData.CreateReport(instance, sequence, boot, time.GetUtcNow()),
                    fingerprint,
                    transition,
                    CancellationToken.None).ConfigureAwait(false);
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static object Percentiles(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new
        {
            p50 = Percentile(0.50),
            p95 = Percentile(0.95),
            p99 = Percentile(0.99),
            maximum = ordered[^1]
        };

        double Percentile(double percentile) => ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    }

    private static async Task WriteEvidenceAsync(string fileName, object evidence)
    {
        var root = GetRepositoryRoot();
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        const string projectDirectory = "HVO.SkyMonitor.CameraAgent.Tests";
        var directory = Path.Combine(root, "tests", projectDirectory, "TestResults", "fleet-status", revision);
        Directory.CreateDirectory(directory);
        var envelope = new
        {
            revision,
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processorCount = Environment.ProcessorCount,
                runtime = RuntimeInformation.FrameworkDescription,
                configuration = "Release"
            },
            evidence
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, fileName),
            JsonSerializer.Serialize(envelope, EvidenceOptions)).ConfigureAwait(false);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root could not be located.");
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
