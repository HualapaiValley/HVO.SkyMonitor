using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class EnvironmentalObservationTelemetryTests
{
    [TestMethod]
    public void MetricsUseOnlyBoundedCategoricalLabels()
    {
        var observations = new List<(string Name, string[] Keys)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == EnvironmentalObservationTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            observations.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            observations.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.Start();
        using var telemetry = new EnvironmentalObservationTelemetry();

        telemetry.RecordIngest(
            EnvironmentalObservationIngestDisposition.Accepted,
            EnvironmentalObservationKind.RelativeHumidity,
            EnvironmentalObservationSourceKind.Simulated,
            512,
            2,
            EnvironmentalClockDiagnostic.WithinTolerance,
            TimeSpan.FromMilliseconds(4));
        telemetry.RecordIngestFailure();
        telemetry.RecordCorrelation(
            EnvironmentalObservationKind.RelativeHumidity,
            EnvironmentalObservationMatchStatus.Fresh,
            overlap: true,
            TimeSpan.FromMilliseconds(2));
        telemetry.RecordValidation("contract");
        telemetry.RecordConflict("observation");
        telemetry.RecordRetention(3, TimeSpan.FromMilliseconds(3), "succeeded");

        observations.Select(item => item.Name).Should().Contain([
            "skymonitor.environment.ingest",
            "skymonitor.environment.ingest.duration",
            "skymonitor.environment.ingest.failures",
            "skymonitor.environment.payload.size",
            "skymonitor.environment.clock.offset",
            "skymonitor.environment.validation",
            "skymonitor.environment.conflicts",
            "skymonitor.environment.correlation",
            "skymonitor.environment.correlation.duration",
            "skymonitor.environment.retention",
            "skymonitor.environment.retention.duration"
        ]);
        var allowed = new HashSet<string>(
            ["schema", "disposition", "observation_kind", "source_kind", "diagnostic", "scope", "outcome", "overlap"],
            StringComparer.Ordinal);
        observations.SelectMany(item => item.Keys).Should().OnlyContain(key => allowed.Contains(key));
    }
}
