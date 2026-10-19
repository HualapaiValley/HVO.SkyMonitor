using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientDetectorInputMetadataTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public void RestoresExactProducerDescriptorWithoutImagePixels(CameraPixelFormat format)
    {
        var source = TransientTestData.CreateDetectorSource(format);
        var produced = TransientDetectorInputFactory.Create(source.Artifact, source.Source, source.Levels).Input!;

        var restored = TransientDetectorInputMetadata.Restore(
            source.Source, source.Artifact.Layout!, source.Levels, source.Artifact.Compatibility,
            produced.Descriptor.SaturationMaskChecksumSha256, produced.Descriptor.InputIdentitySha256);

        Assert.IsNotNull(restored);
        Assert.AreEqual(produced.Descriptor, restored);
        Assert.AreEqual(format == CameraPixelFormat.Mono16 ? 1 : 0.5, restored.SourceToDetectorTransform.ScaleX);
        Assert.AreEqual(format == CameraPixelFormat.Mono16 ? 1 : 0.5, restored.SourceToDetectorTransform.ScaleY);
    }

    [TestMethod]
    public void SameDimensionsCannotAuthorizeDifferentSourceTimingOrMask()
    {
        var source = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var produced = TransientDetectorInputFactory.Create(source.Artifact, source.Source, source.Levels).Input!;
        var descriptor = produced.Descriptor;

        Assert.IsNull(TransientDetectorInputMetadata.Restore(
            source.Source with { ObservationEndedUtc = source.Source.ObservationEndedUtc.AddTicks(1) },
            source.Artifact.Layout!, source.Levels, source.Artifact.Compatibility,
            descriptor.SaturationMaskChecksumSha256, descriptor.InputIdentitySha256));
        Assert.IsNull(TransientDetectorInputMetadata.Restore(
            source.Source, source.Artifact.Layout!, source.Levels, source.Artifact.Compatibility,
            new string('F', 64), descriptor.InputIdentitySha256));
        Assert.IsNull(TransientDetectorInputMetadata.Restore(
            source.Source, source.Artifact.Layout!, source.Levels,
            source.Artifact.Compatibility with { ProcessingProfile = "another-profile" },
            descriptor.SaturationMaskChecksumSha256, descriptor.InputIdentitySha256));
    }

    [TestMethod]
    public void RejectsUnsupportedFormatDespiteSameGeometry()
    {
        var source = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var produced = TransientDetectorInputFactory.Create(source.Artifact, source.Source, source.Levels).Input!;

        Assert.IsNull(TransientDetectorInputMetadata.Restore(
            source.Source, source.Artifact.Layout! with { PixelFormat = CameraPixelFormat.Mono8 },
            source.Levels, source.Artifact.Compatibility,
            produced.Descriptor.SaturationMaskChecksumSha256, produced.Descriptor.InputIdentitySha256));
    }
}
