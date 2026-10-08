using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AnnotationRendererGoldenTests
{
    // Captured from development/v1 b13f0d0e (projected-annotation-raster-v3). The Annotation recipe implementation
    // is unchanged, so inputs without resolved footprints must keep rendering these exact bytes.
    private const string ExpectedMono8Sha256 = "7C964BCB2C9787F85D2705B53D51EF9D413F13B008808D8A3F1E4EA865084F3C";
    private const string ExpectedRgb24Sha256 = "38D6C74D42244DD9D41ED209560A844A4070029F1150F0941F4221C047DE2745";

    private const int Width = 160;
    private const int Height = 120;

    [TestMethod]
    public void FootprintFreeInputsRenderTheV3Bytes()
    {
        var mono = new byte[Width * Height];
        for (var index = 0; index < mono.Length; index++) mono[index] = (byte)(index % Width / 4);
        var rgb = new byte[Width * Height * 3];
        for (var index = 0; index < rgb.Length; index++) rgb[index] = (byte)(index % 7 * 9);

        var mono8 = AnnotationRenderer.AnnotateMono8WithSegments(mono, Width, Height, Objects(), Segments(), Transform,
            Options, Overlay, Metadata);
        var rgb24 = AnnotationRenderer.AnnotateRgb24WithSegments(rgb, Width, Height, Objects(), Segments(), Transform,
            Options, Overlay, Metadata);

        Assert.AreEqual(ExpectedMono8Sha256, Convert.ToHexString(SHA256.HashData(mono8.Pixels.Span)));
        Assert.AreEqual(ExpectedRgb24Sha256, Convert.ToHexString(SHA256.HashData(rgb24.Pixels.Span)));
    }

    private static readonly PreviewTransform Transform = new(0.5, 0.75, 3, -2);

    private static readonly AnnotationOptions Options = new()
    {
        DrawImageCircle = true,
        DrawCardinalDirections = true,
        DrawLabels = true,
        ConstellationLineThickness = 2
    };

    // Anisotropic radius scaling exercises the image-circle path that now also accepts a per-axis radius.
    private static readonly ProjectedAnnotationOverlay Overlay = new(
        new PixelPoint(160, 80), 70, new PixelPoint(160, 10), new PixelPoint(90, 80),
        new PixelPoint(160, 150), new PixelPoint(230, 80));

    private static readonly MetadataCornerOverlay Metadata = new(["HVO"], ["2026-08-25"], [], ["V3"]);

    private static ProjectedAnnotationObject[] Objects() =>
    [
        new("solar-system:Sun", "Sun", new PixelPoint(120.4, 60.6)),
        new("solar-system:Moon", "Moon", new PixelPoint(200.2, 110.9)),
        new("hip-32349", "Sirius", new PixelPoint(150, 40)),
        new("hip-27989", "Betelgeuse", new PixelPoint(250, 130), DrawMark: false),
        new("hip-24436", "Rigel", new PixelPoint(310, 158), DrawLabel: false),
        new("hip-0", "Edge", new PixelPoint(-4, 3))
    ];

    private static ProjectedAnnotationSegment[] Segments() =>
    [
        new("ORI", new PixelPoint(250, 130), new PixelPoint(310, 158)),
        new("CMA", new PixelPoint(150, 40), new PixelPoint(400, -20))
    ];
}
