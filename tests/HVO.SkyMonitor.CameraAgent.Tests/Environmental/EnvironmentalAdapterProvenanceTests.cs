using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalAdapterProvenanceTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    [TestMethod]
    [DataRow("LocalWeatherStation")]
    [DataRow("InfraredSky")]
    [DataRow("SkyQualityMeter")]
    public async Task ParametersContainOnlyTheAdaptersOwnFields(string type)
    {
        var fact = (await EnvironmentalHardwareAdapterTests.Source(type).AcquireAsync(
            EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false)).Fact!;
        var expected = new List<string> { "protocol", "deviceGroup", "acquisitionSourceId", "readingRole" };
        if (type == "InfraredSky") expected.AddRange(["modelVersion", "clearDeltaCelsius", "cloudyDeltaCelsius"]);
        CollectionAssert.AreEquivalent(expected.ToArray(), fact.Source.Provenance.Parameters.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual("environmental-adapter", fact.Source.Provenance.Method.Name);
        Assert.AreEqual("adapter-v1", fact.Source.Provenance.Method.Version);
    }

    [TestMethod]
    [DataRow("LocalWeatherStation")]
    [DataRow("SkyQualityMeter")]
    public async Task UnrelatedInfraredSettingsDoNotChangeSourceIdentity(string type)
    {
        var original = EnvironmentalHardwareAdapterTests.Source(type);
        var payload = type == "LocalWeatherStation" ? EnvironmentalHardwareAdapterTests.Station : EnvironmentalHardwareAdapterTests.Sqm;
        var settings = EnvironmentalHardwareAdapterTests.Options(payload) with
        { ModelVersion = "unrelated-model", ClearDeltaCelsius = -40, CloudyDeltaCelsius = 0 };
        IEnvironmentalSource changed = type == "LocalWeatherStation"
            ? new LocalWeatherStationSource(original.Descriptor, settings)
            : new SkyQualityMeterSource(original.Descriptor, settings);
        var before = (await original.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false)).Fact!;
        var after = (await changed.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false)).Fact!;
        CollectionAssert.AreEqual(EnvironmentalObservationFactJson.Serialize(before), EnvironmentalObservationFactJson.Serialize(after));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InfraredSourceVersionAndParametersMatchTheOriginalAlgorithm(bool simulated)
    {
        var source = new InfraredSkySource(EnvironmentalHardwareAdapterTests.Descriptor("InfraredSky", EnvironmentalObservationKind.CloudCover),
            simulated ? EnvironmentalHardwareAdapterTests.Options(EnvironmentalHardwareAdapterTests.Infrared) : new("virtual-device"),
            simulated ? null : new Device(), new EnvironmentalHardwareAdapterTests.RecordingPublisher());
        var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        var expected = LegacyParameters(source.Descriptor, EnvironmentalHardwareAdapterTests.Options(EnvironmentalHardwareAdapterTests.Infrared));
        Assert.AreEqual(expected.GetRawText(), result.Fact!.Source.Provenance.Parameters.GetRawText());
        Assert.AreEqual(LegacySourceVersion(expected, simulated), result.Fact.Source.Version);
    }

    internal static JsonElement LegacyParameters(EnvironmentalSourceDescriptor descriptor, EnvironmentalDeviceOptions options)
        => CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            protocol = descriptor.Type == "SkyQualityMeter" ? "sqm-rx-v1" : "environmental-bridge-v1",
            deviceGroup = options.DeviceId,
            acquisitionSourceId = descriptor.Id,
            readingRole = "primary",
            options.ModelVersion,
            options.ClearDeltaCelsius,
            options.CloudyDeltaCelsius
        }, SerializerOptions));

    internal static string LegacySourceVersion(JsonElement parameters, bool simulated)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        { Algorithm = "environmental-adapter-v1", Simulated = simulated, ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(parameters) });

    private sealed class Device : IEnvironmentalDevice
    {
        public ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalDeviceSample(System.Text.Encoding.UTF8.GetBytes(EnvironmentalHardwareAdapterTests.Infrared),
                receivedAtUtc, false, ReceivedAtUtc: receivedAtUtc));
    }
}
