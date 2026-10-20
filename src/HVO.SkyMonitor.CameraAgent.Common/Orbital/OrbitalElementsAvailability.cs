using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>Whether the CameraAgent-local orbital store can serve refresh, operations and reads.</summary>
public enum OrbitalElementsAvailabilityState
{
    /// <summary>No initialization has been attempted yet.</summary>
    NotInitialized,

    /// <summary>The store owns its root and its schema verified.</summary>
    Available,

    /// <summary>The root, lock, database or filesystem could not be used. Retried without a restart.</summary>
    StorageUnavailable,

    /// <summary>The schema or durable state failed verification. Refresh fails closed until it is repaired.</summary>
    IntegrityFailure
}

/// <summary>
/// Fail-closed availability of the optional orbital store. Initialization failure never stops the CameraAgent host:
/// it is recorded once, the refresh task, operations and health report it, and every later probe retries, so a repaired
/// root recovers without a restart. Logs carry the state and a stable reason code, never a path.
/// </summary>
public sealed partial class OrbitalElementsAvailability
{
    /// <summary>The reason code of an unavailable store when no more specific storage code is known.</summary>
    public const string StoreUnavailableReasonCode = "orbital.store-unavailable";

    /// <summary>The reason code of a schema or durable-state verification failure.</summary>
    public const string IntegrityFailureReasonCode = "orbital.storage-integrity";

    private readonly SqliteOrbitalElementsStore _store;
    private readonly ILogger<OrbitalElementsAvailability> _logger;
    private readonly object _sync = new();
    private OrbitalElementsAvailabilityState _failure = OrbitalElementsAvailabilityState.NotInitialized;
    private string _failureReasonCode = StoreUnavailableReasonCode;
    private bool _failureLogged;

    public OrbitalElementsAvailability(SqliteOrbitalElementsStore store, ILogger<OrbitalElementsAvailability> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <summary>The current availability; a volatile read with no I/O.</summary>
    public OrbitalElementsAvailabilityState State
    {
        get
        {
            if (_store.IsInitialized)
            {
                return OrbitalElementsAvailabilityState.Available;
            }
            lock (_sync)
            {
                return _failure;
            }
        }
    }

    /// <summary>True once the store owns its root and verified its schema; a volatile read with no I/O.</summary>
    public bool IsAvailable => _store.IsInitialized;

    /// <summary>The stable reason code of the last failure, or null while available.</summary>
    public string? ReasonCode
    {
        get
        {
            if (_store.IsInitialized)
            {
                return null;
            }
            lock (_sync)
            {
                return _failureReasonCode;
            }
        }
    }

    /// <summary>
    /// Initializes the store when it is not yet available. Caller cancellation is rethrown; every other failure is
    /// classified and returned, never thrown.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "An optional feature's storage failure is classified and reported; it must never stop the host.")]
    public async ValueTask<OrbitalElementsAvailabilityState> EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        if (!_store.IsInitialized)
        {
            try
            {
                await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return RecordFailure(exception);
            }
        }
        MarkAvailable();
        return OrbitalElementsAvailabilityState.Available;
    }

    /// <summary>
    /// The deterministic phase offset, read only from memory once the store is initialized. False when it is not, so
    /// the automation epoch hook never performs I/O and never throws.
    /// </summary>
    internal bool TryGetPhaseOffset(out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (!_store.IsInitialized)
        {
            return false;
        }
        try
        {
            offset = _store.PhaseOffset;
            return true;
        }
        catch (InvalidOperationException)
        {
            // Disposed between the check and the read during host shutdown.
            return false;
        }
    }

    internal static (OrbitalElementsAvailabilityState State, string ReasonCode) Classify(Exception exception)
        => exception switch
        {
            OrbitalStorageException storage => (OrbitalElementsAvailabilityState.StorageUnavailable, storage.ReasonCode),
            InvalidDataException => (OrbitalElementsAvailabilityState.IntegrityFailure, IntegrityFailureReasonCode),
            IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
                => (OrbitalElementsAvailabilityState.StorageUnavailable, SqliteOrbitalElementsStore.ClassifyFault(exception)),
            _ => (OrbitalElementsAvailabilityState.StorageUnavailable, StoreUnavailableReasonCode)
        };

    private OrbitalElementsAvailabilityState RecordFailure(Exception exception)
    {
        var (state, reasonCode) = Classify(exception);
        bool log;
        lock (_sync)
        {
            log = !_failureLogged || _failure != state ||
                !string.Equals(_failureReasonCode, reasonCode, StringComparison.Ordinal);
            _failure = state;
            _failureReasonCode = reasonCode;
            _failureLogged = true;
        }
        if (log)
        {
            StoreUnavailable(_logger, state.ToString(), reasonCode, exception);
        }
        return state;
    }

    private void MarkAvailable()
    {
        bool recovered;
        lock (_sync)
        {
            recovered = _failureLogged;
            _failure = OrbitalElementsAvailabilityState.NotInitialized;
            _failureReasonCode = StoreUnavailableReasonCode;
            _failureLogged = false;
        }
        if (recovered)
        {
            StoreRecovered(_logger);
        }
    }

    [LoggerMessage(7730, LogLevel.Warning,
        "Orbital elements storage is unavailable ({State}, {ReasonCode}); refresh fails closed and the host keeps running.")]
    private static partial void StoreUnavailable(ILogger logger, string state, string reasonCode, Exception exception);

    [LoggerMessage(7731, LogLevel.Information, "Orbital elements storage is available again.")]
    private static partial void StoreRecovered(ILogger logger);
}

/// <summary>
/// Initializes the orbital store before the automation runner starts, whether or not refresh is enabled, so the
/// reserved definition's phase offset is in memory and restart reconciliation has run. Failure never stops the host;
/// while the store is unavailable this service retries on a fixed bounded cadence until it recovers.
/// </summary>
internal sealed class OrbitalElementsInitializationService(
    OrbitalElementsAvailability availability,
    TimeProvider timeProvider) : BackgroundService
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await availability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (availability.IsAvailable)
        {
            return;
        }
        using var timer = new PeriodicTimer(RetryInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (await availability.EnsureAvailableAsync(stoppingToken).ConfigureAwait(false) ==
                    OrbitalElementsAvailabilityState.Available)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}
