using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Program = HVO.SkyMonitor.LogicHost.Program;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[DoNotParallelize]
[TestCategory("Manual")]
public sealed class HybridTransientSubmissionIssue251PerformanceTests
{
    private const string BaselineProductionRevision = "86dde5e5852c4846a0c5a88a741ebee50bac4904";
    private static readonly string[] BaselineChangedPaths =
    [
        "docs/runbooks/ci-pipeline.md",
        "scripts/test-categories/Program.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/HybridTransientSubmissionIntegrationTests.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/HybridTransientSubmissionIssue251PerformanceTests.cs",
        "tests/HVO.SkyMonitor.IntegrationTests/HybridTransientSubmissionIssue251Tests.cs"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [TestMethod]
    [Timeout(14_400_000)]
    public async Task W1W2ConcurrencyAndHeadDelay_RecordsComparativeEvidence()
    {
        Assert.AreEqual("Release", typeof(HybridTransientSubmissionIssue251PerformanceTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration);
        var mode = Environment.GetEnvironmentVariable("HVO_ISSUE251_EVIDENCE_MODE") ?? "development";
        Assert.IsTrue(mode is "development" or "canonical");
        var canonical = mode == "canonical";
        var warmups = canonical ? 5 : 1;
        var measured = canonical ? 30 : 2;
        var developmentWorkload = Environment.GetEnvironmentVariable("HVO_ISSUE251_DEVELOPMENT_WORKLOAD") ?? "W1";
        Assert.IsTrue(developmentWorkload is "W1" or "W2");
        var workloads = canonical
            ? new[]
            {
                new Workload("W1", 1936, 1216, CameraPixelFormat.Mono16),
                new Workload("W2", 3096, 2080, CameraPixelFormat.BayerRggb16)
            }
            : developmentWorkload == "W1"
                ? new[] { new Workload("W1", 1936, 1216, CameraPixelFormat.Mono16) }
                : new[] { new Workload("W2", 3096, 2080, CameraPixelFormat.BayerRggb16) };
        var concurrencies = canonical ? new[] { 1, 4, 8 } : new[] { 1 };
        var delays = canonical
            ? new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2) }
            : new[] { TimeSpan.Zero };
        var repositoryRoot = FindRepositoryRoot();
        var source = await EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(HybridTransientSubmissionIssue251PerformanceTests),
            typeof(CentralTransientSubmissionService),
            typeof(CentralObjectApplicationLockSet)).ConfigureAwait(false);
        var phase = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PHASE") ?? "development";
        var productionRevision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PRODUCTION_REVISION")
            ?? source.Head;
        var trial = ParseTrial(Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL"));
        await ValidateEvidenceSourceAsync(
            repositoryRoot, source, phase, productionRevision, canonical).ConfigureAwait(false);
        if (canonical)
        {
            Assert.IsTrue(phase is "baseline" or "after");
            Assert.IsTrue(trial is >= 1 and <= 5);
            Assert.IsTrue(GCSettings.IsServerGC);
        }

        var results = new List<CellEvidence>();
        foreach (var workload in workloads)
        {
            var lanes = new List<HybridTransientSubmissionIntegrationTests.SubmissionScenario>();
            for (var index = 0; index < concurrencies.Max(); index++)
            {
                lanes.Add(await HybridTransientSubmissionIntegrationTests.CreateScenarioAsync(
                    workload.Width, workload.Height, workload.PixelFormat).ConfigureAwait(false));
            }
            foreach (var delay in delays)
            {
                foreach (var concurrency in concurrencies)
                {
                    results.Add(await RunCellAsync(
                        workload, lanes, delay, concurrency, warmups, measured).ConfigureAwait(false));
                }
            }
        }

        var evidence = new
        {
            Schema = "hvo-issue-251-hybrid-generation-performance-v1",
            Issue = 251,
            Mode = mode,
            Phase = phase,
            Trial = trial,
            ProductionRevision = productionRevision,
            Source = source,
            Environment = new
            {
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                SDK = ReadSdkVersion(repositoryRoot),
                Runtime = Environment.Version.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                ServerGc = GCSettings.IsServerGC,
                AssemblyConfiguration = "Release"
            },
            Method = new
            {
                WarmupsPerCell = warmups,
                MeasuredSubmissionsPerCell = measured,
                Concurrencies = concurrencies,
                GenerationHeadDelayMilliseconds = delays.Select(value => value.TotalMilliseconds).ToArray(),
                PayloadReuse = $"{concurrencies.Max()} isolated five-object windows per workload; every submission has unique candidate, event, submission and idempotency identities.",
                MeasurementBoundary = "Per-request latency covers the authenticated HTTP POST including five full object verifications, five fenced generation checks and SQL finalization. Aggregate elapsed, CPU and allocation measurements additionally include bounded client batching, envelope reidentification and response handling.",
                SqlCounterAttribution = "Factory-wide conservative observation; background hosted-service commands and transactions may be included. Exact measured submission/job/input counts are asserted separately.",
                ObjectProtocol = "Per source: explicit verification stat, MinIO GetObject metadata lookup and fenced generation stat are three logical metadata operations; fixture HTTP diagnostics observe two wire HEAD attempts per logical metadata operation, plus one conditional GET. Byte accounting includes GET response bodies only because HEAD Content-Length describes the object rather than transferred content."
            },
            Results = results,
            Limitations = new[]
            {
                "Process CPU, allocations and RSS cover the test process and in-process TestServer; SQL Server and MinIO container resources are excluded.",
                "Development mode is a harness smoke and is never claimable comparative evidence.",
                "Out-of-band MinIO administration bypassing the canonical SQL object fence is outside the application-writer guarantee."
            },
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        var output = Path.Combine(
            repositoryRoot,
            "TestResults",
            "issue-251",
            source.OutputDirectoryName,
            trial is null ? source.RunId : $"trial-{trial}");
        Directory.CreateDirectory(output);
        var evidencePath = Path.Combine(output, "hybrid-generation-performance.json");
        await EvidenceSourceIdentity.WriteJsonAsync(evidencePath, evidence, JsonOptions).ConfigureAwait(false);
        var evidenceBytes = await File.ReadAllBytesAsync(evidencePath).ConfigureAwait(false);
        await EvidenceSourceIdentity.WriteJsonAsync(
            Path.Combine(output, "manifest.json"),
            new
            {
                Schema = "hvo-issue-251-evidence-manifest-v1",
                File = Path.GetFileName(evidencePath),
                Length = evidenceBytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(evidenceBytes))
            },
            JsonOptions).ConfigureAwait(false);
    }

    private static async Task<CellEvidence> RunCellAsync(
        Workload workload,
        IReadOnlyList<HybridTransientSubmissionIntegrationTests.SubmissionScenario> lanes,
        TimeSpan delay,
        int concurrency,
        int warmups,
        int measured)
    {
        var commands = new CountingDbCommandInterceptor();
        var transactions = new CountingDbTransactionInterceptor();
        var generation = new GenerationDelayProbe(delay);
        using var protocol = new Issue251ProtocolCounter(AssemblyHooks.Fixture.MinioEndpoint);
        var applicationName = $"HVO.SkyMonitor.Issue251.{workload.Id}.C{concurrency}.D{delay.TotalMilliseconds:0}.{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;
        using var factory = HybridTransientSubmissionIntegrationTests.CreateHybridFactory(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString)
                .AddInterceptors(commands, transactions));
            services.Replace(ServiceDescriptor.Scoped<ICentralArtifactObjectReader>(provider =>
                new DelayedGenerationReader(
                    ActivatorUtilities.CreateInstance<CentralArtifactObjectReader>(provider), generation)));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await HybridTransientSubmissionIntegrationTests.GetSystemTokenAsync(client).ConfigureAwait(false));
        await RunOperationsAsync(client, lanes, concurrency, warmups, latencies: null).ConfigureAwait(false);
        await QuiesceJobsAsync(factory, lanes.Take(concurrency).SelectMany(lane => lane.CentralArtifactIds).ToArray())
            .ConfigureAwait(false);

        commands.Reset();
        transactions.Reset();
        generation.Reset();
        StabilizeGc();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var rssStart = process.WorkingSet64;
        var allocationsStart = GC.GetTotalAllocatedBytes(precise: true);
        protocol.Start();
        var latencies = new List<double>(measured);
        var elapsedStarted = Stopwatch.GetTimestamp();
        var identities = await RunOperationsAsync(client, lanes, concurrency, measured, latencies)
            .ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(elapsedStarted);
        process.Refresh();
        var cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        var rssEnd = process.WorkingSet64;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationsStart;
        var objectProtocol = protocol.Stop();
        var transactionSnapshot = transactions.Snapshot();
        Assert.AreEqual(measured * 5, generation.Calls);
        Assert.AreEqual(measured * 5, objectProtocol.GetRequests);
        Assert.AreEqual(measured * 30, objectProtocol.HeadRequests);
        Assert.AreEqual(0, objectProtocol.UnknownResponseContentLengths);
        Assert.AreEqual(checked(measured * 5L * workload.Width * workload.Height * 2L),
            objectProtocol.GetResponseContentBytes);
        Assert.IsGreaterThanOrEqualTo(measured, transactionSnapshot.Committed);

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobIds = await db.CentralTransientValidationJobs.AsNoTracking()
            .Where(validation => identities.Contains(validation.SubmissionIdentitySha256))
            .Select(validation => validation.CentralDerivativeJobId)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(measured, jobIds.Length);
        Assert.AreEqual(measured * 5, await db.CentralDerivativeJobInputs.AsNoTracking()
            .CountAsync(input => jobIds.Contains(input.CentralDerivativeJobId)).ConfigureAwait(false));
        Assert.IsFalse(await db.CentralTransientSubmissionAudits.AsNoTracking().AnyAsync(audit =>
            identities.Contains(audit.ClaimedSubmissionIdentitySha256!)).ConfigureAwait(false));
        var retainedSources = lanes.Take(concurrency).SelectMany(lane => lane.CentralArtifactIds).Distinct().ToArray();
        var references = assertionScope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>();
        foreach (var sourceId in retainedSources)
        {
            Assert.IsTrue(await references.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false));
        }
        await QuiesceJobsAsync(factory, retainedSources).ConfigureAwait(false);

        var ordered = latencies.Order().ToArray();
        var orderedTransactions = transactionSnapshot.CommittedDurationMilliseconds.Order().ToArray();
        return new(
            workload.Id,
            workload.Width,
            workload.Height,
            workload.PixelFormat.ToString(),
            checked(workload.Width * workload.Height * 2L),
            concurrency,
            delay.TotalMilliseconds,
            warmups,
            measured,
            elapsed.TotalMilliseconds,
            measured / elapsed.TotalSeconds,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered[^1],
            cpuMilliseconds,
            allocatedBytes,
            rssStart,
            rssEnd,
            commands.Count,
            transactionSnapshot.StartAttempts,
            transactionSnapshot.Committed,
            transactionSnapshot.RolledBack,
            transactionSnapshot.Failed,
            Percentile(orderedTransactions, 0.50),
            Percentile(orderedTransactions, 0.95),
            orderedTransactions[^1],
            generation.Calls,
            generation.TotalConfiguredDelayMilliseconds,
            objectProtocol.HeadRequests,
            objectProtocol.GetRequests,
            objectProtocol.GetResponseContentBytes,
            objectProtocol.UnknownResponseContentLengths,
            measured,
            measured * 5,
            retainedSources.Length);
    }

    private static async Task<string[]> RunOperationsAsync(
        HttpClient client,
        IReadOnlyList<HybridTransientSubmissionIntegrationTests.SubmissionScenario> lanes,
        int concurrency,
        int count,
        List<double>? latencies)
    {
        var identities = new List<string>(count);
        for (var offset = 0; offset < count; offset += concurrency)
        {
            var batchSize = Math.Min(concurrency, count - offset);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var requests = new Task<(HttpResponseMessage Response, double Milliseconds, string Identity)>[batchSize];
            for (var index = 0; index < batchSize; index++)
            {
                var lane = lanes[index];
                var envelope = HybridTransientSubmissionIntegrationTests.CreateEnvelope(
                    HybridTransientSubmissionIntegrationTests.Reidentify(lane.Envelope.Candidate));
                requests[index] = SendMeasuredAfterStartAsync(
                    start.Task, client, lane.DeviceId, envelope, cancellation.Token);
            }
            start.SetResult();
            var responses = await Task.WhenAll(requests).ConfigureAwait(false);
            foreach (var result in responses)
            {
                using (result.Response)
                {
                    if (result.Response.StatusCode != HttpStatusCode.Accepted)
                    {
                        Assert.Fail($"Hybrid evidence submission failed: {(int)result.Response.StatusCode} {await result.Response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
                    }
                }
                latencies?.Add(result.Milliseconds);
                identities.Add(result.Identity);
            }
        }
        return identities.ToArray();
    }

    private static async Task<(HttpResponseMessage Response, double Milliseconds, string Identity)>
        SendMeasuredAfterStartAsync(
            Task start,
            HttpClient client,
            string deviceId,
            HVO.SkyMonitor.Processing.TransientCandidateSubmissionEnvelopeV1 envelope,
            CancellationToken cancellationToken)
    {
        await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await SendMeasuredAsync(client, deviceId, envelope, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(HttpResponseMessage Response, double Milliseconds, string Identity)> SendMeasuredAsync(
        HttpClient client,
        string deviceId,
        HVO.SkyMonitor.Processing.TransientCandidateSubmissionEnvelopeV1 envelope,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await HybridTransientSubmissionIntegrationTests.SendAsync(
            client,
            deviceId,
            HybridTransientSubmissionIntegrationTests.DeviceKey,
            envelope,
            cancellationToken).ConfigureAwait(false);
        return (response, Stopwatch.GetElapsedTime(started).TotalMilliseconds, envelope.SubmissionIdentitySha256);
    }

    private static int? ParseTrial(string? value)
        => int.TryParse(value, out var trial) ? trial : null;

    private static async Task QuiesceJobsAsync(
        WebApplicationFactory<Program> factory,
        IReadOnlyCollection<Guid> sourceArtifactIds)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobIds = db.CentralDerivativeJobInputs
            .Where(input => sourceArtifactIds.Contains(input.CentralArtifactId))
            .Select(input => input.CentralDerivativeJobId);
        await db.CentralDerivativeJobs
            .Where(job => jobIds.Contains(job.Id)
                && (job.Status == CentralDerivativeJobStatus.Waiting
                    || job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.Leased
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure
                    || job.Status == CentralDerivativeJobStatus.CancelRequested))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.StateReasonCode, "issue-251.evidence-cell-complete"))
            .ConfigureAwait(false);
    }

    private static async Task ValidateEvidenceSourceAsync(
        string repositoryRoot,
        EvidenceSourceSnapshot source,
        string phase,
        string productionRevision,
        bool canonical)
    {
        if (!canonical)
        {
            Assert.AreEqual("development", phase);
            return;
        }
        Assert.IsFalse(source.Dirty, "claimable evidence requires a clean worktree");
        Assert.IsNotNull(source.RequestedRevision, "claimable evidence requires HVO_EVIDENCE_REVISION");
        Assert.AreEqual(source.Head, source.RequestedRevision, ignoreCase: true);
        Assert.AreEqual("clean-source-attributed-review-required", source.Claimability);
        var resolvedProduction = (await RunGitAsync(
            repositoryRoot, "rev-parse", $"{productionRevision}^{{commit}}").ConfigureAwait(false)).Trim();
        Assert.AreEqual(productionRevision, resolvedProduction, ignoreCase: true);
        if (phase == "baseline")
        {
            Assert.AreEqual(BaselineProductionRevision, productionRevision, ignoreCase: true);
            Assert.AreEqual(0, await RunGitExitCodeAsync(
                repositoryRoot, "merge-base", "--is-ancestor", productionRevision, source.Head).ConfigureAwait(false));
            var changedPaths = (await RunGitAsync(
                    repositoryRoot, "diff", "--name-only", $"{productionRevision}..{source.Head}", "--")
                .ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            CollectionAssert.AreEqual(BaselineChangedPaths, changedPaths);
        }
        else
        {
            Assert.AreEqual(source.Head, productionRevision, ignoreCase: true);
        }
    }

    private static async Task<string> RunGitAsync(string repositoryRoot, params string[] arguments)
    {
        using var process = StartGit(repositoryRoot, arguments);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, await errorTask.ConfigureAwait(false));
        return output;
    }

    private static async Task<int> RunGitExitCodeAsync(string repositoryRoot, params string[] arguments)
    {
        using var process = StartGit(repositoryRoot, arguments);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static Process StartGit(string repositoryRoot, IReadOnlyList<string> arguments)
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
        var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start());
        return process;
    }

    private static string ReadSdkVersion(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json has no SDK version.");
    }

    private static double Percentile(double[] ordered, double percentile)
        => ordered[Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1)];

    private static void StabilizeGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class GenerationDelayProbe(TimeSpan delay)
    {
        private long calls;
        private long totalDelayTicks;

        internal long Calls => Interlocked.Read(ref calls);
        internal double TotalConfiguredDelayMilliseconds => TimeSpan.FromTicks(
            Interlocked.Read(ref totalDelayTicks)).TotalMilliseconds;

        internal async Task DelayAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Interlocked.Add(ref totalDelayTicks, delay.Ticks);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref calls, 0);
            Interlocked.Exchange(ref totalDelayTicks, 0);
        }
    }

    private sealed class DelayedGenerationReader(
        ICentralArtifactObjectReader inner,
        GenerationDelayProbe probe) : ICentralArtifactObjectReader
    {
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
            => inner.VerifyAsync(artifact, cancellationToken);

        public async Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
        {
            await probe.DelayAsync(cancellationToken).ConfigureAwait(false);
            return await inner.IsCurrentGenerationAsync(artifact, storageETag, cancellationToken).ConfigureAwait(false);
        }

        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => inner.CopyToAsync(snapshot, destination, range, cancellationToken);
    }

    private sealed class Issue251ProtocolCounter :
        IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private const string RequestStart = "System.Net.Http.HttpRequestOut.Start";
        private const string RequestStop = "System.Net.Http.HttpRequestOut.Stop";
        private readonly ConcurrentBag<IDisposable> subscriptions = [];
        private readonly IDisposable allListeners;
        private readonly string minioAuthority;
        private long getRequests;
        private long headRequests;
        private long getResponseContentBytes;
        private long unknownResponseContentLengths;
        private int active;

        internal Issue251ProtocolCounter(string minioAuthority)
        {
            this.minioAuthority = minioAuthority;
            allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        internal void Start()
        {
            Interlocked.Exchange(ref getRequests, 0);
            Interlocked.Exchange(ref headRequests, 0);
            Interlocked.Exchange(ref getResponseContentBytes, 0);
            Interlocked.Exchange(ref unknownResponseContentLengths, 0);
            Volatile.Write(ref active, 1);
        }

        internal ObjectProtocolEvidence Stop()
        {
            Volatile.Write(ref active, 0);
            return new(
                Interlocked.Read(ref headRequests),
                Interlocked.Read(ref getRequests),
                Interlocked.Read(ref getResponseContentBytes),
                Interlocked.Read(ref unknownResponseContentLengths));
        }

        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, "HttpHandlerDiagnosticListener", StringComparison.Ordinal))
            {
                subscriptions.Add(value.Subscribe(this, static eventName => eventName is RequestStart or RequestStop));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Volatile.Read(ref active) != 1)
            {
                return;
            }
            var payloadType = value.Value?.GetType();
            var request = payloadType?.GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage;
            if (request is null || !string.Equals(
                    request.RequestUri?.Authority, minioAuthority, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (value.Key == RequestStart)
            {
                if (request.Method == HttpMethod.Get) Interlocked.Increment(ref getRequests);
                else if (request.Method == HttpMethod.Head) Interlocked.Increment(ref headRequests);
                return;
            }
            var response = payloadType?.GetProperty("Response")?.GetValue(value.Value) as HttpResponseMessage;
            if (request.Method != HttpMethod.Get)
            {
                return;
            }
            if (response?.Content.Headers.ContentLength is long responseBytes)
            {
                Interlocked.Add(ref getResponseContentBytes, responseBytes);
            }
            else
            {
                Interlocked.Increment(ref unknownResponseContentLengths);
            }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void Dispose()
        {
            allListeners.Dispose();
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    private sealed record Workload(string Id, int Width, int Height, CameraPixelFormat PixelFormat);

    private sealed record CellEvidence(
        string Workload,
        int Width,
        int Height,
        string PixelFormat,
        long PayloadBytes,
        int Concurrency,
        double GenerationHeadDelayMilliseconds,
        int Warmups,
        int MeasuredSubmissions,
        double ElapsedMilliseconds,
        double SubmissionsPerSecond,
        double MedianMilliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaximumMilliseconds,
        double CpuMilliseconds,
        long AllocatedBytes,
        long RssStartBytes,
        long RssEndBytes,
        long SqlCommands,
        long TransactionStartAttempts,
        long CommittedTransactions,
        long RolledBackTransactions,
        long FailedTransactions,
        double TransactionMedianMilliseconds,
        double TransactionP95Milliseconds,
        double TransactionMaximumMilliseconds,
        long GenerationChecks,
        double TotalConfiguredGenerationDelayMilliseconds,
        long MinioHeadRequests,
        long MinioGetRequests,
        long MinioGetResponseContentBytes,
        long MinioUnknownResponseContentLengths,
        int ValidationJobs,
        int JobInputs,
        int DistinctRetainedSources);

    private sealed record ObjectProtocolEvidence(
        long HeadRequests,
        long GetRequests,
        long GetResponseContentBytes,
        long UnknownResponseContentLengths);
}
