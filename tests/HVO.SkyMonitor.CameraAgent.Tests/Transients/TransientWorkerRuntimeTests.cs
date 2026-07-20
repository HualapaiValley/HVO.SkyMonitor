using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientWorkerRuntimeTests
{
    [TestMethod]
    [DataRow(TransientOperatingMode.Edge, "finalized")]
    [DataRow(TransientOperatingMode.Hybrid, "handoff_pending")]
    public async Task VirtualSkyEvent_ConvergesToModeSpecificDurableState(
        TransientOperatingMode mode,
        string expectedPhase)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-events", Guid.NewGuid().ToString("N"));
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:TransientDetection:Mode"] = mode.ToString(),
                ["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] =
                    (mode == TransientOperatingMode.Hybrid).ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
            services.AddCameraAgentInfrastructure(configuration);
            using var provider = services.BuildServiceProvider();
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(CreateOneFrameScenario(epoch));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 7).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var iteration = 0; iteration < 16; iteration++)
            {
                var worked = await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false) ||
                    await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false);
                if (!worked)
                {
                    break;
                }
            }

            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            var reasons = await ScalarStringAsync(
                connection,
                "SELECT COALESCE(group_concat(failure_reason, ','), 'none') FROM transient_worker_frames;")
                .ConfigureAwait(false);
            Assert.IsGreaterThanOrEqualTo(1L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false), reasons);
            Assert.AreEqual(expectedPhase, await ScalarStringAsync(
                connection, "SELECT phase FROM transient_candidates ORDER BY created_unix_ms LIMIT 1;").ConfigureAwait(false));
            if (mode == TransientOperatingMode.Edge)
            {
                Assert.AreEqual(1L, await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM transient_candidates WHERE finalization_payload IS NOT NULL;").ConfigureAwait(false));
            }
            else
            {
                Assert.AreEqual(1L, await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM transient_candidates WHERE submission_payload IS NOT NULL AND source_hold_released = 0;").ConfigureAwait(false));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Edge_NoEventVirtualSkyFramesDrainWithoutCandidateAndRetainOnlyCausalHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:TransientDetection:Mode"] = "Edge",
                ["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100"
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
            services.AddCameraAgentInfrastructure(configuration);
            using var provider = services.BuildServiceProvider();
            var cameraConfiguration = CreateConfiguration();
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch).ConfigureAwait(false);

            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 5; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }
            Assert.IsFalse(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));

            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(2L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_frames WHERE state = 'history';").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_frames WHERE state IN ('queued', 'retry_wait');").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_frames WHERE state = 'quarantined';").ConfigureAwait(false));
            Assert.AreEqual(2L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_frames WHERE causal_succeeded = 0;").ConfigureAwait(false));
            Assert.AreEqual(3L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_frames WHERE causal_succeeded = 1;").ConfigureAwait(false));
            Assert.IsFalse(await provider.GetRequiredService<SqliteTransientRuntimeStore>()
                .IsCausalWindowCompleteAsync(
                    "transient-runtime-agent", 3, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Edge_StartupBoundaryCandidateDoesNotConvergeCentered()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(CreateOneFrameScenario(epoch, 10));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 5; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }

            Assert.IsFalse(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_candidates WHERE finalization_payload IS NOT NULL;").ConfigureAwait(false));
            Assert.AreEqual(2L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_worker_frames WHERE causal_succeeded = 0;").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Edge_FailedCausalBackgroundIsPersistedAsUnsuccessful()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-background", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root, maximumAdjacentStartIntervalSeconds: 1);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration();
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 5; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarAsync(
                connection, "SELECT causal_succeeded FROM transient_worker_frames f JOIN raw_captures r ON r.raw_capture_row_id = f.raw_capture_row_id WHERE r.capture_sequence = 3;")
                .ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Edge_QuarantinedContextPreventsCenteredConvergence()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-quarantined-context", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(CreateOneFrameScenario(epoch));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 7).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 7; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }
            var store = provider.GetRequiredService<SqliteTransientRuntimeStore>();
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            var contextRow = await ScalarAsync(
                connection, "SELECT raw_capture_row_id FROM raw_captures WHERE capture_sequence = 6;").ConfigureAwait(false);
            await store.QuarantineAsync(contextRow, "test-context-quarantine", CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(await store.IsCausalWindowCompleteAsync(
                "transient-runtime-agent", 5, CancellationToken.None).ConfigureAwait(false));
            Assert.IsFalse(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_candidates WHERE finalization_payload IS NOT NULL;").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(15)]
    public void SqliteStorageFailures_AreRetryableAndNotIntegrityFailures(int errorCode)
    {
        var exception = new SqliteException("test", errorCode, errorCode);

        Assert.IsTrue(TransientWorkerService.IsRetryableStorageFailure(exception));
        Assert.IsFalse(TransientWorkerService.IsIntegrityFailure(exception));
    }

    [TestMethod]
    [DataRow(11)]
    [DataRow(19)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(26)]
    public void SqliteMalformedOrIntegrityFailures_AreQuarantineEligible(int errorCode)
    {
        var exception = new SqliteException("test", errorCode, errorCode);

        Assert.IsTrue(TransientWorkerService.IsIntegrityFailure(exception));
        Assert.IsFalse(TransientWorkerService.IsRetryableStorageFailure(exception));
    }

    [TestMethod]
    public async Task Edge_SqliteFullReportsUnhealthyWithoutQuarantiningWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-sqlite-full", Guid.NewGuid().ToString("N"));
        try
        {
            var faultInjector = new SqliteRuntimeFaultInjector(
                TransientRuntimeFaultPoint.BeforeIdentityBatchCommit, errorCode: 13);
            using var provider = CreateProvider(root, faultInjector: faultInjector);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(CreateOneFrameScenario(epoch));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 7).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 4; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }

            Assert.IsFalse(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));

            Assert.AreEqual(
                TransientWorkerAvailability.Unhealthy,
                provider.GetRequiredService<TransientWorkerState>().Snapshot.Availability);
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined';").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_worker_frames f JOIN raw_captures r ON r.raw_capture_row_id = f.raw_capture_row_id WHERE r.capture_sequence = 5 AND f.state = 'queued';")
                .ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Edge_PostFinalizationCompletionFaultHealsOnNextSameProcessDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-same-process", Guid.NewGuid().ToString("N"));
        try
        {
            var faultInjector = new ThrowingRuntimeFaultInjector(
                TransientRuntimeFaultPoint.BeforeRuntimeCompletionCommit);
            using var provider = CreateProvider(root, faultInjector: faultInjector);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(CreateOneFrameScenario(epoch));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 7).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 7; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsFalse(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual("completed", await ScalarStringAsync(
                connection, "SELECT state FROM transient_worker_candidates;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_candidates WHERE phase = 'finalized';").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Edge_AdjacentEventFramesWaitForCompleteCausalContextAndAccumulateCanonicalWrappers()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-context", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:TransientDetection:Mode"] = "Edge",
                ["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100"
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
            services.AddCameraAgentInfrastructure(configuration);
            using var provider = services.BuildServiceProvider();
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(CreateAdjacentFrameScenario(epoch));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 8).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();

            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsFalse(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsFalse(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));

            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(2L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_candidates WHERE phase = 'finalized';").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(
                connection, "SELECT COUNT(DISTINCT event_id) FROM transient_candidates;").ConfigureAwait(false));
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.observation_extraction_json, j.finalization_payload
                FROM transient_worker_candidates c
                JOIN transient_candidates j ON j.candidate_id = c.candidate_id
                JOIN raw_captures r ON r.raw_capture_row_id = c.target_raw_capture_row_id
                ORDER BY r.capture_sequence;
                """;
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            var firstExtraction = TransientCandidateExtractionJson.Parse(await reader.GetFieldValueAsync<byte[]>(0).ConfigureAwait(false));
            var firstReceipt = TransientCandidateDeliveryJson.ParseFinalization(
                await reader.GetFieldValueAsync<byte[]>(1).ConfigureAwait(false));
            Assert.IsTrue(firstExtraction.Background.Sources.Any(
                static source => source.Disposition == TransientTemporalSourceDisposition.ExcludedKnownEvent));
            Assert.IsNotNull(firstReceipt.Value);
            Assert.HasCount(1, firstReceipt.Value.Event.Observations);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            var secondReceipt = TransientCandidateDeliveryJson.ParseFinalization(
                await reader.GetFieldValueAsync<byte[]>(1).ConfigureAwait(false));
            Assert.IsNotNull(secondReceipt.Value);
            Assert.HasCount(2, secondReceipt.Value.Event.Observations);
            Assert.HasCount(2, secondReceipt.Value.Event.Assessments);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task StageVirtualFramesAsync(
        ServiceProvider provider,
        CameraModuleConfig cameraConfiguration,
        DateTimeOffset epoch,
        int frameCount = 5)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            provider.GetRequiredService<ICelestialCatalog>(),
            new ProjectedSceneStore());
        await module.InitializeAsync(cameraConfiguration, CancellationToken.None).ConfigureAwait(false);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static value => value.Name == "transient");
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(static value => value.Lane == "transient");
        for (var index = 0; index < frameCount; index++)
        {
            var request = new CaptureRequest(
                epoch.AddSeconds(index * 5),
                TimeSpan.FromSeconds(5),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
            var capture = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var frame = capture.Frame!;
            var extra = new Dictionary<string, string>(frame.Metadata.Extra ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["blackLevelAdu"] = "0",
                ["whiteLevelAdu"] = ushort.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sensorAdcBitDepth"] = "16"
            };
            capture = capture with { Frame = frame with { Metadata = frame.Metadata with { Extra = extra } } };
            var submission = new CaptureLoopSubmission(
                request,
                capture,
                request.RequestedStartUtc,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);
            Assert.IsNotNull(await ingress.AcceptAsync(
                cameraConfiguration, submission, CancellationToken.None).ConfigureAwait(false));
            var lease = await laneStore.ClaimAsync(
                lane, "transient-test", cameraConfiguration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
            await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static CameraModuleConfig CreateConfiguration(VirtualTransientScenarioDefinition? scenario = null)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = 63,
            maximumResults = 1,
            magnitudeZeroElectronsPerSecond = 1_000d,
            backgroundElectronsPerSecond = 1d,
            psfSigmaPixels = 1d,
            psfRadiusPixels = 4d,
            vignettingStrength = 0.1,
            bias = 0d,
            readNoiseStandardDeviation = 0d,
            shotNoiseEnabled = false,
            transientScenario = scenario
        });
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile(
                    "TransientRuntimeFixture", 64, 48, 5.86, SensorColorMode.Mono,
                    CameraPixelFormat.Mono16, SensorResponseMode.Monochrome,
                    SensorRecipeVersion: "transient-runtime-v1"),
                new OpticsProfile(
                    "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    32, 24, 23, CalibrationVersion: "transient-runtime-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    1,
                    1)),
            AgentId: "transient-runtime-agent");
    }

    private static VirtualTransientScenarioDefinition CreateOneFrameScenario(
        DateTimeOffset epoch,
        double offsetSeconds = 20) => new()
        {
            ScenarioId = "runtime-one-frame",
            ScenarioVersion = "1",
            Seed = 63,
            EpochUtc = epoch,
            TemporalSampleCount = 8,
            SkyTracks =
        [
            new VirtualTransientSkyTrack
            {
                PrimitiveId = "runtime-sky-track",
                Keyframes =
                [
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = offsetSeconds,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 270,
                        Magnitude = -10,
                        AngularWidthDegrees = 0.25
                    },
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = offsetSeconds + 1,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 90,
                        Magnitude = -10,
                        AngularWidthDegrees = 0.25
                    }
                ]
            }
        ],
            SensorTracks =
        [
            new VirtualTransientSensorTrack
            {
                PrimitiveId = "runtime-track",
                Keyframes =
                [
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = offsetSeconds,
                        PixelX = 16,
                        PixelY = 24,
                        ElectronsPerSecond = 10_000_000
                    },
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = offsetSeconds + 1,
                        PixelX = 48,
                        PixelY = 24,
                        ElectronsPerSecond = 10_000_000
                    }
                ]
            }
        ]
        };

    private static VirtualTransientScenarioDefinition CreateAdjacentFrameScenario(DateTimeOffset epoch) => new()
    {
        ScenarioId = "runtime-adjacent-frames",
        ScenarioVersion = "1",
        Seed = 63,
        EpochUtc = epoch,
        TemporalSampleCount = 8,
        SkyTracks =
        [
            new VirtualTransientSkyTrack
            {
                PrimitiveId = "runtime-adjacent-track",
                Keyframes =
                [
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = 20,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 270,
                        Magnitude = -10,
                        AngularWidthDegrees = 0.25
                    },
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = 26,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 90,
                        Magnitude = -10,
                        AngularWidthDegrees = 0.25
                    }
                ]
            }
        ]
    };

    private static ServiceProvider CreateProvider(
        string root,
        int? maximumAdjacentStartIntervalSeconds = null,
        ITransientRuntimeFaultInjector? faultInjector = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:TransientDetection:Mode"] = "Edge",
            ["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100"
        };
        if (maximumAdjacentStartIntervalSeconds.HasValue)
        {
            values["CameraAgent:TransientDetection:MaximumAdjacentStartIntervalSeconds"] =
                maximumAdjacentStartIntervalSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        if (faultInjector is not null)
        {
            services.AddSingleton(faultInjector);
            services.AddSingleton<ITransientRuntimeFaultInjector>(faultInjector);
        }
        return services.BuildServiceProvider();
    }

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SqliteRuntimeFaultInjector(
        TransientRuntimeFaultPoint point,
        int errorCode) : ITransientRuntimeFaultInjector
    {
        private int _thrown;

        public void Inject(TransientRuntimeFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new SqliteException("Injected transient SQLite storage failure.", errorCode, errorCode);
            }
        }
    }

    private sealed class ThrowingRuntimeFaultInjector(
        TransientRuntimeFaultPoint point) : ITransientRuntimeFaultInjector
    {
        private int _thrown;

        public void Inject(TransientRuntimeFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InvalidOperationException("Injected transient runtime fault.");
            }
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test callers pass internal constant SQL only.")]
    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test callers pass internal constant SQL only.")]
    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
