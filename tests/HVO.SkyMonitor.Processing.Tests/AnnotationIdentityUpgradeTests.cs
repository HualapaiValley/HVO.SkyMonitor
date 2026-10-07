using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// Annotation output identities change exactly when the drawn bytes can change. Resolved footprints (#518) took a new
/// identity only for footprint inputs; the measured-association label policy (#526) gates every star label, so the
/// Annotation Definition moved to projected-annotation-v4 and every identity moved with it. Otherwise a retry of
/// retained v3 work would write differently labelled bytes under the committed v3 output identity.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class AnnotationIdentityUpgradeTests
{
    // Computed by development/v1 b13f0d0e (projected-annotation-v3) for the inputs below.
    private const string V3IdentitySha256 = "58B46537EBA195521B295A4FF032C10C9936E8CA3DB26EC9ABFE9E7DF71F10BA";

    // Computed under projected-annotation-v4 (#526) for the same inputs.
    private const string V4IdentitySha256 = "6D7AC733C270ACABF7162B38B2C1C24991135DEF9E0F98D3E9335220C2E9538F";

    [TestMethod]
    public void FootprintFreeAnnotationTakesTheV4LabelPolicyIdentity()
    {
        var identity = BuiltInProcessingRecipes.CreateExecutionIdentity(
            BuiltInProcessingRecipes.Annotation, JsonSerializer.SerializeToElement(new { }), Selector, Annotation);

        Assert.AreEqual("projected-annotation-v4", identity.Descriptor.ImplementationVersion);
        Assert.AreEqual(V4IdentitySha256, identity.IdentitySha256);
        Assert.AreNotEqual(V3IdentitySha256, identity.IdentitySha256);
    }

    [TestMethod]
    public void ResolvedFootprintInputsTakeANewIdentityAndAlgorithmVersion()
    {
        var footprint = Annotation with
        {
            Objects = [Annotation.Objects[0] with { FootprintParts = [new ResolvedFootprintPart(true,
                [new PixelPoint(30, 20), new PixelPoint(40, 30), new PixelPoint(30, 40)])] }]
        };
        var ellipse = Annotation with { ProjectionOverlay = Annotation.ProjectionOverlay! with { ImageCircleRadiusY = 40 } };

        Assert.AreEqual(AnnotationRenderer.PointAnnotationAlgorithmVersion,
            AnnotationRenderer.AlgorithmVersionFor(Annotation.Objects, Annotation.ProjectionOverlay));
        foreach (var changed in new[] { footprint, ellipse })
        {
            Assert.AreEqual(AnnotationRenderer.AlgorithmVersion,
                AnnotationRenderer.AlgorithmVersionFor(changed.Objects, changed.ProjectionOverlay));
            Assert.AreNotEqual(V4IdentitySha256, BuiltInProcessingRecipes.CreateExecutionIdentity(
                BuiltInProcessingRecipes.Annotation, JsonSerializer.SerializeToElement(new { }), Selector, changed)
                .IdentitySha256);
        }
    }

    private static readonly ProcessingInputSelector Selector =
        ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, "preview", new string('B', 64));

    private static readonly ProcessingAnnotationInput Annotation = new(
        [new ProjectedAnnotationObject("planet:moon", "Moon", new PixelPoint(35, 30)),
            new ProjectedAnnotationObject("star:1", "STAR", new PixelPoint(12.5, 8), DrawLabel: false)],
        [new ProjectedAnnotationSegment("ORI", new PixelPoint(0, 0), new PixelPoint(7, 7))],
        new PreviewTransform(0.5, 0.75, 3, -2),
        new ProjectedAnnotationOverlay(new PixelPoint(48, 48), 47, new PixelPoint(48, 1), new PixelPoint(1, 48),
            new PixelPoint(48, 95), new PixelPoint(95, 48)),
        new string('A', 64),
        new MetadataCornerOverlay(["HVO"], [], [], ["2025-01-15 08:00Z"]));
}
