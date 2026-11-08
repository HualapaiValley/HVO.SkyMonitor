using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class PresentationMetadataFactsBuilderTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task OverlayRetainsAgeSourceAndMixedLabelsWithinTheLineLimit(bool legacyVirtualSource, bool longIdentity)
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var station = Fact(Guid.NewGuid(), EnvironmentalObservationKind.AirTemperature, 20, sourceId: "station");
        station = station with
        {
            SchemaVersion = EnvironmentalObservationSchemaVersions.V3,
            Source = station.Source with
            {
                Provider = longIdentity ? new string('P', 128) : "LocalWeatherStation",
                SourceId = longIdentity ? new string('S', 128) : "station"
            }
        };
        var virtualHumidity = Fact(Guid.NewGuid(), EnvironmentalObservationKind.RelativeHumidity, 45, sourceId: "virtual");
        virtualHumidity = virtualHumidity with
        {
            SchemaVersion = legacyVirtualSource ? EnvironmentalObservationSchemaVersions.V1 : EnvironmentalObservationSchemaVersions.V3,
            Source = virtualHumidity.Source with { Provider = "VirtualEnvironment", Kind = EnvironmentalObservationSourceKind.Simulated }
        };
        await CommitAsync(store, station).ConfigureAwait(false);
        await CommitAsync(store, virtualHumidity).ConfigureAwait(false);
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        var kinds = new[] { EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RelativeHumidity };
        await fixture.Associations.AssociateAsync(fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, kinds, CancellationToken.None).ConfigureAwait(false);
        fixture.Context.BeginNode("environment-labels", []);
        var facts = await fixture.Builder.BuildAsync(fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture),
            kinds, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(facts);
        var layer = PresentationLayerProducers.FromMetadataFacts(facts.Corners, 1936, 1216);
        var lines = layer.TextBlocks.Single(block => block.Anchor == PresentationTextAnchor.BottomLeft).Lines;
        Assert.IsTrue(lines.Any(line => line == "MIXED SOURCES"));
        var stationLine = lines.Single(line => line.Contains("MEASURED", StringComparison.Ordinal));
        Assert.IsTrue(stationLine.Contains("SRC ", StringComparison.Ordinal));
        if (longIdentity) Assert.IsTrue(stationLine.Contains('…', StringComparison.Ordinal));
        else Assert.IsTrue(stationLine.Contains("SRC LocalWeatherStation/station", StringComparison.Ordinal));
        Assert.IsTrue(stationLine.Contains("AGE ", StringComparison.Ordinal));
        Assert.IsTrue(stationLine.Contains("MEASURED", StringComparison.Ordinal));
        Assert.IsTrue(lines.All(line => line.Length <= PresentationLayerPayloadV1.MaximumLineCharacters));
        Assert.HasCount(2, facts.Environment);
    }
}
