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

        // 1. Live Current Sky MUST still display 104 — late trail on 103 CANNOT replace 104!
        var live = await service.GetCurrentSkyViewAsync(CenteredLiveMode.Centered, token).ConfigureAwait(false);
        Assert.IsTrue(live.IsSuccess);
        Assert.IsNotNull(live.Value?.Centered?.View);
        Assert.AreEqual(stack104.Base.BaseId, live.Value.Centered.View.Base.BaseId);
        Assert.AreEqual(input104.Key.CaptureId, live.Value.Centered.View.Base.Definition.Reference.Capture.CaptureId);
        Assert.IsFalse(live.Value.ProcessingPending);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, live.Value.Centered.PresentationStatus);
        Assert.IsNotNull(live.Value.Centered.Presentation);

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

        // 3. Open the later archive enrichment explicitly by ?base=...&revision=...
        var enrichedRevId = stackRevisions[1].RevisionId;
        var enrichedOpen = await service.GetCaptureDetailViewAsync(
            input103.Key.CaptureId,
            new CameraAgentCenteredArchiveRequest(stack103.Base.BaseId, enrichedRevId),
            token).ConfigureAwait(false);
        Assert.IsTrue(enrichedOpen.IsSuccess);
        Assert.AreEqual(enrichedRevId, enrichedOpen.Value!.CenteredArchive!.SelectedView?.Revision.RevisionId);
        Assert.IsNotNull(enrichedOpen.Value.CenteredArchive.Presentation);
        Assert.HasCount(1, enrichedOpen.Value.CenteredArchive.Presentation.Layers);
        var trailLayerId = enrichedOpen.Value.CenteredArchive.Presentation.Layers[0].IdentitySha256;

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
    }

    private static ClaimsPrincipal CanonicalOwnerPrincipal(string ownerId) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, ownerId),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ],
        IdentityConstants.ApplicationScheme));
}
