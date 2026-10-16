using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The narrow privileged handoff for the only two purge mutations of the root-owned <c>cameraagents</c> namespace:
/// renaming an inode-authenticated, runtime-owned instance root to its operation tombstone, and removing that tombstone
/// once the runtime user has emptied it. The runtime user performs every other step of a purge, including deletion of
/// the tombstone's contents, and the namespace directory keeps its <c>root:root 0755</c> boundary. Each mutation is
/// first attempted without privilege; only a kernel permission denial reaches the <c>sudo</c>-executed helper.
/// </summary>
internal static class PrivilegedPurgeNamespace
{
    internal const string CommandName = "internal-purge-namespace";
    private const uint DirectoryType = 0x4000;
    private const int ArgumentCount = 19;

    /// <summary>
    /// The components from <c>/</c> to the <c>cameraagents</c> namespace of the default product root. The helper opens
    /// each without following links and requires root ownership with no group or other write access; the components
    /// that internal preparation creates must also belong to group 0.
    /// </summary>
    internal static readonly IReadOnlyList<(string Name, bool RootGroup)> NamespaceComponents =
    [
        ("var", false),
        ("lib", false),
        ("hvo", true),
        ("skymonitor", true),
        ("cameraagents", true)
    ];

    internal enum Action
    {
        Quarantine,
        Remove
    }

    internal sealed record Command(
        Action Action,
        Guid InstanceId,
        Guid OperationId,
        uint Uid,
        uint Gid,
        ulong Inode,
        uint DeviceMajor,
        uint DeviceMinor)
    {
        public string InstanceName => InstanceId.ToString("D");

        public string TombstoneName => PrivilegedPurgeNamespace.TombstoneName(InstanceId, OperationId);
    }

    public static string TombstoneName(Guid instanceId, Guid operationId) => $".purge-{instanceId:D}-{operationId:D}";

    public static string NamespaceRoot(string productRoot) => Path.Combine(productRoot, "cameraagents");

    /// <summary>
    /// Renames the authenticated instance root to its operation tombstone. Fails with nothing renamed unless the
    /// helper reports success and the tombstone then carries the exact identity the caller authenticated.
    /// </summary>
    public static async Task QuarantineAsync(
        string productRoot,
        Guid instanceId,
        Guid operationId,
        UnixNodeIdentity expected,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var parent = NamespaceRoot(productRoot);
        var instanceName = instanceId.ToString("D");
        var tombstoneName = TombstoneName(instanceId, operationId);
        if (SafeTreeDeletion.TryRenameChild(parent, instanceName, tombstoneName, expected)) return;
        await RunHelperAsync(Action.Quarantine, productRoot, instanceId, operationId, expected, processRunner, cancellationToken)
            .ConfigureAwait(false);
        using var handle = NativeLinux.OpenDirectoryNoFollow(parent);
        if (NativeLinux.TryGetNodeIdentityAt(handle, instanceName) is not null ||
            NativeLinux.TryGetNodeIdentityAt(handle, tombstoneName) != expected)
        {
            throw new InstallerException("The privileged purge quarantine did not produce the authenticated tombstone.");
        }
        NativeLinux.FlushOpenDirectory(handle);
    }

    /// <summary>Removes the emptied, identity-bound operation tombstone.</summary>
    public static async Task RemoveTombstoneAsync(
        string productRoot,
        Guid instanceId,
        Guid operationId,
        UnixNodeIdentity expectedEmpty,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var parent = NamespaceRoot(productRoot);
        var tombstoneName = TombstoneName(instanceId, operationId);
        if (SafeTreeDeletion.TryRemoveEmptyChild(parent, tombstoneName, expectedEmpty)) return;
        await RunHelperAsync(Action.Remove, productRoot, instanceId, operationId, expectedEmpty, processRunner, cancellationToken)
            .ConfigureAwait(false);
        using var handle = NativeLinux.OpenDirectoryNoFollow(parent);
        if (NativeLinux.TryGetNodeIdentityAt(handle, tombstoneName) is not null)
            throw new InstallerException("The privileged purge removal did not remove the authenticated tombstone.");
        NativeLinux.FlushOpenDirectory(handle);
    }

    internal static IReadOnlyList<string> HelperArguments(
        Action action,
        string productRoot,
        Guid instanceId,
        Guid operationId,
        UnixNodeIdentity expected)
        =>
        [
            "--", $"/proc/{Environment.ProcessId}/exe", CommandName,
            "--action", action == Action.Quarantine ? "quarantine" : "remove",
            "--product-root", productRoot,
            "--instance-id", instanceId.ToString("D"),
            "--operation-id", operationId.ToString("D"),
            "--uid", expected.Uid.ToString(CultureInfo.InvariantCulture),
            "--gid", expected.Gid.ToString(CultureInfo.InvariantCulture),
            "--inode", expected.Inode.ToString(CultureInfo.InvariantCulture),
            "--device-major", expected.DeviceMajor.ToString(CultureInfo.InvariantCulture),
            "--device-minor", expected.DeviceMinor.ToString(CultureInfo.InvariantCulture)
        ];

    public static int RunInternal(string[] args)
    {
        if (!TryParse(args, out var command) || NativeLinux.getuid() != 0 ||
            !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), NumberStyles.None, CultureInfo.InvariantCulture, out var sudoUid) ||
            !uint.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), NumberStyles.None, CultureInfo.InvariantCulture, out var sudoGid) ||
            command.Uid != sudoUid || command.Gid != sudoGid)
        {
            return 2;
        }
        try
        {
            using var parent = OpenRootOwnedNamespace();
            Apply(command, parent);
            return 0;
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static bool TryParse(string[] args, out Command command)
    {
        command = null!;
        if (args.Length != ArgumentCount || args[0] != CommandName ||
            args[1] != "--action" || args[2] is not ("quarantine" or "remove") ||
            args[3] != "--product-root" || !string.Equals(args[4], InstallRequest.DefaultProductRoot, StringComparison.Ordinal) ||
            args[5] != "--instance-id" || !Guid.TryParseExact(args[6], "D", out var instanceId) || instanceId == Guid.Empty ||
            args[7] != "--operation-id" || !Guid.TryParseExact(args[8], "D", out var operationId) || operationId == Guid.Empty ||
            args[9] != "--uid" || !TryParseNumber(args[10], out uint uid) || uid == 0 ||
            args[11] != "--gid" || !TryParseNumber(args[12], out uint gid) ||
            args[13] != "--inode" || !ulong.TryParse(args[14], NumberStyles.None, CultureInfo.InvariantCulture, out var inode) ||
            args[15] != "--device-major" || !TryParseNumber(args[16], out uint deviceMajor) ||
            args[17] != "--device-minor" || !TryParseNumber(args[18], out uint deviceMinor))
        {
            return false;
        }
        command = new Command(
            args[2] == "quarantine" ? Action.Quarantine : Action.Remove,
            instanceId, operationId, uid, gid, inode, deviceMajor, deviceMinor);
        return true;
    }

    /// <summary>
    /// Applies one authenticated namespace mutation through an already-authenticated <c>cameraagents</c> handle. Both
    /// entry names derive only from the command's identifiers, the entry must be the runtime-owned directory with the
    /// exact inode and device the caller authenticated on the namespace's own mount, and the parent is flushed after
    /// the mutation.
    /// </summary>
    internal static void Apply(Command command, SafeFileHandle parent)
    {
        var parentIdentity = NativeLinux.GetOpenNodeIdentity(parent, "cameraagents");
        if (command.Action == Action.Quarantine)
        {
            var source = NativeLinux.TryGetNodeIdentityAt(parent, command.InstanceName)
                ?? throw new InstallerException("The instance root to quarantine is absent.");
            EnsureBound(source, command, parentIdentity, "instance root");
            if (NativeLinux.TryGetNodeIdentityAt(parent, command.TombstoneName) is not null)
                throw new InstallerException("The purge tombstone already exists.");
            if (!NativeLinux.TryRenameNodeAt(parent, command.InstanceName, command.TombstoneName))
                throw new InstallerException("The instance root could not be quarantined through its authenticated parent.");
            if (NativeLinux.TryGetNodeIdentityAt(parent, command.TombstoneName) != source)
                throw new InstallerException("The quarantined tombstone changed during the privileged rename.");
        }
        else
        {
            var tombstone = NativeLinux.TryGetNodeIdentityAt(parent, command.TombstoneName)
                ?? throw new InstallerException("The purge tombstone to remove is absent.");
            EnsureBound(tombstone, command, parentIdentity, "purge tombstone");
            if (tombstone.LinkCount != 2)
                throw new InstallerException("The purge tombstone still contains directories.");
            if (!NativeLinux.TryRemoveDirectoryAt(parent, command.TombstoneName))
                throw new InstallerException("The purge tombstone could not be removed through its authenticated parent.");
        }
        NativeLinux.FlushOpenDirectory(parent);
    }

    private static async Task RunHelperAsync(
        Action action,
        string productRoot,
        Guid instanceId,
        Guid operationId,
        UnixNodeIdentity expected,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (NativeLinux.getuid() == 0)
            throw new InstallerException("The purge namespace mutation was denied to root.");
        var result = await processRunner.RunAsync(
                "sudo", HelperArguments(action, productRoot, instanceId, operationId, expected), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InstallerException(
                $"Privileged purge {(action == Action.Quarantine ? "quarantine" : "removal")} failed: " +
                Redaction.SafeDiagnostic(result.StandardError));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each superseded component handle is disposed as the walk advances, and the current handle is disposed on failure or returned to the caller.")]
    private static SafeFileHandle OpenRootOwnedNamespace()
    {
        var current = NativeLinux.OpenDirectoryNoFollow("/");
        try
        {
            EnsureRootOwned(current, "/", rootGroup: false);
            foreach (var (name, rootGroup) in NamespaceComponents)
            {
                var next = NativeLinux.TryOpenDirectoryAt(current, name)
                    ?? throw new InstallerException($"Product namespace component '{name}' is not an authenticated directory.");
                current.Dispose();
                current = next;
                EnsureRootOwned(current, name, rootGroup);
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void EnsureRootOwned(SafeFileHandle directory, string name, bool rootGroup)
    {
        var identity = NativeLinux.GetOpenNodeIdentity(directory, name);
        if (identity.Type != DirectoryType || identity.Uid != 0 || rootGroup && identity.Gid != 0 ||
            (identity.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InstallerException($"Product namespace component '{name}' must be root-owned and not writable by other users.");
        }
    }

    private static void EnsureBound(UnixNodeIdentity node, Command command, UnixNodeIdentity parent, string description)
    {
        if (node.Type != DirectoryType || node.Uid != command.Uid || node.Gid != command.Gid ||
            (node.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 ||
            node.Inode != command.Inode || node.DeviceMajor != command.DeviceMajor || node.DeviceMinor != command.DeviceMinor ||
            node.DeviceMajor != parent.DeviceMajor || node.DeviceMinor != parent.DeviceMinor || node.MountId != parent.MountId)
        {
            throw new InstallerException($"The {description} does not match the authenticated purge identity.");
        }
    }

    private static bool TryParseNumber(string value, out uint number)
        => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
