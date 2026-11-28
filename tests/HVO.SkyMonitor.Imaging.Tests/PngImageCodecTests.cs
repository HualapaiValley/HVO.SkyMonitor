namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class PngImageCodecTests
{
    [TestMethod]
    public void StaticPngAcceptsAnimationMarkerInTextData()
    {
        // A static 1x1 PNG with "acTL" in the value of its tEXt chunk.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAADHRFWHRDb21tZW50AGFjVEzG7PoNAAAACklEQVR4nGNoAAAAggCBd81ytgAAAABJRU5ErkJggg==");

        Assert.IsTrue(PngImageCodec.IsDecodablePng(png, maximumPixels: 1));
        Assert.AreEqual(1, PngImageCodec.InspectPng(png).Width);
        Assert.AreEqual(1, PngImageCodec.DecodePng(png).Width);
    }

    [TestMethod]
    public void AnimatedPngIsRefusedByEveryStaticCodecEntryPoint()
    {
        // A two-frame APNG whose first frame is a decodable static PNG image.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACGFjVEwAAAACAAAAAPONk3AAAAAaZmNUTAAAAAAAAAABAAAAAQAAAAAAAAAAAAEACgAAWn8w0AAAAApJREFUeJxjYAAAAAIAAUivpHEAAAAaZmNUTAAAAAEAAAABAAAAAQAAAAAAAAAAAAEACgAAwQzaBAAAAA5mZEFUAAAAAnicY/gPAAEBAQDDJ73EAAAAAElFTkSuQmCC");

        Assert.IsFalse(PngImageCodec.IsDecodablePng(png, maximumPixels: 1));
        Assert.ThrowsExactly<ArgumentException>(() => PngImageCodec.InspectPng(png));
        Assert.ThrowsExactly<ArgumentException>(() => PngImageCodec.DecodePng(png));
    }
}
