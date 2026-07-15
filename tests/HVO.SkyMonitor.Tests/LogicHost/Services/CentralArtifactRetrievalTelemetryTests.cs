using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralArtifactRetrievalTelemetryTests
{
    [TestMethod]
    public void PublishesRequiredSignalsWithBoundedLabels()
    {
        var measurements = new List<(string Name, long Value, string[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == CentralArtifactRetrievalTelemetry.MeterName)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, GetTagNames(tags))));
        listener.Start();
        using var telemetry = new CentralArtifactRetrievalTelemetry();

        telemetry.RecordAuthorization("owner", "allowed");
        telemetry.RecordVerification("matched");
        telemetry.RecordObjectRead("serve", "completed", 32, TimeSpan.FromMilliseconds(1));
        telemetry.RecordRetention("held");
        using (telemetry.TrackStream())
        {
            listener.RecordObservableInstruments();
        }

        measurements.Select(static measurement => measurement.Name).Should().Contain([
            "skymonitor.central.retrieval.authorization",
            "skymonitor.central.retrieval.verification",
            "skymonitor.central.retrieval.reads",
            "skymonitor.central.retrieval.bytes",
            "skymonitor.central.retrieval.retention",
            "skymonitor.central.retrieval.active_streams"]);
        var allowedTags = new HashSet<string>(["caller.kind", "outcome", "operation"], StringComparer.Ordinal);
        measurements.SelectMany(static measurement => measurement.Tags)
            .Should().OnlyContain(tag => allowedTags.Contains(tag));
        measurements.Where(measurement => measurement.Name == "skymonitor.central.retrieval.active_streams")
            .Should().Contain(measurement => measurement.Value == 1);
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
