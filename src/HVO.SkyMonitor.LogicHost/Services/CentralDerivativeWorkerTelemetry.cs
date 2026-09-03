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
    private readonly Counter<long> _windowResolutions;
    private readonly Counter<long> _windowNotifications;
    private readonly Counter<long> _windowRejections;
    private readonly Counter<long> _windowDeadlines;
    private readonly Counter<long> _transientOutcomes;
    private readonly Counter<long> _transientClassifications;
    private readonly Histogram<long> _transientCandidates;
    private readonly Histogram<double> _duration;
    private readonly Histogram<long> _windowSelectedInputs;
    private readonly Histogram<long> _windowExpectedInputs;
    private readonly Histogram<long> _windowMissingInputs;
    private readonly Histogram<double> _windowCompleteness;
    private readonly Histogram<double> _windowProcessingLag;
    private readonly Histogram<double> _windowPinDuration;
    private readonly Histogram<long> _windowSelectedBytes;
    private readonly Counter<long> _graphExpansions;
    private readonly Counter<long> _graphConvergences;
    private readonly Counter<long> _graphRecoveryPolls;
    private readonly ConcurrentDictionary<(string Status, string Recipe), long> _queue = new();
    private readonly ConcurrentDictionary<(string Class, string Status), long> _graphQueue = new();
    private long _active;
    private long _lastPollUtcTicks;
    private long _lastSuccessUtcTicks;
    private long _lastRenewalFailureUtcTicks;
    private long _lastDependencyFailureUtcTicks;
    private long _oldestQueueAgeSeconds;
    private long _windowWaiting;
    private long _windowOldestWaitAgeSeconds;
    private long _windowActivePins;
    private long _windowOldestPinAgeSeconds;
    private long _windowPinnedBytes;
    private long _lastGraphRecoveryUtcTicks;
    private long _oldestGraphConvergenceAgeSeconds;

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
        _windowResolutions = _meter.CreateCounter<long>(
            "skymonitor.central.derivative.window.resolutions", "{resolution}");
        _windowNotifications = _meter.CreateCounter<long>(
            "skymonitor.central.derivative.window.notifications", "{notification}");
        _windowRejections = _meter.CreateCounter<long>(
            "skymonitor.central.derivative.window.compatibility_rejections", "{rejection}");
        _windowDeadlines = _meter.CreateCounter<long>(
            "skymonitor.central.derivative.window.deadlines", "{deadline}");
        _transientOutcomes = _meter.CreateCounter<long>(
            "skymonitor.central.transient.outcomes", "{outcome}");
        _transientClassifications = _meter.CreateCounter<long>(
            "skymonitor.central.transient.classifications", "{classification}");
        _transientCandidates = _meter.CreateHistogram<long>(
            "skymonitor.central.transient.candidates", "{candidate}");
        _duration = _meter.CreateHistogram<double>("skymonitor.central.derivative.duration", "ms");
        _windowSelectedInputs = _meter.CreateHistogram<long>(
            "skymonitor.central.derivative.window.selected_inputs", "{artifact}");
        _windowExpectedInputs = _meter.CreateHistogram<long>(
            "skymonitor.central.derivative.window.expected_inputs", "{requirement}");
        _windowMissingInputs = _meter.CreateHistogram<long>(
            "skymonitor.central.derivative.window.missing_inputs", "{requirement}");
        _windowCompleteness = _meter.CreateHistogram<double>(
            "skymonitor.central.derivative.window.completeness", "1");
        _windowProcessingLag = _meter.CreateHistogram<double>(
            "skymonitor.central.derivative.window.processing_lag", "ms");
        _windowPinDuration = _meter.CreateHistogram<double>(
            "skymonitor.central.derivative.window.pin_duration", "ms");
        _windowSelectedBytes = _meter.CreateHistogram<long>(
            "skymonitor.central.derivative.window.selected_bytes", "By");
        _graphExpansions = _meter.CreateCounter<long>(
            "skymonitor.central.processing_graph.expansions", "{execution}");
        _graphConvergences = _meter.CreateCounter<long>(
            "skymonitor.central.processing_graph.convergences", "{convergence}");
        _graphRecoveryPolls = _meter.CreateCounter<long>(
            "skymonitor.central.processing_graph.recovery", "{poll}");
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
        _meter.CreateObservableGauge(
            "skymonitor.central.derivative.window.waiting",
            () => Interlocked.Read(ref _windowWaiting),
            "{job}");
        _meter.CreateObservableGauge(
            "skymonitor.central.derivative.window.waiting.oldest_age",
            () => Interlocked.Read(ref _windowOldestWaitAgeSeconds),
            "s");
        _meter.CreateObservableGauge(
            "skymonitor.central.derivative.window.pins.active",
            () => Interlocked.Read(ref _windowActivePins),
            "{artifact}");
        _meter.CreateObservableGauge(
            "skymonitor.central.derivative.window.pins.bytes",
            () => Interlocked.Read(ref _windowPinnedBytes),
            "By");
        _meter.CreateObservableGauge(
            "skymonitor.central.derivative.window.pins.oldest_age",
            () => Interlocked.Read(ref _windowOldestPinAgeSeconds),
            "s");
        _meter.CreateObservableGauge(
            "skymonitor.central.processing_graph.queue",
            ObserveGraphQueue,
            "{execution}");
        _meter.CreateObservableGauge(
            "skymonitor.central.processing_graph.convergence.oldest_age",
            () => Interlocked.Read(ref _oldestGraphConvergenceAgeSeconds),
            "s");
    }

    public DateTimeOffset? LastPollUtc => ReadUtc(ref _lastPollUtcTicks);

    public DateTimeOffset? LastSuccessUtc => ReadUtc(ref _lastSuccessUtcTicks);

    public long ActiveCount => Interlocked.Read(ref _active);

    public DateTimeOffset? LastGraphRecoveryUtc => ReadUtc(ref _lastGraphRecoveryUtcTicks);

    public void RecordGraphExpansion(string executionClass, string outcome, TimeSpan duration, int nodeCount)
    {
        var tags = new TagList
        {
            { "class", NormalizeGraphClass(executionClass) },
            { "outcome", NormalizeGraphOutcome(outcome) }
        };
        _graphExpansions.Add(1, tags);
        _duration.Record(Math.Max(0, duration.TotalMilliseconds), new TagList
        {
            { "stage", "graph-expansion" },
            { "recipe", "other" },
            { "outcome", NormalizeGraphOutcome(outcome) }
        });
        if (nodeCount > 0)
        {
            _operations.Add(nodeCount, new TagList
            {
                { "operation", "graph-node-expanded" },
                { "outcome", NormalizeGraphOutcome(outcome) }
            });
        }
    }

    public void RecordGraphConvergence(string executionClass, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "class", NormalizeGraphClass(executionClass) },
            { "outcome", NormalizeGraphOutcome(outcome) }
        };
        _graphConvergences.Add(1, tags);
        _duration.Record(Math.Max(0, duration.TotalMilliseconds), new TagList
        {
            { "stage", "graph-convergence" },
            { "recipe", "other" },
            { "outcome", NormalizeGraphOutcome(outcome) }
        });
    }

    public void RecordGraphRecoveryPoll(DateTimeOffset now, int executionCount)
    {
        Interlocked.Exchange(ref _lastGraphRecoveryUtcTicks, now.UtcDateTime.Ticks);
        _graphRecoveryPolls.Add(1, new TagList
        {
            { "outcome", executionCount == 0 ? "empty" : "converged" }
        });
    }

    public void UpdateGraphQueueSnapshot(
        IReadOnlyList<CentralProcessingGraphQueueMeasurement> measurements,
        long oldestConvergenceAgeSeconds)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        _graphQueue.Clear();
        foreach (var measurement in measurements)
        {
            _graphQueue[(NormalizeGraphClass(measurement.ExecutionClass),
                NormalizeGraphOutcome(measurement.Status))] = measurement.Count;
        }
        Interlocked.Exchange(ref _oldestGraphConvergenceAgeSeconds, Math.Max(0, oldestConvergenceAgeSeconds));
    }

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

    public Activity? StartWindowResolution(string recipe, Guid jobId)
    {
        var activity = _activitySource.StartActivity("central-derivative.window.resolve");
        activity?.SetTag("recipe", NormalizeRecipe(recipe));
        activity?.SetTag("job.id", jobId);
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

    public void UpdateWindowSnapshot(
        long waiting,
        long oldestWaitAgeSeconds,
        long activePins,
        long oldestPinAgeSeconds,
        long pinnedBytes)
    {
        Interlocked.Exchange(ref _windowWaiting, Math.Max(0, waiting));
        Interlocked.Exchange(ref _windowOldestWaitAgeSeconds, Math.Max(0, oldestWaitAgeSeconds));
        Interlocked.Exchange(ref _windowActivePins, Math.Max(0, activePins));
        Interlocked.Exchange(ref _windowOldestPinAgeSeconds, Math.Max(0, oldestPinAgeSeconds));
        Interlocked.Exchange(ref _windowPinnedBytes, Math.Max(0, pinnedBytes));
    }

    public void RecordWindowResolution(
        string recipe,
        string outcome,
        TimeSpan duration,
        long selectedInputs,
        long expectedInputs,
        long missingInputs,
        long selectedBytes,
        TimeSpan processingLag)
    {
        var normalizedRecipe = NormalizeRecipe(recipe);
        var tags = new TagList { { "recipe", normalizedRecipe }, { "outcome", outcome } };
        _windowResolutions.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, new TagList
        {
            { "stage", "window-resolution" },
            { "recipe", normalizedRecipe },
            { "outcome", outcome }
        });
        _windowSelectedInputs.Record(Math.Max(0, selectedInputs), tags);
        _windowExpectedInputs.Record(Math.Max(0, expectedInputs), tags);
        _windowMissingInputs.Record(Math.Max(0, missingInputs), tags);
        _windowCompleteness.Record(expectedInputs <= 0
            ? 1
            : Math.Clamp((double)selectedInputs / expectedInputs, 0, 1), tags);
        _windowSelectedBytes.Record(Math.Max(0, selectedBytes), tags);
        _windowProcessingLag.Record(Math.Max(0, processingLag.TotalMilliseconds), tags);
    }

    public void RecordWindowNotification(string recipe, string disposition)
        => _windowNotifications.Add(1, new TagList
        {
            { "recipe", NormalizeRecipe(recipe) },
            { "disposition", disposition is "affected" or "ignored" ? disposition : "other" }
        });

    public void RecordWindowPinDuration(string recipe, TimeSpan duration, string outcome = "released")
        => _windowPinDuration.Record(Math.Max(0, duration.TotalMilliseconds), new TagList
        {
            { "recipe", NormalizeRecipe(recipe) },
            { "outcome", NormalizeStatus(outcome) }
        });

    public void RecordWindowRejection(string recipe, string axis, string outcome)
        => _windowRejections.Add(1, new TagList
        {
            { "recipe", NormalizeRecipe(recipe) },
            { "axis", axis is "layout" or "profile" ? axis : "other" },
            { "outcome", outcome }
        });

    public void RecordWindowDeadline(string recipe, string outcome)
        => _windowDeadlines.Add(1, new TagList
        {
            { "recipe", NormalizeRecipe(recipe) },
            { "outcome", outcome }
        });

    public void RecordTransientValidation(
        string outcome,
        int candidateCount,
        IEnumerable<TransientClassification> classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        var normalizedOutcome = outcome switch
        {
            "persisted" => "persisted",
            "no-candidate" => "no-candidate",
            "adopted" => "adopted",
            _ => "other"
        };
        _transientOutcomes.Add(1, new TagList { { "outcome", normalizedOutcome } });
        _transientCandidates.Record(Math.Max(0, candidateCount), new TagList { { "outcome", normalizedOutcome } });
        foreach (var classification in classifications)
        {
            _transientClassifications.Add(1, new TagList
            {
                { "classification", classification.ToString().ToLowerInvariant() }
            });
        }
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
        BuiltInProcessingRecipes.RollingMean => BuiltInProcessingRecipes.RollingMean,
        CentralTransientRuntime.RecipeName => CentralTransientRuntime.RecipeName,
        _ => "other"
    };

    private IEnumerable<Measurement<long>> ObserveQueue()
        => _queue.Select(pair => new Measurement<long>(pair.Value, new TagList
        {
            { "status", pair.Key.Status },
            { "recipe", pair.Key.Recipe }
        }));

    private IEnumerable<Measurement<long>> ObserveGraphQueue()
        => _graphQueue.Select(pair => new Measurement<long>(pair.Value, new TagList
        {
            { "class", pair.Key.Class },
            { "status", pair.Key.Status }
        }));

    private static string NormalizeStatus(string status) => status switch
    {
        "pending" => "pending",
        "leased" => "leased",
        "retryable" => "retryable",
        "waiting" => "waiting",
        _ => "other"
    };

    private static string NormalizeGraphClass(string executionClass) => executionClass switch
    {
        "Live" => "live",
        "Replay" => "replay",
        _ => "other"
    };

    private static string NormalizeGraphOutcome(string outcome) => outcome.ToLowerInvariant() switch
    {
        "created" => "created",
        "existing" => "existing",
        "conflict" => "conflict",
        "pending" => "pending",
        "running" => "running",
        "completed" => "completed",
        "completedwithoptionalfailures" => "completed-optional",
        "failed" => "failed",
        "cancelrequested" => "cancel-requested",
        "canceled" => "canceled",
        "superseded" => "superseded",
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

internal sealed record CentralProcessingGraphQueueMeasurement(string ExecutionClass, string Status, long Count);
