using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressPerformanceTests
{
    private const int RetainedCaptureCount = 1812;
    private const int RetainedSceneObjectCount = 14000;

    [TestMethod]
    public async Task RetainedHistory_PrepareColdStartFixture()
    {
        var root = ColdStartRoot();
        Assert.IsFalse(Directory.Exists(root), "Fixture preparation requires a new disposable directory.");
        Directory.CreateDirectory(root);
        var configuration = await LoadCanonicalConfigurationAsync().ConfigureAwait(false);
        var locationOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        using (var locations = new ProtectedDeploymentLocationStore(
            locationOptions, new DataProtectionDeploymentLocationProtector(locationOptions), TimeProvider.System,
            NullLogger<ProtectedDeploymentLocationStore>.Instance))
        {
            var location = await locations.InitializeAsync(new DeploymentLocationSeed(
                "local-deployment", "local-configuration", null, DateTimeOffset.UnixEpoch, null,
                configuration.Observatory, DeploymentLocationSourceKind.Manual), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(configuration.DeploymentLocation!.ToProvenance(), location.ToProvenance());
        }
        var input = await RenderCanonicalW2InputAsync(configuration).ConfigureAwait(false);
        var objects = Enumerable.Range(0, RetainedSceneObjectCount)
            .Select(index => new ProjectedObjectProvenance(
                $"synthetic-retained-{index:D5}", $"Synthetic retained star {index:D5}", index % W2Width + 0.5,
                index % W2Height + 0.5, index % 100 / 10d)).ToArray();
        long manifestBytes = 0;
        using (var ingress = CreateIngress(root))
        {
            for (var index = 0; index < RetainedCaptureCount; index++)
            {
                var stageKey = StageKey(index);
                var scene = input.Metadata.Scene! with
                {
                    SceneId = $"retained-scene-{index:D5}",
                    Objects = objects,
                    ProjectedSceneStageKey = stageKey,
                    ProjectedSceneStageIdentitySha256 = stageKey,
                    ProjectedSceneStageSchemaVersion = "v1",
                    VirtualExposure = null
                };
                var receipt = await ingress.Ingress.AcceptAsync(configuration,
                    CreateSubmission(index, input with { Metadata = input.Metadata with { Scene = scene } }),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                manifestBytes += new FileInfo(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")).Length;
            }
        }
        SqliteConnection.ClearAllPools();
        var fingerprint = await ColdStartFingerprintAsync(root).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(root, "fixture.json"), JsonSerializer.Serialize(new
        {
            CaptureCount = RetainedCaptureCount,
            SceneObjectCount = RetainedSceneObjectCount,
            ManifestBytes = manifestBytes,
            PayloadBytes = input.Payload.LongLength * RetainedCaptureCount,
            PayloadSha256 = PayloadChecksum.ComputeSha256(input.Payload),
            JournalBytes = new FileInfo(Path.Combine(root, "journal", "raw-ingress.db")).Length,
            Fingerprint = fingerprint,
            Profile = "virtual-asi178mc.full.json",
            Provenance = "Canonical W2 rendered payload plus deterministic synthetic 14000-object retained scene per capture; all history accepted through production raw ingress."
        }, EvidenceOptions)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RetainedHistory_ColdStartPreservesEvidenceAndBecomesHealthy()
    {
        var root = ColdStartRoot();
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture.json")).ConfigureAwait(false));
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        var state = new RawIngressState(TimeProvider.System);
        var stages = new ColdStartStageOwnership();
        var logger = new ColdStartLogger();
        var spans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RawIngressTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Add($"{activity.OperationName}:{activity.Status}")
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0, RawIngressSqliteBusyTimeoutSeconds = 5 }),
            new UnlimitedCapacityProvider(), state, TimeProvider.System, telemetry,
            logger, new NullRawIngressFaultInjector(), projectedSceneStaging: stages);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var ioBefore = ReadProcessIo();
        var started = Stopwatch.GetTimestamp();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var ioAfter = ReadProcessIo();
        var health = await new RawIngressHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Healthy, health.Status);
        Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
        Assert.AreEqual(RetainedCaptureCount, state.Snapshot.PendingCount);
        Assert.IsNotNull(state.LastReconciliation);
        Assert.AreEqual(RetainedCaptureCount, state.LastReconciliation.Inspected);
        Assert.AreEqual(0, state.LastReconciliation.Recovered);
        Assert.AreEqual(0, state.LastReconciliation.MissingEvidence);
        Assert.AreEqual(0, state.LastReconciliation.Quarantined);
        Assert.AreEqual(0, state.LastReconciliation.IndexProjectionFailures);
        Assert.AreEqual(RetainedCaptureCount, stages.Count);
        // Event 2048 is currently Warning even for a successful checkpoint; retain and inspect it.
        Assert.IsFalse(logger.Events.Any(entry => entry.Level >= LogLevel.Warning &&
            (entry.EventId != 2048 || entry.Message != "Raw ingress SQLite operation checkpoint completed with result success")),
            JsonSerializer.Serialize(logger.Events, EvidenceOptions));
        Assert.IsTrue(logger.Events.Any(entry => entry.EventId == 2040));
        Assert.IsTrue(logger.Events.Any(entry => entry.EventId == 2041));
        CollectionAssert.Contains(spans, "raw-ingress.initialize:Ok");
        CollectionAssert.Contains(spans, "raw-ingress.reconcile:Ok");
        Assert.AreEqual(0L, telemetry.TransactionFailureCount);
        Assert.AreEqual(0L, telemetry.CheckpointFailureCount);
        var fingerprint = await ColdStartFingerprintAsync(root).ConfigureAwait(false);
        Assert.AreEqual(fixture.RootElement.GetProperty("fingerprint").GetString(), fingerprint);
        await File.WriteAllTextAsync(Path.Combine(root, $"cold-start-{revision}.json"), JsonSerializer.Serialize(new
        {
            Revision = revision,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            CpuMilliseconds = cpu.TotalMilliseconds,
            AllocatedBytes = allocated,
            PeakWorkingSetBytes = process.PeakWorkingSet64,
            WorkingSetBytes = process.WorkingSet64,
            Io = ioAfter.ToDictionary(pair => pair.Key, pair => pair.Value - ioBefore.GetValueOrDefault(pair.Key)),
            Health = health.Status.ToString(),
            Logs = logger.Events,
            Spans = spans,
            Sqlite = new { telemetry.LockWaitSamples, LockWaitMilliseconds = telemetry.TotalLockWait.TotalMilliseconds,
                telemetry.TransactionCount, telemetry.TransactionFailureCount, telemetry.CheckpointCount, telemetry.CheckpointFailureCount },
            Reconciliation = state.LastReconciliation,
            StageOwners = stages.Count,
            Fingerprint = fingerprint,
            Fixture = fixture.RootElement.Clone()
        }, EvidenceOptions)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RetainedHistory_PrepareHostReadinessClone()
    {
        // Run only on a disposable copy after the unchanged-history component measurements. Acknowledge
        // the synthetic consumer through its production API, so host readiness measures retained history
        // without draining 1812 deliberately unfinished test captures into the processing pipeline.
        var root = ColdStartRoot();
        Assert.IsTrue(File.Exists(Path.Combine(root, "fixture.json")));
        var configuration = await LoadCanonicalConfigurationAsync().ConfigureAwait(false);
        using var fixture = CreateIngress(root);
        await fixture.Ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var lanes = fixture.Ingress;
        var policy = new CaptureLanePolicy(Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
        var standard = policy.Definitions.Single(lane => lane.Name == "standard");
        var completed = 0;
        while (await lanes.ClaimAsync(standard, "cold-start-fixture-consumer", configuration, CancellationToken.None).ConfigureAwait(false) is { } lease)
        {
            await lanes.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            completed++;
        }
        Assert.AreEqual(RetainedCaptureCount, completed);
        await File.WriteAllTextAsync(Path.Combine(root, "host-fixture-fingerprint.txt"),
            await ColdStartFingerprintAsync(root).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RetainedHistory_VerifyHostReadinessClone()
    {
        var root = ColdStartRoot();
        var expected = await File.ReadAllTextAsync(Path.Combine(root, "host-fixture-fingerprint.txt")).ConfigureAwait(false);
        Assert.AreEqual(expected, await ColdStartFingerprintAsync(root).ConfigureAwait(false));
    }

    private static string ColdStartRoot()
        => Environment.GetEnvironmentVariable("HVO_COLD_START_ROOT")
            ?? throw new InvalidOperationException("Set HVO_COLD_START_ROOT to a dedicated disposable fixture path.");

    private static string StageKey(int index)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"retained-stage-{index:D5}")));

    private static async Task<string> ColdStartFingerprintAsync(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT capture_id, manifest_json, payload_sha256, payload_length, state, retention_hold, evidence_origin FROM raw_captures ORDER BY raw_capture_row_id;";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reader.GetString(0)));
            hash.AppendData((byte[])reader[1]);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reader.GetString(2)));
            hash.AppendData(BitConverter.GetBytes(reader.GetInt64(3)));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reader.GetString(4)));
            hash.AppendData(BitConverter.GetBytes(reader.GetInt64(5)));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reader.GetString(6)));
            count++;
        }
        Assert.AreEqual(RetainedCaptureCount, count);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static Dictionary<string, long> ReadProcessIo()
        => OperatingSystem.IsLinux()
            ? File.ReadAllLines("/proc/self/io").Select(line => line.Split(':'))
                .ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture))
            : [];

    private sealed class ColdStartLogger : ILogger<RawCaptureIngress>
    {
        internal List<ColdStartLogEvent> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add(new ColdStartLogEvent(eventId.Id, logLevel, formatter(state, exception)));
    }

    private sealed record ColdStartLogEvent(int EventId, LogLevel Level, string Message);

    private sealed class ColdStartStageOwnership : IProjectedSceneStagingReconciler
    {
        internal int Count { get; private set; }

        public ValueTask<ProjectedSceneStageReconciliationResult> ReconcileAsync(IReadOnlySet<string> ownedStageKeys, CancellationToken cancellationToken)
        {
            Count = ownedStageKeys.Count;
            Assert.HasCount(RetainedCaptureCount, ownedStageKeys);
            for (var index = 0; index < RetainedCaptureCount; index++)
                Assert.IsTrue(ownedStageKeys.Contains(StageKey(index)));
            return ValueTask.FromResult(new ProjectedSceneStageReconciliationResult(0, 0, 0));
        }
    }
}
