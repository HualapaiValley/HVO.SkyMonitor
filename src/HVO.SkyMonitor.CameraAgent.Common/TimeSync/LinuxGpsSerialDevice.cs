using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

internal interface IGpsSerialDevice : IDisposable
{
    // Nonblocking. Zero means no bytes yet. A disconnect throws IOException.
    int Read(byte[] buffer);
    bool? CarrierDetect();
}

/// <summary>Read-only, nonblocking Linux tty. Never enumerates devices or opens a companion path.</summary>
internal sealed class LinuxGpsSerialDevice : IGpsSerialDevice
{
    private readonly SafeFileHandle _handle;
    private readonly byte[] _original = new byte[36]; // Linux asm-generic struct termios (TCGETS), x64/arm64.

    internal static bool Supported => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    internal LinuxGpsSerialDevice(GpsReceiverSettings settings)
    {
        if (!Supported) throw new PlatformNotSupportedException();
        if (settings.Validate() is not null || !settings.Enabled)
            throw new ArgumentException("Invalid serial receiver settings.", nameof(settings));
        var noFollow = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;
        _handle = Open(settings.Device, 0x80000 | 0x800 | 0x100 | noFollow); // CLOEXEC | NONBLOCK | NOCTTY | NOFOLLOW
        try
        {
            if (_handle.IsInvalid || Ioctl(_handle, 0x5401, _original) < 0) throw new IOException("Serial device unavailable.");
            // Raw 8N1, receiver enabled, no modem/flow control, VMIN=1. Apply immediately so pending tty output cannot block the worker.
            var attributes = new byte[_original.Length];
            attributes[23] = 1; // c_cc[VMIN]
            var speed = settings.BaudRate switch { 4800 => 12, 9600 => 13, 38400 => 15, _ => 0x1002 };
            BinaryPrimitives.WriteInt32LittleEndian(attributes.AsSpan(8), speed | 0x30 | 0x80 | 0x800);
            if (Ioctl(_handle, 0x5402, attributes) < 0) throw new IOException("Serial device configuration failed.");
            if (Flush(_handle, 0) < 0) // TCIFLUSH: discard stale input left by an earlier session.
            {
                _ = Ioctl(_handle, 0x5402, _original);
                throw new IOException("Serial input could not be flushed.");
            }
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public int Read(byte[] buffer)
    {
        var count = NativeRead(_handle, buffer, (nuint)buffer.Length);
        if (count > 0) return (int)count;
        if (count < 0 && Marshal.GetLastPInvokeError() is 11 or 4) return 0; // EAGAIN / EINTR
        throw new IOException("Serial device disconnected.");
    }

    public bool? CarrierDetect()
    {
        if (ReadSignals(_handle, 0x5415, out int signals) == 0) return (signals & 0x40) != 0; // TIOCMGET, TIOCM_CAR
        return null;
    }

    public void Dispose()
    {
        if (!_handle.IsClosed)
        {
            _ = Ioctl(_handle, 0x5402, _original); // TCSETS. Unplugged devices can refuse restoration.
            _handle.Dispose();
        }
    }

#pragma warning disable SYSLIB1054 // Narrow Linux ABI without enabling unsafe code.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern SafeFileHandle Open(string path, int flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(SafeFileHandle fd, nuint request, [In, Out] byte[] attributes);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ReadSignals(SafeFileHandle fd, nuint request, out int signals);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "tcflush", SetLastError = true)]
    private static extern int Flush(SafeFileHandle fd, int queue);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint NativeRead(SafeFileHandle fd, [Out] byte[] buffer, nuint count);
#pragma warning restore SYSLIB1054
}
