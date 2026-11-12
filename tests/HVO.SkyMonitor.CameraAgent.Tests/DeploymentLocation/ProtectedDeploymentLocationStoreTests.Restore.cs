extern alias InstallerRuntime;

using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using static HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.RetainedIntegrityFormat;
using SnapshotApi = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateSnapshot;
using InstallerPaths = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallationPaths;
using InstallerLayout = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateLayout;
using InstallerFiles = InstallerRuntime::HVO.SkyMonitor.Deployment.SafeFileSystem;
using InstallerNative = InstallerRuntime::HVO.SkyMonitor.Deployment.NativeLinux;
using RealInstallerException = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerException;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [DataRow("normal")]
    [DataRow("corrupt")]
    [DataRow("cancel")]
    [DataRow("incomplete-overlay")]
    [DataRow("cancel-after-ledger")]
    [DataRow("new-file-after-restore")]
    [DataRow("live-owner")]
    [DataRow("missing-ledger")]
    [DataRow("missing-ledger-with-overlay")]
    [DataRow("pending-current")]
    [DataRow("pending-target")]
    [DataRow("pending-both")]
    [DataRow("pending-corrupt")]
    [DataRow("pending-torn")]
    [DataRow("legacy-target")]
    [DataRow("rotated-current-key")]
    public async Task DatabaseRestorePreservesAuthenticatedPostSnapshotFilesAndRejectsUntrustedProofs(string fault)
    {
        var id = Guid.NewGuid();
        var paths = InstallerPaths.Create(Path.Combine(_root, "installer"), id, "synthetic");
        foreach (var directory in new[] { paths.ProductRoot, paths.InstanceRoot, paths.StateRoot, paths.OperationsRoot })
            InstallerFiles.CreateOwnerDirectory(directory);
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(paths.StateRoot, "raw"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        _protector = new DataProtectionDeploymentLocationProtector(_options);
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var first = CreateStore();
        var active = await first.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (_, captureJson) = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        first.Dispose();
        if (fault == "legacy-target") await RewriteLedgerAuthenticationAsync(legacy: true).ConfigureAwait(false);
        if (fault is "pending-target" or "pending-both")
            await AddInterruptedIntentAsync("J|1", captureJson).ConfigureAwait(false);
        var image = new ImageInstallationIdentity("registry", "synthetic@sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64), "amd64", null);
        var snapshot = await SnapshotApi.CreateAsync(paths, id, Guid.NewGuid(), image,
            InstallerLayout.SqliteStores, CancellationToken.None).ConfigureAwait(false);
        using var current = CreateStore();
        _ = await current.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var added = Path.Combine(_options.Value.RawIngressRoot, "post-snapshot.json");
        await RetainedFileMutations.WriteAllBytesAsync(added, "{\"sequence\":1}"u8.ToArray()).ConfigureAwait(false);
        Assert.AreEqual(active, await current.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        current.Dispose();
        if (fault == "rotated-current-key") await RewriteLedgerAuthenticationAsync(legacy: false).ConfigureAwait(false);
        var ledger = Path.Combine(_options.Value.RawIngressRoot, ".location", "retained-integrity.v1.db");
        if (fault is "pending-current" or "pending-both" or "pending-corrupt" or "pending-torn")
        {
            var bytes = "{\"pending\":true}"u8.ToArray();
            await AddInterruptedIntentAsync("F|pending.json", bytes).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(_options.Value.RawIngressRoot, "pending.json"),
                fault == "pending-torn" ? "{"u8.ToArray() : bytes).ConfigureAwait(false);
        }
        if (fault is "corrupt" or "pending-corrupt")
        {
            using var connection = new SqliteConnection($"Data Source={ledger};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var edit = connection.CreateCommand();
            if (fault == "corrupt") edit.CommandText = "UPDATE entries SET digest=zeroblob(32) WHERE key='F|post-snapshot.json';";
            else edit.CommandText = "DELETE FROM pending;";
            _ = await edit.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var before = await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false);
        var plan = await SnapshotApi.PlanRestoreAsync(paths, id, snapshot, [], CancellationToken.None).ConfigureAwait(false);
        if (fault is "missing-ledger" or "missing-ledger-with-overlay")
        {
            if (fault == "missing-ledger-with-overlay")
                _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                    [], plan, CancellationToken.None).ConfigureAwait(false);
            File.Delete(ledger);
            await Assert.ThrowsExactlyAsync<RealInstallerException>(async () =>
                _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                    [], plan, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false));
            Assert.IsTrue(File.Exists(added));
            return;
        }
        if (fault is "corrupt" or "pending-corrupt" or "live-owner")
        {
            using var live = fault == "live-owner" ? CreateStore() : null;
            if (live is not null) _ = await live.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<RealInstallerException>(async () =>
                _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                    [], plan, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false));
        }
        else
        {
            if (fault is "cancel" or "incomplete-overlay" or "cancel-after-ledger")
            {
                using var cancelled = new CancellationTokenSource();
                var previous = SnapshotApi.StepCompleted;
                SnapshotApi.StepCompleted = step =>
                {
                    if (step == (fault == "cancel-after-ledger"
                        ? "restore:raw/.location/retained-integrity.v1.db" : "restore-integrity-overlay-ready")) cancelled.Cancel();
                };
                try
                {
                    await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                        _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                            [], plan, cancelled.Token).ConfigureAwait(false)).ConfigureAwait(false);
                }
                finally { SnapshotApi.StepCompleted = previous; }
                CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false));
                if (fault == "incomplete-overlay")
                {
                    var seal = Path.Combine(SnapshotApi.SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId),
                        "retained-files-overlay.v1.protected");
                    await File.WriteAllBytesAsync(seal, [1, 2, 3]).ConfigureAwait(false);
                }
            }
            _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                [], plan, CancellationToken.None).ConfigureAwait(false);
            var once = await File.ReadAllBytesAsync(ledger).ConfigureAwait(false);
            if (fault == "new-file-after-restore")
            {
                using var changed = CreateStore();
                _ = await changed.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
                await RetainedFileMutations.WriteAllBytesAsync(Path.Combine(_options.Value.RawIngressRoot, "second-post-snapshot.json"),
                    "{\"sequence\":2}"u8.ToArray()).ConfigureAwait(false);
            }
            _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                [], plan, CancellationToken.None).ConfigureAwait(false);
            if (fault != "new-file-after-restore")
                CollectionAssert.AreEqual(once, await File.ReadAllBytesAsync(ledger).ConfigureAwait(false));
            using var restarted = CreateStore();
            if (fault == "pending-torn")
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    _ = await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                return;
            }
            var actualReads = 0;
            restarted.BeforeRetainedManifestValidationForTest = () => actualReads++;
            _ = await restarted.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(active, await restarted.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false));
            Assert.IsGreaterThan(0, actualReads, "A paired restore must force actual-byte qualification, even with valid cached hints.");
            Assert.AreEqual(active, await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        }
        Assert.IsTrue(File.Exists(added));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [DataRow("whole")]
    [DataRow("tampered-retained")]
    [DataRow("missing-keys")]
    public async Task CompleteArchiveStagesItsAuthenticatedLedgerWithoutPermittingDatabaseOnlyAdoption(string fault)
    {
        var id = Guid.NewGuid();
        var paths = InstallerPaths.Create(Path.Combine(_root, "archive-installer"), id, "synthetic");
        foreach (var directory in new[] { paths.ProductRoot, paths.InstanceRoot, paths.StateRoot, paths.OperationsRoot })
            InstallerFiles.CreateOwnerDirectory(directory);
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(paths.StateRoot, "raw"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        _protector = new DataProtectionDeploymentLocationProtector(_options);
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var original = CreateStore();
        var active = await original.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (retained, _) = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        original.Dispose();
        var image = new ImageInstallationIdentity("registry", "synthetic@sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64), "amd64", null);
        var snapshot = await SnapshotApi.CreateAsync(paths, id, Guid.NewGuid(), image,
            InstallerLayout.SqliteStores, CancellationToken.None).ConfigureAwait(false);
        var staged = Path.Combine(paths.ProductRoot, "verified-archive-staging");
        foreach (var file in Directory.EnumerateFiles(paths.StateRoot, "*", SearchOption.AllDirectories))
        {
            // The full archive carries the ledger through its database snapshot,
            // while retained files and keys are extracted into the staged tree.
            if (Path.GetFileName(file).StartsWith(RetainedIntegrityLedger.DatabaseName, StringComparison.Ordinal)) continue;
            var target = Path.Combine(staged, "state", Path.GetRelativePath(paths.StateRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.Delete(Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName));
        _ = await SnapshotApi.VerifyAsync(paths, id, snapshot, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<RealInstallerException>(async () =>
            _ = await SnapshotApi.PlanRestoreAsync(paths, id, snapshot, [], CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var stagedRaw = Path.Combine(staged, "state", "raw");
        var stagedLedger = Path.Combine(stagedRaw, ".location", RetainedIntegrityLedger.DatabaseName);
        if (fault == "missing-keys")
        {
            Directory.Delete(Path.Combine(stagedRaw, ".location", "keys"), recursive: true);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                SnapshotApi.StageArchiveIntegrityAsync(paths, id, snapshot, staged, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(stagedLedger));
            return;
        }
        await SnapshotApi.StageArchiveIntegrityAsync(paths, id, snapshot, staged, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(stagedLedger));
        if (fault == "tampered-retained")
            await File.WriteAllTextAsync(Path.Combine(staged, "state", Path.GetRelativePath(paths.StateRoot, retained)), "{}").ConfigureAwait(false);
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = stagedRaw,
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        _protector = new DataProtectionDeploymentLocationProtector(_options);
        using var restored = CreateStore();
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restored.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        if (fault == "tampered-retained")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                _ = await restored.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        else
            Assert.AreEqual(active, await restored.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedFirstUpgradeRestoresRecordedLegacyAbsenceAndForcesFullAdoption(bool pending)
    {
        var id = Guid.NewGuid();
        var paths = InstallerPaths.Create(Path.Combine(_root, "legacy-installer"), id, "synthetic");
        foreach (var directory in new[] { paths.ProductRoot, paths.InstanceRoot, paths.StateRoot, paths.OperationsRoot })
            InstallerFiles.CreateOwnerDirectory(directory);
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(paths.StateRoot, "raw"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        _protector = new DataProtectionDeploymentLocationProtector(_options);
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var original = CreateStore();
        var active = await original.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var (retained, bytes) = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        original.Dispose();
        var legacy = await ReadHistoryObjectAsync().ConfigureAwait(false);
        Assert.IsTrue(legacy.Remove("retainedIntegrityNonce"));
        await WriteHistoryObjectAsync(legacy).ConfigureAwait(false);
        DeleteFixtureLedger();
        var history = await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false);
        var image = new ImageInstallationIdentity("registry", "synthetic@sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64), "amd64", null);
        var snapshot = await SnapshotApi.CreateAsync(paths, id, Guid.NewGuid(), image,
            InstallerLayout.SqliteStores, CancellationToken.None).ConfigureAwait(false);
        using var candidate = CreateStore();
        _ = await candidate.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        candidate.Dispose();
        if (pending) await AddInterruptedIntentAsync("F|unpublished.json", "{}"u8.ToArray()).ConfigureAwait(false);
        var plan = await SnapshotApi.PlanRestoreAsync(paths, id, snapshot, [], CancellationToken.None).ConfigureAwait(false);
        _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
            [], plan, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName)));
        CollectionAssert.AreEqual(history, await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(retained).ConfigureAwait(false));
        using var restarted = CreateStore();
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var observed = 0;
        restarted.BeforeRetainedManifestValidationForTest = () => observed++;
        Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
        Assert.IsGreaterThan(0, observed);
        Assert.AreEqual(active, await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
    }

    private async Task RewriteLedgerAuthenticationAsync(bool legacy)
    {
        var ledger = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        using var connection = new SqliteConnection($"Data Source={ledger};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT payload FROM metadata WHERE id=1;";
        var metadata = JsonSerializer.Deserialize<Metadata>(_protector.Unprotect((byte[])(await read.ExecuteScalarAsync().ConfigureAwait(false))!))!;
        var newKey = legacy ? null : RandomNumberGenerator.GetBytes(32);
        var updated = metadata with { EntryKey = newKey is null ? null : Convert.ToBase64String(newKey) };
        var proofs = new List<EntryProof>();
        read.CommandText = "SELECT key,digest,kind,generation,auth FROM entries;";
        using (var rows = await read.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await rows.ReadAsync().ConfigureAwait(false))
            {
                var proof = new EntryProof(metadata.Nonce, rows.GetString(0), Convert.ToHexString((byte[])rows.GetValue(1)), rows.GetInt32(2), rows.GetInt64(3));
                Assert.IsTrue(AuthenticateEntry(proof, (byte[])rows.GetValue(4), EntryKey(metadata), _protector.Unprotect));
                proofs.Add(proof);
            }
        }
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE entries SET auth=$auth WHERE key=$key;";
        update.Parameters.Add("$auth", SqliteType.Blob);
        update.Parameters.Add("$key", SqliteType.Text);
        foreach (var proof in proofs)
        {
            update.Parameters[0].Value = ProtectEntry(proof, newKey, _protector.Protect);
            update.Parameters[1].Value = proof.Key;
            _ = await update.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        update.Parameters.Clear();
        update.CommandText = "UPDATE metadata SET payload=$payload WHERE id=1;";
        update.Parameters.AddWithValue("$payload", _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(updated)));
        _ = await update.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private async Task AddInterruptedIntentAsync(string key, byte[] plannedBytes)
    {
        var ledger = Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName);
        using var connection = new SqliteConnection($"Data Source={ledger};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT payload FROM metadata WHERE id=1;";
        var metadata = JsonSerializer.Deserialize<Metadata>(_protector.Unprotect((byte[])(await read.ExecuteScalarAsync().ConfigureAwait(false))!))!;
        var proof = new IntentProof(metadata.Nonce, key, false, metadata.Generation + 1,
            Convert.ToHexString(SHA256.HashData(plannedBytes)), null, false);
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO pending(key,tree,generation,planned,source,denied,auth) VALUES($key,0,$generation,$planned,NULL,0,$auth);";
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$generation", proof.Generation);
        insert.Parameters.AddWithValue("$planned", proof.Planned);
        insert.Parameters.AddWithValue("$auth", _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(proof)));
        _ = await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        var updated = metadata with { Generation = proof.Generation, Pending = Convert.ToHexString(SHA512.HashData(JsonSerializer.SerializeToUtf8Bytes(proof))) };
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE metadata SET payload=$payload WHERE id=1;";
        update.Parameters.AddWithValue("$payload", _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(updated)));
        _ = await update.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
