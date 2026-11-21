using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class LinearRegisteredMeanTests
{
    [TestMethod]
    public void IndependentImpulseFootprints_DistinguishCentersDirectionsAndReflection()
    {
        const int size = 64;
        var count = size * size;
        var red = new double[count];
        var green = new double[count];
        var blue = new double[count];
        red[20 * size + 12] = 96;
        green[20 * size + 12] = 32;
        blue[20 * size + 12] = 16;
        var valid = Enumerable.Repeat(true, count).ToArray();
        var zero = new double[count];
        var source = new LinearRegistrationImage(size, size, valid, red, green, blue);
        var center = new LinearRegistrationImage(size, size, valid, zero, zero, zero);
        var rotation = new TestMap(p => new(64.5 - p.Y, p.X - 0.25), p => new(p.Y + 0.25, 64.5 - p.X));
        var translation = new TestMap(p => new(p.X - 1.25, p.Y + 2.5), p => new(p.X + 1.25, p.Y - 2.5));
        var reflection = new TestMap(p => new(64 - p.X, p.Y), p => new(64 - p.X, p.Y));

        Assert.AreEqual(new PixelPoint(44, 12.25), rotation.SourceToReference(new(12.5, 20.5)));
        Assert.AreEqual(new PixelPoint(11.25, 23), translation.SourceToReference(new(12.5, 20.5)));
        Dictionary<(int, int), ushort> expected103 = new() { [(43, 11)] = 6, [(44, 11)] = 6, [(43, 12)] = 18, [(44, 12)] = 18 };
        Dictionary<(int, int), ushort> expected104 = new() { [(10, 22)] = 6, [(11, 22)] = 18, [(10, 23)] = 6, [(11, 23)] = 18 };
        Assert.IsTrue(HasFootprint(Run(rotation), expected103));
        Assert.IsTrue(HasFootprint(Run(translation), expected104));
        Assert.IsFalse(HasFootprint(Run(new TestMap(rotation.Inverse, rotation.Forward)), expected103));
        Assert.IsFalse(HasFootprint(Run(reflection), expected103));
        var excluded = Run(null);
        Assert.IsFalse(HasFootprint(excluded, expected103));
        Assert.IsTrue(excluded.Support.Span.ToArray().All(bits => bits == 4));

        RegisteredLinearSamples Run(IImageRegistrationMap? map)
        {
            var mean = new LinearRegisteredMean(size, size, 3);
            mean.Add(2, center, null);
            if (map is not null)
            {
                mean.Add(4, source, map);
            }
            return mean.Finish();
        }

        static bool HasFootprint(RegisteredLinearSamples result, Dictionary<(int, int), ushort> expected)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var index = (y * size + x) * 3;
                    var r = expected.GetValueOrDefault((x, y));
                    if (result.Codes.Span[index] != r || result.Codes.Span[index + 1] != r / 3 ||
                        result.Codes.Span[index + 2] != r / 6)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    [TestMethod]
    public void PositiveTaps_RespectExactBorderAndDoNotDiluteMaskedPixels()
    {
        bool[] centerValid = [false, true, true, true, true, true];
        bool[] sourceValid = [false, true, true, true, true, true];
        var center = new LinearRegistrationImage(3, 2, centerValid, Enumerable.Repeat(20.0, 6).ToArray());
        var source = new LinearRegistrationImage(3, 2, sourceValid, Enumerable.Repeat(100.0, 6).ToArray());
        var exact = new LinearRegisteredMean(3, 2, 1);
        exact.Add(2, center, null);
        exact.Add(3, source, new TestMap(p => p, p => p));
        var atBorder = exact.Finish();
        CollectionAssert.AreEqual(new ushort[] { 0, 60, 60, 60, 60, 60 }, atBorder.Codes.ToArray());
        CollectionAssert.AreEqual(new byte[] { 0, 12, 12, 12, 12, 12 }, atBorder.Support.ToArray());

        var fractional = new LinearRegisteredMean(3, 2, 1);
        fractional.Add(2, center, null);
        fractional.Add(3, source, new TestMap(p => new(p.X - .25, p.Y), p => new(p.X + .25, p.Y)));
        var partial = fractional.Finish();
        CollectionAssert.AreEqual(new ushort[] { 0, 60, 0, 60, 60, 0 }, partial.Codes.ToArray());
        CollectionAssert.AreEqual(new byte[] { 0, 12, 4, 12, 12, 4 }, partial.Support.ToArray());
    }

    [TestMethod]
    public void OddOriginMosaic_IsReconstructedBeforeCombiningAndWarping()
    {
        const int size = 8;
        var mosaic = new double[size * size];
        var valid = Enumerable.Repeat(true, mosaic.Length).ToArray();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                mosaic[y * size + x] = ((x & 1), (y & 1)) switch
                {
                    (0, 0) => 32,
                    (1, 1) => 240,
                    _ => 96
                };
            }
        }
        valid[2 * size + 2] = false;
        var rgb = LinearBayerReconstruction.Reconstruct(mosaic, valid, size, size, BayerPattern.Bggr);
        var image = new LinearRegistrationImage(size, size, rgb.ValidMask, rgb.Red, rgb.Green, rgb.Blue);
        var mean = new LinearRegisteredMean(size, size, 3);
        mean.Add(2, image, null);
        mean.Add(4, image, new TestMap(p => new(p.X - .25, p.Y), p => new(p.X + .25, p.Y)));
        var result = mean.Finish();
        var supported = 4 * size + 4;
        CollectionAssert.AreEqual(new ushort[] { 240, 96, 32 }, result.Codes.Slice(supported * 3, 3).ToArray());
        Assert.AreEqual(20, result.Support.Span[supported]);
        Assert.AreEqual(0, result.Support.Span[2 * size + 2]);
        Assert.AreEqual(0, result.Support.Span[0]);
        Assert.AreEqual(4, result.Support.Span[4 * size + 6]);
    }

    private sealed record TestMap(Func<PixelPoint, PixelPoint> Forward, Func<PixelPoint, PixelPoint> Inverse) : IImageRegistrationMap
    {
        public PixelPoint? SourceToReference(PixelPoint source) => Forward(source);
        public PixelPoint? ReferenceToSource(PixelPoint reference) => Inverse(reference);
    }
}
