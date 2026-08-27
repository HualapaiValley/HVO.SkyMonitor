using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentLayeredPresentationTests
{
    private static readonly PresentationCompatibilityDescriptor Compatibility = new(
        640, 480, new string('A', 64), new string('B', 64));

    [TestMethod]
    public void RenderProducesDeterministicSanitizedGroupedSvg()
    {
        var payload = PresentationLayerPayloadJson.Create(
            new string('C', 64), 640, 480,
            markers: [new(new(10, 20), 4, new(255, 64, 32)), new(new(12.4, 22.5), 0, new(1, 2, 3))],
            segments: [new(new(1, 2), new(3, 4), 2, new(10, 20, 30))],
            ellipses: [new(new(100, 120), 20, 30, new(40, 50, 60))],
            textBlocks: [new(PresentationTextAnchor.TopLeft, new(0, 0), ["<script>alert(1)</script>"], 1, 4, 1, new(255, 255, 255))]);
        var layer = LayeredPresentationJson.CreateLayer(
            "scene-annotation",
            new(Guid.Parse("00000000-0000-0000-0000-000000000002"), payload.ContentIdentitySha256,
                PresentationLayerPayloadJson.MediaType, Compatibility),
            new string('C', 64), PresentationCoordinateSpace.ScenePixels,
            "renderer-v1", "style-v1", 10, PresentationBlendMode.Screen, 750_000, true,
            JsonSerializer.SerializeToElement(new { }));
        var manifest = LayeredPresentationJson.CreateManifest(
            new(Guid.Parse("00000000-0000-0000-0000-000000000001"), new string('D', 64),
                "application/x-hvo-packed-image", Compatibility),
            new string('C', 64), [layer]);

        var first = CameraAgentLayeredPresentationService.Render(
            Guid.Parse("00000000-0000-0000-0000-000000000003"), manifest, [payload], new string('E', 64));
        var second = CameraAgentLayeredPresentationService.Render(
            Guid.Parse("00000000-0000-0000-0000-000000000003"), manifest, [payload], new string('E', 64));

        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, first.Status);
        Assert.IsNotNull(first.Presentation);
        Assert.IsNotNull(second.Presentation);
        CollectionAssert.AreEqual(first.Presentation.Svg.ToArray(), second.Presentation.Svg.ToArray());
        Assert.AreEqual(first.Presentation.SvgChecksumSha256, second.Presentation.SvgChecksumSha256);
        var text = Encoding.UTF8.GetString(first.Presentation.Svg.Span);
        Assert.IsFalse(text.Contains("<script", StringComparison.OrdinalIgnoreCase));
        var document = XDocument.Parse(text);
        XNamespace svg = "http://www.w3.org/2000/svg";
        var group = AssertSingle(document.Root!.Elements(svg + "g"));
        Assert.AreEqual("hvo-layer-0", group.Attribute("id")?.Value);
        Assert.AreEqual(layer.LayerIdentitySha256, group.Attribute("data-layer-identity")?.Value);
        Assert.AreEqual("mix-blend-mode:screen", group.Attribute("style")?.Value);
        var pixel = AssertSingle(group.Elements(svg + "rect"));
        Assert.AreEqual("12", pixel.Attribute("x")?.Value);
        Assert.AreEqual("23", pixel.Attribute("y")?.Value);
        Assert.AreEqual("#010203", pixel.Attribute("fill")?.Value);
        Assert.AreEqual("<script>alert(1)</script>", AssertSingle(group.Elements(svg + "text")).Value);
        Assert.IsEmpty(document.Descendants(svg + "script"));
    }

    [TestMethod]
    public void RenderRejectsPresentationOverElementBound()
    {
        var segment = new PresentationSegmentV1(new(1, 1), new(2, 2), 1, new(255, 255, 255));
        var payload = new PresentationLayerPayloadV1(
            PresentationLayerPayloadV1.CurrentSchemaVersion, new string('C', 64), new string('D', 64),
            640, 480, [], Enumerable.Repeat(segment, CameraAgentLayeredPresentationService.MaximumSvgElements + 1).ToArray(),
            [], [], null);
        var layer = LayeredPresentationJson.CreateLayer(
            "dense",
            new(Guid.Parse("00000000-0000-0000-0000-000000000002"), new string('C', 64),
                PresentationLayerPayloadJson.MediaType, Compatibility),
            null, PresentationCoordinateSpace.ScenePixels, "renderer-v1", "style-v1", 1,
            PresentationBlendMode.Normal, 1_000_000, true, JsonSerializer.SerializeToElement(new { }));
        var manifest = LayeredPresentationJson.CreateManifest(
            new(Guid.Parse("00000000-0000-0000-0000-000000000001"), new string('E', 64),
                "application/x-hvo-packed-image", Compatibility), null, [layer]);

        var result = CameraAgentLayeredPresentationService.Render(Guid.NewGuid(), manifest, [payload], new string('F', 64));

        Assert.AreEqual(CameraAgentLayeredPresentationStatus.TooLarge, result.Status);
        Assert.IsNull(result.Presentation);
    }

    [TestMethod]
    public void SharedRendererRejectsUnsupportedNormalizedCoordinates()
    {
        var payload = PresentationLayerPayloadJson.Create(new string('C', 64), 640, 480);
        var layer = LayeredPresentationJson.CreateLayer(
            "normalized",
            new(Guid.NewGuid(), payload.ContentIdentitySha256,
                PresentationLayerPayloadJson.MediaType, Compatibility),
            null,
            PresentationCoordinateSpace.NormalizedImage,
            "renderer-v1",
            "style-v1",
            1,
            PresentationBlendMode.Normal,
            1_000_000,
            true,
            JsonSerializer.SerializeToElement(new { }));
        var manifest = LayeredPresentationJson.CreateManifest(
            new(Guid.NewGuid(), new string('D', 64), "application/x-hvo-packed-image", Compatibility),
            null,
            [layer]);

        Assert.Throws<ArgumentException>(() =>
            GroupedSvgPresentationRenderer.Render(manifest, [payload], new string('E', 64)));
    }

    private static T AssertSingle<T>(IEnumerable<T> values)
    {
        var materialized = values.ToArray();
        Assert.HasCount(1, materialized);
        return materialized[0];
    }
}
