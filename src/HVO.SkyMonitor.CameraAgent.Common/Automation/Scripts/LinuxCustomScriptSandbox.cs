using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

internal sealed record CustomScriptSandboxResult(string Status, int? ExitCode, long StdoutBytes, long StderrBytes);

/// <summary>
/// A static POSIX interpreter with an enforced syscall allowlist, private proc/dev,
/// no host directories, network namespace, capabilities, inherited environment or published files.
/// </summary>
internal static class LinuxCustomScriptSandbox
{
    private const string Bubblewrap = "/usr/bin/bwrap";
    private const string SystemdRun = "/usr/bin/systemd-run";
    private const string Prlimit = "/usr/bin/prlimit";
    internal const string ScopeDescription = "HVO bounded custom script";
    // This trusted, single-threaded shell execs in place before prlimit lowers RLIMIT_NOFILE.
    // Enumerate the entire inherited table (including high-numbered fds), never only 3..63.
    // Kernel fd names are decimal; reject anything else before the numeric-only eval.
    private const string CloseInheritedDescriptors = """
        for descriptor in /proc/self/fd/*; do
            descriptor=${descriptor##*/}
            case "$descriptor" in 0|1|2) continue;; ''|*[!0-9]*) exit 125;; esac
            eval "exec $descriptor>&-" || exit 125
        done
        exec "$@"
        """;

    internal static bool Probe() => ProbeUnavailableReason() is null;

    internal static string? ProbeUnavailableReason() => ProbeUnavailableReason(RuntimeInformation.ProcessArchitecture);

    internal static string? ProbeUnavailableReason(Architecture architecture)
    {
        if (architecture != Architecture.X64) return "script.sandbox-architecture-unqualified";
        if (!OperatingSystem.IsLinux()) return "script.sandbox-linux-required";
        if (!FilesAvailable()) return "script.sandbox-tools-required";
        if (IsRoot()) return "script.sandbox-nonroot-required";
        if (!HasProtectedCgroupMount(File.ReadAllText("/proc/self/mountinfo"))) return "script.sandbox-nsdelegate-required";
        if (!IsStaticElf(File.ReadAllBytes("/usr/bin/busybox"))) return "script.sandbox-static-shell-required";
        var result = ExecuteAsync(new() { Id = "probe", Content = ":", TimeoutSeconds = 3 }, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        return result.Status == "completed" && result.ExitCode == 0 ? null : "script.sandbox-probe-failed";
    }

    internal static ValueTask<CustomScriptSandboxResult> ExecuteAsync(CustomScriptRegistration script,
        CancellationToken cancellationToken) => ExecuteAsync(script, RuntimeInformation.ProcessArchitecture, cancellationToken);

    internal static async ValueTask<CustomScriptSandboxResult> ExecuteAsync(CustomScriptRegistration script,
        Architecture architecture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (architecture != Architecture.X64) return new("isolation-unavailable", null, 0, 0);
        if (script.ScratchBytes < Environment.SystemPageSize)
            return new("isolation-unavailable", null, 0, 0);
        if (!HasProtectedCgroupMount(await File.ReadAllTextAsync("/proc/self/mountinfo", cancellationToken).ConfigureAwait(false)))
            return new("isolation-unavailable", null, 0, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(script.TimeoutSeconds));
        using var outputLimit = new CancellationTokenSource();
        using var launchFailure = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, outputLimit.Token, launchFailure.Token);
        using var process = new Process { StartInfo = BuildStartInfo(script) };
        var termination = new CustomScriptTermination(() => process.HasExited, Kill);
        var launched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reaped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // PDEATHSIG is tied to the forking thread, not merely the .NET process. Keep it alive.
        var launchThread = new Thread(() =>
        {
            try
            {
                if (!process.Start()) { launched.TrySetResult(false); return; }
                launched.TrySetResult(true);
                process.WaitForExit();
                termination.Exited();
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
            {
                launched.TrySetResult(false);
            }
            finally { reaped.TrySetResult(); }
        })
        { IsBackground = true, Name = "HVO script lifetime" };
        launchThread.Start();
        if (!await launched.Task.ConfigureAwait(false))
        {
            await reaped.Task.ConfigureAwait(false);
            return new("launch-failed", null, 0, 0);
        }
        using var cancelledRegistration = cancellationToken.Register(() => termination.Stop("cancelled"));
        using var timeoutRegistration = timeout.Token.Register(() => termination.Stop("timeout"));
        using var outputRegistration = outputLimit.Token.Register(() => termination.Stop("output-limit"));
        using var failureRegistration = launchFailure.Token.Register(() => termination.Stop("launch-failed"));
        long total = 0;
        var stdout = DrainAsync(process.StandardOutput.BaseStream);
        var stderr = DrainAsync(process.StandardError.BaseStream);
        var input = WriteInputAsync();
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The individual token callbacks latch the cause and terminate the live process.
        }
        finally
        {
            // The PID namespace init dies with its parent; exiting it kills even detached descendants.
            // Keep the worker/authority until exit and both redirected pipes have really drained.
            if (!process.HasExited) termination.Stop("launch-failed");
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await input.ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await reaped.Task.ConfigureAwait(false);
        }
        return new(termination.Status(process.ExitCode),
            process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));

        async Task<long> DrainAsync(Stream stream)
        {
            var buffer = new byte[4096];
            long count = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                count += read;
                if (Interlocked.Add(ref total, read) > script.OutputBytes)
                    await outputLimit.CancelAsync().ConfigureAwait(false);
            }
            return count;
        }

        async Task WriteInputAsync()
        {
            try
            {
                var unit = process.StartInfo.ArgumentList.First(static argument => argument.StartsWith("--unit=", StringComparison.Ordinal))[7..] + ".scope";
                var verified = false;
                // No untrusted content enters stdin until the kernel reports this launch's exact budgets.
                for (var attempt = 0; attempt < 200 && !process.HasExited; attempt++)
                {
                    if (HasVerifiedBudget(process.Id, unit)) { verified = true; break; }
                    await Task.Delay(TimeSpan.FromMilliseconds(10), stop.Token).ConfigureAwait(false);
                }
                if (!verified)
                {
                    await launchFailure.CancelAsync().ConfigureAwait(false);
                    return;
                }
                // Bubblewrap consumes this finite binary policy before exec; script stdin is /dev/null.
                await process.StandardInput.BaseStream.WriteAsync(CustomScriptSyscallPolicy.Create(), stop.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { /* An early exit can close stdin before the writer flushes. */ }
            }
        }

        void Kill()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (Win32Exception) when (process.HasExited) { }
        }
    }

    internal static ProcessStartInfo BuildStartInfo(CustomScriptRegistration script)
    {
        // tmpfs rounds size UP to host pages; zero means unlimited. Never exceed the registered bound.
        var scratchCapacity = script.ScratchBytes - script.ScratchBytes % Environment.SystemPageSize;
        ArgumentOutOfRangeException.ThrowIfLessThan(scratchCapacity, Environment.SystemPageSize, nameof(script));
        var info = new ProcessStartInfo(SystemdRun)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = "/",
            StandardInputEncoding = new UTF8Encoding(false)
        };
        info.Environment.Clear();
        // Only the launcher gets the non-secret user-bus locator; Bubblewrap clears it for the script.
        info.Environment["XDG_RUNTIME_DIR"] = "/run/user/" + (RealUserId() ?? "unavailable");
        Add("--user", "--scope", "--quiet", "--collect", "--no-ask-password",
            "--unit=hvo-custom-script-" + Guid.NewGuid().ToString("N"),
            "--description=" + ScopeDescription,
            "--property=MemoryMax=67108864", "--property=MemorySwapMax=0",
            "--property=TasksMax=16", "--property=CPUQuota=50%", "--property=KillSignal=SIGKILL",
            "--property=RuntimeMaxSec=" + (script.TimeoutSeconds + 10).ToString(CultureInfo.InvariantCulture), "--",
            "/usr/bin/busybox", "sh", "-c", CloseInheritedDescriptors, "hvo-script-launcher", Prlimit,
            "--core=0", "--as=33554432", "--nofile=64", "--fsize=" + script.ScratchBytes,
            "--cpu=" + script.TimeoutSeconds, "--", Bubblewrap,
            "--unshare-all", "--unshare-user", "--disable-userns", "--assert-userns-disabled", "--die-with-parent", "--new-session", "--cap-drop", "ALL",
            "--ro-bind", "/usr/bin/busybox", "/bin/sh", "--ro-bind", "/usr/bin/busybox", "/bin/sleep",
            "--proc", "/proc", "--remount-ro", "/proc", "--dev-bind", "/dev/null", "/dev/null", "--size", scratchCapacity.ToString(CultureInfo.InvariantCulture),
            "--tmpfs", "/work", "--symlink", "/work", "/tmp", "--chdir", "/work",
            "--clearenv", "--setenv", "PATH", "/bin", "--setenv", "HOME", "/work",
            "--setenv", "TMPDIR", "/work", "--setenv", "LC_ALL", "C",
            "--remount-ro", "/", "--seccomp", "0", "--", "/bin/sh", "-c",
            "exec 0</dev/null; " + script.Content, "registered-script");
        foreach (var argument in script.Arguments) info.ArgumentList.Add(argument.Value);
        return info;

        void Add(params string[] arguments)
        {
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        }
    }

    private static bool FilesAvailable() => RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        new[] { "/sys/fs/cgroup/cgroup.controllers", SystemdRun, Bubblewrap, Prlimit, "/usr/bin/busybox" }.All(File.Exists);

    internal static bool HasProtectedCgroupMount(string mountInfo) => mountInfo.Split('\n')
        .Any(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is var fields &&
            fields.Length >= 10 && fields[4] == "/sys/fs/cgroup" &&
            line.Split(" - ", StringSplitOptions.None) is [_, var tail] &&
            tail.Split(' ', StringSplitOptions.RemoveEmptyEntries) is ["cgroup2", _, var options] &&
            options.Split(',').Contains("nsdelegate", StringComparer.Ordinal));

    internal static bool IsStaticElf(byte[] bytes)
    {
        if (bytes.Length < 64 || bytes[0] != 0x7f || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F' ||
            bytes[4] != 2 || bytes[5] != 1) return false;
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32));
        var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(54));
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(56));
        if (size < 56 || count == 0 || offset > (ulong)bytes.Length || (ulong)size * count > (ulong)bytes.Length - offset) return false;
        for (var i = 0; i < count; i++)
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset + i * size)) == 3) return false;
        return true;
    }

    private static bool HasVerifiedBudget(int processId, string unit)
    {
        try
        {
            var membership = File.ReadLines(FormattableString.Invariant($"/proc/{processId}/cgroup"))
                .SingleOrDefault(static line => line.StartsWith("0::/", StringComparison.Ordinal));
            if (membership is null || !membership.EndsWith("/" + unit, StringComparison.Ordinal)) return false;
            var directory = "/sys/fs/cgroup" + membership[3..];
            var cpu = Read("cpu.max").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return Read("memory.max") == "67108864" && Read("memory.swap.max") == "0" && Read("pids.max") == "16" &&
                cpu.Length == 2 && long.TryParse(cpu[0], NumberStyles.None, CultureInfo.InvariantCulture, out var quota) &&
                long.TryParse(cpu[1], NumberStyles.None, CultureInfo.InvariantCulture, out var period) &&
                quota > 0 && period > 0 && quota <= period / 2;

            string Read(string file) => File.ReadAllText(Path.Combine(directory, file)).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsRoot() => RealUserId() is null or "0";

    private static string? RealUserId()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return File.ReadLines("/proc/self/status")
                .First(static line => line.StartsWith("Uid:", StringComparison.Ordinal))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
