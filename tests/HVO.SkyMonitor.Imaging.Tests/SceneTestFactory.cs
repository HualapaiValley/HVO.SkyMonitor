using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

internal static class SceneTestFactory
{
    private static readonly DateTimeOffset Utc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly CatalogMetadata Metadata = new(
        "test", "1", new Uri("https://example.test/catalog"), "test", "test", "test-v1");

    public static async Task<VisibleScene> CreateCenteredAsync(int width, int height, double magnitude = 0, double? colorIndex = 0.65,
        double? principalX = null, double? principalY = null, double? radius = null)
    {
        var catalog = new InMemoryCelestialCatalog([new CelestialCatalogObject("star", "Star", 2.530301, 89.264109, magnitude, colorIndex)]);
        var preliminary = await new VisibleSceneBuilder(catalog).BuildAsync(Request(width, height,
            new EquidistantProjectionContext(width / 2d, height / 2d, 1, Math.PI),
            HorizonPolicy.ProjectionOnly)).ConfigureAwait(false);
        var direction = preliminary.Objects[0].ApparentHorizontal;
        return await new VisibleSceneBuilder(catalog).BuildAsync(Request(width, height,
            new EquidistantProjectionContext(principalX ?? width / 2d, principalY ?? height / 2d,
                (radius ?? Math.Min(width, height) / 2d) / Math.PI, radius ?? Math.Min(width, height) / 2d,
                direction.AltitudeDegrees, direction.AzimuthDegrees, WidthPixels: width, HeightPixels: height),
            HorizonPolicy.ProjectionOnly)).ConfigureAwait(false);
    }

    public static async Task<VisibleScene> CreateEmptyAsync(int width, int height, double radius)
        => await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(Request(width, height,
            new EquidistantProjectionContext(width / 2d, height / 2d, radius / Math.PI, radius,
                WidthPixels: width, HeightPixels: height),
            HorizonPolicy.ProjectionOnly)).ConfigureAwait(false);

    private static VisibleSceneRequest Request(int width, int height, EquidistantProjectionContext projection, HorizonPolicy policy)
        => new(Utc, new ObserverLocation(0, 0, 0), projection, new CatalogQuery(20, 10), Metadata,
            new RefractionOptions(false), policy);
}
