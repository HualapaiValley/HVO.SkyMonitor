using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using System.Text.Json;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientTrailPresentationTests
{
    [TestMethod]
    [DataRow("single-point")]
    [DataRow("duplicate")]
    [DataRow("outside")]
    public void RejectsIncompleteDegenerateAndOutOfBoundsGeometry(string scenario)
    {
        var source = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var input = TransientDetectorInputFactory.Create(source.Artifact, source.Source, source.Levels).Input!;
        TransientPointV1[] points = scenario switch
        {
            "single-point" => [new(0.5, 0.5)],
            "duplicate" => [new(0.5, 0.5), new(0.5, 0.5)],
            _ => [new(0.5, 0.5), new(input.Descriptor.Layout.Width + 1, 1)]
        };
        var geometry = new TransientGeometryV1(source.Source.EvidenceId, input.Descriptor.Layout.Width,
            input.Descriptor.Layout.Height, new(0, 0, 2, 2), points);
        Assert.ThrowsExactly<ArgumentException>(() => TransientTrailPresentation.CreatePayload(new string('A', 64),
            source.Artifact.Layout!.Width, source.Artifact.Layout.Height, [new(geometry, input.Descriptor, false)]));
    }

    [TestMethod]
    public void PixelMappingRequiresKnownProducerRoleVersionAndEveryAlgorithm()
    {
        var recipe = RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.EncodedPreview,
            "1.0.0", "encoded-preview-v1", JsonSerializer.SerializeToElement(new { }));
        ProcessingAlgorithmIdentity[] algorithms = [new("display-stretch", Mono16DisplayStretch.AlgorithmVersion)];
        Assert.IsTrue(TransientTrailPresentation.PreservesSourcePixels(FrameArtifactRole.Preview, recipe, algorithms));
        Assert.IsFalse(TransientTrailPresentation.PreservesSourcePixels(FrameArtifactRole.Preview,
            recipe with { ImplementationVersion = "encoded-preview-v2" }, algorithms));
        Assert.IsFalse(TransientTrailPresentation.PreservesSourcePixels(FrameArtifactRole.Combined, recipe, algorithms));
        Assert.IsFalse(TransientTrailPresentation.PreservesSourcePixels(FrameArtifactRole.Preview, recipe,
            [.. algorithms, new("downsample", "some-transform-v1")]));
        Assert.IsFalse(TransientTrailPresentation.PreservesSourcePixels(FrameArtifactRole.Preview, recipe,
            [new("display-stretch", "unknown-version")]));
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public void UsesAuthenticatedSourceCoordinatesAndNeverDrawsAnUncertaintyBand(CameraPixelFormat format)
    {
        var source = TransientTestData.CreateDetectorSource(format);
        var input = TransientDetectorInputFactory.Create(source.Artifact, source.Source, source.Levels).Input!;
        var geometry = new TransientGeometryV1(source.Source.EvidenceId,
            input.Descriptor.Layout.Width, input.Descriptor.Layout.Height,
            new(0, 0, input.Descriptor.Layout.Width, input.Descriptor.Layout.Height), [new(0.25, 0.25), new(0.75, 0.75)]);

        var payload = TransientTrailPresentation.CreatePayload(new string('A', 64),
            source.Artifact.Layout!.Width, source.Artifact.Layout.Height,
            [new(geometry, input.Descriptor, false)]);

        var scale = format == CameraPixelFormat.Mono16 ? 1 : 2;
        Assert.AreEqual(0.25 * scale, payload.Segments.Single().From.X);
        Assert.AreEqual(0.75 * scale, payload.Segments.Single().To.X);
        Assert.AreEqual(2, payload.Segments.Single().Thickness);
        Assert.IsNotNull(payload.Segments.Single().Stroke);
        Assert.AreEqual(0, payload.Ellipses.Count);
        Assert.AreEqual(0, payload.Markers.Count);
        Assert.AreEqual(TransientTrailPresentation.PositionalUncertainty, payload.TextBlocks.Single().Lines.Single());
    }

    [TestMethod]
    public void RejectsUnrelatedEvidenceEvenWhenTheRasterDimensionsMatch()
    {
        var source = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var input = TransientDetectorInputFactory.Create(source.Artifact, source.Source, source.Levels).Input!;
        var geometry = new TransientGeometryV1(Guid.NewGuid(), input.Descriptor.Layout.Width,
            input.Descriptor.Layout.Height, new(0, 0, 2, 2), [new(0.5, 0.5), new(1.5, 1.5)]);

        Assert.ThrowsExactly<ArgumentException>(() => TransientTrailPresentation.CreatePayload(new string('A', 64),
            source.Artifact.Layout!.Width, source.Artifact.Layout.Height,
            [new(geometry, input.Descriptor, true)]));
    }

    [TestMethod]
    public void ConfirmedAndProvisionalStylesAreBoundToDifferentCanonicalPayloads()
    {
        var source = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var input = TransientDetectorInputFactory.Create(source.Artifact, source.Source, source.Levels).Input!;
        var geometry = new TransientGeometryV1(source.Source.EvidenceId, input.Descriptor.Layout.Width,
            input.Descriptor.Layout.Height, new(0, 0, 2, 2), [new(0.5, 0.5), new(1.5, 1.5)]);
        var provisional = TransientTrailPresentation.CreatePayload(new string('A', 64),
            source.Artifact.Layout!.Width, source.Artifact.Layout.Height, [new(geometry, input.Descriptor, false)]);
        var confirmed = TransientTrailPresentation.CreatePayload(new string('A', 64),
            source.Artifact.Layout.Width, source.Artifact.Layout.Height, [new(geometry, input.Descriptor, true)]);

        Assert.AreNotEqual(provisional.ContentIdentitySha256, confirmed.ContentIdentitySha256);
        Assert.IsNull(confirmed.Segments.Single().Stroke);
        Assert.AreEqual(provisional.Segments.Single().From, confirmed.Segments.Single().From);
        Assert.AreEqual(provisional.Segments.Single().To, confirmed.Segments.Single().To);
    }
}
