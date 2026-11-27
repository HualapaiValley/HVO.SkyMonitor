using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

public sealed partial class CameraAgentArtifactServiceTests
{
    [TestMethod]
    public async Task CenteredStackLayerPublishesOnlyForItsReferenceAndExactScience()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);

        const int width = 16;
        const int height = 12;
        var pixels = new byte[width * height * 2];
        var raw = (await fixture.AddRawAsync(pixels, width, height).ConfigureAwait(false)).Manifest;
        var neighborRaw = (await fixture.AddRawAsync(pixels, width, height).ConfigureAwait(false)).Manifest;
        var stream = new CenteredViewStream(new(raw.Descriptor.Capture.AgentId, "camera"), Guid.NewGuid(), "test-generation");
        var referenceLinear = await RetainPixelsAsync(raw, FrameArtifactRole.Calibrated, "reference-linear").ConfigureAwait(false);
        var reference = new CenteredViewExposure(Input(raw.Descriptor), Guid.NewGuid(), referenceLinear);
        var neighbor = new CenteredViewExposure(Input(neighborRaw.Descriptor), Guid.NewGuid(),
            await RetainPixelsAsync(neighborRaw, FrameArtifactRole.Calibrated, "neighbor-linear").ConfigureAwait(false));
        var referenceMember = new CenteredViewContributor(reference, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { }));
        var neighborMember = new CenteredViewContributor(neighbor, "VerifiedCentroidSimilarity", JsonSerializer.SerializeToElement(new { }));
        var originalScience = await RetainPixelsAsync(raw, FrameArtifactRole.Combined, "original-science").ConfigureAwait(false);
        var recomputedScience = await RetainPixelsAsync(raw, FrameArtifactRole.Combined, "recomputed-science").ConfigureAwait(false);
        var single = await RegisterAsync(CenteredViewKind.Single, referenceLinear).ConfigureAwait(false);
        var original = await RegisterAsync(CenteredViewKind.Stack, originalScience).ConfigureAwait(false);
        var recomputed = await RegisterAsync(CenteredViewKind.Stack, recomputedScience, original.Base.BaseId).ConfigureAwait(false);

        var style = new CenteredViewLayerStyle("stack-markers", 200, false);
        var matching = await RetainLayerAsync(reference, originalScience, "original-stack-layer").ConfigureAwait(false);
        var enriched = await PublishAsync(original, matching).ConfigureAwait(false);
        Assert.IsNotNull(enriched);
        Assert.AreEqual(original.Base.BaseId, enriched.Base.BaseId);
        Assert.AreNotEqual(original.Revision.RevisionId, enriched.Revision.RevisionId);
        var reopened = await views.ReadRevisionAsync(original.Base.BaseId, enriched.Revision.RevisionId, token).ConfigureAwait(false);
        Assert.IsNotNull(reopened);
        var layer = Assert.ContainsSingle(reopened.Revision.Layers);
        Assert.AreEqual(new CenteredViewLayerKey(style.Kind, CenteredViewLayerScope.Stack, null), layer.Key);
        Assert.AreEqual(new CenteredViewArtifact(matching.Product!.ArtifactId, matching.Product.OutputIdentitySha256,
            matching.Product.PayloadSha256), layer.Evidence);
        Assert.AreEqual(CenteredViewReadiness.Ready, layer.Readiness);
        Assert.IsNotNull(layer.Drawable);
        Assert.AreEqual(layer.Drawable.LayerIdentitySha256, Assert.ContainsSingle(reopened.Revision.Manifest.Layers).LayerIdentitySha256);

        // Sharing the reference and contributor list does not make a recomputation's science interchangeable.
        await AssertRejectedAsync(recomputed, matching).ConfigureAwait(false);
        var foreign = await RetainLayerAsync(neighbor, originalScience, "foreign-owner-layer").ConfigureAwait(false);
        // Give the foreign evidence a newer order so it could replace the accepted layer if the ownership check were bypassed.
        await AssertRejectedAsync(reopened, foreign with { Revision = layer.EvidenceOrder + 1 }).ConfigureAwait(false);
        var singleEvidence = await RetainLayerAsync(reference, referenceLinear, "single-layer").ConfigureAwait(false);
        await AssertRejectedAsync(single, singleEvidence).ConfigureAwait(false);

        var archive = await views.ReadArchiveAsync(stream, reference.Capture.CaptureId, token).ConfigureAwait(false);
        foreach (var target in new[] { reopened, recomputed, single })
        {
            var latest = archive.Single(item => item.Base.BaseId == target.Base.BaseId);
            Assert.AreEqual(target.Revision.RevisionId, latest.LatestRevisionId);

            var persisted = await views.ReadRevisionAsync(latest.Base.BaseId, latest.LatestRevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(persisted);
            CollectionAssert.AreEqual(
                target.Revision.Layers.Select(static layer => (
                    layer.Key, layer.EvidenceOrder, layer.Evidence, layer.ObservedUtc, layer.Readiness, layer.Reason,
                    layer.Drawable?.LayerIdentitySha256)).ToArray(),
                persisted.Revision.Layers.Select(static layer => (
                    layer.Key, layer.EvidenceOrder, layer.Evidence, layer.ObservedUtc, layer.Readiness, layer.Reason,
                    layer.Drawable?.LayerIdentitySha256)).ToArray(),
                "A rejected publication must preserve the stored layers.");
            CollectionAssert.AreEqual(
                target.Revision.Manifest.Layers.Select(static layer => layer.LayerIdentitySha256).ToArray(),
                persisted.Revision.Manifest.Layers.Select(static layer => layer.LayerIdentitySha256).ToArray(),
                "A rejected publication must preserve the stored manifest layers.");
        }

        ExposureInputV1 Input(ReconstructionDescriptor descriptor) => new(new(stream.Camera, stream.RunId, descriptor.Capture.CaptureId),
            descriptor, new(descriptor.Layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));

        async Task<CenteredViewArtifact> RetainPixelsAsync(ArtifactManifestV2 manifest, FrameArtifactRole role, string variant)
        {
            var output = await fixture.AddOutputAsync(manifest, role, width: width, height: height,
                pixelFormat: CameraPixelFormat.Mono16, payload: pixels, variant: variant).ConfigureAwait(false);
            var artifact = output.Manifest.Descriptor.Artifact;
            var identity = ProcessingIdentity.CreateOutputIdentity(role, variant,
                ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256, artifact.SourceArtifactIds);
            return new(output.ArtifactId, identity, output.ChecksumSha256);
        }

        async Task<CenteredView> RegisterAsync(CenteredViewKind kind, CenteredViewArtifact science, string? parent = null)
        {
            var stack = kind == CenteredViewKind.Stack;
            var source = stack ? CenteredViewImageSource.RegisteredLinear : CenteredViewImageSource.QualifiedLinear;
            var raster = await persistence.ReadCenteredViewProductAsync(reference.Capture.CaptureId, science, token).ConfigureAwait(false);
            var preview = CenteredViewPreviewEncoder.Encode([raster], raw.Descriptor.Layout, source, null, null, new(), token);
            await persistence.PersistCenteredViewProductAsync(raw.Descriptor, preview.Product, token).ConfigureAwait(false);
            return await views.RegisterBaseAsync(new(stream, reference, reference.Capture.CaptureSequence, kind, science,
                preview.Preview, stack ? [referenceMember, neighborMember] : [referenceMember], stack ? originalScience : null, parent),
                [], token).ConfigureAwait(false);
        }

        async Task<ExposureProductLookup> RetainLayerAsync(CenteredViewExposure owner, CenteredViewArtifact science, string variant)
        {
            var parent = await persistence.ReadCenteredViewProductAsync(reference.Capture.CaptureId, science, token).ConfigureAwait(false);
            var payload = PresentationLayerPayloadJson.Create(parent.ContentIdentitySha256!, width, height,
                markers: [new(new(5.5, 6.5), 2, new(200, 240, 255))]);
            var product = PresentationProcessingProducts.CreateLayerProduct(payload, variant, [parent], "stack-scope-test-v1");
            await persistence.PersistCenteredViewProductAsync(owner.Input.Raw, product, token).ConfigureAwait(false);
            var slot = new ExposureProductSlot(owner.ExecutionId, style.Kind, payload.SchemaVersion, product.Variant);
            var evidence = new ExposureProductRef(owner.Input.Key, slot, ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
                product.OutputIdentitySha256, product.ChecksumSha256, payload.ContentIdentitySha256);
            return new(owner.Input.Key, slot, ExposureProductAvailability.Available, 1, evidence, null);
        }

        ValueTask<CenteredView?> PublishAsync(CenteredView target, ExposureProductLookup evidence) =>
            layerProducer.PublishAsync(target.Base.BaseId, CenteredViewLayerScope.Stack, evidence, style,
                reference.ExposureEndedUtc, cancellationToken: token);

        async Task AssertRejectedAsync(CenteredView target, ExposureProductLookup evidence)
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PublishAsync(target, evidence).AsTask())
                .ConfigureAwait(false);
            StringAssert.Contains(error.Message, "registered stack", StringComparison.Ordinal);
        }
    }
}
