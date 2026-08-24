namespace HVO.SkyMonitor.Deployment;

internal static class PrivilegedPreparation
{
    public static async Task PrepareAsync(
        InstallationPaths paths,
        uint uid,
        uint gid,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        try
        {
            Prepare(paths, uid, gid, requireRoot: false);
            return;
        }
        catch (UnauthorizedAccessException) when (NativeLinux.getuid() != 0)
        {
        }

        var executable = $"/proc/{Environment.ProcessId}/exe";
        var result = await processRunner.RunAsync(
            "sudo",
            [
                "--", executable, "internal-prepare",
                "--product-root", paths.ProductRoot,
                "--instance-id", Path.GetFileName(paths.InstanceRoot),
                "--uid", uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--gid", gid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InstallerException($"Privileged product-root preparation failed: {Redaction.SafeDiagnostic(result.StandardError)}");
        }
        SafeFileSystem.EnsureSafeExistingAncestors(paths.InstanceRoot);
    }

    public static int RunInternal(string[] args)
    {
        if (args.Length != 9 || args[0] != "internal-prepare" || args[1] != "--product-root" ||
            args[3] != "--instance-id" || args[5] != "--uid" || args[7] != "--gid" ||
            NativeLinux.getuid() != 0 ||
            !Guid.TryParseExact(args[4], "D", out var instanceId) ||
            !uint.TryParse(args[6], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var uid) ||
            !uint.TryParse(args[8], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var gid) ||
            !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out var sudoUid) ||
            !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), out var sudoGid) ||
            uid != sudoUid || gid != sudoGid || uid == 0)
        {
            return 2;
        }

        var productRoot = Path.GetFullPath(args[2]);
        if (!string.Equals(productRoot, InstallRequest.DefaultProductRoot, StringComparison.Ordinal))
        {
            return 2;
        }
        Prepare(InstallationPaths.Create(productRoot, instanceId, ProductionCatalog.CatalogId), uid, gid, requireRoot: true);
        return 0;
    }

    private static void Prepare(InstallationPaths paths, uint uid, uint gid, bool requireRoot)
    {
        if (requireRoot && NativeLinux.getuid() != 0)
        {
            throw new InstallerException("Internal preparation requires root.");
        }

        if (!requireRoot)
        {
            foreach (var directory in new[]
            {
                paths.ProductRoot,
                Path.Combine(paths.ProductRoot, "cameraagents"),
                Path.Combine(paths.ProductRoot, "logichosts"),
                Path.Combine(paths.ProductRoot, "catalogs"),
                paths.InstanceRoot,
                paths.CatalogRoot,
                paths.OperationsRoot
            })
            {
                SafeFileSystem.CreateOwnerDirectory(directory);
            }
            return;
        }

        foreach (var directory in new[]
        {
            "/var/lib/hvo",
            paths.ProductRoot,
            Path.Combine(paths.ProductRoot, "cameraagents"),
            Path.Combine(paths.ProductRoot, "logichosts"),
            Path.Combine(paths.ProductRoot, "catalogs")
        })
        {
            CreateOrValidateRootDirectory(directory);
        }
        CreateOrValidateRuntimeDirectory(paths.InstanceRoot, uid, gid);
        CreateOrValidateRuntimeDirectory(paths.CatalogRoot, uid, gid);
        CreateOrValidateRuntimeDirectory(paths.OperationsRoot, uid, gid);
    }

    private static void CreateOrValidateRootDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            NativeLinux.ChangeOwner(path, 0, 0);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var identity = NativeLinux.GetDirectoryIdentity(path);
        if (identity.Uid != 0 || identity.Gid != 0 ||
            (identity.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InstallerException($"Shared namespace '{path}' must be root-owned and not writable by other users.");
        }
    }

    private static void CreateOrValidateRuntimeDirectory(string path, uint uid, uint gid)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            NativeLinux.ChangeOwner(path, uid, gid);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var identity = NativeLinux.GetDirectoryIdentity(path);
        if (identity.Uid != uid || identity.Gid != gid ||
            (identity.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InstallerException($"Runtime root '{path}' does not belong exclusively to the invoking runtime user.");
        }
    }
}
