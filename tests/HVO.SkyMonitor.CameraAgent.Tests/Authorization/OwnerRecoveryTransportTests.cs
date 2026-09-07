using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using HVO.SkyMonitor.CameraAgent.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authorization;

[TestClass]
[TestCategory("Unit")]
[SupportedOSPlatform("linux")]
public sealed class OwnerRecoveryTransportTests
{
    private const UnixFileMode ParentMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode TransientSocketMode = SocketMode |
        UnixFileMode.UserExecute |
        UnixFileMode.GroupRead |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherExecute;
    private const string CrashRootEnvironmentVariable = "HVO_OWNER_RECOVERY_CRASH_ROOT";

    /// <summary>
    /// Only <see cref="StartCrashChild"/> sets this exact sentinel on the nested child's environment. Without
    /// it the crash child binds nothing, so an inherited crash root cannot make the discovering test host
    /// bind a socket and wait forever.
    /// </summary>
    private const string CrashChildSentinelVariable = "HVO_OWNER_RECOVERY_CRASH_CHILD";
    private const string CrashChildSentinelValue = "1";

    [TestMethod]
    public void OpenParent_OpensARealDirectoryAndRefusesASymlinkedOne()
    {
        // The real-directory assertion is the one that discriminated on aarch64 while the x86 O_DIRECTORY value
        // was hard-coded: it means O_DIRECT there and open(2) refuses it with EINVAL. The symlink assertion pins
        // the O_NOFOLLOW guard going forward; the refusal message does not distinguish EINVAL from ELOOP.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Unix owner recovery is Linux-only.");
        }

        var root = Directory.CreateTempSubdirectory("hvo-owner-recovery-open-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real")).FullName;
            var link = Path.Combine(root.FullName, "link");
            File.CreateSymbolicLink(link, real);

            using var opened = OwnerRecoveryNative.OpenParent(real);
            Assert.IsFalse(opened.IsInvalid);

            var refused = Assert.ThrowsExactly<InvalidOperationException>(() => OwnerRecoveryNative.OpenParent(link));
            StringAssert.Contains(refused.Message, "could not be opened safely", StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void PrepareSocketPath_RefusesASymlinkedParentDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Unix owner recovery is Linux-only.");
        }

        var root = Directory.CreateTempSubdirectory("hvo-owner-recovery-parent-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real")).FullName;
            File.SetUnixFileMode(real, ParentMode);
            var link = Path.Combine(root.FullName, "link");
            File.CreateSymbolicLink(link, real);

            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                () => OwnerRecoveryTransport.PrepareSocketPath(Path.Combine(link, "owner-recovery.sock")));

            StringAssert.Contains(exception.Message, "could not be opened safely", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(Path.Combine(real, "owner-recovery.sock")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task PrepareSocketPath_RemovesSocketLeftByHardTerminationAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Unix owner recovery is Linux-only.");
        }

        var root = CreateRoot();
        var markerPath = Path.Combine(root, "listening");
        using var process = StartCrashChild(root);
        try
        {
            var childProcessId = await WaitForMarkerAsync(process, markerPath).ConfigureAwait(false);
            using (var childProcess = Process.GetProcessById(childProcessId))
            {
                childProcess.Kill(entireProcessTree: true);
                await childProcess.WaitForExitAsync().ConfigureAwait(false);
            }

            var socketPath = Path.Combine(root, OwnerRecoveryTransport.SocketFileName);
            Assert.IsTrue(File.Exists(socketPath));

            OwnerRecoveryTransport.PrepareSocketPath(socketPath);

            Assert.IsFalse(File.Exists(socketPath));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PrepareSocketPath_RefusesActiveListener()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Unix owner recovery is Linux-only.");
        }

        var root = CreateRoot();
        var socketPath = Path.Combine(root, OwnerRecoveryTransport.SocketFileName);
        using var socket = Bind(socketPath);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => OwnerRecoveryTransport.PrepareSocketPath(socketPath));

            StringAssert.Contains(exception.Message, "already active", StringComparison.Ordinal);
            Assert.IsTrue(File.Exists(socketPath));
        }
        finally
        {
            socket.Dispose();
            File.Delete(socketPath);
            Directory.Delete(root);
        }
    }

    [TestMethod]
    public void PrepareSocketPath_RefusesUnexpectedNode()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Unix owner recovery is Linux-only.");
        }

        var root = CreateRoot();
        var socketPath = Path.Combine(root, OwnerRecoveryTransport.SocketFileName);
        File.WriteAllText(socketPath, "not-a-socket");
        File.SetUnixFileMode(socketPath, SocketMode);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => OwnerRecoveryTransport.PrepareSocketPath(socketPath));

            StringAssert.Contains(exception.Message, "not an owner-only runtime socket", StringComparison.Ordinal);
            Assert.IsTrue(File.Exists(socketPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void PrepareSocketPath_RefusesConcurrentStartup()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Unix owner recovery is Linux-only.");
        }

        var root = CreateRoot();
        using var startupLock = OwnerRecoveryNative.OpenParent(root);
        OwnerRecoveryNative.LockForStartup(startupLock);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                OwnerRecoveryTransport.PrepareSocketPath(
                    Path.Combine(root, OwnerRecoveryTransport.SocketFileName)));

            StringAssert.Contains(exception.Message, "already in progress", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [TestMethod]
    public async Task HardTerminationChildAsync()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(CrashChildSentinelVariable),
                CrashChildSentinelValue,
                StringComparison.Ordinal))
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable(CrashRootEnvironmentVariable);
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root),
            $"{CrashChildSentinelVariable} is set but {CrashRootEnvironmentVariable} names no crash root.");
        Assert.IsTrue(Directory.Exists(root), $"{CrashRootEnvironmentVariable} '{root}' does not exist.");

        using var socket = Bind(Path.Combine(root, OwnerRecoveryTransport.SocketFileName), TransientSocketMode);
        var markerPath = Path.Combine(root, "listening");
        var stagingMarkerPath = $"{markerPath}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(
            stagingMarkerPath,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        File.Move(stagingMarkerPath, markerPath);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-owner-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, ParentMode);
        return root;
    }

    private static Socket Bind(string socketPath, UnixFileMode mode = SocketMode)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        socket.Listen();
        File.SetUnixFileMode(socketPath, mode);
        return socket;
    }

    private static Process StartCrashChild(string root)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(typeof(OwnerRecoveryTransportTests).Assembly.Location);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add(
            $"FullyQualifiedName={typeof(OwnerRecoveryTransportTests).FullName}.{nameof(HardTerminationChildAsync)}");
        // Strip anything inherited, then arm this child explicitly; the process-global environment is untouched.
        startInfo.Environment.Remove(CrashChildSentinelVariable);
        startInfo.Environment.Remove(CrashRootEnvironmentVariable);
        startInfo.Environment[CrashChildSentinelVariable] = CrashChildSentinelValue;
        startInfo.Environment[CrashRootEnvironmentVariable] = root;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The owner recovery crash child could not be started.");
    }

    private static async Task<int> WaitForMarkerAsync(Process process, string markerPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(markerPath))
        {
            if (process.HasExited)
            {
                var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                Assert.Fail($"The owner recovery crash child exited early.{Environment.NewLine}{output}{error}");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token).ConfigureAwait(false);
        }
        return int.Parse(
            await File.ReadAllTextAsync(markerPath, timeout.Token).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
