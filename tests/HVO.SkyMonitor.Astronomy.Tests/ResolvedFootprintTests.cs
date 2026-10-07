using System.Text;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ResolvedFootprintTests
{
    private static readonly DateTimeOffset EffectiveUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Site = new(35.347, -113.878, 0);
    private static readonly CatalogMetadata Metadata =
        new("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1");

    [TestMethod]
    public void Sampler_ZenithCircleIsOneClosedOutlineWithinChordTolerance()
    {
        const double focal = 20_000;
        var request = Request(Fisheye(focal, 4000, 4000, 1900), Site);
        var footprint = ResolvedFootprintSampler.Sample(request, "test:disc", "Disc",
            ResolvedFootprintSourceKind.DeepSkyObject, ResolvedFootprintExtent.Circle(0.25, "fixture"),
            new AltAzPoint(90, 0), null)!;

        Assert.IsFalse(footprint.Clipped);
        Assert.HasCount(1, footprint.Parts);
        Assert.IsTrue(footprint.Parts[0].Closed);
        Assert.IsTrue(footprint.Parts[0].Points.Count >= ResolvedFootprintSampler.BaseSampleCount);
        var expectedRadius = focal * 0.25 * Math.PI / 180;
        var points = footprint.Parts[0].Points;
        foreach (var point in points)
        {
            Assert.AreEqual(expectedRadius, Math.Sqrt(Math.Pow(point.X - 2000, 2) + Math.Pow(point.Y - 2000, 2)), 1e-6);
        }
        for (var index = 0; index < points.Count; index++)
        {
            var from = points[index];
            var to = points[(index + 1) % points.Count];
            var chord = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
            var sagitta = expectedRadius - Math.Sqrt(expectedRadius * expectedRadius - chord * chord / 4);
            Assert.IsTrue(sagitta <= ResolvedFootprintSampler.MaximumChordErrorPixels + 1e-9, $"sagitta {sagitta}");
        }
        Assert.AreEqual(new PixelPoint(2000, 2000), footprint.CenterPixel);
        Assert.IsNull(footprint.RefractionModel);
    }

    [TestMethod]
    public void Sampler_GeometricHorizonCutsADiscStraddlingItIntoOneOpenArc()
    {
        const double focal = 2_000;
        var request = Request(Fisheye(focal, 7000, 7000, 3400), Site);
        var footprint = ResolvedFootprintSampler.Sample(request, "test:disc", "Disc",
            ResolvedFootprintSourceKind.DeepSkyObject, ResolvedFootprintExtent.Circle(2, "fixture"),
            new AltAzPoint(-0.5, 120), null)!;

        Assert.IsTrue(footprint.Clipped);
        Assert.HasCount(1, footprint.Parts);
        Assert.IsFalse(footprint.Parts[0].Closed);
        Assert.IsNull(footprint.CenterPixel, "The centre is below the geometric horizon.");
        var horizonRadius = focal * Math.PI / 2;
        foreach (var end in new[] { footprint.Parts[0].Points[0], footprint.Parts[0].Points[^1] })
        {
            Assert.AreEqual(horizonRadius, Math.Sqrt(Math.Pow(end.X - 3500, 2) + Math.Pow(end.Y - 3500, 2)), 1e-3);
        }
        foreach (var point in footprint.Parts[0].Points)
        {
            Assert.IsTrue(Math.Sqrt(Math.Pow(point.X - 3500, 2) + Math.Pow(point.Y - 3500, 2)) <= horizonRadius + 1e-6);
        }
    }

    [TestMethod]
    public void Sampler_SensorEdgeClipsOutlineAndLeavesNoPointOutsideTheFrame()
    {
        var request = Request(new ProjectionContext(ProjectionModel.Perspective, 50, 50, 2000, 2000, 100, 100,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 45, BoresightAzimuthDegrees: 180), Site);
        var footprint = ResolvedFootprintSampler.Sample(request, "test:disc", "Disc",
            ResolvedFootprintSourceKind.DeepSkyObject, ResolvedFootprintExtent.Circle(1, "fixture"),
            new AltAzPoint(45, 181.5), null)!;

        Assert.IsTrue(footprint.Clipped);
        Assert.IsTrue(footprint.Parts.All(static part => !part.Closed));
        Assert.IsTrue(footprint.Parts.SelectMany(static part => part.Points)
            .All(static point => point.X is >= 0 and <= 100 && point.Y is >= 0 and <= 100));
        Assert.IsTrue(Math.Abs(footprint.Bounds.MinX) < 1e-9 || Math.Abs(footprint.Bounds.MaxX - 100) < 1e-9);
    }

    [TestMethod]
    public void Sampler_PositionAngleIsMeasuredFromCelestialNorthThroughEast()
    {
        // At the equator the zenith frame has celestial north on image-up and east on image-right.
        var observer = new ObserverLocation(0, 0, 0);
        var request = Request(Fisheye(10_000, 4000, 4000, 1900), observer);
        ProjectedResolvedFootprint Ellipse(double positionAngle) => ResolvedFootprintSampler.Sample(request,
            "test:galaxy", "Galaxy", ResolvedFootprintSourceKind.DeepSkyObject,
            new ResolvedFootprintExtent(ResolvedFootprintShape.Ellipse, 2, 1, positionAngle, 0.1, null, null, "fixture"),
            new AltAzPoint(90, 0), null)!;

        var northSouth = Ellipse(0);
        var eastWest = Ellipse(90);
        var majorPixels = 10_000 * 2 * Math.PI / 180;
        var minorPixels = 10_000 * 1 * Math.PI / 180;
        Assert.AreEqual(2 * minorPixels, northSouth.Bounds.MaxX - northSouth.Bounds.MinX, 0.5);
        Assert.AreEqual(2 * majorPixels, northSouth.Bounds.MaxY - northSouth.Bounds.MinY, 0.5);
        Assert.AreEqual(2 * majorPixels, eastWest.Bounds.MaxX - eastWest.Bounds.MinX, 0.5);
        Assert.AreEqual(2 * minorPixels, eastWest.Bounds.MaxY - eastWest.Bounds.MinY, 0.5);
    }

    [TestMethod]
    public void Sampler_RefractsEachLimbSampleAndRecordsTheModel()
    {
        var projection = Fisheye(3000, 10_000, 10_000, 4900);
        var refracted = Request(projection, Site, new RefractionOptions(true));
        var geometric = Request(projection, Site);
        var center = new AltAzPoint(1, 200);
        var withRefraction = ResolvedFootprintSampler.Sample(refracted, "test:disc", "Disc",
            ResolvedFootprintSourceKind.DeepSkyObject, ResolvedFootprintExtent.Circle(0.5, "fixture"), center, null)!;
        var without = ResolvedFootprintSampler.Sample(geometric, "test:disc", "Disc",
            ResolvedFootprintSourceKind.DeepSkyObject, ResolvedFootprintExtent.Circle(0.5, "fixture"), center, null)!;

        Assert.AreEqual(AtmosphericRefraction.ModelVersion, withRefraction.RefractionModel);
        Assert.AreEqual(AtmosphericRefraction.Apply(1, new RefractionOptions(true)),
            withRefraction.ApparentCenter.AltitudeDegrees, 1e-12);
        static double Height(ProjectedResolvedFootprint value) =>
            Math.Max(value.Bounds.MaxX - value.Bounds.MinX, value.Bounds.MaxY - value.Bounds.MinY);
        // Refraction lifts the lower limb more than the upper limb, flattening the disc near the horizon.
        Assert.IsTrue(Height(withRefraction) < Height(without));
    }

    [TestMethod]
    public void Frame_LocalAxesMatchTheHorizontalDerivatives()
    {
        var frame = ResolvedSourceFrame.Create(new AltAzPoint(30, 70), Site.LatitudeDegrees);
        var upward = CameraBasis.ToHorizontal(frame.DirectionFromLocal(1e-6, 0));
        var eastward = CameraBasis.ToHorizontal(frame.DirectionFromLocal(0, 1e-6));

        Assert.IsTrue(upward.AltitudeDegrees > 30);
        Assert.AreEqual(70, upward.AzimuthDegrees, 1e-9);
        Assert.IsTrue(eastward.AzimuthDegrees > 70);
        Assert.AreEqual(0, EnuVector.Dot(frame.CelestialNorth, frame.Center), 1e-12);
        Assert.AreEqual(0, EnuVector.Dot(frame.CelestialEast, frame.CelestialNorth), 1e-12);
        Assert.AreEqual(1, frame.CelestialEast.Length, 1e-12);
    }

    [TestMethod]
    public async Task WithResolvedBodies_UsesTopocentricMoonAndKeepsOutlineWhenCentreIsOffFrame()
    {
        var utc = FindMoonAltitude(EffectiveUtc, 25, 60);
        var moon = SolarDiskEphemeris.Get(SolarSystemBody.Moon, utc, Site);
        var scene = await BuildAsync(utc, Fisheye(1000, 3200, 3200, 1590)).ConfigureAwait(false);
        var geocentric = scene.Objects.Single(static item => item.Id == "solar-system:Moon");

        var resolved = scene.WithResolvedBodies([moon]);
        var moonObject = resolved.Objects.Single(static item => item.Id == "solar-system:Moon");
        var footprint = resolved.ResolvedFootprints.Single();

        Assert.AreEqual(moon.Direction, moonObject.GeometricHorizontal);
        Assert.AreEqual(moonObject.ApparentHorizontal, footprint.ApparentCenter);
        Assert.AreEqual(moonObject.Pixel, footprint.CenterPixel);
        Assert.AreEqual(SolarDiskEphemeris.AlgorithmVersion, footprint.Appearance!.EphemerisAlgorithmVersion);
        Assert.AreEqual(moon.AngularRadiusDegrees, footprint.Extent.SemiMajorAxisDegrees);
        // Lunar diurnal parallax lowers the topocentric Moon by up to about one degree.
        Assert.IsTrue(geocentric.GeometricHorizontal.AltitudeDegrees - moonObject.GeometricHorizontal.AltitudeDegrees > 0.3);
        var j2000Round = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(moonObject.J2000Equatorial, utc), utc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        Assert.AreEqual(moonObject.GeometricHorizontal.AltitudeDegrees, j2000Round.AltitudeDegrees, 1e-9);
        Assert.IsTrue(resolved.Objects.SequenceEqual(resolved.Objects.OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)));

        var unselected = await BuildAsync(utc, Fisheye(1000, 3200, 3200, 1590), []).ConfigureAwait(false);
        Assert.IsEmpty(unselected.WithResolvedBodies([moon]).ResolvedFootprints);
        Assert.ThrowsExactly<ArgumentException>(() => scene.WithResolvedBodies(
            [SolarDiskEphemeris.Get(SolarSystemBody.Moon, utc.AddMinutes(1), Site)]));
    }

    [TestMethod]
    public async Task ProjectedScene_EmitsV2OnlyWithFootprintsAndRoundTripsCanonically()
    {
        var utc = FindMoonAltitude(EffectiveUtc, 25, 60);
        var scene = await BuildAsync(utc, Fisheye(1000, 3200, 3200, 1590)).ConfigureAwait(false);
        var v1 = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, scene,
            ProjectedSceneImageTransformV1.Identity(3200, 3200), Source(), "calibration-v1", scene.Request.ProjectionVersion);
        var v2 = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative,
            scene.WithResolvedBodies([SolarDiskEphemeris.Get(SolarSystemBody.Moon, utc, Site)]),
            ProjectedSceneImageTransformV1.Identity(3200, 3200), Source(), "calibration-v1", scene.Request.ProjectionVersion);

        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, v1.SchemaVersion);
        Assert.IsNull(v1.ResolvedFootprints);
        var v1Json = Encoding.UTF8.GetString(ProjectedSceneJson.Serialize(v1));
        Assert.DoesNotContain("resolvedFootprints", v1Json);
        Assert.AreEqual(ProjectedSceneV1.ResolvedFootprintSchemaVersion, v2.SchemaVersion);
        var bytes = ProjectedSceneJson.Serialize(v2);
        var parsed = ProjectedSceneJson.Parse(bytes);
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.AreEqual(v2.SceneIdentitySha256, parsed.Scene!.SceneIdentitySha256);
        CollectionAssert.AreEqual(bytes, ProjectedSceneJson.Serialize(parsed.Scene));
        Assert.IsTrue(ProjectedSceneV1.IsSupportedSchemaVersion(v2.SchemaVersion));
        Assert.IsTrue(SceneProvenance.IsRetainedProjectedSceneSchemaVersion(v1.SchemaVersion));
        Assert.IsTrue(ProjectedSceneV1.IsSupportedSchemaVersion(ProjectedSceneV1.DeepSkySchemaVersion));
        Assert.IsFalse(ProjectedSceneV1.IsSupportedSchemaVersion("projected-scene-v4"));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<PixelPoint>)parsed.Scene.ResolvedFootprints![0].Parts[0].Points).Clear());
    }

    [TestMethod]
    public async Task ProjectedScene_RejectsInconsistentFootprints()
    {
        var utc = FindMoonAltitude(EffectiveUtc, 25, 60);
        var scene = await BuildAsync(utc, Fisheye(1000, 3200, 3200, 1590)).ConfigureAwait(false);
        var v2 = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative,
            scene.WithResolvedBodies([SolarDiskEphemeris.Get(SolarSystemBody.Moon, utc, Site)]),
            ProjectedSceneImageTransformV1.Identity(3200, 3200), Source(), "calibration-v1", scene.Request.ProjectionVersion);
        var footprint = v2.ResolvedFootprints![0];
        ProjectedSceneV1 With(ProjectedResolvedFootprint changed) =>
            Reidentify(v2 with { ResolvedFootprints = [changed] });

        var invalid = new[]
        {
            Reidentify(v2 with { SchemaVersion = ProjectedSceneV1.CurrentSchemaVersion }),
            Reidentify(v2 with { ResolvedFootprints = null }),
            Reidentify(v2 with { ResolvedFootprints = [] }),
            Reidentify(v2 with { ResolvedFootprints = [footprint, footprint] }),
            With(footprint with { ContractVersion = "resolved-footprint-v0" }),
            With(footprint with { RefractionModel = AtmosphericRefraction.ModelVersion }),
            With(footprint with { ApparentCenter = footprint.ApparentCenter with { AltitudeDegrees = footprint.ApparentCenter.AltitudeDegrees + 0.01 } }),
            With(footprint with { CenterPixel = new PixelPoint(footprint.CenterPixel!.Value.X + 1, footprint.CenterPixel.Value.Y) }),
            With(footprint with { CenterPixel = null }),
            With(footprint with { Clipped = !footprint.Clipped }),
            With(footprint with { Bounds = footprint.Bounds with { MaxX = footprint.Bounds.MaxX + 1 } }),
            With(footprint with { Appearance = null }),
            With(footprint with { Id = "solar-system:Sun" }),
            With(footprint with { Extent = footprint.Extent with { PositionAngleDegrees = 10 } }),
            With(footprint with { Extent = footprint.Extent with { SemiMinorAxisUncertaintyDegrees = -1 } }),
            With(footprint with { Parts = [footprint.Parts[0] with { Points = footprint.Parts[0].Points.Take(2).ToArray() }] }),
            With(footprint with { SourceKind = ResolvedFootprintSourceKind.DeepSkyObject }),
            With(footprint with { Parts = [footprint.Parts[0] with { Points = [new PixelPoint(-5, 0), .. footprint.Parts[0].Points] }] })
        };
        foreach (var candidate in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(candidate));
        }

        var json = Encoding.UTF8.GetString(ProjectedSceneJson.Serialize(v2));
        foreach (var malformed in new[]
        {
            json.Replace("\"clipped\":false,", string.Empty, StringComparison.Ordinal),
            json.Replace("\"refractionModel\":null,", string.Empty, StringComparison.Ordinal),
            json.Replace("\"resolvedFootprints\":[", "\"resolvedFootprints\":{\"x\":[", StringComparison.Ordinal),
            json.Replace("\"schemaVersion\":\"projected-scene-v2\"", "\"schemaVersion\":\"projected-scene-v1\"", StringComparison.Ordinal)
        })
        {
            Assert.AreNotEqual(json, malformed);
            Assert.IsFalse(ProjectedSceneJson.Parse(Encoding.UTF8.GetBytes(malformed)).IsValid);
        }
    }

    [TestMethod]
    public async Task ImageTransformAppliesOnceAndCropOpensAClosedOutline()
    {
        var utc = FindMoonAltitude(EffectiveUtc, 25, 60);
        var scene = (await BuildAsync(utc, Fisheye(4000, 12_000, 12_000, 5990)).ConfigureAwait(false))
            .WithResolvedBodies([SolarDiskEphemeris.Get(SolarSystemBody.Moon, utc, Site)]);
        var source = scene.ResolvedFootprints.Single();
        Assert.IsFalse(source.Clipped);
        var center = source.CenterPixel!.Value;

        var whole = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion, 12_000, 12_000,
            (int)center.X - 400, (int)center.Y - 300, 800, 600, 2, 2, true, false,
            ProjectedSceneQuarterRotation.Degrees90, 300, 400);
        var transformed = ProjectedSceneImageTransform.CreateGeometrySnapshot(scene, whole).ResolvedFootprints!.Single();
        Assert.IsFalse(transformed.Clipped);
        Assert.AreEqual(ProjectedSceneImageTransform.Apply(whole, center), transformed.CenterPixel);
        CollectionAssert.AreEqual(source.Parts[0].Points.Select(point => ProjectedSceneImageTransform.Apply(whole, point)).ToArray(),
            transformed.Parts[0].Points.ToArray());

        var halved = whole with { CropX = (int)center.X, CropWidth = 400, OutputWidthPixels = 300, OutputHeightPixels = 200 };
        var cut = ProjectedSceneImageTransform.CreateGeometrySnapshot(scene, halved).ResolvedFootprints!.Single();
        Assert.IsTrue(cut.Clipped);
        Assert.HasCount(1, cut.Parts);
        Assert.IsFalse(cut.Parts[0].Closed);
        Assert.IsTrue(cut.Parts[0].Points.All(static point => point.X is >= 0 and <= 300 && point.Y is >= 0 and <= 200));

        var missed = whole with { CropX = 0, CropY = 0 };
        Assert.IsEmpty(ProjectedSceneImageTransform.CreateGeometrySnapshot(scene, missed).ResolvedFootprints!);

        var binned = VisibleSceneReadoutTransform.ToOutput(scene, Fisheye(2000, 6000, 6000, 2995), 2, 2);
        var scaled = binned.ResolvedFootprints.Single();
        Assert.AreEqual(new PixelPoint(center.X / 2, center.Y / 2), scaled.CenterPixel);
        Assert.AreEqual(source.Bounds.MaxX / 2, scaled.Bounds.MaxX, 1e-12);
    }

    private static DateTimeOffset FindMoonAltitude(DateTimeOffset start, double minimum, double maximum)
    {
        for (var utc = start; utc < start.AddDays(3); utc = utc.AddMinutes(30))
        {
            var altitude = SolarDiskEphemeris.Get(SolarSystemBody.Moon, utc, Site).Direction.AltitudeDegrees;
            if (altitude >= minimum && altitude <= maximum) return utc;
        }
        throw new InvalidOperationException("No fixture instant found.");
    }

    private static ProjectionContext Fisheye(double focal, int width, int height, double imageCircle) =>
        new(ProjectionModel.EquidistantFisheye, width / 2d, height / 2d, focal, focal, width, height,
            ProjectionAperture.Circular, imageCircle, BoresightAltitudeDegrees: 90);

    private static VisibleSceneRequest Request(
        ProjectionContext projection,
        ObserverLocation observer,
        RefractionOptions refraction = default,
        IReadOnlyList<SolarSystemBody>? bodies = null,
        DateTimeOffset? utc = null) => new(
        utc ?? EffectiveUtc, observer, projection, new CatalogQuery(6, 10), Metadata, refraction,
        projectionVersion: "fisheye-v1", solarSystemBodies: bodies);

    private static async Task<VisibleScene> BuildAsync(
        DateTimeOffset utc,
        ProjectionContext projection,
        IReadOnlyList<SolarSystemBody>? bodies = null) =>
        await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, new AstronomyEnginePlanetEphemeris())
            .BuildAsync(Request(projection, Site, default, bodies ?? [SolarSystemBody.Moon], utc)).ConfigureAwait(false);

    private static ProjectedSceneSource Source() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        new string('A', 64));

    private static ProjectedSceneV1 Reidentify(ProjectedSceneV1 scene) =>
        scene with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(scene) };
}
