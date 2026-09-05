using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services.Elastic;

/// <summary>
/// The bounded local proof adapter (#430): launches self-hosted <c>HVO.SkyMonitor.ProcessingRunner</c> processes on
/// this host with the same environment contract as the container image, tracks them by process, asks them to drain
/// on retirement, and re-adopts instances recorded by a previous host process when their process is still alive.
/// No cloud SDK, no credentials beyond the runner client secret file the host already owns.
/// </summary>
internal sealed partial class LocalProcessElasticRunnerProvider(
    IOptions<CentralElasticProviderOptions> options,
    TimeProvider timeProvider,
    ILogger<LocalProcessElasticRunnerProvider> logger) : IElasticRunnerProvider, IDisposable
{
    public const string ProviderName = "local-process";
    private readonly ConcurrentDictionary<string, TrackedInstance> _instances = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<double> _measuredStartups = new();

    public string Name => ProviderName;

    public ElasticProviderCapabilities Capabilities { get; } = new(
        ProviderName,
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        $"local-process:{RuntimeInformation.RuntimeIdentifier}",
        SupportsScaleToZero: true,
        [$"provider:{ProviderName}"]);

    public TimeSpan EstimateStartup()
    {
        var measured = _measuredStartups.ToArray();
        return measured.Length == 0
            ? options.Value.ExpectedColdStart
            : TimeSpan.FromMilliseconds(measured.Average());
    }

    /// <summary>Feeds a measured cold start back into the startup estimate (the last eight measurements).</summary>
    public void RecordMeasuredStartup(TimeSpan duration)
    {
        _measuredStartups.Enqueue(duration.TotalMilliseconds);
        while (_measuredStartups.Count > 8 && _measuredStartups.TryDequeue(out _))
        {
        }
    }

    public Task<ElasticRunnerInstance> ProvisionAsync(ElasticRunnerProvisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ElasticWorkloadClass.EnsureEligible(request.JobClasses);
        var settings = options.Value.LocalProcess;
        var startInfo = new ProcessStartInfo
        {
            FileName = settings.Executable!,
            WorkingDirectory = settings.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in settings.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in ComposeEnvironment(request, settings))
        {
            startInfo.Environment[key] = value;
        }
        var started = timeProvider.GetUtcNow();
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The runner process could not be started.");
        var instance = new ElasticRunnerInstance(request.InstanceId, request.RunnerId, ElasticRunnerInstanceState.Starting, started, process.Id);
        _instances[request.InstanceId] = new TrackedInstance(instance, process);
        Log.Provisioned(logger, request.InstanceId, request.RunnerId, process.Id);
        return Task.FromResult(instance);
    }

    /// <summary>The runner environment contract (mirrors the container image); the secret is passed by file path only.</summary>
    internal static IReadOnlyDictionary<string, string> ComposeEnvironment(ElasticRunnerProvisionRequest request, CentralLocalProcessElasticOptions settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HVO_RUNNER_LOGICHOST_URL"] = settings.LogicHostUrl!,
            ["HVO_RUNNER_ID"] = request.RunnerId,
            ["HVO_RUNNER_DISPLAY_NAME"] = $"elastic {ProviderName} {request.InstanceId}",
            ["HVO_RUNNER_CLIENT_ID"] = settings.ClientId,
            ["HVO_RUNNER_CLIENT_SECRET_FILE"] = settings.ClientSecretFile!,
            ["HVO_RUNNER_MAX_CONCURRENCY"] = request.MaxConcurrency.ToString(CultureInfo.InvariantCulture),
            ["HVO_RUNNER_LABELS"] = string.Join(',', request.Labels),
            ["HVO_RUNNER_IDLE_SHUTDOWN_SECONDS"] = settings.IdleShutdown.TotalSeconds.ToString(CultureInfo.InvariantCulture),
            ["HVO_RUNNER_ALLOW_INSECURE_HTTP"] = settings.AllowInsecureHttp ? "true" : "false",
            ["HVO_RUNNER_LIVENESS_FILE"] = Path.Combine(Path.GetTempPath(), $"hvo-elastic-{request.InstanceId}.alive")
        };
    }

    public async Task RetireAsync(string instanceId, TimeSpan grace, CancellationToken cancellationToken)
    {
        if (!_instances.TryRemove(instanceId, out var tracked))
        {
            return;
        }
        var process = tracked.Process;
        try
        {
            if (!process.HasExited)
            {
                RequestGracefulStop(process);
                using var timeout = new CancellationTokenSource(grace);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            Log.Retired(logger, instanceId, process.Id, process.HasExited ? process.ExitCode : -1);
        }
        finally
        {
            process.Dispose();
        }
    }

    public Task<IReadOnlyList<ElasticRunnerInstance>> ListAsync(CancellationToken cancellationToken)
    {
        var alive = new List<ElasticRunnerInstance>();
        foreach (var (instanceId, tracked) in _instances)
        {
            if (tracked.Process.HasExited)
            {
                _instances.TryRemove(instanceId, out _);
                tracked.Process.Dispose();
                continue;
            }
            alive.Add(tracked.Instance);
        }
        return Task.FromResult<IReadOnlyList<ElasticRunnerInstance>>(alive);
    }

    /// <summary>Re-adopts an instance a previous host process launched when its process is still alive and started when recorded.</summary>
    public bool TryAdopt(string instanceId, string runnerId, int processId, DateTimeOffset startedAtUtc)
    {
        if (_instances.ContainsKey(instanceId))
        {
            return true;
        }
        try
        {
            var process = Process.GetProcessById(processId);
            if (process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - startedAtUtc.UtcDateTime).TotalMinutes) > 2)
            {
                process.Dispose();
                return false;
            }
            _instances[instanceId] = new TrackedInstance(
                new ElasticRunnerInstance(instanceId, runnerId, ElasticRunnerInstanceState.Running, startedAtUtc, processId), process);
            Log.Adopted(logger, instanceId, processId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void RequestGracefulStop(Process process)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            process.Kill(entireProcessTree: true);
            return;
        }
        // SIGTERM lets the runner drain its lease (drain-before-cancel) before it exits.
        using var kill = Process.Start(new ProcessStartInfo("kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)])
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        kill?.WaitForExit(5000);
    }

    public void Dispose()
    {
        foreach (var tracked in _instances.Values)
        {
            tracked.Process.Dispose();
        }
        _instances.Clear();
    }

    private sealed record TrackedInstance(ElasticRunnerInstance Instance, Process Process);

    private static partial class Log
    {
        [LoggerMessage(2230, LogLevel.Information, "Elastic runner instance provisioned: Instance={Instance}, Runner={Runner}, ProcessId={ProcessId}")]
        public static partial void Provisioned(ILogger logger, string instance, string runner, int processId);

        [LoggerMessage(2231, LogLevel.Information, "Elastic runner instance retired: Instance={Instance}, ProcessId={ProcessId}, ExitCode={ExitCode}")]
        public static partial void Retired(ILogger logger, string instance, int processId, int exitCode);

        [LoggerMessage(2232, LogLevel.Information, "Elastic runner instance adopted from a previous host process: Instance={Instance}, ProcessId={ProcessId}")]
        public static partial void Adopted(ILogger logger, string instance, int processId);
    }
}
