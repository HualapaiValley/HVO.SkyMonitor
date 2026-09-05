using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// A live execution is created when its own raw capture is accepted, before the earlier captures of a
/// trailing window have finished processing, so these tests drive a real ingress and standard lane with a
/// processing backlog - the normal state of a paced agent - and prove the combined artifact still spans the
/// configured window while replay keeps consuming its own frozen pins.
/// </summary>
[TestClass]
public sealed class RollingCombinationWindowLineageTests
{
    private const int WindowSize = 5;
    private const int CaptureCount = WindowSize + 1;
    private const string RollingNodeId = "rolling";
    private static readonly DateTimeOffset FixtureUtc = new(2026, 2, 1, 3, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LiveExecutionSpansTheConfiguredWindowAndRecordsWhatItConsumed()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            var (receipt, _) = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            using var store = CreateStore(root);
            var rolling = await store.ReadNodeAsync(captureId, RollingNodeId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            var sources = rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;
            Assert.HasCount(WindowSize, sources);

            // The combined frame must actually accumulate the window, not merely name it.
            Assert.AreEqual(TimeSpan.FromSeconds(WindowSize), rolling.Outputs[0].TotalIntegration);

            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var execution = (await operations.ReadExecutionsAsync(
                    ProcessingGraphExecutionClass.Live, 256, CancellationToken.None).ConfigureAwait(false))
                .Single(state => state.CaptureId == captureId);
            var detail = await operations.ReadExecutionDetailAsync(execution.ExecutionId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(detail);
            var recorded = detail.Nodes.Single(static node => node.NodeId == RollingNodeId).Inputs
                .Where(static input => input.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput)
                .OrderBy(static input => input.WindowPosition)
                .ToArray();

            // The recorded window evidence must be the earlier captures the attempt actually combined.
            CollectionAssert.AreEqual(
                Enumerable.Range(-(WindowSize - 1), WindowSize - 1).ToArray(),
                recorded.Select(static input => input.WindowPosition).ToArray());
            CollectionAssert.AreEqual(
                sources.Take(WindowSize - 1).ToArray(),
                recorded.Select(static input => input.ArtifactId).ToArray());
            Assert.IsFalse(recorded.Any(input => input.CaptureId == captureId));
            Assert.AreEqual(0L, await CountUnreleasedPinsAsync(root, execution.ExecutionId).ConfigureAwait(false));
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ReplayExecutionKeepsItsFrozenWindowPins()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            var (receipt, activeRevisionId) = await RunBacklogAsync(provider, configuration, module)
                .ConfigureAwait(false);
            var descriptor = receipt.Manifest.Descriptor;
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    descriptor.Capture.CaptureId,
                    activeRevisionId,
                    descriptor.Artifact.ArtifactId),
                "rolling-window-replay-key",
                "rolling-window-test",
                CancellationToken.None).ConfigureAwait(false);
            var frozen = await ReadPinsAsync(root, replay.Execution.ExecutionId).ConfigureAwait(false);
            Assert.HasCount(WindowSize - 1, frozen.Where(static pin => pin.StartsWith(
                RollingNodeId + "|", StringComparison.Ordinal)).ToArray());

            // The newest member of the frozen window stops qualifying after submission. A replay that
            // reselected would drop it and reach one capture further back; one that honours its frozen pins
            // consumes the original members unchanged, because a pinned output is read by identity.
            using var store = CreateStore(root);
            var pinnedArtifactIds = (await store.ReadFrozenExecutionOutputsAsync(
                    replay.Execution.ExecutionId, RollingNodeId, CancellationToken.None).ConfigureAwait(false))
                .Select(static output => output.ArtifactId)
                .ToArray();
            Assert.HasCount(WindowSize - 1, pinnedArtifactIds);
            var newestPinned = (await store.ReadFrozenExecutionOutputsAsync(
                    replay.Execution.ExecutionId, RollingNodeId, CancellationToken.None).ConfigureAwait(false))[^1];
            await store.SetOutputAvailabilityAsync(
                newestPinned.OutputIdentitySha256,
                "Missing",
                "rolling-window-test",
                CancellationToken.None).ConfigureAwait(false);

            var worker = provider.GetRequiredService<ProcessingReplayWorker>();
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var detail = await operations.ReadExecutionDetailAsync(
                        replay.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                    Assert.IsNotNull(detail);
                    if (detail.Execution.Status == ProcessingGraphExecutionStatus.Completed) break;
                    Assert.AreNotEqual(ProcessingGraphExecutionStatus.Failed, detail.Execution.Status,
                        detail.Execution.FailureReason);
                    await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // The replay consumed exactly the window frozen when it was submitted, and left it unchanged.
            CollectionAssert.AreEqual(
                frozen, await ReadPinsAsync(root, replay.Execution.ExecutionId).ConfigureAwait(false));
            var replayed = await store.ReadExecutionNodeAsync(
                replay.Execution.ExecutionId,
                descriptor.Capture.CaptureId,
                RollingNodeId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(replayed);
            Assert.HasCount(1, replayed.Outputs);
            var replayedSources = replayed.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;
            Assert.HasCount(WindowSize, replayedSources);
            CollectionAssert.AreEqual(pinnedArtifactIds, replayedSources.Take(WindowSize - 1).ToArray());
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LiveExecutionSkipsAnIneligibleEarlierCaptureAndUsesAnOlderOne()
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            var (excludedReceipt, _) = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
            using var store = CreateStore(root);
            var excludedCalibration = await store.ReadNodeAsync(
                excludedReceipt.Manifest.Descriptor.Capture.CaptureId,
                "calibration",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(excludedCalibration);
            Assert.HasCount(1, excludedCalibration.Outputs);
            var excludedArtifactId = excludedCalibration.Outputs[0].ArtifactId;

            // The next capture is accepted while its predecessor is still eligible, and the predecessor stops
            // qualifying before the consuming node runs, as an evicted, failed, or still-running peer would.
            var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(
                configuration,
                await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            await store.SetOutputAvailabilityAsync(
                excludedCalibration.Outputs[0].OutputIdentitySha256,
                "Missing",
                "rolling-window-test",
                CancellationToken.None).ConfigureAwait(false);
            await DrainOneAsync(provider, configuration).ConfigureAwait(false);

            var rolling = await store.ReadNodeAsync(
                receipt.Manifest.Descriptor.Capture.CaptureId,
                RollingNodeId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            var sources = rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;

            // The ineligible capture is skipped over and an older eligible one takes its place.
            Assert.HasCount(WindowSize, sources);
            Assert.DoesNotContain(excludedArtifactId, sources);
            Assert.AreEqual(TimeSpan.FromSeconds(WindowSize), rolling.Outputs[0].TotalIntegration);
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LiveExecutionResolvesTheWindowAfterAcceptance()
    {
        // A pass-through calibration leaves the compatibility axes alone, so the raw identity would still
        // admit every earlier capture. Only the moment the window is resolved can decide this case, which
        // isolates it from the identity the window is compared against.
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration(syntheticReferences: false);
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            var (excludedReceipt, _) = await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
            using var store = CreateStore(root);
            var excludedCalibration = await store.ReadNodeAsync(
                excludedReceipt.Manifest.Descriptor.Capture.CaptureId,
                "calibration",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(excludedCalibration);
            Assert.HasCount(1, excludedCalibration.Outputs);
            var excludedArtifactId = excludedCalibration.Outputs[0].ArtifactId;

            var receipt = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(
                configuration,
                await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            await store.SetOutputAvailabilityAsync(
                excludedCalibration.Outputs[0].OutputIdentitySha256,
                "Missing",
                "rolling-window-test",
                CancellationToken.None).ConfigureAwait(false);
            await DrainOneAsync(provider, configuration).ConfigureAwait(false);

            var rolling = await store.ReadNodeAsync(
                receipt.Manifest.Descriptor.Capture.CaptureId,
                RollingNodeId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            var sources = rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;
            Assert.HasCount(WindowSize, sources);

            // A window frozen at acceptance would still name the predecessor, which was eligible then.
            Assert.DoesNotContain(excludedArtifactId, sources);
        }
        finally
        {
            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            Cleanup(root);
        }
    }

    private static async Task<ICameraModule> CreateModuleAsync(
        ServiceProvider provider,
        CameraModuleConfig configuration)
    {
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            provider.GetRequiredService<ICelestialCatalog>(),
            provider.GetRequiredService<IProjectedSceneStore>(),
            provider.GetRequiredService<IConstellationTopology>());
        await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        return module;
    }

    private static async Task<(RawCaptureReceipt Receipt, string ActiveRevisionId)> RunBacklogAsync(
        ServiceProvider provider,
        CameraModuleConfig configuration,
        ICameraModule module)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
            .ConfigureAwait(false);
        RawCaptureReceipt? receipt = null;
        for (var index = 0; index < CaptureCount; index++)
        {
            receipt = await ingress.AcceptAsync(
                configuration,
                await CreateSubmissionAsync(module, index, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
        }
        for (var index = 0; index < CaptureCount; index++)
        {
            await DrainOneAsync(provider, configuration).ConfigureAwait(false);
        }
        return (receipt!, registry.ActiveRevisionId);
    }

    private static async Task DrainOneAsync(ServiceProvider provider, CameraModuleConfig configuration)
    {
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");
        var lease = await laneStore.ClaimAsync(
            standard, "rolling-window-test", configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(
            static candidate => candidate.Lane == "standard");
        var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().NotifyLiveWorkChanged();
    }

    private static async Task<string[]> ReadPinsAsync(string root, Guid executionId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, input_ordinal, window_position, output_identity_sha256
            FROM processing_execution_output_input_pins
            WHERE execution_id = $execution
            ORDER BY node_id, input_ordinal;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        var pins = new List<string>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            pins.Add(string.Create(CultureInfo.InvariantCulture,
                $"{reader.GetString(0)}|{reader.GetInt32(1)}|{reader.GetInt32(2)}|{reader.GetString(3)}"));
        }
        return pins.ToArray();
    }

    private static async Task<long> CountUnreleasedPinsAsync(string root, Guid executionId)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM processing_execution_output_input_pins
            WHERE execution_id = $execution AND released_flag = 0;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteCaptureProcessingStore CreateStore(string root)
        => new(Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0
        }));

    private static ServiceProvider CreateProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog(
            [new CelestialCatalogObject("star", "Star", 2.5, 20, 1)]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static CameraModuleConfig CreateConfiguration(bool syntheticReferences = true)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(
                new VirtualSkyCameraModuleOptions
                {
                    MaximumResults = 10,
                    ShotNoiseEnabled = false,
                    FixedSceneUtc = FixtureUtc,
                    SyntheticCalibration = SyntheticCalibration
                })),
            new CameraRigConfig(
                new SensorProfile("rolling-window", 64, 48, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                    SensorResponseMode.Monochrome),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    PrincipalPointX: 32, PrincipalPointY: 24, ImageCircleRadiusPixels: 23),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(
                [
                    // Synthetic references are what the production smoke uses, and they deliberately change the
                    // calibrated artifact's calibration and mask compatibility axes, which is the case a
                    // pass-through calibration would not cover.
                    new CaptureProcessingStepConfig(
                        "Calibration",
                        "calibration",
                        Options: syntheticReferences
                            ? JsonSerializer.SerializeToElement(new
                            {
                                strategy = "SyntheticReferences",
                                outputVariant = "synthetic-corrected",
                                syntheticCalibration = SyntheticCalibration
                            })
                            : JsonSerializer.SerializeToElement(new { }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "RollingCombination",
                        RollingNodeId,
                        Options: JsonSerializer.SerializeToElement(new { windowSize = WindowSize }),
                        DependsOn: ["calibration"])
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            "rolling-window-agent");

    private static readonly SyntheticCalibrationModelV1 SyntheticCalibration = new()
    {
        Seed = 195,
        DarkExposure = TimeSpan.FromSeconds(1),
        FlatExposure = TimeSpan.FromSeconds(1),
        Gain = 1,
        TemperatureC = -10
    };

    private static async Task<CaptureLoopSubmission> CreateSubmissionAsync(
        ICameraModule module,
        int index,
        CancellationToken cancellationToken)
    {
        var startedUtc = FixtureUtc.AddSeconds(index * 5L);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, -10);
        var request = new CaptureRequest(startedUtc, TimeSpan.FromSeconds(2), CaptureMode.Still, setpoint);
        var result = await module.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        return new CaptureLoopSubmission(
            request,
            result with
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(1), startedUtc.AddSeconds(1.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));
    }
}
