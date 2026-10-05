using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HVO.SkyMonitor.Video.FFmpeg;

/// <summary>
/// Linux-only, argument-list execution. Inherited one-CPU affinity, idle I/O and nice 19 protect capture work.
/// prlimit enforces address-space, file-size and CPU-time ceilings; a monitor additionally caps RSS and scratch.
/// Every cancellation/failure kills the complete tree and reaps it before a caller may reuse the slot.
/// </summary>
internal sealed class BoundedMediaProcess(FFmpegOptions options)
{
    internal static bool Supported => OperatingSystem.IsLinux() &&
        File.Exists("/usr/bin/prlimit") && File.Exists("/usr/bin/taskset") &&
        File.Exists("/usr/bin/nice") && File.Exists("/usr/bin/ionice") && File.Exists("/usr/bin/setpriv");

    internal async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string directory,
        CancellationToken cancellationToken, int outputLimit = 64 * 1024)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !Supported)
            throw new TimeLapseEncodingException("timelapse.resource-boundary-unavailable", "The bounded Linux encoder boundary is unavailable.");
        using var current = Process.GetCurrentProcess();
        var affinity = (ulong)current.ProcessorAffinity;
        if (affinity == 0)
            throw new TimeLapseEncodingException("timelapse.cpu-affinity-unavailable", "No bounded encoder CPU is available.");
        var oneCpu = affinity & (~affinity + 1);
        var start = new ProcessStartInfo("/usr/bin/prlimit")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        foreach (var item in new[]
        {
            "--as=" + options.MaximumAddressSpaceBytes.ToString(CultureInfo.InvariantCulture),
            "--fsize=" + options.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture),
            "--cpu=" + ((long)Math.Ceiling(options.Timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
            "--nofile=128", "--", "/usr/bin/taskset", oneCpu.ToString("X", CultureInfo.InvariantCulture),
            "/usr/bin/nice", "-n", "19", "/usr/bin/ionice", "-c", "3",
            "/usr/bin/setpriv", "--pdeathsig", "KILL", "--no-new-privs", "--", executable
        }) start.ArgumentList.Add(item);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(options.Timeout);
        // PR_SET_PDEATHSIG follows the creating native thread, not merely the process. An async
        // thread-pool caller may retire during a long encode. Keep a dedicated creator alive until exit.
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = Task.Factory.StartNew(() =>
        {
            try
            {
                if (!process.Start())
                {
                    started.SetException(new TimeLapseEncodingException("timelapse.start-failed", "Encoder process could not start."));
                    return;
                }
            }
            catch (Win32Exception)
            {
                started.SetException(new TimeLapseEncodingException("timelapse.executable-unavailable", "Encoder executable is unavailable."));
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                started.SetException(exception);
                return;
            }
            started.SetResult(true);
            process.WaitForExit();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { await started.Task.ConfigureAwait(false); }
        catch
        {
            await owner.ConfigureAwait(false);
            throw;
        }
        // Drain both pipes concurrently; neither pipe can deadlock a child or grow memory without a bound.
        var stdout = ReadBoundedAsync(process.StandardOutput, outputLimit, lifetime.Token);
        var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, lifetime.Token);
        try
        {
            while (!process.HasExited)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (stdout.IsFaulted) await stdout.ConfigureAwait(false);
                if (stderr.IsFaulted) await stderr.ConfigureAwait(false);
                if (ResidentBytes(process) > options.MaximumResidentBytes)
                    throw new TimeLapseEncodingException("timelapse.memory-bound", "Encoder working set exceeded its bound.");
                if (Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length) > options.MaximumScratchBytes)
                    throw new TimeLapseEncodingException("timelapse.scratch-bound", "Encoder scratch exceeded its bound.");
                await Task.Delay(TimeSpan.FromMilliseconds(100), lifetime.Token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new TimeLapseEncodingException("timelapse.process-failed",
                    $"Encoder exited {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {error[..Math.Min(error.Length, 2048)]}");
            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await owner.ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            // Observe canceled/faulted readers after reaping; the original process failure remains authoritative.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeLapseEncodingException) { }
            catch (IOException) { }
        }
    }

    private static long ResidentBytes(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // Exit can race the monitor after HasExited was checked. The exit code and both
            // drained pipes still decide success; a completed process no longer consumes RSS.
            return 0;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > limit)
                throw new TimeLapseEncodingException("timelapse.diagnostic-bound", "Encoder diagnostic output exceeded its bound.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
