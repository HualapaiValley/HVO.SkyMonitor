using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AnnotationRendererFootprintTests
{
    private const int Size = 160;
    private static readonly AnnotationOptions Options = new() { MarkRadius = 6, DrawLabels = true };

    [TestMethod]
    public void ResolvedDiscIsFramedByItsPaddedOutlineAndLabelledBeyondIt()
    {
        var result = Render(Circle(20));

        // Outline vertices sit on the padded radius; the limb itself and the point mark stay clear.
        Assert.IsTrue(Near(result, 80 + 24, 60, Options.MarkerValue), "The outline must frame the padded limb.");
        Assert.IsTrue(Near(result, 80, 60 - 24, Options.MarkerValue));
        Assert.AreEqual(0, result[60 * Size + 80 + 20], "The outline must not cover the limb.");
        Assert.AreEqual(0, result[60 * Size + 80 + Options.MarkRadius], "A resolved disc drops the dotted point mark.");
        // The label starts past the padded outline rather than at the point-mark radius.
        var labelColumns = LabelColumns(result);
        Assert.IsNotEmpty(labelColumns);
        Assert.IsTrue(labelColumns.Min() >= 80 + 24 + 2, $"Label began at x={labelColumns.Min()}.");
    }

    [TestMethod]
    public void FootprintWithinThePointMarkKeepsTheReleasedRendering()
    {
        // A padded half-extent of 5 px does not exceed the 6 px mark, so the v3 dotted mark and label are unchanged.
        CollectionAssert.AreEqual(Render(null), Render(Circle(1)));
    }

    [TestMethod]
    public void ClippedOpenOutlineIsDrawnWithoutClosingAcrossTheFrame()
    {
        // The upper half of the limb, as left by a frame edge through the centre.
        var arc = Enumerable.Range(32, 33).Select(static index => new PixelPoint(
            80 + 20 * Math.Cos(index * Math.PI / 32), 60 + 20 * Math.Sin(index * Math.PI / 32))).ToArray();
        var result = Render([new ResolvedFootprintPart(false, arc)]);
        Assert.IsTrue(Near(result, 80, 60 - 24, Options.MarkerValue));
        // The open part's endpoints are 48 px apart on y = 60; a closing chord would light that row between them.
        for (var x = 64; x <= 96; x++)
            Assert.AreNotEqual(Options.MarkerValue, result[60 * Size + x], $"Unexpected chord at x={x}.");
    }

    private static byte[] Render(IReadOnlyList<ResolvedFootprintPart>? parts) =>
        AnnotationRenderer.AnnotateMono8(new byte[Size * Size], Size, Size,
            [new ProjectedAnnotationObject("solar-system:Sun", "Sun", new PixelPoint(80, 60), FootprintParts: parts)],
            new PreviewTransform(1, 1), Options).Pixels.ToArray();

    private static ResolvedFootprintPart[] Circle(double radius) =>
    [
        new(true, Enumerable.Range(0, 64).Select(index => new PixelPoint(
            80 + radius * Math.Cos(index * Math.PI / 32), 60 + radius * Math.Sin(index * Math.PI / 32))).ToArray())
    ];

    private static bool Near(byte[] pixels, int x, int y, byte value)
    {
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (pixels[(y + dy) * Size + x + dx] == value) return true;
        return false;
    }

    private static int[] LabelColumns(byte[] pixels) =>
        Enumerable.Range(0, Size * Size).Where(index => pixels[index] == Options.MarkValue)
            .Select(static index => index % Size).Distinct().ToArray();
}
