using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>
/// Runs independently of acquisition, window closure and base publication. The committed initial revision provides
/// stable work identity and the published disposition acknowledges it. Restart rediscovers unacknowledged work.
/// </summary>
internal sealed partial class CenteredMeasuredDeepSkyScheduler(IRawCaptureIngress rawIngress,
    SqliteCenteredViews views, ICenteredMeasuredDeepSky assessment,
    TimeProvider clock, ILogger<CenteredMeasuredDeepSkyScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private const int MaximumScannedRevisions = 128;
    private const int MaximumBatch = 4;
    private const int MaximumDeferred = 16;
    private readonly Dictionary<long, (CenteredMeasuredDeepSkyWork Work, DateTimeOffset Due)> _deferred = [];
    private long? _lastObservedOrder;
    private long _recoveryThrough;
    private long _recoveryOrder;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var journalInitialized = false;
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                if (!journalInitialized)
                {
                    // Operations initialization validates the journal even when missing evidence keeps acquisition closed.
                    _ = await rawIngress.InitializeForOperationsAsync(stoppingToken).ConfigureAwait(false);
                    journalInitialized = true;
                }
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                ReadFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async ValueTask RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = _deferred.Values.Where(retry => retry.Due <= now).Take(MaximumBatch).ToArray();
        foreach (var retry in due)
        {
            var item = retry.Work;
            if (await TryRunAsync(item, cancellationToken).ConfigureAwait(false))
            {
                _deferred.Remove(item.EvidenceOrder);
            }
            else
            {
                Defer(item);
            }
        }

        var available = Math.Min(MaximumBatch - due.Length, MaximumDeferred - _deferred.Count);
        if (available == 0)
        {
            return;
        }
        if (_lastObservedOrder is null)
        {
            _recoveryThrough = await views.ReadMeasuredStackRecoveryOrderAsync(MaximumScannedRevisions, cancellationToken)
                .ConfigureAwait(false);
            _lastObservedOrder = _recoveryThrough;
        }

        // Give both ranges a share of the budget so old work cannot delay the live head and
        // a continuously busy live head cannot prevent recovery of an older admitted stack.
        var recovering = _recoveryOrder < _recoveryThrough;
        var recentBudget = recovering && available > 1 ? available - 1 : available;
        var used = await DrainPageAsync(recentBudget, recovery: false, cancellationToken).ConfigureAwait(false);
        if (recovering && used < available)
        {
            await DrainPageAsync(available - used, recovery: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<int> DrainPageAsync(int budget, bool recovery, CancellationToken cancellationToken)
    {
        var after = recovery ? _recoveryOrder : _lastObservedOrder.GetValueOrDefault();
        var through = recovery ? _recoveryThrough : long.MaxValue;
        var (scannedThrough, work) = await views.ReadMeasuredStackWorkAsync(after, through, MaximumScannedRevisions,
            cancellationToken).ConfigureAwait(false);
        var count = Math.Min(work.Count, budget);
        for (var index = 0; index < count; index++)
        {
            var item = work[index];
            if (!await TryRunAsync(item, cancellationToken).ConfigureAwait(false))
            {
                Defer(item);
            }
            Advance(item.EvidenceOrder);
        }
        if (work.Count <= budget)
        {
            Advance(recovery && scannedThrough == after ? through : scannedThrough);
        }
        return count;

        void Advance(long order)
        {
            if (recovery)
                _recoveryOrder = order;
            else
                _lastObservedOrder = order;
        }
    }

    private void Defer(CenteredMeasuredDeepSkyWork work) =>
        _deferred[work.EvidenceOrder] = (work, clock.GetUtcNow() + RetryDelay);

    private async ValueTask<bool> TryRunAsync(CenteredMeasuredDeepSkyWork work, CancellationToken cancellationToken)
    {
        try
        {
            await assessment.ProcessAsync(work, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (CenteredMeasuredDeepSky.IsInvalidEvidence(exception))
        {
            AssessmentInvalid(logger, work.BaseId, exception);
            return true;
        }
        catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
        {
            AssessmentFailed(logger, work.BaseId, exception);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the optional centered deep-sky work.")]
    private static partial void ReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not verify or settle deep-sky work for centered base {BaseId}.")]
    private static partial void AssessmentInvalid(ILogger logger, string baseId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Centered base {BaseId} deep-sky assessment remains pending.")]
    private static partial void AssessmentFailed(ILogger logger, string baseId, Exception exception);
}
