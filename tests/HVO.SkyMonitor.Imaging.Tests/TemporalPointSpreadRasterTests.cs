using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class TemporalPointSpreadRasterTests
{
    private static readonly ProjectionContext Projection = new(ProjectionModel.Perspective, 16, 16, 100, 100,
        32, 32, ProjectionAperture.Rectangular);

    [TestMethod]
    public void PointAndTrailConserveOriginalEnergyWithoutSizeChangingWithFlux()
    {
        var point = TemporalPointSpreadRaster.Rasterize([new(new(16.5, 16.5), 1)], Projection, 1, 4);
        var trail = TemporalPointSpreadRaster.Rasterize([new(new(15.5, 16.5), .25), new(new(16.5, 16.5), .5), new(new(18.5, 16.5), .25)], Projection, 1, 4);
        Assert.AreEqual(1, point.RetainedBeforeTransmission, 1e-12);
        Assert.AreEqual(1, trail.RetainedBeforeTransmission, 1e-12);
        var center = trail.Pixels.Sum(pixel => (pixel.Index % 32 + .5) * pixel.BeforeTransmission);
        Assert.AreEqual(16.75, center, 1e-12);
        Assert.IsTrue(trail.Pixels.Count > point.Pixels.Count);
        Assert.IsTrue(trail.Pixels.Zip(trail.Pixels.Skip(1)).All(pair => pair.First.Index < pair.Second.Index));
    }

    [TestMethod]
    public void ClippedOffSensorKernelIsTheSameKernelWithLostEnergy()
    {
        var outside = TemporalPointSpreadRaster.Rasterize([new(new(-1.5, 16.5), 1)], Projection, 1, 4);
        var inside = TemporalPointSpreadRaster.Rasterize([new(new(16.5, 16.5), 1)], Projection, 1, 4);
        var surviving = inside.Pixels.Where(pixel => pixel.Index % 32 >= 18).Sum(pixel => pixel.BeforeTransmission);
        Assert.AreEqual(surviving, outside.RetainedBeforeTransmission, 1e-12);
        Assert.IsTrue(outside.RetainedBeforeTransmission is > 0 and < 1);
        Assert.AreEqual(1, outside.OriginalIntervalEnergy, 1e-12);
    }

    [TestMethod]
    public void TemporalTransmissionIsAppliedBeforeIndependentSamplesAreAveraged()
    {
        var raster = TemporalPointSpreadRaster.Rasterize([new(new(10.5, 16.5), .25, .2), new(new(20.5, 16.5), .75, .8)], Projection, 1, 4);
        Assert.AreEqual(1, raster.RetainedBeforeTransmission, 1e-12);
        Assert.AreEqual(.65, raster.RetainedAfterTransmission, 1e-12);
        var center = raster.Pixels.Sum(pixel => (pixel.Index % 32 + .5) * pixel.AfterTransmission) / .65;
        Assert.AreEqual((10.5 * .05 + 20.5 * .6) / .65, center, 1e-12);
        var omitted = TemporalPointSpreadRaster.Rasterize([new(new(16.5, 16.5), .3)], Projection, 1, 4);
        Assert.AreEqual(.3, omitted.RetainedBeforeTransmission, 1e-12);
    }

    [TestMethod]
    public void ExactAndOneOverRasterAndSparseBudgetsAreObservable()
    {
        WeightedPointSpreadSample[] source = [new(new(16.5, 16.5), 1)];
        var accepted = TemporalPointSpreadRaster.Rasterize(source, Projection, 1, 4);
        var exact = TemporalPointSpreadRaster.Rasterize(source, Projection, 1, 4, accepted.KernelCellVisits, accepted.Pixels.Count);
        Assert.AreEqual(accepted.KernelCellVisits, exact.KernelCellVisits);
        Assert.Throws<InvalidOperationException>(() => TemporalPointSpreadRaster.Rasterize(source, Projection, 1, 4,
            accepted.KernelCellVisits - 1));
        Assert.Throws<InvalidOperationException>(() => TemporalPointSpreadRaster.Rasterize(source, Projection, 1, 4,
            maximumSparsePixels: accepted.Pixels.Count - 1));
        Assert.Throws<ArgumentException>(() => TemporalPointSpreadRaster.Rasterize([new(new(16, 16), .6), new(new(16, 16), .6)], Projection, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => TemporalPointSpreadRaster.Rasterize([new(new(double.NaN, 16), 1)], Projection, 1, 4));
    }
}
