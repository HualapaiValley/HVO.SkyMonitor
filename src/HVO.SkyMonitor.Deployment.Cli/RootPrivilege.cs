using System.Globalization;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// How <c>sudo hvo-sky-install cameraagent backup</c> and <c>restore</c> run (#1353, contract parity with RoofController):
/// started as root, they work as the instance's runtime user and become root again only for the few steps that need it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DropTo"/> joins the runtime user's groups (so Docker answers as it does for that user) and sets the real and
/// effective user and group to the runtime user's, keeping root only as the saved IDs. From then on every file the command
/// writes belongs to the runtime user, exactly as when that user runs a lifecycle command, and every check that compares a
/// file's owner with the process's user holds as it does then. The process cannot be traced or signalled into doing
/// anything by another process of the runtime user's: the kernel compares all three IDs for that, and the saved one is
/// root's.
/// </para>
/// <para>
/// <see cref="AsRoot"/> runs one synchronous step as root, real and effective, and returns to the runtime user before it
/// returns: the backup folder's operations, the machine install record, and the preparation of an absent instance root.
/// Credentials are process-wide, so a step never awaits: nothing else of the command runs while root is held, and a
/// program it starts afterwards gets the runtime user's IDs alone.
/// </para>
/// </remarks>
internal static partial class RootPrivilege
{
    private const uint Unchanged = uint.MaxValue;
    private static readonly Lock Gate = new();
    private static (uint Uid, uint Gid)? runtimeUser;

    /// <summary>Whether this process dropped to a runtime user and can become root again for one step.</summary>
    public static bool Held => runtimeUser is not null;

    /// <summary>
    /// Becomes the runtime user <paramref name="uid"/>:<paramref name="gid"/>, named <paramref name="name"/>, with its
    /// groups, keeping root as the saved IDs. The process must be root, real and effective. The environment's
    /// <c>HOME</c>, <c>USER</c> and <c>LOGNAME</c> become the runtime user's, so Docker reads that user's configuration,
    /// and the working directory becomes <c>/</c>.
    /// </summary>
    public static void DropTo(uint uid, uint gid, string name, string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(home);
        lock (Gate)
        {
            if (runtimeUser is not null) throw new InvalidOperationException("The process already runs as the runtime user.");
            if (NativeLinux.getuid() != 0 || geteuid() != 0) throw new InvalidOperationException("Only root can become the runtime user.");
            if (uid == 0) throw new InvalidOperationException("The runtime user cannot be root.");
            Check(initgroups(name, gid), "join the runtime user's groups");
            Check(setresgid(gid, gid, 0), "become the runtime user's group");
            Check(setresuid(uid, uid, 0), "become the runtime user");
            runtimeUser = (uid, gid);
        }
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("USER", name);
        Environment.SetEnvironmentVariable("LOGNAME", name);
        Environment.CurrentDirectory = "/";
    }

    /// <summary>Runs <paramref name="step"/> as root and returns to the runtime user, which must have been dropped to.</summary>
    public static T AsRoot<T>(Func<T> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (Gate)
        {
            var (uid, gid) = runtimeUser ?? throw new InvalidOperationException("The process holds no root to return to.");
            Check(setresuid(0, 0, Unchanged), "become root");
            Check(setresgid(0, 0, Unchanged), "become root's group");
            try
            {
                return step();
            }
            finally
            {
                // A process that cannot give root back must not go on as root.
                if (setresgid(gid, gid, 0) != 0 || setresuid(uid, uid, 0) != 0)
                {
                    Environment.FailFast("hvo-sky-install could not return from root to the runtime user.");
                }
            }
        }
    }

    public static void AsRoot(Action step)
    {
        ArgumentNullException.ThrowIfNull(step);
        AsRoot<object?>(() =>
        {
            step();
            return null;
        });
    }

    /// <summary>
    /// Why <paramref name="folder"/> cannot hold root's backups, or <see langword="null"/> when it can: it and every folder
    /// above it to <c>/</c> must be a folder, not a link, owned by root, and writable by root alone, so no one else can put
    /// another file in a backup's place between the moment it is named and the moment it is read. A folder that does not
    /// exist yet is skipped; the vault creates it.
    /// </summary>
    public static string? RootAncestryProblem(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        for (var current = Path.GetFullPath(folder); current is not null; current = Path.GetDirectoryName(current))
        {
            var node = NativeLinux.TryGetNodeIdentity(current);
            if (node is null) continue;
            if ((node.Type & 0xF000) != 0x4000)
                return $"{current} is not a folder";
            if (node.Uid != 0 || (node.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{current} is owned by uid {node.Uid} with mode {Convert.ToString((int)node.Mode & 0xFFF, 8)}, so someone other than root can change what is in it");
            }
        }
        return null;
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new InstallerException($"hvo-sky-install could not {what} (errno {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int setresuid(uint realUid, uint effectiveUid, uint savedUid);

    [LibraryImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int setresgid(uint realGid, uint effectiveGid, uint savedGid);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int initgroups(string user, uint group);

    [LibraryImport("libc")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint geteuid();
}
