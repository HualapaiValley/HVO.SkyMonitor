using System.Globalization;

namespace HVO.SkyMonitor.Deployment;

internal static class PrivilegedPreparation
{
    internal const string CommandName = "internal-prepare";

    /// <summary>
    /// One privileged preparation request: the product root to prepare, the instance whose runtime root it also
    /// creates (none for product-level catalog operations), and the runtime identity that owns what it creates. With
    /// <paramref name="Backups"/> it creates only the backup folder (#1353 PR-1369-R3 F20).
    /// </summary>
    internal sealed record Invocation(string ProductRoot, Guid? InstanceId, uint Uid, uint Gid, bool Backups = false);

    /// <summary>
    /// Prepares the product root and one instance root, first without privilege and, only when the kernel denies that,
    /// through the narrow <c>sudo</c>-executed helper.
    /// </summary>
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

        await RunHelperAsync(
            new Invocation(paths.ProductRoot, Guid.ParseExact(Path.GetFileName(paths.InstanceRoot), "D"), uid, gid),
            processRunner,
            cancellationToken).ConfigureAwait(false);
        SafeFileSystem.EnsureSafeExistingAncestors(paths.InstanceRoot);
    }

    /// <summary>
    /// Prepares what product-level catalog operations write: the operations root and the side-by-side root of every
    /// approved catalog. An existing product root keeps the ownership and mode it was authenticated with; only missing
    /// directories are created. When the kernel denies that, because the shared namespace is root-owned, the narrow
    /// <c>sudo</c> helper creates them without creating any instance.
    /// </summary>
    public static async Task PrepareProductAsync(
        InstallationPaths paths,
        uint uid,
        uint gid,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        SafeFileSystem.EnsureSafeExistingAncestors(paths.ProductRoot);
        try
        {
            PrepareProductWithoutPrivilege(paths);
            return;
        }
        catch (UnauthorizedAccessException) when (NativeLinux.getuid() != 0)
        {
        }

        await RunHelperAsync(new Invocation(paths.ProductRoot, null, uid, gid), processRunner, cancellationToken)
            .ConfigureAwait(false);
        SafeFileSystem.EnsureSafeExistingAncestors(paths.OperationsRoot);
    }

    /// <summary>
    /// Creates the backup folder <c>&lt;product-root&gt;/backups</c>, owned by the service account
    /// <paramref name="uid"/>:<paramref name="gid"/> with mode 0700, when it is missing (#1353 PR-1369-R3 F20): first
    /// without privilege and, only when the kernel denies that because the product root is root's, through the narrow
    /// <c>sudo</c>-executed helper install uses. It creates nothing else, and a backup calls it before it changes anything.
    /// </summary>
    public static async Task PrepareBackupFolderAsync(
        string productRoot,
        uint uid,
        uint gid,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(productRoot, BackupVault.FolderName);
        if (Directory.Exists(folder))
        {
            return;
        }
        try
        {
            SafeFileSystem.CreateOwnerDirectory(folder);
            NativeLinux.FlushDirectory(productRoot);
            return;
        }
        catch (UnauthorizedAccessException) when (NativeLinux.getuid() != 0)
        {
        }

        await RunHelperAsync(new Invocation(productRoot, null, uid, gid, Backups: true), processRunner, cancellationToken)
            .ConfigureAwait(false);
        SafeFileSystem.EnsureSafeExistingAncestors(folder);
    }

    internal static IReadOnlyList<string> HelperArguments(Invocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var arguments = new List<string> { "--", $"/proc/{Environment.ProcessId}/exe", CommandName, "--product-root", invocation.ProductRoot };
        if (invocation.Backups)
        {
            arguments.Add("--backups");
        }
        else if (invocation.InstanceId is { } instanceId)
        {
            arguments.Add("--instance-id");
            arguments.Add(instanceId.ToString("D"));
        }
        arguments.Add("--uid");
        arguments.Add(invocation.Uid.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--gid");
        arguments.Add(invocation.Gid.ToString(CultureInfo.InvariantCulture));
        return arguments;
    }

    /// <summary>
    /// Parses exactly <c>internal-prepare --product-root &lt;root&gt; [--instance-id &lt;uuid&gt; | --backups] --uid
    /// &lt;uid&gt; --gid &lt;gid&gt;</c> with a non-root runtime UID.
    /// </summary>
    internal static bool TryParse(string[] args, out Invocation invocation)
    {
        ArgumentNullException.ThrowIfNull(args);
        invocation = null!;
        Guid? instanceId = null;
        var backups = false;
        int uidIndex;
        if (args.Length == 9 && args[3] == "--instance-id" && Guid.TryParseExact(args[4], "D", out var parsed))
        {
            instanceId = parsed;
            uidIndex = 5;
        }
        else if (args.Length == 8 && args[3] == "--backups")
        {
            backups = true;
            uidIndex = 4;
        }
        else if (args.Length == 7)
        {
            uidIndex = 3;
        }
        else
        {
            return false;
        }
        if (args[0] != CommandName || args[1] != "--product-root" ||
            args[uidIndex] != "--uid" || args[uidIndex + 2] != "--gid" ||
            !uint.TryParse(args[uidIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
            !uint.TryParse(args[uidIndex + 3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid) ||
            uid == 0)
        {
            return false;
        }
        invocation = new Invocation(args[2], instanceId, uid, gid, backups);
        return true;
    }

    /// <summary>
    /// The <c>sudo</c>-executed preparation. It re-reads and re-validates the host policy itself rather than trusting
    /// its arguments, and prepares only the production product root that policy resolves, holding the host-policy lock
    /// so a concurrent policy change cannot move the root while an instance is created under it.
    /// </summary>
    public static int RunInternal(string[] args)
    {
        if (NativeLinux.getuid() != 0 || !TryParse(args, out var invocation) ||
            !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out var sudoUid) ||
            !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), out var sudoGid) ||
            invocation.Uid != sudoUid || invocation.Gid != sudoGid)
        {
            return 2;
        }

        try
        {
            if (invocation.Backups)
            {
                // A backup's folder alone: the product root is validated as root's, nothing else is created.
                return PrepareUnderHostPolicy(HostPolicyLocation.Production, invocation.ProductRoot, production =>
                {
                    CreateOrValidateRootDirectory(production.Path);
                    CreateOrValidateRuntimeDirectory(Path.Combine(production.Path, BackupVault.FolderName), invocation.Uid, invocation.Gid);
                });
            }
            return PrepareUnderHostPolicy(HostPolicyLocation.Production, invocation.ProductRoot, production => Prepare(
                InstallationPaths.Create(production.Path, invocation.InstanceId ?? Guid.Empty, ProductionCatalog.DefaultCatalogId),
                invocation.Uid,
                invocation.Gid,
                requireRoot: true,
                includeInstance: invocation.InstanceId is not null));
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// Holds the host-policy lock while it resolves the policy at <paramref name="location"/>, refuses (exit code 2)
    /// a root other than the one the policy resolves, and runs <paramref name="prepare"/>. A policy change takes the
    /// same lock for its empty-root check and publication, so the two can never interleave.
    /// </summary>
    internal static int PrepareUnderHostPolicy(
        HostPolicyLocation location,
        string requestedRoot,
        Action<ProductionProductRoot> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        using var policyLock = HostProductRootPolicy.AcquireLock(location);
        var production = HostProductRootPolicy.Resolve(location);
        if (!string.Equals(requestedRoot, production.Path, StringComparison.Ordinal))
        {
            return 2;
        }
        prepare(production);
        return 0;
    }

    /// <summary>
    /// The root-owned shared namespaces the privileged preparation creates or validates: the product root and its
    /// namespaces. The default root <c>/var/lib/hvo-sky</c> sits directly in the system's <c>/var/lib</c>, which
    /// preparation neither creates nor claims; a host-policy root already exists with every component validated as
    /// <c>root:root</c> by the policy.
    /// </summary>
    internal static IReadOnlyList<string> RootOwnedNamespaces(string productRoot) =>
    [
        productRoot,
        Path.Combine(productRoot, "cameraagents"),
        Path.Combine(productRoot, "logichosts"),
        Path.Combine(productRoot, "catalogs")
    ];

    /// <summary>
    /// Creates only what is missing, as owner-only directories: an existing product root, namespace, or runtime
    /// directory keeps its ownership and mode. Creating beneath a root-owned namespace is denied by the kernel, which
    /// is what hands the work to the privileged helper.
    /// </summary>
    internal static void PrepareProductWithoutPrivilege(InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var directory in new[] { paths.ProductRoot, Path.Combine(paths.ProductRoot, "catalogs"), paths.OperationsRoot }
                     .Concat(paths.ForEveryApprovedCatalog().Select(static catalog => catalog.CatalogRoot)))
        {
            if (!Directory.Exists(directory))
            {
                SafeFileSystem.CreateOwnerDirectory(directory);
            }
        }
    }

    private static async Task RunHelperAsync(
        Invocation invocation,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync("sudo", HelperArguments(invocation), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InstallerException($"Privileged product-root preparation failed: {Redaction.SafeDiagnostic(result.StandardError)}");
        }
    }

    private static void Prepare(
        InstallationPaths paths,
        uint uid,
        uint gid,
        bool requireRoot,
        bool includeInstance = true)
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

        foreach (var directory in RootOwnedNamespaces(paths.ProductRoot))
        {
            CreateOrValidateRootDirectory(directory);
        }
        if (includeInstance)
        {
            CreateOrValidateRuntimeDirectory(paths.InstanceRoot, uid, gid);
        }
        // The root helper's argument contract names no catalog, so it prepares the side-by-side root of every
        // approved catalog; the runtime user cannot create one beneath the root-owned catalogs directory later.
        foreach (var catalogPaths in paths.ForEveryApprovedCatalog())
            CreateOrValidateRuntimeDirectory(catalogPaths.CatalogRoot, uid, gid);
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
