using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[DoNotParallelize]
public sealed class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task CompletedNode_RestartRestoresExactOutputWithoutReexecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root, includeCycleEvidence: true).ConfigureAwait(false);
            CaptureLaneHandlerResult first;
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
                var firstStep = new ProducingStep();
                var firstNode = CreateNode(firstStep);
                first = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([firstNode]),
                    persistence,
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, firstStep.ExecutionCount);
            }

            CaptureLaneHandlerResult second;
            DurableProcessingNode? durable;
            var restartedStep = new ProducingStep();
            var restartedNode = CreateNode(restartedStep);
            using (var telemetry = new CaptureProcessingTelemetry())
            using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            using (var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance))
            {
                second = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    new CaptureProcessingGraph([restartedNode]),
                    CreatePersistence(fixture.Options, store, storage, telemetry),
                    telemetry,
                    2,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                durable = await store.ReadNodeAsync(
                    fixture.Manifest.Descriptor.Capture.CaptureId,
                    restartedNode.Id,
                    CancellationToken.None).ConfigureAwait(false);
            }

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, first.Outcome);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, second.Outcome);
            Assert.AreEqual(0, restartedStep.ExecutionCount);
            Assert.IsNotNull(durable);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, durable.Status);
            Assert.HasCount(1, durable.Outputs);
            var output = durable.Outputs[0];
            Assert.AreEqual(fixture.Manifest.Descriptor.CycleEvidence, output.Descriptor.CycleEvidence);
            Assert.AreEqual(fixture.Manifest.Descriptor.Artifact.ArtifactId, output.Descriptor.Artifact.SourceArtifactIds.Single());
            Assert.IsTrue(File.Exists(Path.Combine(root, output.PayloadRelativePath)));
            Assert.IsTrue(File.Exists(Path.Combine(root, output.SidecarRelativePath)));
            var sidecar = CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(Path.Combine(root, output.SidecarRelativePath)).ConfigureAwait(false));
            Assert.IsTrue(sidecar.IsValid, sidecar.Validation.ReasonCode);
            Assert.AreEqual(
                fixture.Manifest.Descriptor.CycleEvidence,
                sidecar.Document!.Manifest!.Descriptor.CycleEvidence);
            Assert.AreEqual(1, Directory.EnumerateFiles(
                Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task PublishedOutputWithoutSqliteCommit_ReplayConvergesExactEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var step = new ProducingStep();
            var graph = new CaptureProcessingGraph([CreateNode(step)]);

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item,
                    graph,
                    CreatePersistence(fixture.Options, store, new ThrowAfterSaveStorage(storage), telemetry),
                    telemetry,
                    1,
                    NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            var replay = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item,
                graph,
                CreatePersistence(fixture.Options, store, storage, telemetry),
                telemetry,
                2,
                NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replay.Outcome);
            Assert.HasCount(1, Directory.EnumerateFiles(
                Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories));
            var durable = await store.ReadNodeAsync(
                fixture.Manifest.Descriptor.Capture.CaptureId,
                "normalize",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(durable);
            Assert.HasCount(1, durable.Outputs);
            Assert.IsNull(durable.Outputs[0].Descriptor.CycleEvidence);
            var sidecar = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(
                Path.Combine(root, durable.Outputs[0].SidecarRelativePath)).ConfigureAwait(false));
            Assert.IsTrue(sidecar.IsValid, sidecar.Validation.ReasonCode);
            Assert.IsNull(sidecar.Document!.Manifest!.Descriptor.CycleEvidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task StaleLaneLease_CannotCommitProcessingNode()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
            using var store = new SqliteCaptureProcessingStore(options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE capture_lane_work(
                        work_id INTEGER PRIMARY KEY,
                        state TEXT NOT NULL,
                        lease_token TEXT NULL,
                        lease_expires_unix_ms INTEGER NULL);
                    INSERT INTO capture_lane_work(work_id, state, lease_token, lease_expires_unix_ms)
                    VALUES (1, 'leased', 'current-token', 4102444800000);
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var step = new CountingStep();
            var node = new CaptureProcessingGraphNode(
                "node", step, [], true, null, null, null, new string('E', 64));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await store.WriteNodeAsync(
                    Guid.NewGuid(), node, DurableProcessingNodeStatus.Completed, null, 1,
                    1, "stale-token", [], CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE capture_lane_work SET lease_expires_unix_ms = 0 WHERE work_id = 1;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await store.WriteNodeAsync(
                    Guid.NewGuid(), node, DurableProcessingNodeStatus.Completed, null, 1,
                    1, "current-token", [], CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RawWindowHistory_IsAgentBoundedAndRetainsLatestHundredInputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
            using var store = new SqliteCaptureProcessingStore(options);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE raw_captures(
                        raw_capture_row_id INTEGER PRIMARY KEY,
                        capture_id TEXT NOT NULL,
                        raw_artifact_id TEXT NOT NULL,
                        agent_id TEXT NOT NULL,
                        capture_sequence INTEGER NOT NULL,
                        payload_relative_path TEXT NOT NULL,
                        sidecar_relative_path TEXT NOT NULL,
                        manifest_json BLOB NOT NULL,
                        state TEXT NOT NULL);
                    CREATE TABLE capture_lane_work(
                        raw_capture_row_id INTEGER NOT NULL,
                        lane_name TEXT NOT NULL,
                        state TEXT NOT NULL);
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                for (var sequence = 1; sequence <= 101; sequence++)
                {
                    var template = ReconstructableCaptureContractTests.CreateManifest(
                        CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
                    var descriptor = template.Descriptor with
                    {
                        Capture = template.Descriptor.Capture with
                        {
                            AgentId = "agent-a",
                            CaptureSequence = sequence,
                            CaptureId = Guid.Parse($"40000000-0000-0000-0000-{sequence:D12}")
                        },
                        Artifact = template.Descriptor.Artifact with
                        {
                            ArtifactId = Guid.Parse($"50000000-0000-0000-0000-{sequence:D12}")
                        }
                    };
                    var manifest = new ArtifactManifestV2(
                        ArtifactManifestV2.CurrentSchemaVersion, descriptor, $"raw/{sequence}.bin");
                    using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO raw_captures(
                            raw_capture_row_id, capture_id, raw_artifact_id, agent_id, capture_sequence,
                            payload_relative_path, sidecar_relative_path, manifest_json, state)
                        VALUES ($row, $capture, $artifact, $agent, $sequence, $payload, $sidecar, $manifest, 'committed');
                        """;
                    insert.Parameters.AddWithValue("$row", sequence);
                    insert.Parameters.AddWithValue("$capture", descriptor.Capture.CaptureId.ToString("N"));
                    insert.Parameters.AddWithValue("$artifact", descriptor.Artifact.ArtifactId.ToString("N"));
                    insert.Parameters.AddWithValue("$agent", descriptor.Capture.AgentId);
                    insert.Parameters.AddWithValue("$sequence", sequence);
                    insert.Parameters.AddWithValue("$payload", manifest.RelativeArtifactPath);
                    insert.Parameters.AddWithValue("$sidecar", $"raw/{sequence}.json");
                    insert.Parameters.AddWithValue("$manifest", CaptureContractJson.Serialize(manifest));
                    await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            var history = await store.ReadRecentRawInputsAsync(
                "agent-a", 100, 5, CancellationToken.None).ConfigureAwait(false);
            var holds = await store.ReadRetentionHoldsAsync(CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new long[] { 100, 99, 98, 97, 96 },
                history.Select(static entry => entry.Descriptor.Capture.CaptureSequence).ToArray());
            Assert.HasCount(100, holds);
            Assert.IsFalse(holds.Any(static hold => hold.PayloadRelativePath == "raw/1.bin"));
            Assert.IsTrue(holds.Any(static hold => hold.PayloadRelativePath == "raw/101.bin"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RollingWindow_RestartContinuesFromDurableCompatibleHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = await CreateFixtureAsync(
                root, 1, "first", new byte[] { 100, 0, 100, 0, 100, 0, 100, 0 }).ConfigureAwait(false);
            await ProcessCanonicalGraphAsync(first).ConfigureAwait(false);

            var second = await CreateFixtureAsync(
                root, 2, "second", new byte[] { 44, 1, 44, 1, 44, 1, 44, 1 }).ConfigureAwait(false);
            await ProcessCanonicalGraphAsync(second).ConfigureAwait(false);

            using var store = new SqliteCaptureProcessingStore(second.Options);
            var rolling = await store.ReadNodeAsync(
                second.Manifest.Descriptor.Capture.CaptureId,
                "rolling",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            Assert.HasCount(2, rolling.Outputs[0].Descriptor.Artifact.SourceArtifactIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CaptureProcessingPersistence CreatePersistence(
        IOptions<CameraAgentHostOptions> options,
        SqliteCaptureProcessingStore store,
        IFrameStorageService storage,
        CaptureProcessingTelemetry telemetry)
        => new(options, store, storage, telemetry, NullLogger<CaptureProcessingPersistence>.Instance);

    private static CaptureProcessingGraphNode CreateNode(ProducingStep step)
        => new("normalize", step, [], true, step.RecipeName, step.OutputRole, step.OutputVariant, new string('D', 64));

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalTerminalNode_DoesNotBlockIndependentRequiredNode()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var optional = new OutcomeStep(ProcessingOutcome.TerminalFailure("test.optional-terminal"));
        var required = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", optional, [], false, "optional", FrameArtifactRole.Metadata, "optional"),
            new CaptureProcessingGraphNode("required", required, [], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, required.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalRetryableNode_CommitsIndependentWorkAndRetriesLane()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var optional = new OutcomeStep(ProcessingOutcome.RetryableFailure("test.optional-retry"));
        var required = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", optional, [], false, "optional", FrameArtifactRole.Metadata, "optional"),
            new CaptureProcessingGraphNode("required", required, [], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome);
        Assert.AreEqual("test.optional-retry", result.Reason);
        Assert.AreEqual(1, required.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalRetryableNode_OnLastAttemptDoesNotQuarantineRequiredWork()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var optional = new OutcomeStep(ProcessingOutcome.RetryableFailure("test.optional-retry"));
        var required = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", optional, [], false, "optional", FrameArtifactRole.Metadata, "optional"),
            new CaptureProcessingGraphNode("required", required, [], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 5, NullLogger.Instance, CancellationToken.None, 5).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
        Assert.AreEqual(1, required.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RequiredRetryableNode_ReturnsRetryWithoutExecutingDependentNode()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var retry = new OutcomeStep(ProcessingOutcome.RetryableFailure("test.retry"));
        var dependent = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("retry", retry, [], true, "retry", FrameArtifactRole.Metadata, "retry"),
            new CaptureProcessingGraphNode("dependent", dependent, ["retry"], true, null, null, null)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome);
        Assert.AreEqual("test.retry", result.Reason);
        Assert.AreEqual(0, dependent.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task OptionalRetryableDependency_RunsDependentAfterRetrySucceeds()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var retryOnce = new RetryOnceStep();
        var dependent = new CountingStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("optional", retryOnce, [], false, null, null, null),
            new CaptureProcessingGraphNode("dependent", dependent, ["optional"], true, null, null, null)
        ]);

        var first = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
        var second = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 2, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, first.Outcome);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, second.Outcome);
        Assert.AreEqual(1, dependent.ExecutionCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CanonicalTerminalOutcome_RemainsTerminal()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        var step = new CalibrationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("calibration", "calibration", 0),
            new CalibrationProcessingStepOptions(),
            new CameraAgentRecipeExecutionAdapter(new FixedOutcomeExecutor(
                ProcessingOutcome.TerminalFailure("test.canonical-terminal"))));
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode(
                "calibration", step, [], true, step.RecipeName, step.OutputRole, step.OutputVariant)
        ]);

        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            CreateEphemeralItem(), graph, null, telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Outcome);
        Assert.AreEqual("test.canonical-terminal", result.Reason);
    }

    private static async Task<Fixture> CreateFixtureAsync(string root, bool includeCycleEvidence = false)
    {
        var payload = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        if (includeCycleEvidence)
        {
            manifest = manifest with
            {
                Descriptor = manifest.Descriptor with
                {
                    CycleEvidence = CreateCycleEvidence(manifest.Descriptor)
                }
            };
        }
        var payloadPath = Path.Combine(root, "raw.bin");
        await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.ChangeExtension(payloadPath, ".json"),
            CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
        var stored = new StoredFrameReference("raw.bin", payloadPath, manifest.Descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw);
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest, stored);
        var reconstruction = FrameReconstructor.TryReconstruct(manifest.Descriptor, payload, out var frame);
        Assert.IsTrue(reconstruction.IsValid);
        var config = CreateConfig();
        var submission = CreateSubmission(frame!) with
        {
            CycleEvidence = manifest.Descriptor.CycleEvidence
        };
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0
        });
        return new Fixture(
            manifest,
            options,
            new FrameProcessingItem(config, submission, receipt));
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root,
        long sequence,
        string suffix,
        byte[] payload)
    {
        var template = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var captureId = Guid.Parse($"20000000-0000-0000-0000-{sequence:D12}");
        var artifactId = Guid.Parse($"30000000-0000-0000-0000-{sequence:D12}");
        var descriptor = template.Descriptor with
        {
            Capture = template.Descriptor.Capture with
            {
                CaptureSequence = sequence,
                CaptureId = captureId
            },
            Artifact = template.Descriptor.Artifact with { ArtifactId = artifactId }
        };
        var manifest = new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion,
            descriptor,
            $"{suffix}.bin");
        var payloadPath = Path.Combine(root, $"{suffix}.bin");
        await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.ChangeExtension(payloadPath, ".json"),
            CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
        var reconstruction = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame);
        Assert.IsTrue(reconstruction.IsValid);
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
        return new Fixture(
            manifest,
            options,
            new FrameProcessingItem(
                CreateConfig(),
                CreateSubmission(frame!),
                new RawCaptureReceipt(
                    RawIngressOutcome.Committed,
                    manifest,
                    new StoredFrameReference($"{suffix}.bin", payloadPath, descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw))));
    }

    private static async Task ProcessCanonicalGraphAsync(Fixture fixture)
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        var adapter = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var calibration = new CalibrationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("calibration", "calibration", 0),
            new CalibrationProcessingStepOptions(),
            adapter);
        var rolling = new RollingCombinationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("rolling", "rolling", 1),
            new RollingCombinationProcessingStepOptions { WindowSize = 2 },
            adapter);
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode(
                "calibration", calibration, [], true, calibration.RecipeName,
                calibration.OutputRole, calibration.OutputVariant, new string('C', 64)),
            new CaptureProcessingGraphNode(
                "rolling", rolling, ["calibration"], true, rolling.RecipeName,
                rolling.OutputRole, rolling.OutputVariant, new string('R', 64))
        ]);
        var result = await FrameProcessingWorker.ProcessGraphItemAsync(
            fixture.Item,
            graph,
            CreatePersistence(fixture.Options, store, storage, telemetry),
            telemetry,
            1,
            NullLogger.Instance,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
    }

    private static FrameProcessingItem CreateEphemeralItem()
    {
        var frame = new CameraFrame(
            DateTimeOffset.UnixEpoch,
            2,
            2,
            CameraPixelFormat.Mono8,
            new byte[4],
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        return new FrameProcessingItem(CreateConfig(), CreateSubmission(frame));
    }

    private static CameraModuleConfig CreateConfig()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Test", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            AgentId: "agent-test");

    private static CaptureLoopSubmission CreateSubmission(CameraFrame frame)
        => new(
            new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            frame.TimestampUtc,
            frame.Metadata.Exposure,
            TimeSpan.Zero);

    private static CaptureCycleEvidence CreateCycleEvidence(ReconstructionDescriptor descriptor)
    {
        var decisionStartedUtc = descriptor.Timing.ReadoutCompletedUtc.AddMilliseconds(100);
        return new CaptureCycleEvidence(
            CaptureCadenceMode.MinimumStartInterval,
            CaptureStartReason.DeadlineReached,
            AutomaticControlOwnership.Disabled,
            AutomaticControlOwnership.Disabled,
            null,
            descriptor.Timing.RequestedStartUtc.AddMilliseconds(500),
            TimeSpan.FromSeconds(1),
            null,
            new CaptureControlDecisionEvidence(
                decisionStartedUtc,
                decisionStartedUtc.AddMilliseconds(100),
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveGain,
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveGain,
                CaptureControlDecisionReason.Disabled),
            decisionStartedUtc.AddMilliseconds(200));
    }

    private sealed class ProducingStep : ICaptureProcessingStep, ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public int ExecutionCount { get; private set; }
        public string Name => "normalize";
        public int Order => 0;
        public string RecipeName => "test-normalization";
        public FrameArtifactRole OutputRole => FrameArtifactRole.Calibrated;
        public string OutputVariant => "none";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var raw = context.Artifacts!.Raw;
            var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                RecipeName, "1.0.0", "test-v1", JsonSerializer.SerializeToElement(new { mode = "none" })));
            var sources = new[] { raw.ArtifactId };
            var payload = raw.Frame.PixelData.ToArray();
            var product = new ProcessingProduct(
                OutputRole,
                OutputVariant,
                ProcessingIdentity.CreateOutputIdentity(OutputRole, OutputVariant, recipe.IdentitySha256, sources),
                "application/x-hvo-linear-frame",
                context.RawCapture!.Manifest.Descriptor.Layout,
                payload,
                ProcessingIdentity.ComputePayloadSha256(payload),
                recipe,
                [new ProcessingAlgorithmIdentity("test", "v1")],
                sources,
                raw.Frame.Metadata.Exposure,
                CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source").Compatibility);
            var artifact = context.AddDerivative(
                OutputRole,
                raw.Frame with { Metadata = raw.Frame.Metadata with { SourceId = "normalize" } },
                "test-v1",
                sources,
                CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
            context.AssociateProcessingProduct(artifact, product);
            context.AddProcessingOutcome(ProcessingOutcome.Produced(product));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OutcomeStep(ProcessingOutcome outcome) : ICaptureProcessingStep
    {
        public string Name => "outcome";
        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            context.AddProcessingOutcome(outcome);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingStep : ICaptureProcessingStep
    {
        public int ExecutionCount { get; private set; }
        public string Name => "counting";
        public int Order => 1;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RetryOnceStep : ICaptureProcessingStep
    {
        private int _attempt;

        public string Name => "retry-once";
        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempt) == 1)
            {
                context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure("test.retry-once"));
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedOutcomeExecutor(ProcessingOutcome outcome) : IProcessingRecipeExecutor
    {
        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(outcome);
    }

    private sealed class ThrowAfterSaveStorage(IFrameStorageService inner) : IFrameStorageService
    {
        public ValueTask<StoredFrameReference> SaveAsync(
            string storageRoot,
            FrameArtifact artifact,
            CancellationToken cancellationToken) => inner.SaveAsync(storageRoot, artifact, cancellationToken);

        public async ValueTask<StoredFrameReference> SaveAsync(
            string storageRoot,
            FrameArtifact artifact,
            ReconstructionDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            _ = await inner.SaveAsync(storageRoot, artifact, descriptor, cancellationToken).ConfigureAwait(false);
            throw new IOException("Injected failure after immutable publication.");
        }

        public ValueTask RemoveAsync(
            string storageRoot,
            StoredFrameReference storedFrame,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            inner.RemoveAsync(storageRoot, storedFrame, artifactId, cancellationToken);

        public IReadOnlyList<StoredFrameReference> List(
            string storageRoot,
            DateOnly utcDate,
            FrameArtifactRole? role,
            int maximumResults) => inner.List(storageRoot, utcDate, role, maximumResults);
    }

    private sealed record Fixture(
        ArtifactManifestV2 Manifest,
        IOptions<CameraAgentHostOptions> Options,
        FrameProcessingItem Item);
}
