using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

internal static class StorageLifecycleTelemetry
{
    // This is a diagnostic budget, not a timeout: durable operations must finish safely.
    internal static readonly TimeSpan WaitBudget = TimeSpan.FromMilliseconds(250);
    private static readonly Meter Meter = new(CaptureProcessingTelemetry.MeterName);
    private static readonly Histogram<double> WaitDuration = Meter.CreateHistogram<double>(
        "camera_agent.storage.lifecycle.wait.duration", "s");
    private static readonly Action<ILogger, string, double, Exception?> Waited = LoggerMessage.Define<string, double>(
        LogLevel.Debug, new EventId(8450, "StorageLifecycleWait"),
        "Storage lifecycle lock wait by {Caller} ended after {WaitMilliseconds} ms");
    private static readonly Action<ILogger, string, double, Exception?> BudgetExceeded = LoggerMessage.Define<string, double>(
        LogLevel.Warning, new EventId(8451, "StorageLifecycleWaitBudgetExceeded"),
        "Storage lifecycle lock wait by {Caller} exceeded the 250 ms budget: {WaitMilliseconds} ms");

    internal static void Record(string caller, TimeSpan elapsed, ILogger? logger, bool acquired = true)
    {
        WaitDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("caller", caller));
        Activity.Current?.AddEvent(new ActivityEvent(acquired ? "storage.lifecycle.lock.acquired" : "storage.lifecycle.lock.cancelled", tags: new ActivityTagsCollection
        {
            { "caller", caller }, { "wait.duration", elapsed.TotalSeconds }
        }));
        if (logger is null) return;
        Waited(logger, caller, elapsed.TotalMilliseconds, null);
        if (elapsed > WaitBudget) BudgetExceeded(logger, caller, elapsed.TotalMilliseconds, null);
    }
}
