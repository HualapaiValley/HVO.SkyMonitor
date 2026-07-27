using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

public sealed class CaptureLaneTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.CaptureLanes";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.CaptureLanes";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly CaptureLaneState _state;
    private readonly Counter<long> _created;
    private readonly Counter<long> _claims;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _quarantined;
    private readonly Counter<long> _abandoned;
    private readonly Counter<long> _wakeups;
    private readonly Histogram<double> _claimDuration;
    private readonly Histogram<double> _processingDuration;
    private readonly Histogram<double> _ackDuration;
    private readonly Histogram<double> _lockWaitDuration;
    private long _lockWaitTicks;
    private long _lockWaitSamples;

    public CaptureLaneTelemetry(CaptureLaneState state)
    {
        _state = state;
        _created = _meter.CreateCounter<long>("camera_agent.lanes.work.created", "{work}");
        _claims = _meter.CreateCounter<long>("camera_agent.lanes.claims", "{claim}");
        _completed = _meter.CreateCounter<long>("camera_agent.lanes.completed", "{work}");
        _retries = _meter.CreateCounter<long>("camera_agent.lanes.retries", "{retry}");
        _quarantined = _meter.CreateCounter<long>("camera_agent.lanes.quarantined", "{work}");
        _abandoned = _meter.CreateCounter<long>("camera_agent.lanes.abandoned", "{work}");
        _wakeups = _meter.CreateCounter<long>("camera_agent.lanes.wakeups", "{notification}");
        _claimDuration = _meter.CreateHistogram<double>("camera_agent.lanes.claim.duration", "s");
        _processingDuration = _meter.CreateHistogram<double>("camera_agent.lanes.processing.duration", "s");
        _ackDuration = _meter.CreateHistogram<double>("camera_agent.lanes.ack.duration", "s");
        _lockWaitDuration = _meter.CreateHistogram<double>("camera_agent.lanes.sqlite.lock_wait.duration", "s");

        _meter.CreateObservableGauge("camera_agent.lanes.accepting", ObserveAccepting);
        _meter.CreateObservableGauge("camera_agent.lanes.pending", ObservePending, "{work}");
        _meter.CreateObservableGauge("camera_agent.lanes.pending.bytes", ObservePendingBytes, "By");
        _meter.CreateObservableGauge("camera_agent.lanes.oldest.age", ObserveOldestAge, "s");
        _meter.CreateObservableGauge("camera_agent.lanes.leased", ObserveLeased, "{lease}");
        _meter.CreateObservableGauge("camera_agent.lanes.pressure", ObservePressure);
    }

    internal void RecordCreated(CaptureLaneDefinition lane)
        => _created.Add(1, Tags(lane.Name, lane.Required));

    internal void RecordWork(string lane, bool required, string outcome)
    {
        _created.Add(1, Tags(lane, required, new("outcome", outcome)));
        if (outcome == "abandoned")
        {
            _abandoned.Add(1, Tags(lane, required, new("reason", "optional-pressure")));
        }
    }

    internal void RecordLockWait(TimeSpan duration)
    {
        Interlocked.Add(ref _lockWaitTicks, duration.Ticks);
        Interlocked.Increment(ref _lockWaitSamples);
        _lockWaitDuration.Record(duration.TotalSeconds);
    }

    internal TimeSpan TotalLockWait => TimeSpan.FromTicks(Interlocked.Read(ref _lockWaitTicks));

    internal long LockWaitSamples => Interlocked.Read(ref _lockWaitSamples);

    internal void RecordClaim(CaptureLaneDefinition lane, bool claimed, TimeSpan duration)
    {
        var tags = Tags(lane.Name, lane.Required, new("result", claimed ? "claimed" : "empty"));
        _claimDuration.Record(duration.TotalSeconds, tags);
        _claims.Add(1, tags);
    }

    internal void RecordProcessing(string lane, bool required, CaptureLaneHandlerOutcome outcome, TimeSpan duration)
        => _processingDuration.Record(
            duration.TotalSeconds,
            Tags(lane, required, new("outcome", Outcome(outcome))));

    internal void RecordAcknowledgement(
        CaptureLaneLease lease,
        CaptureLaneHandlerOutcome outcome,
        TimeSpan duration)
    {
        var tags = Tags(lease.Lane, lease.Required, new("outcome", Outcome(outcome)));
        _ackDuration.Record(duration.TotalSeconds, tags);
        if (outcome == CaptureLaneHandlerOutcome.Completed)
        {
            _completed.Add(1, tags);
        }
        else if (outcome == CaptureLaneHandlerOutcome.RetryableFailure)
        {
            _retries.Add(1, tags);
        }
        else if (outcome == CaptureLaneHandlerOutcome.TerminalFailure)
        {
            _quarantined.Add(1, tags);
        }
    }

    internal void RecordWakeup(string lane, bool required, bool queued)
        => _wakeups.Add(1, Tags(lane, required, new("result", queued ? "queued" : "coalesced")));

    internal void RecordAbandoned(string lane, bool required, string reason)
        => _abandoned.Add(1, Tags(lane, required, new("reason", reason)));

    private Measurement<long> ObserveAccepting()
        => new(_state.Snapshot.Availability != CaptureLaneAvailability.Unhealthy ? 1 : 0);

    private IEnumerable<Measurement<long>> ObservePending()
        => _state.Snapshot.Lanes.Select(static lane =>
            new Measurement<long>(lane.PendingCount, Tags(lane.Lane, lane.Required)));

    private IEnumerable<Measurement<long>> ObservePendingBytes()
        => _state.Snapshot.Lanes.Select(static lane =>
            new Measurement<long>(lane.PendingBytes, Tags(lane.Lane, lane.Required)));

    private IEnumerable<Measurement<double>> ObserveOldestAge()
    {
        var now = _state.GetUtcNow();
        return _state.Snapshot.Lanes.Select(lane => new Measurement<double>(
            lane.OldestPendingUtc is null ? 0 : Math.Max(0, (now - lane.OldestPendingUtc.Value).TotalSeconds),
            Tags(lane.Lane, lane.Required)));
    }

    private IEnumerable<Measurement<long>> ObserveLeased()
        => _state.Snapshot.Lanes.Select(static lane =>
            new Measurement<long>(lane.LeasedCount, Tags(lane.Lane, lane.Required)));

    private IEnumerable<Measurement<long>> ObservePressure()
        => _state.Snapshot.Lanes.Select(static lane =>
            new Measurement<long>(lane.PressureLevel, Tags(lane.Lane, lane.Required)));

    private static TagList Tags(
        string lane,
        bool required,
        KeyValuePair<string, object?> additional = default)
    {
        var tags = new TagList
        {
            { "lane", lane },
            { "required", required }
        };
        if (additional.Key is not null)
        {
            tags.Add(additional.Key, additional.Value);
        }
        return tags;
    }

    private static string Outcome(CaptureLaneHandlerOutcome outcome) => outcome switch
    {
        CaptureLaneHandlerOutcome.Completed => "completed",
        CaptureLaneHandlerOutcome.Deferred => "waiting",
        CaptureLaneHandlerOutcome.RetryableFailure => "retry",
        _ => "terminal"
    };

    public void Dispose() => _meter.Dispose();
}
