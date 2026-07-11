using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
public sealed class AnnotationRendererTests
{
    [TestMethod]
    public void AnnotateMono8_DrawsProjectedPointWithoutChangingSource()
    {
        var source = new byte[9];
        var projector = ProjectorFactory.CreatePerspective(new PerspectiveProjectionContext(1, 1, 1, 1, 3, 3));

        var annotated = AnnotationRenderer.AnnotateMono8(source, 3, 3, projector, [new AltAzPoint(90, 0)]);

        Assert.AreEqual(0, source[4]);
        Assert.AreEqual(byte.MaxValue, annotated[4]);
    }
}
