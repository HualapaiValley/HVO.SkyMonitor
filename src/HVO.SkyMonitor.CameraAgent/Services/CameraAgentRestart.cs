using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// CameraAgent's restart: it stops its own host and relies on whatever supervises it (its container's restart policy)
/// to start it again. The Camera &amp; rig page's Restart now and <c>POST /api/v1/operations/restart</c> share one
/// one-shot latch per host lifetime, so a restart is requested once however many callers ask.
/// </summary>
internal static partial class CameraAgentRestart
{
    internal const string SupervisedKey = "CameraAgent:Restart:Supervised";

    // Callers are scoped (a circuit, a request), so the latch is keyed by the process-wide host lifetime.
    private static readonly ConditionalWeakTable<IHostApplicationLifetime, string> Requests = new();

    /// <summary>The delay between answering the request and stopping, so the answer reaches the caller.</summary>
    internal static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Restart is offered only when something will start the process again: an explicit
    /// <c>CameraAgent:Restart:Supervised</c> setting wins; otherwise a .NET container image
    /// (<c>DOTNET_RUNNING_IN_CONTAINER=true</c>) is treated as supervised by its restart policy.
    /// </summary>
    internal static bool IsSupervised(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[SupervisedKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return bool.TryParse(configured, out var supervised) && supervised;
        return string.Equals(configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True once a restart was requested in this host lifetime, or the host is already stopping.</summary>
    internal static bool IsRequested(IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        return lifetime.ApplicationStopping.IsCancellationRequested || Requests.TryGetValue(lifetime, out _);
    }

    /// <summary>
    /// Requests the restart for <paramref name="actor"/>: the host stops <paramref name="delay"/> after this returns,
    /// unless it is not supervised or a restart was already requested.
    /// </summary>
    internal static CameraAgentRestartDisposition Request(
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        string actor,
        TimeSpan delay,
        TimeProvider timeProvider,
        ILogger logger,
        Action<ILogger, string> logRequested)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logRequested);
        if (!IsSupervised(configuration))
            return CameraAgentRestartDisposition.Unsupervised;
        if (lifetime.ApplicationStopping.IsCancellationRequested || !Requests.TryAdd(lifetime, actor))
            return CameraAgentRestartDisposition.AlreadyRequested;
        logRequested(logger, actor);
        _ = StopAfterDelayAsync(lifetime, delay, timeProvider, logger);
        return CameraAgentRestartDisposition.Scheduled;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed background stop is logged and the one-shot latch is released for a retry.")]
    private static async Task StopAfterDelayAsync(
        IHostApplicationLifetime lifetime,
        TimeSpan delay,
        TimeProvider timeProvider,
        ILogger logger)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timeProvider).ConfigureAwait(false);
            lifetime.StopApplication();
        }
        catch (Exception exception)
        {
            Requests.Remove(lifetime);
            LogStopFailed(logger, exception);
        }
    }

    [LoggerMessage(LogLevel.Error, "CameraAgent restart request could not stop the host.")]
    private static partial void LogStopFailed(ILogger logger, Exception exception);
}
