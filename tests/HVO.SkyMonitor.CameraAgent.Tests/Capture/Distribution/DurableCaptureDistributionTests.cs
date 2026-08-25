using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class DurableCaptureDistributionTests
{
    private static readonly CaptureLaneFaultPoint[] CompletionFaultPoints =
    [
        CaptureLaneFaultPoint.BeforeCompletionCommit,
        CaptureLaneFaultPoint.AfterCompletionCommit
    ];
    private static readonly CaptureLaneFaultPoint[] HandlerFaultPoints =
    [
        CaptureLaneFaultPoint.BeforeHandler,
        CaptureLaneFaultPoint.AfterHandler
    ];

    [TestMethod]
    public async Task AcceptAsync_CreatesReferenceOnlyWorkAndRequiredCompletionsReleaseHold()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            UploadEnabled = true,
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
        });
        var receipt = await fixture.AcceptAsync(0).ConfigureAwait(false);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);

        Assert.AreEqual(3L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_contexts;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(1, Directory.EnumerateFiles(fixture.Root, "*.bin", SearchOption.AllDirectories).Count());
        Assert.AreEqual(4, new FileInfo(receipt.StoredFrame.AbsolutePath).Length);

        var standard = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(standard);
        await fixture.Store.CompleteAsync(standard, CancellationToken.None).ConfigureAwait(false);
        await fixture.Store.CompleteAsync(standard, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));

        var upload = await fixture.ClaimAsync("upload").ConfigureAwait(false);
        Assert.IsNotNull(upload);
        await fixture.Store.CompleteAsync(upload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));

        var optional = await fixture.ClaimAsync("secondary").ConfigureAwait(false);
        Assert.IsNotNull(optional);
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
        await fixture.Store.CompleteAsync(optional, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AcceptAsync_UsesInjectedClockForLaneDefinitionAndWorkTimestamps()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions { UploadEnabled = true });
        var expectedUnixMilliseconds = fixture.Time.GetUtcNow().ToUnixTimeMilliseconds();

        await fixture.AcceptAsync(0).ConfigureAwait(false);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);

        Assert.AreEqual(expectedUnixMilliseconds, await ScalarLongAsync(
            connection,
            "SELECT MIN(created_unix_ms) FROM capture_lane_definitions;").ConfigureAwait(false));
        Assert.AreEqual(expectedUnixMilliseconds, await ScalarLongAsync(
            connection,
            "SELECT MIN(created_unix_ms) FROM capture_lane_work;").ConfigureAwait(false));
        Assert.AreEqual(expectedUnixMilliseconds, await ScalarLongAsync(
            connection,
            "SELECT MAX(updated_unix_ms) FROM capture_lane_work;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task OrderedLane_RetryBlocksLaterCaptureAndSurvivesTimeAdvance()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions());
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        await fixture.AcceptAsync(1).ConfigureAwait(false);

        var first = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(first);
        await fixture.Store.FailAsync(
            first,
            CaptureLaneHandlerResult.Retry("test-retry"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(await fixture.ClaimAsync("standard").ConfigureAwait(false));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var retried = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(retried);
        Assert.AreEqual(first.WorkId, retried.WorkId);
        Assert.AreEqual(2, retried.Attempt);
        await fixture.Store.CompleteAsync(retried, CancellationToken.None).ConfigureAwait(false);

        var second = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(second);
        Assert.AreNotEqual(first.WorkId, second.WorkId);
    }

    [TestMethod]
    public async Task DeferredEnvironmentAssociation_RestartDoesNotConsumeAttemptAndConverges()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions());
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var first = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(first);

        var disposition = await fixture.Store.FailAsync(
            first, CaptureLaneHandlerResult.Wait(ProcessingReasonCodes.EnvironmentAssociationPending),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CaptureLaneHandlerOutcome.Deferred, disposition);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var resumed = await fixture.Store.ClaimAsync(
            fixture.Policy.Definitions.Single(static lane => lane.Name == "standard"),
            "restart", fixture.Configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(resumed);
        Assert.AreEqual(first.WorkId, resumed.WorkId);
        Assert.AreEqual(first.Attempt, resumed.Attempt);
        await fixture.Store.CompleteAsync(resumed, CancellationToken.None).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual("completed", await ScalarStringAsync(connection,
            "SELECT state FROM capture_lane_work WHERE lane_name = 'standard';").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT attempt_count FROM capture_lane_work WHERE lane_name = 'standard';").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ExpiredLease_IsReclaimedAndStaleOwnerCannotAcknowledge()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            LeaseSeconds = 10,
            LeaseRenewalSeconds = 4
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var first = await fixture.ClaimAsync("standard", "owner-a").ConfigureAwait(false);
        Assert.IsNotNull(first);
        fixture.Time.Advance(TimeSpan.FromSeconds(11));

        var reclaimed = await fixture.ClaimAsync("standard", "owner-b").ConfigureAwait(false);
        Assert.IsNotNull(reclaimed);
        Assert.AreEqual(first.WorkId, reclaimed.WorkId);
        Assert.AreNotEqual(first.LeaseToken, reclaimed.LeaseToken);
        await Assert.ThrowsExactlyAsync<CaptureLaneLeaseLostException>(async () =>
            await fixture.Store.CompleteAsync(first, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await fixture.Store.CompleteAsync(reclaimed, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OptionalHardLimit_AbandonsOnlyOptionalWorkWhileRequiredLimitRefusesNextCapture()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            OptionalMaximumPendingCount = 1,
            RequiredMaximumPendingCount = 2,
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        await fixture.AcceptAsync(1).ConfigureAwait(false);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);

        Assert.AreEqual(1L, await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'pending';").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'abandoned' AND failure_reason = 'optional-pressure';").ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
            await fixture.Store.EnsureCanAcceptAsync(4, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OptionalBlockedTransientLaneDoesNotBlockStandardIngress()
    {
        using var fixture = CreateFixture(
            new CaptureDistributionOptions { OptionalMaximumPendingCount = 1 },
            transient: new TransientDetectionOptions { Mode = TransientOperatingMode.Edge, Required = false });

        await fixture.AcceptAsync(0).ConfigureAwait(false);
        await fixture.AcceptAsync(1).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(2L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state = 'pending';").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'transient' AND state = 'pending';").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'transient' AND state = 'abandoned' AND failure_reason = 'optional-pressure';")
            .ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RequiredBlockedTransientLaneRefusesNextIngressWithoutLosingStandardWork()
    {
        using var fixture = CreateFixture(
            new CaptureDistributionOptions { RequiredMaximumPendingCount = 1 },
            transient: new TransientDetectionOptions { Mode = TransientOperatingMode.Edge, Required = true });
        await fixture.AcceptAsync(0).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
            await fixture.AcceptAsync(1).ConfigureAwait(false)).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state = 'pending';").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'transient' AND state = 'pending';").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DuplicateAccept_DoesNotDuplicateLaneWorkOrContext()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions { UploadEnabled = true });
        var submission = fixture.CreateSubmission(0);
        var first = await fixture.Ingress.AcceptAsync(
            fixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false);
        var duplicate = await fixture.Ingress.AcceptAsync(
            fixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);

        Assert.AreEqual(RawIngressOutcome.Committed, first!.Outcome);
        Assert.AreEqual(RawIngressOutcome.Existing, duplicate!.Outcome);
        Assert.AreEqual(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_contexts;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task EnrichedEvidence_SurvivesFrameStrippedContextClaimAndRestart()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions());
        var submission = fixture.CreateSubmission(0, includeCycleEvidence: true);
        var expected = submission.CycleEvidence;
        var receipt = await fixture.Ingress.AcceptAsync(
            fixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(expected, receipt.Manifest.Descriptor.CycleEvidence);

        byte[] contextJson;
        string contextSha256;
        using (var connection = await OpenAsync(fixture.Root).ConfigureAwait(false))
        {
            contextJson = await ScalarBytesAsync(
                connection, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false);
            contextSha256 = await ScalarStringAsync(
                connection, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false);
        }
        var envelope = CaptureLaneEnvelopeSerializer.Deserialize(contextJson, contextSha256);
        Assert.IsNull(envelope.Submission.Result.Frame);
        Assert.IsNull(envelope.Submission.Result.Artifacts);
        Assert.AreEqual(expected, envelope.Submission.CycleEvidence);

        var restarted = fixture.RestartLaneStore();
        await restarted.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
        var lease = await restarted.ClaimAsync(
            fixture.Policy.Definitions.Single(static definition => definition.Name == "standard"),
            "restart-owner",
            fixture.Configuration,
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(lease);
        Assert.IsNull(lease.Context.Submission.Result.Frame);
        Assert.AreEqual(expected, lease.Context.Submission.CycleEvidence);
        Assert.AreEqual(expected, lease.Context.RawCapture.Manifest.Descriptor.CycleEvidence);
        Assert.AreEqual(receipt.CommittedManifestSha256, lease.Context.RawCapture.CommittedManifestSha256);
    }

    [TestMethod]
    public async Task InFlightOldPipelineEnvelopeSurvivesConfigurationMigrationUntilDrain()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions());
        var oldStep = new CaptureProcessingStepConfig("Annotation", "sky-annotation", DependsOn: ["preview"]);
        var oldConfiguration = fixture.Configuration with
        {
            ProcessingSteps = null,
            Pipeline = new CapturePipelineConfig([oldStep], CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        var submission = fixture.CreateSubmission(0);
        _ = await fixture.Ingress.AcceptAsync(oldConfiguration, submission, CancellationToken.None).ConfigureAwait(false);
        var newConfiguration = fixture.Configuration with
        {
            ProcessingSteps = null,
            Pipeline = new CapturePipelineConfig([
                new CaptureProcessingStepConfig("OverlayManifest", "overlay-manifest", DependsOn: ["combined-preview"])
            ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };

        var restarted = fixture.RestartLaneStore();
        await restarted.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
        var oldLease = await restarted.ClaimAsync(
            fixture.Policy.Definitions.Single(static definition => definition.Name == "standard"),
            "restart-owner", newConfiguration, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(oldLease);
        Assert.AreEqual("sky-annotation", oldLease.Context.Configuration.Pipeline!.Steps.Single().Id);
        await restarted.CompleteAsync(oldLease, CancellationToken.None).ConfigureAwait(false);
        var newEnvelope = CaptureLaneEnvelopeSerializer.Serialize(newConfiguration, fixture.CreateSubmission(1));
        var decoded = CaptureLaneEnvelopeSerializer.Deserialize(newEnvelope.Json, newEnvelope.Sha256);
        Assert.AreEqual("overlay-manifest", decoded.Configuration.Pipeline!.Steps.Single().Id);
        Assert.AreEqual("sky-annotation", oldLease.Context.Configuration.Pipeline!.Steps.Single().Id);
    }

    [TestMethod]
    public async Task AlteredJournalManifest_IsQuarantinedBeforeLaneHandling()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions());
        var receipt = await fixture.AcceptAsync(0).ConfigureAwait(false);
        var altered = receipt.Manifest with
        {
            Descriptor = receipt.Manifest.Descriptor with
            {
                Capture = receipt.Manifest.Descriptor.Capture with
                {
                    CaptureSequence = receipt.Manifest.Descriptor.Capture.CaptureSequence + 1
                }
            }
        };
        Assert.IsTrue(altered.Validate().IsValid);
        using (var connection = await OpenAsync(fixture.Root).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE raw_captures SET manifest_json = $manifest;";
            command.Parameters.AddWithValue("$manifest", CaptureContractJson.Serialize(altered));
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        var lease = await fixture.ClaimAsync("standard").ConfigureAwait(false);

        Assert.IsNull(lease);
        using var verification = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(
            "quarantined",
            await ScalarStringAsync(verification, "SELECT state FROM capture_lane_work;").ConfigureAwait(false));
        Assert.AreEqual(
            "evidence-invalid",
            await ScalarStringAsync(verification, "SELECT failure_reason FROM capture_lane_work;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task AlteredJournalManifest_IsQuarantinedBeforeRollingWindowUse()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions());
        var receipt = await fixture.AcceptAsync(0).ConfigureAwait(false);
        var altered = receipt.Manifest with
        {
            Descriptor = receipt.Manifest.Descriptor with
            {
                Capture = receipt.Manifest.Descriptor.Capture with
                {
                    CaptureSequence = receipt.Manifest.Descriptor.Capture.CaptureSequence + 1
                }
            }
        };
        using (var connection = await OpenAsync(fixture.Root).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE raw_captures SET manifest_json = $manifest;";
            command.Parameters.AddWithValue("$manifest", CaptureContractJson.Serialize(altered));
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        using var processingStore = new SqliteCaptureProcessingStore(Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            RawIngressReserveBytes = 0
        }));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await processingStore.ReadRecentRawInputsAsync(
                receipt.Manifest.Descriptor.Capture.AgentId,
                receipt.Manifest.Descriptor.Capture.CaptureSequence,
                1,
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        using var verification = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(
            "quarantined",
            await ScalarStringAsync(verification, "SELECT state FROM capture_lane_work;").ConfigureAwait(false));
        Assert.AreEqual(
            "evidence-invalid",
            await ScalarStringAsync(verification, "SELECT failure_reason FROM capture_lane_work;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(verification, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LegacyDescriptorFallback_KeepsCycleEvidenceNull()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions());
        var receipt = await fixture.AcceptAsync(0).ConfigureAwait(false);
        Assert.IsNull(receipt.Manifest.Descriptor.CycleEvidence);
        using (var connection = await OpenAsync(fixture.Root).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM capture_lane_contexts;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var lease = await fixture.ClaimAsync("standard").ConfigureAwait(false);

        Assert.IsNotNull(lease);
        Assert.IsNull(lease.Context.Submission.Result.Frame);
        Assert.IsNull(lease.Context.Submission.CycleEvidence);
        Assert.IsNull(lease.Context.RawCapture.Manifest.Descriptor.CycleEvidence);
    }

    [TestMethod]
    public async Task Service_RediscoversWithoutWakeupAndBlockedOptionalLaneDoesNotBlockStandard()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            UploadEnabled = true,
            ShutdownDrainSeconds = 1,
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        using var standard = new StandardCaptureLaneHandler(
            new EmptyPipelineFactory(),
            NullLogger<StandardCaptureLaneHandler>.Instance,
            fixture.Ingress);
        var secondary = new GatedLaneHandler("secondary");
        using var service = new CaptureDistributionService(
            new ConfigurationAccessor(fixture.Configuration),
            fixture.Ingress,
            fixture.Store,
            fixture.Policy,
            [standard, new SuccessfulLaneHandler("upload"), secondary],
            standard,
            fixture.Options,
            fixture.Time,
            new NullCaptureLaneFaultInjector(),
            fixture.LaneTelemetry,
            fixture.LaneState,
            NullLogger<CaptureDistributionService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await secondary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitForCountAsync(
            fixture.Root,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name IN ('standard', 'upload') AND state = 'completed';",
            2).ConfigureAwait(false);

        await fixture.AcceptAsync(1).ConfigureAwait(false);
        service.NotifyCommittedCapture();
        await WaitForCountAsync(
            fixture.Root,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name IN ('standard', 'upload') AND state = 'completed';",
            4).ConfigureAwait(false);
        await fixture.Store.EnsureCanAcceptAsync(4, CancellationToken.None).ConfigureAwait(false);

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(0L, await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'leased';").ConfigureAwait(false));
        Assert.AreEqual(2L, await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'pending';").ConfigureAwait(false));
        await Phase14ScenarioEvidence.RecordAsync(
            "optional-lane-backlog",
            "secondary-blocked-standard-and-upload-drained",
            null,
            ["optional-lane-remained-pending", "standard-lane-drained", "upload-lane-drained", "leased-work-released-on-shutdown"])
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CameraModuleRunner_DurableLaneOutagesDoNotChangeCaptureCadence()
    {
        foreach (var cadenceMode in new[] { CaptureCadenceMode.MinimumStartInterval, CaptureCadenceMode.Continuous })
        {
            var baseline = await RunCadenceScenarioAsync(cadenceMode, blockedLane: null).ConfigureAwait(false);
            var optionalOutage = await RunCadenceScenarioAsync(cadenceMode, "secondary").ConfigureAwait(false);
            var uploadOutage = await RunCadenceScenarioAsync(cadenceMode, "upload").ConfigureAwait(false);

            CollectionAssert.AreEqual(baseline.RequestedStarts, optionalOutage.RequestedStarts, cadenceMode.ToString());
            CollectionAssert.AreEqual(baseline.RequestedStarts, uploadOutage.RequestedStarts, cadenceMode.ToString());
            CollectionAssert.AreEqual(baseline.ActualStarts, optionalOutage.ActualStarts, cadenceMode.ToString());
            CollectionAssert.AreEqual(baseline.ActualStarts, uploadOutage.ActualStarts, cadenceMode.ToString());
            CollectionAssert.AreEqual(baseline.MonotonicJitter, optionalOutage.MonotonicJitter, cadenceMode.ToString());
            CollectionAssert.AreEqual(baseline.MonotonicJitter, uploadOutage.MonotonicJitter, cadenceMode.ToString());
            Assert.IsTrue(baseline.MonotonicJitter.All(static jitter => jitter >= TimeSpan.Zero), cadenceMode.ToString());
            Assert.IsTrue(optionalOutage.MonotonicJitter.All(static jitter => jitter >= TimeSpan.Zero), cadenceMode.ToString());
            Assert.IsTrue(uploadOutage.MonotonicJitter.All(static jitter => jitter >= TimeSpan.Zero), cadenceMode.ToString());

            var expectedTimerCount = cadenceMode == CaptureCadenceMode.Continuous ? 0 : 3;
            Assert.AreEqual(expectedTimerCount, baseline.TimerCreationCount, cadenceMode.ToString());
            Assert.AreEqual(expectedTimerCount, optionalOutage.TimerCreationCount, cadenceMode.ToString());
            Assert.AreEqual(expectedTimerCount, uploadOutage.TimerCreationCount, cadenceMode.ToString());
        }
    }

    [TestMethod]
    public async Task SchemaV1Migration_BackfillsEnabledLanesAtomicallyAndRetriesAfterInterruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-lanes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "journal"));
        try
        {
            var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = V1SchemaWithCaptureSql;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                command.CommandText = """
                    UPDATE raw_capture_sequences SET last_sequence = 3 WHERE agent_id = 'agent';
                    INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
                    VALUES ('55555555555555555555555555555555', '66666666666666666666666666666666', 'agent', 3);
                    INSERT INTO raw_captures(
                        capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                        manifest_sha256, payload_sha256, payload_length, payload_relative_path,
                        sidecar_relative_path, manifest_json, exposure_started_unix_ms,
                        durable_ingress_unix_ms, committed_unix_ms, state, retention_hold)
                    VALUES (
                        '55555555555555555555555555555555', '66666666666666666666666666666666', 'agent', 3,
                        '1111111111111111111111111111111111111111111111111111111111111111',
                        '2222222222222222222222222222222222222222222222222222222222222222',
                        '3333333333333333333333333333333333333333333333333333333333333333',
                        4, 'frames/fresh.bin', 'frames/fresh.json', X'7B7D', $now, $now, $now, 'committed', 1);
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                foreach (var manifest in new[]
                {
                    CreateV1MigrationManifest(
                        1,
                        "11111111-1111-1111-1111-111111111111",
                        "22222222-2222-2222-2222-222222222222",
                        "frames/raw.bin"),
                    CreateV1MigrationManifest(
                        2,
                        "33333333-3333-3333-3333-333333333333",
                        "44444444-4444-4444-4444-444444444444",
                        "frames/released.bin"),
                    CreateV1MigrationManifest(
                        3,
                        "55555555-5555-5555-5555-555555555555",
                        "66666666-6666-6666-6666-666666666666",
                        "frames/fresh.bin")
                })
                {
                    command.CommandText = """
                        UPDATE raw_captures
                        SET descriptor_sha256 = $descriptor, manifest_sha256 = $manifest_sha,
                            manifest_json = $manifest
                        WHERE capture_sequence = $sequence;
                        """;
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue(
                        "$descriptor",
                        CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor));
                    command.Parameters.AddWithValue(
                        "$manifest_sha",
                        CaptureContractJson.ComputeManifestSha256(manifest));
                    command.Parameters.AddWithValue("$manifest", CaptureContractJson.Serialize(manifest));
                    command.Parameters.AddWithValue("$sequence", manifest.Descriptor.Capture.CaptureSequence);
                    Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                }
            }
            var distribution = new CaptureDistributionOptions
            {
                UploadEnabled = true,
                OptionalMaximumPendingCount = 1,
                SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
            };
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                CaptureDistribution = distribution
            });
            var policy = new CaptureLanePolicy(options);
            var interrupted = new SqliteRawCaptureJournal(
                databasePath,
                1,
                faultInjector: new ThrowingFaultInjector(RawIngressFaultPoint.BeforeMigrationCommit),
                distributionOptions: distribution,
                laneFaultInjector: new NullCaptureLaneFaultInjector());

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await interrupted.InitializeAsync(policy.Definitions, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            using (var verifyRollback = await OpenAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(1L, await ScalarLongAsync(verifyRollback, "PRAGMA user_version;").ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarLongAsync(
                    verifyRollback,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'capture_lane_work';").ConfigureAwait(false));
            }

            var retry = new SqliteRawCaptureJournal(
                databasePath,
                1,
                distributionOptions: distribution,
                laneFaultInjector: new NullCaptureLaneFaultInjector());
            await retry.InitializeAsync(policy.Definitions, CancellationToken.None).ConfigureAwait(false);
            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual((long)SqliteRawCaptureJournal.CurrentSchemaVersion,
                await ScalarLongAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(6L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false));
            Assert.AreEqual(3L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM capture_lane_contexts;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'abandoned';").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify, "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'pending';").ConfigureAwait(false));
            Assert.AreEqual(2L, await ScalarLongAsync(
                verify, "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'secondary';").ConfigureAwait(false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DirectIngress_EnforcesRequiredHardLimitInsideSerializedAcceptance()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions { RequiredMaximumPendingCount = 1 });
        await fixture.Ingress.AcceptAsync(
            fixture.Configuration, fixture.CreateSubmission(0), CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
            await fixture.Ingress.AcceptAsync(
                fixture.Configuration, fixture.CreateSubmission(1), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DuplicateAccept_BypassesPressureWithoutCreatingAdditionalWork()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions { RequiredMaximumPendingCount = 1 });
        var submission = fixture.CreateSubmission(0);
        var committed = await fixture.Ingress.AcceptAsync(
            fixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false);

        var replayed = await fixture.Ingress.AcceptAsync(
            fixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(RawIngressOutcome.Committed, committed!.Outcome);
        Assert.AreEqual(RawIngressOutcome.Existing, replayed!.Outcome);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CommittedReplay_WithReleasedEvidenceFailsBeforeReplacementWrite()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions());
        var submission = fixture.CreateSubmission(0);
        var receipt = await fixture.Ingress.AcceptAsync(
            fixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false);
        var standard = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(standard);
        await fixture.Store.CompleteAsync(standard, CancellationToken.None).ConfigureAwait(false);
        File.Delete(receipt!.StoredFrame.AbsolutePath);
        File.Delete(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json"));

        await Assert.ThrowsExactlyAsync<RawIngressConflictException>(async () =>
            await fixture.Ingress.AcceptAsync(
                fixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsFalse(File.Exists(receipt.StoredFrame.AbsolutePath));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")));
    }

    [TestMethod]
    public async Task ReservedButUncommittedRetry_StillEnforcesCurrentPressure()
    {
        using var fixture = CreateFixture(
            new CaptureDistributionOptions { RequiredMaximumPendingCount = 1 },
            rawFaultInjector: new ThrowingFaultInjector(RawIngressFaultPoint.ValidationCompleted));
        var interrupted = fixture.CreateSubmission(0);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await fixture.Ingress.AcceptAsync(
                fixture.Configuration, interrupted, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await fixture.Ingress.AcceptAsync(
            fixture.Configuration, fixture.CreateSubmission(1), CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
            await fixture.Ingress.AcceptAsync(
                fixture.Configuration, interrupted, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_capture_assignments;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RetryLimit_QuarantinesRequiredWorkAndKeepsRawHeld()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions { MaximumAttempts = 2 });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var first = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.AreEqual(
            CaptureLaneHandlerOutcome.RetryableFailure,
            await fixture.Store.FailAsync(first, CaptureLaneHandlerResult.Retry("test-retry"), CancellationToken.None).ConfigureAwait(false));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var second = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(second);
        Assert.AreEqual(
            CaptureLaneHandlerOutcome.TerminalFailure,
            await fixture.Store.FailAsync(second, CaptureLaneHandlerResult.Retry("test-retry"), CancellationToken.None).ConfigureAwait(false));

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual("quarantined", await ScalarStringAsync(
            connection, "SELECT state FROM capture_lane_work WHERE lane_name = 'standard';").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InvalidContext_QuarantinesRequiredAndAbandonsOptionalWithoutLeasing()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        using (var corrupt = await OpenAsync(fixture.Root).ConfigureAwait(false))
        {
            using var command = corrupt.CreateCommand();
            command.CommandText = "UPDATE capture_lane_contexts SET context_sha256 = $sha;";
            command.Parameters.AddWithValue("$sha", new string('D', 64));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        Assert.IsNull(await fixture.ClaimAsync("standard").ConfigureAwait(false));
        Assert.IsNull(await fixture.ClaimAsync("secondary").ConfigureAwait(false));
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual("quarantined", await ScalarStringAsync(
            connection, "SELECT state FROM capture_lane_work WHERE lane_name = 'standard';").ConfigureAwait(false));
        Assert.AreEqual("abandoned", await ScalarStringAsync(
            connection, "SELECT state FROM capture_lane_work WHERE lane_name = 'secondary';").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DisablingOptionalLane_AbandonsLeaseAndRecomputesRawHold()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var standard = await fixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(standard);
        await fixture.Store.CompleteAsync(standard, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(await fixture.ClaimAsync("secondary").ConfigureAwait(false));
        var disabledOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            CaptureDistribution = new CaptureDistributionOptions
            {
                SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = false }]
            }
        });
        var disabledPolicy = new CaptureLanePolicy(disabledOptions);
        var journal = new SqliteRawCaptureJournal(Path.Combine(fixture.Root, "journal", "raw-ingress.db"), 1);

        await journal.InitializeAsync(disabledPolicy.Definitions, CancellationToken.None).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual("abandoned", await ScalarStringAsync(
            connection, "SELECT state FROM capture_lane_work WHERE lane_name = 'secondary';").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RemovingRequiredLane_WithUnfinishedWorkRefusesStartup()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true, Required = true }]
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var replacementOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            CaptureDistribution = new CaptureDistributionOptions()
        });
        var replacementPolicy = new CaptureLanePolicy(replacementOptions);
        var journal = new SqliteRawCaptureJournal(Path.Combine(fixture.Root, "journal", "raw-ingress.db"), 1);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await journal.InitializeAsync(replacementPolicy.Definitions, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual("pending", await ScalarStringAsync(
            connection, "SELECT state FROM capture_lane_work WHERE lane_name = 'secondary';").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RenewalFailure_CancelsHandlerAndReclaimsWorkBeforeRetry()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            LeaseSeconds = 10,
            LeaseRenewalSeconds = 1,
            PollIntervalMilliseconds = 100
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var store = new OneShotRenewalFailureStore(fixture.Store);
        using var standard = new StandardCaptureLaneHandler(
            new EmptyPipelineFactory(), NullLogger<StandardCaptureLaneHandler>.Instance, fixture.Ingress);
        var handler = new CancellationThenSuccessHandler();
        using var service = new CaptureDistributionService(
            new ConfigurationAccessor(fixture.Configuration), fixture.Ingress, store, fixture.Policy,
            [handler], standard, fixture.Options, TimeProvider.System,
            new NullCaptureLaneFaultInjector(), fixture.LaneTelemetry, fixture.LaneState,
            NullLogger<CaptureDistributionService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitForCountAsync(
            fixture.Root,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state = 'completed';",
            1).ConfigureAwait(false);
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(2L, await ScalarLongAsync(
            connection, "SELECT attempt_count FROM capture_lane_work WHERE lane_name = 'standard';").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PersistentClaimFailure_MarksLaneHealthUnhealthy()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            PollIntervalMilliseconds = 100,
            ShutdownDrainSeconds = 1
        });
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        var store = new FailingClaimStore(fixture.Store);
        using var standard = new StandardCaptureLaneHandler(
            new EmptyPipelineFactory(), NullLogger<StandardCaptureLaneHandler>.Instance, fixture.Ingress);
        using var service = new CaptureDistributionService(
            new ConfigurationAccessor(fixture.Configuration), fixture.Ingress, store, fixture.Policy,
            [standard], standard, fixture.Options, TimeProvider.System,
            new NullCaptureLaneFaultInjector(), fixture.LaneTelemetry, fixture.LaneState,
            NullLogger<CaptureDistributionService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await store.ClaimAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (fixture.LaneState.Snapshot.Availability != CaptureLaneAvailability.Unhealthy &&
               DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.AreEqual(CaptureLaneAvailability.Unhealthy, fixture.LaneState.Snapshot.Availability);
        Assert.AreEqual("lane-claim-failed", fixture.LaneState.Snapshot.Reason);
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OptionalHardPressure_RemainsLatchedUntilBelowRecoveryThreshold()
    {
        using var fixture = CreateFixture(new CaptureDistributionOptions
        {
            OptionalMaximumPendingCount = 5,
            PressureRecoveryPercent = 80,
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
        });
        for (var index = 0; index < 6; index++)
        {
            await fixture.AcceptAsync(index).ConfigureAwait(false);
        }
        var first = await fixture.ClaimAsync("secondary").ConfigureAwait(false);
        Assert.IsNotNull(first);
        await fixture.Store.CompleteAsync(first, CancellationToken.None).ConfigureAwait(false);
        await fixture.AcceptAsync(6).ConfigureAwait(false);
        var second = await fixture.ClaimAsync("secondary").ConfigureAwait(false);
        Assert.IsNotNull(second);
        await fixture.Store.CompleteAsync(second, CancellationToken.None).ConfigureAwait(false);
        await fixture.AcceptAsync(7).ConfigureAwait(false);

        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual(2L, await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'abandoned';").ConfigureAwait(false));
        Assert.AreEqual(4L, await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'pending';").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(
            connection,
            "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'secondary';").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CompletionCommitFault_DoesNotTerminateWorkerOrDuplicateCompletion()
    {
        var fault = new OneShotLaneFaultInjector(CaptureLaneFaultPoint.AfterCompletionCommit);
        using var fixture = CreateFixture(new CaptureDistributionOptions(), fault);
        await fixture.AcceptAsync(0).ConfigureAwait(false);
        await fixture.AcceptAsync(1).ConfigureAwait(false);
        using var standard = new StandardCaptureLaneHandler(
            new EmptyPipelineFactory(), NullLogger<StandardCaptureLaneHandler>.Instance, fixture.Ingress);
        using var service = new CaptureDistributionService(
            new ConfigurationAccessor(fixture.Configuration), fixture.Ingress, fixture.Store, fixture.Policy,
            [standard, new SuccessfulLaneHandler("upload")], standard, fixture.Options, fixture.Time,
            fault, fixture.LaneTelemetry, fixture.LaneState, NullLogger<CaptureDistributionService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitForCountAsync(
            fixture.Root,
            "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state = 'completed';",
            2).ConfigureAwait(false);
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LaneCommitAndClaimFaults_RetryToOneDurableWorkItem()
    {
        using (var commitFixture = CreateFixture(
                   new CaptureDistributionOptions(),
                   new OneShotLaneFaultInjector(CaptureLaneFaultPoint.AfterWorkRowsInserted)))
        {
            var submission = commitFixture.CreateSubmission(0);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await commitFixture.Ingress.AcceptAsync(
                    commitFixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await commitFixture.Ingress.AcceptAsync(
                commitFixture.Configuration, submission, CancellationToken.None).ConfigureAwait(false);
            using var connection = await OpenAsync(commitFixture.Root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false));
            await Phase14ScenarioEvidence.RecordAsync(
                "capture-lane-work-rows-commit",
                "fault-point-AfterWorkRowsInserted",
                "AfterWorkRowsInserted",
                ["commit-fault-observed", "raw-capture-converged-once", "lane-work-converged-once"])
                .ConfigureAwait(false);
        }

        using var claimFixture = CreateFixture(
            new CaptureDistributionOptions(),
            new OneShotLaneFaultInjector(CaptureLaneFaultPoint.AfterClaimCommitted));
        await claimFixture.AcceptAsync(0).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await claimFixture.ClaimAsync("standard").ConfigureAwait(false)).ConfigureAwait(false);
        claimFixture.Time.Advance(TimeSpan.FromSeconds(121));
        var reclaimed = await claimFixture.ClaimAsync("standard").ConfigureAwait(false);
        Assert.IsNotNull(reclaimed);
        await claimFixture.Store.CompleteAsync(reclaimed, CancellationToken.None).ConfigureAwait(false);
        await Phase14ScenarioEvidence.RecordAsync(
            "capture-lane-claim-commit",
            "fault-point-AfterClaimCommitted",
            "AfterClaimCommitted",
            ["claim-fault-observed", "expired-lease-reclaimed", "reclaimed-work-completed"])
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CompletionRetryAndQuarantineFaults_PreserveCommittedTransition()
    {
        foreach (var point in CompletionFaultPoints)
        {
            using var fixture = CreateFixture(new CaptureDistributionOptions(), new OneShotLaneFaultInjector(point));
            await fixture.AcceptAsync(0).ConfigureAwait(false);
            var lease = await fixture.ClaimAsync("standard").ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await fixture.Store.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await fixture.Store.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            await Phase14ScenarioEvidence.RecordAsync(
                point == CaptureLaneFaultPoint.BeforeCompletionCommit
                    ? "capture-lane-completion-before-commit"
                    : "capture-lane-completion-after-commit",
                $"fault-point-{point}",
                point.ToString(),
                ["completion-fault-observed", "completion-retry-succeeded", "committed-transition-preserved"])
                .ConfigureAwait(false);
        }

        using (var retryFixture = CreateFixture(
                   new CaptureDistributionOptions(),
                   new OneShotLaneFaultInjector(CaptureLaneFaultPoint.AfterRetryCommit)))
        {
            await retryFixture.AcceptAsync(0).ConfigureAwait(false);
            var lease = await retryFixture.ClaimAsync("standard").ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await retryFixture.Store.FailAsync(
                    lease, CaptureLaneHandlerResult.Retry("test-retry"), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            using var connection = await OpenAsync(retryFixture.Root).ConfigureAwait(false);
            Assert.AreEqual("retry_wait", await ScalarStringAsync(
                connection, "SELECT state FROM capture_lane_work;").ConfigureAwait(false));
            var backlog = (await retryFixture.Store.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(static item => item.Lane == "standard");
            Assert.AreEqual(1, backlog.RetryCount);
            await Phase14ScenarioEvidence.RecordAsync(
                "capture-lane-retry-commit",
                "fault-point-AfterRetryCommit",
                "AfterRetryCommit",
                ["retry-fault-observed", "retry-transition-committed"])
                .ConfigureAwait(false);
        }

        using (var quarantineFixture = CreateFixture(
                   new CaptureDistributionOptions { MaximumAttempts = 1 },
                   new OneShotLaneFaultInjector(CaptureLaneFaultPoint.AfterQuarantineCommit)))
        {
            await quarantineFixture.AcceptAsync(0).ConfigureAwait(false);
            var lease = await quarantineFixture.ClaimAsync("standard").ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await quarantineFixture.Store.FailAsync(
                    lease, CaptureLaneHandlerResult.Retry("test-retry"), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            using var connection = await OpenAsync(quarantineFixture.Root).ConfigureAwait(false);
            Assert.AreEqual("quarantined", await ScalarStringAsync(
                connection, "SELECT state FROM capture_lane_work;").ConfigureAwait(false));
            await Phase14ScenarioEvidence.RecordAsync(
                "capture-lane-quarantine-commit",
                "fault-point-AfterQuarantineCommit",
                "AfterQuarantineCommit",
                ["quarantine-fault-observed", "quarantine-transition-committed"])
                .ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task HandlerBoundaryFaults_AreReleasedAndRecoveredByLiveWorker()
    {
        foreach (var point in HandlerFaultPoints)
        {
            var fault = new OneShotLaneFaultInjector(point);
            using var fixture = CreateFixture(new CaptureDistributionOptions(), fault);
            await fixture.AcceptAsync(0).ConfigureAwait(false);
            using var standard = new StandardCaptureLaneHandler(
                new EmptyPipelineFactory(), NullLogger<StandardCaptureLaneHandler>.Instance, fixture.Ingress);
            using var service = new CaptureDistributionService(
                new ConfigurationAccessor(fixture.Configuration), fixture.Ingress, fixture.Store, fixture.Policy,
                [standard, new SuccessfulLaneHandler("upload")], standard, fixture.Options, fixture.Time,
                fault, fixture.LaneTelemetry, fixture.LaneState, NullLogger<CaptureDistributionService>.Instance);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForCountAsync(
                fixture.Root,
                "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state = 'completed';",
                1).ConfigureAwait(false);
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Phase14ScenarioEvidence.RecordAsync(
                point == CaptureLaneFaultPoint.BeforeHandler ? "lease-crash-before-work" : "lease-crash-after-work",
                $"fault-point-{point}",
                point.ToString(),
                ["handler-fault-observed", "lease-released", "live-worker-recovered", "work-completed-once"])
                .ConfigureAwait(false);
        }
    }

    private static Fixture CreateFixture(
        CaptureDistributionOptions distribution,
        ICaptureLaneFaultInjector? laneFaultInjector = null,
        IRawIngressFaultInjector? rawFaultInjector = null,
        CameraModuleConfig? configuration = null,
        TransientDetectionOptions? transient = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-lanes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
        var state = new RawIngressState(time);
        var telemetry = new RawIngressTelemetry(state);
        var hostOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0,
            CaptureDistribution = distribution,
            TransientDetection = transient ?? new TransientDetectionOptions()
        });
        var policy = new CaptureLanePolicy(hostOptions);
        var laneState = new CaptureLaneState(time, hostOptions);
        var laneTelemetry = new CaptureLaneTelemetry(laneState);
        var resolvedLaneFaultInjector = laneFaultInjector ?? new NullCaptureLaneFaultInjector();
        var ingress = new RawCaptureIngress(
            hostOptions,
            new FixedCapacityProvider(),
            state,
            time,
            telemetry,
            NullLogger<RawCaptureIngress>.Instance,
            rawFaultInjector ?? new NullRawIngressFaultInjector(),
            policy,
            resolvedLaneFaultInjector,
            laneState,
            laneTelemetry);
        return new Fixture(
            root,
            time,
            telemetry,
            ingress,
            policy,
            hostOptions,
            laneState,
            laneTelemetry,
            configuration ?? CreateConfiguration());
    }

    private static async Task<CadenceScenario> RunCadenceScenarioAsync(
        CaptureCadenceMode cadenceMode,
        string? blockedLane)
    {
        const int captureCount = 4;
        var interval = TimeSpan.FromSeconds(1);
        var acquisitionDuration = TimeSpan.FromMilliseconds(100);
        var configuration = CreateCadenceConfiguration(interval, cadenceMode);
        using var fixture = CreateFixture(
            new CaptureDistributionOptions
            {
                UploadEnabled = true,
                ShutdownDrainSeconds = 1,
                RequiredMaximumPendingCount = 16,
                OptionalMaximumPendingCount = 16,
                SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
            },
            configuration: configuration);
        using var standard = new StandardCaptureLaneHandler(
            new EmptyPipelineFactory(),
            NullLogger<StandardCaptureLaneHandler>.Instance,
            fixture.Ingress);
        var gate = blockedLane is null ? null : new GatedLaneHandler(blockedLane);
        ICaptureLaneHandler upload = blockedLane == "upload" ? gate! : new SuccessfulLaneHandler("upload");
        ICaptureLaneHandler secondary = blockedLane == "secondary" ? gate! : new SuccessfulLaneHandler("secondary");
        using var service = new CaptureDistributionService(
            new ConfigurationAccessor(configuration),
            fixture.Ingress,
            fixture.Store,
            fixture.Policy,
            [standard, upload, secondary],
            standard,
            fixture.Options,
            fixture.Time,
            new NullCaptureLaneFaultInjector(),
            fixture.LaneTelemetry,
            fixture.LaneState,
            NullLogger<CaptureDistributionService>.Instance);
        using var cancellation = new CancellationTokenSource();
        var recordingIngress = new RecordingRawCaptureIngress(fixture.Ingress, cancellation, captureCount);
        var hostContext = new CaptureHostContext(configuration, recordingIngress, service);
        var runnerTime = new DeterministicTimeProvider(fixture.Time.GetUtcNow());
        var module = new ScriptedFrameCameraModule(runnerTime, fixture.Time, acquisitionDuration);
        var runner = new CameraModuleRunner(module, hostContext, runnerTime, NullLogger.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        var run = runner.RunAsync(cancellation.Token);
        var firstReceipt = recordingIngress.WaitForCountAsync(1);
        if (await Task.WhenAny(firstReceipt, run).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) == run)
        {
            await run.ConfigureAwait(false);
        }
        await firstReceipt.ConfigureAwait(false);
        if (gate is not null)
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        if (cadenceMode == CaptureCadenceMode.MinimumStartInterval)
        {
            for (var count = 1; count < captureCount; count++)
            {
                await runnerTime.WaitForTimerCountAsync(count).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                fixture.Time.Advance(interval - acquisitionDuration);
                runnerTime.Advance(interval - acquisitionDuration);
                var nextReceipt = recordingIngress.WaitForCountAsync(count + 1);
                if (await Task.WhenAny(nextReceipt, run).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) == run)
                {
                    await run.ConfigureAwait(false);
                }
                await nextReceipt.ConfigureAwait(false);
            }
        }
        await recordingIngress.WaitForCountAsync(captureCount).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.HasCount(captureCount, module.Requests);
        Assert.HasCount(captureCount, recordingIngress.Receipts);
        Assert.IsTrue(gate is null || !gate.Released.Task.IsCompleted);
        foreach (var receipt in recordingIngress.Receipts)
        {
            Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
            Assert.IsNotNull(receipt.Manifest.Descriptor.CycleEvidence);
            Assert.IsTrue(File.Exists(receipt.StoredFrame.AbsolutePath));
            Assert.AreEqual(16 * 16 * 2, new FileInfo(receipt.StoredFrame.AbsolutePath).Length);
            Assert.IsTrue(File.Exists(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")));
        }

        using (var connection = await OpenAsync(fixture.Root).ConfigureAwait(false))
        {
            Assert.AreEqual(captureCount, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(captureCount, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_contexts;").ConfigureAwait(false));
            Assert.AreEqual(captureCount * 3L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM raw_captures r WHERE (SELECT COUNT(*) FROM capture_lane_work w WHERE w.raw_capture_row_id = r.raw_capture_row_id AND w.required = 1) != 2;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM raw_captures r WHERE (SELECT COUNT(*) FROM capture_lane_work w WHERE w.raw_capture_row_id = r.raw_capture_row_id AND w.required = 0) != 1;").ConfigureAwait(false));
        }

        if (gate is not null)
        {
            await WaitForCountAsync(
                fixture.Root,
                "SELECT COUNT(*) FROM capture_lane_work WHERE state = 'completed';",
                captureCount * 2L).ConfigureAwait(false);
            var backlog = (await fixture.Store.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(item => item.Lane == blockedLane);
            Assert.AreEqual(captureCount, backlog.PendingCount);
            Assert.AreEqual(1L, backlog.LeasedCount);
            Assert.AreEqual(blockedLane == "upload", backlog.Required);
            await WaitForCountAsync(
                fixture.Root,
                "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;",
                blockedLane == "upload" ? captureCount : backlog.LeasedCount).ConfigureAwait(false);
            gate.Release();
        }

        await WaitForCountAsync(
            fixture.Root,
            "SELECT COUNT(*) FROM capture_lane_work WHERE state = 'completed';",
            captureCount * 3L).ConfigureAwait(false);
        Assert.IsTrue((await fixture.Store.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
            .All(static backlog => backlog.PendingCount == 0 && backlog.LeasedCount == 0));
        using (var connection = await OpenAsync(fixture.Root).ConfigureAwait(false))
        {
            Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
        }
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        return new CadenceScenario(
            module.Requests.Select(static request => request.RequestedStartUtc).ToArray(),
            module.ActualStarts.ToArray(),
            recordingIngress.Receipts
                .Select(static receipt => receipt.Manifest.Descriptor.CycleEvidence!.MonotonicStartJitter)
                .ToArray(),
            runnerTime.TimerCreationCount);
    }

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only test-owned constant SQL is passed to this helper.")]
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only test-owned constant SQL is passed to this helper.")]
    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only test-owned constant SQL is passed to this helper.")]
    private static async Task<byte[]> ScalarBytesAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task WaitForCountAsync(string root, string sql, long expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            if (await ScalarLongAsync(connection, sql).ConfigureAwait(false) == expected)
            {
                return;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for durable lane count {expected}.");
    }

    private sealed class Fixture : IDisposable
    {
        private bool _ingressDisposed;

        internal Fixture(
            string root,
            MutableTimeProvider time,
            RawIngressTelemetry telemetry,
            RawCaptureIngress ingress,
            CaptureLanePolicy policy,
            IOptions<CameraAgentHostOptions> options,
            CaptureLaneState laneState,
            CaptureLaneTelemetry laneTelemetry,
            CameraModuleConfig configuration)
        {
            Root = root;
            Time = time;
            Telemetry = telemetry;
            Ingress = ingress;
            Store = ingress;
            Policy = policy;
            Options = options;
            LaneState = laneState;
            LaneTelemetry = laneTelemetry;
            Configuration = configuration;
        }

        internal string Root { get; }
        internal MutableTimeProvider Time { get; }
        internal RawIngressTelemetry Telemetry { get; }
        internal RawCaptureIngress Ingress { get; }
        internal RawCaptureIngress Store { get; }
        internal CaptureLanePolicy Policy { get; }
        internal IOptions<CameraAgentHostOptions> Options { get; }
        internal CaptureLaneState LaneState { get; }
        internal CaptureLaneTelemetry LaneTelemetry { get; }
        internal CameraModuleConfig Configuration { get; }

        internal async Task<RawCaptureReceipt> AcceptAsync(int index)
        {
            await Store.EnsureCanAcceptAsync(4, CancellationToken.None).ConfigureAwait(false);
            return (await Ingress.AcceptAsync(
                Configuration, CreateSubmission(index), CancellationToken.None).ConfigureAwait(false))!;
        }

        internal CaptureLoopSubmission CreateSubmission(int index, bool includeCycleEvidence = false)
        {
            var timestamp = Time.GetUtcNow().AddMinutes(-1).AddSeconds(index);
            var frame = new CameraFrame(
                timestamp,
                2,
                1,
                CameraPixelFormat.Mono16,
                new byte[] { 1, 2, (byte)index, 4 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN),
                4);
            var submission = new CaptureLoopSubmission(
                new CaptureRequest(timestamp, TimeSpan.FromSeconds(5), CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null)),
                new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null), TimeSpan.Zero, CaptureMode.Still, false)
                {
                    AcquisitionTiming = new CaptureAcquisitionTiming(timestamp, timestamp.AddSeconds(1), timestamp.AddSeconds(1))
                },
                timestamp,
                TimeSpan.FromSeconds(5),
                TimeSpan.Zero);
            if (!includeCycleEvidence)
            {
                return submission;
            }
            var decisionStartedUtc = timestamp.AddMilliseconds(1100);
            return submission with
            {
                CycleEvidence = new CaptureCycleEvidence(
                    CaptureCadenceMode.MinimumStartInterval,
                    CaptureStartReason.DeadlineReached,
                    AutomaticControlOwnership.Disabled,
                    AutomaticControlOwnership.Disabled,
                    null,
                    timestamp,
                    TimeSpan.FromSeconds(5),
                    null,
                    new CaptureControlDecisionEvidence(
                        decisionStartedUtc,
                        decisionStartedUtc.AddMilliseconds(100),
                        TimeSpan.FromSeconds(1),
                        1,
                        TimeSpan.FromSeconds(1),
                        1,
                        CaptureControlDecisionReason.Disabled),
                    decisionStartedUtc.AddMilliseconds(200))
            };
        }

        internal async Task<CaptureLaneLease?> ClaimAsync(string lane, string owner = "test-owner")
        {
            await Ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            return await Store.ClaimAsync(
                Policy.Definitions.Single(definition => definition.Name == lane),
                owner,
                Configuration,
                CancellationToken.None).ConfigureAwait(false);
        }

        internal SqliteCaptureLaneStore RestartLaneStore()
        {
            Ingress.Dispose();
            _ingressDisposed = true;
            return new SqliteCaptureLaneStore(
                Root,
                1,
                Options.Value.CaptureDistribution,
                Policy,
                Time,
                new NullCaptureLaneFaultInjector());
        }

        public void Dispose()
        {
            if (!_ingressDisposed)
            {
                Ingress.Dispose();
            }
            Telemetry.Dispose();
            LaneTelemetry.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static CameraModuleConfig CreateConfiguration()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            AgentId: "agent-lanes");

    private static CameraModuleConfig CreateCadenceConfiguration(
        TimeSpan interval,
        CaptureCadenceMode cadenceMode)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("DurableCadenceTest"),
            new CameraRigConfig(
                new SensorProfile("DurableCadenceTest", 16, 16, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16, StrideBytes: 32),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    interval,
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100),
                    1,
                    1,
                    CadenceMode: cadenceMode)),
            AgentId: "agent-durable-cadence");

    private sealed record CadenceScenario(
        DateTimeOffset[] RequestedStarts,
        DateTimeOffset[] ActualStarts,
        TimeSpan[] MonotonicJitter,
        int TimerCreationCount);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FixedCapacityProvider : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string root) => new(long.MaxValue, long.MaxValue);
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public IReadOnlyList<ICaptureProcessingStep> CreatePipeline(CameraModuleConfig config) => [];
    }

    private sealed class ConfigurationAccessor(CameraModuleConfig configuration) : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => true;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(configuration);
    }

    private sealed class SuccessfulLaneHandler(string lane) : ICaptureLaneHandler
    {
        public string Lane => lane;

        public ValueTask<CaptureLaneHandlerResult> HandleAsync(
            CaptureLaneHandlerContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(CaptureLaneHandlerResult.Success);
    }

    private sealed class RecordingRawCaptureIngress(
        IRawCaptureIngress inner,
        CancellationTokenSource cancellation,
        int targetCount) : IRawCaptureIngress
    {
        private readonly List<RawCaptureReceipt> _receipts = [];
        private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
        private readonly object _sync = new();

        public IReadOnlyList<RawCaptureReceipt> Receipts
        {
            get
            {
                lock (_sync)
                {
                    return _receipts.ToArray();
                }
            }
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
            => inner.InitializeAsync(cancellationToken);

        public async ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            var receipt = await inner.AcceptAsync(configuration, submission, cancellationToken).ConfigureAwait(false);
            if (receipt is null)
            {
                return null;
            }

            List<TaskCompletionSource> completed;
            var targetReached = false;
            lock (_sync)
            {
                _receipts.Add(receipt);
                targetReached = _receipts.Count == targetCount;
                completed = _waiters
                    .Where(waiter => waiter.Count <= _receipts.Count)
                    .Select(static waiter => waiter.Completion)
                    .ToList();
                _waiters.RemoveAll(waiter => waiter.Count <= _receipts.Count);
            }
            foreach (var completion in completed)
            {
                completion.TrySetResult();
            }
            if (targetReached)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            return receipt;
        }

        public Task WaitForCountAsync(int count)
        {
            lock (_sync)
            {
                if (_receipts.Count >= count)
                {
                    return Task.CompletedTask;
                }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, completion));
                return completion.Task;
            }
        }
    }

    private sealed class ScriptedFrameCameraModule(
        DeterministicTimeProvider timeProvider,
        MutableTimeProvider ingressTimeProvider,
        TimeSpan acquisitionDuration) : ICameraModule
    {
        private readonly List<CaptureRequest> _requests = [];
        private readonly List<DateTimeOffset> _actualStarts = [];

        public IReadOnlyList<CaptureRequest> Requests => _requests;
        public IReadOnlyList<DateTimeOffset> ActualStarts => _actualStarts;
        public string Id => "durable-cadence-test";
        public string DisplayName => "Durable cadence test";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            var index = _requests.Count;
            var startedUtc = timeProvider.GetUtcNow();
            _requests.Add(request);
            _actualStarts.Add(startedUtc);
            var pixels = new byte[16 * 16 * 2];
            for (var offset = 0; offset < pixels.Length; offset += 2)
            {
                var sample = (ushort)(1000 + index * 100 + offset / 2);
                pixels[offset] = (byte)sample;
                pixels[offset + 1] = (byte)(sample >> 8);
            }
            timeProvider.Advance(acquisitionDuration);
            ingressTimeProvider.Advance(acquisitionDuration);
            var completedUtc = timeProvider.GetUtcNow();
            var frame = new CameraFrame(
                startedUtc,
                16,
                16,
                CameraPixelFormat.Mono16,
                pixels,
                new FrameMetadata(acquisitionDuration, 1, 0),
                32);
            return Task.FromResult(new CaptureResult(
                frame,
                request.RequestedSetpoint!,
                TimeSpan.Zero,
                CaptureMode.Still,
                false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(startedUtc, completedUtc, completedUtc)
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeterministicTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<DeterministicTimer> _timers = [];
        private readonly List<(int Count, TaskCompletionSource Completion)> _timerWaiters = [];
        private long _timestamp;
        private long _utcTicks = startUtc.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int TimerCreationCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return new DateTimeOffset(_utcTicks, TimeSpan.Zero);
            }
        }

        public override long GetTimestamp()
        {
            lock (_sync)
            {
                return _timestamp;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            DeterministicTimer timer;
            List<TaskCompletionSource> completed;
            lock (_sync)
            {
                TimerCreationCount++;
                timer = new DeterministicTimer(this, callback, state, Deadline(dueTime), PeriodTicks(period));
                _timers.Add(timer);
                completed = _timerWaiters
                    .Where(waiter => waiter.Count <= TimerCreationCount)
                    .Select(static waiter => waiter.Completion)
                    .ToList();
                _timerWaiters.RemoveAll(waiter => waiter.Count <= TimerCreationCount);
            }
            foreach (var completion in completed)
            {
                completion.TrySetResult();
            }
            return timer;
        }

        public Task WaitForTimerCountAsync(int count)
        {
            lock (_sync)
            {
                if (TimerCreationCount >= count)
                {
                    return Task.CompletedTask;
                }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _timerWaiters.Add((count, completion));
                return completion.Task;
            }
        }

        public void Advance(TimeSpan amount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
            List<DeterministicTimer> due;
            lock (_sync)
            {
                _utcTicks = checked(_utcTicks + amount.Ticks);
                _timestamp = checked(_timestamp + amount.Ticks);
                due = _timers.Where(timer => timer.PrepareToFire(_timestamp)).ToList();
            }
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private long Deadline(TimeSpan dueTime)
            => dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_timestamp + dueTime.Ticks);

        private static long PeriodTicks(TimeSpan period)
            => period == Timeout.InfiniteTimeSpan ? long.MaxValue : period.Ticks;

        private void Change(DeterministicTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                timer.ChangeCore(Deadline(dueTime), PeriodTicks(period));
            }
        }

        private void Remove(DeterministicTimer timer)
        {
            lock (_sync)
            {
                timer.DisposeCore();
                _timers.Remove(timer);
            }
        }

        private sealed class DeterministicTimer(
            DeterministicTimeProvider owner,
            TimerCallback callback,
            object? state,
            long deadline,
            long periodTicks) : ITimer
        {
            private bool _disposed;
            private long _deadline = deadline;
            private long _periodTicks = periodTicks;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Change(this, dueTime, period);
                return !_disposed;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool PrepareToFire(long timestamp)
            {
                if (_disposed || timestamp < _deadline)
                {
                    return false;
                }
                _deadline = _periodTicks == long.MaxValue ? long.MaxValue : checked(timestamp + _periodTicks);
                return true;
            }

            public void Fire() => callback(state);

            public void ChangeCore(long deadline, long periodTicks)
            {
                if (_disposed)
                {
                    return;
                }
                _deadline = deadline;
                _periodTicks = periodTicks;
            }

            public void DisposeCore() => _disposed = true;
        }
    }

    private sealed class CancellationThenSuccessHandler : ICaptureLaneHandler
    {
        private int _attempts;

        public string Lane => "standard";

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<CaptureLaneHandlerResult> HandleAsync(
            CaptureLaneHandlerContext context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) > 1)
            {
                return CaptureLaneHandlerResult.Success;
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The first handler attempt completed without cancellation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class OneShotRenewalFailureStore(ICaptureLaneStore inner) : ICaptureLaneStore
    {
        private int _renewalFailures = 1;

        public ValueTask InitializeLanesAsync(CancellationToken cancellationToken)
            => inner.InitializeLanesAsync(cancellationToken);

        public ValueTask EnsureCanAcceptAsync(long payloadLength, CancellationToken cancellationToken)
            => inner.EnsureCanAcceptAsync(payloadLength, cancellationToken);

        public ValueTask<CaptureLaneLease?> ClaimAsync(
            CaptureLaneDefinition lane,
            string owner,
            CameraModuleConfig fallbackConfiguration,
            CancellationToken cancellationToken)
            => inner.ClaimAsync(lane, owner, fallbackConfiguration, cancellationToken);

        public ValueTask<bool> RenewAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
            => Interlocked.Exchange(ref _renewalFailures, 0) == 1
                ? ValueTask.FromException<bool>(new IOException("Injected renewal failure."))
                : inner.RenewAsync(lease, cancellationToken);

        public ValueTask CompleteAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
            => inner.CompleteAsync(lease, cancellationToken);

        public ValueTask<CaptureLaneHandlerOutcome> FailAsync(
            CaptureLaneLease lease,
            CaptureLaneHandlerResult result,
            CancellationToken cancellationToken)
            => inner.FailAsync(lease, result, cancellationToken);

        public ValueTask ReleaseAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
            => inner.ReleaseAsync(lease, cancellationToken);

        public ValueTask<IReadOnlyList<CaptureLaneBacklog>> ReadBacklogsAsync(CancellationToken cancellationToken)
            => inner.ReadBacklogsAsync(cancellationToken);
    }

    private sealed class FailingClaimStore(ICaptureLaneStore inner) : ICaptureLaneStore
    {
        public TaskCompletionSource ClaimAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeLanesAsync(CancellationToken cancellationToken)
            => inner.InitializeLanesAsync(cancellationToken);

        public ValueTask EnsureCanAcceptAsync(long payloadLength, CancellationToken cancellationToken)
            => inner.EnsureCanAcceptAsync(payloadLength, cancellationToken);

        public ValueTask<CaptureLaneLease?> ClaimAsync(
            CaptureLaneDefinition lane,
            string owner,
            CameraModuleConfig fallbackConfiguration,
            CancellationToken cancellationToken)
        {
            ClaimAttempted.TrySetResult();
            return ValueTask.FromException<CaptureLaneLease?>(new IOException("Injected claim failure."));
        }

        public ValueTask<bool> RenewAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
            => inner.RenewAsync(lease, cancellationToken);

        public ValueTask CompleteAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
            => inner.CompleteAsync(lease, cancellationToken);

        public ValueTask<CaptureLaneHandlerOutcome> FailAsync(
            CaptureLaneLease lease,
            CaptureLaneHandlerResult result,
            CancellationToken cancellationToken)
            => inner.FailAsync(lease, result, cancellationToken);

        public ValueTask ReleaseAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
            => inner.ReleaseAsync(lease, cancellationToken);

        public ValueTask<IReadOnlyList<CaptureLaneBacklog>> ReadBacklogsAsync(CancellationToken cancellationToken)
            => inner.ReadBacklogsAsync(cancellationToken);
    }

    private sealed class GatedLaneHandler(string lane) : ICaptureLaneHandler
    {
        public string Lane => lane;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => Released.TrySetResult();

        public async ValueTask<CaptureLaneHandlerResult> HandleAsync(
            CaptureLaneHandlerContext context,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CaptureLaneHandlerResult.Success;
        }
    }

    private sealed class ThrowingFaultInjector(RawIngressFaultPoint point) : IRawIngressFaultInjector
    {
        private int _armed = 1;

        public void Inject(RawIngressFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new InvalidOperationException("Injected migration interruption.");
            }
        }
    }

    private sealed class OneShotLaneFaultInjector(CaptureLaneFaultPoint point) : ICaptureLaneFaultInjector
    {
        private int _armed = 1;

        public void Inject(CaptureLaneFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new InvalidOperationException("Injected capture lane fault.");
            }
        }
    }

    private static ArtifactManifestV2 CreateV1MigrationManifest(
        long sequence,
        string captureId,
        string artifactId,
        string relativePath)
    {
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono8,
            2,
            2,
            2,
            [1, 2, 3, 4]);
        return manifest with
        {
            RelativeArtifactPath = relativePath,
            Descriptor = manifest.Descriptor with
            {
                Capture = manifest.Descriptor.Capture with
                {
                    AgentId = "agent",
                    CaptureSequence = sequence,
                    CaptureId = Guid.Parse(captureId)
                },
                Artifact = manifest.Descriptor.Artifact with { ArtifactId = Guid.Parse(artifactId) }
            }
        };
    }

    private const string V1SchemaWithCaptureSql = """
        PRAGMA user_version = 1;
        CREATE TABLE raw_capture_sequences (
            agent_id TEXT PRIMARY KEY,
            last_sequence INTEGER NOT NULL CHECK (last_sequence >= 0)
        ) STRICT;
        CREATE TABLE raw_capture_assignments (
            capture_id TEXT PRIMARY KEY,
            raw_artifact_id TEXT NOT NULL UNIQUE,
            agent_id TEXT NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            UNIQUE (agent_id, capture_sequence)
        ) STRICT;
        CREATE TABLE raw_captures (
            raw_capture_row_id INTEGER PRIMARY KEY,
            capture_id TEXT NOT NULL UNIQUE,
            raw_artifact_id TEXT NOT NULL UNIQUE,
            agent_id TEXT NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
            descriptor_sha256 TEXT NOT NULL UNIQUE CHECK (length(descriptor_sha256) = 64),
            manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            payload_length INTEGER NOT NULL CHECK (payload_length >= 0),
            payload_relative_path TEXT NOT NULL UNIQUE,
            sidecar_relative_path TEXT NOT NULL UNIQUE,
            manifest_json BLOB NOT NULL,
            exposure_started_unix_ms INTEGER NOT NULL,
            durable_ingress_unix_ms INTEGER NOT NULL,
            committed_unix_ms INTEGER NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('committed', 'missing_evidence', 'quarantined')),
            retention_hold INTEGER NOT NULL DEFAULT 1 CHECK (retention_hold IN (0, 1)),
            failure_reason TEXT,
            UNIQUE (agent_id, capture_sequence),
            FOREIGN KEY (capture_id) REFERENCES raw_capture_assignments(capture_id)
        ) STRICT;
        CREATE INDEX ix_raw_captures_discovery ON raw_captures(state, agent_id, capture_sequence);
        CREATE INDEX ix_raw_captures_backlog ON raw_captures(state, durable_ingress_unix_ms);
        CREATE INDEX ix_raw_captures_retention ON raw_captures(retention_hold, exposure_started_unix_ms);
        CREATE TABLE raw_ingress_reconciliation (
            reconciliation_id INTEGER PRIMARY KEY,
            evidence_key TEXT NOT NULL UNIQUE,
            source_relative_path TEXT NOT NULL,
            companion_relative_path TEXT,
            quarantine_relative_path TEXT,
            outcome TEXT NOT NULL CHECK (outcome IN ('cleaned', 'quarantined')),
            reason TEXT NOT NULL,
            operation_state TEXT NOT NULL CHECK (operation_state IN ('planned', 'completed')),
            observed_bytes INTEGER NOT NULL DEFAULT 0,
            observed_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER
        ) STRICT;
        INSERT INTO raw_capture_sequences(agent_id, last_sequence) VALUES ('agent', 2);
        INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
        VALUES ('11111111111111111111111111111111', '22222222222222222222222222222222', 'agent', 1);
        INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
        VALUES ('33333333333333333333333333333333', '44444444444444444444444444444444', 'agent', 2);
        INSERT INTO raw_captures(
            capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
            manifest_sha256, payload_sha256, payload_length, payload_relative_path,
            sidecar_relative_path, manifest_json, exposure_started_unix_ms,
            durable_ingress_unix_ms, committed_unix_ms, state, retention_hold)
        VALUES (
            '11111111111111111111111111111111', '22222222222222222222222222222222', 'agent', 1,
            'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
            'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB',
            'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC',
            4, 'frames/raw.bin', 'frames/raw.json', X'7B7D', 1, 2, 3, 'committed', 1);
        INSERT INTO raw_captures(
            capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
            manifest_sha256, payload_sha256, payload_length, payload_relative_path,
            sidecar_relative_path, manifest_json, exposure_started_unix_ms,
            durable_ingress_unix_ms, committed_unix_ms, state, retention_hold)
        VALUES (
            '33333333333333333333333333333333', '44444444444444444444444444444444', 'agent', 2,
            'DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD',
            'EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE',
            'FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF',
            4, 'frames/released.bin', 'frames/released.json', X'7B7D', 1, 2, 3, 'committed', 0);
        """;
}
