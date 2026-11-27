using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Silo;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using static HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution.DurableCaptureDistributionTests;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
[DoNotParallelize]
public sealed partial class SiloRawEvidenceRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SiloEvidenceRecoverySettlesLaneFailuresBeforeOrAfterPublication(bool publishBeforeFailure)
    {
        using var fixture = CreateFixture(new()
        {
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true, Required = true }]
        });
        var token = CancellationToken.None;
        var (settings, store) = await CreateRecoverySiloAsync().ConfigureAwait(false);
        var faults = new SiloTestFaults();
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, faults, decorateReadStore: null,
            store, new SiloTestPayloads());
        var artifacts = new SiloArtifactStorage(storage, fixture.Time);
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(), fixture.Policy,
            laneState: fixture.LaneState, silo: storage, siloSettings: settings, siloArtifacts: artifacts);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        var capture = await ingress.AcceptAsync(fixture.Configuration, fixture.CreateSubmission(0), token).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        var descriptor = capture.Manifest.Descriptor;
        faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        var groupId = descriptor.Capture.CaptureId.ToString("N");
        Assert.AreEqual(SiloStagedState.Recorded, (await storage.State.ReadGroupAsync(groupId, "raw-1", token).ConfigureAwait(false))?.State);

        var standard = await ingress.ClaimAsync(fixture.Policy.Definitions.Single(lane => lane.Name == "standard"),
            "test", fixture.Configuration, token).ConfigureAwait(false);
        var secondaryDefinition = fixture.Policy.Definitions.Single(lane => lane.Name == "secondary");
        var secondary = await ingress.ClaimAsync(secondaryDefinition, "test", fixture.Configuration, token).ConfigureAwait(false);
        Assert.IsNotNull(standard);
        Assert.IsNotNull(secondary);

        File.Delete(capture.StoredFrame.AbsolutePath);
        File.Delete(Path.ChangeExtension(capture.StoredFrame.AbsolutePath, ".json"));
        using var handler = new StandardCaptureLaneHandler(new EmptyPipelineFactory(),
            NullLogger<StandardCaptureLaneHandler>.Instance, ingress);
        var standardFailure = await handler.HandleAsync(standard.Context, token).ConfigureAwait(false);
        var secondaryFailure = await new SecondaryEvidenceReader(artifacts).HandleAsync(secondary.Context, token).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerResult.Terminal("evidence-unavailable"), standardFailure);
        Assert.AreEqual(CaptureLaneHandlerResult.Terminal("evidence-invalid"), secondaryFailure);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        if (publishBeforeFailure)
        {
            await PublishAsync().ConfigureAwait(false);
            Assert.AreEqual(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_work WHERE state = 'leased';")
                .ConfigureAwait(false));
            Assert.AreEqual(0L, await CountRestorationsAsync().ConfigureAwait(false));
        }

        Assert.AreEqual(publishBeforeFailure ? CaptureLaneHandlerOutcome.Abandoned : CaptureLaneHandlerOutcome.TerminalFailure,
            await ingress.FailAsync(standard, standardFailure, token).ConfigureAwait(false));
        Assert.AreEqual(publishBeforeFailure ? CaptureLaneHandlerOutcome.RetryableFailure : CaptureLaneHandlerOutcome.TerminalFailure,
            await ingress.FailAsync(secondary, secondaryFailure, token).ConfigureAwait(false));
        if (!publishBeforeFailure)
        {
            Assert.AreEqual(2L, fixture.LaneState.Snapshot.QuarantineCount);
            // Exercise the durable lane gate directly. Raw health can independently refuse admission while files are missing.
            var laneStore = new SqliteCaptureLaneStore(fixture.Root, 5, fixture.Options.Value.CaptureDistribution,
                fixture.Policy, fixture.Time, new NullCaptureLaneFaultInjector());
            await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(() => laneStore.EnsureCanAcceptAsync(4, token).AsTask())
                .ConfigureAwait(false);
            Assert.AreEqual(2L, await ScalarLongAsync(connection,
                "SELECT COUNT(*) FROM capture_lane_definitions WHERE enabled = 1 AND required = 1 AND pressure_state = 2;")
                .ConfigureAwait(false));
            await PublishAsync().ConfigureAwait(false);
        }

        AssertRecoveredLaneHealth(fixture.LaneState);
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT MAX(pressure_state) FROM capture_lane_definitions;")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state = 'abandoned'
                AND failure_reason = 'raw-evidence-revalidated' AND completed_unix_ms IS NOT NULL AND lease_token IS NULL;
            """).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'pending'
                AND failure_reason IS NULL AND attempt_count = 0 AND completed_unix_ms IS NULL AND lease_token IS NULL;
            """).ConfigureAwait(false));
        Assert.AreEqual(publishBeforeFailure ? 2L : 1L, await CountRestorationsAsync().ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));

        var retry = await ingress.ClaimAsync(secondaryDefinition, "retry", fixture.Configuration, token).ConfigureAwait(false);
        Assert.IsNotNull(retry);
        Assert.IsTrue(retry.SiloEvidenceVisibleAtClaim);
        Assert.AreEqual(1, retry.Attempt);
        Assert.AreEqual(CaptureLaneHandlerResult.Success,
            await new SecondaryEvidenceReader(artifacts).HandleAsync(retry.Context, token).ConfigureAwait(false));
        if (publishBeforeFailure)
        {
            // Even with a pre-visibility sample, a second failure cannot reuse the lane's restoration.
            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure,
                await ingress.FailAsync(retry with { SiloEvidenceVisibleAtClaim = false }, secondaryFailure, token)
                    .ConfigureAwait(false));
            Assert.AreEqual(2L, await CountRestorationsAsync().ConfigureAwait(false));
        }

        Task<long> CountRestorationsAsync() => ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM raw_capture_stage_events
            WHERE stage_key GLOB 'raw-evidence-silo-restored.*' AND state = 'evidence-restored' AND source = 'silo-publication';
            """);

        async Task PublishAsync()
        {
            Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
            Assert.AreEqual(SiloStagedState.Visible, (await storage.State.ReadGroupAsync(groupId, "raw-1", token).ConfigureAwait(false))?.State);
            Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM silo_raw_admissions;").ConfigureAwait(false));
        }
    }

    [TestMethod]
    [DataRow("standard", "evidence-unavailable")]
    [DataRow("secondary", "evidence-invalid")]
    public async Task PublishedSiloEvidenceFailureQuarantinesRequiredLane(string name, string reason)
    {
        using var fixture = CreateFixture(new()
        {
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true, Required = true }]
        });
        var token = CancellationToken.None;
        var (settings, store) = await CreateRecoverySiloAsync().ConfigureAwait(false);
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, new SiloTestFaults(),
            decorateReadStore: null, store, new SiloTestPayloads());
        var artifacts = new SiloArtifactStorage(storage, fixture.Time);
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(), fixture.Policy,
            laneState: fixture.LaneState, silo: storage, siloSettings: settings, siloArtifacts: artifacts);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        var capture = await ingress.AcceptAsync(fixture.Configuration, fixture.CreateSubmission(0), token).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.IsFalse(File.Exists(capture.StoredFrame.AbsolutePath));

        var lane = fixture.Policy.Definitions.Single(lane => lane.Name == name);
        var lease = await ingress.ClaimAsync(lane, "test", fixture.Configuration, token).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        Assert.IsTrue(lease.SiloEvidenceVisibleAtClaim);
        var result = name == "standard" ? CaptureLaneHandlerResult.Terminal(reason) : CaptureLaneHandlerResult.Retry(reason);
        Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, await ingress.FailAsync(lease, result, token).ConfigureAwait(false));
        Assert.IsNull(await ingress.ClaimAsync(lane, "retry", fixture.Configuration, token).ConfigureAwait(false));

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        using var disposition = connection.CreateCommand();
        disposition.CommandText = "SELECT state, failure_reason, attempt_count FROM capture_lane_work WHERE lane_name = $lane;";
        disposition.Parameters.AddWithValue("$lane", name);
        using (var reader = await disposition.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
            Assert.AreEqual(("quarantined", reason, 1L), (reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        Assert.AreEqual(0L, await ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM raw_capture_stage_events WHERE stage_key GLOB 'raw-evidence-silo-restored.*';
            """).ConfigureAwait(false));
        Assert.AreEqual(CaptureLaneAvailability.Unhealthy, fixture.LaneState.Snapshot.Availability);
        Assert.AreEqual("required-quarantine", fixture.LaneState.Snapshot.Reason);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SiloAdmissionSettlementClearsRetentionAndPressureWithRequiredTransientLane(bool replayReclaimIntent)
    {
        using var fixture = CreateFixture(new(), transient: RequiredTransient());
        var token = CancellationToken.None;
        var (settings, store) = await CreateRecoverySiloAsync().ConfigureAwait(false);
        var faults = new SiloTestFaults();
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, faults,
            decorateReadStore: null, store, new SiloTestPayloads());
        var artifacts = new SiloArtifactStorage(storage, fixture.Time);
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        var reclaim = new InterruptReclaim();
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        await QuarantineAndPublishAsync().ConfigureAwait(false);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        if (replayReclaimIntent)
        {
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM silo_raw_admissions;").ConfigureAwait(false));
            using var restarted = CreateIngress();
            await restarted.InitializeAsync(token).ConfigureAwait(false);
        }

        AssertRecoveredLaneHealth(fixture.LaneState);
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT MAX(pressure_state) FROM capture_lane_definitions;")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM silo_raw_admissions;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state = 'abandoned';
            """).ConfigureAwait(false));

        RawCaptureIngress CreateIngress() => new(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, reclaim, fixture.Policy,
            laneState: fixture.LaneState, silo: storage, siloSettings: settings, siloArtifacts: artifacts);

        async Task QuarantineAndPublishAsync()
        {
            using var ingress = CreateIngress();
            var capture = await ingress.AcceptAsync(fixture.Configuration, fixture.CreateSubmission(0), token).ConfigureAwait(false);
            Assert.IsNotNull(capture);
            var transient = await ClaimAsync("transient").ConfigureAwait(false);
            await ingress.CompleteAsync(transient, token).ConfigureAwait(false);
            var standard = await ClaimAsync("standard").ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure,
                await ingress.FailAsync(standard, CaptureLaneHandlerResult.Terminal("evidence-invalid"), token).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(() => ingress.EnsureCanAcceptAsync(4, token).AsTask())
                .ConfigureAwait(false);
            using (var before = await OpenAsync(fixture.Root).ConfigureAwait(false))
            {
                Assert.AreEqual(2L, await ScalarLongAsync(before, "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'standard';")
                    .ConfigureAwait(false));
                Assert.AreEqual(1L, await ScalarLongAsync(before, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
            }
            reclaim.Armed = replayReclaimIntent;
            var status = (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status;
            Assert.AreEqual(replayReclaimIntent ? "silo-raw-admission-failed" : null, status);

            async Task<CaptureLaneLease> ClaimAsync(string name)
            {
                var lane = fixture.Policy.Definitions.Single(lane => lane.Name == name);
                var lease = await ingress.ClaimAsync(lane, "test", fixture.Configuration, token).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                return lease;
            }
        }
    }

    [TestMethod]
    [DataRow(RawEvidenceReleaseAction.Revalidate)]
    [DataRow(RawEvidenceReleaseAction.WriteOff)]
    public async Task OperatorEvidenceReleaseClearsDurableRequiredLanePressure(RawEvidenceReleaseAction action)
    {
        using var fixture = CreateFixture(new()
        {
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true, Required = true }]
        }, transient: RequiredTransient());
        var token = CancellationToken.None;
        var receipt = await fixture.AcceptAsync(0).ConfigureAwait(false);
        foreach (var name in new[] { "standard", "secondary" })
        {
            var lease = await fixture.ClaimAsync(name).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await fixture.Ingress.FailAsync(lease, CaptureLaneHandlerResult.Terminal("evidence-invalid"), token).ConfigureAwait(false);
        }
        await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(() => fixture.Ingress.EnsureCanAcceptAsync(4, token).AsTask())
            .ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneAvailability.Unhealthy, fixture.LaneState.Snapshot.Availability);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(2L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM capture_lane_definitions WHERE enabled = 1 AND required = 1 AND pressure_state = 2;")
            .ConfigureAwait(false));

        var backup = Path.Combine(fixture.Root, "evidence-backup");
        File.Move(receipt.StoredFrame.AbsolutePath, backup);
        fixture.Ingress.InvalidateEvidence();
        var target = (await fixture.Ingress.ReadEvidencePageAsync(10, null, token).ConfigureAwait(false)).Items.Single().Target;
        if (action == RawEvidenceReleaseAction.Revalidate)
            File.Move(backup, receipt.StoredFrame.AbsolutePath);
        var reason = action == RawEvidenceReleaseAction.Revalidate ? "evidence-restored" : "operator-approved-loss";
        await fixture.Ingress.ReleaseEvidenceAsync(target, action, "release-pressure", "operator", reason, token).ConfigureAwait(false);

        AssertRecoveredLaneHealth(fixture.LaneState);
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT MAX(pressure_state) FROM capture_lane_definitions;")
            .ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SiloAdmissionSettlementRecomputesRetentionHoldWhenEvidenceRestoredWithoutLaneRelease()
    {
        using var fixture = CreateFixture(new());
        var token = CancellationToken.None;
        var (settings, store, _) = await CreateRecoverySiloWithStoreAsync().ConfigureAwait(false);
        var faults = new SiloTestFaults();
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, faults,
            decorateReadStore: null, store, new SiloTestPayloads());
        var artifacts = new SiloArtifactStorage(storage, fixture.Time);
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(), fixture.Policy,
            laneState: fixture.LaneState, silo: storage, siloSettings: settings, siloArtifacts: artifacts);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);

        var capture = await ingress.AcceptAsync(fixture.Configuration, fixture.CreateSubmission(0), token).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        var captureId = capture.Manifest.Descriptor.Capture.CaptureId;
        var standardLane = fixture.Policy.Definitions.Single(static lane => lane.Name == "standard");
        var standardLease = await ingress.ClaimAsync(standardLane, "test", fixture.Configuration, token).ConfigureAwait(false);
        Assert.IsNotNull(standardLease);
        await ingress.CompleteAsync(standardLease, token).ConfigureAwait(false);

        // Interrupt after P3 so Silo has the committed segment while P4 settlement has not run yet.
        faults.Armed = SiloFaultPoint.AfterSegmentCommitted;
        Assert.AreEqual("silo-raw-admission-failed",
            (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);

        // Lose local staging after standard already acknowledged: raw_captures becomes missing_evidence with retention_hold = 1,
        // while no lane is quarantined (so settlement restores evidence with evidenceRestored=true, lanesReleased=false).
        File.Delete(capture.StoredFrame.AbsolutePath);
        ingress.InvalidateEvidence();
        Assert.IsFalse(await ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(token).ConfigureAwait(false));

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM raw_captures WHERE state = 'missing_evidence' AND retention_hold = 1;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state = 'completed';").ConfigureAwait(false));

        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);

        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM raw_captures WHERE state = 'committed' AND failure_reason IS NULL AND retention_hold = 0;")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM raw_capture_stage_events
            WHERE stage_key GLOB 'raw-evidence-silo-restored.*' AND state = 'evidence-restored' AND source = 'silo-publication';
            """).ConfigureAwait(false));
        var journal = new SqliteRawCaptureJournal(Path.Combine(fixture.Root, "journal", "raw-ingress.db"), 5, usesSilo: true);
        var holds = await journal.ReadRetentionHoldsAsync(token).ConfigureAwait(false);
        Assert.IsFalse(holds.Any(hold => hold.ArtifactId == capture.Manifest.Descriptor.Artifact.ArtifactId),
            $"Capture {captureId} should not remain retention-held after evidence restoration with no remaining holds.");
    }

    [TestMethod]
    public async Task ProcessingStepCallingEnsureRawFramePropagatesPublishedSiloEvidenceFailure()
    {
        using var fixture = CreateFixture(new());
        var token = CancellationToken.None;
        var (settings, installedStore, testStore) = await CreateRecoverySiloWithStoreAsync().ConfigureAwait(false);
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, new SiloTestFaults(),
            decorateReadStore: null, installedStore, new SiloTestPayloads());
        var artifacts = new SiloArtifactStorage(storage, fixture.Time);
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(), fixture.Policy,
            laneState: fixture.LaneState, silo: storage, siloSettings: settings, siloArtifacts: artifacts);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);

        var capture = await ingress.AcceptAsync(fixture.Configuration, fixture.CreateSubmission(0), token).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.IsFalse(File.Exists(capture.StoredFrame.AbsolutePath));

        var descriptor = capture.Manifest.Descriptor;
        var payloadMember = await storage.Index.ReadRoleAsync(new(SiloIdentityKind.Member,
            descriptor.Artifact.ArtifactId.ToString("D"),
            CaptureContractJson.ComputeDescriptorSha256(descriptor)), token).ConfigureAwait(false);
        Assert.IsNotNull(payloadMember);
        testStore.Delete(payloadMember.Locators.Single(static copy => copy.Eligible && copy.CopyState == "active").Locator);

        var standard = fixture.Policy.Definitions.Single(static lane => lane.Name == "standard");
        var lease = await ingress.ClaimAsync(standard, "test", fixture.Configuration, token).ConfigureAwait(false);
        Assert.IsNotNull(lease);

        using var processingStore = new SqliteCaptureProcessingStore(fixture.Options);
        using var frameStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var processingTelemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(fixture.Options, processingStore, frameStorage,
            processingTelemetry, NullLogger<CaptureProcessingPersistence>.Instance, silo: artifacts);

        var step = new DescriptorOnlyEnsureRawFrameStep();
        var graph = new CaptureProcessingGraph([
            new CaptureProcessingGraphNode("raw-reader", step, [], Required: true, RecipeName: null,
                OutputRole: null, OutputVariant: null, PlanSha256: new string('A', 64))
        ]);
        var item = new FrameProcessingItem(lease.Context.Configuration, lease.Context.Submission,
            lease.Context.RawCapture, lease.Context.WorkId, lease.Context.LeaseToken, lease.Context.Execution);

        await Assert.ThrowsExactlyAsync<PublishedSiloEvidenceException>(() =>
            FrameProcessingWorker.ProcessGraphItemAsync(item, graph, persistence, processingTelemetry,
                lease.Attempt, NullLogger.Instance, token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(1, step.Invocations);

        using var handler = new StandardCaptureLaneHandler(new SingleGraphPipelineFactory(graph), persistence,
            processingTelemetry, NullLogger<StandardCaptureLaneHandler>.Instance, ingress);
        var result = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Outcome);
        Assert.AreEqual("evidence-unavailable", result.Reason);
        Assert.IsTrue(result.PublishedSiloEvidenceFailed);
    }

    private static TransientDetectionOptions RequiredTransient() => new() { Mode = TransientOperatingMode.Edge, Required = true };

    private sealed class InterruptReclaim : IRawIngressFaultInjector
    {
        public bool Armed { get; set; }

        public void Inject(RawIngressFaultPoint point) { }

        public void AfterSiloStagingReclaimed()
        {
            if (!Armed) return;
            Armed = false;
            throw new IOException("Interrupted before Silo admission settlement.");
        }
    }

    private static void AssertRecoveredLaneHealth(CaptureLaneState state)
    {
        Assert.AreEqual(CaptureLaneAvailability.Healthy, state.Snapshot.Availability);
        Assert.AreEqual("accepting", state.Snapshot.Reason);
        Assert.AreEqual(0L, state.Snapshot.QuarantineCount);
    }

    internal static async Task<(SiloInstalledSettings Settings, SiloInstalledStore Store)> CreateRecoverySiloAsync()
    {
        var (settings, installed, _) = await CreateRecoverySiloWithStoreAsync().ConfigureAwait(false);
        return (settings, installed);
    }

    private static async Task<(SiloInstalledSettings Settings, SiloInstalledStore Store, SiloTestStore TestStore)> CreateRecoverySiloWithStoreAsync(string? dataRoot = null)
    {
        var instance = Guid.NewGuid();
        var binding = new SiloStorageBinding(instance.ToString("D"), CameraAgentSiloBinding.StoreIdFor(instance),
            CameraAgentSiloBinding.BucketFor(instance));
        const string accessKey = "fixturewriter0001";
        var section = new Microsoft.Extensions.Configuration.ConfigurationManager();
        section["Silo:StateContract"] = CameraAgentSiloBinding.StateContract;
        section["Silo:StoreId"] = binding.StoreId;
        section["Silo:Bucket"] = binding.Bucket;
        section["Silo:StorageBindingId"] = binding.BindingId;
        section["Silo:WriterGeneration"] = "1";
        section["Silo:Endpoint"] = "http://127.0.0.1";
        section["Silo:DataRoot"] = dataRoot;
        section["Silo:AccessKey"] = accessKey;
        section["Silo:SecretKey"] = "fixturewritersecret";
        var settings = SiloInstalledSettings.Read(section.GetSection("Silo"));
        var store = new SiloTestStore(binding.StoreId, binding.Bucket);
        await PutBindingAsync(CameraAgentSiloBinding.MarkerKey, JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.MarkerFormat,
            instanceId = binding.InstanceId,
            storeId = binding.StoreId,
            bucket = binding.Bucket,
            storageBindingId = binding.BindingId,
            stateContract = CameraAgentSiloBinding.StateContract
        })).ConfigureAwait(false);
        await PutBindingAsync(CameraAgentSiloBinding.FenceKey, JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.FenceFormat,
            instanceId = binding.InstanceId,
            storageBindingId = binding.BindingId,
            writerGeneration = 1,
            writerAccessKeyId = accessKey
        })).ConfigureAwait(false);
        return (settings, new SiloInstalledStore(settings, store, _ => ValueTask.FromResult(true)), store);

        async Task PutBindingAsync(string key, byte[] bytes)
        {
            var put = new SiloPutRequest(key, bytes.Length, SiloHash.Sha256(bytes),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)), new Dictionary<string, string>());
            Assert.IsTrue((await store.PutCreateOnlyAsync(put, CancellationToken.None).ConfigureAwait(false)).Created);
        }
    }

    private sealed class DescriptorOnlyEnsureRawFrameStep : IDescriptorOnlyCaptureProcessingStep
    {
        public string Name => "raw-reader";
        public int Order => 1;
        public int Invocations { get; private set; }

        public ValueTask ProcessAsync(CaptureDescriptorProcessingContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        async ValueTask ICaptureProcessingStep.ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            await context.EnsureRawFrameAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SingleGraphPipelineFactory(CaptureProcessingGraph graph) : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => graph;

        public CaptureProcessingGraph CreateRetainedGraph(CameraModuleConfig config) => graph;

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) =>
            throw new NotSupportedException();
    }

    private sealed class SecondaryEvidenceReader(SiloArtifactStorage artifacts) : ICaptureLaneHandler
    {
        public string Lane => "secondary";

        public async ValueTask<CaptureLaneHandlerResult> HandleAsync(CaptureLaneHandlerContext context, CancellationToken cancellationToken)
        {
            var capture = context.RawCapture;
            var evidence = await artifacts.ReadRawEvidenceAsync(capture.Manifest.Descriptor.Artifact.ArtifactId,
                capture.CommittedManifestSha256, Path.ChangeExtension(capture.StoredFrame.AbsolutePath, ".json"), cancellationToken)
                .ConfigureAwait(false);
            return evidence is null ? CaptureLaneHandlerResult.Terminal("evidence-invalid") : CaptureLaneHandlerResult.Success;
        }
    }
}
