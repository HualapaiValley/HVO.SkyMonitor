using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Replay;

namespace HVO.SkyMonitor.CameraAgent.ReplayRunner;

internal static class Program
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The executable boundary converts startup and server failures into structured diagnostics.")]
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The await-using resource has no synchronization-context dependency.")]
    internal static async Task<int> Main(string[] args)
    {
        var capabilitiesOnly = args.Length == 1 && args[0] is "--capabilities" or "--self-test";
        var probeOnly = args.Length == 1 && args[0] == "--probe";
        if (args.Length != 0 && !capabilitiesOnly && !probeOnly)
        {
            WriteLog("error", "usage-error", "Supported arguments are --capabilities, --self-test, and --probe.");
            return 2;
        }

        TrySetBelowNormalPriority();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });

        try
        {
            var options = RunnerConfiguration.Load(requireAuthentication: !capabilitiesOnly);
            if (probeOnly)
            {
                await using var client = new LocalReplayRunnerClient(options);
                var peerCapabilities = await client.ProbeAsync(cancellation.Token).ConfigureAwait(false);
                await Console.Out.WriteLineAsync(
                    JsonSerializer.Serialize(peerCapabilities, RunnerJsonContext.Default.ReplayRunnerCapabilities)).ConfigureAwait(false);
                return 0;
            }
            var warmup = await RunnerWarmup.ExecuteAsync(cancellation.Token).ConfigureAwait(false);
            var capabilities = ReplayRunnerCapabilities.Create(options, warmup.Stages);
            var warmupSucceeded = warmup.Stages.RuntimeJit.Status == ReplayRunnerWarmupStatus.Completed &&
                warmup.Stages.NativeLibraries.Status == ReplayRunnerWarmupStatus.Completed;
            if (capabilitiesOnly)
            {
                await Console.Out.WriteLineAsync(
                    JsonSerializer.Serialize(capabilities, RunnerJsonContext.Default.ReplayRunnerCapabilities)).ConfigureAwait(false);
                return warmupSucceeded ? 0 : 1;
            }
            if (!warmupSucceeded)
            {
                throw new InvalidOperationException("Replay runner warmup did not establish its declared recipe capabilities.");
            }

            var endpoint = options.Transport == ReplayRunnerTransport.UnixDomainSocket
                ? options.SocketPath
                : $"127.0.0.1:{options.LoopbackPort}";
            WriteLog(
                "info",
                "starting",
                $"Replay runner starting with concurrency {options.MaxConcurrency} and idle shutdown {options.IdleShutdownSeconds}s.",
                options.Transport.ToString(),
                endpoint);
            await using var server = new LocalReplayRunnerServer(options, warmup.Executor, capabilities);
            await server.RunAsync(cancellation.Token).ConfigureAwait(false);
            WriteLog("info", "stopped", "Replay runner stopped gracefully.", options.Transport.ToString(), endpoint);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            WriteLog("info", "stopped", "Replay runner canceled gracefully.");
            return 0;
        }
        catch (Exception exception)
        {
            WriteLog("error", "runner-failed", exception.Message);
            return 1;
        }
    }

    private static void TrySetBelowNormalPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void WriteLog(
        string level,
        string eventName,
        string message,
        string? transport = null,
        string? endpoint = null)
    {
        var entry = new RunnerLogEvent(
            DateTimeOffset.UtcNow,
            level,
            eventName,
            message,
            transport,
            endpoint,
            Environment.ProcessId);
        var json = JsonSerializer.Serialize(entry, RunnerJsonContext.Default.RunnerLogEvent);
        if (string.Equals(level, "error", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(json);
        }
        else
        {
            Console.Out.WriteLine(json);
        }
    }
}
