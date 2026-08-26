using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;

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
