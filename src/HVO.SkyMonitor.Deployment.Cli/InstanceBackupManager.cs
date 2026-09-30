using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Transactional image-swap snapshot, not a general capture-history backup.</summary>
internal static class InstanceBackupManager
{
    private const int SnapshotSchema = 2;
    private const long MaximumRecordBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan StepDeadline = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SnapshotDeadline = TimeSpan.FromMinutes(5);
    private static readonly string[] RecordDirectories =
        ["config/", "state/data-protection/", "state/provisioning/", "state/raw/.location/",
         "state/raw/index/", "state/archive/index/"];
    private static readonly string[] MutableRecordPaths =
        ["state/raw/.deployment-location.v1.identity", "state/raw/journal/projected-scene-stage.cursor"];
    private static readonly string[] MutableStorePaths =
    [
        "state/identity/cameraagent_identity.db",
        "state/raw/journal/raw-ingress.db",
        "state/raw/.automation/local-automations.db",
        "state/raw/.environment/environmental-observation-outbox.db",
        "state/raw/.fleet/fleet-status.db",
        "state/raw/outbox/artifact-outbox.db",
        "state/archive/outbox/artifact-outbox.db",
        "state/raw/evidence/execution-evidence-outbox.db"
    ];

    internal static void EnsureEligible(InstallationPaths paths, CancellationToken cancellationToken)
    {
        ValidateStateRoots(paths, cancellationToken);
        var selectedCount = 3 + MutableStorePaths.Count(relative => File.Exists(Path.Combine(paths.InstanceRoot, relative))) +
            MutableRecordPaths.Count(relative => File.Exists(Path.Combine(paths.InstanceRoot, relative)));
        long recordBytes = 0;
        foreach (var prefix in RecordDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(paths.InstanceRoot, prefix);
            if (!Directory.Exists(directory)) continue;
            SafeFileSystem.EnsureSafeExistingAncestors(directory);
            foreach (var (file, stream) in EnumerateRecordFiles(directory, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++selectedCount > 256)
                    throw new InstallerException("The deployment snapshot exceeds its 256-file inventory limit.");
                recordBytes = checked(recordBytes + stream.Length);
                if (recordBytes > MaximumRecordBytes)
                    throw new InstallerException("The deployment records exceed the bounded snapshot size.");
            }
        }
        if (selectedCount > 256)
            throw new InstallerException("The deployment snapshot exceeds its 256-file inventory limit.");
    }

    private static void ValidateStateRoots(InstallationPaths paths, CancellationToken cancellationToken)
    {
        string[] rawDirectories = [".automation", ".environment", ".fleet", ".location", "calibration",
            "derived", "evidence", "frames", "index", "journal", "outbox", "processing-quarantine",
            "processing-deletion-tombstones", "quarantine", "staging"];
        string[] archiveDirectories = ["derived", "frames", "index", "outbox"];
        foreach (var (rootName, directories) in new[] { ("raw", rawDirectories), ("archive", archiveDirectories) })
        {
            var root = Path.Combine(paths.StateRoot, rootName);
            if (NativeLinux.TryGetNodeIdentity(root) is null) continue;
            var parentIdentity = NativeLinux.GetNodeIdentity(paths.StateRoot);
            using var handle = NativeLinux.OpenDirectoryNoFollow(root);
            var owner = NativeLinux.GetOpenNodeIdentity(handle, root);
            if (owner.MountId != parentIdentity.MountId || owner.DeviceMajor != parentIdentity.DeviceMajor ||
                owner.DeviceMinor != parentIdentity.DeviceMinor || owner.Uid != parentIdentity.Uid ||
                owner.Gid != parentIdentity.Gid)
                throw new InstallerException($"The CameraAgent state root is mounted or foreign-owned: {rootName}.");
            foreach (var entry in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                var identity = NativeLinux.GetNodeIdentityAt(handle, name);
                var valid = directories.Contains(name, StringComparer.Ordinal)
                    ? identity.Type == 0x4000 && identity.LinkCount >= 2
                    : rootName == "raw" && name == ".deployment-location.v1.identity" &&
                      identity.Type == 0x8000 && identity.LinkCount == 1;
                if (!valid || identity.Uid != owner.Uid || identity.Gid != owner.Gid ||
                    (identity.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 ||
                    identity.MountId != owner.MountId || identity.DeviceMajor != owner.DeviceMajor ||
                    identity.DeviceMinor != owner.DeviceMinor)
                    throw new InstallerException($"The CameraAgent state root contains an unsupported entry: {rootName}/{name}.");
            }
        }
    }

    internal static long RequiredBytes(InstallationPaths paths, string? imageArchive)
    {
        // Candidate startup updates lane and retention state even when the raw journal's
        // schema is unchanged. Snapshot every mutable store before starting the candidate.
        long databaseBytes = 0;
        foreach (var relative in MutableStorePaths)
        {
            var source = Path.Combine(paths.InstanceRoot, relative);
            if (File.Exists(source)) databaseBytes = checked(databaseBytes + new FileInfo(source).Length);
            foreach (var suffix in new[] { "-wal", "-journal" })
                if (File.Exists(source + suffix)) databaseBytes = checked(databaseBytes + new FileInfo(source + suffix).Length);
        }
        foreach (var relative in RecordDirectories)
        {
            var root = Path.Combine(paths.InstanceRoot, relative);
            if (!Directory.Exists(root)) continue;
            foreach (var (_, stream) in EnumerateRecordFiles(root, CancellationToken.None))
                databaseBytes = checked(databaseBytes + stream.Length);
        }
        var archiveBytes = imageArchive is null ? 0 : new FileInfo(imageArchive).Length;
        return checked(databaseBytes * 2 + archiveBytes + 256L * 1024 * 1024);
    }

    public static async Task<(InstanceBackupManifest Manifest, string ManifestSha256)> CreateAsync(
        InstallationPaths paths,
        InstanceManifest instance,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var backupId = Guid.NewGuid();
        SafeFileSystem.CreateOwnerDirectory(paths.BackupsRoot);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SnapshotDeadline);
        EnsureEligible(paths, budget.Token);
        var backupRoot = Path.Combine(paths.BackupsRoot, backupId.ToString("D"));
        SafeFileSystem.CreateOwnerDirectory(backupRoot);
        try
        {
            foreach (var root in new[] { paths.ConfigRoot, paths.StateRoot })
            {
                SafeFileSystem.EnsureSafeExistingAncestors(root);
                var identity = NativeLinux.GetDirectoryIdentity(root);
                if (identity.Uid != instance.RuntimeUid || identity.Gid != instance.RuntimeGid ||
                    identity.Mode != SafeFileSystem.OwnerDirectoryMode)
                    throw new InstallerException("The instance snapshot root changed ownership or permissions.");
            }
            var files = new List<BackupFileIdentity>();
            foreach (var (source, relative) in new[]
                     {
                         (paths.ManifestPath, "instance-manifest.json"),
                         (paths.ApplicationIdentityPath, "application-identity.json"),
                         (paths.ResultPath, "state/deployment/installation-result.json")
                     })
            {
                budget.Token.ThrowIfCancellationRequested();
                await CopyAsync(source, relative).ConfigureAwait(false);
            }
            foreach (var directory in RecordDirectories)
            {
                var root = Path.Combine(paths.InstanceRoot, directory);
                if (!Directory.Exists(root)) continue;
                SafeFileSystem.EnsureSafeExistingAncestors(root);
                foreach (var (source, stream) in EnumerateRecordFiles(root, budget.Token))
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(paths.InstanceRoot, source);
                    var destination = Path.Combine(backupRoot, relative);
                    var copiedHash = await SafeFileSystem.CopyPrivateFileAsync(stream, destination, budget.Token).ConfigureAwait(false);
                    files.Add(new BackupFileIdentity(relative, new FileInfo(destination).Length, copiedHash));
                }
            }
            foreach (var relative in MutableRecordPaths)
            {
                var path = Path.Combine(paths.InstanceRoot, relative);
                if (File.Exists(path)) await CopyAsync(path, relative).ConfigureAwait(false);
            }

            foreach (var relative in MutableStorePaths)
            {
                budget.Token.ThrowIfCancellationRequested();
                var identity = Path.Combine(paths.InstanceRoot, relative);
                if (!File.Exists(identity)) continue;
                var destination = Path.Combine(backupRoot, relative);
                SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(destination)!);
                SafeFileSystem.EnsureSafeExistingAncestors(identity);
                using (var input = SafeFileSystem.OpenRegularFileRead(identity))
                {
                    using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = identity,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                        Cache = SqliteCacheMode.Private
                    }.ToString());
                    using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = destination,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                        Pooling = false,
                        Cache = SqliteCacheMode.Private
                    }.ToString());
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    deadline.CancelAfter(StepDeadline);
                    await source.OpenAsync(deadline.Token).ConfigureAwait(false);
                    await target.OpenAsync(deadline.Token).ConfigureAwait(false);
                    // Bound the time SQLite can wait inside any one native step; the overall
                    // deadline is checked again before each retry and each page batch.
                    if (SQLitePCL.raw.sqlite3_busy_timeout(source.Handle!, 1000) != SQLitePCL.raw.SQLITE_OK ||
                        SQLitePCL.raw.sqlite3_busy_timeout(target.Handle!, 1000) != SQLitePCL.raw.SQLITE_OK)
                        throw new InstallerException("SQLite could not bound state snapshot lock waits.");
                    // Managed BackupDatabase performs one uninterruptible backup_step(-1).
                    // Step in bounded batches so cancellation is checked without abandoning a
                    // native call or disposing live SQLite handles from another thread.
                    var backup = SQLitePCL.raw.sqlite3_backup_init(target.Handle!, "main", source.Handle!, "main");
                    if (backup is null) throw new InstallerException("SQLite could not initialize the state snapshot.");
                    var completed = false;
                    var finish = SQLitePCL.raw.SQLITE_OK;
                    try
                    {
                        while (!completed)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            var result = SQLitePCL.raw.sqlite3_backup_step(backup, 64);
                            if (result == SQLitePCL.raw.SQLITE_DONE) completed = true;
                            else if (result is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
                                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
                            else if (result != SQLitePCL.raw.SQLITE_OK)
                                throw new InstallerException("SQLite could not complete the state snapshot.");
                        }
                        deadline.Token.ThrowIfCancellationRequested();
                    }
                    finally
                    {
                        finish = SQLitePCL.raw.sqlite3_backup_finish(backup);
                    }
                    if (finish != SQLitePCL.raw.SQLITE_OK)
                        throw new InstallerException("SQLite could not finish the state snapshot.");
                }
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                // A backup is only published once its SQLite image has reached durable storage.
                await using (var durable = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
#pragma warning disable CA1849 // SQLite snapshot publication requires flush-to-disk, not just FlushAsync.
                    durable.Flush(flushToDisk: true);
#pragma warning restore CA1849
                }
                files.Add(new BackupFileIdentity(relative, new FileInfo(destination).Length,
                    await SafeFileSystem.ComputeSha256Async(destination, budget.Token).ConfigureAwait(false)));
            }

            if (files.Count > 256)
                throw new InstallerException("The deployment snapshot exceeds its 256-file inventory limit.");

            var manifest = new InstanceBackupManifest(
                SnapshotSchema, backupId, instance.InstanceId, operationId, DateTimeOffset.UtcNow,
                files.Single(file => file.RelativePath == "instance-manifest.json").Sha256,
                files.Single(file => file.RelativePath == "application-identity.json").Sha256,
                instance.ConfigurationSha256, instance.ComposeModelSha256, instance.Catalog, instance.Image,
                files, string.Empty, 0);
            var manifestPath = Path.Combine(backupRoot, "backup-manifest.json");
            await SafeFileSystem.WriteJsonAtomicAsync(manifestPath, manifest,
                DeploymentJsonContext.Default.InstanceBackupManifest, budget.Token).ConfigureAwait(false);
            var hash = await SafeFileSystem.ComputeSha256Async(manifestPath, budget.Token).ConfigureAwait(false);
            // File fsync alone does not make a newly created backup directory durable.
            foreach (var directory in Directory.EnumerateDirectories(backupRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length))
            {
                budget.Token.ThrowIfCancellationRequested();
                NativeLinux.FlushDirectory(directory);
            }
            NativeLinux.FlushDirectory(backupRoot);
            NativeLinux.FlushDirectory(paths.BackupsRoot);
            NativeLinux.FlushDirectory(Path.GetDirectoryName(paths.BackupsRoot)!);
            NativeLinux.FlushDirectory(Path.GetDirectoryName(Path.GetDirectoryName(paths.BackupsRoot)!)!);
            return (manifest, hash);

            async Task CopyAsync(string source, string relative)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                deadline.CancelAfter(StepDeadline);
                var destination = Path.Combine(backupRoot, relative);
                var hash = await SafeFileSystem.CopyPrivateFileAsync(source, destination, deadline.Token).ConfigureAwait(false);
                files.Add(new BackupFileIdentity(relative, new FileInfo(destination).Length, hash));
            }
        }
        catch
        {
            SafeTreeDeletion.DeleteChild(paths.BackupsRoot, backupId.ToString("D"), NativeLinux.getuid(), NativeLinux.getgid());
            throw;
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The iterator holds each authenticated file handle in a using scope until the caller finishes its current copy.")]
    private static IEnumerable<(string Path, FileStream Stream)> EnumerateRecordFiles(
        string root, CancellationToken cancellationToken)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(root);
        using var rootHandle = NativeLinux.OpenDirectoryNoFollow(root);
        var rootIdentity = NativeLinux.GetOpenNodeIdentity(rootHandle, root);
        using var parentHandle = NativeLinux.OpenDirectoryNoFollow(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!);
        var parentIdentity = NativeLinux.GetOpenNodeIdentity(parentHandle, root);
        if (rootIdentity.MountId != parentIdentity.MountId || rootIdentity.DeviceMajor != parentIdentity.DeviceMajor ||
            rootIdentity.DeviceMinor != parentIdentity.DeviceMinor || rootIdentity.Uid != parentIdentity.Uid ||
            rootIdentity.Gid != parentIdentity.Gid ||
            (rootIdentity.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new InstallerException("The deployment record root is mounted or writable by others.");
        var entries = 0;
        foreach (var file in Visit(rootHandle, root, rootIdentity)) yield return file;

        IEnumerable<(string Path, FileStream Stream)> Visit(
            SafeFileHandle directory, string path, UnixNodeIdentity expectedRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptorPath = $"/proc/self/fd/{directory.DangerousGetHandle().ToInt64()}";
            foreach (var entry in Directory.EnumerateFileSystemEntries(descriptorPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > 1024)
                    throw new InstallerException("The deployment record tree exceeds its entry limit.");
                var name = Path.GetFileName(entry);
                var identity = NativeLinux.GetNodeIdentityAt(directory, name);
                if (identity.Uid != expectedRoot.Uid || identity.Gid != expectedRoot.Gid ||
                    identity.MountId != expectedRoot.MountId ||
                    identity.DeviceMajor != expectedRoot.DeviceMajor ||
                    identity.DeviceMinor != expectedRoot.DeviceMinor)
                    throw new InstallerException("The deployment record tree contains a foreign-owned entry.");
                if (identity.Type == 0x4000)
                {
                    if ((identity.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                        throw new InstallerException("The deployment record directory is writable by others.");
                    using var child = NativeLinux.TryOpenDirectoryAt(directory, name)
                        ?? throw new InstallerException("The deployment record directory changed during inspection.");
                    if (NativeLinux.GetOpenNodeIdentity(child, name) != identity)
                        throw new InstallerException("The deployment record directory changed during inspection.");
                    foreach (var file in Visit(child, Path.Combine(path, name), expectedRoot)) yield return file;
                }
                else if (identity.Type == 0x8000 && identity.LinkCount == 1)
                {
                    using var handle = NativeLinux.OpenNodeAt(directory, name);
                    if (NativeLinux.GetOpenNodeIdentity(handle, name) != identity)
                        throw new InstallerException("The deployment record file changed during inspection.");
                    using var stream = new FileStream(
                        $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}",
                        FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
                    yield return (Path.Combine(path, name), stream);
                    if (NativeLinux.GetNodeIdentityAt(directory, name) != identity ||
                        NativeLinux.GetOpenNodeIdentity(handle, name) != identity)
                        throw new InstallerException("The deployment record file changed during snapshot copying.");
                }
                else
                {
                    throw new InstallerException("The deployment record tree contains a linked or special entry.");
                }
                // Restore materializes the walk before deleting any extra records.
            }
        }
    }

    public static async Task EnsureSupportedSnapshotAsync(
        InstallationPaths paths, LifecycleOperationState operation, CancellationToken cancellationToken)
    {
        if (operation.BackupManifestSha256 is null) return;
        if (!Directory.Exists(paths.BackupsRoot)) return;
        SafeFileSystem.EnsureSafeExistingAncestors(paths.BackupsRoot);
        foreach (var directory in Directory.EnumerateDirectories(paths.BackupsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out _) ||
                new DirectoryInfo(directory).LinkTarget is not null) continue;
            var path = Path.Combine(directory, "backup-manifest.json");
            if (!File.Exists(path) ||
                await SafeFileSystem.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false) != operation.BackupManifestSha256)
                continue;
            await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            var manifest = await JsonSerializer.DeserializeAsync(stream,
                DeploymentJsonContext.Default.InstanceBackupManifest, cancellationToken).ConfigureAwait(false);
            if (manifest is { SchemaVersion: 1 })
                throw new InstallerException("This interrupted operation has a legacy whole-instance archive, not a restorable SQLite snapshot; do not retry the image swap with this CLI. Use the original recovery procedure.");
            return;
        }
    }

    public static async Task RestoreStoresAsync(
        InstallationPaths paths, InstanceManifest instance, LifecycleOperationState operation, CancellationToken cancellationToken,
        Action<string>? afterStoreRestore = null)
    {
        if (operation.BackupManifestSha256 is null)
            throw new InstallerException("The mutated image operation has no verified state snapshot.");
        SafeFileSystem.EnsureSafeExistingAncestors(paths.BackupsRoot);
        var matches = new List<(string Root, InstanceBackupManifest Manifest)>();
        foreach (var directory in Directory.EnumerateDirectories(paths.BackupsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out _) ||
                new DirectoryInfo(directory).LinkTarget is not null)
                continue;
            var path = Path.Combine(directory, "backup-manifest.json");
            if (!File.Exists(path)) continue;
            if (await SafeFileSystem.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false) != operation.BackupManifestSha256)
                continue;
            await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            var manifest = await JsonSerializer.DeserializeAsync(stream,
                DeploymentJsonContext.Default.InstanceBackupManifest, cancellationToken).ConfigureAwait(false);
            if (manifest is { SchemaVersion: 1 })
                throw new InstallerException("This interrupted operation has a legacy whole-instance archive, not a restorable SQLite snapshot; do not retry the image swap with this CLI. Use the original recovery procedure.");
            if (manifest is not { SchemaVersion: SnapshotSchema } || manifest.OperationId != operation.OperationId ||
                manifest.InstanceId != instance.InstanceId || manifest.Image != instance.Image)
                throw new InstallerException("The retained state snapshot does not belong to this image operation.");
            matches.Add((Path.GetDirectoryName(path)!, manifest));
        }
        if (matches.Count != 1) throw new InstallerException("The image operation has no unique authenticated state snapshot.");
        var (root, snapshot) = matches[0];
        if (snapshot.BackupId.ToString("D") != Path.GetFileName(root) ||
            snapshot.Files is null || snapshot.Files.Count < 3 || snapshot.Files.Count > 256 ||
            snapshot.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != snapshot.Files.Count ||
            !snapshot.Files.Any(file => file.RelativePath == "instance-manifest.json" && file.Sha256 == snapshot.InstanceManifestSha256) ||
            !snapshot.Files.Any(file => file.RelativePath == "application-identity.json" && file.Sha256 == snapshot.ApplicationIdentitySha256) ||
            !snapshot.Files.Any(file => file.RelativePath == "state/deployment/installation-result.json") ||
            snapshot.Files.Any(file => file.Length < 0 || file.Sha256 is not { Length: 64 } ||
                !(file.RelativePath is "instance-manifest.json" or "application-identity.json" or
                    "state/deployment/installation-result.json") &&
                !MutableStorePaths.Contains(file.RelativePath, StringComparer.Ordinal) &&
                !MutableRecordPaths.Contains(file.RelativePath, StringComparer.Ordinal) &&
                !RecordDirectories
                    .Any(prefix => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal)) ||
                Path.IsPathRooted(file.RelativePath) || file.RelativePath.Split('/').Any(segment => segment is "" or "." or "..")))
            throw new InstallerException("The retained state snapshot inventory is invalid.");
        foreach (var file in snapshot.Files)
        {
            var source = Path.Combine(root, file.RelativePath);
            SafeFileSystem.EnsureSafeExistingAncestors(source);
            if (!File.Exists(source) || new FileInfo(source).Length != file.Length ||
                await SafeFileSystem.ComputeSha256Async(source, cancellationToken).ConfigureAwait(false) != file.Sha256)
                throw new InstallerException("A retained snapshot file is corrupt; the candidate remains stopped.");
        }
        ValidateStateRoots(paths, cancellationToken);
        // Validate all paths which recovery could replace or remove before changing the first one.
        var stateIdentity = NativeLinux.GetNodeIdentity(paths.StateRoot);
        foreach (var relative in MutableStorePaths)
        {
            var destination = Path.Combine(paths.InstanceRoot, relative);
            SafeFileSystem.EnsureSafeExistingAncestors(destination);
            var parent = Path.GetDirectoryName(destination)!;
            for (var directory = parent; directory != paths.StateRoot &&
                 directory.StartsWith(paths.StateRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                 directory = Path.GetDirectoryName(directory)!)
            {
                if (NativeLinux.TryGetNodeIdentity(directory) is not { } identity)
                    continue;
                if (identity.Type != 0x4000 || identity.Uid != stateIdentity.Uid || identity.Gid != stateIdentity.Gid ||
                    identity.MountId != stateIdentity.MountId || identity.DeviceMajor != stateIdentity.DeviceMajor ||
                    identity.DeviceMinor != stateIdentity.DeviceMinor)
                    throw new InstallerException("The SQLite restore destination crosses a mounted or foreign directory.");
            }
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var current = destination + suffix;
                if (NativeLinux.TryGetNodeIdentity(current) is not null)
                {
                    using var existing = SafeFileSystem.OpenRegularFileRead(current);
                }
            }
            var temporary = destination + ".restore-" + operation.OperationId.ToString("N");
            if (NativeLinux.TryGetNodeIdentity(temporary) is not null)
            {
                using var staged = SafeFileSystem.OpenRegularFileRead(temporary);
            }
        }
        foreach (var prefix in RecordDirectories)
        {
            var directory = Path.Combine(paths.InstanceRoot, prefix);
            SafeFileSystem.EnsureSafeExistingAncestors(directory);
            if (NativeLinux.TryGetNodeIdentity(directory) is { Type: not 0x4000 })
                throw new InstallerException("A deployment record directory was replaced with a non-directory entry.");
            if (Directory.Exists(directory)) foreach (var _ in EnumerateRecordFiles(directory, cancellationToken)) { }
            foreach (var file in snapshot.Files.Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var destination = Path.Combine(paths.InstanceRoot, file.RelativePath);
                SafeFileSystem.EnsureSafeExistingAncestors(destination);
                if (NativeLinux.TryGetNodeIdentity(destination) is not null)
                {
                    using var existing = SafeFileSystem.OpenRegularFileRead(destination);
                }
                var temporary = destination + ".restore-" + operation.OperationId.ToString("N");
                if (NativeLinux.TryGetNodeIdentity(temporary) is not null)
                {
                    using var staged = SafeFileSystem.OpenRegularFileRead(temporary);
                }
            }
        }
        foreach (var relative in MutableRecordPaths)
        {
            var path = Path.Combine(paths.InstanceRoot, relative);
            SafeFileSystem.EnsureSafeExistingAncestors(path);
            if (NativeLinux.TryGetNodeIdentity(path) is not null)
            {
                using var current = SafeFileSystem.OpenRegularFileRead(path);
            }
            var temporary = path + ".restore-" + operation.OperationId.ToString("N");
            if (NativeLinux.TryGetNodeIdentity(temporary) is not null)
            {
                using var staged = SafeFileSystem.OpenRegularFileRead(temporary);
            }
        }
        var restore = snapshot.Files.Where(file => MutableStorePaths.Contains(file.RelativePath, StringComparer.Ordinal)).ToArray();
        // Authenticate the entire set before changing any store. An absent store at snapshot time
        // must be removed again if the candidate initialized it before failing.
        foreach (var relative in MutableStorePaths)
        {
            var destination = Path.Combine(paths.InstanceRoot, relative);
            SafeFileSystem.EnsureSafeExistingAncestors(destination);
            var source = restore.FirstOrDefault(file => file.RelativePath == relative);
            var parent = Path.GetDirectoryName(destination)!;
            if (source is not null && !Directory.Exists(parent))
            {
                var missing = new Stack<string>();
                for (var directory = parent; !Directory.Exists(directory); directory = Path.GetDirectoryName(directory)!)
                    missing.Push(directory);
                while (missing.TryPop(out var directory))
                {
                    SafeFileSystem.CreateRuntimeDirectory(directory, instance.RuntimeUid, instance.RuntimeGid);
                    NativeLinux.FlushDirectory(Path.GetDirectoryName(directory)!);
                }
            }
            var temporary = destination + ".restore-" + operation.OperationId.ToString("N");
            try
            {
                // An interrupted earlier attempt may have left this owned partial publication.
                // Never follow or overwrite it; remove it before making a fresh verified copy.
                if (File.Exists(temporary))
                {
                    using var prior = SafeFileSystem.OpenRegularFileRead(temporary);
                    File.Delete(temporary);
                }
                if (source is not null)
                {
                    await SafeFileSystem.CopyPrivateFileAsync(Path.Combine(root, relative), temporary, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (!Directory.Exists(parent)) continue;
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    var sidecar = destination + suffix;
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }
                if (source is not null) File.Move(temporary, destination, overwrite: true);
                else if (File.Exists(destination)) File.Delete(destination);
                if (Directory.Exists(Path.GetDirectoryName(destination)!))
                    NativeLinux.FlushDirectory(Path.GetDirectoryName(destination)!);
                afterStoreRestore?.Invoke(relative);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        foreach (var prefix in RecordDirectories)
        {
            var directory = Path.Combine(paths.InstanceRoot, prefix);
            SafeFileSystem.EnsureSafeExistingAncestors(directory);
            var retained = snapshot.Files.Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
            if (!Directory.Exists(directory) && retained.Count != 0)
            {
                var missing = new Stack<string>();
                for (var ancestor = directory; !Directory.Exists(ancestor); ancestor = Path.GetDirectoryName(ancestor)!)
                    missing.Push(ancestor);
                while (missing.TryPop(out var ancestor))
                {
                    SafeFileSystem.CreateRuntimeDirectory(ancestor, instance.RuntimeUid, instance.RuntimeGid);
                    NativeLinux.FlushDirectory(Path.GetDirectoryName(ancestor)!);
                }
            }
            if (Directory.Exists(directory)) foreach (var current in EnumerateRecordFiles(directory, cancellationToken)
                .Select(static item => item.Path).ToArray())
            {
                SafeFileSystem.EnsureSafeExistingAncestors(current);
                var relative = Path.GetRelativePath(paths.InstanceRoot, current);
                if (!retained.ContainsKey(relative))
                {
                    using var existing = SafeFileSystem.OpenRegularFileRead(current);
                    File.Delete(current);
                    NativeLinux.FlushDirectory(Path.GetDirectoryName(current)!);
                }
            }
            foreach (var file in retained.Values)
            {
                var destination = Path.Combine(paths.InstanceRoot, file.RelativePath);
                SafeFileSystem.EnsureSafeExistingAncestors(destination);
                var parent = Path.GetDirectoryName(destination)!;
                if (!Directory.Exists(parent))
                {
                    var missing = new Stack<string>();
                    for (var ancestor = parent; !Directory.Exists(ancestor); ancestor = Path.GetDirectoryName(ancestor)!)
                        missing.Push(ancestor);
                    while (missing.TryPop(out var ancestor))
                    {
                        SafeFileSystem.CreateRuntimeDirectory(ancestor, instance.RuntimeUid, instance.RuntimeGid);
                        NativeLinux.FlushDirectory(Path.GetDirectoryName(ancestor)!);
                    }
                }
                var temporary = destination + ".restore-" + operation.OperationId.ToString("N");
                if (File.Exists(temporary))
                {
                    using var prior = SafeFileSystem.OpenRegularFileRead(temporary);
                    File.Delete(temporary);
                }
                try
                {
                    await SafeFileSystem.CopyPrivateFileAsync(Path.Combine(root, file.RelativePath), temporary, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temporary, destination, overwrite: true);
                    NativeLinux.FlushDirectory(parent);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
        foreach (var relative in MutableRecordPaths)
        {
            var path = Path.Combine(paths.InstanceRoot, relative);
            SafeFileSystem.EnsureSafeExistingAncestors(path);
            var saved = snapshot.Files.SingleOrDefault(file => file.RelativePath == relative);
            if (saved is null)
            {
                if (File.Exists(path))
                {
                    using var existing = SafeFileSystem.OpenRegularFileRead(path);
                    File.Delete(path);
                    NativeLinux.FlushDirectory(Path.GetDirectoryName(path)!);
                }
                continue;
            }
            var parent = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent))
            {
                var missing = new Stack<string>();
                for (var directory = parent; !Directory.Exists(directory); directory = Path.GetDirectoryName(directory)!)
                    missing.Push(directory);
                while (missing.TryPop(out var directory))
                {
                    SafeFileSystem.CreateRuntimeDirectory(directory, instance.RuntimeUid, instance.RuntimeGid);
                    NativeLinux.FlushDirectory(Path.GetDirectoryName(directory)!);
                }
            }
            var temporary = path + ".restore-" + operation.OperationId.ToString("N");
            try
            {
                if (File.Exists(temporary))
                {
                    using var prior = SafeFileSystem.OpenRegularFileRead(temporary);
                    File.Delete(temporary);
                }
                await SafeFileSystem.CopyPrivateFileAsync(Path.Combine(root, relative), temporary,
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
                NativeLinux.FlushDirectory(Path.GetDirectoryName(path)!);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
