using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    public async Task QualifiedProtectedReadNeverTraversesRetainedEvidenceAndConcurrentReadsAgree()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        store.BeforeRetainedManifestValidationForTest = () => Assert.Fail("Protected reads must not scan.");
        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false))).ConfigureAwait(false);
        Assert.IsTrue(reads.All(result => result == active));
        var changed = seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } };
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(changed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, store.Active, "Fast reads must not commit configuration edits.");
    }

    [TestMethod]
    [DataRow("replace")]
    [DataRow("delete")]
    [DataRow("add")]
    public async Task ApprovedOutOfBandBoundaryDetectsDriftAtFullScrub(string mutation)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        // Valid JSON/contract changes are drift too; not only parser errors.
        if (mutation == "replace") await File.WriteAllBytesAsync(path, [.. bytes, (byte)' ']).ConfigureAwait(false);
        else if (mutation == "delete") File.Delete(path);
        else await File.WriteAllBytesAsync(path + ".extra.json", bytes).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false),
            "Roy approved delayed out-of-band detection, explicitly exercised here.");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false),
            "Recovery/startup must remain available without replacing expected evidence.");
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ManagedPublishCopyMoveRetentionAndTreeMoveMaintainTheCheckpoint()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var copy = path + ".copy.json";
        RetainedFileMutations.Copy(path, copy);
        await RetainedFileMutations.WriteAllBytesAsync(copy, [.. bytes, (byte)' ']).ConfigureAwait(false);
        var moved = path + ".moved.json";
        RetainedFileMutations.Move(copy, moved);
        RetainedFileMutations.Delete(moved);
        var sourceDirectory = Path.GetDirectoryName(path)!;
        var destination = Path.Combine(_options.Value.RawIngressRoot, "quarantine", "managed");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        RetainedFileMutations.MoveDirectory(sourceDirectory, destination);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        RetainedFileMutations.DeleteDirectory(destination, recursive: true);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("entry-delete")]
    [DataRow("entry-replay")]
    [DataRow("checkpoint")]
    public async Task LedgerTamperCannotCertifyAFullScrub(string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var state = Path.Combine(_options.Value.RawIngressRoot, ".location");
        if (fault == "checkpoint")
        {
            var checkpoint = Path.Combine(state, RetainedIntegrityLedger.CheckpointName);
            var protectedBytes = await File.ReadAllBytesAsync(checkpoint).ConfigureAwait(false);
            protectedBytes[^1] ^= 1;
            await File.WriteAllBytesAsync(checkpoint, protectedBytes).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
            return;
        }
        using var connection = new SqliteConnection($"Data Source={Path.Combine(state, RetainedIntegrityLedger.DatabaseName)};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE replay AS SELECT * FROM entries;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await RetainedFileMutations.WriteAllBytesAsync(path, [.. bytes, (byte)' ']).ConfigureAwait(false);
        if (fault == "entry-delete") command.CommandText = "DELETE FROM entries;";
        else command.CommandText = "DELETE FROM entries; INSERT INTO entries SELECT * FROM replay;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("restart")]
    [DataRow("restore")]
    [DataRow("rollback")]
    public async Task NoNewProcessInheritsCheckpointTrust(string scenario)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var first = CreateStore();
        var active = await first.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        first.Dispose();
        var snapshot = Directory.GetFiles(_options.Value.RawIngressRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_options.Value.RawIngressRoot, path), File.ReadAllBytes, StringComparer.Ordinal);
        first.Dispose();
        if (scenario == "restore")
        {
            Directory.Delete(_options.Value.RawIngressRoot, recursive: true);
            foreach (var (relative, bytes) in snapshot)
            {
                var path = Path.Combine(_options.Value.RawIngressRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            }
        }
        if (scenario == "rollback")
        {
            // Simulate an old validator's last checkpoint. A new epoch must scrub regardless of it.
            var checkpoint = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.CheckpointName);
            var json = System.Text.Json.Nodes.JsonNode.Parse(_protector.Unprotect(await File.ReadAllBytesAsync(checkpoint).ConfigureAwait(false)))!;
            json["Binding"] = Convert.ToHexString(SHA256.HashData("old-validator"u8));
            await File.WriteAllBytesAsync(checkpoint, _protector.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json))).ConfigureAwait(false);
        }
        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var scanned = 0;
        restarted.BeforeRetainedManifestValidationForTest = () => scanned++;
        Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(2, scanned);
        Assert.AreEqual(active, await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The scrub is released in finally and all tasks finish before store/fixture disposal.")]
    public async Task ScrubMergesConcurrentManagedChangesAndProtectedWaitIsFinite()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        store.BeforeRetainedManifestValidationForTest = () =>
        {
            if (Interlocked.Increment(ref first) != 1) return;
            entered.TrySetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var scrub = Task.Run(async () => await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Task? writer = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            writer = Task.Run(async () =>
                await RetainedFileMutations.WriteAllBytesAsync(path, [.. bytes, (byte)' ']).ConfigureAwait(false));
        }
        finally { release.Set(); }
        _ = await scrub.ConfigureAwait(false);
        if (writer is not null) await writer.ConfigureAwait(false);
        store.BeforeRetainedManifestValidationForTest = null;
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(active, await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DailyScrubDeadlineCannotBeExtendedByManagedWrites()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddHours(23);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        _timeProvider.UtcNow = Now.AddHours(24);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task IncompleteRestoreAndUnmanagedDeletionCannotBeHiddenByManagedWriters()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (path, _) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        File.Delete(path);
        Assert.ThrowsExactly<InvalidDataException>(() => RetainedFileMutations.Delete(path));
        store.Dispose();
        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("truncated")]
    [DataRow("metadata")]
    [DataRow("pending")]
    public async Task DamagedRequiredLedgerLeavesRecoveryAvailableButCannotQualify(string fault)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        store.Dispose();
        var path = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        if (fault == "missing") File.Delete(path);
        else if (fault == "truncated") await File.WriteAllBytesAsync(path, [1, 2, 3]).ConfigureAwait(false);
        else
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            if (fault == "metadata") command.CommandText = "UPDATE metadata SET payload=zeroblob(64);";
            else command.CommandText = "INSERT INTO pending VALUES('F|forged.json',0,1,NULL,NULL,0,zeroblob(64));";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var restarted = CreateStore();
        Assert.AreEqual(active, await restarted.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReplacedLedgerPathCannotBeHiddenByAnAlreadyOpenSqliteHandle()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (manifestPath, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var path = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        var replacement = path + ".replacement";
        File.Copy(path, replacement);
        using (var connection = new SqliteConnection($"Data Source={replacement};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE metadata SET payload=zeroblob(64);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        File.Move(replacement, path, overwrite: true);
        var refused = false;
        try { _ = await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is CryptographicException or IOException or SqliteException) { refused = true; }
        Assert.IsTrue(refused, "A full scrub must inspect the durable path, not trust an old open inode.");
        Assert.AreEqual(active, await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "The pre-registration write lease is released in finally and initialization is awaited before fixture disposal.")]
    public async Task InitialQualificationIncludesManagedWritesAlreadyInFlightBeforeRegistration(bool capture)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        var root = _options.Value.RawIngressRoot;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "owner-state.json");
        var bytes = "{}"u8.ToArray();
        if (capture)
        {
            var location = DeploymentLocationSnapshot.Create(seed.LocationId, 1, seed.Source, null, Now, null,
                35, -113, 0, "America/Phoenix");
            var original = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
            bytes = CaptureContractJson.Serialize(original with
            {
                Descriptor = original.Descriptor with
                {
                    Location = location.ToProvenance(),
                    Timing = new CaptureTimingDescriptor(Now, Now.AddSeconds(1), Now.AddSeconds(2), Now.AddSeconds(3), Now.AddSeconds(4)),
                    Artifact = original.Descriptor.Artifact with { CreatedUtc = Now.AddSeconds(4) }
                }
            });
            Assert.IsTrue(CaptureContractJson.ParseManifest(bytes).IsValid);
        }
        var mutation = RetainedIntegrityLedger.BeginWrite(path, bytes);
        using var store = CreateStore();
        Task<DeploymentLocationSnapshot> initialize;
        using (ExecutionContext.SuppressFlow())
            initialize = Task.Run(async () => await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(40)).ConfigureAwait(false);
            Assert.IsFalse(initialize.IsCompleted, "Root registration must wait for an earlier untracked write.");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        }
        finally { mutation.Dispose(); }
        if (capture)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await initialize.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsNull(store.Active);
            Assert.IsFalse(File.Exists(Path.Combine(root, ".location", "deployment-location.v1.protected")),
                "Configuration cannot become first immutable identity for previously published capture evidence.");
            return;
        }
        var active = await initialize.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.AreEqual(active, await store.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        await File.WriteAllTextAsync(path, "{\"changed\":true}").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StaleBackgroundConfigurationCannotCommitNewLocationAuthority()
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var store = CreateStore();
        var active = await store.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _timeProvider.UtcNow = Now.AddMinutes(1);
        var changed = seed with { Coordinates = seed.Coordinates with { LatitudeDegrees = 36 } };
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            await store.LoadCurrentAsync(changed, false, () => false, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(active, store.Active);
        store.Dispose();
        using var restarted = CreateStore();
        Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }
}
