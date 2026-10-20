namespace HVO.SkyMonitor.Deployment;

/// <summary>A narrow sudo handoff: only the fixed machine installer and its .previous, never an arbitrary target.</summary>
internal static class PrivilegedPathInstaller
{
    internal const string CommandName = "internal-path-installer";

    internal static async Task ApplyAsync(
        InstallationPaths paths, string sha256, bool remove, string? previousSha256, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "--", $"/proc/{Environment.ProcessId}/exe", CommandName, paths.ProductRoot,
            Path.GetFileName(paths.InstanceRoot), remove ? "remove" : "install", sha256, previousSha256 ?? "none"
        };
        var result = await PathPrograms.PrivilegedRunner.RunAsync("sudo", arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InstallerException($"Machine installer publication failed: {Redaction.SafeDiagnostic(result.StandardError)}");
    }

    internal static bool ValidArguments(string[] args)
        => args.Length == 6 && args[0] == CommandName && Guid.TryParseExact(args[2], "D", out var id) && id != Guid.Empty &&
           args[3] is "install" or "remove" && ReleaseManifest.Sha256Pattern().IsMatch(args[4]) &&
           (args[5] == "none" || ReleaseManifest.Sha256Pattern().IsMatch(args[5]));

    public static int RunInternal(string[] args)
    {
        if (NativeLinux.getuid() != 0 || !ValidArguments(args) ||
            !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out var uid) || uid == 0)
            return 2;
        try
        {
            return PrivilegedPreparation.PrepareUnderHostPolicy(HostPolicyLocation.Production, args[1], production =>
            {
                var paths = InstallationPaths.Create(production.Path, Guid.Parse(args[2]), ProductionCatalog.DefaultCatalogId);
                var ownerPath = args[3] == "remove" && !Directory.Exists(paths.InstanceRoot) ? paths.OperationsRoot : paths.InstanceRoot;
                SafeFileSystem.EnsureSafeExistingAncestors(ownerPath);
                var owner = NativeLinux.GetDirectoryIdentity(ownerPath);
                if (owner.Uid != uid) throw new InstallerException("The instance is not owned by the invoking runtime user.");
                ValidateMachineFolder();
                ApplyToTargetAsync(paths, PathPrograms.SystemInstaller, args[3] == "remove", args[4],
                    args[5] == "none" ? null : args[5], CancellationToken.None).GetAwaiter().GetResult();
                if (args[3] == "install")
                {
                    foreach (var file in new[] { PathPrograms.SystemInstaller, PathPrograms.SystemInstaller + ".previous" })
                    {
                        var node = NativeLinux.TryGetNodeIdentity(file);
                        if (node is null) continue;
                        if (node.Type != NativeLinux.RegularFileNodeType || node.LinkCount != 1)
                            throw new InstallerException("The machine installer must be a regular file without links.");
                        NativeLinux.ChangeOwner(file, 0, 0);
                        using var installed = SafeFileSystem.OpenRegularFileRead(file);
                        NativeLinux.FlushProgramFile(installed.SafeFileHandle);
                    }
                }
            });
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void ValidateMachineFolder()
    {
        var folder = Path.GetDirectoryName(PathPrograms.SystemInstaller)!;
        SafeFileSystem.EnsureSafeExistingAncestors(folder);
        for (string? current = folder; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current)) continue;
            var owner = NativeLinux.GetDirectoryIdentity(current);
            if (owner.Uid != 0 || (owner.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new InstallerException("The machine installer's folders must be root-owned and not writable by other users.");
        }
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            File.SetUnixFileMode(folder, PathPrograms.ProgramMode);
            PathPrograms.FlushProgramDirectories(folder);
        }
    }

    internal static async Task ApplyToTargetAsync(
        InstallationPaths paths, string target, bool remove, string sha256, string? previousSha256, CancellationToken cancellationToken)
    {
        if (!remove)
        {
            await PathPrograms.PublishAsync(PathPrograms.Cached(paths, sha256), target, sha256, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PathPrograms.RemoveMatchingAsync(target, sha256, cancellationToken).ConfigureAwait(false);
            if (previousSha256 is not null)
                await PathPrograms.RemoveMatchingAsync(target + ".previous", previousSha256, cancellationToken).ConfigureAwait(false);
        }
    }
}
