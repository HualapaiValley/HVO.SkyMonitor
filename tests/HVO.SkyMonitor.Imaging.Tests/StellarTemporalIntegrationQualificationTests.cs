using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarTemporalIntegrationQualificationTests
{
    private static readonly DateTimeOffset Midpoint = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(35.347, -113.878, 1000);
    private static readonly CatalogMetadata Metadata = new("HYG", "fixture", new("https://astronexus.com/projects/hyg"),
        new string('A', 64), "CC BY-SA 4.0", "2");

    [TestMethod]
    [DataRow("asi676-equator", 3552, 1097.0456, 20d, 0d, false, 0d)]
    [DataRow("short-point", 3552, 1097.0456, .01d, 0d, false, 0d)]
    [DataRow("rolled", 3552, 1097.0456, 20d, 73d, false, 0d)]
    [DataRow("mirrored", 3552, 1097.0456, 20d, 19d, true, 0d)]
    [DataRow("long", 3552, 1097.0456, 60d, 0d, false, 0d)]
    [DataRow("pole", 3552, 1097.0456, 20d, 0d, false, 90d)]
    public async Task MotionFluxAndCentroidAgreeWithIndependent4096SampleQuadrature(
        string label, int size, double focal, double exposure, double roll, bool flip, double declination)
    {
        var boresight = CoordinateTransforms.EquatorialToHorizontal(
            new(AstronomyTime.LocalMeanSiderealDegrees(Midpoint, Observer.LongitudeDegrees) / 15, declination),
            Midpoint, Observer.LatitudeDegrees, Observer.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.EquidistantFisheye, size / 2d, size / 2d,
            focal, focal, size, size, ProjectionAperture.Circular, focal * 85 * Math.PI / 180,
            BoresightAltitudeDegrees: boresight.AltitudeDegrees, BoresightAzimuthDegrees: boresight.AzimuthDegrees,
            RollDegrees: roll, HorizontalFlip: flip);
        var source = SourceAt(projection, new(size / 2d + .3, size / 2d - .2));
        var request = Request(projection);
        var duration = TimeSpan.FromSeconds(exposure);
        var start = Midpoint - TimeSpan.FromTicks(duration.Ticks / 2);
        var geometry = await new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]))
            .BuildAsync(request, start, duration).ConfigureAwait(false);
        Assert.HasCount(1, geometry.Sources);
        var samples = geometry.Sources[0].Samples;
        var raster = TemporalPointSpreadRaster.Rasterize(samples.Select(sample =>
            new WeightedPointSpreadSample(sample.Pixel, sample.ExposureFraction)).ToArray(), projection, 1, 4);
        var actual = Moments(raster.Pixels.ToDictionary(static pixel => pixel.Index, static pixel => pixel.AfterTransmission), projection);
        var expected = IndependentQuadrature(source, request, start, duration);
        Assert.AreEqual(expected.Flux, actual.Flux, expected.Flux * .005, label + " flux");
        Assert.AreEqual(expected.X, actual.X, .02, label + " centroid X native pixels");
        Assert.AreEqual(expected.Y, actual.Y, .02, label + " centroid Y native pixels");
        if (declination == 0 && exposure == 20)
        {
            var first = Project(source, request, start)!.Value;
            var last = Project(source, request, start + duration)!.Value;
            var distance = Math.Sqrt(Math.Pow(last.X - first.X, 2) + Math.Pow(last.Y - first.Y, 2));
            Assert.AreEqual(focal * 7.2921158553e-5 * exposure, distance, .01, "equatorial endpoint trail");
        }
        if (label == "pole")
        {
            var first = Project(source, request, start)!.Value; var last = Project(source, request, start + duration)!.Value;
            Assert.IsLessThan(.01, Math.Sqrt(Math.Pow(last.X - first.X, 2) + Math.Pow(last.Y - first.Y, 2)));
        }
    }

    [TestMethod]
    [DataRow(-1.5)]
    [DataRow(127.8)]
    public async Task ClippedSweptPsfPreservesLostEnergyAndCentroidAgainstIndependentQuadrature(double sourceX)
    {
        var projection = new ProjectionContext(ProjectionModel.Perspective, 64, 64, 100, 100, 128, 128,
            ProjectionAperture.Rectangular, EnforceSensorBounds: false);
        var source = SourceAt(projection, new(sourceX, 64.3));
        var request = Request(projection);
        var duration = TimeSpan.FromSeconds(20); var start = Midpoint.AddSeconds(-10);
        var geometry = await new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]))
            .BuildAsync(request, start, duration).ConfigureAwait(false);
        Assert.HasCount(1, geometry.Sources);
        var raster = TemporalPointSpreadRaster.Rasterize(geometry.Sources[0].Samples.Select(sample =>
            new WeightedPointSpreadSample(sample.Pixel, sample.ExposureFraction)).ToArray(), projection, 1, 4);
        var actual = Moments(raster.Pixels.ToDictionary(static pixel => pixel.Index, static pixel => pixel.AfterTransmission), projection);
        var expected = IndependentQuadrature(source, request, start, duration);
        Assert.IsTrue(actual.Flux is > 0 and < 1);
        Assert.AreEqual(expected.Flux, actual.Flux, expected.Flux * .005);
        Assert.AreEqual(expected.X, actual.X, .02);
        Assert.AreEqual(expected.Y, actual.Y, .02);
    }

    [TestMethod]
    public async Task PreparationRefusesGeometryThatUnderSamplesNarrowPsf()
    {
        var projection = new ProjectionContext(ProjectionModel.Perspective, 64, 64, 100, 100, 128, 128,
            ProjectionAperture.Rectangular);
        var request = Request(projection);
        var source = SourceAt(projection, new(64.01, 64.5));
        var duration = TimeSpan.FromSeconds(10);
        var geometry = await new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]))
            .BuildAsync(request, Midpoint.AddSeconds(-5), duration).ConfigureAwait(false);
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => StellarExposureRenderPlan.Prepare(geometry,
            new Mono16SceneRenderOptions { ExposureSeconds = 10, PsfSigmaPixels = .05 }));
        Assert.AreEqual("stellar-exposure-psf-temporal-resolution-insufficient", exception.Message);
    }

    [TestMethod]
    [DataRow(64d, false)]
    [DataRow(64.01, false)]
    [DataRow(0d, false)]
    [DataRow(127.99, false)]
    [DataRow(96d, true)]
    public async Task NarrowPsfAcrossPixelSensorAndApertureBoundariesAgreesWithIndependentQuadrature(double sourceX, bool circular)
    {
        const double sigma = .05;
        var projection = new ProjectionContext(circular ? ProjectionModel.EquidistantFisheye : ProjectionModel.Perspective,
            64, 64, 100, 100, 128, 128,
            circular ? ProjectionAperture.Circular : ProjectionAperture.Rectangular,
            circular ? 32 : null, EnforceSensorBounds: false);
        var request = Request(projection);
        var source = SourceAt(projection, new(sourceX, circular ? 64 : 64.5));
        var duration = TimeSpan.FromSeconds(1);
        var start = Midpoint.AddSeconds(-.5);
        var geometry = await new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]))
            .BuildAsync(request, start, duration, new(MaximumStepPixels:
                TemporalPointSpreadRaster.MaximumTemporalStepPixels(sigma, 4))).ConfigureAwait(false);
        var plan = StellarExposureRenderPlan.Prepare(geometry,
            new Mono16SceneRenderOptions { ExposureSeconds = 1, PsfSigmaPixels = sigma }, new(MinimumSignalToNoise: .01));
        Assert.HasCount(1, plan.Sources);
        Assert.IsTrue(geometry.TemporalSlots is > 1 and <= 64);
        var raster = TemporalPointSpreadRaster.Rasterize(plan.Sources[0].Samples, projection, sigma, 4);
        var actual = Moments(raster.Pixels.ToDictionary(static pixel => pixel.Index, static pixel => pixel.AfterTransmission), projection);
        var expected = IndependentQuadrature(source, request, start, duration, sigma);
        Assert.IsTrue(expected.Flux > 0);
        Assert.AreEqual(expected.Flux, actual.Flux, expected.Flux * .005, "flux");
        Assert.AreEqual(expected.X, actual.X, .02, "native centroid X");
        Assert.AreEqual(expected.Y, actual.Y, .02, "native centroid Y");
    }

    [TestMethod]
    public void StronglyTruncatedPsfRefusesUnqualifiedTemporalIntegration()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(() =>
            TemporalPointSpreadRaster.MaximumTemporalStepPixels(1, .75));
        Assert.AreEqual("stellar-exposure-psf-support-unqualified", exception.Message);
    }

    private static VisibleSceneRequest Request(ProjectionContext projection)
        => new(Midpoint, Observer, projection, new(7, 100), Metadata, horizonPolicy: HorizonPolicy.ProjectionOnly);

    private static CelestialCatalogObject SourceAt(ProjectionContext projection, PixelPoint pixel)
    {
        var horizontal = ProjectorFactory.Create(projection).Unproject(pixel)!.Value;
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(horizontal, Midpoint,
            Observer.LatitudeDegrees, Observer.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, Midpoint);
        return new("reference", "reference", j2000.RightAscensionHours, j2000.DeclinationDegrees, 1);
    }

    private static PixelPoint? Project(CelestialCatalogObject source, VisibleSceneRequest request, DateTimeOffset utc)
    {
        var ofDate = EquatorialPrecession.PrecessJ2000(new(source.RightAscensionHours, source.DeclinationDegrees), utc);
        var horizontal = CoordinateTransforms.EquatorialToHorizontal(ofDate, utc,
            Observer.LatitudeDegrees, Observer.LongitudeDegrees);
        // Continue projecting centers beyond the aperture for independent PSF clipping;
        // clipping is applied separately to each native photosite below.
        var projection = request.Projection.ImageCircleRadiusPixels is { } circle
            ? request.Projection with { ImageCircleRadiusPixels = circle + 4 } : request.Projection;
        return ProjectorFactory.Create(projection).Project(horizontal);
    }

    // Independent uniform high-resolution integration and direct Gaussian equation. This does
    // not call the production temporal selector, sparse raster, SNR model or renderer.
    private static (double Flux, double X, double Y) IndependentQuadrature(CelestialCatalogObject source,
        VisibleSceneRequest request, DateTimeOffset start, TimeSpan duration, double sigma = 1)
    {
        const int count = 4096;
        var totals = new Dictionary<int, double>();
        for (var sample = 0; sample < count; sample++)
        {
            var utc = start.AddTicks(checked((long)(duration.Ticks * ((sample + .5) / count))));
            if (Project(source, request, utc) is not { } point) continue;
            var kernel = new List<(int X, int Y, double Weight)>();
            var sum = 0d;
            for (var y = (int)Math.Floor(point.Y) - 5; y <= (int)Math.Floor(point.Y) + 5; y++)
                for (var x = (int)Math.Floor(point.X) - 5; x <= (int)Math.Floor(point.X) + 5; x++)
                {
                    var square = Math.Pow(x + .5 - point.X, 2) + Math.Pow(y + .5 - point.Y, 2);
                    if (square > 16) continue;
                    var weight = Math.Exp(-square / (2 * sigma * sigma)); sum += weight; kernel.Add((x, y, weight));
                }
            foreach (var pixel in kernel)
            {
                if (pixel.X < 0 || pixel.X >= request.Projection.WidthPixels || pixel.Y < 0 ||
                    pixel.Y >= request.Projection.HeightPixels || !request.Projection.ContainsSample(pixel.X + .5, pixel.Y + .5)) continue;
                var index = pixel.Y * request.Projection.WidthPixels + pixel.X;
                totals[index] = totals.GetValueOrDefault(index) + pixel.Weight / sum / count;
            }
        }
        return Moments(totals, request.Projection);
    }

    private static (double Flux, double X, double Y) Moments(Dictionary<int, double> pixels, ProjectionContext projection)
    {
        var flux = pixels.Values.Sum();
        return (flux, pixels.Sum(pixel => (pixel.Key % projection.WidthPixels + .5) * pixel.Value) / flux,
            pixels.Sum(pixel => (pixel.Key / projection.WidthPixels + .5) * pixel.Value) / flux);
    }
}
