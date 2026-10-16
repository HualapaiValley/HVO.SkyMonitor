namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarExposureGeometryTests
{
    private static readonly DateTimeOffset MidpointUtc = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(0, 0, 0);
    private static readonly CatalogMetadata Metadata = new("HYG", "fixture", new("https://astronexus.com/projects/hyg"),
        new string('A', 64), "CC BY-SA 4.0", "2");
    private static readonly string[] TestConstellationIds = ["TST"];
    private static readonly string[] RankedEnteringIds = ["entering", "inside"];
    private static readonly string[] CompleteEnteringIds = ["entering", "inside", "fainter"];
    private static readonly string[] RankedEndpointIds = ["brightest", "endpoint-a", "endpoint-b"];

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
        var atDirectionBound = await builder.BuildAsync(request, MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20),
            new(MaximumCandidates: 2, MaximumDirectionEvaluations: accepted.DirectionEvaluations)).ConfigureAwait(false);
        Assert.AreEqual(accepted.DirectionEvaluations, atDirectionBound.DirectionEvaluations);
        var directionFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.BuildAsync(request, MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20),
                new(MaximumCandidates: 2, MaximumDirectionEvaluations: accepted.DirectionEvaluations - 1)).ConfigureAwait(false))
            .ConfigureAwait(false);
        StringAssert.Contains(directionFailure.Message, "direction-budget-exceeded", StringComparison.Ordinal);
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
    public async Task BrightestSupportedKeepsEnteringStarsInRankWhileExhaustiveStillRefuses()
    {
        var projection = Projection(ProjectionModel.Perspective);
        var unbounded = projection with { EnforceSensorBounds = false };
        // The brightest star never reaches the frame, so it must not use up the rank. The entering star's centre
        // stays off the sensor but its PSF support crosses the edge, so it outranks fainter in-frame stars.
        var catalog = new InMemoryCelestialCatalog([
            AtPixel(unbounded, new(-200, 64)) with { Id = "outside", Magnitude = 0 },
            AtPixel(unbounded, new(-1.5, 64)) with { Id = "entering", Magnitude = 1 },
            AtPixel(projection, new(64, 64)) with { Id = "inside", Magnitude = 2 },
            AtPixel(projection, new(32, 32)) with { Id = "fainter", Magnitude = 3 }
        ]);
        var builder = new StellarExposureGeometryBuilder(catalog);
        var request = Request(projection);
        var start = MidpointUtc.AddSeconds(-.05);
        var exposure = TimeSpan.FromSeconds(.1);

        var ranked = await builder.BuildAsync(request, start, exposure,
            new(MaximumCandidates: 2, Selection: StellarCandidateSelection.BrightestSupported)).ConfigureAwait(false);
        var complete = await builder.BuildAsync(request, start, exposure, new(MaximumCandidates: 4)).ConfigureAwait(false);

        Assert.AreEqual(4, ranked.CandidateCount, "The candidate count still reports the complete merged query.");
        CollectionAssert.AreEqual(RankedEnteringIds, ranked.Sources.Select(static item => item.Source.Id).ToArray());
        Assert.IsNull(ranked.Sources[0].MidpointReference);
        CollectionAssert.AreEqual(CompleteEnteringIds,
            complete.Sources.Select(static item => item.Source.Id).ToArray());
        Assert.IsLessThan(complete.DirectionEvaluations, ranked.DirectionEvaluations,
            "Stars past the rank are not evaluated.");
        var candidateFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.BuildAsync(request, start, exposure, new(MaximumCandidates: 2)).ConfigureAwait(false))
            .ConfigureAwait(false);
        StringAssert.Contains(candidateFailure.Message, "candidate-budget-exceeded", StringComparison.Ordinal);
        var directionFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await builder.BuildAsync(request, start, exposure, new(MaximumCandidates: 2,
                MaximumDirectionEvaluations: ranked.DirectionEvaluations - 1,
                Selection: StellarCandidateSelection.BrightestSupported)).ConfigureAwait(false)).ConfigureAwait(false);
        StringAssert.Contains(directionFailure.Message, "direction-budget-exceeded", StringComparison.Ordinal);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new StellarExposureGeometryOptions(Selection: (StellarCandidateSelection)2).Validate());
    }

    [TestMethod]
    public async Task BrightestSupportedAddsSweptConstellationEndpointStarsBeyondTheRank()
    {
        var projection = Projection(ProjectionModel.Perspective);
        var unbounded = projection with { EnforceSensorBounds = false };
        var catalog = new InMemoryCelestialCatalog([
            AtPixel(projection, new(64, 64)) with { Id = "brightest", Magnitude = 1 },
            AtPixel(projection, new(40, 40)) with { Id = "skipped", Magnitude = 2 },
            AtPixel(projection, new(90, 90)) with { Id = "endpoint-a", Magnitude = 5, HipparcosId = "1" },
            AtPixel(projection, new(20, 100)) with { Id = "endpoint-b", Magnitude = 8, HipparcosId = "2" },
            AtPixel(unbounded, new(-200, 64)) with { Id = "endpoint-outside", Magnitude = 6, HipparcosId = "3" }
        ]);
        var topology = new InMemoryConstellationTopology([new("TST", "1", "2"), new("TST", "2", "3")]);
        var builder = new StellarExposureGeometryBuilder(catalog, topology);
        // The magnitude query stops at 7, so the fainter endpoint star arrives only through the endpoint merge.
        var request = new VisibleSceneRequest(MidpointUtc, Observer, projection, new(7, 1), Metadata,
            constellationIds: TestConstellationIds, includeConstellationEndpointStars: true);

        var ranked = await builder.BuildAsync(request, MidpointUtc.AddSeconds(-.05), TimeSpan.FromSeconds(.1),
            new(MaximumCandidates: 1, Selection: StellarCandidateSelection.BrightestSupported)).ConfigureAwait(false);

        Assert.AreEqual(5, ranked.CandidateCount);
        CollectionAssert.AreEqual(RankedEndpointIds,
            ranked.Sources.Select(static item => item.Source.Id).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.BuildAsync(request,
            MidpointUtc.AddSeconds(-.05), TimeSpan.FromSeconds(.1), new(MaximumCandidates: 1)).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(32768)]
    public async Task BrightestSupportedWithinTheBoundIsIdenticalToExhaustiveSelection(int maximumCandidates)
    {
        var projection = Projection(ProjectionModel.EquidistantFisheye);
        var belowHorizon = EquatorialPrecession.PrecessToJ2000(CoordinateTransforms.HorizontalToEquatorial(
            new(-30, 0), MidpointUtc, Observer.LatitudeDegrees, Observer.LongitudeDegrees), MidpointUtc);
        var catalog = new InMemoryCelestialCatalog([
            AtPixel(projection, new(64, 64)) with { Id = "centre", Magnitude = 1 },
            AtPixel(projection, new(20, 64)) with { Id = "west", Magnitude = 2, HipparcosId = "1" },
            AtPixel(projection, new(64, 110)) with { Id = "south", Magnitude = 2 },
            new("below", "below", belowHorizon.RightAscensionHours, belowHorizon.DeclinationDegrees, 3),
            AtPixel(projection, new(100, 30)) with { Id = "endpoint", Magnitude = 9, HipparcosId = "2" }
        ]);
        var topology = new InMemoryConstellationTopology([new("TST", "1", "2")]);
        var builder = new StellarExposureGeometryBuilder(catalog, topology);
        var request = new VisibleSceneRequest(MidpointUtc, Observer, projection, new(7, 1), Metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon, constellationIds: TestConstellationIds, includeConstellationEndpointStars: true);
        var start = MidpointUtc.AddSeconds(-10);
        var exposure = TimeSpan.FromSeconds(20);

        var exhaustive = await builder.BuildAsync(request, start, exposure,
            new(MaximumCandidates: maximumCandidates)).ConfigureAwait(false);
        var ranked = await builder.BuildAsync(request, start, exposure,
            new(MaximumCandidates: maximumCandidates, Selection: StellarCandidateSelection.BrightestSupported))
            .ConfigureAwait(false);

        Assert.AreEqual(5, exhaustive.CandidateCount);
        Assert.HasCount(4, exhaustive.Sources);
        Assert.AreEqual(exhaustive.CandidateCount, ranked.CandidateCount);
        Assert.AreEqual(exhaustive.TemporalSlots, ranked.TemporalSlots);
        Assert.AreEqual(exhaustive.DirectionEvaluations, ranked.DirectionEvaluations);
        Assert.AreEqual(exhaustive.MaximumSampleMotionPixels, ranked.MaximumSampleMotionPixels);
        Assert.HasCount(exhaustive.Sources.Count, ranked.Sources);
        foreach (var (expected, actual) in exhaustive.Sources.Zip(ranked.Sources))
        {
            Assert.AreEqual(expected.Source, actual.Source);
            Assert.AreEqual(expected.MidpointReference, actual.MidpointReference);
            CollectionAssert.AreEqual(expected.Samples.ToArray(), actual.Samples.ToArray());
        }
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
    [DataRow(180d, 1e-7)]
    [DataRow(0d, -1e-7)]
    public async Task GrazingHorizonPassageBetweenEqualSignSampleTimesPreservesVisibleEnergy(double azimuth, double peakAltitude)
    {
        var observer = new ObserverLocation(35, 0, 0);
        var projection = Projection(ProjectionModel.Perspective) with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = azimuth };
        var peakUtc = MidpointUtc.AddSeconds(2.5);
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(new(peakAltitude, azimuth), peakUtc,
            observer.LatitudeDegrees, observer.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, peakUtc);
        var source = new CelestialCatalogObject("grazing", "grazing", j2000.RightAscensionHours, j2000.DeclinationDegrees, 1);
        var start = MidpointUtc.AddSeconds(-10);
        var exposure = TimeSpan.FromSeconds(20);
        var visible = 0;
        for (var index = 0; index < 8192; index++)
        {
            var time = start.AddTicks(exposure.Ticks * (2 * index + 1) / 16384);
            var position = EquatorialPrecession.PrecessJ2000(j2000, time);
            if (CoordinateTransforms.EquatorialToHorizontal(position, time, observer.LatitudeDegrees,
                observer.LongitudeDegrees).AltitudeDegrees >= 0) visible++;
        }
        Assert.IsTrue(visible is > 0 and < 8192, "The independent dense oracle must see a brief grazing passage.");
        var geometry = await new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source])).BuildAsync(
            new VisibleSceneRequest(MidpointUtc, observer, projection, new(7, 1), Metadata,
                horizonPolicy: HorizonPolicy.GeometricHorizon), start, exposure).ConfigureAwait(false);
        Assert.HasCount(1, geometry.Sources);
        Assert.AreEqual((double)visible / 8192, geometry.Sources[0].Samples.Sum(sample => sample.ExposureFraction), .001);
        Assert.IsTrue(geometry.Sources[0].Samples.All(sample => sample.Horizontal.AltitudeDegrees >= 0));
    }

    [TestMethod]
    public async Task RefractedMotionIsExplicitlyUnsupportedRatherThanUsingGeometricSpeedBound()
    {
        var projection = Projection(ProjectionModel.Perspective);
        var source = AtPixel(projection, new(64, 64));
        var request = new VisibleSceneRequest(MidpointUtc, Observer, projection, new(7, 1), Metadata,
            refraction: new(true), horizonPolicy: HorizonPolicy.GeometricHorizon);
        var builder = new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([source]));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await builder.BuildAsync(request,
            MidpointUtc.AddSeconds(-10), TimeSpan.FromSeconds(20)).ConfigureAwait(false)).ConfigureAwait(false);
        var zero = await builder.BuildAsync(request, MidpointUtc, TimeSpan.Zero).ConfigureAwait(false);
        Assert.IsEmpty(zero.Sources);
    }

    [TestMethod]
    public async Task ZeroExposureCancellationAndConflictingMidpointAreExplicit()
    {
        var projection = Projection(ProjectionModel.Perspective);
        var builder = new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog([AtPixel(projection, new(64, 64))]));
        var empty = await builder.BuildAsync(Request(projection), MidpointUtc, TimeSpan.Zero).ConfigureAwait(false);
        Assert.AreEqual(0, empty.CandidateCount);
        Assert.IsEmpty(empty.Sources);
        // The full equisolid domain has an unbounded edge Jacobian, but a zero-duration
        // exposure needs neither projection samples nor a catalog query.
        var singularProjection = new ProjectionContext(ProjectionModel.EquisolidFisheye,
            64, 64, 100, 100, 128, 128, ProjectionAperture.Circular, 200);
        var singularEmpty = await builder.BuildAsync(Request(singularProjection), MidpointUtc, TimeSpan.Zero)
            .ConfigureAwait(false);
        Assert.AreEqual(0, singularEmpty.CandidateCount);
        Assert.IsEmpty(singularEmpty.Sources);
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
