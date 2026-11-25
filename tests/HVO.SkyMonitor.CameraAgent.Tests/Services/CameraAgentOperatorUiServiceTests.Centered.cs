using System.Buffers.Binary;
using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.Gallery;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

public sealed partial class CameraAgentOperatorUiServiceTests
{
    private static readonly JsonSerializerOptions CenteredWebJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task CenteredServiceReopensSingleStackSavedSelectionAndPreventsLateTrailOn103FromReplacing104Async(
        CameraPixelFormat pixelFormat)
    {
        using var fixture = await CameraAgentArtifactServiceTests.ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var producer = new CenteredViewProducer(persistence, fixture.Service, views, options, fixture.ProcessingStore);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);
        var presentationReader = new CenteredViewPresentationReader(views, fixture.Service);

        const int width = 64;
        const int height = 48;
        var layout = pixelFormat == CameraPixelFormat.BayerRggb16
            ? new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.BayerRggb16,
                FrameByteOrder.LittleEndian, 12, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 64, 4095, width * height * 2)
            {
                Readout = new(width + 2, height + 2, 1, 1, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, 1, 1),
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                LevelCodeSpace = FrameLevelCodeSpace.NativeSample
            }
            : new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
                FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 65535, width * height * 2);

        var pixels = new byte[width * height * 2];
        for (var i = 0; i < width * height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2), (ushort)(200 + (i % 500)));
        }

        var raw103 = await fixture.AddRawAsync(pixels, width, height, layout).ConfigureAwait(false);
        var raw104 = await fixture.AddRawAsync(pixels, width, height, layout).ConfigureAwait(false);
        var raw105 = await fixture.AddRawAsync(pixels, width, height, layout).ConfigureAwait(false);
        var stream = new CenteredViewStream(new(raw103.Manifest.Descriptor.Capture.AgentId, "camera-1"), Guid.NewGuid(), "gen-1923");

        ExposureInputV1 Input(CameraAgentArtifactServiceTests.StoredArtifact raw) =>
            new(new(stream.Camera, stream.RunId, raw.Manifest.Descriptor.Capture.CaptureId), raw.Manifest.Descriptor,
                new(layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));

        var input103 = Input(raw103);
        var input104 = Input(raw104);
        var input105 = Input(raw105);

        // Publish Single 103
        var single103 = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input103.Raw.Capture.CaptureSequence,
            new(input103, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single103);
        await views.SelectLiveAsync(stream, single103.Base.BaseId, token).ConfigureAwait(false);

        // Register Stack 103 and Stack 104
        var exp103 = single103.Base.Definition.Reference with { Linear = new(Guid.NewGuid(), new string('A', 64), new string('B', 64)) };
        var exp104 = new CenteredViewExposure(input104, Guid.NewGuid(), new(Guid.NewGuid(), new string('C', 64), new string('D', 64)));
        var exp105 = new CenteredViewExposure(input105, Guid.NewGuid(), new(Guid.NewGuid(), new string('E', 64), new string('F', 64)));

        var stack103 = await RegisterStackAsync(exp103, exp105).ConfigureAwait(false);
        var stack104 = await RegisterStackAsync(exp104, exp105).ConfigureAwait(false);
        await views.SelectLiveAsync(stream, stack103.Base.BaseId, token).ConfigureAwait(false);
        await views.SelectLiveAsync(stream, stack104.Base.BaseId, token).ConfigureAwait(false);

        // Late trail arrives on 105 (contributor to BOTH 103 and 104) AFTER 104 is current!
        var rawPin = input105.Raw.Artifact;
        var sourceArtifact = new ProcessingArtifact(rawPin.ArtifactId, FrameArtifactRole.Raw, rawPin.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawPin.Recipe).IdentitySha256, rawPin.MediaType, layout, pixels,
            rawPin.CreatedUtc, input105.Raw.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(input105.Raw))
        {
            CaptureId = input105.Key.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(input105.Raw)
        };
        var trailPayload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
            segments: [new(new(10, 10), new(30, 30), 2, new(255, 200, 100))]);
        var trailProduct = PresentationProcessingProducts.CreateLayerProduct(trailPayload, "measured-trail-source", [sourceArtifact], "trail-v1");
        await persistence.PersistCenteredViewProductAsync(input105.Raw, trailProduct, token).ConfigureAwait(false);
        var trailSlot = new ExposureProductSlot(Guid.NewGuid(), "measured-trails", trailPayload.SchemaVersion, trailProduct.Variant);
        var trailRef = new ExposureProductRef(input105.Key, trailSlot, ProcessingIdentity.CreateArtifactId(trailProduct.OutputIdentitySha256),
            trailProduct.OutputIdentitySha256, trailProduct.ChecksumSha256, trailPayload.ContentIdentitySha256);
        var trailLookup = new ExposureProductLookup(input105.Key, trailSlot, ExposureProductAvailability.Available, 7, trailRef, null);
        await layerProducer.PublishToContributorsAsync(trailLookup, new(TransientTrailPresentation.LayerKind, 500, true),
            input105.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);

        // Wire real CameraAgentOperatorUiService with mock lookup pointing to the real SqliteCenteredViews!
        var principal = CanonicalOwnerPrincipal("owner-1923");
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(a => a.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        authorization.Setup(a => a.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(AuthorizationResult.Success());

        var lookup = new Mock<ICenteredViewLookup>(MockBehavior.Strict);
        lookup.Setup(l => l.ReadLiveContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CenteredViewLiveContext(stream,
                new(input105.Key.CaptureId, input105.Raw.Capture.CaptureSequence, input105.Raw.Timing.ExposureStartedUtc, 5, GalleryEvidenceOrigin.Simulated),
                DateTimeOffset.UtcNow, CenteredViewLiveState.Ready, null));
        lookup.Setup(l => l.ReadArchiveAsync(input103.Key.CaptureId, It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => views.ReadArchiveAsync(stream, id, ct));

        var capture103 = OperatorUiTestData.Capture(input103.Key.CaptureId) with { CaptureSequence = input103.Raw.Capture.CaptureSequence };
        var capture104 = OperatorUiTestData.Capture(input104.Key.CaptureId) with { CaptureSequence = input104.Raw.Capture.CaptureSequence };
        var gallery = new Mock<ICameraAgentGallery>(MockBehavior.Strict);
        gallery.Setup(g => g.GetCaptureAsync(input103.Key.CaptureId, It.IsAny<CancellationToken>())).ReturnsAsync(capture103);
        gallery.Setup(g => g.GetCaptureAsync(input104.Key.CaptureId, It.IsAny<CancellationToken>())).ReturnsAsync(capture104);

        var presentations = new Mock<ICameraAgentCurrentImagePresentationService>(MockBehavior.Strict);
        presentations.Setup(p => p.ProjectCaptureAsync(capture103, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentCapturePresentation(CameraAgentPresentationStage.Raw, OperatorUiTestData.CurrentImage().Stages));

        var service = CreateService(
            new CountingAuthenticationStateProvider(principal),
            authorization.Object,
            gallery: gallery.Object,
            presentations: presentations.Object,
            centeredViewLookup: lookup.Object,
            centeredViewIndex: views,
            centeredViewPublisher: views,
            centeredViewPresentation: presentationReader);

        // 1. Live Current Sky MUST still display 104 immediately without awaiting optional overlays,
        //    and late trail on 103 CANNOT replace 104!
        var live = await service.GetCurrentSkyViewAsync(CenteredLiveMode.Centered, token).ConfigureAwait(false);
        Assert.IsTrue(live.IsSuccess);
        Assert.IsNotNull(live.Value?.Centered?.View);
        Assert.AreEqual(stack104.Base.BaseId, live.Value.Centered.View.Base.BaseId);
        Assert.AreEqual(input104.Key.CaptureId, live.Value.Centered.View.Base.Definition.Reference.Capture.CaptureId);
        Assert.IsFalse(live.Value.ProcessingPending);
        Assert.IsTrue(live.Value.Centered.PresentationDeferred);
        Assert.IsNull(live.Value.Centered.Presentation);

        var liveOverlays = await service.GetCenteredPresentationAsync(
            live.Value.Centered.View.Base.BaseId, live.Value.Centered.View.Revision.RevisionId, token).ConfigureAwait(false);
        Assert.IsTrue(liveOverlays.IsSuccess);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, liveOverlays.Value!.Status);
        Assert.IsNotNull(liveOverlays.Value.Presentation);

        // 2. Archive reopen of 103 offers Single and Stack, defaults to last published-live revision, and distinctly labels later enrichment!
        var archiveDefault = await service.GetCaptureDetailViewAsync(input103.Key.CaptureId, null, token).ConfigureAwait(false);
        Assert.IsTrue(archiveDefault.IsSuccess);
        var centeredArchive = archiveDefault.Value?.CenteredArchive;
        Assert.IsNotNull(centeredArchive);
        Assert.HasCount(2, centeredArchive.Bases);
        Assert.AreEqual(CenteredViewKind.Single, centeredArchive.Bases[0].ArchiveBase.Base.Definition.Kind);
        Assert.AreEqual(CenteredViewKind.Stack, centeredArchive.Bases[1].ArchiveBase.Base.Definition.Kind);
        Assert.AreEqual(stack103.Base.BaseId, centeredArchive.SelectedView?.Base.BaseId);
        Assert.AreEqual(stack103.Revision.RevisionId, centeredArchive.SelectedView?.Revision.RevisionId);

        var stackRevisions = centeredArchive.Bases[1].Revisions;
        Assert.HasCount(2, stackRevisions);
        Assert.AreEqual(CenteredArchiveRevisionRole.LastPublishedLive, stackRevisions[0].Role);
        Assert.AreEqual(CenteredArchiveRevisionRole.LaterArchiveEnrichment, stackRevisions[1].Role);
        Assert.IsTrue(stackRevisions[1].IsLaterEnrichment);

        // 3. Open the later archive enrichment explicitly by ?base=...&revision=... (base-first, overlays deferred)
        var enrichedRevId = stackRevisions[1].RevisionId;
        var enrichedOpen = await service.GetCaptureDetailViewAsync(
            input103.Key.CaptureId,
            new CameraAgentCenteredArchiveRequest(stack103.Base.BaseId, enrichedRevId),
            token).ConfigureAwait(false);
        Assert.IsTrue(enrichedOpen.IsSuccess);
        Assert.AreEqual(enrichedRevId, enrichedOpen.Value!.CenteredArchive!.SelectedView?.Revision.RevisionId);
        Assert.IsNull(enrichedOpen.Value.CenteredArchive.Presentation);
        Assert.IsTrue(enrichedOpen.Value.CenteredArchive.PresentationDeferred);

        var enrichedPresentation = await service.GetCenteredPresentationAsync(
            stack103.Base.BaseId, enrichedRevId, token).ConfigureAwait(false);
        Assert.IsTrue(enrichedPresentation.IsSuccess);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, enrichedPresentation.Value!.Status);
        Assert.IsNotNull(enrichedPresentation.Value.Presentation);
        Assert.HasCount(1, enrichedPresentation.Value.Presentation.Layers);
        var trailLayerId = enrichedPresentation.Value.Presentation.Layers[0].IdentitySha256;

        // 4. Save personal selection on the enriched revision and reopen it!
        var saved = await service.SaveCenteredViewSelectionAsync(
            input103.Key.CaptureId, stack103.Base.BaseId, enrichedRevId, [trailLayerId], token).ConfigureAwait(false);
        Assert.IsTrue(saved.IsSuccess);
        var selectionUrl = CenteredViewLinks.ArchiveSelection(input103.Key.CaptureId, saved.Value!.SelectionId);
        StringAssert.Contains(selectionUrl, $"/gallery/{input103.Key.CaptureId:D}?selection=", StringComparison.Ordinal);

        var reopenedSaved = await service.GetCaptureDetailViewAsync(
            input103.Key.CaptureId,
            new CameraAgentCenteredArchiveRequest(SelectionId: saved.Value.SelectionId),
            token).ConfigureAwait(false);
        Assert.IsTrue(reopenedSaved.IsSuccess);
        Assert.AreEqual(saved.Value.SelectionId, reopenedSaved.Value!.CenteredArchive!.SavedSelection?.SelectionId);
        CollectionAssert.AreEqual(new[] { trailLayerId }, reopenedSaved.Value.CenteredArchive.SavedSelection!.EnabledLayerIds.ToArray());

        // 5. Mismatched capture/base pair fails closed with explicit SelectionError!
        var mismatched = await service.GetCaptureDetailViewAsync(
            input103.Key.CaptureId,
            new CameraAgentCenteredArchiveRequest(stack104.Base.BaseId, stack104.Revision.RevisionId),
            token).ConfigureAwait(false);
        Assert.IsTrue(mismatched.IsSuccess);
        Assert.IsNull(mismatched.Value!.CenteredArchive!.SelectedView);
        Assert.IsNotNull(mismatched.Value.CenteredArchive.SelectionError);

        // 6. Another user CANNOT read owner-1923's saved personal selection!
        var otherUser = CanonicalOwnerPrincipal("other-owner");
        authorization.Setup(a => a.AuthorizeAsync(otherUser, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var otherService = CreateService(
            new CountingAuthenticationStateProvider(otherUser),
            authorization.Object,
            gallery: gallery.Object,
            presentations: presentations.Object,
            centeredViewLookup: lookup.Object,
            centeredViewIndex: views,
            centeredViewPublisher: views,
            centeredViewPresentation: presentationReader);
        var foreignSelection = await otherService.GetCaptureDetailViewAsync(
            input103.Key.CaptureId,
            new CameraAgentCenteredArchiveRequest(SelectionId: saved.Value.SelectionId),
            token).ConfigureAwait(false);
        Assert.IsTrue(foreignSelection.IsSuccess);
        Assert.IsNull(foreignSelection.Value!.CenteredArchive!.SelectedView);
        StringAssert.Contains(foreignSelection.Value.CenteredArchive.SelectionError!, "not found for this account", StringComparison.Ordinal);

        async ValueTask<CenteredView> RegisterStackAsync(CenteredViewExposure reference, CenteredViewExposure contributor)
        {
            var sourceEvidence = new CenteredStackSourceEvidence(2, contributor.Capture.CaptureId,
                CenteredRegistrationReason.Contributed, CenteredRegistrationMethod.VerifiedCentroidSimilarity,
                new ImageSimilarity(1, 0, 1.0, -1.0), 100);
            CenteredViewContributor[] members =
            [
                new(reference, nameof(CenteredRegistrationMethod.ReferenceIdentity), JsonSerializer.SerializeToElement(new { })),
                new(contributor, nameof(CenteredRegistrationMethod.VerifiedCentroidSimilarity), JsonSerializer.SerializeToElement(sourceEvidence, CenteredWebJson))
            ];
            var rawArtifact = new ProcessingArtifact(reference.Input.Raw.Artifact.ArtifactId, FrameArtifactRole.Calibrated,
                reference.Input.Raw.Artifact.Variant, new string('1', 64), reference.Input.Raw.Artifact.MediaType,
                layout, pixels, reference.ExposureStartedUtc, reference.Input.Raw.Controls.EffectiveExposure,
                CameraAgentRecipeExecutionAdapter.CreateCompatibility(reference.Input.Raw))
            {
                CaptureId = reference.Capture.CaptureId
            };
            var encoded = CenteredViewPreviewEncoder.Encode([rawArtifact], layout, CenteredViewImageSource.RegisteredLinear,
                null, null, new(), token);
            await persistence.PersistCenteredViewProductAsync(reference.Input.Raw, encoded.Product, token).ConfigureAwait(false);
            var science = new CenteredViewArtifact(Guid.NewGuid(), new string('5', 64), new string('6', 64));
            var closedWindow = new CenteredViewArtifact(Guid.NewGuid(), new string('7', 64), new string('8', 64));
            return await views.RegisterBaseAsync(
                new(stream, reference, reference.Capture.CaptureSequence, CenteredViewKind.Stack, science, encoded.Preview, members, closedWindow),
                [], token).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SaveCenteredViewSelectionEnforcesAuthorizationAndInputValidationAsync()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner-id")], "test"));
        var auth = new CountingAuthenticationStateProvider(principal);
        var denied = new Mock<IAuthorizationService>(MockBehavior.Strict);
        denied.Setup(a => a.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(AuthorizationResult.Failed());
        var service = CreateService(auth, denied.Object);

        var result = await service.SaveCenteredViewSelectionAsync(
            Guid.NewGuid(), new string('A', 64), new string('B', 64), [], CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(1, auth.ReadCount);
    }

    [TestMethod]
    public void CenteredViewLinksBuildEscapedArchiveAndSelectionUrls()
    {
        var captureId = Guid.Parse("19230000-0000-0000-0000-000000000103");
        var selectionId = Guid.Parse("19230000-0000-0000-0000-000000000999");
        var baseId = new string('A', 64);
        var revId = new string('B', 64);

        Assert.AreEqual($"/gallery/{captureId:D}?base={baseId}&revision={revId}",
            CenteredViewLinks.Archive(captureId, baseId, revId));
        Assert.AreEqual($"/gallery/{captureId:D}?selection={selectionId:D}",
            CenteredViewLinks.ArchiveSelection(captureId, selectionId));
        Assert.AreEqual($"/api/v1/operations/gallery/{captureId:D}/views/{baseId}",
            CenteredViewLinks.Base(captureId, baseId));
    }

    [TestMethod]
    public async Task CenteredPresentationReaderDegradesIndividualBadOptionalLayerAndServiceKeepsReadyBaseDisplayableAsync()
    {
        using var fixture = await CameraAgentArtifactServiceTests.ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root });
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var telemetry = new CaptureProcessingTelemetry();
        var persistence = new CaptureProcessingPersistence(options, fixture.ProcessingStore, storage, telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var views = new SqliteCenteredViews(fixture.ProcessingStore, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
        var producer = new CenteredViewProducer(persistence, fixture.Service, views, options, fixture.ProcessingStore);
        var layerProducer = new CenteredViewLayerProducer(views, persistence);
        var presentationReader = new CenteredViewPresentationReader(views, fixture.Service);

        const int width = 64;
        const int height = 48;
        var layout = new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 65535, width * height * 2);
        var pixels = new byte[width * height * 2];
        for (var i = 0; i < width * height; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2), (ushort)(200 + (i % 500)));
        }

        var raw = await fixture.AddRawAsync(pixels, width, height, layout).ConfigureAwait(false);
        var stream = new CenteredViewStream(new(raw.Manifest.Descriptor.Capture.AgentId, "camera-1"), Guid.NewGuid(), "gen-h2");
        var input = new ExposureInputV1(
            new(stream.Camera, stream.RunId, raw.Manifest.Descriptor.Capture.CaptureId),
            raw.Manifest.Descriptor,
            new(layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));

        var single = await producer.PublishSingleAsync(stream, Guid.NewGuid(), input.Raw.Capture.CaptureSequence,
            new(input, LinearExposureDisposition.Unsupported, 1, null, null, "raw-fallback"), new(), token).ConfigureAwait(false);
        Assert.IsNotNull(single);
        await views.SelectLiveAsync(stream, single.Base.BaseId, token).ConfigureAwait(false);

        var rawPin = input.Raw.Artifact;
        var sourceArtifact = new ProcessingArtifact(rawPin.ArtifactId, FrameArtifactRole.Raw, rawPin.Variant,
            ProcessingIdentity.CreateRecipeIdentity(rawPin.Recipe).IdentitySha256, rawPin.MediaType, layout, pixels,
            rawPin.CreatedUtc, input.Raw.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(input.Raw))
        {
            CaptureId = input.Key.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(input.Raw)
        };

        var healthyPayload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
            segments: [new(new(4, 4), new(20, 20), 2, new(255, 255, 255))]);
        var healthyProduct = PresentationProcessingProducts.CreateLayerProduct(healthyPayload, "scene-annotation-layer-v1", [sourceArtifact], "layer-v1");
        await persistence.PersistCenteredViewProductAsync(input.Raw, healthyProduct, token).ConfigureAwait(false);
        var healthySlot = new ExposureProductSlot(Guid.NewGuid(), "stars", healthyPayload.SchemaVersion, healthyProduct.Variant);
        var healthyRef = new ExposureProductRef(input.Key, healthySlot, ProcessingIdentity.CreateArtifactId(healthyProduct.OutputIdentitySha256),
            healthyProduct.OutputIdentitySha256, healthyProduct.ChecksumSha256, healthyPayload.ContentIdentitySha256);
        await layerProducer.PublishAsync(single.Base.BaseId, CenteredViewLayerScope.Reference,
            new(input.Key, healthySlot, ExposureProductAvailability.Available, 1, healthyRef, null),
            new("star-annotations", 20, true), input.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);

        var badPayload = PresentationLayerPayloadJson.Create(sourceArtifact.DescriptorIdentitySha256!, width, height,
            segments: [new(new(8, 8), new(28, 28), 2, new(255, 200, 100))]);
        var badProduct = PresentationProcessingProducts.CreateLayerProduct(badPayload, "scene-constellation-layer-v1", [sourceArtifact], "layer-v1");
        await persistence.PersistCenteredViewProductAsync(input.Raw, badProduct, token).ConfigureAwait(false);
        var badSlot = new ExposureProductSlot(Guid.NewGuid(), "constellations", badPayload.SchemaVersion, badProduct.Variant);
        var badRef = new ExposureProductRef(input.Key, badSlot, ProcessingIdentity.CreateArtifactId(badProduct.OutputIdentitySha256),
            badProduct.OutputIdentitySha256, badProduct.ChecksumSha256, badPayload.ContentIdentitySha256);
        var enriched = await layerProducer.PublishAsync(single.Base.BaseId, CenteredViewLayerScope.Reference,
            new(input.Key, badSlot, ExposureProductAvailability.Available, 2, badRef, null),
            new("scene-constellations", 10, true), input.Raw.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
        Assert.IsNotNull(enriched);

        // Corrupt the second layer's artifact on disk; first layer and ready base MUST remain displayable!
        var badLayerArtifactId = enriched.Revision.Manifest.Layers.Single(layer => layer.LayerKind == "scene-constellations").SourceProduct.ArtifactId;
        var badStored = await fixture.ProcessingStore.ReadOutputByArtifactIdAsync(badLayerArtifactId, token).ConfigureAwait(false);
        Assert.IsNotNull(badStored);
        File.Delete(Path.Combine(fixture.Root, badStored.PayloadRelativePath));

        var partial = await presentationReader.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, partial.Status);
        Assert.IsNotNull(partial.Presentation);
        Assert.HasCount(1, partial.Presentation.Layers);
        Assert.AreEqual("star-annotations", partial.Presentation.Layers[0].Kind);
        Assert.IsTrue(CameraAgentCenteredViewProjection.IsPresentationCompatible(enriched, partial.Presentation));

        // Even when presentation reader throws on an optional overlay read, operator service still returns the ready base!
        var principal = CanonicalOwnerPrincipal("owner-h2");
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(a => a.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var lookup = new Mock<ICenteredViewLookup>(MockBehavior.Strict);
        lookup.Setup(l => l.ReadLiveContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CenteredViewLiveContext(stream,
                new(input.Key.CaptureId, input.Raw.Capture.CaptureSequence, input.Raw.Timing.ExposureStartedUtc, 5, GalleryEvidenceOrigin.Simulated),
                DateTimeOffset.UtcNow, CenteredViewLiveState.Ready, null));
        lookup.Setup(l => l.ReadArchiveAsync(input.Key.CaptureId, It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => views.ReadArchiveAsync(stream, id, ct));

        var throwingReader = new Mock<ICenteredViewPresentationReader>(MockBehavior.Strict);
        throwingReader.Setup(r => r.ReadAsync(enriched.Base.BaseId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Transient optional overlay storage failure"));

        var capture = OperatorUiTestData.Capture(input.Key.CaptureId) with { CaptureSequence = input.Raw.Capture.CaptureSequence };
        var gallery = new Mock<ICameraAgentGallery>(MockBehavior.Strict);
        gallery.Setup(g => g.GetCaptureAsync(input.Key.CaptureId, It.IsAny<CancellationToken>())).ReturnsAsync(capture);
        var presentations = new Mock<ICameraAgentCurrentImagePresentationService>(MockBehavior.Strict);
        presentations.Setup(p => p.ProjectCaptureAsync(capture, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentCapturePresentation(CameraAgentPresentationStage.Raw, OperatorUiTestData.CurrentImage().Stages));

        var service = CreateService(
            new CountingAuthenticationStateProvider(principal),
            authorization.Object,
            gallery: gallery.Object,
            presentations: presentations.Object,
            centeredViewLookup: lookup.Object,
            centeredViewIndex: views,
            centeredViewPublisher: views,
            centeredViewPresentation: throwingReader.Object);

        // Live ready base returns immediately without invoking optional overlay I/O, and async overlay failure degrades cleanly!
        var live = await service.GetCurrentSkyViewAsync(CenteredLiveMode.Centered, token).ConfigureAwait(false);
        Assert.IsTrue(live.IsSuccess);
        Assert.IsNotNull(live.Value?.Centered?.View);
        Assert.IsNotNull(live.Value.Centered.PreviewUrl);
        Assert.IsNull(live.Value.Centered.Presentation);
        Assert.IsTrue(live.Value.Centered.PresentationDeferred);

        var liveOverlayFailure = await service.GetCenteredPresentationAsync(
            live.Value.Centered.View.Base.BaseId, live.Value.Centered.View.Revision.RevisionId, token).ConfigureAwait(false);
        Assert.IsTrue(liveOverlayFailure.IsSuccess);
        Assert.IsNull(liveOverlayFailure.Value!.Presentation);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Unavailable, liveOverlayFailure.Value.Status);

        var archive = await service.GetCaptureDetailViewAsync(input.Key.CaptureId, null, token).ConfigureAwait(false);
        Assert.IsTrue(archive.IsSuccess);
        Assert.IsNotNull(archive.Value?.CenteredArchive?.SelectedView);
        Assert.IsNotNull(archive.Value.CenteredArchive.PreviewUrl);
        Assert.IsNull(archive.Value.CenteredArchive.Presentation);
        Assert.IsTrue(archive.Value.CenteredArchive.PresentationDeferred);

        // Sol H1: Holding optional overlay I/O on a TaskCompletionSource NEVER blocks ready-base delivery
        // for EITHER live Current Sky OR shared archive reopen!
        var blockedOverlayGate = new TaskCompletionSource<CameraAgentLayeredPresentationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlayReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingReader = new Mock<ICenteredViewPresentationReader>(MockBehavior.Strict);
        blockingReader.Setup(r => r.ReadAsync(enriched.Base.BaseId, enriched.Revision.RevisionId, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                overlayReadStarted.TrySetResult();
                return new ValueTask<CameraAgentLayeredPresentationResult>(blockedOverlayGate.Task);
            });
        var decoupledService = CreateService(
            new CountingAuthenticationStateProvider(principal),
            authorization.Object,
            gallery: gallery.Object,
            presentations: presentations.Object,
            centeredViewLookup: lookup.Object,
            centeredViewIndex: views,
            centeredViewPublisher: views,
            centeredViewPresentation: blockingReader.Object);

        var readyBase = await decoupledService.GetCurrentSkyViewAsync(CenteredLiveMode.Centered, token).ConfigureAwait(false);
        Assert.IsTrue(readyBase.IsSuccess);
        Assert.IsNotNull(readyBase.Value?.Centered?.View);
        Assert.AreEqual(enriched.Base.BaseId, readyBase.Value.Centered.View.Base.BaseId);
        Assert.IsTrue(readyBase.Value.Centered.PresentationDeferred);
        Assert.IsFalse(overlayReadStarted.Task.IsCompleted, "Ready-base delivery must not await optional overlay I/O.");

        var readyArchiveBase = await decoupledService.GetCaptureDetailViewAsync(input.Key.CaptureId, null, token).ConfigureAwait(false);
        Assert.IsTrue(readyArchiveBase.IsSuccess);
        Assert.IsNotNull(readyArchiveBase.Value?.CenteredArchive?.SelectedView);
        Assert.AreEqual(enriched.Base.BaseId, readyArchiveBase.Value.CenteredArchive.SelectedView.Base.BaseId);
        Assert.IsNull(readyArchiveBase.Value.CenteredArchive.Presentation);
        Assert.IsTrue(readyArchiveBase.Value.CenteredArchive.PresentationDeferred);
        Assert.IsFalse(overlayReadStarted.Task.IsCompleted, "Archive base delivery must not await optional overlay I/O.");

        var asyncOverlayTask = decoupledService.GetCenteredPresentationAsync(
            enriched.Base.BaseId, enriched.Revision.RevisionId, token).AsTask();
        await overlayReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsFalse(asyncOverlayTask.IsCompleted);
        blockedOverlayGate.SetResult(partial);
        var attached = await asyncOverlayTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(attached.IsSuccess);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, attached.Value!.Status);
        Assert.IsNotNull(attached.Value.Presentation);
        Assert.HasCount(1, attached.Value.Presentation.Layers);
    }

    [TestMethod]
    public async Task CenteredArchivePagesBeyond128RevisionsPrefersLiveBaseHandlesTransientErrorAndMutationGuardsAsync()
    {
        var capture103Id = Guid.Parse("19230000-0000-0000-0000-000000001103");
        var capture104Id = Guid.Parse("19230000-0000-0000-0000-000000001104");
        var compat = new PresentationCompatibilityDescriptor(640, 480, new string('B', 64), new string('C', 64));
        var descriptor103 = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 640, 480, 640, new byte[640 * 480]).Descriptor;
        descriptor103 = descriptor103 with
        {
            Capture = descriptor103.Capture with { AgentId = "agent-test", CaptureId = capture103Id, CaptureSequence = 103 },
            Timing = descriptor103.Timing with { ExposureStartedUtc = OperatorUiTestData.Now.AddSeconds(-30), ExposureEndedUtc = OperatorUiTestData.Now.AddSeconds(-28) }
        };
        var stream = new CenteredViewStream(new("agent-test", "camera-1"), Guid.NewGuid(), "gen-paging");
        var exp103 = new CenteredViewExposure(
            new(new(stream.Camera, stream.RunId, capture103Id), descriptor103,
                new(descriptor103.Layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null)),
            Guid.NewGuid(),
            new(Guid.NewGuid(), new string('A', 64), new string('B', 64)));
        var preview = new CenteredViewPreview(
            new(Guid.NewGuid(), new string('C', 64), "image/jpeg", compat),
            new string('D', 64),
            RecipeIdentityDescriptor.Create("preview", "1.0.0", "preview-v1", JsonSerializer.SerializeToElement(new { })),
            "Skia JPEG", "jpeg-v1", new(640, 480, 0, 0, 640, 480, 640, 480));
        var singleDef = new CenteredViewBaseDefinition(stream, exp103, 103, CenteredViewKind.Single, exp103.Linear!, preview,
            [new(exp103, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { }))], null);
        var singleBase = new CenteredViewBase(SqliteCenteredViews.BaseIdentity(singleDef), singleDef, OperatorUiTestData.Now);
        var stackDef = singleDef with
        {
            Kind = CenteredViewKind.Stack,
            ClosedWindow = new CenteredViewArtifact(Guid.NewGuid(), new string('1', 64), new string('2', 64))
        };
        var stackBase = new CenteredViewBase(SqliteCenteredViews.BaseIdentity(stackDef), stackDef, OperatorUiTestData.Now);

        // Build 135 revisions for singleBase with LastLiveRevisionId at order 130 (past 128 cap!)
        var singleRevisions = new List<CenteredViewRevision>(135);
        for (var order = 1; order <= 135; order++)
        {
            var manifest = LayeredPresentationJson.CreateManifest(preview.Product, null, []);
            var revId = FormattableString.Invariant($"{order:X64}");
            singleRevisions.Add(new(revId, singleBase.BaseId, order, OperatorUiTestData.Now.AddSeconds(order), manifest, []));
        }
        var singleArchiveBase = new CenteredViewArchiveBase(
            singleBase, singleRevisions[0].RevisionId, singleRevisions[^1].RevisionId, singleRevisions[129].RevisionId);

        // Build never-live startup stack (LastLiveRevisionId == null) with 3 revisions
        var stackRevisions = new List<CenteredViewRevision>(3);
        for (var order = 1; order <= 3; order++)
        {
            var manifest = LayeredPresentationJson.CreateManifest(preview.Product, null, []);
            var revId = FormattableString.Invariant($"{(1000 + order):X64}");
            stackRevisions.Add(new(revId, stackBase.BaseId, order, OperatorUiTestData.Now.AddSeconds(order), manifest, []));
        }
        var neverLiveStackArchive = new CenteredViewArchiveBase(
            stackBase, stackRevisions[0].RevisionId, stackRevisions[^1].RevisionId, LastLiveRevisionId: null);

        var principal = CanonicalOwnerPrincipal("owner-paging");
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(a => a.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var capture103 = OperatorUiTestData.Capture(capture103Id) with { CaptureSequence = 103 };
        var capture104 = OperatorUiTestData.Capture(capture104Id) with { CaptureSequence = 104 };
        var gallery = new Mock<ICameraAgentGallery>(MockBehavior.Strict);
        gallery.Setup(g => g.GetCaptureAsync(capture103Id, It.IsAny<CancellationToken>())).ReturnsAsync(capture103);
        gallery.Setup(g => g.GetCaptureAsync(capture104Id, It.IsAny<CancellationToken>())).ReturnsAsync(capture104);
        var presentations = new Mock<ICameraAgentCurrentImagePresentationService>(MockBehavior.Strict);
        presentations.Setup(p => p.ProjectCaptureAsync(It.IsAny<CameraAgentGalleryCapture>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentCapturePresentation(CameraAgentPresentationStage.Raw, OperatorUiTestData.CurrentImage().Stages));

        var index = new Mock<ICenteredViewReadIndex>(MockBehavior.Strict);
        index.Setup(i => i.ReadRevisionsAsync(singleBase.BaseId, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, long after, int limit, CancellationToken _) =>
                ValueTask.FromResult<IReadOnlyList<CenteredViewRevision>>(
                    singleRevisions.Where(r => r.Order > after).Take(limit).ToArray()));
        index.Setup(i => i.ReadLiveHistoryAsync(singleBase.BaseId, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, long after, int limit, CancellationToken _) =>
                ValueTask.FromResult<IReadOnlyList<CenteredViewLiveInterval>>(
                    after == 0 ? [new(singleBase.BaseId, singleRevisions[129].RevisionId, 130, OperatorUiTestData.Now, null, null)] : []));
        index.Setup(i => i.ReadRevisionsAsync(stackBase.BaseId, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, long after, int limit, CancellationToken _) =>
                ValueTask.FromResult<IReadOnlyList<CenteredViewRevision>>(
                    stackRevisions.Where(r => r.Order > after).Take(limit).ToArray()));
        index.Setup(i => i.ReadLiveHistoryAsync(stackBase.BaseId, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        index.Setup(i => i.ReadRevisionAsync(singleBase.BaseId, singleRevisions[129].RevisionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CenteredView(singleBase, singleRevisions[129]));

        var selectionFor103Id = Guid.NewGuid();
        index.Setup(i => i.ReadSavedAsync("owner-paging", selectionFor103Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CenteredViewSavedSelection(selectionFor103Id, singleBase.BaseId, singleRevisions[129].RevisionId, [], OperatorUiTestData.Now));

        var lookup = new Mock<ICenteredViewLookup>(MockBehavior.Strict);
        lookup.Setup(l => l.ReadArchiveAsync(capture103Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([neverLiveStackArchive, singleArchiveBase]);
        lookup.Setup(l => l.ReadArchiveAsync(capture104Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([neverLiveStackArchive]);

        // Test manifest-identity mismatch mutation guard (Opus L-6 #2)
        var selectedView = new CenteredView(singleBase, singleRevisions[129]);
        var wrongManifestPresentation = new CameraAgentLayeredPresentation(
            capture103Id, preview.Product.ArtifactId, new string('F', 64), new string('E', 64), new string('D', 64),
            640, 480, [], "<svg/>"u8.ToArray());
        Assert.IsFalse(CameraAgentCenteredViewProjection.IsPresentationCompatible(selectedView, wrongManifestPresentation));

        var reader = new Mock<ICenteredViewPresentationReader>(MockBehavior.Strict);
        reader.Setup(r => r.ReadAsync(singleBase.BaseId, singleRevisions[129].RevisionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentLayeredPresentationResult(CameraAgentLayeredPresentationStatus.Found, wrongManifestPresentation));

        var service = CreateService(
            new CountingAuthenticationStateProvider(principal),
            authorization.Object,
            gallery: gallery.Object,
            presentations: presentations.Object,
            centeredViewLookup: lookup.Object,
            centeredViewIndex: index.Object,
            centeredViewPresentation: reader.Object);

        // 1. Default archive open prefers live-published Single over never-live Stack (Opus L-3),
        //    pages all 135 revisions past 128 cap (Sol M4), never mislabels non-live revisions (Opus L-4),
        //    defers overlay presentation (Sol H1b), and rejects wrong ManifestIdentitySha256 as Malformed on async read (Opus L-6 #2)!
        var result = await service.GetCaptureDetailViewAsync(capture103Id, null, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(result.IsSuccess);
        var archive = result.Value!.CenteredArchive!;
        Assert.AreEqual(singleBase.BaseId, archive.SelectedView?.Base.BaseId);
        Assert.AreEqual(singleRevisions[129].RevisionId, archive.SelectedView?.Revision.RevisionId);
        Assert.IsNull(archive.Presentation);
        Assert.IsTrue(archive.PresentationDeferred);

        var malformedRead = await service.GetCenteredPresentationAsync(
            singleBase.BaseId, singleRevisions[129].RevisionId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(malformedRead.IsSuccess);
        Assert.IsNull(malformedRead.Value!.Presentation);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Malformed, malformedRead.Value.Status);

        var singleSummary = archive.Bases.Single(b => b.ArchiveBase.Base.BaseId == singleBase.BaseId);
        Assert.HasCount(135, singleSummary.Revisions);
        Assert.AreEqual(CenteredArchiveRevisionRole.Initial, singleSummary.Revisions[0].Role);
        Assert.AreEqual(CenteredArchiveRevisionRole.Earlier, singleSummary.Revisions[128].Role);
        Assert.IsFalse(singleSummary.Revisions[128].IsLaterEnrichment);
        Assert.AreEqual(CenteredArchiveRevisionRole.LastPublishedLive, singleSummary.Revisions[129].Role);
        Assert.AreEqual(CenteredArchiveRevisionRole.LaterArchiveEnrichment, singleSummary.Revisions[130].Role);
        Assert.AreEqual(CenteredArchiveRevisionRole.LaterArchiveEnrichment, singleSummary.Revisions[134].Role);

        var stackSummary = archive.Bases.Single(b => b.ArchiveBase.Base.BaseId == stackBase.BaseId);
        Assert.HasCount(3, stackSummary.Revisions);
        Assert.IsTrue(stackSummary.Revisions.All(static r => !r.IsLaterEnrichment && r.Role != CenteredArchiveRevisionRole.LaterArchiveEnrichment));

        // 2. Saved selection belonging to capture 103 opened under capture 104 MUST fail closed (Opus L-6 #1)!
        var wrongCaptureSaved = await service.GetCaptureDetailViewAsync(
            capture104Id,
            new CameraAgentCenteredArchiveRequest(SelectionId: selectionFor103Id),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(wrongCaptureSaved.IsSuccess);
        Assert.IsNull(wrongCaptureSaved.Value!.CenteredArchive!.SelectedView);
        Assert.IsNull(wrongCaptureSaved.Value.CenteredArchive.SavedSelection);
        StringAssert.Contains(wrongCaptureSaved.Value.CenteredArchive.SelectionError!, "does not belong to this capture", StringComparison.Ordinal);

        // 3. Transient centered-index error falls back to legacy on plain open, fails closed on explicit target (Opus L-5)!
        var failingLookup = new Mock<ICenteredViewLookup>(MockBehavior.Strict);
        failingLookup.Setup(l => l.ReadArchiveAsync(capture103Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Transient SQLite lock"));
        var failingService = CreateService(
            new CountingAuthenticationStateProvider(principal),
            authorization.Object,
            gallery: gallery.Object,
            presentations: presentations.Object,
            centeredViewLookup: failingLookup.Object,
            centeredViewIndex: index.Object,
            centeredViewPresentation: reader.Object);

        var plainFallback = await failingService.GetCaptureDetailViewAsync(capture103Id, null, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(plainFallback.IsSuccess);
        Assert.IsNull(plainFallback.Value!.CenteredArchive);

        var explicitFailure = await failingService.GetCaptureDetailViewAsync(
            capture103Id,
            new CameraAgentCenteredArchiveRequest(singleBase.BaseId, singleRevisions[0].RevisionId),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(explicitFailure.IsSuccess);
        Assert.IsNotNull(explicitFailure.Value!.CenteredArchive);
        Assert.IsNull(explicitFailure.Value.CenteredArchive.SelectedView);
        Assert.IsNotNull(explicitFailure.Value.CenteredArchive.SelectionError);
    }

    [TestMethod]
    public async Task CenteredAstrometryPinCallsGetExactAsyncAndFreshnessGradesByDisplayAgeVersusCadenceAsync()
    {
        var captureId = Guid.Parse("19230000-0000-0000-0000-000000002103");
        var pin = new CenteredViewArtifact(Guid.NewGuid(), new string('A', 64), new string('B', 64));
        var principal = CanonicalOwnerPrincipal("owner-m3");
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(a => a.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var astrometryService = new Mock<ICameraAgentRetainedAstrometryService>(MockBehavior.Strict);
        astrometryService.Setup(a => a.GetExactAsync(captureId, pin, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentRetainedAstrometry(captureId, "Retained", "accepted", ArtifactId: pin.ArtifactId));

        var service = CreateService(
            new CountingAuthenticationStateProvider(principal),
            authorization.Object,
            retainedAstrometry: astrometryService.Object);

        var exact = await service.GetRetainedAstrometryAsync(captureId, pin, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(exact.IsSuccess);
        Assert.AreEqual(pin.ArtifactId, exact.Value!.ArtifactId);
        astrometryService.Verify(a => a.GetExactAsync(captureId, pin, It.IsAny<CancellationToken>()), Times.Once);

        // Freshness grading by display age vs cadence:
        // (c) Single view thresholds unchanged (>2x Delayed, >6x Stale)
        var startedUtc = OperatorUiTestData.Now;
        var descriptor = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 64, 48, 64, new byte[64 * 48]).Descriptor;
        descriptor = descriptor with
        {
            Capture = descriptor.Capture with { AgentId = "agent-test", CaptureId = captureId, CaptureSequence = 103 },
            Timing = descriptor.Timing with { ExposureStartedUtc = startedUtc, ExposureEndedUtc = startedUtc.AddSeconds(20) },
            Controls = descriptor.Controls with { EffectiveExposure = TimeSpan.FromSeconds(20) }
        };
        var stream = new CenteredViewStream(new("agent-test", "camera-1"), Guid.NewGuid(), "gen-fresh");
        var compat = new PresentationCompatibilityDescriptor(64, 48, new string('B', 64), new string('C', 64));
        var exp = new CenteredViewExposure(
            new(new(stream.Camera, stream.RunId, captureId), descriptor,
                new(descriptor.Layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null)),
            Guid.NewGuid(),
            new(Guid.NewGuid(), new string('A', 64), new string('B', 64)));
        var preview = new CenteredViewPreview(
            new(Guid.NewGuid(), new string('C', 64), "image/jpeg", compat),
            new string('D', 64),
            RecipeIdentityDescriptor.Create("preview", "1.0.0", "preview-v1", JsonSerializer.SerializeToElement(new { })),
            "Skia JPEG", "jpeg-v1", new(64, 48, 0, 0, 64, 48, 64, 48));
        var def = new CenteredViewBaseDefinition(stream, exp, 103, CenteredViewKind.Single, exp.Linear!, preview,
            [new(exp, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { }))], null);
        var singleView = new CenteredView(
            new(SqliteCenteredViews.BaseIdentity(def), def, startedUtc),
            new(new string('1', 64), SqliteCenteredViews.BaseIdentity(def), 1, startedUtc,
                LayeredPresentationJson.CreateManifest(preview.Product, null, []), []));
        var singleCadence = TimeSpan.FromSeconds(10);

        Assert.AreEqual(CameraAgentPresentationImageFreshness.Current,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(singleView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(15), singleCadence));
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Delayed,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(singleView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(25), singleCadence));
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Stale,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(singleView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(65), singleCadence));
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Historical,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(singleView, true, CenteredViewLiveState.Warming, startedUtc.AddSeconds(5), singleCadence));

        // (a) Healthy live Stack (30s interval + 20s exposure + 5s grace => 85s closure lag):
        //     earliest possible close (80s / 85s) AND just before next advance (114s / 115s) MUST stay Current!
        // (b) Stack past delayed threshold (> 85 + 60 = 145s) => Delayed; past stale threshold (> 85 + 180 = 265s) => Stale!
        var stackDef = def with
        {
            Kind = CenteredViewKind.Stack,
            ClosedWindow = new CenteredViewArtifact(Guid.NewGuid(), new string('7', 64), new string('8', 64))
        };
        var stackView = new CenteredView(
            new(SqliteCenteredViews.BaseIdentity(stackDef), stackDef, startedUtc),
            new(new string('2', 64), SqliteCenteredViews.BaseIdentity(stackDef), 2, startedUtc,
                LayeredPresentationJson.CreateManifest(preview.Product, null, []), []));
        var stackCadence = TimeSpan.FromSeconds(30);

        Assert.AreEqual(CameraAgentPresentationImageFreshness.Current,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(stackView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(80), stackCadence));
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Current,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(stackView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(85), stackCadence));
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Current,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(stackView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(114), stackCadence));
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Current,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(stackView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(145), stackCadence));
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Delayed,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(stackView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(146), stackCadence));
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Stale,
            CameraAgentOperatorUiService.ComputeCenteredFreshness(stackView, false, CenteredViewLiveState.Ready, startedUtc.AddSeconds(266), stackCadence));

        // Shared interval resolution falls back cleanly to facts when scheduleRuntime is absent
        var factCapture = OperatorUiTestData.Capture(captureId) with
        {
            Detail = OperatorUiTestData.Capture(captureId).Detail! with
            {
                CaptureProfile = new CameraAgentCaptureProfileFacts(3.75, 2.8, 180, "Equidistant", TimeSpan.FromSeconds(30), CaptureCadenceMode.MinimumStartInterval)
            }
        };
        var facts = CameraAgentCurrentSkyFactsProjector.Project(factCapture, ObservingDayCalendar.Utc);
        Assert.AreEqual(TimeSpan.FromSeconds(30), CameraAgentOperatorUiService.ResolveCenteredExpectedCaptureInterval(null, facts));
        Assert.IsNull(CameraAgentOperatorUiService.ResolveCenteredExpectedCaptureInterval(null, null));
    }

    [TestMethod]
    public async Task CenteredArchiveQueryUsesReferenceIndexWithoutFullTableScanAsync()
    {
        using var fixture = await CameraAgentArtifactServiceTests.ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var token = CancellationToken.None;
        using var connection = await fixture.ProcessingStore.OpenCenteredViewsAsync(token).ConfigureAwait(false);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "EXPLAIN QUERY PLAN " + SqliteCenteredViews.ArchiveBasesByReferenceSql;
            command.Parameters.AddWithValue("$reference", Guid.NewGuid());
            await AssertUsesIndexesWithoutScanAsync(
                command,
                "ix_processing_centered_view_archive_reference",
                "ix_processing_centered_view_revision_order",
                "ix_processing_centered_view_changes_base").ConfigureAwait(false);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "EXPLAIN QUERY PLAN " + SqliteCenteredViews.ArchiveBasesByStreamAndReferenceSql;
            command.Parameters.AddWithValue("$stream", new string('A', 64));
            command.Parameters.AddWithValue("$reference", Guid.NewGuid());
            await AssertUsesIndexesWithoutScanAsync(
                command,
                "ix_processing_centered_view_reference",
                "ix_processing_centered_view_revision_order",
                "ix_processing_centered_view_changes_base").ConfigureAwait(false);
        }

        static async Task AssertUsesIndexesWithoutScanAsync(
            Microsoft.Data.Sqlite.SqliteCommand command,
            params string[] expectedIndexes)
        {
            using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
            var details = new List<string>();
            while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                details.Add(reader.GetString(3));
            }
            var joined = string.Join(" | ", details);
            StringAssert.Contains(joined, "SEARCH", StringComparison.Ordinal);
            foreach (var expectedIndex in expectedIndexes)
            {
                StringAssert.Contains(joined, expectedIndex, StringComparison.Ordinal);
            }
            Assert.IsFalse(joined.Contains("SCAN", StringComparison.Ordinal), $"Unexpected full table scan in query plan: {joined}");
        }
    }

    private static ClaimsPrincipal CanonicalOwnerPrincipal(string ownerId) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ownerId),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ],
        IdentityConstants.ApplicationScheme));
}
