using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Evidence cleanup attempts every object and SQL cleanup before reporting aggregate failures.")]
public sealed partial class CentralArtifactRetentionPerformanceTests
{
    private const string BaselineRevision = "f72da9adcace56f207f69a6aa370cecdc7dc13ad";
    private const string Bucket = "skymonitor-artifacts";
    private const int PayloadBytes = 12_879_360;
    private const int WarmupOperations = 5;
    private const int MeasuredOperations = 30;
    private const int DelayedOperations = 8;
    private const string ExpectedPayloadSha256 = "054AB9B92C65290B869893B8CF16EA4B3DC6A094878AE35A63CDEF7D425A1643";
    private static readonly int[] ConcurrencyLevels = [1, 4, 8];
    private static readonly int[] DeleteDelaysMilliseconds = [0, 250, 2_000];
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private readonly List<Guid> frameIds = [];
    private readonly List<Guid> artifactIds = [];
    private readonly List<string> objectKeys = [];
    private readonly List<Guid> generatedEntityIds = [];

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public async Task CleanupAsync()
    {
        var failures = new List<Exception>();
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        foreach (var key in objectKeys)
        {
            try
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(key)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        try
        {
            if (frameIds.Count > 0)
            {
                await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.CentralFrames.Where(frame => frameIds.Contains(frame.Id)).ExecuteDeleteAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("Issue #246 evidence cleanup failed.", failures);
        }
    }

    [TestMethod]
    public async Task Release_W2W4AndDelayedDelete_RecordsEvidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var phase = ReadEvidencePhase();
        var productionRevision = await ResolveProductionRevisionAsync(repositoryRoot).ConfigureAwait(false);
        var payload = CreatePayload();
        Convert.ToHexString(SHA256.HashData(payload)).Should().Be(ExpectedPayloadSha256);

        var fixture = AssemblyHooks.Fixture;
        var runToken = Guid.NewGuid().ToString("N");
        var performance = new List<RetentionScenarioEvidence>();
        foreach (var concurrency in ConcurrencyLevels)
        {
            var applicationName = $"HVO.SkyMonitor.Issue246.{runToken}.C{concurrency}";
            var subjectConnectionString = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
            {
                ApplicationName = applicationName
            }.ConnectionString;
            using var collector = new Issue246RetentionEvidenceCollector();
            using var factory = CreateFactory(fixture, subjectConnectionString, collector);
            _ = factory.Services;
            var artifacts = await SeedArtifactsAsync(
                $"issue-246/{runToken}/c{concurrency}",
                WarmupOperations + MeasuredOperations,
                payload,
                ExpectedPayloadSha256).ConfigureAwait(false);
            collector.Http.Configure(TimeSpan.Zero);
            _ = await ExecuteReleasesAsync(
                factory, artifacts.Take(WarmupOperations).ToArray(), concurrency).ConfigureAwait(false);
            collector.Reset();
            var logBefore = await ReadDatabaseLogUsageAsync(fixture.SqlServerConnectionString).ConfigureAwait(false);
            var measured = await MeasureReleasesAsync(
                factory,
                artifacts.Skip(WarmupOperations).ToArray(),
                concurrency,
                fixture.SqlServerConnectionString,
                applicationName).ConfigureAwait(false);
            var protocol = collector.Snapshot();
            var logAfter = await ReadDatabaseLogUsageAsync(fixture.SqlServerConnectionString).ConfigureAwait(false);
            await VerifyReleasedAsync(artifacts).ConfigureAwait(false);
            protocol.ObjectStore.Deletes.Should().Be(MeasuredOperations);
            measured.Samples.Should().OnlyContain(
                sample => sample.Outcome == CentralArtifactRetentionResult.Released.ToString());
            performance.Add(CreateScenarioEvidence(
                concurrency,
                measured,
                protocol,
                logBefore,
                logAfter));
        }

        var delayed = new List<DelayedDeleteEvidence>();
        foreach (var delayMilliseconds in DeleteDelaysMilliseconds)
        {
            var applicationName = $"HVO.SkyMonitor.Issue246.{runToken}.Delay{delayMilliseconds}";
            var subjectConnectionString = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
            {
                ApplicationName = applicationName
            }.ConnectionString;
            using var collector = new Issue246RetentionEvidenceCollector();
            using var factory = CreateFactory(fixture, subjectConnectionString, collector);
            _ = factory.Services;
            var artifacts = await SeedArtifactsAsync(
                $"issue-246/{runToken}/delay-{delayMilliseconds}",
                DelayedOperations,
                payload,
                ExpectedPayloadSha256).ConfigureAwait(false);
            var artifactByKey = artifacts.Select((artifact, ordinal) => new { artifact, ordinal })
                .ToDictionary(item => item.artifact.ObjectKey, StringComparer.Ordinal);
            var writerTasks = new ConcurrentBag<Task<BlockedWriterEvidence>>();
            collector.Reset();
            collector.Http.Configure(TimeSpan.FromMilliseconds(delayMilliseconds), key =>
            {
                var sessionId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var executionTask = ObserveArtifactLockAsync(
                    fixture.SqlServerConnectionString,
                    applicationName,
                    artifactByKey[key].artifact.CentralArtifactId,
                    artifactByKey[key].ordinal,
                    sessionId);
                var observationTask = ObserveWriterRequestAsync(
                    fixture.SqlServerConnectionString,
                    applicationName,
                    sessionId.Task,
                    executionTask);
                writerTasks.Add(CreateBlockedWriterEvidenceAsync(
                    artifactByKey[key].ordinal, executionTask, observationTask));
                return new DeleteProbe(observationTask);
            });
            using var samplingCancellation = new CancellationTokenSource();
            var samplesTask = SampleSqlCriticalSectionAsync(
                fixture.SqlServerConnectionString,
                applicationName,
                samplingCancellation.Token);
            IReadOnlyList<RetentionOperationSample>? operationSamples = null;
            IReadOnlyList<SqlCriticalSectionSample> sqlSamples;
            Exception? operationFailure = null;
            try
            {
                operationSamples = await ExecuteReleasesAsync(factory, artifacts, concurrency: 4).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                operationFailure = exception;
            }
            finally
            {
                await samplingCancellation.CancelAsync().ConfigureAwait(false);
                sqlSamples = await samplesTask.ConfigureAwait(false);
            }
            var writers = await Task.WhenAll(writerTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (operationFailure is not null)
            {
                ExceptionDispatchInfo.Capture(operationFailure).Throw();
            }
            var protocol = collector.Snapshot();
            await VerifyReleasedAsync(artifacts).ConfigureAwait(false);
            operationSamples.Should().NotBeNull();
            protocol.ObjectStore.Deletes.Should().Be(DelayedOperations);
            writers.Should().HaveCount(DelayedOperations).And.OnlyContain(writer => !writer.TimedOut);
            if (phase is "baseline" or "development")
            {
                writers.All(writer =>
                    writer.SqlObservation is
                    {
                        BlockerIsAttributedRetentionSession: true,
                        TransactionIsolationLevel: 4,
                        WaitingResourceType: "KEY",
                        WaitingRequestMode: "U",
                        WaitingRequestStatus: "WAIT",
                        WaitingRequestOwnerType: "TRANSACTION"
                    }
                    && writer.SqlObservation.WaitType is not null
                    && writer.SqlObservation.WaitType.StartsWith("LCK_M_", StringComparison.Ordinal))
                    .Should().BeTrue();
            }
            else
            {
                writers.All(writer => writer.SqlObservation is null).Should().BeTrue();
            }
            delayed.Add(new DelayedDeleteEvidence(
                delayMilliseconds,
                operationSamples!,
                writers.OrderBy(writer => writer.Ordinal).ToArray(),
                CreateSqlSamplingEvidence(sqlSamples),
                protocol,
                LogicalObjectBytesRemoved: (long)DelayedOperations * PayloadBytes));
        }
        var source = await EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(CentralArtifactRetentionPerformanceTests),
            typeof(CentralArtifactRetentionService)).ConfigureAwait(false);
        var changedPaths = await ReadChangedPathsAsync(repositoryRoot, productionRevision).ConfigureAwait(false);
        await ValidateEvidenceIdentityAsync(
            repositoryRoot, phase, productionRevision, source, changedPaths).ConfigureAwait(false);
        var environment = await CaptureEnvironmentAsync(repositoryRoot).ConfigureAwait(false);
        var harnessSha256 = ComputeHarnessSha256(repositoryRoot);
        var evidence = new
        {
            Schema = "hvo-issue-246-central-artifact-retention-evidence-v1",
            Issue = 246,
            Phase = phase,
            Source = source,
            ProductionRevision = productionRevision,
            CommittedChangedPathsFromProductionRevision = changedPaths,
            HarnessSha256 = harnessSha256,
            Environment = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                environment.CpuModel,
                environment.TotalMemoryBytes,
                environment.PinnedSdkVersion,
                environment.ExecutingSdkVersion,
                environment.DockerServerVersion,
                environment.EnvironmentFingerprintSha256,
                Configuration = "Release",
                ServerGc = System.Runtime.GCSettings.IsServerGC,
                Topology = "In-process LogicHost with shared SQL Server and MinIO Testcontainers; fixture startup excluded",
                WorkspaceStorage = "N/A: container workspace storage is bind-mounted and the physical media/type is not attributable",
                IntegrationTestFixture.SqlServerImage,
                IntegrationTestFixture.ExternalS3ImageLabel,
                ContainerCpuAndRss = "N/A: shared dependency containers are not process-attributable; service protocol, waits and I/O are recorded",
                ConnectionPool = "Application-name-attributed SQL sessions/open transactions/granted application locks are the declared proxy; exact SqlClient pool occupancy is unavailable"
            },
            Workload = new
            {
                Id = "W2/reduced-issue-specific-W4",
                Dimensions = "3096x2080",
                PixelFormat = "RGGB16 deterministic byte-equivalent payload; not a rendered VirtualSky frame",
                PayloadBytes,
                PayloadSha256 = ExpectedPayloadSha256,
                PayloadAlgorithm = "byte[i] = ((i * 31 + 29) % 251)",
                WarmupOperations,
                MeasuredOperationsPerLevel = MeasuredOperations,
                OperationUnit = "total distinct artifact releases per concurrency level",
                ConcurrencyLevels,
                DeleteDelaysMilliseconds,
                DelayedOperations,
                InitialBacklog = 0,
                ArrivalRate = 0,
                SqlSamplingTargetIntervalMilliseconds = 10,
                PercentileMethod = "nearest-rank over 30 independent measured releases after five warmups",
                MeasuredBoundary = "ICentralArtifactRetentionService.ReleaseAsync including object-lock wait, SQL reference checks/mutation, MinIO DELETE and commit",
                ExcludedBoundary = "fixture startup, SQL/MinIO seeding, pre-run GET checksum validation, final verification and cleanup"
            },
            Performance = performance,
            DelayedDelete = delayed,
            Correctness = new
            {
                ExactPreDeleteLengthAndSha256 = true,
                FinalSqlState = "Expired/retention.expired",
                FinalObjectState = "absent",
                DistinctObjectsPerOperation = true
            },
            RuntimeSignals = new
            {
                Baseline = "Only skymonitor.central.retrieval.retention outcome counters exist; dedicated retention stage metrics, spans, pending gauges, structured events and health are absent and are candidate requirements.",
                ForbiddenValueScan = "The serialized evidence is scanned before its manifest is accepted for object keys/references, entity IDs, credentials, connection strings and absolute repository paths. The fixed public workload checksum is retained as required evidence."
            },
            ProtocolScope = new
            {
                EfCommands = "Interceptor-attributed commands issued by measured retention DbContext instances only.",
                DirectApplicationLockCommands = "Not intercepted: production issues one sp_getapplock and one sp_releaseapplock on dedicated attributed sessions per completed release.",
                HttpBytes = "Entity-body bytes only. DELETE request content and HTTP 204 response content are exact zero; HTTP headers, request target, TLS and network framing are N/A.",
                DatabaseLog = "Global database log-capacity snapshots are retained before/after without workload attribution; active-transaction log bytes are sampled separately."
            },
            Command = "HVO_ISSUE_246_RETENTION_EVIDENCE=1 HVO_EVIDENCE_REVISION=<HEAD> HVO_EVIDENCE_PRODUCTION_REVISION=<REVISION> HVO_EVIDENCE_PHASE=baseline|after HVO_EVIDENCE_TRIAL=1..5 dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~CentralArtifactRetentionPerformanceTests.Release_W2W4AndDelayedDelete_RecordsEvidence",
            RegressionRule = "Correctness loss always fails. A timing/resource change is material above max(20%, 2 * baseline five-trial (max-min)/median range).",
            ResidualRisk = "This baseline does not inject crash boundaries because current production has no durable reservation to recover; candidate fault evidence uses the identical service boundary plus the pinned state-machine fault matrix.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };

        var output = Path.Combine(
            repositoryRoot, "TestResults", "issue-246", source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(output);
        var evidencePath = Path.Combine(output, "central-artifact-retention-evidence.json");
        var serializedEvidence = JsonSerializer.SerializeToUtf8Bytes(evidence, EvidenceJsonOptions);
        AssertNoForbiddenEvidenceValues(
            serializedEvidence,
            repositoryRoot,
            fixture.SqlServerConnectionString,
            objectKeys,
            artifactIds.Concat(frameIds).Concat(generatedEntityIds));
        await EvidenceSourceIdentity.WriteJsonAsync(evidencePath, evidence, EvidenceJsonOptions).ConfigureAwait(false);
        var evidenceBytes = await File.ReadAllBytesAsync(evidencePath).ConfigureAwait(false);
        evidenceBytes.Should().Equal(serializedEvidence);
        var manifest = new
        {
            Schema = "hvo-issue-246-evidence-manifest-v1",
            Source = source,
            Files = new[]
            {
                new
                {
                    Name = Path.GetFileName(evidencePath),
                    ByteLength = evidenceBytes.LongLength,
                    Sha256 = Convert.ToHexString(SHA256.HashData(evidenceBytes))
                }
            }
        };
        var manifestPath = Path.Combine(output, "manifest.json");
        await EvidenceSourceIdentity.WriteJsonAsync(manifestPath, manifest, EvidenceJsonOptions).ConfigureAwait(false);
        await TryWriteFiveTrialSummaryAsync(
            Path.GetDirectoryName(output)!, phase, source, productionRevision, harnessSha256).ConfigureAwait(false);
        TestContext.WriteLine(
            "issue246 retention evidence: phase={0}, trial={1}, output={2}",
            phase,
            source.Trial?.ToString(CultureInfo.InvariantCulture) ?? "development",
            Path.GetRelativePath(repositoryRoot, output));
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateFactory(
        IntegrationTestFixture fixture,
        string connectionString,
        Issue246RetentionEvidenceCollector collector)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options
                .UseSqlServer(connectionString)
                .AddInterceptors(collector.Commands, collector.Transactions));
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(IntegrationTestFixture.ExternalS3Endpoint)
                .WithCredentials(IntegrationTestFixture.ExternalS3AccessKey, IntegrationTestFixture.ExternalS3SecretKey)
                .WithHttpClient(new HttpClient(collector.Http, disposeHandler: false), disposeHttpClient: true)
                .Build());
            ObjectStoreTestClient.Replace(services);
        }));

    private async Task<IReadOnlyList<SeededRetentionArtifact>> SeedArtifactsAsync(
        string prefix,
        int count,
        byte[] payload,
        string checksum)
    {
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = $"issue-246-{Guid.NewGuid():N}",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            FirstReceivedAtUtc = DateTimeOffset.UnixEpoch
        };
        var artifacts = Enumerable.Range(0, count).Select(index =>
        {
            var key = $"{prefix}/{index:D3}.bin";
            var artifact = new CentralArtifact
            {
                CentralFrameId = frame.Id,
                DevicePublicId = frame.DevicePublicId,
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Raw,
                RecipeVersion = "issue-246-w2-v1",
                ManifestSchemaVersion = "evidence-v1",
                MediaType = "application/octet-stream",
                ByteLength = payload.LongLength,
                ChecksumSha256 = checksum,
                StorageReference = $"object://{Bucket}/{key}",
                ReceivedAtUtc = DateTimeOffset.UnixEpoch,
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            };
            return new SeededRetentionArtifact(artifact.Id, key, artifact);
        }).ToArray();
        frameIds.Add(frame.Id);
        artifactIds.AddRange(artifacts.Select(artifact => artifact.CentralArtifactId));
        generatedEntityIds.AddRange([
            frame.RegistrationId,
            frame.DevicePublicId,
            frame.ObservatoryId,
            frame.FrameId
        ]);
        objectKeys.AddRange(artifacts.Select(artifact => artifact.ObjectKey));
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CentralFrames.Add(frame);
            db.CentralArtifacts.AddRange(artifacts.Select(artifact => artifact.Artifact));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }
        await ForEachConcurrentAsync(artifacts, concurrency: 8, async artifact =>
        {
            await using var stream = new MemoryStream(payload, writable: false);
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(Bucket)
                .WithObject(artifact.ObjectKey)
                .WithStreamData(stream)
                .WithObjectSize(payload.LongLength)
                .WithContentType("application/octet-stream")).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await ForEachConcurrentAsync(artifacts, concurrency: 4, async artifact =>
        {
            var stat = await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(Bucket).WithObject(artifact.ObjectKey)).ConfigureAwait(false);
            checked((long)stat.Size).Should().Be(payload.LongLength);
            string? observedChecksum = null;
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(Bucket)
                .WithObject(artifact.ObjectKey)
                .WithCallbackStream(stream => observedChecksum = Convert.ToHexString(SHA256.HashData(stream))))
                .ConfigureAwait(false);
            observedChecksum.Should().Be(checksum);
        }).ConfigureAwait(false);
        return artifacts;
    }

    private static async Task<IReadOnlyList<RetentionOperationSample>> ExecuteReleasesAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        IReadOnlyList<SeededRetentionArtifact> artifacts,
        int concurrency)
    {
        var work = new ConcurrentQueue<(int Ordinal, SeededRetentionArtifact Artifact)>(
            artifacts.Select((artifact, ordinal) => (ordinal, artifact)));
        var samples = new RetentionOperationSample[artifacts.Count];
        var workers = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (work.TryDequeue(out var item))
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var started = Stopwatch.GetTimestamp();
                var result = await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
                    .ReleaseAsync(item.Artifact.CentralArtifactId, CancellationToken.None).ConfigureAwait(false);
                samples[item.Ordinal] = new RetentionOperationSample(
                    item.Ordinal,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    result.ToString());
            }
        });
        await Task.WhenAll(workers).ConfigureAwait(false);
        return samples;
    }

    private static async Task ForEachConcurrentAsync<T>(
        IReadOnlyCollection<T> items,
        int concurrency,
        Func<T, Task> operation)
    {
        var work = new ConcurrentQueue<T>(items);
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (work.TryDequeue(out var item))
            {
                await operation(item).ConfigureAwait(false);
            }
        })).ConfigureAwait(false);
    }

    private static async Task VerifyReleasedAsync(IReadOnlyList<SeededRetentionArtifact> artifacts)
    {
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var ids = artifacts.Select(artifact => artifact.CentralArtifactId).ToArray();
            var states = await db.CentralArtifacts.AsNoTracking()
                .Where(artifact => ids.Contains(artifact.Id))
                .Select(artifact => new { artifact.ObjectState, artifact.StateReasonCode })
                .ToArrayAsync().ConfigureAwait(false);
            states.Should().HaveCount(artifacts.Count).And.OnlyContain(state =>
                state.ObjectState == CentralArtifactObjectState.Expired
                && state.StateReasonCode == "retention.expired");
        }
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        foreach (var artifact in artifacts)
        {
            try
            {
                _ = await minio.StatObjectAsync(new StatObjectArgs()
                    .WithBucket(Bucket).WithObject(artifact.ObjectKey)).ConfigureAwait(false);
                Assert.Fail("The released object still exists.");
            }
            catch (MinioException exception) when (ObjectStoreTestClient.IsNotFound(exception))
            {
            }
        }
    }

    private static async Task<MeasuredReleaseBatch> MeasureReleasesAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        IReadOnlyList<SeededRetentionArtifact> artifacts,
        int concurrency,
        string samplerConnectionString,
        string applicationName,
        double maximumMedianSamplingIntervalMilliseconds = 25)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var workingSetBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        using var samplingCancellation = new CancellationTokenSource();
        var workingSetTask = SampleWorkingSetAsync(workingSetBefore, samplingCancellation.Token);
        var sqlSamplesTask = SampleSqlCriticalSectionAsync(
            samplerConnectionString, applicationName, samplingCancellation.Token);
        IReadOnlyList<RetentionOperationSample> samples;
        var started = Stopwatch.GetTimestamp();
        try
        {
            samples = await ExecuteReleasesAsync(factory, artifacts, concurrency).ConfigureAwait(false);
        }
        finally
        {
            await samplingCancellation.CancelAsync().ConfigureAwait(false);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var resources = new ProcessResourceEvidence(
            GC.GetTotalAllocatedBytes(false) - allocatedBefore,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            workingSetBefore,
            process.WorkingSet64,
            await workingSetTask.ConfigureAwait(false));
        return new MeasuredReleaseBatch(
            samples,
            elapsed,
            resources,
            CreateSqlSamplingEvidence(
                await sqlSamplesTask.ConfigureAwait(false), maximumMedianSamplingIntervalMilliseconds));
    }

    private static async Task<long> SampleWorkingSetAsync(long initialWorkingSet, CancellationToken cancellationToken)
    {
        var peak = initialWorkingSet;
        using var process = Process.GetCurrentProcess();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return peak;
    }

    private static RetentionScenarioEvidence CreateScenarioEvidence(
        int concurrency,
        MeasuredReleaseBatch measured,
        Issue246ProtocolSnapshot protocol,
        DatabaseLogUsage logBefore,
        DatabaseLogUsage logAfter)
    {
        var samples = measured.Samples;
        var durations = samples.Select(sample => sample.DurationMilliseconds).Order().ToArray();
        return new RetentionScenarioEvidence(
            concurrency,
            samples,
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            durations[^1],
            measured.WallElapsed.TotalMilliseconds,
            samples.Count / measured.WallElapsed.TotalSeconds,
            measured.Resources,
            LogicalObjectBytesRemoved: (long)samples.Count * PayloadBytes,
            protocol,
            measured.Sql,
            logBefore,
            logAfter);
    }

    private static double Percentile(double[] ordered, double percentile)
        => ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * percentile) - 1)];

    private static async Task<ArtifactLockExecution> ObserveArtifactLockAsync(
        string connectionString,
        string applicationName,
        Guid centralArtifactId,
        int ordinal,
        TaskCompletionSource<int> sessionId)
    {
        var writerConnectionString = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName + ".Writer"
        }.ConnectionString;
        await using var connection = new SqlConnection(writerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.CommandText = "SELECT @@SPID;";
            sessionId.TrySetResult(Convert.ToInt32(
                await sessionCommand.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture));
        }
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = """
            SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
            BEGIN TRANSACTION;
            SELECT CAST(1 AS int) AS [Value]
            FROM [CentralArtifacts] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @id;
            COMMIT TRANSACTION;
            """;
        command.Parameters.AddWithValue("@id", centralArtifactId);
        var timer = Stopwatch.GetTimestamp();
        try
        {
            _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return new ArtifactLockExecution(ordinal, false, Stopwatch.GetElapsedTime(timer).TotalMilliseconds);
        }
        catch (SqlException exception) when (exception.Number is -2 or 1222)
        {
            return new ArtifactLockExecution(ordinal, true, Stopwatch.GetElapsedTime(timer).TotalMilliseconds);
        }
    }

    private static async Task<WriterRequestObservation?> ObserveWriterRequestAsync(
        string connectionString,
        string applicationName,
        Task<int> sessionIdTask,
        Task<ArtifactLockExecution> executionTask)
    {
        var sessionId = await sessionIdTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var timeout = Stopwatch.GetTimestamp();
        while (!executionTask.IsCompleted && Stopwatch.GetElapsedTime(timeout) < TimeSpan.FromSeconds(5))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP(1)
                    request.[blocking_session_id],
                    request.[wait_type],
                    session.[transaction_isolation_level],
                    blocker.[program_name],
                    resource.[resource_type],
                    resource.[request_mode],
                    resource.[request_status],
                    resource.[request_owner_type]
                FROM [sys].[dm_exec_requests] AS request
                INNER JOIN [sys].[dm_exec_sessions] AS session ON session.[session_id] = request.[session_id]
                LEFT JOIN [sys].[dm_exec_sessions] AS blocker ON blocker.[session_id] = request.[blocking_session_id]
                LEFT JOIN [sys].[dm_tran_locks] AS resource
                    ON resource.[request_session_id] = request.[session_id]
                    AND resource.[request_status] = N'WAIT'
                WHERE request.[session_id] = @session_id
                    AND request.[blocking_session_id] <> 0;
                """;
            command.Parameters.AddWithValue("@session_id", sessionId);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                var waitType = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetString(1);
                var blockerProgram = await reader.IsDBNullAsync(3).ConfigureAwait(false) ? null : reader.GetString(3);
                var resourceType = await reader.IsDBNullAsync(4).ConfigureAwait(false) ? null : reader.GetString(4);
                var requestMode = await reader.IsDBNullAsync(5).ConfigureAwait(false) ? null : reader.GetString(5);
                var requestStatus = await reader.IsDBNullAsync(6).ConfigureAwait(false) ? null : reader.GetString(6);
                var requestOwnerType = await reader.IsDBNullAsync(7).ConfigureAwait(false) ? null : reader.GetString(7);
                return new WriterRequestObservation(
                    sessionId,
                    reader.GetInt16(0),
                    waitType,
                    reader.GetInt16(2),
                    string.Equals(blockerProgram, applicationName, StringComparison.Ordinal),
                    resourceType,
                    requestMode,
                    requestStatus,
                    requestOwnerType);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
        }
        if (!executionTask.IsCompleted)
        {
            throw new TimeoutException("The competing writer was neither observed in SQL nor completed within five seconds.");
        }
        return null;
    }

    private static async Task<BlockedWriterEvidence> CreateBlockedWriterEvidenceAsync(
        int ordinal,
        Task<ArtifactLockExecution> executionTask,
        Task<WriterRequestObservation?> observationTask)
    {
        var observation = await observationTask.ConfigureAwait(false);
        var execution = await executionTask.ConfigureAwait(false);
        return new BlockedWriterEvidence(ordinal, execution.TimedOut, execution.ElapsedMilliseconds, observation);
    }

    private static async Task<IReadOnlyList<SqlCriticalSectionSample>> SampleSqlCriticalSectionAsync(
        string connectionString,
        string applicationName,
        CancellationToken cancellationToken)
    {
        var samples = new List<SqlCriticalSectionSample>();
        var started = Stopwatch.GetTimestamp();
        var nextSample = TimeSpan.Zero;
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                samples.Add(await ReadSqlCriticalSectionAsync(
                    connection, applicationName, elapsed.TotalMilliseconds, CancellationToken.None)
                    .ConfigureAwait(false));
                nextSample += TimeSpan.FromMilliseconds(10);
                var remaining = nextSample - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return samples;
    }

    private static async Task<SqlCriticalSectionSample> ReadSqlCriticalSectionAsync(
        SqlConnection connection,
        string applicationName,
        double elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @attributed TABLE ([session_id] smallint PRIMARY KEY);
            INSERT INTO @attributed ([session_id])
            SELECT [session_id]
            FROM [sys].[dm_exec_sessions]
            WHERE [program_name] = @application_name;

            WITH
            [open_transactions] AS
            (
                SELECT DISTINCT [session_id]
                FROM [sys].[dm_tran_session_transactions]
                WHERE [session_id] IN (SELECT [session_id] FROM @attributed)
            ),
            [transaction_log] AS
            (
                SELECT COALESCE(SUM([database_transaction_log_bytes_used]), 0) AS [bytes]
                FROM [sys].[dm_tran_database_transactions] AS [database_transaction]
                INNER JOIN [sys].[dm_tran_session_transactions] AS [session_transaction]
                    ON [session_transaction].[transaction_id] = [database_transaction].[transaction_id]
                WHERE [session_transaction].[session_id] IN (SELECT [session_id] FROM @attributed)
                    AND [database_transaction].[database_id] = DB_ID()
            )
            SELECT
                (SELECT COUNT(*) FROM @attributed),
                (SELECT COUNT(*)
                    FROM [sys].[dm_exec_sessions]
                    WHERE [session_id] IN (SELECT [session_id] FROM @attributed)
                        AND [status] = N'sleeping'),
                (SELECT COUNT(*)
                    FROM [sys].[dm_exec_sessions]
                    WHERE [session_id] IN (SELECT [session_id] FROM @attributed)
                        AND [status] <> N'sleeping'),
                (SELECT COUNT(*)
                    FROM [sys].[dm_exec_requests]
                    WHERE [session_id] IN (SELECT [session_id] FROM @attributed)),
                (SELECT COUNT(*) FROM [open_transactions]),
                (SELECT COUNT(DISTINCT [request_session_id])
                    FROM [sys].[dm_tran_locks]
                    WHERE [request_session_id] IN (SELECT [session_id] FROM @attributed)
                        AND [resource_type] = N'APPLICATION'
                        AND [request_owner_type] = N'SESSION'
                        AND [request_status] = N'GRANT'),
                (SELECT COUNT(*)
                    FROM
                    (
                        SELECT DISTINCT [application_lock].[request_session_id]
                        FROM [sys].[dm_tran_locks] AS [application_lock]
                        INNER JOIN [sys].[dm_tran_session_transactions] AS [session_transaction]
                            ON [session_transaction].[session_id] = [application_lock].[request_session_id]
                        WHERE [application_lock].[request_session_id] IN
                            (SELECT [session_id] FROM @attributed)
                            AND [application_lock].[resource_type] = N'APPLICATION'
                            AND [application_lock].[request_owner_type] = N'SESSION'
                            AND [application_lock].[request_status] = N'GRANT'
                    ) AS [application_lock_transaction]),
                (SELECT COUNT(*)
                    FROM [sys].[dm_exec_requests] AS request
                    INNER JOIN [sys].[dm_exec_sessions] AS session ON session.[session_id] = request.[session_id]
                    WHERE session.[program_name] = @writer_application_name
                        AND [blocking_session_id] <> 0),
                (SELECT [bytes] FROM [transaction_log]);
            """;
        command.Parameters.AddWithValue("@application_name", applicationName);
        command.Parameters.AddWithValue("@writer_application_name", applicationName + ".Writer");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new SqlCriticalSectionSample(
            elapsedMilliseconds,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt64(8));
    }

    private static SqlSamplingEvidence CreateSqlSamplingEvidence(
        IReadOnlyList<SqlCriticalSectionSample> samples,
        double maximumMedianSamplingIntervalMilliseconds = 25)
    {
        var effectiveInterval = CalculateMedianSamplingInterval(samples);
        if (samples.Count >= 2)
        {
            effectiveInterval.Should().BeLessThanOrEqualTo(maximumMedianSamplingIntervalMilliseconds);
        }
        return new SqlSamplingEvidence(
            samples.Count,
            effectiveInterval,
            samples.Count == 0 ? 0 : samples.Max(sample => sample.AttributedSqlSessions),
            samples.Count == 0 ? 0 : samples.Max(sample => sample.SleepingSessions),
            samples.Count == 0 ? 0 : samples.Max(sample => sample.ActiveSessions),
            samples.Count == 0 ? 0 : samples.Max(sample => sample.ActiveRequests),
            samples.Count == 0 ? 0 : samples.Max(sample => sample.OpenTransactionSessions),
            samples.Count == 0 ? 0 : samples.Max(sample => sample.SessionApplicationLocks),
            samples.Count == 0 ? 0 : samples.Max(sample => sample.ApplicationLockSessionsWithOpenTransactions),
            samples.Count == 0 ? 0 : samples.Max(sample => sample.BlockedRequests),
            samples.Count == 0 ? 0 : samples.Max(sample => sample.ActiveTransactionLogBytes),
            CalculateApplicationLockWindow(samples),
            samples);
    }

    private static double CalculateMedianSamplingInterval(IReadOnlyList<SqlCriticalSectionSample> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }
        var intervals = samples.Zip(samples.Skip(1),
                (previous, current) => current.ElapsedMilliseconds - previous.ElapsedMilliseconds)
            .Order()
            .ToArray();
        return Percentile(intervals, 0.50);
    }

    private static double CalculateApplicationLockWindow(IReadOnlyList<SqlCriticalSectionSample> samples)
    {
        var locked = samples.Where(sample => sample.SessionApplicationLocks > 0).ToArray();
        if (locked.Length == 0)
        {
            return 0;
        }
        return locked[^1].ElapsedMilliseconds - locked[0].ElapsedMilliseconds
            + CalculateMedianSamplingInterval(samples);
    }

    private static async Task<DatabaseLogUsage> ReadDatabaseLogUsageAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [total_log_size_in_bytes], [used_log_space_in_bytes], [used_log_space_in_percent]
            FROM [sys].[dm_db_log_space_usage];
            """;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new DatabaseLogUsage(reader.GetInt64(0), reader.GetInt64(1), reader.GetFloat(2));
    }

    private static async Task TryWriteFiveTrialSummaryAsync(
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
        var evidencePaths = Enumerable.Range(1, 5)
            .Select(trial => Path.Combine(sourceDirectory, $"trial-{trial}", "central-artifact-retention-evidence.json"))
            .ToArray();
        var manifestPaths = Enumerable.Range(1, 5)
            .Select(trial => Path.Combine(sourceDirectory, $"trial-{trial}", "manifest.json"))
            .ToArray();
        if (evidencePaths.Any(path => !File.Exists(path)) || manifestPaths.Any(path => !File.Exists(path)))
        {
            return;
        }

        var inputs = new List<TrialEvidenceInput>();
        for (var index = 0; index < evidencePaths.Length; index++)
        {
            var evidenceBytes = await File.ReadAllBytesAsync(evidencePaths[index]).ConfigureAwait(false);
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPaths[index]).ConfigureAwait(false));
            var fileRecord = manifest.RootElement.GetProperty("Files").EnumerateArray().Single();
            fileRecord.GetProperty("ByteLength").GetInt64().Should().Be(evidenceBytes.LongLength);
            fileRecord.GetProperty("Sha256").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(evidenceBytes)));
            using var evidence = JsonDocument.Parse(evidenceBytes);
            var root = evidence.RootElement;
            root.GetProperty("Schema").GetString().Should().Be("hvo-issue-246-central-artifact-retention-evidence-v1");
            root.GetProperty("Phase").GetString().Should().Be(phase);
            root.GetProperty("ProductionRevision").GetString().Should().Be(productionRevision);
            root.GetProperty("HarnessSha256").GetString().Should().Be(harnessSha256);
            root.GetProperty("Source").GetProperty("Head").GetString().Should().Be(source.Head);
            root.GetProperty("Source").GetProperty("Trial").GetInt32().Should().Be(index + 1);
            root.GetProperty("Source").GetProperty("Claimability").GetString()
                .Should().Be(phase == "development"
                    ? "dirty-development-not-claimable"
                    : "clean-source-attributed-review-required");
            inputs.Add(TrialEvidenceInput.Create(index + 1, root));
        }
        inputs.Select(input => input.EnvironmentFingerprintSha256).Distinct(StringComparer.Ordinal)
            .Should().ContainSingle();
        inputs.Select(input => input.WorkloadSha256).Distinct(StringComparer.Ordinal).Should().ContainSingle();

        var performance = ConcurrencyLevels.Select(concurrency =>
        {
            var trials = inputs.Select(input => input.Performance.Single(item => item.Concurrency == concurrency)).ToArray();
            return new
            {
                Concurrency = concurrency,
                Trials = trials,
                P50Milliseconds = Summarize(trials.Select(trial => trial.P50Milliseconds)),
                P95Milliseconds = Summarize(trials.Select(trial => trial.P95Milliseconds)),
                MaximumMilliseconds = Summarize(trials.Select(trial => trial.MaximumMilliseconds)),
                OperationsPerSecond = Summarize(trials.Select(trial => trial.OperationsPerSecond)),
                AllocatedBytes = Summarize(trials.Select(trial => (double)trial.AllocatedBytes)),
                ProcessCpuMilliseconds = Summarize(trials.Select(trial => trial.ProcessCpuMilliseconds)),
                PeakObservedWorkingSetBytes = Summarize(trials.Select(trial => (double)trial.PeakObservedWorkingSetBytes)),
                PeakWorkingSetAboveStartBytes = Summarize(
                    trials.Select(trial => (double)trial.PeakWorkingSetAboveStartBytes)),
                WorkingSetEndMinusStartBytes = Summarize(
                    trials.Select(trial => (double)trial.WorkingSetEndMinusStartBytes)),
                EfCommands = Summarize(trials.Select(trial => (double)trial.EfCommands)),
                TransactionsSuccessfullyStarted = Summarize(
                    trials.Select(trial => (double)trial.TransactionsSuccessfullyStarted)),
                TransactionsCommitted = Summarize(trials.Select(trial => (double)trial.TransactionsCommitted)),
                TransactionsRolledBack = Summarize(trials.Select(trial => (double)trial.TransactionsRolledBack)),
                TransactionsFailed = Summarize(trials.Select(trial => (double)trial.TransactionsFailed)),
                TransactionP95Milliseconds = Summarize(trials.Select(trial => trial.TransactionP95Milliseconds)),
                MinioRequests = Summarize(trials.Select(trial => (double)trial.MinioRequests)),
                MinioDeletes = Summarize(trials.Select(trial => (double)trial.MinioDeletes)),
                RequestEntityBytes = Summarize(trials.Select(trial => (double)trial.RequestEntityBytes)),
                ResponseEntityBytes = Summarize(trials.Select(trial => (double)trial.ResponseEntityBytes)),
                UnknownEntityLengths = Summarize(trials.Select(trial => (double)trial.UnknownEntityLengths)),
                LogicalObjectBytesRemoved = Summarize(
                    trials.Select(trial => (double)trial.LogicalObjectBytesRemoved)),
                GlobalDatabaseLogUsedDeltaBytes = Summarize(
                    trials.Select(trial => (double)trial.GlobalDatabaseLogUsedDeltaBytes)),
                MinioDeleteP50Milliseconds = Summarize(trials.Select(trial => trial.MinioDeleteP50Milliseconds)),
                MinioDeleteP95Milliseconds = Summarize(trials.Select(trial => trial.MinioDeleteP95Milliseconds)),
                PeakAttributedSqlSessions = Summarize(
                    trials.Select(trial => (double)trial.PeakAttributedSqlSessions)),
                PeakActiveRequests = Summarize(trials.Select(trial => (double)trial.PeakActiveRequests)),
                PeakOpenTransactionSessions = Summarize(
                    trials.Select(trial => (double)trial.PeakOpenTransactionSessions)),
                PeakSessionApplicationLocks = Summarize(
                    trials.Select(trial => (double)trial.PeakSessionApplicationLocks)),
                PeakBlockedRequests = Summarize(trials.Select(trial => (double)trial.PeakBlockedRequests)),
                PeakActiveTransactionLogBytes = Summarize(
                    trials.Select(trial => (double)trial.PeakActiveTransactionLogBytes)),
                EffectiveSamplingIntervalMilliseconds = Summarize(
                    trials.Select(trial => trial.EffectiveSamplingIntervalMilliseconds)),
                ObservedBatchApplicationLockOccupancyMilliseconds = Summarize(
                    trials.Select(trial => trial.ObservedBatchApplicationLockOccupancyMilliseconds))
            };
        }).ToArray();
        var delays = DeleteDelaysMilliseconds.Select(delay =>
        {
            var trials = inputs.Select(input => input.Delays.Single(item => item.DelayMilliseconds == delay)).ToArray();
            return new
            {
                DelayMilliseconds = delay,
                Trials = trials,
                WriterP50Milliseconds = Summarize(trials.Select(trial => trial.WriterP50Milliseconds)),
                OperationP50Milliseconds = Summarize(trials.Select(trial => trial.OperationP50Milliseconds)),
                TransactionP50Milliseconds = Summarize(trials.Select(trial => trial.TransactionP50Milliseconds)),
                ObservedBatchApplicationLockOccupancyMilliseconds = Summarize(
                    trials.Select(trial => trial.ObservedBatchApplicationLockOccupancyMilliseconds)),
                PeakOpenTransactionSessions = Summarize(
                    trials.Select(trial => (double)trial.PeakOpenTransactionSessions)),
                PeakSessionApplicationLocks = Summarize(
                    trials.Select(trial => (double)trial.PeakSessionApplicationLocks)),
                PeakAttributedSqlSessions = Summarize(
                    trials.Select(trial => (double)trial.PeakAttributedSqlSessions)),
                PeakBlockedRequests = Summarize(trials.Select(trial => (double)trial.PeakBlockedRequests)),
                PeakActiveTransactionLogBytes = Summarize(
                    trials.Select(trial => (double)trial.PeakActiveTransactionLogBytes)),
                EffectiveSamplingIntervalMilliseconds = Summarize(
                    trials.Select(trial => trial.EffectiveSamplingIntervalMilliseconds)),
                EfCommands = Summarize(trials.Select(trial => (double)trial.EfCommands)),
                TransactionsCommitted = Summarize(trials.Select(trial => (double)trial.TransactionsCommitted)),
                TransactionsRolledBackOrFailed = Summarize(
                    trials.Select(trial => (double)trial.TransactionsRolledBackOrFailed)),
                MinioDeletes = Summarize(trials.Select(trial => (double)trial.MinioDeletes)),
                LogicalObjectBytesRemoved = Summarize(
                    trials.Select(trial => (double)trial.LogicalObjectBytesRemoved)),
                AttributedBlockedWriterObservations = Summarize(
                    trials.Select(trial => (double)trial.AttributedBlockedWriterObservations)),
                PeakActiveRequests = Summarize(trials.Select(trial => (double)trial.PeakActiveRequests)),
                MinioDeleteP50Milliseconds = Summarize(trials.Select(trial => trial.MinioDeleteP50Milliseconds)),
                MinioDeleteP95Milliseconds = Summarize(trials.Select(trial => trial.MinioDeleteP95Milliseconds))
            };
        }).ToArray();
        var summary = new
        {
            Schema = "hvo-issue-246-five-trial-summary-v1",
            Issue = 246,
            Phase = phase,
            SourceHead = source.Head,
            ProductionRevision = productionRevision,
            HarnessSha256 = harnessSha256,
            EnvironmentFingerprintSha256 = inputs[0].EnvironmentFingerprintSha256,
            WorkloadSha256 = inputs[0].WorkloadSha256,
            TrialCount = inputs.Count,
            Performance = performance,
            DelayedDelete = delays,
            BaselineBacklog = phase == "baseline"
                ? "N/A: current production has no durable retention-deletion backlog; candidate after/fault evidence records pending count, bytes, oldest age and drain."
                : "Recorded by candidate fault/runtime evidence; this performance summary covers the synchronous request workload.",
            Method = "Five independent end-to-end trials; each aggregate reports minimum, median and maximum. Per-operation p95 comes only from each trial's 30 measured operations.",
            Correctness = "All trial manifests, source/phase/harness/environment/workload identities and exact SQL/object convergence were verified before aggregation.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        var summaryPath = Path.Combine(sourceDirectory, $"{phase}-five-trial-summary.json");
        await EvidenceSourceIdentity.WriteJsonAsync(summaryPath, summary, EvidenceJsonOptions).ConfigureAwait(false);
        string? comparisonPath = null;
        if (phase == "after")
        {
            var baselineSummaryPath = Environment.GetEnvironmentVariable("HVO_EVIDENCE_BASELINE_SUMMARY");
            if (string.IsNullOrWhiteSpace(baselineSummaryPath) || !File.Exists(baselineSummaryPath))
            {
                throw new InvalidOperationException(
                    "After trial aggregation requires HVO_EVIDENCE_BASELINE_SUMMARY pointing to the reviewed baseline summary.");
            }
            comparisonPath = Path.Combine(sourceDirectory, "baseline-after-comparison.json");
            await WriteBaselineAfterComparisonAsync(
                baselineSummaryPath, summaryPath, comparisonPath).ConfigureAwait(false);
        }
        var retainedPaths = evidencePaths.Concat(manifestPaths).Append(summaryPath);
        if (comparisonPath is not null)
        {
            retainedPaths = retainedPaths.Append(comparisonPath);
        }
        var retainedFiles = retainedPaths
            .Order(StringComparer.Ordinal)
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
            new { Schema = "hvo-issue-246-five-trial-manifest-v1", Source = source, Files = retainedFiles },
            EvidenceJsonOptions).ConfigureAwait(false);
    }

    private static async Task WriteBaselineAfterComparisonAsync(
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
        baselineRoot.GetProperty("Phase").GetString().Should().Be("baseline");
        afterRoot.GetProperty("Phase").GetString().Should().Be("after");
        foreach (var identity in new[] { "HarnessSha256", "EnvironmentFingerprintSha256", "WorkloadSha256" })
        {
            afterRoot.GetProperty(identity).GetString().Should().Be(baselineRoot.GetProperty(identity).GetString());
        }

        var comparisons = new List<ComparisonMetric>();
        var performanceMetrics = new (string Name, bool HigherIsRegression)[]
        {
            ("P50Milliseconds", true),
            ("P95Milliseconds", true),
            ("MaximumMilliseconds", true),
            ("OperationsPerSecond", false),
            ("AllocatedBytes", true),
            ("ProcessCpuMilliseconds", true),
            ("PeakObservedWorkingSetBytes", true),
            ("PeakWorkingSetAboveStartBytes", true),
            ("WorkingSetEndMinusStartBytes", true),
            ("EfCommands", true),
            ("TransactionsSuccessfullyStarted", true),
            ("TransactionsCommitted", true),
            ("TransactionsRolledBack", true),
            ("TransactionsFailed", true),
            ("TransactionP95Milliseconds", true),
            ("MinioRequests", true),
            ("MinioDeletes", true),
            ("RequestEntityBytes", true),
            ("ResponseEntityBytes", true),
            ("UnknownEntityLengths", true),
            ("LogicalObjectBytesRemoved", true),
            ("MinioDeleteP50Milliseconds", true),
            ("MinioDeleteP95Milliseconds", true),
            ("PeakAttributedSqlSessions", true),
            ("PeakActiveRequests", true),
            ("PeakOpenTransactionSessions", true),
            ("PeakSessionApplicationLocks", true),
            ("PeakBlockedRequests", true),
            ("PeakActiveTransactionLogBytes", true),
            ("EffectiveSamplingIntervalMilliseconds", true),
            ("ObservedBatchApplicationLockOccupancyMilliseconds", true)
        };
        foreach (var concurrency in ConcurrencyLevels)
        {
            var baselineScenario = FindScenario(baselineRoot.GetProperty("Performance"), "Concurrency", concurrency);
            var afterScenario = FindScenario(afterRoot.GetProperty("Performance"), "Concurrency", concurrency);
            foreach (var metric in performanceMetrics)
            {
                comparisons.Add(CreateComparison(
                    $"C{concurrency}.{metric.Name}",
                    baselineScenario.GetProperty(metric.Name),
                    afterScenario.GetProperty(metric.Name),
                    metric.HigherIsRegression));
            }
        }
        var delayMetrics = new (string Name, bool HigherIsRegression)[]
        {
            ("WriterP50Milliseconds", true),
            ("OperationP50Milliseconds", true),
            ("TransactionP50Milliseconds", true),
            ("ObservedBatchApplicationLockOccupancyMilliseconds", true),
            ("PeakOpenTransactionSessions", true),
            ("PeakSessionApplicationLocks", true),
            ("PeakAttributedSqlSessions", true),
            ("PeakActiveRequests", true),
            ("PeakBlockedRequests", true),
            ("PeakActiveTransactionLogBytes", true),
            ("EffectiveSamplingIntervalMilliseconds", true),
            ("EfCommands", true),
            ("TransactionsCommitted", true),
            ("TransactionsRolledBackOrFailed", true),
            ("MinioDeletes", true),
            ("LogicalObjectBytesRemoved", true),
            ("MinioDeleteP50Milliseconds", true),
            ("MinioDeleteP95Milliseconds", true)
        };
        foreach (var delay in DeleteDelaysMilliseconds)
        {
            var baselineScenario = FindScenario(baselineRoot.GetProperty("DelayedDelete"), "DelayMilliseconds", delay);
            var afterScenario = FindScenario(afterRoot.GetProperty("DelayedDelete"), "DelayMilliseconds", delay);
            foreach (var metric in delayMetrics)
            {
                comparisons.Add(CreateComparison(
                    $"Delay{delay}.{metric.Name}",
                    baselineScenario.GetProperty(metric.Name),
                    afterScenario.GetProperty(metric.Name),
                    metric.HigherIsRegression));
            }
        }

        var correctnessFailures = new List<string>();
        foreach (var scenario in afterRoot.GetProperty("Performance").EnumerateArray())
        {
            RequireMedian(scenario, "TransactionsRolledBack", 0, correctnessFailures);
            RequireMedian(scenario, "TransactionsFailed", 0, correctnessFailures);
            RequireMedian(scenario, "UnknownEntityLengths", 0, correctnessFailures);
            RequireMedian(scenario, "MinioDeletes", MeasuredOperations, correctnessFailures);
            RequireMedian(scenario, "LogicalObjectBytesRemoved", (long)MeasuredOperations * PayloadBytes, correctnessFailures);
        }
        foreach (var scenario in afterRoot.GetProperty("DelayedDelete").EnumerateArray())
        {
            RequireMedian(scenario, "TransactionsRolledBackOrFailed", 0, correctnessFailures);
            RequireMedian(scenario, "MinioDeletes", DelayedOperations, correctnessFailures);
            RequireMedian(scenario, "LogicalObjectBytesRemoved", (long)DelayedOperations * PayloadBytes, correctnessFailures);
            RequireMedian(scenario, "AttributedBlockedWriterObservations", 0, correctnessFailures);
        }
        var materialRegressions = comparisons.Where(comparison => comparison.MaterialRegression)
            .Select(comparison => comparison.Name).ToArray();
        var comparison = new
        {
            Schema = "hvo-issue-246-baseline-after-comparison-v1",
            Issue = 246,
            BaselineSummarySha256 = Convert.ToHexString(SHA256.HashData(baselineBytes)),
            AfterSummarySha256 = Convert.ToHexString(SHA256.HashData(afterBytes)),
            Identity = new
            {
                HarnessSha256 = afterRoot.GetProperty("HarnessSha256").GetString(),
                EnvironmentFingerprintSha256 = afterRoot.GetProperty("EnvironmentFingerprintSha256").GetString(),
                WorkloadSha256 = afterRoot.GetProperty("WorkloadSha256").GetString()
            },
            Metrics = comparisons,
            CorrectnessFailures = correctnessFailures,
            MaterialRegressions = materialRegressions,
            ExcludedComparisons = new[]
            {
                "GlobalDatabaseLogUsedDeltaBytes: global point-in-time capacity/reuse sample is retained but not attributable to one workload."
            },
            Result = correctnessFailures.Count > 0
                ? "failed-correctness"
                : materialRegressions.Length > 0
                    ? "review-required-material-regression"
                    : "passed-no-material-regression",
            Rule = "Material when the regression exceeds max(20%, 2 * baseline five-trial (max-min)/median range). Throughput regresses downward; latency/resource/SQL metrics regress upward.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        await EvidenceSourceIdentity.WriteJsonAsync(outputPath, comparison, EvidenceJsonOptions).ConfigureAwait(false);
    }

    private static JsonElement FindScenario(JsonElement scenarios, string selector, int value)
        => scenarios.EnumerateArray().Single(item => item.GetProperty(selector).GetInt32() == value);

    private static ComparisonMetric CreateComparison(
        string name,
        JsonElement baselineAggregate,
        JsonElement afterAggregate,
        bool higherIsRegression)
    {
        var baselineMinimum = baselineAggregate.GetProperty("Minimum").GetDouble();
        var baselineMedian = baselineAggregate.GetProperty("Median").GetDouble();
        var baselineMaximum = baselineAggregate.GetProperty("Maximum").GetDouble();
        var afterMedian = afterAggregate.GetProperty("Median").GetDouble();
        var absoluteChange = afterMedian - baselineMedian;
        double? relativeChange = baselineMedian == 0 ? null : absoluteChange / Math.Abs(baselineMedian);
        var noiseFraction = baselineMedian == 0
            ? 0
            : 2 * Math.Abs(baselineMaximum - baselineMinimum) / Math.Abs(baselineMedian);
        var materialThreshold = Math.Max(0.20, noiseFraction);
        var regressionFraction = relativeChange is null
            ? afterMedian == baselineMedian ? 0 : double.PositiveInfinity
            : higherIsRegression ? relativeChange.Value : -relativeChange.Value;
        return new ComparisonMetric(
            name,
            baselineMedian,
            afterMedian,
            absoluteChange,
            relativeChange,
            materialThreshold,
            higherIsRegression ? "higher" : "lower",
            regressionFraction > materialThreshold);
    }

    private static void RequireMedian(
        JsonElement scenario,
        string metric,
        double expected,
        List<string> failures)
    {
        var observed = scenario.GetProperty(metric).GetProperty("Median").GetDouble();
        if (observed != expected)
        {
            failures.Add($"{metric}: expected {expected.ToString(CultureInfo.InvariantCulture)}, observed {observed.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static AggregateMetric Summarize(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        ordered.Should().HaveCount(5);
        return new AggregateMetric(ordered[0], Percentile(ordered, 0.50), ordered[^1]);
    }

    private static async Task ValidateEvidenceIdentityAsync(
        string repositoryRoot,
        string phase,
        string productionRevision,
        EvidenceSourceSnapshot source,
        IReadOnlyCollection<string> changedPaths)
    {
        if (phase == "development")
        {
            return;
        }
        if (source.Dirty || source.RequestedRevision is null || source.Trial is null
            || !string.Equals(source.Claimability, "clean-source-attributed-review-required", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Baseline/after evidence requires a clean requested revision and HVO_EVIDENCE_TRIAL=1..5.");
        }
        if (!string.Equals(
                Environment.GetEnvironmentVariable("HVO_ISSUE_246_RETENTION_EVIDENCE"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Baseline/after evidence requires HVO_ISSUE_246_RETENTION_EVIDENCE=1 before test-host startup.");
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HVO_EVIDENCE_PRODUCTION_REVISION")))
        {
            throw new InvalidOperationException(
                "Baseline/after evidence requires explicit HVO_EVIDENCE_PRODUCTION_REVISION.");
        }
        if (!await IsAncestorAsync(repositoryRoot, productionRevision, source.Head).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The production revision must be an ancestor of evidence HEAD.");
        }
        if (phase == "baseline")
        {
            if (!string.Equals(productionRevision, BaselineRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Baseline evidence must use the pinned merged #243 production revision.");
            }
            var permitted = new HashSet<string>(StringComparer.Ordinal)
            {
                "docs/runbooks/ci-pipeline.md",
                "scripts/test-categories/Program.cs",
                "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/CentralArtifactRetentionPerformanceTests.cs",
                "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/Infrastructure/Issue246RetentionEvidenceCollector.cs",
                "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/AssemblyHooks.cs",
                "tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/LogicHostIntegrationFixture.cs"
            };
            if (changedPaths.Any(path => !permitted.Contains(path)))
            {
                throw new InvalidOperationException(
                    "Baseline evidence HEAD contains a committed change outside the reviewed harness envelope.");
            }
        }
        else if (!string.Equals(productionRevision, source.Head, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("After evidence production revision must be evidence HEAD.");
        }
    }

    private static async Task<bool> IsAncestorAsync(string repositoryRoot, string ancestor, string descendant)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("merge-base");
        startInfo.ArgumentList.Add("--is-ancestor");
        startInfo.ArgumentList.Add(ancestor);
        startInfo.ArgumentList.Add(descendant);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start git ancestry validation.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode is not (0 or 1))
        {
            throw new InvalidOperationException(
                $"git merge-base --is-ancestor failed: {await process.StandardError.ReadToEndAsync().ConfigureAwait(false)}");
        }
        return process.ExitCode == 0;
    }

    private static async Task<Issue246EnvironmentFacts> CaptureEnvironmentAsync(string repositoryRoot)
    {
        var globalJson = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "global.json")).ConfigureAwait(false));
        var pinnedSdk = globalJson.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidOperationException("global.json has no SDK version.");
        var executingSdk = (await RunProcessAsync(repositoryRoot, "dotnet", "--version").ConfigureAwait(false)).Trim();
        var dockerVersion = (await RunProcessAsync(
            repositoryRoot, "docker", "version", "--format", "{{.Server.Version}}").ConfigureAwait(false)).Trim();
        var cpuModel = ReadProcValue("/proc/cpuinfo", "model name") ?? "unknown";
        var memoryKilobytes = long.Parse(
            (ReadProcValue("/proc/meminfo", "MemTotal") ?? throw new InvalidOperationException("MemTotal is unavailable."))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
            CultureInfo.InvariantCulture);
        var fingerprintValue = string.Join('|',
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC,
            "Release",
            cpuModel,
            memoryKilobytes,
            pinnedSdk,
            executingSdk,
            dockerVersion,
            IntegrationTestFixture.SqlServerImage,
            IntegrationTestFixture.ExternalS3ImageLabel);
        return new Issue246EnvironmentFacts(
            cpuModel,
            checked(memoryKilobytes * 1024),
            pinnedSdk,
            executingSdk,
            dockerVersion,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintValue))));
    }

    private static string? ReadProcValue(string path, string key)
    {
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator >= 0 && string.Equals(line[..separator].Trim(), key, StringComparison.Ordinal))
            {
                return line[(separator + 1)..].Trim();
            }
        }
        return null;
    }

    private static string ComputeHarnessSha256(string repositoryRoot)
    {
        var paths = new[]
        {
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/CentralArtifactRetentionPerformanceTests.cs",
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/Infrastructure/Issue246RetentionEvidenceCollector.cs",
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/AssemblyHooks.cs",
            "tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/LogicHostIntegrationFixture.cs",
            "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj",
            "tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/HVO.SkyMonitor.LogicHost.TestInfrastructure.csproj",
            "src/HVO.SkyMonitor.TestSupport/EvidenceSourceIdentity.cs",
            "src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj",
            "Directory.Build.props",
            "Directory.Packages.props",
            "global.json"
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in paths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AssertNoForbiddenEvidenceValues(
        byte[] evidenceBytes,
        string repositoryRoot,
        string connectionString,
        IEnumerable<string> keys,
        IEnumerable<Guid> entityIds)
    {
        var json = Encoding.UTF8.GetString(evidenceBytes);
        var keyValues = keys.ToArray();
        var forbidden = keyValues
            .Concat(keyValues.Select(key => $"object://{Bucket}/{key}"))
            .Concat(entityIds.SelectMany(id => new[] { id.ToString("D"), id.ToString("N") }))
            .Concat([
                IntegrationTestFixture.ExternalS3AccessKey,
                IntegrationTestFixture.ExternalS3SecretKey,
                connectionString,
                new SqlConnectionStringBuilder(connectionString).Password,
                repositoryRoot
            ])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        forbidden.Should().OnlyContain(value => !json.Contains(value, StringComparison.Ordinal));
    }

    private static async Task<string> RunProcessAsync(
        string workingDirectory,
        string fileName,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {fileName} for issue #246 evidence.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error}");
        }
        return output;
    }

    private static byte[] CreatePayload()
    {
        var payload = new byte[PayloadBytes];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(((long)index * 31 + 29) % 251);
        }
        return payload;
    }

    private static string ReadEvidencePhase()
    {
        var phase = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PHASE");
        if (string.IsNullOrWhiteSpace(phase))
        {
            return "development";
        }
        if (phase is not ("baseline" or "after"))
        {
            throw new InvalidOperationException("HVO_EVIDENCE_PHASE must be 'baseline' or 'after'.");
        }
        return phase;
    }

    private static async Task<string> ResolveProductionRevisionAsync(string repositoryRoot)
    {
        var requested = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PRODUCTION_REVISION");
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = BaselineRevision;
        }
        return (await RunGitAsync(repositoryRoot, "rev-parse", $"{requested}^{{commit}}")
            .ConfigureAwait(false)).Trim();
    }

    private static async Task<string[]> ReadChangedPathsAsync(string repositoryRoot, string productionRevision)
        => (await RunGitAsync(repositoryRoot, "diff", "--name-only", $"{productionRevision}..HEAD", "--")
            .ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start git for issue #246 evidence.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error}");
        }
        return output;
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

    private sealed record SeededRetentionArtifact(
        Guid CentralArtifactId,
        string ObjectKey,
        CentralArtifact Artifact);

    private sealed record RetentionOperationSample(int Ordinal, double DurationMilliseconds, string Outcome);

    private sealed record RetentionScenarioEvidence(
        int Concurrency,
        IReadOnlyList<RetentionOperationSample> Samples,
        double P50Milliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double WallMilliseconds,
        double OperationsPerSecond,
        ProcessResourceEvidence Resources,
        long LogicalObjectBytesRemoved,
        Issue246ProtocolSnapshot Protocol,
        SqlSamplingEvidence Sql,
        DatabaseLogUsage DatabaseGlobalLogCapacityBefore,
        DatabaseLogUsage DatabaseGlobalLogCapacityAfter);

    private sealed record MeasuredReleaseBatch(
        IReadOnlyList<RetentionOperationSample> Samples,
        TimeSpan WallElapsed,
        ProcessResourceEvidence Resources,
        SqlSamplingEvidence Sql);

    private sealed record ProcessResourceEvidence(
        long AllocatedBytes,
        double ProcessCpuMilliseconds,
        long WorkingSetStartBytes,
        long WorkingSetEndBytes,
        long PeakObservedWorkingSetBytes);

    private sealed record DelayedDeleteEvidence(
        int ConfiguredDelayMilliseconds,
        IReadOnlyList<RetentionOperationSample> Operations,
        IReadOnlyList<BlockedWriterEvidence> BlockedWriters,
        SqlSamplingEvidence Sql,
        Issue246ProtocolSnapshot Protocol,
        long LogicalObjectBytesRemoved);

    private sealed record ArtifactLockExecution(int Ordinal, bool TimedOut, double ElapsedMilliseconds);

    private sealed record BlockedWriterEvidence(
        int Ordinal,
        bool TimedOut,
        double ElapsedMilliseconds,
        WriterRequestObservation? SqlObservation);

    private sealed record WriterRequestObservation(
        int WriterSessionId,
        int BlockingSessionId,
        string? WaitType,
        short TransactionIsolationLevel,
        bool BlockerIsAttributedRetentionSession,
        string? WaitingResourceType,
        string? WaitingRequestMode,
        string? WaitingRequestStatus,
        string? WaitingRequestOwnerType);

    private sealed record SqlCriticalSectionSample(
        double ElapsedMilliseconds,
        int AttributedSqlSessions,
        int SleepingSessions,
        int ActiveSessions,
        int ActiveRequests,
        int OpenTransactionSessions,
        int SessionApplicationLocks,
        int ApplicationLockSessionsWithOpenTransactions,
        int BlockedRequests,
        long ActiveTransactionLogBytes);

    private sealed record SqlSamplingEvidence(
        int SampleCount,
        double EffectiveMedianIntervalMilliseconds,
        int PeakAttributedSqlSessions,
        int PeakSleepingSessions,
        int PeakActiveSessions,
        int PeakActiveRequests,
        int PeakOpenTransactionSessions,
        int PeakSessionApplicationLocks,
        int PeakApplicationLockSessionsWithOpenTransactions,
        int PeakBlockedRequests,
        long PeakActiveTransactionLogBytes,
        double ObservedBatchApplicationLockOccupancyMilliseconds,
        IReadOnlyList<SqlCriticalSectionSample> Samples);

    private sealed record DatabaseLogUsage(
        long TotalLogSizeBytes,
        long UsedLogSpaceBytes,
        double UsedLogSpacePercent);

    private sealed record Issue246EnvironmentFacts(
        string CpuModel,
        long TotalMemoryBytes,
        string PinnedSdkVersion,
        string ExecutingSdkVersion,
        string DockerServerVersion,
        string EnvironmentFingerprintSha256);

    private sealed record TrialEvidenceInput(
        int Trial,
        string EnvironmentFingerprintSha256,
        string WorkloadSha256,
        IReadOnlyList<TrialPerformanceMetric> Performance,
        IReadOnlyList<TrialDelayMetric> Delays)
    {
        internal static TrialEvidenceInput Create(int trial, JsonElement root)
        {
            var phase = root.GetProperty("Phase").GetString();
            var performance = root.GetProperty(nameof(Performance)).EnumerateArray().Select(item =>
            {
                var resources = item.GetProperty("Resources");
                var samples = item.GetProperty("Samples").EnumerateArray().ToArray();
                samples.Should().HaveCount(MeasuredOperations).And.OnlyContain(sample =>
                    sample.GetProperty("Outcome").GetString() == CentralArtifactRetentionResult.Released.ToString());
                var protocol = item.GetProperty("Protocol");
                var transactions = protocol.GetProperty("SqlTransactions");
                var objectStore = protocol.GetProperty("ObjectStore");
                var transactionDurations = transactions.GetProperty("CommittedDurationMilliseconds")
                    .EnumerateArray().Select(duration => duration.GetDouble()).Order().ToArray();
                var deleteDurations = objectStore.GetProperty("DeleteDurationMilliseconds")
                    .EnumerateArray().Select(duration => duration.GetDouble()).Order().ToArray();
                var sql = item.GetProperty("Sql");
                transactions.GetProperty("Committed").GetInt64().Should().BeGreaterThanOrEqualTo(MeasuredOperations);
                transactions.GetProperty("RolledBack").GetInt64().Should().Be(0);
                transactions.GetProperty("Failed").GetInt64().Should().Be(0);
                objectStore.GetProperty("Deletes").GetInt64().Should().Be(MeasuredOperations);
                var logBefore = item.GetProperty("DatabaseGlobalLogCapacityBefore");
                var logAfter = item.GetProperty("DatabaseGlobalLogCapacityAfter");
                return new TrialPerformanceMetric(
                    trial,
                    item.GetProperty("Concurrency").GetInt32(),
                    item.GetProperty("P50Milliseconds").GetDouble(),
                    item.GetProperty("P95Milliseconds").GetDouble(),
                    item.GetProperty("MaximumMilliseconds").GetDouble(),
                    item.GetProperty("OperationsPerSecond").GetDouble(),
                    resources.GetProperty("AllocatedBytes").GetInt64(),
                    resources.GetProperty("ProcessCpuMilliseconds").GetDouble(),
                    resources.GetProperty("PeakObservedWorkingSetBytes").GetInt64(),
                    resources.GetProperty("PeakObservedWorkingSetBytes").GetInt64()
                        - resources.GetProperty("WorkingSetStartBytes").GetInt64(),
                    resources.GetProperty("WorkingSetEndBytes").GetInt64()
                        - resources.GetProperty("WorkingSetStartBytes").GetInt64(),
                    protocol.GetProperty("EfCommands").GetInt64(),
                    transactions.GetProperty("SuccessfullyStarted").GetInt64(),
                    transactions.GetProperty("Committed").GetInt64(),
                    transactions.GetProperty("RolledBack").GetInt64(),
                    transactions.GetProperty("Failed").GetInt64(),
                    Percentile(transactionDurations, 0.95),
                    objectStore.GetProperty("Requests").GetInt64(),
                    objectStore.GetProperty("Deletes").GetInt64(),
                    objectStore.GetProperty("RequestEntityBytes").GetInt64(),
                    objectStore.GetProperty("ResponseEntityBytes").GetInt64(),
                    objectStore.GetProperty("UnknownRequestEntityLengths").GetInt64()
                        + objectStore.GetProperty("UnknownResponseEntityLengths").GetInt64(),
                    item.GetProperty("LogicalObjectBytesRemoved").GetInt64(),
                    logAfter.GetProperty("UsedLogSpaceBytes").GetInt64()
                        - logBefore.GetProperty("UsedLogSpaceBytes").GetInt64(),
                    Percentile(deleteDurations, 0.50),
                    Percentile(deleteDurations, 0.95),
                    sql.GetProperty("PeakAttributedSqlSessions").GetInt32(),
                    sql.GetProperty("PeakActiveRequests").GetInt32(),
                    sql.GetProperty("PeakOpenTransactionSessions").GetInt32(),
                    sql.GetProperty("PeakSessionApplicationLocks").GetInt32(),
                    sql.GetProperty("PeakBlockedRequests").GetInt32(),
                    sql.GetProperty("PeakActiveTransactionLogBytes").GetInt64(),
                    sql.GetProperty("EffectiveMedianIntervalMilliseconds").GetDouble(),
                    sql.GetProperty("ObservedBatchApplicationLockOccupancyMilliseconds").GetDouble());
            }).ToArray();
            performance.Should().HaveCount(ConcurrencyLevels.Length);
            var delays = root.GetProperty("DelayedDelete").EnumerateArray().Select(item =>
            {
                var writers = item.GetProperty("BlockedWriters").EnumerateArray().ToArray();
                writers.Should().HaveCount(DelayedOperations).And.OnlyContain(writer =>
                    !writer.GetProperty("TimedOut").GetBoolean());
                if (phase == "baseline")
                {
                    writers.Should().OnlyContain(writer => writer.GetProperty("SqlObservation").ValueKind == JsonValueKind.Object
                        && writer.GetProperty("SqlObservation").GetProperty("BlockerIsAttributedRetentionSession").GetBoolean());
                }
                var operations = item.GetProperty("Operations").EnumerateArray().ToArray();
                operations.Should().HaveCount(DelayedOperations).And.OnlyContain(operation =>
                    operation.GetProperty("Outcome").GetString() == CentralArtifactRetentionResult.Released.ToString());
                var writerDurations = writers
                    .Select(writer => writer.GetProperty("ElapsedMilliseconds").GetDouble()).Order().ToArray();
                var operationDurations = operations
                    .Select(operation => operation.GetProperty("DurationMilliseconds").GetDouble()).Order().ToArray();
                var protocol = item.GetProperty("Protocol");
                var transactions = protocol.GetProperty("SqlTransactions");
                var transactionDurations = transactions
                    .GetProperty("CommittedDurationMilliseconds").EnumerateArray()
                    .Select(duration => duration.GetDouble()).Order().ToArray();
                transactions.GetProperty("RolledBack").GetInt64().Should().Be(0);
                transactions.GetProperty("Failed").GetInt64().Should().Be(0);
                var objectStore = protocol.GetProperty("ObjectStore");
                objectStore.GetProperty("Deletes").GetInt64().Should().Be(DelayedOperations);
                var deleteDurations = objectStore.GetProperty("DeleteDurationMilliseconds")
                    .EnumerateArray().Select(duration => duration.GetDouble()).Order().ToArray();
                var sql = item.GetProperty("Sql");
                return new TrialDelayMetric(
                    trial,
                    item.GetProperty("ConfiguredDelayMilliseconds").GetInt32(),
                    Percentile(writerDurations, 0.50),
                    Percentile(operationDurations, 0.50),
                    Percentile(transactionDurations, 0.50),
                    sql.GetProperty("PeakOpenTransactionSessions").GetInt32(),
                    sql.GetProperty("PeakSessionApplicationLocks").GetInt32(),
                    sql.GetProperty("ObservedBatchApplicationLockOccupancyMilliseconds").GetDouble(),
                    sql.GetProperty("PeakAttributedSqlSessions").GetInt32(),
                    sql.GetProperty("PeakBlockedRequests").GetInt32(),
                    sql.GetProperty("PeakActiveTransactionLogBytes").GetInt64(),
                    sql.GetProperty("EffectiveMedianIntervalMilliseconds").GetDouble(),
                    protocol.GetProperty("EfCommands").GetInt64(),
                    transactions.GetProperty("Committed").GetInt64(),
                    transactions.GetProperty("RolledBack").GetInt64()
                        + transactions.GetProperty("Failed").GetInt64(),
                    objectStore.GetProperty("Deletes").GetInt64(),
                    item.GetProperty("LogicalObjectBytesRemoved").GetInt64(),
                    writers.Count(writer => writer.GetProperty("SqlObservation").ValueKind == JsonValueKind.Object
                        && writer.GetProperty("SqlObservation").GetProperty("BlockerIsAttributedRetentionSession").GetBoolean()),
                    sql.GetProperty("PeakActiveRequests").GetInt32(),
                    Percentile(deleteDurations, 0.50),
                    Percentile(deleteDurations, 0.95));
            }).ToArray();
            delays.Should().HaveCount(DeleteDelaysMilliseconds.Length);
            return new TrialEvidenceInput(
                trial,
                root.GetProperty("Environment").GetProperty(nameof(Issue246EnvironmentFacts.EnvironmentFingerprintSha256))
                    .GetString()!,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    root.GetProperty("Workload").GetRawText()))),
                performance,
                delays);
        }
    }

    private sealed record TrialPerformanceMetric(
        int Trial,
        int Concurrency,
        double P50Milliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double OperationsPerSecond,
        long AllocatedBytes,
        double ProcessCpuMilliseconds,
        long PeakObservedWorkingSetBytes,
        long PeakWorkingSetAboveStartBytes,
        long WorkingSetEndMinusStartBytes,
        long EfCommands,
        long TransactionsSuccessfullyStarted,
        long TransactionsCommitted,
        long TransactionsRolledBack,
        long TransactionsFailed,
        double TransactionP95Milliseconds,
        long MinioRequests,
        long MinioDeletes,
        long RequestEntityBytes,
        long ResponseEntityBytes,
        long UnknownEntityLengths,
        long LogicalObjectBytesRemoved,
        long GlobalDatabaseLogUsedDeltaBytes,
        double MinioDeleteP50Milliseconds,
        double MinioDeleteP95Milliseconds,
        int PeakAttributedSqlSessions,
        int PeakActiveRequests,
        int PeakOpenTransactionSessions,
        int PeakSessionApplicationLocks,
        int PeakBlockedRequests,
        long PeakActiveTransactionLogBytes,
        double EffectiveSamplingIntervalMilliseconds,
        double ObservedBatchApplicationLockOccupancyMilliseconds);

    private sealed record TrialDelayMetric(
        int Trial,
        int DelayMilliseconds,
        double WriterP50Milliseconds,
        double OperationP50Milliseconds,
        double TransactionP50Milliseconds,
        int PeakOpenTransactionSessions,
        int PeakSessionApplicationLocks,
        double ObservedBatchApplicationLockOccupancyMilliseconds,
        int PeakAttributedSqlSessions,
        int PeakBlockedRequests,
        long PeakActiveTransactionLogBytes,
        double EffectiveSamplingIntervalMilliseconds,
        long EfCommands,
        long TransactionsCommitted,
        long TransactionsRolledBackOrFailed,
        long MinioDeletes,
        long LogicalObjectBytesRemoved,
        int AttributedBlockedWriterObservations,
        int PeakActiveRequests,
        double MinioDeleteP50Milliseconds,
        double MinioDeleteP95Milliseconds);

    private sealed record AggregateMetric(double Minimum, double Median, double Maximum);

    private sealed record ComparisonMetric(
        string Name,
        double BaselineMedian,
        double AfterMedian,
        double AbsoluteChange,
        double? RelativeChange,
        double MaterialThresholdFraction,
        string RegressionDirection,
        bool MaterialRegression);
}
