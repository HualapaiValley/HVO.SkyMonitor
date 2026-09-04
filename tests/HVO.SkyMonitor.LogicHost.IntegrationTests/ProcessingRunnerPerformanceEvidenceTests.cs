using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Tier C evidence for #428: cold/warm start stages, registration and claim latency, transfer bytes, in-process
/// versus runner throughput for the same frozen preview plan, and backlog with no runner. Output is revision-scoped
/// under <c>TestResults/processing-runner/</c>.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class ProcessingRunnerPerformanceEvidenceTests
{
    private const int Width = 1936;
    private const int Height = 1216;
    private const int Trials = 10;

    /// <summary>
    /// Runner-placed jobs seeded here stay pending for the shared in-process worker of other test classes; retire
    /// them (and this class's runners) so unrelated claim-order tests never observe them.
    /// </summary>
    [TestCleanup]
    public Task CleanupAsync() => DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory);
    private static readonly JsonSerializerOptions EvidenceSerializerOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task Issue428ProcessingRunner_RecordsEvidence()
    {
        var revision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? $"local-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var payload = CreatePayload(Width, Height);
        using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProcessingRunners:Enabled", "true");
            builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.EncodedPreview}", "Runner");
        });
        await DisableClaimableJobsAsync(factory).ConfigureAwait(false);

        // Cold and warm start: the first warm-up in this process pays JIT and native initialization; the second does not.
        var cold = await RunnerWarmup.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
        var warm = await RunnerWarmup.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);

        // In-process control: claim and execute N preview jobs through the shared pipeline.
        var inProcessSources = await SeedAsync("runner-perf-inprocess", payload, Trials).ConfigureAwait(false);
        var inProcessStarted = Stopwatch.GetTimestamp();
        var inProcessDurations = new List<double>(Trials);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ICentralDerivativeRunnerLeaseService>();
            var executor = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>();
            for (var trial = 0; trial < Trials; trial++)
            {
                var started = Stopwatch.GetTimestamp();
                var lease = await leases.ClaimNextAsync(
                    "perf-in-process", TimeSpan.FromMinutes(2),
                    CentralDerivativeClaimScope.Only([BuiltInProcessingRecipes.EncodedPreview]), CancellationToken.None)
                    .ConfigureAwait(false);
                lease.Should().NotBeNull();
                (await executor.ExecuteAsync(lease!, CancellationToken.None).ConfigureAwait(false)).Status
                    .Should().Be(ProcessingOutcomeStatus.Produced);
                inProcessDurations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        var inProcessElapsed = Stopwatch.GetElapsedTime(inProcessStarted);

        // Runner path: register, then claim/fetch/execute/complete N jobs over the protocol.
        var runnerSources = await SeedAsync("runner-perf-runner", payload, Trials).ConfigureAwait(false);
        using var http = factory.CreateClient();
        using var client = new ProcessingRunnerClient(http, new ProcessingRunnerClientOptions
        {
            RunnerId = $"perf-runner-{revision[..Math.Min(8, revision.Length)]}",
            ClientId = TestClients.SystemProcessingRunner.ClientId,
            ClientSecret = TestClients.SystemProcessingRunner.ClientSecret
        });
        var registrationStarted = Stopwatch.GetTimestamp();
        var registration = await client.RegisterAsync(new ProcessingRunnerRegistrationRequest(
            client.RunnerId, "Performance runner",
            ProcessingRunnerCapabilities.CreateForCurrentProcess(1, ProcessingRunnerProtocol.MaximumTransferBytes, null, null, null, warm.Stages),
            Environment.ProcessId, ProcessingRunnerProcessInfo.GetProcessStartedUtc()), CancellationToken.None).ConfigureAwait(false);
        var registrationMilliseconds = Stopwatch.GetElapsedTime(registrationStarted).TotalMilliseconds;
        registration.EligibleRecipes.Should().Contain(BuiltInProcessingRecipes.EncodedPreview);
        var claimLatencies = new List<double>(Trials);
        var fetchLatencies = new List<double>(Trials);
        var executeLatencies = new List<double>(Trials);
        var completeLatencies = new List<double>(Trials);
        var endToEnd = new List<double>(Trials);
        long inputBytes = 0;
        long productBytes = 0;
        var runnerStarted = Stopwatch.GetTimestamp();
        for (var trial = 0; trial < Trials; trial++)
        {
            var trialStarted = Stopwatch.GetTimestamp();
            var claimStarted = Stopwatch.GetTimestamp();
            var claim = await client.ClaimAsync(
                new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None)
                .ConfigureAwait(false);
            claimLatencies.Add(Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds);
            claim.Should().NotBeNull();
            var fetchStarted = Stopwatch.GetTimestamp();
            var payloads = new ReadOnlyMemory<byte>[claim!.Inputs.Count];
            long trialInputBytes = 0;
            for (var index = 0; index < payloads.Length; index++)
            {
                payloads[index] = await client.DownloadInputAsync(claim, claim.Inputs[index], CancellationToken.None).ConfigureAwait(false);
                trialInputBytes += payloads[index].Length;
            }
            inputBytes += trialInputBytes;
            fetchLatencies.Add(Stopwatch.GetElapsedTime(fetchStarted).TotalMilliseconds);
            var executeStarted = Stopwatch.GetTimestamp();
            var outcome = await warm.Executor.ExecuteAsync(
                ProcessingRunnerProjection.ReconstructRequest(claim, payloads), CancellationToken.None).ConfigureAwait(false);
            var executeElapsed = Stopwatch.GetElapsedTime(executeStarted);
            executeLatencies.Add(executeElapsed.TotalMilliseconds);
            outcome.Status.Should().Be(ProcessingOutcomeStatus.Produced);
            var (request, products) = ProcessingRunnerProjection.ProjectOutcome(claim.LeaseToken, outcome, trialInputBytes, executeElapsed);
            productBytes += products.Sum(static product => (long)product.Length);
            var completeStarted = Stopwatch.GetTimestamp();
            (await client.CompleteAsync(claim.JobId, request, products, CancellationToken.None).ConfigureAwait(false)).Status
                .Should().Be(ProcessingOutcomeStatus.Produced);
            completeLatencies.Add(Stopwatch.GetElapsedTime(completeStarted).TotalMilliseconds);
            endToEnd.Add(Stopwatch.GetElapsedTime(trialStarted).TotalMilliseconds);
        }
        var runnerElapsed = Stopwatch.GetElapsedTime(runnerStarted);
        await client.RetireAsync(CancellationToken.None).ConfigureAwait(false);

        // Backlog with no runner: placed jobs stay pending and the in-process claim never takes them.
        await SeedAsync("runner-perf-backlog", payload, 3).ConfigureAwait(false);
        int backlog;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
            while (await jobs.ClaimNextAsync("perf-in-process", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false) is { } lease)
            {
                lease.RecipeName.Should().NotBe(BuiltInProcessingRecipes.EncodedPreview);
            }
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            backlog = await db.CentralDerivativeJobs.CountAsync(job =>
                job.RecipeName == BuiltInProcessingRecipes.EncodedPreview
                && job.Status == CentralDerivativeJobStatus.Pending).ConfigureAwait(false);
        }
        backlog.Should().BeGreaterThanOrEqualTo(3);

        var evidence = new
        {
            schema = "hvo-processing-runner-evidence-v1",
            issue = 428,
            revision,
            environment = new
            {
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                processors = Environment.ProcessorCount
            },
            workload = new { id = "W1", width = Width, height = Height, pixelFormat = "Mono16", rawBytes = payload.Length, recipe = BuiltInProcessingRecipes.EncodedPreview, trials = Trials, concurrency = 1 },
            startup = new
            {
                coldStages = cold.Stages.All().Select(stage => new { stage.Name, status = stage.Status.ToString(), milliseconds = stage.Elapsed.TotalMilliseconds }),
                warmStages = warm.Stages.All().Select(stage => new { stage.Name, status = stage.Status.ToString(), milliseconds = stage.Elapsed.TotalMilliseconds }),
                registrationMilliseconds
            },
            inProcess = Summarize(inProcessDurations, inProcessElapsed),
            runner = new
            {
                claim = Percentiles(claimLatencies),
                fetch = Percentiles(fetchLatencies),
                execute = Percentiles(executeLatencies),
                complete = Percentiles(completeLatencies),
                endToEnd = Summarize(endToEnd, runnerElapsed),
                inputBytes,
                productBytes
            },
            backlogWithoutRunner = new { pendingRunnerPlacedJobs = backlog, inProcessClaimedRunnerPlaced = 0 },
            correctness = new { inProcessSources = inProcessSources.Count, runnerSources = runnerSources.Count, equivalence = "ProcessingRunnerProtocolIntegrationTests.Runner_OutputIsEquivalentToInProcessExecutionForTheSameFrozenPlan" }
        };
        var directory = Path.Combine(FindRepositoryRoot(), "TestResults", "processing-runner", revision);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "processing-runner-evidence.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceSerializerOptions)).ConfigureAwait(false);
        Console.WriteLine($"Processing runner evidence written to {path}");
    }

    private static object Summarize(List<double> samples, TimeSpan elapsed)
        => new
        {
            latency = Percentiles(samples),
            jobsPerSecond = elapsed.TotalSeconds <= 0 ? 0 : samples.Count / elapsed.TotalSeconds,
            totalMilliseconds = elapsed.TotalMilliseconds
        };

    private static object Percentiles(List<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return new
        {
            count = sorted.Length,
            median = Percentile(sorted, 0.5),
            minimum = sorted.Length == 0 ? 0 : sorted[0],
            maximum = sorted.Length == 0 ? 0 : sorted[^1],
            p95 = sorted.Length >= 30 ? Percentile(sorted, 0.95) : (double?)null
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "A deterministic synthetic frame payload is not a security input.")]
    private static byte[] CreatePayload(int width, int height)
    {
        var payload = new byte[width * height * 2];
        var random = new Random(428);
        random.NextBytes(payload);
        return payload;
    }

    private static async Task<List<Guid>> SeedAsync(string scenario, byte[] payload, int count)
    {
        var ids = new List<Guid>(count);
        var device = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        for (var sequence = 1; sequence <= count; sequence++)
        {
            ids.Add(await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
                $"{scenario}-{suffix}", device, sequence, DateTimeOffset.UtcNow.AddMinutes(-10), payload,
                $"{scenario}-profile", width: Width, height: Height).ConfigureAwait(false));
        }
        return ids;
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
        await db.CentralProcessingRunners.Where(runner => runner.Status != CentralProcessingRunnerStatus.Retired)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(runner => runner.RetiredAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(runner => runner.UpdatedAtUtc, DateTimeOffset.UtcNow))
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
