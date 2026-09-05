using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
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

    /// <summary>
    /// The capabilities a child registers: probed from the configured executable (<c>--capabilities</c>, the runner's
    /// own advertisement, so a separately published binary, launcher, or other architecture is described as it really
    /// is) with the instance's concurrency and labels applied. A failed probe describes nothing (null), so the host
    /// provisions no instance that would abort before registration; the probe is retried after a cooldown.
    /// </summary>
    public ProcessingRunnerCapabilities? DescribeInstance(int maxConcurrency, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var normalized = labels.Distinct(StringComparer.Ordinal).OrderBy(static label => label, StringComparer.Ordinal).ToArray();
        return ProbeConfiguredRunner() is { } probed
            ? probed with { MaxConcurrency = maxConcurrency, Labels = normalized }
            : null;
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan ProbeRetryInterval = TimeSpan.FromMinutes(5);
    private readonly Lock _probeGate = new();
    private DateTimeOffset? _probeAttemptedAtUtc;
    private ProcessingRunnerCapabilities? _probed;

    /// <summary>
    /// Runs the configured executable with <c>--capabilities</c> and parses its advertisement (accepted only on a
    /// zero exit, the runner's own warm signal); a successful probe is kept for the host's lifetime, a failed one is
    /// retried after <see cref="ProbeRetryInterval"/>. Null while no probe has succeeded.
    /// </summary>
    internal ProcessingRunnerCapabilities? ProbeConfiguredRunner()
    {
        lock (_probeGate)
        {
            var now = timeProvider.GetUtcNow();
            if (_probed is not null || (_probeAttemptedAtUtc is { } attempted && now - attempted < ProbeRetryInterval))
            {
                return _probed;
            }
            _probeAttemptedAtUtc = now;
            var settings = options.Value.LocalProcess;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = settings.Executable!,
                    WorkingDirectory = settings.WorkingDirectory ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in settings.Arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }
                startInfo.ArgumentList.Add("--capabilities");
                startInfo.Environment.Clear();
                foreach (var (key, value) in FilterInheritedEnvironment(Environment.GetEnvironmentVariables()))
                {
                    startInfo.Environment[key] = value;
                }
                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The runner probe process could not be started.");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(ProbeTimeout))
                {
                    process.Kill(entireProcessTree: true);
                    throw new TimeoutException("The runner capability probe did not finish in time.");
                }
                if (process.ExitCode != 0)
                {
                    // The runner prints its capabilities even when warmup is incomplete (exit 1); such a runner aborts
                    // normal startup, so its advertisement is not accepted either.
                    throw new InvalidOperationException($"The runner capability probe exited with code {process.ExitCode}; its warmup is incomplete.");
                }
                var line = stdout.GetAwaiter().GetResult().Split('\n').Select(static item => item.Trim()).LastOrDefault(static item => item.StartsWith('{'))
                    ?? throw new InvalidOperationException("The runner capability probe wrote no capabilities.");
                var probed = JsonSerializer.Deserialize<ProcessingRunnerCapabilities>(line, ProcessingRunnerProtocol.SerializerOptions)
                    ?? throw new InvalidOperationException("The runner capability probe wrote an empty document.");
                probed.Validate();
                _probed = probed;
                Log.CapabilitiesProbed(logger, settings.Executable!, probed.ProcessArchitecture, probed.RuntimeIdentifier, probed.BuiltInRecipes.Count);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or JsonException
                or System.ComponentModel.Win32Exception or ProcessingRunnerProtocolException or UnauthorizedAccessException)
            {
                Log.CapabilityProbeFailed(logger, settings.Executable ?? string.Empty, exception);
                _probed = null;
            }
            return _probed;
        }
    }

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
            if (settings.LocalProcess.RequireProcessGroupIsolation && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SetsidPath is null)
            {
                throw new InvalidOperationException(
                    "Process-group isolation is required for elastic runner instances but 'setsid' is not available on this host; install util-linux or set ElasticProviders:LocalProcess:RequireProcessGroupIsolation=false for an executable that is the runner itself.");
            }
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
        => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SetsidPath is { } setsid
            ? (setsid, [settings.Executable!])
            : (settings.Executable!, []);

    /// <summary>The <c>setsid</c> binary found on PATH or in the usual system locations; null when unavailable.</summary>
    internal static string? SetsidPath { get; } = LocateSetsid();

    private static string? LocateSetsid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/bin", "/bin", "/usr/local/bin", "/opt/homebrew/bin"]);
        return directories.Select(directory => Path.Combine(directory, "setsid")).FirstOrDefault(File.Exists);
    }

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
            ["HVO_RUNNER_IDLE_SHUTDOWN_SECONDS"] = options.EffectiveInstanceIdleShutdown(request.KeepWarm).TotalSeconds.ToString(CultureInfo.InvariantCulture),
            // The child drains for the same grace the host waits before forcing it, so a longer RetireGrace lets long jobs finish.
            ["HVO_RUNNER_SHUTDOWN_GRACE_SECONDS"] = options.RetireGrace.TotalSeconds.ToString(CultureInfo.InvariantCulture),
            ["HVO_RUNNER_ALLOW_INSECURE_HTTP"] = settings.AllowInsecureHttp ? "true" : "false",
            ["HVO_RUNNER_LIVENESS_FILE"] = Path.Combine(Path.GetTempPath(), $"hvo-elastic-{request.InstanceId}.alive"),
            ["HVO_RUNNER_STOP_FILE"] = StopFilePath(request.InstanceId)
        };
    }

    public Task RetireAsync(string instanceId, TimeSpan grace, CancellationToken cancellationToken)
        => RetireAsync([instanceId], grace, cancellationToken);

    /// <summary>
    /// Drain requests for every instance are issued first (stop file, and SIGTERM to the process group on Unix), then
    /// each instance is awaited until its whole process group is gone or the grace period ends, and only then forced.
    /// An instance stays tracked until its drain request has been signalled, so a failed stop-file write never leaves
    /// a running process unaccounted.
    /// </summary>
    public async Task RetireAsync(IReadOnlyCollection<string> instanceIds, TimeSpan grace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        var draining = new List<(string InstanceId, TrackedInstance Tracked)>();
        foreach (var instanceId in instanceIds.Distinct(StringComparer.Ordinal))
        {
            if (!_instances.TryGetValue(instanceId, out var tracked))
            {
                continue;
            }
            if (!tracked.Process.HasExited)
            {
                RequestGracefulStop(instanceId, tracked.Process);
            }
            draining.Add((instanceId, tracked));
        }
        var deadline = timeProvider.GetUtcNow() + grace;
        foreach (var (instanceId, tracked) in draining)
        {
            var process = tracked.Process;
            var stopped = false;
            try
            {
                var remaining = deadline - timeProvider.GetUtcNow();
                if (!process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                    try
                    {
                        await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !process.HasExited)
                    {
                        ForceStop(process);
                    }
                }
                // A launcher may exit before the runner it started: wait for the whole group until the deadline, then force it.
                await WaitForGroupExitAsync(process.Id, deadline, cancellationToken).ConfigureAwait(false);
                ForceStopGroup(process.Id);
                stopped = true;
                Log.Retired(logger, instanceId, process.Id, ExitCodeOrUnknown(process));
            }
            finally
            {
                if (stopped)
                {
                    _instances.TryRemove(instanceId, out _);
                    try
                    {
                        File.Delete(StopFilePath(instanceId));
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    process.Dispose();
                }
                // A caller cancellation (host shutdown) leaves the instance tracked and its stop file in place: the
                // runner still drains on its own signal, and the next host reconciles or re-adopts it.
            }
        }
    }

    private async Task WaitForGroupExitAsync(int processId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || SetsidPath is null)
        {
            return;
        }
        while (GroupHasMembers(processId) && timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><c>kill -0</c> against the group reports whether any member is still alive.</summary>
    private static bool GroupHasMembers(int processId)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("kill", ["-0", "--", $"-{processId.ToString(CultureInfo.InvariantCulture)}"]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
            if (probe is null)
            {
                return false;
            }
            probe.WaitForExit(5000);
            return probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
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

    private void RequestGracefulStop(string instanceId, Process process)
    {
        // The stop file is the provider-neutral drain request the runner honours on every platform; a failed write
        // is logged and the Unix signal (or the forced stop after grace) still retires the instance.
        try
        {
            File.WriteAllText(StopFilePath(instanceId), "stop");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.StopFileFailed(logger, instanceId, exception);
        }
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
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SetsidPath is not null && GroupHasMembers(processId))
        {
            Signal(processId, "-KILL");
        }
    }

    /// <summary>Signals the process group led by <paramref name="processId"/> (the instance was started through setsid).</summary>
    private static void Signal(int processId, string signal)
    {
        var target = SetsidPath is not null ? $"-{processId.ToString(CultureInfo.InvariantCulture)}" : processId.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>An adopted process (started by a previous host process) exposes no exit code; -1 stands in.</summary>
    private static int ExitCodeOrUnknown(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
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

        [LoggerMessage(2243, LogLevel.Information, "Elastic runner capabilities probed from the configured executable: Executable={Executable}, ProcessArchitecture={ProcessArchitecture}, RuntimeIdentifier={RuntimeIdentifier}, Recipes={Recipes}")]
        public static partial void CapabilitiesProbed(ILogger logger, string executable, string processArchitecture, string runtimeIdentifier, int recipes);

        [LoggerMessage(2244, LogLevel.Warning, "Elastic runner capability probe failed; nothing is provisioned until a later probe succeeds: Executable={Executable}")]
        public static partial void CapabilityProbeFailed(ILogger logger, string executable, Exception exception);

        [LoggerMessage(2238, LogLevel.Warning, "Elastic runner stop file could not be written; the instance is retired by signal or force: Instance={Instance}")]
        public static partial void StopFileFailed(ILogger logger, string instance, Exception exception);
    }
}
