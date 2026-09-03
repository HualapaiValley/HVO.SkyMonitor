using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class CentralArtifactRetentionPerformanceTests
{
    private const string Issue254BaselineRevision = "300d1b5940fd2241cc32a3f2e35cd27e89ff03ca";
    private const string Issue254OptInVariable = "HVO_ISSUE_254_SQL_SESSION_EVIDENCE";
    private const string Issue254SmokeVariable = "HVO_ISSUE_254_SMOKE";
    private const int Issue254PayloadBytes = 1936 * 1216 * 2;
    private const int Issue254Warmups = 20;
    private const int Issue254Measurements = 200;
    private static readonly string[] Issue254DiagnosticSqlMetrics =
    [
        "PeakAttributedSqlSessions", "PeakSleepingSessions", "PeakActiveSessions",
        "PeakActiveRequests", "PeakOpenTransactionSessions", "PeakSessionApplicationLocks",
        "PeakActiveTransactionLogBytes", "ObservedBatchApplicationLockOccupancyMilliseconds",
        "EffectiveSamplingIntervalMilliseconds"
    ];

    [TestMethod]
    public async Task RuntimeSqlPolicy_W4AndDelayedObjectIo_RecordsEvidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var phase = ReadEvidencePhase();
        var productionRevision = await ResolveIssue254ProductionRevisionAsync(repositoryRoot).ConfigureAwait(false);
        var source = await EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(CentralArtifactRetentionPerformanceTests),
            typeof(LogicHostSqlConnectionProfiles),
            typeof(CentralArtifactRetentionService)).ConfigureAwait(false);
        await ValidateIssue254EvidenceIdentityAsync(
            repositoryRoot, phase, productionRevision, source).ConfigureAwait(false);

        var fixture = AssemblyHooks.Fixture;
        var profile = LogicHostSqlConnectionProfiles.Resolve(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:skymonitordb"] = fixture.SqlServerConnectionString
            }).Build(),
            new Issue254HostEnvironment(),
            LogicHostSqlConnectionPurpose.Runtime);
        var connectionPolicy = ReadEffectiveConnectionPolicy(profile.ConnectionString);
        connectionPolicy.ApplicationName.Should().Be(LogicHostSqlConnectionProfiles.RuntimeApplicationName);
        var payload = CreateIssue254Payload();
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(payload));
        var runToken = Guid.NewGuid().ToString("N");
        var smoke = string.Equals(Environment.GetEnvironmentVariable(Issue254SmokeVariable), "1", StringComparison.Ordinal);
        var warmups = smoke ? 1 : Issue254Warmups;
        var measurements = smoke ? 4 : Issue254Measurements;

        var performance = new List<Issue254PerformanceEvidence>();
        foreach (var concurrency in ConcurrencyLevels)
        {
            ClearIssue254RuntimePool(profile.ConnectionString);
            using var collector = new Issue246RetentionEvidenceCollector();
            using var factory = CreateFactory(fixture, profile.ConnectionString, collector);
            _ = factory.Services;
            var artifacts = await SeedArtifactsAsync(
                $"issue-254/{runToken}/w4-c{concurrency}",
                warmups + measurements,
                payload,
                payloadSha256).ConfigureAwait(false);
            collector.Http.Configure(TimeSpan.Zero);
            _ = await ExecuteReleasesAsync(
                factory, artifacts.Take(warmups).ToArray(), concurrency).ConfigureAwait(false);
            collector.Reset();
            var measured = await MeasureReleasesAsync(
                factory,
                artifacts.Skip(warmups).ToArray(),
                concurrency,
                fixture.SqlServerConnectionString,
                profile.ApplicationName,
                maximumMedianSamplingIntervalMilliseconds: 50).ConfigureAwait(false);
            var protocol = collector.Snapshot();
            await VerifyReleasedAsync(artifacts).ConfigureAwait(false);
            measured.Samples.Should().HaveCount(measurements).And.OnlyContain(
                sample => sample.Outcome == CentralArtifactRetentionResult.Released.ToString());
            protocol.ObjectStore.Deletes.Should().Be(measurements);
            measured.Sql.PeakAttributedSqlSessions.Should().BeGreaterThan(0);
            performance.Add(CreateIssue254PerformanceEvidence(concurrency, measured, protocol));
        }

        var delayed = new List<Issue254DelayEvidence>();
        foreach (var concurrency in ConcurrencyLevels)
        {
            foreach (var delayMilliseconds in DeleteDelaysMilliseconds)
            {
                ClearIssue254RuntimePool(profile.ConnectionString);
                using var collector = new Issue246RetentionEvidenceCollector();
                using var factory = CreateFactory(fixture, profile.ConnectionString, collector);
                _ = factory.Services;
                var artifacts = await SeedArtifactsAsync(
                    $"issue-254/{runToken}/delay-{delayMilliseconds}-c{concurrency}",
                    concurrency,
                    payload,
                    payloadSha256).ConfigureAwait(false);
                collector.Reset();
                collector.Http.Configure(TimeSpan.FromMilliseconds(delayMilliseconds));
                var measured = await MeasureReleasesAsync(
                    factory,
                    artifacts,
                    concurrency,
                    fixture.SqlServerConnectionString,
                    profile.ApplicationName,
                    maximumMedianSamplingIntervalMilliseconds: 50).ConfigureAwait(false);
                var protocol = collector.Snapshot();
                await VerifyReleasedAsync(artifacts).ConfigureAwait(false);
                measured.Samples.Should().HaveCount(concurrency).And.OnlyContain(
                    sample => sample.Outcome == CentralArtifactRetentionResult.Released.ToString());
                protocol.ObjectStore.Deletes.Should().Be(concurrency);
                if (delayMilliseconds > 0)
                {
                    measured.Sql.PeakSessionApplicationLocks.Should().BeGreaterThanOrEqualTo(concurrency);
                    var stableDelaySamples = measured.Sql.Samples
                        .Where(sample => sample.SessionApplicationLocks >= concurrency).ToArray();
                    stableDelaySamples.Should().NotBeEmpty().And.OnlyContain(
                        sample => sample.ApplicationLockSessionsWithOpenTransactions == 0);
                }
                delayed.Add(CreateIssue254DelayEvidence(concurrency, delayMilliseconds, measured, protocol));
            }
        }

        var environment = await CaptureEnvironmentAsync(repositoryRoot).ConfigureAwait(false);
        var harnessSha256 = ComputeIssue254HarnessSha256(repositoryRoot);
        var evidence = new
        {
            Schema = "hvo-issue-254-sql-session-policy-evidence-v1",
            Issue = 254,
            Phase = phase,
            Source = source,
            ProductionRevision = productionRevision,
            HarnessSha256 = harnessSha256,
            Environment = new
            {
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                environment.CpuModel,
                environment.TotalMemoryBytes,
                environment.PinnedSdkVersion,
                environment.ExecutingSdkVersion,
                environment.DockerServerVersion,
                environment.EnvironmentFingerprintSha256,
                Configuration = "Release",
                ServerGc = System.Runtime.GCSettings.IsServerGC,
                Topology = "In-process LogicHost with shared SQL Server and MinIO Testcontainers; fixture startup excluded"
            },
            Workload = new
            {
                Id = "W4/W1-retention-release",
                Dimensions = "1936x1216",
                PixelFormat = "Mono16 deterministic byte-equivalent payload",
                PayloadBytes = Issue254PayloadBytes,
                PayloadSha256 = payloadSha256,
                Smoke = smoke,
                WarmupOperations = warmups,
                MeasuredOperationsPerLevel = measurements,
                CanonicalWarmupOperations = Issue254Warmups,
                CanonicalMeasuredOperationsPerLevel = Issue254Measurements,
                ConcurrencyLevels,
                DeleteDelaysMilliseconds,
                DelayOperationsPerCell = "equal to concurrency",
                InitialBacklog = 0,
                ArrivalRate = 0,
                DurableBacklog = "N/A: the measured production service is a bounded synchronous release operation with no durable work queue.",
                SamplingTargetMilliseconds = 10,
                MaximumEffectiveMedianSamplingIntervalMilliseconds = 50,
                Boundary = "ICentralArtifactRetentionService.ReleaseAsync including SQL reference checks/state mutation, session-owned object lock, and MinIO DELETE"
            },
            EffectiveConnectionPolicy = connectionPolicy,
            Performance = performance,
            DelayedObjectIo = delayed,
            Correctness = new
            {
                ExactPreDeleteLengthAndSha256 = true,
                FinalSqlState = "Expired/retention.expired",
                FinalObjectState = "absent",
                DistinctObjectPerOperation = true,
                CallerVisibleFailures = 0,
                InternalSqlRetries = "N/A: the collector records commands and transaction outcomes but does not classify internally recovered SQL exceptions."
            },
            Method = new
            {
                PoolOccupancy = "Application-name-attributed active and sleeping SQL sessions are the declared proxy; exact internal SqlClient checked-out pool occupancy is unavailable.",
                Attribution = "Runtime EF and dedicated object-lock connections use the production-normalized application name. The administrative sampler uses the fixture connection and is excluded.",
                Sql = "10 ms target aggregate DMV samples retain session, request, transaction, and session-owned application-lock counts only. Lock/transaction overlap is authoritative only in the delayed stable window where all expected locks are held; transition samples can race pooled-session reuse and remain diagnostic. SQL text, parameters, session IDs, lock resource names, login names, and connection strings are excluded.",
                Resources = "Process CPU, managed allocations, and working-set start/end/peak cover the in-process LogicHost/testhost.",
                Percentiles = "Nearest-rank p50/p95/p99/maximum over 200 measured operations after 20 warmups per concurrency.",
                RegressionRule = "Correctness loss always fails. A timing/resource change is material above max(20%, 2 * baseline five-trial (max-min)/median range)."
            },
            Command = $"{Issue254OptInVariable}=1 DOTNET_gcServer=1 HVO_EVIDENCE_PHASE=baseline|after HVO_EVIDENCE_REVISION=<HARNESS_OR_CANDIDATE_HEAD> HVO_EVIDENCE_PRODUCTION_REVISION=<PRODUCTION_HEAD> HVO_EVIDENCE_TRIAL=1..5 dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~CentralArtifactRetentionPerformanceTests.RuntimeSqlPolicy_W4AndDelayedObjectIo_RecordsEvidence",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };

        var sourceDirectory = Path.Combine(
            repositoryRoot, "TestResults", "issue-254", source.OutputDirectoryName);
        var outputDirectory = Path.Combine(sourceDirectory, source.RunId);
        Directory.CreateDirectory(outputDirectory);
        var evidencePath = Path.Combine(outputDirectory, "sql-session-policy-evidence.json");
        var serialized = JsonSerializer.SerializeToUtf8Bytes(evidence, EvidenceJsonOptions);
        AssertNoForbiddenEvidenceValues(
            serialized,
            repositoryRoot,
            fixture.SqlServerConnectionString,
            objectKeys,
            artifactIds.Concat(frameIds).Concat(generatedEntityIds));
        await EvidenceSourceIdentity.WriteJsonAsync(evidencePath, evidence, EvidenceJsonOptions).ConfigureAwait(false);
        await WriteIssue254ManifestAsync(outputDirectory, source, evidencePath).ConfigureAwait(false);
        await TryWriteIssue254SummaryAsync(sourceDirectory, phase, source, productionRevision, harnessSha256)
            .ConfigureAwait(false);
        TestContext.WriteLine(
            "issue254 SQL session evidence: phase={0}, trial={1}, output={2}",
            phase,
            source.Trial?.ToString(CultureInfo.InvariantCulture) ?? "development",
            Path.GetRelativePath(repositoryRoot, outputDirectory));
    }

    private static Issue254PerformanceEvidence CreateIssue254PerformanceEvidence(
        int concurrency,
        MeasuredReleaseBatch measured,
        Issue246ProtocolSnapshot protocol)
    {
        var durations = measured.Samples.Select(sample => sample.DurationMilliseconds).Order().ToArray();
        return new Issue254PerformanceEvidence(
            concurrency,
            measured.Samples,
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            Percentile(durations, 0.99),
            durations[^1],
            measured.WallElapsed.TotalMilliseconds,
            measured.Samples.Count / measured.WallElapsed.TotalSeconds,
            (long)measured.Samples.Count * Issue254PayloadBytes,
            measured.Resources,
            measured.Sql,
            protocol);
    }

    private static Issue254DelayEvidence CreateIssue254DelayEvidence(
        int concurrency,
        int delayMilliseconds,
        MeasuredReleaseBatch measured,
        Issue246ProtocolSnapshot protocol)
    {
        var durations = measured.Samples.Select(sample => sample.DurationMilliseconds).Order().ToArray();
        var stableWindowOverlap = delayMilliseconds == 0
            ? 0
            : measured.Sql.Samples.Where(sample => sample.SessionApplicationLocks >= concurrency)
                .Max(sample => sample.ApplicationLockSessionsWithOpenTransactions);
        return new Issue254DelayEvidence(
            concurrency,
            delayMilliseconds,
            measured.Samples,
            Percentile(durations, 0.50),
            durations[^1],
            measured.WallElapsed.TotalMilliseconds,
            measured.Resources,
            measured.Sql,
            protocol,
            stableWindowOverlap);
    }

    private static Issue254ConnectionPolicy ReadEffectiveConnectionPolicy(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        using var command = new SqlCommand();
        return new Issue254ConnectionPolicy(
            builder.ApplicationName,
            builder.Pooling,
            builder.MinPoolSize,
            builder.MaxPoolSize,
            builder.ConnectTimeout,
            command.CommandTimeout,
            builder.LoadBalanceTimeout,
            builder.ConnectRetryCount,
            builder.ConnectRetryInterval,
            "Provider defaults are retained when absent; operator-supplied pool and timeout keywords are preserved. Only Application Name is application-owned and normalized.");
    }

    private static byte[] CreateIssue254Payload()
    {
        var payload = new byte[Issue254PayloadBytes];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(((long)index * 31 + 29) % 251);
        }
        return payload;
    }

    private static void ClearIssue254RuntimePool(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        SqlConnection.ClearPool(connection);
    }

    private static async Task<string> ResolveIssue254ProductionRevisionAsync(string repositoryRoot)
    {
        var requested = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PRODUCTION_REVISION");
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = Issue254BaselineRevision;
        }
        return (await RunGitAsync(repositoryRoot, "rev-parse", $"{requested}^{{commit}}")
            .ConfigureAwait(false)).Trim();
    }

    private static async Task ValidateIssue254EvidenceIdentityAsync(
        string repositoryRoot,
        string phase,
        string productionRevision,
        EvidenceSourceSnapshot source)
    {
        if (phase == "development")
        {
            return;
        }
        if (!string.Equals(Environment.GetEnvironmentVariable(Issue254OptInVariable), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable(Issue254SmokeVariable), "1", StringComparison.Ordinal)
            || source.Dirty
            || source.RequestedRevision is null
            || source.Trial is null
            || !string.Equals(source.Claimability, "clean-source-attributed-review-required", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Claimable evidence requires {Issue254OptInVariable}=1, a clean requested revision, and HVO_EVIDENCE_TRIAL=1..5.");
        }
        if (phase == "baseline")
        {
            if (!string.Equals(productionRevision, Issue254BaselineRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Issue #254 baseline evidence must use the pinned merged #253 revision.");
            }
            if (!await IsAncestorAsync(repositoryRoot, productionRevision, source.Head).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The pinned issue #254 baseline must be an ancestor of evidence HEAD.");
            }
            var permitted = new HashSet<string>(StringComparer.Ordinal)
            {
                "docs/runbooks/ci-pipeline.md",
                "scripts/test-categories/Program.cs",
                "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/CentralArtifactRetentionIssue254PerformanceTests.cs",
                "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/CentralArtifactRetentionPerformanceTests.cs"
            };
            var changedPaths = await ReadChangedPathsAsync(repositoryRoot, productionRevision).ConfigureAwait(false);
            if (changedPaths.Any(path => !permitted.Contains(path)))
            {
                throw new InvalidOperationException("Issue #254 baseline HEAD contains changes outside the frozen harness envelope.");
            }
        }
        else if (!string.Equals(productionRevision, source.Head, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Issue #254 after evidence production revision must equal evidence HEAD.");
        }
    }

    private static string ComputeIssue254HarnessSha256(string repositoryRoot)
    {
        var paths = new[]
        {
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/CentralArtifactRetentionIssue254PerformanceTests.cs",
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/CentralArtifactRetentionPerformanceTests.cs",
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/Infrastructure/Issue246RetentionEvidenceCollector.cs",
            "tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/LogicHostIntegrationFixture.cs",
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/AssemblyHooks.cs",
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj",
            "tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/HVO.SkyMonitor.LogicHost.TestInfrastructure.csproj",
            "src/HVO.SkyMonitor.TestSupport/EvidenceSourceIdentity.cs",
            "src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj",
            "Directory.Build.props",
            "Directory.Packages.props",
            "global.json"
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData(File.ReadAllBytes(Path.Combine(repositoryRoot, path)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task WriteIssue254ManifestAsync(
        string outputDirectory,
        EvidenceSourceSnapshot source,
        string evidencePath)
    {
        var bytes = await File.ReadAllBytesAsync(evidencePath).ConfigureAwait(false);
        await EvidenceSourceIdentity.WriteJsonAsync(
            Path.Combine(outputDirectory, "manifest.json"),
            new
            {
                Schema = "hvo-issue-254-evidence-manifest-v1",
                Source = source,
                Files = new[]
                {
                    new
                    {
                        Name = Path.GetFileName(evidencePath),
                        ByteLength = bytes.LongLength,
                        Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                    }
                }
            },
            EvidenceJsonOptions).ConfigureAwait(false);
    }

    private static async Task TryWriteIssue254SummaryAsync(
        string sourceDirectory,
        string phase,
        EvidenceSourceSnapshot source,
        string productionRevision,
        string harnessSha256)
    {
        if (source.Trial is null)
        {
            return;
        }
        var paths = Enumerable.Range(1, 5)
            .Select(trial => Path.Combine(sourceDirectory, $"trial-{trial}", "sql-session-policy-evidence.json"))
            .ToArray();
        var manifestPaths = Enumerable.Range(1, 5)
            .Select(trial => Path.Combine(sourceDirectory, $"trial-{trial}", "manifest.json"))
            .ToArray();
        if (paths.Any(path => !File.Exists(path)) || manifestPaths.Any(path => !File.Exists(path)))
        {
            return;
        }
        var documents = new List<JsonDocument>();
        try
        {
            for (var index = 0; index < paths.Length; index++)
            {
                var evidenceBytes = await File.ReadAllBytesAsync(paths[index]).ConfigureAwait(false);
                using var manifest = JsonDocument.Parse(
                    await File.ReadAllBytesAsync(manifestPaths[index]).ConfigureAwait(false));
                manifest.RootElement.GetProperty("Source").GetProperty("Head").GetString().Should().Be(source.Head);
                manifest.RootElement.GetProperty("Source").GetProperty("Trial").GetInt32().Should().Be(index + 1);
                var file = manifest.RootElement.GetProperty("Files").EnumerateArray().Single();
                file.GetProperty("ByteLength").GetInt64().Should().Be(evidenceBytes.LongLength);
                file.GetProperty("Sha256").GetString().Should().Be(
                    Convert.ToHexString(SHA256.HashData(evidenceBytes)));
                documents.Add(JsonDocument.Parse(evidenceBytes));
            }
            var roots = documents.Select(document => document.RootElement).ToArray();
            roots.Select((root, index) => new { root, index }).Should().OnlyContain(item =>
                item.root.GetProperty("Schema").GetString() == "hvo-issue-254-sql-session-policy-evidence-v1"
                && item.root.GetProperty("Phase").GetString() == phase
                && item.root.GetProperty("Source").GetProperty("Head").GetString() == source.Head
                && item.root.GetProperty("Source").GetProperty("Trial").GetInt32() == item.index + 1
                && item.root.GetProperty("Source").GetProperty("Claimability").GetString()
                    == "clean-source-attributed-review-required"
                && item.root.GetProperty("ProductionRevision").GetString() == productionRevision
                && item.root.GetProperty("HarnessSha256").GetString() == harnessSha256);
            roots.Select(root => root.GetProperty("Environment").GetProperty("EnvironmentFingerprintSha256")
                    .GetString()).Distinct(StringComparer.Ordinal).Should().ContainSingle();
            var environmentFingerprint = roots[0].GetProperty("Environment")
                .GetProperty("EnvironmentFingerprintSha256").GetString()!;
            var workloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                roots[0].GetProperty("Workload").GetRawText())));
            var policySha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                roots[0].GetProperty("EffectiveConnectionPolicy").GetRawText())));
            roots.Select(root => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    root.GetProperty("Workload").GetRawText()))))
                .Distinct(StringComparer.Ordinal).Should().ContainSingle();
            roots.Select(root => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    root.GetProperty("EffectiveConnectionPolicy").GetRawText()))))
                .Distinct(StringComparer.Ordinal).Should().ContainSingle();
            var performance = ConcurrencyLevels.Select(concurrency =>
            {
                var cells = roots.Select(root => root.GetProperty("Performance").EnumerateArray()
                    .Single(item => item.GetProperty("Concurrency").GetInt32() == concurrency)).ToArray();
                cells.Should().OnlyContain(cell => cell.GetProperty("Samples").GetArrayLength() == Issue254Measurements
                    && cell.GetProperty("Samples").EnumerateArray().All(sample =>
                        sample.GetProperty("Outcome").GetString() == CentralArtifactRetentionResult.Released.ToString()));
                return new
                {
                    Concurrency = concurrency,
                    P50Milliseconds = Summarize(cells.Select(cell => cell.GetProperty("P50Milliseconds").GetDouble())),
                    P95Milliseconds = Summarize(cells.Select(cell => cell.GetProperty("P95Milliseconds").GetDouble())),
                    P99Milliseconds = Summarize(cells.Select(cell => cell.GetProperty("P99Milliseconds").GetDouble())),
                    MaximumMilliseconds = Summarize(cells.Select(cell => cell.GetProperty("MaximumMilliseconds").GetDouble())),
                    WallMilliseconds = Summarize(cells.Select(cell => cell.GetProperty("WallMilliseconds").GetDouble())),
                    OperationsPerSecond = Summarize(cells.Select(cell => cell.GetProperty("OperationsPerSecond").GetDouble())),
                    LogicalObjectBytesRemoved = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("LogicalObjectBytesRemoved").GetInt64())),
                    PeakAttributedSqlSessions = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Sql").GetProperty("PeakAttributedSqlSessions").GetInt32())),
                    PeakSleepingSessions = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Sql").GetProperty("PeakSleepingSessions").GetInt32())),
                    PeakActiveSessions = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Sql").GetProperty("PeakActiveSessions").GetInt32())),
                    PeakActiveRequests = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Sql").GetProperty("PeakActiveRequests").GetInt32())),
                    PeakOpenTransactionSessions = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Sql").GetProperty("PeakOpenTransactionSessions").GetInt32())),
                    PeakSessionApplicationLocks = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Sql").GetProperty("PeakSessionApplicationLocks").GetInt32())),
                    PeakBlockedRequests = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Sql").GetProperty("PeakBlockedRequests").GetInt32())),
                    PeakActiveTransactionLogBytes = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Sql").GetProperty("PeakActiveTransactionLogBytes").GetInt64())),
                    ObservedBatchApplicationLockOccupancyMilliseconds = Summarize(cells.Select(cell =>
                        cell.GetProperty("Sql").GetProperty("ObservedBatchApplicationLockOccupancyMilliseconds")
                            .GetDouble())),
                    EffectiveSamplingIntervalMilliseconds = Summarize(cells.Select(cell =>
                        cell.GetProperty("Sql").GetProperty("EffectiveMedianIntervalMilliseconds").GetDouble())),
                    AllocatedBytes = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Resources").GetProperty("AllocatedBytes").GetInt64())),
                    ProcessCpuMilliseconds = Summarize(cells.Select(cell =>
                        cell.GetProperty("Resources").GetProperty("ProcessCpuMilliseconds").GetDouble())),
                    WorkingSetStartBytes = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Resources").GetProperty("WorkingSetStartBytes").GetInt64())),
                    WorkingSetEndBytes = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Resources").GetProperty("WorkingSetEndBytes").GetInt64())),
                    PeakObservedWorkingSetBytes = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Resources").GetProperty("PeakObservedWorkingSetBytes").GetInt64())),
                    EfCommands = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Protocol").GetProperty("EfCommands").GetInt64())),
                    TransactionsCommitted = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Protocol").GetProperty("SqlTransactions")
                            .GetProperty("Committed").GetInt64())),
                    TransactionsRolledBack = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Protocol").GetProperty("SqlTransactions")
                            .GetProperty("RolledBack").GetInt64())),
                    TransactionsFailed = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Protocol").GetProperty("SqlTransactions")
                            .GetProperty("Failed").GetInt64())),
                    MinioRequests = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Protocol").GetProperty("ObjectStore")
                            .GetProperty("Requests").GetInt64())),
                    MinioDeletes = Summarize(cells.Select(cell =>
                        (double)cell.GetProperty("Protocol").GetProperty("ObjectStore")
                            .GetProperty("Deletes").GetInt64()))
                };
            }).ToArray();
            var delayed = (from concurrency in ConcurrencyLevels
                           from delay in DeleteDelaysMilliseconds
                           let cells = roots.Select(root => root.GetProperty("DelayedObjectIo").EnumerateArray()
                               .Single(item => item.GetProperty("Concurrency").GetInt32() == concurrency
                                   && item.GetProperty("ConfiguredDelayMilliseconds").GetInt32() == delay)).ToArray()
                           let validatedCells = ValidateIssue254DelayCells(cells, concurrency)
                           select new
                           {
                               Concurrency = concurrency,
                               DelayMilliseconds = delay,
                               P50Milliseconds = Summarize(validatedCells.Select(cell => cell.GetProperty("P50Milliseconds").GetDouble())),
                               MaximumMilliseconds = Summarize(validatedCells.Select(cell => cell.GetProperty("MaximumMilliseconds").GetDouble())),
                               WallMilliseconds = Summarize(validatedCells.Select(cell => cell.GetProperty("WallMilliseconds").GetDouble())),
                               PeakAttributedSqlSessions = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Sql").GetProperty("PeakAttributedSqlSessions").GetInt32())),
                               PeakSleepingSessions = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Sql").GetProperty("PeakSleepingSessions").GetInt32())),
                               PeakActiveSessions = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Sql").GetProperty("PeakActiveSessions").GetInt32())),
                               PeakActiveRequests = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Sql").GetProperty("PeakActiveRequests").GetInt32())),
                               PeakOpenTransactionSessions = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Sql").GetProperty("PeakOpenTransactionSessions").GetInt32())),
                               PeakSessionApplicationLocks = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Sql").GetProperty("PeakSessionApplicationLocks").GetInt32())),
                               StableWindowApplicationLockSessionsWithOpenTransactions = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("StableWindowApplicationLockSessionsWithOpenTransactions")
                                       .GetInt32())),
                               PeakBlockedRequests = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Sql").GetProperty("PeakBlockedRequests").GetInt32())),
                               PeakActiveTransactionLogBytes = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Sql").GetProperty("PeakActiveTransactionLogBytes").GetInt64())),
                               ObservedBatchApplicationLockOccupancyMilliseconds = Summarize(validatedCells.Select(cell =>
                                   cell.GetProperty("Sql").GetProperty("ObservedBatchApplicationLockOccupancyMilliseconds")
                                       .GetDouble())),
                               EffectiveSamplingIntervalMilliseconds = Summarize(validatedCells.Select(cell =>
                                   cell.GetProperty("Sql").GetProperty("EffectiveMedianIntervalMilliseconds").GetDouble())),
                               AllocatedBytes = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Resources").GetProperty("AllocatedBytes").GetInt64())),
                               ProcessCpuMilliseconds = Summarize(validatedCells.Select(cell =>
                                   cell.GetProperty("Resources").GetProperty("ProcessCpuMilliseconds").GetDouble())),
                               PeakObservedWorkingSetBytes = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Resources").GetProperty("PeakObservedWorkingSetBytes").GetInt64())),
                               EfCommands = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Protocol").GetProperty("EfCommands").GetInt64())),
                               TransactionsCommitted = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Protocol").GetProperty("SqlTransactions")
                                       .GetProperty("Committed").GetInt64())),
                               TransactionsRolledBack = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Protocol").GetProperty("SqlTransactions")
                                       .GetProperty("RolledBack").GetInt64())),
                               TransactionsFailed = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Protocol").GetProperty("SqlTransactions")
                                       .GetProperty("Failed").GetInt64())),
                               MinioDeletes = Summarize(validatedCells.Select(cell =>
                                   (double)cell.GetProperty("Protocol").GetProperty("ObjectStore")
                                       .GetProperty("Deletes").GetInt64()))
                           }).ToArray();
            var summaryPath = Path.Combine(sourceDirectory, $"{phase}-five-trial-summary.json");
            await EvidenceSourceIdentity.WriteJsonAsync(
                summaryPath,
                new
                {
                    Schema = "hvo-issue-254-five-trial-summary-v1",
                    Issue = 254,
                    Phase = phase,
                    SourceHead = source.Head,
                    ProductionRevision = productionRevision,
                    HarnessSha256 = harnessSha256,
                    EnvironmentFingerprintSha256 = environmentFingerprint,
                    WorkloadSha256 = workloadSha256,
                    EffectiveConnectionPolicySha256 = policySha256,
                    TrialCount = 5,
                    EffectiveConnectionPolicy = roots[0].GetProperty("EffectiveConnectionPolicy"),
                    Performance = performance,
                    DelayedObjectIo = delayed,
                    Correctness = "Every trial passed attribution, caller-visible outcome, SQL/MinIO convergence, transaction, and forbidden-value gates. Internal recovered SQL retries are not classified.",
                    RecordedAtUtc = DateTimeOffset.UtcNow
                }, EvidenceJsonOptions).ConfigureAwait(false);
            string? comparisonPath = null;
            if (phase == "after")
            {
                var baselineSummaryPath = Environment.GetEnvironmentVariable("HVO_EVIDENCE_BASELINE_SUMMARY");
                if (string.IsNullOrWhiteSpace(baselineSummaryPath) || !File.Exists(baselineSummaryPath))
                {
                    throw new InvalidOperationException(
                        "Issue #254 after aggregation requires HVO_EVIDENCE_BASELINE_SUMMARY pointing to the reviewed baseline summary.");
                }
                comparisonPath = Path.Combine(sourceDirectory, "baseline-after-comparison.json");
                await WriteIssue254ComparisonAsync(baselineSummaryPath, summaryPath, comparisonPath)
                    .ConfigureAwait(false);
            }
            var retainedPaths = paths.Concat(manifestPaths).Append(summaryPath);
            if (comparisonPath is not null)
            {
                retainedPaths = retainedPaths.Append(comparisonPath);
            }
            var retainedFiles = retainedPaths.Order(StringComparer.Ordinal)
                .Select(path =>
                {
                    var bytes = File.ReadAllBytes(path);
                    return new
                    {
                        Name = Path.GetRelativePath(sourceDirectory, path),
                        ByteLength = bytes.LongLength,
                        Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                    };
                }).ToArray();
            await EvidenceSourceIdentity.WriteJsonAsync(
                Path.Combine(sourceDirectory, $"{phase}-five-trial-manifest.json"),
                new { Schema = "hvo-issue-254-five-trial-manifest-v1", Source = source, Files = retainedFiles },
                EvidenceJsonOptions).ConfigureAwait(false);
        }
        finally
        {
            foreach (var document in documents)
            {
                document.Dispose();
            }
        }
    }

    private static async Task WriteIssue254ComparisonAsync(
        string baselineSummaryPath,
        string afterSummaryPath,
        string outputPath)
    {
        var baselineBytes = await File.ReadAllBytesAsync(baselineSummaryPath).ConfigureAwait(false);
        var afterBytes = await File.ReadAllBytesAsync(afterSummaryPath).ConfigureAwait(false);
        using var baseline = JsonDocument.Parse(baselineBytes);
        using var after = JsonDocument.Parse(afterBytes);
        var baselineRoot = baseline.RootElement;
        var afterRoot = after.RootElement;
        baselineRoot.GetProperty("Schema").GetString().Should().Be("hvo-issue-254-five-trial-summary-v1");
        baselineRoot.GetProperty("Phase").GetString().Should().Be("baseline");
        afterRoot.GetProperty("Phase").GetString().Should().Be("after");
        foreach (var identity in new[]
        {
            "HarnessSha256",
            "EnvironmentFingerprintSha256",
            "WorkloadSha256",
            "EffectiveConnectionPolicySha256"
        })
        {
            afterRoot.GetProperty(identity).GetString().Should().Be(baselineRoot.GetProperty(identity).GetString());
        }

        var comparisons = new List<ComparisonMetric>();
        var performanceMetrics = new[]
        {
            "P50Milliseconds", "P95Milliseconds", "P99Milliseconds", "MaximumMilliseconds",
            "WallMilliseconds", "LogicalObjectBytesRemoved", "PeakAttributedSqlSessions",
            "PeakSleepingSessions", "PeakActiveSessions", "PeakActiveRequests", "PeakOpenTransactionSessions",
            "PeakSessionApplicationLocks",
            "PeakBlockedRequests", "PeakActiveTransactionLogBytes", "ObservedBatchApplicationLockOccupancyMilliseconds",
            "EffectiveSamplingIntervalMilliseconds", "AllocatedBytes", "ProcessCpuMilliseconds",
            "WorkingSetStartBytes", "WorkingSetEndBytes", "PeakObservedWorkingSetBytes", "EfCommands",
            "TransactionsCommitted", "TransactionsRolledBack", "TransactionsFailed", "MinioRequests", "MinioDeletes"
        };
        foreach (var concurrency in ConcurrencyLevels)
        {
            var baselineCell = FindScenario(baselineRoot.GetProperty("Performance"), "Concurrency", concurrency);
            var afterCell = FindScenario(afterRoot.GetProperty("Performance"), "Concurrency", concurrency);
            comparisons.Add(CreateComparison(
                $"C{concurrency}.OperationsPerSecond",
                baselineCell.GetProperty("OperationsPerSecond"),
                afterCell.GetProperty("OperationsPerSecond"),
                higherIsRegression: false));
            comparisons.AddRange(performanceMetrics
                .Except(Issue254DiagnosticSqlMetrics, StringComparer.Ordinal)
                .Select(metric => CreateComparison(
                $"C{concurrency}.{metric}",
                baselineCell.GetProperty(metric),
                afterCell.GetProperty(metric),
                higherIsRegression: true)));
        }
        var delayMetrics = new[]
        {
            "P50Milliseconds", "MaximumMilliseconds", "WallMilliseconds", "PeakAttributedSqlSessions",
            "PeakSleepingSessions", "PeakActiveRequests", "PeakOpenTransactionSessions",
            "PeakSessionApplicationLocks", "StableWindowApplicationLockSessionsWithOpenTransactions",
            "PeakBlockedRequests", "PeakActiveTransactionLogBytes", "ObservedBatchApplicationLockOccupancyMilliseconds",
            "EffectiveSamplingIntervalMilliseconds", "AllocatedBytes", "ProcessCpuMilliseconds",
            "PeakObservedWorkingSetBytes", "EfCommands", "TransactionsCommitted", "TransactionsRolledBack",
            "TransactionsFailed", "MinioDeletes"
        };
        foreach (var concurrency in ConcurrencyLevels)
        {
            foreach (var delay in DeleteDelaysMilliseconds)
            {
                var baselineCell = FindIssue254Delay(baselineRoot.GetProperty("DelayedObjectIo"), concurrency, delay);
                var afterCell = FindIssue254Delay(afterRoot.GetProperty("DelayedObjectIo"), concurrency, delay);
                comparisons.AddRange(delayMetrics
                    .Except(Issue254DiagnosticSqlMetrics, StringComparer.Ordinal)
                    .Select(metric => CreateComparison(
                    $"C{concurrency}.Delay{delay}.{metric}",
                    baselineCell.GetProperty(metric),
                    afterCell.GetProperty(metric),
                    higherIsRegression: true)));
            }
        }
        var materialRegressions = comparisons.Where(comparison => comparison.MaterialRegression)
            .Select(comparison => comparison.Name).ToArray();
        await EvidenceSourceIdentity.WriteJsonAsync(
            outputPath,
            new
            {
                Schema = "hvo-issue-254-baseline-after-comparison-v1",
                Issue = 254,
                BaselineSummarySha256 = Convert.ToHexString(SHA256.HashData(baselineBytes)),
                AfterSummarySha256 = Convert.ToHexString(SHA256.HashData(afterBytes)),
                Metrics = comparisons,
                DiagnosticMetrics = new[]
                {
                    new
                    {
                        Name = "SQL DMV state and occupancy counts",
                        Disposition = "Recorded in every raw and five-trial summary but not assigned an invented regression direction. Session, request, transaction, lock, log-byte, occupancy-window, and sampling-interval values are pool/behavior diagnostics from non-atomic DMV samples. Blocked requests and stable-window application-lock sessions with open transactions remain compared correctness signals."
                    }
                },
                MaterialRegressions = materialRegressions,
                Result = materialRegressions.Length == 0
                    ? "passed-no-material-regression"
                    : "review-required-material-regression",
                Rule = "Material when regression exceeds max(20%, 2 * baseline five-trial (max-min)/median range). Throughput regresses downward; other compared metrics regress upward.",
                RecordedAtUtc = DateTimeOffset.UtcNow
            }, EvidenceJsonOptions).ConfigureAwait(false);
        materialRegressions.Should().BeEmpty("unexplained material regression blocks issue #254 evidence");
    }

    private static JsonElement FindIssue254Delay(JsonElement cells, int concurrency, int delay)
        => cells.EnumerateArray().Single(cell => cell.GetProperty("Concurrency").GetInt32() == concurrency
            && cell.GetProperty("DelayMilliseconds").GetInt32() == delay);

    private static JsonElement[] ValidateIssue254DelayCells(JsonElement[] cells, int concurrency)
    {
        cells.Should().OnlyContain(cell => cell.GetProperty("Samples").GetArrayLength() == concurrency
            && cell.GetProperty("Samples").EnumerateArray().All(sample =>
                sample.GetProperty("Outcome").GetString() == CentralArtifactRetentionResult.Released.ToString()));
        return cells;
    }

    private sealed class Issue254HostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "HVO.SkyMonitor.Issue254";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed record Issue254ConnectionPolicy(
        string ApplicationName,
        bool Pooling,
        int MinPoolSize,
        int MaxPoolSize,
        int ConnectTimeoutSeconds,
        int CommandTimeoutSeconds,
        int LoadBalanceTimeoutSeconds,
        int ConnectRetryCount,
        int ConnectRetryIntervalSeconds,
        string Disposition);

    private sealed record Issue254PerformanceEvidence(
        int Concurrency,
        IReadOnlyList<RetentionOperationSample> Samples,
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaximumMilliseconds,
        double WallMilliseconds,
        double OperationsPerSecond,
        long LogicalObjectBytesRemoved,
        ProcessResourceEvidence Resources,
        SqlSamplingEvidence Sql,
        Issue246ProtocolSnapshot Protocol);

    private sealed record Issue254DelayEvidence(
        int Concurrency,
        int ConfiguredDelayMilliseconds,
        IReadOnlyList<RetentionOperationSample> Samples,
        double P50Milliseconds,
        double MaximumMilliseconds,
        double WallMilliseconds,
        ProcessResourceEvidence Resources,
        SqlSamplingEvidence Sql,
        Issue246ProtocolSnapshot Protocol,
        int StableWindowApplicationLockSessionsWithOpenTransactions);
}
