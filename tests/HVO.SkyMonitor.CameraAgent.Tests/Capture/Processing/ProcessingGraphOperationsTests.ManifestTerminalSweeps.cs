using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("replay-cancelled")]
    [DataRow("replay-cancelled-interrupted")]
    [DataRow("replay-cancelled-leased")]
    [DataRow("replay-cancelled-leased-interrupted")]
    [DataRow("replay-failed")]
    [DataRow("replay-failed-interrupted")]
    [DataRow("replay-failed-recovery-interrupted")]
    [DataRow("replay-expired")]
    [DataRow("replay-expired-interrupted")]
    [DataRow("replay-expired-pending")]
    [DataRow("replay-expired-sweep-interrupted")]
    [DataRow("replay-expired-lease-interrupted")]
    [DataRow("live-failed")]
    [DataRow("live-failed-interrupted")]
    [DataRow("live-expired")]
    [DataRow("live-expired-interrupted")]
    public async Task TerminalSweepsKeepProvablyUncommittedNodesDownloadableAcrossRestart(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-sweep");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var settings = new Dictionary<string, string?>
        {
            ["CameraAgent:ProcessingGraphs:LiveDeadlineSeconds"] = "10",
            ["CameraAgent:ProcessingGraphs:LiveMaximumQueueAgeSeconds"] = "10",
            ["CameraAgent:ProcessingGraphs:ReplayDeadlineSeconds"] = "10",
            ["CameraAgent:ProcessingGraphs:ReplayMaximumQueueAgeSeconds"] = "10"
        };
        if (path == "replay-failed-recovery-interrupted")
        {
            settings["CameraAgent:ProcessingGraphs:ReplayDeadlineSeconds"] = "300";
            settings["CameraAgent:ProcessingGraphs:ReplayLeaseSeconds"] = "3";
            settings["CameraAgent:ProcessingGraphs:ReplayMaximumAttempts"] = "1";
        }
        Guid id;
        string manifest;
        var interrupted = path.EndsWith("-interrupted", StringComparison.Ordinal);
        var expected = path.Contains("cancelled", StringComparison.Ordinal) ? ProcessingGraphExecutionStatus.Cancelled
            : path.Contains("expired", StringComparison.Ordinal) ? ProcessingGraphExecutionStatus.Expired : ProcessingGraphExecutionStatus.Failed;
        try
        {
            using (var provider = CreateProvider(root, settings, clock))
            {
                var configuration = CreateConfiguration() with
                {
                    Pipeline = new([new("Preview", "preview", DependsOn: ["$raw"])],
                        CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
                };
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var receipt = (await ingress.AcceptAsync(configuration,
                    CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)), CancellationToken.None).ConfigureAwait(false))!;
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(configuration);
                try
                {
                    if (path.StartsWith("replay", StringComparison.Ordinal))
                    {
                        await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                        id = (await operations.SubmitReplayAsync(new(receipt.Manifest.Descriptor.Capture.CaptureId,
                            registry.ActiveRevisionId, receipt.Manifest.Descriptor.Artifact.ArtifactId), "sweep", "test", CancellationToken.None).ConfigureAwait(false)).Execution.ExecutionId;
                        await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                        if (path == "replay-cancelled")
                            await operations.CancelReplayAsync(id, "sweep-cancel", "test", null, CancellationToken.None).ConfigureAwait(false);
                        else if (path == "replay-expired-pending")
                        {
                            clock.Advance(TimeSpan.FromSeconds(11));
                            Assert.IsNull(await store.ClaimReplayAsync("sweep", CancellationToken.None).ConfigureAwait(false));
                        }
                        else
                        {
                            var lease = (await store.ClaimReplayAsync("sweep", CancellationToken.None).ConfigureAwait(false))!;
                            if (interrupted)
                                await store.BeginNodeAttemptAsync(new(id, ProcessingGraphExecutionClass.Replay,
                                    lease.Execution.GraphRevisionId, lease.Execution.LocalPlanIdentitySha256, false,
                                    lease.WorkId, lease.LeaseToken, lease.LeaseOwner, lease.Execution.DeadlineUtc, lease.ClaimCount),
                                    graph.Nodes.Single(), lease.ClaimCount, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                            if (path.Contains("-leased", StringComparison.Ordinal))
                            {
                                await operations.CancelReplayAsync(id, "sweep-cancel", "test", null, CancellationToken.None).ConfigureAwait(false);
                                await store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Terminal("sweep-terminal"), CancellationToken.None).ConfigureAwait(false);
                            }
                            else if (path == "replay-expired-sweep-interrupted")
                            {
                                await store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Wait("sweep-wait"), CancellationToken.None).ConfigureAwait(false);
                                clock.Advance(TimeSpan.FromSeconds(11));
                                Assert.IsNull(await store.ClaimReplayAsync("sweep", CancellationToken.None).ConfigureAwait(false));
                            }
                            else if (path is "replay-expired-lease-interrupted" or "replay-failed-recovery-interrupted")
                            {
                                clock.Advance(TimeSpan.FromSeconds(path == "replay-expired-lease-interrupted" ? 31 : 4));
                                Assert.IsNull(await store.ClaimReplayAsync("sweep", CancellationToken.None).ConfigureAwait(false));
                            }
                            else if (expected == ProcessingGraphExecutionStatus.Cancelled)
                            {
                                await store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Wait("sweep-wait"), CancellationToken.None).ConfigureAwait(false);
                                await operations.CancelReplayAsync(id, "sweep-cancel", "test", null, CancellationToken.None).ConfigureAwait(false);
                            }
                            else
                            {
                                if (expected == ProcessingGraphExecutionStatus.Expired) clock.Advance(TimeSpan.FromSeconds(11));
                                await store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Terminal("sweep-terminal"), CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        var lanes = provider.GetRequiredService<ICaptureLaneStore>();
                        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard");
                        if (path == "live-expired")
                        {
                            clock.Advance(TimeSpan.FromSeconds(11));
                            Assert.IsNull(await lanes.ClaimAsync(standard, "sweep", configuration, CancellationToken.None).ConfigureAwait(false));
                        }
                        else
                        {
                            var lease = (await lanes.ClaimAsync(standard, "sweep", configuration, CancellationToken.None).ConfigureAwait(false))!;
                            if (interrupted)
                                await store.BeginNodeAttemptAsync(lease.Context.Execution!, graph.Nodes.Single(), lease.Attempt,
                                    clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                            if (expected == ProcessingGraphExecutionStatus.Expired)
                            {
                                clock.Advance(TimeSpan.FromSeconds(11));
                                Assert.IsFalse(await lanes.RenewAsync(lease, CancellationToken.None).ConfigureAwait(false));
                                // Renewal refuses the expired lease; claim runs the actual expiry sweep.
                                Assert.IsNull(await lanes.ClaimAsync(standard, "sweep-expire", configuration, CancellationToken.None).ConfigureAwait(false));
                            }
                            else await lanes.FailAsync(lease, CaptureLaneHandlerResult.Terminal("sweep-terminal"), CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                finally { graph.DisposeSteps(); }
                var detail = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                Assert.AreEqual(expected, detail.Execution.Status);
                Assert.IsEmpty(detail.Nodes.Single().Outputs);
                Assert.HasCount(interrupted ? 1 : 0, detail.Nodes.Single().Attempts);
                Assert.IsTrue(detail.Nodes.Single().Attempts.All(static attempt => attempt.Status == "Interrupted" && attempt.Outcome is null));
                manifest = await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root, settings, clock);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            Assert.AreEqual(expected, (await recovered.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!.Execution.Status);
            Assert.AreEqual(manifest, await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RequiredTerminalGateKeepsItsNeverStartedDownstreamManifestAcrossRestart()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-gate");
        Guid id;
        string manifest;
        try
        {
            using (var provider = CreateProvider(root, configureServices: services => services.AddSingleton(
                new CaptureProcessingStepRegistration("TerminalGate", typeof(ManifestTerminalGate), typeof(ManifestTerminalGateOptions), AutoInclude: false))))
            {
                var configuration = CreateConfiguration() with
                {
                    Pipeline = new([new("TerminalGate", "gate", DependsOn: ["$raw"]), new("Preview", "preview", DependsOn: ["gate"])],
                        CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
                };
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var receipt = (await ingress.AcceptAsync(configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false))!;
                var lanes = provider.GetRequiredService<ICaptureLaneStore>();
                var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard");
                var lease = (await lanes.ClaimAsync(standard, "gate", configuration, CancellationToken.None).ConfigureAwait(false))!;
                var result = await provider.GetServices<ICaptureLaneHandler>().Single(static handler => handler.Lane == "standard")
                    .HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Outcome);
                await lanes.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false);
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                var detail = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                Assert.AreEqual(ProcessingGraphExecutionStatus.Failed, detail.Execution.Status);
                Assert.AreEqual("TerminalFailure", detail.Nodes.Single(static node => node.NodeId == "gate").Status);
                Assert.IsEmpty(detail.Nodes.Single(static node => node.NodeId == "preview").Attempts);
                manifest = await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(manifest, await RunManifestTestAssertions.ResponseAsync(restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>(), id).ConfigureAwait(false));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Constructed by the registered test step factory.")]
    private sealed class ManifestTerminalGateOptions;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Constructed by the registered test step factory.")]
    private sealed class ManifestTerminalGate(CaptureProcessingStepMetadata metadata, ManifestTerminalGateOptions options)
        : ConfigurableCaptureProcessingStep<ManifestTerminalGateOptions>(metadata, options), ICaptureProcessingGraphStep
    {
        public bool Enabled => true;
        public string RecipeName => "terminal-manifest-gate";
        public FrameArtifactRole OutputRole => FrameArtifactRole.Calibrated;
        public string OutputVariant => "default";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
        public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure("manifest.test-required-gate"));
            return ValueTask.CompletedTask;
        }
    }
}
