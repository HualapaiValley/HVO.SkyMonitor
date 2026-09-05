using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner;

internal static class Program
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The executable boundary converts startup and loop failures into structured diagnostics.")]
    internal static async Task<int> Main(string[] args)
    {
        var capabilitiesOnly = args.Length == 1 && args[0] is "--capabilities" or "--self-test";
        var probeOnly = args.Length == 1 && args[0] == "--probe";
        var log = RunnerLog.Console(Environment.GetEnvironmentVariable("HVO_RUNNER_ID"));
        if (args.Length != 0 && !capabilitiesOnly && !probeOnly)
        {
            log.Error("usage-error", "Supported arguments are --capabilities, --self-test, and --probe.");
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
            if (capabilitiesOnly)
            {
                var warmupOnly = await RunnerWarmup.ExecuteAsync(cancellation.Token).ConfigureAwait(false);
                var advertised = ProcessingRunnerCapabilities.CreateForCurrentProcess(
                    1, ProcessingRunnerProtocol.MaximumTransferBytes, null, null, null, warmupOnly.Stages);
                await Console.Out.WriteLineAsync(
                    JsonSerializer.Serialize(advertised, RunnerJsonContext.Default.ProcessingRunnerCapabilities)).ConfigureAwait(false);
                return warmupOnly.Stages.IsWarm ? 0 : 1;
            }
            var options = RunnerOptions.Load();
            log = RunnerLog.Console(options.RunnerId);
            using var http = new HttpClient { BaseAddress = options.LogicHostBaseAddress, Timeout = options.RequestTimeout };
            using var client = new ProcessingRunnerClient(http, new ProcessingRunnerClientOptions
            {
                RunnerId = options.RunnerId,
                ClientId = options.ClientId,
                ClientSecret = options.ClientSecret,
                MaxTransferBytes = options.MaxTransferBytes
            });
            if (probeOnly)
            {
                // The probe never heartbeats: a hung runner loop must fail the probe, not be revived by it. It checks
                // the local liveness file written by the real heartbeat loop and LogicHost's non-mutating status.
                if (!File.Exists(options.LivenessFile))
                {
                    log.Error("probe", $"Liveness file {options.LivenessFile} is missing; the runner loop has not heartbeated.");
                    return 1;
                }
                var content = await File.ReadAllTextAsync(options.LivenessFile, cancellation.Token).ConfigureAwait(false);
                var heartbeatUtc = RunnerLiveness.TryParse(content, out var stamped, out var interval)
                    ? stamped
                    : File.GetLastWriteTimeUtc(options.LivenessFile);
                var age = DateTimeOffset.UtcNow - heartbeatUtc;
                var maxAge = RunnerLiveness.ResolveMaxAge(options.ProbeMaxAge, interval);
                if (age > maxAge)
                {
                    log.Error("probe", $"Liveness file is {age.TotalSeconds:0}s old (limit {maxAge.TotalSeconds:0}s from the negotiated heartbeat interval); the runner loop is stalled.");
                    return 1;
                }
                var status = await client.GetStatusAsync(cancellation.Token).ConfigureAwait(false);
                log.Info("probe", $"Runner loop heartbeated {age.TotalSeconds:0}s ago; LogicHost reports {status.Status} with {status.ActiveLeases} active lease(s).");
                return status.Status == ProcessingRunnerRegistrationStatus.Active ? 0 : 1;
            }
            var warmup = await RunnerWarmup.ExecuteAsync(cancellation.Token).ConfigureAwait(false);
            var capabilities = ProcessingRunnerCapabilities.CreateForCurrentProcess(
                options.MaxConcurrency,
                options.MaxTransferBytes,
                options.ResourceClass,
                options.LatencyClass,
                options.Labels,
                warmup.Stages,
                options.GpuAvailable);
            if (!warmup.Stages.IsWarm)
            {
                throw new InvalidOperationException("Processing runner warmup did not establish its declared recipe capabilities.");
            }
            log.Info("starting",
                $"Processing runner starting: concurrency {options.MaxConcurrency}, {capabilities.ProcessArchitecture}, resource class {capabilities.ResourceClass}, latency class {capabilities.LatencyClass}, warm-up {warmup.Stages.RuntimeJit.Elapsed.TotalMilliseconds + warmup.Stages.NativeLibraries.Elapsed.TotalMilliseconds:0} ms.");
            using var host = new RunnerHost(
                client,
                warmup.Executor,
                new RunnerHostOptions(
                    options.RunnerId,
                    options.DisplayName,
                    capabilities,
                    options.MaxConcurrency,
                    options.MaxTransferBytes,
                    options.IdleShutdown,
                    options.ShutdownGrace,
                    options.RegistrationRetry,
                    LivenessFile: options.LivenessFile,
                    StopFile: options.StopFile),
                log);
            var exit = await host.RunAsync(cancellation.Token).ConfigureAwait(false);
            log.Info("stopped", "Processing runner stopped gracefully.");
            return exit;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            log.Info("stopped", "Processing runner canceled gracefully.");
            return 0;
        }
        catch (Exception exception)
        {
            log.Error("runner-failed", exception.Message);
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
}
