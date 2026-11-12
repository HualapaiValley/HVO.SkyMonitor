using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal static partial class CameraAgentStateSnapshot
{
    internal const string RetainedIntegrityStore = "raw/.location/retained-integrity.v1.db";
    private static readonly string[] ProtectedLocations =
    [
        "raw/.location/deployment-location.v1.protected",
        "raw/.location/manual-deployment-location.v1.protected",
        "raw/.deployment-location.v1.identity",
        "raw/.location/retained-integrity.v1.protected"
    ];

    private static long ProtectedStateBytes(string stateRoot)
        => ProtectedLocations.Sum(relative => File.Exists(StorePath(stateRoot, relative))
            ? new FileInfo(StorePath(stateRoot, relative)).Length : 0L);

    private static async Task<IReadOnlyList<StateSnapshotFile>> SnapshotProtectedFilesAsync(
        InstallationPaths paths, string root, CancellationToken cancellationToken)
    {
        var files = new List<StateSnapshotFile>();
        for (var index = 0; index < ProtectedLocations.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = ProtectedLocations[index];
            var source = StorePath(paths.StateRoot, relative);
            SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(source)!);
            if (!File.Exists(source))
            {
                files.Add(new StateSnapshotFile(relative, false, null, 0, null));
                continue;
            }
            EnsureRegularStore(source, relative);
            var name = $"protected-{index}.bin";
            var destination = Path.Combine(root, name);
            var digest = await CopySyncedAsync(source, destination, cancellationToken).ConfigureAwait(false);
            if (await SafeFileSystem.ComputeSha256Async(source, cancellationToken).ConfigureAwait(false) != digest)
                throw new InstallerException("Protected location state changed while its stopped snapshot was taken.");
            NativeLinux.FlushDirectory(root);
            files.Add(new StateSnapshotFile(relative, true, name, new FileInfo(destination).Length, digest));
            StepCompleted($"snapshot-protected:{relative}");
        }
        return files;
    }

    private static string ProtectedSnapshotPath(string root, StateSnapshotFile file)
    {
        var index = Array.IndexOf(ProtectedLocations, file.RelativePath);
        if (index < 0 || file.SnapshotFile != $"protected-{index}.bin")
            throw new InstallerException("The paired protected-state snapshot names an invalid file.");
        return Path.Combine(root, file.SnapshotFile);
    }

    private static async Task VerifyProtectedFilesAsync(
        InstallationPaths paths, string root, IReadOnlyList<StateSnapshotFile>? files, CancellationToken cancellationToken)
    {
        if (files is null)
        {
            // Historical database-only snapshots remain readable for legacy state.
            // They cannot reconstruct a sealed location/ledger pair.
            if (IntegrityPathExists(StorePath(paths.StateRoot, RetainedIntegrityStore), directory: false) ||
                IntegrityPathExists(StorePath(paths.StateRoot, ProtectedLocations[^1]), directory: false))
                throw new InstallerException("This database-only snapshot predates paired retained-integrity state; restore a complete paired backup.");
            await VerifyCurrentLegacyHistoryAsync(paths, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (files.Count != ProtectedLocations.Length || files.Any(static file => file is null) || files.Select(static file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != files.Count ||
            files.Any(file => !ProtectedLocations.Contains(file.RelativePath, StringComparer.Ordinal)))
            throw new InstallerException("The paired protected-state snapshot has an incomplete inventory.");
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.Present)
            {
                if (file.SnapshotFile is not null || file.Length != 0 || file.Sha256 is not null)
                    throw new InstallerException("The paired protected-state snapshot has invalid absence evidence.");
                continue;
            }
            var path = ProtectedSnapshotPath(root, file);
            EnsureRegularStore(path, file.RelativePath);
            if (new FileInfo(path).Length != file.Length ||
                await SafeFileSystem.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false) != file.Sha256)
                throw new InstallerException("A paired protected-state snapshot file differs from its manifest.");
        }
    }

    private static async Task RestoreProtectedFilesAsync(
        InstallationPaths paths, string root, IReadOnlyList<StateSnapshotFile>? files, uint uid, uint gid, CancellationToken cancellationToken)
    {
        if (files is null) return; // VerifyProtectedFilesAsync already checked legacy eligibility.
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = StorePath(paths.StateRoot, file.RelativePath);
            var directory = Path.GetDirectoryName(target)!;
            SafeFileSystem.EnsureSafeExistingAncestors(directory);
            if (!file.Present)
            {
                if (!Directory.Exists(directory)) continue;
                if (File.Exists(target))
                {
                    EnsureRegularStore(target, file.RelativePath);
                    File.Delete(target); // Only recorded absence from a verified paired snapshot.
                }
                SafeFileSystem.FlushDirectoryChain(directory, paths.ProductRoot);
                StepCompleted($"restore-protected:{file.RelativePath}");
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
            if (File.Exists(target)) EnsureRegularStore(target, file.RelativePath);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.paired-restore.tmp");
            try
            {
                if (File.Exists(temporary))
                {
                    EnsureRegularStore(temporary, file.RelativePath);
                    File.Delete(temporary);
                }
                var copied = await CopySyncedAsync(ProtectedSnapshotPath(root, file), temporary, cancellationToken).ConfigureAwait(false);
                if (copied != file.Sha256) throw new InstallerException("The staged protected-state restore differs from its snapshot.");
                File.Move(temporary, target, overwrite: true);
                SafeFileSystem.FlushDirectoryChain(directory, paths.ProductRoot);
                if (await SafeFileSystem.ComputeSha256Async(target, cancellationToken).ConfigureAwait(false) != file.Sha256)
                    throw new InstallerException("The restored protected-state file differs from its snapshot.");
                StepCompleted($"restore-protected:{file.RelativePath}");
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    EnsureRegularStore(temporary, file.RelativePath);
                    File.Delete(temporary);
                }
            }
        }
    }
}
