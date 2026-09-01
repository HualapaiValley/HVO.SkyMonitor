using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task LiveAcceptanceAndReplayLifecycleAreDurableAndIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-executions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var values = new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:RawIngressSqliteBusyTimeoutSeconds"] = "2",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCameraAgentInfrastructure(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            using var provider = services.BuildServiceProvider();
            var rawIngress = provider.GetRequiredService<IRawCaptureIngress>();
            await rawIngress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var configuration = CreateConfiguration();
            var registry = await operations.EnsureConfiguredBasicAsync(
                configuration, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(ProcessingGraphRegistryMode.ConfiguredBasic, registry.Mode);
            Assert.AreEqual(registry.ActiveRevisionId, registry.ConfiguredBasicRevisionId);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Active, registry.Revisions.Single().Lifecycle);

            var submission = CreateSubmission();
            using var replayPreemption = new CancellationTokenSource();
            using var replayPreemptionRegistration = provider.GetRequiredService<ProcessingReplayWakeup>()
                .RegisterLivePreemption(replayPreemption);
            var receipt = await rawIngress.AcceptAsync(
                configuration, submission, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
            Assert.IsTrue(replayPreemption.IsCancellationRequested);

            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var liveExecutions = await store.ReadExecutionsAsync(
                ProcessingGraphExecutionClass.Live, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, liveExecutions);
            var live = liveExecutions[0];
            Assert.AreEqual(receipt.Manifest.Descriptor.Capture.CaptureId, live.CaptureId);
            Assert.AreEqual(registry.ActiveRevisionId, live.GraphRevisionId);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Pending, live.Status);

            var replayRequest = new ProcessingReplaySubmission(
                live.CaptureId,
                registry.ActiveRevisionId,
                live.PrimaryArtifactId,
                TriggerReference: "operator-test",
                Reason: "test replay");
            var replay = await operations.SubmitReplayAsync(
                replayRequest, "replay-key", "owner-test", CancellationToken.None).ConfigureAwait(false);
            var replayed = await operations.SubmitReplayAsync(
                replayRequest, "replay-key", "owner-test", CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(replay.Replayed);
            Assert.IsTrue(replayed.Replayed);
            Assert.AreEqual(replay.Execution.ExecutionId, replayed.Execution.ExecutionId);

            Assert.IsNull(await store.ClaimReplayAsync("worker-1", CancellationToken.None).ConfigureAwait(false));

            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            var liveLease = await laneStore.ClaimAsync(
                standard, "live-worker", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(liveLease);
            Assert.IsNotNull(liveLease.Context.Execution);
            Assert.AreEqual(live.ExecutionId, liveLease.Context.Execution.ExecutionId);
            Assert.AreEqual(
                CaptureLaneHandlerOutcome.TerminalFailure,
                await laneStore.FailAsync(
                    liveLease, CaptureLaneHandlerResult.Terminal("processing-test-terminal"),
                    CancellationToken.None).ConfigureAwait(false));
            var failedLive = await operations.ReadExecutionAsync(
                live.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(failedLive);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Failed, failedLive.Status);

            var replayLease = await store.ClaimReplayAsync("worker-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(replayLease);
            Assert.AreEqual(replay.Execution.ExecutionId, replayLease.Execution.ExecutionId);
            Assert.IsFalse(replayLease.Execution.CancellationRequested);
            Assert.AreEqual(registry.ActiveRevisionId, replayLease.Revision.State.RevisionId);

            var requestedCancellation = await operations.CancelReplayAsync(
                replay.Execution.ExecutionId,
                "cancel-key",
                "owner-test",
                "cancel test",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(requestedCancellation.CancellationRequested);
            await store.CompleteReplayAsync(
                replayLease, CaptureLaneHandlerResult.Success, CancellationToken.None).ConfigureAwait(false);
            var cancelled = await operations.ReadExecutionAsync(
                replay.Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(cancelled);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Cancelled, cancelled.Status);

            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM processing_executions WHERE execution_class = 'Live'),
                    (SELECT COUNT(*) FROM processing_executions WHERE execution_class = 'Replay'),
                    (SELECT COUNT(*) FROM processing_execution_input_pins
                     WHERE execution_id = $replay AND released_flag = 1),
                    (SELECT COUNT(*) FROM processing_graph_commands);
                """;
            command.Parameters.AddWithValue("$replay", replay.Execution.ExecutionId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(1L, reader.GetInt64(0));
            Assert.AreEqual(1L, reader.GetInt64(1));
            Assert.AreEqual(1L, reader.GetInt64(2));
            Assert.AreEqual(2L, reader.GetInt64(3));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task NamedRevisionLifecycleUsesOptimisticVersionAndIdempotency()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-registry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CameraAgent:RawIngressRoot"] = root,
                    ["CameraAgent:RawIngressReserveBytes"] = "0"
                }).Build());
            using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IRawCaptureIngress>()
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var configuration = CreateConfiguration();
            var initial = await operations.EnsureConfiguredBasicAsync(
                configuration, CancellationToken.None).ConfigureAwait(false);
            var named = await operations.CreateRevisionAsync(
                "nightly", "2026-08-31", configuration.Pipeline,
                "create-key", "owner-test", "named test", CancellationToken.None).ConfigureAwait(false);
            var replayed = await operations.CreateRevisionAsync(
                "nightly", "2026-08-31", configuration.Pipeline,
                "create-key", "owner-test", "named test", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(named.RevisionId, replayed.RevisionId);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, named.Lifecycle);

            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(
                    named.RevisionId, initial.StateVersion, "draft-activate-key", "owner-test", null,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var validated = await operations.ValidateRevisionAsync(
                named.RevisionId, "validate-key", "owner-test", "validation test", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Validated, validated.Lifecycle);

            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(
                    named.RevisionId, initial.StateVersion + 1, "stale-key", "owner-test", null,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(
                named.RevisionId, initial.StateVersion, "activate-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRegistryMode.Named, active.Mode);
            Assert.AreEqual(named.RevisionId, active.ActiveRevisionId);
            Assert.AreEqual(initial.StateVersion + 1, active.StateVersion);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.RetireRevisionAsync(
                    named.RevisionId, active.StateVersion, "retire-key", "owner-test", null,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var retirable = await operations.CreateRevisionAsync(
                "nightly", "retire-me", configuration.Pipeline,
                "create-retirable-key", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            _ = await operations.ValidateRevisionAsync(
                retirable.RevisionId, "validate-retirable-key", "owner-test", null, CancellationToken.None)
                .ConfigureAwait(false);
            var retired = await operations.RetireRevisionAsync(
                retirable.RevisionId, active.StateVersion, "retire-retirable-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.RetireRevisionAsync(
                    retirable.RevisionId, retired.StateVersion, "retire-again-key", "owner-test", null,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplayFreezesTrailingRawWindowAtSubmission()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-replay-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CameraAgent:RawIngressRoot"] = root,
                    ["CameraAgent:RawIngressReserveBytes"] = "0",
                    ["CameraAgent:ProcessingGraphs:ReplayMaximumPendingBytes"] = "30"
                }).Build());
            using var provider = services.BuildServiceProvider();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var baseConfiguration = CreateConfiguration();
            var configuration = baseConfiguration with
            {
                Rig = baseConfiguration.Rig with
                {
                    Sensor = baseConfiguration.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 }
                },
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig(
                        "RollingCombination",
                        "rolling",
                        Options: JsonSerializer.SerializeToElement(new { windowSize = 3, outputVariant = "rolling-mean" }),
                        DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var receipts = new List<RawCaptureReceipt>();
            for (var index = 0; index < 3; index++)
            {
                var receipt = await ingress.AcceptAsync(
                    configuration, CreateSubmission(index, CameraPixelFormat.Mono16), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                receipts.Add(receipt);
            }
            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipts[^1].Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId,
                    receipts[^1].Manifest.Descriptor.Artifact.ArtifactId,
                    Reason: "freeze test"),
                "window-replay-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);

            _ = await ingress.AcceptAsync(
                configuration, CreateSubmission(3, CameraPixelFormat.Mono16), CancellationToken.None).ConfigureAwait(false);
            var processingStore = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var frozen = await processingStore
                .ReadFrozenReplayRawInputsAsync(replay.Execution.ExecutionId, "rolling", CancellationToken.None)
                .ConfigureAwait(false);

            Assert.HasCount(3, frozen);
            CollectionAssert.AreEqual(
                receipts.Select(static receipt => receipt.Manifest.Descriptor.Artifact.ArtifactId).ToArray(),
                frozen.Select(static input => input.Descriptor.Artifact.ArtifactId).ToArray());
            CollectionAssert.AreEqual(new[] { -2, -1, 0 }, frozen.Select(static input => input.WindowPosition).ToArray());
            var operational = await processingStore.ReadOperationalStateAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(24L, operational.ReplayPendingBytes);
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM processing_execution_input_pins WHERE execution_id = $execution AND released_flag = 0;";
            command.Parameters.AddWithValue("$execution", replay.Execution.ExecutionId.ToString("N"));
            Assert.AreEqual(3L, Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture));
            await Assert.ThrowsExactlyAsync<ProcessingReplayCapacityException>(async () =>
                await operations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(
                        receipts[^1].Manifest.Descriptor.Capture.CaptureId,
                        registry.ActiveRevisionId,
                        receipts[^1].Manifest.Descriptor.Artifact.ArtifactId,
                        TriggerReference: "capacity-second"),
                    "window-capacity-key",
                    "owner-test",
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE processing_execution_nodes
                SET status = 'Running', attempt_count = 1, started_unix_ms = 1
                WHERE execution_id = $execution AND node_id = 'rolling';
                INSERT INTO processing_node_attempts(
                    execution_id, node_id, attempt_number, lease_owner, lease_token, started_unix_ms, status)
                VALUES ($execution, 'rolling', 1, 'expired-test', 'expired-token', 1, 'Running');
                UPDATE processing_executions SET started_unix_ms = 1, deadline_unix_ms = 1 WHERE execution_id = $execution;
                """;
            command.Parameters.AddWithValue("$execution", replay.Execution.ExecutionId.ToString("N"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            Assert.IsNull(await provider.GetRequiredService<SqliteCaptureProcessingStore>()
                .ClaimReplayAsync("expiry-worker", CancellationToken.None).ConfigureAwait(false));
            var expired = await operations.ReadExecutionDetailAsync(
                replay.Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(expired);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Expired, expired.Execution.Status);
            Assert.AreEqual("TerminalFailure", expired.Nodes.Single().Status);
            Assert.AreEqual("Interrupted", expired.Nodes.Single().Attempts.Single().Status);
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            CaptureLaneLease? liveLease;
            while ((liveLease = await laneStore.ClaimAsync(
                       standard, "retention-test", configuration, CancellationToken.None).ConfigureAwait(false)) is not null)
            {
                await laneStore.CompleteAsync(liveLease, CancellationToken.None).ConfigureAwait(false);
            }
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;";
            Assert.AreEqual(0L, Convert.ToInt64(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
            var queuedPastInitialDeadline = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipts[^1].Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId,
                    receipts[^1].Manifest.Descriptor.Artifact.ArtifactId,
                    TriggerReference: "queued-past-initial-deadline"),
                "queued-past-initial-deadline-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);
            command.Parameters.Clear();
            command.CommandText = "UPDATE processing_executions SET deadline_unix_ms = 1 WHERE execution_id = $execution;";
            command.Parameters.AddWithValue("$execution", queuedPastInitialDeadline.Execution.ExecutionId.ToString("N"));
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            var queuedLease = await provider.GetRequiredService<SqliteCaptureProcessingStore>()
                .ClaimReplayAsync("queue-deadline-worker", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(queuedLease);
            Assert.AreEqual(queuedPastInitialDeadline.Execution.ExecutionId, queuedLease.Execution.ExecutionId);
            _ = await operations.CancelReplayAsync(
                queuedPastInitialDeadline.Execution.ExecutionId, "queue-deadline-cancel-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            await provider.GetRequiredService<SqliteCaptureProcessingStore>().CompleteReplayAsync(
                queuedLease, CaptureLaneHandlerResult.Success, CancellationToken.None).ConfigureAwait(false);
            var centeredPipeline = new CapturePipelineConfig(
                [new CaptureProcessingStepConfig(
                    "RollingCombination",
                    "rolling",
                    Options: JsonSerializer.SerializeToElement(new
                    {
                        windowSize = 3,
                        windowKind = "Centered",
                        outputVariant = "centered-mean"
                    }),
                    DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent);
            var centeredRevision = await operations.CreateRevisionAsync(
                "centered", "v1", centeredPipeline, "centered-create-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            _ = await operations.ValidateRevisionAsync(
                centeredRevision.RevisionId, "centered-validate-key", "owner-test", null, CancellationToken.None)
                .ConfigureAwait(false);
            var currentRegistry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(
                    centeredRevision.RevisionId, currentRegistry.StateVersion, "centered-activate-key", "owner-test", null,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var centeredReplay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipts[1].Manifest.Descriptor.Capture.CaptureId,
                    centeredRevision.RevisionId,
                    receipts[1].Manifest.Descriptor.Artifact.ArtifactId,
                    TriggerReference: "centered-replay"),
                "centered-replay-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);
            var centered = await provider.GetRequiredService<SqliteCaptureProcessingStore>()
                .ReadFrozenReplayRawInputsAsync(centeredReplay.Execution.ExecutionId, "rolling", CancellationToken.None)
                .ConfigureAwait(false);
            CollectionAssert.AreEqual(
                new[] { -1, 0, 1 }, centered.Select(static input => input.WindowPosition).ToArray());
            CollectionAssert.AreEqual(
                receipts.Take(3).Select(static receipt => receipt.Manifest.Descriptor.Artifact.ArtifactId).ToArray(),
                centered.Select(static input => input.Descriptor.Artifact.ArtifactId).ToArray());
            _ = await operations.CancelReplayAsync(
                centeredReplay.Execution.ExecutionId, "centered-cancel-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            File.Delete(receipts[0].StoredFrame.AbsolutePath);
            var idempotentReplay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipts[^1].Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId,
                    receipts[^1].Manifest.Descriptor.Artifact.ArtifactId,
                    Reason: "freeze test"),
                "window-replay-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(idempotentReplay.Replayed);
            Assert.AreEqual(replay.Execution.ExecutionId, idempotentReplay.Execution.ExecutionId);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(
                        receipts[^1].Manifest.Descriptor.Capture.CaptureId,
                        registry.ActiveRevisionId,
                        receipts[^1].Manifest.Descriptor.Artifact.ArtifactId,
                        TriggerReference: "missing-history"),
                    "missing-history-key",
                    "owner-test",
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplayWakeupRetainsOneSignalPerConfiguredWorker()
    {
        var wakeup = new ProcessingReplayWakeup(Options.Create(new CameraAgentHostOptions
        {
            ProcessingGraphs = new ProcessingGraphExecutionOptions { ReplayMaximumConcurrency = 2 }
        }));
        wakeup.Signal();
        wakeup.Signal();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Task.WhenAll(
            wakeup.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, timeout.Token).AsTask(),
            wakeup.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System, timeout.Token).AsTask()).ConfigureAwait(false);
        using var livePreemption = new CancellationTokenSource();
        using var registration = wakeup.RegisterLivePreemption(livePreemption);
        wakeup.SignalLiveWork();
        Assert.IsTrue(livePreemption.IsCancellationRequested);
    }

    [TestMethod]
    public async Task PendingLiveExecutionExpiresAtMaximumQueueAge()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-live-queue-expiry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
            using var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:LiveDeadlineSeconds"] = "100",
                ["CameraAgent:ProcessingGraphs:LiveMaximumQueueAgeSeconds"] = "10"
            }, clock);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var configuration = CreateConfiguration();
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(receipt);

            clock.Advance(TimeSpan.FromSeconds(11));
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            Assert.IsNull(await laneStore.ClaimAsync(
                standard, "queue-expiry-test", configuration, CancellationToken.None).ConfigureAwait(false));

            var execution = (await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                .ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 10, CancellationToken.None)
                .ConfigureAwait(false)).Single();
            Assert.AreEqual(ProcessingGraphExecutionStatus.Expired, execution.Status);
            Assert.AreEqual("processing.live-maximum-age", execution.FailureReason);
            Assert.IsNull(execution.StartedUtc);
            Assert.AreEqual(0, execution.AttemptCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LiveProcessingDeadlineStartsAtFirstClaimAndSurvivesRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-live-deadline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var acceptedUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var clock = new MutableTimeProvider(acceptedUtc);
            using var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:LiveDeadlineSeconds"] = "10",
                ["CameraAgent:ProcessingGraphs:LiveMaximumQueueAgeSeconds"] = "100"
            }, clock);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var configuration = CreateConfiguration();
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(receipt);

            clock.Advance(TimeSpan.FromSeconds(20));
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            var firstLease = await laneStore.ClaimAsync(
                standard, "deadline-test-1", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(firstLease);
            var running = (await operations.ReadExecutionsAsync(
                ProcessingGraphExecutionClass.Live, 10, CancellationToken.None).ConfigureAwait(false)).Single();
            Assert.AreEqual(ProcessingGraphExecutionStatus.Running, running.Status);
            Assert.AreEqual(clock.GetUtcNow(), running.StartedUtc);
            Assert.AreEqual(clock.GetUtcNow().AddSeconds(10), running.DeadlineUtc);
            Assert.AreEqual(1, running.AttemptCount);

            await laneStore.ReleaseAsync(firstLease, CancellationToken.None).ConfigureAwait(false);
            var pendingRetry = await operations.ReadExecutionAsync(
                running.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(pendingRetry);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Pending, pendingRetry.Status);
            Assert.AreEqual(running.StartedUtc, pendingRetry.StartedUtc);
            Assert.AreEqual(running.DeadlineUtc, pendingRetry.DeadlineUtc);

            clock.Advance(TimeSpan.FromSeconds(1));
            var secondLease = await laneStore.ClaimAsync(
                standard, "deadline-test-2", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(secondLease);
            var retried = await operations.ReadExecutionAsync(
                running.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(retried);
            Assert.AreEqual(2, retried.AttemptCount);
            Assert.AreEqual(running.StartedUtc, retried.StartedUtc);
            Assert.AreEqual(running.DeadlineUtc, retried.DeadlineUtc);

            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await laneStore.RenewAsync(secondLease, CancellationToken.None).ConfigureAwait(false));
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.IsFalse(await laneStore.RenewAsync(secondLease, CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<CaptureLaneLeaseLostException>(async () =>
                await laneStore.CompleteAsync(secondLease, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            var expired = await operations.ReadExecutionAsync(
                running.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(expired);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Expired, expired.Status);
            Assert.AreEqual("processing.live-deadline", expired.FailureReason);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconciledRawEvidenceIsBoundToFrozenLiveExecutionBeforeClaim()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = CreateConfiguration();
            Guid captureId;
            using (var provider = CreateProvider(root))
            {
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                _ = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                    .EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var receipt = await ingress.AcceptAsync(
                    configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            }
            SqliteConnection.ClearAllPools();
            using (var connection = new SqliteConnection(
                       $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var removeExecution = connection.CreateCommand();
                removeExecution.CommandText = "DELETE FROM processing_executions WHERE execution_class = 'Live';";
                Assert.AreEqual(1, await removeExecution.ExecuteNonQueryAsync().ConfigureAwait(false));
            }

            using (var provider = CreateProvider(root))
            {
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                _ = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                await ingress.BindRecoveredLiveExecutionsAsync(configuration, CancellationToken.None).ConfigureAwait(false);

                var executions = await operations.ReadExecutionsAsync(
                    ProcessingGraphExecutionClass.Live, 10, CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(1, executions);
                Assert.AreEqual(captureId, executions[0].CaptureId);
                var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
                await laneStore.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
                var lease = await laneStore.ClaimAsync(
                    provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                        static definition => definition.Name == "standard"),
                    "recovery-test",
                    configuration,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                Assert.IsNotNull(lease.Context.Execution);
                Assert.AreEqual(executions[0].ExecutionId, lease.Context.Execution.ExecutionId);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplayWorkerExecutesNonEmptyGraphWithoutPublishingOrOverwritingLiveProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-replay-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CameraAgent:RawIngressRoot"] = root,
                    ["CameraAgent:RawIngressReserveBytes"] = "0",
                    ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
                }).Build());
            using var provider = services.BuildServiceProvider();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var baseConfiguration = CreateConfiguration();
            var configuration = baseConfiguration with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
                .ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);

            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            await laneStore.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            var liveLease = await laneStore.ClaimAsync(
                standard, "test-live", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(liveLease);
            var liveHandler = provider.GetServices<ICaptureLaneHandler>().Single(
                static handler => handler.Lane == "standard");
            var liveResult = await liveHandler.HandleAsync(liveLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, liveResult.Outcome);
            await laneStore.CompleteAsync(liveLease, CancellationToken.None).ConfigureAwait(false);

            var replayPipeline = new CapturePipelineConfig(
                [new CaptureProcessingStepConfig(
                    "Preview",
                    "preview",
                    Options: JsonSerializer.SerializeToElement(new { outputVariant = "replay-only" }),
                    DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent);
            var replayRevision = await operations.CreateRevisionAsync(
                "replay-preview", "v1", replayPipeline, "replay-preview-create-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            _ = await operations.ValidateRevisionAsync(
                replayRevision.RevisionId, "replay-preview-validate-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            var renamedPipeline = new CapturePipelineConfig(
                [new CaptureProcessingStepConfig("Preview", "renamed-preview", DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent);
            var renamedRevision = await operations.CreateRevisionAsync(
                "renamed-preview", "v1", renamedPipeline, "renamed-preview-create-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            _ = await operations.ValidateRevisionAsync(
                renamedRevision.RevisionId, "renamed-preview-validate-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);

            var renamedReplay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    renamedRevision.RevisionId,
                    receipt.Manifest.Descriptor.Artifact.ArtifactId,
                    TriggerReference: "renamed-node-collision"),
                "renamed-replay-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);
            var collisionReplay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId,
                    receipt.Manifest.Descriptor.Artifact.ArtifactId,
                    TriggerReference: "published-output-collision"),
                "collision-replay-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);
            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    replayRevision.RevisionId,
                    receipt.Manifest.Descriptor.Artifact.ArtifactId),
                "worker-replay-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);
            var worker = provider.GetRequiredService<ProcessingReplayWorker>();
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ProcessingGraphExecutionDetail? detail = null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!timeout.IsCancellationRequested)
                {
                    detail = await operations.ReadExecutionDetailAsync(
                        replay.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                    if (detail?.Execution.Status is ProcessingGraphExecutionStatus.Completed or ProcessingGraphExecutionStatus.Failed)
                        break;
                    await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                }
                Assert.IsNotNull(detail);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, detail.Execution.Status);
                Assert.HasCount(1, detail.Nodes);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed.ToString(), detail.Nodes[0].Status);
                Assert.IsTrue(detail.Nodes.SelectMany(static node => node.Inputs)
                    .Any(static input => input.Kind == ProcessingGraphExecutionInputKind.RawCapture));
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var deferredReplay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId,
                    receipt.Manifest.Descriptor.Artifact.ArtifactId,
                    TriggerReference: "deferred-replay"),
                "deferred-replay-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var deferredLease = await store.ClaimReplayAsync("deferred-worker", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(deferredLease);
            await store.CompleteReplayAsync(
                deferredLease, CaptureLaneHandlerResult.Wait("environment.association-pending"), CancellationToken.None)
                .ConfigureAwait(false);
            var deferredState = await operations.ReadExecutionAsync(
                deferredReplay.Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(deferredState);
            Assert.AreEqual(0, deferredState.AttemptCount);
            _ = await operations.CancelReplayAsync(
                deferredReplay.Execution.ExecutionId, "deferred-cancel-key", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            var cancelledDetail = await operations.ReadExecutionDetailAsync(
                deferredReplay.Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(cancelledDetail);
            Assert.AreEqual("TerminalFailure", cancelledDetail.Nodes.Single().Status);
            var futureReceipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(sequenceOffset: 1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(futureReceipt);

            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM processing_nodes WHERE capture_id = $capture),
                    (SELECT COUNT(*) FROM processing_outputs WHERE capture_id = $capture),
                    (SELECT association.published_flag
                     FROM processing_execution_outputs association
                     JOIN processing_executions execution ON execution.execution_id = association.execution_id
                     WHERE execution.execution_class = 'Live'),
                    (SELECT association.published_flag
                     FROM processing_execution_outputs association
                     JOIN processing_executions execution ON execution.execution_id = association.execution_id
                     WHERE execution.execution_id = $collision),
                    (SELECT association.published_flag
                     FROM processing_execution_outputs association
                     JOIN processing_executions execution ON execution.execution_id = association.execution_id
                     WHERE execution.execution_id = $renamed),
                    (SELECT association.published_flag
                     FROM processing_execution_outputs association
                     JOIN processing_executions execution ON execution.execution_id = association.execution_id
                     WHERE execution.execution_id = $replay);
                """;
            command.Parameters.AddWithValue("$capture", receipt.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
            command.Parameters.AddWithValue("$collision", collisionReplay.Execution.ExecutionId.ToString("N"));
            command.Parameters.AddWithValue("$renamed", renamedReplay.Execution.ExecutionId.ToString("N"));
            command.Parameters.AddWithValue("$replay", replay.Execution.ExecutionId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(1L, reader.GetInt64(0));
            Assert.AreEqual(2L, reader.GetInt64(1));
            Assert.AreEqual(1L, reader.GetInt64(2));
            Assert.AreEqual(1L, reader.GetInt64(3));
            Assert.AreEqual(1L, reader.GetInt64(4));
            Assert.AreEqual(0L, reader.GetInt64(5));
            await reader.DisposeAsync().ConfigureAwait(false);
            var published = await store.ReadCaptureProductsAsync(
                receipt.Manifest.Descriptor.Capture.CaptureId, null, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, published);
            Assert.AreEqual("default", published[0].Variant);
            var gallery = await store.ReadGalleryNodesAsync(
                [receipt.Manifest.Descriptor.Capture.CaptureId], 10, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, gallery.Nodes);
            Assert.HasCount(1, gallery.Nodes[0].Outputs);
            Assert.AreEqual("default", gallery.Nodes[0].Outputs[0].Artifact.Variant);

            var inputContracts = JsonSerializer.Serialize(
                new[]
                {
                    new ProcessingGraphInputContract(
                        [FrameArtifactRole.Preview], [], [], [], [])
                });
            var window = new ProcessingGraphWindowRequirement(
                ProcessingGraphWindowKind.Trailing, 1, 2, [], []);
            var replaySnapshot = await store.ReadRevisionAsync(
                replayRevision.RevisionId, CancellationToken.None).ConfigureAwait(false);
            var replayPlan = replaySnapshot.Nodes.Single().PlanSha256;
            var liveEligibleReplayOutputs = await ProcessingOutputWindowSelector.SelectAsync(
                connection, transaction: null, futureReceipt.Manifest.Descriptor, "preview",
                replayRevision.RevisionId, replayPlan, inputContracts, window, 2,
                includeUnpublishedRevisionOutputs: false, CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(liveEligibleReplayOutputs);
            var replayEligibleOutputs = await ProcessingOutputWindowSelector.SelectAsync(
                connection, transaction: null, futureReceipt.Manifest.Descriptor, "preview",
                replayRevision.RevisionId, replayPlan, inputContracts, window, 2,
                includeUnpublishedRevisionOutputs: true, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, replayEligibleOutputs);

            var renamedSnapshot = await store.ReadRevisionAsync(
                renamedRevision.RevisionId, CancellationToken.None).ConfigureAwait(false);
            var renamedEligibleOutputs = await ProcessingOutputWindowSelector.SelectAsync(
                connection, transaction: null, futureReceipt.Manifest.Descriptor, "renamed-preview",
                renamedRevision.RevisionId, renamedSnapshot.Nodes.Single().PlanSha256,
                inputContracts, window, 2, includeUnpublishedRevisionOutputs: false,
                CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, renamedEligibleOutputs);
            Assert.AreEqual(published[0].OutputIdentitySha256, renamedEligibleOutputs[0].OutputIdentitySha256);

            command.Parameters.Clear();
            command.CommandText = """
                DELETE FROM processing_outputs WHERE capture_id = $capture AND variant = 'replay-only';
                SELECT COUNT(*) FROM processing_execution_outputs WHERE execution_id = $replay;
                """;
            command.Parameters.AddWithValue("$capture", receipt.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
            command.Parameters.AddWithValue("$replay", replay.Execution.ExecutionId.ToString("N"));
            Assert.AreEqual(0L, Convert.ToInt64(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CameraModuleConfig CreateConfiguration()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("Test", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            CapturePipelineConfig.Empty,
            "agent-test");

    private static ServiceProvider CreateProvider(
        string root,
        IReadOnlyDictionary<string, string?>? overrides = null,
        TimeProvider? timeProvider = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0"
        };
        if (overrides is not null)
        {
            foreach (var pair in overrides) values[pair.Key] = pair.Value;
        }
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null) services.AddSingleton(timeProvider);
        services.AddCameraAgentInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    private static CaptureLoopSubmission CreateSubmission(
        int sequenceOffset = 0,
        CameraPixelFormat pixelFormat = CameraPixelFormat.Mono8,
        DateTimeOffset? capturedUtc = null)
    {
        var now = (capturedUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1)).AddSeconds(sequenceOffset * 2);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var frame = new CameraFrame(
            now,
            2,
            2,
            pixelFormat,
            pixelFormat == CameraPixelFormat.Mono16
                ? new byte[] { (byte)(sequenceOffset + 1), 0, 2, 0, 3, 0, 4, 0 }
                : new byte[] { (byte)(sequenceOffset + 1), 2, 3, 4 },
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 10, "test"));
        return new CaptureLoopSubmission(
            new CaptureRequest(now, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    now, now.AddSeconds(1), now.AddSeconds(1.1))
            },
            now,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
