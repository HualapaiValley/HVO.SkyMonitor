using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

public sealed partial class CuratedDeepSkySceneTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CanonicalSceneMatchesOriginalDomPipelineWith500Stars(int schema)
    {
        var scene = await CanonicalFixtureAsync(schema).ConfigureAwait(false);

        Assert.HasCount(500, scene.Objects);
        if (schema >= 2)
        {
            Assert.IsNotNull(scene.ResolvedFootprints);
            Assert.HasCount(2, scene.ResolvedFootprints);
            CollectionAssert.AreEquivalent(new[] { ResolvedFootprintShape.Circle, ResolvedFootprintShape.Ellipse },
                scene.ResolvedFootprints!.Select(static item => item.Extent.Shape).ToArray());
        }
        if (schema == 3)
        {
            Assert.HasCount(3, scene.DeepSky!.Objects);
            Assert.IsTrue(scene.DeepSky.Objects.Any(static item => item.Representation == DeepSkyRepresentation.UnknownExtentGlyph));
        }
        AssertOriginalCanonicalParity(scene);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CanonicalScenePreservesEscapingNullsNumbersAndOmittedDefaults(int schema)
    {
        var scene = await CanonicalFixtureAsync(schema).ConfigureAwait(false);
        const string escaped = "Andromeda \"<é>\\星😀";
        scene = scene with
        {
            Catalog = scene.Catalog with { Name = escaped, SourceUrl = new Uri("https://example.test/catalog?q=%E6%98%9F&x=1") },
            Objects = scene.Objects.Select((item, index) => item with
            {
                DisplayName = escaped + index.ToString(CultureInfo.InvariantCulture),
                Magnitude = index == 0 ? -0d : index / 1_000d,
                ColorIndex = index % 2 == 0 ? null : 1e-10,
                HipparcosId = index % 2 == 0 ? null : item.HipparcosId
            }).ToArray(),
            Observer = scene.Observer with { ElevationMeters = -0d },
            Refraction = scene.Refraction with { MinimumAltitudeDegrees = -0d }
        };
        scene = scene with { SceneIdentitySha256 = OriginalSceneIdentity(scene) };

        AssertOriginalCanonicalParity(scene);
        var json = Encoding.UTF8.GetString(ProjectedSceneJson.Serialize(scene));
        StringAssert.Contains(json, "\\u", StringComparison.Ordinal);
        StringAssert.Contains(json, "\"colorIndex\":null", StringComparison.Ordinal);
        Assert.IsFalse(json.Contains("\"radialDistortionK1\"", StringComparison.Ordinal));
        Assert.AreEqual(schema == 3, json.Contains("\"deepSky\"", StringComparison.Ordinal));
        Assert.AreEqual(schema >= 2, json.Contains("\"resolvedFootprints\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CanonicalIdentityPreservesArrayOrderAndRejectsCallerMutation()
    {
        var scene = await CanonicalFixtureAsync(3).ConfigureAwait(false);
        var reversed = scene with { Objects = scene.Objects.Reverse().ToArray() };
        var reversedIdentity = OriginalSceneIdentity(reversed);
        Assert.AreEqual(reversedIdentity, ProjectedSceneJson.ComputeIdentity(reversed));
        Assert.AreNotEqual(scene.SceneIdentitySha256, reversedIdentity);
        Assert.Throws<ArgumentException>(() => ProjectedSceneJson.Serialize(reversed with { SceneIdentitySha256 = reversedIdentity }));

        var borrowed = scene.Objects.ToArray();
        var mutable = scene with { Objects = borrowed };
        CollectionAssert.AreEqual(OriginalSceneBytes(mutable), ProjectedSceneJson.Serialize(mutable));
        borrowed[0] = borrowed[0] with { DisplayName = "caller changed the star" };
        Assert.AreEqual(OriginalSceneIdentity(mutable), ProjectedSceneJson.ComputeIdentity(mutable));
        Assert.AreNotEqual(scene.SceneIdentitySha256, ProjectedSceneJson.ComputeIdentity(mutable));
        Assert.Throws<ArgumentException>(() => ProjectedSceneJson.Validate(mutable));
        Assert.Throws<ArgumentException>(() => ProjectedSceneJson.Serialize(mutable));
    }

    [TestMethod]
    public async Task IdentityPreservesOptionalTypedShapesBeforeSemanticValidation()
    {
        var scene = await CanonicalFixtureAsync(3).ConfigureAwait(false);
        var footprint = scene.ResolvedFootprints![0];
        // ComputeIdentity has always hashed typed content before validation, including rejected wire shapes.
        // Exercise the remaining reachable records; populated contours still fail semantic validation.
        var changed = scene with
        {
            ConstellationTopology = new("topology", "v1", new Uri("https://example.test/topology"),
                new string('D', 64), null, "license", "p1"),
            ResolvedFootprints = [footprint with
            {
                Appearance = new("ephemeris-v1", 1.23456789e8, 0.5, -0d, -1.25),
                Extent = footprint.Extent with { SemiMajorAxisUncertaintyDegrees = 1e-12 }
            }, .. scene.ResolvedFootprints.Skip(1)],
            DeepSky = scene.DeepSky! with
            {
                Outlines = [new(footprint.Id, ProjectedDeepSkyOutline.CurrentContractVersion,
                    ProjectedDeepSkyOutline.SamplingAlgorithm, 1, 1, null, false, footprint.Bounds, footprint.Parts)]
            },
            Segments = [new("ORI", "a", "b", new PixelPoint(0, 1), new PixelPoint(2, 3))]
        };
        Assert.AreEqual(OriginalSceneIdentity(changed), ProjectedSceneJson.ComputeIdentity(changed));
        Assert.Throws<ArgumentException>(() => ProjectedSceneJson.Serialize(changed));
    }

    [TestMethod]
    public void CanonicalSceneGraphHasOnlyTypedObjectsOrderedListsAndScalarConverters()
    {
        var options = OriginalSceneOptions();
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();
        var visited = new HashSet<Type>();
        Visit(typeof(ProjectedSceneV1));
        CollectionAssert.IsSubsetOf(new[] { typeof(ProjectedDeepSkyOutline), typeof(ResolvedBodyAppearance),
            typeof(ProjectedConstellationSegment), typeof(EnuVector) }, visited.ToArray());

        void Visit(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!visited.Add(type)) return;
            var info = options.GetTypeInfo(type);
            if (info.Kind == JsonTypeInfoKind.None)
            {
                Assert.IsTrue(type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(int) ||
                    type == typeof(double) || type == typeof(Guid) || type == typeof(DateTimeOffset) || type == typeof(Uri), type.FullName);
                return;
            }
            if (info.Kind == JsonTypeInfoKind.Enumerable)
            {
                Assert.IsTrue(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>), type.FullName);
                Visit(type.GetGenericArguments()[0]);
                return;
            }
            Assert.AreEqual(JsonTypeInfoKind.Object, info.Kind, type.FullName);
            Assert.AreEqual(typeof(ProjectedSceneV1).Assembly, type.Assembly, type.FullName);
            Assert.IsTrue(type.IsSealed || type.IsValueType, type.FullName);
            foreach (var property in info.Properties.Where(static property => property.Get is not null)) Visit(property.PropertyType);
        }
    }

    private static async Task<ProjectedSceneV1> CanonicalFixtureAsync(int schema)
    {
        var request = new VisibleSceneRequest(Utc, new ObserverLocation(30, 0, 0),
            Scene().Request.Projection, new CatalogQuery(6, 500), Scene().Request.CatalogMetadata,
            projectionVersion: "cal-v1");
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(Utc, 0) / 15;
        var stars = new InMemoryCelestialCatalog(Enumerable.Range(0, 500).Select(index =>
            new CelestialCatalogObject($"star-{index:D4}", $"Star {index:D4}", rightAscension, 30,
                index / 1_000d, ColorIndex: index % 2 == 0 ? null : 0.25,
                HipparcosId: (index + 1).ToString(CultureInfo.InvariantCulture))));
        var visible = await new VisibleSceneBuilder(stars).BuildAsync(request).ConfigureAwait(false);
        if (schema >= 2)
        {
            var circle = Object("M045", 45, 150, 150, null);
            var ellipse = Object("NGC0224", 31, 120, 60, 90);
            var unknown = Object("NGC1976", 42, null, null, null);
            var catalog = Catalog([circle, ellipse, unknown], [new("M45", circle.Id, DeepSkyAliasKinds.Messier),
                new("M31", ellipse.Id, DeepSkyAliasKinds.Messier), new("M42", unknown.Id, DeepSkyAliasKinds.Messier)]);
            visible = visible.WithCuratedDeepSky(catalog, Selection());
            if (schema == 2)
                visible = new VisibleScene(visible.Request, visible.Objects, visible.Segments,
                    visible.ComputationProvenance, visible.ResolvedFootprints);
        }
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(484, 304),
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('B', 64)),
            "calibration-v1", request.ProjectionVersion);
    }

    private static void AssertOriginalCanonicalParity(ProjectedSceneV1 scene)
    {
        Assert.AreEqual(OriginalSceneIdentity(scene), scene.SceneIdentitySha256);
        Assert.AreEqual(OriginalSceneIdentity(scene), ProjectedSceneJson.ComputeIdentity(scene));
        var expected = OriginalSceneBytes(scene);
        CollectionAssert.AreEqual(expected, ProjectedSceneJson.Serialize(scene));
        var parsed = ProjectedSceneJson.Parse(expected);
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        CollectionAssert.AreEqual(expected, ProjectedSceneJson.Serialize(parsed.Scene!));
    }

    // Independent original DOM pipeline and original option values, rather than the optimized scene path/hash API.
    private static byte[] OriginalSceneBytes(ProjectedSceneV1 scene) => JsonSerializer.SerializeToUtf8Bytes(
        CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(scene, OriginalSceneOptions())));

    private static string OriginalSceneIdentity(ProjectedSceneV1 scene) => Convert.ToHexString(
        SHA256.HashData(OriginalSceneBytes(scene with { SceneIdentitySha256 = string.Empty })));

    private static JsonSerializerOptions OriginalSceneOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
