using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>A scoped Linux EFBIG boundary for the actual asynchronous pure-copy writes. Requires serialized tests.</summary>
[SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Test-only Linux resource-limit ABI.")]
[SuppressMessage("Usage", "CA2216:Disposable types should declare finalizer", Justification = "Process-wide hooks must be restored deterministically by the serialized test, never by a late finalizer.")]
internal sealed class NativeFileSizeLimit : IDisposable
{
    private readonly Limit _previous;
    private readonly nint _signalHandler;

    public NativeFileSizeLimit(ulong bytes)
    {
        Assert.AreEqual(0, GetLimit(1, out _previous)); // RLIMIT_FSIZE
        _signalHandler = Signal(25, 1); // SIGXFSZ, SIG_IGN: writes return EFBIG rather than terminating testhost.
        Assert.AreNotEqual(new nint(-1), _signalHandler);
        var limit = new Limit { Current = bytes, Maximum = _previous.Maximum };
        if (SetLimit(1, in limit) != 0)
        {
            Signal(25, _signalHandler);
            Assert.Fail("Could not install the file-size fault boundary.");
        }
    }

    public void Dispose()
    {
        Assert.AreEqual(0, SetLimit(1, in _previous));
        Assert.AreNotEqual(new nint(-1), Signal(25, _signalHandler));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Limit
    {
        public ulong Current;
        public ulong Maximum;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "getrlimit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetLimit(int resource, out Limit limit);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "setrlimit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetLimit(int resource, in Limit limit);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "signal", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint Signal(int signal, nint handler);
}
