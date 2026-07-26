using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

[TestClass]
[TestCategory("Unit")]
public sealed class CalibrationTelemetryTests
{
    [TestMethod]
    public void RecordsBoundedMetricsAndReservedEvents()
    {
        var samples = new List<MetricSample>();
        var logger = new RecordingLogger<CalibrationTelemetry>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CalibrationTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            samples.Add(new MetricSample(instrument.Name, measurement, ReadTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            samples.Add(new MetricSample(instrument.Name, measurement, ReadTags(tags))));
        listener.Start();
        using var telemetry = new CalibrationTelemetry(logger);

        telemetry.ReplaceInventory(new CalibrationTelemetryInventory(
            new Dictionary<(string State, string Source), long>
            {
                [("published", "legacy-synthetic-v1")] = 1
            },
            new Dictionary<(string Kind, string State), long>
            {
                [("bias", "published")] = 100
            }));
        telemetry.AddPublishedBundle(
            "virtual-acquisition-v1",
            [new CalibrationTelemetryArtifact("dark", 200)],
            200);
        telemetry.RecordLibraryOperation("initialize", "success");
        telemetry.RecordAcquisitionTransition("planned", "acquiring", "source-bias-0", 1, null);
        telemetry.RecordAcquisition("published", TimeSpan.FromMilliseconds(20));
        telemetry.RecordMasterBuild("flat", "success", TimeSpan.FromMilliseconds(10));
        telemetry.RecordActivation("activate", "success", 2);
        telemetry.RecordSelection("calibration.library.selected", TimeSpan.FromMilliseconds(2), 2, log: true);
        telemetry.RecordValidationFailure("select", "private-path-or-error");
        telemetry.RecordQuarantine("calibration.library.incomplete", 300);
        listener.RecordObservableInstruments();

        var expectedNames = new[]
        {
            "camera_agent.calibration.bundles",
            "camera_agent.calibration.reference.bytes",
            "camera_agent.calibration.acquisitions",
            "camera_agent.calibration.acquisition.duration",
            "camera_agent.calibration.master_build.duration",
            "camera_agent.calibration.selections",
            "camera_agent.calibration.selection.duration",
            "camera_agent.calibration.failures"
        };
        CollectionAssert.AreEquivalent(expectedNames, samples.Select(static sample => sample.Name).Distinct().ToArray());
        Assert.IsTrue(samples.All(static sample => sample.Tags.Keys.All(static key =>
            key is "state" or "source" or "kind" or "outcome" or "reason" or "boundary")));
        Assert.IsFalse(samples.SelectMany(static sample => sample.Tags.Values)
            .Any(static value => value.Contains("private", StringComparison.Ordinal)));
        CollectionAssert.AreEquivalent(
            Enumerable.Range(2610, 8).ToArray(),
            logger.EventIds.Distinct().Order().ToArray());
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => EventIds.Add(eventId.Id);
    }
}
