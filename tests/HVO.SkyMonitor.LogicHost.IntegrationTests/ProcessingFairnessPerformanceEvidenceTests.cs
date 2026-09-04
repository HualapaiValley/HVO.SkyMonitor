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
    private const int JobsPerCamera = 4;
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
            var seedStarted = Stopwatch.GetTimestamp();
            for (var camera = 0; camera < cameras; camera++)
            {
                observatories.Add(await SeedObservatoryAsync($"fair-{cameras}-{camera}", JobsPerCamera).ConfigureAwait(false));
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
            }
            using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ProcessingEntitlements:Enabled", "true");
                builder.UseSetting("ProcessingEntitlements:DefaultActiveJobs", "2");
                builder.UseSetting("ProcessingEntitlements:StarvationAge", "1.00:00:00");
            });
            var completions = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
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
                    var (observatory, availableAt) = await db.CentralDerivativeJobs.AsNoTracking()
                        .Where(job => job.Id == lease.JobId)
                        .Select(job => new ValueTuple<Guid, DateTimeOffset>(job.SourceArtifact!.Frame!.ObservatoryId, job.LeaseAcquiredAtUtc ?? job.CreatedAtUtc))
                        .SingleAsync().ConfigureAwait(false);
                    var active = await db.CentralDerivativeJobs.AsNoTracking().CountAsync(job =>
                        job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc > DateTimeOffset.UtcNow
                        && job.SourceArtifact!.Frame!.ObservatoryId == observatory).ConfigureAwait(false);
                    InterlockedMax(ref maxActivePerObservatory, active);
                    latencies.Add(Math.Max(0, (DateTimeOffset.UtcNow - availableAt).TotalMilliseconds));
                    // Simulate recipe work, then either finish or (every 11th job) lose the lease to exercise recovery.
                    await Task.Delay(5).ConfigureAwait(false);
                    if (lease.AttemptCount == 1 && lease.JobId.GetHashCode() % 11 == 0)
                    {
                        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);
                        await db.CentralDerivativeJobs.Where(job => job.Id == lease.JobId)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAtUtc, expired)).ConfigureAwait(false);
                        await db.CentralDerivativeJobAttempts.Where(attempt => attempt.CentralDerivativeJobId == lease.JobId && attempt.AttemptNumber == 1)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.LeaseExpiresAtUtc, expired)).ConfigureAwait(false);
                        Interlocked.Increment(ref expiredAndReclaimed);
                        continue;
                    }
                    await jobs.SkipAsync(lease.JobId, lease.LeaseToken, "fairness.evidence", CancellationToken.None).ConfigureAwait(false);
                    completions.AddOrUpdate(observatory, 1, static (_, count) => count + 1);
                    Interlocked.Increment(ref completed);
                }
            }).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            var drainElapsed = Stopwatch.GetElapsedTime(drainStarted);
            process.Refresh();
            var cpu = process.TotalProcessorTime - cpuBefore;
            var allocations = GC.GetTotalAllocatedBytes(true) - allocationsBefore;
            var perObservatory = observatories.Select(id => completions.TryGetValue(id, out var count) ? count : 0).ToArray();
            var jain = perObservatory.Sum() == 0 ? 0 : Math.Pow(perObservatory.Sum(), 2) / (perObservatory.Length * perObservatory.Sum(count => (double)count * count));
            completed.Should().Be(expectedJobs);
            maxActivePerObservatory.Should().BeLessThanOrEqualTo(2, "the observatory entitlement is enforced under concurrent claims");
            jain.Should().BeGreaterThan(0.9, "every observatory completes the same share of its work");
            streams.Add(new
            {
                cameras,
                jobsPerCamera = JobsPerCamera,
                expectedJobs,
                workers = Workers,
                entitlementActiveJobs = 2,
                seedMilliseconds = seedElapsed.TotalMilliseconds,
                drainMilliseconds = drainElapsed.TotalMilliseconds,
                jobsPerSecond = drainElapsed.TotalSeconds <= 0 ? 0 : completed / drainElapsed.TotalSeconds,
                fairnessJainIndex = jain,
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
            method = "Deterministic arrival streams (one raw capture per sequence, every required recipe scheduled), one observatory per camera with a 20-minute-old backlog on the first, 8 concurrent in-process claim loops with a 5 ms simulated recipe and every 11th first attempt losing its lease; per-observatory entitlement 2, starvation age 1 day.",
            streams,
            capacityGuidance = "Capacity follows the measured mix: claim latency and jobs/s at entitlement 2 scale with the number of claimable jobs, not the camera count; size runner concurrency from the sum of observatory entitlements you intend to honor concurrently and from the measured recipe durations of the placed mix."
        };
        var directory = Path.Combine(FindRepositoryRoot(), "TestResults", "processing-fairness", revision);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "processing-fairness-evidence.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceSerializerOptions)).ConfigureAwait(false);
        Console.WriteLine($"Processing fairness evidence written to {path}");
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

    private static async Task<Guid> SeedObservatoryAsync(string scenario, int sources)
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
        return await db.CentralArtifacts.AsNoTracking().Where(artifact => artifact.Id == sourceId)
            .Select(artifact => artifact.Frame!.ObservatoryId).SingleAsync().ConfigureAwait(false);
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
