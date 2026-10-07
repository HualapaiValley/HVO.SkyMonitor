namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>
/// Builds representative projected-scene documents at one fixed instant so their canonical bytes can be pinned. The
/// pinned values were produced by this file compiled against the revision before deep-sky placement existed. Keep it
/// unchanged, so a pinned value keeps comparing the same construction across both revisions.
/// </summary>
internal static class RetainedSceneFixtures
{
    private const int Width = 12_000;
    private const double Focal = 4_000;
    private static readonly DateTimeOffset EffectiveUtc = new(2025, 1, 15, 12, 30, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Site = new(35.347, -113.878, 0);
    private static readonly CatalogMetadata Metadata =
        new("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1");

    private static readonly CelestialCatalogObject[] Stars =
    [
        new("HIP11767", "Polaris", 2.530301, 89.264109, 1.98, 0.60, "11767"),
        new("HIP24608", "Capella", 5.278155, 45.997991, 0.08, 0.80, "24608"),
        new("HIP27989", "Betelgeuse", 5.919529, 7.407064, 0.42, 1.85, "27989"),
        new("HIP32349", "Sirius", 6.752481, -16.716116, -1.46, 0.00, "32349"),
        new("HIP49669", "Regulus", 10.139532, 11.967207, 1.40, -0.11, "49669"),
        new("HIP54061", "Dubhe", 11.062130, 61.751033, 1.79, 1.07, "54061"),
        new("HIP65474", "Spica", 13.419883, -11.161319, 0.97, -0.23, "65474"),
        new("HIP67301", "Alkaid", 13.792344, 49.313267, 1.86, -0.10, "67301"),
        new("HIP69673", "Arcturus", 14.261020, 19.182410, -0.05, 1.23, "69673"),
        new("HIP91262", "Vega", 18.615649, 38.783690, 0.03, 0.00, "91262"),
        new("HIP97649", "Altair", 19.846388, 8.868321, 0.76, 0.22, "97649")
    ];

    /// <summary>Every fixture name, in the order the pinned values list them.</summary>
    internal static IReadOnlyList<string> Names { get; } =
    [
        "v1-stars-identity",
        "v1-stars-transformed",
        "v1-moon-cropped-away",
        "v2-moon-identity",
        "v2-moon-transformed",
        "v2-moon-crop-clipped",
        "v2-moon-binned-readout",
        "v2-moon-circle-clipped",
        "v2-moon-circle-and-crop-clipped"
    ];

    /// <summary>Builds one fixture's canonical bytes, and fails if the fixture no longer has the shape its name states.</summary>
    internal static async Task<byte[]> BuildAsync(string name)
    {
        var wide = Fisheye(Width, 5990);
        switch (name)
        {
            case "v1-stars-identity":
                return Serialize(await SceneAsync(wide, []).ConfigureAwait(false),
                    ProjectedSceneImageTransformV1.Identity(Width, Width), ProjectedSceneV1.CurrentSchemaVersion, null);
            case "v1-stars-transformed":
                return Serialize(await SceneAsync(wide, []).ConfigureAwait(false),
                    new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion, Width, Width,
                        2000, 1000, 8000, 10_000, 2, 2, false, true, ProjectedSceneQuarterRotation.Degrees270, 5000, 4000),
                    ProjectedSceneV1.CurrentSchemaVersion, null);
        }

        var moon = Moon();
        var scene = await MoonSceneAsync(wide, moon).ConfigureAwait(false);
        var center = CenterOf(scene);
        var whole = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion, Width, Width,
            (int)center.X - 400, (int)center.Y - 300, 800, 600, 2, 2, true, false,
            ProjectedSceneQuarterRotation.Degrees90, 300, 400);
        switch (name)
        {
            case "v1-moon-cropped-away":
                return Serialize(scene, whole with { CropX = 0, CropY = 0 }, ProjectedSceneV1.CurrentSchemaVersion, null);
            case "v2-moon-identity":
                return Serialize(scene, ProjectedSceneImageTransformV1.Identity(Width, Width),
                    ProjectedSceneV1.ResolvedFootprintSchemaVersion, false);
            case "v2-moon-transformed":
                return Serialize(scene, whole, ProjectedSceneV1.ResolvedFootprintSchemaVersion, false);
            case "v2-moon-crop-clipped":
                return Serialize(scene,
                    whole with { CropX = (int)center.X, CropWidth = 400, OutputWidthPixels = 300, OutputHeightPixels = 200 },
                    ProjectedSceneV1.ResolvedFootprintSchemaVersion, true);
            case "v2-moon-binned-readout":
                return Serialize(VisibleSceneReadoutTransform.ToOutput(scene, Fisheye(Width / 2, 2995, Focal / 2), 2, 2),
                    ProjectedSceneImageTransformV1.Identity(Width / 2, Width / 2),
                    ProjectedSceneV1.ResolvedFootprintSchemaVersion, false);
        }

        // An image circle half a lunar radius beyond the Moon's center keeps the center in view and cuts its limb.
        var circle = Focal * (90 - moon.Direction.AltitudeDegrees + moon.AngularRadiusDegrees / 2) * Math.PI / 180;
        var circled = await MoonSceneAsync(Fisheye(Width, circle), moon).ConfigureAwait(false);
        var circledCenter = CenterOf(circled);
        switch (name)
        {
            case "v2-moon-circle-clipped":
                return Serialize(circled, ProjectedSceneImageTransformV1.Identity(Width, Width),
                    ProjectedSceneV1.ResolvedFootprintSchemaVersion, true);
            case "v2-moon-circle-and-crop-clipped":
                return Serialize(circled,
                    new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion, Width, Width,
                        (int)circledCenter.X - 100, (int)circledCenter.Y, 200, 100, 1, 1, false, false,
                        ProjectedSceneQuarterRotation.Degrees0, 200, 100),
                    ProjectedSceneV1.ResolvedFootprintSchemaVersion, true);
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown retained-scene fixture.");
        }
    }

    private static byte[] Serialize(
        VisibleScene scene,
        ProjectedSceneImageTransformV1 transform,
        string schemaVersion,
        bool? footprintClipped)
    {
        var projected = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, scene, transform,
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", scene.Request.ProjectionVersion);
        if (!string.Equals(projected.SchemaVersion, schemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"Fixture produced {projected.SchemaVersion}, not {schemaVersion}.");
        var footprints = projected.ResolvedFootprints ?? [];
        if (footprintClipped is { } clipped && (footprints.Count != 1 || footprints[0].Clipped != clipped))
            throw new InvalidOperationException("Fixture no longer produces the one footprint, clipped as named, that it pins.");
        return ProjectedSceneJson.Serialize(projected);
    }

    private static SolarDiskAppearance Moon()
    {
        var moon = SolarDiskEphemeris.Get(SolarSystemBody.Moon, EffectiveUtc, Site);
        if (moon.Direction.AltitudeDegrees is < 25 or > 60)
            throw new InvalidOperationException("The fixed instant no longer places the Moon between 25 and 60 degrees.");
        return moon;
    }

    private static PixelPoint CenterOf(VisibleScene scene) =>
        scene.ResolvedFootprints.Single().CenterPixel ??
        throw new InvalidOperationException("Fixture Moon center is outside the image.");

    private static async Task<VisibleScene> MoonSceneAsync(ProjectionContext projection, SolarDiskAppearance moon) =>
        (await SceneAsync(projection, [SolarSystemBody.Moon]).ConfigureAwait(false)).WithResolvedBodies([moon]);

    private static async Task<VisibleScene> SceneAsync(ProjectionContext projection, IReadOnlyList<SolarSystemBody> bodies) =>
        await new VisibleSceneBuilder(new InMemoryCelestialCatalog(Stars), null, new AstronomyEnginePlanetEphemeris())
            .BuildAsync(new VisibleSceneRequest(EffectiveUtc, Site, projection, new CatalogQuery(6, 10), Metadata,
                projectionVersion: "fisheye-v1", solarSystemBodies: bodies))
            .ConfigureAwait(false);

    private static ProjectionContext Fisheye(int width, double imageCircle, double focal = Focal) =>
        new(ProjectionModel.EquidistantFisheye, width / 2d, width / 2d, focal, focal, width, width,
            ProjectionAperture.Circular, imageCircle, BoresightAltitudeDegrees: 90);
}
