using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
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
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            var (receipt, _) = await RunBacklogAsync(provider, configuration).ConfigureAwait(false);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            using var store = CreateStore(root);
            var rolling = await store.ReadNodeAsync(captureId, RollingNodeId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            var sources = rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds;
            Assert.HasCount(WindowSize, sources);

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
            Cleanup(root);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ReplayExecutionKeepsItsFrozenWindowPins()
    {
        var root = CreateRoot();
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            var (receipt, activeRevisionId) = await RunBacklogAsync(provider, configuration).ConfigureAwait(false);
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
            using var store = CreateStore(root);
            var replayed = await store.ReadExecutionNodeAsync(
                replay.Execution.ExecutionId,
                descriptor.Capture.CaptureId,
                RollingNodeId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(replayed);
            Assert.HasCount(1, replayed.Outputs);
            Assert.HasCount(WindowSize, replayed.Outputs[0].Descriptor!.Artifact.SourceArtifactIds);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task<(RawCaptureReceipt Receipt, string ActiveRevisionId)> RunBacklogAsync(
        ServiceProvider provider,
        CameraModuleConfig configuration)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
            .ConfigureAwait(false);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var laneHandler = provider.GetServices<ICaptureLaneHandler>().Single(
            static handler => handler.Lane == "standard");
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");

        RawCaptureReceipt? receipt = null;
        for (var index = 0; index < CaptureCount; index++)
        {
            receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(index), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
        }
        for (var index = 0; index < CaptureCount; index++)
        {
            var lease = await laneStore.ClaimAsync(
                standard, "rolling-window-test", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var result = await laneHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            operations.NotifyLiveWorkChanged();
        }
        return (receipt!, registry.ActiveRevisionId);
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

    private static CameraModuleConfig CreateConfiguration()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("rolling-window", 4, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("rolling-window", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig(
                        "Calibration",
                        "calibration",
                        Options: JsonSerializer.SerializeToElement(new { }),
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

    private static CaptureLoopSubmission CreateSubmission(int index)
    {
        var startedUtc = FixtureUtc.AddSeconds(index * 5L);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var payload = new byte[16];
        for (var offset = 0; offset < payload.Length; offset++)
        {
            payload[offset] = (byte)(offset + index);
        }
        var frame = new CameraFrame(
            startedUtc,
            4,
            2,
            CameraPixelFormat.Mono16,
            payload,
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 10, "rolling-window"));
        return new CaptureLoopSubmission(
            new CaptureRequest(startedUtc, TimeSpan.FromSeconds(2), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(1), startedUtc.AddSeconds(1.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));
    }
}
