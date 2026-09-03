using System.Text;
using System.Diagnostics;
using System.IO.Compression;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProjectedSceneContractTests
{
    private static readonly DateTimeOffset EffectiveUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid CaptureId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ArtifactId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Create_ProducesCanonicalGoldenIdentityAndRoundTrips()
    {
        var visible = await BuildSceneAsync(ProjectionModel.EquidistantFisheye).ConfigureAwait(false);

        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.VirtualRenderAuthoritative,
            visible,
            Readout(200, 200),
            Source(),
            "calibration-v1",
            visible.Request.ProjectionVersion);
        var bytes = ProjectedSceneJson.Serialize(scene);
        var parsed = ProjectedSceneJson.Parse(bytes);

        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.AreEqual("D5B424FE0316830F2D8AFD15FBE167353FF10CD6956DE8EABCFF5735A09EC1DD", scene.SceneIdentitySha256);
        Assert.AreEqual(scene.SceneIdentitySha256, parsed.Scene!.SceneIdentitySha256);
        Assert.AreEqual(ProjectedSceneCoordinateConvention.ContinuousTopLeftPixelEdge, scene.CoordinateConvention);
        StringAssert.Contains(ProjectedSceneImageTransformV1.OperationOrder, "crop-bin", StringComparison.Ordinal);
        Assert.IsTrue(bytes.Length < ProjectedSceneJson.MaximumPayloadBytes);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Create_CanonicalizesOrderingAndEverySemanticAxisChangesIdentity()
    {
        var visible = await BuildSceneAsync(ProjectionModel.Perspective).ConfigureAwait(false);
        var predicted = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(), "calibration-v1", visible.Request.ProjectionVersion);
        var repeated = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(), "calibration-v1", visible.Request.ProjectionVersion);
        var registered = ProjectedSceneJson.Create(
            ProjectedSceneKind.ImageRegistered, visible, Readout(200, 200), Source(), "calibration-v1", visible.Request.ProjectionVersion);
        var changedSource = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200),
            Source() with { ArtifactId = Guid.Parse("33333333-3333-3333-3333-333333333333") },
            "calibration-v1", visible.Request.ProjectionVersion);

        Assert.AreEqual(predicted.SceneIdentitySha256, repeated.SceneIdentitySha256);
        Assert.AreNotEqual(predicted.SceneIdentitySha256, registered.SceneIdentitySha256);
        Assert.AreNotEqual(predicted.SceneIdentitySha256, changedSource.SceneIdentitySha256);
        Assert.IsTrue(predicted.Objects.SequenceEqual(predicted.Objects.OrderBy(item => item.Magnitude)
            .ThenBy(item => item.Id, StringComparer.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Validate_RejectsNonUtcNonFiniteUnorderedAndOversizedPayloads()
    {
        var visible = await BuildSceneAsync(ProjectionModel.EquidistantFisheye).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(), "calibration-v1", visible.Request.ProjectionVersion);
        var nonUtc = scene with { EffectiveUtc = scene.EffectiveUtc.ToOffset(TimeSpan.FromHours(1)) };
        var nonFiniteObject = scene.Objects[0] with { Pixel = new PixelPoint(double.NaN, 1) };
        var nonFinite = scene with { Objects = [nonFiniteObject, .. scene.Objects.Skip(1)] };
        var unordered = Reidentify(scene with { Objects = scene.Objects.Reverse().ToArray() });

        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(nonUtc));
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(nonFinite));
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(unordered));
        Assert.IsFalse(ProjectedSceneJson.Parse(new byte[ProjectedSceneJson.MaximumPayloadBytes + 1]).IsValid);
        Assert.IsFalse(ProjectedSceneJson.Parse(Encoding.UTF8.GetBytes("{not-json}")).IsValid);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ParseRejectsNullMissingUnknownDuplicateAndNonCanonicalWireForms()
    {
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, await BuildSceneAsync(ProjectionModel.Perspective).ConfigureAwait(false),
            Readout(200, 200), Source(), "calibration-v1", "perspective-v1");
        var json = Encoding.UTF8.GetString(ProjectedSceneJson.Serialize(scene));
        var malformed = new[]
        {
            "null",
            json.Replace("\"catalog\":{", "\"catalog\":null,\"ignored\":{", StringComparison.Ordinal),
            json.Replace("\"objects\":[", "\"objects\":[null,", StringComparison.Ordinal),
            json.Replace("\"segments\":[]", "\"segments\":[null]", StringComparison.Ordinal),
            json.Replace("\"schemaVersion\":\"projected-scene-v1\",", string.Empty, StringComparison.Ordinal),
            json.Replace("\"kind\":\"Predicted\"", "\"kind\":\"Predicted\",\"KIND\":\"Predicted\"", StringComparison.Ordinal),
            json.Replace("\"widthPixels\":200", "\"widthPixels\":2e2", StringComparison.Ordinal),
            "{" + json[1..^1] + ",\"unknown\":true}"
        };

        foreach (var candidate in malformed)
        {
            var parsed = ProjectedSceneJson.Parse(Encoding.UTF8.GetBytes(candidate));
            Assert.IsFalse(parsed.IsValid, candidate);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SceneCollectionsAreDefensiveAndSemanticIdentityAxesAreComplete()
    {
        var visible = await BuildSceneAsync(ProjectionModel.Perspective).ConfigureAwait(false);
        var baseline = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, Readout(200, 200),
            Source() with { ArtifactIdentitySha256 = new string('a', 64) }, "calibration-v1", visible.Request.ProjectionVersion);
        var parsed = ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(baseline)).Scene!;

        Assert.AreEqual(new string('A', 64), baseline.Source.ArtifactIdentitySha256);
        Assert.IsFalse(baseline.Objects is ProjectedCelestialObject[]);
        Assert.IsFalse(parsed.Objects is ProjectedCelestialObject[]);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<ProjectedCelestialObject>)baseline.Objects).Clear());

        var semanticChanges = new ProjectedSceneV1[]
        {
            baseline with { Projection = baseline.Projection with { EnforceSensorBounds = !baseline.Projection.EnforceSensorBounds } },
            baseline with { Projection = baseline.Projection with { RollDegrees = 18 } },
            baseline with { Projection = baseline.Projection with { HorizontalFlip = false } },
            baseline with { ImageTransform = baseline.ImageTransform with { HorizontalMirror = true } },
            baseline with { AstronomyAlgorithmVersion = "changed" },
            baseline with { HorizonPolicy = HorizonPolicy.ProjectionOnly }
        };
        foreach (var changed in semanticChanges)
        {
            Assert.AreNotEqual(baseline.SceneIdentitySha256, ProjectedSceneJson.ComputeIdentity(changed));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SelectionDescriptorIsCanonicalAndEverySelectionAxisChangesIdentity()
    {
        var visible = await BuildSceneAsync(ProjectionModel.Perspective).ConfigureAwait(false);
        var baseline = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(),
            "calibration-v1", visible.Request.ProjectionVersion);
        var selection = baseline.Selection;
        var changes = new[]
        {
            selection with { MaximumMagnitude = selection.MaximumMagnitude + 0.5 },
            selection with { MaximumResults = selection.MaximumResults + 1 },
            selection with { ConstellationIds = ["ORI"] },
            selection with { SolarSystemBodies = [SolarSystemBody.Mars] },
            selection with { IncludeConstellationEndpointStars = true }
        };

        Assert.IsFalse(selection.ConstellationIds is string[]);
        Assert.IsFalse(selection.SolarSystemBodies is SolarSystemBody[]);
        foreach (var changed in changes)
        {
            Assert.AreNotEqual(baseline.SceneIdentitySha256,
                ProjectedSceneJson.ComputeIdentity(baseline with { Selection = changed }));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ParseAndValidationRejectMalformedSelectionsAndMissingEphemerisProvenance()
    {
        var visible = await BuildSceneAsync(ProjectionModel.Perspective).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(),
            "calibration-v1", visible.Request.ProjectionVersion);
        var json = Encoding.UTF8.GetString(ProjectedSceneJson.Serialize(scene));
        var malformed = new[]
        {
            json.Replace("\"selection\":{", "\"selection\":null,\"ignored\":{", StringComparison.Ordinal),
            json.Replace("\"maximumResults\":10,", string.Empty, StringComparison.Ordinal),
            json.Replace("\"constellationIds\":[]", "\"constellationIds\":[\"ori\"]", StringComparison.Ordinal),
            json.Replace("\"constellationIds\":[]", "\"constellationIds\":[\"ORI\",\"ORI\"]", StringComparison.Ordinal),
            json.Replace("\"solarSystemBodies\":[]", "\"solarSystemBodies\":[\"Mars\",\"Mars\"]", StringComparison.Ordinal),
            json.Replace("\"maximumResults\":10", "\"maximumResults\":0", StringComparison.Ordinal)
        };
        foreach (var candidate in malformed)
        {
            Assert.IsFalse(ProjectedSceneJson.Parse(Encoding.UTF8.GetBytes(candidate)).IsValid, candidate);
        }

        var requestedBody = Reidentify(scene with
        {
            Selection = scene.Selection with { SolarSystemBodies = [SolarSystemBody.Mars] },
            EphemerisModelVersion = null
        });
        var bodyObject = scene.Objects[0] with { Kind = CelestialObjectKind.SolarSystemBody };
        var resultingBody = Reidentify(scene with { Objects = [bodyObject, .. scene.Objects.Skip(1)], EphemerisModelVersion = null });
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(requestedBody));
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(resultingBody));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SolarSystemSelectionRequiresAndRetainsEphemerisModelVersion()
    {
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(EffectiveUtc, 0) / 15;
        var position = new EquatorialPoint(siderealHours, 0);
        var request = new VisibleSceneRequest(
            EffectiveUtc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 100, 100, 100, 100, 200, 200,
                ProjectionAperture.Rectangular, RollDegrees: 17, HorizontalFlip: true),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"),
            projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Mars, SolarSystemBody.Mars]);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Mars] = new(position, -1)
        }, "ephemeris-v1");
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), planetEphemeris: ephemeris)
            .BuildAsync(request).ConfigureAwait(false);

        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(),
            "calibration-v1", "perspective-v1");
        Assert.HasCount(1, scene.Selection.SolarSystemBodies);
        Assert.AreEqual(SolarSystemBody.Mars, scene.Selection.SolarSystemBodies[0]);
        Assert.AreEqual("ephemeris-v1", scene.EphemerisModelVersion);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task BuilderProviderProvenanceIsBoundAndChangesSceneIdentity()
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(EffectiveUtc, 0) / 15;
        var request = new VisibleSceneRequest(
            EffectiveUtc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 100, 100, 100, 100, 200, 200,
                ProjectionAperture.Rectangular, RollDegrees: 17, HorizontalFlip: true),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"),
            projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Mars]);
        var positions = new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Mars] = new(new EquatorialPoint(rightAscension, 0), -1)
        };
        var firstVisible = await new VisibleSceneBuilder(
            new InMemoryCelestialCatalog([]), planetEphemeris: new FixedPlanetEphemeris(positions, "ephemeris-v1"))
            .BuildAsync(request).ConfigureAwait(false);
        var secondVisible = await new VisibleSceneBuilder(
            new InMemoryCelestialCatalog([]), planetEphemeris: new FixedPlanetEphemeris(positions, "ephemeris-v2"))
            .BuildAsync(request).ConfigureAwait(false);
        var first = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, firstVisible, Readout(200, 200), Source(), "calibration-v1", "perspective-v1");
        var second = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, secondVisible, Readout(200, 200), Source(), "calibration-v1", "perspective-v1");

        Assert.AreEqual("ephemeris-v1", firstVisible.ComputationProvenance.EphemerisModelVersion);
        Assert.AreEqual("ephemeris-v1", first.EphemerisModelVersion);
        Assert.AreNotEqual(first.SceneIdentitySha256, second.SceneIdentitySha256);
        Assert.IsFalse(typeof(ProjectedSceneJson).GetMethod(nameof(ProjectedSceneJson.Create))!.GetParameters()
            .Any(parameter => parameter.Name!.Contains("ephemeris", StringComparison.OrdinalIgnoreCase) ||
                parameter.Name.Contains("topology", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task TopologyProviderProvenanceIsBoundPreservedAndCannotBeCallerInvented()
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(EffectiveUtc, 0) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new("from", "From", rightAscension, 0, 1, HipparcosId: "1"),
            new("to", "To", rightAscension, 20, 2, HipparcosId: "2")
        ]);
        var request = new VisibleSceneRequest(
            EffectiveUtc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 100, 100, 100, 100, 200, 200,
                ProjectionAperture.Circular, 150, RollDegrees: 17, HorizontalFlip: true),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"),
            projectionVersion: "fisheye-v1", constellationIds: ["TST"]);
        InMemoryConstellationTopology Topology(char checksum) => new(
            [new ConstellationSegment("TST", "1", "2")],
            new ConstellationTopologyMetadata(
                "fixture-topology", "1", new Uri("https://example.test/topology"),
                new string(checksum, 64), "test", "fixture-v1"));
        var firstVisible = await new VisibleSceneBuilder(catalog, Topology('D')).BuildAsync(request).ConfigureAwait(false);
        var secondVisible = await new VisibleSceneBuilder(catalog, Topology('E')).BuildAsync(request).ConfigureAwait(false);
        var transformed = VisibleSceneReadoutTransform.ToOutput(
            firstVisible, request.Projection with
            {
                PrincipalPointX = 50,
                PrincipalPointY = 50,
                FocalLengthXPixels = 50,
                FocalLengthYPixels = 50,
                WidthPixels = 100,
                HeightPixels = 100,
                ImageCircleRadiusPixels = 75
            }, 2, 2);
        var first = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, firstVisible, Readout(200, 200), Source(), "calibration-v1", "fisheye-v1");
        var second = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, secondVisible, Readout(200, 200), Source(), "calibration-v1", "fisheye-v1");

        Assert.AreEqual(new string('D', 64), first.ConstellationTopology!.SourceSha256);
        Assert.AreEqual(firstVisible.ComputationProvenance, transformed.ComputationProvenance);
        Assert.AreNotEqual(first.SceneIdentitySha256, second.SceneIdentitySha256);

        var unbound = new VisibleScene(request, firstVisible.Objects, firstVisible.Segments);
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, unbound, Readout(200, 200), Source(), "calibration-v1", "fisheye-v1"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CatalogProviderMetadataMismatchIsRejectedBeforeCandidateQuery()
    {
        var actual = new CatalogMetadata(
            "provider-catalog", "2", new Uri("https://example.test/provider"), new string('C', 64), "provider-license", "provider-v2");
        var catalog = new MetadataCatalog([], actual, "provider-import-v2");
        var request = CreateRequestWithMetadata(actual with { Version = "wrong" });

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await new VisibleSceneBuilder(catalog).BuildAsync(request).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsFalse(catalog.QueryStarted);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CatalogProviderMetadataIsRetainedAndChangesProjectedIdentity()
    {
        var firstMetadata = new CatalogMetadata(
            "provider-catalog", "1", new Uri("https://example.test/provider"), new string('C', 64), "provider-license", "provider-v1");
        var secondMetadata = firstMetadata with { Version = "2", Checksum = new string('D', 64), SchemaVersion = "provider-v2" };
        var firstVisible = await new VisibleSceneBuilder(new MetadataCatalog([], firstMetadata, "import-v1"))
            .BuildAsync(CreateRequestWithMetadata(firstMetadata)).ConfigureAwait(false);
        var secondVisible = await new VisibleSceneBuilder(new MetadataCatalog([], secondMetadata, "import-v2"))
            .BuildAsync(CreateRequestWithMetadata(secondMetadata)).ConfigureAwait(false);
        var first = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, firstVisible, Readout(200, 200), Source(), "calibration-v1", "perspective-v1");
        var second = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, secondVisible, Readout(200, 200), Source(), "calibration-v1", "perspective-v1");

        Assert.AreSame(firstMetadata, firstVisible.Request.CatalogMetadata);
        Assert.AreEqual("provider-catalog", first.Catalog.Name);
        Assert.AreEqual(firstMetadata.SourceUrl, first.Catalog.SourceUrl);
        Assert.AreEqual(firstMetadata.License, first.Catalog.License);
        Assert.AreEqual("import-v1", first.Catalog.PreprocessingVersion);
        Assert.AreEqual(new string('C', 64), first.Catalog.ChecksumSha256);
        Assert.AreNotEqual(first.SceneIdentitySha256, second.SceneIdentitySha256);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ValidationEnforcesHorizonApertureAndUniqueObjectIdentifiers()
    {
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, await BuildSceneAsync(ProjectionModel.EquidistantFisheye).ConfigureAwait(false),
            Readout(200, 200), Source(), "calibration-v1", "fisheye-v1");
        var belowHorizon = scene.Objects[0] with { GeometricHorizontal = new AltAzPoint(-1, 0) };
        var outside = scene.Objects[0] with { Pixel = new PixelPoint(201, 0) };
        var duplicate = scene.Objects[1] with { Id = scene.Objects[0].Id, Magnitude = scene.Objects[0].Magnitude + 0.5 };

        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(Reidentify(scene with { Objects = [belowHorizon, .. scene.Objects.Skip(1)] })));
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(Reidentify(scene with { Objects = [outside, .. scene.Objects.Skip(1)] })));
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(Reidentify(scene with { Objects = [scene.Objects[0], duplicate] })));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ValidationEnforcesObjectVersionsAndExistingProjectorNumerics()
    {
        var visible = await BuildSceneAsync(ProjectionModel.Perspective).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(), "calibration-v1", visible.Request.ProjectionVersion);
        var item = scene.Objects[0];
        var changes = new[]
        {
            item with { CatalogVersion = "other" },
            item with { ProjectionVersion = "other" },
            item with { AlgorithmVersion = "other" },
            item with { CameraDirection = item.CameraDirection with { East = item.CameraDirection.East + 1e-6 } },
            item with { ApparentHorizontal = item.ApparentHorizontal with { AzimuthDegrees = item.ApparentHorizontal.AzimuthDegrees + 0.01 } },
            item with { Pixel = item.Pixel with { X = item.Pixel.X + 1e-6 } }
        };

        foreach (var changed in changes)
        {
            Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(
                Reidentify(scene with { Objects = [changed, .. scene.Objects.Skip(1)] })));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ValidationHonorsDisabledSensorBoundsButAlwaysEnforcesAperture()
    {
        var projection = new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 100, 100, 100, 100, 200, 200,
            ProjectionAperture.Circular, 150, EnforceSensorBounds: false);
        var apparent = new AltAzPoint(20, 90);
        var pixel = ProjectorFactory.Create(projection).Project(apparent)!.Value;
        Assert.IsGreaterThan(200, pixel.X);
        var request = new VisibleSceneRequest(
            EffectiveUtc, new ObserverLocation(0, 0, 0), projection, new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"),
            projectionVersion: "fisheye-v1");
        var basis = CameraBasis.Create(90, 0);
        var item = new ProjectedCelestialObject(
            "off-sensor", "Off sensor", CelestialObjectKind.Star,
            new EquatorialPoint(0, 0), new EquatorialPoint(0, 0), apparent, apparent,
            basis.ToCamera(CameraBasis.FromHorizontal(apparent)), pixel, 1, null,
            "1", "fisheye-v1", request.AlgorithmVersion);
        var visible = new VisibleScene(request, [item]);
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(), "calibration-v1", "fisheye-v1");

        ProjectedSceneJson.Validate(scene);
        var outsideAperture = item with { Pixel = new PixelPoint(251, 100) };
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(
            Reidentify(scene with { Objects = [outsideAperture] })));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DimensionsAreaAndUnsupportedPostReadoutTransformsAreRejectedOrUnrepresentable()
    {
        var visible = await BuildSceneAsync(ProjectionModel.Perspective).ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(ProjectedSceneJson.MaximumDimensionPixels + 1, 200),
            Source(), "calibration-v1", visible.Request.ProjectionVersion));
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(65_536, 65_536),
            Source(), "calibration-v1", visible.Request.ProjectionVersion));
        StringAssert.Contains(ProjectedSceneImageTransformV1.OperationOrder, "crop-bin", StringComparison.Ordinal);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ClippedConstellationGoldenRoundTripsAndRejectsDuplicateOrGappedParts()
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(EffectiveUtc, 0) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new("east", "East", (rightAscension + 22) % 24, 0, 1, HipparcosId: "1"),
            new("west", "West", (rightAscension + 2) % 24, 0, 1, HipparcosId: "2")
        ]);
        var topology = new InMemoryConstellationTopology(
            [new ConstellationSegment("TST", "1", "2")],
            new ConstellationTopologyMetadata(
                "fixture-topology", "1", new Uri("https://example.test/topology"),
                new string('D', 64), "test", "fixture-v1"));
        var request = new VisibleSceneRequest(
            EffectiveUtc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 100, 100, 100, 100, 200, 200,
                ProjectionAperture.Circular, 50, RollDegrees: 17, HorizontalFlip: true),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"),
            projectionVersion: "fisheye-v1", constellationIds: ["TST"]);
        var visible = await new VisibleSceneBuilder(catalog, topology).BuildAsync(request).ConfigureAwait(false);
        Assert.IsGreaterThan(1, visible.Segments.Count);
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(), "calibration-v1", "fisheye-v1");
        var bytes = ProjectedSceneJson.Serialize(scene);
        var parsed = ProjectedSceneJson.Parse(bytes);

        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.AreEqual("927D8ACC5279DEF422EECA3C2DDB034E18854D6E39DA8A938ACF71C82D671C57", scene.SceneIdentitySha256);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, scene.Segments.Count).ToArray(),
            scene.Segments.Select(static segment => segment.PartIndex).ToArray());

        var duplicate = Reidentify(scene with
        {
            Segments = [scene.Segments[0], scene.Segments[0], .. scene.Segments.Skip(2)]
        });
        var gap = Reidentify(scene with
        {
            Segments = [scene.Segments[0], scene.Segments[1] with { PartIndex = 2 }, .. scene.Segments.Skip(2)]
        });
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(duplicate));
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(gap));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task FisheyeAndRectilinearScenesRetainExistingProjectedCoordinates()
    {
        foreach (var model in new[] { ProjectionModel.EquidistantFisheye, ProjectionModel.Perspective })
        {
            var visible = await BuildSceneAsync(model).ConfigureAwait(false);
            var scene = ProjectedSceneJson.Create(
                ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(), "calibration-v1", visible.Request.ProjectionVersion);

            Assert.AreEqual(visible.Objects[0].Pixel.X, scene.Objects[0].Pixel.X, 1e-12);
            Assert.AreEqual(visible.Objects[0].Pixel.Y, scene.Objects[0].Pixel.Y, 1e-12);
            Assert.IsTrue(scene.Objects.All(item => item.Pixel.X is >= 0 and <= 200 && item.Pixel.Y is >= 0 and <= 200));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ReadoutSnapshotRecordsRoiBinRollFlipAndUsesPostBinCoordinates()
    {
        var native = await BuildSceneAsync(ProjectionModel.EquidistantFisheye).ConfigureAwait(false);
        var outputProjection = native.Request.Projection with
        {
            PrincipalPointX = 50,
            PrincipalPointY = 50,
            FocalLengthXPixels = 50,
            FocalLengthYPixels = 50,
            WidthPixels = 100,
            HeightPixels = 100,
            ImageCircleRadiusPixels = 75
        };
        var output = VisibleSceneReadoutTransform.ToOutput(native, outputProjection, 2, 2);
        var readout = ProjectedSceneImageTransformV1.Identity(100, 100);
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.VirtualRenderAuthoritative, output, readout, Source(), "calibration-v1", output.Request.ProjectionVersion);

        Assert.AreEqual(native.Objects[0].Pixel.X / 2, scene.Objects[0].Pixel.X, 1e-12);
        Assert.AreEqual(native.Objects[0].Pixel.Y / 2, scene.Objects[0].Pixel.Y, 1e-12);
        Assert.AreEqual(100, scene.ImageTransform.OutputWidthPixels);
        Assert.AreEqual(17, scene.Projection.RollDegrees);
        Assert.IsTrue(scene.Projection.HorizontalFlip);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ExistingSceneProvenanceRemainsAdditivelyCompatible()
    {
        var legacy = new SceneProvenance(
            "scene", "rig", "catalog", "1", new string('A', 64), "fisheye", "projection", "astronomy", "sensor");

        Assert.AreEqual("scene", legacy.SceneId);
        Assert.IsNull(legacy.SceneUtc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RepresentativeSceneHasBoundedSerializationSizeAndCost()
    {
        const int objectCount = 1_000;
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(EffectiveUtc, 0) / 15;
        var catalog = new InMemoryCelestialCatalog(Enumerable.Range(0, objectCount).Select(index =>
            new CelestialCatalogObject($"object-{index:D4}", $"Object {index:D4}", siderealHours, 0,
                index / 1_000d, HipparcosId: (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var request = new VisibleSceneRequest(
            EffectiveUtc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 100, 100, 100, 100, 200, 200, ProjectionAperture.Rectangular,
                RollDegrees: 17, HorizontalFlip: true),
            new CatalogQuery(2, objectCount),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"));
        var visible = await new VisibleSceneBuilder(catalog).BuildAsync(request).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, Readout(200, 200), Source(), "calibration-v1", visible.Request.ProjectionVersion);

        _ = ProjectedSceneJson.Serialize(scene);
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var bytes = ProjectedSceneJson.Serialize(scene);
        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(bytes);
        }

        Assert.HasCount(objectCount, scene.Objects);
        Assert.IsTrue(bytes.Length < ProjectedSceneJson.MaximumPayloadBytes);
        Assert.IsTrue(compressed.Length < bytes.Length);
        Console.WriteLine(
            $"objects={objectCount};jsonBytes={bytes.Length};brotliBytes={compressed.Length};elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3};allocatedBytes={allocatedBytes}");
    }

    private static ProjectedSceneV1 Reidentify(ProjectedSceneV1 scene) =>
        scene with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(scene) };

    private static ProjectedSceneImageTransformV1 Readout(int width, int height) =>
        ProjectedSceneImageTransformV1.Identity(width, height);

    private static ProjectedSceneSource Source() => new(CaptureId, ArtifactId, new string('A', 64));

    private static VisibleSceneRequest CreateRequestWithMetadata(CatalogMetadata metadata) => new(
        EffectiveUtc,
        new ObserverLocation(0, 0, 0),
        new ProjectionContext(ProjectionModel.Perspective, 100, 100, 100, 100, 200, 200,
            ProjectionAperture.Rectangular, RollDegrees: 17, HorizontalFlip: true),
        new CatalogQuery(6, 10),
        metadata,
        projectionVersion: "perspective-v1");

    private static async Task<VisibleScene> BuildSceneAsync(ProjectionModel model)
    {
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(EffectiveUtc, 0) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new("b", "B", siderealHours, 15, 2, HipparcosId: "2"),
            new("a", "A", siderealHours, 0, 1, HipparcosId: "1")
        ]);
        var projection = model == ProjectionModel.Perspective
            ? new ProjectionContext(model, 100, 100, 100, 100, 200, 200, ProjectionAperture.Rectangular,
                BoresightAltitudeDegrees: 90, RollDegrees: 17, HorizontalFlip: true)
            : new ProjectionContext(model, 100, 100, 100, 100, 200, 200, ProjectionAperture.Circular, 150,
                BoresightAltitudeDegrees: 90, RollDegrees: 17, HorizontalFlip: true);
        var request = new VisibleSceneRequest(
            EffectiveUtc,
            new ObserverLocation(0, 0, 0),
            projection,
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"),
            projectionVersion: model == ProjectionModel.Perspective ? "perspective-v1" : "fisheye-v1");
        return await new VisibleSceneBuilder(catalog).BuildAsync(request).ConfigureAwait(false);
    }

    private sealed class MetadataCatalog(
        IEnumerable<CelestialCatalogObject> objects,
        CatalogMetadata metadata,
        string preprocessingVersion) : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _inner = new(objects);

        public bool QueryStarted { get; private set; }
        public CatalogMetadata Metadata { get; } = metadata;
        public string PreprocessingVersion { get; } = preprocessingVersion;

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _inner.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryStarted = true;
            return _inner.QueryCandidatesAsync(query, cancellationToken);
        }
    }
}
