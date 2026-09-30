using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;

namespace HVO.SkyMonitor.CameraAgent.Tests.SkyMap;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSkyMapProjectionTests
{
    private static readonly DateTimeOffset Instant = new(2026, 1, 15, 6, 30, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] FixtureBodiesAboveTheHorizon = ["Moon", "Jupiter", "Neptune"];
    private static readonly double[] RingAltitudes = [0d, 30d, 60d];

    [TestMethod]
    public async Task ProjectAsync_ForTheSameInstantLocationRigAndCatalog_IsDeterministicAsync()
    {
        var first = await Project(CreateCatalog(BrightStars())).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);
        var second = await Project(CreateCatalog(BrightStars())).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(Serialize(first), Serialize(second));
        Assert.AreEqual(Instant, first.AtUtc);
        Assert.AreEqual("fixture-catalog", first.Catalog.Name);
        Assert.AreEqual(CameraAgentSkyMapProjection.DefaultMaximumObjects, first.MaximumObjects);
        Assert.IsFalse(first.ObjectsAtBound, "a sky below the bound must not be reported as truncated");
        Assert.AreEqual(CameraAgentSkyMapProjection.AstronomyAlgorithmVersion, first.AstronomyAlgorithmVersion);
    }

    [TestMethod]
    public async Task ProjectAsync_WhenMoreObjectsAreVisibleThanTheBound_StopsAtTheBoundInBrightnessThenIdOrderAsync()
    {
        var result = await Project(CreateCatalog(ZenithField(600))).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.HasCount(CameraAgentSkyMapProjection.DefaultMaximumObjects, result.Objects);
        Assert.IsTrue(result.ObjectsAtBound);
        var ordered = result.Objects
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static item => item.Id)
            .ToArray();
        CollectionAssert.AreEqual(ordered, result.Objects.Select(static item => item.Id).ToArray());
        Assert.IsTrue(result.Objects.All(static item => item.Magnitude <= CameraAgentSkyMapProjection.MaximumMagnitude));
    }

    [TestMethod]
    public async Task ProjectAsync_WithAConfiguredBound_ReturnsThatManyAndReportsItAsync()
    {
        var catalog = CreateCatalog(ZenithField(600));

        var lower = await Project(catalog, maximumObjects: 25).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);
        var higher = await Project(catalog, maximumObjects: 1_000).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.HasCount(25, lower.Objects);
        Assert.AreEqual(25, lower.MaximumObjects);
        Assert.IsTrue(lower.ObjectsAtBound);
        Assert.Contains("of at most 25 catalog objects", lower.Summary, StringComparison.Ordinal);
        // A bound above the visible sky returns every visible object, says it was not truncated, and keeps the same
        // brightness order, so the lower result is its prefix.
        Assert.IsGreaterThan(200, higher.Objects.Count);
        Assert.IsLessThan(1_000, higher.Objects.Count);
        Assert.AreEqual(1_000, higher.MaximumObjects);
        Assert.IsFalse(higher.ObjectsAtBound);
        CollectionAssert.AreEqual(
            lower.Objects.Select(static item => item.Id).ToArray(),
            higher.Objects.Take(25).Select(static item => item.Id).ToArray());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(CameraAgentSkyMapProjection.MaximumConfigurableObjects + 1)]
    public void Constructor_WithAnOutOfRangeBound_Throws(int maximumObjects)
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Project(CreateCatalog(BrightStars()), maximumObjects));

    [TestMethod]
    public async Task ProjectAsync_ReadsTheAuthoritativeLocationAndNeverOffersAnEditingContractAsync()
    {
        var result = await Project(CreateCatalog(BrightStars())).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual("sky-map-fixture-location", result.Observer.LocationId);
        Assert.AreEqual(1, result.Observer.Version);
        Assert.AreEqual(CameraAgentSkyMapLocationOrigin.StartupSeed, result.Observer.Origin);
        Assert.IsFalse(result.Observer.StagedAcknowledgementPending);
        Assert.IsTrue(result.Observer.EffectiveAtInstant);
        Assert.AreEqual(35d, result.Observer.LatitudeDegrees);
        Assert.AreEqual(-115d, result.Observer.LongitudeDegrees);
        Assert.AreEqual("UTC", result.Observer.TimeZoneId);
        Assert.IsNull(result.LatestCaptureScene);
        Assert.Contains("capture-time scene identity is omitted", result.Summary, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ProjectAsync_SerializedResult_CarriesNoLocalStoragePathAsync()
    {
        var result = await Project(CreateCatalog(BrightStars())).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        var json = Serialize(result);
        foreach (var forbidden in new[] { "://", ".db", ".sqlite", ".tsv", ".csv", ".json", "\\\\", "/tmp", "/home", "/var" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase, forbidden);
        }
        using var document = JsonDocument.Parse(json);
        foreach (var name in PropertyNames(document.RootElement))
        {
            Assert.IsFalse(
                name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Url", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Root", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Directory", StringComparison.OrdinalIgnoreCase),
                name);
        }
    }

    [TestMethod]
    public void Constructor_TakesOnlyOfflineLocalDependencies()
    {
        var parameters = typeof(CameraAgentSkyMapProjection)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.IsFalse(parameters.Any(static type =>
            type.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true ||
            type.Namespace?.Contains("LogicHost", StringComparison.Ordinal) == true));
        Assert.IsFalse(typeof(CameraAgentSkyMapProjection).Assembly.GetReferencedAssemblies()
            .Any(static assembly => assembly.Name?.Contains("LogicHost", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task ProjectAsync_ForConsecutiveInstants_ProducesDifferentScenesAsync()
    {
        var projection = Project(CreateCatalog(ZenithField(40)));

        var first = await projection.ProjectAsync(Instant, CancellationToken.None).ConfigureAwait(false);
        var later = await projection.ProjectAsync(Instant.AddHours(6), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(Instant.AddHours(6), later.AtUtc);
        Assert.AreNotEqual(Serialize(first), Serialize(later));
    }

    [TestMethod]
    public async Task ProjectAsync_WithAnEphemeris_AddsTheVisibleBodiesByKindWithoutSpendingTheStarBoundAsync()
    {
        var ephemeris = FixtureEphemeris();
        var projection = Project(CreateCatalog(ZenithField(600)), maximumObjects: 25, planetEphemeris: ephemeris);

        var result = await projection.ProjectAsync(Instant, CancellationToken.None).ConfigureAwait(false);

        var stars = result.Objects.Where(static item => item.Kind == "Star").ToArray();
        var bodies = result.Objects.Where(static item => item.Kind != "Star").ToDictionary(static item => item.DisplayName);
        Assert.HasCount(25, stars, "bodies must not take a place from a catalog star");
        Assert.IsTrue(result.ObjectsAtBound, "the bound is reported against catalog stars");
        CollectionAssert.AreEquivalent(FixtureBodiesAboveTheHorizon, bodies.Keys.ToArray());
        Assert.AreEqual("Moon", bodies["Moon"].Kind);
        Assert.AreEqual("Planet", bodies["Jupiter"].Kind);
        Assert.AreEqual("Planet", bodies["Neptune"].Kind);
        Assert.AreEqual("solar-system:Jupiter", bodies["Jupiter"].Id);
        Assert.IsGreaterThan(
            CameraAgentSkyMapProjection.MaximumMagnitude,
            bodies["Neptune"].Magnitude,
            "a body fainter than the limiting magnitude is still shown");
        Assert.IsTrue(bodies.Values.All(static item => item.AltitudeDegrees > 0 && item.HipparcosId is null));
        Assert.IsTrue(bodies.Values.All(static item => Math.Sqrt(
            Math.Pow(item.PixelX - 968, 2) + Math.Pow(item.PixelY - 608, 2)) <= 561));
        Assert.AreEqual(ephemeris.ModelVersion, result.EphemerisModelVersion);
        Assert.Contains("of at most 25 catalog objects", result.Summary, StringComparison.Ordinal);
        Assert.Contains(
            "3 solar-system bodies are also above the horizon in the image, placed by ephemeris fixture-ephemeris-v1",
            result.Summary,
            StringComparison.Ordinal);
        var ordered = result.Objects
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static item => item.Id)
            .ToArray();
        CollectionAssert.AreEqual(ordered, result.Objects.Select(static item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task ProjectAsync_WithoutAnEphemeris_ReturnsStarsOnlyAndSaysSoAsync()
    {
        var result = await Project(CreateCatalog(BrightStars())).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Objects.All(static item => item.Kind == "Star"));
        Assert.IsNull(result.EphemerisModelVersion);
        Assert.Contains("No planet ephemeris is installed", result.Summary, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ProjectAsync_ListsEveryInstalledFigureAndDrawsOnlyThoseInTheImageAsync()
    {
        var result = await Project(CreateCatalog(BrightStars())).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.HasCount(88, result.InstalledConstellationIds);
        CollectionAssert.AllItemsAreUnique(result.InstalledConstellationIds.ToArray());
        Assert.Contains("UMA", result.InstalledConstellationIds);
        var visible = result.Constellations.Select(static item => item.ConstellationId).ToArray();
        CollectionAssert.IsSubsetOf(visible, result.InstalledConstellationIds.ToArray());
        CollectionAssert.DoesNotContain(visible, "UMA", "a figure whose stars are not in the catalog cannot be drawn");
        var orion = result.Constellations.Single(static item => item.ConstellationId == "ORI");
        Assert.IsNotEmpty(orion.Lines);
        var points = orion.Lines.SelectMany(static line => line.Points).ToArray();
        Assert.IsTrue(orion.Lines.All(static line => line.Points.Count >= 2));
        Assert.IsLessThan(orion.VisibleSegmentCount + orion.Lines.Count, points.Length,
            "joined and simplified runs must be smaller than the raw chord list");
        Assert.IsTrue(points.All(static point =>
            Math.Sqrt(Math.Pow(point.X - 968, 2) + Math.Pow(point.Y - 608, 2)) <= 561 &&
            point.X == Math.Round(point.X, 1) && point.Y == Math.Round(point.Y, 1)));
    }

    [TestMethod]
    public async Task ProjectAsync_PublishesTheAltitudeRingsTheImageReachesAsync()
    {
        var result = await Project(CreateCatalog(BrightStars())).ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        var rings = result.Geometry.AltitudeRings.ToDictionary(static ring => ring.AltitudeDegrees);
        CollectionAssert.AreEqual(RingAltitudes, rings.Keys.ToArray());
        // The fixture optics reach 57.3 degrees from the zenith, so only the 60 degree circle is inside the image.
        Assert.IsEmpty(rings[0].Lines);
        Assert.IsEmpty(rings[30].Lines);
        var circle = rings[60].Lines.Single().Points;
        Assert.AreEqual(circle[0], circle[^1], "a circle wholly inside the image is closed");
        Assert.IsTrue(circle.All(static point => Math.Abs(
            Math.Sqrt(Math.Pow(point.X - 968, 2) + Math.Pow(point.Y - 608, 2)) - (560 * Math.PI / 6)) <= 1.5));
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task ProjectAsync_PublishesCardinalsThatFollowTheHorizontalFlipAsync(bool horizontalFlip, bool eastRight)
    {
        var result = await Project(CreateCatalog(BrightStars()), horizontalFlip: horizontalFlip)
            .ProjectAsync(Instant, CancellationToken.None)
            .ConfigureAwait(false);

        var east = result.Geometry.Cardinals.Single(static cardinal => cardinal.Name == "East");
        var west = result.Geometry.Cardinals.Single(static cardinal => cardinal.Name == "West");
        Assert.AreEqual(horizontalFlip, result.Geometry.HorizontalFlip);
        Assert.AreEqual(eastRight, east.PixelX > result.Geometry.PrincipalPointX);
        Assert.AreEqual(eastRight, west.PixelX < result.Geometry.PrincipalPointX);
    }

    private static FixedPlanetEphemeris FixtureEphemeris()
    {
        // Near the fixture zenith (RA about 6.5 h, Dec +35) or far below the horizon on the opposite side.
        var below = new SolarSystemPosition(new EquatorialPoint(18.5, -30), 1);
        var positions = Enum.GetValues<SolarSystemBody>().ToDictionary(static body => body, _ => below);
        positions[SolarSystemBody.Moon] = new SolarSystemPosition(new EquatorialPoint(6.2, 30), -12.5);
        positions[SolarSystemBody.Jupiter] = new SolarSystemPosition(new EquatorialPoint(6.8, 28), -2.7);
        positions[SolarSystemBody.Neptune] = new SolarSystemPosition(new EquatorialPoint(6.4, 40), 7.8);
        return new FixedPlanetEphemeris(positions, "fixture-ephemeris-v1");
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }
                break;
            default:
                break;
        }
    }

    private static string Serialize(CameraAgentSkyMapProjectionResult result)
        => JsonSerializer.Serialize(result, SerializerOptions);

    internal static CameraAgentSkyMapProjection Project(
        FixtureCatalog catalog,
        int maximumObjects = CameraAgentSkyMapProjection.DefaultMaximumObjects,
        IPlanetEphemeris? planetEphemeris = null,
        bool horizontalFlip = false)
    {
        var accessor = new CameraAgentConfigurationAccessor();
        accessor.SetConfiguration(CreateConfig(horizontalFlip));
        return new CameraAgentSkyMapProjection(
            accessor,
            catalog,
            TimeProvider.System,
            StandardConstellationTopology.CreateD3Celestial(),
            maximumObjects: maximumObjects,
            planetEphemeris: planetEphemeris);
    }

    internal static FixtureCatalog CreateCatalog(IEnumerable<CelestialCatalogObject> objects)
        => new(objects, new CatalogMetadata(
            "fixture-catalog", "1.4.0", new Uri("https://catalog.invalid/fixture"),
            new string('C', 64), "CC-BY-4.0", "2"));

    // Real Hipparcos identities so the installed topology can resolve figures.
    internal static IEnumerable<CelestialCatalogObject> BrightStars() =>
    [
        new("sirius", "Sirius", 6.752481, -16.716116, -1.46, 0.009, "32349"),
        new("canopus", "Canopus", 6.399195, -52.695661, -0.74, 0.164, "30438"),
        new("rigel", "Rigel", 5.242298, -8.201638, 0.13, -0.03, "24436"),
        new("betelgeuse", "Betelgeuse", 5.919529, 7.407064, 0.50, 1.85, "27989"),
        new("procyon", "Procyon", 7.655033, 5.224993, 0.34, 0.432, "37279"),
        new("aldebaran", "Aldebaran", 4.598677, 16.509301, 0.85, 1.538, "21421"),
        new("bellatrix", "Bellatrix", 5.418850, 6.349703, 1.64, -0.224, "25336"),
        new("alnilam", "Alnilam", 5.603559, -1.201919, 1.69, -0.184, "26311"),
        new("alnitak", "Alnitak", 5.679313, -1.942573, 1.74, -0.199, "26727"),
        new("mintaka", "Mintaka", 5.533445, -0.299092, 2.25, -0.175, "25930"),
        new("saiph", "Saiph", 5.795942, -9.669605, 2.09, -0.169, "27366")
    ];

    // A dense field around the fixture zenith so the hard bound is exercised.
    private static IEnumerable<CelestialCatalogObject> ZenithField(int count)
    {
        for (var index = 0; index < count; index++)
        {
            var ring = index % 20;
            yield return new CelestialCatalogObject(
                string.Create(CultureInfo.InvariantCulture, $"field-{index:D4}"),
                string.Create(CultureInfo.InvariantCulture, $"Field {index:D4}"),
                (index * 24d / count) % 24d,
                20d + ring * 2d,
                1d + index % 5 * 0.25d);
        }
    }

    private static CameraModuleConfig CreateConfig(bool horizontalFlip = false) => new(
        new ObservatoryLocation(35, -115, 1000, "UTC"),
        new CameraModuleDescriptor("VirtualSky"),
        new CameraRigConfig(
            new SensorProfile(
                "SkyMapFixture", 1936, 1216, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                StrideBytes: 1936 * 2, ByteOrder: SampleByteOrder.LittleEndian),
            new OpticsProfile(
                "EquidistantFisheye", 2.5, 170, 0, LensKind.Fisheye,
                PrincipalPointX: 968, PrincipalPointY: 608, ImageCircleRadiusPixels: 560,
                FocalLengthXPixels: 560, FocalLengthYPixels: 560, HorizontalFlip: horizontalFlip,
                CalibrationVersion: "sky-map-fixture-calibration-v1"),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
            ProfileVersion: "sky-map-fixture-rig-v1"),
        CapturePipelineConfig.Empty,
        AgentId: "sky-map-fixture")
    {
        DeploymentLocation = DeploymentLocationSnapshot.Create(
            "sky-map-fixture-location", 1, "startup-seed", null, DateTimeOffset.UnixEpoch, null,
            35, -115, 1000, "UTC")
    };

    internal sealed class FixtureCatalog : ICelestialCatalog, IHipparcosCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _catalog;

        internal FixtureCatalog(IEnumerable<CelestialCatalogObject> objects, CatalogMetadata metadata)
        {
            _catalog = new InMemoryCelestialCatalog(objects);
            Metadata = metadata;
        }

        public CatalogMetadata Metadata { get; }

        public string PreprocessingVersion => "3";

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _catalog.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
            => _catalog.QueryCandidatesAsync(query, cancellationToken);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> GetByHipparcosIdsAsync(
            IReadOnlyCollection<string> hipparcosIds,
            CancellationToken cancellationToken = default)
            => _catalog.GetByHipparcosIdsAsync(hipparcosIds, cancellationToken);
    }
}
