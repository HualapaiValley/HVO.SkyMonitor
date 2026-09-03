using System.Diagnostics;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Replay;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ProcessingGraphOperationsTests
{
    private static readonly string[] PreemptionAttemptStatuses = ["Interrupted", "Completed"];

    [TestMethod]
    public void CoordinatorValidationHelpersRejectEveryUnsupportedShape()
    {
        var expectedFailures = new Exception[]
        {
            new ArgumentException(),
            new System.ComponentModel.DataAnnotations.ValidationException(),
            new InvalidDataException(),
            new ProcessingGraphStoreConflictException("conflict"),
            new KeyNotFoundException(),
            new JsonException(),
            new NotSupportedException(),
            new FormatException(),
            new OverflowException(),
            new OptionsValidationException("options", typeof(CameraAgentHostOptions), ["invalid"])
        };
        foreach (var exception in expectedFailures)
        {
            Assert.IsTrue(ProcessingGraphOperationsCoordinator.IsExpectedProposalValidationFailure(exception));
        }
        Assert.IsFalse(ProcessingGraphOperationsCoordinator.IsExpectedProposalValidationFailure(
            new InvalidOperationException()));

        var explicitPipeline = new CapturePipelineConfig(
            [],
            CapturePipelineSchemaVersions.ExplicitV2,
            CapturePipelineDependencyPolicy.RejectEnabledDependent);
        ProcessingGraphOperationsCoordinator.EnsureExplicitPipeline(explicitPipeline);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            ProcessingGraphOperationsCoordinator.EnsureExplicitPipeline(null!));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ProcessingGraphOperationsCoordinator.EnsureExplicitPipeline(
                explicitPipeline with { SchemaVersion = CapturePipelineSchemaVersions.LegacyV1 }));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ProcessingGraphOperationsCoordinator.EnsureExplicitPipeline(
                explicitPipeline with { DependencyPolicy = CapturePipelineDependencyPolicy.LegacyInference }));

        ProcessingGraphOperationsCoordinator.ValidateRevisionName("revision", "value");
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingGraphOperationsCoordinator.ValidateRevisionName(" ", "value"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingGraphOperationsCoordinator.ValidateRevisionName(new string('x', 129), "value"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ProcessingGraphOperationsCoordinator.ValidateRevisionName("bad\0name", "value"));
        Assert.IsTrue(ProcessingGraphOperationsCoordinator.IsSha256(new string('A', 64)));
        Assert.IsFalse(ProcessingGraphOperationsCoordinator.IsSha256("short"));
        Assert.IsFalse(ProcessingGraphOperationsCoordinator.IsSha256(new string('Z', 64)));

        ProcessingGraphRevisionSnapshot Snapshot(string? windowJson)
            => new(
                new ProcessingGraphRevisionState(
                    "revision", "name", "1", ProcessingGraphRevisionLifecycle.Validated,
                    new string('A', 64), new string('B', 64), new string('C', 64),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null),
                explicitPipeline,
                [],
                [],
                [],
                [new ProcessingExecutionNodeSeed(
                    "node", true, new string('D', 64), new string('E', 64), "[]", "[]", "[]", windowJson)]);

        ProcessingGraphOperationsCoordinator.EnsureLiveEligible(Snapshot(null));
        ProcessingGraphOperationsCoordinator.EnsureLiveEligible(Snapshot(JsonSerializer.Serialize(
            new ProcessingGraphWindowRequirement(
                ProcessingGraphWindowKind.Trailing, 3, 3, [-2, -1, 0], ["rig"]))));
        Assert.ThrowsExactly<ProcessingGraphStoreConflictException>(() =>
            ProcessingGraphOperationsCoordinator.EnsureLiveEligible(Snapshot(JsonSerializer.Serialize(
                new ProcessingGraphWindowRequirement(
                    ProcessingGraphWindowKind.Centered, 3, 3, [-1, 0, 1], ["rig"])))));
    }

    [TestMethod]
    public async Task SameFrozenReplayMatchesInProcessAndLocalRunnerIdentityAndProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-replay-equivalence-{Guid.NewGuid():N}");
        var socketPath = Path.Combine(root, "runner.sock");
        const string authorizationKey = "local-replay-equivalence-key-0001";
        Directory.CreateDirectory(root);
        try
        {
            Guid captureId;
            Guid artifactId;
            string revisionId;
            ProcessingGraphExecutionDetail inProcess;
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            using (var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            }))
            {
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
                    .ConfigureAwait(false);
                var receipt = await ingress.AcceptAsync(
                    configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
                artifactId = receipt.Manifest.Descriptor.Artifact.ArtifactId;
                revisionId = registry.ActiveRevisionId;
                inProcess = await ExecuteReplayAsync(
                    provider,
                    new ProcessingReplaySubmission(captureId, revisionId, artifactId),
                    "in-process-equivalence").ConfigureAwait(false);
            }

            var runnerOptions = new LocalReplayRunnerOptions
            {
                SocketPath = socketPath,
                PreSharedAuthKey = Encoding.UTF8.GetBytes(authorizationKey),
                HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                HeartbeatTimeout = TimeSpan.FromSeconds(2),
                ConnectTimeout = TimeSpan.FromSeconds(2)
            };
            using var runnerStopping = new CancellationTokenSource();
            var runner = new LocalReplayRunnerServer(runnerOptions);
            await using var runnerLifetime = runner.ConfigureAwait(false);
            var runnerTask = runner.RunAsync(runnerStopping.Token);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (!Path.Exists(socketPath))
                {
                    await Task.Delay(10, timeout.Token).ConfigureAwait(false);
                }
            }

            ProcessingGraphExecutionDetail external;
            using (var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:ReplayProfile"] = "LocalRunner",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1",
                ["CameraAgent:ProcessingGraphs:LocalRunner:SocketPath"] = socketPath,
                ["CameraAgent:ProcessingGraphs:LocalRunner:AuthorizationKey"] = authorizationKey,
                ["CameraAgent:ProcessingGraphs:LocalRunner:HeartbeatIntervalSeconds"] = "1",
                ["CameraAgent:ProcessingGraphs:LocalRunner:HeartbeatTimeoutSeconds"] = "2"
            }))
            {
                await provider.GetRequiredService<IRawCaptureIngress>()
                    .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                external = await ExecuteReplayAsync(
                    provider,
                    new ProcessingReplaySubmission(captureId, revisionId, artifactId),
                    "local-runner-equivalence").ConfigureAwait(false);
            }

            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, inProcess.Execution.Status);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, external.Execution.Status);
            Assert.AreEqual(inProcess.Execution.GraphRevisionId, external.Execution.GraphRevisionId);
            Assert.AreEqual(inProcess.Execution.LocalPlanIdentitySha256, external.Execution.LocalPlanIdentitySha256);
            Assert.HasCount(inProcess.Nodes.Count, external.Nodes);
            for (var index = 0; index < inProcess.Nodes.Count; index++)
            {
                var expected = inProcess.Nodes[index];
                var actual = external.Nodes[index];
                Assert.AreEqual(expected.NodeId, actual.NodeId);
                CollectionAssert.AreEqual(
                    expected.Inputs.Select(InputEvidence).ToArray(),
                    actual.Inputs.Select(InputEvidence).ToArray());
                CollectionAssert.AreEqual(
                    expected.Outputs.Select(OutputEvidence).ToArray(),
                    actual.Outputs.Select(OutputEvidence).ToArray());
            }

            await runnerStopping.CancelAsync().ConfigureAwait(false);
            await runnerTask.ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        static string InputEvidence(ProcessingGraphExecutionInputState input) => string.Join('|',
            input.Ordinal,
            input.WindowPosition,
            input.Kind,
            input.CaptureId,
            input.ArtifactId,
            input.DescriptorSha256,
            input.PayloadSha256,
            input.OutputIdentitySha256);

        static string OutputEvidence(ProcessingGraphExecutionOutputState output) => string.Join('|',
            output.Ordinal,
            output.OutputIdentitySha256,
            output.ArtifactId,
            output.Role,
            output.Variant,
            output.AvailabilityState,
            output.AvailabilityReason);
    }

    [TestMethod]
    public async Task MissingLocalRunnerDefersDurableReplayWithoutConsumingAttempt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-replay-runner-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            using var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:ReplayProfile"] = "LocalRunner",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1",
                ["CameraAgent:ProcessingGraphs:ReplayMaximumAttempts"] = "1",
                ["CameraAgent:ProcessingGraphs:LocalRunner:SocketPath"] = Path.Combine(root, "missing-runner.sock"),
                ["CameraAgent:ProcessingGraphs:LocalRunner:AuthorizationKey"] = "0123456789ABCDEF0123456789ABCDEF",
                ["CameraAgent:ProcessingGraphs:LocalRunner:ConnectTimeoutSeconds"] = "1"
            });
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
                .ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId,
                    receipt.Manifest.Descriptor.Artifact.ArtifactId),
                "missing-runner-replay-key",
                "owner-test",
                CancellationToken.None).ConfigureAwait(false);
            var worker = provider.GetRequiredService<ProcessingReplayWorker>();
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            ProcessingGraphExecutionDetail? detail = null;
            long claimCount = 0;
            try
            {
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(10))
                {
                    detail = await operations.ReadExecutionDetailAsync(
                        replay.Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
                    using var connection = new SqliteConnection(
                        $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                    await connection.OpenAsync().ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT claim_count FROM processing_replay_work WHERE execution_id = $execution_id;";
                    command.Parameters.AddWithValue("$execution_id", replay.Execution.ExecutionId.ToString("N"));
                    claimCount = Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
                    if (claimCount >= 3 && detail is { Nodes.Count: > 0 } &&
                        detail.Execution.Status == ProcessingGraphExecutionStatus.Pending &&
                        detail.Execution.FailureReason == "processing.replay-runner-unavailable")
                    {
                        break;
                    }
                    await Task.Delay(25).ConfigureAwait(false);
                }
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            Assert.IsNotNull(detail);
            Assert.IsGreaterThanOrEqualTo(3, claimCount);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Pending, detail.Execution.Status);
            Assert.AreEqual(0, detail.Execution.AttemptCount);
            Assert.AreEqual("processing.replay-runner-unavailable", detail.Execution.FailureReason);
            Assert.AreEqual("Pending", detail.Nodes.Single().Status);
            Assert.AreEqual(0, detail.Nodes.Single().AttemptCount);
            Assert.IsNull(detail.Nodes.Single().StartedUtc);
            Assert.IsEmpty(detail.Nodes.Single().Attempts);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunnerCrashAndHostRestartRecoverDurableFrozenReplayExactlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-replay-runner-recovery-{Guid.NewGuid():N}");
        var socketPath = Path.Combine(root, "runner.sock");
        const string authorizationKey = "local-replay-recovery-key-000001";
        Directory.CreateDirectory(root);
        try
        {
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var runnerOptions = new LocalReplayRunnerOptions
            {
                SocketPath = socketPath,
                PreSharedAuthKey = Encoding.UTF8.GetBytes(authorizationKey),
                HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                HeartbeatTimeout = TimeSpan.FromSeconds(2),
                ConnectTimeout = TimeSpan.FromSeconds(2)
            };
            var blocking = new CrashBlockingExecutor();
            Guid executionId;
            using (var runnerStopping = new CancellationTokenSource())
            {
                var runner = new LocalReplayRunnerServer(runnerOptions, blocking);
                await using var runnerLifetime = runner.ConfigureAwait(false);
                var runnerTask = runner.RunAsync(runnerStopping.Token);
                await WaitForPathAsync(socketPath).ConfigureAwait(false);
                using var provider = CreateProvider(root, LocalRunnerSettings());
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
                    .ConfigureAwait(false);
                var receipt = await ingress.AcceptAsync(
                    configuration,
                    CreateSubmission(),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                var replay = await operations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(
                        receipt.Manifest.Descriptor.Capture.CaptureId,
                        registry.ActiveRevisionId,
                        receipt.Manifest.Descriptor.Artifact.ArtifactId),
                    "runner-crash-recovery-key",
                    "owner-test",
                    CancellationToken.None).ConfigureAwait(false);
                executionId = replay.Execution.ExecutionId;
                var worker = provider.GetRequiredService<ProcessingReplayWorker>();
                await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await runnerStopping.CancelAsync().ConfigureAwait(false);
                    await runnerTask.ConfigureAwait(false);
                    var deferred = await WaitForExecutionAsync(
                        operations,
                        executionId,
                        static detail => detail.Execution.Status == ProcessingGraphExecutionStatus.Pending &&
                            detail.Execution.FailureReason == "processing.replay-runner-unavailable",
                        TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    Assert.AreEqual(0, deferred.Execution.AttemptCount);
                    Assert.IsEmpty(deferred.Nodes.Single().Outputs);
                    Assert.AreEqual("Interrupted", deferred.Nodes.Single().Attempts[^1].Status);
                }
                finally
                {
                    await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            using (var runnerStopping = new CancellationTokenSource())
            {
                var runner = new LocalReplayRunnerServer(runnerOptions);
                await using var runnerLifetime = runner.ConfigureAwait(false);
                var runnerTask = runner.RunAsync(runnerStopping.Token);
                await WaitForPathAsync(socketPath).ConfigureAwait(false);
                using var recoveredProvider = CreateProvider(root, LocalRunnerSettings());
                await recoveredProvider.GetRequiredService<IRawCaptureIngress>()
                    .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = recoveredProvider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var worker = recoveredProvider.GetRequiredService<ProcessingReplayWorker>();
                await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                ProcessingGraphExecutionDetail completed;
                try
                {
                    completed = await WaitForExecutionAsync(
                        operations,
                        executionId,
                        static detail => detail.Execution.Status == ProcessingGraphExecutionStatus.Completed,
                        TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
                finally
                {
                    await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    await runnerStopping.CancelAsync().ConfigureAwait(false);
                    await runnerTask.ConfigureAwait(false);
                }

                Assert.AreEqual(1, completed.Execution.AttemptCount);
                Assert.HasCount(1, completed.Nodes.Single().Outputs);
                Assert.AreEqual("Completed", completed.Nodes.Single().Attempts[^1].Status);
                using var connection = new SqliteConnection(
                    $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM processing_execution_outputs WHERE execution_id = $execution;";
                command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                Assert.AreEqual(1L, Convert.ToInt64(
                    await command.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            Dictionary<string, string?> LocalRunnerSettings() => new()
            {
                ["CameraAgent:ProcessingGraphs:ReplayProfile"] = "LocalRunner",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1",
                ["CameraAgent:ProcessingGraphs:ReplayLeaseSeconds"] = "3",
                ["CameraAgent:ProcessingGraphs:LocalRunner:SocketPath"] = socketPath,
                ["CameraAgent:ProcessingGraphs:LocalRunner:AuthorizationKey"] = authorizationKey,
                ["CameraAgent:ProcessingGraphs:LocalRunner:HeartbeatIntervalSeconds"] = "1",
                ["CameraAgent:ProcessingGraphs:LocalRunner:HeartbeatTimeoutSeconds"] = "2",
                ["CameraAgent:ProcessingGraphs:LocalRunner:ConnectTimeoutSeconds"] = "1"
            };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StaleReplayLeaseCannotPublishOrCompleteAfterHostReclaim()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-replay-stale-{Guid.NewGuid():N}");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:ReplayLeaseSeconds"] = "3",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            };
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            ProcessingReplayLease staleLease;
            Guid executionId;
            using (var originalHost = CreateProvider(root, settings, clock))
            {
                var ingress = originalHost.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = originalHost.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
                    .ConfigureAwait(false);
                var receipt = await ingress.AcceptAsync(
                    configuration,
                    CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(originalHost, configuration).ConfigureAwait(false);
                var replay = await operations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(
                        receipt.Manifest.Descriptor.Capture.CaptureId,
                        registry.ActiveRevisionId,
                        receipt.Manifest.Descriptor.Artifact.ArtifactId),
                    "stale-replay-key",
                    "owner-test",
                    CancellationToken.None).ConfigureAwait(false);
                executionId = replay.Execution.ExecutionId;
                staleLease = await originalHost.GetRequiredService<SqliteCaptureProcessingStore>()
                    .ClaimReplayAsync("crashed-host", CancellationToken.None).ConfigureAwait(false)
                    ?? throw new AssertFailedException("The original host did not claim replay work.");
            }

            clock.Advance(TimeSpan.FromSeconds(4));
            using var recoveredHost = CreateProvider(root, settings, clock);
            await recoveredHost.GetRequiredService<IRawCaptureIngress>()
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recoveredStore = recoveredHost.GetRequiredService<SqliteCaptureProcessingStore>();
            var recoveredLease = await recoveredStore.ClaimReplayAsync("recovered-host", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(recoveredLease);
            Assert.AreEqual(executionId, recoveredLease.Execution.ExecutionId);
            Assert.AreEqual(staleLease.WorkId, recoveredLease.WorkId);
            Assert.AreNotEqual(staleLease.LeaseToken, recoveredLease.LeaseToken);
            Assert.AreEqual(staleLease.ClaimCount + 1, recoveredLease.ClaimCount);

            var staleContext = new ProcessingExecutionContext(
                executionId,
                ProcessingGraphExecutionClass.Replay,
                staleLease.Execution.GraphRevisionId,
                staleLease.Execution.LocalPlanIdentitySha256,
                false,
                staleLease.WorkId,
                staleLease.LeaseToken,
                staleLease.LeaseOwner,
                staleLease.Execution.DeadlineUtc,
                staleLease.ClaimCount);
            await Assert.ThrowsExactlyAsync<CaptureLaneLeaseLostException>(async () =>
                await recoveredStore.EnsureExecutionLeaseAsync(staleContext, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CaptureLaneLeaseLostException>(async () =>
                await recoveredStore.CompleteReplayAsync(
                    staleLease, CaptureLaneHandlerResult.Success, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            using (var connection = new SqliteConnection(
                       $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM processing_execution_outputs WHERE execution_id = $execution;";
                command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                Assert.AreEqual(0L, Convert.ToInt64(
                    await command.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            _ = await recoveredStore.CompleteReplayAsync(
                recoveredLease, CaptureLaneHandlerResult.Wait("test-cleanup"), CancellationToken.None)
                .ConfigureAwait(false);
            var cancelled = await recoveredHost.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                .CancelReplayAsync(
                    executionId, "stale-replay-cleanup", "owner-test", null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Cancelled, cancelled.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
    public async Task CentralProposalLifecycleStagesActivatesRollsBackAndSettlesDurably()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-delivery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>()
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var baseConfiguration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var configured = await operations.EnsureConfiguredBasicAsync(
                baseConfiguration, CancellationToken.None).ConfigureAwait(false);
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var graph = factory.CreateGraph(baseConfiguration);
            ProcessingGraphDefinition definition;
            ProcessingGraphExecutionPlan plan;
            try
            {
                var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(
                    graph.SharedPlan!.CanonicalDefinition.GetRawText()));
                Assert.IsTrue(parsed.IsValid);
                definition = parsed.Definition!;
                plan = graph.SharedPlan;
                Assert.AreEqual(
                    "application/x-hvo-packed-image",
                    definition.Nodes.Single().Outputs.Single().MediaType);
            }
            finally
            {
                graph.DisposeSteps();
            }
            var now = DateTimeOffset.UtcNow;
            ProcessingGraphDeliveryProposalV1 CreateProposal(
                Guid proposalId,
                ProcessingGraphDefinition proposalDefinition,
                string definitionIdentity,
                string sharedPlanIdentity,
                string? expectedActiveRevisionId = null,
                DateTimeOffset? issuedAtUtc = null,
                DateTimeOffset? expiresAtUtc = null)
                => new(
                    ProcessingGraphDeliverySchemaVersions.Current,
                    proposalId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    expectedActiveRevisionId ?? configured.ActiveRevisionId,
                    operations.Capabilities.IdentitySha256,
                    definitionIdentity,
                    sharedPlanIdentity,
                    proposalDefinition,
                    issuedAtUtc ?? now.AddMinutes(-1),
                    expiresAtUtc ?? now.AddHours(1));

            var proposal = CreateProposal(
                Guid.NewGuid(), definition, plan.DefinitionIdentitySha256, plan.PlanIdentitySha256);
            await operations.StageAsync(proposal, CancellationToken.None).ConfigureAwait(false);
            await operations.StageAsync(proposal, CancellationToken.None).ConfigureAwait(false);
            var accepted = await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(accepted);
            Assert.AreEqual(ProcessingGraphDeliveryFactKind.Accepted, accepted.Kind);
            Assert.AreEqual(plan.DefinitionIdentitySha256, accepted.DefinitionIdentitySha256);
            Assert.AreEqual(plan.PlanIdentitySha256, accepted.SharedPlanIdentitySha256);
            await operations.RetryFactAsync(
                accepted.FactId, now.AddMinutes(-1), "retry-test", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(
                accepted.FactId,
                (await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false))!.FactId);
            await operations.AcknowledgeFactAsync(accepted.FactId, CancellationToken.None).ConfigureAwait(false);

            var active = await operations.ActivateRevisionAsync(
                accepted.LocalRevisionId!,
                configured.StateVersion,
                "central-activate",
                "central-delivery",
                "accepted",
                CancellationToken.None).ConfigureAwait(false);
            var activated = await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(activated);
            Assert.AreEqual(ProcessingGraphDeliveryFactKind.Activated, activated.Kind);
            await operations.AcknowledgeFactAsync(activated.FactId, CancellationToken.None).ConfigureAwait(false);

            var rolledBackRegistry = await operations.RollbackRevisionAsync(
                configured.ActiveRevisionId,
                active.StateVersion,
                "central-rollback",
                "operator",
                "rollback-test",
                CancellationToken.None).ConfigureAwait(false);
            await operations.ObserveActiveRevisionAsync(CancellationToken.None).ConfigureAwait(false);
            var rolledBack = await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(rolledBack);
            Assert.AreEqual(ProcessingGraphDeliveryFactKind.RolledBack, rolledBack.Kind);
            Assert.AreEqual(rolledBackRegistry.ActiveRevisionId, configured.ActiveRevisionId);
            await operations.AcknowledgeFactAsync(rolledBack.FactId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false));

            var expired = proposal with
            {
                ProposalId = Guid.NewGuid(),
                CatalogRevisionId = Guid.NewGuid(),
                AssignmentId = Guid.NewGuid(),
                IssuedAtUtc = now.AddHours(-2),
                ExpiresAtUtc = now.AddHours(-1)
            };
            await operations.StageAsync(expired, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(
                ProcessingGraphDeliveryFactKind.Expired,
                (await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false))!.Kind);
            await operations.SupersedeProposalAsync(expired.ProposalId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false));

            var activeChanged = proposal with
            {
                ProposalId = Guid.NewGuid(),
                CatalogRevisionId = Guid.NewGuid(),
                AssignmentId = Guid.NewGuid(),
                ExpectedActiveLocalRevisionId = new string('0', 64)
            };
            await operations.StageAsync(activeChanged, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(
                ProcessingGraphDeliveryFactKind.Rejected,
                (await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false))!.Kind);
            await operations.SupersedeProposalAsync(activeChanged.ProposalId, CancellationToken.None).ConfigureAwait(false);

            var originalNode = definition.Nodes[0];
            var unknownNode = new ProcessingGraphNodeDefinition(
                originalNode.Id,
                "UnknownStep",
                originalNode.StepVersion,
                originalNode.OperationKind,
                originalNode.Enabled,
                originalNode.FailurePolicy,
                originalNode.Order,
                originalNode.EffectiveOptions,
                originalNode.Dependencies,
                originalNode.Inputs,
                originalNode.Outputs,
                originalNode.Window,
                originalNode.CapabilityLabels,
                originalNode.HostApplicability);
            var unsupportedDefinition = definition with
            {
                Name = "unsupported",
                Nodes = [unknownNode]
            };
            var unsupportedPlan = ProcessingGraphCompiler.Compile(unsupportedDefinition).Plan!;
            var unsupported = CreateProposal(
                Guid.NewGuid(),
                unsupportedDefinition,
                unsupportedPlan.DefinitionIdentitySha256,
                unsupportedPlan.PlanIdentitySha256);
            await operations.StageAsync(unsupported, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(
                ProcessingGraphDeliveryFactKind.Rejected,
                (await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false))!.Kind);
            await operations.SupersedeProposalAsync(unsupported.ProposalId, CancellationToken.None).ConfigureAwait(false);

            var wrongCaseDefinition = definition with
            {
                Name = "wrong-case",
                Nodes = [new ProcessingGraphNodeDefinition(
                    originalNode.Id,
                    "preview",
                    originalNode.StepVersion,
                    originalNode.OperationKind,
                    originalNode.Enabled,
                    originalNode.FailurePolicy,
                    originalNode.Order,
                    originalNode.EffectiveOptions,
                    originalNode.Dependencies,
                    originalNode.Inputs,
                    originalNode.Outputs,
                    originalNode.Window,
                    originalNode.CapabilityLabels,
                    originalNode.HostApplicability)]
            };
            var wrongCasePlan = ProcessingGraphCompiler.Compile(wrongCaseDefinition).Plan!;
            var wrongCase = CreateProposal(
                Guid.NewGuid(), wrongCaseDefinition,
                wrongCasePlan.DefinitionIdentitySha256, wrongCasePlan.PlanIdentitySha256);
            await operations.StageAsync(wrongCase, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(
                ProcessingGraphDeliveryFactKind.Rejected,
                (await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false))!.Kind);
            await operations.SupersedeProposalAsync(wrongCase.ProposalId, CancellationToken.None).ConfigureAwait(false);

            var disabledNode = new ProcessingGraphNodeDefinition(
                "disabled-unknown",
                "UnknownStep",
                "cameraagent-v2-disabled",
                ProcessingOperationKind.Transform,
                false,
                ProcessingGraphNodeFailurePolicy.Required,
                100,
                JsonSerializer.SerializeToElement(new { }),
                [],
                [],
                [],
                null,
                [],
                [ProcessingGraphHosts.CameraAgent]);
            var disabledDefinition = definition with
            {
                Name = "disabled-unsupported",
                Nodes = [originalNode, disabledNode]
            };
            var disabledPlan = ProcessingGraphCompiler.Compile(disabledDefinition).Plan!;
            var disabledUnsupported = CreateProposal(
                Guid.NewGuid(), disabledDefinition,
                disabledPlan.DefinitionIdentitySha256, disabledPlan.PlanIdentitySha256);
            await operations.StageAsync(disabledUnsupported, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(
                ProcessingGraphDeliveryFactKind.Accepted,
                (await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false))!.Kind);
            await operations.SupersedeProposalAsync(
                disabledUnsupported.ProposalId, CancellationToken.None).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await operations.StageAsync(
                    proposal with
                    {
                        ProposalId = Guid.NewGuid(),
                        CapabilitySnapshotSha256 = new string('9', 64)
                    },
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var backlog = await operations.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, backlog.PendingFactCount);
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
    public async Task ExpiredLiveExecutionCannotPublishNodeOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-live-publication-deadline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
            using var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:LiveDeadlineSeconds"] = "10"
            }, clock, services => services.AddSingleton<ICaptureProcessingFaultInjector>(
                new AdvanceClockAtFaultPoint(
                    clock,
                    CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit,
                    TimeSpan.FromSeconds(11))));
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(receipt);

            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            var lease = await laneStore.ClaimAsync(
                standard, "deadline-publication-test", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var handler = provider.GetServices<ICaptureLaneHandler>().Single(static value => value.Lane == "standard");
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            StringAssert.Contains(exception.Message, "expired", StringComparison.Ordinal);
            await Assert.ThrowsExactlyAsync<CaptureLaneLeaseLostException>(async () =>
                await laneStore.FailAsync(
                    lease, CaptureLaneHandlerResult.Retry("handler-exception"), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            var execution = (await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                .ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 10, CancellationToken.None)
                .ConfigureAwait(false)).Single();
            Assert.AreEqual(ProcessingGraphExecutionStatus.Expired, execution.Status);
            Assert.AreEqual("processing.live-deadline", execution.FailureReason);
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM processing_outputs WHERE capture_id = $capture),
                    (SELECT COUNT(*) FROM processing_execution_outputs WHERE execution_id = $execution),
                    (SELECT COUNT(*) FROM processing_nodes WHERE capture_id = $capture);
                """;
            command.Parameters.AddWithValue("$capture", receipt.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
            command.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(0L, reader.GetInt64(0));
            Assert.AreEqual(0L, reader.GetInt64(1));
            Assert.AreEqual(0L, reader.GetInt64(2));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LiveCompletionCrossingDeadlineExpiresWithoutPublishing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-live-completion-deadline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
            using var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:LiveDeadlineSeconds"] = "10"
            }, clock, services => services.AddSingleton<ICaptureLaneFaultInjector>(
                new AdvanceClockAtLaneFaultPoint(
                    clock, CaptureLaneFaultPoint.BeforeCompletionCommit, TimeSpan.FromSeconds(11))));
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            var lease = await laneStore.ClaimAsync(
                standard, "completion-deadline-test", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var handler = provider.GetServices<ICaptureLaneHandler>().Single(static value => value.Lane == "standard");
            Assert.AreEqual(
                CaptureLaneHandlerOutcome.Completed,
                (await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false)).Outcome);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var staged = await operations.ReadExecutionDetailAsync(
                lease.Context.Execution!.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(staged);
            var artifactId = staged.Nodes.Single().Outputs.Single().ArtifactId;

            await Assert.ThrowsExactlyAsync<CaptureLaneLeaseLostException>(async () =>
                await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            var expired = await operations.ReadExecutionAsync(
                lease.Context.Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(expired);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Expired, expired.Status);
            Assert.AreEqual("processing.live-deadline", expired.FailureReason);
            Assert.AreEqual(
                CameraAgentArtifactReadStatus.NotFound,
                (await provider.GetRequiredService<ICameraAgentArtifactService>()
                    .OpenContentAsync(artifactId, CancellationToken.None).ConfigureAwait(false)).Status);
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT work.state, association.published_flag
                FROM capture_lane_work work
                JOIN raw_captures raw ON raw.raw_capture_row_id = work.raw_capture_row_id
                JOIN processing_executions execution ON execution.capture_id = raw.capture_id
                JOIN processing_execution_outputs association ON association.execution_id = execution.execution_id
                WHERE work.work_id = $work;
                """;
            command.Parameters.AddWithValue("$work", lease.WorkId);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual("quarantined", reader.GetString(0));
            Assert.AreEqual(0L, reader.GetInt64(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LivePriorityPreemptionDoesNotConsumeReplayAttempt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-replay-preemption-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var barrier = new ReplayBarrierObservation();
            using var provider = CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:ProcessingGraphs:ReplayMaximumAttempts"] = "1",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
            }, configureServices: services =>
            {
                services.AddSingleton(barrier);
                services.AddSingleton(new CaptureProcessingStepRegistration(
                    "ReplayPreemptionBarrier", typeof(ReplayPreemptionBarrierStep),
                    typeof(ReplayPreemptionBarrierOptions), AutoInclude: false));
            });
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var configuration = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig("ReplayPreemptionBarrier", "barrier", DependsOn: ["$raw"])],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
                .ConfigureAwait(false);
            var original = await ingress.AcceptAsync(
                configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(original);
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            await laneStore.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            var handler = provider.GetServices<ICaptureLaneHandler>().Single(static value => value.Lane == "standard");
            var originalLease = await laneStore.ClaimAsync(
                standard, "preemption-live-original", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(originalLease);
            Assert.AreEqual(
                CaptureLaneHandlerOutcome.Completed,
                (await handler.HandleAsync(originalLease.Context, CancellationToken.None).ConfigureAwait(false)).Outcome);
            await laneStore.CompleteAsync(originalLease, CancellationToken.None).ConfigureAwait(false);

            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    original.Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId,
                    original.Manifest.Descriptor.Artifact.ArtifactId),
                "preemption-replay-key", "owner-test", CancellationToken.None).ConfigureAwait(false);
            barrier.BlockNextReplay();
            var worker = provider.GetRequiredService<ProcessingReplayWorker>();
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await barrier.WaitUntilBlockedAsync(timeout.Token).ConfigureAwait(false);
                var liveReceipt = await ingress.AcceptAsync(
                    configuration, CreateSubmission(sequenceOffset: 1), timeout.Token).ConfigureAwait(false);
                Assert.IsNotNull(liveReceipt);

                ProcessingGraphExecutionState? preempted = null;
                while (!timeout.IsCancellationRequested)
                {
                    preempted = await operations.ReadExecutionAsync(
                        replay.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                    if (barrier.CancellationCount == 1 && preempted?.AttemptCount == 0) break;
                    await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                }
                Assert.IsNotNull(preempted);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Pending, preempted.Status);
                Assert.AreEqual(0, preempted.AttemptCount);
                var preemptedDetail = await operations.ReadExecutionDetailAsync(
                    replay.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                Assert.IsNotNull(preemptedDetail);
                Assert.AreEqual("Pending", preemptedDetail.Nodes.Single().Status);
                Assert.HasCount(1, preemptedDetail.Nodes.Single().Attempts);
                Assert.AreEqual("Interrupted", preemptedDetail.Nodes.Single().Attempts[0].Status);
                Assert.AreEqual(
                    "processing.replay-live-priority",
                    preemptedDetail.Nodes.Single().Attempts[0].Reason);

                var liveLease = await laneStore.ClaimAsync(
                    standard, "preemption-live-priority", configuration, timeout.Token).ConfigureAwait(false);
                Assert.IsNotNull(liveLease);
                Assert.AreEqual(
                    CaptureLaneHandlerOutcome.Completed,
                    (await handler.HandleAsync(liveLease.Context, timeout.Token).ConfigureAwait(false)).Outcome);
                await laneStore.CompleteAsync(liveLease, timeout.Token).ConfigureAwait(false);
                operations.NotifyLiveWorkChanged();

                ProcessingGraphExecutionState? completed = null;
                while (!timeout.IsCancellationRequested)
                {
                    completed = await operations.ReadExecutionAsync(
                        replay.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                    if (completed?.Status == ProcessingGraphExecutionStatus.Completed) break;
                    await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                }
                Assert.IsNotNull(completed);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Status);
                Assert.AreEqual(1, completed.AttemptCount);
                var completedDetail = await operations.ReadExecutionDetailAsync(
                    replay.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                Assert.IsNotNull(completedDetail);
                Assert.HasCount(2, completedDetail.Nodes.Single().Attempts);
                CollectionAssert.AreEqual(
                    PreemptionAttemptStatuses,
                    completedDetail.Nodes.Single().Attempts.Select(static attempt => attempt.Status).ToArray());
            }
            finally
            {
                barrier.Release();
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplayOldestAgeUsesImmutableAcceptanceTime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-replay-oldest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
            using var provider = CreateProvider(root, timeProvider: clock);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var configuration = CreateConfiguration();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
                .ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(
                configuration, CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static definition => definition.Name == "standard");
            var liveLease = await laneStore.ClaimAsync(
                standard, "oldest-live", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(liveLease);
            await laneStore.CompleteAsync(liveLease, CancellationToken.None).ConfigureAwait(false);
            var replay = await operations.SubmitReplayAsync(
                new ProcessingReplaySubmission(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    registry.ActiveRevisionId,
                    receipt.Manifest.Descriptor.Artifact.ArtifactId),
                "oldest-replay-key", "owner-test", CancellationToken.None).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();

            clock.Advance(TimeSpan.FromMinutes(5));
            var lease = await store.ClaimReplayAsync("oldest-worker", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            Assert.AreEqual(
                replay.Execution.AcceptedUtc,
                (await store.ReadOperationalStateAsync(CancellationToken.None).ConfigureAwait(false)).OldestReplayPendingUtc);
            await store.CompleteReplayAsync(
                lease, CaptureLaneHandlerResult.Wait("environment.association-pending"), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(
                replay.Execution.AcceptedUtc,
                (await store.ReadOperationalStateAsync(CancellationToken.None).ConfigureAwait(false)).OldestReplayPendingUtc);
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
            var artifactService = provider.GetRequiredService<ICameraAgentArtifactService>();
            var stagedLive = await operations.ReadExecutionDetailAsync(
                liveLease.Context.Execution!.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(stagedLive);
            var liveOutputArtifactId = stagedLive.Nodes.Single().Outputs.Single().ArtifactId;
            Assert.AreEqual(
                CameraAgentArtifactReadStatus.NotFound,
                (await artifactService.OpenContentAsync(liveOutputArtifactId, CancellationToken.None)
                    .ConfigureAwait(false)).Status);
            await laneStore.CompleteAsync(liveLease, CancellationToken.None).ConfigureAwait(false);
            var publishedLive = await artifactService.OpenContentAsync(
                liveOutputArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, publishedLive.Status);
            await publishedLive.Content!.DisposeAsync().ConfigureAwait(false);

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
            ProcessingGraphExecutionDetail? detail = null;
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
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

            var replayOutput = detail!.Nodes.Single().Outputs.Single();
            Assert.AreEqual(
                CameraAgentArtifactReadStatus.NotFound,
                (await artifactService.OpenContentAsync(replayOutput.ArtifactId, CancellationToken.None)
                    .ConfigureAwait(false)).Status);
            var openedReplay = await artifactService.OpenReplayOutputContentAsync(
                replay.Execution.ExecutionId, replayOutput.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, openedReplay.Status);
            var replayContent = openedReplay.Content!;
            await using (replayContent.ConfigureAwait(false))
            {
                using var contentBytes = new MemoryStream();
                await replayContent.CopyToAsync(contentBytes).ConfigureAwait(false);
                Assert.IsNotEmpty(contentBytes.ToArray());
                Assert.AreEqual(
                    replayContent.ChecksumSha256,
                    PayloadChecksum.ComputeSha256(contentBytes.ToArray()),
                    ignoreCase: true);
            }
            Assert.AreEqual(
                CameraAgentArtifactReadStatus.NotFound,
                (await artifactService.OpenReplayOutputContentAsync(
                    liveLease.Context.Execution!.ExecutionId, replayOutput.ArtifactId, CancellationToken.None)
                    .ConfigureAwait(false)).Status);
            Assert.AreEqual(
                CameraAgentArtifactReadStatus.NotFound,
                (await artifactService.OpenReplayOutputContentAsync(
                    Guid.NewGuid(), replayOutput.ArtifactId, CancellationToken.None).ConfigureAwait(false)).Status);
            Assert.AreEqual(
                CameraAgentArtifactReadStatus.NotFound,
                (await artifactService.OpenReplayOutputContentAsync(
                    renamedReplay.Execution.ExecutionId, replayOutput.ArtifactId, CancellationToken.None)
                    .ConfigureAwait(false)).Status);

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

    private static async Task CompleteLiveAsync(ServiceProvider provider, CameraModuleConfig configuration)
    {
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        await laneStore.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static definition => definition.Name == "standard");
        var lease = await laneStore.ClaimAsync(
            standard, "equivalence-live", configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(static value => value.Lane == "standard");
        var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<ProcessingGraphExecutionDetail> ExecuteReplayAsync(
        ServiceProvider provider,
        ProcessingReplaySubmission submission,
        string idempotencyKey)
    {
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var submitted = await operations.SubmitReplayAsync(
            submission, idempotencyKey, "equivalence-test", CancellationToken.None).ConfigureAwait(false);
        var worker = provider.GetRequiredService<ProcessingReplayWorker>();
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var detail = await operations.ReadExecutionDetailAsync(
                    submitted.Execution.ExecutionId, timeout.Token).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                if (detail.Execution.Status == ProcessingGraphExecutionStatus.Completed)
                {
                    return detail;
                }
                if (detail.Execution.Status is ProcessingGraphExecutionStatus.Failed or
                    ProcessingGraphExecutionStatus.Cancelled or ProcessingGraphExecutionStatus.Expired)
                {
                    Assert.Fail($"Replay became terminal as {detail.Execution.Status}: {detail.Execution.FailureReason}");
                }
                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task WaitForPathAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!Path.Exists(path))
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task<ProcessingGraphExecutionDetail> WaitForExecutionAsync(
        ProcessingGraphOperationsCoordinator operations,
        Guid executionId,
        Func<ProcessingGraphExecutionDetail, bool> predicate,
        TimeSpan timeoutValue)
    {
        using var timeout = new CancellationTokenSource(timeoutValue);
        while (true)
        {
            var detail = await operations.ReadExecutionDetailAsync(executionId, timeout.Token).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            if (predicate(detail))
            {
                return detail;
            }
            await Task.Delay(25, timeout.Token).ConfigureAwait(false);
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
        TimeProvider? timeProvider = null,
        Action<IServiceCollection>? configureServices = null)
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
        configureServices?.Invoke(services);
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

    private sealed class CrashBlockingExecutor : IProcessingRecipeExecutor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The crash-recovery executor completed without cancellation.");
        }
    }

    private sealed class AdvanceClockAtFaultPoint(
        MutableTimeProvider clock,
        CaptureProcessingFaultPoint target,
        TimeSpan advance)
        : ICaptureProcessingFaultInjector
    {
        private int _advanced;

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == target &&
                Interlocked.Exchange(ref _advanced, 1) == 0)
            {
                clock.Advance(advance);
            }
        }
    }

    private sealed class AdvanceClockAtLaneFaultPoint(
        MutableTimeProvider clock,
        CaptureLaneFaultPoint target,
        TimeSpan advance)
        : ICaptureLaneFaultInjector
    {
        private int _advanced;

        public void Inject(CaptureLaneFaultPoint point)
        {
            if (point == target && Interlocked.Exchange(ref _advanced, 1) == 0)
            {
                clock.Advance(advance);
            }
        }
    }

    private sealed class ReplayBarrierObservation
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNextReplay;
        private int _cancellationCount;

        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public void BlockNextReplay() => Interlocked.Exchange(ref _blockNextReplay, 1);

        public Task<bool> WaitUntilBlockedAsync(CancellationToken cancellationToken) =>
            _entered.Task.WaitAsync(cancellationToken);

        public void Release() => _release.TrySetResult(true);

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _blockNextReplay, 0) != 1) return;
            _entered.TrySetResult(true);
            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "The processing pipeline factory deserializes this test options type.")]
    private sealed class ReplayPreemptionBarrierOptions;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "The processing pipeline factory creates this test step through ActivatorUtilities.")]
    private sealed class ReplayPreemptionBarrierStep(
        CaptureProcessingStepMetadata metadata,
        ReplayPreemptionBarrierOptions options,
        ReplayBarrierObservation observation)
        : ConfigurableCaptureProcessingStep<ReplayPreemptionBarrierOptions>(metadata, options),
          IDescriptorOnlyCaptureProcessingStep,
          ICaptureProcessingGraphStep
    {
        public bool Enabled => true;

        public string RecipeName => "replay-preemption-barrier";

        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;

        public string OutputVariant => Metadata.Id;

        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public override ValueTask ProcessAsync(
            CaptureProcessingContext context,
            CancellationToken cancellationToken) =>
            ((IDescriptorOnlyCaptureProcessingStep)this).ProcessAsync(
                new CaptureDescriptorProcessingContext(context), cancellationToken);

        public async ValueTask ProcessAsync(
            CaptureDescriptorProcessingContext context,
            CancellationToken cancellationToken)
        {
            await observation.WaitAsync(cancellationToken).ConfigureAwait(false);
            context.AddProcessingOutcome(ProcessingOutcome.Produced());
        }
    }
}
