using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalHardwareAdapterTests
{
    internal static readonly DateTimeOffset Epoch = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);
    internal const string Station = """{"temperature":{"value":68,"unit":"F","uncertainty":0.9},"humidity":{"value":40,"unit":"%"},"pressure":{"value":900,"unit":"hPa"},"windSpeed":{"value":10,"unit":"mph"},"windDirection":{"value":180,"unit":"deg"},"windGust":{"value":12,"unit":"mph"},"rainRate":{"value":0.1,"unit":"in/h"},"rain":{"value":true,"unit":"bool"}}""";
    internal const string Infrared = """{"skyCelsius":-20,"ambientCelsius":10,"rain":false,"drying":false,"brightness":20.5}""";
    internal const string Sqm = "r, 21.10m,0000000001Hz,0000000002c,0000001.000s, 15.0C";

    [TestMethod]
    [DataRow(EnvironmentalObservationKind.AirTemperature, 20, EnvironmentalObservationUnit.DegreesCelsius)]
    [DataRow(EnvironmentalObservationKind.RelativeHumidity, 40, EnvironmentalObservationUnit.Percent)]
    [DataRow(EnvironmentalObservationKind.AtmosphericPressure, 90000, EnvironmentalObservationUnit.Pascals)]
    [DataRow(EnvironmentalObservationKind.WindSpeed, 4.4704, EnvironmentalObservationUnit.MetersPerSecond)]
    [DataRow(EnvironmentalObservationKind.WindDirection, 180, EnvironmentalObservationUnit.DegreesTrue)]
    [DataRow(EnvironmentalObservationKind.WindGust, 5.36448, EnvironmentalObservationUnit.MetersPerSecond)]
    [DataRow(EnvironmentalObservationKind.PrecipitationRate, 2.54, EnvironmentalObservationUnit.MillimetersPerHour)]
    public async Task StationConvertsCanonicalUnits(EnvironmentalObservationKind kind, double expected, EnvironmentalObservationUnit unit)
    {
        var result = await StationSource(kind).AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalSourceAcquisitionOutcome.Produced, result.Outcome);
        Assert.AreEqual(expected, result.Fact!.Value.NumericValue!.Value, 0.00001);
        Assert.AreEqual(unit, result.Fact.Value.Unit);
        Assert.AreEqual(EnvironmentalObservationSourceKind.Simulated, result.Fact.Source.Kind);
        Assert.IsTrue(EnvironmentalObservationFactJson.Validate(result.Fact).IsValid);
        if (kind == EnvironmentalObservationKind.AirTemperature)
            Assert.AreEqual(0.5, result.Fact.Value.Uncertainty!.Value, 0.00001);
    }

    [TestMethod]
    [DataRow(SimulatedEnvironmentalDeviceMode.Disconnected)]
    [DataRow(SimulatedEnvironmentalDeviceMode.Missing)]
    [DataRow(SimulatedEnvironmentalDeviceMode.Stale)]
    [DataRow(SimulatedEnvironmentalDeviceMode.Faulted)]
    [DataRow(SimulatedEnvironmentalDeviceMode.ClockSkewed)]
    [DataRow(SimulatedEnvironmentalDeviceMode.OutOfRange)]
    public async Task EveryHardwareFamilyRejectsFaults(SimulatedEnvironmentalDeviceMode mode)
    {
        foreach (var type in new[] { "LocalWeatherStation", "InfraredSky", "SkyQualityMeter" })
        {
            var source = Source(type, mode: mode);
            var result = await source.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(EnvironmentalSourceAcquisitionOutcome.Produced, result.Outcome, type);
            Assert.IsNull(result.Fact, type);
            Assert.IsTrue(result.Reason.Length < 64);
        }
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not json")]
    [DataRow("{\"temperature\":{\"value\":999,\"unit\":\"C\"}}")]
    [DataRow("{\"temperature\":{\"value\":12,\"unit\":\"bogus\"}}")]
    [DataRow("{\"temperature\":{\"value\":12,\"unit\":\"C\",\"fault\":true}}")]
    public async Task StationRejectsMissingMalformedRangeUnitAndSensorFault(string payload)
    {
        var result = await StationSource(EnvironmentalObservationKind.AirTemperature, payload).AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(result.Fact);
    }

    [TestMethod]
    public async Task StationDoesNotInventDirectionDuringCalmOrRainAfterCounterReset()
    {
        foreach (var (kind, payload) in new[]
        {
            (EnvironmentalObservationKind.WindDirection, Station.Replace("10,", "0,", StringComparison.Ordinal)),
            (EnvironmentalObservationKind.PrecipitationRate, Station.Replace("\"rainRate\":{", "\"rainRate\":{\"counterReset\":true,", StringComparison.Ordinal))
        })
            Assert.IsNull((await StationSource(kind, payload).AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false)).Fact);
    }

    [TestMethod]
    public async Task PhysicalSeamProducesMeasuredAndReconnectDoesNotReuseFailure()
    {
        var device = new ScriptedDevice();
        var source = new LocalWeatherStationSource(Descriptor("LocalWeatherStation", EnvironmentalObservationKind.AirTemperature), new EnvironmentalDeviceOptions("station"), device);
        Assert.IsNull((await source.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false)).Fact);
        var recovered = await source.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalObservationSourceKind.Measured, recovered.Fact!.Source.Kind);
        Assert.AreEqual(Epoch.AddSeconds(-1), recovered.Fact.ObservedAtUtc);
        Assert.AreEqual(Epoch, recovered.Fact.Value.Metadata!.DeviceReceivedAtUtc);
    }

    [TestMethod]
    public async Task InfraredPublishesRealLineageAndVersionedThresholds()
    {
        var publisher = new RecordingPublisher();
        var source = new InfraredSkySource(Descriptor("InfraredSky", EnvironmentalObservationKind.CloudCover), Options(Infrared), publisher: publisher);
        var result = await source.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, result.Fact!.Value.NumericValue);
        Assert.AreEqual(EnvironmentalObservationSourceKind.Derived, result.Fact.Source.Kind);
        Assert.HasCount(2, result.Fact.Lineage);
        Assert.HasCount(2, publisher.Facts);
        Assert.IsTrue(publisher.Facts.Any(f => f.Value.Kind == EnvironmentalObservationKind.SkyTemperature));
        Assert.IsTrue(result.Fact.Source.Provenance.Parameters.TryGetProperty("clearDeltaCelsius", out _));
        foreach (var reference in result.Fact.Lineage)
            Assert.IsTrue(publisher.Facts.Any(f => f.ObservationId == reference.ObservationId && EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(f) == reference.SourceIdentitySha256));
    }

    [TestMethod]
    [DataRow(-20, 0)]
    [DataRow(-5, 0.5)]
    [DataRow(10, 1)]
    public async Task InfraredClearAndCloudyModel(double sky, double expected)
    {
        var payload = JsonSerializer.Serialize(new { skyCelsius = sky, ambientCelsius = 10 });
        var source = new InfraredSkySource(Descriptor("InfraredSky", EnvironmentalObservationKind.CloudCover), Options(payload), publisher: new RecordingPublisher());
        Assert.AreEqual(expected, (await source.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false)).Fact!.Value.NumericValue);
    }

    [TestMethod]
    public async Task InfraredRainAndDryingAreDistinct()
    {
        var dry = new InfraredSkySource(Descriptor("InfraredSky", EnvironmentalObservationKind.RainState), Options(Infrared));
        var rain = new InfraredSkySource(dry.Descriptor, Options(Infrared.Replace("\"rain\":false", "\"rain\":true", StringComparison.Ordinal)));
        var drying = new InfraredSkySource(dry.Descriptor, Options(Infrared.Replace("\"drying\":false", "\"drying\":true", StringComparison.Ordinal)));
        Assert.IsFalse((await dry.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false)).Fact!.Value.BooleanValue);
        Assert.IsTrue((await rain.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false)).Fact!.Value.BooleanValue);
        Assert.AreEqual("wet-sensor-drying", (await drying.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false)).Reason);
    }

    [TestMethod]
    public async Task SqmPreservesIntegrationAndSensorTemperature()
    {
        var result = await Source("SkyQualityMeter").AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(21.1, result.Fact!.Value.NumericValue);
        Assert.AreEqual(Epoch.AddSeconds(-1), result.Fact.ObservedFromUtc);
        Assert.AreEqual(Epoch, result.Fact.ObservedThroughUtc);
        Assert.AreEqual(15, result.Fact.Value.Metadata!.SensorTemperatureCelsius);
    }

    [TestMethod]
    [DataRow("r, 0.00m,1Hz,1c,1.0s,15C")]
    [DataRow("r, 99.0m,1Hz,1c,1.0s,15C")]
    [DataRow("r, 21.1m,1Hz,1c,-1s,15C")]
    [DataRow("r, 21.1m,1Hz,1c,NaNs,15C")]
    [DataRow("garbage")]
    public async Task SqmRejectsSaturationRangeAndMalformedResponses(string payload)
    {
        var source = new SkyQualityMeterSource(Descriptor("SkyQualityMeter", EnvironmentalObservationKind.SkyQuality), Options(payload));
        Assert.IsNull((await source.AcquireAsync(Context(), CancellationToken.None).ConfigureAwait(false)).Fact);
    }

    [TestMethod]
    public async Task EveryHardwareFamilyHonorsTimeoutCancellation()
    {
        foreach (var type in new[] { "LocalWeatherStation", "InfraredSky", "SkyQualityMeter" })
        {
            using var cancellation = new CancellationTokenSource(20);
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await Source(type, mode: SimulatedEnvironmentalDeviceMode.Timeout).AcquireAsync(Context(), cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    internal static IEnvironmentalSource Source(string type, SimulatedEnvironmentalDeviceMode mode = SimulatedEnvironmentalDeviceMode.Normal)
        => type switch
        {
            "LocalWeatherStation" => StationSource(EnvironmentalObservationKind.AirTemperature, mode: mode),
            "InfraredSky" => new InfraredSkySource(Descriptor(type, EnvironmentalObservationKind.SkyTemperature), Options(Infrared, mode)),
            _ => new SkyQualityMeterSource(Descriptor(type, EnvironmentalObservationKind.SkyQuality), Options(Sqm, mode))
        };
    internal static EnvironmentalDeviceOptions Options(string payload, SimulatedEnvironmentalDeviceMode mode = SimulatedEnvironmentalDeviceMode.Normal)
        => new("virtual-device", new SimulatedEnvironmentalDeviceOptions(payload, mode));
    private static LocalWeatherStationSource StationSource(EnvironmentalObservationKind kind, string payload = Station, SimulatedEnvironmentalDeviceMode mode = SimulatedEnvironmentalDeviceMode.Normal)
        => new(Descriptor("LocalWeatherStation", kind), Options(payload, mode));
    internal static EnvironmentalSourceDescriptor Descriptor(string type, EnvironmentalObservationKind kind)
        => new(type + "-" + kind, type, kind, false, [EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionTrigger.OnDemand], Epoch.AddDays(-1), 60, 1, 900, 300, null, JsonSerializer.SerializeToElement(new { }));
    internal static EnvironmentalSourceAcquisitionContext Context()
        => new(EnvironmentalAcquisitionTrigger.Periodic, Epoch, DeploymentLocationSnapshot.Create("site", 1, "test", null, Epoch.AddDays(-1), null, 35, -113, 520, "America/Phoenix"));
    internal sealed class RecordingPublisher : IEnvironmentalObservationPublisher
    {
        internal List<EnvironmentalObservationFactV1> Facts { get; } = [];
        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
        {
            Facts.Add(fact);
            return ValueTask.FromResult(new EnvironmentalObservationPublishResult(EnvironmentalObservationPublishDisposition.Enqueued, null));
        }
    }
    private sealed class ScriptedDevice : IEnvironmentalDevice
    {
        private int calls;
        public ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalDeviceSample(Encoding.UTF8.GetBytes(Station), Epoch.AddSeconds(-1), false, ++calls == 1 ? EnvironmentalDeviceStatus.Disconnected : EnvironmentalDeviceStatus.Ready, ReceivedAtUtc: Epoch));
    }
}
