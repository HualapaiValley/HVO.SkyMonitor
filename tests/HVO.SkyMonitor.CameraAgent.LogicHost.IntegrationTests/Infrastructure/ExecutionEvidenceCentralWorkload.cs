using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

/// <summary>Real operator binding and current preview graph; every job is created by normal artifact ingest.</summary>
internal sealed class ExecutionEvidenceCentralWorkload(
    CameraAgentIntegrationFixture fixture,
    ExecutionEvidenceCombinedHarness edge,
    ExecutionEvidenceBindingView binding,
    Guid centralRevisionId,
    string ownerUserId)
{
    internal ExecutionEvidenceBindingView Binding { get; } = binding;
    internal Guid CentralRevisionId { get; } = centralRevisionId;
    internal string OwnerUserId { get; } = ownerUserId;
    // RawCaptureIngress constructs SqliteRawCaptureJournal with this journal subdirectory.
    private string RawJournalPath => Path.Combine(edge.Root, "journal", "raw-ingress.db");

    internal static async Task<ExecutionEvidenceCentralWorkload> BindAsync(
        CameraAgentIntegrationFixture fixture, ExecutionEvidenceCombinedHarness edge)
    {
        using var scope = fixture.CreateHostScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(item => item.DeviceId == edge.Agent.DeviceId)
            .ConfigureAwait(false);
        var owner = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString("N"),
            UserName = $"issue538-{Guid.NewGuid():N}",
            Email = $"issue538-{Guid.NewGuid():N}@integration.invalid"
        };
        owner.NormalizedUserName = owner.UserName.ToUpperInvariant();
        owner.NormalizedEmail = owner.Email.ToUpperInvariant();
        var observatory = await db.Observatories.SingleAsync(item => item.Id == registration.ObservatoryId)
            .ConfigureAwait(false);
        observatory.OwnerUserId = owner.Id;
        registration.OwnerUserId = owner.Id;
        db.Users.Add(owner);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = observatory.Id,
            UserId = owner.Id,
            User = owner,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = DateTimeOffset.UtcNow
        });
        var camera = new LogicalCamera
        {
            ObservatoryId = registration.ObservatoryId,
            Slug = $"evidence-{Guid.NewGuid():N}",
            Name = "Execution evidence acceptance camera",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            CreatedByUserId = registration.OwnerUserId
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = registration.Id,
            InstallationPublicId = registration.DevicePublicId!.Value,
            AssignedAtUtc = DateTimeOffset.UnixEpoch,
            AssignedByUserId = registration.OwnerUserId,
            AssignmentReasonCode = "issue538-acceptance"
        };
        var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        definition = definition with
        {
            Name = camera.Slug,
            Nodes = definition.Nodes.Where(node => node.StepAlias == BuiltInProcessingRecipes.EncodedPreview)
                .ToImmutableArray()
        };
        Assert.HasCount(1, definition.Nodes);
        var portable = ProcessingGraphCompiler.Compile(definition);
        var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingGraphNodeRegistry>();
        var central = LogicHostProcessingGraphAdapter.Compile(definition, registry.Capabilities);
        Assert.IsTrue(portable.IsValid, string.Join('\n', portable.Diagnostics));
        Assert.IsTrue(central.IsValid, string.Join('\n', central.Diagnostics));
        var revision = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            CreatedByUserId = registration.OwnerUserId,
            PublishedAtUtc = DateTimeOffset.UnixEpoch,
            PublishedByUserId = registration.OwnerUserId
        };
        var assignment = new CentralProcessingGraphAssignment
        {
            Revision = revision,
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = registration.ObservatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            EffectiveFromUtc = DateTimeOffset.UnixEpoch,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            ActorUserId = registration.OwnerUserId,
            ReasonCode = "issue538-acceptance"
        };
        db.AddRange(camera, installation, revision, assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var bound = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>().CreateBindingAsync(
            new(registration.ObservatoryId, registration.Id, installation.Id,
                edge.Descriptor.OriginInstallationId, edge.Descriptor.AgentInstanceId),
            registration.OwnerUserId, registration.ObservatoryId).ConfigureAwait(false);
        Assert.IsNotNull(bound, "Only an explicit authorized operator binding admits this local origin pair.");
        return new(fixture, edge, bound, revision.Id, owner.Id);
    }

    internal async Task AssertPersistedAsync(IReadOnlyList<EdgeEvidenceRow> expected)
    {
        using var scope = fixture.CreateHostScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Set<ImportedExecutionEvidenceUnit>().AsNoTracking()
            .Where(unit => unit.BindingId == Binding.Id).ToListAsync().ConfigureAwait(false);
        Assert.HasCount(expected.Count, stored);
        foreach (var row in expected)
        {
            var unit = stored.Single(candidate => candidate.EvidenceId == row.Envelope.EvidenceId);
            Assert.IsTrue(unit.Accepted, unit.RejectionReasonCode);
            Assert.AreEqual(row.Envelope.PayloadSha256, unit.PayloadSha256);
            Assert.AreEqual(row.Envelope.OriginSequence, unit.OriginSequence);
            CollectionAssert.AreEqual(row.Bytes, unit.CanonicalBytes,
                "The importer must persist the sender's exact canonical envelope, not a reconstruction.");
        }
        Assert.AreEqual(expected.Count, stored.Select(unit => unit.EvidenceId).Distinct().Count());
    }

    internal async Task<EdgeHealthEvidence> ReadHealthAsync(CancellationToken cancellationToken = default)
    {
        using var local = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = RawJournalPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await local.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = local.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM raw_captures),
                (SELECT COUNT(*) FROM raw_captures WHERE state <> 'committed'),
                (SELECT COUNT(*) FROM capture_lane_work WHERE state = 'quarantined'),
                (SELECT COUNT(*) FROM capture_lane_work WHERE state = 'abandoned'),
                (SELECT COALESCE(MAX(pressure_state), 0) FROM capture_lane_definitions);
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        using var outboxConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(edge.Root, "outbox", "artifact-outbox.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await outboxConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var pending = outboxConnection.CreateCommand();
        pending.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(payload_length), 0) FROM artifact_outbox_records
            WHERE role = 'Raw' AND status <> 'acknowledged';
            """;
        using var pendingReader = await pending.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await pendingReader.ReadAsync(cancellationToken).ConfigureAwait(false));
        using var scope = fixture.CreateHostScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobs = db.CentralDerivativeJobs.Where(job => job.SourceArtifact!.DevicePublicId == edge.Agent.DevicePublicId);
        var pendingJobs = await jobs.CountAsync(job => job.Status != CentralDerivativeJobStatus.Completed,
            cancellationToken).ConfigureAwait(false);
        var failedJobs = await jobs.CountAsync(job => job.Status == CentralDerivativeJobStatus.TerminalFailure ||
            job.Status == CentralDerivativeJobStatus.RetryableFailure || job.Status == CentralDerivativeJobStatus.Canceled,
            cancellationToken).ConfigureAwait(false);
        return new(edge.Metrics.Snapshot, edge.Metrics.FailedCaptureSamples,
            edge.Agent.Services.GetRequiredService<FleetRuntimeState>().Snapshot.Capture.LastFailedUtc,
            edge.Agent.Services.GetRequiredService<RawIngressTelemetry>().TransactionFailureCount,
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4),
            pendingReader.GetInt64(0), pendingReader.GetInt64(1), pendingJobs, failedJobs,
            await edge.Agent.Services.GetRequiredService<IArtifactOutbox>()
                .GetSnapshotAsync(edge.Root, cancellationToken).ConfigureAwait(false));
    }

    internal async Task<ExecutionEvidenceStatusView> ReadStatusAsync(CancellationToken cancellationToken)
    {
        using var scope = fixture.CreateHostScope();
        var status = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>()
            .GetStatusAsync(Binding.Id, OwnerUserId, Binding.ObservatoryId, cancellationToken).ConfigureAwait(false);
        Assert.IsNotNull(status);
        return status;
    }

    internal static void AssertHealthy(EdgeHealthEvidence baseline, EdgeHealthEvidence current)
    {
        Assert.AreEqual(baseline.CaptureLastFailedUtc, current.CaptureLastFailedUtc, "Capture reported a failure.");
        Assert.AreEqual(baseline.FailedCaptureSamples, current.FailedCaptureSamples, "A capture produced no frame.");
        Assert.AreEqual(baseline.TransactionFailures, current.TransactionFailures);
        Assert.AreEqual(baseline.Counters.IngressFailures, current.Counters.IngressFailures);
        Assert.AreEqual(baseline.Counters.IngressCancellations, current.Counters.IngressCancellations);
        Assert.AreEqual(baseline.Counters.LaneQuarantines, current.Counters.LaneQuarantines);
        Assert.AreEqual(baseline.Counters.LaneAbandonedForPressure, current.Counters.LaneAbandonedForPressure);
        Assert.AreEqual(baseline.BadRawCaptures, current.BadRawCaptures);
        Assert.AreEqual(baseline.QuarantinedLaneRecords, current.QuarantinedLaneRecords);
        Assert.AreEqual(baseline.AbandonedLaneRecords, current.AbandonedLaneRecords, "Optional pressure discarded work.");
        Assert.AreEqual(baseline.FailedCentralJobs, current.FailedCentralJobs);
        Assert.IsLessThan(2, current.MaximumLanePressure, "A durable lane reached its overflow/pressure bound.");
        Assert.IsLessThanOrEqualTo(256, current.RawUploadPendingCount);
        Assert.IsLessThanOrEqualTo(2L * 1024 * 1024 * 1024, current.Upload.HeldBytes);
        Assert.IsLessThanOrEqualTo(128, current.PendingCentralJobs);
        Assert.AreEqual(baseline.Upload.QuarantinedCount, current.Upload.QuarantinedCount);
        Assert.AreEqual(baseline.Upload.AbandonedCount, current.Upload.AbandonedCount);
    }

    internal async Task<EdgeHealthEvidence> ConvergeUploadsAndJobsAsync(CancellationToken cancellationToken)
    {
        while ((await ReadHealthAsync(cancellationToken).ConfigureAwait(false)).RawUploadPendingCount != 0)
        {
            var raw = await UploadNextRawAsync(cancellationToken).ConfigureAwait(false);
            _ = await ExecutePreviewAsync(raw, cancellationToken).ConfigureAwait(false);
        }
        var outbox = edge.Agent.Services.GetRequiredService<IArtifactOutbox>();
        var uploader = edge.Agent.Services.GetRequiredService<ArtifactUploadClient>();
        while (true)
        {
            var snapshot = await outbox.GetSnapshotAsync(edge.Root, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, snapshot.QuarantinedCount + snapshot.AbandonedCount);
            if (snapshot.HeldCount == 0) { break; }
            var lease = await outbox.ClaimAsync(edge.Root, "issue538-final-drain", TimeSpan.FromMinutes(2), cancellationToken)
                .ConfigureAwait(false);
            if (lease is null) { await Task.Delay(100, cancellationToken).ConfigureAwait(false); continue; }
            Assert.AreNotEqual(FrameArtifactRole.Raw, lease.Record.Role);
            var uploaded = await uploader.UploadAsync(edge.Root, lease.Record, cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(ArtifactUploadDisposition.Acknowledged, uploaded.Disposition, uploaded.Reason);
            Assert.IsNotNull(uploaded.Acknowledgement);
            await outbox.AcknowledgeAsync(edge.Root, lease, uploaded.Acknowledgement, cancellationToken).ConfigureAwait(false);
        }
        var final = await ReadHealthAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, final.RawUploadPendingCount);
        Assert.AreEqual(0, final.Upload.HeldCount);
        Assert.AreEqual(0, final.PendingCentralJobs);
        return final;
    }

    internal async Task<RawUploadEvidence> UploadNextRawAsync(CancellationToken cancellationToken = default)
    {
        var outbox = edge.Agent.Services.GetRequiredService<IArtifactOutbox>();
        var client = edge.Agent.Services.GetRequiredService<ArtifactUploadClient>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        while (true)
        {
            var lease = await outbox.ClaimAsync(edge.Root, "issue538-manual-upload", TimeSpan.FromMinutes(2), timeout.Token)
                .ConfigureAwait(false);
            if (lease is null)
            {
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                continue;
            }
            var started = Stopwatch.GetTimestamp();
            var result = await client.UploadAsync(edge.Root, lease.Record, timeout.Token).ConfigureAwait(false);
            var uploadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.AreEqual(ArtifactUploadDisposition.Acknowledged, result.Disposition, result.Reason);
            Assert.IsNotNull(result.Acknowledgement);
            var settlementStarted = Stopwatch.GetTimestamp();
            await outbox.AcknowledgeAsync(edge.Root, lease, result.Acknowledgement, timeout.Token).ConfigureAwait(false);
            var settlementMs = Stopwatch.GetElapsedTime(settlementStarted).TotalMilliseconds;
            if (lease.Record.Role != FrameArtifactRole.Raw) { continue; }
            using var scope = fixture.CreateHostScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var raw = await db.CentralArtifacts.AsNoTracking().Include(item => item.Frame)
                .SingleAsync(item => item.DevicePublicId == edge.Agent.DevicePublicId &&
                    item.ArtifactId == lease.Record.ArtifactId, timeout.Token).ConfigureAwait(false);
            Assert.AreEqual(lease.Record.PayloadSha256, raw.ChecksumSha256, ignoreCase: true);
            Assert.AreEqual(lease.Record.PayloadLength, raw.ByteLength);
            Assert.AreEqual(Binding.LogicalCameraInstallationId, raw.Frame!.LogicalCameraInstallationId);
            await scope.ServiceProvider.GetRequiredService<ICentralArtifactObjectReader>().VerifyAsync(raw, timeout.Token)
                .ConfigureAwait(false);
            var stored = await outbox.ReadAsync(edge.Root, lease.Record.IdempotencyKey, timeout.Token).ConfigureAwait(false);
            Assert.IsNotNull(stored);
            Assert.AreEqual(ArtifactOutboxStatus.Acknowledged, stored.Status);
            using var local = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = RawJournalPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await local.OpenAsync(timeout.Token).ConfigureAwait(false);
            using var command = local.CreateCommand();
            command.CommandText = """
                SELECT committed_unix_ms,
                    (SELECT COUNT(*) FROM raw_captures prior WHERE prior.raw_capture_row_id <= current.raw_capture_row_id)
                FROM raw_captures current WHERE capture_id = $capture;
                """;
            command.Parameters.AddWithValue("$capture", raw.Frame.FrameId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync(timeout.Token).ConfigureAwait(false));
            var descriptor = lease.Record.Manifest!.Manifest.Descriptor;
            Assert.IsNotNull(descriptor.CycleEvidence);
            return new(raw.Frame.FrameId, raw.ArtifactId, raw.Id, raw.Frame.CaptureSequence!.Value, raw.ByteLength,
                raw.ChecksumSha256, uploadMs, settlementMs, raw.Frame.CapturedAtUtc,
                reader.GetInt32(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                descriptor.CycleEvidence.ModuleCallStartedUtc);
        }
    }

    internal async Task<CentralJobEvidence> ExecutePreviewAsync(RawUploadEvidence raw, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        while (true)
        {
            using var scope = fixture.CreateHostScope();
            var started = Stopwatch.GetTimestamp();
            var lease = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeRunnerLeaseService>()
                .ClaimNextAsync("issue538-preview", TimeSpan.FromMinutes(2),
                    CentralDerivativeClaimScope.Only([BuiltInProcessingRecipes.EncodedPreview]), timeout.Token)
                .ConfigureAwait(false);
            if (lease is null)
            {
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                continue;
            }
            var result = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, timeout.Token).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, result.ReasonCode);
            if (lease.SourceDevicePublicId != edge.Agent.DevicePublicId || lease.SourceArtifactId != raw.ArtifactId)
            {
                // Other test-owned queued work may precede this artifact; it contributes no measured sample.
                continue;
            }
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.ResultArtifact)
                .SingleAsync(item => item.Id == lease.JobId, timeout.Token).ConfigureAwait(false);
            Assert.AreEqual(CentralDerivativeJobStatus.Completed, job.Status);
            Assert.AreEqual(CentralRevisionId, lease.GraphRevisionId);
            Assert.AreEqual(raw.CentralArtifactId, job.SourceCentralArtifactId);
            Assert.IsNotNull(job.ResultArtifact);
            Assert.IsNotNull(job.CompletedAtUtc);
            await scope.ServiceProvider.GetRequiredService<ICentralArtifactObjectReader>()
                .VerifyAsync(job.ResultArtifact, timeout.Token).ConfigureAwait(false);
            return new(job.Id, job.GraphExecutionId!.Value, lease.GraphRevisionId!.Value,
                raw.ArtifactId, job.ResultArtifact.ArtifactId, job.ResultArtifact.ChecksumSha256,
                job.ResultArtifact.ByteLength, elapsed, (job.CompletedAtUtc.Value - job.CreatedAtUtc).TotalMilliseconds);
        }
    }
}

internal sealed record RawUploadEvidence(
    Guid CaptureId, Guid ArtifactId, Guid CentralArtifactId, long CaptureSequence,
    long Bytes, string Sha256, double UploadMilliseconds, double SettlementMilliseconds, DateTimeOffset CapturedAtUtc,
    int RawCommitOrdinal, DateTimeOffset JournalCommittedAtUtc, DateTimeOffset ModuleCallStartedUtc);

internal sealed record CentralJobEvidence(
    Guid JobId, Guid GraphExecutionId, Guid GraphRevisionId, Guid SourceArtifactId, Guid OutputArtifactId,
    string OutputSha256, long OutputBytes, double ClaimAndExecuteMilliseconds, double QueueToCompletionMilliseconds);

internal sealed record EdgeHealthEvidence(EdgeFailureCounters Counters, int FailedCaptureSamples,
    DateTimeOffset? CaptureLastFailedUtc, long TransactionFailures, long RawCaptures, long BadRawCaptures,
    long QuarantinedLaneRecords, long AbandonedLaneRecords, int MaximumLanePressure,
    long RawUploadPendingCount, long RawUploadPendingBytes, int PendingCentralJobs, int FailedCentralJobs,
    ArtifactOutboxSnapshot Upload);
