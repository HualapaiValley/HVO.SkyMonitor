using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Temporarily replaces the Unix VFS syscall pointers in the SQLite library used by Microsoft.Data.Sqlite.
/// Only a fleet store staged below this fixture is faulted. Tests using this process-wide hook must not run in parallel.
/// SQLite's published version-3 VFS ABI defines xSetSystemCall and xGetSystemCall after its first 13 method pointers:
/// https://www.sqlite.org/c3ref/vfs.html.
/// </summary>
[SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Test-only native callbacks use marshalled delegates.")]
[SuppressMessage("Usage", "CA2216:Disposable types should declare finalizer", Justification = "Process-wide hooks must be restored deterministically by the serialized test, never by a late finalizer.")]
internal sealed class NativeSqliteIoFault : IDisposable
{
    private readonly string _directory;
    private readonly nint _vfs;
    private readonly SetSystemCall _set;
    private readonly List<(string Name, nint Original, Delegate Callback)> _replacements = [];
    private string? _mode;

    public NativeSqliteIoFault(string directory)
    {
        _directory = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        // Initialize precisely the pinned provider before looking up its default Unix VFS.
        using (var connection = new SqliteConnection("Data Source=:memory:")) connection.Open();
        Assert.AreEqual(8, IntPtr.Size, "The Linux x64/ARM64 VFS layout is required.");
        _vfs = FindVfs(0);
        Assert.AreNotEqual(nint.Zero, _vfs);
        Assert.AreEqual(3, Marshal.ReadInt32(_vfs));
        var setOffset = 16 + 16 * IntPtr.Size;
        _set = Marshal.GetDelegateForFunctionPointer<SetSystemCall>(Marshal.ReadIntPtr(_vfs, setOffset));
        var get = Marshal.GetDelegateForFunctionPointer<GetSystemCall>(Marshal.ReadIntPtr(_vfs, setOffset + IntPtr.Size));
        var writeName = get(_vfs, "pwrite64") != 0 ? "pwrite64" : "pwrite";
        var write = Marshal.GetDelegateForFunctionPointer<WriteCall>(get(_vfs, writeName));
        var close = Marshal.GetDelegateForFunctionPointer<FdCall>(get(_vfs, "close"));
        try
        {
            Replace(writeName, new WriteCall((fd, buffer, count, offset) =>
            {
                if (Target(fd) && _mode is "commit" or "rollback" or "dispose")
                {
                    WriteFailures++;
                    return Fail();
                }
                return write(fd, buffer, count, offset);
            }), get);
            Replace("close", new FdCall(fd =>
            {
                var fail = Target(fd) && _mode == "dispose";
                var result = close(fd);
                if (!fail) return result;
                CloseFailures++;
                // Release the real descriptor even when reporting EIO, as Linux close can do.
                return Fail();
            }), get);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int WriteFailures { get; private set; }
    public int CloseFailures { get; private set; }

    public void Arm(string mode) => _mode = mode;

    public void Dispose()
    {
        _mode = null;
        foreach (var (name, original, callback) in Enumerable.Reverse(_replacements))
        {
            Assert.AreEqual(0, _set(_vfs, name, original));
            GC.KeepAlive(callback);
        }
        _replacements.Clear();
    }

    private void Replace(string name, Delegate callback, GetSystemCall get)
    {
        var original = get(_vfs, name);
        Assert.AreNotEqual(nint.Zero, original, name);
        Assert.AreEqual(0, _set(_vfs, name, Marshal.GetFunctionPointerForDelegate(callback)), name);
        _replacements.Add((name, original, callback));
    }

    private bool Target(int descriptor)
    {
        var path = new FileInfo($"/proc/self/fd/{descriptor}").LinkTarget;
        return path is not null && path.StartsWith(_directory, StringComparison.Ordinal) &&
               Path.GetFileName(path).StartsWith(".fleet-status.db.restore-", StringComparison.Ordinal) &&
               path.EndsWith(".tmp", StringComparison.Ordinal);
    }

    private static int Fail()
    {
        Marshal.WriteInt32(ErrnoLocation(), 5); // EIO, consumed by the real Unix VFS.
        return -1;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint WriteCall(int fd, nint buffer, nuint count, long offset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FdCall(int fd);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSystemCall(nint vfs, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetSystemCall(nint vfs, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_vfs_find", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint FindVfs(nint name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "__errno_location", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ErrnoLocation();
}
