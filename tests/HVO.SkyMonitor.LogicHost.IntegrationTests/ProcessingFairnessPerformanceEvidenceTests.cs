using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Tier C evidence for #429: deterministic 5-, 10-, and 100-camera arrival streams drained by concurrent claim loops
/// under entitlements, measuring fairness (Jain's index of per-observatory completions), claim queue latency,
/// throughput, CPU, memory, and recovery after simulated lease loss. Output is revision-scoped under
/// <c>TestResults/processing-fairness/</c>.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class ProcessingFairnessPerformanceEvidenceTests
{
    private static readonly byte[] SourcePayload = [1, 0, 2, 0, 3, 0, 4, 0];
    private static readonly JsonSerializerOptions EvidenceSerializerOptions = new() { WriteIndented = true };
    private static readonly int[] CameraCounts = [5, 10, 100];
    private const int CapturesPerCamera = 4;
    private static readonly double[] FairnessCheckpoints = [0.25, 0.5, 0.75, 1.0];
    private const int Workers = 8;

    [TestCleanup]
    public Task CleanupAsync() => DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory);

    [TestMethod]
    public async Task Issue429FairScheduling_RecordsEvidence()
    {
        var revision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? $"local-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var streams = new List<object>();
        foreach (var cameras in CameraCounts)
        {
            await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
            var observatories = new List<Guid>(cameras);
            var observatoryByDevice = new Dictionary<Guid, Guid>();
            var availableByDevice = new Dictionary<Guid, DateTimeOffset>();
            var seedStarted = Stopwatch.GetTimestamp();
            for (var camera = 0; camera < cameras; camera++)
            {
                var (observatoryId, deviceId) = await SeedObservatoryAsync($"fair-{cameras}-{camera}", CapturesPerCamera).ConfigureAwait(false);
                observatories.Add(observatoryId);
                observatoryByDevice[deviceId] = observatoryId;
                availableByDevice[deviceId] = DateTimeOffset.UtcNow;
            }
            var seedElapsed = Stopwatch.GetElapsedTime(seedStarted);
            // One observatory arrives with a far older backlog: fairness must still serve the rest.
            var backlogged = observatories[0];
            await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var old = DateTimeOffset.UtcNow.AddMinutes(-20);
                await db.CentralDerivativeJobs.Where(job => job.SourceArtifact!.Frame!.ObservatoryId == backlogged && job.AvailableAtUtc != null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.AvailableAtUtc, old)).ConfigureAwait(false);
                // Queue latency is measured from the availability the scheduler actually sees, including the backdate.
                availableByDevice[observatoryByDevice.Single(pair => pair.Value == backlogged).Key] = old;
            }
            using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ProcessingEntitlements:Enabled", "true");
                builder.UseSetting("ProcessingEntitlements:DefaultActiveJobs", "2");
                builder.UseSetting("ProcessingEntitlements:StarvationAge", "1.00:00:00");
            });
            var completions = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
            var completionOrder = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
            var claimOrder = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
            var claimOrdinal = 0;
            var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
            var claimDurations = new System.Collections.Concurrent.ConcurrentBag<double>();
            var expiredAndReclaimed = 0;
            var maxActivePerObservatory = 0;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocationsBefore = GC.GetTotalAllocatedBytes(true);
            var drainStarted = Stopwatch.GetTimestamp();
            var expectedJobs = await CountClaimableAsync(factory, observatories).ConfigureAwait(false);
            string readiness;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var rows = await db.CentralDerivativeJobs.AsNoTracking()
                    .Where(job => observatories.Contains(job.SourceArtifact!.Frame!.ObservatoryId))
                    .GroupBy(job => job.SourceArtifact!.Frame!.ObservatoryId)
                    .Select(group => new
                    {
                        Observatory = group.Key,
                        Pending = group.Count(job => job.Status == CentralDerivativeJobStatus.Pending),
                        Ready = group.Count(job => job.Status == CentralDerivativeJobStatus.Pending && job.InputSetIdentitySha256 != null && job.AvailableAtUtc <= DateTimeOffset.UtcNow),
                        Waiting = group.Count(job => job.Status == CentralDerivativeJobStatus.Waiting),
                        Other = group.Count(job => job.Status != CentralDerivativeJobStatus.Pending && job.Status != CentralDerivativeJobStatus.Waiting)
                    })
                    .ToListAsync().ConfigureAwait(false);
                readiness = string.Join(' ', rows.Select(row => $"{observatories.IndexOf(row.Observatory)}:p{row.Pending}/r{row.Ready}/w{row.Waiting}/o{row.Other}"));
            }
            var completed = 0;
            var workers = Enumerable.Range(0, Workers).Select(async worker =>
            {
                var empties = 0;
                while (Volatile.Read(ref completed) < expectedJobs && empties < 5)
                {
                    await using var scope = factory.Services.CreateAsyncScope();
                    var jobs = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
                    var claimStarted = Stopwatch.GetTimestamp();
                    var lease = await jobs.ClaimNextAsync($"fair-worker-{worker}", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
                    claimDurations.Add(Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds);
                    if (lease is null)
                    {
                        empties++;
                        await Task.Delay(25).ConfigureAwait(false);
                        continue;
                    }
                    empties = 0;
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    // Bookkeeping reads stay lock-free so the harness never participates in claim lock cycles.
                    var observatory = observatoryByDevice[lease.SourceDevicePublicId];
                    claimOrder.Enqueue(observatory);
                    var active = await db.Database.SqlQueryRaw<int>("""
                            SELECT COUNT(*) AS [Value]
                            FROM [CentralDerivativeJobs] AS job WITH (NOLOCK)
                            INNER JOIN [CentralArtifacts] AS source WITH (NOLOCK) ON source.[Id] = job.[SourceCentralArtifactId]
                            INNER JOIN [CentralFrames] AS frame WITH (NOLOCK) ON frame.[Id] = source.[CentralFrameId]
                            WHERE job.[Status] = N'Leased' AND job.[LeaseExpiresAtUtc] > SYSDATETIMEOFFSET() AND frame.[ObservatoryId] = @observatory
                            """, new Microsoft.Data.SqlClient.SqlParameter("@observatory", observatory))
                        .SingleAsync().ConfigureAwait(false);
                    InterlockedMax(ref maxActivePerObservatory, active);
                    latencies.Add(Math.Max(0, (DateTimeOffset.UtcNow - availableByDevice[lease.SourceDevicePublicId]).TotalMilliseconds));
                    // Simulate recipe work, then either finish or (every 11th successful claim of a first attempt) lose
                    // the lease to exercise recovery deterministically.
                    await Task.Delay(5).ConfigureAwait(false);
                    if (lease.AttemptCount == 1 && Interlocked.Increment(ref claimOrdinal) % 11 == 0)
                    {
                        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
                        await db.CentralDerivativeJobs.Where(job => job.Id == lease.JobId)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, expired)).ConfigureAwait(false);
                        await db.CentralDerivativeJobAttempts.Where(attempt => attempt.CentralDerivativeJobId == lease.JobId && attempt.AttemptNumber == 1)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.LeaseExpiresAtUtc, expired)).ConfigureAwait(false);
                        Interlocked.Increment(ref expiredAndReclaimed);
                        continue;
                    }
                    await RetryDeadlockAsync(() => jobs.SkipAsync(lease.JobId, lease.LeaseToken, "fairness.evidence", CancellationToken.None)).ConfigureAwait(false);
                    completions.AddOrUpdate(observatory, 1, static (_, count) => count + 1);
                    completionOrder.Enqueue(observatory);
                    Interlocked.Increment(ref completed);
                }
            }).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            var drainElapsed = Stopwatch.GetElapsedTime(drainStarted);
            process.Refresh();
            var cpu = process.TotalProcessorTime - cpuBefore;
            var allocations = GC.GetTotalAllocatedBytes(true) - allocationsBefore;
            var perObservatory = observatories.Select(id => completions.TryGetValue(id, out var count) ? count : 0).ToArray();
            // Fairness is judged on completion order, not only on the drained totals (which are equal by
            // construction): Jain's index over the first quarter, half, and three quarters of completions detects a
            // scheduler that drains one observatory before touching another.
            var order = completionOrder.ToArray();
            var jainByQuartile = FairnessCheckpoints
                .Select(fraction => JainIndex(order.Take((int)Math.Ceiling(order.Length * fraction)), observatories))
                .ToArray();
            var jain = jainByQuartile.Min();
            completed.Should().Be(expectedJobs);
            maxActivePerObservatory.Should().BeLessThanOrEqualTo(2, "the observatory entitlement is enforced under concurrent claims");
            var firstQuarter = order.Take((int)Math.Ceiling(order.Length * 0.25)).GroupBy(id => id)
                .Select(group => $"{observatories.IndexOf(group.Key)}:{group.Count()}");
            jain.Should().BeGreaterThan(0.7,
                $"every observatory progresses at the same share throughout the drain, not only at the end (quartiles {string.Join('/', jainByQuartile.Select(value => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}, first quarter by observatory index {string.Join(' ', firstQuarter)}, readiness at start {readiness}, claim order {string.Join("", claimOrder.Take(40).Select(id => observatories.IndexOf(id).ToString(System.Globalization.CultureInfo.InvariantCulture)))})");
            expiredAndReclaimed.Should().BeGreaterThan(0, "lease loss and reclaim is part of the measured workload");
            streams.Add(new
            {
                cameras,
                capturesPerCamera = CapturesPerCamera,
                jobsPerCamera = expectedJobs / cameras,
                expectedJobs,
                workers = Workers,
                entitlementActiveJobs = 2,
                seedMilliseconds = seedElapsed.TotalMilliseconds,
                drainMilliseconds = drainElapsed.TotalMilliseconds,
                jobsPerSecond = drainElapsed.TotalSeconds <= 0 ? 0 : completed / drainElapsed.TotalSeconds,
                fairnessJainIndex = jain,
                fairnessJainIndexByQuartile = jainByQuartile,
                completionsPerObservatoryMin = perObservatory.Min(),
                completionsPerObservatoryMax = perObservatory.Max(),
                maxActivePerObservatory,
                claimMilliseconds = Percentiles(claimDurations),
                queueLatencyMilliseconds = Percentiles(latencies),
                expiredAndReclaimed,
                cpuMilliseconds = cpu.TotalMilliseconds,
                allocatedBytes = allocations,
                workingSetBytes = process.WorkingSet64
            });
        }
        var evidence = new
        {
            schema = "hvo-processing-fairness-evidence-v1",
            issue = 429,
            revision,
            environment = new
            {
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                processors = Environment.ProcessorCount
            },
            method = "Deterministic arrival streams (one raw capture per sequence, every required recipe scheduled, so jobsPerCamera is the scheduled job count per camera), one observatory per camera with a 20-minute-old backlog on the first, 8 concurrent in-process claim loops with a 5 ms simulated recipe and every 11th successful first-attempt claim losing its lease; per-observatory entitlement 2, starvation age 1 day. fairnessJainIndex is the minimum of Jain's index over the first 25%, 50%, 75%, and 100% of completions in completion order.",
            streams,
            capacityGuidance = "Capacity follows the measured mix: claim latency and jobs/s at entitlement 2 scale with the number of claimable jobs, not the camera count; size runner concurrency from the sum of observatory entitlements you intend to honor concurrently and from the measured recipe durations of the placed mix."
        };
        var directory = Path.Combine(FindRepositoryRoot(), "TestResults", "processing-fairness", revision);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "processing-fairness-evidence.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceSerializerOptions)).ConfigureAwait(false);
        Console.WriteLine($"Processing fairness evidence written to {path}");
    }

    private static async Task RetryDeadlockAsync(Func<Task> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < 10
                && exception.GetBaseException() is Microsoft.Data.SqlClient.SqlException { Number: 1205 })
            {
                await Task.Delay(10 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    private static double JainIndex(IEnumerable<Guid> completions, IReadOnlyCollection<Guid> observatories)
    {
        var counts = completions.GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
        var shares = observatories.Select(id => counts.TryGetValue(id, out var count) ? (double)count : 0).ToArray();
        var sum = shares.Sum();
        return sum == 0 ? 0 : sum * sum / (shares.Length * shares.Sum(share => share * share));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static object Percentiles(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return new
        {
            count = sorted.Length,
            median = Percentile(sorted, 0.5),
            p95 = sorted.Length >= 30 ? Percentile(sorted, 0.95) : (double?)null,
            maximum = sorted.Length == 0 ? 0 : sorted[^1]
        };
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }
        var index = (int)Math.Round(fraction * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static async Task<int> CountClaimableAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, IReadOnlyCollection<Guid> observatories)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CentralDerivativeJobs.AsNoTracking().CountAsync(job =>
            job.Status == CentralDerivativeJobStatus.Pending
            && job.Inputs.Any()
            && observatories.Contains(job.SourceArtifact!.Frame!.ObservatoryId)).ConfigureAwait(false);
    }

    private static async Task<(Guid ObservatoryId, Guid DeviceId)> SeedObservatoryAsync(string scenario, int sources)
    {
        var name = $"{scenario}-{Guid.NewGuid():N}"[..Math.Min(40, scenario.Length + 33)];
        var device = Guid.NewGuid();
        Guid sourceId = Guid.Empty;
        for (var sequence = 1; sequence <= sources; sequence++)
        {
            sourceId = await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
                name, device, sequence, DateTimeOffset.UtcNow.AddMinutes(-10), SourcePayload, $"{scenario}-profile").ConfigureAwait(false);
        }
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var observatory = await db.CentralArtifacts.AsNoTracking().Where(artifact => artifact.Id == sourceId)
            .Select(artifact => artifact.Frame!.ObservatoryId).SingleAsync().ConfigureAwait(false);
        return (observatory, device);
    }

    private static async Task DisableClaimableJobsAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs.Where(job => job.Status != CentralDerivativeJobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
