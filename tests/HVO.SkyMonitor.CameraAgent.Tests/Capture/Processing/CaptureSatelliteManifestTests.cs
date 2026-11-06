using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureSatelliteManifestTests
{
    [TestMethod]
    public async Task ExistingOverlayManifestAggregatesActualSatellitePayloadAndItsQueryStatus()
    {
        var f = new CaptureSatelliteFixture();
        var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, f.Descriptor, "raw.bin");
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("raw.bin", Path.Combine(Directory.GetCurrentDirectory(), "raw.bin"),
                f.Descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw), CaptureContractJson.ComputeManifestSha256(manifest));
        var submission = CaptureSatelliteIntegrationTests.Submission(f);
        submission = submission with
        {
            Result = submission.Result with
            {
                Artifacts = new FrameArtifactSet(
            new FrameArtifact(f.Descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, submission.Result.Frame!))
            }
        };
        var context = new CaptureProcessingContext(f.Configuration, submission, receipt);
        context.BeginNode("preview", [], ["$raw"]);
        await new PreviewCaptureProcessingStep(new("preview", "Preview", 0), new PreviewProcessingStepOptions(),
            new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor())).ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var source = f.Raw with
        {
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Metadata,
            Layout = null,
            Payload = "{}"u8.ToArray(),
            DescriptorIdentitySha256 = null,
            ContentIdentitySha256 = ProcessingIdentity.ComputePayloadSha256("{}"u8.ToArray())
        };
        var empty = PresentationLayerPayloadJson.Create(source.ContentIdentitySha256!, 640, 640);
        context.BeginNode("scene", []);
        foreach (var variant in new[] { "scene-annotation-layer-v1", "scene-cardinal-layer-v1", "scene-image-circle-layer-v1", "scene-constellation-layer-v1" })
            LayeredPresentationCaptureProcessing.Add(context, PresentationProcessingProducts.CreateLayerProduct(empty, variant, [source], "fixture-v1"));
        context.BeginNode("environment", []);
        LayeredPresentationCaptureProcessing.Add(context, PresentationProcessingProducts.CreateLayerProduct(empty, "environment-layer-v1", [source], "fixture-v1"));
        var numerical = await CaptureSatelliteProducts.PredictAsync(f.Evidence, f.Raw, "capture-satellites-v1", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(numerical);
        context.BeginNode("satellite", []);
        var products = CaptureSatelliteProducts.Create(f.Evidence, f.Raw, "capture-satellites-v1", numerical.Payload);
        LayeredPresentationCaptureProcessing.Add(context, products.ToArray());
        context.BeginNode("manifest", ["preview", "scene", "environment", "satellite"]);
        await new OverlayManifestCaptureProcessingStep(new("manifest", "OverlayManifest", 3), new OverlayManifestProcessingStepOptions
        {
            BasePreviewVariant = "default",
            SatelliteVariant = "capture-satellites-v1-layer",
            SatelliteStatusVariant = "capture-satellites-v1-status"
        }).ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var result = LayeredPresentationJson.ParseManifest(context.ProcessingProducts[^1].Payload).Document!;
        var satellite = result.Layers.Single(static l => l.LayerKind == OrbitalTrackPresentation.LayerKind);
        Assert.IsFalse(satellite.EnabledByDefault);
        Assert.AreEqual(ProcessingIdentity.CreateArtifactId(products.Single(p => p.Variant == "capture-satellites-v1-layer").OutputIdentitySha256),
            satellite.SourceProduct.ArtifactId);
        Assert.IsTrue(CaptureSatelliteProducts.ParseStatus(HVO.SkyMonitor.Astronomy.OrbitalContract.SerializeCanonical(
            satellite.Options.GetProperty("Satellite"))).Renderable);
        Assert.HasCount(6, result.Layers);
    }
}
