using System.Globalization;
using System.Text;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DeepSkySceneTests
{
    private const double Focal = 2_000;
    private static readonly DateTimeOffset EffectiveUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Site = new(35.347, -113.878, 0);
    private static readonly string[] FootprintIds = ["deep-sky:NGC0006", "deep-sky:NGC5457", "deep-sky:NGC6720"];
    private static readonly CatalogMetadata Metadata =
        new("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1");
    private static readonly DeepSkySemantics Semantics = new(
        "OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b", new Uri("https://github.com/mattiaverga/OpenNGC"),
        "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
        "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest",
        "b-mag-per-square-arcsecond-within-25-mag-isophote");
    private static readonly string[] V1AndV2 =
        [ProjectedSceneV1.CurrentSchemaVersion, ProjectedSceneV1.ResolvedFootprintSchemaVersion];

    [TestMethod]
    public async Task WithDeepSky_GivesEachObjectTheRichestRepresentationItsGeometryAllows()
    {
        var andromeda = At(60, 30, EffectiveUtc);
        var catalog = Catalog(
            [
                Galaxy("NGC0224", andromeda, 190, 60, 35, messier: 31, common: "Andromeda Galaxy"),
                Galaxy("NGC5457", At(70, 120, EffectiveUtc), 24, 23, 28, messier: 101),
                Galaxy("NGC6720", At(50, 200, EffectiveUtc), 20, 20, null, messier: 57, type: "PN"),
                Galaxy("NGC0001", At(65, 250, EffectiveUtc), 30, null, null),
                Galaxy("NGC0002", At(55, 300, EffectiveUtc), 30, 10, null),
                Galaxy("NGC0003", At(45, 340, EffectiveUtc), 2, null, null),
                Galaxy("NGC0004", At(75, 80, EffectiveUtc), null, null, null, type: "Neb"),
                Galaxy("NGC0005", At(-20, 10, EffectiveUtc), null, null, null),
                Galaxy("NGC0006", At(-0.3, 45, EffectiveUtc), 120, 60, 90),
                Galaxy("NGC1990", At(50, 60, EffectiveUtc), null, null, null, type: DeepSkyObjectTypes.Star),
                new DeepSkyObject("M040", "M40", "M40", DeepSkyObjectTypes.DoubleStar, At(40, 160, EffectiveUtc).Ra,
                    At(40, 160, EffectiveUtc).Dec, "UMa", null, null, null, null, 8, null, null, 40, null, null, null)
            ],
            [Square("NGC0224", 1, andromeda, 1.5), Square("NGC0224", 3, andromeda, 0.5)]);

        var scene = (await BuildAsync(EffectiveUtc, Fisheye()).ConfigureAwait(false))
            .WithDeepSky(catalog, ProjectedSceneDeepSkySelection.Default);

        var section = scene.DeepSky!;
        var byId = section.Objects.ToDictionary(static item => item.CatalogObjectId, StringComparer.Ordinal);
        Assert.AreEqual(DeepSkyRepresentation.Outline, byId["NGC0224"].Representation);
        Assert.AreEqual(DeepSkyOutline.WidestLevel, byId["NGC0224"].OutlineLevel);
        Assert.IsTrue(byId["NGC0224"].Featured);
        Assert.AreEqual(DeepSkyRepresentation.Footprint, byId["NGC5457"].Representation);
        Assert.AreEqual(DeepSkyRepresentation.Footprint, byId["NGC6720"].Representation, "Equal axes need no position angle.");
        Assert.AreEqual(DeepSkyRepresentation.SizedGlyph, byId["NGC0001"].Representation, "A major axis alone is a glyph.");
        Assert.AreEqual(DeepSkyRepresentation.SizedGlyph, byId["NGC0002"].Representation, "Unequal axes need a position angle.");
        Assert.IsNull(byId["NGC0002"].PositionAngleDegrees);
        Assert.AreEqual(DeepSkyRepresentation.MinimumGlyph, byId["NGC0003"].Representation);
        Assert.AreEqual(DeepSkyRepresentation.UnknownExtentGlyph, byId["NGC0004"].Representation);
        Assert.IsNull(byId["NGC0004"].MajorAxisArcminutes, "An unknown extent stays null, never zero.");
        Assert.AreEqual(DeepSkyRepresentation.StellarGlyph, byId["M040"].Representation);
        Assert.AreEqual(DeepSkyRepresentation.Footprint, byId["NGC0006"].Representation);
        Assert.IsNull(byId["NGC0006"].Pixel, "A centre below the geometric horizon has no pixel.");
        Assert.IsFalse(byId.ContainsKey("NGC1990"), "A stellar row without a Messier number is identity only.");
        Assert.IsFalse(byId.ContainsKey("NGC0005"), "An object below the horizon without extent cannot reach the field.");
        Assert.IsFalse(byId.Values.Any(static item => item.Degradation is not null));
        Assert.IsTrue(section.Objects.All(static item => item.Id.StartsWith(ProjectedDeepSkyObject.IdPrefix, StringComparison.Ordinal)));
        Assert.IsFalse(scene.Objects.Any(item => item.Id.StartsWith(ProjectedDeepSkyObject.IdPrefix, StringComparison.Ordinal)),
            "Deep-sky objects are never stars or reference candidates.");

        var outline = section.Outlines.Single();
        Assert.AreEqual("deep-sky:NGC0224", outline.Id);
        Assert.IsFalse(outline.Clipped);
        var deepSkyFootprints = scene.ResolvedFootprints
            .Where(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject).ToArray();
        CollectionAssert.AreEqual(
            FootprintIds,
            deepSkyFootprints.Select(static item => item.Id).ToArray());
        Assert.AreEqual(ResolvedFootprintShape.Circle, deepSkyFootprints[2].Extent.Shape);
        Assert.IsTrue(deepSkyFootprints[0].Clipped);
        // Every placed object is drawn by exactly one geometry.
        var drawn = section.Outlines.Select(static item => item.Id).Concat(deepSkyFootprints.Select(static item => item.Id))
            .ToArray();
        Assert.AreEqual(drawn.Length, drawn.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, scene.ProjectedSceneSchemaVersion);
    }

    [TestMethod]
    public void FootprintPositionAngleTurnsFromJ2000NorthToNorthOfDate()
    {
        var j2000 = new EquatorialPoint(6, 80);
        Assert.AreEqual(30, DeepSkySceneProjector.PositionAngleOfDate(6, 80, 30,
            new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero)), 1e-4);

        var ofDate = DeepSkySceneProjector.PositionAngleOfDate(6, 80, 30, EffectiveUtc);

        // About n sin(ra) sec(dec) t = 20.04" x 5.76 x 25 years, so near the pole the turn is most of a degree.
        Assert.AreEqual(.8, Math.Abs(ofDate - 30), .05);
        // A point one degree along the axis of date, rotated back to J2000, lies on the catalogue's axis.
        var along = EquatorialPrecession.PrecessToJ2000(
            Destination(EquatorialPrecession.PrecessJ2000(j2000, EffectiveUtc), ofDate, 1), EffectiveUtc);
        Assert.AreEqual(30, DeepSkySceneProjector.Bearing(j2000, along), 1e-9);
    }

    [TestMethod]
    public async Task ProjectedScene_V3RoundTripsAndAV2OnlyReaderFailsClosedOnTheSchemaVersion()
    {
        var projected = await CreateV3Async().ConfigureAwait(false);

        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, projected.SchemaVersion);
        Assert.AreEqual(ProjectedSceneDeepSkySelection.Default, projected.Selection.DeepSky);
        var bytes = ProjectedSceneJson.Serialize(projected);
        var parsed = ProjectedSceneJson.Parse(bytes);
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.AreEqual(projected.SceneIdentitySha256, parsed.Scene!.SceneIdentitySha256);
        CollectionAssert.AreEqual(bytes, ProjectedSceneJson.Serialize(parsed.Scene));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<ProjectedDeepSkyObject>)parsed.Scene.DeepSky!.Objects).Clear());

        var v2Reader = ProjectedSceneJson.Parse(bytes, V1AndV2);
        Assert.IsFalse(v2Reader.IsValid);
        Assert.AreEqual("$schemaVersion", v2Reader.ErrorPath);
    }

    [TestMethod]
    public async Task ProjectedScene_WithoutDeepSkyKeepsItsV1AndV2BytesAndAnEmptyPlacementChangesNothing()
    {
        var plain = await BuildAsync(EffectiveUtc, Fisheye()).ConfigureAwait(false);
        var json = Encoding.UTF8.GetString(ProjectedSceneJson.Serialize(Create(plain)));
        Assert.DoesNotContain("deepSky", json);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, plain.ProjectedSceneSchemaVersion);

        var belowHorizon = Catalog([Galaxy("NGC0005", At(-20, 10, EffectiveUtc), null, null, null)]);
        Assert.AreSame(plain, plain.WithDeepSky(belowHorizon, ProjectedSceneDeepSkySelection.Default));

        var utc = FindMoonAltitude(EffectiveUtc, 25, 60);
        var v2 = (await BuildAsync(utc, Fisheye(), [SolarSystemBody.Moon]).ConfigureAwait(false))
            .WithResolvedBodies([SolarDiskEphemeris.Get(SolarSystemBody.Moon, utc, Site)]);
        var v2Bytes = ProjectedSceneJson.Serialize(Create(v2));
        // The v2 scene is at a later instant, where the first object may have risen, so it gets its own.
        var belowHorizonAtV2 = Catalog([Galaxy("NGC0005", At(-20, 10, utc), null, null, null)]);
        Assert.AreSame(v2, v2.WithDeepSky(belowHorizonAtV2, ProjectedSceneDeepSkySelection.Default));
        Assert.AreEqual(ProjectedSceneV1.ResolvedFootprintSchemaVersion, ProjectedSceneJson.Parse(v2Bytes).Scene!.SchemaVersion);
        Assert.DoesNotContain("deepSky", Encoding.UTF8.GetString(v2Bytes));
    }

    [TestMethod]
    [DataRow(0, DisplayName = "exactly the slots the Moon leaves")]
    [DataRow(1, DisplayName = "one over")]
    public async Task FootprintBudget_ServesTheMoonFirstAndRecordsTheOverflow(int extra)
    {
        var utc = FindMoonAltitude(EffectiveUtc, 25, 60);
        var scene = (await BuildAsync(utc, Fisheye(), [SolarSystemBody.Moon]).ConfigureAwait(false))
            .WithResolvedBodies([SolarDiskEphemeris.Get(SolarSystemBody.Moon, utc, Site)]);
        var solar = scene.ResolvedFootprints.Count;
        Assert.AreEqual(1, solar);
        var slots = ProjectedSceneJson.MaximumResolvedFootprintCount - solar;
        // Larger objects rank first, so the smallest is the one an overflow degrades.
        var objects = Enumerable.Range(0, slots)
            .Select(index => Galaxy(
                "NGC" + (1000 + index).ToString(CultureInfo.InvariantCulture),
                At(30 + index % 6 * 9, index / 6 * 33, utc), 60 + index, 30, 10))
            .ToList();
        if (extra == 1) objects.Add(Galaxy("NGC2000", At(50, 15, utc), 20, 10, 10));

        var placed = scene.WithDeepSky(Catalog(objects), ProjectedSceneDeepSkySelection.Default);

        Assert.AreEqual(ProjectedSceneJson.MaximumResolvedFootprintCount, placed.ResolvedFootprints.Count);
        Assert.AreEqual(1, placed.ResolvedFootprints.Count(static item => item.SourceKind == ResolvedFootprintSourceKind.SolarSystemBody));
        var degraded = placed.DeepSky!.Objects.Where(static item => item.Degradation is not null).ToArray();
        if (extra == 0)
        {
            Assert.AreEqual(0, degraded.Length);
            Assert.IsTrue(placed.DeepSky.Objects.All(static item => item.Representation == DeepSkyRepresentation.Footprint));
        }
        else
        {
            Assert.AreEqual(1, degraded.Length);
            Assert.AreEqual("deep-sky:NGC2000", degraded[0].Id);
            Assert.AreEqual(DeepSkyDegradation.FootprintLimit, degraded[0].Degradation);
            Assert.AreEqual(DeepSkyRepresentation.SizedGlyph, degraded[0].Representation);
        }
        var projected = Create(placed);
        Assert.AreEqual(ProjectedSceneJson.MaximumResolvedFootprintCount, projected.ResolvedFootprints!.Count);
        Assert.IsTrue(ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(projected)).IsValid);
    }

    [TestMethod]
    public async Task OutlineBudgetAndPreferredLevelAreRecordedOnTheObjectAndSelection()
    {
        var andromeda = At(60, 30, EffectiveUtc);
        var other = At(50, 220, EffectiveUtc);
        var catalog = Catalog(
            [
                Galaxy("NGC0224", andromeda, 190, 60, 35, messier: 31, common: "Andromeda Galaxy"),
                Galaxy("NGC0007", other, 100, 50, 10)
            ],
            [
                Square("NGC0224", 1, andromeda, 1.5), Square("NGC0224", 3, andromeda, 0.5),
                Square("NGC0007", 2, other, 0.8)
            ]);
        var selection = ProjectedSceneDeepSkySelection.Default with { MaximumOutlines = 1, PreferredOutlineLevel = 3 };

        var scene = (await BuildAsync(EffectiveUtc, Fisheye()).ConfigureAwait(false)).WithDeepSky(catalog, selection);

        var byId = scene.DeepSky!.Objects.ToDictionary(static item => item.CatalogObjectId, StringComparer.Ordinal);
        Assert.AreEqual(DeepSkyRepresentation.Outline, byId["NGC0224"].Representation);
        Assert.AreEqual(3, byId["NGC0224"].OutlineLevel);
        Assert.AreEqual(3, scene.DeepSky.Outlines.Single().Level);
        Assert.AreEqual(DeepSkyRepresentation.Footprint, byId["NGC0007"].Representation);
        Assert.AreEqual(DeepSkyDegradation.OutlineLimit, byId["NGC0007"].Degradation);
        Assert.IsNull(byId["NGC0007"].OutlineLevel);
        var projected = Create(scene);
        Assert.AreEqual(selection, projected.Selection.DeepSky);
        Assert.AreEqual(3, projected.Selection.DeepSky!.PreferredOutlineLevel);
    }

    [TestMethod]
    public async Task Validate_RejectsADeepSkySceneThatBreaksItsContract()
    {
        var projected = await CreateV3Async().ConfigureAwait(false);
        var section = projected.DeepSky!;
        var footprintObject = section.Objects.First(static item => item.Representation == DeepSkyRepresentation.Footprint);
        var outlineObject = section.Objects.First(static item => item.Representation == DeepSkyRepresentation.Outline);

        AssertRejected(projected with { SchemaVersion = ProjectedSceneV1.ResolvedFootprintSchemaVersion });
        AssertRejected(projected with { Selection = projected.Selection with { DeepSky = null } });
        AssertRejected(projected with { DeepSky = null });
        var remaining = projected.ResolvedFootprints!.Where(item => item.Id != footprintObject.Id).ToArray();
        AssertRejected(projected with { ResolvedFootprints = remaining.Length == 0 ? null : remaining });
        AssertRejected(projected with { DeepSky = section with { Outlines = [] } });
        AssertRejected(projected with
        {
            DeepSky = section with { Objects = Replace(section.Objects, outlineObject, outlineObject with { OutlineLevel = 2 }) }
        });
        AssertRejected(projected with
        {
            DeepSky = section with
            {
                Objects = Replace(section.Objects, footprintObject, footprintObject with { Representation = DeepSkyRepresentation.Outline })
            }
        });
        AssertRejected(projected with
        {
            DeepSky = section with { Objects = Replace(section.Objects, footprintObject, footprintObject with { Id = "NGC5457" }) }
        });
        AssertRejected(projected with
        {
            DeepSky = section with { Objects = section.Objects.Reverse().ToArray() }
        });
        AssertRejected(projected with
        {
            DeepSky = section with { Objects = Replace(section.Objects, footprintObject, footprintObject with { MinorAxisArcminutes = 0 }) }
        });
        AssertRejected(projected with { DeepSky = section with { OmittedCandidateCount = -1 } });
        AssertRejected(projected with { DeepSky = section with { ContractVersion = "projected-deep-sky-v2" } });
        AssertRejected(projected with
        {
            DeepSky = section with { Outlines = [section.Outlines[0] with { Clipped = true }] }
        });
    }

    [TestMethod]
    public async Task Validate_RejectsAFootprintWhoseShapeOrientationOrSourceContradictsItsCatalogExtent()
    {
        var catalog = Catalog(
            [
                Galaxy("NGC5457", At(70, 120, EffectiveUtc), 24, 23, 28, messier: 101),
                Galaxy("NGC6720", At(50, 200, EffectiveUtc), 20, 20, null, messier: 57, type: "PN")
            ]);
        var projected = Create((await BuildAsync(EffectiveUtc, Fisheye()).ConfigureAwait(false))
            .WithDeepSky(catalog, ProjectedSceneDeepSkySelection.Default));
        ProjectedSceneJson.Validate(projected);
        var section = projected.DeepSky!;
        var ellipse = section.Objects.Single(static item => item.CatalogObjectId == "NGC5457");
        var ellipseFootprint = projected.ResolvedFootprints!.Single(item => item.Id == ellipse.Id);
        var circleFootprint = projected.ResolvedFootprints!.Single(static item => item.Id == "deep-sky:NGC6720");
        Assert.AreEqual(ResolvedFootprintShape.Ellipse, ellipseFootprint.Extent.Shape);
        Assert.AreEqual(ResolvedFootprintShape.Circle, circleFootprint.Extent.Shape);
        Assert.AreNotEqual(28, ellipseFootprint.Extent.PositionAngleDegrees, 1e-6, "The angle of date is not the J2000 angle.");

        // The catalog angle turns a right angle and the footprint stays as drawn, or the reverse.
        AssertRejected(projected with
        {
            DeepSky = section with { Objects = Replace(section.Objects, ellipse, ellipse with { PositionAngleDegrees = 118 }) }
        });
        AssertRejected(WithExtent(projected, ellipseFootprint, ellipseFootprint.Extent with
        {
            PositionAngleDegrees = (ellipseFootprint.Extent.PositionAngleDegrees + 90) % 180
        }));
        // The footprint keeps the catalog's J2000 angle instead of the angle of date.
        AssertRejected(WithExtent(projected, ellipseFootprint, ellipseFootprint.Extent with { PositionAngleDegrees = 28 }));
        // Equal axes are a circle, and unequal axes are an ellipse.
        AssertRejected(WithExtent(projected, circleFootprint, circleFootprint.Extent with { Shape = ResolvedFootprintShape.Ellipse }));
        AssertRejected(WithExtent(projected, ellipseFootprint, ResolvedFootprintExtent.Circle(
            ellipseFootprint.Extent.SemiMajorAxisDegrees, ellipseFootprint.Extent.Source)));
        // The extent names another catalog.
        AssertRejected(WithExtent(projected, ellipseFootprint, ellipseFootprint.Extent with { Source = "OpenNGC v20250101" }));
    }

    [TestMethod]
    [DataRow("fisheye-off-axis")]
    [DataRow("perspective-off-axis")]
    [DataRow("fisheye-horizon")]
    [DataRow("perspective-aperture")]
    public async Task Outline_FollowsIndependentGreatCircleEdgesAndClipsToTheAdmittedSky(string name)
    {
        // Horizon clipping bisects along the arc, so a clipped end stays on it; sensor clipping cuts a chord, so a
        // clipped end lies within the chord tolerance of its arc.
        var (projection, altitude, azimuth, half, clipped, chordTolerance) = name switch
        {
            "fisheye-off-axis" => (Fisheye(), 40d, 100d, 2d, false, 0d),
            "perspective-off-axis" => (Perspective(), 48d, 184d, 1d, false, 0d),
            "fisheye-horizon" => (Fisheye(), 0.5d, 220d, 2d, true, 0d),
            "perspective-aperture" => (Perspective(), 45d, 200d, 2d, true, ResolvedFootprintSampler.MaximumChordErrorPixels),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        var center = At(altitude, azimuth, EffectiveUtc);
        var outline = Square("NGC0008", 1, center, half);
        var catalog = Catalog([Galaxy("NGC0008", center, half * 120, half * 60, 0)], [outline]);

        var scene = (await BuildAsync(EffectiveUtc, projection).ConfigureAwait(false))
            .WithDeepSky(catalog, ProjectedSceneDeepSkySelection.Default);

        var projected = scene.DeepSky!.Outlines.Single();
        Assert.AreEqual(clipped, projected.Clipped);
        if (!clipped)
        {
            Assert.HasCount(1, projected.Parts);
            Assert.IsTrue(projected.Parts[0].Closed);
        }
        var points = projected.Parts.SelectMany(static part => part.Points).ToArray();
        foreach (var point in points)
        {
            Assert.IsTrue(point.X >= -1e-9 && point.X <= projection.WidthPixels + 1e-9 &&
                point.Y >= -1e-9 && point.Y <= projection.HeightPixels + 1e-9, $"{point} is outside the frame.");
        }
        if (name == "fisheye-horizon")
        {
            var horizonRadius = Focal * Math.PI / 2;
            Assert.IsTrue(points.All(point => Radius(point, projection) <= horizonRadius + 1e-6));
        }

        var reference = ReferenceRuns(scene.Request, outline);
        Assert.IsTrue(reference.Sum(static run => run.Count) > 100);
        // A clipped end can lie up to one reference sample beyond the last sample the reference admits.
        var spacing = clipped
            ? reference.SelectMany(static run => run.Zip(run.Skip(1), Distance)).Max()
            : 0;
        Assert.IsTrue(spacing < 0.1, $"{name}: the reference is too coarse ({spacing} px).");
        foreach (var point in points)
        {
            var distance = reference.Min(run => DistanceToPolyline(point, run, closed: false));
            Assert.IsTrue(distance <= 1e-4 + chordTolerance + spacing,
                $"{name}: {point} lies {distance} px from the reference arc.");
        }
        foreach (var expected in reference.SelectMany(static run => run))
        {
            var distance = projected.Parts.Min(part => DistanceToPolyline(expected, part.Points, part.Closed));
            Assert.IsTrue(distance <= ResolvedFootprintSampler.MaximumChordErrorPixels + spacing + 1e-3,
                $"{name}: the reference arc at {expected} is {distance} px from the outline.");
        }
        Assert.IsTrue(ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(Create(scene))).IsValid);
    }

    [TestMethod]
    [DataRow("fisheye-zenith")]
    [DataRow("fisheye-narrow")]
    [DataRow("fisheye-distorted")]
    [DataRow("perspective")]
    [DataRow("perspective-distorted")]
    [DataRow("perspective-unbounded")]
    public async Task RegionQuery_PlacesTheSameBytesAsScanningEveryObject(string name)
    {
        var projection = name switch
        {
            "fisheye-zenith" => Fisheye(),
            "fisheye-narrow" => Fisheye() with
            {
                ImageCircleRadiusPixels = 1200,
                BoresightAltitudeDegrees = 35,
                BoresightAzimuthDegrees = 250,
                RollDegrees = 10
            },
            "fisheye-distorted" => new ProjectionContext(ProjectionModel.EquidistantFisheye, 259.5, 253, 150, 150, 512, 512,
                ProjectionAperture.Circular, 232, BoresightAltitudeDegrees: 25, BoresightAzimuthDegrees: 250, RollDegrees: 10,
                HorizontalFlip: true, RadialDistortionK1: -.006),
            "perspective" => Perspective(),
            "perspective-distorted" => new ProjectionContext(ProjectionModel.Perspective, 322, 236, 500, 505, 640, 480,
                ProjectionAperture.Rectangular, null, BoresightAltitudeDegrees: 40, BoresightAzimuthDegrees: 120,
                RollDegrees: 7, RadialDistortionK1: .05),
            "perspective-unbounded" => Perspective() with { EnforceSensorBounds = false },
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        RefractionOptions[] refractions = [default, new(true), new(true, -1.9), new(true, -3)];
        HorizonPolicy[] horizons = [HorizonPolicy.GeometricHorizon, HorizonPolicy.ProjectionOnly];
        DateTimeOffset[] instants = [EffectiveUtc, new(2041, 7, 3, 4, 30, 0, TimeSpan.Zero)];
        var placedFromOutside = 0;
        foreach (var utc in instants)
        {
            var catalog = EdgeCatalog(projection, utc);
            foreach (var refraction in refractions)
            {
                foreach (var horizon in horizons)
                {
                    var label = $"{name} at {utc:O}, {refraction}, {horizon}";
                    var scene = await BuildAsync(utc, projection, refraction: refraction, horizon: horizon).ConfigureAwait(false);
                    var bounded = scene.WithDeepSky(catalog, ProjectedSceneDeepSkySelection.Default, queryRegion: true);
                    var scanned = scene.WithDeepSky(catalog, ProjectedSceneDeepSkySelection.Default, queryRegion: false);

                    Assert.IsNotNull(scanned.DeepSky, label);
                    CollectionAssert.AreEqual(ProjectedSceneJson.Serialize(Create(scanned)),
                        ProjectedSceneJson.Serialize(Create(bounded)), label);
                    var region = DeepSkySceneProjector.CandidateRegion(scene.Request);
                    if (name == "perspective-unbounded" || refraction.Enabled && refraction.MinimumAltitudeDegrees < -1.9)
                    {
                        Assert.IsNull(region, label);
                        continue;
                    }
                    Assert.IsTrue(region.HasValue, label);
                    var cap = region.GetValueOrDefault();
                    var visited = catalog.Query(new DeepSkyQuery(catalog.Objects.Count, cap)).Objects.Count;
                    Assert.IsLessThan(catalog.Objects.Count, visited, $"{label}: the region query left nothing out.");
                    placedFromOutside += bounded.DeepSky!.Objects.Count(item => !cap.Contains(
                        item.J2000Equatorial.RightAscensionHours, item.J2000Equatorial.DeclinationDegrees));
                }
            }
        }
        // An object placed with its centre outside the region shows that the query's extent reach is what kept it.
        if (name != "perspective-unbounded")
            Assert.IsGreaterThan(0, placedFromOutside, $"{name}: no placed object reached the field from outside the region.");
    }

    /// <summary>
    /// Surrounds the boresight with every kind of object the projector distinguishes, at distances inside the field
    /// and on both sides of the candidate region's edge, so extents and outlines straddle it.
    /// </summary>
    private static DeepSkyCatalog EdgeCatalog(ProjectionContext projection, DateTimeOffset utc)
    {
        var boresight = At(projection.BoresightAltitudeDegrees, projection.BoresightAzimuthDegrees, utc);
        var start = new EquatorialPoint(boresight.Ra, boresight.Dec);
        var radius = VisibleSceneBuilder.OpticalRadiusDegrees(projection);
        var edge = radius + 1;
        double[] distances = [0.35 * radius, 0.8 * radius, edge - 1.5, edge - 0.4, edge + 0.3, edge + 0.9, edge + 1.6,
            edge + 2.4, edge + 3.9];
        var objects = new List<DeepSkyObject>();
        var outlines = new List<DeepSkyOutline>();
        var messier = 0;
        foreach (var distance in distances)
        {
            for (var direction = 0; direction < 6; direction++)
            {
                for (var kind = 0; kind < 7; kind++)
                {
                    var id = "NGC" + (objects.Count + 1).ToString("D4", CultureInfo.InvariantCulture);
                    var center = Destination(start, direction * 60 + kind * 7, distance);
                    var at = (center.RightAscensionHours, center.DeclinationDegrees);
                    objects.Add(kind switch
                    {
                        0 => Galaxy(id, at, null, null, null),
                        1 => Galaxy(id, at, 60, 30, 40),
                        2 => Galaxy(id, at, 90, null, null),
                        3 => Galaxy(id, at, 100, 80, 10),
                        4 or 5 => Galaxy(id, at, null, null, null),
                        _ => objects.Count % 2 == 0
                            ? Galaxy(id, at, null, null, null, messier: ++messier, type: DeepSkyObjectTypes.DoubleStar)
                            : Galaxy(id, at, null, null, null, type: DeepSkyObjectTypes.Star)
                    });
                    if (kind is >= 3 and <= 5)
                        outlines.Add(Polygon(id, center, kind switch { 3 => 1.4, 4 => 3.5, _ => 11 }));
                }
            }
        }
        return Catalog(objects, outlines);
    }

    /// <summary>A closed four-vertex widest-level outline whose vertices lie the given angle from a J2000 centre.</summary>
    private static DeepSkyOutline Polygon(string id, EquatorialPoint center, double radius)
    {
        var vertices = Enumerable.Range(0, 4)
            .Select(index => Destination(center, 45 + index * 90, radius))
            .Select(static point => new DeepSkyOutlinePoint(point.RightAscensionHours * 15 % 360, point.DeclinationDegrees))
            .ToArray();
        return new DeepSkyOutline(id, DeepSkyOutline.WidestLevel, [new DeepSkyOutlineRing([.. vertices, vertices[0]])]);
    }

    /// <summary>
    /// Independently samples each outline edge as a great-circle arc between its J2000 vertices, keeping only the
    /// directions above the geometric horizon whose projection lands inside the sensor and aperture.
    /// </summary>
    private static List<List<PixelPoint>> ReferenceRuns(VisibleSceneRequest request, DeepSkyOutline outline)
    {
        const int samplesPerEdge = 2_000;
        var projector = ProjectorFactory.Create(request.Projection);
        var runs = new List<List<PixelPoint>>();
        foreach (var ring in outline.Rings)
        {
            var vertices = ring.Points.Take(ring.Points.Count - 1).Select(point => Unit(Geometric(request, point))).ToArray();
            for (var edge = 0; edge < vertices.Length; edge++)
            {
                var from = vertices[edge];
                var to = vertices[(edge + 1) % vertices.Length];
                List<PixelPoint>? run = null;
                for (var sample = 0; sample <= samplesPerEdge; sample++)
                {
                    var direction = ToHorizontal(Slerp(from, to, (double)sample / samplesPerEdge));
                    if (direction.AltitudeDegrees >= 0 && projector.Project(direction) is { } pixel &&
                        IsInsideFrame(pixel, request.Projection))
                    {
                        if (run is null) runs.Add(run = []);
                        run.Add(pixel);
                    }
                    else
                    {
                        run = null;
                    }
                }
            }
        }
        return runs;
    }

    private static AltAzPoint Geometric(VisibleSceneRequest request, DeepSkyOutlinePoint point)
    {
        var ofDate = EquatorialPrecession.PrecessJ2000(
            new EquatorialPoint(point.RightAscensionDegrees / 15, point.DeclinationDegrees), request.Utc);
        return CoordinateTransforms.EquatorialToHorizontal(
            ofDate, request.Utc, request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees);
    }

    private static double[] Unit(AltAzPoint direction)
    {
        var altitude = direction.AltitudeDegrees * Math.PI / 180;
        var azimuth = direction.AzimuthDegrees * Math.PI / 180;
        return [Math.Cos(altitude) * Math.Sin(azimuth), Math.Cos(altitude) * Math.Cos(azimuth), Math.Sin(altitude)];
    }

    private static AltAzPoint ToHorizontal(double[] vector)
    {
        var azimuth = Math.Atan2(vector[0], vector[1]) * 180 / Math.PI;
        return new AltAzPoint(Math.Asin(Math.Clamp(vector[2], -1, 1)) * 180 / Math.PI, (azimuth + 360) % 360);
    }

    private static double[] Slerp(double[] from, double[] to, double parameter)
    {
        var omega = Math.Acos(Math.Clamp(from[0] * to[0] + from[1] * to[1] + from[2] * to[2], -1, 1));
        var sine = Math.Sin(omega);
        var a = Math.Sin((1 - parameter) * omega) / sine;
        var b = Math.Sin(parameter * omega) / sine;
        return [a * from[0] + b * to[0], a * from[1] + b * to[1], a * from[2] + b * to[2]];
    }

    private static bool IsInsideFrame(PixelPoint pixel, ProjectionContext projection) =>
        pixel.X >= 0 && pixel.X <= projection.WidthPixels && pixel.Y >= 0 && pixel.Y <= projection.HeightPixels &&
        (projection.Aperture != ProjectionAperture.Circular || Radius(pixel, projection) <= projection.ImageCircleRadiusPixels);

    private static double Radius(PixelPoint pixel, ProjectionContext projection) =>
        Math.Sqrt(Math.Pow(pixel.X - projection.PrincipalPointX, 2) + Math.Pow(pixel.Y - projection.PrincipalPointY, 2));

    private static double DistanceToPolyline(PixelPoint point, IReadOnlyList<PixelPoint> line, bool closed)
    {
        if (line.Count == 1) return Distance(point, line[0]);
        var best = double.PositiveInfinity;
        var segments = closed ? line.Count : line.Count - 1;
        for (var index = 0; index < segments; index++)
        {
            best = Math.Min(best, DistanceToSegment(point, line[index], line[(index + 1) % line.Count]));
        }
        return best;
    }

    private static double DistanceToSegment(PixelPoint point, PixelPoint from, PixelPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = dx * dx + dy * dy;
        var t = length == 0 ? 0 : Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / length, 0, 1);
        return Distance(point, new PixelPoint(from.X + t * dx, from.Y + t * dy));
    }

    private static double Distance(PixelPoint left, PixelPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static void AssertRejected(ProjectedSceneV1 scene)
    {
        var reidentified = scene with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(scene) };
        Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneJson.Validate(reidentified));
    }

    private static ProjectedSceneV1 WithExtent(
        ProjectedSceneV1 scene,
        ProjectedResolvedFootprint footprint,
        ResolvedFootprintExtent extent) => scene with
        {
            ResolvedFootprints = scene.ResolvedFootprints!
                .Select(item => item.Id == footprint.Id ? item with { Extent = extent } : item).ToArray()
        };

    private static ProjectedDeepSkyObject[] Replace(
        IReadOnlyList<ProjectedDeepSkyObject> objects,
        ProjectedDeepSkyObject original,
        ProjectedDeepSkyObject replacement)
    {
        var copy = objects.ToArray();
        copy[Array.IndexOf(copy, original)] = replacement;
        return copy;
    }

    private static async Task<ProjectedSceneV1> CreateV3Async()
    {
        var andromeda = At(60, 30, EffectiveUtc);
        var catalog = Catalog(
            [
                Galaxy("NGC0224", andromeda, 190, 60, 35, messier: 31, common: "Andromeda Galaxy"),
                Galaxy("NGC5457", At(70, 120, EffectiveUtc), 24, 23, 28, messier: 101),
                Galaxy("NGC0001", At(65, 250, EffectiveUtc), 30, null, null)
            ],
            [Square("NGC0224", 1, andromeda, 1.5)]);
        var scene = (await BuildAsync(EffectiveUtc, Fisheye()).ConfigureAwait(false))
            .WithDeepSky(catalog, ProjectedSceneDeepSkySelection.Default);
        return Create(scene);
    }

    private static ProjectedSceneV1 Create(VisibleScene scene) => ProjectedSceneJson.Create(
        ProjectedSceneKind.VirtualRenderAuthoritative, scene,
        ProjectedSceneImageTransformV1.Identity(scene.Request.Projection.WidthPixels, scene.Request.Projection.HeightPixels),
        Source(), "calibration-v1", scene.Request.ProjectionVersion);

    private static DeepSkyCatalog Catalog(IEnumerable<DeepSkyObject> objects, IEnumerable<DeepSkyOutline>? outlines = null)
    {
        var items = objects.ToArray();
        return new DeepSkyCatalog(Semantics, items,
            items.Select(static item => new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation)), [],
            outlines ?? []);
    }

    private static DeepSkyObject Galaxy(
        string id,
        (double Ra, double Dec) at,
        double? major,
        double? minor,
        double? positionAngle,
        int? messier = null,
        string? common = null,
        string type = "G")
    {
        var designation = "NGC " + int.Parse(id[3..], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        var displayName = messier is { } number ? "M" + number.ToString(CultureInfo.InvariantCulture) : designation;
        return new DeepSkyObject(id, designation, displayName, type, at.Ra, at.Dec, "UMa", major, minor, positionAngle,
            null, 10, null, null, messier, null, null, common);
    }

    /// <summary>A closed square ring of the given half-size in degrees of declination about a J2000 centre.</summary>
    private static DeepSkyOutline Square(string id, int level, (double Ra, double Dec) center, double half)
    {
        var ra = center.Ra * 15;
        var raHalf = half / Math.Cos(center.Dec * Math.PI / 180);
        static DeepSkyOutlinePoint Point(double raDegrees, double dec) => new((raDegrees % 360 + 360) % 360, dec);
        DeepSkyOutlinePoint[] corners =
        [
            Point(ra - raHalf, center.Dec - half), Point(ra + raHalf, center.Dec - half),
            Point(ra + raHalf, center.Dec + half), Point(ra - raHalf, center.Dec + half)
        ];
        return new DeepSkyOutline(id, level, [new DeepSkyOutlineRing([.. corners, corners[0]])]);
    }

    /// <summary>Returns the point the given number of degrees from a start along a position angle.</summary>
    private static EquatorialPoint Destination(EquatorialPoint start, double positionAngleDegrees, double distanceDegrees)
    {
        var declination = start.DeclinationDegrees * Math.PI / 180;
        var angle = positionAngleDegrees * Math.PI / 180;
        var distance = distanceDegrees * Math.PI / 180;
        var end = Math.Asin(Math.Sin(declination) * Math.Cos(distance) +
            Math.Cos(declination) * Math.Sin(distance) * Math.Cos(angle));
        var hours = start.RightAscensionHours + Math.Atan2(Math.Sin(angle) * Math.Sin(distance) * Math.Cos(declination),
            Math.Cos(distance) - Math.Sin(declination) * Math.Sin(end)) * 12 / Math.PI;
        return new EquatorialPoint((hours % 24 + 24) % 24, end * 180 / Math.PI);
    }

    /// <summary>Returns the J2000 position whose geometric direction at the given instant is the given one.</summary>
    private static (double Ra, double Dec) At(double altitude, double azimuth, DateTimeOffset utc)
    {
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(
            new AltAzPoint(altitude, azimuth), utc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, utc);
        return (j2000.RightAscensionHours, j2000.DeclinationDegrees);
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

    private static ProjectionContext Fisheye() =>
        new(ProjectionModel.EquidistantFisheye, 3500, 3500, Focal, Focal, 7000, 7000, ProjectionAperture.Circular, 3400,
            BoresightAltitudeDegrees: 90);

    private static ProjectionContext Perspective() =>
        new(ProjectionModel.Perspective, 500, 500, Focal, Focal, 1000, 1000, ProjectionAperture.Rectangular,
            BoresightAltitudeDegrees: 45, BoresightAzimuthDegrees: 180);

    private static async Task<VisibleScene> BuildAsync(
        DateTimeOffset utc,
        ProjectionContext projection,
        IReadOnlyList<SolarSystemBody>? bodies = null,
        RefractionOptions refraction = default,
        HorizonPolicy horizon = HorizonPolicy.GeometricHorizon) =>
        await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, new AstronomyEnginePlanetEphemeris())
            .BuildAsync(new VisibleSceneRequest(
                utc, Site, projection, new CatalogQuery(6, 10), Metadata, refraction, horizon,
                projectionVersion: "fisheye-v1", solarSystemBodies: bodies ?? [])).ConfigureAwait(false);

    private static ProjectedSceneSource Source() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        new string('A', 64));
}
