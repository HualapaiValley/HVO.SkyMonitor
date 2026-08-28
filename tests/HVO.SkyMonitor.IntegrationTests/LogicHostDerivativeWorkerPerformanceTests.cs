using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Data.Common;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Git and streamed SHA-256 observations are intentionally synchronous outside measured phases.")]
public sealed class LogicHostDerivativeWorkerPerformanceTests
{
    private const int CanonicalStandardWarmups = 5;
    private const int CanonicalStandardMeasurements = 30;
    private const int CanonicalQueueJobs = 10_000;
    private const int CanonicalQueueWarmups = 100;
    private const int CanonicalDuplicateSubmissions = 1_000;
    private const int CanonicalExpiredLeases = 1_000;
    private const int CanonicalConcurrentWarmups = 20;
    private const int CanonicalConcurrentMeasurements = 200;
    private const int CanonicalRecoveryTrials = 5;
    private const int CanonicalFaultJobs = 100;
    private const int CanonicalBacklogInitialJobs = 30;
    private const int CanonicalBacklogDisabledArrivals = 3;
    private const int CanonicalBacklogEnabledArrivals = 6;
    private const long RssSamplingToleranceBytes = 1024 * 1024;
    private const string SmokeVariable = "HVO_DERIVATIVE_PERF_SMOKE";
    private const string P5DiagnosticTrialVariable = "HVO_DERIVATIVE_P5_DIAGNOSTIC_TRIAL";
    private const string ArtifactBucket = "skymonitor-artifacts";
    private const string MeterName = "HVO.SkyMonitor.LogicHost.DerivativeWorker";
    private const string ActivitySourceName = "HVO.SkyMonitor.LogicHost.DerivativeWorker";
    private static readonly int[] QueueConcurrencyLevels = [1, 8];
    private static readonly int[] PreviewConcurrencyLevels = [1, 4, 8];
    private static readonly int[] BacklogConcurrencyLevels = [1, 4];
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    public async Task CentralDerivativeWorker_CanonicalWorkloads_RecordPerformanceEvidence()
    {
        var fixture = AssemblyHooks.Fixture;
        var scale = HarnessScale.Create();
        if (!scale.Smoke)
        {
            Assert.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                AppContext.BaseDirectory,
                StringComparison.Ordinal);
        }
        var repositoryRoot = GetRepositoryRoot();
        var revision = GetEvidenceRevision(repositoryRoot);
        var runId = Guid.NewGuid().ToString("N");
        var harnessStarted = Stopwatch.GetTimestamp();
        await DisableClaimableJobsAsync().ConfigureAwait(false);

        var w1 = CreateWorkload("W1", 1936, 1216, CameraPixelFormat.Mono16, seed: 2025);
        var w2 = CreateWorkload("W2", 3096, 2080, CameraPixelFormat.BayerRggb16, seed: 2025);
        var w0 = CreateWorkload("W0", 64, 48, CameraPixelFormat.Mono16, seed: 2025);
        await PublishSourceAsync(fixture, w0, runId).ConfigureAwait(false);
        await PublishSourceAsync(fixture, w1, runId).ConfigureAwait(false);
        await PublishSourceAsync(fixture, w2, runId).ConfigureAwait(false);

        using var telemetry = new TelemetryCollector();
        using var protocol = new WorkerProtocolCounter();
        using var protocolFactory = CreateProtocolFactory(fixture, protocol);
        var measuredServices = protocolFactory.Services;
        var p1 = new List<DerivativeMeasurement>();
        foreach (var recipeName in new[]
        {
            BuiltInProcessingRecipes.EncodedPreview,
            BuiltInProcessingRecipes.Annotation,
            BuiltInProcessingRecipes.ImageQuality
        })
        {
            p1.Add(await MeasureDerivativesAsync(
                fixture,
                telemetry,
                w2,
                recipeName,
                $"{runId}-P1-{recipeName}",
                scale.StandardWarmups,
                scale.StandardMeasurements,
                concurrency: 1,
                measuredServices,
                protocol).ConfigureAwait(false));
        }

        var p2 = new List<QueueMeasurement>();
        foreach (var concurrency in QueueConcurrencyLevels)
        {
            _ = await MeasureQueueAsync(
                fixture,
                telemetry,
                $"{runId}-P2-warmup-C{concurrency}",
                scale.QueueWarmups,
                expiredLeases: 0,
                duplicateSubmissions: 0,
                concurrency,
                measuredServices,
                protocol).ConfigureAwait(false);
            p2.Add(await MeasureQueueAsync(
                fixture,
                telemetry,
                $"{runId}-P2-C{concurrency}",
                scale.QueueJobs,
                scale.ExpiredLeases,
                scale.DuplicateSubmissions,
                concurrency,
                measuredServices,
                protocol).ConfigureAwait(false));
        }

        var p3 = new List<DerivativeMeasurement>();
        foreach (var concurrency in PreviewConcurrencyLevels)
        {
            p3.Add(await MeasureDerivativesAsync(
                fixture,
                telemetry,
                w1,
                BuiltInProcessingRecipes.EncodedPreview,
                $"{runId}-P3-C{concurrency}",
                scale.ConcurrentWarmups,
                scale.ConcurrentMeasurements,
                concurrency,
                measuredServices,
                protocol).ConfigureAwait(false));
        }

        var p4 = await MeasureFaultRecoveryAsync(
            fixture, telemetry, w0, $"{runId}-P4", scale).ConfigureAwait(false);
        foreach (var trial in p4.TrialEvidence)
        {
            var boundary = Enum.Parse<PublicationBoundary>(trial.PublicationBoundary.Boundary);
            var scenarioIds = boundary switch
            {
                PublicationBoundary.IntentCommitted => new[]
                {
                    "worker-before-output-crash",
                    "central-job-intent-commit"
                },
                PublicationBoundary.StagingWritten => ["central-job-staging-publication"],
                PublicationBoundary.CanonicalPublished => new[]
                {
                    "worker-after-output-before-completion-crash",
                    "central-job-canonical-publication"
                },
                PublicationBoundary.CompletionPreCommit => ["central-job-completion-before-commit"],
                PublicationBoundary.CompletionPostCommit => ["central-job-completion-after-commit"],
                _ => throw new ArgumentOutOfRangeException(nameof(boundary))
            };
            foreach (var scenarioId in scenarioIds)
            {
                await Phase14ScenarioEvidence.RecordAsync(
                    scenarioId,
                    $"{runId}-p4-{trial.Trial}-{boundary}-{scenarioId}",
                    boundary.ToString(),
                    boundary == PublicationBoundary.CompletionPostCommit
                        ? ["boundary-fault-observed", "completion-commit-remained-durable", "recovery-converged", "zero-final-backlog"]
                        : ["boundary-fault-observed", "completion-not-committed-before-restart", "recovery-converged", "zero-final-backlog"],
                    [
                        new Phase14EvidenceMeasurement("recovery-duration", (long)Math.Round(trial.RecoveryMilliseconds), "milliseconds"),
                        new Phase14EvidenceMeasurement("restart-backlog", trial.RestartBacklog.Count, "count")
                    ]).ConfigureAwait(false);
            }
        }
        var p5 = new List<BacklogRecoveryMeasurement>();
        foreach (var concurrency in BacklogConcurrencyLevels)
        {
            p5.Add(await MeasureBacklogRecoveryAsync(
                fixture, telemetry, w2, $"{runId}-P5-C{concurrency}", concurrency, scale)
                .ConfigureAwait(false));
        }

        var git = ReadGitEvidence(repositoryRoot);
        var evidence = new
        {
            Schema = "hvo-logichost-central-derivative-worker-performance-v2",
            Issue = 100,
            Revision = new { EvidenceDirectoryRevision = revision, git.Commit, git.Branch, git.Dirty },
            Command = "dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --configuration Release --filter \"FullyQualifiedName~LogicHostDerivativeWorkerPerformanceTests\"",
            Scale = new
            {
                CanonicalDefaults = new
                {
                    P1 = new { Warmups = CanonicalStandardWarmups, MeasurementsPerRecipe = CanonicalStandardMeasurements },
                    P2 = new { WarmupClaimsPerConcurrency = CanonicalQueueWarmups, JobsPerConcurrency = CanonicalQueueJobs, ExpiredLeasesPerConcurrency = CanonicalExpiredLeases, DuplicateSubmissionsPerConcurrency = CanonicalDuplicateSubmissions, QueueConcurrencyLevels },
                    P3 = new { WarmupsPerLevel = CanonicalConcurrentWarmups, MeasurementsPerLevel = CanonicalConcurrentMeasurements, PreviewConcurrencyLevels },
                    P4 = new { Trials = CanonicalRecoveryTrials, JobsPerTrial = CanonicalFaultJobs, ArrivalRatePerSecond = 2, OutageSeconds = 10 },
                    P5 = new { TrialsPerConcurrency = CanonicalRecoveryTrials, InitialJobs = CanonicalBacklogInitialJobs, DisabledSeconds = 30, DisabledArrivals = CanonicalBacklogDisabledArrivals, EnabledSeconds = 60, EnabledArrivals = CanonicalBacklogEnabledArrivals, BacklogConcurrencyLevels }
                },
                Selected = scale,
                SmokeOptInEnvironmentVariable = SmokeVariable,
                Canonical = !scale.Smoke
            },
            Workloads = new
            {
                P1 = new
                {
                    Id = "W2",
                    w2.Width,
                    w2.Height,
                    PixelFormat = w2.PixelFormat.ToString(),
                    w2.ByteLength,
                    Recipes = new[] { BuiltInProcessingRecipes.EncodedPreview, BuiltInProcessingRecipes.Annotation, BuiltInProcessingRecipes.ImageQuality },
                    Measurements = p1
                },
                P2 = new
                {
                    Id = "W3M",
                    Description = "Durable image-quality metadata-job claim, expired-lease reclaim, and terminal skip drain; recipe execution is intentionally excluded so this isolates queue behavior.",
                    Measurements = p2
                },
                P3 = new
                {
                    Id = "W4/W1",
                    w1.Width,
                    w1.Height,
                    PixelFormat = w1.PixelFormat.ToString(),
                    w1.ByteLength,
                    Recipe = BuiltInProcessingRecipes.EncodedPreview,
                    Measurements = p3
                },
                P4 = new
                {
                    Id = "W0/fault-recovery",
                    w0.Width,
                    w0.Height,
                    PixelFormat = w0.PixelFormat.ToString(),
                    w0.ByteLength,
                    Recipe = BuiltInProcessingRecipes.ImageQuality,
                    Dependency = "worker-facing MinIO",
                    Measurement = p4
                },
                P5 = new
                {
                    Id = "W2/backlog-recovery",
                    w2.Width,
                    w2.Height,
                    PixelFormat = w2.PixelFormat.ToString(),
                    w2.ByteLength,
                    Recipe = BuiltInProcessingRecipes.ImageQuality,
                    Measurements = p5
                }
            },
            Method = new
            {
                Boundary = "Derivative phases measure durable claim start through executor completion, including SQL claim, verified MinIO load, recipe execution, output publication/verification, lineage persistence, and durable completion. Queue phases measure claim through durable skip.",
                Latency = "Nearest-rank median/p95/maximum over independent measured operations after the declared warmups.",
                Throughput = "Closed-loop completed operations divided by measured wall time at the declared maximum concurrency.",
                StageTelemetry = "Production derivative-worker Meter histograms/counters and ActivitySource stages are captured after warmups. Harness-driven claims call the same telemetry methods used by the disabled hosted worker.",
                Resources = "Process.TotalProcessorTime, GC.GetTotalAllocatedBytes(false), and 10 ms Process.WorkingSet64 observations cover the in-process LogicHost/test runner only.",
                StorageEconomy = "All unique durable raw artifacts for a workload reference one immutable checksum-verified MinIO source object; output identities remain unique because every source artifact identity is unique.",
                QueueAge = "Initial and final unresolved counts and oldest durable queue age are read from SQL for each scenario.",
                Correctness = "Every derivative job is checked for exactly one unique result, processing identity, checksum, immediate source lineage, completed attempt, and zero final backlog; every result object is streamed and SHA-256 checked. Queue jobs verify reclaim attempt history and zero final backlog.",
                P4Recovery = "Jobs arrive on a fixed monotonic schedule while the worker is disabled. A fresh worker then observes a worker-facing MinIO outage for the declared duration; recovery starts with a fresh clean worker and ends at durable zero backlog.",
                P5Recovery = $"Thirty initial W2 jobs plus declared arrivals accumulate while disabled. Recovery includes continued arrivals, and ends only after arrivals stop and durable backlog returns to zero. Drain rate is (restart backlog + enabled arrivals) / enabled recovery duration. RSS plateau compares middle-third and final-third medians with a concurrency-plus-harness full-frame envelope and {RssSamplingToleranceBytes} bytes of declared sampling tolerance.",
                FiveTrialStatistics = "P4/P5 aggregate recovery values report median/minimum/maximum over five independent canonical trials; no p95 is inferred from five trials."
            },
            Environment = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                ServerGarbageCollection = GCSettings.IsServerGC,
                TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                SqlServer = DescribeSqlEndpoint(fixture.SqlServerConnectionString),
                fixture.MinioEndpoint
            },
            UnavailableEvidence = new
            {
                SqlWireBytes = "N/A: Microsoft.Data.SqlClient and EF Core do not expose authoritative SQL wire-byte counters; no estimate is reported.",
                ContainerResources = "N/A: IntegrationTestFixture does not expose portable per-container CPU, allocation, or RSS counters; process resources intentionally cover only the in-process host/test runner.",
                QueueOutputs = "N/A for P2: W3M isolates durable claim/reclaim behavior and drains jobs with the production SkipAsync transition, so no derivative object is expected.",
                NetworkTransport = "ASP.NET Core is in-process and MinIO/SQL run in Testcontainers; transport framing and kernel TCP/TLS costs are not separately attributed.",
                PublicationCrashMatrix = "Each of the five canonical P4 trials uses an in-process fail-stop surrogate at a different boundary: intent commit, staging write, canonical publication, completion pre-commit, or completion post-commit. Staging/canonical trials suppress normal cleanup and verify time-shifted production reconciliation. Metadata availability and job completion share one atomic SQL transaction, so pre/post commit are the honest sides of that boundary. A literal operating-system process kill remains outside this in-process harness."
            },
            HarnessElapsedMilliseconds = Stopwatch.GetElapsedTime(harnessStarted).TotalMilliseconds,
            RecordedAtUtc = DateTimeOffset.UtcNow
        };

        var outputDirectory = Path.Combine(
            repositoryRoot,
            "tests",
            "HVO.SkyMonitor.IntegrationTests",
            "TestResults",
            "central-derivative-worker",
            revision);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "logichost-derivative-worker-performance.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CentralDerivativeWorker_P5C1FreshProcess_RecordsDiagnosticEvidence()
    {
        var trialValue = Environment.GetEnvironmentVariable(P5DiagnosticTrialVariable);
        Assert.IsTrue(int.TryParse(trialValue, out var trial) && trial is >= 1 and <= CanonicalRecoveryTrials,
            $"{P5DiagnosticTrialVariable} must select one trial from 1 through {CanonicalRecoveryTrials}.");
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.Ordinal);
        Assert.IsTrue(GCSettings.IsServerGC, "Canonical P5 diagnostics require server GC.");

        var fixture = AssemblyHooks.Fixture;
        var repositoryRoot = GetRepositoryRoot();
        var revision = GetEvidenceRevision(repositoryRoot);
        var runId = $"issue-253-P5-C1-T{trial}-{Guid.NewGuid():N}";
        await DisableClaimableJobsAsync().ConfigureAwait(false);
        var workload = CreateWorkload("W2", 3096, 2080, CameraPixelFormat.BayerRggb16, seed: 2025);
        await PublishSourceAsync(fixture, workload, runId).ConfigureAwait(false);

        using var telemetry = new TelemetryCollector();
        var scale = HarnessScale.Create() with { RecoveryTrials = 1 };
        Assert.IsFalse(scale.Smoke, $"{SmokeVariable} is not valid for canonical P5 diagnostics.");
        var measurement = await MeasureBacklogRecoveryAsync(
            fixture,
            telemetry,
            workload,
            runId,
            concurrency: 1,
            scale,
            assertRssPlateau: false,
            retainRssDiagnostics: true).ConfigureAwait(false);
        var trialEvidence = measurement.TrialEvidence.Single();

        var outputDirectory = Path.Combine(
            repositoryRoot,
            "tests",
            "HVO.SkyMonitor.IntegrationTests",
            "TestResults",
            "central-derivative-worker",
            revision);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, $"p5-c1-fresh-process-trial-{trial:D2}.json"),
            JsonSerializer.Serialize(new
            {
                Schema = "hvo-logichost-central-derivative-worker-p5-diagnostic-v1",
                Issue = 253,
                Revision = new { EvidenceDirectoryRevision = revision, Git = ReadGitEvidence(repositoryRoot) },
                Trial = trial,
                Command = $"DOTNET_gcServer=1 {P5DiagnosticTrialVariable}={trial} HVO_EVIDENCE_REVISION={revision} dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~LogicHostDerivativeWorkerPerformanceTests.CentralDerivativeWorker_P5C1FreshProcess_RecordsDiagnosticEvidence",
                ProcessId = Environment.ProcessId,
                ServerGarbageCollection = GCSettings.IsServerGC,
                Workload = new { workload.Id, workload.Width, workload.Height, PixelFormat = workload.PixelFormat.ToString(), workload.ByteLength },
                Measurement = measurement,
                RecordedAtUtc = DateTimeOffset.UtcNow
            }, EvidenceJsonOptions)).ConfigureAwait(false);

        Assert.IsTrue(trialEvidence.RssPlateau.Passed,
            $"P5 RSS median growth {trialEvidence.RssPlateau.ObservedGrowthBytes} exceeded the {trialEvidence.RssPlateau.AllowedGrowthBytes}-byte full-frame envelope.");
    }

    private static async Task<DerivativeMeasurement> MeasureDerivativesAsync(
        IntegrationTestFixture fixture,
        TelemetryCollector telemetry,
        Workload workload,
        string recipeName,
        string scenario,
        int warmups,
        int measurements,
        int concurrency,
        IServiceProvider measuredServices,
        WorkerProtocolCounter protocol)
    {
        var jobIds = await SeedExecutableJobsAsync(
            fixture, workload, recipeName, scenario, warmups + measurements, DateTimeOffset.UtcNow)
            .ConfigureAwait(false);
        var enqueuedAtUtc = await ReadEnqueuedAtAsync(jobIds).ConfigureAwait(false);
        await ExecuteDerivativeJobsAsync(
            measuredServices, enqueuedAtUtc, warmups, concurrency, latencies: null).ConfigureAwait(false);
        telemetry.Clear();
        protocol.Clear();
        var initialBacklog = await ReadBacklogAsync(jobIds).ConfigureAwait(false);
        Assert.AreEqual(measurements, initialBacklog.Count);
        StabilizeGc();
        using var resources = new ResourceSampler();
        var latencies = new ConcurrentBag<double>();
        var started = Stopwatch.GetTimestamp();
        await ExecuteDerivativeJobsAsync(
            measuredServices, enqueuedAtUtc, measurements, concurrency, latencies).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var resourceEvidence = await resources.StopAsync().ConfigureAwait(false);
        var finalBacklog = await ReadBacklogAsync(jobIds).ConfigureAwait(false);
        Assert.AreEqual(0, finalBacklog.Count);
        var correctness = await ValidateDerivativeResultsAsync(fixture, jobIds).ConfigureAwait(false);
        var sorted = latencies.Order().ToArray();
        Assert.AreEqual(measurements, sorted.Length);
        var protocolEvidence = protocol.Snapshot();
        Assert.IsGreaterThan(0, protocolEvidence.SqlCommands);
        Assert.IsGreaterThan(0, protocolEvidence.Minio.Get);
        Assert.IsGreaterThan(0, protocolEvidence.Minio.Put);
        return new DerivativeMeasurement(
            scenario,
            workload.Id,
            recipeName,
            warmups,
            measurements,
            concurrency,
            elapsed.TotalMilliseconds,
            Percentile(sorted, 0.50),
            sorted.Length >= 30 ? Percentile(sorted, 0.95) : null,
            sorted[^1],
            measurements / elapsed.TotalSeconds,
            initialBacklog,
            finalBacklog,
            resourceEvidence,
            protocolEvidence,
            telemetry.Snapshot(),
            correctness);
    }

    private static async Task ExecuteDerivativeJobsAsync(
        IServiceProvider measuredServices,
        IReadOnlyDictionary<Guid, DateTimeOffset> enqueuedAtUtc,
        int count,
        int concurrency,
        ConcurrentBag<double>? latencies)
    {
        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (_, cancellationToken) =>
            {
                CentralDerivativeJobLease lease;
                await using (var claimScope = measuredServices.CreateAsyncScope())
                {
                    var telemetry = claimScope.ServiceProvider.GetRequiredService<CentralDerivativeWorkerTelemetry>();
                    var claimStarted = Stopwatch.GetTimestamp();
                    using (telemetry.StartStage("claim", "other"))
                    {
                        var service = claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
                        CentralDerivativeJobLease? claimed = null;
                        while (claimed is null)
                        {
                            claimed = await service.ClaimNextAsync(
                                "issue-100-performance", TimeSpan.FromMinutes(10), cancellationToken)
                                .ConfigureAwait(false);
                            if (claimed is null)
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
                            }
                        }
                        lease = claimed;
                    }
                    telemetry.RecordClaim("claimed", Stopwatch.GetElapsedTime(claimStarted));
                }

                await using (var executionScope = measuredServices.CreateAsyncScope())
                {
                    var result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                        .ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
                    Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, result.ReasonCode);
                    Assert.IsNotNull(result.ArtifactId);
                    executionScope.ServiceProvider.GetRequiredService<CentralDerivativeWorkerTelemetry>()
                        .RecordAttempt(lease.RecipeName, "produced", "none", DateTimeOffset.UtcNow);
                }
                latencies?.Add((DateTimeOffset.UtcNow - enqueuedAtUtc[lease.JobId]).TotalMilliseconds);
            }).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> ReadEnqueuedAtAsync(Guid[] jobIds)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CentralDerivativeJobs.AsNoTracking().Where(job => jobIds.Contains(job.Id))
            .ToDictionaryAsync(job => job.Id, job => job.CreatedAtUtc).ConfigureAwait(false);
    }

    private static async Task<QueueMeasurement> MeasureQueueAsync(
        IntegrationTestFixture fixture,
        TelemetryCollector telemetry,
        string scenario,
        int jobCount,
        int expiredLeases,
        int duplicateSubmissions,
        int concurrency,
        IServiceProvider measuredServices,
        WorkerProtocolCounter protocol)
    {
        await SeedQueueJobsAsync(fixture, scenario, jobCount, expiredLeases).ConfigureAwait(false);
        var duplicateEvidence = await MeasureDuplicateSubmissionsAsync(
            fixture, scenario, duplicateSubmissions, measuredServices, protocol).ConfigureAwait(false);
        telemetry.Clear();
        protocol.Clear();
        var initialBacklog = await ReadBacklogAsync(scenario).ConfigureAwait(false);
        Assert.AreEqual(jobCount, initialBacklog.Count);
        StabilizeGc();
        using var resources = new ResourceSampler();
        var claimLatencies = new ConcurrentBag<double>();
        var reclaimLatencies = new ConcurrentBag<double>();
        var operationLatencies = new ConcurrentBag<double>();
        var reclaimed = 0;
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, jobCount),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (_, cancellationToken) =>
            {
                var operationStarted = Stopwatch.GetTimestamp();
                await using var scope = measuredServices.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
                var workerTelemetry = scope.ServiceProvider.GetRequiredService<CentralDerivativeWorkerTelemetry>();
                var claimStarted = Stopwatch.GetTimestamp();
                CentralDerivativeJobLease? lease = null;
                while (lease is null)
                {
                    lease = await service.ClaimNextAsync(
                        $"issue-100-queue-{concurrency}", TimeSpan.FromMinutes(10), cancellationToken)
                        .ConfigureAwait(false);
                    if (lease is null)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
                    }
                }
                var claimElapsed = Stopwatch.GetElapsedTime(claimStarted);
                workerTelemetry.RecordClaim("claimed", claimElapsed);
                claimLatencies.Add(claimElapsed.TotalMilliseconds);
                Assert.AreEqual(BuiltInProcessingRecipes.ImageQuality, lease.RecipeName);
                if (lease.AttemptCount == 2)
                {
                    Interlocked.Increment(ref reclaimed);
                    reclaimLatencies.Add(claimElapsed.TotalMilliseconds);
                    workerTelemetry.RecordRecovery("lease-expired");
                }
                await service.SkipAsync(
                    lease.JobId, lease.LeaseToken, "performance.queue-drain", cancellationToken)
                    .ConfigureAwait(false);
                operationLatencies.Add(Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds);
            }).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var resourceEvidence = await resources.StopAsync().ConfigureAwait(false);
        Assert.AreEqual(expiredLeases, reclaimed);
        var finalBacklog = await ReadBacklogAsync(scenario).ConfigureAwait(false);
        Assert.AreEqual(0, finalBacklog.Count);
        var correctness = await ValidateQueueResultsAsync(scenario, jobCount, expiredLeases).ConfigureAwait(false);
        var sortedClaims = claimLatencies.Order().ToArray();
        var sortedReclaims = reclaimLatencies.Order().ToArray();
        var sortedOperations = operationLatencies.Order().ToArray();
        var protocolEvidence = protocol.Snapshot();
        Assert.IsGreaterThan(0, protocolEvidence.SqlCommands);
        Assert.AreEqual(0, protocolEvidence.Minio.Requests);
        return new QueueMeasurement(
            scenario,
            jobCount,
            expiredLeases,
            reclaimed,
            duplicateEvidence,
            concurrency,
            elapsed.TotalMilliseconds,
            Percentile(sortedClaims, 0.50),
            Percentile(sortedClaims, 0.95),
            sortedClaims[^1],
            sortedReclaims.Length == 0 ? null : Percentile(sortedReclaims, 0.50),
            sortedReclaims.Length < 30 ? null : Percentile(sortedReclaims, 0.95),
            sortedReclaims.Length == 0 ? null : sortedReclaims[^1],
            Percentile(sortedOperations, 0.50),
            Percentile(sortedOperations, 0.95),
            sortedOperations[^1],
            jobCount / elapsed.TotalSeconds,
            initialBacklog,
            finalBacklog,
            resourceEvidence,
            protocolEvidence,
            telemetry.Snapshot(),
            correctness);
    }

    private static async Task<FaultRecoveryMeasurement> MeasureFaultRecoveryAsync(
        IntegrationTestFixture fixture,
        TelemetryCollector telemetry,
        Workload workload,
        string scenario,
        HarnessScale scale)
    {
        var trials = new List<RecoveryTrialMeasurement>();
        for (var trial = 1; trial <= scale.RecoveryTrials; trial++)
        {
            var trialScenario = $"{scenario}-T{trial}";
            var jobIds = new ConcurrentBag<Guid>();
            var arrivalStarted = Stopwatch.GetTimestamp();
            await SubmitAtFixedRateAsync(
                scale.FaultJobs,
                scale.FaultArrivalInterval,
                async submittedAtUtc =>
                {
                    var ids = await SeedExecutableJobsAsync(
                        fixture,
                        workload,
                        BuiltInProcessingRecipes.ImageQuality,
                        $"{trialScenario}-{jobIds.Count:D4}",
                        1,
                        submittedAtUtc).ConfigureAwait(false);
                    jobIds.Add(ids[0]);
                }).ConfigureAwait(false);
            var arrivalElapsed = Stopwatch.GetElapsedTime(arrivalStarted);
            var ids = jobIds.ToArray();
            var initialBacklog = await ReadBacklogAsync(ids).ConfigureAwait(false);
            Assert.AreEqual(scale.FaultJobs, initialBacklog.Count);

            using var outage = new TimedMinioOutageHandler { InnerHandler = new SocketsHttpHandler() };
            using var outageFactory = CreateMinioFaultFactory(fixture, outage);
            _ = outageFactory.Services;
            outage.Arm();
            var outageStarted = Stopwatch.GetTimestamp();
            await using (var worker = CreateWorker(
                outageFactory.Services,
                $"issue-100-P4-outage-{trial}",
                concurrency: 1,
                leaseDuration: TimeSpan.FromSeconds(5)))
            {
                await worker.StartAsync().ConfigureAwait(false);
                await Task.Delay(scale.FaultOutageDuration).ConfigureAwait(false);
            }
            outage.Disarm();
            var outageElapsed = Stopwatch.GetElapsedTime(outageStarted);
            Assert.IsGreaterThan(0, outage.InjectedFailures);

            var boundary = (PublicationBoundary)((trial - 1) % Enum.GetValues<PublicationBoundary>().Length);
            var boundaryEvidence = await InjectPublicationBoundaryFaultAsync(
                fixture, boundary, trial).ConfigureAwait(false);
            if (!boundaryEvidence.CompletedBeforeRestart)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1100)).ConfigureAwait(false);
            }

            var restartBacklog = await ReadBacklogAsync(ids).ConfigureAwait(false);
            Assert.IsGreaterThan(0, restartBacklog.Count);
            telemetry.Clear();
            StabilizeGc();
            using var resources = new ResourceSampler();
            var recoveryStarted = Stopwatch.GetTimestamp();
            await using (var worker = CreateWorker(
                fixture.Factory.Services,
                $"issue-100-P4-recovery-{trial}",
                concurrency: 1,
                leaseDuration: TimeSpan.FromSeconds(5)))
            {
                await worker.StartAsync().ConfigureAwait(false);
                await WaitForCompletionAsync(ids, scale.RecoveryTimeout).ConfigureAwait(false);
            }
            var recoveryElapsed = Stopwatch.GetElapsedTime(recoveryStarted);
            var resourceEvidence = await resources.StopAsync().ConfigureAwait(false);
            var finalBacklog = await ReadBacklogAsync(ids).ConfigureAwait(false);
            Assert.AreEqual(0, finalBacklog.Count);
            var correctness = await ValidateDerivativeResultsAsync(
                fixture, ids, allowRecoveryAttempts: true).ConfigureAwait(false);
            if (boundaryEvidence.StagingCleanupSuppressed)
            {
                var reconciliationCycles = await ReconcileExpiredStagingAsync(
                    fixture, boundaryEvidence.StagingObjectKey!).ConfigureAwait(false);
                Assert.IsFalse(await ObjectExistsAsync(
                    fixture, boundaryEvidence.StagingObjectKey!).ConfigureAwait(false));
                boundaryEvidence = boundaryEvidence with
                {
                    StagingObjectReconciled = true,
                    StagingReconciliationCycles = reconciliationCycles
                };
            }
            trials.Add(new RecoveryTrialMeasurement(
                trial,
                scale.FaultJobs,
                arrivalElapsed.TotalMilliseconds,
                scale.FaultJobs / arrivalElapsed.TotalSeconds,
                outageElapsed.TotalMilliseconds,
                outage.InjectedFailures,
                boundaryEvidence,
                recoveryElapsed.TotalMilliseconds,
                restartBacklog.Count / recoveryElapsed.TotalSeconds,
                restartBacklog.LogicalInputBytes / recoveryElapsed.TotalSeconds,
                initialBacklog,
                restartBacklog,
                finalBacklog,
                resourceEvidence,
                telemetry.Snapshot(),
                correctness));
        }

        return new FaultRecoveryMeasurement(
            trials.Count,
            scale.FaultJobs,
            1d / scale.FaultArrivalInterval.TotalSeconds,
            scale.FaultOutageDuration.TotalMilliseconds,
            SummarizeTrials(trials.Select(trial => trial.RecoveryMilliseconds)),
            SummarizeTrials(trials.Select(trial => trial.JobsPerSecond)),
            trials);
    }

    private static async Task<PublicationBoundaryEvidence> InjectPublicationBoundaryFaultAsync(
        IntegrationTestFixture fixture,
        PublicationBoundary boundary,
        int trial)
    {
        CentralDerivativeJobLease lease;
        while (true)
        {
            await using var claimScope = fixture.Factory.Services.CreateAsyncScope();
            var claimed = await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync(
                    $"issue-100-P4-boundary-{trial}",
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None).ConfigureAwait(false);
            if (claimed is not null)
            {
                lease = claimed;
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }

        using var factory = CreatePublicationFaultFactory(fixture, boundary);
        _ = factory.Services;
        var publicationFault = factory.Services.GetService<PublicationFaultHandler>();
        if (boundary == PublicationBoundary.CompletionPreCommit)
        {
            factory.Services.GetRequiredService<CompletionCommitFaultInterceptor>().Arm();
        }
        Exception? observed = null;
        try
        {
            await using var executionScope = factory.Services.CreateAsyncScope();
            _ = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            observed = exception;
        }
        Assert.IsNotNull(observed, $"The {boundary} publication fault was not observed.");

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var job = await db.CentralDerivativeJobs.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == lease.JobId).ConfigureAwait(false);
        var evidence = await db.CentralArtifactProcessingEvidence.AsNoTracking()
            .Include(item => item.Artifact)
            .SingleOrDefaultAsync(item => item.CentralDerivativeJobId == lease.JobId).ConfigureAwait(false);
        var completed = job.Status == CentralDerivativeJobStatus.Completed;
        if (boundary == PublicationBoundary.CompletionPostCommit)
        {
            Assert.IsTrue(completed);
        }
        else
        {
            Assert.IsFalse(completed);
        }
        var stagingObjectExists = publicationFault?.StagingObjectKey is { } stagingObjectKey
            && await ObjectExistsAsync(fixture, stagingObjectKey).ConfigureAwait(false);
        var expectedCleanupSuppression = boundary is PublicationBoundary.StagingWritten
            or PublicationBoundary.CanonicalPublished;
        Assert.AreEqual(expectedCleanupSuppression, publicationFault?.StagingCleanupSuppressed == true);
        if (expectedCleanupSuppression)
        {
            Assert.IsTrue(stagingObjectExists);
        }
        return new PublicationBoundaryEvidence(
            boundary.ToString(),
            lease.JobId,
            observed!.GetType().Name,
            job.Status.ToString(),
            evidence?.Artifact?.ObjectState.ToString(),
            evidence is not null,
            completed,
            publicationFault?.StagingCleanupSuppressed == true,
            publicationFault?.StagingObjectKey,
            stagingObjectExists,
            StagingObjectReconciled: false,
            StagingReconciliationCycles: null);
    }

    private static async Task<int> ReconcileExpiredStagingAsync(
        IntegrationTestFixture fixture,
        string stagingObjectKey)
    {
        const string derivativePrefix = "staging/derivatives/";
        Assert.IsTrue(stagingObjectKey.StartsWith(derivativePrefix, StringComparison.Ordinal));
        var partition = 31 + Convert.ToInt32(stagingObjectKey[derivativePrefix.Length].ToString(), 16);
        await using (var checkpointScope = fixture.Factory.Services.CreateAsyncScope())
        {
            await checkpointScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .CentralRecoveryCheckpoints.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Phase, CentralRecoveryPhases.Idle)
                    .SetProperty(item => item.StagingPartition, partition)
                    .SetProperty(item => item.StagingCursor, (string?)null)
                    .SetProperty(item => item.NextInventoryAtUtc, DateTimeOffset.UtcNow.AddDays(1))
                    .SetProperty(item => item.LeaseToken, (Guid?)null)
                    .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }
        using var telemetry = new CentralIngestTelemetry();
        var reconciliation = new CentralArtifactReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new OffsetTimeProvider(CentralArtifactReconciliationService.StagingObjectGracePeriod + TimeSpan.FromMinutes(1)),
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);
        await reconciliation.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        if (await ObjectExistsAsync(fixture, stagingObjectKey).ConfigureAwait(false))
        {
            Assert.Fail("The targeted derivative staging partition did not remove the captured expired object.");
        }
        return 1;
    }

    private static async Task<bool> ObjectExistsAsync(IntegrationTestFixture fixture, string objectKey)
    {
        try
        {
            await fixture.Factory.Services.GetRequiredService<IMinioClient>()
                .StatObjectAsync(new StatObjectArgs().WithBucket(ArtifactBucket).WithObject(objectKey))
                .ConfigureAwait(false);
            return true;
        }
        catch (Minio.Exceptions.MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return false;
        }
    }

    private static async Task<BacklogRecoveryMeasurement> MeasureBacklogRecoveryAsync(
        IntegrationTestFixture fixture,
        TelemetryCollector telemetry,
        Workload workload,
        string scenario,
        int concurrency,
        HarnessScale scale,
        bool assertRssPlateau = true,
        bool retainRssDiagnostics = false)
    {
        var trials = new List<BacklogRecoveryTrialMeasurement>();
        for (var trial = 1; trial <= scale.RecoveryTrials; trial++)
        {
            var trialScenario = $"{scenario}-T{trial}";
            var initialIds = await SeedExecutableJobsAsync(
                fixture,
                workload,
                BuiltInProcessingRecipes.ImageQuality,
                $"{trialScenario}-initial",
                scale.BacklogInitialJobs,
                DateTimeOffset.UtcNow).ConfigureAwait(false);
            var allIds = new ConcurrentBag<Guid>(initialIds);
            await SubmitAtFixedRateAsync(
                scale.BacklogDisabledArrivals,
                scale.BacklogArrivalInterval,
                async submittedAtUtc =>
                {
                    var ids = await SeedExecutableJobsAsync(
                        fixture,
                        workload,
                        BuiltInProcessingRecipes.ImageQuality,
                        $"{trialScenario}-disabled-{allIds.Count:D3}",
                        1,
                        submittedAtUtc).ConfigureAwait(false);
                    allIds.Add(ids[0]);
                }).ConfigureAwait(false);
            var disabledBacklog = await ReadBacklogAsync(allIds.ToArray()).ConfigureAwait(false);
            Assert.AreEqual(scale.BacklogInitialJobs + scale.BacklogDisabledArrivals, disabledBacklog.Count);

            telemetry.Clear();
            StabilizeGc();
            using var resources = new ResourceSampler(retainRssDiagnostics);
            var recoveryStarted = Stopwatch.GetTimestamp();
            var initialDrain = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var monitoringCancellation = new CancellationTokenSource();
            var monitor = MonitorInitialDrainAsync(initialIds, recoveryStarted, initialDrain, monitoringCancellation.Token);
            try
            {
                await using var worker = CreateWorker(
                    fixture.Factory.Services,
                    $"issue-100-P5-C{concurrency}-T{trial}",
                    concurrency,
                    leaseDuration: TimeSpan.FromSeconds(10));
                await worker.StartAsync().ConfigureAwait(false);
                await SubmitAtFixedRateAsync(
                        scale.BacklogEnabledArrivals,
                        scale.BacklogArrivalInterval,
                        async submittedAtUtc =>
                        {
                            var ids = await SeedExecutableJobsAsync(
                                fixture,
                                workload,
                                BuiltInProcessingRecipes.ImageQuality,
                                $"{trialScenario}-enabled-{allIds.Count:D3}",
                                1,
                                submittedAtUtc).ConfigureAwait(false);
                            allIds.Add(ids[0]);
                        })
                    .ConfigureAwait(false);
                await WaitForCompletionAsync(allIds.ToArray(), scale.RecoveryTimeout).ConfigureAwait(false);
            }
            finally
            {
                await monitoringCancellation.CancelAsync().ConfigureAwait(false);
                await monitor.ConfigureAwait(false);
            }
            var recoveryElapsed = Stopwatch.GetElapsedTime(recoveryStarted);
            var resourceEvidence = await resources.StopAsync().ConfigureAwait(false);
            var ids = allIds.ToArray();
            var finalBacklog = await ReadBacklogAsync(ids).ConfigureAwait(false);
            Assert.AreEqual(0, finalBacklog.Count);
            var correctness = await ValidateDerivativeResultsAsync(fixture, ids).ConfigureAwait(false);
            var arrivalAdjustedDrainRate = ids.Length / recoveryElapsed.TotalSeconds;
            Assert.IsGreaterThan(0.1, arrivalAdjustedDrainRate);
            var rssGrowth = Math.Max(0, resourceEvidence.RssFinalThirdMedianBytes
                - resourceEvidence.RssMiddleThirdMedianBytes);
            var allowedRssGrowth = checked((concurrency + 1L) * workload.ByteLength
                + RssSamplingToleranceBytes);
            var rssPlateauPassed = rssGrowth <= allowedRssGrowth;
            if (!scale.Smoke && assertRssPlateau)
            {
                Assert.IsTrue(rssPlateauPassed,
                    $"P5 RSS median growth {rssGrowth} exceeded the {allowedRssGrowth}-byte full-frame envelope.");
            }
            trials.Add(new BacklogRecoveryTrialMeasurement(
                trial,
                concurrency,
                ids.Length,
                scale.BacklogInitialJobs,
                scale.BacklogDisabledArrivals,
                scale.BacklogEnabledArrivals,
                initialDrain.Task.IsCompletedSuccessfully ? initialDrain.Task.Result : recoveryElapsed.TotalMilliseconds,
                recoveryElapsed.TotalMilliseconds,
                arrivalAdjustedDrainRate,
                disabledBacklog,
                finalBacklog,
                resourceEvidence,
                new RssPlateauEvidence(
                    resourceEvidence.RssSamples,
                    resourceEvidence.RssMiddleThirdMedianBytes,
                    resourceEvidence.RssFinalThirdMedianBytes,
                    allowedRssGrowth,
                    rssGrowth,
                    rssPlateauPassed),
                telemetry.Snapshot(),
                correctness));
        }

        return new BacklogRecoveryMeasurement(
            concurrency,
            trials.Count,
            scale.BacklogInitialJobs,
            scale.BacklogDisabledArrivals,
            scale.BacklogEnabledArrivals,
            1d / scale.BacklogArrivalInterval.TotalSeconds,
            SummarizeTrials(trials.Select(trial => trial.InitialBacklogDrainMilliseconds)),
            SummarizeTrials(trials.Select(trial => trial.RecoveryMilliseconds)),
            SummarizeTrials(trials.Select(trial => trial.ArrivalAdjustedDrainRate)),
            trials);
    }

    private static async Task SubmitAtFixedRateAsync(
        int count,
        TimeSpan interval,
        Func<DateTimeOffset, Task> submit)
    {
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < count; index++)
        {
            var due = interval * (index + 1);
            var remaining = due - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining).ConfigureAwait(false);
            }
            await submit(DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
    }

    private static async Task MonitorInitialDrainAsync(
        Guid[] initialJobIds,
        long recoveryStarted,
        TaskCompletionSource<double> drained,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!await HasBacklogAsync(initialJobIds, cancellationToken).ConfigureAwait(false))
                    {
                        drained.TrySetResult(Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds);
                        return;
                    }
                }
                catch (SqlException exception) when (exception.Number == 1205)
                {
                    // The worker may deadlock this observer query while transitioning the same jobs.
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<bool> HasBacklogAsync(Guid[] jobIds, CancellationToken cancellationToken)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CentralDerivativeJobs.AsNoTracking().AnyAsync(job =>
            jobIds.Contains(job.Id) && (job.Status == CentralDerivativeJobStatus.Pending
                || job.Status == CentralDerivativeJobStatus.RetryableFailure
                || job.Status == CentralDerivativeJobStatus.Leased), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForCompletionAsync(Guid[] jobIds, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var completed = await db.CentralDerivativeJobs.AsNoTracking()
                .CountAsync(job => jobIds.Contains(job.Id)
                    && job.Status == CentralDerivativeJobStatus.Completed).ConfigureAwait(false);
            if (completed == jobIds.Length)
            {
                return;
            }
            var failed = await db.CentralDerivativeJobs.AsNoTracking()
                .CountAsync(job => jobIds.Contains(job.Id)
                    && (job.Status == CentralDerivativeJobStatus.TerminalFailure
                        || job.Status == CentralDerivativeJobStatus.Quarantined
                        || job.Status == CentralDerivativeJobStatus.Canceled
                        || job.Status == CentralDerivativeJobStatus.Skipped)).ConfigureAwait(false);
            Assert.AreEqual(0, failed, "Recovery produced a terminal derivative job.");
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
        Assert.Fail($"Derivative recovery did not converge within {timeout}.");
    }

    private static TrialDistribution SummarizeTrials(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new TrialDistribution(sorted.Length, Percentile(sorted, 0.5), sorted[0], sorted[^1]);
    }

    private static async Task<Guid[]> SeedExecutableJobsAsync(
        IntegrationTestFixture fixture,
        Workload workload,
        string recipeName,
        string scenario,
        int count,
        DateTimeOffset? submittedAtUtc = null)
    {
        var recipe = new CentralDerivativeRecipeCatalog().GetRequiredRecipes(FrameArtifactRole.Raw)
            .Single(item => string.Equals(item.RecipeName, recipeName, StringComparison.Ordinal));
        var now = submittedAtUtc ?? DateTimeOffset.UtcNow;
        var availableAtUtc = submittedAtUtc ?? now.AddMinutes(-1);
        var sceneJson = JsonSerializer.Serialize(CreateScene(workload));
        var rawOptions = CaptureContractJson.SerializeToElement(new { });
        var rawOptionsJson = CaptureContractJson.Canonicalize(rawOptions).GetRawText();
        var rawOptionsSha = CaptureContractJson.ComputeCanonicalJsonSha256(rawOptions);
        var profileSha = HashText($"{scenario}-profiles");
        var devicePublicId = Guid.NewGuid();
        var jobIds = new Guid[count];
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        for (var index = 0; index < count; index++)
        {
            var captured = now.AddTicks(index);
            var frame = new CentralFrame
            {
                RegistrationId = Guid.NewGuid(),
                DevicePublicId = devicePublicId,
                ObservatoryId = Guid.NewGuid(),
                AgentId = "issue-100-performance",
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = captured,
                FirstReceivedAtUtc = captured,
                RigProfileVersion = 1,
                RigId = $"issue-100-{workload.Id}",
                CaptureSequence = index + 1L,
                SceneProvenanceJson = sceneJson
            };
            frame.Timing = new CentralCaptureTiming
            {
                RequestedStartUtc = captured.AddSeconds(-2),
                ExposureStartedUtc = captured.AddSeconds(-1),
                ExposureEndedUtc = captured.AddMilliseconds(-100),
                ReadoutCompletedUtc = captured.AddMilliseconds(-50),
                DurableIngressUtc = captured
            };
            frame.Control = new CentralCaptureControl
            {
                RequestedExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
                EffectiveExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
                RequestedGain = 100,
                EffectiveGain = 100,
                EffectiveTemperatureC = -5
            };
            foreach (var kind in Enum.GetValues<CentralProfileKind>())
            {
                frame.Profiles.Add(new CentralCaptureProfile
                {
                    Kind = kind,
                    Name = $"issue-100-{kind}",
                    Version = "1",
                    Sha256 = profileSha
                });
            }

            var source = new CentralArtifact
            {
                Frame = frame,
                CentralFrameId = frame.Id,
                ArtifactId = Guid.NewGuid(),
                DevicePublicId = devicePublicId,
                Role = FrameArtifactRole.Raw,
                RecipeVersion = "issue-100-raw-v1",
                ManifestSchemaVersion = "v2",
                MediaType = "application/octet-stream",
                ByteLength = workload.ByteLength,
                ChecksumSha256 = workload.ChecksumSha256,
                StorageReference = $"minio://{ArtifactBucket}/{workload.ObjectKey}",
                ReceivedAtUtc = captured,
                IdempotencyKey = HashText($"{scenario}-source-{index}"),
                SourceId = "issue-100-performance",
                Variant = "native",
                CreatedUtc = captured,
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                ReconciledAtUtc = captured,
                Layout = new CentralArtifactLayout
                {
                    Width = workload.Width,
                    Height = workload.Height,
                    StrideBytes = workload.StrideBytes,
                    PixelFormat = workload.PixelFormat.ToString(),
                    ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                    SampleDepthBits = 16,
                    ContainerDepthBits = 16,
                    Packing = FrameSamplePacking.ByteAligned.ToString(),
                    CfaPattern = (workload.PixelFormat == CameraPixelFormat.BayerRggb16
                        ? ColorFilterArrayPattern.Rggb
                        : ColorFilterArrayPattern.None).ToString(),
                    BlackLevel = workload.PixelFormat == CameraPixelFormat.BayerRggb16 ? 64 : 0,
                    WhiteLevel = workload.PixelFormat == CameraPixelFormat.BayerRggb16 ? 16383 : ushort.MaxValue,
                    ByteLength = workload.ByteLength
                },
                Recipe = new CentralArtifactRecipe
                {
                    Name = "raw-capture",
                    SemanticVersion = "1.0.0",
                    ImplementationVersion = "issue-100-v1",
                    OptionsJson = rawOptionsJson,
                    OptionsSha256 = rawOptionsSha
                }
            };
            var job = CreateJob(source, recipe, availableAtUtc);
            jobIds[index] = job.Id;
            db.CentralFrames.Add(frame);
            db.CentralArtifacts.Add(source);
            db.CentralDerivativeJobs.Add(job);
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        return jobIds;
    }

    private static async Task SeedQueueJobsAsync(
        IntegrationTestFixture fixture,
        string scenario,
        int jobCount,
        int expiredLeases)
    {
        var recipe = new CentralDerivativeRecipeCatalog().GetRequiredRecipes(FrameArtifactRole.Raw)
            .Single(item => item.RecipeName == BuiltInProcessingRecipes.ImageQuality);
        var now = DateTimeOffset.UtcNow;
        var queuePayload = new byte[8];
        var queueChecksum = Convert.ToHexString(SHA256.HashData(queuePayload));
        var queueObjectKey = $"performance/{scenario}/queue-source.raw";
        var rawOptions = CaptureContractJson.SerializeToElement(new { });
        var rawOptionsJson = CaptureContractJson.Canonicalize(rawOptions).GetRawText();
        var rawOptionsSha = CaptureContractJson.ComputeCanonicalJsonSha256(rawOptions);
        var profileSha = HashText($"{scenario}-queue-profiles");
        var sourceIds = new List<Guid>();
        await using (var objectScope = fixture.Factory.Services.CreateAsyncScope())
        {
            await using var stream = new MemoryStream(queuePayload, writable: false);
            await objectScope.ServiceProvider.GetRequiredService<IMinioClient>()
                .PutObjectAsync(new PutObjectArgs()
                    .WithBucket(ArtifactBucket)
                    .WithObject(queueObjectKey)
                    .WithStreamData(stream)
                    .WithObjectSize(queuePayload.LongLength)
                    .WithContentType("application/octet-stream"))
                .ConfigureAwait(false);
        }
        await using (var sourceScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = sourceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var devicePublicId = Guid.NewGuid();
            var sourceCount = Math.Max(1, (int)Math.Ceiling(jobCount / 10d));
            for (var index = 0; index < sourceCount; index++)
            {
                var captured = now.AddTicks(index);
                var frame = new CentralFrame
                {
                    RegistrationId = Guid.NewGuid(),
                    DevicePublicId = devicePublicId,
                    ObservatoryId = Guid.NewGuid(),
                    AgentId = "issue-100-queue",
                    FrameId = Guid.NewGuid(),
                    RigProfileVersion = 1,
                    RigId = "issue-100-queue",
                    CaptureSequence = index + 1,
                    CapturedAtUtc = captured,
                    FirstReceivedAtUtc = captured
                };
                frame.Timing = new CentralCaptureTiming
                {
                    RequestedStartUtc = captured.AddSeconds(-2),
                    ExposureStartedUtc = captured.AddSeconds(-1),
                    ExposureEndedUtc = captured.AddMilliseconds(-100),
                    ReadoutCompletedUtc = captured.AddMilliseconds(-50),
                    DurableIngressUtc = captured
                };
                frame.Control = new CentralCaptureControl
                {
                    RequestedExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
                    EffectiveExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
                    RequestedGain = 100,
                    EffectiveGain = 100,
                    EffectiveTemperatureC = -5
                };
                foreach (var kind in Enum.GetValues<CentralProfileKind>())
                {
                    frame.Profiles.Add(new CentralCaptureProfile
                    {
                        Kind = kind,
                        Name = $"issue-100-queue-{kind}",
                        Version = "1",
                        Sha256 = profileSha
                    });
                }
                var source = new CentralArtifact
                {
                    Frame = frame,
                    CentralFrameId = frame.Id,
                    ArtifactId = Guid.NewGuid(),
                    DevicePublicId = frame.DevicePublicId,
                    Role = FrameArtifactRole.Raw,
                    RecipeVersion = "issue-100-queue-source-v1",
                    ManifestSchemaVersion = "v2",
                    MediaType = "application/octet-stream",
                    ByteLength = queuePayload.LongLength,
                    ChecksumSha256 = queueChecksum,
                    StorageReference = $"minio://{ArtifactBucket}/{queueObjectKey}",
                    ReceivedAtUtc = captured,
                    IdempotencyKey = HashText($"{scenario}-queue-source-{index}"),
                    SourceId = "issue-100-queue",
                    Variant = "native",
                    CreatedUtc = captured,
                    ObjectState = CentralArtifactObjectState.Available,
                    ReconstructionState = CentralReconstructionState.Complete,
                    ReconciledAtUtc = captured,
                    Layout = new CentralArtifactLayout
                    {
                        Width = 2,
                        Height = 2,
                        StrideBytes = 4,
                        PixelFormat = CameraPixelFormat.Mono16.ToString(),
                        ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                        SampleDepthBits = 16,
                        ContainerDepthBits = 16,
                        Packing = FrameSamplePacking.ByteAligned.ToString(),
                        CfaPattern = ColorFilterArrayPattern.None.ToString(),
                        BlackLevel = 0,
                        WhiteLevel = ushort.MaxValue,
                        ByteLength = queuePayload.LongLength
                    },
                    Recipe = new CentralArtifactRecipe
                    {
                        Name = "raw-capture",
                        SemanticVersion = "1.0.0",
                        ImplementationVersion = "issue-100-v1",
                        OptionsJson = rawOptionsJson,
                        OptionsSha256 = rawOptionsSha
                    }
                };
                sourceIds.Add(source.Id);
                db.CentralFrames.Add(frame);
                db.CentralArtifacts.Add(source);
            }
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        const int batchSize = 500;
        for (var offset = 0; offset < jobCount; offset += batchSize)
        {
            await using var batchScope = fixture.Factory.Services.CreateAsyncScope();
            var db = batchScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var end = Math.Min(jobCount, offset + batchSize);
            var batchSourceIds = sourceIds.Skip(offset / 10).Take((end - offset + 9) / 10).ToArray();
            var sources = await db.CentralArtifacts.Where(item => batchSourceIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id).ConfigureAwait(false);
            for (var index = offset; index < end; index++)
            {
                var source = sources[sourceIds[index / 10]];
                var expired = index < expiredLeases;
                var job = CreateJob(source, recipe, now.AddMinutes(-2));
                job.TargetVariant = $"{scenario}-{index:D5}";
                job.RequestIdentitySha256 = HashText($"{scenario}-queue-job-{index}");
                if (expired)
                {
                    var acquired = now.AddMinutes(-2);
                    var expires = now.AddMinutes(-1);
                    job.Status = CentralDerivativeJobStatus.Leased;
                    job.AttemptCount = 1;
                    job.AvailableAtUtc = null;
                    job.LeaseOwner = "expired-issue-100-worker";
                    job.LeaseToken = Guid.NewGuid();
                    job.LeaseAcquiredAtUtc = acquired;
                    job.LeaseExpiresAtUtc = expires;
                    job.Attempts.Add(new CentralDerivativeJobAttempt
                    {
                        AttemptNumber = 1,
                        WorkerId = job.LeaseOwner,
                        LeaseAcquiredAtUtc = acquired,
                        LeaseExpiresAtUtc = expires,
                        Outcome = CentralDerivativeAttemptOutcome.Leased
                    });
                }
                db.CentralDerivativeJobs.Add(job);
            }
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    private static async Task<DuplicateSubmissionEvidence> MeasureDuplicateSubmissionsAsync(
        IntegrationTestFixture fixture,
        string scenario,
        int duplicateSubmissions,
        IServiceProvider measuredServices,
        WorkerProtocolCounter protocol)
    {
        if (duplicateSubmissions == 0)
        {
            return new DuplicateSubmissionEvidence(0, 0, 0, 0, true, null, null);
        }
        Guid sourceId;
        await using (var lookupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = lookupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            sourceId = await db.CentralDerivativeJobs.Where(job => job.TargetVariant.StartsWith(scenario))
                .Select(job => job.SourceCentralArtifactId)
                .FirstAsync().ConfigureAwait(false);
        }

        await SubmitRequiredJobsAsync(sourceId).ConfigureAwait(false);
        protocol.Clear();
        StabilizeGc();
        using var resources = new ResourceSampler();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < duplicateSubmissions; index++)
        {
            await SubmitRequiredJobsAsync(sourceId).ConfigureAwait(false);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var resourceEvidence = await resources.StopAsync().ConfigureAwait(false);
        var protocolEvidence = protocol.Snapshot();
        Assert.IsGreaterThan(0, protocolEvidence.SqlCommands);

        await using var assertionScope = fixture.Factory.Services.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var canonicalJobs = await assertionDb.CentralDerivativeJobs.Where(job =>
                job.SourceCentralArtifactId == sourceId && !job.TargetVariant.StartsWith(scenario))
            .ToListAsync().ConfigureAwait(false);
        Assert.AreEqual(4, canonicalJobs.Count);
        Assert.AreEqual(4, canonicalJobs.Select(job => job.RequestIdentitySha256).Distinct().Count());
        foreach (var job in canonicalJobs)
        {
            job.Status = CentralDerivativeJobStatus.TerminalFailure;
            job.AvailableAtUtc = null;
        }
        await assertionDb.SaveChangesAsync().ConfigureAwait(false);
        return new DuplicateSubmissionEvidence(
            duplicateSubmissions,
            elapsed.TotalMilliseconds,
            duplicateSubmissions / elapsed.TotalSeconds,
            canonicalJobs.Count,
            true,
            resourceEvidence,
            protocolEvidence);

        async Task SubmitRequiredJobsAsync(Guid artifactId)
        {
            await using var scope = measuredServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var source = await db.CentralArtifacts.Include(artifact => artifact.Frame)!
                .ThenInclude(frame => frame!.Artifacts)
                .SingleAsync(artifact => artifact.Id == artifactId).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>()
                .EnsureRequiredJobsAsync(source, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static CentralDerivativeJob CreateJob(
        CentralArtifact source,
        CentralDerivativeRecipe recipe,
        DateTimeOffset createdAtUtc)
    {
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = recipe.TargetRole,
            TargetRecipeVersion = recipe.RecipeVersion,
            TargetVariant = recipe.TargetVariant,
            RecipeName = recipe.RecipeName,
            RecipeOptionsJson = CaptureContractJson.Canonicalize(recipe.Options).GetRawText(),
            InputSelectorJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(recipe.InputSelector)).GetRawText(),
            RequestedRecipeIdentitySha256 = recipe.RequestedRecipeIdentitySha256,
            RequestIdentitySha256 = CentralDerivativeJobIdentity.CreateRequestIdentity(
                source.DevicePublicId, source.ArtifactId, recipe),
            Status = CentralDerivativeJobStatus.Pending,
            ResolutionCompletedAtUtc = createdAtUtc,
            AttemptCount = 0,
            MaxAttempts = recipe.MaxAttempts,
            AvailableAtUtc = createdAtUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = 0,
            BindingName = "input",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            SequenceOffset = 0,
            IsRequired = true,
            SelectorJson = job.InputSelectorJson,
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = source.Frame?.AgentId ?? string.Empty,
            ExpectedRigId = source.Frame?.RigId,
            ExpectedCaptureSequence = source.Frame?.CaptureSequence,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = createdAtUtc
        };
        job.InputRequirements.Add(requirement);
        job.Inputs.Add(new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = 0,
            CentralArtifactId = source.Id,
            Artifact = source,
            CaptureSequence = source.Frame?.CaptureSequence,
            CompatibilityJson = "{}",
            CompatibilitySha256 = new string('0', 64),
            ByteLength = source.ByteLength,
            SelectedAtUtc = createdAtUtc
        });
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        return job;
    }

    private static async Task<DerivativeCorrectness> ValidateDerivativeResultsAsync(
        IntegrationTestFixture fixture,
        Guid[] jobIds,
        bool allowRecoveryAttempts = false)
    {
        List<CentralDerivativeJob> jobs;
        Dictionary<Guid, CentralArtifactProcessingEvidence> evidenceByJob;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobs = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => jobIds.Contains(job.Id))
                .Include(job => job.Attempts)
                .Include(job => job.SourceArtifact)
                .Include(job => job.ResultArtifact)!.ThenInclude(artifact => artifact!.Sources)
                .Include(job => job.ResultArtifact)!.ThenInclude(artifact => artifact!.Recipe)
                .AsSplitQuery()
                .ToListAsync().ConfigureAwait(false);
            evidenceByJob = await db.CentralArtifactProcessingEvidence.AsNoTracking()
                .Where(item => jobIds.Contains(item.CentralDerivativeJobId))
                .ToDictionaryAsync(item => item.CentralDerivativeJobId).ConfigureAwait(false);
        }
        Assert.AreEqual(jobIds.Length, jobs.Count);
        Assert.AreEqual(jobIds.Length, evidenceByJob.Count);
        Assert.AreEqual(jobIds.Length, jobs.Select(job => job.ResultCentralArtifactId).Distinct().Count());

        long outputBytes = 0;
        await using var validationScope = fixture.Factory.Services.CreateAsyncScope();
        var minio = validationScope.ServiceProvider.GetRequiredService<IMinioClient>();
        foreach (var job in jobs)
        {
            Assert.AreEqual(CentralDerivativeJobStatus.Completed, job.Status);
            Assert.IsNull(job.LeaseOwner);
            Assert.IsNull(job.LeaseToken);
            Assert.IsNull(job.LeaseAcquiredAtUtc);
            Assert.IsNull(job.LeaseExpiresAtUtc);
            Assert.AreEqual(job.AttemptCount, job.Attempts.Count);
            Assert.AreEqual(0, job.Attempts.Count(attempt =>
                attempt.Outcome == CentralDerivativeAttemptOutcome.Leased));
            Assert.AreEqual(1, job.Attempts.Count(attempt =>
                attempt.Outcome == CentralDerivativeAttemptOutcome.Completed));
            Assert.AreEqual(CentralDerivativeAttemptOutcome.Completed,
                job.Attempts.OrderBy(attempt => attempt.AttemptNumber).Last().Outcome);
            if (!allowRecoveryAttempts)
            {
                Assert.AreEqual(1, job.AttemptCount);
            }
            var artifact = job.ResultArtifact ?? throw new AssertFailedException("The completed job has no result artifact.");
            var processing = evidenceByJob[job.Id];
            Assert.AreEqual(job.RequestedRecipeIdentitySha256, processing.RequestedRecipeIdentitySha256, ignoreCase: true);
            Assert.AreEqual(job.Id, processing.CentralDerivativeJobId);
            Assert.AreEqual(artifact.ArtifactId, ProcessingIdentity.CreateArtifactId(processing.OutputIdentitySha256));
            Assert.AreEqual(1, artifact.Sources.Count);
            Assert.AreEqual(job.SourceArtifact!.ArtifactId, artifact.Sources.Single().SourceArtifactId);
            Assert.AreEqual(job.SourceCentralArtifactId, artifact.Sources.Single().ResolvedCentralArtifactId);
            Assert.AreEqual(
                processing.OutputIdentitySha256,
                ProcessingIdentity.CreateOutputIdentity(
                    artifact.Role,
                    artifact.Variant!,
                    processing.RecipeIdentitySha256,
                    [job.SourceArtifact.ArtifactId]),
                ignoreCase: true);
            var recipe = artifact.Recipe ?? throw new AssertFailedException("The derivative result has no recipe evidence.");
            using var options = JsonDocument.Parse(recipe.OptionsJson);
            var recomputedRecipe = ProcessingIdentity.CreateRecipeIdentity(new RecipeIdentityDescriptor(
                recipe.Name,
                recipe.SemanticVersion,
                recipe.ImplementationVersion,
                options.RootElement.Clone(),
                recipe.OptionsSha256));
            Assert.AreEqual(processing.RecipeIdentitySha256, recomputedRecipe.IdentitySha256, ignoreCase: true);
            Assert.IsFalse(string.IsNullOrWhiteSpace(processing.AlgorithmsJson));
            Assert.IsFalse(string.IsNullOrWhiteSpace(processing.CompatibilityJson));
            Assert.AreEqual(CentralArtifactObjectState.Available, artifact.ObjectState);
            Assert.AreEqual(CentralReconstructionState.Complete, artifact.ReconstructionState);
            string? checksum = null;
            var objectKey = artifact.StorageReference[$"minio://{ArtifactBucket}/".Length..];
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(ArtifactBucket)
                .WithObject(objectKey)
                .WithCallbackStream(stream => checksum = Convert.ToHexString(SHA256.HashData(stream))))
                .ConfigureAwait(false);
            Assert.AreEqual(artifact.ChecksumSha256, checksum, ignoreCase: true);
            outputBytes += artifact.ByteLength;
        }
        return new DerivativeCorrectness(
            true,
            jobs.Count,
            jobs.Count,
            evidenceByJob.Count,
            jobs.Sum(job => job.Attempts.Count(attempt =>
                attempt.Outcome == CentralDerivativeAttemptOutcome.Completed)),
            outputBytes,
            "Every result object was streamed and SHA-256 checked; every SQL result, output identity, immediate lineage edge, processing-evidence row, and completed attempt was asserted.");
    }

    private static async Task<QueueCorrectness> ValidateQueueResultsAsync(
        string scenario,
        int jobCount,
        int expiredLeases)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobs = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.TargetVariant.StartsWith(scenario))
            .Include(job => job.Attempts)
            .AsSplitQuery()
            .ToListAsync().ConfigureAwait(false);
        Assert.AreEqual(jobCount, jobs.Count);
        Assert.AreEqual(jobCount, jobs.Count(job => job.Status == CentralDerivativeJobStatus.Skipped));
        Assert.AreEqual(expiredLeases, jobs.Count(job => job.AttemptCount == 2));
        Assert.AreEqual(jobCount - expiredLeases, jobs.Count(job => job.AttemptCount == 1));
        Assert.AreEqual(expiredLeases, jobs.Sum(job => job.Attempts.Count(attempt =>
            attempt.Outcome == CentralDerivativeAttemptOutcome.LeaseExpired)));
        Assert.AreEqual(jobCount, jobs.Sum(job => job.Attempts.Count(attempt =>
            attempt.Outcome == CentralDerivativeAttemptOutcome.Skipped)));
        return new QueueCorrectness(
            true,
            jobs.Count,
            jobs.Sum(job => job.Attempts.Count),
            expiredLeases,
            jobCount,
            "Each pre-expired lease has a LeaseExpired first attempt and Skipped second attempt; every normal job has one Skipped attempt.");
    }

    private static async Task<BacklogSnapshot> ReadBacklogAsync(Guid[] jobIds)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(job => jobIds.Contains(job.Id) && (job.Status == CentralDerivativeJobStatus.Pending
                || job.Status == CentralDerivativeJobStatus.RetryableFailure
                || job.Status == CentralDerivativeJobStatus.Leased))
            .Select(job => new
            {
                job.Status,
                AgeFrom = job.AvailableAtUtc ?? job.CreatedAtUtc,
                InputBytes = job.SourceArtifact!.ByteLength
            })
            .ToListAsync().ConfigureAwait(false);
        return CreateBacklog(rows.Select(row => (row.Status, row.AgeFrom, row.InputBytes)).ToArray());
    }

    private static async Task<BacklogSnapshot> ReadBacklogAsync(string scenario)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.TargetVariant.StartsWith(scenario) && (job.Status == CentralDerivativeJobStatus.Pending
                || job.Status == CentralDerivativeJobStatus.RetryableFailure
                || job.Status == CentralDerivativeJobStatus.Leased))
            .Select(job => new
            {
                job.Status,
                AgeFrom = job.AvailableAtUtc ?? job.CreatedAtUtc,
                InputBytes = job.SourceArtifact!.ByteLength
            })
            .ToListAsync().ConfigureAwait(false);
        return CreateBacklog(rows.Select(row => (row.Status, row.AgeFrom, row.InputBytes)).ToArray());
    }

    private static BacklogSnapshot CreateBacklog(
        (CentralDerivativeJobStatus Status, DateTimeOffset AgeFrom, long InputBytes)[] rows)
    {
        var now = DateTimeOffset.UtcNow;
        return new BacklogSnapshot(
            rows.Length,
            rows.Count(row => row.Status == CentralDerivativeJobStatus.Pending),
            rows.Count(row => row.Status == CentralDerivativeJobStatus.Leased),
            rows.Count(row => row.Status == CentralDerivativeJobStatus.RetryableFailure),
            rows.Length == 0 ? 0 : Math.Max(0, (now - rows.Min(row => row.AgeFrom)).TotalMilliseconds),
            rows.Sum(row => row.InputBytes));
    }

    private static async Task DisableClaimableJobsAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobAttempts
            .Where(attempt => attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.Outcome, CentralDerivativeAttemptOutcome.TerminalFailure)
                .SetProperty(attempt => attempt.EndedAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(attempt => attempt.ReasonCode, "performance.isolation"))
            .ConfigureAwait(false);
        await db.CentralDerivativeJobs
            .Where(job => job.Status == CentralDerivativeJobStatus.Pending
                || job.Status == CentralDerivativeJobStatus.RetryableFailure
                || job.Status == CentralDerivativeJobStatus.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
    }

    private static Workload CreateWorkload(
        string id,
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        int seed)
    {
        var stride = checked(width * 2);
        var payload = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = pixelFormat == CameraPixelFormat.Mono16
                    ? (ushort)((seed + 257L * (y * (long)width + x)) & 0xFFFF)
                    : (ushort)((64 + seed + 31 * x + 17 * y + 997 * ((y & 1) * 2 + (x & 1))) & 0x3FFF);
                var offset = y * stride + x * 2;
                payload[offset] = (byte)value;
                payload[offset + 1] = (byte)(value >> 8);
            }
        }
        return new Workload(
            id,
            width,
            height,
            stride,
            pixelFormat,
            payload,
            Convert.ToHexString(SHA256.HashData(payload)));
    }

    private static async Task PublishSourceAsync(
        IntegrationTestFixture fixture,
        Workload workload,
        string runId)
    {
        workload.ObjectKey = $"performance/central-derivative-worker/{runId}/{workload.Id}.raw";
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(ArtifactBucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(ArtifactBucket)).ConfigureAwait(false);
        }
        await using var stream = new MemoryStream(workload.Payload, writable: false);
        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(ArtifactBucket)
            .WithObject(workload.ObjectKey)
            .WithStreamData(stream)
            .WithObjectSize(workload.Payload.LongLength)
                .WithContentType("application/octet-stream")).ConfigureAwait(false);
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateMinioFaultFactory(
        IntegrationTestFixture fixture,
        TimedMinioOutageHandler outage)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(outage, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateProtocolFactory(
        IntegrationTestFixture fixture,
        WorkerProtocolCounter protocol)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(fixture.SqlServerConnectionString);
                options.AddInterceptors(protocol.Database);
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            });
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(protocol.Http, disposeHandler: false), disposeHttpClient: true)
                .Build());
        }));

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreatePublicationFaultFactory(
        IntegrationTestFixture fixture,
        PublicationBoundary boundary)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (boundary is PublicationBoundary.IntentCommitted
                or PublicationBoundary.StagingWritten
                or PublicationBoundary.CanonicalPublished)
            {
                var handler = new PublicationFaultHandler(boundary) { InnerHandler = new SocketsHttpHandler() };
                services.AddSingleton(handler);
                services.RemoveAll<IMinioClient>();
                services.AddSingleton<IMinioClient>(provider => new MinioClient()
                    .WithEndpoint(fixture.MinioEndpoint)
                    .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                    .WithHttpClient(
                        new HttpClient(provider.GetRequiredService<PublicationFaultHandler>(), disposeHandler: false),
                        disposeHttpClient: true)
                    .Build());
            }
            if (boundary == PublicationBoundary.CompletionPreCommit)
            {
                services.AddSingleton<CompletionCommitFaultInterceptor>();
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();
                services.AddDbContext<ApplicationDbContext>((provider, options) =>
                {
                    options.UseSqlServer(fixture.SqlServerConnectionString);
                    options.AddInterceptors(provider.GetRequiredService<CompletionCommitFaultInterceptor>());
                    options.EnableSensitiveDataLogging();
                    options.EnableDetailedErrors();
                });
            }
            if (boundary == PublicationBoundary.CompletionPostCommit)
            {
                services.RemoveAll<ICentralDerivativeJobExecutor>();
                services.AddScoped<CentralDerivativeJobExecutor>();
                services.AddScoped<ICentralDerivativeJobExecutor>(provider =>
                    new PostCommitFaultExecutor(provider.GetRequiredService<CentralDerivativeJobExecutor>()));
            }
        }));

    private static WorkerHandle CreateWorker(
        IServiceProvider services,
        string workerId,
        int concurrency,
        TimeSpan leaseDuration)
    {
        var options = Options.Create(new CentralDerivativeWorkerOptions
        {
            WorkerId = workerId,
            Concurrency = concurrency,
            PollInterval = TimeSpan.FromMilliseconds(20),
            QueueSampleInterval = TimeSpan.FromHours(1),
            LeaseDuration = leaseDuration,
            RenewalInterval = TimeSpan.FromSeconds(1),
            ShutdownTimeout = TimeSpan.FromSeconds(10)
        });
        var worker = new CentralDerivativeWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            services.GetRequiredService<CentralDerivativeWorkerTelemetry>(),
            TimeProvider.System,
            NullLogger<CentralDerivativeWorker>.Instance);
        return new WorkerHandle(worker);
    }

    private static SceneProvenance CreateScene(Workload workload)
        => new(
            $"issue-100-{workload.Id}",
            "issue-100-rig-v1",
            "performance-fixture",
            "1",
            HashText("issue-100-catalog"),
            "equidistant",
            "performance-projection-v1",
            "performance-astronomy-v1",
            "performance-sensor-v1",
            Objects:
            [
                new ProjectedObjectProvenance("star:1", "Vega", workload.Width * 0.35, workload.Height * 0.4, 0.03),
                new ProjectedObjectProvenance("star:2", "Deneb", workload.Width * 0.65, workload.Height * 0.6, 1.25)
            ],
            Segments:
            [
                new ProjectedSegmentProvenance(
                    "constellation:lyra", "star:1", "star:2",
                    workload.Width * 0.35, workload.Height * 0.4,
                    workload.Width * 0.65, workload.Height * 0.6)
            ]);

    private sealed class WorkerHandle(CentralDerivativeWorker worker) : IAsyncDisposable
    {
        private bool _started;

        public async Task StartAsync()
        {
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            _started = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_started)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await worker.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            worker.Dispose();
        }
    }

    private sealed class TimedMinioOutageHandler : DelegatingHandler
    {
        private int _armed;
        private long _injectedFailures;

        public long InjectedFailures => Interlocked.Read(ref _injectedFailures);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Disarm() => Volatile.Write(ref _armed, 0);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _armed) == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _injectedFailures);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "<Error><Code>ServiceUnavailable</Code><Message>Injected worker-facing outage</Message></Error>",
                        Encoding.UTF8,
                        "application/xml")
                };
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class WorkerProtocolCounter : IDisposable
    {
        public WorkerProtocolCounter()
        {
            Http = new CountingHttpHandler { InnerHandler = new SocketsHttpHandler() };
            Database = new CountingDbCommandInterceptor();
        }

        public CountingHttpHandler Http { get; }

        public CountingDbCommandInterceptor Database { get; }

        public void Clear()
        {
            Http.Clear();
            Database.Clear();
        }

        public ProtocolSnapshot Snapshot() => new(Database.Commands, Http.Snapshot());

        public void Dispose() => Http.Dispose();
    }

    private sealed class CountingDbCommandInterceptor : DbCommandInterceptor
    {
        private long _commands;

        public long Commands => Interlocked.Read(ref _commands);

        public void Clear() => Interlocked.Exchange(ref _commands, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountingHttpHandler : DelegatingHandler
    {
        private long _requests;
        private long _head;
        private long _get;
        private long _put;
        private long _copy;
        private long _delete;
        private long _requestBytes;
        private long _responseBytes;
        private long _unknownRequestLengths;
        private long _unknownResponseLengths;

        public void Clear()
        {
            Interlocked.Exchange(ref _requests, 0);
            Interlocked.Exchange(ref _head, 0);
            Interlocked.Exchange(ref _get, 0);
            Interlocked.Exchange(ref _put, 0);
            Interlocked.Exchange(ref _copy, 0);
            Interlocked.Exchange(ref _delete, 0);
            Interlocked.Exchange(ref _requestBytes, 0);
            Interlocked.Exchange(ref _responseBytes, 0);
            Interlocked.Exchange(ref _unknownRequestLengths, 0);
            Interlocked.Exchange(ref _unknownResponseLengths, 0);
        }

        public ObjectProtocolSnapshot Snapshot()
            => new(
                Interlocked.Read(ref _requests),
                Interlocked.Read(ref _head),
                Interlocked.Read(ref _get),
                Interlocked.Read(ref _put),
                Interlocked.Read(ref _copy),
                Interlocked.Read(ref _delete),
                Interlocked.Read(ref _requestBytes),
                Interlocked.Read(ref _responseBytes),
                Interlocked.Read(ref _unknownRequestLengths),
                Interlocked.Read(ref _unknownResponseLengths));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref _head);
            }
            else if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref _get);
            }
            else if (request.Method == HttpMethod.Put)
            {
                Interlocked.Increment(ref _put);
                if (request.Headers.Contains("x-amz-copy-source"))
                {
                    Interlocked.Increment(ref _copy);
                }
            }
            else if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref _delete);
            }
            RecordLength(request.Content?.Headers.ContentLength, ref _requestBytes, ref _unknownRequestLengths);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            RecordLength(response.Content.Headers.ContentLength, ref _responseBytes, ref _unknownResponseLengths);
            return response;
        }

        private static void RecordLength(long? length, ref long bytes, ref long unknown)
        {
            if (length.HasValue)
            {
                Interlocked.Add(ref bytes, length.Value);
            }
            else
            {
                Interlocked.Increment(ref unknown);
            }
        }
    }

    private enum PublicationBoundary
    {
        IntentCommitted,
        StagingWritten,
        CanonicalPublished,
        CompletionPreCommit,
        CompletionPostCommit
    }

    private sealed class PublicationFaultHandler(PublicationBoundary boundary) : DelegatingHandler
    {
        private int _canonicalReads;
        private int _injected;
        private int _stagingCleanupSuppressed;

        public bool StagingCleanupSuppressed => Volatile.Read(ref _stagingCleanupSuppressed) == 1;

        public string? StagingObjectKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var stagingPut = request.Method == HttpMethod.Put
                && path.Contains("/staging/derivatives/", StringComparison.Ordinal)
                && !request.Headers.Contains("x-amz-copy-source");
            if (stagingPut)
            {
                StagingObjectKey = Uri.UnescapeDataString(path[(path.IndexOf("/staging/", StringComparison.Ordinal) + 1)..]);
            }
            var copy = request.Method == HttpMethod.Put && request.Headers.Contains("x-amz-copy-source");
            var canonicalRead = (request.Method == HttpMethod.Head || request.Method == HttpMethod.Get)
                && path.Contains("/derivatives/", StringComparison.Ordinal);
            var shouldFail = boundary switch
            {
                PublicationBoundary.IntentCommitted => stagingPut,
                PublicationBoundary.StagingWritten => copy,
                PublicationBoundary.CanonicalPublished => canonicalRead
                    && Interlocked.Increment(ref _canonicalReads) >= 2,
                _ => false
            };
            if (boundary is PublicationBoundary.StagingWritten or PublicationBoundary.CanonicalPublished
                && request.Method == HttpMethod.Delete
                && path.Contains("/staging/derivatives/", StringComparison.Ordinal))
            {
                Volatile.Write(ref _stagingCleanupSuppressed, 1);
                return Task.FromResult(CreateFailure(request));
            }
            if (shouldFail && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                return Task.FromResult(CreateFailure(request));
            }
            return base.SendAsync(request, cancellationToken);
        }

        private static HttpResponseMessage CreateFailure(HttpRequestMessage request)
            => new(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "<Error><Code>ServiceUnavailable</Code><Message>Injected publication fail-stop</Message></Error>",
                    Encoding.UTF8,
                    "application/xml")
            };
    }

    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override long TimestampFrequency => Stopwatch.Frequency;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;

        public override long GetTimestamp() => Stopwatch.GetTimestamp();
    }

    private sealed class CompletionCommitFaultInterceptor : DbTransactionInterceptor
    {
        private int _armed;
        private int _commits;

        public void Arm()
        {
            Interlocked.Exchange(ref _commits, 0);
            Volatile.Write(ref _armed, 1);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 && Interlocked.Increment(ref _commits) == 2)
            {
                throw new IOException("Injected completion pre-commit fail-stop.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PostCommitFaultExecutor(ICentralDerivativeJobExecutor inner)
        : ICentralDerivativeJobExecutor
    {
        public async Task<CentralDerivativeExecutionResult> ExecuteAsync(
            CentralDerivativeJobLease lease,
            CancellationToken cancellationToken)
        {
            _ = await inner.ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
            throw new IOException("Injected completion post-commit fail-stop.");
        }
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * percentile) - 1)];

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static SqlEndpoint DescribeSqlEndpoint(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return new SqlEndpoint(builder.DataSource, builder.InitialCatalog, builder.Encrypt);
    }

    private static void StabilizeGc()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static string GetEvidenceRevision(string repositoryRoot)
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        if (string.IsNullOrWhiteSpace(revision))
        {
            var commit = RunGit(repositoryRoot, "rev-parse", "HEAD");
            revision = string.IsNullOrWhiteSpace(RunGit(repositoryRoot, "status", "--porcelain"))
                ? commit
                : $"{commit[..12]}-dirty";
        }
        if (revision is "." or ".."
            || revision.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidOperationException("HVO_EVIDENCE_REVISION must be a single safe path segment.");
        }
        return revision;
    }

    private static GitEvidence ReadGitEvidence(string repositoryRoot)
        => new(
            RunGit(repositoryRoot, "rev-parse", "HEAD"),
            RunGit(repositoryRoot, "branch", "--show-current"),
            !string.IsNullOrWhiteSpace(RunGit(repositoryRoot, "status", "--porcelain")));

    private static string RunGit(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class TelemetryCollector : IDisposable
    {
        private readonly ConcurrentQueue<TelemetryPoint> _metrics = new();
        private readonly ConcurrentQueue<ActivityPoint> _activities = new();
        private readonly MeterListener _meterListener;
        private readonly ActivityListener _activityListener;

        public TelemetryCollector()
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                _metrics.Enqueue(new TelemetryPoint(instrument.Name, value, ToTags(tags))));
            _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _metrics.Enqueue(new TelemetryPoint(instrument.Name, value, ToTags(tags))));
            _meterListener.Start();
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => _activities.Enqueue(new ActivityPoint(
                    activity.OperationName,
                    activity.Duration.TotalMilliseconds,
                    activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString(), StringComparer.Ordinal)))
            };
            ActivitySource.AddActivityListener(_activityListener);
        }

        public void Clear()
        {
            _metrics.Clear();
            _activities.Clear();
        }

        public TelemetrySnapshot Snapshot()
        {
            _meterListener.RecordObservableInstruments();
            var duration = _metrics.Where(point => point.Name == "skymonitor.central.derivative.duration")
                .GroupBy(point => new
                {
                    Stage = point.Tags.GetValueOrDefault("stage") ?? "unknown",
                    Recipe = point.Tags.GetValueOrDefault("recipe") ?? "unknown",
                    Outcome = point.Tags.GetValueOrDefault("outcome") ?? "unknown"
                })
                .Select(group => CreateStageSummary(
                    group.Key.Stage, group.Key.Recipe, group.Key.Outcome, group.Select(point => point.Value).Order().ToArray()))
                .OrderBy(item => item.Stage, StringComparer.Ordinal)
                .ThenBy(item => item.Recipe, StringComparer.Ordinal)
                .ToArray();
            var activities = _activities
                .GroupBy(point => new
                {
                    point.Name,
                    Stage = point.Tags.GetValueOrDefault("stage") ?? point.Name,
                    Recipe = point.Tags.GetValueOrDefault("recipe") ?? "unknown"
                })
                .Select(group => CreateActivitySummary(
                    group.Key.Name, group.Key.Stage, group.Key.Recipe,
                    group.Select(point => point.DurationMilliseconds).Order().ToArray()))
                .OrderBy(item => item.Stage, StringComparer.Ordinal)
                .ThenBy(item => item.Recipe, StringComparer.Ordinal)
                .ToArray();
            var counters = _metrics.Where(point => point.Name != "skymonitor.central.derivative.duration")
                .GroupBy(point => new { point.Name, Tags = FormatTags(point.Tags) })
                .Select(group => new CounterSummary(group.Key.Name, group.Key.Tags, group.Sum(point => point.Value)))
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Tags, StringComparer.Ordinal)
                .ToArray();
            return new TelemetrySnapshot(duration, activities, counters);
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }

        private static Dictionary<string, string?> ToTags(
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
            => tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value?.ToString(), StringComparer.Ordinal);

        private static StageSummary CreateStageSummary(
            string stage,
            string recipe,
            string outcome,
            double[] sorted)
            => new(
                stage,
                recipe,
                outcome,
                sorted.Length,
                Percentile(sorted, 0.50),
                sorted.Length >= 30 ? Percentile(sorted, 0.95) : null,
                sorted[^1]);

        private static ActivitySummary CreateActivitySummary(
            string name,
            string stage,
            string recipe,
            double[] sorted)
            => new(
                name,
                stage,
                recipe,
                sorted.Length,
                Percentile(sorted, 0.50),
                sorted.Length >= 30 ? Percentile(sorted, 0.95) : null,
                sorted[^1]);

        private static string FormatTags(IReadOnlyDictionary<string, string?> tags)
            => string.Join(",", tags.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private sealed class ResourceSampler : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly TimeSpan _cpuStart;
        private readonly long _allocatedStart;
        private readonly long _managedHeapStart;
        private readonly long _gcCommittedStart;
        private readonly bool _retainRssDiagnostics;
        private readonly RssSampler _rss;
        private bool _stopped;

        public ResourceSampler(bool retainRssDiagnostics = false)
        {
            _process.Refresh();
            _cpuStart = _process.TotalProcessorTime;
            _allocatedStart = GC.GetTotalAllocatedBytes(precise: false);
            _managedHeapStart = GC.GetTotalMemory(forceFullCollection: false);
            _gcCommittedStart = GC.GetGCMemoryInfo().TotalCommittedBytes;
            _retainRssDiagnostics = retainRssDiagnostics;
            _rss = new RssSampler(_process.WorkingSet64);
        }

        public async Task<ResourceEvidence> StopAsync()
        {
            _stopped = true;
            var peak = await _rss.StopAsync().ConfigureAwait(false);
            var timedRssSamples = _rss.Snapshot();
            var rssSamples = timedRssSamples.Select(sample => sample.Bytes).ToArray();
            var third = Math.Max(1, rssSamples.Length / 3);
            var middleStart = Math.Min(third, rssSamples.Length - 1);
            var finalStart = Math.Min(third * 2, rssSamples.Length);
            var middleThird = rssSamples[middleStart..finalStart].Order().ToArray();
            var finalThird = rssSamples[finalStart..].Order().ToArray();
            if (middleThird.Length == 0)
            {
                middleThird = rssSamples.Order().ToArray();
            }
            if (finalThird.Length == 0)
            {
                finalThird = middleThird;
            }
            _process.Refresh();
            var gcInfo = GC.GetGCMemoryInfo();
            return new ResourceEvidence(
                (_process.TotalProcessorTime - _cpuStart).TotalMilliseconds,
                Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _allocatedStart),
                _rss.Initial,
                peak,
                _process.WorkingSet64,
                rssSamples.Length,
                Median(middleThird),
                Median(finalThird),
                _retainRssDiagnostics
                    ? new ResourceDiagnosticEvidence(
                        _managedHeapStart,
                        GC.GetTotalMemory(forceFullCollection: false),
                        _gcCommittedStart,
                        gcInfo.TotalCommittedBytes,
                        timedRssSamples)
                    : null);
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                _rss.StopAsync().GetAwaiter().GetResult();
            }
            _rss.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _process.Dispose();
        }
    }

    private sealed class RssSampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _sampling;
        private readonly ConcurrentQueue<RssSample> _samples = new();
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _peak;
        private bool _stopped;

        public RssSampler(long initial)
        {
            Initial = initial;
            _peak = initial;
            _samples.Enqueue(new RssSample(0, initial));
            _sampling = Task.Run(SampleAsync);
        }

        public long Initial { get; }

        public RssSample[] Snapshot() => _samples.ToArray();

        public async Task<long> StopAsync()
        {
            if (!_stopped)
            {
                _stopped = true;
                await _stopping.CancelAsync().ConfigureAwait(false);
                await _sampling.ConfigureAwait(false);
                Observe();
            }
            return Interlocked.Read(ref _peak);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _stopping.Dispose();
        }

        private async Task SampleAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
                {
                    Observe();
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        private void Observe()
        {
            using var process = Process.GetCurrentProcess();
            var observed = process.WorkingSet64;
            _samples.Enqueue(new RssSample(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, observed));
            long current;
            do
            {
                current = Interlocked.Read(ref _peak);
                if (observed <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _peak, observed, current) != current);
        }
    }

    private static long Median(long[] sorted)
        => sorted[(sorted.Length - 1) / 2];

    private sealed record HarnessScale(
        bool Smoke,
        int StandardWarmups,
        int StandardMeasurements,
        int QueueWarmups,
        int QueueJobs,
        int ExpiredLeases,
        int DuplicateSubmissions,
        int ConcurrentWarmups,
        int ConcurrentMeasurements,
        int RecoveryTrials,
        int FaultJobs,
        TimeSpan FaultArrivalInterval,
        TimeSpan FaultOutageDuration,
        int BacklogInitialJobs,
        int BacklogDisabledArrivals,
        int BacklogEnabledArrivals,
        TimeSpan BacklogArrivalInterval,
        TimeSpan RecoveryTimeout)
    {
        public static HarnessScale Create()
        {
            var value = Environment.GetEnvironmentVariable(SmokeVariable);
            var smoke = value is not null && (value == "1" || bool.TryParse(value, out var enabled) && enabled);
            return smoke
                ? new(
                    true, 1, 2, 10, 100, 10, 10, 2, 8,
                    5, 5, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(500),
                    4, 1, 2, TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(1))
                : new(false, CanonicalStandardWarmups, CanonicalStandardMeasurements,
                    CanonicalQueueWarmups, CanonicalQueueJobs, CanonicalExpiredLeases,
                    CanonicalDuplicateSubmissions,
                    CanonicalConcurrentWarmups, CanonicalConcurrentMeasurements,
                    CanonicalRecoveryTrials, CanonicalFaultJobs,
                    TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10),
                    CanonicalBacklogInitialJobs, CanonicalBacklogDisabledArrivals,
                    CanonicalBacklogEnabledArrivals, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));
        }
    }

    private sealed record GitEvidence(string Commit, string Branch, bool Dirty);

    private sealed record SqlEndpoint(string DataSource, string Database, bool Encrypt);

    private sealed record Workload(
        string Id,
        int Width,
        int Height,
        int StrideBytes,
        CameraPixelFormat PixelFormat,
        byte[] Payload,
        string ChecksumSha256)
    {
        public long ByteLength => Payload.LongLength;
        public string ObjectKey { get; set; } = string.Empty;
    }

    private sealed record BacklogSnapshot(
        int Count,
        int Pending,
        int Leased,
        int Retryable,
        double OldestAgeMilliseconds,
        long LogicalInputBytes);

    private sealed record ResourceEvidence(
        double CpuMilliseconds,
        long AllocatedBytes,
        long RssStartBytes,
        long RssPeakBytes,
        long RssEndBytes,
        int RssSamples,
        long RssMiddleThirdMedianBytes,
        long RssFinalThirdMedianBytes,
        ResourceDiagnosticEvidence? Diagnostic);

    private sealed record ResourceDiagnosticEvidence(
        long ManagedHeapStartBytes,
        long ManagedHeapEndBytes,
        long GcCommittedStartBytes,
        long GcCommittedEndBytes,
        IReadOnlyList<RssSample> RssSamples);

    private sealed record RssSample(double ElapsedMilliseconds, long Bytes);

    private sealed record DerivativeCorrectness(
        bool Passed,
        int CompletedJobs,
        int UniqueOutputs,
        int ProcessingEvidenceRows,
        int CompletedAttempts,
        long OutputBytesChecksumValidated,
        string Scope);

    private sealed record QueueCorrectness(
        bool Passed,
        int SkippedJobs,
        int AttemptRows,
        int LeaseExpiredAttempts,
        int SkippedAttempts,
        string Scope);

    private sealed record DerivativeMeasurement(
        string Scenario,
        string Workload,
        string Recipe,
        int Warmups,
        int Measurements,
        int Concurrency,
        double WallMilliseconds,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MaximumMilliseconds,
        double OperationsPerSecond,
        BacklogSnapshot InitialBacklog,
        BacklogSnapshot FinalBacklog,
        ResourceEvidence Resources,
        ProtocolSnapshot Protocol,
        TelemetrySnapshot Telemetry,
        DerivativeCorrectness Correctness);

    private sealed record QueueMeasurement(
        string Scenario,
        int Jobs,
        int SeededExpiredLeases,
        int ReclaimedLeases,
        DuplicateSubmissionEvidence DuplicateSubmissions,
        int Concurrency,
        double WallMilliseconds,
        double ClaimMedianMilliseconds,
        double ClaimP95Milliseconds,
        double ClaimMaximumMilliseconds,
        double? ReclaimMedianMilliseconds,
        double? ReclaimP95Milliseconds,
        double? ReclaimMaximumMilliseconds,
        double EndToEndMedianMilliseconds,
        double EndToEndP95Milliseconds,
        double EndToEndMaximumMilliseconds,
        double OperationsPerSecond,
        BacklogSnapshot InitialBacklog,
        BacklogSnapshot FinalBacklog,
        ResourceEvidence Resources,
        ProtocolSnapshot Protocol,
        TelemetrySnapshot Telemetry,
        QueueCorrectness Correctness);

    private sealed record DuplicateSubmissionEvidence(
        int Submissions,
        double WallMilliseconds,
        double SubmissionsPerSecond,
        int DurableJobs,
        bool Idempotent,
        ResourceEvidence? Resources,
        ProtocolSnapshot? Protocol);

    private sealed record ProtocolSnapshot(
        long SqlCommands,
        ObjectProtocolSnapshot Minio);

    private sealed record ObjectProtocolSnapshot(
        long Requests,
        long Head,
        long Get,
        long Put,
        long Copy,
        long Delete,
        long RequestContentLengthBytes,
        long ResponseContentLengthBytes,
        long RequestsWithoutContentLength,
        long ResponsesWithoutContentLength);

    private sealed record TrialDistribution(
        int Trials,
        double Median,
        double Minimum,
        double Maximum);

    private sealed record RecoveryTrialMeasurement(
        int Trial,
        int Jobs,
        double ArrivalMilliseconds,
        double ObservedArrivalRate,
        double OutageMilliseconds,
        long InjectedFailures,
        PublicationBoundaryEvidence PublicationBoundary,
        double RecoveryMilliseconds,
        double JobsPerSecond,
        double LogicalInputBytesPerSecond,
        BacklogSnapshot InitialBacklog,
        BacklogSnapshot RestartBacklog,
        BacklogSnapshot FinalBacklog,
        ResourceEvidence Resources,
        TelemetrySnapshot Telemetry,
        DerivativeCorrectness Correctness);

    private sealed record FaultRecoveryMeasurement(
        int Trials,
        int JobsPerTrial,
        double ConfiguredArrivalRatePerSecond,
        double ConfiguredOutageMilliseconds,
        TrialDistribution RecoveryMilliseconds,
        TrialDistribution JobsPerSecond,
        IReadOnlyList<RecoveryTrialMeasurement> TrialEvidence);

    private sealed record PublicationBoundaryEvidence(
        string Boundary,
        Guid JobId,
        string ObservedException,
        string JobStatusBeforeRestart,
        string? ArtifactObjectStateBeforeRestart,
        bool ProcessingEvidenceExists,
        bool CompletedBeforeRestart,
        bool StagingCleanupSuppressed,
        string? StagingObjectKey,
        bool StagingObjectExistedBeforeRestart,
        bool StagingObjectReconciled,
        int? StagingReconciliationCycles);

    private sealed record BacklogRecoveryTrialMeasurement(
        int Trial,
        int Concurrency,
        int Jobs,
        int InitialJobs,
        int DisabledArrivals,
        int EnabledArrivals,
        double InitialBacklogDrainMilliseconds,
        double RecoveryMilliseconds,
        double ArrivalAdjustedDrainRate,
        BacklogSnapshot DisabledBacklog,
        BacklogSnapshot FinalBacklog,
        ResourceEvidence Resources,
        RssPlateauEvidence RssPlateau,
        TelemetrySnapshot Telemetry,
        DerivativeCorrectness Correctness);

    private sealed record RssPlateauEvidence(
        int Samples,
        long MiddleThirdMedianBytes,
        long FinalThirdMedianBytes,
        long AllowedGrowthBytes,
        long ObservedGrowthBytes,
        bool Passed);

    private sealed record BacklogRecoveryMeasurement(
        int Concurrency,
        int Trials,
        int InitialJobs,
        int DisabledArrivals,
        int EnabledArrivals,
        double ConfiguredArrivalRatePerSecond,
        TrialDistribution InitialBacklogDrainMilliseconds,
        TrialDistribution RecoveryMilliseconds,
        TrialDistribution ArrivalAdjustedDrainRate,
        IReadOnlyList<BacklogRecoveryTrialMeasurement> TrialEvidence);

    private sealed record TelemetryPoint(
        string Name,
        double Value,
        IReadOnlyDictionary<string, string?> Tags);

    private sealed record ActivityPoint(
        string Name,
        double DurationMilliseconds,
        IReadOnlyDictionary<string, string?> Tags);

    private sealed record StageSummary(
        string Stage,
        string Recipe,
        string Outcome,
        int Samples,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MaximumMilliseconds);

    private sealed record ActivitySummary(
        string Name,
        string Stage,
        string Recipe,
        int Samples,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MaximumMilliseconds);

    private sealed record CounterSummary(string Name, string Tags, double Value);

    private sealed record TelemetrySnapshot(
        IReadOnlyList<StageSummary> DurationMetrics,
        IReadOnlyList<ActivitySummary> Activities,
        IReadOnlyList<CounterSummary> Counters);
}
