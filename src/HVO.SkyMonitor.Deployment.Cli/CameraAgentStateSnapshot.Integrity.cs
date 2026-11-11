using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.RetainedIntegrityFormat;

namespace HVO.SkyMonitor.Deployment;

internal static partial class CameraAgentStateSnapshot
{
    private const string OverlayName = "retained-files-overlay.v1.db";
    private const string OverlaySealName = "retained-files-overlay.v1.protected";
    private sealed record OverlaySeal(string Contract, string Snapshot, string FilesRoot, string Nonce, StateSnapshotDatabase Database);
    private sealed record LedgerFacts(Metadata Metadata, string FilesRoot);
    private sealed record LedgerRow(string Key, byte[] Digest, int Kind, long Generation, byte[] Auth);
    private sealed record PendingRow(IntentProof Proof, byte[] Auth);

    private static ServiceProvider IntegrityProtectionServices(InstallationPaths paths)
    {
        var keys = Path.Combine(paths.StateRoot, "raw", ".location", "keys");
        SafeFileSystem.EnsureSafeExistingAncestors(keys);
        if (!Directory.Exists(keys) || !Directory.EnumerateFiles(keys, "*.xml").Any())
            throw new InvalidDataException("Retained integrity keys are unavailable.");
        foreach (var key in Directory.EnumerateFiles(keys, "*.xml")) EnsureRegularStore(key, RetainedIntegrityStore);
        var services = new ServiceCollection();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys))
            .SetApplicationName("HVO.SkyMonitor.CameraAgent.DeploymentLocation").DisableAutomaticKeyGeneration();
        return services.BuildServiceProvider();
    }

    private static async Task VerifyIntegrityRestoreInventoryAsync(InstallationPaths paths, string root,
        StateSnapshotManifest manifest, CancellationToken cancellationToken)
    {
        var target = manifest.Databases.SingleOrDefault(static database => database.RelativePath == RetainedIntegrityStore);
        var currentExists = File.Exists(StorePath(paths.StateRoot, RetainedIntegrityStore));
        if (target is { Present: true } && !currentExists)
            throw new InstallerException("The established retained integrity ledger is missing; restore refuses to adopt an older ledger silently.");
        if (target is { Present: true } || !currentExists) return;
        await VerifyLegacyBoundarySnapshotAsync(paths, root, manifest, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyLegacyBoundarySnapshotAsync(InstallationPaths paths, string root,
        StateSnapshotManifest manifest, CancellationToken cancellationToken)
    {
        var target = manifest.Databases.SingleOrDefault(static database => database.RelativePath == RetainedIntegrityStore);
        // Only explicit, verified absence paired with genuine pre-ledger authority
        // permits removing the ledger created by a failed first candidate.
        if (target is not { Present: false } || manifest.ProtectedFiles is null ||
            manifest.ProtectedFiles.Single(static file => file.RelativePath == ProtectedLocations[^1]).Present)
            throw new InstallerException("This snapshot does not record paired pre-ledger state; restore a complete paired backup.");
        var history = manifest.ProtectedFiles.Single(static file => file.RelativePath == ProtectedLocations[0]);
        if (!history.Present) return;
        try
        {
            using var provider = IntegrityProtectionServices(paths);
            var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("CameraAgent", "DeploymentLocation", "v1");
            using var document = JsonDocument.Parse(protector.Unprotect(await File.ReadAllBytesAsync(
                ProtectedSnapshotPath(root, history), cancellationToken).ConfigureAwait(false)));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.TryGetProperty("retainedIntegrityNonce", out _))
                throw new InvalidDataException("Sealed history requires its original ledger.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        {
            throw new InstallerException("The snapshot does not contain verifiable pre-ledger history.", exception);
        }
    }

    // A failed rollback can have intentionally removed the ledger while applying
    // its own pre-ledger S0 snapshot. Only that active operation may recover its
    // authenticated, stopped S1 snapshot; generic missing-ledger restore still refuses.
    internal static async Task RecoverLegacyBoundaryAuthorityAsync(InstallationPaths paths, Guid instanceId,
        LifecycleOperationState operation, LifecycleStateSnapshot recovery, CancellationToken cancellationToken)
    {
        var destination = StorePath(paths.StateRoot, RetainedIntegrityStore);
        if (File.Exists(destination)) return;
        var target = await VerifyAsync(paths, instanceId, recovery, cancellationToken).ConfigureAwait(false);
        var ledger = target.Databases.SingleOrDefault(static database => database.RelativePath == RetainedIntegrityStore && database.Present);
        if (ledger is null) return;
        if (operation.Kind != LifecycleOperationKind.Rollback || !operation.MutationStarted ||
            operation.StateSnapshot != recovery || recovery.OperationId != operation.OperationId ||
            operation.RetainedStateSnapshot is not { } legacy ||
            operation.Phase is LifecycleOperationPhase.Prepared or LifecycleOperationPhase.Committed)
            throw new InstallerException("Missing retained authority is not an active ledger-boundary rollback recovery.");
        var prior = await VerifyAsync(paths, instanceId, legacy, cancellationToken).ConfigureAwait(false);
        var priorRoot = SnapshotRoot(paths, legacy.OperationId, legacy.SnapshotId);
        await VerifyLegacyBoundarySnapshotAsync(paths, priorRoot, prior, cancellationToken).ConfigureAwait(false);
        var root = SnapshotRoot(paths, recovery.OperationId, recovery.SnapshotId);
        using var ownership = AcquireIntegrityRestoreOwnership(paths);
        using var services = IntegrityProtectionServices(paths);
        var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("CameraAgent", "DeploymentLocation", "v1");
        using (var source = CameraAgentStatePreflight.ReadOnlyDatabase.Open(SnapshotFile(root, ledger)))
        {
            var facts = AuthenticateLedger(source.Connection, protector, cancellationToken);
            var history = target.ProtectedFiles?.Single(static file => file.RelativePath == ProtectedLocations[0]);
            if (history is not { Present: true }) throw new InstallerException("Recovery snapshot has no sealed location authority.");
            using var document = JsonDocument.Parse(protector.Unprotect(await File.ReadAllBytesAsync(
                ProtectedSnapshotPath(root, history), cancellationToken).ConfigureAwait(false)));
            if (!document.RootElement.TryGetProperty("retainedIntegrityNonce", out var nonce) || nonce.GetString() != facts.Metadata.Nonce)
                throw new InstallerException("Recovery snapshot ledger does not match its sealed location authority.");
        }
        async Task CheckPairedTransitionAsync()
        {
            foreach (var relative in ProtectedLocations)
            {
                var before = target.ProtectedFiles!.Single(file => file.RelativePath == relative);
                var after = prior.ProtectedFiles!.Single(file => file.RelativePath == relative);
                var current = StorePath(paths.StateRoot, relative);
                SafeFileSystem.EnsureSafeExistingAncestors(current);
                var exists = File.Exists(current);
                string? digest = null;
                if (exists)
                {
                    EnsureRegularStore(current, relative);
                    var length = new FileInfo(current).Length;
                    if (!(before.Present && length == before.Length) && !(after.Present && length == after.Length))
                        throw new InstallerException("Retained authority changed outside the recorded ledger-boundary rollback.");
                    digest = await SafeFileSystem.ComputeSha256Async(current, cancellationToken).ConfigureAwait(false);
                }
                if (!((exists == before.Present && (!exists || digest == before.Sha256)) ||
                      (exists == after.Present && (!exists || digest == after.Sha256))))
                    throw new InstallerException("Retained authority changed outside the recorded ledger-boundary rollback.");
            }
        }
        await CheckPairedTransitionAsync().ConfigureAwait(false);
        // Stage beside the destination so the final rename remains atomic even
        // when operations and instance state live on different filesystems.
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".boundary-recovery.tmp";
        DiscardStaged(temporary);
        await CopyPureAsync(root, ledger, temporary, cancellationToken).ConfigureAwait(false);
        await CheckPairedTransitionAsync().ConfigureAwait(false);
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (File.Exists(destination)) throw new InstallerException("Retained authority appeared during boundary recovery.");
        // A crash during the earlier removal may have left SQLite recovery files.
        // They must never replay over the authenticated stopped snapshot.
        RemoveStore(destination, RetainedIntegrityStore);
        File.Move(temporary, destination, overwrite: false);
        SafeFileSystem.FlushDirectoryChain(Path.GetDirectoryName(destination)!, paths.ProductRoot);
        // This installs expected evidence only. A new process still must fully scrub
        // actual files/schema; no proof is manufactured from current unchecked bytes.
        StepCompleted("restore-boundary-authority");
    }

    // Called only after a complete archive has been verified/extracted. The archive's
    // retained files and keys are in this disposable staged tree; database-only
    // rollback never calls this or materializes an absent current authority.
    internal static async Task StageArchiveIntegrityAsync(InstallationPaths paths, Guid instanceId,
        LifecycleStateSnapshot snapshot, string stagedInstanceRoot, CancellationToken cancellationToken)
    {
        var manifest = await VerifyAsync(paths, instanceId, snapshot, cancellationToken).ConfigureAwait(false);
        var ledger = manifest.Databases.SingleOrDefault(static database => database.RelativePath == RetainedIntegrityStore && database.Present);
        if (ledger is null) return;
        var stagedPaths = paths with { StateRoot = Path.Combine(stagedInstanceRoot, "state") };
        using var services = IntegrityProtectionServices(stagedPaths);
        var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("CameraAgent", "DeploymentLocation", "v1");
        var root = SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId);
        using (var source = CameraAgentStatePreflight.ReadOnlyDatabase.Open(SnapshotFile(root, ledger)))
            _ = AuthenticateLedger(source.Connection, protector, cancellationToken);
        var destination = StorePath(stagedPaths.StateRoot, RetainedIntegrityStore);
        SafeFileSystem.EnsureSafeExistingAncestors(destination);
        if (File.Exists(destination)) throw new InstallerException("A complete archive must carry its ledger only through the verified database snapshot.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await CopyPureAsync(root, ledger, destination, cancellationToken).ConfigureAwait(false);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        SafeFileSystem.FlushDirectoryChain(Path.GetDirectoryName(destination)!, stagedInstanceRoot);
    }

    private static FileStream? AcquireIntegrityRestoreOwnership(InstallationPaths paths)
    {
        var directory = Path.Combine(paths.StateRoot, "raw", ".location");
        if (!Directory.Exists(directory)) return null;
        SafeFileSystem.EnsureSafeExistingAncestors(directory);
        var path = Path.Combine(directory, "retained-integrity.v1.lock");
        if (File.Exists(path)) EnsureRegularStore(path, RetainedIntegrityStore);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            throw new InstallerException("The retained root has a live integrity owner; restore requires the instance stopped.", exception);
        }
    }

    // A database rollback leaves capture/product files in place. Preserve their
    // authenticated current expectations; only journal expectations rewind.
    private static async Task<StateSnapshotDatabase?> PrepareIntegrityRestoreAsync(
        InstallationPaths paths, string root, StateSnapshotManifest manifest, LifecycleStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var target = manifest.Databases.SingleOrDefault(static database => database.RelativePath == RetainedIntegrityStore);
        var currentPath = StorePath(paths.StateRoot, RetainedIntegrityStore);
        if (target is not { Present: true })
        {
            await VerifyIntegrityRestoreInventoryAsync(paths, root, manifest, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (!File.Exists(currentPath))
            throw new InstallerException("The established retained integrity ledger disappeared before restoration.");
        try
        {
            using var provider = IntegrityProtectionServices(paths);
            var protector = provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("CameraAgent", "DeploymentLocation", "v1");
            var sealProtector = protector.CreateProtector("RetainedRestore", "v1");
            using var old = CameraAgentStatePreflight.ReadOnlyDatabase.Open(SnapshotFile(root, target));
            var targetFacts = AuthenticateLedger(old.Connection, protector, cancellationToken);
            using var current = CameraAgentStatePreflight.ReadOnlyDatabase.Open(currentPath);
            var currentFacts = AuthenticateLedger(current.Connection, protector, cancellationToken);
            if (currentFacts.Metadata.Nonce != targetFacts.Metadata.Nonce)
                throw new InvalidDataException("Retained integrity snapshots belong to different authorities.");
            StateSnapshotDatabase? cached;
            try
            {
                cached = await ReadOverlayAsync(root, snapshot, targetFacts.Metadata.Nonce, currentFacts.FilesRoot,
                    sealProtector, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or CryptographicException or JsonException or InvalidDataException)
            {
                // Derived overlay state can be rebuilt from both authenticated sources.
                // Without the current authority, a broken overlay remains a hard refusal.
                cached = null;
            }
            if (cached is not null) return cached;
            var temporary = Path.Combine(root, ".retained-files-overlay.tmp");
            if (File.Exists(temporary)) { EnsureRegularStore(temporary, RetainedIntegrityStore); File.Delete(temporary); }
            if (await CopySyncedAsync(SnapshotFile(root, target), temporary, cancellationToken).ConfigureAwait(false) != target.Sha256)
                throw new InvalidDataException("Retained integrity snapshot changed while staging its restore.");
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = temporary, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                await copy.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = (SqliteTransaction)await copy.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                using (var remove = copy.CreateCommand())
                {
                    remove.Transaction = transaction;
                    remove.CommandText = "DELETE FROM entries WHERE key LIKE 'F|%'; DELETE FROM pending WHERE key LIKE 'F|%'; DELETE FROM scan_entries;";
                    _ = await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                var carried = WalkLedger(current.Connection, protector, currentFacts.Metadata, cancellationToken, row =>
                {
                    if (!row.Key.StartsWith("F|", StringComparison.Ordinal)) return;
                    using var insert = copy.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO entries(key,digest,kind,generation,auth) VALUES($key,$digest,$kind,$generation,$auth);";
                    insert.Parameters.AddWithValue("$key", row.Key);
                    insert.Parameters.AddWithValue("$digest", row.Digest);
                    insert.Parameters.AddWithValue("$kind", row.Kind);
                    insert.Parameters.AddWithValue("$generation", row.Generation);
                    insert.Parameters.AddWithValue("$auth", ProtectEntry(
                        new EntryProof(targetFacts.Metadata.Nonce, row.Key, Convert.ToHexString(row.Digest), row.Kind, row.Generation),
                        EntryKey(targetFacts.Metadata), protector.Protect));
                    _ = insert.ExecuteNonQuery();
                });
                var pendingCarried = WalkPending(current.Connection, protector, currentFacts.Metadata, cancellationToken, row =>
                {
                    if (!row.Proof.Key.StartsWith("F|", StringComparison.Ordinal)) return;
                    using var insert = copy.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO pending(key,tree,generation,planned,source,denied,auth) VALUES($key,$tree,$generation,$planned,$source,$denied,$auth);";
                    insert.Parameters.AddWithValue("$key", row.Proof.Key);
                    insert.Parameters.AddWithValue("$tree", row.Proof.Tree);
                    insert.Parameters.AddWithValue("$generation", row.Proof.Generation);
                    insert.Parameters.AddWithValue("$planned", (object?)row.Proof.Planned ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$source", (object?)row.Proof.Source ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$denied", row.Proof.Denied);
                    insert.Parameters.AddWithValue("$auth", row.Auth);
                    _ = insert.ExecuteNonQuery();
                });
                if (Convert.ToHexString(carried.Chain) != currentFacts.Metadata.Chain || carried.Count != currentFacts.Metadata.Count ||
                    Convert.ToHexString(pendingCarried.Chain) != currentFacts.Metadata.Pending ||
                    FileStateRoot(carried.Files, pendingCarried.Files) != currentFacts.FilesRoot)
                    throw new InvalidDataException("Retained integrity source changed while staging its restore.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                var generation = checked(Math.Max(currentFacts.Metadata.Generation, targetFacts.Metadata.Generation) + 1);
                var inherited = targetFacts.Metadata with { Generation = generation };
                var (chain, count, _) = WalkLedger(copy, protector, inherited, cancellationToken);
                var (pendingChain, _) = WalkPending(copy, protector, inherited, cancellationToken);
                using var metadata = copy.CreateCommand();
                metadata.CommandText = "UPDATE metadata SET payload=$payload WHERE id=1;";
                metadata.Parameters.AddWithValue("$payload", protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
                    inherited with { Chain = Convert.ToHexString(chain), Count = count, Pending = Convert.ToHexString(pendingChain) })));
                _ = await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _ = AuthenticateLedger(copy, protector, cancellationToken);
            }
            SyncFile(temporary);
            var overlay = Path.Combine(root, OverlayName);
            File.Move(temporary, overlay, overwrite: true);
            NativeLinux.FlushDirectory(root);
            var facts = VerifyFile(overlay, RetainedIntegrityStore);
            var result = new StateSnapshotDatabase(RetainedIntegrityStore, true, OverlayName, new FileInfo(overlay).Length,
                await SafeFileSystem.ComputeSha256Async(overlay, cancellationToken).ConfigureAwait(false),
                facts.UserVersion, facts.SchemaSha256, facts.Tables);
            var seal = new OverlaySeal("retained-files-overlay-v2", snapshot.ManifestSha256, currentFacts.FilesRoot,
                targetFacts.Metadata.Nonce, result);
            var sealPath = Path.Combine(root, OverlaySealName);
            var sealTemporary = sealPath + ".tmp";
            if (File.Exists(sealTemporary)) { EnsureRegularStore(sealTemporary, RetainedIntegrityStore); File.Delete(sealTemporary); }
            await File.WriteAllBytesAsync(sealTemporary, sealProtector.Protect(JsonSerializer.SerializeToUtf8Bytes(seal)), cancellationToken).ConfigureAwait(false);
            File.SetUnixFileMode(sealTemporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            SyncFile(sealTemporary);
            File.Move(sealTemporary, sealPath, overwrite: true);
            NativeLinux.FlushDirectory(root);
            StepCompleted("restore-integrity-overlay-ready");
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or
            CryptographicException or JsonException or InvalidDataException or FormatException or OverflowException)
        {
            throw new InstallerException("The retained integrity state cannot be safely paired with this database restore.", exception);
        }
    }

    private static async Task<StateSnapshotDatabase?> ReadOverlayAsync(string root, LifecycleStateSnapshot snapshot,
        string nonce, string? filesRoot, IDataProtector protector, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, OverlaySealName);
        if (!File.Exists(path)) return null;
        EnsureRegularStore(path, RetainedIntegrityStore);
        if (new FileInfo(path).Length > 65_536) throw new InvalidDataException("Retained overlay seal is invalid.");
        var seal = JsonSerializer.Deserialize<OverlaySeal>(protector.Unprotect(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
        if (seal is null || seal.Contract != "retained-files-overlay-v2" || seal.Snapshot != snapshot.ManifestSha256 ||
            seal.Nonce != nonce || seal.Database.RelativePath != RetainedIntegrityStore || seal.Database.SnapshotFile != OverlayName)
            throw new InvalidDataException("Retained overlay seal is invalid.");
        if (filesRoot is not null && filesRoot != seal.FilesRoot) return null;
        var overlay = Path.Combine(root, OverlayName);
        EnsureRegularStore(overlay, RetainedIntegrityStore);
        if (new FileInfo(overlay).Length != seal.Database.Length ||
            await SafeFileSystem.ComputeSha256Async(overlay, cancellationToken).ConfigureAwait(false) != seal.Database.Sha256)
            throw new InvalidDataException("Retained overlay differs from its seal.");
        return seal.Database;
    }

    private static LedgerFacts AuthenticateLedger(SqliteConnection connection, IDataProtector protector, CancellationToken token)
    {
        using (var layout = connection.CreateCommand())
        {
            layout.CommandText = "SELECT name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';";
            using var rows = layout.ExecuteReader();
            var expected = SchemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeSchema).ToHashSet(StringComparer.Ordinal);
            while (rows.Read()) if (!expected.Remove(NormalizeSchema(rows.GetString(1))))
                throw new InvalidDataException("Retained integrity layout is invalid.");
            if (expected.Count != 0) throw new InvalidDataException("Retained integrity layout is incomplete.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM metadata WHERE id=1 AND length(payload)<=16384;";
        var meta = command.ExecuteScalar() is byte[] payload
            ? JsonSerializer.Deserialize<Metadata>(protector.Unprotect(payload)) : null;
        if (meta is null || meta.Nonce is not { Length: 32 } || meta.Chain is not { Length: 128 } ||
            !meta.Established || meta.Generation < 0 || meta.Count < 0 || meta.Pending is not { Length: 128 })
            throw new InvalidDataException("Retained integrity authority is unavailable.");
        var (chain, count, files) = WalkLedger(connection, protector, meta, token);
        var pending = WalkPending(connection, protector, meta, token);
        if (Convert.ToHexString(chain) != meta.Chain || count != meta.Count || Convert.ToHexString(pending.Chain) != meta.Pending)
            throw new InvalidDataException("Retained integrity commitment is invalid.");
        return new LedgerFacts(meta, FileStateRoot(files, pending.Files));
    }

    private static string FileStateRoot(byte[] entries, byte[] pending)
        => Convert.ToHexString(SHA512.HashData([.. entries, .. pending]));

    private static (byte[] Chain, byte[] Files) WalkPending(SqliteConnection connection, IDataProtector protector,
        Metadata metadata, CancellationToken token, Action<PendingRow>? visit = null)
    {
        var chain = new byte[64];
        var files = new byte[64];
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(key),length(auth),key,tree,generation,planned,source,denied,auth FROM pending;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            if (rows.GetInt64(0) > 4096 || rows.GetInt64(1) > 16384)
                throw new InvalidDataException("Retained integrity pending entry is invalid.");
            var proof = new IntentProof(metadata.Nonce, rows.GetString(2), rows.GetBoolean(3), rows.GetInt64(4),
                rows.IsDBNull(5) ? null : rows.GetString(5), rows.IsDBNull(6) ? null : rows.GetString(6), rows.GetBoolean(7));
            if (!(proof.Key.StartsWith("F|", StringComparison.Ordinal) || proof.Key.StartsWith("J|", StringComparison.Ordinal)) ||
                proof.Generation < 0 || proof.Generation > metadata.Generation || proof.Planned is { Length: not 64 } || proof.Source is { Length: > 4096 })
                throw new InvalidDataException("Retained integrity pending entry is invalid.");
            var auth = (byte[])rows.GetValue(8);
            if (JsonSerializer.Deserialize<IntentProof>(protector.Unprotect(auth)) != proof)
                throw new InvalidDataException("Retained integrity pending authentication failed.");
            var leaf = SHA512.HashData(JsonSerializer.SerializeToUtf8Bytes(proof));
            Accumulate(chain, leaf);
            if (proof.Key.StartsWith("F|", StringComparison.Ordinal)) Accumulate(files, leaf);
            visit?.Invoke(new PendingRow(proof, auth));
        }
        return (chain, files);
    }

    private static string NormalizeSchema(string sql)
        => string.Concat(sql.Where(static c => !char.IsWhiteSpace(c))).Replace("IFNOTEXISTS", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();

    private static (byte[] Chain, long Count, byte[] Files) WalkLedger(SqliteConnection connection, IDataProtector protector,
        Metadata metadata, CancellationToken token, Action<LedgerRow>? visit = null)
    {
        var root = new byte[64];
        var files = new byte[64];
        var entryKey = EntryKey(metadata);
        long count = 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(key),length(digest),length(auth),key,digest,kind,generation,auth FROM entries;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            if (rows.GetInt64(0) > 4096 || rows.GetInt64(1) is not (0 or 32) || rows.GetInt64(2) > 16384)
                throw new InvalidDataException("Retained integrity entry is invalid.");
            var row = new LedgerRow(rows.GetString(3), (byte[])rows.GetValue(4), rows.GetInt32(5), rows.GetInt64(6), (byte[])rows.GetValue(7));
            if (!(row.Key.StartsWith("F|", StringComparison.Ordinal) || row.Key.StartsWith("J|", StringComparison.Ordinal)) ||
                row.Kind is < 0 or > 3 || row.Generation < 0 || row.Generation > metadata.Generation)
                throw new InvalidDataException("Retained integrity entry is invalid.");
            var expected = new EntryProof(metadata.Nonce, row.Key, Convert.ToHexString(row.Digest), row.Kind, row.Generation);
            if (!AuthenticateEntry(expected, row.Auth, entryKey, protector.Unprotect))
                throw new InvalidDataException("Retained integrity entry authentication failed.");
            var leaf = Hash(expected);
            Accumulate(root, leaf);
            if (row.Key.StartsWith("F|", StringComparison.Ordinal)) Accumulate(files, leaf);
            if (row.Kind != 0) count++;
            visit?.Invoke(row);
        }
        return (root, count, files);
    }
}
