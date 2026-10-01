namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarExposureGeometryTests
{
    private static readonly DateTimeOffset MidpointUtc = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(0, 0, 0);
    private static readonly CatalogMetadata Metadata = new("HYG", "fixture", new("https://astronexus.com/projects/hyg"),
        new string('A', 64), "CC BY-SA 4.0", "2");

    [TestMethod]
    public async Task Asi676TwentySecondEquatorialMotionAndShortPointLimit()
    {
        const double focal = 1097.0456;
        var projection = new ProjectionContext(ProjectionModel.EquidistantFisheye, 1776, 1776, focal, focal,
            3552, 3552, ProjectionAperture.Circular, focal * 85 * Math.PI / 180);
        var source = AtPixel(projection, new(1776, 1776));
        var builder = new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]));
        var request = Request(projection);
        var geometry = await builder.BuildAsync(request, MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.HasCount(1, geometry.Sources);
        var samples = geometry.Sources[0].Samples;
        Assert.IsTrue(samples.Count is > 1 and <= 64);
        var span = Math.Sqrt(Math.Pow(samples[^1].Pixel.X - samples[0].Pixel.X, 2) +
            Math.Pow(samples[^1].Pixel.Y - samples[0].Pixel.Y, 2));
        // Independent geometric sidereal rate; samples are slot midpoints rather than endpoints.
        var expected = focal * 7.2921158553e-5 * 20 * (samples.Count - 1) / samples.Count;
        Assert.AreEqual(expected, span, .01);
        Assert.AreEqual(1, samples.Sum(sample => sample.ExposureFraction), 1e-12);
        var shortGeometry = await builder.BuildAsync(request, MidpointUtc.AddSeconds(-.05), TimeSpan.FromSeconds(.1)).ConfigureAwait(false);
        Assert.HasCount(1, shortGeometry.Sources[0].Samples);
        Assert.AreEqual(1776, shortGeometry.Sources[0].Samples[0].Pixel.X, 1e-6);
        Assert.AreEqual(1776, shortGeometry.Sources[0].Samples[0].Pixel.Y, 1e-6);
    }

    [TestMethod]
    [DataRow(ProjectionModel.EquidistantFisheye)]
    [DataRow(ProjectionModel.EquisolidFisheye)]
    [DataRow(ProjectionModel.OrthographicFisheye)]
    [DataRow(ProjectionModel.StereographicFisheye)]
    [DataRow(ProjectionModel.Perspective)]
    public async Task ProjectionFamiliesPreserveReferenceAndOrderedExactWeights(ProjectionModel model)
    {
        var projection = Projection(model) with { RollDegrees = 37, HorizontalFlip = true };
        var source = AtPixel(projection, new(64, 64));
        var builder = new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]));
        var geometry = await builder.BuildAsync(Request(projection), MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.HasCount(1, geometry.Sources);
        var result = geometry.Sources[0];
        Assert.IsNotNull(result.MidpointReference);
        Assert.AreEqual(64, result.MidpointReference.Pixel.X, 1e-6);
        Assert.AreEqual(64, result.MidpointReference.Pixel.Y, 1e-6);
        Assert.AreEqual(1, result.Samples.Sum(sample => sample.ExposureFraction), 1e-12);
        Assert.IsTrue(result.Samples.Zip(result.Samples.Skip(1)).All(pair => pair.First.Utc < pair.Second.Utc));
    }

    [TestMethod]
    public async Task OffSensorCentersWithPsfSupportSurviveTheSweptSelection()
    {
        var projection = Projection(ProjectionModel.Perspective);
        var source = AtPixel(projection with { EnforceSensorBounds = false }, new(-1.5, 64));
        var builder = new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]));
        var geometry = await builder.BuildAsync(Request(projection), MidpointUtc.AddSeconds(-.05), TimeSpan.FromSeconds(.1)).ConfigureAwait(false);
        Assert.AreEqual(1, geometry.CandidateCount);
        Assert.HasCount(1, geometry.Sources);
        Assert.IsNull(geometry.Sources[0].MidpointReference);
        Assert.IsTrue(geometry.Sources[0].Samples.All(sample => sample.Pixel.X < 0));
    }

    [TestMethod]
    public async Task CandidateAndTemporalOneOverBoundsRefuseWithoutSilentTruncation()
    {
        var projection = Projection(ProjectionModel.EquidistantFisheye);
        var first = AtPixel(projection, new(64, 64));
        var second = first with { Id = "second", Magnitude = 2 };
        var builder = new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([first, second]));
        var request = Request(projection);
        var accepted = await builder.BuildAsync(request, MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20),
            new(MaximumCandidates: 2)).ConfigureAwait(false);
        Assert.AreEqual(2, accepted.CandidateCount);
        Assert.HasCount(2, accepted.Sources);
        var candidateFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.BuildAsync(request, MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20),
                new(MaximumCandidates: 1)).ConfigureAwait(false)).ConfigureAwait(false);
        StringAssert.Contains(candidateFailure.Message, "candidate-budget-exceeded", StringComparison.Ordinal);
        var temporalFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.BuildAsync(request, MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20),
                new(MaximumSamplesPerSource: 1)).ConfigureAwait(false)).ConfigureAwait(false);
        StringAssert.Contains(temporalFailure.Message, "temporal-budget-exceeded", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task HorizonCrossingUsesOnlyVisibleDurationWithoutRenormalization()
    {
        var projection = Projection(ProjectionModel.Perspective) with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = 90 };
        var source = AtPixel(projection, new(64, 64));
        var builder = new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]));
        var geometry = await builder.BuildAsync(Request(projection, HorizonPolicy.GeometricHorizon),
            MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Assert.HasCount(1, geometry.Sources);
        Assert.AreEqual(.5, geometry.Sources[0].Samples.Sum(sample => sample.ExposureFraction), 1e-4);
        Assert.IsTrue(geometry.Sources[0].Samples.All(sample => sample.Horizontal.AltitudeDegrees >= 0));
    }

    [TestMethod]
    public async Task ZeroExposureCancellationAndConflictingMidpointAreExplicit()
    {
        var projection = Projection(ProjectionModel.Perspective);
        var builder = new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([AtPixel(projection, new(64, 64))]));
        var empty = await builder.BuildAsync(Request(projection), MidpointUtc, TimeSpan.Zero).ConfigureAwait(false);
        Assert.AreEqual(0, empty.CandidateCount);
        Assert.IsEmpty(empty.Sources);
        using var canceled = new CancellationTokenSource(); await canceled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await builder.BuildAsync(Request(projection),
            MidpointUtc, TimeSpan.Zero, cancellationToken: canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(async () => await builder.BuildAsync(Request(projection),
            MidpointUtc, TimeSpan.FromSeconds(20)).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static ProjectionContext Projection(ProjectionModel model) => new(model, 64, 64, 100, 100, 128, 128,
        model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
        model == ProjectionModel.Perspective ? null : 94);

    private static VisibleSceneRequest Request(ProjectionContext projection, HorizonPolicy horizon = HorizonPolicy.ProjectionOnly)
        => new(MidpointUtc, Observer, projection, new(7, 1), Metadata, horizonPolicy: horizon);

    private static CelestialCatalogObject AtPixel(ProjectionContext projection, PixelPoint pixel)
    {
        var horizontal = ProjectorFactory.Create(projection).Unproject(pixel)!.Value;
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(horizontal, MidpointUtc, Observer.LatitudeDegrees, Observer.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, MidpointUtc);
        return new("source", "source", j2000.RightAscensionHours, j2000.DeclinationDegrees, 1);
    }
}
