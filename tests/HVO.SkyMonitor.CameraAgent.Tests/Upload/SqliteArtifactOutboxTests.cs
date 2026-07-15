using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Upload;

[TestClass]
[TestCategory("Integration")]
public sealed class SqliteArtifactOutboxTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Payload = [1, 2, 3, 4];
    private static readonly string[] ResolutionActions = ["quarantine", "replay", "quarantine", "abandon"];

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
    }

    [TestMethod]
    public async Task Initialize_ImportsValidLegacyAndQuarantinesMalformedWithoutChangingEitherFile()
    {
        using var root = new TemporaryRoot();
        var legacy = CreateLegacyManifest();
        var fileOutbox = new FileSystemArtifactOutbox();
        await fileOutbox.EnqueueAsync(root.Path, legacy, CancellationToken.None).ConfigureAwait(false);
        var validPath = Path.Combine(root.Path, "outbox", string.Concat(legacy.IdempotencyKey, ".json"));
        var validBytes = await File.ReadAllBytesAsync(validPath, CancellationToken.None).ConfigureAwait(false);
        var malformedPath = Path.Combine(root.Path, "outbox", "malformed.json");
        var malformedBytes = "{"u8.ToArray();
        await File.WriteAllBytesAsync(malformedPath, malformedBytes, CancellationToken.None).ConfigureAwait(false);

        using (var outbox = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc)))
        {
            await outbox.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
            var imported = await outbox.ReadAsync(root.Path, legacy.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(imported);
            Assert.AreEqual(ArtifactOutboxManifestKind.LegacyV1, imported.ManifestKind);
            Assert.AreEqual(ArtifactOutboxStatus.Pending, imported.Status);
            Assert.AreEqual("outbox/" + Path.GetFileName(validPath), imported.LegacyEvidencePath);
            Assert.HasCount(1, outbox.List(root.Path, 10));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                outbox.EnumeratePending(root.Path, CancellationToken.None).ToArray());
            var snapshot = await outbox.GetSnapshotAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, snapshot.HeldCount);
            Assert.AreEqual(1, snapshot.QuarantinedCount);
            Assert.HasCount(1, await outbox.GetRetentionHoldsAsync(root.Path, CancellationToken.None).ConfigureAwait(false));
        }

        using (var restarted = new SqliteArtifactOutbox(new MutableTimeProvider(StartUtc)))
        {
            await restarted.InitializeAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
            var snapshot = await restarted.GetSnapshotAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, snapshot.QuarantinedCount);
        }
        CollectionAssert.AreEqual(validBytes, await File.ReadAllBytesAsync(validPath, CancellationToken.None).ConfigureAwait(false));
        CollectionAssert.AreEqual(malformedBytes, await File.ReadAllBytesAsync(malformedPath, CancellationToken.None).ConfigureAwait(false));

        using var connection = OpenDatabase(root.Path);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_outbox_audit WHERE action = 'quarantine' AND actor = 'legacy-import';";
        Assert.AreEqual(1L, Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
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

    private static ArtifactUploadManifest CreateLegacyManifest() => new(
        ArtifactUploadManifest.CurrentSchemaVersion,
        "legacy-agent",
        Guid.NewGuid(),
        Guid.NewGuid(),
        FrameArtifactRole.Raw,
        "application/octet-stream",
        Payload.LongLength,
        Convert.ToHexString(SHA256.HashData(Payload)),
        StartUtc,
        "raw-v1",
        "frames/legacy.bin");

    private static void WritePayload(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Payload);
    }

    private static SqliteConnection OpenDatabase(string root)
        => new($"Data Source={Path.Combine(root, "outbox", "artifact-outbox.db")};Mode=ReadWrite");

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
