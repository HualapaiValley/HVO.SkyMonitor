using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SyntheticCalibrationReferenceGeneratorTests
{
    [TestMethod]
    public void GenerateAndCorrect_ReducesDeterministicStructureAndRepairsDefect()
    {
        var model = new SyntheticCalibrationModelV1
        {
            Defects = [new SyntheticCalibrationDefect(2, 2)]
        };
        var idealValues = Enumerable.Repeat((ushort)1000, 25).ToArray();
        var ideal = new Linear16Frame(5, 5, 10, CameraPixelFormat.Mono16, Bytes(idealValues));
        var references = SyntheticCalibrationReferenceGenerator.Generate(5, 5, CameraPixelFormat.Mono16, model);
        var corrupted = SyntheticCalibrationReferenceGenerator.ApplyToLight(
            ideal, TimeSpan.FromSeconds(2), model);

        var corrected = Linear16ReferenceCalibration.Correct(
            new Linear16Frame(5, 5, 10, CameraPixelFormat.Mono16, corrupted),
            references.Bias,
            references.Dark,
            references.Flat,
            references.DefectMask,
            new(TimeSpan.FromSeconds(2), model.DarkExposure, model.FlatExposure, references.FlatNormalizationAdu));

        var rawResidual = MeanAbsoluteResidual(Values(corrupted.Span), idealValues);
        var correctedResidual = MeanAbsoluteResidual(Values(corrected.PixelData.Span), idealValues);
        Assert.IsLessThan(rawResidual / 10, correctedResidual);
        Assert.IsLessThanOrEqualTo(2d, correctedResidual);
        Assert.AreEqual(1000, Values(corrected.PixelData.Span)[12]);
    }

    [TestMethod]
    public void ApplyToLightWithStatistics_RecordsSyntheticClipping()
    {
        var model = new SyntheticCalibrationModelV1
        {
            BiasPedestalAdu = 100,
            PixelResponseVariationFraction = 0,
            VignettingStrength = 0,
            DarkCurrentAduPerSecond = 0
        };
        var ideal = new Linear16Frame(
            1, 1, 2, CameraPixelFormat.Mono16, Bytes([ushort.MaxValue]));

        var result = SyntheticCalibrationReferenceGenerator.ApplyToLightWithStatistics(
            ideal, TimeSpan.FromSeconds(1), model);

        Assert.AreEqual(1, result.Statistics.ActivePixelCount);
        Assert.AreEqual(0, result.Statistics.ClippedLow);
        Assert.AreEqual(1, result.Statistics.ClippedHigh);
        Assert.AreEqual(ushort.MaxValue, Values(result.PixelData.Span).Single());
    }

    private static double MeanAbsoluteResidual(ushort[] actual, ushort[] expected)
        => actual.Select((value, index) => Math.Abs((double)value - expected[index])).Average();

    private static byte[] Bytes(ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            bytes[index * 2] = (byte)values[index];
            bytes[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return bytes;
    }

    private static ushort[] Values(ReadOnlySpan<byte> bytes)
    {
        var values = new ushort[bytes.Length / 2];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)(bytes[index * 2] | bytes[index * 2 + 1] << 8);
        }
        return values;
    }
}
