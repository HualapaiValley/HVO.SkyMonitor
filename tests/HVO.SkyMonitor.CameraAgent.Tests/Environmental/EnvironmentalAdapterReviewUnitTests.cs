using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalAdapterReviewUnitTests
{
    [TestMethod]
    [DataRow(200)]
    [DataRow(429)]
    public async Task ProviderWireBudgetIsSharedAcrossDifferentDisplayMetadata(int status)
    {
        using var handler = new OnlineWeatherSourceTests.FakeHandler(_ => OnlineWeatherSourceTests.Response(status, Payload));
        using var client = new WeatherProviderClient(new HttpClient(handler), new OnlineWeatherSourceTests.Clock());
        var descriptor = OnlineWeatherSourceTests.Create(client).Descriptor;
        var first = new OnlineWeatherSource(descriptor, new(WeatherProvider.Nws, "KIGM", StationDistanceKilometers: 12), client);
        var second = new OnlineWeatherSource(descriptor with { Id = "humidity", Kind = EnvironmentalObservationKind.RelativeHumidity },
            new(WeatherProvider.Nws, "KIGM", StationElevationMeters: 1000), client);
        var a = await first.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        var c = await second.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, handler.Calls);
        if (status == 200)
        {
            Assert.IsNotNull(a.Fact);
            Assert.IsNotNull(c.Fact);
            Assert.AreEqual(12, a.Fact.Source.Provenance.Parameters.GetProperty("stationDistanceKilometers").GetDouble());
            Assert.AreEqual(1000, c.Fact.Source.Provenance.Parameters.GetProperty("stationElevationMeters").GetDouble());
        }
        else Assert.AreEqual("provider-rate-limited", c.Reason);
    }

    [TestMethod]
    [DataRow("LocalWeatherStation", 0, true)]
    [DataRow("LocalWeatherStation", 60, false)]
    [DataRow("InfraredSky", 0, true)]
    [DataRow("InfraredSky", 60, false)]
    [DataRow("SkyQualityMeter", 0, true)]
    [DataRow("SkyQualityMeter", 60, false)]
    public async Task PhysicalFreshnessUsesReceiptTimeRatherThanTheScheduledSlot(string type, int ageSeconds, bool produced)
    {
        var descriptor = EnvironmentalHardwareAdapterTests.Source(type).Descriptor with
        { PeriodSeconds = 300, ValidForSeconds = 120, StaleAfterSeconds = 45 };
        var payload = type == "LocalWeatherStation" ? EnvironmentalHardwareAdapterTests.Station :
            type == "InfraredSky" ? EnvironmentalHardwareAdapterTests.Infrared : EnvironmentalHardwareAdapterTests.Sqm;
        var device = new PhysicalDevice(payload, ageSeconds);
        var options = new EnvironmentalDeviceOptions("device");
        IEnvironmentalSource source = type switch
        {
            "LocalWeatherStation" => new LocalWeatherStationSource(descriptor, options, device),
            "InfraredSky" => new InfraredSkySource(descriptor, options, device),
            _ => new SkyQualityMeterSource(descriptor, options, device)
        };
        var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with
        { ObservedAtUtc = EnvironmentalHardwareAdapterTests.Epoch.AddSeconds(-180) }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(produced, result.Fact is not null, result.Reason);
    }

    [TestMethod]
    public void FailedSourceDoesNotOwnAnotherSourceWithTheSamePrefix()
    {
        var fact = Fact("station-temperature", EnvironmentalObservationKind.AirTemperature, 20);
        var record = Record(fact);
        var options = new EnvironmentalAcquisitionOptions { Sources = [Configuration("station", EnvironmentalObservationKind.RelativeHumidity), Configuration("station-temperature", fact.Value.Kind)] };
        var states = new[] { new EnvironmentalSourceRuntimeState("station", EnvironmentalObservationKind.RelativeHumidity, false,
            EnvironmentalAcquisitionDisposition.Failed, "device-disconnected", null, null, null, Epoch, Epoch, 1, null) };
        Assert.HasCount(1, EnvironmentalReadingSelection.Select([record], options, states, fact.Value.Kind, Epoch, Epoch.AddSeconds(1)));
    }

    internal static readonly DateTimeOffset Epoch = EnvironmentalHardwareAdapterTests.Epoch;
    internal const string Payload = """{"properties":{"timestamp":"2026-11-08T06:00:00Z","temperature":{"value":20,"unitCode":"wmoUnit:degC"},"relativeHumidity":{"value":40,"unitCode":"wmoUnit:percent"}}}""";
    internal static EnvironmentalSourceConfiguration Configuration(string id, EnvironmentalObservationKind kind, string type = "LocalWeatherStation")
        => new() { Id = id, Kind = kind, Type = type, Required = false, Triggers = [EnvironmentalAcquisitionTrigger.Periodic] };
    internal static EnvironmentalObservationFactV1 Fact(string id, EnvironmentalObservationKind kind, double numeric,
        EnvironmentalObservationSourceKind nature = EnvironmentalObservationSourceKind.Measured)
    {
        var parameters = JsonSerializer.SerializeToElement(new { });
        return new(EnvironmentalObservationSchemaVersions.V3, Guid.NewGuid(), new("fixture", id, "fixture-v1", nature,
            new(new("fixture", "v1"), parameters, HVO.SkyMonitor.AgentCore.CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            Epoch, null, null, Epoch, Epoch.AddSeconds(900), Epoch.AddSeconds(300),
            new(kind, kind == EnvironmentalObservationKind.RelativeHumidity ? EnvironmentalObservationUnit.Percent : EnvironmentalObservationUnit.DegreesCelsius,
                numeric, null, EnvironmentalObservationQuality.Good), []);
    }
    private static LocalEnvironmentalObservationRecord Record(EnvironmentalObservationFactV1 fact)
        => new(1, fact, EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(fact), EnvironmentalObservationFactJson.ComputeSourceContentSha256(fact),
            EnvironmentalObservationFactJson.ComputeContentSha256(fact), EnvironmentalObservationFactJson.Serialize(fact).Length, Epoch);
    private sealed class PhysicalDevice(string payload, int ageSeconds) : IEnvironmentalDevice
    {
        public ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalDeviceSample(Encoding.UTF8.GetBytes(payload), Epoch.AddSeconds(-ageSeconds), false, ReceivedAtUtc: Epoch));
    }
}
