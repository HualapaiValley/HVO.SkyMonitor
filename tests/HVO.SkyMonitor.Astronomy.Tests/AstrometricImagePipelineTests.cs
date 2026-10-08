namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricImagePipelineTests
{
    [TestMethod]
    [DataRow(20d, false, 185d)]
    [DataRow(60d, false, 185d)]
    [DataRow(60d, true, 185d)]
    [DataRow(60d, true, 170d)]
    public void IntegratedPixels_BlindWarmMappingProducesSharperRegisteredStack(double exposureSeconds, bool color, double fieldDegrees)
    {
        var catalog = AstrometricTestFixture.Catalog(); var initialTruth = AstrometricTestFixture.Truth(fieldDegrees);
        var truth = initialTruth with { PrincipalPointX = 320, PrincipalPointY = 320, WidthPixels = 640, HeightPixels = 640, ImageCircleRadiusPixels = 290, FocalLengthXPixels = initialTruth.FocalLengthXPixels * 1.25, FocalLengthYPixels = initialTruth.FocalLengthYPixels * 1.25 };
        var calibration = AstrometricTestFixture.Calibration(truth);
        var images = new List<AstrometricTestFixture.Image>(); var fits = new List<AstrometricSolveResult>(); var maps = new List<AstrometricMapping>();
        for (var i = 0; i < 3; i++)
        {
            var midpoint = AstrometricTestFixture.Utc.AddSeconds(i * 180); var image = AstrometricTestFixture.Render(catalog, truth, midpoint, exposureSeconds, color, 773 + i);
            var detected = AstrometricTestFixture.Detect(image, truth.WidthPixels, color); var frame = AstrometricTestFixture.Frame(midpoint, exposureSeconds);
            var fit = i == 0 ? AstrometricSolver.Solve(frame, calibration, catalog, detected) : AstrometricSolver.Refine(frame, calibration, catalog, detected, fits[^1].Assessment);
            Assert.IsTrue(fit.Assessment.HasMeasuredMapping, fit.Assessment.Reason);
            Assert.IsLessThan(.06, AstrometricTestFixture.RotationError(AstrometricMapping.Projection(calibration, fit.Assessment), truth));
            Assert.AreEqual(1.037, fit.Assessment.Parameters!.FocalScale, .001);
            if (i > 0) Assert.AreEqual(0, fit.Metrics.Hypotheses);
            Assert.AreEqual(fit.Associations.Count, fit.Associations.Select(a => a.DetectionIndex).Distinct().Count());
            var truthProjector = ProjectorFactory.Create(truth);
            foreach (var association in fit.Associations)
            {
                var star = catalog.Stars.Single(s => s.Id == association.CatalogId); var measured = detected.Single(d => d.Index == association.DetectionIndex);
                Assert.IsLessThan(.5, AstrometricTestFixture.Distance(measured.Pixel, truthProjector.Project(AstrometricTestFixture.Horizontal(star, midpoint))!.Value));
            }
            images.Add(image); fits.Add(fit); maps.Add(new(calibration, fit.Assessment));
        }
        // The registration map comes exclusively from accepted measured fits, never renderer truth.
        // Scientific PSF evaluation uses ideal companions to keep finite read noise from biasing moments.
        var size = truth.WidthPixels; var aligned = new double[size * size]; var raw = new double[aligned.Length]; var counts = new int[aligned.Length];
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var k = y * size + x;
            for (var i = 0; i < images.Count; i++)
            {
                raw[k] += images[i].Ideal[k] / images.Count;
                if (maps[1].MapPixelTo(new(x + .5, y + .5), maps[i]) is not { } source) continue;
                var sample = Sample(images[i].Ideal, images[i].Mask, size, source);
                if (sample is { } value) { aligned[k] += value; counts[k]++; }
            }
            if (counts[k] > 0) aligned[k] /= counts[k];
        }
        var referenceProjector = ProjectorFactory.Create(truth);
        var selected = catalog.Stars.Select(s => (Star: s, H: AstrometricTestFixture.Horizontal(s, fits[1].Assessment.Frame.MidpointUtc)))
            .Where(s => s.H.AltitudeDegrees > 25).Select(s => (s.Star, Pixel: referenceProjector.Project(s.H)))
            .Where(s => s.Pixel is { } p && p.X > 15 && p.Y > 15 && p.X < size - 15 && p.Y < size - 15).ToArray();
        var isolated = selected.Where(s => selected.All(t => t.Star.Id == s.Star.Id || AstrometricTestFixture.Distance(s.Pixel!.Value, t.Pixel!.Value) > 25)).Take(30).ToArray();
        Assert.IsGreaterThanOrEqualTo(8, isolated.Length);
        var rawWidth = isolated.Average(s => MomentWidth(raw, size, s.Pixel!.Value));
        var alignedWidth = isolated.Average(s => MomentWidth(aligned, size, s.Pixel!.Value));
        Assert.IsLessThan(rawWidth * .95, alignedWidth, $"Registered equivalent PSF {alignedWidth:F3}px, raw {rawWidth:F3}px");
        Assert.IsTrue(counts.Any(c => c == 0)); Assert.IsTrue(counts.Any(c => c == 3));
        Assert.IsTrue(aligned.Where((_, i) => counts[i] == 0).All(value => value == 0));
    }
    private static double? Sample(double[] values, bool[] mask, int size, PixelPoint point)
    {
        var x = point.X - .5; var y = point.Y - .5; var ix = (int)Math.Floor(x); var iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix + 1 >= size || iy + 1 >= size) return null;
        var k = iy * size + ix; if (!mask[k] || !mask[k + 1] || !mask[k + size] || !mask[k + size + 1]) return null;
        var dx = x - ix; var dy = y - iy;
        return values[k] * (1 - dx) * (1 - dy) + values[k + 1] * dx * (1 - dy) + values[k + size] * (1 - dx) * dy + values[k + size + 1] * dx * dy;
    }
    private static double MomentWidth(double[] pixels, int size, PixelPoint center)
    {
        double sum = 0, sx = 0, sy = 0, square = 0;
        for (var y = (int)center.Y - 8; y <= (int)center.Y + 8; y++) for (var x = (int)center.X - 8; x <= (int)center.X + 8; x++)
        {
            var dx = x + .5 - center.X; var dy = y + .5 - center.Y; if (dx * dx + dy * dy > 64) continue;
            var w = Math.Max(0, pixels[y * size + x] - 300); sum += w; sx += w * dx; sy += w * dy; square += w * (dx * dx + dy * dy);
        }
        return 2.354820045 * Math.Sqrt(Math.Max(0, (square / sum - Math.Pow(sx / sum, 2) - Math.Pow(sy / sum, 2)) / 2));
    }
}
