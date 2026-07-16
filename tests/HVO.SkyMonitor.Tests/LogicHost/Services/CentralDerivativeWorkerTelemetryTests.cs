using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralDerivativeWorkerTelemetryTests
{
    [TestMethod]
    public void PublishesWorkerSignalsWithBoundedLabelsAndNestedStageSpans()
    {
        var measurements = new List<(string Name, string[] Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CentralDerivativeWorkerTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, GetTagNames(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, GetTagNames(tags))));
        meterListener.Start();

        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CentralDerivativeWorkerTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        using var telemetry = new CentralDerivativeWorkerTelemetry();
        telemetry.RecordPoll(DateTimeOffset.UtcNow);
        telemetry.UpdateQueueSnapshot(
            [new CentralDerivativeQueueMeasurement("pending", "unregistered-recipe", 4)],
            12);
        telemetry.UpdateWindowSnapshot(2, 15, 5, 20, 320);
        telemetry.RecordClaim("claimed", TimeSpan.FromMilliseconds(1));
        var renewalFailureAt = DateTimeOffset.UtcNow;
        telemetry.RecordRenewal("failed", renewalFailureAt);
        telemetry.RecordRenewal("renewed", renewalFailureAt.AddMilliseconds(1));
        telemetry.RecordDependencyFailure("database", DateTimeOffset.UtcNow);
        telemetry.RecordStage("load", "unregistered-recipe", "completed", TimeSpan.FromMilliseconds(2), 64);
        telemetry.RecordAttempt("unregistered-recipe", "retryable", "storage", DateTimeOffset.UtcNow);
        telemetry.RecordRecovery("adopted");
        telemetry.RecordOperation("requeue", "completed");
        telemetry.RecordWindowResolution(
            "rolling-mean", "pending", TimeSpan.FromMilliseconds(3), 4, 5, 1, 320, TimeSpan.FromSeconds(2));
        telemetry.RecordWindowNotification("rolling-mean", "affected");
        telemetry.RecordWindowPinDuration("rolling-mean", TimeSpan.FromSeconds(3));
        telemetry.RecordWindowRejection("rolling-mean", "profile", "skip");
        telemetry.RecordWindowDeadline("rolling-mean", "skipped");
        ActivitySpanId executionSpanId;
        ActivitySpanId stageSpanId;
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var traceParent = $"00-{parentTraceId}-{parentSpanId}-01";
        using (telemetry.TrackActive())
        using (var executionActivity = telemetry.StartExecution(
            "unregistered-recipe", traceParent, "vendor=value", Guid.Empty, 2))
        using (var stageActivity = telemetry.StartStage("execute", "unregistered-recipe"))
        using (var windowActivity = telemetry.StartWindowResolution("rolling-mean", Guid.Empty))
        {
            executionSpanId = executionActivity!.SpanId;
            stageSpanId = stageActivity!.SpanId;
            foreach (var stageName in new[] { "claim", "load", "verify", "publish", "complete", "recover" })
            {
                using var additionalStage = telemetry.StartStage(stageName, "unregistered-recipe");
            }
            meterListener.RecordObservableInstruments();
        }

        measurements.Select(static measurement => measurement.Name).Should().Contain([
            "skymonitor.central.derivative.claims",
            "skymonitor.central.derivative.lease.renewals",
            "skymonitor.central.derivative.attempts",
            "skymonitor.central.derivative.recoveries",
            "skymonitor.central.derivative.operations",
            "skymonitor.central.derivative.dependency.failures",
            "skymonitor.central.derivative.bytes",
            "skymonitor.central.derivative.window.resolutions",
            "skymonitor.central.derivative.window.notifications",
            "skymonitor.central.derivative.window.compatibility_rejections",
            "skymonitor.central.derivative.window.deadlines",
            "skymonitor.central.derivative.window.selected_inputs",
            "skymonitor.central.derivative.window.expected_inputs",
            "skymonitor.central.derivative.window.missing_inputs",
            "skymonitor.central.derivative.window.completeness",
            "skymonitor.central.derivative.window.processing_lag",
            "skymonitor.central.derivative.window.pin_duration",
            "skymonitor.central.derivative.window.selected_bytes",
            "skymonitor.central.derivative.duration",
            "skymonitor.central.derivative.active",
            "skymonitor.central.derivative.queue",
            "skymonitor.central.derivative.queue.oldest_age",
            "skymonitor.central.derivative.window.waiting",
            "skymonitor.central.derivative.window.waiting.oldest_age",
            "skymonitor.central.derivative.window.pins.active",
            "skymonitor.central.derivative.window.pins.bytes"]);
        measurements.Select(static measurement => measurement.Name).Should().Contain(
            "skymonitor.central.derivative.window.pins.oldest_age");
        var allowedTags = new HashSet<string>(
            ["outcome", "stage", "recipe", "direction", "cause", "operation", "dependency", "status", "axis", "disposition"],
            StringComparer.Ordinal);
        measurements.SelectMany(static measurement => measurement.Tags)
            .Should().OnlyContain(tag => allowedTags.Contains(tag));
        var execution = activities.Single(activity => activity.SpanId == executionSpanId);
        var stage = activities.Single(activity => activity.SpanId == stageSpanId);
        execution.GetTagItem("recipe").Should().Be("other");
        execution.TraceId.Should().Be(parentTraceId);
        execution.ParentSpanId.Should().Be(parentSpanId);
        execution.TraceStateString.Should().Be("vendor=value");
        execution.GetTagItem("job.id").Should().Be(Guid.Empty);
        execution.GetTagItem("attempt").Should().Be(2);
        stage.ParentSpanId.Should().Be(execution.SpanId);
        activities.Select(activity => activity.GetTagItem("stage") as string).Should().Contain(
            ["claim", "load", "verify", "execute", "publish", "complete", "recover"]);
        activities.Should().Contain(activity => activity.OperationName == "central-derivative.window.resolve"
            && Equals(activity.GetTagItem("recipe"), "rolling-mean"));
        telemetry.HasRecentRenewalFailure(renewalFailureAt.AddSeconds(1), TimeSpan.FromMinutes(1))
            .Should().BeTrue();
    }

    private static string[] GetTagNames(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var names = new string[tags.Length];
        for (var index = 0; index < tags.Length; index++)
        {
            names[index] = tags[index].Key;
        }
        return names;
    }
}
