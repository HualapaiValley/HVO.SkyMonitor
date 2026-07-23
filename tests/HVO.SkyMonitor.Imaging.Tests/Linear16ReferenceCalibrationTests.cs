using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Linear16ReferenceCalibrationTests
{
    [TestMethod]
    public void Correct_RemovesBiasScaledDarkAndFlatStructureWithoutMutatingSources()
    {
        var lightValues = new ushort[]
        {
            370, 620, 1120,
            370, 65535, 1120,
            370, 620, 1120
        };
        var lightBytes = Bytes(lightValues);
        var original = lightBytes.ToArray();

        var result = Linear16ReferenceCalibration.Correct(
            Frame(3, 3, CameraPixelFormat.Mono16, lightBytes),
            Frame(3, 3, CameraPixelFormat.Mono16, Repeat(100, 9)),
            Frame(3, 3, CameraPixelFormat.Mono16, Repeat(200, 9)),
            Frame(3, 3, CameraPixelFormat.Mono16, Bytes([700, 1200, 2200, 700, 1200, 2200, 700, 1200, 2200])),
            Frame(3, 3, CameraPixelFormat.Mono16, Bytes([0, 0, 0, 0, 1, 0, 0, 0, 0])),
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), 1000));

        CollectionAssert.AreEqual(original, lightBytes);
        CollectionAssert.AreEqual(Repeat(500, 9), result.PixelData.ToArray());
        Assert.AreEqual(1, result.CorrectedDefectCount);
        Assert.AreEqual(Linear16ReferenceCalibration.AlgorithmVersion, result.AlgorithmVersion);
    }

    [TestMethod]
    public void Correct_BayerUsesSamePhotositeNeighborsAndClamps()
    {
        var values = Enumerable.Repeat((ushort)1120, 25).ToArray();
        values[12] = ushort.MaxValue;
        values[0] = ushort.MaxValue;
        var mask = new ushort[25];
        mask[12] = 1;

        var result = Linear16ReferenceCalibration.Correct(
            Frame(5, 5, CameraPixelFormat.BayerRggb16, Bytes(values)),
            Frame(5, 5, CameraPixelFormat.BayerRggb16, Repeat(100, 25)),
            Frame(5, 5, CameraPixelFormat.BayerRggb16, Repeat(200, 25)),
            Frame(5, 5, CameraPixelFormat.BayerRggb16, Repeat(2100, 25)),
            Frame(5, 5, CameraPixelFormat.BayerRggb16, Bytes(mask)),
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), ushort.MaxValue));

        var corrected = Values(result.PixelData.Span);
        Assert.AreEqual(ushort.MaxValue, corrected[0]);
        Assert.AreEqual(34492, corrected[12]);
    }

    [TestMethod]
    public void Correct_RejectsUnsupportedMismatchedAndInvalidFlatInputs()
    {
        var mono = Frame(2, 2, CameraPixelFormat.Mono16, Repeat(100, 4));
        var bayer = Frame(2, 2, CameraPixelFormat.BayerRggb16, Repeat(100, 4));
        var parameters = new Linear16CalibrationParameters(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1000);

        Assert.ThrowsExactly<ArgumentException>(() => Linear16ReferenceCalibration.Correct(
            mono, mono, bayer, mono, mono, parameters));
        Assert.ThrowsExactly<InvalidDataException>(() => Linear16ReferenceCalibration.Correct(
            mono,
            Frame(2, 2, CameraPixelFormat.Mono16, Repeat(100, 4)),
            Frame(2, 2, CameraPixelFormat.Mono16, Repeat(100, 4)),
            Frame(2, 2, CameraPixelFormat.Mono16, Repeat(100, 4)),
            Frame(2, 2, CameraPixelFormat.Mono16, Repeat(0, 4)),
            parameters));
    }

    [TestMethod]
    public void Correct_ReportsUnrepairableDefect()
    {
        var frame = Frame(2, 2, CameraPixelFormat.BayerRggb16, Repeat(100, 4));
        var mask = Frame(2, 2, CameraPixelFormat.BayerRggb16, Repeat(1, 4));

        Assert.ThrowsExactly<UnrepairableCalibrationDefectException>(() =>
            Linear16ReferenceCalibration.Correct(
                frame,
                Frame(2, 2, CameraPixelFormat.BayerRggb16, Repeat(0, 4)),
                Frame(2, 2, CameraPixelFormat.BayerRggb16, Repeat(0, 4)),
                Frame(2, 2, CameraPixelFormat.BayerRggb16, Repeat(1000, 4)),
                mask,
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1000)));
    }

    [TestMethod]
    public void Correct_ExtremeExposureScalingDoesNotOverflow()
    {
        var light = Frame(1, 1, CameraPixelFormat.Mono16, Repeat(ushort.MaxValue, 1));
        var bias = Frame(1, 1, CameraPixelFormat.Mono16, Repeat(0, 1));
        var dark = Frame(1, 1, CameraPixelFormat.Mono16, Repeat(1, 1));
        var flat = Frame(1, 1, CameraPixelFormat.Mono16, Repeat(1000, 1));
        var mask = Frame(1, 1, CameraPixelFormat.Mono16, Repeat(0, 1));

        var result = Linear16ReferenceCalibration.Correct(
            light, bias, dark, flat, mask,
            new(TimeSpan.MaxValue, TimeSpan.FromTicks(1), TimeSpan.FromTicks(1), 1000));

        Assert.AreEqual(0, Values(result.PixelData.Span).Single());
    }

    private static Linear16Frame Frame(int width, int height, CameraPixelFormat format, byte[] bytes)
        => new(width, height, width * 2, format, bytes);

    private static byte[] Repeat(ushort value, int count)
        => Bytes(Enumerable.Repeat(value, count));

    private static byte[] Bytes(IEnumerable<ushort> values)
    {
        var source = values.ToArray();
        var bytes = new byte[source.Length * 2];
        for (var index = 0; index < source.Length; index++)
        {
            bytes[index * 2] = (byte)source[index];
            bytes[index * 2 + 1] = (byte)(source[index] >> 8);
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
