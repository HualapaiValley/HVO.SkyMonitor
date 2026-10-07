using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// Annotation work retained before resolved footprints must retry under the identity it was first planned with, or
/// the retry would publish a second annotated preview for the same capture.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class AnnotationIdentityUpgradeTests
{
    // Computed by development/v1 b13f0d0e for the inputs below. A footprint-free annotation keeps this exact identity.
    private const string V3IdentitySha256 = "58B46537EBA195521B295A4FF032C10C9936E8CA3DB26EC9ABFE9E7DF71F10BA";

    [TestMethod]
    public void FootprintFreeAnnotationKeepsTheReleasedIdentity()
    {
        var identity = BuiltInProcessingRecipes.CreateExecutionIdentity(
            BuiltInProcessingRecipes.Annotation, JsonSerializer.SerializeToElement(new { }), Selector, Annotation);

        Assert.AreEqual(V3IdentitySha256, identity.IdentitySha256);
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
            Assert.AreNotEqual(V3IdentitySha256, BuiltInProcessingRecipes.CreateExecutionIdentity(
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
