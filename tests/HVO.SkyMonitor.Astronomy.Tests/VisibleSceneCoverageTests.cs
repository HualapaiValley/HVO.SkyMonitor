using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
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

    [TestMethod]
    public async Task BuildAsync_PerspectiveRegionReducesCandidatesWithoutChangingVisibleScene()
    {
        var objects = CreateBoresightCatalog();
        var filteredCatalog = new RecordingCatalog(objects, honorRegion: true);
        var unfilteredCatalog = new RecordingCatalog(objects, honorRegion: false);
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 100, 50, 100, 100, 200, 100, ProjectionAperture.Rectangular,
            BoresightAltitudeDegrees: 90);
        var request = CreateModelRequest(projection, HorizonPolicy.ProjectionOnly);

        var filtered = await new VisibleSceneBuilder(filteredCatalog).BuildAsync(request).ConfigureAwait(false);
        var unfiltered = await new VisibleSceneBuilder(unfilteredCatalog).BuildAsync(request).ConfigureAwait(false);

        Assert.IsNotNull(filteredCatalog.LastQuery?.J2000Region);
        Assert.AreEqual(48.1896851042, filteredCatalog.LastQuery.J2000Region.Value.RadiusDegrees, 2e-9);
        Assert.IsTrue(filteredCatalog.ReturnedCandidateCount < unfilteredCatalog.ReturnedCandidateCount);
        CollectionAssert.AreEqual(unfiltered.Objects.Select(item => item.Id).ToArray(),
            filtered.Objects.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(unfiltered.Objects.Select(item => item.Pixel).ToArray(),
            filtered.Objects.Select(item => item.Pixel).ToArray());
    }

    [TestMethod]
    public async Task BuildAsync_UnboundedPerspectiveIncludesFrontFacingObjectOutsideSensorFov()
    {
        EquatorialPoint J2000(AltAzPoint horizontal)
            => EquatorialPrecession.PrecessToJ2000(CoordinateTransforms.HorizontalToEquatorial(
                horizontal, Utc, Observer.LatitudeDegrees, Observer.LongitudeDegrees), Utc);
        var horizontal = new AltAzPoint(30, 90);
        var point = J2000(horizontal);
        var item = new CelestialCatalogObject(
            "off-sensor", "Off sensor", point.RightAscensionHours, point.DeclinationDegrees, 1);
        var boundedCatalog = new RecordingCatalog([item], honorRegion: true);
        var unboundedCatalog = new RecordingCatalog([item], honorRegion: true);
        var boundedProjection = new ProjectionContext(
            ProjectionModel.Perspective, 100, 50, 100, 100, 200, 100,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90);
        var unboundedProjection = boundedProjection with { EnforceSensorBounds = false };

        var bounded = await new VisibleSceneBuilder(boundedCatalog).BuildAsync(CreateModelRequest(
            boundedProjection, HorizonPolicy.ProjectionOnly)).ConfigureAwait(false);
        var unbounded = await new VisibleSceneBuilder(unboundedCatalog).BuildAsync(CreateModelRequest(
            unboundedProjection, HorizonPolicy.ProjectionOnly)).ConfigureAwait(false);

        Assert.IsNotNull(boundedCatalog.LastQuery!.J2000Region);
        Assert.IsNull(unboundedCatalog.LastQuery!.J2000Region);
        Assert.IsEmpty(bounded.Objects);
        Assert.HasCount(1, unbounded.Objects);
        Assert.AreEqual("off-sensor", unbounded.Objects[0].Id);
        Assert.IsGreaterThan(unboundedProjection.WidthPixels, unbounded.Objects[0].Pixel.X);
        Assert.IsTrue(unbounded.Objects.Count <= unbounded.Request.CatalogQuery.MaximumResults);
    }

    [TestMethod]
    public async Task BuildAsync_RefractionUsesHorizonRegionOrFallsBackToAllSky()
    {
        var geometricCatalog = new RecordingCatalog([], honorRegion: true);
        var projectionCatalog = new RecordingCatalog([], honorRegion: true);

        await new VisibleSceneBuilder(geometricCatalog).BuildAsync(CreateRequest(
            refraction: new RefractionOptions(true),
            horizonPolicy: HorizonPolicy.GeometricHorizon)).ConfigureAwait(false);
        await new VisibleSceneBuilder(projectionCatalog).BuildAsync(CreateRequest(
            refraction: new RefractionOptions(true),
            horizonPolicy: HorizonPolicy.ProjectionOnly)).ConfigureAwait(false);

        Assert.AreEqual(90, geometricCatalog.LastQuery!.J2000Region!.Value.RadiusDegrees, 2e-9);
        Assert.IsNull(projectionCatalog.LastQuery?.J2000Region);
    }

    [TestMethod]
    [DataRow(ProjectionModel.EquidistantFisheye, 57.2957795131)]
    [DataRow(ProjectionModel.EquisolidFisheye, 60d)]
    [DataRow(ProjectionModel.OrthographicFisheye, 90d)]
    [DataRow(ProjectionModel.StereographicFisheye, 53.1301023542)]
    public async Task BuildAsync_FisheyeModelsSupplyConservativeOpticalRegion(
        ProjectionModel model,
        double expectedRadiusDegrees)
    {
        var catalog = new RecordingCatalog([], honorRegion: true);
        var projection = new ProjectionContext(
            model, 100, 100, 100, 100, 200, 200, ProjectionAperture.Circular, 100);

        await new VisibleSceneBuilder(catalog).BuildAsync(CreateModelRequest(
            projection, HorizonPolicy.ProjectionOnly)).ConfigureAwait(false);

        Assert.AreEqual(expectedRadiusDegrees, catalog.LastQuery!.J2000Region!.Value.RadiusDegrees, 2e-9);
    }

    [TestMethod]
    public async Task BuildAsync_GeometricHorizonUsesSmallerConservativeRegion()
    {
        var catalog = new RecordingCatalog([], honorRegion: true);

        await new VisibleSceneBuilder(catalog).BuildAsync(CreateRequest(
            projection: new EquidistantProjectionContext(0, 0, 100, Math.PI * 100),
            horizonPolicy: HorizonPolicy.GeometricHorizon)).ConfigureAwait(false);

        Assert.AreEqual(90, catalog.LastQuery!.J2000Region!.Value.RadiusDegrees, 2e-9);
    }

    [TestMethod]
    public async Task BuildAsync_StillAppliesExactHorizonWhenCatalogIgnoresRegionHint()
    {
        var catalog = new RecordingCatalog(CreateBoresightCatalog(), honorRegion: false);

        var scene = await new VisibleSceneBuilder(catalog).BuildAsync(CreateRequest(
            horizonPolicy: HorizonPolicy.GeometricHorizon)).ConfigureAwait(false);

        Assert.IsFalse(scene.Objects.Any(item => item.GeometricHorizontal.AltitudeDegrees < 0));
        Assert.IsTrue(catalog.ReturnedCandidateCount > scene.Objects.Count);
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

    private static VisibleSceneRequest CreateModelRequest(
        ProjectionContext projection,
        HorizonPolicy horizonPolicy)
        => new(
            Utc,
            Observer,
            projection,
            new CatalogQuery(6, 100),
            Metadata,
            horizonPolicy: horizonPolicy,
            projectionVersion: "projection-test",
            algorithmVersion: "algorithm-test");

    private static CelestialCatalogObject[] CreateBoresightCatalog()
    {
        EquatorialPoint J2000(AltAzPoint horizontal)
            => EquatorialPrecession.PrecessToJ2000(CoordinateTransforms.HorizontalToEquatorial(
                horizontal, Utc, Observer.LatitudeDegrees, Observer.LongitudeDegrees), Utc);
        CelestialCatalogObject Create(string id, AltAzPoint horizontal, double magnitude)
        {
            var point = J2000(horizontal);
            return new CelestialCatalogObject(id, id, point.RightAscensionHours, point.DeclinationDegrees, magnitude);
        }

        return
        [
            Create("center", new AltAzPoint(90, 0), 1),
            Create("near", new AltAzPoint(70, 45), 2),
            Create("far", new AltAzPoint(-80, 0), 0)
        ];
    }

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

    private sealed class RecordingCatalog(
        IEnumerable<CelestialCatalogObject> objects,
        bool honorRegion) : ICelestialCatalog
    {
        private readonly InMemoryCelestialCatalog _inner = new(objects);

        public CatalogCandidateQuery? LastQuery { get; private set; }
        public int ReturnedCandidateCount { get; private set; }

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _inner.Query(query);

        public async ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            var effectiveQuery = honorRegion ? query : query with { J2000Region = null };
            var result = await _inner.QueryCandidatesAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);
            ReturnedCandidateCount = result.Count;
            return result;
        }
    }
}
