using System.Diagnostics.Metrics;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CentralIngestTelemetryTests
{
    [TestMethod]
    public void PublishesReadinessManifestWithBoundedLabels()
    {
        var measurements = new List<(string Name, double Value, string? Unit, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == CentralIngestTelemetry.MeterName)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, instrument.Unit, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, instrument.Unit, tags.ToArray())));
        listener.Start();
        using var telemetry = new CentralIngestTelemetry();

        telemetry.RecordValidation("v2", "accepted");
        telemetry.RecordChecksum("upload", "matched");
        telemetry.RecordDuplicate("v2");
        telemetry.RecordQuarantine("object.checksum-mismatch");
        telemetry.RecordObjectWrite("staging", "completed", TimeSpan.FromMilliseconds(1));
        telemetry.RecordSqlCommit("completed", TimeSpan.FromMilliseconds(2));
        telemetry.RecordReconciliationDuration("completed", TimeSpan.FromMilliseconds(3));
        telemetry.RecordVerificationDuration("stream", "matched", TimeSpan.FromMilliseconds(4));
        telemetry.RecordReconciliationConcurrency("retry");
        telemetry.RecordReconciliationConcurrency("converged");
        telemetry.RecordReconciliationConcurrency("exhausted");
        telemetry.RecordBacklog(1, 10, 100, 2, 20, 200, 3, 30, 300, 4, 40, 400);
        listener.RecordObservableInstruments();

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "skymonitor.central.ingest.validation",
            "skymonitor.central.ingest.checksums",
            "skymonitor.central.ingest.duplicates",
            "skymonitor.central.ingest.quarantines",
            "skymonitor.central.ingest.object_write.duration",
            "skymonitor.central.ingest.sql_commit.duration",
            "skymonitor.central.ingest.reconciliation.duration",
            "skymonitor.central.ingest.verification.duration",
            "skymonitor.central.ingest.reconciliation_concurrency",
            "skymonitor.central.ingest.pending_objects",
            "skymonitor.central.ingest.pending_object_bytes",
            "skymonitor.central.ingest.pending_object_oldest_age",
            "skymonitor.central.ingest.pending_references",
            "skymonitor.central.ingest.pending_reference_bytes",
            "skymonitor.central.ingest.pending_reference_oldest_age",
            "skymonitor.central.ingest.quarantined",
            "skymonitor.central.ingest.quarantined_bytes",
            "skymonitor.central.ingest.quarantined_oldest_age",
            "skymonitor.central.ingest.pending_verifications",
            "skymonitor.central.ingest.pending_verification_bytes",
            "skymonitor.central.ingest.pending_verification_oldest_age"
        };
        expected.Should().BeSubsetOf(measurements.Select(static measurement => measurement.Name).ToHashSet(StringComparer.Ordinal));
        var allowedTags = new HashSet<string>(["schema", "outcome", "operation", "reason", "phase"], StringComparer.Ordinal);
        measurements.SelectMany(static measurement => measurement.Tags.Select(tag => tag.Key))
            .Should().OnlyContain(tag => allowedTags.Contains(tag));
        var concurrency = measurements.Where(measurement =>
            measurement.Name == "skymonitor.central.ingest.reconciliation_concurrency").ToArray();
        concurrency.Should().HaveCount(3);
        concurrency.Should().OnlyContain(measurement => measurement.Unit == "{conflict}" && measurement.Value == 1);
        concurrency.SelectMany(measurement => measurement.Tags)
            .Where(tag => tag.Key == "outcome")
            .Select(tag => tag.Value)
            .Should().BeEquivalentTo(["retry", "converged", "exhausted"]);
        measurements.Single(measurement => measurement.Name == "skymonitor.central.ingest.pending_object_bytes").Value.Should().Be(10);
        measurements.Single(measurement => measurement.Name == "skymonitor.central.ingest.pending_reference_oldest_age").Value.Should().Be(200);
        measurements.Single(measurement => measurement.Name == "skymonitor.central.ingest.quarantined").Value.Should().Be(3);
        measurements.Single(measurement => measurement.Name == "skymonitor.central.ingest.pending_verifications").Value.Should().Be(4);
    }
}
