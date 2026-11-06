using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

/// <summary>
/// Serializes host commands: entire job runs, resume prechecks and writes, and acknowledgements.
/// Always acquire this gate before the runner gate; never acquire it while holding the runner gate.
/// Command publication acquires the job's dedicated publication lock after this gate; never the reverse.
/// Publication never acquires this command gate. Timed reads remain ungated. SqliteSwpcStore.Retention.cs:15–34 purges expired groups, their
/// messages/ranks/acknowledgements, orphan markers, completed attempts, receipts and non-LKG raw
/// history. It never changes a halt, checkpoint or retained group's acknowledgement. A purge
/// cannot invalidate a halt precheck; it can bump revision or remove an expired group, yielding
/// revision-conflict or not-found without a command receipt. No internal command retry is used.
/// </summary>
internal sealed class SpaceWeatherWriteGate : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool startupObserved;

    internal Task EnterAsync(CancellationToken ct) => gate.WaitAsync(ct);
    internal void Exit() => gate.Release();

    /// <summary>Five seconds on the injected clock; request cancellation always propagates.</summary>
    internal async ValueTask<bool> TryEnterAsync(TimeProvider clock, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { await gate.WaitAsync(linked.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested) { return false; }
    }

    /// <summary>Called only under this gate, before the first runner call by either run or resume.</summary>
    internal async ValueTask ObserveStartupAsync(ISwpcStore store, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        if (startupObserved) return;
        var health = await store.ReadHealthAsync(clock.GetUtcNow().ToUniversalTime(), ct).ConfigureAwait(false);
        if (health.IncompleteAttempts > 0) SpaceWeatherLog.Incomplete(logger, health.IncompleteAttempts);
        startupObserved = true;
    }

    public void Dispose() => gate.Dispose();
}
