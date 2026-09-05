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

    private readonly SemaphoreSlim _provisionLock = new(1, 1);

    public async Task<ElasticRunnerInstance> ProvisionAsync(ElasticRunnerProvisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ElasticWorkloadClass.EnsureEligible(request.JobClasses);
        await _provisionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Idempotent per instance id: a retried request returns the instance already launched for it.
            if (_instances.TryGetValue(request.InstanceId, out var existing) && !existing.Process.HasExited)
            {
                return existing.Instance;
            }
            var settings = options.Value;
            var (fileName, leadingArguments) = ResolveLauncher(settings.LocalProcess);
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = settings.LocalProcess.WorkingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in leadingArguments.Concat(settings.LocalProcess.Arguments))
            {
                startInfo.ArgumentList.Add(argument);
            }
            // The child never inherits the host's secrets: only an allowlisted runtime environment plus the runner contract.
            startInfo.Environment.Clear();
            foreach (var (key, value) in FilterInheritedEnvironment(Environment.GetEnvironmentVariables()))
            {
                startInfo.Environment[key] = value;
            }
            foreach (var (key, value) in ComposeEnvironment(request, settings))
            {
                startInfo.Environment[key] = value;
            }
            File.Delete(StopFilePath(request.InstanceId));
            var started = timeProvider.GetUtcNow();
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The runner process could not be started.");
            var instance = new ElasticRunnerInstance(request.InstanceId, request.RunnerId, ElasticRunnerInstanceState.Starting, started, process.Id);
            _instances[request.InstanceId] = new TrackedInstance(instance, process);
            Log.Provisioned(logger, request.InstanceId, request.RunnerId, process.Id);
            return instance;
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    /// <summary>
    /// On Unix the runner is started through <c>setsid</c> so it leads its own process group: a launcher script and
    /// every descendant are then terminated together. Elsewhere the executable is started directly.
    /// </summary>
    private static (string FileName, string[] LeadingArguments) ResolveLauncher(CentralLocalProcessElasticOptions settings)
        => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(SetsidPath)
            ? (SetsidPath, [settings.Executable!])
            : (settings.Executable!, []);

    private const string SetsidPath = "/usr/bin/setsid";

    /// <summary>Environment a launched runner may inherit from the host: runtime and locale essentials, never credentials or connection strings.</summary>
    internal static IReadOnlyDictionary<string, string> FilterInheritedEnvironment(System.Collections.IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var allowed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in environment)
        {
            if (entry.Key is not string key || entry.Value is not string value)
            {
                continue;
            }
            var inherit = InheritedEnvironmentNames.Contains(key)
                || InheritedEnvironmentPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
            if (inherit && !key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) && !key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                && !key.Contains("KEY", StringComparison.OrdinalIgnoreCase) && !key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase))
            {
                allowed[key] = value;
            }
        }
        return allowed;
    }

    private static readonly HashSet<string> InheritedEnvironmentNames = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LANGUAGE", "TZ", "USER", "LOGNAME", "SHELL", "SystemRoot", "SYSTEMROOT",
        "windir", "ProgramData", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "COMSPEC", "PATHEXT", "SSL_CERT_FILE", "SSL_CERT_DIR"
    };

    private static readonly string[] InheritedEnvironmentPrefixes = ["LC_", "DOTNET_", "XDG_"];

    internal static string StopFilePath(string instanceId) => Path.Combine(Path.GetTempPath(), $"hvo-elastic-{instanceId}.stop");

    /// <summary>The runner environment contract (mirrors the container image); the secret is passed by file path only.</summary>
    internal static IReadOnlyDictionary<string, string> ComposeEnvironment(ElasticRunnerProvisionRequest request, CentralElasticProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.LocalProcess;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HVO_RUNNER_LOGICHOST_URL"] = settings.LogicHostUrl!,
            ["HVO_RUNNER_ID"] = request.RunnerId,
            ["HVO_RUNNER_DISPLAY_NAME"] = $"elastic {ProviderName} {request.InstanceId}",
            ["HVO_RUNNER_CLIENT_ID"] = settings.ClientId,
            ["HVO_RUNNER_CLIENT_SECRET_FILE"] = settings.ClientSecretFile!,
            ["HVO_RUNNER_MAX_CONCURRENCY"] = request.MaxConcurrency.ToString(CultureInfo.InvariantCulture),
            ["HVO_RUNNER_LABELS"] = string.Join(',', request.Labels),
            ["HVO_RUNNER_IDLE_SHUTDOWN_SECONDS"] = options.EffectiveInstanceIdleShutdown().TotalSeconds.ToString(CultureInfo.InvariantCulture),
            ["HVO_RUNNER_ALLOW_INSECURE_HTTP"] = settings.AllowInsecureHttp ? "true" : "false",
            ["HVO_RUNNER_LIVENESS_FILE"] = Path.Combine(Path.GetTempPath(), $"hvo-elastic-{request.InstanceId}.alive"),
            ["HVO_RUNNER_STOP_FILE"] = StopFilePath(request.InstanceId)
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
                // Drain first on every platform (stop file, plus SIGTERM to the process group on Unix); force after grace.
                RequestGracefulStop(instanceId, process);
                using var timeout = new CancellationTokenSource(grace);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !process.HasExited)
                {
                    ForceStop(process);
                }
            }
            // The launcher may have exited while a descendant lingers: the whole group is signalled once more.
            ForceStopGroup(process.Id);
            Log.Retired(logger, instanceId, process.Id, process.HasExited ? process.ExitCode : -1);
        }
        finally
        {
            File.Delete(StopFilePath(instanceId));
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

    private static void RequestGracefulStop(string instanceId, Process process)
    {
        // The stop file is the provider-neutral drain request the runner honours on every platform.
        File.WriteAllText(StopFilePath(instanceId), "stop");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // SIGTERM to the process group lets the runner (and anything a launcher script started) drain its lease.
            Signal(process.Id, "-TERM");
        }
    }

    private static void ForceStop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        ForceStopGroup(process.Id);
    }

    private static void ForceStopGroup(int processId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(SetsidPath))
        {
            Signal(processId, "-KILL");
        }
    }

    /// <summary>Signals the process group led by <paramref name="processId"/> (the instance was started through setsid).</summary>
    private static void Signal(int processId, string signal)
    {
        var target = File.Exists(SetsidPath) ? $"-{processId.ToString(CultureInfo.InvariantCulture)}" : processId.ToString(CultureInfo.InvariantCulture);
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", [signal, "--", target]) { UseShellExecute = false, CreateNoWindow = true });
            kill?.WaitForExit(5000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        foreach (var tracked in _instances.Values)
        {
            tracked.Process.Dispose();
        }
        _instances.Clear();
        _provisionLock.Dispose();
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
