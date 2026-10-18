using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// <c>cameraagent restore &lt;archive&gt;</c> (#1353): puts an instance back from a committed backup. The archive is read and
/// proved whole before anything changes, and refused (exit 3) when it is partial, unrecorded, damaged, or another
/// instance's, product root's or runtime user's. The target is an instance that is uninstalled and preserved, or absent;
/// an installed instance is refused, so a restore never replaces a running instance. The instance is restored
/// uninstalled, and <c>cameraagent reinstall</c> then starts it through the verified reinstall path.
/// </summary>
/// <remarks>
/// <para>The restore stages the archive's files beside the instance, in <c>.hvo-restore/&lt;operation&gt;</c> on the same
/// filesystem, verifying every byte again as it extracts them, and puts the backup's database snapshot where the
/// lifecycle keeps snapshots. It then swaps the staged files in, keeping what they replace until it completes, and puts the
/// databases back through #1340's staged, durable and verified snapshot restore (<see cref="RestoreStateSnapshotAsync"/>),
/// which carries the live counters past everything the replaced instance handed out.</para>
/// <para>Credentials the live instance revoked after the backup are revoked again (<see cref="RestoredCredentials"/>);
/// where there is no live store to compare, the restore warns loudly that they are active again. The release records
/// come back with the archive and are settled for the restored image, and the install record is settled from the
/// restored manifest, so <c>/etc/hvo-sky/install.json</c> stays a view of the authoritative records.</para>
/// <para>Each step a later one depends on is durable before the next, and the journal records the boundary, so
/// <c>--resume</c> completes an interrupted restore. Before the swap, a failure or cancellation removes what was staged
/// and changes nothing else; after it, the restore can only go forward, and <c>--resume</c> finishes it.</para>
/// </remarks>
internal sealed partial class CameraAgentLifecycleManager
{
    private const string InstanceManifestEntry = BackupArchive.InstancePrefix + "instance-manifest.json";
    private const string InstallationResultEntry = BackupArchive.InstancePrefix + "state/deployment/installation-result.json";
    private const string SwapPlanFileName = "swap.json";

    /// <summary>The most a captured instance record may hold.</summary>
    private const int MaximumCapturedRecordBytes = 4 * 1024 * 1024;

    private static async Task<LifecycleResult> RestoreAsync(
        LifecycleRequest request,
        IProcessRunner processRunner,
        uint uid,
        uint gid,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var instanceId = request.InstanceId!.Value;
        var name = request.Archive!;
        var vault = BackupVault.For(request.ProductRoot, request.ProductionProductRoot);
        var docker = new DockerClient(processRunner);
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.DefaultCatalogId);
        var archived = await AdmitRestoreAsync(request, vault, docker, paths, uid, gid, error, cancellationToken).ConfigureAwait(false);
        paths = paths.WithCatalog(archived.Manifest.Catalog.CatalogId);
        var daemon = archived.Manifest.DockerDaemon;
        if (request.DryRun)
        {
            var planned = await BeginAsync(request, paths, LifecycleOperationKind.Restore, archived.Manifest, cancellationToken)
                .ConfigureAwait(false);
            var plannedResult = Result(planned.Kind, "planned", planned.OperationId, paths,
                archived.Manifest with { LifecycleCondition = InstanceLifecycleCondition.Uninstalled }, daemon, false, false);
            return plannedResult with { Plan = RestorePlan(request, paths, archived, name, IsAbsentTarget(paths)) };
        }

        var written = ReadWrittenState(paths);
        try
        {
            if (IsAbsentTarget(paths))
            {
                await PrivilegedPreparation.PrepareAsync(paths, uid, gid, processRunner, cancellationToken).ConfigureAwait(false);
            }
            SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
            using var productLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
            using var instanceLock = OperationLock.Acquire(Path.Combine(paths.InstanceRoot, ".deployment.lock"), cancellationToken: cancellationToken);
            // Decided again under the locks: another run may have installed the instance since it was admitted.
            if (!request.Resume) EnsureRestorableTarget(paths, instanceId);
            var operation = await BeginAsync(request, paths, LifecycleOperationKind.Restore, archived.Manifest, cancellationToken)
                .ConfigureAwait(false);
            return await RestoreLockedAsync(request, paths, vault, name, archived, operation, daemon, error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InstallerException exception) when (exception.IsRefusal && ReadWrittenState(paths) != written)
        {
            throw new InstallerException(exception.Message, exception);
        }
    }

    private static async Task<LifecycleResult> RestoreLockedAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        IBackupVault vault,
        string name,
        ArchivedInstance archived,
        LifecycleOperationState operation,
        DockerDaemonIdentity daemon,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(paths.InstanceRoot, BackupInventory.RestoreFolderName, operation.OperationId.ToString("D"));
        var staged = Path.Combine(staging, "staged");
        var previous = Path.Combine(staging, "previous");
        var manifest = archived.Manifest;
        try
        {
            if (!operation.MutationStarted)
            {
                // The counters the instance being replaced handed out, carried past by the database restore; recorded
                // before anything is replaced, so a retry after a crash still knows them.
                var (observed, _) = Directory.Exists(paths.StateRoot)
                    ? CameraAgentStateCounters.ReadReadable(paths.StateRoot)
                    : ([], []);
                operation = await RecordAsync(paths, operation with
                {
                    MutationStarted = true,
                    StateCounterFloors = CameraAgentStateCounters.Merge(operation.StateCounterFloors, observed)
                }, cancellationToken).ConfigureAwait(false);
            }
            if (operation.Phase == LifecycleOperationPhase.Prepared)
            {
                await StageRestoreAsync(paths, vault, name, archived, staging, staged, error, request.Json, cancellationToken)
                    .ConfigureAwait(false);
                operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.CandidateValidated }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (operation.Phase is LifecycleOperationPhase.Prepared or LifecycleOperationPhase.CandidateValidated)
        {
            // Nothing of the instance has been replaced yet: what was staged goes, and the operation settles as failed.
            RemoveStaging(paths, staging);
            await FailAsync(paths, operation with { Phase = LifecycleOperationPhase.Prepared, MutationStarted = false }, exception, CancellationToken.None)
                .ConfigureAwait(false);
            if (exception is OperationCanceledException) throw;
            throw new InstallerException(
                $"The restore failed before it replaced anything, and the instance is as it was. {SafeMessage(exception)}".TrimEnd(), exception);
        }

        try
        {
            if (operation.Phase == LifecycleOperationPhase.CandidateValidated)
            {
                operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Mutating }, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (operation.Phase == LifecycleOperationPhase.Mutating)
            {
                SwapRestoredFiles(paths, staging, staged, previous);
                if (operation.StateSnapshotRestoredUtc is null)
                {
                    operation = await RestoreStateSnapshotAsync(
                            paths, manifest, operation, archived.Archive.StateSnapshot, request.Json, error, cancellationToken)
                        .ConfigureAwait(false);
                }
                var outcome = await RevokeAgainAsync(paths, previous, archived.Archive, request.Json, error).ConfigureAwait(false);
                operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.CandidateVerified }, cancellationToken)
                    .ConfigureAwait(false);
                archived = archived with { Credentials = outcome, CredentialsCompared = true };
            }
            var committed = manifest with
            {
                LifecycleCondition = InstanceLifecycleCondition.Uninstalled,
                LastLifecycleOperationId = operation.OperationId,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            if (operation.Phase == LifecycleOperationPhase.CandidateVerified)
            {
                // The release records came back with the archive; they are settled for the restored image, so the record of
                // the image the instance runs is the current one and the install record reads its versions from them.
                await ReleaseRecords.SettleAsync(paths, null, manifest.Image, cancellationToken).ConfigureAwait(false);
                await CatalogReferenceStore.PinHistoricalAsync(paths, manifest.Catalog, operation.OperationId, cancellationToken)
                    .ConfigureAwait(false);
                await SafeFileSystem.WriteJsonAtomicAsync(paths.ManifestPath, committed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
                    .ConfigureAwait(false);
                operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                committed = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            }
            await SettleInstallRecordAsync(request, paths, committed, operation.Kind, cancellationToken).ConfigureAwait(false);
            RemoveStaging(paths, staging);
            operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            var warnings = new List<InstallationWarning>(archived.Warnings);
            warnings.AddRange(await PruneStateSnapshotsAsync(paths, committed, request.Json, error).ConfigureAwait(false) ?? []);
            var completed = Result(operation.Kind, "completed", operation.OperationId, paths, committed, daemon, false, false);
            return completed with
            {
                // What the credential comparison did is reported only by the run that made it; a resumed run past it
                // reports nothing rather than guess.
                Restore = new LifecycleRestore(
                    name, archived.Archive.CreatedUtc, archived.Archive.OperationId,
                    archived.Credentials?.ApiKeysRevoked, archived.Credentials?.AccountsReset,
                    archived.CredentialsCompared ? archived.Credentials is null : null),
                Warnings = warnings.Count == 0 ? null : warnings
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The swap has begun, so the restore can only go forward; the staged copy and what it replaced are kept.
            throw new InstallerException(
                $"The restore stopped after it began replacing the instance: {SafeMessage(exception)} Rerun the same command with --resume to finish it.",
                exception);
        }
    }

    /// <summary>
    /// What a restore decides before anything changes, for the command and its plan alike: the archive is recorded,
    /// whole, and this instance's, product root's and runtime user's; the target is absent or uninstalled and preserved;
    /// the catalog the archive's instance needs is installed; Docker is the daemon the instance was installed against
    /// and holds no container of it; and there is room to stage the archive.
    /// </summary>
    private static async Task<ArchivedInstance> AdmitRestoreAsync(
        LifecycleRequest request,
        IBackupVault vault,
        DockerClient docker,
        InstallationPaths paths,
        uint uid,
        uint gid,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (uid == 0) throw InstallerException.Refused("Run lifecycle operations as the Docker-capable runtime user, not as root.");
        var name = request.Archive!;
        var instanceId = request.InstanceId!.Value;
        var record = await vault.ReadRecordAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw InstallerException.Refused(
                $"'{name}' is not a committed backup in {vault.Root}: a partial or unrecorded archive is never restored.");
        if (record.InstanceId != instanceId)
            throw InstallerException.Refused($"'{name}' is a backup of instance {record.InstanceId:D}, not {instanceId:D}.");

        var captured = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var (archive, sha256, length) = await ReadBackupArchiveAsync(
                vault, name, partial: false,
                async (entry, directory, _, data, token) =>
                {
                    if (directory || data is null || entry is not (InstanceManifestEntry or InstallationResultEntry)) return;
                    using var buffer = new MemoryStream();
                    var chunk = new byte[64 * 1024];
                    int read;
                    while ((read = await data.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                    {
                        if (buffer.Length + read > MaximumCapturedRecordBytes)
                            throw InstallerException.Refused($"The backup is not one the installer can restore: its {entry} is too large. Nothing was changed.");
                        await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    captured[entry] = buffer.ToArray();
                },
                new BackupProgress(error, request.Json, "verify", record.TotalBytes), cancellationToken)
            .ConfigureAwait(false);
        if (sha256 != record.ArchiveSha256 || length != record.ArchiveLength)
            throw InstallerException.Refused($"'{name}' is not the archive its record names, so it was not restored.");
        var manifest = await ArchivedRecordAsync(captured, InstanceManifestEntry, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
            .ConfigureAwait(false);
        _ = await ArchivedRecordAsync(captured, InstallationResultEntry, DeploymentJsonContext.Default.InstallationResult, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ValidateCanonicalManifest(manifest);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused($"The backup's instance manifest is not one this installer reads ({exception.Message})", exception);
        }
        if (manifest.InstanceId != instanceId || archive.InstanceId != instanceId || archive.OperationId != record.OperationId)
            throw InstallerException.Refused($"'{name}' does not hold instance {instanceId:D}.");
        if (!string.Equals(manifest.ProductRoot, paths.ProductRoot, StringComparison.Ordinal) ||
            !string.Equals(archive.ProductRoot, paths.ProductRoot, StringComparison.Ordinal))
            throw InstallerException.Refused($"'{name}' was taken under the product root {archive.ProductRoot}, not {paths.ProductRoot}; a restore keeps the product root.");
        if (manifest.RuntimeUid != uid || manifest.RuntimeGid != gid || archive.RuntimeUid != uid || archive.RuntimeGid != gid)
            throw InstallerException.Refused(string.Create(
                CultureInfo.InvariantCulture,
                $"'{name}' belongs to the runtime user {archive.RuntimeUid}:{archive.RuntimeGid}; run the restore as that user."));
        // The instance lock belongs to the running command and the staging folder to the restore, so an archive that
        // holds either, or a snapshot entry not in the one shape a snapshot has, is not one this installer wrote.
        if (archive.Entries.Any(static entry =>
                entry.Path.StartsWith(BackupArchive.InstancePrefix + ".deployment.lock", StringComparison.Ordinal) ||
                entry.Path.StartsWith(BackupArchive.InstancePrefix + BackupInventory.RestoreFolderName, StringComparison.Ordinal) ||
                entry.Path.StartsWith(BackupArchive.SnapshotPrefix, StringComparison.Ordinal) && BackupInventory.ParseSnapshotEntry(entry.Path) is null))
        {
            throw InstallerException.Refused("The backup is not one the installer can restore: it holds an entry a backup never holds. Nothing was changed.");
        }
        if (!archive.Entries.Any(entry => entry.Path == BackupInventory.SnapshotPrefix(archive.StateSnapshot) + "manifest.json"))
            throw InstallerException.Refused($"The backup is not one the installer can restore: it does not hold its database snapshot. Nothing was changed.");

        var catalogPaths = paths.WithCatalog(manifest.Catalog.CatalogId);
        try
        {
            ValidateCanonicalCatalog(catalogPaths, manifest.Catalog);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused(exception.Message, exception);
        }
        if (!Directory.Exists(manifest.Catalog.InstallRoot))
        {
            throw InstallerException.Refused(
                $"The restored instance needs catalog {manifest.Catalog.CatalogId} {manifest.Catalog.PackageVersion}, which is not installed. Install it with 'hvo-sky-install catalog install --catalog-bundle <bundle>', then run the restore again.");
        }
        var daemon = await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDaemon(manifest.DockerDaemon, daemon);
        }
        catch (InstallerException exception)
        {
            throw InstallerException.Refused(
                "The backup was taken against another Docker daemon; restoring an instance onto another Docker host is not supported.", exception);
        }
        if ((await docker.InspectContainerAsync($"hvo-skymonitor-{instanceId:N}", cancellationToken).ConfigureAwait(false)).Exists)
            throw InstallerException.Refused("The instance's container exists; uninstall the instance before restoring it.");

        if (!request.Resume)
        {
            EnsureRestorableTarget(catalogPaths, instanceId);
        }
        _ = await AdmitAsync(request, catalogPaths, LifecycleOperationKind.Restore, manifest, cancellationToken).ConfigureAwait(false);
        EnsureRestoreCapacity(catalogPaths, archive);
        return new ArchivedInstance(manifest, archive, [], null);
    }

    /// <summary>
    /// Requires the target to be absent, or uninstalled and preserved: a restore never replaces an installed instance.
    /// </summary>
    private static void EnsureRestorableTarget(InstallationPaths paths, Guid instanceId)
    {
        if (IsAbsentTarget(paths)) return;
        if (!File.Exists(paths.ManifestPath))
        {
            throw InstallerException.Refused(
                $"{paths.InstanceRoot} holds files but no instance manifest, so it is not a preserved instance; a restore does not replace it.");
        }
        var live = ReadManifestAsync(paths.ManifestPath, CancellationToken.None).GetAwaiter().GetResult();
        if (live.InstanceId != instanceId)
            throw InstallerException.Refused($"{paths.InstanceRoot} holds another instance.");
        if (live.LifecycleCondition != InstanceLifecycleCondition.Uninstalled)
        {
            throw InstallerException.Refused(
                $"Instance {instanceId:D} is installed; a restore never replaces an installed instance. Uninstall it first with 'hvo-sky-install cameraagent uninstall --instance-id {instanceId:D}'.");
        }
    }

    /// <summary>
    /// Whether nothing of the instance exists: no instance root, or one that holds nothing but its lock and a staging
    /// folder a restore that failed before it replaced anything left behind.
    /// </summary>
    private static bool IsAbsentTarget(InstallationPaths paths)
        => !Directory.Exists(paths.InstanceRoot) ||
           Directory.EnumerateFileSystemEntries(paths.InstanceRoot)
               .Select(Path.GetFileName)
               .All(static entry => entry is ".deployment.lock" or BackupInventory.RestoreFolderName);

    /// <summary>Requires room to stage every file of the archive beside the instance, with headroom.</summary>
    private static void EnsureRestoreCapacity(InstallationPaths paths, BackupArchiveManifest archive)
    {
        var required = checked(archive.TotalBytes + (long)archive.Entries.Count * 4096 + BackupHeadroomBytes);
        var measured = BackupHost.MeasureFileSystem(paths.InstanceRoot);
        if (measured.AvailableBytes < required)
        {
            throw InstallerException.Refused(string.Create(
                CultureInfo.InvariantCulture,
                $"There is not enough free space to restore the backup on the filesystem holding {measured.MeasuredPath}: {FormatBytes(required)} is needed and {FormatBytes(measured.AvailableBytes)} is free."));
        }
    }

    private static async Task<T> ArchivedRecordAsync<T>(
        Dictionary<string, byte[]> captured,
        string entry,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!captured.TryGetValue(entry, out var bytes))
            throw InstallerException.Refused($"The backup is not one the installer can restore: it does not hold {entry}. Nothing was changed.");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return await DeserializeRetainedAsync(stream, typeInfo, entry, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused($"The backup is not one the installer can restore: its {entry} is not valid. Nothing was changed.", exception);
        }
    }

    /// <summary>
    /// Extracts the archive a second time, verifying every byte again as it is written: the instance's files into
    /// <paramref name="staged"/>, and each snapshot into the staging folder, from where it moves to where the lifecycle
    /// keeps it once the whole archive has verified. A snapshot already there that verifies is kept. Everything staged
    /// is durable before the journal records it.
    /// </summary>
    private static async Task StageRestoreAsync(
        InstallationPaths paths,
        IBackupVault vault,
        string name,
        ArchivedInstance archived,
        string staging,
        string staged,
        TextWriter error,
        bool json,
        CancellationToken cancellationToken)
    {
        RemoveStaging(paths, staging);
        SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(staging)!);
        SafeFileSystem.CreateOwnerDirectory(staging);
        SafeFileSystem.CreateOwnerDirectory(staged);
        var snapshots = Path.Combine(staging, "snapshots");
        var directories = new List<(string Path, UnixFileMode Mode)>();
        await ReadBackupArchiveAsync(
                vault, name, partial: false,
                async (entry, directory, mode, data, token) =>
                {
                    string target;
                    if (entry.StartsWith(BackupArchive.InstancePrefix, StringComparison.Ordinal))
                    {
                        target = Path.Combine(staged, entry[BackupArchive.InstancePrefix.Length..].TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
                    }
                    else if (BackupInventory.ParseSnapshotEntry(entry) is { } snapshot)
                    {
                        target = Path.Combine(snapshots, snapshot.OperationId.ToString("D"), snapshot.SnapshotId.ToString("D"), snapshot.File);
                    }
                    else
                    {
                        throw InstallerException.Refused($"The backup is not one the installer can restore: it holds {entry}. Nothing was changed.");
                    }
                    if (directory)
                    {
                        Directory.CreateDirectory(target);
                        File.SetUnixFileMode(target, SafeFileSystem.OwnerDirectoryMode);
                        directories.Add((target, mode));
                        return;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var file = new FileStream(target, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = 1024 * 1024,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    });
                    await data!.CopyToAsync(file, token).ConfigureAwait(false);
                    await file.FlushAsync(token).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync does not provide the required flush-to-disk guarantee.
                    file.Flush(flushToDisk: true);
#pragma warning restore CA1849
                    File.SetUnixFileMode(target, mode);
                },
                new BackupProgress(error, json, "extract", archived.Archive.TotalBytes), cancellationToken)
            .ConfigureAwait(false);
        // Folders take their recorded modes once everything in them is written, deepest first.
        foreach (var (directory, mode) in directories.OrderByDescending(static entry => entry.Path.Length))
        {
            File.SetUnixFileMode(directory, mode);
        }
        SyncTree(staged);
        await PlaceSnapshotsAsync(paths, archived, snapshots, cancellationToken).ConfigureAwait(false);
        SafeFileSystem.FlushDirectoryChain(staging, paths.ProductRoot);
        BackupStepCompleted("restore-staged");
    }

    /// <summary>
    /// Moves each staged snapshot to where the lifecycle keeps it, unless one already there verifies against the
    /// reference the archive gives; the backup's own snapshot must then verify where it is.
    /// </summary>
    private static async Task PlaceSnapshotsAsync(
        InstallationPaths paths, ArchivedInstance archived, string snapshots, CancellationToken cancellationToken)
    {
        var references = new List<LifecycleStateSnapshot> { archived.Archive.StateSnapshot };
        if (archived.Manifest.PreviousStateSnapshot is { } previous && previous.SnapshotId != archived.Archive.StateSnapshot.SnapshotId)
            references.Add(previous);
        foreach (var reference in references)
        {
            var source = Path.Combine(snapshots, reference.OperationId.ToString("D"), reference.SnapshotId.ToString("D"));
            if (!Directory.Exists(source)) continue;
            var target = CameraAgentStateSnapshot.SnapshotRoot(paths, reference.OperationId, reference.SnapshotId);
            if (Directory.Exists(target))
            {
                try
                {
                    await CameraAgentStateSnapshot.VerifyAsync(paths, archived.Manifest.InstanceId, reference, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (InstallerException)
                {
                    SafeFileSystem.MakeTreeOwnerWritable(target);
                    Directory.Delete(target, recursive: true);
                }
            }
            foreach (var directory in new[]
                     {
                         Path.Combine(paths.OperationsRoot, "lifecycle"),
                         Path.Combine(paths.OperationsRoot, "lifecycle", reference.OperationId.ToString("D")),
                         Path.GetDirectoryName(target)!
                     })
            {
                SafeFileSystem.CreateOwnerDirectory(directory);
            }
            SyncTree(source);
            // The staging folder is under the instance root and the snapshots under the operations root; both are under
            // the product root, but they may be different filesystems, so a move that cannot rename copies.
            MoveTree(source, target);
            SafeFileSystem.FlushDirectoryChain(target, paths.ProductRoot);
        }
        await CameraAgentStateSnapshot.VerifyAsync(paths, archived.Manifest.InstanceId, archived.Archive.StateSnapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Swaps the staged files in. A durable swap plan names the archive's top-level entries; for each one, whatever is live
    /// moves to <paramref name="previous"/> before the staged entry moves in, and every other live entry moves to
    /// <paramref name="previous"/> too, so the instance root ends holding exactly the archive's files. Each move is a
    /// rename on one filesystem, so a repeat after a crash finishes it.
    /// </summary>
    private static void SwapRestoredFiles(InstallationPaths paths, string staging, string staged, string previous)
    {
        var planPath = Path.Combine(staging, SwapPlanFileName);
        string[] plan;
        if (File.Exists(planPath))
        {
            using var stream = SafeFileSystem.OpenOwnerFileRead(planPath);
            plan = JsonSerializer.Deserialize(stream, BackupVaultJsonContext.Default.StringArray)
                   ?? throw new InstallerException("The restore's swap plan is empty.");
        }
        else
        {
            plan = [.. Directory.EnumerateFileSystemEntries(staged).Select(static entry => Path.GetFileName(entry)!).Order(StringComparer.Ordinal)];
            SafeFileSystem.WriteTextAtomic(planPath, JsonSerializer.Serialize(plan, BackupVaultJsonContext.Default.StringArray));
        }
        SafeFileSystem.CreateOwnerDirectory(previous);
        foreach (var entry in plan)
        {
            var source = Path.Combine(staged, entry);
            var live = Path.Combine(paths.InstanceRoot, entry);
            var kept = Path.Combine(previous, entry);
            if (!Exists(source)) continue;
            if (Exists(live))
            {
                if (Exists(kept)) throw new InstallerException($"The restore cannot keep the replaced '{entry}': it was kept already.");
                Move(live, kept);
                NativeLinux.FlushDirectory(previous);
            }
            Move(source, live);
            NativeLinux.FlushDirectory(paths.InstanceRoot);
            BackupStepCompleted($"restore-swapped:{entry}");
        }
        foreach (var live in Directory.EnumerateFileSystemEntries(paths.InstanceRoot).Order(StringComparer.Ordinal))
        {
            var entry = Path.GetFileName(live)!;
            if (entry is ".deployment.lock" or BackupInventory.RestoreFolderName || plan.Contains(entry, StringComparer.Ordinal)) continue;
            Move(live, Path.Combine(previous, entry));
        }
        NativeLinux.FlushDirectory(previous);
        NativeLinux.FlushDirectory(paths.InstanceRoot);

        static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        static void Move(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to, overwrite: false);
        }
    }

    /// <summary>
    /// Revokes again what the live instance revoked after the backup, comparing the restored Identity database with the
    /// one the swap kept aside; with no live store, warns loudly instead. Returns what it did, or <see langword="null"/>
    /// when there was nothing to compare.
    /// </summary>
    private static async Task<RestoredCredentials.Outcome?> RevokeAgainAsync(
        InstallationPaths paths, string previous, BackupArchiveManifest archive, bool json, TextWriter error)
    {
        var relative = Path.Combine(CameraAgentStateLayout.IdentityDirectoryName, CameraAgentStateLayout.IdentityDatabaseFileName);
        var restored = Path.Combine(paths.StateRoot, relative);
        var live = Path.Combine(previous, "state", relative);
        if (!File.Exists(restored)) return new RestoredCredentials.Outcome(0, 0, 0);
        if (!File.Exists(live))
        {
            await ShowCredentialWarningAsync(
                    archive,
                    string.Create(CultureInfo.InvariantCulture,
                        $"WARNING: there was no live credential store to compare, so every API key and owner password that was valid when this backup was taken ({archive.CreatedUtc:yyyy-MM-ddTHH:mm:ssZ}) is valid again, including any revoked or changed since. Revoke the API keys you no longer trust and change the owner password before you expose the instance."),
                    json, error)
                .ConfigureAwait(false);
            return null;
        }
        var outcome = RestoredCredentials.Revoke(restored, live);
        if (outcome.ApiKeysRevoked > 0 || outcome.AccountsReset > 0 || outcome.AccountsNotLive > 0)
        {
            await ShowCredentialWarningAsync(
                    archive,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Warning: the backup ({archive.CreatedUtc:yyyy-MM-ddTHH:mm:ssZ}) held credentials the instance revoked since; the restore revoked them again: {outcome.ApiKeysRevoked} API key(s), and {outcome.AccountsReset} account password(s) kept as the live instance had them.{(outcome.AccountsNotLive > 0 ? $" {outcome.AccountsNotLive} account(s) the live instance no longer had were restored with their old passwords; review them." : string.Empty)}"),
                    json, error)
                .ConfigureAwait(false);
        }
        return outcome;
    }

    private static Task ShowCredentialWarningAsync(BackupArchiveManifest archive, string warning, bool json, TextWriter error)
        => error.WriteLineAsync(json
            ? JsonLine(
                new LifecycleCredentialsRestoredEvent(
                    DeploymentSchemaVersions.LifecycleEvent, LifecycleCredentialsRestoredEvent.Name, warning, archive.CreatedUtc),
                DeploymentJsonContext.Default.LifecycleCredentialsRestoredEvent)
            : warning);

    private static void RemoveStaging(InstallationPaths paths, string staging)
    {
        if (!Directory.Exists(staging)) return;
        SafeFileSystem.MakeTreeOwnerWritable(staging);
        Directory.Delete(staging, recursive: true);
        var parent = Path.GetDirectoryName(staging)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        NativeLinux.FlushDirectory(paths.InstanceRoot);
    }

    /// <summary>Makes every directory of a staged tree durable, deepest first, so each entry written into it survives a power loss.</summary>
    private static void SyncTree(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(static path => path.Length))
        {
            NativeLinux.FlushDirectory(directory);
        }
        NativeLinux.FlushDirectory(root);
    }

    private static void MoveTree(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            Directory.CreateDirectory(target);
            File.SetUnixFileMode(target, SafeFileSystem.OwnerDirectoryMode);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var destination = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, destination, overwrite: true);
                using (var stream = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite)) stream.Flush(flushToDisk: true);
            }
            NativeLinux.FlushDirectory(target);
            Directory.Delete(source, recursive: true);
        }
    }

    /// <summary>An archive admitted for restore: the instance manifest it holds, its own manifest, and what the restore found.</summary>
    private sealed record ArchivedInstance(
        InstanceManifest Manifest,
        BackupArchiveManifest Archive,
        IReadOnlyList<InstallationWarning> Warnings,
        RestoredCredentials.Outcome? Credentials,
        bool CredentialsCompared = false);
}
