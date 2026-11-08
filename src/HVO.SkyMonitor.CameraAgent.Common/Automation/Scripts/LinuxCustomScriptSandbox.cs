using System.ComponentModel;
using System.Diagnostics;
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
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, outputLimit.Token);
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
        return new(outputLimit.IsCancellationRequested ? "output-limit" : timeout.IsCancellationRequested ? "timeout" : "completed",
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
        var info = new ProcessStartInfo(Prlimit)
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
        Add("--core=0", "--as=33554432", "--nofile=64", "--nproc=512", "--fsize=" + script.ScratchBytes,
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
        new[] { Bubblewrap, Prlimit, "/usr/bin/dash", "/usr/bin/sleep", LibcPath, LoaderPath }.All(File.Exists);

    // RLIMIT_NPROC is enforced against the real UID and does not restrict root.
    private static bool IsRoot() => File.ReadLines("/proc/self/status")
        .First(static line => line.StartsWith("Uid:", StringComparison.Ordinal)).Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)[1] == "0";
}
