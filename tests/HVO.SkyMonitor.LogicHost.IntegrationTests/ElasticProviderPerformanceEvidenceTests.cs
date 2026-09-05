using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Elastic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Tier C evidence for #430 (Manual): cold and warm start, backlog drain with 1, 2, and 4 local instances,
/// scale-to-zero and cleanup timing, and host CPU/memory, written to <c>TestResults/elastic-providers/</c>.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class ElasticProviderPerformanceEvidenceTests
{
    private static readonly byte[] SourcePayload = [1, 0, 2, 0, 3, 0, 4, 0];
    private static readonly int[] InstanceTiers = [1, 2, 4];
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };
    private const int JobsPerTier = 12;

    [TestMethod]
    public async Task Issue430ElasticProviders_RecordsEvidence()
    {
        var revision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? $"local-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var tiers = new List<object>();
        foreach (var maxInstances in InstanceTiers)
        {
            // The reset precedes the host so the autoscaler's startup sample finds no backlog and touches no rows.
            await DisableClaimableAsync().ConfigureAwait(false);
            await using var host = await ElasticProviderIntegrationTests.ElasticHost.StartAsync(maxInstances, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var tierStartedUtc = DateTimeOffset.UtcNow;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var jobs = new List<Guid>();
            for (var i = 0; i < JobsPerTier; i++)
            {
                jobs.Add(await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
                    $"elastic-evidence-{maxInstances}-{i}-{Guid.NewGuid():N}"[..40], Guid.NewGuid(), 1,
                    DateTimeOffset.UtcNow.AddMinutes(-5), SourcePayload, $"elastic-evidence-{maxInstances}").ConfigureAwait(false));
            }
            var drainStarted = Stopwatch.GetTimestamp();
            var decision = await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
            decision.Provision.Should().Be(maxInstances, "the backlog fills every allowed instance");
            var coldStarts = new List<double>();
            await host.SampleUntilAsync(async () =>
            {
                var rows = await InstancesAsync(host).ConfigureAwait(false);
                return rows.Count(row => row.StartedAtUtc >= tierStartedUtc && row.State == nameof(ElasticRunnerInstanceState.Running)) == maxInstances;
            }, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            var firstRegistered = Stopwatch.GetElapsedTime(drainStarted);
            coldStarts.AddRange((await InstancesAsync(host).ConfigureAwait(false)).Where(row => row.StartedAtUtc >= tierStartedUtc).Select(row => (double)(row.ColdStartMilliseconds ?? 0)));
            await ElasticProviderIntegrationTests.ElasticHost.WaitUntilAsync(async () =>
            {
                await using var scope = host.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return await db.CentralDerivativeJobs.AsNoTracking().CountAsync(job => jobs.Contains(job.SourceCentralArtifactId)
                    && job.RecipeName == HVO.SkyMonitor.Processing.BuiltInProcessingRecipes.EncodedPreview && job.Status == CentralDerivativeJobStatus.Completed).ConfigureAwait(false) == jobs.Count;
            }, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            var drained = Stopwatch.GetElapsedTime(drainStarted);
            // Warm reuse: one more job on the already-running instances, measured from availability to completion.
            var warmStarted = Stopwatch.GetTimestamp();
            var warmJob = await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
                $"elastic-warm-{maxInstances}-{Guid.NewGuid():N}"[..40], Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-5), SourcePayload, $"elastic-warm-{maxInstances}").ConfigureAwait(false);
            await ElasticProviderIntegrationTests.ElasticHost.WaitUntilAsync(async () => await host.JobStatusAsync(warmJob).ConfigureAwait(false) == CentralDerivativeJobStatus.Completed, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            var warm = Stopwatch.GetElapsedTime(warmStarted);
            // The runner processes do the recipe work: their CPU and peak memory are sampled while they are alive.
            double runnerCpu = 0, runnerPeakWorkingSet = 0, runnerWorkingSet = 0;
            var runnerProcesses = 0;
            foreach (var row in (await InstancesAsync(host).ConfigureAwait(false)).Where(row => row.StartedAtUtc >= tierStartedUtc && row.ProcessId is { }))
            {
                try
                {
                    using var runner = Process.GetProcessById(row.ProcessId!.Value);
                    runner.Refresh();
                    runnerCpu += runner.TotalProcessorTime.TotalMilliseconds;
                    runnerPeakWorkingSet += runner.PeakWorkingSet64;
                    runnerWorkingSet += runner.WorkingSet64;
                    runnerProcesses++;
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
            // Scale to zero after the idle delay, measured until every instance is stopped and no process remains.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var scaleDownStarted = Stopwatch.GetTimestamp();
            await host.SampleUntilAsync(async () => (await InstancesAsync(host).ConfigureAwait(false)).Where(row => row.StartedAtUtc >= tierStartedUtc).All(row => row.State == nameof(ElasticRunnerInstanceState.Stopped)), TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            var scaledDown = Stopwatch.GetElapsedTime(scaleDownStarted);
            (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
            process.Refresh();
            var rows2 = (await InstancesAsync(host).ConfigureAwait(false)).Where(row => row.StartedAtUtc >= tierStartedUtc).ToList();
            tiers.Add(new
            {
                maxInstances,
                jobs = jobs.Count,
                coldStartMilliseconds = new { min = coldStarts.Min(), max = coldStarts.Max(), mean = coldStarts.Average() },
                firstInstanceReadyMilliseconds = firstRegistered.TotalMilliseconds,
                drainMilliseconds = drained.TotalMilliseconds,
                jobsPerSecond = drained.TotalSeconds <= 0 ? 0 : jobs.Count / drained.TotalSeconds,
                warmJobMilliseconds = warm.TotalMilliseconds,
                scaleToZeroMilliseconds = scaledDown.TotalMilliseconds,
                instancesStopped = rows2.Count(row => row.State == nameof(ElasticRunnerInstanceState.Stopped)),
                orphans = rows2.Count(row => row.State == nameof(ElasticRunnerInstanceState.Orphaned)),
                hostCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                hostWorkingSetBytes = process.WorkingSet64,
                runnerProcessesSampled = runnerProcesses,
                runnerCpuMilliseconds = runnerCpu,
                runnerPeakWorkingSetBytes = runnerPeakWorkingSet,
                runnerWorkingSetBytes = runnerWorkingSet
            });
        }
        var evidence = new
        {
            schema = "hvo-elastic-provider-evidence-v1",
            issue = 430,
            revision,
            provider = "local-process",
            environment = new
            {
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                processors = Environment.ProcessorCount
            },
            method = "Kestrel-hosted LogicHost with the local-process adapter launching the real self-hosted runner binary; per tier 12 runner-placed encoded-preview jobs are seeded, the autoscaler is sampled until every allowed instance registers, drain is measured until all jobs complete, one warm job measures reuse, then the 2-second scale-to-zero delay is measured until every instance is stopped and no process remains. Runner CPU and peak working set are the sums over the tier's runner processes sampled while alive; host figures cover the test process with the in-process LogicHost.",
            tiers,
            capacityGuidance = "Cold start is the registration latency of a fresh runner process on this host; size MinWarmInstances so expected backlog arrival within QueueDeadline never waits on a cold start, and MaxInstances from the sum of observatory entitlements you intend to honor on elastic capacity."
        };
        var directory = Path.Combine(FindRepositoryRoot(), "TestResults", "elastic-providers", revision);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "elastic-provider-evidence.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        Console.WriteLine($"Elastic provider evidence written to {path}");
    }

    private static async Task<List<CentralElasticRunnerInstance>> InstancesAsync(ElasticProviderIntegrationTests.ElasticHost host)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CentralElasticRunnerInstances.AsNoTracking().Where(row => row.State != nameof(ElasticRunnerInstanceState.Stopped) || row.StoppedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-10))
            .OrderByDescending(row => row.StartedAtUtc).Take(8).ToListAsync().ConfigureAwait(false);
    }

    private static async Task DisableClaimableAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs.Where(job => job.Status != CentralDerivativeJobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        await db.CentralProcessingRunners.Where(runner => runner.Status != CentralProcessingRunnerStatus.Retired)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(runner => runner.RetiredAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(runner => runner.UpdatedAtUtc, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);
        await db.CentralElasticRunnerInstances.Where(row => row.State != nameof(ElasticRunnerInstanceState.Stopped) && row.State != nameof(ElasticRunnerInstanceState.Orphaned))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, nameof(ElasticRunnerInstanceState.Stopped))
                .SetProperty(row => row.Reason, "evidence-reset")
                .SetProperty(row => row.StoppedAtUtc, DateTimeOffset.UtcNow))
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
