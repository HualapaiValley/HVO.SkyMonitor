using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
public sealed class RawIngressTelemetryTests
{
    private static readonly string[] ExpectedMetricNames =
    [
        "camera_agent.ingress.committed",
        "camera_agent.ingress.committed.bytes",
        "camera_agent.ingress.commit.duration",
        "camera_agent.ingress.failures",
        "camera_agent.ingress.accepting",
        "camera_agent.ingress.pending",
        "camera_agent.ingress.pending.bytes",
        "camera_agent.ingress.quarantine.records",
        "camera_agent.ingress.quarantine.bytes",
        "camera_agent.ingress.reconciliation.records",
        "camera_agent.ingress.sqlite.transactions",
        "camera_agent.ingress.sqlite.checkpoints"
    ];

    [TestMethod]
    public void RecordsCommittedFailureReconciliationAndBoundedStateMetrics()
    {
        var measurements = new List<MetricSample>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RawIngressTelemetry.MeterName)
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
        var state = new RawIngressState(TimeProvider.System);
        state.Set(RawIngressAvailability.Degraded, "reconciliation-findings", 3, 400, 1, 20);
        using var telemetry = new RawIngressTelemetry(state);

        telemetry.RecordCommit(RawIngressOutcome.Committed, 100, TimeSpan.FromMilliseconds(10));
        telemetry.RecordTransaction("commit", succeeded: true);
        telemetry.RecordFailure("accept", "io");
        telemetry.RecordReconciliation(new RawIngressReconciliationSummary(4, 1, 1, 1, 1, 20));
        telemetry.RecordCheckpoint(succeeded: true);
        listener.RecordObservableInstruments();

        var names = measurements.Select(static sample => sample.Name).ToHashSet(StringComparer.Ordinal);
        CollectionAssert.IsSubsetOf(ExpectedMetricNames, names.ToArray());
        Assert.IsTrue(measurements.All(static sample =>
            sample.Tags.Keys.All(static key => key is "root" or "outcome" or "operation" or "result" or "phase" or "reason")));
        Assert.IsTrue(measurements.All(static sample =>
            sample.Tags.TryGetValue("root", out var root) && root == "primary"));
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
