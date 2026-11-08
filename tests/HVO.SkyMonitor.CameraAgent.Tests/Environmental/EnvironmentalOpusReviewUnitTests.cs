using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalOpusReviewUnitTests
{
    [TestMethod]
    public async Task DistinctInfraredInputsKeepDistinctDerivedIdentitiesWhenCloudValuesAreClipped()
    {
        var device = new ChangingInfraredDevice();
        var publisher = new EnvironmentalHardwareAdapterTests.RecordingPublisher();
        var source = new InfraredSkySource(EnvironmentalHardwareAdapterTests.Descriptor("InfraredSky", EnvironmentalObservationKind.CloudCover),
            new EnvironmentalDeviceOptions("ir"), device, publisher);
        var first = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        var second = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(first.Fact);
        Assert.IsNotNull(second.Fact);
        Assert.AreEqual(first.Fact.Value.NumericValue, second.Fact.Value.NumericValue);
        Assert.AreNotEqual(first.Fact.Lineage[0].ObservationId, second.Fact.Lineage[0].ObservationId);
        Assert.AreNotEqual(first.Fact.ObservationId, second.Fact.ObservationId);
    }

    [TestMethod]
    [DataRow(false, 0, "station")]
    [DataRow(false, 1, "provider")]
    [DataRow(true, 0, "provider")]
    public void WeatherPriorityUsesHalfOpenExposureFreshnessAndStrictPointFreshness(bool point, int afterTicks, string expected)
    {
        var epoch = EnvironmentalHardwareAdapterTests.Epoch;
        var station = EnvironmentalAdapterReviewUnitTests.Fact("station", EnvironmentalObservationKind.AirTemperature, 20)
            with
        { StaleAfterUtc = epoch.AddSeconds(30) };
        var provider = EnvironmentalAdapterReviewUnitTests.Fact("provider", station.Value.Kind, 21, EnvironmentalObservationSourceKind.Imported);
        var options = new EnvironmentalAcquisitionOptions
        {
            Sources = [EnvironmentalAdapterReviewUnitTests.Configuration("station", station.Value.Kind),
            EnvironmentalAdapterReviewUnitTests.Configuration("provider", station.Value.Kind, "OnlineWeather")],
            WeatherSourcePriority = ["station", "provider"]
        };
        var through = station.StaleAfterUtc.AddTicks(afterTicks);
        var selected = EnvironmentalReadingSelection.Select([Record(station), Record(provider)], options, [], station.Value.Kind,
            point ? through : epoch, through);
        Assert.HasCount(1, selected);
        Assert.AreEqual(expected, selected[0].Fact.Source.SourceId);
    }

    [TestMethod]
    public async Task SqmFrequencyModeHasAnExplicitUnavailableReason()
    {
        var source = new SkyQualityMeterSource(EnvironmentalHardwareAdapterTests.Descriptor("SkyQualityMeter", EnvironmentalObservationKind.SkyQuality),
            EnvironmentalHardwareAdapterTests.Options(EnvironmentalHardwareAdapterTests.Sqm.Replace("21.10m", "8.00m", StringComparison.Ordinal)
                .Replace("0000001.000s", "0000000.000s", StringComparison.Ordinal)));
        var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(result.Fact);
        Assert.AreEqual("sqm-frequency-mode", result.Reason);
    }

    private static LocalEnvironmentalObservationRecord Record(EnvironmentalObservationFactV1 fact)
        => new(1, fact, EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(fact),
            EnvironmentalObservationFactJson.ComputeSourceContentSha256(fact), EnvironmentalObservationFactJson.ComputeContentSha256(fact),
            EnvironmentalObservationFactJson.Serialize(fact).Length, fact.ObservedAtUtc);

    private sealed class ChangingInfraredDevice : IEnvironmentalDevice
    {
        private int calls;
        public ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalDeviceSample(System.Text.Encoding.UTF8.GetBytes(++calls == 1
                ? EnvironmentalHardwareAdapterTests.Infrared : EnvironmentalHardwareAdapterTests.Infrared.Replace("-20", "-21", StringComparison.Ordinal)),
                receivedAtUtc, true, ReceivedAtUtc: receivedAtUtc));
    }
}
