using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

/// <summary>Measures this host's clock against network time and keeps the latest result.</summary>
public interface IClockSyncMonitor
{
    /// <summary>The settings as the host configuration holds them now.</summary>
    TimeSyncSettings Settings { get; }

    /// <summary>The latest completed round, or null before the first one completes.</summary>
    ClockSyncSnapshot? Latest { get; }

    /// <summary>
    /// Runs a measurement round now, or joins the one already running. On-demand rounds are bounded to one per
    /// <see cref="ClockSyncMonitor.OnDemandInterval"/>; a request inside that window returns the latest result instead.
    /// </summary>
    Task<ClockCheckResult> CheckNowAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One measurement round: every queried server's outcome, the answer chosen from them, and the kernel's view.
/// <see cref="IgnoredEntries"/> counts configured entries that were not queried because they are not valid server
/// names or exceed <see cref="TimeSyncSettings.MaximumServers"/>; their text is never kept.
/// </summary>
public sealed record ClockSyncSnapshot(
    DateTimeOffset MeasuredUtc,
    bool Enabled,
    TimeServerResult? Selected,
    IReadOnlyList<TimeServerResult> Servers,
    int IgnoredEntries,
    KernelClockState Kernel);

public enum ClockCheckOutcome
{
    /// <summary>A new round ran.</summary>
    Measured,

    /// <summary>A round was already running and this request waited for it.</summary>
    Joined,

    /// <summary>An on-demand round ran too recently; the latest result is returned.</summary>
    RateLimited,
}

public sealed record ClockCheckResult(ClockCheckOutcome Outcome, ClockSyncSnapshot? Snapshot, DateTimeOffset? RetryAfterUtc);

/// <summary>
/// Measures the clock at startup and then every <see cref="TimeSyncSettings.Interval"/>, reading the settings afresh
/// each round. A round queries the configured servers in parallel, each within the query timeout, and chooses the
/// valid reply with the smallest round trip, the one least distorted by an asymmetric network path. It never sets the
/// clock: the host's time service owns it and this container has no capability to change it.
/// </summary>
public sealed class ClockSyncMonitor : BackgroundService, IClockSyncMonitor
{
    public static readonly TimeSpan OnDemandInterval = TimeSpan.FromSeconds(30);

    private readonly Func<TimeSyncSettings> _settings;
    private readonly ISntpClient _client;
    private readonly IKernelClockReader _kernel;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ClockSyncMonitor> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task<ClockSyncSnapshot>? _round;
    private long? _lastOnDemandTimestamp;
    private volatile ClockSyncSnapshot? _latest;

    public ClockSyncMonitor(
        Func<TimeSyncSettings> settings,
        ISntpClient client,
        IKernelClockReader kernel,
        TimeProvider timeProvider,
        ILogger<ClockSyncMonitor> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public TimeSyncSettings Settings => _settings();

    public ClockSyncSnapshot? Latest => _latest;

    public async Task<ClockCheckResult> CheckNowAsync(CancellationToken cancellationToken)
    {
        Task<ClockSyncSnapshot> round;
        ClockCheckOutcome outcome;
        lock (_gate)
        {
            if (_round is { IsCompleted: false } running)
            {
                round = running;
                outcome = ClockCheckOutcome.Joined;
            }
            else
            {
                if (_lastOnDemandTimestamp is { } last && _timeProvider.GetElapsedTime(last) is var elapsed &&
                    elapsed < OnDemandInterval)
                {
                    return new ClockCheckResult(
                        ClockCheckOutcome.RateLimited, _latest, _timeProvider.GetUtcNow() + (OnDemandInterval - elapsed));
                }
                _lastOnDemandTimestamp = _timeProvider.GetTimestamp();
                round = StartRound();
                outcome = ClockCheckOutcome.Measured;
            }
        }
        return new ClockCheckResult(outcome, await round.WaitAsync(cancellationToken).ConfigureAwait(false), null);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _stopping.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Task<ClockSyncSnapshot> round;
                lock (_gate)
                {
                    round = _round is { IsCompleted: false } running ? running : StartRound();
                }
                await round.WaitAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_settings().Interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Called under _gate. The round runs on the pool so no query work happens while the lock is held.
    private Task<ClockSyncSnapshot> StartRound()
    {
        var token = _stopping.Token;
        return _round = Task.Run(() => RunRoundAsync(token), token);
    }

    private async Task<ClockSyncSnapshot> RunRoundAsync(CancellationToken cancellationToken)
    {
        var settings = _settings();
        var kernel = _kernel.Read();
        ClockSyncSnapshot snapshot;
        if (!settings.Enabled)
        {
            snapshot = new ClockSyncSnapshot(_timeProvider.GetUtcNow(), false, null, [], 0, kernel);
        }
        else
        {
            var configured = settings.EffectiveServers;
            var servers = configured
                .Where(static entry => TimeSyncSettings.TryParseServer(entry, out _, out _))
                .Take(TimeSyncSettings.MaximumServers)
                .ToArray();
            TimeServerResult[] results;
            try
            {
                results = await Task.WhenAll(servers.Select(server =>
                    _client.QueryAsync(server, settings.QueryTimeout, cancellationToken))).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                _logger.ClockCheckFailed(exception.GetType().Name);
                results = [];
            }
            var selected = results
                .Where(static result => result.Succeeded)
                .MinBy(static result => result.RoundTrip);
            snapshot = new ClockSyncSnapshot(
                _timeProvider.GetUtcNow(), true, selected, results, configured.Count - servers.Length, kernel);
            if (selected is { Offset: { } offset, RoundTrip: { } roundTrip, Stratum: { } stratum })
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.ClockMeasured(
                        Math.Round(offset.TotalMilliseconds, 1),
                        Math.Round(roundTrip.TotalMilliseconds, 1),
                        stratum,
                        results.Count(static result => result.Succeeded),
                        results.Length,
                        kernel.Status.ToString());
                }
            }
            else if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.ClockUnmeasured(results.Length, snapshot.IgnoredEntries, kernel.Status.ToString());
            }
        }
        _latest = snapshot;
        return snapshot;
    }
}
