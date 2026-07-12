using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class VisibleSceneCoverageTests
{
    private static readonly DateTimeOffset Utc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(35.347, -113.878, 850);
    private static readonly CatalogMetadata Metadata = new(
        "Test catalog", "2025.1", new Uri("https://example.test/catalog"), "sha256:fixture", "CC0", "1");

    [TestMethod]
    public void ObserverLocation_RejectsEveryInvalidFiniteRange()
    {
        ObserverLocation[] invalid =
        [
            new(double.NaN, 0, 0), new(-91, 0, 0), new(91, 0, 0),
            new(0, double.NaN, 0), new(0, -181, 0), new(0, 181, 0),
            new(0, 0, double.PositiveInfinity)
        ];

        foreach (var location in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(location.Validate);
        }

        Observer.Validate();
    }

    [TestMethod]
    public void Request_NormalizesUtcAndRetainsValidatedMetadataAndOptions()
    {
        var local = Utc.ToOffset(TimeSpan.FromHours(-7));
        var projection = new EquidistantProjectionContext(100, 100, 50, Math.PI * 50);
        var query = new CatalogQuery(6, 25);
        var refraction = new RefractionOptions(true, -1);
        var request = new VisibleSceneRequest(
            local, Observer, projection, query, Metadata, refraction,
            HorizonPolicy.ProjectionOnly, "calibration-7", "algorithm-3");

        Assert.AreEqual(Utc, request.Utc);
        Assert.AreEqual(Observer, request.Observer);
        Assert.AreEqual(ProjectionModel.EquidistantFisheye, request.Projection.Model);
        Assert.AreEqual(projection.PrincipalPointX, request.Projection.PrincipalPointX);
        Assert.AreSame(query, request.CatalogQuery);
        Assert.AreSame(Metadata, request.CatalogMetadata);
        Assert.AreEqual(refraction, request.Refraction);
        Assert.AreEqual(HorizonPolicy.ProjectionOnly, request.HorizonPolicy);
        Assert.AreEqual("calibration-7", request.ProjectionVersion);
        Assert.AreEqual("algorithm-3", request.AlgorithmVersion);
    }

    [TestMethod]
    public void Request_RejectsNullDependenciesInvalidPolicyAndVersions()
    {
        Assert.Throws<ArgumentNullException>(() => new VisibleSceneRequest(
            Utc, Observer, new EquidistantProjectionContext(0, 0, 100, 100), null!, Metadata));
        Assert.Throws<ArgumentNullException>(() => new VisibleSceneRequest(
            Utc, Observer, new EquidistantProjectionContext(0, 0, 100, 100), new CatalogQuery(6, 100), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRequest(horizonPolicy: (HorizonPolicy)99));
        Assert.Throws<ArgumentException>(() => CreateRequest(projectionVersion: " "));
        Assert.Throws<ArgumentNullException>(() => CreateRequest(algorithmVersion: null));
    }

    [TestMethod]
    public void Request_RejectsIncompleteOrNonAbsoluteCatalogMetadata()
    {
        CatalogMetadata[] invalid =
        [
            Metadata with { Name = "" },
            Metadata with { Version = " " },
            Metadata with { SourceUrl = null! },
            Metadata with { Checksum = "" },
            Metadata with { License = " " },
            Metadata with { SchemaVersion = "" },
            Metadata with { SourceUrl = new Uri("relative/catalog", UriKind.Relative) }
        ];

        foreach (var metadata in invalid)
        {
            Assert.Throws<ArgumentException>(() => CreateRequest(metadata: metadata));
        }
    }

    [TestMethod]
    public void Builder_RejectsNullCatalogAndRequest()
    {
        Assert.Throws<ArgumentNullException>(() => new VisibleSceneBuilder(null!));
        var builder = new VisibleSceneBuilder(new InMemoryCelestialCatalog([]));
        Assert.Throws<ArgumentNullException>(() => builder.BuildAsync(null!).AsTask().GetAwaiter().GetResult());
    }

    [TestMethod]
    public async Task BuildAsync_ProjectionOnlyIncludesBelowHorizonWhileGeometricPolicyRejectsIt()
    {
        var catalog = CreateKnownStarCatalog();
        var builder = new VisibleSceneBuilder(catalog);
        var projectionOnly = await builder.BuildAsync(CreateRequest(
            projection: new(0, 0, 100, Math.PI * 100),
            horizonPolicy: HorizonPolicy.ProjectionOnly)).ConfigureAwait(false);
        var geometric = await builder.BuildAsync(CreateRequest(
            projection: new(0, 0, 100, Math.PI * 100),
            horizonPolicy: HorizonPolicy.GeometricHorizon)).ConfigureAwait(false);

        Assert.IsTrue(projectionOnly.Objects.Any(item => item.GeometricHorizontal.AltitudeDegrees < 0));
        Assert.IsFalse(geometric.Objects.Any(item => item.GeometricHorizontal.AltitudeDegrees < 0));
        Assert.IsTrue(projectionOnly.Objects.Count > geometric.Objects.Count);
    }

    [TestMethod]
    public async Task BuildAsync_RejectsOffSensorObjectsAndAppliesRefractionAndMetadata()
    {
        var catalog = CreateKnownStarCatalog();
        var fullScene = await new VisibleSceneBuilder(catalog).BuildAsync(CreateRequest(
            projection: new(50, 50, 20, 60, WidthPixels: 100, HeightPixels: 100),
            refraction: new RefractionOptions(true),
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: "sensor-v2",
            algorithmVersion: "scene-v4")).ConfigureAwait(false);

        Assert.AreSame(fullScene.Request.CatalogMetadata, Metadata);
        Assert.IsTrue(fullScene.Objects.Count > 0);
        Assert.IsTrue(fullScene.Objects.Count < catalog.Query(new CatalogQuery(6, 100)).Count);
        var item = fullScene.Objects[0];
        Assert.AreEqual(item.Id, item.DisplayName);
        Assert.AreEqual(CelestialObjectKind.Star, item.Kind);
        Assert.AreNotEqual(default, item.J2000Equatorial);
        Assert.AreNotEqual(default, item.EquatorialOfDate);
        Assert.IsTrue(item.ApparentHorizontal.AltitudeDegrees > item.GeometricHorizontal.AltitudeDegrees);
        Assert.AreEqual(1, item.CameraDirection.Length, 1e-10);
        Assert.IsTrue(item.Pixel.X is >= 0 and <= 100);
        Assert.IsTrue(item.Pixel.Y is >= 0 and <= 100);
        Assert.IsTrue(double.IsFinite(item.Magnitude));
        Assert.IsNotNull(item.ColorIndex);
        Assert.AreEqual(Metadata.Version, item.CatalogVersion);
        Assert.AreEqual("sensor-v2", item.ProjectionVersion);
        Assert.AreEqual("scene-v4", item.AlgorithmVersion);
    }

    [TestMethod]
    public async Task BuildAsync_PropagatesCancellationTokenToCatalog()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var catalog = new CancellationCatalog();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new VisibleSceneBuilder(catalog).BuildAsync(CreateRequest(), cancellation.Token)
                .ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(cancellation.Token, catalog.ReceivedToken);
    }

    private static InMemoryCelestialCatalog CreateKnownStarCatalog()
        => new([
            new("sirius", "sirius", 6.752477, -16.716116, -1.46, 0.00),
            new("vega", "vega", 18.615649, 38.783689, 0.03, 0.00),
            new("polaris", "polaris", 2.530301, 89.264109, 1.98, 0.60)
        ]);

    private static VisibleSceneRequest CreateRequest(
        CatalogQuery? catalogQuery = null,
        CatalogMetadata? metadata = null,
        EquidistantProjectionContext? projection = null,
        RefractionOptions refraction = default,
        HorizonPolicy horizonPolicy = HorizonPolicy.GeometricHorizon,
        string? projectionVersion = "projection-test",
        string? algorithmVersion = "algorithm-test")
        => new(
            Utc,
            Observer,
            projection ?? new EquidistantProjectionContext(0, 0, 100, Math.PI * 100),
            catalogQuery ?? new CatalogQuery(6, 100),
            metadata ?? Metadata,
            refraction,
            horizonPolicy,
            projectionVersion!,
            algorithmVersion!);

    private sealed class CancellationCatalog : ICelestialCatalog
    {
        public CancellationToken ReceivedToken { get; private set; }

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => [];

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<CelestialCatalogObject>>([]);
        }
    }
}
