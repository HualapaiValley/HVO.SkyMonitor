using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class HorizonVisibleSceneTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(35, -114, 1200);
    private static readonly CatalogMetadata Metadata = new("synthetic", "1", new Uri("https://example.test/catalog"), "fixture");

    [TestMethod]
    [DataRow("projection")]
    [DataRow("observer")]
    public async Task RejectsAnEvaluatorForAnotherProjectionOrObserver(string mismatch)
    {
        var rig = Rig();
        var evaluator = new HorizonObstructionEvaluator(Profile(rig, "clear"), rig,
            mismatch == "observer" ? Observer with { ElevationMeters = 1300 } : Observer);
        var projection = RigProjectionContextFactory.Create(rig);
        var request = new VisibleSceneRequest(Epoch, Observer,
            mismatch == "projection" ? projection with { PrincipalPointX = 63 } : projection, new CatalogQuery(6, 1), Metadata);
        var builder = new VisibleSceneBuilder(new InMemoryCelestialCatalog([]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await builder.BuildWithHorizonAsync(request, evaluator).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("blocked")]
    [DataRow("unknown")]
    [DataRow("clear")]
    public async Task TopologyEndpointStarsAndBodiesRespectEligibilityOutsideTheNormalStarSelection(string mode)
    {
        var rig = Rig();
        EquatorialPoint Position(double altitude) => EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new(altitude, 45), Epoch, 35, -114), Epoch);
        var bright = Position(40);
        var faint = Position(80);
        var catalog = new InMemoryCelestialCatalog([
            new("bright", "Bright", bright.RightAscensionHours, bright.DeclinationDegrees, 1, HipparcosId: "1"),
            new("faint", "Faint", faint.RightAscensionHours, faint.DeclinationDegrees, 8, HipparcosId: "2")]);
        var topology = new InMemoryConstellationTopology([new("TST", "1", "2")]);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        { [SolarSystemBody.Moon] = new(faint, -10) }, "synthetic-v1");
        var request = new VisibleSceneRequest(Epoch, Observer, RigProjectionContextFactory.Create(rig), new CatalogQuery(6, 1), Metadata,
            constellationIds: ["TST"], solarSystemBodies: [SolarSystemBody.Moon], includeConstellationEndpointStars: true);
        var builder = new VisibleSceneBuilder(catalog, topology, ephemeris);
        var baseline = await builder.BuildAsync(request).ConfigureAwait(false);
        var filtered = await builder.BuildWithHorizonAsync(request, new(Profile(rig, mode), rig, Observer)).ConfigureAwait(false);
        Assert.HasCount(3, baseline.Objects, "The endpoint and Moon are outside the ordinary brightness-limited star selection.");
        if (mode == "clear")
            CollectionAssert.AreEqual(baseline.Objects.ToArray(), filtered.Objects.ToArray());
        else Assert.IsEmpty(filtered.Objects, "Neither endpoint lookup nor body append may bypass an active horizon.");
    }

    private static CameraRigConfig Rig() => new(new("virtual", 128, 128, 2, SensorColorMode.Mono, CameraPixelFormat.Mono8),
        new("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, ImageCircleRadiusPixels: 60,
            FocalLengthXPixels: 40, FocalLengthYPixels: 40, CalibrationVersion: "synthetic-v1"),
        new(90, 0, 0), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1));

    private static HorizonObstructionProfile Profile(CameraRigConfig rig, string mode) => new(
        HorizonObstructionProfile.CurrentSchemaVersion, "profile", 1, "installation",
        HorizonObstructionProfile.ComputeSiteIdentitySha256(35, -114, 1200), HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig),
        HorizonObstructionSource.Synthetic, 90, mode == "unknown" ? [] :
            Enumerable.Range(0, 4).Select(i => new HorizonObstructionSample(i * 90, mode == "blocked" ? 90 : 0)).ToArray());
}
