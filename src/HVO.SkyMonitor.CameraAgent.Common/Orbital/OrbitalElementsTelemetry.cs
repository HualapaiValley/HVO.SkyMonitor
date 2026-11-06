using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// Orbital-element acquisition metrics. Labels are bounded enum names only; no attempt identity, run key, digest,
/// path, actor or provider text ever becomes a label. The gauges read a cached provider-state snapshot, so an
/// observation callback never performs I/O.
/// </summary>
/// <remarks>
/// Only the refresh job publishes that snapshot, from a durable read serialized with its single-flight lock: at the end
/// of every occurrence (skipped ones included), once the store initializes (after restart reconciliation, so a halt
/// that survived a restart reads 1 before any attempt), and after an applied operator resume. Status reads and health
/// evaluation never publish. Nothing is emitted until the first publication, and a store that later becomes unusable
/// leaves the last published values in place.
/// </remarks>
public sealed class OrbitalElementsTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.OrbitalElements";

    private readonly Meter _meter;
    private readonly TimeProvider _timeProvider;
    private readonly Counter<long> _attempts;
    private readonly Histogram<double> _fetchDuration;
    private readonly Histogram<long> _rawBytes;
    private volatile ObservedState? _observed;

    public OrbitalElementsTelemetry(TimeProvider timeProvider)
        : this(timeProvider, new Meter(MeterName))
    {
    }

    /// <summary>Uses a caller-supplied meter, which this instance then owns and disposes.</summary>
    internal OrbitalElementsTelemetry(TimeProvider timeProvider, Meter meter)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(meter);
        _timeProvider = timeProvider;
        _meter = meter;
        _attempts = _meter.CreateCounter<long>(
            "skymonitor.cameraagent.orbital_elements.attempts", "{attempt}",
            "Completed orbital-element provider attempts by durable outcome.");
        _fetchDuration = _meter.CreateHistogram<double>(
            "skymonitor.cameraagent.orbital_elements.fetch_duration", "ms",
            "Duration of one bounded provider request, by fetch disposition.");
        _rawBytes = _meter.CreateHistogram<long>(
            "skymonitor.cameraagent.orbital_elements.raw_bytes", "By",
            "Decoded raw bytes of a retrieved provider response.");
        _meter.CreateObservableGauge(
            "skymonitor.cameraagent.orbital_elements.active_records", ObserveActiveRecords, "{record}",
            "Records in the active orbital snapshot.");
        _meter.CreateObservableGauge(
            "skymonitor.cameraagent.orbital_elements.active_objects", ObserveActiveObjects, "{object}",
            "Distinct objects in the active orbital snapshot.");
        _meter.CreateObservableGauge(
            "skymonitor.cameraagent.orbital_elements.newest_element_age", ObserveNewestElementAge, "s",
            "Seconds since the newest element epoch in the active snapshot; element freshness follows this.");
        _meter.CreateObservableGauge(
            "skymonitor.cameraagent.orbital_elements.oldest_element_age", ObserveOldestElementAge, "s",
            "Seconds since the oldest element epoch in the active snapshot.");
        _meter.CreateObservableGauge(
            "skymonitor.cameraagent.orbital_elements.activation_age", ObserveActivationAge, "s",
            "Seconds since the active payload last changed; an unchanged re-download does not reset it.");
        _meter.CreateObservableGauge(
            "skymonitor.cameraagent.orbital_elements.halted", ObserveHalted, "{halt}",
            "1 while a provider-wide halt is in force, otherwise 0.");
    }

    /// <summary>Counts one attempt after its terminal outcome is durable.</summary>
    public void RecordAttempt(OrbitalAttemptOutcome outcome)
        => _attempts.Add(1, new TagList { { "outcome", outcome.ToString() } });

    /// <summary>Records the bounded request duration and, for a retrieval, its decoded size.</summary>
    public void RecordFetch(OrbitalFetchDisposition disposition, TimeSpan duration, long? rawBytes)
    {
        var tags = new TagList { { "disposition", disposition.ToString() } };
        _fetchDuration.Record(Math.Max(0, duration.TotalMilliseconds), tags);
        if (rawBytes is { } bytes and >= 0)
        {
            _rawBytes.Record(bytes);
        }
    }

    /// <summary>Publishes a durable provider state for the gauges. Nothing is emitted before the first call.</summary>
    public void Observe(OrbitalProviderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _observed = new ObservedState(state.CurrentRecordCount, state.CurrentObjectCount, state.CurrentMaximumEpochUtc,
            state.CurrentMinimumEpochUtc, state.LastActivationUtc, state.Halt is not null);
    }

    public void Dispose()
    {
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }

    private IEnumerable<Measurement<long>> ObserveActiveRecords()
        => _observed is { ActiveRecords: { } records } ? [new Measurement<long>(records)] : [];

    private IEnumerable<Measurement<long>> ObserveActiveObjects()
        => _observed is { ActiveObjects: { } objects } ? [new Measurement<long>(objects)] : [];

    private IEnumerable<Measurement<double>> ObserveNewestElementAge() => Age(_observed?.NewestEpochUtc);

    private IEnumerable<Measurement<double>> ObserveOldestElementAge() => Age(_observed?.OldestEpochUtc);

    private IEnumerable<Measurement<double>> ObserveActivationAge() => Age(_observed?.LastActivationUtc);

    private IEnumerable<Measurement<double>> Age(DateTimeOffset? since)
        => since is { } value ? [new Measurement<double>(Math.Max(0, (_timeProvider.GetUtcNow() - value).TotalSeconds))] : [];

    private IEnumerable<Measurement<int>> ObserveHalted()
        => _observed is { } observed ? [new Measurement<int>(observed.Halted ? 1 : 0)] : [];

    private sealed record ObservedState(int? ActiveRecords, int? ActiveObjects, DateTimeOffset? NewestEpochUtc,
        DateTimeOffset? OldestEpochUtc, DateTimeOffset? LastActivationUtc, bool Halted);
}
