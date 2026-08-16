using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureLaneTelemetryTests
{
    private static readonly string[] ExpectedMetrics =
    [
        "camera_agent.lanes.work.created",
        "camera_agent.lanes.claims",
        "camera_agent.lanes.completed",
        "camera_agent.lanes.retries",
        "camera_agent.lanes.quarantined",
        "camera_agent.lanes.abandoned",
        "camera_agent.lanes.wakeups",
        "camera_agent.lanes.claim.duration",
        "camera_agent.lanes.processing.duration",
        "camera_agent.lanes.ack.duration",
        "camera_agent.lanes.sqlite.lock_wait.duration",
        "camera_agent.lanes.accepting",
        "camera_agent.lanes.pending",
        "camera_agent.lanes.pending.bytes",
        "camera_agent.lanes.oldest.age",
        "camera_agent.lanes.leased",
        "camera_agent.lanes.pressure"
    ];

    [TestMethod]
    public void RecordsBoundedPerLaneOperationsAndDurableBacklog()
    {
        var measurements = new List<MetricSample>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CaptureLaneTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            measurements.Add(new MetricSample(instrument.Name, measurement, ReadTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            measurements.Add(new MetricSample(instrument.Name, measurement, ReadTags(tags))));
        listener.Start();
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = "raw" });
        var state = new CaptureLaneState(TimeProvider.System, options);
        state.Update([
            new CaptureLaneBacklog("standard", true, 2, 8, DateTimeOffset.UtcNow.AddSeconds(-2), 1, 0, 0)
        ]);
        using var telemetry = new CaptureLaneTelemetry(state);
        var lane = new CaptureLaneDefinition("standard", true, true, true, new string('A', 64));
        var lease = new CaptureLaneLease(
            1, "standard", true, true, 1, "token", "owner", DateTimeOffset.UtcNow.AddMinutes(1), null!);

        telemetry.RecordCreated(lane);
        telemetry.RecordWork("secondary", false, "abandoned");
        telemetry.RecordClaim(lane, claimed: true, TimeSpan.FromMilliseconds(1));
        telemetry.RecordProcessing("standard", true, CaptureLaneHandlerOutcome.Completed, TimeSpan.FromMilliseconds(2));
        telemetry.RecordAcknowledgement(lease, CaptureLaneHandlerOutcome.Completed, TimeSpan.FromMilliseconds(1));
        telemetry.RecordAcknowledgement(lease, CaptureLaneHandlerOutcome.RetryableFailure, TimeSpan.FromMilliseconds(1));
        telemetry.RecordAcknowledgement(lease, CaptureLaneHandlerOutcome.TerminalFailure, TimeSpan.FromMilliseconds(1));
        telemetry.RecordLockWait(TimeSpan.FromMilliseconds(1));
        telemetry.RecordWakeup("standard", true, queued: false);
        listener.RecordObservableInstruments();

        var names = measurements.Select(static sample => sample.Name).ToHashSet(StringComparer.Ordinal);
        CollectionAssert.IsSubsetOf(ExpectedMetrics, names.ToArray());
        Assert.IsTrue(measurements.All(static sample =>
            sample.Tags.Keys.All(static key => key is "lane" or "required" or "result" or "outcome" or "reason")));
        Assert.IsFalse(measurements.Any(static sample =>
            sample.Tags.ContainsKey("capture_id") || sample.Tags.ContainsKey("path")));
    }

    private static Dictionary<string, string> ReadTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            result[tag.Key] = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        return result;
    }

    private sealed record MetricSample(string Name, double Value, Dictionary<string, string> Tags);
}
