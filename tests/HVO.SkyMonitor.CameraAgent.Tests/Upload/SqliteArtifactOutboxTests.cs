using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Upload;

[TestClass]
[TestCategory("Integration")]
public sealed class SqliteArtifactOutboxTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Payload = [1, 2, 3, 4];
    private static readonly string[] ResolutionActions = ["quarantine", "replay", "quarantine", "abandon"];
    private static readonly string[] CanonicalIndexes =
    [
        "ix_artifact_outbox_claim", "ix_artifact_outbox_lease", "ix_artifact_outbox_operations",
        "ux_artifact_outbox_audit_operation"
    ];

    [TestMethod]
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "The fixture proves lowercase SHA-256 canonicalization.")]
    public async Task EnqueueV2_CanonicalDuplicateIsIdempotentAndConflictFailsClosed()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(StartUtc);
        using var outbox = new SqliteArtifactOutbox(clock);
        var manifest = CreateManifest(root.Path, "frames/raw.bin", StartUtc, 1);

        await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        var lowerCaseHash = manifest with
        {
            Descriptor = manifest.Descriptor with
            {
                Artifact = manifest.Descriptor.Artifact with
                {
                    ChecksumSha256 = manifest.Descriptor.Artifact.ChecksumSha256.ToLowerInvariant()
                }
            }
        };
        await outbox.EnqueueAsync(root.Path, lowerCaseHash, CancellationToken.None).ConfigureAwait(false);

        var conflicting = manifest with { RelativeArtifactPath = "frames/other.bin" };
        WritePayload(root.Path, conflicting.RelativeArtifactPath);
        await File.WriteAllBytesAsync(
            Path.ChangeExtension(Path.Combine(root.Path, conflicting.RelativeArtifactPath), ".json"),
            CaptureContractJson.Serialize(conflicting),
            CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArtifactOutboxConflictException>(async () =>
            await outbox.EnqueueAsync(root.Path, conflicting, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var record = await outbox.ReadAsync(root.Path, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(record);
        Assert.AreEqual(ArtifactOutboxManifestKind.ManifestV2, record.ManifestKind);
        Assert.AreEqual(ArtifactOutboxStatus.Quarantined, record.Status);
        CollectionAssert.AreEqual(CaptureContractJson.Serialize(manifest), record.ManifestBytes.ToArray());
        var snapshot = await outbox.GetSnapshotAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, snapshot.PendingCount);
        Assert.AreEqual(1, snapshot.QuarantinedCount);
        Assert.IsNull(await outbox.ClaimAsync(
            root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));

        using var connection = OpenDatabase(root.Path);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT journal_mode FROM pragma_journal_mode), (SELECT COUNT(*) FROM artifact_outbox_conflicts);";
        using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("wal", reader.GetString(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        await Phase14ScenarioEvidence.RecordAsync(
            "outbox-enqueue-commit",
            "canonical-duplicate-and-conflict",
            null,
            ["canonical-duplicate-idempotent", "identity-conflict-quarantined", "conflict-audited", "quarantined-record-not-claimable"])
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StructuredProduct_RoundTripsAcrossRestartAndProjectsExactDelivery()
    {
        using var root = new TemporaryRoot();
        var manifest = CreateStructuredManifest(root.Path, "derived/scene.json");
        using (var outbox = new SqliteArtifactOutbox())
        {
            await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        }

        using var restarted = new SqliteArtifactOutbox();
        var lease = await restarted.ClaimAsync(
            root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(lease);
        Assert.AreEqual(ArtifactOutboxManifestKind.StructuredProductV1, lease.Record.ManifestKind);
        Assert.IsNotNull(lease.Record.ProductManifest);
        Assert.IsNull(lease.Record.Manifest);
        var delivery = ArtifactUploadClient.ResolveDelivery(lease.Record);
        Assert.AreEqual(StructuredProcessingProductManifestV1.CurrentSchemaVersion, delivery.SchemaVersion);
        Assert.AreEqual(manifest.Descriptor.Artifact.ArtifactId, delivery.ArtifactId);
        Assert.AreEqual(manifest.Descriptor.ByteLength, delivery.ByteLength);
        Assert.AreEqual(manifest.IdempotencyKey, delivery.IdempotencyKey);
        var holds = await restarted.GetRetentionHoldsAsync(
            root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, holds);
        Assert.AreEqual("derived/scene.manifest.json", holds[0].RelativeSidecarPath.Replace('\\', '/'));
    }

    [TestMethod]
    public async Task StructuredProduct_CorruptQuarantinedManifestRetainsStructuredSidecar()
    {
        using var root = new TemporaryRoot();
        var manifest = CreateStructuredManifest(root.Path, "derived/corrupt-scene.json");
        using var outbox = new SqliteArtifactOutbox();
        await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE artifact_outbox_records SET manifest_bytes = x'7B', status = 'quarantined';";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var holds = await outbox.GetRetentionHoldsAsync(root.Path, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(1, holds);
        Assert.AreEqual("derived/corrupt-scene.manifest.json", holds[0].RelativeSidecarPath.Replace('\\', '/'));
        var record = await outbox.ReadAsync(root.Path, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(record);
        Assert.IsNull(record.ProductManifest);
    }

    [TestMethod]
    public async Task StructuredProduct_RequiresMatchingDurableTypedSidecar()
    {
        using var root = new TemporaryRoot();
        var manifest = CreateStructuredManifest(root.Path, "derived/sidecar-scene.json");
        var sidecarPath = Path.ChangeExtension(
            Path.Combine(root.Path, manifest.RelativeArtifactPath), ".manifest.json");
        File.Delete(sidecarPath);
        using var outbox = new SqliteArtifactOutbox();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        manifest = CreateStructuredManifest(root.Path, "derived/mismatched-sidecar-scene.json");
        var mismatch = manifest with
        {
            Descriptor = manifest.Descriptor with { ContentIdentitySha256 = new string('B', 64) }
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await outbox.EnqueueAsync(root.Path, mismatch, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        manifest = CreateStructuredManifest(root.Path, "derived/replay-sidecar-scene.json");
        await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        var lease = await outbox.ClaimAsync(
            root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        await outbox.QuarantineAsync(
            root.Path, lease, "test-quarantine", CancellationToken.None).ConfigureAwait(false);
        File.Delete(Path.ChangeExtension(
            Path.Combine(root.Path, manifest.RelativeArtifactPath), ".manifest.json"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await outbox.ReplayAsync(
                root.Path, manifest.IdempotencyKey, "operator", "retry", CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RetryAndAcknowledgement_PersistAttemptDeadlineAndFenceStaleLeaseAcrossReopen()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(StartUtc);
        var manifest = CreateManifest(root.Path, "frames/retry.bin", StartUtc, 1);
        ArtifactOutboxLease firstLease;
        using (var outbox = new SqliteArtifactOutbox(clock))
        {
            await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
            var claimed = await outbox.ClaimAsync(
                root.Path, "worker-a", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(claimed);
            firstLease = claimed;
            Assert.AreEqual(1, firstLease.Record.AttemptCount);
            await outbox.RetryAsync(
                root.Path, firstLease, StartUtc.AddMinutes(5), "central-unavailable", CancellationToken.None)
                .ConfigureAwait(false);
        }

        using var restarted = new SqliteArtifactOutbox(clock);
        Assert.IsNull(await restarted.ClaimAsync(
            root.Path, "worker-b", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false));
        var persisted = await restarted.ReadAsync(root.Path, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(ArtifactOutboxStatus.Retry, persisted.Status);
        Assert.AreEqual(1, persisted.AttemptCount);
        Assert.AreEqual(StartUtc.AddMinutes(5), persisted.NextAttemptUtc);

        clock.Advance(TimeSpan.FromMinutes(5));
        var secondLease = await restarted.ClaimAsync(
            root.Path, "worker-b", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(secondLease);
        Assert.AreEqual(2, secondLease.Record.AttemptCount);
        await Assert.ThrowsExactlyAsync<ArtifactOutboxLeaseLostException>(async () =>
            await restarted.QuarantineAsync(
                root.Path, firstLease, "stale-worker", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var delivered = ArtifactUploadClient.ResolveDelivery(secondLease.Record);
        Assert.AreEqual(ArtifactManifestV2.CurrentSchemaVersion, delivered.SchemaVersion);
        Assert.IsTrue(secondLease.Record.ManifestBytes.Span.SequenceEqual(CaptureContractJson.Serialize(manifest)));
        var queriedArtifactIds = new HashSet<Guid> { delivered.ArtifactId, Guid.NewGuid() };
        Assert.IsEmpty(await restarted.GetAcknowledgedArtifactIdsAsync(
            root.Path, queriedArtifactIds, CancellationToken.None).ConfigureAwait(false));
        var acknowledgement = new ArtifactUploadAcknowledgement(
            ArtifactUploadAcknowledgement.CurrentSchemaVersion,
            delivered.IdempotencyKey,
            delivered.ArtifactId,
            delivered.ChecksumSha256,
            delivered.ByteLength,
            StartUtc,
            delivered.SchemaVersion);
        await restarted.AcknowledgeAsync(
            root.Path, secondLease, acknowledgement, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(
            new[] { delivered.ArtifactId },
            (await restarted.GetAcknowledgedArtifactIdsAsync(
                root.Path, queriedArtifactIds, CancellationToken.None).ConfigureAwait(false)).ToArray());
        await restarted.AcknowledgeAsync(
            root.Path, secondLease, acknowledgement, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArtifactOutboxConflictException>(async () =>
            await restarted.AcknowledgeAsync(
                root.Path, secondLease, acknowledgement with { ByteLength = acknowledgement.ByteLength + 1 }, CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);

        var snapshot = await restarted.GetSnapshotAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, snapshot.AcknowledgedCount);
        Assert.AreEqual(0, snapshot.HeldCount);
        Assert.IsEmpty(await restarted.GetRetentionHoldsAsync(root.Path, CancellationToken.None).ConfigureAwait(false));
        await Phase14ScenarioEvidence.RecordAsync(
            "outbox-retry-commit",
            "retry-deadline-and-reopen",
            null,
            ["retry-deadline-persisted", "retry-attempt-incremented", "stale-lease-fenced"])
            .ConfigureAwait(false);
        await Phase14ScenarioEvidence.RecordAsync(
            "outbox-acknowledgement-commit",
            "acknowledgement-idempotency-and-conflict",
            null,
            ["acknowledgement-idempotent", "acknowledgement-conflict-rejected", "retention-hold-released"])
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ExpiredLease_IsReclaimedInCreatedOrderAndOldOwnerIsFenced()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(StartUtc);
        using var outbox = new SqliteArtifactOutbox(clock);
        var newer = CreateManifest(root.Path, "frames/newer.bin", StartUtc.AddMinutes(1), 2);
        var older = CreateManifest(root.Path, "frames/older.bin", StartUtc, 1);
        await outbox.EnqueueAsync(root.Path, newer, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(root.Path, older, CancellationToken.None).ConfigureAwait(false);

        var first = await outbox.ClaimAsync(
            root.Path, "owner-a", TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.AreEqual(older.IdempotencyKey, first.Record.IdempotencyKey);
        clock.Advance(TimeSpan.FromSeconds(31));
        var replacement = await outbox.ClaimAsync(
            root.Path, "owner-b", TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(replacement);
        Assert.AreEqual(older.IdempotencyKey, replacement.Record.IdempotencyKey);
        Assert.AreEqual(2, replacement.Record.AttemptCount);
        await Assert.ThrowsExactlyAsync<ArtifactOutboxLeaseLostException>(async () =>
            await outbox.RetryAsync(
                root.Path, first, clock.GetUtcNow(), "late-result", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Phase14ScenarioEvidence.RecordAsync(
            "outbox-claim-commit",
            "expired-lease-reclaim",
            null,
            ["oldest-record-claimed-first", "expired-lease-reclaimed", "attempt-count-incremented", "old-owner-fenced"])
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task QuarantineReplayAndAbandon_AreAuditedAndReleaseHoldOnlyAtTerminalResolution()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(StartUtc);
        using var outbox = new SqliteArtifactOutbox(clock);
        var manifest = CreateManifest(root.Path, "frames/quarantine.bin", StartUtc, 1);
        await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        var first = await outbox.ClaimAsync(
            root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(first);
        await outbox.QuarantineAsync(
            root.Path, first, "invalid-acknowledgement", CancellationToken.None).ConfigureAwait(false);

        var held = await outbox.GetRetentionHoldsAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, held);
        Assert.AreEqual(ArtifactOutboxStatus.Quarantined, held[0].Status);
        await outbox.ReplayAsync(
            root.Path, manifest.IdempotencyKey, "operator-a", "central-fixed", CancellationToken.None).ConfigureAwait(false);
        var replayed = await outbox.ClaimAsync(
            root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(replayed);
        await outbox.QuarantineAsync(
            root.Path, replayed, "still-invalid", CancellationToken.None).ConfigureAwait(false);
        await outbox.AbandonAsync(
            root.Path, manifest.IdempotencyKey, "operator-b", "approved-disposition", CancellationToken.None).ConfigureAwait(false);

        var record = await outbox.ReadAsync(root.Path, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(record);
        Assert.AreEqual(ArtifactOutboxStatus.Abandoned, record.Status);
        Assert.IsEmpty(await outbox.GetRetentionHoldsAsync(root.Path, CancellationToken.None).ConfigureAwait(false));
        var audit = await outbox.ReadAuditAsync(root.Path, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            ResolutionActions,
            audit.Select(static entry => entry.Action).ToArray());
        Assert.AreEqual("operator-b", audit[^1].Actor);
        Assert.AreEqual("approved-disposition", audit[^1].Reason);
        await Phase14ScenarioEvidence.RecordAsync(
            "outbox-quarantine-commit",
            "quarantine-replay-abandon",
            null,
            ["quarantine-retained-hold", "replay-audited", "terminal-abandon-released-hold", "resolution-history-preserved"])
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OperationsProjection_UsesBoundedKeysetQueriesWithoutReadingEvidenceBlobs()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc));
        var manifests = Enumerable.Range(0, 3)
            .Select(index => CreateManifest(root.Path, $"frames/page-{index}.bin", StartUtc.AddMinutes(index), index + 1))
            .ToArray();
        foreach (var manifest in manifests)
        {
            await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        }
        foreach (var index in Enumerable.Range(0, 55))
        {
            var pending = CreateManifest(
                root.Path, $"frames/newer-pending-{index}.bin", StartUtc.AddHours(1).AddMinutes(index), index + 10);
            await outbox.EnqueueAsync(root.Path, pending, CancellationToken.None).ConfigureAwait(false);
        }
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE artifact_outbox_records SET status = 'quarantined', last_reason = 'test-quarantine' WHERE idempotency_key IN ($key0, $key1, $key2); UPDATE artifact_outbox_records SET manifest_bytes = zeroblob(1048576), media_type = printf('%.*c', 1000, 'm') WHERE idempotency_key = $key0;";
            command.Parameters.AddWithValue("$key0", manifests[0].IdempotencyKey);
            command.Parameters.AddWithValue("$key1", manifests[1].IdempotencyKey);
            command.Parameters.AddWithValue("$key2", manifests[2].IdempotencyKey);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var first = await outbox.ReadOperationsPageAsync(root.Path, 2, null, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(2, first.Items);
        Assert.IsNotNull(first.NextCursor);
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE artifact_outbox_records SET updated_unix_ms = updated_unix_ms + 100000 WHERE idempotency_key = $key;";
            command.Parameters.AddWithValue("$key", first.Items[0].RecordKey);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        var second = await outbox.ReadOperationsPageAsync(
            root.Path, 2, first.NextCursor, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, second.Items);
        Assert.IsNull(second.NextCursor);
        Assert.AreEqual(3, first.Items.Concat(second.Items).Select(static item => item.RecordKey).Distinct().Count());
        var detail = await outbox.ReadOperationsDetailAsync(
            root.Path, manifests[0].IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(detail);
        Assert.HasCount(128, detail.MediaType!);
        Assert.AreEqual(FrameArtifactRole.Raw, detail.Role);
    }

    [TestMethod]
    public async Task OperationsProjection_PagesBeyondFiftyQuarantinedRecordsAsync()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc));
        foreach (var index in Enumerable.Range(0, 55))
        {
            await outbox.EnqueueAsync(
                root.Path,
                CreateManifest(root.Path, $"frames/quarantine-{index}.bin", StartUtc.AddMinutes(index), index + 1),
                CancellationToken.None).ConfigureAwait(false);
        }
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE artifact_outbox_records SET status = 'quarantined', last_reason = 'test-quarantine';";
            Assert.AreEqual(55, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        var first = await outbox.ReadOperationsPageAsync(
            root.Path, 50, null, CancellationToken.None).ConfigureAwait(false);
        var second = await outbox.ReadOperationsPageAsync(
            root.Path, 50, first.NextCursor, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(50, first.Items);
        Assert.IsNotNull(first.NextCursor);
        Assert.HasCount(5, second.Items);
        Assert.IsNull(second.NextCursor);
        Assert.AreEqual(55, first.Items.Concat(second.Items).Select(static item => item.RecordKey).Distinct().Count());
    }

    [TestMethod]
    public async Task OperationsResolution_IsDurablyIdempotentAndReplayRevalidatesChecksum()
    {
        using var root = new TemporaryRoot();
        var manifest = CreateManifest(root.Path, "frames/operation.bin", StartUtc, 1);
        const string operationKey = "artifact-operation-1";
        using (var outbox = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc)))
        {
            await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
            var lease = await outbox.ClaimAsync(
                root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await outbox.QuarantineAsync(root.Path, lease, "upstream-rejected", CancellationToken.None).ConfigureAwait(false);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                outbox.ResolveOperationsAsync(
                    root.Path, manifest.IdempotencyKey, OutboxOperationAction.Replay, operationKey,
                    "owner", "upstream-recovered", CancellationToken.None).AsTask())).ConfigureAwait(false);
            Assert.AreEqual(1, concurrent.Count(static result => result == OutboxOperationDisposition.Applied));
            Assert.AreEqual(7, concurrent.Count(static result => result == OutboxOperationDisposition.Duplicate));
        }

        using (var restarted = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc)))
        {
            Assert.AreEqual(
                OutboxOperationDisposition.Duplicate,
                await restarted.ResolveOperationsAsync(
                    root.Path, manifest.IdempotencyKey, OutboxOperationAction.Replay, operationKey,
                    "owner", "upstream-recovered", CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<OutboxOperationCollisionException>(async () =>
                await restarted.ResolveOperationsAsync(
                    root.Path, manifest.IdempotencyKey, OutboxOperationAction.Replay, operationKey,
                    "owner", "configuration-corrected", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            var lease = await restarted.ClaimAsync(
                root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await restarted.QuarantineAsync(root.Path, lease, "still-invalid", CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                Path.Combine(root.Path, manifest.RelativeArtifactPath.Replace('/', Path.DirectorySeparatorChar)),
                [9, 9, 9, 9]).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await restarted.ResolveOperationsAsync(
                    root.Path, manifest.IdempotencyKey, OutboxOperationAction.Replay, "artifact-operation-2",
                    "owner", "evidence-restored", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var detail = await restarted.ReadOperationsDetailAsync(
                root.Path, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            Assert.AreEqual(ArtifactOutboxStatus.Quarantined, detail.Status);
            var audit = await restarted.ReadOperationsAuditAsync(
                root.Path, manifest.IdempotencyKey, 10, null, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(audit.Items.Any(static item => item is
            { Action: "replay", ActorKind: "owner", ReasonCode: "upstream-recovered" }));
        }
    }

    [TestMethod]
    public async Task OperationReceiptsAreTransactionallyCappedAndRecentReplaySurvivesAsync()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(StartUtc.AddDays(31));
        using var outbox = new SqliteArtifactOutbox(clock);
        var manifest = CreateManifest(root.Path, "frames/receipt-cap.bin", StartUtc, 1);
        await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        var lease = await outbox.ClaimAsync(
            root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        await outbox.QuarantineAsync(root.Path, lease!, "upstream-rejected", CancellationToken.None).ConfigureAwait(false);

        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                WITH RECURSIVE values_to_seed(value) AS (
                    SELECT 0 UNION ALL SELECT value + 1 FROM values_to_seed WHERE value < $maximum)
                INSERT INTO artifact_outbox_operations(
                    operation_key, idempotency_key, action, actor_kind, reason, occurred_unix_ms)
                SELECT printf('seed-%05d', value), $record, 'replay', 'owner', 'upstream-recovered',
                       CASE WHEN value = 0 THEN $start ELSE $recent + value END
                FROM values_to_seed;
                INSERT INTO artifact_outbox_audit(
                    idempotency_key, action, actor, reason, occurred_unix_ms, operation_key)
                SELECT idempotency_key, action, actor_kind, reason, occurred_unix_ms, operation_key
                FROM artifact_outbox_operations;
                """;
            seed.Parameters.AddWithValue("$maximum", SqliteArtifactOutbox.MaximumOperationReceipts);
            seed.Parameters.AddWithValue("$record", manifest.IdempotencyKey);
            seed.Parameters.AddWithValue("$start", StartUtc.ToUnixTimeMilliseconds());
            seed.Parameters.AddWithValue("$recent", StartUtc.AddDays(30).ToUnixTimeMilliseconds());
            await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        Assert.AreEqual(OutboxOperationDisposition.Applied, await outbox.ResolveOperationsAsync(
            root.Path, manifest.IdempotencyKey, OutboxOperationAction.Replay, "newest-operation",
            "owner", "upstream-recovered", CancellationToken.None).ConfigureAwait(false));

        using var verify = OpenDatabase(root.Path);
        await verify.OpenAsync().ConfigureAwait(false);
        using var counts = verify.CreateCommand();
        counts.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM artifact_outbox_operations),
                (SELECT COUNT(*) FROM artifact_outbox_audit WHERE operation_key IS NOT NULL),
                (SELECT COUNT(*) FROM artifact_outbox_operations WHERE operation_key = 'seed-00000');
            """;
        using var reader = await counts.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        Assert.AreEqual(SqliteArtifactOutbox.MaximumOperationReceipts, reader.GetInt32(0));
        Assert.AreEqual(SqliteArtifactOutbox.MaximumOperationReceipts, reader.GetInt32(1));
        Assert.AreEqual(0, reader.GetInt32(2));
        await reader.DisposeAsync().ConfigureAwait(false);
        Assert.AreEqual(OutboxOperationDisposition.Duplicate, await outbox.ResolveOperationsAsync(
            root.Path, manifest.IdempotencyKey, OutboxOperationAction.Replay,
            $"seed-{SqliteArtifactOutbox.MaximumOperationReceipts:D5}",
            "owner", "upstream-recovered", CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("schema-1")]
    [DataRow("schema-2-old")]
    [DataRow("version-zero")]
    [DataRow("legacy-row")]
    public async Task Initialize_UnsupportedPopulatedStateIsRejectedWithoutMutation(string state)
    {
        using var root = new TemporaryRoot();
        await SeedUnsupportedDatabaseAsync(root.Path, state).ConfigureAwait(false);
        var beforeDatabase = await SnapshotUnsupportedDatabaseAsync(root.Path).ConfigureAwait(false);
        var before = SnapshotOutboxFiles(root.Path);

        using var outbox = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc));
        Exception? failure = null;
        try
        {
            await outbox.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            failure = exception;
        }

        Assert.IsNotNull(failure);
        CollectionAssert.AreEqual(before, SnapshotOutboxFiles(root.Path));
        CollectionAssert.AreEqual(
            beforeDatabase,
            await SnapshotUnsupportedDatabaseAsync(root.Path).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Initialize_FreshDatabaseHasExactCurrentSchemaAndNoLegacyEvidenceColumn()
    {
        using var root = new TemporaryRoot();
        using var outbox = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc));

        await outbox.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false);

        using var connection = OpenDatabase(root.Path);
        await connection.OpenAsync().ConfigureAwait(false);
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(artifact_outbox_records);";
            var observed = new Dictionary<string, int>(StringComparer.Ordinal);
            using var reader = await columns.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                observed.Add(reader.GetString(1), reader.GetInt32(3));
            }
            Assert.IsFalse(observed.ContainsKey("legacy_evidence_path"));
            foreach (var required in new[]
                     {
                         "idempotency_key", "manifest_kind", "manifest_bytes", "artifact_id", "role",
                         "relative_artifact_path", "payload_sha256", "payload_length", "media_type", "status"
                     })
            {
                Assert.AreEqual(1, observed[required], required);
            }
        }
        using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY name;";
            var observed = new List<string>();
            using var reader = await indexes.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) observed.Add(reader.GetString(0));
            CollectionAssert.AreEqual(CanonicalIndexes, observed.ToArray());
        }
    }

    [TestMethod]
    public async Task Initialize_MalformedCurrentRowIsRejectedReadOnlyWithoutChangingRetentionEvidence()
    {
        using var root = new TemporaryRoot();
        var manifest = CreateManifest(root.Path, "frames/retained.bin", StartUtc, 1);
        using (var initialized = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc)))
        {
            await initialized.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        }
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE artifact_outbox_records SET relative_artifact_path = 'frames/different.bin';";
            Assert.AreEqual(1, await corrupt.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        var beforeDatabase = await SnapshotCurrentDatabaseAsync(root.Path).ConfigureAwait(false);
        var before = SnapshotOutboxFiles(root.Path);

        using var restarted = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        CollectionAssert.AreEqual(before, SnapshotOutboxFiles(root.Path));
        CollectionAssert.AreEqual(beforeDatabase, await SnapshotCurrentDatabaseAsync(root.Path).ConfigureAwait(false));
        Assert.IsTrue(beforeDatabase.Any(value => value.Contains("frames/different.bin", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Initialize_MalformedAcknowledgementIsRejectedReadOnlyWithoutMutation()
    {
        using var root = new TemporaryRoot();
        _ = await SeedAcknowledgedRecordAsync(root.Path).ConfigureAwait(false);
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE artifact_outbox_records SET acknowledgement = X'7B';";
            Assert.AreEqual(1, await corrupt.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await AssertAcknowledgementInitializationFailsWithoutMutationAsync(root.Path).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("acknowledgement-schema")]
    [DataRow("accepted-manifest-version")]
    [DataRow("idempotency-key")]
    [DataRow("artifact-id")]
    [DataRow("checksum")]
    [DataRow("length")]
    public async Task Initialize_MismatchedAcknowledgementBindingIsRejectedReadOnlyWithoutMutation(string binding)
    {
        using var root = new TemporaryRoot();
        var manifest = await SeedAcknowledgedRecordAsync(root.Path).ConfigureAwait(false);
        var acknowledgement = CreateAcknowledgement(manifest);
        var mismatched = binding switch
        {
            "acknowledgement-schema" => acknowledgement with { SchemaVersion = "v999" },
            "accepted-manifest-version" => acknowledgement with { AcceptedManifestSchemaVersion = "artifact-manifest-v999" },
            "idempotency-key" => acknowledgement with { IdempotencyKey = new string('B', 64) },
            "artifact-id" => acknowledgement with { ArtifactId = Guid.NewGuid() },
            "checksum" => acknowledgement with { ChecksumSha256 = new string('C', 64) },
            "length" => acknowledgement with { ByteLength = acknowledgement.ByteLength + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, "Unknown acknowledgement binding.")
        };
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE artifact_outbox_records SET acknowledgement = $acknowledgement;";
            corrupt.Parameters.AddWithValue(
                "$acknowledgement", JsonSerializer.SerializeToUtf8Bytes(mismatched));
            Assert.AreEqual(1, await corrupt.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await AssertAcknowledgementInitializationFailsWithoutMutationAsync(root.Path).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("completion-token")]
    [DataRow("acknowledgement")]
    [DataRow("acknowledged-timestamp")]
    public async Task Initialize_NonAcknowledgedCompletionEvidenceIsRejectedReadOnlyWithoutMutation(string field)
    {
        using var root = new TemporaryRoot();
        var manifest = CreateManifest(root.Path, "frames/pending.bin", StartUtc, 1);
        using (var outbox = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc)))
        {
            await outbox.EnqueueAsync(root.Path, manifest, CancellationToken.None).ConfigureAwait(false);
        }
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                PRAGMA ignore_check_constraints=ON;
                UPDATE artifact_outbox_records SET
                    completion_token = CASE WHEN $field = 'completion-token' THEN 'forbidden' ELSE completion_token END,
                    acknowledgement = CASE WHEN $field = 'acknowledgement' THEN X'7B7D' ELSE acknowledgement END,
                    acknowledged_unix_ms = CASE WHEN $field = 'acknowledged-timestamp' THEN 0 ELSE acknowledged_unix_ms END;
                """;
            corrupt.Parameters.AddWithValue("$field", field);
            Assert.AreEqual(1, await corrupt.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await AssertAcknowledgementInitializationFailsWithoutMutationAsync(root.Path).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Initialize_FreshStoreRestartAcceptsRuntimeGeneratedAcknowledgement()
    {
        using var root = new TemporaryRoot();
        var manifest = await SeedAcknowledgedRecordAsync(root.Path).ConfigureAwait(false);

        using var restarted = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc));
        await restarted.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        var record = await restarted.ReadAsync(
            root.Path, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(record);
        Assert.AreEqual(ArtifactOutboxStatus.Acknowledged, record.Status);
        Assert.IsTrue(record.Acknowledgement.HasValue);
        Assert.IsTrue(record.Acknowledgement.Value.Span.SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(CreateAcknowledgement(manifest))));
    }

    [TestMethod]
    public async Task Claim_MalformedCommittedRecordIsQuarantinedWithoutBlockingFollower()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(StartUtc);
        using var outbox = new SqliteArtifactOutbox(clock);
        var malformed = CreateManifest(root.Path, "frames/malformed.bin", StartUtc, 1);
        var follower = CreateManifest(root.Path, "frames/follower.bin", StartUtc.AddSeconds(1), 2);
        await outbox.EnqueueAsync(root.Path, malformed, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(root.Path, follower, CancellationToken.None).ConfigureAwait(false);
        using (var connection = OpenDatabase(root.Path))
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE artifact_outbox_records SET manifest_bytes = X'7B' WHERE idempotency_key = $key;";
            command.Parameters.AddWithValue("$key", malformed.IdempotencyKey);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false));
        }

        Assert.IsNull(await outbox.ClaimAsync(
            root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));
        var quarantined = await outbox.ReadAsync(root.Path, malformed.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(quarantined);
        Assert.AreEqual(ArtifactOutboxStatus.Quarantined, quarantined.Status);
        Assert.AreEqual("malformed-committed-record", quarantined.LastReason);
        var claimedFollower = await outbox.ClaimAsync(
            root.Path, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(claimedFollower);
        Assert.AreEqual(follower.IdempotencyKey, claimedFollower.Record.IdempotencyKey);
    }

    private static ArtifactManifestV2 CreateManifest(
        string root,
        string relativePath,
        DateTimeOffset createdUtc,
        long captureSequence)
    {
        WritePayload(root, relativePath);
        var hash = Convert.ToHexString(SHA256.HashData(Payload));
        var profile = new ProfileIdentityDescriptor("fixture", "1.0.0", new string('A', 64));
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor("agent-a", "rig-a", captureSequence, Guid.NewGuid()),
            new CaptureTimingDescriptor(
                createdUtc.AddSeconds(-5), createdUtc.AddSeconds(-4), createdUtc.AddSeconds(-3),
                createdUtc.AddSeconds(-2), createdUtc.AddSeconds(-1)),
            new CaptureControlDescriptor(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 10, 10, null, null, null, null),
            new CaptureProfileSet(profile, profile, profile, profile, profile),
            new FrameLayoutDescriptor(
                2, 2, 2, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, Payload.LongLength),
            new ArtifactDescriptor(
                Guid.NewGuid(), FrameArtifactRole.Raw, "fixture", "native", createdUtc, [],
                RecipeIdentityDescriptor.Create(
                    "capture-raw", "1.0.0", "test", JsonSerializer.SerializeToElement(new { mode = "raw" })),
                "application/octet-stream", hash));
        var manifest = new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath);
        Assert.IsTrue(manifest.Validate().IsValid, manifest.Validate().ReasonCode);
        File.WriteAllBytes(
            Path.ChangeExtension(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)), ".json"),
            CaptureContractJson.Serialize(manifest));
        return manifest;
    }

    private static async Task<ArtifactManifestV2> SeedAcknowledgedRecordAsync(string root)
    {
        var manifest = CreateManifest(root, "frames/acknowledged.bin", StartUtc, 1);
        using var outbox = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc));
        await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
        var lease = await outbox.ClaimAsync(
            root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        await outbox.AcknowledgeAsync(
            root,
            lease,
            CreateAcknowledgement(manifest),
            CancellationToken.None).ConfigureAwait(false);
        return manifest;
    }

    private static ArtifactUploadAcknowledgement CreateAcknowledgement(ArtifactManifestV2 manifest)
        => new(
            ArtifactUploadAcknowledgement.CurrentSchemaVersion,
            manifest.IdempotencyKey,
            manifest.Descriptor.Artifact.ArtifactId,
            manifest.Descriptor.Artifact.ChecksumSha256,
            manifest.Descriptor.Layout.ByteLength,
            StartUtc,
            manifest.SchemaVersion);

    private static async Task AssertAcknowledgementInitializationFailsWithoutMutationAsync(string root)
    {
        var beforeDatabase = await SnapshotAcknowledgedDatabaseAsync(root).ConfigureAwait(false);
        var beforeFiles = SnapshotOutboxFiles(root);

        using var restarted = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(root, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        CollectionAssert.AreEqual(beforeFiles, SnapshotOutboxFiles(root));
        CollectionAssert.AreEqual(beforeDatabase, await SnapshotAcknowledgedDatabaseAsync(root).ConfigureAwait(false));
    }

    private static StructuredProcessingProductManifestV1 CreateStructuredManifest(string root, string relativePath)
    {
        var source = CreateManifest(root, "frames/structured-source.bin", StartUtc, 1).Descriptor;
        var payload = "{}"u8.ToArray();
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, payload);
        var sourceIds = new[] { source.Artifact.ArtifactId };
        var recipe = RecipeIdentityDescriptor.Create(
            "projected-scene", "1.0.0", "test", JsonSerializer.SerializeToElement(new { mode = "predicted" }));
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe);
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata, "projected-scene", recipeIdentity.IdentitySha256, sourceIds);
        var artifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.Metadata,
            "projected-scene-step",
            "projected-scene",
            StartUtc,
            sourceIds,
            recipe,
            "application/vnd.hvo.projected-scene+json",
            Convert.ToHexString(SHA256.HashData(payload)));
        var manifest = new StructuredProcessingProductManifestV1(
            StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(
                source,
                artifact,
                outputIdentity,
                [new("projected-scene", "test")],
                new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                TimeSpan.FromSeconds(1).Ticks,
                payload.LongLength,
                ProcessingProductKind.Metadata,
                ProjectedSceneV1.CurrentSchemaVersion,
                new string('A', 64)),
            relativePath,
            ProducerStepId: "projected-scene-step");
        var sidecar = new DurableTypedMetadataProductManifestV3(
            DurableTypedMetadataProductManifestV3.CurrentSchemaVersion,
            source.Capture,
            artifact,
            outputIdentity,
            manifest.Descriptor.Algorithms,
            manifest.Descriptor.Compatibility,
            manifest.Descriptor.TotalIntegrationTicks,
            payload.LongLength,
            relativePath,
            JsonSerializer.SerializeToElement<object?>(null),
            ProcessingProductKind.Metadata,
            ProjectedSceneV1.CurrentSchemaVersion,
            manifest.Descriptor.ContentIdentitySha256);
        File.WriteAllBytes(Path.ChangeExtension(path, ".manifest.json"),
            DurableProcessingProductManifestJson.Serialize(sidecar));
        return manifest;
    }

    private static void WritePayload(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Payload);
    }

    private static SqliteConnection OpenDatabase(string root)
        => new($"Data Source={Path.Combine(root, "outbox", "artifact-outbox.db")};Mode=ReadWrite");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command is assembled exclusively from fixed test fixture SQL selected by a DataRow value.")]
    private static async Task SeedUnsupportedDatabaseAsync(string root, string state)
    {
        var outboxDirectory = Path.Combine(root, "outbox");
        Directory.CreateDirectory(outboxDirectory);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(outboxDirectory, "artifact-outbox.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        var schema = state == "version-zero"
            ? string.Empty
            : $"CREATE TABLE artifact_outbox_schema(schema_key INTEGER PRIMARY KEY, version INTEGER NOT NULL); INSERT INTO artifact_outbox_schema VALUES(1, {(state == "schema-1" ? 1 : 2)});";
        command.CommandText = string.Concat(schema, """
            CREATE TABLE artifact_outbox_records(
                idempotency_key TEXT, manifest_kind TEXT, manifest_bytes BLOB, artifact_id TEXT,
                role TEXT, relative_artifact_path TEXT, payload_sha256 TEXT, payload_length INTEGER,
                media_type TEXT, legacy_evidence_path TEXT);
            CREATE TABLE artifact_outbox_audit(id INTEGER);
            CREATE TABLE artifact_outbox_conflicts(id INTEGER);
            CREATE TABLE artifact_outbox_operations(id INTEGER);
            """, state == "legacy-row"
                ? "INSERT INTO artifact_outbox_records VALUES('legacy-key','legacy-v1',x'7B7D','00000000000000000000000000000001','Raw','frames/legacy.bin','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',1,'application/octet-stream','outbox/legacy.json');"
                : "INSERT INTO artifact_outbox_records VALUES('unsupported','v2',x'7B7D','00000000000000000000000000000001','Raw','frames/raw.bin','AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',1,'application/octet-stream',NULL);");
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string[] SnapshotOutboxFiles(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "outbox"))
            .Order(StringComparer.Ordinal)
            .Select(path => string.Concat(
                Path.GetFileName(path), ":", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .ToArray();

    private static async Task<string[]> SnapshotUnsupportedDatabaseAsync(string root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "outbox", "artifact-outbox.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        var values = new List<string>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT type || ':' || name || ':' || COALESCE(sql, '') FROM sqlite_master ORDER BY type, name;";
            using var reader = await schema.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) values.Add(reader.GetString(0));
        }
        using (var rows = connection.CreateCommand())
        {
            rows.CommandText = "SELECT quote(idempotency_key) || ':' || quote(manifest_kind) || ':' || hex(manifest_bytes) || ':' || quote(relative_artifact_path) || ':' || quote(legacy_evidence_path) FROM artifact_outbox_records ORDER BY idempotency_key;";
            using var reader = await rows.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) values.Add(reader.GetString(0));
        }
        return values.ToArray();
    }

    private static async Task<string[]> SnapshotCurrentDatabaseAsync(string root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "outbox", "artifact-outbox.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idempotency_key || ':' || relative_artifact_path || ':' || payload_sha256 || ':' || payload_length || ':' || status || ':' || hex(manifest_bytes) FROM artifact_outbox_records ORDER BY record_id;";
        var values = new List<string>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false)) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static async Task<string[]> SnapshotAcknowledgedDatabaseAsync(string root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "outbox", "artifact-outbox.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        var values = new List<string>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT type || ':' || name || ':' || COALESCE(sql, '') FROM sqlite_master ORDER BY type, name;";
            using var reader = await schema.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) values.Add(reader.GetString(0));
        }
        using (var rows = connection.CreateCommand())
        {
            rows.CommandText = "SELECT record_id || ':' || idempotency_key || ':' || manifest_kind || ':' || hex(manifest_bytes) || ':' || artifact_id || ':' || relative_artifact_path || ':' || payload_sha256 || ':' || payload_length || ':' || status || ':' || quote(completion_token) || ':' || hex(acknowledgement) || ':' || quote(acknowledged_unix_ms) FROM artifact_outbox_records ORDER BY record_id;";
            using var reader = await rows.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) values.Add(reader.GetString(0));
        }
        return values.ToArray();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "skymonitor-sqlite-outbox", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
