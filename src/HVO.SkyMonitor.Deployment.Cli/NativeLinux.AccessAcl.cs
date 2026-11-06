using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Deployment;

internal static partial class NativeLinux
{
    private const int NoExtendedAttributeData = 61;

    internal static Func<SafeFileHandle, (nint Size, int Error)> AccessAclSizeReader { get; set; } = ReadAccessAclSize;

    internal static bool HasNoExtendedAccessAcl(string path)
    {
        try
        {
            using var directory = OpenDirectoryNoFollow(path);
            var (size, error) = AccessAclSizeReader(directory);
            // Only ENODATA proves absence. Any present data (including malformed/empty data), unsupported lookup
            // or read failure must not turn a private-group mode bit into permission for another ACL principal.
            return size == -1 && error == NoExtendedAttributeData;
        }
        catch (InstallerException)
        {
            return false;
        }
    }

    private static (nint Size, int Error) ReadAccessAclSize(SafeFileHandle directory)
    {
        var size = FGetExtendedAttribute(directory.DangerousGetHandle().ToInt32(), "system.posix_acl_access", 0, 0);
        return (size, Marshal.GetLastPInvokeError());
    }

    [LibraryImport("libc", EntryPoint = "fgetxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint FGetExtendedAttribute(int descriptor, string name, nint value, nuint size);
}
