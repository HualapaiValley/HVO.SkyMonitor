using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

internal sealed record CustomScriptSandboxResult(string Status, int? ExitCode, long StdoutBytes, long StderrBytes);

/// <summary>
/// A deliberately small Linux runtime: dash builtins and sleep, system libc/loader, private proc/dev,
/// no host directories, network namespace, capabilities, inherited environment or published files.
/// </summary>
internal static class LinuxCustomScriptSandbox
{
    private const string Bubblewrap = "/usr/bin/bwrap";
    private const string SystemdRun = "/usr/bin/systemd-run";
    private const string Prlimit = "/usr/bin/prlimit";

    internal static bool Probe()
    {
        if (!OperatingSystem.IsLinux() || !FilesAvailable() || IsRoot()) return false;
        var result = ExecuteAsync(new() { Id = "probe", Content = ":", TimeoutSeconds = 3 }, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        return result.Status == "completed" && result.ExitCode == 0;
    }

    internal static async ValueTask<CustomScriptSandboxResult> ExecuteAsync(CustomScriptRegistration script,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(script.TimeoutSeconds));
        using var outputLimit = new CancellationTokenSource();
        using var launchFailure = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, outputLimit.Token, launchFailure.Token);
        using var process = new Process { StartInfo = BuildStartInfo(script) };
        try
        {
            if (!process.Start()) return new("launch-failed", null, 0, 0);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return new("launch-failed", null, 0, 0);
        }
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
            Kill();
        }
        finally
        {
            // The PID namespace init dies with its parent; exiting it kills even detached descendants.
            // Keep the worker/authority until exit and both redirected pipes have really drained.
            if (!process.HasExited) Kill();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await input.ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(launchFailure.IsCancellationRequested ? "launch-failed" : outputLimit.IsCancellationRequested ? "output-limit" : timeout.IsCancellationRequested ? "timeout" : "completed",
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
                var unit = process.StartInfo.ArgumentList.Single(static argument => argument.StartsWith("--unit=", StringComparison.Ordinal))[7..] + ".scope";
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
                await process.StandardInput.WriteAsync(script.Content.AsMemory(), stop.Token).ConfigureAwait(false);
                await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
                await process.StandardInput.FlushAsync(stop.Token).ConfigureAwait(false);
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
            "--property=MemoryMax=67108864", "--property=MemorySwapMax=0",
            "--property=TasksMax=16", "--property=CPUQuota=50%", "--", Prlimit,
            "--core=0", "--as=33554432", "--nofile=64", "--fsize=" + script.ScratchBytes,
            "--cpu=" + script.TimeoutSeconds, "--", Bubblewrap,
            "--unshare-all", "--unshare-user", "--die-with-parent", "--new-session", "--cap-drop", "ALL",
            "--ro-bind", "/usr/bin/dash", "/bin/sh", "--ro-bind", "/usr/bin/sleep", "/bin/sleep",
            "--ro-bind", LibcPath, "/lib/libc.so.6", "--ro-bind", LoaderPath, LoaderPath,
            "--proc", "/proc", "--remount-ro", "/proc", "--dev-bind", "/dev/null", "/dev/null", "--size", script.ScratchBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--tmpfs", "/work", "--symlink", "/work", "/tmp", "--chdir", "/work",
            "--clearenv", "--setenv", "PATH", "/bin", "--setenv", "HOME", "/work",
            "--setenv", "TMPDIR", "/work", "--setenv", "LC_ALL", "C",
            "--remount-ro", "/", "--", "/bin/sh", "-s", "--");
        foreach (var argument in script.Arguments) info.ArgumentList.Add(argument.Value);
        return info;

        void Add(params string[] arguments)
        {
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        }
    }

    private static string LibcPath => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "/lib/aarch64-linux-gnu/libc.so.6" : "/lib/x86_64-linux-gnu/libc.so.6";
    private static string LoaderPath => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "/lib/ld-linux-aarch64.so.1" : "/lib64/ld-linux-x86-64.so.2";

    private static bool FilesAvailable() => RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64 &&
        new[] { "/sys/fs/cgroup/cgroup.controllers", SystemdRun, Bubblewrap, Prlimit, "/usr/bin/dash", "/usr/bin/sleep", LibcPath, LoaderPath }.All(File.Exists);

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
