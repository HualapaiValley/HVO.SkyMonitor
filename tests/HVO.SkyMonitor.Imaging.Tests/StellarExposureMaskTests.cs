using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarExposureMaskTests
{
    private static readonly DateTimeOffset Utc = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(0, 0, 0);
    private static readonly CatalogMetadata Metadata = new("HYG", "fixture", new("https://astronexus.com/projects/hyg"),
        new string('A', 64), "CC BY-SA 4.0", "2");

    [TestMethod]
    [DataRow(ProjectionModel.Perspective, 1d)]
    [DataRow(ProjectionModel.Perspective, .5d)]
    [DataRow(ProjectionModel.EquidistantFisheye, 1d)]
    [DataRow(ProjectionModel.EquidistantFisheye, .5d)]
    [DataRow(ProjectionModel.EquisolidFisheye, 1d)]
    [DataRow(ProjectionModel.EquisolidFisheye, .5d)]
    [DataRow(ProjectionModel.OrthographicFisheye, 1d)]
    [DataRow(ProjectionModel.OrthographicFisheye, .5d)]
    [DataRow(ProjectionModel.StereographicFisheye, 1d)]
    [DataRow(ProjectionModel.StereographicFisheye, .5d)]
    public async Task SweptMaskCoversDenseIndependentTimesIncludingShutterEndpoints(ProjectionModel model, double scale)
    {
        var projection = Projection(model) with { RollDegrees = 37, HorizontalFlip = true };
        var source = AtPixel(projection, new(64 - .5 / scale, 64 - .5 / scale));
        var start = Utc.AddSeconds(-60);
        var geometry = await Geometry(projection, [source], start, TimeSpan.FromSeconds(120), .5, .5).ConfigureAwait(false);
        var width = (int)(128 * scale);
        var result = StellarExposureMask.Create(geometry, width, width, scale, scale, options: new(.5));
        Assert.IsTrue(result.TemporalSamples > 1);
        var projector = ProjectorFactory.Create(projection);
        var covered = 0;
        // This oracle samples 1,025 separate times, including both shutter endpoints.
        // It does not use the geometry samples or the mask producer's circles.
        for (var index = 0; index <= 1024; index++)
        {
            var time = start.AddTicks(TimeSpan.FromSeconds(120).Ticks * index / 1024);
            var ofDate = EquatorialPrecession.PrecessJ2000(new(source.RightAscensionHours, source.DeclinationDegrees), time);
            var direction = CoordinateTransforms.EquatorialToHorizontal(ofDate, time, Observer.LatitudeDegrees, Observer.LongitudeDegrees);
            var pixel = projector.Project(direction)!.Value;
            for (var y = 0; y < width; y++)
                for (var x = 0; x < width; x++)
                    if (Math.Pow(x + .5 - pixel.X * scale, 2) + Math.Pow(y + .5 - pixel.Y * scale, 2) <= Math.Pow(.5 * scale, 2))
                    {
                        Assert.IsTrue(Linear16MaskOperations.IsExcluded(result.Mask, x, y), $"Uncovered support at {time:O}, {x},{y}");
                        covered++;
                    }
        }
        Assert.IsTrue(covered > 0, "The oracle must exercise actual supported detector pixels.");
    }

    [TestMethod]
    public async Task CompleteCatalogQueryAndExactRasterBudgetRefuseOneOverBound()
    {
        var projection = Projection(ProjectionModel.Perspective);
        var first = AtPixel(projection, new(64, 64));
        var geometry = await Geometry(projection, [first, first with { Id = "second" }], Utc.AddSeconds(-.05),
            TimeSpan.FromSeconds(.1), 2).ConfigureAwait(false);
        Assert.AreEqual(2, geometry.CandidateCount, "The scene request has MaximumResults=1; swept geometry must not silently Take(1).");
        var result = StellarExposureMask.Create(geometry, 128, 128, 1, 1, options: new(2));
        Assert.AreEqual(2, result.TemporalSamples);
        var atBound = StellarExposureMask.Create(geometry, 128, 128, 1, 1,
            options: new(2, MaximumKernelCellVisits: result.KernelCellVisits));
        CollectionAssert.AreEqual(result.Mask.Bits.ToArray(), atBound.Mask.Bits.ToArray());
        var exception = Assert.Throws<InvalidOperationException>(() => StellarExposureMask.Create(geometry, 128, 128, 1, 1,
            options: new(2, MaximumKernelCellVisits: result.KernelCellVisits - 1)));
        StringAssert.Contains(exception.Message, "raster-budget-exceeded", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ClippedSourceSupportSurvivesAndTransformsWithoutRendererAdmissions()
    {
        var projection = Projection(ProjectionModel.Perspective) with { EnforceSensorBounds = false };
        var source = AtPixel(projection, new(-1.25, 64));
        var geometry = await Geometry(projection with { EnforceSensorBounds = true }, [source], Utc.AddSeconds(-.05),
            TimeSpan.FromSeconds(.1), 2).ConfigureAwait(false);
        Assert.IsNull(geometry.Sources[0].MidpointReference);
        var clipped = StellarExposureMask.Create(geometry, 128, 128, 1, 1, options: new(2));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(clipped.Mask, 0, 63));
        var shifted = StellarExposureMask.Create(geometry, 128, 128, .5, .5, 12, 8, new(2));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(shifted.Mask, 11, 39));
        Assert.IsFalse(Linear16MaskOperations.IsExcluded(shifted.Mask, 0, 63));
        var far = StellarExposureMask.Create(geometry, 128, 128, 1, 1, 16777216, 16777216, new(2));
        Assert.AreEqual(0, far.KernelCellVisits);
        Assert.IsTrue(far.Mask.Bits.Span.IndexOfAnyExcept((byte)0) < 0);
    }

    [TestMethod]
    public async Task EmptyExposureSupportMismatchAndCancellationAreExplicit()
    {
        var projection = Projection(ProjectionModel.Perspective);
        var source = AtPixel(projection, new(64, 64));
        var geometry = await Geometry(projection, [source], Utc, TimeSpan.Zero, 2, .5).ConfigureAwait(false);
        var result = StellarExposureMask.Create(geometry, 128, 128, 1, 1, options: new(2));
        Assert.AreEqual(0, result.KernelCellVisits);
        Assert.IsTrue(result.Mask.Bits.Span.IndexOfAnyExcept((byte)0) < 0);
        Assert.Throws<ArgumentException>(() => StellarExposureMask.Create(geometry, 128, 128, 1, 1, options: new(3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => StellarExposureMask.Create(geometry, 128, 128, 2, 1, options: new(2)));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync().ConfigureAwait(false);
        Assert.Throws<OperationCanceledException>(() => StellarExposureMask.Create(geometry, 128, 128, 1, 1,
            options: new(2), cancellationToken: canceled.Token));
    }

    private static async Task<StellarExposureGeometry> Geometry(ProjectionContext projection,
        IReadOnlyList<CelestialCatalogObject> sources, DateTimeOffset start, TimeSpan exposure, double support, double step = .15)
        => await new StellarExposureGeometryBuilder(new InMemoryCelestialCatalog(sources)).BuildAsync(
            new VisibleSceneRequest(start.AddTicks(exposure.Ticks / 2), Observer, projection, new(7, 1), Metadata,
                horizonPolicy: HorizonPolicy.ProjectionOnly), start, exposure,
            new(MaximumCandidates: sources.Count, MaximumStepPixels: step, PsfSupportRadiusPixels: support)).ConfigureAwait(false);

    private static ProjectionContext Projection(ProjectionModel model) => new(model, 64, 64, 100, 100, 128, 128,
        model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
        model == ProjectionModel.Perspective ? null : 94);

    private static CelestialCatalogObject AtPixel(ProjectionContext projection, PixelPoint pixel)
    {
        var horizontal = ProjectorFactory.Create(projection).Unproject(pixel)!.Value;
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(horizontal, Utc, Observer.LatitudeDegrees, Observer.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, Utc);
        return new("source", "source", j2000.RightAscensionHours, j2000.DeclinationDegrees, 1);
    }
}
