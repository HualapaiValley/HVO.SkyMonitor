using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Common.Configuration;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false, "evidence-invalid", false, false)]
    [DataRow(true, "evidence-unavailable", false, false)]
    [DataRow(true, "evidence-unavailable", false, true)]
    [DataRow(true, "evidence-unavailable", true, false)]
    public async Task VisibleRawPublicationReleasesLiveLaneEvidenceQuarantines(
        bool claimBeforeLoss, string reason, bool failAfterPublication, bool quarantineUnrelated)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var instance = Guid.NewGuid();
        var binding = new SiloStorageBinding(instance.ToString("D"), CameraAgentSiloBinding.StoreIdFor(instance),
            CameraAgentSiloBinding.BucketFor(instance));
        var access = RandomHex(12);
        var secret = RandomHex(24);
        var container = BuildStockContainer(access, secret, "on");
        await using var containerLease = container.ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);
        var endpoint = Endpoint(container);
        using var admin = SiloS3ObjectStore.CreateClient(endpoint, access, secret);
        await CreateVersionedBucketAsync(admin, binding.Bucket, token).ConfigureAwait(false);
        var writer = await CreateWriterAsync(container, binding, token).ConfigureAwait(false);
        await InstallBindingAsync(admin, binding, writer.Access, token).ConfigureAwait(false);
        var settings = InstalledSettings(binding, endpoint, writer, Guid.NewGuid(), RandomHex(24));
        settings["CameraAgent:CaptureDistribution:SecondaryLanes:0:Name"] = "retry";
        settings["CameraAgent:CaptureDistribution:SecondaryLanes:0:Enabled"] = "true";
        settings["CameraAgent:CaptureDistribution:SecondaryLanes:0:Required"] = "true";
        settings["CameraAgent:CaptureDistribution:SecondaryLanes:1:Name"] = "unrelated";
        settings["CameraAgent:CaptureDistribution:SecondaryLanes:1:Enabled"] = "true";

        var root = Directory.CreateTempSubdirectory("silo-lane-recovery-").FullName;
        var fault = new InterruptRawRecovery();
        using var host = new CameraAgentIntegrationFixture();
        await InitializeSiloLaneRecoveryHostAsync(host, settings, root, fault, token).ConfigureAwait(false);
        using var scope = host.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var storage = services.GetRequiredService<SiloInstalledStorage>();
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = ConfigureSiloLaneRecovery(services);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        var capture = await ingress.AcceptAsync(config, RecoverySubmission(), token).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        var id = capture.Manifest.Descriptor.Capture.CaptureId;
        fault.InterruptPublication(SiloFaultPoint.AfterSegmentCommitted);
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.AreEqual(SiloStagedState.Recorded,
            (await storage.State.ReadGroupAsync(id.ToString("N"), "raw-1", token).ConfigureAwait(false))?.State);

        var policy = services.GetRequiredService<CaptureLanePolicy>();
        var definitions = policy.Definitions;
        var standard = definitions.Single(static lane => lane.Name == "standard");
        var retry = await ClaimAsync("retry").ConfigureAwait(false);
        if (quarantineUnrelated)
        {
            var unrelated = await ClaimAsync("unrelated").ConfigureAwait(false);
            await ingress.FailAsync(unrelated, CaptureLaneHandlerResult.Terminal("handler-failure"), token).ConfigureAwait(false);
        }
        var standardLease = claimBeforeLoss ? await ClaimAsync("standard").ConfigureAwait(false) : null;
        if (!failAfterPublication)
            await ingress.FailAsync(retry, CaptureLaneHandlerResult.Terminal("evidence-invalid"), token).ConfigureAwait(false);

        File.Delete(capture.StoredFrame.AbsolutePath);
        File.Delete(Path.ChangeExtension(capture.StoredFrame.AbsolutePath, ".json"));
        var result = CaptureLaneHandlerResult.Terminal(reason);
        if (standardLease is null)
        {
            Assert.IsNull(await ingress.ClaimAsync(standard, "silo-test", config, token).ConfigureAwait(false));
            Assert.AreEqual(("quarantined", "lane-evidence-invalid"), await ReadRawDispositionAsync(root, id, token).ConfigureAwait(false));
        }
        else
        {
            result = await services.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(standardLease.Context, token)
                .ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerResult.Terminal(reason), result);
            if (!failAfterPublication)
                Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, await ingress.FailAsync(standardLease, result, token).ConfigureAwait(false));
            Assert.AreEqual(("committed", (string?)null), await ReadRawDispositionAsync(root, id, token).ConfigureAwait(false));
        }

        using var connection = await OpenRawJournalAsync(root, token).ConfigureAwait(false);
        if (!failAfterPublication)
        {
            Assert.AreEqual(("quarantined", reason), (await ReadLaneAsync("standard").ConfigureAwait(false)).Disposition);
            Assert.AreEqual(1L, StandardQuarantineCount());
            var hostOptions = services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
            var laneStore = new SqliteCaptureLaneStore(root, hostOptions.RawIngressSqliteBusyTimeoutSeconds,
                hostOptions.CaptureDistribution, policy, services.GetRequiredService<TimeProvider>(), new NullCaptureLaneFaultInjector());
            await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(() => laneStore.EnsureCanAcceptAsync(1, token).AsTask())
                .ConfigureAwait(false);
            Assert.AreEqual(2L, await CountRequiredLanesAtPressureAsync(2).ConfigureAwait(false));
        }

        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.AreEqual(SiloStagedState.Visible,
            (await storage.State.ReadGroupAsync(id.ToString("N"), "raw-1", token).ConfigureAwait(false))?.State);
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5, usesSilo: true);
        Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(id, token).ConfigureAwait(false));
        Assert.AreEqual(("committed", (string?)null), await ReadRawDispositionAsync(root, id, token).ConfigureAwait(false));
        if (failAfterPublication)
        {
            Assert.IsNotNull(standardLease);
            Assert.AreEqual("leased", (await ReadLaneAsync("standard").ConfigureAwait(false)).Disposition.State);
            Assert.AreEqual("leased", (await ReadLaneAsync("retry").ConfigureAwait(false)).Disposition.State);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Abandoned, await ingress.FailAsync(standardLease, result, token).ConfigureAwait(false));
            Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure,
                await ingress.FailAsync(retry, CaptureLaneHandlerResult.Terminal("evidence-invalid"), token).ConfigureAwait(false));
        }

        var restored = await ReadLaneAsync("standard").ConfigureAwait(false);
        Assert.AreEqual(("abandoned", "raw-evidence-revalidated"), restored.Disposition);
        Assert.IsTrue(restored.Completed);
        var released = await ReadLaneAsync("retry").ConfigureAwait(false);
        Assert.AreEqual((("pending", (string?)null), 0, false), released);
        if (quarantineUnrelated)
            Assert.AreEqual((("quarantined", "handler-failure"), 1, false), await ReadLaneAsync("unrelated").ConfigureAwait(false));
        Assert.AreEqual(failAfterPublication ? 2L : 1L, await CountSiloRestorationEventsAsync(connection, id, token).ConfigureAwait(false));
        Assert.AreEqual(0L, StandardQuarantineCount(), "Live settlement must refresh lane state.");
        Assert.AreEqual(2L, await CountRequiredLanesAtPressureAsync(0).ConfigureAwait(false));
        if (!quarantineUnrelated)
        {
            var snapshot = services.GetRequiredService<CaptureLaneState>().Snapshot;
            Assert.AreEqual(CaptureLaneAvailability.Healthy, snapshot.Availability);
            Assert.AreEqual("accepting", snapshot.Reason);
        }

        var fresh = await ingress.AcceptAsync(config, RecoverySubmission(), token).ConfigureAwait(false);
        Assert.AreEqual(RawIngressOutcome.Committed, fresh?.Outcome);
        Assert.AreEqual(0L, StandardQuarantineCount());
        Assert.AreEqual(fresh!.Manifest.Descriptor.Capture.CaptureId,
            (await ClaimAsync("standard").ConfigureAwait(false)).Context.RawCapture.Manifest.Descriptor.Capture.CaptureId);

        long StandardQuarantineCount() => services.GetRequiredService<CaptureLaneState>().Snapshot.Lanes
            .Single(static lane => lane.Lane == "standard").QuarantineCount;

        async Task<long> CountRequiredLanesAtPressureAsync(int pressure)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM capture_lane_definitions
                WHERE enabled = 1 AND required = 1 AND pressure_state = $pressure;
                """;
            command.Parameters.AddWithValue("$pressure", pressure);
            return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }

        async Task<CaptureLaneLease> ClaimAsync(string name)
        {
            var lane = definitions.Single(lane => lane.Name == name);
            var lease = await ingress.ClaimAsync(lane, "silo-test", config, token).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            return lease;
        }

        async Task<((string State, string? FailureReason) Disposition, int Attempts, bool Completed)> ReadLaneAsync(string lane)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT work.state, work.failure_reason, work.attempt_count, work.completed_unix_ms
                FROM capture_lane_work work JOIN raw_captures raw ON raw.raw_capture_row_id = work.raw_capture_row_id
                WHERE raw.capture_id = $capture AND work.lane_name = $lane;
                """;
            command.Parameters.AddWithValue("$capture", id.ToString("N"));
            command.Parameters.AddWithValue("$lane", lane);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
            var failure = await reader.IsDBNullAsync(1, token).ConfigureAwait(false) ? null : reader.GetString(1);
            var completed = !await reader.IsDBNullAsync(3, token).ConfigureAwait(false);
            return ((reader.GetString(0), failure), reader.GetInt32(2), completed);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public async Task MissingPublishedRawEvidenceQuarantinesStandardLane(
        bool interruptReclaim, bool claimBeforePublication, bool deletePayload)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var instance = Guid.NewGuid();
        var binding = new SiloStorageBinding(instance.ToString("D"), CameraAgentSiloBinding.StoreIdFor(instance),
            CameraAgentSiloBinding.BucketFor(instance));
        var access = RandomHex(12);
        var secret = RandomHex(24);
        var container = BuildStockContainer(access, secret, "on");
        await using var containerLease = container.ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);
        var endpoint = Endpoint(container);
        using var admin = SiloS3ObjectStore.CreateClient(endpoint, access, secret);
        await CreateVersionedBucketAsync(admin, binding.Bucket, token).ConfigureAwait(false);
        var writer = await CreateWriterAsync(container, binding, token).ConfigureAwait(false);
        await InstallBindingAsync(admin, binding, writer.Access, token).ConfigureAwait(false);

        var root = Directory.CreateTempSubdirectory("silo-missing-raw-evidence-").FullName;
        var fault = new InterruptRawRecovery();
        using var host = new CameraAgentIntegrationFixture();
        var settings = InstalledSettings(binding, endpoint, writer, Guid.NewGuid(), RandomHex(24));
        await InitializeSiloLaneRecoveryHostAsync(host, settings, root, fault, token).ConfigureAwait(false);
        using var scope = host.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var storage = services.GetRequiredService<SiloInstalledStorage>();
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = ConfigureSiloLaneRecovery(services, requireRawFrame: deletePayload);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        var capture = await ingress.AcceptAsync(config, RecoverySubmission(), token).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        var descriptor = capture.Manifest.Descriptor;
        var captureId = descriptor.Capture.CaptureId;
        var standard = services.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard");
        var lease = claimBeforePublication ? await ClaimAsync().ConfigureAwait(false) : null;
        if (interruptReclaim)
            fault.InterruptReclamation();
        var publication = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        Assert.AreEqual(interruptReclaim ? "silo-raw-admission-failed" : null, publication.Status);
        Assert.AreEqual(SiloStagedState.Visible,
            (await storage.State.ReadGroupAsync(captureId.ToString("N"), "raw-1", token).ConfigureAwait(false))?.State);
        Assert.IsFalse(File.Exists(capture.StoredFrame.AbsolutePath));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(capture.StoredFrame.AbsolutePath, ".json")));

        var revision = deletePayload
            ? CaptureContractJson.ComputeDescriptorSha256(descriptor)
            : SiloArtifactStorage.RawEvidenceRevision;
        var member = await storage.Index.ReadRoleAsync(new(SiloIdentityKind.Member, descriptor.Artifact.ArtifactId.ToString("D"),
            revision), token).ConfigureAwait(false);
        Assert.IsNotNull(member);
        var locator = member.Locators.Single(static copy => copy.Eligible && copy.CopyState == "active").Locator;
        await admin.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = locator.Bucket,
            Key = locator.Key,
            VersionId = locator.VersionId
        }, token).ConfigureAwait(false);

        lease ??= await ClaimAsync().ConfigureAwait(false);
        Assert.AreEqual(!claimBeforePublication, lease.SiloEvidenceVisibleAtClaim);
        var result = await services.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, token).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Outcome);
        Assert.AreEqual("evidence-unavailable", result.Reason);
        Assert.IsTrue(result.PublishedSiloEvidenceFailed);
        Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, await ingress.FailAsync(lease, result, token).ConfigureAwait(false));

        if (interruptReclaim)
            Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        using var connection = await OpenRawJournalAsync(root, token).ConfigureAwait(false);
        using var disposition = connection.CreateCommand();
        disposition.CommandText = """
            SELECT state, failure_reason, attempt_count FROM capture_lane_work
            WHERE lane_name = 'standard' AND state = 'quarantined';
            """;
        using (var reader = await disposition.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
            Assert.AreEqual(("quarantined", "evidence-unavailable", 1L), (reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        Assert.AreEqual(0L, await CountSiloRestorationEventsAsync(connection, captureId, token).ConfigureAwait(false));
        var snapshot = services.GetRequiredService<CaptureLaneState>().Snapshot;
        Assert.AreEqual(CaptureLaneAvailability.Unhealthy, snapshot.Availability);
        Assert.AreEqual("required-quarantine", snapshot.Reason);

        async Task<CaptureLaneLease> ClaimAsync()
        {
            var claimed = await ingress.ClaimAsync(standard, "silo-test", config, token).ConfigureAwait(false);
            Assert.IsNotNull(claimed);
            return claimed;
        }
    }

    private static async Task InitializeSiloLaneRecoveryHostAsync(CameraAgentIntegrationFixture host,
        Dictionary<string, string?> settings, string root, InterruptRawRecovery fault, CancellationToken token)
    {
        var keyDirectory = Directory.CreateDirectory(Path.Combine(root, "settings")).FullName;
        foreach (var (key, value) in settings)
            await File.WriteAllTextAsync(Path.Combine(keyDirectory, key.Replace(":", "__", StringComparison.Ordinal)), value, token)
                .ConfigureAwait(false);
        var priorKeyDirectory = Environment.GetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable);
        var priorSettingsPath = Environment.GetEnvironmentVariable(DeploymentLocalSettings.PathVariable);
        try
        {
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, keyDirectory);
            Environment.SetEnvironmentVariable(DeploymentLocalSettings.PathVariable, Path.Combine(root, DeploymentLocalSettings.FileName));
            await host.InitializeAsync(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<ISiloFaults>(fault);
                services.AddSingleton<IRawIngressFaultInjector>(fault);
            }, settings, root).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, priorKeyDirectory);
            Environment.SetEnvironmentVariable(DeploymentLocalSettings.PathVariable, priorSettingsPath);
        }
    }

    private static CameraModuleConfig ConfigureSiloLaneRecovery(IServiceProvider services, bool requireRawFrame = false)
    {
        var config = NightlyProductFixture.Configuration() with
        {
            Pipeline = new(
                requireRawFrame
                    ? [new("Preview", "preview", DependsOn: ["$raw"]), new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false)]
                    : [new("ProjectedScene", "scene", DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        services.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(config);
        return config;
    }

    private static CaptureLoopSubmission RecoverySubmission()
    {
        var exposure = TimeSpan.FromSeconds(1);
        var start = DateTimeOffset.UtcNow - exposure;
        var setpoint = new CaptureSetpoint(exposure, 1, null, null);
        const int size = NightlyProductFixture.Size;
        var frame = new CameraFrame(start, size, size, CameraPixelFormat.Mono16, new byte[size * size * 2],
            new FrameMetadata(exposure, setpoint.Gain, 10, "silo-lane-recovery"));
        return new(new CaptureRequest(start, exposure, CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false), start, exposure, exposure);
    }

    private static async Task<RawIngressJournalEntry> InterruptRawReclamationAsync(IServiceProvider services,
        SiloInstalledStorage storage, SqliteRawCaptureJournal journal, ReconstructionDescriptor template,
        InterruptRawRecovery fault, CancellationToken token)
    {
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var capture = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), token).ConfigureAwait(false);
        Assert.IsNotNull(capture);
        var id = capture.Manifest.Descriptor.Artifact.ArtifactId;
        fault.InterruptPublication(SiloFaultPoint.AfterSegmentPut);
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        File.Delete(capture.StoredFrame.AbsolutePath);
        ingress.InvalidateEvidence();
        Assert.IsFalse(await ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(token).ConfigureAwait(false));
        fault.InterruptReclamation();
        var interrupted = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        Assert.AreEqual("silo-raw-admission-failed", interrupted.Status);
        var row = await journal.ReadSiloAdmissionByArtifactAsync(id, token).ConfigureAwait(false);
        Assert.IsNotNull(row);
        Assert.AreEqual("missing_evidence", row.State);
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(row.CaptureId, token).ConfigureAwait(false));
        var group = await storage.State.ReadGroupAsync(row.CaptureId.ToString("N"), "raw-1", token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, group?.State);
        Assert.IsFalse(File.Exists(capture.StoredFrame.AbsolutePath));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(capture.StoredFrame.AbsolutePath, ".json")));
        var intent = ReclaimIntentPath(storage.PhysicalState.Resolve("raw-admission-intents"), id);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(intent, token).ConfigureAwait(false));
        Assert.IsTrue(document.RootElement.GetProperty("Reclaim").GetBoolean());
        Assert.IsTrue(document.RootElement.GetProperty("SiloVisible").GetBoolean());
        return row;
    }

    private static async Task AssertStartupReclaimsRawAsync(Dictionary<string, string?> settings, string root,
        RawIngressJournalEntry row, SqliteRawCaptureJournal journal, ReconstructionDescriptor template, CancellationToken token)
    {
        var fault = new InterruptRawRecovery();
        using var restarted = new CameraAgentIntegrationFixture { KeepStorageOnDispose = true };
        await restarted.InitializeAsync(services =>
        {
            // Isolate raw-ingress startup from the background publication sweep.
            services.RemoveAll<IHostedService>();
            services.AddSingleton<ISiloFaults>(fault);
            services.AddSingleton<IRawIngressFaultInjector>(fault);
        }, settings, root).ConfigureAwait(false);
        using var scope = restarted.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var storage = services.GetRequiredService<SiloInstalledStorage>();
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        services.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(config);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(row.CaptureId, token).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(ReclaimIntentPath(storage.PhysicalState.Resolve("raw-admission-intents"), row.ArtifactId)));
        Assert.IsTrue((await storage.Catalog.ReopenAsync(row.CaptureId.ToString("N"), "raw-1", token).ConfigureAwait(false)).Reopenable);
        await AssertRecoveredRawAsync(row.ArtifactId, token).ConfigureAwait(false);
        await AssertLiveAdmissionAsync(token).ConfigureAwait(false);

        await AssertCommittedRawRecoversAsync("written-off", SiloFaultPoint.AfterSegmentPut, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("written-off", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("missing_evidence", SiloFaultPoint.AfterSegmentPut, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("missing_evidence", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("quarantined", SiloFaultPoint.AfterSegmentPut, token).ConfigureAwait(false);
        await AssertCommittedRawRecoversAsync("quarantined", SiloFaultPoint.AfterSegmentCommitted, token).ConfigureAwait(false);
        await AssertQuarantinedWriteOffReclaimsAsync(services, storage, journal, template, root, fault, token).ConfigureAwait(false);
        await AssertRawPublicationIsolatesFailuresAsync(services, storage, journal, template, fault, token).ConfigureAwait(false);
        await AssertPreCommitQuarantineRetainsStagingAsync(services, storage, journal, template, fault, token).ConfigureAwait(false);

        async Task AssertRecoveredRawAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            var recovered = await journal.ReadSiloAdmissionByArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(recovered);
            Assert.IsFalse(recovered.EvidenceWrittenOff);
            Assert.AreEqual(("committed", (string?)null),
                await ReadRawDispositionAsync(root, recovered.CaptureId, cancellationToken).ConfigureAwait(false));
            Assert.AreEqual(0L, (await journal.ReadHealthTotalsAsync(cancellationToken).ConfigureAwait(false)).FailureCount);
            var result = await services.GetRequiredService<ICameraAgentArtifactService>().OpenContentAsync(artifactId, cancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
            Assert.IsNotNull(result.Content);
            await using (result.Content.ConfigureAwait(false))
                Assert.AreEqual(recovered.PayloadSha256,
                    Convert.ToHexString(await SHA256.HashDataAsync(result.Content, cancellationToken).ConfigureAwait(false)));
        }

        async Task AssertLiveAdmissionAsync(CancellationToken cancellationToken)
        {
            // The live coordinator uses the operations readiness path, which can cache a failed initialization.
            Assert.IsTrue(await ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(cancellationToken).ConfigureAwait(false));
            await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var admitted = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), cancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(RawIngressOutcome.Committed, admitted?.Outcome);
            Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, cancellationToken).ConfigureAwait(false)).Status);
        }

        async Task AssertCommittedRawRecoversAsync(string disposition, SiloFaultPoint point, CancellationToken cancellationToken)
        {
            var receipt = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), cancellationToken)
                .ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var descriptor = receipt.Manifest.Descriptor;
            var capture = descriptor.Capture.CaptureId;
            var groupId = capture.ToString("N");
            fault.InterruptPublication(point);
            Assert.AreEqual("silo-raw-admission-failed",
                (await ingress.PublishSiloAdmissionsAsync(16, cancellationToken).ConfigureAwait(false)).Status);
            var group = await storage.State.ReadGroupAsync(groupId, "raw-1", cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(group);
            Assert.AreEqual(point == SiloFaultPoint.AfterSegmentPut ? SiloStagedState.ManifestQualified : SiloStagedState.Recorded, group.State);
            Assert.HasCount(2, group.Members);
            foreach (var member in group.Members)
            {
                using var staged = await storage.Spool.OpenAsync(member.Draft.SourceToken, cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(member.Draft.Length, staged.Length);
            }

            var sidecar = Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json");
            if (disposition == "missing_evidence" && point == SiloFaultPoint.AfterSegmentPut)
                File.Delete(sidecar);
            else
                File.Delete(receipt.StoredFrame.AbsolutePath);
            if (disposition == "quarantined")
                await File.WriteAllTextAsync(sidecar, "damaged staging", cancellationToken).ConfigureAwait(false);
            if (disposition == "quarantined")
                await journal.MarkEvidenceFailureAsync(capture, disposition, "silo-test-interrupted-evidence", cancellationToken)
                    .ConfigureAwait(false);
            ingress.InvalidateEvidence();
            Assert.IsFalse(await ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(cancellationToken).ConfigureAwait(false));
            var original = await ReadRawDispositionAsync(root, capture, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(disposition == "quarantined" ? disposition : "missing_evidence", original.State);
            Assert.IsNotNull(original.FailureReason);
            if (disposition == "written-off")
            {
                var evidence = await ingress.ReadEvidencePageAsync(10, null, cancellationToken).ConfigureAwait(false);
                var target = evidence.Items.Single(item => item.Target.CaptureId == capture).Target;
                await ingress.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.WriteOff, "silo-write-off-" + groupId, "operator",
                    "operator-approved-loss", cancellationToken).ConfigureAwait(false);
            }

            Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(capture, cancellationToken).ConfigureAwait(false));
            Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, cancellationToken).ConfigureAwait(false)).Status);
            var settled = await storage.State.ReadGroupAsync(groupId, "raw-1", cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(settled);
            Assert.AreEqual(SiloStagedState.Visible, settled.State);
            foreach (var member in settled.Members)
            {
                Assert.IsTrue(member.Released);
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
                {
                    using var leaked = await storage.Spool.OpenAsync(member.Draft.SourceToken, cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(capture, cancellationToken).ConfigureAwait(false));
            Assert.IsFalse(File.Exists(receipt.StoredFrame.AbsolutePath));
            Assert.IsFalse(File.Exists(sidecar));
            Assert.IsTrue((await storage.Catalog.ReopenAsync(groupId, "raw-1", cancellationToken).ConfigureAwait(false)).Reopenable);
            if (disposition == "written-off")
            {
                var writtenOff = await journal.ReadSiloAdmissionByArtifactAsync(descriptor.Artifact.ArtifactId, cancellationToken)
                    .ConfigureAwait(false);
                Assert.IsTrue(writtenOff?.EvidenceWrittenOff);
                Assert.AreEqual(original, await ReadRawDispositionAsync(root, capture, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                await AssertRecoveredRawAsync(descriptor.Artifact.ArtifactId, cancellationToken).ConfigureAwait(false);
                await AssertLiveAdmissionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task AssertPreCommitQuarantineRetainsStagingAsync(IServiceProvider services, SiloInstalledStorage storage,
        SqliteRawCaptureJournal journal, ReconstructionDescriptor template, InterruptRawRecovery fault, CancellationToken token)
    {
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var receipt = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), token).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var descriptor = receipt.Manifest.Descriptor;
        var id = descriptor.Capture.CaptureId;
        var groupId = id.ToString("N");
        var payload = receipt.StoredFrame.AbsolutePath;
        var sidecar = Path.ChangeExtension(payload, ".json");
        fault.InterruptPublication(SiloFaultPoint.AfterManifestQualified);
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.AreEqual(SiloStagedState.ManifestQualified, (await storage.State.ReadGroupAsync(groupId, "raw-1", token).ConfigureAwait(false))?.State);
        await journal.MarkEvidenceFailureAsync(id, "quarantined", "silo-test-lane-quarantine", token).ConfigureAwait(false);
        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.AreEqual(SiloStagedState.Abandoned, (await storage.State.ReadGroupAsync(groupId, "raw-1", token).ConfigureAwait(false))?.State);
        Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(id, token).ConfigureAwait(false));
        var row = await journal.ReadSiloAdmissionByArtifactAsync(descriptor.Artifact.ArtifactId, token).ConfigureAwait(false);
        Assert.IsNotNull(row);
        Assert.AreEqual("quarantined", row.State);
        Assert.IsFalse(row.EvidenceWrittenOff);
        Assert.IsTrue(File.Exists(payload));
        Assert.IsTrue(File.Exists(sidecar));
    }

    private static async Task AssertQuarantinedWriteOffReclaimsAsync(IServiceProvider services, SiloInstalledStorage storage,
        SqliteRawCaptureJournal journal, ReconstructionDescriptor template, string root, InterruptRawRecovery fault, CancellationToken token)
    {
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var receipt = await ingress.AcceptAsync(config, MakeRawSubmission(template, RecoveryTimestamp(template)), token).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var descriptor = receipt.Manifest.Descriptor;
        var capture = descriptor.Capture.CaptureId;
        var artifact = descriptor.Artifact.ArtifactId;
        fault.InterruptPublication(SiloFaultPoint.AfterManifestQualified);
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        await journal.MarkEvidenceFailureAsync(capture, "quarantined", "silo-test-lane-quarantine", token).ConfigureAwait(false);
        var evidence = await ingress.ReadEvidencePageAsync(10, null, token).ConfigureAwait(false);
        var target = evidence.Items.Single(item => item.Target.CaptureId == capture).Target;
        await ingress.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.WriteOff, "silo-pre-p3-write-off", "operator",
            "operator-approved-loss", token).ConfigureAwait(false);
        var payload = receipt.StoredFrame.AbsolutePath;
        var sidecar = Path.ChangeExtension(payload, ".json");
        Assert.IsTrue(File.Exists(payload));
        Assert.IsTrue(File.Exists(sidecar));

        fault.InterruptReclamation();
        Assert.AreEqual("silo-raw-admission-failed", (await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(capture, token).ConfigureAwait(false));
        Assert.AreEqual(SiloStagedState.Abandoned,
            (await storage.State.ReadGroupAsync(capture.ToString("N"), "raw-1", token).ConfigureAwait(false))?.State);
        Assert.IsFalse(File.Exists(payload));
        Assert.IsFalse(File.Exists(sidecar));

        // Exercise startup replay of the previous intent format for a pre-P3 abandonment.
        var intent = ReclaimIntentPath(storage.PhysicalState.Resolve("raw-admission-intents"), artifact);
        var document = JsonNode.Parse(await File.ReadAllBytesAsync(intent, token).ConfigureAwait(false))!.AsObject();
        Assert.IsFalse(document["SiloVisible"]!.GetValue<bool>());
        document.Remove("SiloVisible");
        await File.WriteAllTextAsync(intent, document.ToJsonString(), token).ConfigureAwait(false);
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(capture, token).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(intent));
        Assert.IsTrue((await journal.ReadSiloAdmissionByArtifactAsync(artifact, token).ConfigureAwait(false))?.EvidenceWrittenOff);
        Assert.AreEqual(("quarantined", "silo-test-lane-quarantine"), await ReadRawDispositionAsync(root, capture, token).ConfigureAwait(false));
    }

    private static async Task AssertRawPublicationIsolatesFailuresAsync(IServiceProvider services, SiloInstalledStorage storage,
        SqliteRawCaptureJournal journal, ReconstructionDescriptor template, InterruptRawRecovery fault, CancellationToken token)
    {
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var config = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var at = RecoveryTimestamp(template);
        var first = await ingress.AcceptAsync(config, MakeRawSubmission(template, at.AddMilliseconds(-100)), token).ConfigureAwait(false);
        var second = await ingress.AcceptAsync(config, MakeRawSubmission(template, at), token).ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        var firstId = first.Manifest.Descriptor.Capture.CaptureId;
        var secondId = second.Manifest.Descriptor.Capture.CaptureId;
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(firstId, token).ConfigureAwait(false));
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(secondId, token).ConfigureAwait(false));

        fault.InterruptPublication(SiloFaultPoint.AfterManifestQualified);
        var scan = await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false);
        Assert.AreEqual((false, "silo-raw-admission-failed"), scan);
        Assert.IsTrue(await journal.HasPendingSiloAdmissionAsync(firstId, token).ConfigureAwait(false));
        Assert.IsTrue(File.Exists(first.StoredFrame.AbsolutePath));
        Assert.IsTrue(File.Exists(Path.ChangeExtension(first.StoredFrame.AbsolutePath, ".json")));
        await AssertSettledAsync(secondId, second.StoredFrame.AbsolutePath).ConfigureAwait(false);

        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        await AssertSettledAsync(firstId, first.StoredFrame.AbsolutePath).ConfigureAwait(false);

        async Task AssertSettledAsync(Guid captureId, string payload)
        {
            Assert.IsFalse(await journal.HasPendingSiloAdmissionAsync(captureId, token).ConfigureAwait(false));
            Assert.IsFalse(File.Exists(payload));
            Assert.IsFalse(File.Exists(Path.ChangeExtension(payload, ".json")));
            Assert.AreEqual(SiloStagedState.Visible,
                (await storage.State.ReadGroupAsync(captureId.ToString("N"), "raw-1", token).ConfigureAwait(false))?.State);
        }
    }

    private static async Task<(string State, string? FailureReason)> ReadRawDispositionAsync(string root, Guid captureId,
        CancellationToken token)
    {
        using var connection = await OpenRawJournalAsync(root, token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, failure_reason FROM raw_captures WHERE capture_id = $capture;";
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
        var reason = await reader.IsDBNullAsync(1, token).ConfigureAwait(false) ? null : reader.GetString(1);
        return (reader.GetString(0), reason);
    }

    private static async Task<SqliteConnection> OpenRawJournalAsync(string root, CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task<long> CountSiloRestorationEventsAsync(SqliteConnection connection, Guid captureId, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM raw_capture_stage_events event
            JOIN raw_captures raw ON raw.raw_capture_row_id = event.raw_capture_row_id
            WHERE raw.capture_id = $capture AND event.stage_key GLOB 'raw-evidence-silo-restored.*'
                AND event.state = 'evidence-restored' AND event.source = 'silo-publication';
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    private static string ReclaimIntentPath(string directory, Guid artifactId) => Path.Combine(directory, artifactId.ToString("N") + ".json");

    private static DateTimeOffset RecoveryTimestamp(ReconstructionDescriptor template) =>
        DateTimeOffset.UtcNow.Subtract(template.Controls.EffectiveExposure).AddMilliseconds(-250);

    private sealed class InterruptRawRecovery : ISiloFaults, IRawIngressFaultInjector
    {
        private int _publicationPoint = -1;
        private int _reclaim;

        public void InterruptPublication(SiloFaultPoint point) => Volatile.Write(ref _publicationPoint, (int)point);
        public void InterruptReclamation() => Volatile.Write(ref _reclaim, 1);

        public void Hit(SiloFaultPoint point)
        {
            if (Interlocked.CompareExchange(ref _publicationPoint, -1, (int)point) == (int)point)
                throw new IOException("Interrupted raw journal publication.");
        }

        public void Inject(RawIngressFaultPoint point) { }

        public void AfterSiloStagingReclaimed()
        {
            if (Interlocked.Exchange(ref _reclaim, 0) == 1)
                throw new IOException("Interrupted raw reclamation before settlement.");
        }
    }
}
