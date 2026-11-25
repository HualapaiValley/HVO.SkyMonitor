using System.Buffers.Binary;
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

    [TestMethod]
    [DataRow(BayerPattern.Rggb, 0, 0)]
    [DataRow(BayerPattern.Grbg, 1, 0)]
    [DataRow(BayerPattern.Gbrg, 0, 1)]
    [DataRow(BayerPattern.Bggr, 1, 1)]
    public void DemosaicToRgb24_PreservesAllDeliveredCfaPhases(BayerPattern pattern, int originX, int originY)
    {
        const int width = 6;
        const int height = 6;
        var raw = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = ((y + originY) & 1, (x + originX) & 1) switch
                {
                    (0, 0) => (ushort)3900,
                    (1, 1) => (ushort)900,
                    _ => (ushort)2100
                };
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan((y * width + x) * 2), value);
            }
        }

        var rgb = BayerRggb16Demosaicer.DemosaicToRgb24(
            width, height, raw,
            stretchOptions: new Mono16DisplayStretchOptions(0, 1, 0.01),
            pattern: pattern);

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var offset = (y * width + x) * 3;
                Assert.IsGreaterThan(rgb[offset + 1], rgb[offset], $"Expected R > G at ({x},{y}) for {pattern}.");
                Assert.IsGreaterThan(rgb[offset + 2], rgb[offset + 1], $"Expected G > B at ({x},{y}) for {pattern}.");
            }
        }
    }

    [TestMethod]
    public void DemosaicToRgb24_OddOriginBggrCropPreservesRedAndBlueTargetsAndMismatchedPhaseSwapsThem()
    {
        const int width = 160;
        const int height = 128;
        var raw = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var isRedSite = (x & 1) == 1 && (y & 1) == 1;
                var isBlueSite = (x & 1) == 0 && (y & 1) == 0;
                var inRedTarget = x is >= 22 and <= 32 && y is >= 17 and <= 27;
                var inBlueTarget = x is >= 121 and <= 131 && y is >= 35 and <= 45;
                var code = (inRedTarget && isRedSite) || (inBlueTarget && isBlueSite) ? (ushort)3800 : (ushort)90;
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan((y * width + x) * 2), code);
            }
        }

        var bggr = BayerRggb16Demosaicer.DemosaicToRgb24(width, height, raw, pattern: BayerPattern.Bggr);
        var redPatch = SumPatch(bggr, width, 27, 22, radius: 5);
        var bluePatch = SumPatch(bggr, width, 126, 40, radius: 5);
        Assert.IsGreaterThan(redPatch.Blue * 3, redPatch.Red, $"Odd-origin BGGR red target expected R >> B, got {redPatch}.");
        Assert.IsGreaterThan(bluePatch.Red * 3, bluePatch.Blue, $"Odd-origin BGGR blue target expected B >> R, got {bluePatch}.");

        // Prospective negative control: applying RGGB phase to an odd-origin (1,1) BGGR crop swaps R and B.
        var wrongPhase = BayerRggb16Demosaicer.DemosaicToRgb24(width, height, raw, pattern: BayerPattern.Rggb);
        var swappedRed = SumPatch(wrongPhase, width, 27, 22, radius: 5);
        var swappedBlue = SumPatch(wrongPhase, width, 126, 40, radius: 5);
        Assert.IsGreaterThan(swappedRed.Red * 3, swappedRed.Blue, $"Mismatched RGGB phase must invert red target, got {swappedRed}.");
        Assert.IsGreaterThan(swappedBlue.Blue * 3, swappedBlue.Red, $"Mismatched RGGB phase must invert blue target, got {swappedBlue}.");
    }

    [TestMethod]
    public void DemosaicToRgb24_SinglePixelAndInvalidPatternBehaveDeterministically()
    {
        var single = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(single, 1200);
        var rgb = BayerRggb16Demosaicer.DemosaicToRgb24(1, 1, single, pattern: BayerPattern.Bggr);
        Assert.AreEqual(3, rgb.Length);
        Assert.AreEqual(rgb[0], rgb[1]);
        Assert.AreEqual(rgb[1], rgb[2]);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BayerRggb16Demosaicer.DemosaicToRgb24(
            2, 2, new byte[8], pattern: (BayerPattern)99));
    }

    [TestMethod]
    public void DemosaicToRgb24_PreCanceledTokenThrows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => BayerRggb16Demosaicer.DemosaicToRgb24(
            1, 1, new byte[2], cancellation.Token));
    }

    private static (long Red, long Green, long Blue) SumPatch(byte[] rgb, int width, int centerX, int centerY, int radius)
    {
        long red = 0, green = 0, blue = 0;
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                var offset = (y * width + x) * 3;
                red += rgb[offset];
                green += rgb[offset + 1];
                blue += rgb[offset + 2];
            }
        }
        return (red, green, blue);
    }
}
