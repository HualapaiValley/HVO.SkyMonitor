using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class CentralTransientLifecycleTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CentralTransientLifecycle";
    public const string ActivitySourceName = "HVO.SkyMonitor.CentralTransientLifecycle";

    private readonly Meter meter = new(MeterName, "1.0.0");
    private readonly ActivitySource activitySource = new(ActivitySourceName, "1.0.0");
    private readonly Counter<long> operations;
    private readonly Histogram<double> duration;
    private readonly Counter<long> derivativeOutputs;
    private readonly Histogram<long> derivativeBytes;
    private readonly Counter<long> retentionItems;
    private readonly ILogger<CentralTransientLifecycleTelemetry> logger;
    private readonly string runtimeId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public CentralTransientLifecycleTelemetry(ILogger<CentralTransientLifecycleTelemetry> logger)
    {
        this.logger = logger;
        operations = meter.CreateCounter<long>(
            "skymonitor.central.transient.lifecycle.operations", "{operation}");
        duration = meter.CreateHistogram<double>(
            "skymonitor.central.transient.lifecycle.duration", "ms");
        derivativeOutputs = meter.CreateCounter<long>(
            "skymonitor.central.transient.derivative.outputs", "{artifact}");
        derivativeBytes = meter.CreateHistogram<long>(
            "skymonitor.central.transient.derivative.output.bytes", "By");
        retentionItems = meter.CreateCounter<long>(
            "skymonitor.central.transient.retention.items", "{item}");
    }

    public Activity? Start(string operation, ActivityKind kind = ActivityKind.Internal)
        => activitySource.StartActivity($"central-transient.{NormalizeOperation(operation)}", kind);

    public void RecordReview(string outcome, TimeSpan elapsed)
    {
        Record("review", outcome, elapsed);
        Log.Review(logger, runtimeId, NormalizeOutcome(outcome));
    }

    public void RecordReprocessing(string outcome, TimeSpan elapsed)
    {
        Record("reprocess", outcome, elapsed);
        Log.Reprocessing(logger, runtimeId, NormalizeOutcome(outcome));
    }

    public void RecordDerivative(string kind, string outcome, long bytes)
    {
        var normalizedKind = NormalizeDerivativeKind(kind);
        var normalizedOutcome = NormalizeOutcome(outcome);
        derivativeOutputs.Add(1,
            new KeyValuePair<string, object?>("kind", normalizedKind),
            new KeyValuePair<string, object?>("outcome", normalizedOutcome));
        derivativeBytes.Record(Math.Max(0, bytes),
            new KeyValuePair<string, object?>("kind", normalizedKind));
    }

    public void RecordDerivativeBundle(string outcome, int outputCount, TimeSpan elapsed)
    {
        Record("derivative", outcome, elapsed);
        Log.Derivative(logger, runtimeId, NormalizeOutcome(outcome), Math.Max(0, outputCount));
    }

    public void RecordNotification(string outcome, TimeSpan elapsed)
    {
        Record("notification", outcome, elapsed);
        Log.Notification(logger, runtimeId, NormalizeOutcome(outcome));
    }

    public void RecordRetention(string outcome, int itemCount, TimeSpan elapsed)
    {
        Record("retention", outcome, elapsed);
        Log.Retention(logger, runtimeId, NormalizeOutcome(outcome), Math.Max(0, itemCount));
    }

    public void RecordRetentionItem(string kind, string outcome, int retryCount)
    {
        var normalizedKind = NormalizeRetentionKind(kind);
        var normalizedOutcome = NormalizeOutcome(outcome);
        retentionItems.Add(1,
            new KeyValuePair<string, object?>("kind", normalizedKind),
            new KeyValuePair<string, object?>("outcome", normalizedOutcome));
        Log.RetentionItem(logger, normalizedKind, normalizedOutcome, Math.Max(0, retryCount));
    }

    private void Record(string operation, string outcome, TimeSpan elapsed)
    {
        var normalizedOperation = NormalizeOperation(operation);
        var normalizedOutcome = NormalizeOutcome(outcome);
        operations.Add(1,
            new KeyValuePair<string, object?>("operation", normalizedOperation),
            new KeyValuePair<string, object?>("outcome", normalizedOutcome));
        duration.Record(Math.Max(0, elapsed.TotalMilliseconds),
            new KeyValuePair<string, object?>("operation", normalizedOperation),
            new KeyValuePair<string, object?>("outcome", normalizedOutcome));
    }

    private static string NormalizeOperation(string value) => value switch
    {
        "review" => "review",
        "reprocess" => "reprocess",
        "derivative" => "derivative",
        "notification" => "notification",
        "retention" => "retention",
        _ => "other"
    };

    private static string NormalizeOutcome(string value) => value switch
    {
        "applied" => "applied",
        "scheduled" => "scheduled",
        "replayed" => "replayed",
        "persisted" => "persisted",
        "adopted" => "adopted",
        "sent" => "sent",
        "failed" => "failed",
        "suppressed" => "suppressed",
        "recovered" => "recovered",
        "completed" => "completed",
        "conflict" => "conflict",
        "invalid" => "invalid",
        "reserved" => "reserved",
        "released" => "released",
        "preserved" => "preserved",
        "retry" => "retry",
        _ => "other"
    };

    private static string NormalizeDerivativeKind(string value) => value switch
    {
        "Crop" => "crop",
        "Preview" => "preview",
        "Mask" => "mask",
        "Overlay" => "overlay",
        "Reconstruction" => "reconstruction",
        _ => "other"
    };

    private static string NormalizeRetentionKind(string value) => value switch
    {
        "SourceArtifact" => "source",
        "Derivative" => "derivative",
        _ => "other"
    };

    public void Dispose()
    {
        activitySource.Dispose();
        meter.Dispose();
    }

    private static partial class Log
    {
        [LoggerMessage(2162, LogLevel.Information,
            "Central transient review completed: RuntimeId={RuntimeId} Outcome={Outcome}")]
        public static partial void Review(ILogger logger, string runtimeId, string outcome);

        [LoggerMessage(2163, LogLevel.Information,
            "Central transient reprocessing scheduled: RuntimeId={RuntimeId} Outcome={Outcome}")]
        public static partial void Reprocessing(ILogger logger, string runtimeId, string outcome);

        [LoggerMessage(2164, LogLevel.Information,
            "Central transient derivative bundle committed: RuntimeId={RuntimeId} Outcome={Outcome} OutputCount={OutputCount}")]
        public static partial void Derivative(ILogger logger, string runtimeId, string outcome, int outputCount);

        [LoggerMessage(2165, LogLevel.Information,
            "Central transient notification completed: RuntimeId={RuntimeId} Outcome={Outcome}")]
        public static partial void Notification(ILogger logger, string runtimeId, string outcome);

        [LoggerMessage(2166, LogLevel.Information,
            "Central transient payload release completed: RuntimeId={RuntimeId} Outcome={Outcome} ItemCount={ItemCount}")]
        public static partial void Retention(ILogger logger, string runtimeId, string outcome, int itemCount);

        [LoggerMessage(2167, LogLevel.Information,
            "Central transient payload release item transitioned: Kind={Kind} Outcome={Outcome} RetryCount={RetryCount}")]
        public static partial void RetentionItem(ILogger logger, string kind, string outcome, int retryCount);
    }
}
