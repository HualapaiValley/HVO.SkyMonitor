using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SolarDiskRenderPlanTests
{
    private static readonly ProjectionContext Projection = new(ProjectionModel.Perspective,
        32, 32, 4000, 4000, 64, 64, ProjectionAperture.Rectangular);

    [TestMethod]
    public void DiskConservesFluxAndLunarPhaseMirrorsWithCameraOrientation()
    {
        var moon = new SolarDiskAppearance(SolarSystemBody.Moon, DateTimeOffset.UnixEpoch,
            new(90, 0), .25, 0, .5, 90, 384400);
        var plan = new SolarDiskRenderPlan(Projection, [moon], 1000);
        var flipped = new SolarDiskRenderPlan(Projection with { HorizontalFlip = true }, [moon], 1000);
        double flux = 0, weightedX = 0;
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                var rate = plan.ElectronRate(x, y);
                flux += rate;
                weightedX += (x + .5) * rate;
                Assert.AreEqual(rate, flipped.ElectronRate(63 - x, y), 1e-8);
            }
        Assert.AreEqual(1000, flux, 1e-8);
        Assert.IsTrue(weightedX / flux > 36, "The bright hemisphere must face local east before camera mirroring.");
    }

    [TestMethod]
    public void HorizonClippingNeverRestoresLostDiskFlux()
    {
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, DateTimeOffset.UnixEpoch,
            new(0, 180), .25, 0, 1, 0, 149600000);
        var horizon = Projection with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = 180 };
        var plan = new SolarDiskRenderPlan(horizon, [disk], 1000);
        var total = Enumerable.Range(0, 64 * 64).Sum(i => plan.ElectronRate(i % 64, i / 64));
        Assert.AreEqual(500, total, 2);
        var hidden = new SolarDiskRenderPlan(horizon, [disk with { Direction = new(-1, 180) }], 1000);
        Assert.AreEqual(0, hidden.MaximumElectronRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolarDiskRenderPlan(Projection,
            [disk with { IlluminatedFraction = double.NaN }], 1000));
    }
    [TestMethod]
    public void OffAxisPerspectiveDiskHasNoInteriorSamplingHolesAndConservesFlux()
    {
        var projection = new ProjectionContext(ProjectionModel.Perspective,
            2048, 2048, 100, 100, 4096, 4096, ProjectionAperture.Rectangular);
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, DateTimeOffset.UnixEpoch,
            new(5, 90), .25, 0, 1, 0, 149600000);
        var plan = new SolarDiskRenderPlan(projection, [disk], 1000);
        var projector = ProjectorFactory.Create(projection);
        var center = CameraBasis.FromHorizontal(disk.Direction);
        var checkedPixels = 0;
        double total = 0;
        for (var y = 2030; y < 2066; y++)
            for (var x = 3120; x < 3270; x++)
            {
                var rate = plan.ElectronRate(x, y);
                total += rate;
                var direction = projector.Unproject(new(x + .5, y + .5));
                if (direction is null || EnuVector.Dot(center, CameraBasis.FromHorizontal(direction.Value)) < Math.Cos(.2 * Math.PI / 180)) continue;
                checkedPixels++;
                Assert.IsTrue(rate > 0, $"Interior disk pixel ({x},{y}) must receive light.");
            }
        Assert.IsTrue(checkedPixels > 100);
        Assert.AreEqual(1000, total, 1e-8);
    }

    [TestMethod]
    public void ExcessiveProjectedMagnificationFailsExplicitlyAndOffFieldDisksStayEmpty()
    {
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, DateTimeOffset.UnixEpoch,
            new(90, 0), .25, 0, 1, 0, 149600000);
        var excessive = Projection with { FocalLengthXPixels = 400000, FocalLengthYPixels = 400000 };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new SolarDiskRenderPlan(excessive, [disk], 1000));
        StringAssert.Contains(error.Message, "projection sampling budget", StringComparison.Ordinal);
        var outside = new SolarDiskRenderPlan(Projection, [disk with { Direction = new(20, 90) }], 1000);
        Assert.AreEqual(0, outside.MaximumElectronRate);
    }

    [TestMethod]
    public void InteriorDiskRetainsEveryVisibleElectronThroughThePsf()
    {
        var moon = new SolarDiskAppearance(SolarSystemBody.Moon, DateTimeOffset.UnixEpoch,
            new(90, 0), .25, 0, 1, 0, 384400);
        var plan = new SolarDiskRenderPlan(Projection, [moon], 1000);
        Assert.AreEqual(1000, plan.SourceElectronRate, 1e-9);
        Assert.AreEqual(plan.SourceElectronRate, plan.VisibleElectronRate, 1e-9);
        Assert.AreEqual(plan.VisibleElectronRate, plan.RetainedElectronRate, 1e-9);
        Assert.AreEqual(plan.RetainedElectronRate, Sum(plan, 64, 64), 1e-9);
        Assert.IsTrue(plan.KernelCellVisits > 0 && plan.KernelCellVisits <= SolarDiskRenderPlan.MaximumKernelCellVisits);
    }

    [TestMethod]
    public void WiderPsfSpreadsTheDiskWithoutChangingItsFlux()
    {
        var projection = Projection with { FocalLengthXPixels = 917, FocalLengthYPixels = 917 };
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, DateTimeOffset.UnixEpoch,
            new(90, 0), .25, 0, 1, 0, 149600000);
        var narrow = new SolarDiskRenderPlan(projection, [disk], 1000, new SolarDiskRenderSettings(1, 4));
        var wide = new SolarDiskRenderPlan(projection, [disk], 1000, new SolarDiskRenderSettings(2.5, 8));
        Assert.AreEqual(1000, narrow.RetainedElectronRate, 1e-9);
        Assert.AreEqual(1000, wide.RetainedElectronRate, 1e-9);
        Assert.IsTrue(wide.MaximumElectronRate < narrow.MaximumElectronRate);
        Assert.IsTrue(Lit(wide, 64, 64) > Lit(narrow, 64, 64));
    }

    [TestMethod]
    public void SensorAndApertureClipLoseLightWithoutRenormalizing()
    {
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, DateTimeOffset.UnixEpoch,
            new(90, 0), .25, 0, 1, 0, 149600000);
        var edge = new SolarDiskRenderPlan(Projection with { PrincipalPointX = 0 }, [disk], 1000);
        Assert.AreEqual(1000, edge.VisibleElectronRate, 1e-9, "The sensor clip happens after the PSF, not at sampling.");
        Assert.AreEqual(500, edge.RetainedElectronRate, 10);
        Assert.AreEqual(edge.RetainedElectronRate, Sum(edge, 64, 64), 1e-9);

        var fisheye = new ProjectionContext(ProjectionModel.EquidistantFisheye, 32, 32, 917, 917, 64, 64,
            ProjectionAperture.Circular, 20);
        // The disk centre sits on the image-circle boundary, 20 px (1.2496 degrees) from the zenith.
        var rim = new SolarDiskRenderPlan(fisheye, [disk with { Direction = new(90 - 20 / 917d * 180 / Math.PI, 0) }], 1000);
        Assert.AreEqual(1000, rim.VisibleElectronRate, 1e-9);
        Assert.IsTrue(rim.RetainedElectronRate is > 300 and < 700);
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                if (!fisheye.ContainsSample(x + .5, y + .5)) Assert.AreEqual(0, rim.ElectronRate(x, y));
    }

    [TestMethod]
    public void RefractedDiskFollowsTheProjectedFootprintAndFlattensNearTheHorizon()
    {
        var projection = new ProjectionContext(ProjectionModel.Perspective, 32, 32, 1000, 1000, 64, 64,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 0, BoresightAzimuthDegrees: 180);
        var refraction = new RefractionOptions(true);
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, DateTimeOffset.UnixEpoch,
            new(.6, 180), .25, 0, 1, 0, 149600000);
        var plain = new SolarDiskRenderPlan(projection, [disk], 1000);
        var refracted = new SolarDiskRenderPlan(projection, [disk], 1000, new SolarDiskRenderSettings(Refraction: refraction));
        Assert.AreEqual(1000, refracted.RetainedElectronRate, 1e-9);
        var (plainY, plainSpread) = VerticalMoments(plain);
        var (refractedY, refractedSpread) = VerticalMoments(refracted);
        Assert.IsTrue(plainY - refractedY > 5, "Refraction must lift the disk toward the zenith.");
        Assert.IsTrue(refractedSpread < plainSpread, "Refraction lifts the lower limb more than the upper limb.");

        var request = new VisibleSceneRequest(DateTimeOffset.UnixEpoch, new ObserverLocation(0, 0, 0), projection,
            new(7, 100), new("HYG", "fixture", new("https://astronexus.com/projects/hyg"), new string('A', 64), "CC BY-SA 4.0", "2"),
            refraction);
        var footprint = ResolvedFootprintSampler.Sample(request, "solar-system:Sun", "Sun", ResolvedFootprintSourceKind.SolarSystemBody,
            ResolvedFootprintExtent.Circle(.25, "fixture"), disk.Direction, null)!;
        Assert.AreEqual(footprint.CenterPixel!.Value.Y, refractedY, .5, "The raster and the footprint share one projection path.");
        var padding = refracted.Settings.PsfRadiusPixels + 1;
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                if (refracted.ElectronRate(x, y) > 0)
                    Assert.IsTrue(x + .5 >= footprint.Bounds.MinX - padding && x + .5 <= footprint.Bounds.MaxX + padding &&
                        y + .5 >= footprint.Bounds.MinY - padding && y + .5 <= footprint.Bounds.MaxY + padding,
                        $"Disk light at ({x},{y}) lies outside the padded footprint.");
    }

    [TestMethod]
    public void InvalidRenderSettingsAreRejected()
    {
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, DateTimeOffset.UnixEpoch,
            new(90, 0), .25, 0, 1, 0, 149600000);
        foreach (var settings in new SolarDiskRenderSettings[]
        {
            new(0), new(double.NaN), new(1, 0), new(1, 65), new(HorizonPolicy: (HorizonPolicy)99),
            new(Refraction: new RefractionOptions(true, double.NaN))
        })
            Assert.Throws<ArgumentOutOfRangeException>(() => new SolarDiskRenderPlan(Projection, [disk], 1000, settings));
    }

    private static double Sum(SolarDiskRenderPlan plan, int width, int height) =>
        Enumerable.Range(0, width * height).Sum(i => plan.ElectronRate(i % width, i / width));

    private static int Lit(SolarDiskRenderPlan plan, int width, int height) =>
        Enumerable.Range(0, width * height).Count(i => plan.ElectronRate(i % width, i / width) > 0);

    private static (double Mean, double Deviation) VerticalMoments(SolarDiskRenderPlan plan)
    {
        double total = 0, first = 0, second = 0;
        for (var y = 0; y < plan.Projection.HeightPixels; y++)
            for (var x = 0; x < plan.Projection.WidthPixels; x++)
            {
                var rate = plan.ElectronRate(x, y);
                total += rate;
                first += (y + .5) * rate;
                second += (y + .5) * (y + .5) * rate;
            }
        var mean = first / total;
        return (mean, Math.Sqrt(second / total - mean * mean));
    }
}
