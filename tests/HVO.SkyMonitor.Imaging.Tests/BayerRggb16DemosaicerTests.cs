using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class BayerRggb16DemosaicerTests
{
    [TestMethod]
    public void DemosaicToRgb24_UsesRggbPhaseAndDoesNotChangeRawSource()
    {
        const int width = 4;
        const int height = 4;
        var samples = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                samples[y * width + x] = (y & 1, x & 1) switch
                {
                    (0, 0) => 4000,
                    (1, 1) => 1000,
                    _ => 2000
                };
            }
        }
        var raw = samples.SelectMany(BitConverter.GetBytes).ToArray();
        var original = raw.ToArray();

        var rgb = BayerRggb16Demosaicer.DemosaicToRgb24(
            width, height, raw,
            stretchOptions: new Mono16DisplayStretchOptions(0, 1, 0.01));

        CollectionAssert.AreEqual(original, raw);
        Assert.AreEqual(width * height * 3, rgb.Length);
        var interiorRed = (2 * width + 2) * 3;
        Assert.IsGreaterThan(rgb[interiorRed + 1], rgb[interiorRed]);
        Assert.IsGreaterThan(rgb[interiorRed + 2], rgb[interiorRed + 1]);
    }
}
