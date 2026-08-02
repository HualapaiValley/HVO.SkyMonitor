using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CentralRecoveryPerformanceTests
{
    private const int MetadataArtifactCount = 10_000;
    private const int DefaultPayloadArtifactCount = 125;
    private const int DefaultPayloadBytes = 64 * 1024;
    private const int CanonicalW3PArtifactCount = 100;
    private const int CanonicalW3PPayloadBytes = 12_879_360;
    private const uint PayloadSeed = 0x0114_2026;
    private const string Bucket = "skymonitor-artifacts";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private readonly List<string> objectKeys = [];
    private readonly List<Guid> frameIds = [];

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public async Task CleanupAsync()
    {
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        foreach (var key in objectKeys)
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(key)).ConfigureAwait(false);
        }
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralFrames.Where(frame => frameIds.Contains(frame.Id)).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task W3M_10000MetadataAndBoundedPayloadRecovery_RecordsExactEvidence()
    {
        var runId = Guid.NewGuid().ToString("N");
        var scale = GetPayloadScale();
        var metadataFrameId = await InsertMetadataWorkloadAsync(runId).ConfigureAwait(false);
        frameIds.Add(metadataFrameId);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var metadataCpuStarted = process.TotalProcessorTime;
        var metadataWorkingSetStarted = process.WorkingSet64;
        var queryStarted = Stopwatch.GetTimestamp();
        var marked = 0;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            while (true)
            {
                var ids = await db.CentralArtifacts.AsNoTracking()
                    .Where(artifact => artifact.CentralFrameId == metadataFrameId
                        && artifact.ObjectState == CentralArtifactObjectState.Available
                        && artifact.RecoveryGeneration == 0)
                    .OrderBy(artifact => artifact.Id)
                    .Take(CentralArtifactReconciliationService.MaximumSqlInventoryArtifactsPerCycle)
                    .Select(artifact => artifact.Id)
                    .ToListAsync().ConfigureAwait(false);
                if (ids.Count == 0)
                {
                    break;
                }
                marked += await db.CentralArtifacts.Where(artifact => ids.Contains(artifact.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(artifact => artifact.RecoveryGeneration, 1L))
                    .ConfigureAwait(false);
            }
        }
        var queryElapsed = Stopwatch.GetElapsedTime(queryStarted);
        process.Refresh();
        var metadataCpuElapsed = process.TotalProcessorTime - metadataCpuStarted;
        var metadataWorkingSetCompleted = process.WorkingSet64;
        marked.Should().Be(MetadataArtifactCount);

        var payload = CreateDeterministicPayload(scale.PayloadBytes);
        var checksum = Convert.ToHexString(SHA256.HashData(payload));
        await InsertPayloadWorkloadAsync(runId, payload, checksum, scale.ArtifactCount).ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralRecoveryCheckpoints
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Phase, CentralRecoveryPhases.Idle)
                    .SetProperty(item => item.NextInventoryAtUtc, DateTimeOffset.UtcNow.AddDays(1))
                    .SetProperty(item => item.LeaseToken, (Guid?)null)
                    .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }
        var initialBacklog = await ReadBacklogAsync().ConfigureAwait(false);
        var initialObjects = await ReadObjectSnapshotAsync($"artifacts/c0/{runId}/").ConfigureAwait(false);
        process.Refresh();
        var recoveryCpuStarted = process.TotalProcessorTime;
        var recoveryWorkingSetStarted = process.WorkingSet64;
        var recoveryPeakWorkingSetStarted = process.PeakWorkingSet64;
        using var services = CreateServices();
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);
        var recoveryStarted = Stopwatch.GetTimestamp();
        var recoveryCycles = 0;
        while (true)
        {
            recoveryCycles++;
            var immediate = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var remaining = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
                .CountAsync(artifact => frameIds.Contains(artifact.CentralFrameId)
                    && artifact.ObjectState == CentralArtifactObjectState.Pending).ConfigureAwait(false);
            if (remaining == 0)
            {
                immediate.Should().BeFalse("the final production recovery cycle must report a drained backlog");
                break;
            }
            immediate.Should().BeTrue("pending recovery rows must continue without a 30-second idle");
            recoveryCycles.Should().BeLessThanOrEqualTo(scale.ArtifactCount);
        }

        var recoveryElapsed = Stopwatch.GetElapsedTime(recoveryStarted);
        process.Refresh();
        var recoveryCpuElapsed = process.TotalProcessorTime - recoveryCpuStarted;
        var recoveryWorkingSetCompleted = process.WorkingSet64;
        var recoveryPeakWorkingSetCompleted = process.PeakWorkingSet64;
        var throughput = scale.ArtifactCount / recoveryElapsed.TotalSeconds;
        throughput.Should().BeGreaterThan(1);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralArtifacts.CountAsync(artifact => artifact.CentralFrameId == metadataFrameId
                && artifact.RecoveryGeneration == 1).ConfigureAwait(false)).Should().Be(MetadataArtifactCount);
            var payloadArtifacts = await db.CentralArtifacts.AsNoTracking()
                .Where(artifact => frameIds.Contains(artifact.CentralFrameId) && artifact.CentralFrameId != metadataFrameId)
                .ToListAsync().ConfigureAwait(false);
            payloadArtifacts.Should().HaveCount(scale.ArtifactCount);
            payloadArtifacts.Should().OnlyContain(artifact => artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ByteLength == scale.PayloadBytes && artifact.ChecksumSha256 == checksum);
        }
        var verifiedObjects = 0;
        await foreach (var item in AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>()
            .ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(Bucket)
                .WithPrefix($"artifacts/c0/{runId}/").WithRecursive(true)))
        {
            item.Size.Should().Be((ulong)scale.PayloadBytes);
            var observed = await ReadChecksumAsync(item.Key).ConfigureAwait(false);
            observed.Should().Be(checksum);
            verifiedObjects++;
        }
        verifiedObjects.Should().Be(scale.ArtifactCount);
        var finalBacklog = await ReadBacklogAsync().ConfigureAwait(false);
        var finalObjects = await ReadObjectSnapshotAsync($"artifacts/c0/{runId}/").ConfigureAwait(false);
        initialBacklog.Count.Should().Be(scale.ArtifactCount);
        initialBacklog.Bytes.Should().Be((long)scale.ArtifactCount * scale.PayloadBytes);
        finalBacklog.Should().Be(new BacklogSnapshot(0, 0));
        initialObjects.Should().Be(new ObjectSnapshot(scale.ArtifactCount, (long)scale.ArtifactCount * scale.PayloadBytes));
        finalObjects.Should().Be(initialObjects);

        var evidence = new
        {
            Schema = "hvo-central-recovery-performance-v1",
            Issue = 114,
            Workloads = new
            {
                W3M = new
                {
                    Classification = "metadata index microbenchmark",
                    MetadataArtifacts = MetadataArtifactCount,
                    MetadataPageSize = CentralArtifactReconciliationService.MaximumSqlInventoryArtifactsPerCycle
                },
                W3P = new
                {
                    Classification = "byte-equivalent persisted payload recovery; not rendered VirtualSky",
                    RenderedVirtualSky = false,
                    PayloadArtifacts = scale.ArtifactCount,
                    PayloadBytesEach = scale.PayloadBytes,
                    TotalPayloadBytes = (long)scale.ArtifactCount * scale.PayloadBytes,
                    scale.CanonicalW3P,
                    PayloadContent = new
                    {
                        Algorithm = "xorshift32-most-significant-byte",
                        Seed = $"0x{PayloadSeed:X8}",
                        Description = "Deterministic fixed-seed bytes reused for every persisted object"
                    }
                }
            },
            MetadataIndexMicrobenchmark = new
            {
                Scope = "SQL metadata paging and generation marking only; not production recovery throughput",
                MetadataRowsMarked = marked,
                MetadataQueryMilliseconds = queryElapsed.TotalMilliseconds,
                MetadataRowsPerSecond = MetadataArtifactCount / queryElapsed.TotalSeconds,
                ProcessCpuMilliseconds = metadataCpuElapsed.TotalMilliseconds,
                ProcessWorkingSetStartBytes = metadataWorkingSetStarted,
                ProcessWorkingSetEndBytes = metadataWorkingSetCompleted
            },
            ProductionRecovery = new
            {
                Scope = "CentralArtifactReconciliationService over persisted SQL backlog and MinIO payload objects",
                InitialBacklog = initialBacklog,
                FinalBacklog = finalBacklog,
                InitialObjects = initialObjects,
                FinalObjects = finalObjects,
                RecoveredRows = scale.ArtifactCount,
                VerifiedObjects = verifiedObjects,
                RecoveryCycles = recoveryCycles,
                ChecksumSha256 = checksum,
                RecoveryMilliseconds = recoveryElapsed.TotalMilliseconds,
                RecoveryObjectsPerSecond = throughput,
                ProcessCpuMilliseconds = recoveryCpuElapsed.TotalMilliseconds,
                ProcessWorkingSetStartBytes = recoveryWorkingSetStarted,
                ProcessWorkingSetEndBytes = recoveryWorkingSetCompleted,
                ProcessPeakWorkingSetStartBytes = recoveryPeakWorkingSetStarted,
                ProcessPeakWorkingSetEndBytes = recoveryPeakWorkingSetCompleted
            },
            OperationalLimits = new
            {
                CatchAllAudit = "One linear O(namespace) streaming listing per prefix and 24-hour generation; each prefix must complete within the renewable 30-minute recovery lease",
                CanonicalGeneratedKeys = $"Bounded to {CentralArtifactReconciliationService.MaximumMinioInventoryObjectsPerCycle} objects per partition cycle"
            },
            Command = "dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter \"FullyQualifiedName~CentralRecoveryPerformanceTests.W3M_10000MetadataAndBoundedPayloadRecovery_RecordsExactEvidence\"",
            CanonicalW3PCommand = "HVO_RECOVERY_PERF_W3P=1 dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter \"FullyQualifiedName~CentralRecoveryPerformanceTests.W3M_10000MetadataAndBoundedPayloadRecovery_RecordsExactEvidence\""
        };
        TestContext.WriteLine(JsonSerializer.Serialize(evidence, EvidenceJsonOptions));
    }

    private static async Task<Guid> InsertMetadataWorkloadAsync(string runId)
    {
        var frameId = Guid.NewGuid();
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CentralFrames.Add(CreateFrame(frameId, $"recovery-performance-{runId}"));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("CentralFrameId", typeof(Guid));
        table.Columns.Add("ArtifactId", typeof(Guid));
        table.Columns.Add("DevicePublicId", typeof(Guid));
        table.Columns.Add("Role", typeof(string));
        table.Columns.Add("RecipeVersion", typeof(string));
        table.Columns.Add("ManifestSchemaVersion", typeof(string));
        table.Columns.Add("MediaType", typeof(string));
        table.Columns.Add("ByteLength", typeof(long));
        table.Columns.Add("ChecksumSha256", typeof(string));
        table.Columns.Add("StorageReference", typeof(string));
        table.Columns.Add("ReceivedAtUtc", typeof(DateTimeOffset));
        table.Columns.Add("IdempotencyKey", typeof(string));
        table.Columns.Add("ObjectState", typeof(string));
        table.Columns.Add("ReconstructionState", typeof(string));
        var devicePublicId = Guid.NewGuid();
        for (var index = 0; index < MetadataArtifactCount; index++)
        {
            var artifactId = Guid.NewGuid();
            table.Rows.Add(Guid.NewGuid(), frameId, artifactId, devicePublicId, "Preview", "w3m-v1", "legacy",
                "application/octet-stream", 1L, new string('A', 64), $"minio://legacy-w3m/{runId}/{index:D5}",
                DateTimeOffset.UnixEpoch, Convert.ToHexString(SHA256.HashData(artifactId.ToByteArray())),
                "Available", "LegacyIncomplete");
        }
        await using var connection = new SqlConnection(AssemblyHooks.Fixture.SqlServerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "CentralArtifacts", BatchSize = 1000 };
        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }
        await bulk.WriteToServerAsync(table).ConfigureAwait(false);
        return frameId;
    }

    private async Task InsertPayloadWorkloadAsync(string runId, byte[] payload, string checksum, int artifactCount)
    {
        var minio = AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
        }
        for (var index = 0; index < artifactCount; index++)
        {
            var frame = CreateFrame(Guid.NewGuid(), $"recovery-performance-{runId}");
            var key = $"artifacts/c0/{runId}/{index:D3}.bin";
            objectKeys.Add(key);
            frameIds.Add(frame.Id);
            await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(key)
                .WithStreamData(new MemoryStream(payload, writable: false)).WithObjectSize(payload.LongLength))
                .ConfigureAwait(false);
            frame.Artifacts.Add(new CentralArtifact
            {
                Frame = frame,
                ArtifactId = Guid.NewGuid(),
                DevicePublicId = frame.DevicePublicId,
                Role = FrameArtifactRole.Preview,
                RecipeVersion = "w3p-bounded-v1",
                ManifestSchemaVersion = "perf-v1",
                MediaType = "application/octet-stream",
                ByteLength = payload.LongLength,
                ChecksumSha256 = checksum,
                StorageReference = $"minio://{Bucket}/{key}",
                ReceivedAtUtc = DateTimeOffset.UnixEpoch,
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())),
                ObjectState = CentralArtifactObjectState.Pending,
                ReconstructionState = CentralReconstructionState.Complete
            });
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralFrames.Add(frame);
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync().ConfigureAwait(false);
        }
    }

    private static CentralFrame CreateFrame(Guid id, string agentId)
        => new()
        {
            Id = id,
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = agentId,
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            FirstReceivedAtUtc = DateTimeOffset.UnixEpoch
        };

    private static PayloadScale GetPayloadScale()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_RECOVERY_PERF_W3P"), "1", StringComparison.Ordinal))
        {
            return new PayloadScale(CanonicalW3PArtifactCount, CanonicalW3PPayloadBytes, CanonicalW3P: true);
        }
        var count = int.TryParse(Environment.GetEnvironmentVariable("HVO_RECOVERY_PERF_PAYLOAD_COUNT"), out var configuredCount)
            ? configuredCount
            : DefaultPayloadArtifactCount;
        var bytes = int.TryParse(Environment.GetEnvironmentVariable("HVO_RECOVERY_PERF_PAYLOAD_BYTES"), out var configuredBytes)
            ? configuredBytes
            : DefaultPayloadBytes;
        return new PayloadScale(count, bytes, CanonicalW3P: false);
    }

    private static byte[] CreateDeterministicPayload(int byteLength)
    {
        var payload = new byte[byteLength];
        var state = PayloadSeed;
        for (var index = 0; index < payload.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            payload[index] = (byte)(state >> 24);
        }
        return payload;
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(AssemblyHooks.Fixture.SqlServerConnectionString));
        services.AddSingleton(AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>());
        services.AddSingleton<CentralArtifactRetrievalTelemetry>();
        services.AddScoped<ICentralArtifactObjectReader>(provider => new CentralArtifactObjectReader(
            provider.GetRequiredService<IMinioClient>(),
            provider.GetRequiredService<CentralArtifactRetrievalTelemetry>(),
            TimeProvider.System,
            NullLogger<CentralArtifactObjectReader>.Instance));
        services.AddScoped<ICentralDerivativeJobScheduler, NoOpScheduler>();
        return services.BuildServiceProvider();
    }

    private static async Task<string> ReadChecksumAsync(string key)
    {
        byte[]? checksum = null;
        await AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>()
            .GetObjectAsync(new GetObjectArgs().WithBucket(Bucket).WithObject(key).WithCallbackStream(stream =>
            {
                using var hash = SHA256.Create();
                checksum = hash.ComputeHash(stream);
            })).ConfigureAwait(false);
        return Convert.ToHexString(checksum!);
    }

    private async Task<BacklogSnapshot> ReadBacklogAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts.AsNoTracking()
            .Where(artifact => frameIds.Contains(artifact.CentralFrameId)
                && artifact.ObjectState == CentralArtifactObjectState.Pending);
        return new BacklogSnapshot(
            await query.CountAsync().ConfigureAwait(false),
            await query.SumAsync(artifact => (long?)artifact.ByteLength).ConfigureAwait(false) ?? 0);
    }

    private static async Task<ObjectSnapshot> ReadObjectSnapshotAsync(string prefix)
    {
        var count = 0;
        var bytes = 0L;
        await foreach (var item in AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IMinioClient>()
            .ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(Bucket).WithPrefix(prefix).WithRecursive(true)))
        {
            count++;
            bytes += checked((long)item.Size);
        }
        return new ObjectSnapshot(count, bytes);
    }

    private sealed class NoOpScheduler : ICentralDerivativeJobScheduler
    {
        public Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record PayloadScale(int ArtifactCount, int PayloadBytes, bool CanonicalW3P);
    private sealed record BacklogSnapshot(int Count, long Bytes);
    private sealed record ObjectSnapshot(int Count, long Bytes);
}
