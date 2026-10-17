using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Snapshots, verifies and restores an instance's small SQLite databases for an image lifecycle operation that
/// declares a state migration. Only the stores in <see cref="CameraAgentStateLayout.SqliteStores"/> are read; bulk
/// frames, archives and derived products are never copied.
/// </summary>
/// <remarks>
/// Every method runs while the instance is stopped. Each write the next step depends on is durable before that step
/// runs: a snapshot file is synced, renamed into place and its directory synced; the manifest is written atomically
/// and synced; a restored database is synced under a temporary name, its stale sidecars are removed, and it is
/// renamed over the store before the directory is synced. Every step can be repeated after a crash: creation writes
/// a new snapshot directory each attempt, and restoring the same verified snapshot twice leaves the same bytes.
/// </remarks>
internal static class CameraAgentStateSnapshot
{
    public const int SchemaVersion = 1;
    private const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Runs at each durable step inside a snapshot or restore, named <c>snapshot:&lt;store&gt;</c>,
    /// <c>restore-sidecars-removed:&lt;store&gt;</c> or <c>restore:&lt;store&gt;</c>. Crash-injection tests capture what a power
    /// loss at that moment would leave behind; production leaves it empty.
    /// </summary>
    internal static Action<string> StepCompleted { get; set; } = static _ => { };

    public static string SnapshotRoot(InstallationPaths paths, Guid operationId, Guid snapshotId)
        => Path.Combine(paths.OperationsRoot, "lifecycle", operationId.ToString("D"), "state-snapshots", snapshotId.ToString("D"));

    /// <summary>The bytes the stores and their sidecars occupy now, which bounds what one snapshot writes.</summary>
    public static long StoreBytes(string stateRoot, IReadOnlyList<string> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        long total = 0;
        foreach (var relative in stores)
        {
            var path = StorePath(stateRoot, relative);
            foreach (var file in CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => path + suffix).Prepend(path))
            {
                if (File.Exists(file)) total = checked(total + new FileInfo(file).Length);
            }
        }
        return total;
    }

    /// <summary>
    /// Copies every present store through the SQLite online backup API into a new snapshot directory, then verifies
    /// each copy against its source before the manifest that binds them is written.
    /// </summary>
    public static async Task<LifecycleStateSnapshot> CreateAsync(
        InstallationPaths paths,
        Guid instanceId,
        Guid operationId,
        ImageInstallationIdentity activeImage,
        IReadOnlyList<string> stores,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(activeImage);
        ArgumentNullException.ThrowIfNull(stores);
        var snapshotId = Guid.NewGuid();
        var root = SnapshotRoot(paths, operationId, snapshotId);
        var lifecycleRoot = Path.Combine(paths.OperationsRoot, "lifecycle");
        foreach (var directory in new[]
                 {
                     lifecycleRoot, Path.Combine(lifecycleRoot, operationId.ToString("D")), Path.GetDirectoryName(root)!, root
                 })
        {
            SafeFileSystem.CreateOwnerDirectory(directory);
        }
        var databases = new List<StateSnapshotDatabase>();
        for (var index = 0; index < stores.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = stores[index];
            var source = StorePath(paths.StateRoot, relative);
            SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(source)!);
            if (!File.Exists(source))
            {
                if (CameraAgentStateLayout.SqliteSidecarSuffixes.Any(suffix => File.Exists(source + suffix)))
                    throw new InstallerException($"The CameraAgent store '{relative}' has recovery files but no database; it cannot be snapshotted.");
                databases.Add(new StateSnapshotDatabase(relative, false, null, 0, null, 0, null, []));
                continue;
            }
            EnsureRegularStore(source, relative);
            var fileName = string.Create(CultureInfo.InvariantCulture, $"{index:D2}-{Path.GetFileName(relative)}");
            try
            {
                databases.Add(await SnapshotStoreAsync(source, relative, root, fileName, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                throw new InstallerException(
                    $"The CameraAgent store '{relative}' could not be snapshotted: {Redaction.SafeDiagnostic(exception.Message)}", exception);
            }
            StepCompleted($"snapshot:{relative}");
        }
        var manifest = new StateSnapshotManifest(
            SchemaVersion, snapshotId, instanceId, operationId, DateTimeOffset.UtcNow, activeImage, databases);
        var manifestPath = Path.Combine(root, ManifestFileName);
        await SafeFileSystem.WriteJsonAtomicAsync(
            manifestPath, manifest, DeploymentJsonContext.Default.StateSnapshotManifest, cancellationToken).ConfigureAwait(false);
        // The reference is published only once every directory from the snapshot to the product root is durable, so a
        // power loss cannot keep the journal's reference while losing the entry of a directory created earlier.
        SafeFileSystem.FlushDirectoryChain(root, paths.ProductRoot);
        return new LifecycleStateSnapshot(
            snapshotId,
            operationId,
            await SafeFileSystem.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false),
            manifest.CreatedUtc);
    }

    /// <summary>
    /// Copies one store through the online backup API into a self-contained file, makes it durable, and proves the
    /// copy intact and equal to its source before it is recorded.
    /// </summary>
    private static async Task<StateSnapshotDatabase> SnapshotStoreAsync(
        string source,
        string relative,
        string root,
        string fileName,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(root, fileName);
        var temporary = Path.Combine(root, $".{fileName}.tmp");
        StoreFacts facts;
        using (var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(source))
        {
            facts = ReadFacts(database.Connection);
            await using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString()))
            {
                await copy.OpenAsync(cancellationToken).ConfigureAwait(false);
                database.Connection.BackupDatabase(copy);
                // A snapshot is one self-contained file: no write-ahead log or rollback journal beside it.
                using var mode = copy.CreateCommand();
                mode.CommandText = "PRAGMA journal_mode=DELETE;";
                if (!string.Equals(
                        Convert.ToString(await mode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture),
                        "delete", StringComparison.OrdinalIgnoreCase))
                    throw new InstallerException($"The snapshot of '{relative}' could not be made self-contained.");
            }
        }
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        SyncFile(temporary);
        File.Move(temporary, destination, overwrite: false);
        NativeLinux.FlushDirectory(root);
        var snapshotFacts = VerifyFile(destination, relative);
        if (snapshotFacts != facts)
            throw new InstallerException($"The snapshot of '{relative}' does not match its source schema and row counts.");
        return new StateSnapshotDatabase(
            relative, true, fileName, new FileInfo(destination).Length,
            await SafeFileSystem.ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false),
            facts.UserVersion, facts.SchemaSha256, facts.Tables);
    }

    /// <summary>
    /// Proves a recorded snapshot is the one its reference names and that every file in it is intact: the manifest
    /// hash, each file's hash, <c>PRAGMA integrity_check</c>, and the schema and row counts recorded at creation.
    /// </summary>
    public static async Task<StateSnapshotManifest> VerifyAsync(
        InstallationPaths paths,
        Guid instanceId,
        LifecycleStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId);
        var manifestPath = Path.Combine(root, ManifestFileName);
        SafeFileSystem.EnsureSafeExistingAncestors(root);
        if (!File.Exists(manifestPath))
            throw new InstallerException("The recorded CameraAgent database snapshot is missing its manifest.");
        if (await SafeFileSystem.ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false) != snapshot.ManifestSha256)
            throw new InstallerException("The recorded CameraAgent database snapshot manifest differs from its journal record.");
        StateSnapshotManifest manifest;
        await using (var stream = SafeFileSystem.OpenOwnerFileRead(manifestPath))
        {
            manifest = await CameraAgentLifecycleManager.DeserializeRetainedAsync(
                stream, DeploymentJsonContext.Default.StateSnapshotManifest, "database snapshot manifest", cancellationToken)
                .ConfigureAwait(false);
        }
        var locations = manifest.Databases.Select(static database => database.RelativePath).ToArray();
        if (manifest.SchemaVersion != SchemaVersion || manifest.SnapshotId != snapshot.SnapshotId ||
            manifest.OperationId != snapshot.OperationId || manifest.InstanceId != instanceId ||
            !locations.All(CameraAgentStateInventory.IsValidStoreLocation) ||
            locations.Distinct(StringComparer.Ordinal).Count() != locations.Length ||
            !CameraAgentStateLayout.SqliteStores.All(store => locations.Contains(store, StringComparer.Ordinal)))
            throw new InstallerException("The recorded CameraAgent database snapshot does not belong to this instance and operation.");
        foreach (var database in manifest.Databases.Where(static database => database.Present))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = SnapshotFile(root, database);
            EnsureRegularStore(file, database.RelativePath);
            if (new FileInfo(file).Length != database.Length ||
                await SafeFileSystem.ComputeSha256Async(file, cancellationToken).ConfigureAwait(false) != database.Sha256)
                throw new InstallerException($"The snapshot of '{database.RelativePath}' differs from its manifest.");
            if (VerifyFile(file, database.RelativePath) != new StoreFacts(database.UserVersion, database.SchemaSha256!, database.Tables))
                throw new InstallerException($"The snapshot of '{database.RelativePath}' does not match its recorded schema and row counts.");
        }
        return manifest;
    }

    /// <summary>
    /// The plan a restore of <paramref name="snapshot"/> follows: the authenticated snapshot's store locations, and
    /// <paramref name="currentLocations"/>, the instance's configured inventory now together with any an earlier attempt
    /// of the same restore recorded. Every location is validated.
    /// </summary>
    public static async Task<StateRestorePlan> PlanRestoreAsync(
        InstallationPaths paths,
        Guid instanceId,
        LifecycleStateSnapshot snapshot,
        IEnumerable<string> currentLocations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentLocations);
        var manifest = await VerifyAsync(paths, instanceId, snapshot, cancellationToken).ConfigureAwait(false);
        var current = currentLocations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!current.All(CameraAgentStateInventory.IsValidStoreLocation))
            throw new InstallerException("The CameraAgent store inventory names a location a restore cannot use.");
        return new StateRestorePlan(
            snapshot.SnapshotId,
            current,
            [.. manifest.Databases.Select(static database => database.RelativePath).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Puts every store back exactly as the snapshot recorded it. A store recorded as absent is removed, and so is a
    /// store at a location <paramref name="plan"/> lists as current that the snapshot does not record at all, because
    /// it was created after the snapshot (bulk files are never touched). Stale sidecars are removed before the snapshot
    /// replaces a store, because a write-ahead log or hot journal left by a candidate would otherwise be replayed over
    /// the restored bytes. Before it returns, the directory of every store it restored or removed is synced together
    /// with each directory above it to the product root, whether or not this attempt created it, so the caller's
    /// completion record never outlives a directory entry an interrupted attempt created and never synced.
    /// <paramref name="carry"/> is the planned carry (<see cref="CameraAgentStateCounters.PlanCarry"/>). A store whose
    /// carry still fails is left exactly as the snapshot recorded it and returned with why, and the restore goes on.
    /// </summary>
    public static async Task<IReadOnlyList<(string What, string Reason)>> RestoreAsync(
        InstallationPaths paths,
        Guid instanceId,
        LifecycleStateSnapshot snapshot,
        uint uid,
        uint gid,
        IReadOnlyList<StateCounterFloor> carry,
        StateRestorePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carry);
        ArgumentNullException.ThrowIfNull(plan);
        var manifest = await VerifyAsync(paths, instanceId, snapshot, cancellationToken).ConfigureAwait(false);
        var targetLocations = manifest.Databases.Select(static database => database.RelativePath).Order(StringComparer.Ordinal).ToArray();
        if (plan.SnapshotId != snapshot.SnapshotId || !plan.TargetLocations.SequenceEqual(targetLocations, StringComparer.Ordinal) ||
            !plan.CurrentLocations.All(CameraAgentStateInventory.IsValidStoreLocation))
            throw new InstallerException("The journaled restore plan does not match the CameraAgent database snapshot it restores.");
        var root = SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId);
        var touchedDirectories = new SortedSet<string>(StringComparer.Ordinal);
        var uncarried = new List<(string What, string Reason)>();
        foreach (var database in manifest.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = StorePath(paths.StateRoot, database.RelativePath);
            var directory = Path.GetDirectoryName(target)!;
            SafeFileSystem.EnsureSafeExistingAncestors(directory);
            if (!database.Present)
            {
                if (!Directory.Exists(directory)) continue;
                // A store the snapshot did not have but whose counters have since handed out identities is kept, so
                // those identities are never handed out again; the previous release reads it under the migration rule.
                if (carry.Any(floor => floor.Store == database.RelativePath) && File.Exists(target)) continue;
                RemoveStore(target, database.RelativePath);
                touchedDirectories.Add(directory);
                continue;
            }
            if (!Directory.Exists(directory))
            {
                foreach (var ancestor in MissingAncestors(paths.StateRoot, directory))
                {
                    SafeFileSystem.CreateRuntimeDirectory(ancestor, uid, gid);
                    NativeLinux.FlushDirectory(Path.GetDirectoryName(ancestor)!);
                }
            }
            if (File.Exists(target)) EnsureRegularStore(target, database.RelativePath);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.restore-{snapshot.SnapshotId:N}.tmp");
            if (File.Exists(temporary)) File.Delete(temporary);
            var copied = await CopySyncedAsync(SnapshotFile(root, database), temporary, cancellationToken).ConfigureAwait(false);
            if (copied != database.Sha256)
                throw new InstallerException($"The restored copy of '{database.RelativePath}' differs from its snapshot.");
            foreach (var sidecar in CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => target + suffix))
            {
                if (!File.Exists(sidecar)) continue;
                EnsureRegularStore(sidecar, database.RelativePath);
                File.Delete(sidecar);
            }
            NativeLinux.FlushDirectory(directory);
            StepCompleted($"restore-sidecars-removed:{database.RelativePath}");
            File.Move(temporary, target, overwrite: true);
            NativeLinux.FlushDirectory(directory);
            if (await SafeFileSystem.ComputeSha256Async(target, cancellationToken).ConfigureAwait(false) != database.Sha256)
                throw new InstallerException($"The restored '{database.RelativePath}' differs from its snapshot.");
            // The restored counters are raised to the marks the instance had already handed out; SQLite syncs the
            // database on commit, and the directory sync makes its rollback journal's removal durable. A carry that
            // still fails rolls back, leaving the store as the snapshot recorded it.
            if (CameraAgentStateCounters.Apply(target, database.RelativePath, carry) is { } failure)
                uncarried.Add(($"the counters of '{database.RelativePath}'", failure));
            NativeLinux.FlushDirectory(directory);
            touchedDirectories.Add(directory);
            StepCompleted($"restore:{database.RelativePath}");
        }
        // A store under a root the instance named after the snapshot was taken (a capture schedule created through the
        // API, say) belongs to no state the snapshot records. Its database changes are lost like every other's, so it is
        // removed and the previous release never meets it; the files under that root stay.
        foreach (var location in plan.CurrentLocations.Except(targetLocations, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = StorePath(paths.StateRoot, location);
            var directory = Path.GetDirectoryName(target)!;
            SafeFileSystem.EnsureSafeExistingAncestors(directory);
            if (!Directory.Exists(directory)) continue;
            RemoveStore(target, location);
            touchedDirectories.Add(directory);
            StepCompleted($"restore-removed:{location}");
        }
        foreach (var directory in touchedDirectories)
        {
            SafeFileSystem.FlushDirectoryChain(directory, paths.ProductRoot);
        }
        StepCompleted("restore-directories-synced");
        return uncarried;
    }

    /// <summary>Unlinks a store and its SQLite sidecars, each proved a regular file first, and syncs its directory.</summary>
    private static void RemoveStore(string target, string relative)
    {
        var directory = Path.GetDirectoryName(target)!;
        var removed = false;
        foreach (var file in CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => target + suffix).Prepend(target))
        {
            if (!File.Exists(file)) continue;
            EnsureRegularStore(file, relative);
            File.Delete(file);
            removed = true;
        }
        if (removed) NativeLinux.FlushDirectory(directory);
    }

    /// <summary>The snapshot's copy of the store at <paramref name="relative"/>, or null when it recorded the store as absent.</summary>
    public static string? StoreFile(
        InstallationPaths paths,
        LifecycleStateSnapshot snapshot,
        StateSnapshotManifest manifest,
        string relative)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(manifest);
        var database = manifest.Databases.SingleOrDefault(database => database.RelativePath == relative);
        return database is { Present: true }
            ? SnapshotFile(SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId), database)
            : null;
    }

    /// <summary>
    /// The snapshot's Identity and raw-ingress copies, for a preflight of the state a rollback will start on. A store
    /// the snapshot recorded as absent maps to a path that does not exist, which the preflight reads as absent.
    /// </summary>
    public static CameraAgentStateDatabaseView PreflightView(
        InstallationPaths paths,
        LifecycleStateSnapshot snapshot,
        StateSnapshotManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(manifest);
        var root = SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId);
        string View(string relative)
        {
            var database = manifest.Databases.Single(database => database.RelativePath == relative);
            return database.Present ? SnapshotFile(root, database) : Path.Combine(root, "absent", Path.GetFileName(relative));
        }
        return new CameraAgentStateDatabaseView(
            View(CameraAgentStateLayout.SqliteStores[0]),
            View(CameraAgentStateLayout.SqliteStores[1]));
    }

    /// <summary>The operator-facing warning for a restore of <paramref name="snapshot"/>.</summary>
    public static string Warning(LifecycleStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Warning: restoring the CameraAgent databases from the snapshot taken at {snapshot.RecordedUtc:yyyy-MM-ddTHH:mm:ssZ} (snapshot {snapshot.SnapshotId:D}). Every database change made after that point is lost, including captures, settings and accounts recorded since then. Files written since then stay on disk. When it starts, CameraAgent records again the raw frames it can verify and may process and upload them again; other products written since then are no longer indexed.");
    }

    /// <summary>The <c>--json</c> form of <see cref="Warning"/>, as the one line written to standard error.</summary>
    public static string Event(LifecycleStateSnapshot snapshot)
    {
        var line = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(line))
        {
            System.Text.Json.JsonSerializer.Serialize(
                writer,
                new LifecycleStateSnapshotRestoreEvent(
                    DeploymentSchemaVersions.LifecycleEvent, LifecycleStateSnapshotRestoreEvent.Name, Warning(snapshot), snapshot),
                DeploymentJsonContext.Default.LifecycleStateSnapshotRestoreEvent);
        }
        return Encoding.UTF8.GetString(line.WrittenSpan);
    }

    public static Task ShowWarningAsync(LifecycleStateSnapshot snapshot, bool json, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.WriteLineAsync(json ? Event(snapshot) : Warning(snapshot));
    }

    /// <summary>
    /// Says that a restore of <paramref name="snapshot"/> could not read part of the state it replaces: what is named in
    /// <paramref name="unread"/> is carried only as far as configuration and the journal already recorded it.
    /// </summary>
    public static Task ShowRestoreIncompleteAsync(LifecycleStateSnapshot snapshot, string unread, bool json, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(error);
        var warning = string.Create(
            CultureInfo.InvariantCulture,
            $"Warning: the restore of the CameraAgent databases from snapshot {snapshot.SnapshotId:D} could not read {unread}. Counters in a store it could not read are carried only as far as the lifecycle journal recorded them, and a database under a storage root named only in an unreadable journal may be left in place; no file is touched.");
        if (!json) return error.WriteLineAsync(warning);
        var line = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(line))
        {
            System.Text.Json.JsonSerializer.Serialize(
                writer,
                new LifecycleStateSnapshotRestoreEvent(
                    DeploymentSchemaVersions.LifecycleEvent, LifecycleStateSnapshotRestoreEvent.IncompleteName, warning, snapshot),
                DeploymentJsonContext.Default.LifecycleStateSnapshotRestoreEvent);
        }
        return error.WriteLineAsync(Encoding.UTF8.GetString(line.WrittenSpan));
    }

    /// <summary>
    /// Says that a restore of <paramref name="snapshot"/> puts back what <paramref name="uncarried"/> names without carrying
    /// its counters forward, and why; the databases themselves are restored.
    /// </summary>
    public static Task ShowRestoreUncarriedAsync(
        LifecycleStateSnapshot snapshot, IReadOnlyList<(string What, string Reason)> uncarried, bool json, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(uncarried);
        ArgumentNullException.ThrowIfNull(error);
        var list = string.Join("; ", uncarried.Select(static entry => $"{entry.What} ({Redaction.SafeDiagnostic(entry.Reason)})"));
        var warning = string.Create(
            CultureInfo.InvariantCulture,
            $"Warning: the restore of the CameraAgent databases from snapshot {snapshot.SnapshotId:D} could not carry {list}. Each is restored as the snapshot recorded it, so a sequence or version handed out since the snapshot may be handed out again; the restore itself completes.");
        if (!json) return error.WriteLineAsync(warning);
        var line = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(line))
        {
            System.Text.Json.JsonSerializer.Serialize(
                writer,
                new LifecycleStateSnapshotRestoreEvent(
                    DeploymentSchemaVersions.LifecycleEvent, LifecycleStateSnapshotRestoreEvent.IncompleteName, warning, snapshot),
                DeploymentJsonContext.Default.LifecycleStateSnapshotRestoreEvent);
        }
        return error.WriteLineAsync(Encoding.UTF8.GetString(line.WrittenSpan));
    }

    internal static string StorePath(string stateRoot, string relative)
        => Path.Combine(stateRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string SnapshotFile(string root, StateSnapshotDatabase database)
    {
        if (database.SnapshotFile is not { Length: > 0 } name || name != Path.GetFileName(name) || name.StartsWith('.'))
            throw new InstallerException($"The snapshot manifest names an invalid file for '{database.RelativePath}'.");
        return Path.Combine(root, name);
    }

    private static Stack<string> MissingAncestors(string stateRoot, string directory)
    {
        var missing = new Stack<string>();
        for (var current = directory;
             !string.Equals(current, stateRoot, StringComparison.Ordinal) && !Directory.Exists(current);
             current = Path.GetDirectoryName(current)!)
        {
            missing.Push(current);
        }
        return missing;
    }

    private static void EnsureRegularStore(string path, string relative)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            (info.Attributes & FileAttributes.Directory) != 0)
            throw new InstallerException($"The CameraAgent store '{relative}' is not a regular file.");
    }

    private static StoreFacts VerifyFile(string path, string relative)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(path).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            using var reader = integrity.ExecuteReader();
            var results = new List<string>();
            while (reader.Read()) results.Add(reader.GetString(0));
            if (results is not ["ok"])
                throw new InstallerException($"The snapshot of '{relative}' failed its integrity check.");
        }
        return ReadFacts(connection);
    }

    private static StoreFacts ReadFacts(SqliteConnection connection)
    {
        long userVersion;
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var schema = new StringBuilder();
        var tables = new List<string>();
        using (var objects = connection.CreateCommand())
        {
            objects.CommandText = "SELECT type, name, tbl_name, COALESCE(sql, '') FROM sqlite_schema ORDER BY type, name;";
            using var reader = objects.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                schema.Append(type).Append('\u001f').Append(name).Append('\u001f').Append(reader.GetString(2))
                    .Append('\u001f').Append(reader.GetString(3)).Append('\u001e');
                if (type == "table") tables.Add(name);
            }
        }
        var counts = new List<StateSnapshotTableCount>(tables.Count);
        foreach (var table in tables)
        {
            using var count = connection.CreateCommand();
#pragma warning disable CA2100 // The table name comes from sqlite_schema and is quoted as an identifier.
            count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
#pragma warning restore CA2100
            counts.Add(new StateSnapshotTableCount(table, Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture)));
        }
        return new StoreFacts(
            userVersion,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema.ToString()))),
            counts);
    }

    private static async Task<string> CopySyncedAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = SafeFileSystem.OpenRegularFileRead(source);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not provide the required flush-to-disk guarantee.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void SyncFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>What a snapshot is checked against: the user version, a hash of the schema, and every table's row count.</summary>
    private sealed record StoreFacts(long UserVersion, string SchemaSha256, IReadOnlyList<StateSnapshotTableCount> Tables)
    {
        public bool Equals(StoreFacts? other)
            => other is not null && UserVersion == other.UserVersion &&
               string.Equals(SchemaSha256, other.SchemaSha256, StringComparison.Ordinal) && Tables.SequenceEqual(other.Tables);

        public override int GetHashCode() => HashCode.Combine(UserVersion, SchemaSha256, Tables.Count);
    }
}
