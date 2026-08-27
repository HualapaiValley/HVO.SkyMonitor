using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using SkiaSharp;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralPresentationGenerationGateTests
{
    [TestMethod]
    public void Decode_RejectsJpegPixelBoundBeforeDecode()
    {
        var jpeg = JpegImageCodec.EncodeMono8ToJpeg(2, 2, new byte[4]);
        var artifact = new CentralArtifact
        {
            MediaType = JpegImageCodec.MediaType,
            ByteLength = jpeg.LongLength
        };

        Assert.Throws<InvalidDataException>(() =>
            CentralPresentationBaseDecoder.Decode(artifact, jpeg, 3, CancellationToken.None));
    }

    [TestMethod]
    public void Decode_AcceptsPngForMaterialization()
    {
        var pixels = new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            255, 255, 255, 255
        };
        using var image = SKImage.FromPixelCopy(
            new SKImageInfo(2, 2, SKColorType.Rgb888x, SKAlphaType.Opaque), pixels, 8);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        var artifact = new CentralArtifact
        {
            MediaType = PngImageCodec.MediaType,
            ByteLength = png.LongLength
        };

        var decoded = CentralPresentationBaseDecoder.Decode(artifact, png, 4, CancellationToken.None);

        Assert.AreEqual(2, decoded.Layout.Width);
        Assert.AreEqual(2, decoded.Layout.Height);
        Assert.AreEqual(CameraPixelFormat.Rgb24, decoded.Layout.PixelFormat);
        Assert.AreEqual(PngImageCodec.AlgorithmVersion, decoded.DecoderVersion);
        CollectionAssert.AreEqual(new byte[]
        {
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            255, 255, 255
        }, decoded.PixelData.ToArray());
    }

    [TestMethod]
    public void Decode_RejectsTransparentPng()
    {
        var pixels = new byte[] { 255, 0, 0, 127 };
        using var image = SKImage.FromPixelCopy(
            new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul), pixels, 4);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        var artifact = new CentralArtifact
        {
            MediaType = PngImageCodec.MediaType,
            ByteLength = png.LongLength
        };

        Assert.Throws<ArgumentException>(() =>
            CentralPresentationBaseDecoder.Decode(artifact, png, 1, CancellationToken.None));
    }

    [TestMethod]
    public async Task TryEnterAsync_BoundsConcurrentAndWaitingGenerations()
    {
        var gate = new CentralPresentationGenerationGate();
        var active = new List<IDisposable>();
        for (var index = 0; index < CentralPresentationGenerationGate.MaximumConcurrent; index++)
        {
            active.Add((await gate.TryEnterAsync())!);
        }

        var waiting = Enumerable.Range(
                0,
                CentralPresentationGenerationGate.MaximumAdmitted -
                CentralPresentationGenerationGate.MaximumConcurrent)
            .Select(_ => gate.TryEnterAsync().AsTask())
            .ToArray();

        Assert.IsNull(await gate.TryEnterAsync());
        foreach (var lease in active)
        {
            lease.Dispose();
        }
        foreach (var waiter in waiting)
        {
            var lease = await waiter;
            Assert.IsNotNull(lease);
            lease.Dispose();
        }
    }
}
