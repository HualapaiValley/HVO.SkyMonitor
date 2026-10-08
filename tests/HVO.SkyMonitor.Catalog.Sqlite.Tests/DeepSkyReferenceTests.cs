using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

/// <summary>
/// Compares deep-sky placement over the composed fixture with the independent numerical reference in
/// <c>deep-sky-reference-v1.json</c>, which <c>docs/validation/issue-525-deep-sky-reference.py</c> generated from ERFA
/// and the published footprint, outline and camera definitions (<c>SOURCE-deep-sky-reference-v1.md</c>).
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "MSTest constructs this class through the assembly's DiscoverInternals contract.")]
internal sealed class DeepSkyReferenceTests
{
    private const string ReferenceSha256 = "2ce52b9eaf706c9b8f6fa5d7cd6e25e53261df8ea15b151cd3e925f52efba035";
    private const string FixtureChecksum = "42590e36f804b30e444d917ca188f35287684584d1b741838ed297446bd669c8";
    private const string Schema = "hvo-525-deep-sky-reference-v1";

    // The reference evaluates Greenwich mean sidereal time with the IAU 1982 quadratic term, which AstronomyTime omits:
    // about 0.09 arcseconds at this instant, at most 0.0034 px at 8000 px per radian. It also precesses at TT where
    // production uses UTC, which moves a direction by about 0.0001 arcseconds. 0.01 px bounds both with margin.
    private const double ModelPixels = .01;

    // The reference rounds coordinates to six decimals.
    private const double RoundingPixels = 1e-6;

    // Both sides measure plate scale by finite differences: the reference over 360 directions at a 1e-6 rad step,
    // production as the mean of a north and an east step of 1e-5 rad.
    private const double GlyphPixels = 1e-3;

    private static readonly string FixturesPath = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly CatalogMetadata Metadata =
        new("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1");
    private static readonly Lazy<JsonDocument> Reference = new(Load);

    [TestMethod]
    public void TheReferenceIsTheRecordedFileGeneratedFromTheFixtureInputs()
    {
        var root = Reference.Value.RootElement;
        Assert.AreEqual(Schema, root.GetProperty("schema").GetString());
        var provenance = root.GetProperty("provenance");
        var sums = File.ReadAllLines(Path.Combine(FixturesPath, "SHA256SUMS-v44-openngc"))
            .Select(static line => line.Split("  "))
            .ToDictionary(static parts => parts[1], static parts => parts[0], StringComparer.Ordinal);

        // The inputs are the CSV files the composed fixture was built from, so reference and catalogue share a source.
        var inputs = provenance.GetProperty("inputs").EnumerateArray().ToArray();
        Assert.HasCount(2, inputs);
        foreach (var input in inputs)
        {
            var file = input.GetProperty("file").GetString()!;
            var sha256 = input.GetProperty("sha256").GetString()!;
            Assert.AreEqual(sha256, Checksum(Path.Combine(FixturesPath, file)), file);
            Assert.AreEqual(sha256, sums[file], file);
        }
        Assert.AreEqual(FixtureChecksum, sums["hyg-v44-openngc-subset.sqlite"]);
        Assert.AreEqual(FixtureChecksum, Checksum(Path.Combine(FixturesPath, "hyg-v44-openngc-subset.sqlite")));

        var pinned = typeof(DeepSkyReferenceTests)
            .GetMethod(nameof(PlacementAgreesWithTheReference))!
            .GetCustomAttributes<DataRowAttribute>()
            .Select(static row => (string)row.Data[0]!)
            .ToArray();
        var cases = root.GetProperty("cases").EnumerateArray().Select(static item => item.GetProperty("name").GetString()!)
            .ToArray();
        // Reflection does not promise declaration order, so compare as multisets: one row per case, no strays.
        CollectionAssert.AreEquivalent(cases, pinned);
    }

    [TestMethod]
    [DataRow("ngc0224-ellipse-perspective-centred")]
    [DataRow("ngc0224-ellipse-fisheye-off-axis")]
    [DataRow("mel022-outline-level1-perspective-centred")]
    [DataRow("mel022-outline-level1-fisheye-off-axis")]
    [DataRow("mel022-circle-outline-limit-perspective-centred")]
    [DataRow("mel022-circle-outline-limit-fisheye-off-axis")]
    [DataRow("ngc1976-outline-level3-perspective-centred")]
    [DataRow("ngc1976-outline-level3-fisheye-off-axis")]
    [DataRow("ngc1976-glyph-outline-limit-perspective-centred")]
    [DataRow("ngc1976-glyph-outline-limit-fisheye-off-axis")]
    public async Task PlacementAgreesWithTheReference(string name)
    {
        var root = Reference.Value.RootElement;
        var item = root.GetProperty("cases").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        var maximumSag = root.GetProperty("provenance").GetProperty("sampling").GetProperty("maximumSagPixels").GetDouble();
        var maximumOutlines = item.GetProperty("maximumOutlines").GetInt32();
        var selection = ProjectedSceneDeepSkySelection.Default with
        {
            MaximumOutlines = maximumOutlines,
            PreferredOutlineLevel = item.GetProperty("preferredOutlineLevel").GetInt32()
        };
        var catalog = new SqliteCelestialCatalog(new SqliteCelestialCatalogOptions(
            Path.Combine(FixturesPath, "hyg-v44-openngc-subset.sqlite"), FixtureChecksum, "4", "5"));

        var scene = (await BuildAsync(root, item.GetProperty("camera")).ConfigureAwait(false))
            .WithDeepSky(catalog.DeepSky!, selection);

        var objectId = item.GetProperty("objectId").GetString()!;
        var placed = scene.DeepSky!.Objects.Single(candidate => candidate.CatalogObjectId == objectId);
        var center = Point(item.GetProperty("centerPixel"));
        Assert.IsNotNull(placed.Pixel, name);
        var pixel = placed.Pixel!.Value;
        Assert.IsLessThanOrEqualTo(ModelPixels, Distance(pixel, center), $"{name}: the centre {pixel} is not the reference {center}.");
        Assert.AreEqual(
            maximumOutlines == 0 ? DeepSkyDegradation.OutlineLimit : (DeepSkyDegradation?)null, placed.Degradation, name);
        var kind = item.GetProperty("kind").GetString();
        switch (kind)
        {
            case "footprint":
                {
                    Assert.AreEqual(DeepSkyRepresentation.Footprint, placed.Representation, name);
                    var footprint = scene.ResolvedFootprints.Single(candidate => candidate.Id == placed.Id);
                    Assert.AreEqual(ResolvedFootprintSourceKind.DeepSkyObject, footprint.SourceKind);
                    var circle = item.GetProperty("shape").GetString() == "circle";
                    Assert.AreEqual(circle ? ResolvedFootprintShape.Circle : ResolvedFootprintShape.Ellipse,
                        footprint.Extent.Shape, name);
                    Assert.AreEqual(item.GetProperty("semiMajorAxisDegrees").GetDouble(), footprint.Extent.SemiMajorAxisDegrees,
                        1e-12, name);
                    Assert.AreEqual(item.GetProperty("semiMinorAxisDegrees").GetDouble(), footprint.Extent.SemiMinorAxisDegrees,
                        1e-12, name);
                    AssertCurvesAgree(name, footprint.Clipped, footprint.Parts, Rings(item), maximumSag);
                    break;
                }
            case "outline":
                {
                    var level = item.GetProperty("level").GetInt32();
                    Assert.AreEqual(DeepSkyRepresentation.Outline, placed.Representation, name);
                    Assert.AreEqual(level, placed.OutlineLevel, name);
                    var outline = scene.DeepSky.Outlines.Single(candidate => candidate.Id == placed.Id);
                    Assert.AreEqual(level, outline.Level, name);
                    var rings = Rings(item);
                    Assert.AreEqual(item.GetProperty("ringVertexCounts").GetArrayLength(), rings.Length, name);
                    Assert.AreEqual(rings.Length, outline.RingCount, name);
                    AssertCurvesAgree(name, outline.Clipped, outline.Parts, rings, maximumSag);
                    break;
                }
            case "glyph":
                {
                    Assert.AreEqual(DeepSkyRepresentation.SizedGlyph, placed.Representation, name);
                    Assert.IsEmpty(Rings(item));
                    var minimum = item.GetProperty("majorAxisPixelsMinimum").GetDouble();
                    var maximum = item.GetProperty("majorAxisPixelsMaximum").GetDouble();
                    Assert.IsNotNull(placed.MajorAxisPixels, name);
                    var majorAxis = placed.MajorAxisPixels!.Value;
                    Assert.IsTrue(
                        majorAxis >= minimum - GlyphPixels && majorAxis <= maximum + GlyphPixels,
                        $"{name}: the glyph's major axis {majorAxis} px is outside the reference [{minimum}, {maximum}].");
                    break;
                }
            default:
                Assert.Fail($"{name}: unknown kind {kind}.");
                break;
        }
    }

    /// <summary>
    /// Production's vertices lie on the true curve, so each lies within the reference's sampling bound of the reference
    /// ring. The reference's samples are dense, so each lies within production's chord tolerance of production's ring.
    /// </summary>
    private static void AssertCurvesAgree(
        string name,
        bool clipped,
        IReadOnlyList<ResolvedFootprintPart> parts,
        PixelPoint[][] rings,
        double maximumSag)
    {
        Assert.IsFalse(clipped, name);
        Assert.HasCount(rings.Length, parts);
        Assert.IsTrue(parts.All(static part => part.Closed), name);
        Assert.IsTrue(rings.All(static ring => ring.Length > 100), $"{name}: the reference is too sparse.");

        // The reference checks the quarter points of every chord against twice its midpoint bound.
        var toReference = ModelPixels + 2 * maximumSag + RoundingPixels;
        foreach (var point in parts.SelectMany(static part => part.Points))
        {
            var distance = rings.Min(ring => DistanceToClosedPolyline(point, ring));
            Assert.IsLessThanOrEqualTo(toReference, distance, $"{name}: {point} lies {distance} px from the reference.");
        }

        var toProduction = ResolvedFootprintSampler.MaximumChordErrorPixels + ModelPixels + RoundingPixels;
        foreach (var expected in rings.SelectMany(static ring => ring))
        {
            var distance = parts.Min(part => DistanceToClosedPolyline(expected, part.Points));
            Assert.IsLessThanOrEqualTo(toProduction, distance,
                $"{name}: the reference point {expected} lies {distance} px from production's ring.");
        }
    }

    private static async Task<VisibleScene> BuildAsync(JsonElement root, JsonElement camera)
    {
        // The reference models only an unrolled, unflipped and undistorted camera.
        Assert.AreEqual(0d, camera.GetProperty("rollDegrees").GetDouble());
        Assert.IsFalse(camera.GetProperty("horizontalFlip").GetBoolean());
        Assert.AreEqual(0d, camera.GetProperty("radialDistortion").GetDouble());
        var fisheye = camera.GetProperty("model").GetString() switch
        {
            "perspective" => false,
            "equidistant-fisheye" => true,
            var model => throw new InvalidDataException($"Unknown camera model {model}.")
        };
        var focal = camera.GetProperty("focalPixels").GetDouble();
        var projection = new ProjectionContext(
            fisheye ? ProjectionModel.EquidistantFisheye : ProjectionModel.Perspective,
            camera.GetProperty("principalPointX").GetDouble(), camera.GetProperty("principalPointY").GetDouble(),
            focal, focal, camera.GetProperty("widthPixels").GetInt32(), camera.GetProperty("heightPixels").GetInt32(),
            fisheye ? ProjectionAperture.Circular : ProjectionAperture.Rectangular,
            fisheye ? camera.GetProperty("apertureRadiusPixels").GetDouble() : null,
            BoresightAltitudeDegrees: camera.GetProperty("boresightAltitudeDegrees").GetDouble(),
            BoresightAzimuthDegrees: camera.GetProperty("boresightAzimuthDegrees").GetDouble());
        var site = root.GetProperty("site");
        var utc = DateTimeOffset.Parse(root.GetProperty("utc").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, new AstronomyEnginePlanetEphemeris())
            .BuildAsync(new VisibleSceneRequest(
                utc,
                new ObserverLocation(
                    site.GetProperty("latitudeDegrees").GetDouble(), site.GetProperty("longitudeDegrees").GetDouble(),
                    site.GetProperty("elevationMeters").GetDouble()),
                projection, new CatalogQuery(6, 10), Metadata, projectionVersion: "deep-sky-reference-v1"))
            .ConfigureAwait(false);
    }

    private static JsonDocument Load()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesPath, "deep-sky-reference-v1.json"));
        Assert.AreEqual(ReferenceSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonDocument.Parse(bytes);
    }

    private static PixelPoint[][] Rings(JsonElement item) =>
        [.. item.GetProperty("rings").EnumerateArray().Select(static ring => ring.EnumerateArray().Select(Point).ToArray())];

    private static PixelPoint Point(JsonElement pair) => new(pair[0].GetDouble(), pair[1].GetDouble());

    private static double DistanceToClosedPolyline(PixelPoint point, IReadOnlyList<PixelPoint> ring)
    {
        var best = double.PositiveInfinity;
        for (var index = 0; index < ring.Count; index++)
        {
            best = Math.Min(best, DistanceToSegment(point, ring[index], ring[(index + 1) % ring.Count]));
        }
        return best;
    }

    private static double DistanceToSegment(PixelPoint point, PixelPoint from, PixelPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared, 0, 1);
        return Distance(point, new PixelPoint(from.X + t * dx, from.Y + t * dy));
    }

    private static double Distance(PixelPoint left, PixelPoint right) =>
        Math.Sqrt((left.X - right.X) * (left.X - right.X) + (left.Y - right.Y) * (left.Y - right.Y));

    private static string Checksum(string path)
    {
        using var source = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(source));
    }
}
