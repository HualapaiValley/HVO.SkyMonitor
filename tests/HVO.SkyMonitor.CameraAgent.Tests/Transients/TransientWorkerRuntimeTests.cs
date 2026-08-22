using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientWorkerRuntimeTests
{
    [TestMethod]
    public void ResolveObservatory_MigratedLegacyContextFailsClosed()
    {
        var runtime = new TransientDetectorRuntime(new InMemoryCelestialCatalog([]));
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var configuration = CreateConfiguration() with
        {
            Observatory = new ObservatoryLocation(0, 0, 0, "UTC"),
            DeploymentLocation = null,
            DeploymentLocationRedacted = true
        };

        var exception = Assert.ThrowsExactly<TransientWorkerExecutionException>(() =>
            runtime.ResolveObservatory(descriptor, configuration));

        Assert.AreEqual("transient-runtime.capture-location-missing", exception.Message);
    }

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
    public async Task Edge_CandidateLimitCompletesFrameWithoutQuarantiningRequiredLane()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-candidate-limit", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(width: 192, height: 192);
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 7, candidateLimitFrame: true).ConfigureAwait(false);
            var stagedBacklog = (await provider.GetRequiredService<ICaptureLaneStore>()
                .ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(static lane => lane.Lane == "transient");
            CollectionAssert.AreEqual(
                new long[] { 5, 6, 7 },
                stagedBacklog.PendingCaptures!.Select(static capture => capture.CaptureSequence).ToArray());
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 7; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }
            var transientBacklog = (await provider.GetRequiredService<ICaptureLaneStore>()
                .ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(static lane => lane.Lane == "transient");
            var pendingCaptures = transientBacklog.PendingCaptures
                ?? throw new AssertFailedException("Transient pending capture identities are required.");
            CollectionAssert.AreEqual(
                new long[] { 6, 7 },
                pendingCaptures.Select(static capture => capture.CaptureSequence).ToArray());
            Assert.IsTrue(pendingCaptures.All(static capture =>
                capture.AgentId == "transient-runtime-agent"));
            var operations = await provider.GetRequiredService<CameraAgentOperationsSummaryProvider>()
                .GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2L, operations.RawIngress.Value.PendingCount);
            Assert.AreEqual(2L, operations.CaptureLanes.Value.PendingCount);
            CollectionAssert.AreEqual(
                new long[] { 6, 7 },
                operations.CaptureLanes.Value.Lanes.Single(static lane => lane.Name == "transient")
                    .PendingCaptures.Select(static capture => capture.CaptureSequence).ToArray());

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            var reasons = await ScalarStringAsync(connection,
                "SELECT COALESCE(group_concat(failure_reason, ','), 'none') FROM transient_worker_frames;")
                .ConfigureAwait(false);
            var candidateCount = await ScalarAsync(connection, "SELECT COUNT(*) FROM transient_candidates;")
                .ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_frames WHERE failure_reason = 'transient-extraction.candidate-limit' AND state = 'completed';")
                .ConfigureAwait(false), $"{reasons}; candidates={candidateCount}");
            Assert.AreEqual(0L, candidateCount);
            Assert.AreEqual(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_frames WHERE state = 'quarantined';").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined';").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_capture_work w JOIN transient_worker_frames f USING(raw_capture_row_id) WHERE f.failure_reason = 'transient-extraction.candidate-limit' AND w.state = 'completed';")
                .ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM raw_captures r JOIN transient_worker_frames f USING(raw_capture_row_id) WHERE f.failure_reason = 'transient-extraction.candidate-limit' AND r.retention_hold != 0;")
                .ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Hybrid_InputLevelsInvalid_AuditedAbandonReleasesOnlyExactBlockingHold()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-operator", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root, mode: TransientOperatingMode.Hybrid);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration();
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(
                provider, cameraConfiguration, epoch, frameCount: 5, invalidLevels: true).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));

            var runtime = provider.GetRequiredService<ITransientRuntimeManagement>();
            var store = provider.GetRequiredService<SqliteTransientRuntimeStore>();
            var initial = await runtime.ReadQuarantinePageAsync(10, null, CancellationToken.None).ConfigureAwait(false);
            var blocked = initial.Items.Single();
            Assert.AreEqual(3L, blocked.CaptureSequence);
            Assert.AreEqual("transient-runtime.input-levels-invalid", blocked.FailureReason);
            Assert.AreEqual("completed", blocked.OuterLaneState);
            Assert.AreEqual("quarantined", blocked.WorkState);
            Assert.AreEqual("quarantined", blocked.FrameState);
            Assert.IsTrue(blocked.RetentionHold);

            string payloadPath;
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var payload = connection.CreateCommand())
            {
                payload.CommandText = "SELECT payload_relative_path FROM raw_captures WHERE raw_capture_row_id = $raw;";
                payload.Parameters.AddWithValue("$raw", blocked.RawCaptureRowId);
                payloadPath = Path.Combine(root, Convert.ToString(
                    await payload.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture)!.Replace('/', Path.DirectorySeparatorChar));
            }
            var retainedBytes = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
            var changedBytes = retainedBytes.ToArray();
            changedBytes[0] ^= 0xFF;
            await File.WriteAllBytesAsync(payloadPath, changedBytes).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runtime
                .AbandonQuarantinedCaptureAsync(
                    Target(blocked), "payload-mismatch-key", "owner-id", "operator-approved-loss",
                    CancellationToken.None).AsTask()).ConfigureAwait(false);
            await File.WriteAllBytesAsync(payloadPath, retainedBytes).ConfigureAwait(false);

            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var activeLease = connection.CreateCommand())
            {
                activeLease.CommandText = """
                    UPDATE capture_lane_work
                    SET state = 'leased', lease_token = 'target-token', lease_owner = 'target-worker',
                        lease_expires_unix_ms = 9999999999999
                    WHERE work_id = $work;
                    """;
                activeLease.Parameters.AddWithValue("$work", blocked.OuterLaneWorkId);
                Assert.AreEqual(1, await activeLease.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runtime
                .AbandonQuarantinedCaptureAsync(
                    Target(blocked), "active-lease-key", "owner-id", "operator-approved-loss",
                    CancellationToken.None).AsTask()).ConfigureAwait(false);
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var restore = connection.CreateCommand())
            {
                restore.CommandText = """
                    UPDATE capture_lane_work
                    SET state = 'completed', lease_token = NULL, lease_owner = NULL,
                        lease_expires_unix_ms = NULL, updated_unix_ms = $updated
                    WHERE work_id = $work;
                    """;
                restore.Parameters.AddWithValue("$updated", blocked.OuterLaneUpdatedUtc.ToUnixTimeMilliseconds());
                restore.Parameters.AddWithValue("$work", blocked.OuterLaneWorkId);
                Assert.AreEqual(1, await restore.ExecuteNonQueryAsync().ConfigureAwait(false));
            }

            var unrelatedFrame = await store.ReadFrameAsync(
                (await store.ReadNextAsync(CancellationToken.None).ConfigureAwait(false))!.RawCaptureRowId,
                CancellationToken.None).ConfigureAwait(false);
            await store.QuarantineAsync(
                unrelatedFrame.RawCaptureRowId, "unrelated-quarantine", CancellationToken.None).ConfigureAwait(false);
            var firstPage = await runtime.ReadQuarantinePageAsync(1, null, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, firstPage.Items);
            Assert.IsNotNull(firstPage.NextCursor);
            var secondPage = await runtime.ReadQuarantinePageAsync(
                1, firstPage.NextCursor, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, secondPage.Items);
            Assert.AreNotEqual(firstPage.Items[0].RawCaptureRowId, secondPage.Items[0].RawCaptureRowId);
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var lease = connection.CreateCommand())
            {
                lease.CommandText = """
                    UPDATE capture_lane_work
                    SET state = 'leased', lease_token = 'unrelated-token', lease_owner = 'unrelated-worker',
                        lease_expires_unix_ms = 9999999999999
                    WHERE lane_name = 'transient' AND capture_sequence = 5;
                    """;
                Assert.AreEqual(1, await lease.ExecuteNonQueryAsync().ConfigureAwait(false));
            }

            var target = Target(blocked);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime
                .AbandonQuarantinedCaptureAsync(
                    target with { ExternalOwnershipEvidence = null }, "missing-ownership-key", "owner-id",
                    "operator-approved-loss", CancellationToken.None).AsTask()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime
                .AbandonQuarantinedCaptureAsync(
                    target with { ExternalOwnershipEvidence = new("d331-0821084607", "not-a-sha", true) },
                    "malformed-ownership-key", "owner-id", "operator-approved-loss",
                    CancellationToken.None).AsTask()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<OutboxOperationCollisionException>(() => runtime
                .AbandonQuarantinedCaptureAsync(
                    target with { CaptureSequence = 99 }, "mismatch-key", "owner-id",
                    "operator-approved-loss", CancellationToken.None).AsTask()).ConfigureAwait(false);

            var receipts = await Task.WhenAll(
                runtime.AbandonQuarantinedCaptureAsync(
                    target, "runtime-abandon-key", "owner-id", "operator-approved-loss", CancellationToken.None).AsTask(),
                runtime.AbandonQuarantinedCaptureAsync(
                    target, "runtime-abandon-key", "owner-id", "operator-approved-loss", CancellationToken.None).AsTask())
                .ConfigureAwait(false);
            CollectionAssert.AreEquivalent(
                new[] { TransientRuntimeOperationDisposition.Applied, TransientRuntimeOperationDisposition.Duplicate },
                receipts.Select(static receipt => receipt.Disposition).ToArray());
            Assert.AreEqual(receipts[0].CompletedUtc, receipts[1].CompletedUtc);
            Assert.AreEqual("d331-0821084607", receipts[0].DeploymentRunId);
            Assert.AreEqual(new string('D', 64), receipts[0].InventorySha256);
            await Assert.ThrowsExactlyAsync<OutboxOperationCollisionException>(() => runtime
                .AbandonQuarantinedCaptureAsync(
                    target with
                    {
                        ExternalOwnershipEvidence = new("d331-0821084608", new string('E', 64), true)
                    },
                    "runtime-abandon-key", "owner-id", "operator-approved-loss",
                    CancellationToken.None).AsTask()).ConfigureAwait(false);

            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(verify, """
                SELECT COUNT(*) FROM transient_runtime_operations
                WHERE idempotency_key = 'runtime-abandon-key' AND actor = 'owner-id'
                  AND reason_code = 'operator-approved-loss' AND expected_failure_reason = 'transient-runtime.input-levels-invalid'
                  AND deployment_run_id = 'd331-0821084607' AND inventory_sha256 = 'DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD'
                  AND legacy_ownership_externally_established = 1;
                """).ConfigureAwait(false));
            Assert.AreEqual("abandoned", await ScalarStringAsync(verify,
                "SELECT state FROM transient_capture_work WHERE raw_capture_row_id = " + blocked.RawCaptureRowId + ";").ConfigureAwait(false));
            Assert.AreEqual("abandoned", await ScalarStringAsync(verify,
                "SELECT state FROM transient_worker_frames WHERE raw_capture_row_id = " + blocked.RawCaptureRowId + ";").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(verify,
                "SELECT retention_hold FROM raw_captures WHERE raw_capture_row_id = " + blocked.RawCaptureRowId + ";").ConfigureAwait(false));
            Assert.AreEqual("quarantined", await ScalarStringAsync(verify,
                "SELECT state FROM transient_capture_work WHERE raw_capture_row_id = " + unrelatedFrame.RawCaptureRowId + ";").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(verify,
                "SELECT retention_hold FROM raw_captures WHERE raw_capture_row_id = " + unrelatedFrame.RawCaptureRowId + ";").ConfigureAwait(false));
            Assert.AreEqual("leased", await ScalarStringAsync(verify,
                "SELECT state FROM capture_lane_work WHERE lane_name = 'transient' AND capture_sequence = 5;").ConfigureAwait(false));
            using (var removeAudit = verify.CreateCommand())
            {
                removeAudit.CommandText = "DELETE FROM transient_runtime_operations WHERE idempotency_key = 'runtime-abandon-key';";
                Assert.AreEqual(1, await removeAudit.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            var unsafeBacklog = (await provider.GetRequiredService<ICaptureLaneStore>()
                .ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(static lane => lane.Lane == "transient");
            Assert.IsGreaterThanOrEqualTo(2L, unsafeBacklog.QuarantineCount);
            using var holdVerify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(holdVerify,
                "SELECT retention_hold FROM raw_captures WHERE raw_capture_row_id = " + blocked.RawCaptureRowId + ";").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Column names come only from the fixed malformed-audit test matrix; values are parameterized.")]
    public async Task MalformedAbandonmentAudit_RestoresHoldAndProjectsQuarantine()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-malformed-audit", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root, mode: TransientOperatingMode.Hybrid);
            var cameraConfiguration = CreateConfiguration();
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(
                provider, cameraConfiguration, new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero),
                frameCount: 5, invalidLevels: true).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            var runtime = provider.GetRequiredService<ITransientRuntimeManagement>();
            var blocked = (await runtime.ReadQuarantinePageAsync(10, null, CancellationToken.None).ConfigureAwait(false))
                .Items.Single();
            var target = Target(blocked);
            await runtime.AbandonQuarantinedCaptureAsync(
                target, "malformed-audit-key", "owner-id", "operator-approved-loss", CancellationToken.None)
                .ConfigureAwait(false);
            string receiptIdentity;
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            {
                receiptIdentity = await ScalarStringAsync(
                    connection,
                    "SELECT receipt_identity_sha256 FROM transient_runtime_operations WHERE idempotency_key = 'malformed-audit-key';")
                    .ConfigureAwait(false);
            }

            var evidence = target.ExternalOwnershipEvidence!;
            (string Column, object Bad, object Good)[] mutations =
            [
                ("outer_lane_work_id", target.OuterLaneWorkId + 1000, target.OuterLaneWorkId),
                ("agent_id", "wrong-agent", target.AgentId),
                ("capture_id", Guid.NewGuid().ToString("N"), target.CaptureId.ToString("N")),
                ("artifact_id", Guid.NewGuid().ToString("N"), target.ArtifactId.ToString("N")),
                ("manifest_sha256", new string('E', 64), target.ManifestSha256),
                ("processing_profile_sha256", new string('E', 64), target.ProcessingProfileSha256),
                ("expected_outer_lane_state", "quarantined", target.ExpectedOuterLaneState),
                ("expected_work_state", "completed", target.ExpectedWorkState),
                ("action", "replay", "abandon"),
                ("deployment_run_id", "wrong-run", evidence.DeploymentRunId),
                ("inventory_sha256", new string('E', 64), evidence.InventorySha256),
                ("legacy_ownership_externally_established", 0, 1),
                ("actor", "wrong-owner", "owner-id"),
                ("reason_code", "configuration-corrected", "operator-approved-loss"),
                ("receipt_identity_sha256", new string('E', 64), receiptIdentity)
            ];

            foreach (var mutation in mutations)
            {
                using (var connection = await OpenAsync(root).ConfigureAwait(false))
                using (var corrupt = connection.CreateCommand())
                {
                    corrupt.CommandText = $"""
                        PRAGMA foreign_keys = OFF;
                        PRAGMA ignore_check_constraints = ON;
                        UPDATE transient_runtime_operations SET {mutation.Column} = $value
                        WHERE idempotency_key = 'malformed-audit-key';
                        UPDATE raw_captures SET retention_hold = 0 WHERE raw_capture_row_id = $raw;
                        """;
                    corrupt.Parameters.AddWithValue("$value", mutation.Bad);
                    corrupt.Parameters.AddWithValue("$raw", target.RawCaptureRowId);
                    await corrupt.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                var backlog = (await provider.GetRequiredService<ICaptureLaneStore>()
                    .ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                    .Single(static lane => lane.Lane == "transient");
                Assert.IsGreaterThanOrEqualTo(1L, backlog.QuarantineCount, mutation.Column);
                await Assert.ThrowsExactlyAsync<OutboxOperationCollisionException>(() => runtime
                    .AbandonQuarantinedCaptureAsync(
                        target, "malformed-audit-key", "owner-id", "operator-approved-loss",
                        CancellationToken.None).AsTask()).ConfigureAwait(false);
                using (var verify = await OpenAsync(root).ConfigureAwait(false))
                {
                    Assert.AreEqual(1L, await ScalarAsync(verify,
                        $"SELECT retention_hold FROM raw_captures WHERE raw_capture_row_id = {target.RawCaptureRowId};")
                        .ConfigureAwait(false), mutation.Column);
                    using var restore = verify.CreateCommand();
                    restore.CommandText = $"""
                        PRAGMA foreign_keys = OFF;
                        PRAGMA ignore_check_constraints = ON;
                        UPDATE transient_runtime_operations SET {mutation.Column} = $value
                        WHERE idempotency_key = 'malformed-audit-key';
                        """;
                    restore.Parameters.AddWithValue("$value", mutation.Good);
                    await restore.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task RawIngressV10_MigratesRuntimeOperationAuditSchemaToV11()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-v11-migration", Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = CreateProvider(root))
            {
                await provider.GetRequiredService<IRawCaptureIngress>()
                    .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var downgrade = connection.CreateCommand())
            {
                downgrade.CommandText = """
                    DROP INDEX ix_transient_runtime_operations_target;
                    DROP TABLE transient_runtime_operations;
                    PRAGMA user_version = 10;
                    """;
                await downgrade.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>()
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(11L, await ScalarAsync(verify, "PRAGMA user_version;").ConfigureAwait(false));
            Assert.AreEqual(30L, await ScalarAsync(verify,
                "SELECT COUNT(*) FROM pragma_table_info('transient_runtime_operations');").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_transient_runtime_operations_target';").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("index")]
    [DataRow("unique")]
    [DataRow("check")]
    [DataRow("strict")]
    [DataRow("foreign-key")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The test mutates the locally generated canonical schema with fixed replacements to exercise drift detection.")]
    public async Task CurrentV11RuntimeOperationSchemaDrift_FailsClosed(string drift)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-schema", Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = CreateProvider(root))
            {
                await provider.GetRequiredService<IRawCaptureIngress>()
                    .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                if (drift is "unique" or "check" or "strict" or "foreign-key")
                {
                    using var schema = connection.CreateCommand();
                    schema.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'transient_runtime_operations';";
                    var createSql = Convert.ToString(
                        await schema.ExecuteScalarAsync().ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture)!;
                    createSql = drift switch
                    {
                        "unique" => createSql.Replace(
                            "idempotency_key TEXT NOT NULL UNIQUE",
                            "idempotency_key TEXT NOT NULL",
                            StringComparison.Ordinal),
                        "check" => createSql.Replace(
                            "CHECK (action = 'abandon')",
                            "CHECK (action IN ('abandon', 'replay'))",
                            StringComparison.Ordinal),
                        "strict" => createSql.Replace(") STRICT", ")", StringComparison.Ordinal),
                        _ => createSql.Replace(
                            "FOREIGN KEY (outer_lane_work_id) REFERENCES capture_lane_work(work_id)",
                            "CHECK (outer_lane_work_id > 0)",
                            StringComparison.Ordinal)
                    };
                    command.CommandText = $"DROP INDEX ix_transient_runtime_operations_target; DROP TABLE transient_runtime_operations; {createSql}; CREATE INDEX ix_transient_runtime_operations_target ON transient_runtime_operations(raw_capture_row_id, operation_id DESC);";
                }
                else
                {
                    command.CommandText = drift == "table"
                        ? "DROP INDEX ix_transient_runtime_operations_target; DROP TABLE transient_runtime_operations;"
                        : "DROP INDEX ix_transient_runtime_operations_target; CREATE INDEX ix_transient_runtime_operations_target ON transient_runtime_operations(operation_id);";
                }
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using var restarted = CreateProvider(root);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted
                .GetRequiredService<IRawCaptureIngress>()
                .InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Hybrid_BacklogProjectsActiveCentersAcrossDurableLayers()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-active-centers", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration();
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 6).ConfigureAwait(false);

            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                var candidateId = Guid.NewGuid().ToString("N");
                var eventId = Guid.NewGuid().ToString("N");
                command.CommandText = """
                    UPDATE transient_capture_work
                    SET state = 'completed'
                    WHERE raw_capture_row_id IN (
                        SELECT raw_capture_row_id FROM raw_captures WHERE capture_sequence < 6);
                    UPDATE transient_capture_work
                    SET state = 'candidate_persisted'
                    WHERE raw_capture_row_id IN (
                        SELECT raw_capture_row_id FROM raw_captures WHERE capture_sequence = 6);
                    UPDATE capture_lane_work
                    SET state = 'pending'
                    WHERE lane_name = 'transient' AND capture_sequence = 5;
                    INSERT INTO transient_event_identities(event_id, agent_id, created_unix_ms)
                    VALUES ($event, 'transient-runtime-agent', 1);
                    INSERT INTO transient_candidates(
                        candidate_id, event_id, agent_id, mode, required, reservation_identity_sha256,
                        state, phase, source_hold_released, timeout_unix_ms, created_unix_ms, updated_unix_ms)
                    VALUES ($candidate, $event, 'transient-runtime-agent', 'hybrid', 1, $identity,
                        'pending', 'handoff_pending', 0, 2, 1, 1);
                    INSERT INTO transient_candidate_sources(
                        candidate_id, source_ordinal, evidence_id, raw_capture_row_id,
                        source_schema, locator_schema, locator_kind, artifact_id, artifact_role,
                        artifact_variant, recipe_identity_sha256, checksum_sha256,
                        observation_started_utc_ticks, observation_ended_utc_ticks,
                        timing_quality, timing_source, timing_version)
                    SELECT $candidate, capture_sequence - 2, 'evidence-' || capture_sequence, raw_capture_row_id,
                           'source-v1', 'locator-v1', 0, raw_artifact_id, 0,
                           'source', $identity, payload_sha256,
                           capture_sequence, capture_sequence, 0, 'test', '1'
                    FROM raw_captures WHERE capture_sequence BETWEEN 2 AND 4;
                    """;
                command.Parameters.AddWithValue("$candidate", candidateId);
                command.Parameters.AddWithValue("$event", eventId);
                command.Parameters.AddWithValue("$identity", new string('A', 64));
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var backlog = (await provider.GetRequiredService<ICaptureLaneStore>()
                .ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(static lane => lane.Lane == "transient");
            var pendingCaptures = backlog.PendingCaptures
                ?? throw new AssertFailedException("Transient pending capture identities are required.");
            Assert.AreEqual(3L, backlog.PendingCount);
            CollectionAssert.AreEqual(
                new long[] { 4, 5, 6 },
                pendingCaptures.Select(static capture => capture.CaptureSequence).ToArray());
            Assert.IsTrue(pendingCaptures.All(static capture =>
                capture.AgentId == "transient-runtime-agent"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public void Edge_OnlySceneContentLimitsCompleteWithoutQuarantine()
    {
        static TransientCandidateExtractionOutcome Outcome(string reason, string field) => new(
            TransientCandidateExtractionStatus.LimitExceeded,
            reason,
            field,
            null,
            [],
            0,
            0,
            0,
            0);

        Assert.IsTrue(TransientWorkerService.IsBoundedSceneLimit(Outcome(
            TransientCandidateExtractionReasonCodes.CandidateLimit,
            "options.maximumCandidates")));
        Assert.IsTrue(TransientWorkerService.IsBoundedSceneLimit(Outcome(
            TransientCandidateExtractionReasonCodes.CandidateLimit,
            "descriptor")));
        Assert.IsTrue(TransientWorkerService.IsBoundedSceneLimit(Outcome(
            TransientCandidateExtractionReasonCodes.ResourceLimit,
            "options.maximumForegroundPixels")));
        Assert.IsFalse(TransientWorkerService.IsBoundedSceneLimit(Outcome(
            TransientCandidateExtractionReasonCodes.ResourceLimit,
            "target.input.descriptor.layout")));
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
    [DataRow(26)]
    public void SqliteDatabaseCorruption_IsUnavailableRatherThanQuarantineEligible(int errorCode)
    {
        var exception = new SqliteException("test", errorCode, errorCode);

        Assert.IsTrue(TransientWorkerService.IsDatabaseCorruption(exception));
        Assert.IsFalse(TransientWorkerService.IsIntegrityFailure(exception));
        Assert.IsFalse(TransientWorkerService.IsRetryableStorageFailure(exception));
    }

    [TestMethod]
    [DataRow(19)]
    [DataRow(20)]
    [DataRow(24)]
    public void SqliteMalformedRecordFailures_AreQuarantineEligible(int errorCode)
    {
        var exception = new SqliteException("test", errorCode, errorCode);

        Assert.IsTrue(TransientWorkerService.IsIntegrityFailure(exception));
        Assert.IsFalse(TransientWorkerService.IsDatabaseCorruption(exception));
        Assert.IsFalse(TransientWorkerService.IsRetryableStorageFailure(exception));
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(15)]
    public async Task Edge_SqliteStorageFailureReportsUnhealthyWithoutQuarantiningWork(int errorCode)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-sqlite-full", Guid.NewGuid().ToString("N"));
        try
        {
            var faultInjector = new SqliteRuntimeFaultInjector(
                TransientRuntimeFaultPoint.BeforeIdentityBatchCommit, errorCode);
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
    public async Task HostedWorker_PersistentStorageFailureRemainsUnhealthyAcrossPollsUntilSuccessfulRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-persistent-storage", Guid.NewGuid().ToString("N"));
        try
        {
            var injector = new BlockingSqliteRuntimeFaultInjector(
                TransientRuntimeFaultPoint.BeforeIdentityBatchCommit,
                errorCode: 6);
            using var provider = CreateProvider(root, faultInjector: injector);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(CreateOneFrameScenario(epoch));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 7).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(
                () => injector.CompletedAttempts >= 1 &&
                    provider.GetRequiredService<TransientWorkerState>().Snapshot.Availability ==
                    TransientWorkerAvailability.Unhealthy,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            for (var poll = 0; poll < 3; poll++)
            {
                var expectedAttempt = injector.CompletedAttempts + 1;
                await WaitUntilAsync(
                    () => injector.Attempts >= expectedAttempt,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(
                    TransientWorkerAvailability.Unhealthy,
                    provider.GetRequiredService<TransientWorkerState>().Snapshot.Availability);
                var health = await new TransientWorkerHealthCheck(provider.GetRequiredService<TransientWorkerState>())
                    .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
                Assert.AreEqual(HealthStatus.Unhealthy, health.Status);
                injector.ReleaseBlockedAttempt();
                await WaitUntilAsync(
                    () => injector.CompletedAttempts >= expectedAttempt,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                injector.BlockNextAttempt();
            }

            injector.Disable();
            injector.ReleaseBlockedAttempt();
            await WaitUntilAsync(
                () => provider.GetRequiredService<TransientWorkerState>().Snapshot.Availability ==
                    TransientWorkerAvailability.Healthy,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var recoveredHealth = await new TransientWorkerHealthCheck(provider.GetRequiredService<TransientWorkerState>())
                .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
            Assert.AreEqual(HealthStatus.Healthy, recoveredHealth.Status);
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Edge_MalformedPersistedEvidenceQuarantinesOnlyAffectedWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-malformed-evidence", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cameraConfiguration = CreateConfiguration(CreateOneFrameScenario(epoch));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 7).ConfigureAwait(false);
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE raw_captures SET manifest_json = X'00' WHERE capture_sequence = 5;";
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 4; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }

            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));

            using var verified = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(
                verified, "SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined';").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(
                verified, "SELECT COUNT(*) FROM transient_worker_frames WHERE state = 'quarantined';").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(
                verified, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Edge_ActualSqliteCorruptionIsUnhealthyAndRestoredDatabaseRecovers()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-database-corruption", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
        var backupPath = Path.Combine(root, "raw-ingress.backup.db");
        var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var cameraConfiguration = CreateConfiguration();
        try
        {
            using (var staged = CreateProvider(root))
            {
                staged.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
                await StageVirtualFramesAsync(staged, cameraConfiguration, epoch).ConfigureAwait(false);
                using var connection = await OpenAsync(root).ConfigureAwait(false);
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            File.Copy(databasePath, backupPath, overwrite: true);
            using (var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(new byte[512]).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            using (var corrupted = CreateProvider(root))
            {
                corrupted.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
                var worker = corrupted.GetRequiredService<TransientWorkerService>();
                await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await WaitUntilAsync(
                    () => corrupted.GetRequiredService<TransientWorkerState>().Snapshot.Availability ==
                        TransientWorkerAvailability.Unhealthy,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(
                    TransientWorkerAvailability.Unhealthy,
                    corrupted.GetRequiredService<TransientWorkerState>().Snapshot.Availability);
                await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            SqliteConnection.ClearAllPools();
            File.Copy(backupPath, databasePath, overwrite: true);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
            using var recovered = CreateProvider(root);
            recovered.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            var recoveredWorker = recovered.GetRequiredService<TransientWorkerService>();
            await recoveredWorker.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(
                () => recovered.GetRequiredService<TransientWorkerState>().Snapshot.Availability ==
                    TransientWorkerAvailability.Healthy,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await recoveredWorker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            using var verified = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual("ok", await ScalarStringAsync(verified, "PRAGMA integrity_check;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(
                verified, "SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined';").ConfigureAwait(false));
            await StageVirtualFramesAsync(
                recovered, cameraConfiguration, epoch.AddHours(1), frameCount: 1).ConfigureAwait(false);
            Assert.IsTrue(await recoveredWorker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
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
    public async Task Edge_StableCloudControlProducesNoCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-cloud-control", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateProvider(root);
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var cloud = new VirtualCloudScenarioDefinition
            {
                ScenarioId = "stable-cloud-control",
                ScenarioVersion = "1",
                Seed = 63,
                EpochUtc = epoch,
                DriftEastCellsPerSecond = 0,
                DriftNorthCellsPerSecond = 0,
                EvolutionCellsPerSecond = 0,
                TemporalSampleCount = 4,
                Keyframes = [new VirtualCloudKeyframe { OffsetSeconds = 0, Coverage = 0.55, MaximumOpacity = 0.55, ScatterFraction = 0.2 }]
            };
            var cameraConfiguration = CreateConfiguration(cloudScenario: cloud);
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch, 7).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 7; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(5L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_worker_frames WHERE causal_succeeded = 1;").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public async Task Edge_ExpiredCenteredDeadlineFinalizesNeedsReviewWithoutConvergence()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-timeout", Guid.NewGuid().ToString("N"));
        try
        {
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var timeProvider = new MutableTimeProvider(epoch.AddHours(1));
            using var provider = CreateProvider(root, timeProvider: timeProvider);
            var cameraConfiguration = CreateConfiguration(CreateOneFrameScenario(epoch, 10));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(cameraConfiguration);
            await StageVirtualFramesAsync(provider, cameraConfiguration, epoch).ConfigureAwait(false);
            var worker = provider.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 5; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }
            Assert.IsFalse(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));
            timeProvider.Advance(TimeSpan.FromMinutes(11));

            Assert.IsTrue(await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false));

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual("needs_review", await ScalarStringAsync(
                connection, "SELECT state FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual("finalized", await ScalarStringAsync(
                connection, "SELECT phase FROM transient_candidates;").ConfigureAwait(false));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    public void AssociationSplitAndMergeRemainSeparateAmbiguousEvidence()
    {
        var start = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var priorEvent = Guid.NewGuid();
        var previous = CreateAssociationCandidate(priorEvent, start, 0);
        var split = TransientWorkerService.ResolveAssociations(
            [previous],
            [
                CreateAssociationCandidate(Guid.NewGuid(), start.AddSeconds(1), 0),
                CreateAssociationCandidate(Guid.NewGuid(), start.AddSeconds(1), 1)
            ],
            new TransientCandidateAssociationOptions());
        Assert.HasCount(2, split);
        Assert.IsTrue(split.All(static decision => decision.Ambiguous && decision.ExistingEventId is null));

        var merge = TransientWorkerService.ResolveAssociations(
            [previous, CreateAssociationCandidate(Guid.NewGuid(), start, 1)],
            [CreateAssociationCandidate(Guid.NewGuid(), start.AddSeconds(1), 0)],
            new TransientCandidateAssociationOptions());
        Assert.HasCount(1, merge);
        Assert.IsTrue(merge[0].Ambiguous);
        Assert.IsNull(merge[0].ExistingEventId);
    }

    [TestMethod]
    public void AssociationTimeGapResetsToNewEventWithoutGuessing()
    {
        var start = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var decision = TransientWorkerService.ResolveAssociations(
            [CreateAssociationCandidate(Guid.NewGuid(), start, 0)],
            [CreateAssociationCandidate(Guid.NewGuid(), start.AddMinutes(2), 0)],
            new TransientCandidateAssociationOptions { MaximumStartIntervalSeconds = 30 }).Single();

        Assert.IsFalse(decision.Ambiguous);
        Assert.IsNull(decision.ExistingEventId);
    }

    [TestMethod]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeIdentityBatchCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterIdentityBatchCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeCausalExtractionCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterCausalExtractionCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterCandidateJournalCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterCandidateJournalCommit, TransientOperatingMode.Hybrid, "handoff_pending")]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeObservationExtractionCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterObservationExtractionCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeAssessmentCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterAssessmentCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterFinalizationJournalCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterHandoffJournalCommit, TransientOperatingMode.Hybrid, "handoff_pending")]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeRuntimeCompletionCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeRuntimeCompletionCommit, TransientOperatingMode.Hybrid, "handoff_pending")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterRuntimeCompletionCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterRuntimeCompletionCommit, TransientOperatingMode.Hybrid, "handoff_pending")]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeRetirementCommit, TransientOperatingMode.Edge, "finalized")]
    [DataRow((int)TransientRuntimeFaultPoint.AfterRetirementCommit, TransientOperatingMode.Edge, "finalized")]
    public async Task HostedWorker_DurableRuntimeFaultMatrix_RestartConvergesExactlyOnce(
        int faultPoint,
        TransientOperatingMode mode,
        string expectedPhase)
    {
        var point = (TransientRuntimeFaultPoint)faultPoint;
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-fault-matrix", Guid.NewGuid().ToString("N"));
        try
        {
            var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
            var configuration = CreateConfiguration(CreateOneFrameScenario(epoch));
            var injector = new ThrowingRuntimeFaultInjector(point);
            string interruptedIds;
            using (var interrupted = CreateProvider(root, faultInjector: injector, mode: mode))
            {
                interrupted.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
                await StageVirtualFramesAsync(interrupted, configuration, epoch, 7).ConfigureAwait(false);
                var worker = interrupted.GetRequiredService<TransientWorkerService>();
                var observed = false;
                for (var iteration = 0; iteration < 32 && !observed; iteration++)
                {
                    try
                    {
                        if (!await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false))
                        {
                            _ = await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (InvalidOperationException exception) when (
                        string.Equals(exception.Message, "Injected transient runtime fault.", StringComparison.Ordinal))
                    {
                        observed = true;
                    }
                }
                Assert.IsTrue(observed);
                Assert.IsTrue(injector.WasInjected);
                using var interruptedConnection = await OpenAsync(root).ConfigureAwait(false);
                interruptedIds = await RuntimeIdentityTupleAsync(interruptedConnection).ConfigureAwait(false);
                Assert.IsGreaterThan(0L, await ScalarAsync(
                    interruptedConnection, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
                await AssertBacklogMatchesDurableStateAsync(interrupted, interruptedConnection).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();

            using var recovered = CreateProvider(root, mode: mode);
            recovered.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
            var recoveredWorker = recovered.GetRequiredService<TransientWorkerService>();
            for (var iteration = 0; iteration < 64; iteration++)
            {
                var worked = await recoveredWorker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false) ||
                    await recoveredWorker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false);
                if (!worked)
                {
                    break;
                }
            }
            await recovered.GetRequiredService<SqliteTransientRuntimeStore>()
                .RetireBeforeAsync(configuration.AgentId!, long.MaxValue, CancellationToken.None).ConfigureAwait(false);

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(DISTINCT candidate_id) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(DISTINCT event_id) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(expectedPhase, await ScalarStringAsync(connection, "SELECT phase FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual("completed", await ScalarStringAsync(connection, "SELECT state FROM transient_worker_candidates;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_candidates WHERE state = 'quarantined';").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_worker_frames WHERE state != 'completed';").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM transient_capture_work WHERE state != 'completed';").ConfigureAwait(false));
            var recoveredIds = await RuntimeIdentityTupleAsync(connection).ConfigureAwait(false);
            if (interruptedIds.Length > 0)
            {
                Assert.AreEqual(interruptedIds, recoveredIds, "Restart changed a durably allocated runtime identity.");
            }
            await AssertCanonicalWorkflowAsync(connection, mode).ConfigureAwait(false);
            var finalBacklog = await AssertBacklogMatchesDurableStateAsync(recovered, connection).ConfigureAwait(false);
            if (mode == TransientOperatingMode.Edge)
            {
                Assert.AreEqual(0L, finalBacklog.ActiveCount);
                Assert.AreEqual(0L, finalBacklog.HeldSourceBytes);
                Assert.IsNull(finalBacklog.OldestCreatedUtc);
                Assert.AreEqual(0L, await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
            }
            else
            {
                Assert.AreEqual(1L, finalBacklog.ActiveCount);
                Assert.IsGreaterThan(0L, finalBacklog.HeldSourceBytes);
                Assert.IsNotNull(finalBacklog.OldestCreatedUtc);
                Assert.AreEqual(
                    await ScalarAsync(connection, "SELECT COUNT(DISTINCT raw_capture_row_id) FROM transient_candidate_sources;").ConfigureAwait(false),
                    await ScalarAsync(connection, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
            }
            var scenarioId = point switch
            {
                TransientRuntimeFaultPoint.BeforeIdentityBatchCommit => "transient-runtime-identity-before-commit",
                TransientRuntimeFaultPoint.AfterIdentityBatchCommit => "transient-runtime-identity-after-commit",
                TransientRuntimeFaultPoint.BeforeCausalExtractionCommit => "transient-runtime-causal-before-commit",
                TransientRuntimeFaultPoint.AfterCausalExtractionCommit => "transient-runtime-causal-after-commit",
                TransientRuntimeFaultPoint.BeforeObservationExtractionCommit => "transient-runtime-observation-before-commit",
                TransientRuntimeFaultPoint.AfterObservationExtractionCommit => "transient-runtime-observation-after-commit",
                TransientRuntimeFaultPoint.BeforeAssessmentCommit => "transient-runtime-assessment-before-commit",
                TransientRuntimeFaultPoint.AfterAssessmentCommit => "transient-runtime-assessment-after-commit",
                TransientRuntimeFaultPoint.AfterFinalizationJournalCommit => "transient-runtime-finalization-after-commit",
                TransientRuntimeFaultPoint.AfterHandoffJournalCommit => "transient-runtime-handoff-after-commit",
                TransientRuntimeFaultPoint.BeforeRuntimeCompletionCommit => "transient-runtime-completion-before-commit",
                TransientRuntimeFaultPoint.AfterRuntimeCompletionCommit => "transient-runtime-completion-after-commit",
                TransientRuntimeFaultPoint.BeforeRetirementCommit => "transient-runtime-retirement-before-commit",
                TransientRuntimeFaultPoint.AfterRetirementCommit => "transient-runtime-retirement-after-commit",
                _ => null
            };
            if (scenarioId is not null)
            {
                await Phase14ScenarioEvidence.RecordAsync(
                    scenarioId,
                    $"fault-point-{point}-mode-{mode}-expected-phase-{expectedPhase}",
                    point.ToString(),
                    ["runtime-fault-observed", "restart-converged-one-candidate", "runtime-identity-stable", "durable-backlog-consistent"])
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [TestMethod]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeFrameHistoryCommit, false)]
    [DataRow((int)TransientRuntimeFaultPoint.AfterFrameHistoryCommit, false)]
    [DataRow((int)TransientRuntimeFaultPoint.BeforeFrameHistoryCommit, true)]
    [DataRow((int)TransientRuntimeFaultPoint.AfterFrameHistoryCommit, true)]
    public async Task FrameHistoryCommitFault_RestartPreservesCausalSuccessAndFailure(
        int faultPoint,
        bool expectedSucceeded)
    {
        var point = (TransientRuntimeFaultPoint)faultPoint;
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-frame-history", Guid.NewGuid().ToString("N"));
        var epoch = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var configuration = CreateConfiguration();
        try
        {
            using (var staged = CreateProvider(root))
            {
                staged.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
                await StageVirtualFramesAsync(staged, configuration, epoch).ConfigureAwait(false);
                if (expectedSucceeded)
                {
                    var worker = staged.GetRequiredService<TransientWorkerService>();
                    Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
                    Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
                }
            }
            SqliteConnection.ClearAllPools();

            var injector = new ThrowingRuntimeFaultInjector(point);
            using (var interrupted = CreateProvider(root, faultInjector: injector))
            {
                interrupted.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await interrupted.GetRequiredService<TransientWorkerService>()
                        .ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.IsTrue(injector.WasInjected);
                using var connection = await OpenAsync(root).ConfigureAwait(false);
                var targetSequence = expectedSucceeded ? 3 : 1;
                var committed = point == TransientRuntimeFaultPoint.AfterFrameHistoryCommit;
                Assert.AreEqual(
                    committed ? "history" : "queued",
                    await ScalarStringAsync(connection,
                        $"SELECT f.state FROM transient_worker_frames f JOIN raw_captures r ON r.raw_capture_row_id = f.raw_capture_row_id WHERE r.capture_sequence = {targetSequence};")
                        .ConfigureAwait(false));
                if (committed)
                {
                    Assert.AreEqual(expectedSucceeded ? 1L : 0L, await ScalarAsync(connection,
                        $"SELECT f.causal_succeeded FROM transient_worker_frames f JOIN raw_captures r ON r.raw_capture_row_id = f.raw_capture_row_id WHERE r.capture_sequence = {targetSequence};")
                        .ConfigureAwait(false));
                }
                Assert.IsGreaterThan(0L, await ScalarAsync(
                    connection, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
                await AssertBacklogMatchesDurableStateAsync(interrupted, connection).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();

            using var recovered = CreateProvider(root);
            recovered.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
            var recoveredWorker = recovered.GetRequiredService<TransientWorkerService>();
            for (var iteration = 0; iteration < 16; iteration++)
            {
                if (!await recoveredWorker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    break;
                }
            }
            await recovered.GetRequiredService<SqliteTransientRuntimeStore>()
                .RetireBeforeAsync(configuration.AgentId!, long.MaxValue, CancellationToken.None).ConfigureAwait(false);
            using var verified = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarAsync(verified, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(
                verified, "SELECT COUNT(*) FROM transient_worker_frames WHERE state != 'completed';").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(
                verified, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;").ConfigureAwait(false));
            var backlog = await AssertBacklogMatchesDurableStateAsync(recovered, verified).ConfigureAwait(false);
            Assert.AreEqual(0L, backlog.ActiveCount);
            Assert.AreEqual(0L, backlog.HeldSourceBytes);
            Assert.IsNull(backlog.OldestCreatedUtc);
            await Phase14ScenarioEvidence.RecordAsync(
                point == TransientRuntimeFaultPoint.BeforeFrameHistoryCommit
                    ? "transient-runtime-frame-history-before-commit"
                    : "transient-runtime-frame-history-after-commit",
                $"fault-point-{point}-expected-succeeded-{expectedSucceeded}",
                point.ToString(),
                ["frame-history-fault-observed", "causal-result-preserved", "restart-drained-frames", "source-holds-released"])
                .ConfigureAwait(false);
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

    [TestMethod]
    public async Task Hybrid_AdjacentEventFramesReserveDistinctSubmittedEventIdentities()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-transient-runtime-hybrid-context", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:TransientDetection:Mode"] = "Hybrid",
                ["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "true"
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
            while (await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false))
            {
            }

            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(2L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_candidates WHERE phase = 'handoff_pending';").ConfigureAwait(false));
            Assert.AreEqual(2L, await ScalarAsync(
                connection, "SELECT COUNT(DISTINCT event_id) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(2L, await ScalarAsync(
                connection, "SELECT COUNT(DISTINCT submission_identity_sha256) FROM transient_candidates;").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarAsync(
                connection, "SELECT COUNT(*) FROM transient_candidates WHERE quarantine_reason IS NOT NULL;").ConfigureAwait(false));
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
        int frameCount = 5,
        bool candidateLimitFrame = false,
        bool invalidLevels = false)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            provider.GetRequiredService<ICelestialCatalog>(),
            new ProjectedSceneStore());
        await module.InitializeAsync(cameraConfiguration, CancellationToken.None).ConfigureAwait(false);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var definitions = provider.GetRequiredService<CaptureLanePolicy>().Definitions;
        var lane = definitions.Single(static value => value.Name == "transient");
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
            if (candidateLimitFrame)
            {
                var width = cameraConfiguration.Rig.Sensor.WidthPixels;
                var height = cameraConfiguration.Rig.Sensor.HeightPixels;
                var strideBytes = checked(width * 2);
                var pixels = new byte[checked(strideBytes * height)];
                if (index == 4)
                {
                    for (var component = 0; component < 48; component++)
                    {
                        var column = component % 8;
                        var row = component / 8;
                        var horizontal = (column + row) % 2 == 0;
                        for (var sample = 0; sample < 4; sample++)
                        {
                            var x = 32 + column * 16 + (horizontal ? sample + 1 : 3);
                            var y = 48 + row * 16 + (horizontal ? 3 : sample + 1);
                            BinaryPrimitives.WriteUInt16LittleEndian(
                                new Span<byte>(pixels, y * strideBytes + x * 2, 2),
                                10_000);
                        }
                    }
                }
                frame = frame with
                {
                    Width = width,
                    Height = height,
                    StrideBytes = strideBytes,
                    PixelData = pixels
                };
            }
            var extra = new Dictionary<string, string>(frame.Metadata.Extra ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["sensorAdcBitDepth"] = "16"
            };
            if (invalidLevels)
            {
                extra.Remove("blackLevelAdu");
                extra.Remove("whiteLevelAdu");
            }
            else
            {
                extra["blackLevelAdu"] = "0";
                extra["whiteLevelAdu"] = ushort.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            capture = capture with
            {
                Frame = frame with
                {
                    Metadata = frame.Metadata with { Extra = extra },
                    Layout = frame.Layout! with
                    {
                        BlackLevel = invalidLevels ? null : 0,
                        WhiteLevel = invalidLevels ? null : ushort.MaxValue
                    }
                }
            };
            var submission = new CaptureLoopSubmission(
                request,
                capture,
                request.RequestedStartUtc,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);
            Assert.IsNotNull(await ingress.AcceptAsync(
                cameraConfiguration, submission, CancellationToken.None).ConfigureAwait(false));
            foreach (var completedLane in definitions.Where(static value => value.Name != "transient"))
            {
                var completedLease = await laneStore.ClaimAsync(
                    completedLane, $"{completedLane.Name}-test", cameraConfiguration, CancellationToken.None).ConfigureAwait(false);
                if (completedLease is not null)
                {
                    await laneStore.CompleteAsync(completedLease, CancellationToken.None).ConfigureAwait(false);
                }
            }
            var lease = await laneStore.ClaimAsync(
                lane, "transient-test", cameraConfiguration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
            await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static TransientRuntimeOperationTarget Target(TransientRuntimeQuarantineRecord item) => new(
        item.RawCaptureRowId, item.LaneWorkId, item.OuterLaneWorkId, item.AgentId, item.CaptureSequence,
        item.CaptureId, item.ArtifactId, item.ManifestSha256, item.PayloadSha256,
        item.ProcessingProfileSha256, item.Mode,
        item.Required, item.OuterLaneState, item.WorkState, item.FrameState, item.FailureReason,
        item.OuterLaneUpdatedUtc, item.WorkUpdatedUtc, item.FrameUpdatedUtc,
        new TransientRuntimeExternalOwnershipEvidence("d331-0821084607", new string('D', 64), true));

    private static async Task<TransientCandidateBacklog> AssertBacklogMatchesDurableStateAsync(
        ServiceProvider provider,
        SqliteConnection connection)
    {
        var backlog = await provider.GetRequiredService<ITransientCandidateJournal>()
            .ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(
            await ScalarAsync(connection, """
                SELECT (SELECT COUNT(*) FROM transient_capture_work WHERE state IN ('pending', 'quarantined')) +
                    (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0);
                """).ConfigureAwait(false),
            backlog.ActiveCount);
        Assert.AreEqual(
            await ScalarAsync(connection, """
                SELECT COALESCE(SUM(payload_length), 0) FROM raw_captures WHERE raw_capture_row_id IN (
                    SELECT raw_capture_row_id FROM transient_capture_work WHERE state IN ('pending', 'quarantined')
                    UNION
                    SELECT s.raw_capture_row_id FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE c.source_hold_released = 0);
                """).ConfigureAwait(false),
            backlog.HeldSourceBytes);
        var oldest = await ScalarNullableLongAsync(connection, """
            SELECT MIN(created_unix_ms) FROM (
                SELECT created_unix_ms FROM transient_capture_work WHERE state IN ('pending', 'quarantined')
                UNION ALL
                SELECT created_unix_ms FROM transient_candidates WHERE source_hold_released = 0);
            """).ConfigureAwait(false);
        Assert.AreEqual(
            oldest.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(oldest.Value) : null,
            backlog.OldestCreatedUtc);
        return backlog;
    }

    private static async Task<string> RuntimeIdentityTupleAsync(SqliteConnection connection)
        => await ScalarStringAsync(connection, """
            SELECT COALESCE(group_concat(value, '|'), '') FROM (
                SELECT candidate_id || ':' || event_id || ':' || observation_id || ':' || assessment_id || ':' || event_version_id AS value
                FROM transient_worker_candidates ORDER BY slot_ordinal);
            """).ConfigureAwait(false);

    private static async Task AssertCanonicalWorkflowAsync(
        SqliteConnection connection,
        TransientOperatingMode mode)
    {
        using var identities = connection.CreateCommand();
        identities.CommandText = """
            SELECT c.candidate_id, c.event_id, c.observation_id, c.assessment_id, c.event_version_id,
                   j.candidate_payload, j.finalization_payload, j.submission_payload,
                   j.candidate_payload_sha256, j.finalization_receipt_identity_sha256, j.submission_identity_sha256,
                   j.source_hold_released
            FROM transient_worker_candidates c
            JOIN transient_candidates j ON j.candidate_id = c.candidate_id;
            """;
        using var reader = await identities.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        var candidateId = Guid.ParseExact(reader.GetString(0), "N");
        var eventId = Guid.ParseExact(reader.GetString(1), "N");
        var observationId = Guid.ParseExact(reader.GetString(2), "N");
        var assessmentId = Guid.ParseExact(reader.GetString(3), "N");
        var eventVersionId = Guid.ParseExact(reader.GetString(4), "N");
        var candidateBytes = await reader.GetFieldValueAsync<byte[]>(5).ConfigureAwait(false);
        var candidateIdentity = reader.GetString(8);
        var parsedCandidate = TransientContractJson.ParseCandidate(candidateBytes);
        Assert.IsTrue(parsedCandidate.Validation.IsValid, parsedCandidate.Validation.ReasonCode);
        Assert.IsNotNull(parsedCandidate.Value);
        Assert.AreEqual(candidateId, parsedCandidate.Value.CandidateId);
        Assert.AreEqual(eventId, parsedCandidate.Value.EventId);
        Assert.AreEqual(candidateIdentity, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(candidateBytes)));
        if (mode == TransientOperatingMode.Edge)
        {
            var receiptBytes = await reader.GetFieldValueAsync<byte[]>(6).ConfigureAwait(false);
            var receiptIdentity = reader.GetString(9);
            var parsedReceipt = TransientCandidateDeliveryJson.ParseFinalization(receiptBytes);
            Assert.IsTrue(parsedReceipt.Validation.IsValid, parsedReceipt.Validation.ReasonCode);
            Assert.IsNotNull(parsedReceipt.Value);
            Assert.AreEqual(candidateId, parsedReceipt.Value.CandidateId);
            Assert.AreEqual(eventId, parsedReceipt.Value.EventId);
            Assert.AreEqual(receiptIdentity, parsedReceipt.Value.ReceiptIdentitySha256);
            Assert.AreEqual(eventId, parsedReceipt.Value.Event.EventId);
            Assert.AreEqual(eventVersionId, parsedReceipt.Value.Event.EventVersionId);
            Assert.AreEqual(1, parsedReceipt.Value.Event.Version);
            Assert.IsNull(parsedReceipt.Value.Event.PreviousEventVersionId);
            Assert.IsTrue(parsedReceipt.Value.Event.Observations.Any(value =>
                value.ObservationId == observationId && value.Extraction.OriginatingCandidateId == candidateId));
            Assert.IsTrue(parsedReceipt.Value.Event.Assessments.Any(value => value.AssessmentId == assessmentId));
            Assert.AreEqual(1L, reader.GetInt64(11));
        }
        else
        {
            var submissionBytes = await reader.GetFieldValueAsync<byte[]>(7).ConfigureAwait(false);
            var submissionIdentity = reader.GetString(10);
            var parsedSubmission = TransientCandidateDeliveryJson.ParseSubmission(submissionBytes);
            Assert.IsTrue(parsedSubmission.Validation.IsValid, parsedSubmission.Validation.ReasonCode);
            Assert.IsNotNull(parsedSubmission.Value);
            Assert.AreEqual(candidateId, parsedSubmission.Value.CandidateId);
            Assert.AreEqual(eventId, parsedSubmission.Value.EventId);
            Assert.AreEqual(candidateId, parsedSubmission.Value.Candidate.CandidateId);
            Assert.AreEqual(eventId, parsedSubmission.Value.Candidate.EventId);
            Assert.AreEqual(submissionIdentity, parsedSubmission.Value.SubmissionIdentitySha256);
            Assert.AreEqual(0L, reader.GetInt64(11));
        }
        Assert.IsFalse(await reader.ReadAsync().ConfigureAwait(false));
    }

    private static CameraModuleConfig CreateConfiguration(
        VirtualTransientScenarioDefinition? scenario = null,
        VirtualCloudScenarioDefinition? cloudScenario = null,
        int width = 64,
        int height = 48)
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
            transientScenario = scenario,
            cloudScenario
        });
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile(
                    "TransientRuntimeFixture", width, height, 5.86, SensorColorMode.Mono,
                    CameraPixelFormat.Mono16, SensorResponseMode.Monochrome,
                    SensorRecipeVersion: "transient-runtime-v1"),
                new OpticsProfile(
                    "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    width / 2d, height / 2d, Math.Min(width, height) / 2d - 1,
                    CalibrationVersion: "transient-runtime-optics-v1"),
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
        ITransientRuntimeFaultInjector? faultInjector = null,
        ITransientCandidateFaultInjector? candidateFaultInjector = null,
        TimeProvider? timeProvider = null,
        TransientOperatingMode mode = TransientOperatingMode.Edge)
    {
        var values = new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:TransientDetection:Mode"] = mode.ToString(),
            ["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] =
                (mode == TransientOperatingMode.Hybrid).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (maximumAdjacentStartIntervalSeconds.HasValue)
        {
            values["CameraAgent:TransientDetection:MaximumAdjacentStartIntervalSeconds"] =
                maximumAdjacentStartIntervalSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        if (faultInjector is not null)
        {
            services.AddSingleton(faultInjector);
            services.AddSingleton<ITransientRuntimeFaultInjector>(faultInjector);
        }
        if (candidateFaultInjector is not null)
        {
            services.AddSingleton(candidateFaultInjector);
            services.AddSingleton<ITransientCandidateFaultInjector>(candidateFaultInjector);
        }
        return services.BuildServiceProvider();
    }

    private static TransientCandidateV1 CreateAssociationCandidate(
        Guid eventId,
        DateTimeOffset startedUtc,
        double yOffset)
    {
        var candidateId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var source = new TransientSourceEvidenceReferenceV1(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            evidenceId,
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    Guid.NewGuid(), FrameArtifactRole.Raw, "raw", new string('A', 64), new string('B', 64))),
            startedUtc,
            startedUtc.AddSeconds(1),
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("test", "1"));
        return new TransientCandidateV1(
            TransientCandidateV1.CurrentSchemaVersion,
            candidateId,
            eventId,
            "transient-runtime-agent",
            TransientCandidateState.Provisional,
            startedUtc.AddSeconds(1),
            evidenceId,
            [source],
            new TransientObservationProvenanceV1(new string('C', 64), "calibration", "mask", "profile"),
            new TransientObservationExtractionV1(
                candidateId,
                new TransientExtractionProducerV1(
                    TransientExtractionProducerV1.CurrentSchemaVersion,
                    TransientExtractionProducerKind.DeterministicAlgorithm,
                    "test",
                    "1"),
                new string('D', 64)),
            new TransientGeometryV1(
                evidenceId,
                10,
                20,
                new TransientBoundingRegionV1(0, yOffset, 10, 1),
                [new TransientPointV1(0, yOffset), new TransientPointV1(10, yOffset)]),
            null,
            []);
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                Assert.Fail($"Condition was not satisfied within {timeout}.");
            }
            await Task.Delay(10).ConfigureAwait(false);
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

    private sealed class BlockingSqliteRuntimeFaultInjector(
        TransientRuntimeFaultPoint point,
        int errorCode) : ITransientRuntimeFaultInjector, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _attempts;
        private int _completedAttempts;
        private int _enabled = 1;

        internal int Attempts => Volatile.Read(ref _attempts);

        internal int CompletedAttempts => Volatile.Read(ref _completedAttempts);

        public void Inject(TransientRuntimeFaultPoint current)
        {
            if (current != point || Volatile.Read(ref _enabled) == 0)
            {
                return;
            }
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt > 1 && !_release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the blocked SQLite failure.");
            }
            Interlocked.Increment(ref _completedAttempts);
            if (Volatile.Read(ref _enabled) != 0)
            {
                throw new SqliteException("Injected persistent transient SQLite storage failure.", errorCode, errorCode);
            }
        }

        internal void BlockNextAttempt() => _release.Reset();

        internal void ReleaseBlockedAttempt() => _release.Set();

        internal void Disable() => Volatile.Write(ref _enabled, 0);

        public void Dispose() => _release.Dispose();
    }

    private sealed class ThrowingRuntimeFaultInjector(
        TransientRuntimeFaultPoint point) : ITransientRuntimeFaultInjector
    {
        private int _thrown;

        internal bool WasInjected => Volatile.Read(ref _thrown) != 0;

        public void Inject(TransientRuntimeFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InvalidOperationException("Injected transient runtime fault.");
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
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
    private static async Task<long?> ScalarNullableLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
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
