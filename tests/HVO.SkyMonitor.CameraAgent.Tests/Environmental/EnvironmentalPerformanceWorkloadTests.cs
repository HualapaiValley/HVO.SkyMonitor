using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalPerformanceWorkloadTests
{
    // These ordered values define the historical #209 v1 workload, independently of later enum additions.
    private static readonly (EnvironmentalObservationKind Kind, double? Numeric, bool? Boolean)[] OriginalReadings =
    [
        (EnvironmentalObservationKind.AirTemperature, 12.5, null),
        (EnvironmentalObservationKind.RelativeHumidity, 42, null),
        (EnvironmentalObservationKind.AtmosphericPressure, 85_000, null),
        (EnvironmentalObservationKind.WindSpeed, 4, null),
        (EnvironmentalObservationKind.WindDirection, 250, null),
        (EnvironmentalObservationKind.WindGust, 7, null),
        (EnvironmentalObservationKind.PrecipitationRate, 0, null),
        (EnvironmentalObservationKind.RainState, null, false),
        (EnvironmentalObservationKind.SkyBrightness, 21.2, null),
        (EnvironmentalObservationKind.SkyQuality, 20.8, null),
        (EnvironmentalObservationKind.CloudCover, 0.2, null),
        (EnvironmentalObservationKind.CameraSensorTemperature, -10, null)
    ];

    [TestMethod]
    public async Task LegacyVirtualSourcesKeepTheOriginalOrderedKindsAndBaselines()
    {
        var sources = EnvironmentalAcquisitionPerformanceTests.CreateVirtualSources(
            "legacy-workload", [EnvironmentalAcquisitionTrigger.Periodic]);
        CollectionAssert.AreEqual(OriginalReadings.Select(static item => item.Kind).ToArray(),
            sources.Select(static source => source.Descriptor.Kind).ToArray());
        foreach (var (kind, numeric, boolean) in OriginalReadings)
        {
            Assert.AreEqual((numeric, boolean), EnvironmentalAcquisitionPerformanceTests.Baseline(kind));
            var source = sources.Single(item => item.Descriptor.Kind == kind);
            var context = EnvironmentalHardwareAdapterTests.Context() with
            {
                Trigger = EnvironmentalAcquisitionTrigger.Periodic,
                ObservedAtUtc = source.Descriptor.ScheduleEpochUtc
            };
            var acquired = await source.AcquireAsync(context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalSourceAcquisitionOutcome.Produced, acquired.Outcome);
            Assert.IsNotNull(acquired.Fact);
            Assert.AreEqual(kind, acquired.Fact.Value.Kind);
            Assert.IsTrue(EnvironmentalObservationFactJson.Validate(acquired.Fact).IsValid);
        }
    }

    [TestMethod]
    public void LegacyTrackingSourcesKeepTheOriginalOrderedKindsAndBaselines()
    {
        var configurations = EnvironmentalAcquisitionPerformanceTests.CreateTrackingSourceConfigurations("legacy-workload");
        CollectionAssert.AreEqual(OriginalReadings.Select(static item => item.Kind).ToArray(),
            configurations.Select(static configuration => configuration.Kind).ToArray());
        foreach (var (kind, numeric, boolean) in OriginalReadings)
        {
            Assert.AreEqual((numeric, boolean), EnvironmentalAcquisitionPerformanceTests.Baseline(kind));
            var configuration = configurations.Single(item => item.Kind == kind);
            if (numeric is { } numericValue)
                Assert.AreEqual(numericValue, configuration.Options.GetProperty("numericValue").GetDouble());
            if (boolean is { } booleanValue)
                Assert.AreEqual(booleanValue, configuration.Options.GetProperty("booleanValue").GetBoolean());
        }
    }
}
