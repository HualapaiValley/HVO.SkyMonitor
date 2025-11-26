using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.Telemetry;

public sealed class CaptureTelemetryMetricsRecorder
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.Capture";

    private static readonly Meter Meter = new(MeterName);

    private readonly Counter<long> _captureCounter;
    private readonly Counter<long> _framesStoredCounter;
    private readonly Counter<long> _immediateUploadCounter;
    private readonly Histogram<double> _intervalMs;
    private readonly Histogram<double> _exposureMs;
    private readonly Histogram<double> _processingMs;
    private readonly Histogram<double> _loopMs;
    private readonly Histogram<double> _gain;
    private readonly Histogram<double> _temperatureC;

    public CaptureTelemetryMetricsRecorder()
    {
        _captureCounter = Meter.CreateCounter<long>(
            name: "camera_agent.capture.count",
            unit: null,
            description: "Total number of capture cycles completed.");

        _framesStoredCounter = Meter.CreateCounter<long>(
            name: "camera_agent.capture.frames.stored",
            unit: null,
            description: "Number of captures that produced a frame.");

        _immediateUploadCounter = Meter.CreateCounter<long>(
            name: "camera_agent.capture.immediate_uploads",
            unit: null,
            description: "Number of captures requesting immediate upload.");

        _intervalMs = Meter.CreateHistogram<double>(
            name: "camera_agent.capture.interval_ms",
            unit: "ms",
            description: "Effective interval between capture cycles.");

        _exposureMs = Meter.CreateHistogram<double>(
            name: "camera_agent.capture.exposure_ms",
            unit: "ms",
            description: "Exposure duration for each capture.");

        _processingMs = Meter.CreateHistogram<double>(
            name: "camera_agent.capture.processing_ms",
            unit: "ms",
            description: "Processing latency for capture pipelines.");

        _loopMs = Meter.CreateHistogram<double>(
            name: "camera_agent.capture.loop_ms",
            unit: "ms",
            description: "Total duration of a capture loop iteration.");

        _gain = Meter.CreateHistogram<double>(
            name: "camera_agent.capture.gain",
            unit: null,
            description: "Gain applied per capture.");

        _temperatureC = Meter.CreateHistogram<double>(
            name: "camera_agent.capture.temperature_c",
            unit: "C",
            description: "Reported sensor or setpoint temperature.");
    }

    public void Record(CaptureTelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var tags = new TagList
        {
            { "mode", sample.Mode.ToString() }
        };

        _captureCounter.Add(1, tags);
        _intervalMs.Record(sample.Interval.TotalMilliseconds, tags);
        _exposureMs.Record(sample.Exposure.TotalMilliseconds, tags);
        _processingMs.Record(sample.ProcessingLatency.TotalMilliseconds, tags);
        _loopMs.Record(sample.LoopDuration.TotalMilliseconds, tags);
        _gain.Record(sample.Gain, tags);

        if (sample.FrameStored)
        {
            _framesStoredCounter.Add(1, tags);
        }

        if (sample.RequiresImmediateUpload)
        {
            _immediateUploadCounter.Add(1, tags);
        }

        if (sample.TemperatureC is { } temperature && !double.IsNaN(temperature))
        {
            _temperatureC.Record(temperature, tags);
        }
    }
}
