using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CaptureControlTelemetryTests
{
    private static readonly DateTimeOffset StartUtc = new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> ExpectedMetricUnits =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["camera_agent.capture_control.cycles"] = "{cycle}",
            ["camera_agent.capture_control.decisions"] = "{decision}",
            ["camera_agent.capture_control.metering.samples"] = "{sample}",
            ["camera_agent.capture_control.metering.scanned"] = "By",
            ["camera_agent.capture_control.start_jitter"] = "s",
            ["camera_agent.capture_control.cycle.duration"] = "s",
            ["camera_agent.capture_control.segment.duration"] = "s",
            ["camera_agent.capture_control.metering.duration"] = "s",
            ["camera_agent.capture_control.decision.duration"] = "s"
        };

    private static readonly Dictionary<string, string[]> ExpectedMetricTags =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["camera_agent.capture_control.cycles"] = ["cadence"],
            ["camera_agent.capture_control.decisions"] = ["reason", "regime"],
            ["camera_agent.capture_control.metering.samples"] = ["outcome"],
            ["camera_agent.capture_control.metering.scanned"] = ["outcome"],
            ["camera_agent.capture_control.start_jitter"] = ["cadence"],
            ["camera_agent.capture_control.cycle.duration"] = ["cadence"],
            ["camera_agent.capture_control.segment.duration"] = ["segment", "cadence"],
            ["camera_agent.capture_control.metering.duration"] = ["outcome"],
            ["camera_agent.capture_control.decision.duration"] = ["reason", "regime"]
        };

    private static readonly string[] ExpectedActivities =
    [
        "capture-cycle", "capture-cycle",
        "capture-meter", "capture-meter",
        "capture-control", "capture-control",
        "capture-ingress-handoff", "capture-ingress-handoff"
    ];

    [TestMethod]
    public async Task RunAsync_EmitsBoundedMetricsSpansAndDecisionSignals()
    {
        var measurements = new List<MetricSample>();
        var instrumentUnits = new Dictionary<string, string?>(StringComparer.Ordinal);
        var activities = new List<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CaptureControlTelemetry.MeterName)
                {
                    instrumentUnits[instrument.Name] = instrument.Unit;
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new MetricSample(instrument.Name, value, ReadTags(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new MetricSample(instrument.Name, value, ReadTags(tags))));
        meterListener.Start();

        using var parentActivity = new Activity("capture-control-telemetry-test").Start();
        var observedTraceId = parentActivity.TraceId;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CaptureControlTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == observedTraceId)
                {
                    activities.Add(activity.DisplayName);
                }
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var module = new DeterministicModule(timeProvider, CameraPixelFormat.Mono16);
        var context = new DeterministicHostContext(
            CreateConfig(CameraPixelFormat.Mono16, CaptureCadenceMode.MinimumStartInterval),
            timeProvider,
            cancellation,
            publishTarget: 2,
            TimeSpan.FromSeconds(1));
        using var telemetry = new CaptureControlTelemetry();
        var runner = new CameraModuleRunner(
            module,
            context,
            timeProvider,
            logger,
            new ZenithSunEphemeris(),
            telemetry);

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        CollectionAssert.AreEquivalent(
            ExpectedMetricUnits.Keys.Append("camera_agent.capture_control.admission.commands").ToArray(),
            instrumentUnits.Keys.ToArray());
        Assert.AreEqual("{command}", instrumentUnits["camera_agent.capture_control.admission.commands"]);
        foreach (var expected in ExpectedMetricUnits)
        {
            Assert.AreEqual(expected.Value, instrumentUnits[expected.Key], $"Unexpected unit for {expected.Key}.");
        }

        CollectionAssert.AreEquivalent(
            ExpectedMetricUnits.Keys.ToArray(),
            measurements.Select(static sample => sample.Name).Distinct(StringComparer.Ordinal).ToArray());
        Assert.IsTrue(measurements.All(static sample => double.IsFinite(sample.Value) && sample.Value >= 0));
        foreach (var sample in measurements)
        {
            CollectionAssert.AreEquivalent(ExpectedMetricTags[sample.Name], sample.Tags.Keys.ToArray());
        }

        AssertBoundedTags(measurements);
        AssertValues(measurements, "camera_agent.capture_control.cycles", 1, 1);
        AssertValues(measurements, "camera_agent.capture_control.decisions", 1, 1);
        AssertValues(measurements, "camera_agent.capture_control.metering.samples", 4, 4);
        AssertValues(measurements, "camera_agent.capture_control.metering.scanned", 8, 8);
        AssertValues(measurements, "camera_agent.capture_control.start_jitter", 0, 2);
        AssertValues(measurements, "camera_agent.capture_control.cycle.duration", 4, 4);
        AssertValues(measurements, "camera_agent.capture_control.metering.duration", 0, 0);
        AssertValues(measurements, "camera_agent.capture_control.decision.duration", 0, 0);
        AssertSegmentValues(measurements, "exposure", 2, 2);
        AssertSegmentValues(measurements, "readout", 1, 1);
        AssertSegmentValues(measurements, "metering", 0, 0);
        AssertSegmentValues(measurements, "control", 0, 0);
        AssertSegmentValues(measurements, "setpoint", 0, 0);
        AssertSegmentValues(measurements, "module", 0, 0);
        AssertSegmentValues(measurements, "ingress", 1, 1);
        AssertSegmentValues(measurements, "host-gap", 0, 0);

        CollectionAssert.AreEquivalent(
            ExpectedActivities,
            activities.ToArray());
        CollectionAssert.Contains(logger.EventIds.ToArray(), 2073);
        CollectionAssert.Contains(logger.EventIds.ToArray(), 2075);
    }

    [TestMethod]
    public async Task RunAsync_UnsupportedMeteringFormatEmitsEvent2074()
    {
        var timeProvider = new ManualTimeProvider(StartUtc);
        using var cancellation = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var runner = new CameraModuleRunner(
            new DeterministicModule(timeProvider, CameraPixelFormat.Mono8),
            new DeterministicHostContext(
                CreateConfig(CameraPixelFormat.Mono8, CaptureCadenceMode.Continuous),
                timeProvider,
                cancellation,
                publishTarget: 1,
                TimeSpan.Zero),
            timeProvider,
            logger,
            new ZenithSunEphemeris());

        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        CollectionAssert.Contains(logger.EventIds.ToArray(), 2073);
        CollectionAssert.Contains(logger.EventIds.ToArray(), 2074);
    }

    [TestMethod]
    public void RecordCycle_FailedSetpointAttemptEmitsSegmentAndReconcilesHostGap()
    {
        var measurements = new List<MetricSample>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CaptureControlTelemetry.MeterName &&
                    instrument.Name == "camera_agent.capture_control.segment.duration")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new MetricSample(instrument.Name, value, ReadTags(tags))));
        listener.Start();
        using var telemetry = new CaptureControlTelemetry();
        var evidence = new CaptureCycleEvidence(
            CaptureCadenceMode.Continuous,
            CaptureStartReason.Initial,
            AutomaticControlOwnership.CameraNative,
            AutomaticControlOwnership.Disabled,
            null,
            StartUtc,
            null,
            null,
            new CaptureControlDecisionEvidence(
                StartUtc.AddSeconds(2),
                StartUtc.AddSeconds(3),
                TimeSpan.FromSeconds(1),
                10,
                TimeSpan.FromSeconds(2),
                10,
                CaptureControlDecisionReason.SetpointApplicationFailed),
            StartUtc.AddSeconds(6));

        telemetry.RecordCycle(
            evidence,
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(1));

        AssertSegmentValues(measurements, "module", 2);
        AssertSegmentValues(measurements, "control", 1);
        AssertSegmentValues(measurements, "setpoint", 3);
        AssertSegmentValues(measurements, "ingress", 1);
        AssertSegmentValues(measurements, "host-gap", 3);
        Assert.AreEqual(10, measurements.Sum(static sample => sample.Value));
    }

    private static void AssertBoundedTags(IEnumerable<MetricSample> measurements)
    {
        var allowedValues = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["cadence"] = new([CaptureCadenceMode.MinimumStartInterval.ToString()], StringComparer.Ordinal),
            ["reason"] = new([CaptureControlDecisionReason.ExposureAdjusted.ToString()], StringComparer.Ordinal),
            ["regime"] = new([CaptureSolarRegime.Day.ToString()], StringComparer.Ordinal),
            ["outcome"] = new([CaptureMeteringOutcome.Measured.ToString()], StringComparer.Ordinal),
            ["segment"] = new(
                ["exposure", "readout", "metering", "control", "setpoint", "module", "ingress", "host-gap"],
                StringComparer.Ordinal)
        };
        foreach (var tag in measurements.SelectMany(static sample => sample.Tags))
        {
            Assert.IsTrue(allowedValues.TryGetValue(tag.Key, out var values));
            Assert.IsTrue(values.Contains(tag.Value), $"Unexpected {tag.Key} tag value '{tag.Value}'.");
        }
    }

    private static void AssertValues(
        IEnumerable<MetricSample> measurements,
        string name,
        params double[] expected)
        => CollectionAssert.AreEquivalent(
            expected,
            measurements.Where(sample => sample.Name == name).Select(static sample => sample.Value).ToArray(),
            $"Unexpected measurements for {name}.");

    private static void AssertSegmentValues(
        IEnumerable<MetricSample> measurements,
        string segment,
        params double[] expected)
        => CollectionAssert.AreEquivalent(
            expected,
            measurements
                .Where(sample => sample.Name == "camera_agent.capture_control.segment.duration" &&
                    sample.Tags["segment"] == segment)
                .Select(static sample => sample.Value)
                .ToArray(),
            $"Unexpected measurements for segment {segment}.");

    private static Dictionary<string, string> ReadTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            result[tag.Key] = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        return result;
    }

    private static CameraModuleConfig CreateConfig(CameraPixelFormat pixelFormat, CaptureCadenceMode cadenceMode)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("TelemetryTest"),
            new CameraRigConfig(
                new SensorProfile(
                    "TelemetryTest",
                    2,
                    2,
                    1,
                    SensorColorMode.Mono,
                    pixelFormat,
                    StrideBytes: pixelFormat == CameraPixelFormat.Mono16 ? 4 : 2),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(4),
                    10,
                    40,
                    new ExposureEnvelope(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(8),
                        1,
                        100,
                        new ExposureDefaults(TimeSpan.FromSeconds(1), 10),
                        new ExposureDefaults(TimeSpan.FromSeconds(4), 40),
                        0.5,
                        Hysteresis: 0.01,
                        AdjustmentFactor: 2,
                        GainStep: 10),
                    CadenceMode: cadenceMode),
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.HostMetered,
                    GainControl = AutomaticControlOwnership.Disabled,
                    Metering = new CaptureMeteringPolicy
                    {
                        XStride = 1,
                        YStride = 1,
                        UseImageCircle = false,
                        SaturationFraction = 0.98
                    },
                    SolarRegimes = new CaptureSolarRegimePolicy
                    {
                        DayAltitudeThresholdDegrees = 0,
                        NightAltitudeThresholdDegrees = -12
                    }
                }),
            CapturePipelineConfig.Empty);

    private sealed record MetricSample(string Name, double Value, Dictionary<string, string> Tags);

    private sealed class DeterministicModule(
        ManualTimeProvider timeProvider,
        CameraPixelFormat pixelFormat) : ICameraModule, ICameraSetpointController
    {
        public string Id => "telemetry-test";
        public string DisplayName => "Telemetry Test";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            var exposureStartedUtc = timeProvider.GetUtcNow();
            timeProvider.Advance(TimeSpan.FromSeconds(3));
            var pixels = pixelFormat == CameraPixelFormat.Mono16
                ? new byte[] { 0xE8, 0x03, 0xE8, 0x03, 0xE8, 0x03, 0xE8, 0x03 }
                : new byte[] { 10, 10, 10, 10 };
            var frame = new CameraFrame(
                exposureStartedUtc,
                2,
                2,
                pixelFormat,
                pixels,
                new FrameMetadata(
                    request.RequestedSetpoint!.Exposure,
                    request.RequestedSetpoint.Gain,
                    0,
                    Extra: new Dictionary<string, string>
                    {
                        ["blackLevelAdu"] = "0",
                        ["whiteLevelAdu"] = "10000"
                    }),
                pixelFormat == CameraPixelFormat.Mono16 ? 4 : 2);
            var result = new CaptureResult(
                frame,
                request.RequestedSetpoint,
                TimeSpan.Zero,
                CaptureMode.Still,
                false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    exposureStartedUtc,
                    exposureStartedUtc + TimeSpan.FromSeconds(2),
                    exposureStartedUtc + TimeSpan.FromSeconds(3))
            };
            return Task.FromResult(result);
        }

        public ValueTask<DateTimeOffset> ApplySetpointAsync(
            CaptureSetpoint setpoint,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(timeProvider.GetUtcNow());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeterministicHostContext(
        CameraModuleConfig configuration,
        ManualTimeProvider timeProvider,
        CancellationTokenSource cancellation,
        int publishTarget,
        TimeSpan ingressDuration) : ICaptureHostContext
    {
        private int _published;

        public CameraModuleConfig Configuration => configuration;

        public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            timeProvider.Advance(ingressDuration);
            if (++_published == publishTarget)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ZenithSunEphemeris : IPlanetEphemeris
    {
        public string ModelVersion => "zenith-sun-test-v1";

        public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
        {
            Assert.AreEqual(SolarSystemBody.Sun, body);
            return new SolarSystemPosition(new EquatorialPoint(18.697374558, 0), -26.74);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = startUtc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
            _timestamp += amount.Ticks;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<int> EventIds { get; } = [];

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
