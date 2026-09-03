using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
#if CAMERAAGENT_FLEET_PERFORMANCE_TESTS
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
#endif
using HVO.SkyMonitor.Fleet.Contracts;
#if !CAMERAAGENT_FLEET_PERFORMANCE_TESTS
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
#endif
using HVO.SkyMonitor.TestSupport;
#if CAMERAAGENT_FLEET_PERFORMANCE_TESTS
using Microsoft.Data.Sqlite;
#else
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
#endif

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class FleetStatusPerformanceTests
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

#if CAMERAAGENT_FLEET_PERFORMANCE_TESTS
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
        var process = Process.GetCurrentProcess();
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
            process.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

#else
    [TestMethod]
    public async Task CentralMultiAgentIngestRecordsPerformanceEvidence()
    {
        const int agentCount = 1_000;
        const int measuredPerConcurrency = 200;
        int[] concurrencyLevels = [1, 8, 32];
        var agents = await SeedAgentsAsync(agentCount).ConfigureAwait(false);
        var sequenceByAgent = agents.ToDictionary(static agent => agent.RegistrationId, static _ => 1L);
        var measurements = new List<object>();
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var rssBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        var initializationStarted = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(
            agents,
            new ParallelOptions { MaxDegreeOfParallelism = 32 },
            async (agent, cancellationToken) =>
            {
                await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IDeviceHeartbeatService>();
                var acknowledgement = await service.RecordHeartbeatAsync(
                    agent.DeviceId,
                    agent.DeviceKey,
                    FleetStatusTestData.CreateReport(agent.DevicePublicId, 1, observedAtUtc: DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
                acknowledgement.Disposition.Should().Be(FleetHeartbeatDisposition.Advanced);
            }).ConfigureAwait(false);
        var initializationDuration = Stopwatch.GetElapsedTime(initializationStarted);

        foreach (var concurrency in concurrencyLevels)
        {
            var operations = Enumerable.Range(0, 5 + measuredPerConcurrency)
                .Select(index =>
                {
                    var agent = agents[index % agents.Count];
                    var sequence = ++sequenceByAgent[agent.RegistrationId];
                    return (Agent: agent, Sequence: sequence, Warmup: index < 5);
                })
                .ToArray();
            var latencies = new ConcurrentBag<double>();
            var started = Stopwatch.GetTimestamp();
            await Parallel.ForEachAsync(
                operations,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency },
                async (operation, cancellationToken) =>
                {
                    var operationStarted = Stopwatch.GetTimestamp();
                    await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<IDeviceHeartbeatService>();
                    var acknowledgement = await service.RecordHeartbeatAsync(
                        operation.Agent.DeviceId,
                        operation.Agent.DeviceKey,
                        FleetStatusTestData.CreateReport(
                            operation.Agent.DevicePublicId,
                            operation.Sequence,
                            observedAtUtc: DateTimeOffset.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                    acknowledgement.Disposition.Should().NotBe(FleetHeartbeatDisposition.Duplicate);
                    if (!operation.Warmup)
                    {
                        latencies.Add(Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds);
                    }
                }).ConfigureAwait(false);
            var duration = Stopwatch.GetElapsedTime(started);
            measurements.Add(new
            {
                concurrency,
                measuredOperations = latencies.Count,
                durationMilliseconds = duration.TotalMilliseconds,
                operationsPerSecond = operations.Length / duration.TotalSeconds,
                latencyMilliseconds = Percentiles(latencies)
            });
        }

        await using var assertionScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registrationIds = agents.Select(static agent => agent.RegistrationId).ToArray();
        var currentCount = await db.DeviceFleetStates.CountAsync(state => registrationIds.Contains(state.RegistrationId)).ConfigureAwait(false);
        var historyCount = await db.DeviceHeartbeatRecords.CountAsync(record => registrationIds.Contains(record.RegistrationId)).ConfigureAwait(false);
        currentCount.Should().Be(agentCount);
        historyCount.Should().Be(agentCount + concurrencyLevels.Length * (5 + measuredPerConcurrency));
        process.Refresh();
        var samplePayloadBytes = FleetContractJson.Serialize(FleetStatusTestData.CreateReport(agents[0].DevicePublicId)).Length;
        var evidence = new
        {
            workload = "multi-agent-central-ingest",
            agentCount,
            samplePayloadBytes,
            initialization = new
            {
                operations = agentCount,
                durationMilliseconds = initializationDuration.TotalMilliseconds,
                operationsPerSecond = agentCount / initializationDuration.TotalSeconds
            },
            measurements,
            currentRows = currentCount,
            historyRows = historyCount,
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            workingSetBeforeBytes = rssBefore,
            workingSetAfterBytes = process.WorkingSet64,
            projectedReceiptRowsAtOneDay = agentCount * 24 * 60,
            projectedCheckpointRowsAtThirtyDays = agentCount * 30 * 24 * 4,
            correctness = new { independentCurrentRows = true, monotonicSequences = true }
        };
        await WriteEvidenceAsync("central-fleet-performance.json", evidence).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PerformanceAgent>> SeedAgentsAsync(int count)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var observatory = new Observatory
        {
            OwnerUserId = $"fleet-performance-{Guid.NewGuid():N}",
            Name = "Fleet performance observatory",
            TimeZoneId = "UTC",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };
        var agents = Enumerable.Range(0, count).Select(index =>
        {
            var deviceId = $"fleet-performance-{Guid.NewGuid():N}";
            var deviceKey = $"fleet-performance-key-{Guid.NewGuid():N}";
            var registration = new DeviceRegistration
            {
                DeviceId = deviceId,
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                FriendlyName = $"Fleet performance {index}",
                ObservatoryName = observatory.Name,
                OwnerUserId = observatory.OwnerUserId,
                OwnerDisplayName = "Fleet Performance",
                Status = DeviceRegistrationStatus.Active,
                VerificationCodeHash = new string('A', 64),
                DevicePublicId = Guid.NewGuid(),
                DeviceKeyHash = DeviceRegistrationService.ComputeSha256(deviceKey),
                IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            db.DeviceRegistrations.Add(registration);
            return new PerformanceAgent(registration.Id, deviceId, deviceKey, registration.DevicePublicId.Value);
        }).ToArray();
        await db.SaveChangesAsync().ConfigureAwait(false);
        return agents;
    }
#endif

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
#if CAMERAAGENT_FLEET_PERFORMANCE_TESTS
        const string projectDirectory = "HVO.SkyMonitor.CameraAgent.Tests";
#else
        const string projectDirectory = "HVO.SkyMonitor.LogicHost.IntegrationTests";
#endif
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
                configuration = "Release",
                sqlServer = "Testcontainers"
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

#if CAMERAAGENT_FLEET_PERFORMANCE_TESTS
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
#else
    private sealed record PerformanceAgent(Guid RegistrationId, string DeviceId, string DeviceKey, Guid DevicePublicId);
#endif
}
