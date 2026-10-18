using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// <c>cameraagent backup</c> (#1353): the explicit full copy of an instance, its files and a verified snapshot of its
/// small databases, into one root-only, self-verifying archive (<see cref="BackupArchive"/>) in the backup folder
/// (<see cref="IBackupVault"/>). An image lifecycle operation never copies bulk data (install contract draft 4); this
/// command is the path that does.
/// </summary>
/// <remarks>
/// A running instance is paused, drained and stopped only while its snapshot and archive are written, and is started,
/// verified and resumed before the archive is read back. The archive is written as a partial, read back and proved
/// whole, and only then committed with its record, so a partial or unrecorded archive is never restorable. A failure or
/// cancellation discards the partial and puts the instance back as it was. An interruption is recovered by
/// <c>--resume</c>: before the archive is complete nothing is kept; after, it is verified and committed.
/// </remarks>
internal sealed partial class CameraAgentLifecycleManager
{
    /// <summary>The space a backup leaves free on each filesystem it writes, beyond what it needs.</summary>
    private const long BackupHeadroomBytes = 256L * 1024 * 1024;

    /// <summary>How long the recovery after a failed or cancelled backup may take to put the instance back.</summary>
    private static readonly TimeSpan BackupRecoveryTimeout = TimeSpan.FromMinutes(4);

    /// <summary>The host whose free space a backup or restore measures. Tests replace it; production leaves it.</summary>
    internal static IHostEnvironment BackupHost { get; set; } = LinuxHostEnvironment.Instance;

    /// <summary>
    /// Runs at each durable step of a backup or restore that no journal record marks. Crash-injection tests capture what
    /// a power loss at that moment would leave behind; production leaves it empty.
    /// </summary>
    internal static Action<string> BackupStepCompleted { get; set; } = static _ => { };

    /// <summary>
    /// What a backup decides before anything changes, for the command and its plan alike: that every entry of the
    /// instance is a file or a folder, and that there is room for the archive and the snapshot (#1353).
    /// </summary>
    private static async Task AdmitBackupAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        CancellationToken cancellationToken)
    {
        var stores = BackupStores(paths, manifest);
        var (items, _) = BackupInventory.CollectInstance(paths, stores);
        var vault = BackupVault.For(request);
        var partials = await vault.ListPartialsAsync(manifest.InstanceId, cancellationToken).ConfigureAwait(false);
        EnsureBackupCapacity(paths, manifest, vault, items, stores, partials);
    }

    /// <summary>
    /// The stores the backup's snapshot holds: those an upgrade snapshots. An inventory the installer cannot read refuses
    /// the backup before anything changes.
    /// </summary>
    private static IReadOnlyList<string> BackupStores(InstallationPaths paths, InstanceManifest manifest)
    {
        try
        {
            return CameraAgentStateInventory.Stores(paths, manifest.ReplayProfile);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused(exception.Message, exception);
        }
    }

    /// <summary>
    /// Requires room for the compressed archive at its worst case (no compression, every entry's tar overhead) and for the
    /// snapshot, which is also what the read-back verifies against, each with headroom, summed when they share a
    /// filesystem. The archive holds every snapshot it will contain: the one this backup takes, bounded by the stores'
    /// size, and the rollback snapshot the manifest keeps (#1353 PR-1369-R1 F9). A partial an interrupted backup of this
    /// instance left is discarded before the archive is written, so its bytes count as free.
    /// </summary>
    private static void EnsureBackupCapacity(
        InstallationPaths paths,
        InstanceManifest manifest,
        IBackupVault vault,
        IReadOnlyList<BackupItem> items,
        IReadOnlyList<string> stores,
        IReadOnlyList<BackupPartial> partials)
    {
        var snapshotBytes = CameraAgentStateSnapshot.StoreBytes(paths.StateRoot, stores);
        var archiveBytes = checked(BackupArchive.EstimateArchiveBytes([.. items, .. SnapshotItemsBound(paths, manifest, stores)]) + BackupHeadroomBytes);
        var snapshotRequired = checked(snapshotBytes + BackupHeadroomBytes);
        var archiveFileSystem = BackupHost.MeasureFileSystem(vault.Root);
        var snapshotFileSystem = BackupHost.MeasureFileSystem(paths.OperationsRoot);
        var reclaimed = partials.Sum(static partial => partial.Length);
        if (archiveFileSystem.FileSystem == snapshotFileSystem.FileSystem)
        {
            Require(archiveFileSystem, checked(archiveBytes + snapshotRequired), reclaimed, "the archive and the database snapshot");
            return;
        }
        Require(archiveFileSystem, archiveBytes, reclaimed, "the archive");
        Require(snapshotFileSystem, snapshotRequired, 0, "the database snapshot");

        static void Require(HostFileSystemMeasurement measured, long required, long reclaimed, string what)
        {
            if (checked(measured.AvailableBytes + reclaimed) < required)
            {
                throw InstallerException.Refused(string.Create(
                    CultureInfo.InvariantCulture,
                    $"There is not enough free space for {what} on the filesystem holding {measured.MeasuredPath}: {FormatBytes(required)} is needed and {FormatBytes(measured.AvailableBytes + reclaimed)} is free. Free space there, then run the backup again."));
            }
        }
    }

    /// <summary>
    /// The snapshot entries an archive can hold at most: one per store, as large as the store and its recovery files are
    /// now, a manifest, and every file of the rollback snapshot the manifest keeps.
    /// </summary>
    private static IEnumerable<BackupItem> SnapshotItemsBound(InstallationPaths paths, InstanceManifest manifest, IReadOnlyList<string> stores)
    {
        const long ManifestBound = 1024 * 1024;
        yield return new BackupItem(BackupArchive.SnapshotPrefix + "manifest.json", null, Directory: false, UnixFileMode.UserRead, ManifestBound);
        foreach (var store in stores)
        {
            yield return new BackupItem(BackupArchive.SnapshotPrefix + store, null, Directory: false, UnixFileMode.UserRead,
                CameraAgentStateSnapshot.StoreBytes(paths.StateRoot, [store]));
        }
        if (manifest.PreviousStateSnapshot is not { } previous) yield break;
        var root = CameraAgentStateSnapshot.SnapshotRoot(paths, previous.OperationId, previous.SnapshotId);
        if (!Directory.Exists(root)) yield break;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles())
        {
            yield return new BackupItem(BackupInventory.SnapshotPrefix(previous) + file.Name, null, Directory: false, UnixFileMode.UserRead, file.Length);
        }
    }

    private static async Task<LifecycleResult> BackupAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult installationResult,
        ComposeFiles compose,
        DockerClient docker,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        string lifecycleControlToken,
        string verificationToken,
        DockerDaemonIdentity daemon,
        HostPrerequisiteReport? hostLimits,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var vault = BackupVault.For(request);
        var stores = BackupStores(paths, manifest);
        var running = manifest.LifecycleCondition == InstanceLifecycleCondition.Installed &&
                      (await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false)).Running;
        var operation = await BeginAsync(request, paths, LifecycleOperationKind.Backup, manifest, cancellationToken)
            .ConfigureAwait(false);
        var name = BackupVaultFiles.NameFor(manifest.InstanceId, operation.StartedUtc, operation.OperationId);
        if (request.DryRun)
        {
            var (planItems, _) = BackupInventory.CollectInstance(paths, stores);
            var partials = await vault.ListPartialsAsync(manifest.InstanceId, cancellationToken).ConfigureAwait(false);
            var planned = Result(operation.Kind, "planned", operation.OperationId, paths, manifest, daemon, running, null);
            return planned with
            {
                Plan = BackupPlan(request, paths, manifest, vault, name, running, planItems, partials,
                    await CameraAgentStateSnapshotRetention.PlanAsync(paths, manifest, cancellationToken).ConfigureAwait(false))
            };
        }
        var service = new BackupService(
            paths, manifest, installationResult, compose, docker,
            CreateLifecycleClient(installationResult.Url, lifecycleClientFactory),
            ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url),
            lifecycleControlToken, verificationToken, hostLimits,
            Path.Combine(paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D")));
        // The limits the restart goes ahead without are kept in the operation's install log, as a rollback keeps them.
        await WriteHostLimitsToInstallLogAsync(request, paths, operation.OperationId, hostLimits, cancellationToken).ConfigureAwait(false);
        if (operation.MutationStarted)
        {
            return await ResumeBackupAsync(request, paths, manifest, vault, name, service, operation, daemon, error, cancellationToken)
                .ConfigureAwait(false);
        }

        // An earlier backup of this instance that was interrupted left its partial; it can never be restored, so it goes.
        foreach (var partial in await vault.ListPartialsAsync(manifest.InstanceId, cancellationToken).ConfigureAwait(false))
        {
            await vault.DiscardAsync(partial.Name, cancellationToken).ConfigureAwait(false);
        }
        var drainAttempted = false;
        try
        {
            if (running)
            {
                await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
                // Recorded before the pause, so an interruption anywhere after it is resumed by starting and resuming
                // the instance again; an instance that was not running is left as it was.
                var before = await service.Lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false);
                operation = await RecordAsync(paths, operation with
                {
                    MutationStarted = true,
                    PreMutationContinuity = ToBoundary(before)
                }, cancellationToken).ConfigureAwait(false);
                drainAttempted = true;
                var drained = await service.Lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleControlToken, cancellationToken)
                    .ConfigureAwait(false);
                operation = await RecordAsync(paths, operation with
                {
                    Phase = LifecycleOperationPhase.Drained,
                    PreMutationContinuity = ToBoundary(drained)
                }, cancellationToken).ConfigureAwait(false);
                EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
                await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["stop"], cancellationToken)
                    .ConfigureAwait(false);
                OwnerRecoverySocket.RemoveStoppedSocket(paths, manifest.RuntimeUid, manifest.RuntimeGid);
            }
            else
            {
                operation = await RecordAsync(paths, operation with { MutationStarted = true }, cancellationToken).ConfigureAwait(false);
            }
            var snapshot = await CameraAgentStateSnapshot.CreateAsync(
                    paths, manifest.InstanceId, operation.OperationId, manifest.Image, stores, cancellationToken)
                .ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.StateSnapshotRecorded,
                StateSnapshot = snapshot
            }, cancellationToken).ConfigureAwait(false);
            var (items, omitted) = await BackupInventory.CollectAsync(paths, manifest, stores, snapshot, cancellationToken)
                .ConfigureAwait(false);
            var releaseVersion = await ReleaseRecords.VersionOfAsync(paths, manifest.Image.ImageId, cancellationToken).ConfigureAwait(false);
            var progress = new BackupProgress(error, request.Json, "write", items.Sum(static item => item.Size));
            var (archiveSha256, _) = await WriteBackupArchiveAsync(
                    vault, name, items,
                    (entries, total) => new BackupArchiveManifest(
                        BackupArchive.SchemaVersion, BackupArchive.Product, BackupArchive.Component, manifest.InstanceId,
                        manifest.FriendlyName, operation.OperationId, operation.StartedUtc, InstallerBuildIdentity.Current,
                        paths.ProductRoot, manifest.RuntimeUid, manifest.RuntimeGid, manifest.Image.ImageId, releaseVersion,
                        manifest.Catalog.CatalogId, manifest.Catalog.PackageVersion, snapshot, entries, omitted, total),
                    progress, cancellationToken)
                .ConfigureAwait(false);
            // The archive is complete and durable: from here an interruption keeps it, verified and committed by --resume.
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.BackupRecorded,
                BackupManifestSha256 = archiveSha256
            }, cancellationToken).ConfigureAwait(false);
            if (operation.PreMutationContinuity is not null)
            {
                await service.RestartAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            }
            var record = await CommitBackupArchiveAsync(vault, name, operation, error, request.Json, cancellationToken).ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                .ConfigureAwait(false);
            operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return await BackupCompletedAsync(paths, manifest, vault, record, operation, daemon, running, request.Json, error)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (operation.Phase == LifecycleOperationPhase.Committed)
            {
                throw new InstallerException(
                    "The backup is committed, but its completion was not recorded; rerun the same command with --resume.", exception);
            }
            using var recovery = new CancellationTokenSource(BackupRecoveryTimeout);
            var (cleanupFailure, restartFailure) = await RecoverBackupAsync(
                    vault, name, service, operation, drainAttempted || operation.PreMutationContinuity is not null, recovery.Token)
                .ConfigureAwait(false);
            if (cleanupFailure is not null || restartFailure is not null)
            {
                // What is left stays journaled as mutated, so --resume finishes it.
                throw new InstallerException(
                    RecoveryFailureMessage($"The backup failed ({Redaction.SafeDiagnostic(exception.Message)})", cleanupFailure, restartFailure,
                        drainAttempted || operation.PreMutationContinuity is not null),
                    exception);
            }
            operation = operation with { Phase = LifecycleOperationPhase.Prepared, MutationStarted = false };
            await FailAsync(paths, operation, exception, CancellationToken.None).ConfigureAwait(false);
            if (exception is OperationCanceledException) throw;
            throw new InstallerException(
                $"The backup failed, and nothing was kept; the instance is as it was. {SafeMessage(exception)}".TrimEnd(), exception);
        }
    }

    /// <summary>
    /// Finishes an interrupted backup. Before its archive was complete, nothing is kept: the partial is discarded, the
    /// instance is put back as it was, and the operation is settled as failed. After, the archive is read back, proved
    /// whole and committed, and the instance is put back.
    /// </summary>
    private static async Task<LifecycleResult> ResumeBackupAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        IBackupVault vault,
        string name,
        BackupService service,
        LifecycleOperationState operation,
        DockerDaemonIdentity daemon,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (operation.Phase is LifecycleOperationPhase.BackupRecorded or LifecycleOperationPhase.Committed)
        {
            if (operation.PreMutationContinuity is not null)
            {
                await service.RestartAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            }
            var record = await CommitBackupArchiveAsync(vault, name, operation, error, request.Json, cancellationToken).ConfigureAwait(false);
            if (operation.Phase != LifecycleOperationPhase.Committed)
            {
                operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                    .ConfigureAwait(false);
            }
            operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return await BackupCompletedAsync(
                    paths, manifest, vault, record, operation, daemon, operation.PreMutationContinuity is not null, request.Json, error)
                .ConfigureAwait(false);
        }
        var (cleanupFailure, restartFailure) = await RecoverBackupAsync(
                vault, name, service, operation, operation.PreMutationContinuity is not null, cancellationToken)
            .ConfigureAwait(false);
        if (cleanupFailure is not null || restartFailure is not null)
        {
            throw new InstallerException(RecoveryFailureMessage(
                "The backup was interrupted before its archive was complete", cleanupFailure, restartFailure, operation.PreMutationContinuity is not null));
        }
        var interrupted = new InstallerException(
            "The backup was interrupted before its archive was complete, so nothing was kept; the instance is as it was. Run the backup again.");
        await FailAsync(paths, operation with { Phase = LifecycleOperationPhase.Prepared, MutationStarted = false }, interrupted, cancellationToken)
            .ConfigureAwait(false);
        throw interrupted;
    }

    /// <summary>
    /// Discards the partial and, in its own <c>finally</c>, restarts and resumes the instance when the backup stopped it,
    /// so a cleanup that fails never leaves the instance stopped (#1353 PR-1369-R1 F4). Returns each failure.
    /// </summary>
    private static async Task<(Exception? Cleanup, Exception? Restart)> RecoverBackupAsync(
        IBackupVault vault,
        string name,
        BackupService service,
        LifecycleOperationState operation,
        bool restart,
        CancellationToken cancellationToken)
    {
        Exception? cleanupFailure = null;
        Exception? restartFailure = null;
        try
        {
            await vault.DiscardAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
        {
            cleanupFailure = cleanup;
        }
        finally
        {
            if (restart)
            {
                try
                {
                    await service.RestartAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    restartFailure = failure;
                }
            }
        }
        return (cleanupFailure, restartFailure);
    }

    private static string RecoveryFailureMessage(string cause, Exception? cleanupFailure, Exception? restartFailure, bool restarted)
    {
        var parts = new List<string> { cause };
        if (cleanupFailure is not null) parts.Add($"discarding its partial archive also failed ({Redaction.SafeDiagnostic(cleanupFailure.Message)})");
        if (restartFailure is not null) parts.Add($"restarting the instance also failed ({Redaction.SafeDiagnostic(restartFailure.Message)})");
        else if (restarted) parts.Add("the instance was restarted and resumed");
        return $"{string.Join("; ", parts)}. Rerun the same command with --resume to finish.";
    }

    /// <summary>
    /// Proves the archive whole and commits it: a partial is read back from the vault and its record written; an archive
    /// an interrupted commit already gave its name is checked against its record. Either way its bytes must be the ones
    /// the journal recorded when it was written.
    /// </summary>
    private static async Task<BackupRecord> CommitBackupArchiveAsync(
        IBackupVault vault,
        string name,
        LifecycleOperationState operation,
        TextWriter error,
        bool json,
        CancellationToken cancellationToken)
    {
        if (await vault.ReadRecordAsync(name, cancellationToken).ConfigureAwait(false) is { } committed)
        {
            if (committed.ArchiveSha256 != operation.BackupManifestSha256 || committed.OperationId != operation.OperationId)
                throw new InstallerException($"The committed backup '{name}' is not the archive this backup wrote.");
            return committed;
        }
        var (manifest, sha256, length) = await ReadBackupArchiveAsync(
                vault, name, partial: true, extract: null, new BackupProgress(error, json, "verify", null), cancellationToken)
            .ConfigureAwait(false);
        if (sha256 != operation.BackupManifestSha256 || manifest.OperationId != operation.OperationId)
        {
            throw new InstallerException($"The backup '{name}' read back differs from what was written, so it was not kept.");
        }
        var record = new BackupRecord(
            BackupRecord.CurrentSchemaVersion, name, manifest.InstanceId, manifest.OperationId, manifest.CreatedUtc, length, sha256,
            manifest.TotalBytes, manifest.Entries.Count, manifest.RuntimeUid, manifest.RuntimeGid);
        await vault.CommitAsync(record, cancellationToken).ConfigureAwait(false);
        BackupStepCompleted("backup-committed");
        return record;
    }

    private static async Task<LifecycleResult> BackupCompletedAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        IBackupVault vault,
        BackupRecord record,
        LifecycleOperationState operation,
        DockerDaemonIdentity daemon,
        bool running,
        bool json,
        TextWriter error)
    {
        var warnings = await PruneStateSnapshotsAsync(paths, manifest, json, error).ConfigureAwait(false);
        var completed = Result(operation.Kind, "completed", operation.OperationId, paths, manifest, daemon, running, running ? true : null);
        return completed with
        {
            Backup = new LifecycleBackup(
                record.Name, Path.Combine(vault.Root, record.Name), record.CreatedUtc, record.ArchiveLength, record.ArchiveSha256,
                record.TotalBytes, record.Entries),
            Warnings = warnings
        };
    }

    /// <summary>
    /// Writes the archive into a new partial, hashing every byte the vault receives, and makes it durable. Returns the
    /// archive's SHA-256 and length.
    /// </summary>
    private static async Task<(string Sha256, long Length)> WriteBackupArchiveAsync(
        IBackupVault vault,
        string name,
        IReadOnlyList<BackupItem> items,
        Func<IReadOnlyList<BackupArchiveEntry>, long, BackupArchiveManifest> createManifest,
        BackupProgress progress,
        CancellationToken cancellationToken)
    {
        string sha256;
        long length;
        await using (var transfer = await vault.WriteAsync(name, cancellationToken).ConfigureAwait(false))
        {
            await using var hashing = new Sha256Stream(transfer.Stream, leaveOpen: true);
            await BackupArchive.WriteAsync(hashing, items, createManifest, progress.Report, cancellationToken).ConfigureAwait(false);
            await hashing.FlushAsync(cancellationToken).ConfigureAwait(false);
            await transfer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            (sha256, length) = (hashing.Sha256(), hashing.Bytes);
        }
        progress.Finish();
        BackupStepCompleted("archive-written");
        return (sha256, length);
    }

    /// <summary>
    /// Reads an archive from the vault and proves it whole (<see cref="BackupArchive.ReadAsync"/>), hashing every byte,
    /// the ones after its last entry included. A vault that could not serve it says why.
    /// </summary>
    private static async Task<(BackupArchiveManifest Manifest, string Sha256, long Length)> ReadBackupArchiveAsync(
        IBackupVault vault,
        string name,
        bool partial,
        Func<string, bool, UnixFileMode, Stream?, CancellationToken, Task>? extract,
        BackupProgress progress,
        CancellationToken cancellationToken)
    {
        await using var transfer = await vault.ReadAsync(name, partial, cancellationToken).ConfigureAwait(false);
        await using var hashing = new Sha256Stream(transfer.Stream, leaveOpen: true);
        BackupArchiveManifest manifest;
        try
        {
            manifest = await BackupArchive.ReadAsync(hashing, extract, progress.Report, cancellationToken).ConfigureAwait(false);
            await hashing.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerException) when (!cancellationToken.IsCancellationRequested)
        {
            // A vault that failed mid-stream explains the archive's apparent damage better than the archive does.
            await transfer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        await transfer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        progress.Finish();
        return (manifest, hashing.Sha256(), hashing.Bytes);
    }

    /// <summary>
    /// Removes every database snapshot of this instance nothing refers to any more (<see cref="CameraAgentStateSnapshotRetention"/>),
    /// once the operation is complete. A snapshot it cannot remove is named in a warning and left for the next run.
    /// </summary>
    private static async Task<IReadOnlyList<InstallationWarning>?> PruneStateSnapshotsAsync(
        InstallationPaths paths, InstanceManifest manifest, bool json, TextWriter error)
    {
        try
        {
            await CameraAgentStateSnapshotRetention.PruneAsync(paths, manifest, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
        {
            var warning = new InstallationWarning(
                "state-snapshot-cleanup-incomplete",
                $"Database snapshots no longer needed were not all removed ({Redaction.SafeDiagnostic(exception.Message)}); the next backup, restore or upgrade removes them.");
            if (!json) await error.WriteLineAsync($"Warning: {warning.Message}").ConfigureAwait(false);
            return [warning];
        }
    }

    /// <summary>One event as the single line standard error carries with <c>--json</c>.</summary>
    internal static string JsonLine<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var line = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(line))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }
        return Encoding.UTF8.GetString(line.WrittenSpan);
    }

    internal static string FormatBytes(long bytes)
        => bytes >= 1L << 30
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GiB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MiB");

    /// <summary>
    /// What a backup does to the instance's service: a running instance is paused, drained and stopped for the snapshot
    /// and archive, then started, verified and resumed. Starting is idempotent, so a resumed backup always starts it.
    /// </summary>
    private sealed record BackupService(
        InstallationPaths Paths,
        InstanceManifest Manifest,
        InstallationResult Result,
        ComposeFiles Compose,
        DockerClient Docker,
        ICameraAgentLifecycleClient Lifecycle,
        IOwnerBootstrapClient Owner,
        string LifecycleControlToken,
        string VerificationToken,
        HostPrerequisiteReport? HostLimits,
        string OperationRoot)
    {
        /// <summary>
        /// Starts the instance as Docker here can run it, as every lifecycle start does (#1357,
        /// <see cref="StartOnThisHostAsync"/>), then verifies it and resumes capture. A host without CPU quota support was
        /// warned of, and its install log written, in <see cref="OperationRoot"/>, before the instance was stopped.
        /// </summary>
        public async Task RestartAsync(Guid operationId, CancellationToken cancellationToken)
        {
            EnsureDaemon(Manifest.DockerDaemon, await Docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await StartOnThisHostAsync(Docker, Compose, HostLimits, OperationRoot, cancellationToken).ConfigureAwait(false);
            await VerifyCandidateAsync(Owner, Docker, Compose, Paths, Manifest, Result, Manifest.Image, VerificationToken,
                Manifest.RuntimeUid, Manifest.RuntimeGid, cancellationToken).ConfigureAwait(false);
            await Lifecycle.ResumeAsync(operationId, LifecycleControlToken, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports how far a backup's write or read-back, or a restore's verification or extraction, has come, on standard
    /// error at most every two seconds and when it ends; with <c>--json</c> as <see cref="LifecycleBackupProgressEvent"/> lines.
    /// </summary>
    private sealed class BackupProgress(TextWriter error, bool json, string stage, long? totalBytes)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private TimeSpan last = TimeSpan.Zero;
        private long bytes;

        public void Report(long done, long total)
        {
            bytes = done;
            if (clock.Elapsed - last < Interval) return;
            last = clock.Elapsed;
            Write(done, total);
        }

        public void Report(long done) => Report(done, totalBytes ?? -1);

        public void Finish() => Write(bytes, totalBytes ?? bytes);

        private void Write(long done, long total)
        {
            if (json)
            {
                error.WriteLine(JsonLine(
                    new LifecycleBackupProgressEvent(
                        DeploymentSchemaVersions.LifecycleEvent, LifecycleBackupProgressEvent.Name, stage, done, total < 0 ? null : total),
                    DeploymentJsonContext.Default.LifecycleBackupProgressEvent));
                return;
            }
            var verb = stage switch
            {
                "write" => "Backed up",
                "verify" => "Verified",
                "extract" => "Restored",
                _ => "Read"
            };
            error.WriteLine(total > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{verb} {FormatBytes(done)} of {FormatBytes(total)} ({100.0 * done / total:0}%).")
                : string.Create(CultureInfo.InvariantCulture, $"{verb} {FormatBytes(done)}."));
        }
    }
}

/// <summary>Passes every byte read or written through to another stream, hashing it.</summary>
internal sealed class Sha256Stream(Stream inner, bool leaveOpen) : Stream
{
    private readonly System.Security.Cryptography.IncrementalHash hash =
        System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

    public long Bytes { get; private set; }

    public string Sha256() => Convert.ToHexStringLower(hash.GetCurrentHash());

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Bytes;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        hash.AppendData(buffer[..read]);
        Bytes += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        hash.AppendData(buffer.Span[..read]);
        Bytes += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        hash.AppendData(buffer);
        Bytes += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        hash.AppendData(buffer.Span);
        Bytes += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hash.Dispose();
            if (!leaveOpen) inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        hash.Dispose();
        if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
