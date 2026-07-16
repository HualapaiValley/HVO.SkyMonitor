using HVO.SkyMonitor.Processing;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralDerivativeWorkerTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.DerivativeWorker";
    public const string ActivitySourceName = "HVO.SkyMonitor.LogicHost.DerivativeWorker";
    private readonly Meter _meter = new(MeterName);
    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly Counter<long> _claims;
    private readonly Counter<long> _renewals;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _recoveries;
    private readonly Counter<long> _operations;
    private readonly Counter<long> _dependencyFailures;
    private readonly Counter<long> _bytes;
    private readonly Histogram<double> _duration;
    private readonly ConcurrentDictionary<(string Status, string Recipe), long> _queue = new();
    private long _active;
    private long _lastPollUtcTicks;
    private long _lastSuccessUtcTicks;
    private long _lastRenewalFailureUtcTicks;
    private long _lastDependencyFailureUtcTicks;
    private long _oldestQueueAgeSeconds;

    public CentralDerivativeWorkerTelemetry()
    {
        _claims = _meter.CreateCounter<long>("skymonitor.central.derivative.claims", "{claim}");
        _renewals = _meter.CreateCounter<long>("skymonitor.central.derivative.lease.renewals", "{renewal}");
        _attempts = _meter.CreateCounter<long>("skymonitor.central.derivative.attempts", "{attempt}");
        _recoveries = _meter.CreateCounter<long>("skymonitor.central.derivative.recoveries", "{recovery}");
        _operations = _meter.CreateCounter<long>("skymonitor.central.derivative.operations", "{operation}");
        _dependencyFailures = _meter.CreateCounter<long>(
            "skymonitor.central.derivative.dependency.failures", "{failure}");
        _bytes = _meter.CreateCounter<long>("skymonitor.central.derivative.bytes", "By");
        _duration = _meter.CreateHistogram<double>("skymonitor.central.derivative.duration", "ms");
        _meter.CreateObservableGauge(
            "skymonitor.central.derivative.active",
            () => Interlocked.Read(ref _active),
            "{worker}");
        _meter.CreateObservableGauge(
            "skymonitor.central.derivative.queue",
            ObserveQueue,
            "{job}");
        _meter.CreateObservableGauge(
            "skymonitor.central.derivative.queue.oldest_age",
            () => Interlocked.Read(ref _oldestQueueAgeSeconds),
            "s");
    }

    public DateTimeOffset? LastPollUtc => ReadUtc(ref _lastPollUtcTicks);

    public DateTimeOffset? LastSuccessUtc => ReadUtc(ref _lastSuccessUtcTicks);

    public long ActiveCount => Interlocked.Read(ref _active);

    public Activity? StartExecution(
        string recipe,
        string? traceParent = null,
        string? traceState = null,
        Guid? jobId = null,
        int? attempt = null)
    {
        var activity = ActivityContext.TryParse(traceParent, traceState, out var parent)
            ? _activitySource.StartActivity("central-derivative.execute", ActivityKind.Consumer, parent)
            : _activitySource.StartActivity("central-derivative.execute", ActivityKind.Consumer);
        activity?.SetTag("recipe", NormalizeRecipe(recipe));
        activity?.SetTag("job.id", jobId);
        activity?.SetTag("attempt", attempt);
        return activity;
    }

    public Activity? StartStage(string stage, string recipe)
    {
        var activity = _activitySource.StartActivity($"central-derivative.{stage}");
        activity?.SetTag("stage", stage);
        activity?.SetTag("recipe", NormalizeRecipe(recipe));
        return activity;
    }

    public void RecordPoll(DateTimeOffset now)
        => Interlocked.Exchange(ref _lastPollUtcTicks, now.UtcDateTime.Ticks);

    public void UpdateQueueSnapshot(
        IReadOnlyCollection<CentralDerivativeQueueMeasurement> measurements,
        long oldestAgeSeconds)
    {
        _queue.Clear();
        foreach (var measurement in measurements)
        {
            _queue[(NormalizeStatus(measurement.Status), NormalizeRecipe(measurement.Recipe))]
                = Math.Max(0, measurement.Count);
        }
        Interlocked.Exchange(ref _oldestQueueAgeSeconds, Math.Max(0, oldestAgeSeconds));
    }

    public void RecordClaim(string outcome, TimeSpan duration)
    {
        _claims.Add(1, new TagList { { "outcome", outcome } });
        RecordDuration("claim", "other", outcome, duration);
    }

    public void RecordRenewal(string outcome, DateTimeOffset now)
    {
        _renewals.Add(1, new TagList { { "outcome", outcome } });
        if (string.Equals(outcome, "failed", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _lastRenewalFailureUtcTicks, now.UtcDateTime.Ticks);
        }
    }

    public bool HasRecentRenewalFailure(DateTimeOffset now, TimeSpan window)
    {
        var failure = ReadUtc(ref _lastRenewalFailureUtcTicks);
        return failure.HasValue && now - failure.Value <= window;
    }

    public void RecordDependencyFailure(string dependency, DateTimeOffset now)
    {
        _dependencyFailures.Add(1, new TagList { { "dependency", dependency } });
        Interlocked.Exchange(ref _lastDependencyFailureUtcTicks, now.UtcDateTime.Ticks);
    }

    public bool HasRecentDependencyFailure(DateTimeOffset now, TimeSpan window)
    {
        var failure = ReadUtc(ref _lastDependencyFailureUtcTicks);
        return failure.HasValue && now - failure.Value <= window;
    }

    public void RecordStage(string stage, string recipe, string outcome, TimeSpan duration, long bytes = 0)
    {
        var normalizedRecipe = NormalizeRecipe(recipe);
        RecordDuration(stage, normalizedRecipe, outcome, duration);
        if (bytes > 0)
        {
            _bytes.Add(bytes, new TagList
            {
                { "direction", string.Equals(stage, "load", StringComparison.Ordinal) ? "input" : "output" },
                { "recipe", normalizedRecipe }
            });
        }
    }

    public void RecordAttempt(string recipe, string outcome, string cause, DateTimeOffset now)
    {
        _attempts.Add(1, new TagList
        {
            { "recipe", NormalizeRecipe(recipe) },
            { "outcome", outcome },
            { "cause", cause }
        });
        if (outcome is "produced" or "skipped")
        {
            Interlocked.Exchange(ref _lastSuccessUtcTicks, now.UtcDateTime.Ticks);
        }
    }

    public void RecordRecovery(string outcome)
        => _recoveries.Add(1, new TagList { { "outcome", outcome } });

    public void RecordOperation(string operation, string outcome)
        => _operations.Add(1, new TagList { { "operation", operation }, { "outcome", outcome } });

    public IDisposable TrackActive()
    {
        Interlocked.Increment(ref _active);
        return new ActiveLease(() => Interlocked.Decrement(ref _active));
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }

    private void RecordDuration(string stage, string recipe, string outcome, TimeSpan duration)
        => _duration.Record(duration.TotalMilliseconds, new TagList
        {
            { "stage", stage },
            { "recipe", recipe },
            { "outcome", outcome }
        });

    private static string NormalizeRecipe(string recipe) => recipe switch
    {
        BuiltInProcessingRecipes.EncodedPreview => BuiltInProcessingRecipes.EncodedPreview,
        BuiltInProcessingRecipes.Annotation => BuiltInProcessingRecipes.Annotation,
        BuiltInProcessingRecipes.ImageQuality => BuiltInProcessingRecipes.ImageQuality,
        _ => "other"
    };

    private IEnumerable<Measurement<long>> ObserveQueue()
        => _queue.Select(pair => new Measurement<long>(pair.Value, new TagList
        {
            { "status", pair.Key.Status },
            { "recipe", pair.Key.Recipe }
        }));

    private static string NormalizeStatus(string status) => status switch
    {
        "pending" => "pending",
        "leased" => "leased",
        "retryable" => "retryable",
        _ => "other"
    };

    private static DateTimeOffset? ReadUtc(ref long value)
    {
        var ticks = Interlocked.Read(ref value);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private sealed class ActiveLease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal sealed record CentralDerivativeQueueMeasurement(string Status, string Recipe, long Count);
