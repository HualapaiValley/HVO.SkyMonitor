using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.CameraAgent.Authorization;

internal sealed class OwnerRecoveryTransport : IDisposable
{
    internal const string SocketFileName = "owner.sock";
    private const string EndpointName = "HvoOwnerRecovery";
    private const UnixFileMode ParentMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode SpecialModeBits = (UnixFileMode)0x0E00;
    private readonly UnixDomainSocketEndPoint? _endpoint;
    private SafeFileHandle? _startupLock;

    internal OwnerRecoveryTransport(string? socketPath, SafeFileHandle? startupLock = null)
    {
        SocketPath = socketPath;
        _endpoint = socketPath is null ? null : new UnixDomainSocketEndPoint(socketPath);
        _startupLock = startupLock;
    }

    internal string? SocketPath { get; }

    internal static OwnerRecoveryTransport Configure(
        WebApplicationBuilder builder,
        string identityDatabasePath,
        LocalIdentityOptions identityOptions)
    {
        if (!OperatingSystem.IsLinux() ||
            !identityOptions.AllowMissingAdminPassword ||
            !string.IsNullOrEmpty(identityOptions.AdminPassword) ||
            string.IsNullOrEmpty(builder.Configuration["LifecycleControl:Token"]))
        {
            return new OwnerRecoveryTransport(null);
        }

        var socketPath = Path.Combine(Path.GetDirectoryName(identityDatabasePath)!, SocketFileName);
        _ = new UnixDomainSocketEndPoint(socketPath);
        var startupLock = OpenStartupLock(socketPath);
        try
        {
            PrepareSocketPath(socketPath, startupLock);
            var socketUrl = $"http://unix:{socketPath}";
            var configuredEndpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
            if (configuredEndpoints.GetChildren().Any())
            {
                if (configuredEndpoints.GetSection(EndpointName).Exists())
                {
                    throw new InvalidOperationException($"Kestrel endpoint name '{EndpointName}' is reserved.");
                }
                builder.Configuration[$"Kestrel:Endpoints:{EndpointName}:Url"] = socketUrl;
                builder.Configuration[$"Kestrel:Endpoints:{EndpointName}:Protocols"] = "Http1";
            }
            else
            {
                var publicUrls = builder.Configuration[WebHostDefaults.ServerUrlsKey];
                if (string.IsNullOrWhiteSpace(publicUrls))
                {
                    publicUrls = "http://localhost:5000";
                }
                builder.WebHost.UseUrls($"{publicUrls.TrimEnd(';')};{socketUrl}");
            }

            return new OwnerRecoveryTransport(socketPath, startupLock);
        }
        catch
        {
            startupLock.Dispose();
            throw;
        }
    }

    internal bool IsLocal(HttpContext context)
        => IsLocalEndpoint(
            context.Features.Get<IConnectionEndPointFeature>()?.LocalEndPoint ??
            context.Features.Get<IConnectionSocketFeature>()?.Socket.LocalEndPoint);

    internal bool IsLocalEndpoint(EndPoint? endpoint)
        => _endpoint is not null && _endpoint.Equals(endpoint);

    internal void RestrictSocket()
    {
        if (SocketPath is null)
        {
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Owner recovery Unix sockets require Linux.");
        }
        var startupLock = Interlocked.Exchange(ref _startupLock, null);
        try
        {
            File.SetUnixFileMode(SocketPath, SocketMode);
            using var openedParent = startupLock is null
                ? OwnerRecoveryNative.OpenParent(Path.GetDirectoryName(SocketPath)!)
                : null;
            var parent = startupLock ?? openedParent!;
            AuthenticateParent(parent);
            AuthenticateSocket(OwnerRecoveryNative.GetNode(parent, SocketFileName), requireRestrictedMode: true);
        }
        finally
        {
            startupLock?.Dispose();
        }
    }

    internal static void PrepareSocketPath(string socketPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Owner recovery Unix sockets require Linux.");
        }

        using var parent = OpenStartupLock(socketPath);
        PrepareSocketPath(socketPath, parent);
    }

    private static SafeFileHandle OpenStartupLock(string socketPath)
    {
        var parent = OwnerRecoveryNative.OpenParent(Path.GetDirectoryName(socketPath)!);
        try
        {
            AuthenticateParent(parent);
            OwnerRecoveryNative.LockForStartup(parent);
            return parent;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    private static void PrepareSocketPath(string socketPath, SafeFileHandle parent)
    {
        var socketName = Path.GetFileName(socketPath);
        if (!OwnerRecoveryNative.TryGetNode(parent, socketName, out var before))
        {
            return;
        }

        AuthenticateSocket(before, requireRestrictedMode: false);
        ProbeInactive(parent, socketName);
        var after = OwnerRecoveryNative.GetNode(parent, socketName);
        AuthenticateSocket(after, requireRestrictedMode: false);
        if (before != after)
        {
            throw new InvalidOperationException("The owner recovery socket changed during startup cleanup.");
        }
        OwnerRecoveryNative.Unlink(parent, socketName);
    }

    private static void AuthenticateParent(SafeFileHandle parent)
    {
        var identity = OwnerRecoveryNative.GetOpenNode(parent);
        if (identity.Type != OwnerRecoveryNative.DirectoryNodeType ||
            identity.Uid != OwnerRecoveryNative.GetUid() ||
            identity.Gid != OwnerRecoveryNative.GetGid() ||
            identity.Mode != ParentMode ||
            identity.LinkCount < 2)
        {
            throw new InvalidOperationException("The owner recovery socket directory is not owner-only.");
        }
    }

    private static void AuthenticateSocket(OwnerRecoveryNodeIdentity identity, bool requireRestrictedMode)
    {
        if (identity.Type != OwnerRecoveryNative.SocketNodeType ||
            identity.Uid != OwnerRecoveryNative.GetUid() ||
            identity.Gid != OwnerRecoveryNative.GetGid() ||
            (requireRestrictedMode ? identity.Mode != SocketMode : (identity.Mode & SpecialModeBits) != 0) ||
            identity.LinkCount != 1)
        {
            throw new InvalidOperationException("The owner recovery socket is not an owner-only runtime socket.");
        }
    }

    private static void ProbeInactive(SafeFileHandle parent, string socketName)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(
                $"/proc/self/fd/{parent.DangerousGetHandle().ToInt32()}/{socketName}"));
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return;
        }

        throw new InvalidOperationException("The owner recovery socket is already active.");
    }

    public void Dispose() => Interlocked.Exchange(ref _startupLock, null)?.Dispose();
}

internal static class OwnerRecoveryNative
{
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int LockExclusiveNonBlocking = 0x06;
    private const int MissingEntry = 2;
    private const uint StatxType = 0x00000001;
    private const uint StatxMode = 0x00000002;
    private const uint StatxLinkCount = 0x00000004;
    private const uint StatxUid = 0x00000008;
    private const uint StatxGid = 0x00000010;
    private const uint StatxInode = 0x00000100;
    private const uint StatxMountId = 0x00001000;
    private const uint StatxIdentity =
        StatxType | StatxMode | StatxLinkCount | StatxUid | StatxGid | StatxInode | StatxMountId;
    private const uint TypeMask = 0xF000;

    internal const uint DirectoryNodeType = 0x4000;
    internal const uint SocketNodeType = 0xC000;

#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "getuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetUserId();

    [DllImport("libc", EntryPoint = "getgid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetGroupId();

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int StatX(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out OwnerRecoveryFileStatus status);

    [DllImport("libc", EntryPoint = "unlinkat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int UnlinkAt(int directoryFileDescriptor, string path, int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Flock(int fileDescriptor, int operation);
#pragma warning restore SYSLIB1054

    internal static uint GetUid() => GetUserId();

    internal static uint GetGid() => GetGroupId();

    internal static SafeFileHandle OpenParent(string path)
    {
        var descriptor = Open(path, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        return descriptor >= 0
            ? new SafeFileHandle(descriptor, ownsHandle: true)
            : throw new InvalidOperationException("The owner recovery socket directory could not be opened safely.");
    }

    internal static void LockForStartup(SafeFileHandle parent)
    {
        if (Flock(parent.DangerousGetHandle().ToInt32(), LockExclusiveNonBlocking) != 0)
        {
            throw new InvalidOperationException("Another owner recovery socket startup is already in progress.");
        }
    }

    internal static OwnerRecoveryNodeIdentity GetOpenNode(SafeFileHandle handle)
    {
        if (StatX(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                AtEmptyPath,
                StatxIdentity,
                out var status) != 0 ||
            (status.Mask & StatxIdentity) != StatxIdentity)
        {
            throw new InvalidOperationException("The owner recovery socket directory could not be authenticated.");
        }
        return ToIdentity(status);
    }

    internal static bool TryGetNode(
        SafeFileHandle parent,
        string name,
        out OwnerRecoveryNodeIdentity identity)
    {
        var result = StatX(
            parent.DangerousGetHandle().ToInt32(),
            name,
            AtSymbolicLinkNoFollow,
            StatxIdentity,
            out var status);
        if (result == 0 && (status.Mask & StatxIdentity) == StatxIdentity)
        {
            identity = ToIdentity(status);
            return true;
        }
        if (result != 0 && Marshal.GetLastPInvokeError() == MissingEntry)
        {
            identity = default;
            return false;
        }
        throw new InvalidOperationException("The owner recovery socket could not be authenticated.");
    }

    internal static OwnerRecoveryNodeIdentity GetNode(SafeFileHandle parent, string name)
        => TryGetNode(parent, name, out var identity)
            ? identity
            : throw new InvalidOperationException("The owner recovery socket changed during startup cleanup.");

    internal static void Unlink(SafeFileHandle parent, string name)
    {
        if (UnlinkAt(parent.DangerousGetHandle().ToInt32(), name, 0) != 0)
        {
            throw new InvalidOperationException("The stale owner recovery socket could not be removed safely.");
        }
    }

    private static OwnerRecoveryNodeIdentity ToIdentity(OwnerRecoveryFileStatus status)
        => new(
            status.Uid,
            status.Gid,
            (UnixFileMode)(status.Mode & 0x0FFF),
            status.Mode & TypeMask,
            status.LinkCount,
            status.Inode,
            status.DeviceMajor,
            status.DeviceMinor,
            status.MountId);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct OwnerRecoveryFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(24)] internal uint Gid;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(144)] internal ulong MountId;
    }
}

internal readonly record struct OwnerRecoveryNodeIdentity(
    uint Uid,
    uint Gid,
    UnixFileMode Mode,
    uint Type,
    uint LinkCount,
    ulong Inode,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong MountId);
