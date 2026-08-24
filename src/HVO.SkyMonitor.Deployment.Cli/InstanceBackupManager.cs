using System.Security.Cryptography;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal static class InstanceBackupManager
{
    public static async Task<(InstanceBackupManifest Manifest, string ManifestSha256)> CreateAsync(
        InstallationPaths paths,
        InstanceManifest instance,
        Guid operationId,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var backupId = Guid.NewGuid();
        var backupRoot = Path.Combine(paths.BackupsRoot, backupId.ToString("D"));
        SafeFileSystem.CreateOwnerDirectory(paths.BackupsRoot);
        SafeFileSystem.CreateOwnerDirectory(backupRoot);
        SafeTreeDeletion.ValidateChild(paths.InstanceRoot, "config", instance.RuntimeUid, instance.RuntimeGid);
        SafeTreeDeletion.ValidateChild(paths.InstanceRoot, "state", instance.RuntimeUid, instance.RuntimeGid);
        var archivePath = Path.Combine(backupRoot, "instance-state.tar.gz");
        var result = await processRunner.RunAsync(
            "tar",
            ["--create", "--gzip", "--file", archivePath, "--directory", paths.InstanceRoot, "config", "state", "instance-manifest.json", "application-identity.json"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InstallerException("The consistent instance backup archive could not be created.");
        File.SetUnixFileMode(archivePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var validationRoot = Path.Combine(backupRoot, ".validation");
        SafeFileSystem.CreateOwnerDirectory(validationRoot);
        var extract = await processRunner.RunAsync(
            "tar",
            ["--extract", "--gzip", "--file", archivePath, "--directory", validationRoot, "--no-same-owner", "--no-same-permissions"],
            cancellationToken).ConfigureAwait(false);
        if (extract.ExitCode != 0) throw new InstallerException("The instance backup archive could not be independently extracted.");
        var expectedRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "config", "state", "instance-manifest.json", "application-identity.json"
        };
        if (Directory.EnumerateFileSystemEntries(validationRoot).Any(path => !expectedRoots.Contains(Path.GetFileName(path))))
            throw new InstallerException("The instance backup archive contains an unexpected root entry.");
        var files = await BuildInventoryAsync(validationRoot, cancellationToken).ConfigureAwait(false);
        await using var archive = SafeFileSystem.OpenOwnerFileRead(archivePath);
        var archiveSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, cancellationToken).ConfigureAwait(false));
        var manifest = new InstanceBackupManifest(
            DeploymentSchemaVersions.LifecycleOperation,
            backupId,
            instance.InstanceId,
            operationId,
            DateTimeOffset.UtcNow,
            await HashAsync(Path.Combine(validationRoot, "instance-manifest.json"), cancellationToken).ConfigureAwait(false),
            await HashAsync(Path.Combine(validationRoot, "application-identity.json"), cancellationToken).ConfigureAwait(false),
            instance.ConfigurationSha256,
            instance.ComposeModelSha256,
            instance.Catalog,
            instance.Image,
            files,
            archiveSha256,
            archive.Length);
        var manifestPath = Path.Combine(backupRoot, "backup-manifest.json");
        await SafeFileSystem.WriteJsonAtomicAsync(
            manifestPath, manifest, DeploymentJsonContext.Default.InstanceBackupManifest, cancellationToken).ConfigureAwait(false);
        SafeTreeDeletion.DeleteChild(backupRoot, ".validation", NativeLinux.getuid(), NativeLinux.getgid());
        return (manifest, await HashAsync(manifestPath, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IReadOnlyList<BackupFileIdentity>> BuildInventoryAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var files = new List<BackupFileIdentity>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InstallerException("The instance backup archive contains a linked entry.");
            if (Directory.Exists(path)) continue;
            await using var stream = SafeFileSystem.OpenRegularFileRead(path);
            if (NativeLinux.GetLinkCount(path) != 1)
                throw new InstallerException("The instance backup archive contains a hard-linked entry.");
            files.Add(new BackupFileIdentity(
                Path.GetRelativePath(root, path),
                stream.Length,
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))));
        }
        return files;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenRegularFileRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
