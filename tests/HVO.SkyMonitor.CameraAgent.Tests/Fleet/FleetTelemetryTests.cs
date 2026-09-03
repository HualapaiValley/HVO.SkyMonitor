using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;

namespace HVO.SkyMonitor.Tests.Fleet;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class FleetTelemetryTests
{
    [TestMethod]
    public void EdgeTelemetryUsesOnlyBoundedLabels()
    {
        var observations = Observe(FleetHeartbeatTelemetry.MeterName, () =>
        {
            using var telemetry = new FleetHeartbeatTelemetry();
            telemetry.RecordQueued(1_024, transition: true, TimeSpan.FromMilliseconds(2));
            telemetry.RecordDelivery(FleetDeliveryDisposition.Retry, TimeSpan.FromMilliseconds(3));
        });

        observations.Select(static observation => observation.Name).Should().Contain([
            "hvo.fleet.edge.reports",
            "hvo.fleet.edge.payload.size",
            "hvo.fleet.edge.operation.duration",
            "hvo.fleet.edge.retries"
        ]);
        var allowed = new HashSet<string>(["schema", "kind", "operation", "reason"], StringComparer.Ordinal);
        observations.SelectMany(static observation => observation.TagKeys)
            .Should().OnlyContain(key => allowed.Contains(key));
    }

    private static List<Observation> Observe(string meterName, Action record)
    {
        var observations = new List<Observation>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            observations.Add(new Observation(instrument.Name, tags.ToArray().Select(static tag => tag.Key).ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            observations.Add(new Observation(instrument.Name, tags.ToArray().Select(static tag => tag.Key).ToArray())));
        listener.Start();

        record();

        return observations;
    }

    private sealed record Observation(string Name, IReadOnlyList<string> TagKeys);
}
