using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class SqliteCameraAgentGalleryTests
{
    private static readonly string[] SourceDependency = ["source"];
    private static readonly string[] ExpectedDeliveryStatuses = ["Pending", "Retry", "Acknowledged", "Quarantined"];
    private static readonly string[] ExpectedTwoRootDeliveryStatuses = ["raw-ingress:Pending", "storage-1:Acknowledged"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task PageBoundsAndCursorBindingAreEnforcedAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        for (var index = 0; index < 25; index++)
        {
            await fixture.AddRawAsync(Utc(1).AddSeconds(index), "Physical", null).ConfigureAwait(false);
        }

        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () =>
            await fixture.Gallery.GetPageAsync(new CameraAgentGalleryQuery(PageSize: 0), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () =>
            await fixture.Gallery.GetPageAsync(new CameraAgentGalleryQuery(PageSize: 101), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var defaultPage = await fixture.Gallery.GetPageAsync(new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(24, defaultPage.Items);
        Assert.IsNotNull(defaultPage.NextCursor);
        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () =>
            await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: 1, Cursor: "not-a-cursor"), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task KeysetPagingUsesSequenceDespiteNonMonotonicTimestampsAndRemainsStableAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var timestamp = Utc(2);
        var first = await fixture.AddRawAsync(timestamp.AddHours(2), "Physical", null).ConfigureAwait(false);
        var second = await fixture.AddRawAsync(timestamp.AddHours(-1), "Physical", null).ConfigureAwait(false);
        var third = await fixture.AddRawAsync(timestamp, "Physical", null).ConfigureAwait(false);

        var page1 = await fixture.Gallery.GetPageAsync(new CameraAgentGalleryQuery(PageSize: 2), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(2, page1.Items);
        CollectionAssert.AreEqual(
            new[] { third.Descriptor.Capture.CaptureId, second.Descriptor.Capture.CaptureId },
            page1.Items.Select(static item => item.CaptureId).ToArray());
        Assert.IsNotNull(page1.NextCursor);
        await fixture.AddRawAsync(timestamp.AddHours(-2), "Physical", null).ConfigureAwait(false);
        var page2 = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(PageSize: 2, Cursor: page1.NextCursor), CancellationToken.None).ConfigureAwait(false);

        var observed = page1.Items.Concat(page2.Items).Select(static item => item.CaptureId).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { first.Descriptor.Capture.CaptureId, second.Descriptor.Capture.CaptureId, third.Descriptor.Capture.CaptureId },
            observed);
        Assert.AreEqual(observed.Length, observed.Distinct().Count());
        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () =>
            await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: 2, Cursor: page1.NextCursor, RawState: "committed"),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task IndexedRawAndProcessingFiltersSelectCaptureGroupsAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var start = Utc(3);
        var simulated = await fixture.AddRawAsync(start, "VirtualSky", CreateScene()).ConfigureAwait(false);
        var random = await fixture.AddRawAsync(start.AddMinutes(1), "RandomImage", null).ConfigureAwait(false);
        var unknown = await fixture.AddRawAsync(start.AddMinutes(2), "PhysicalCamera", null).ConfigureAwait(false);
        await fixture.AddProcessingOutputAsync(simulated, "Preview", DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        await fixture.SetRawStateAsync(unknown.Descriptor.Capture.CaptureId, "quarantined").ConfigureAwait(false);

        var simulatedPage = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(EvidenceOrigin: GalleryEvidenceOrigin.Simulated), CancellationToken.None).ConfigureAwait(false);
        var randomPage = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(EvidenceOrigin: GalleryEvidenceOrigin.DeveloperFixture), CancellationToken.None).ConfigureAwait(false);
        var statePage = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(RawState: "QUARANTINED"), CancellationToken.None).ConfigureAwait(false);
        var timeSequencePage = await fixture.Gallery.GetPageAsync(new CameraAgentGalleryQuery(
            FromUtc: start.AddSeconds(30),
            ToUtc: start.AddMinutes(2),
            MinimumSequence: random.Descriptor.Capture.CaptureSequence,
            MaximumSequence: unknown.Descriptor.Capture.CaptureSequence), CancellationToken.None).ConfigureAwait(false);
        var rolePage = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(ProcessingRole: FrameArtifactRole.Preview), CancellationToken.None).ConfigureAwait(false);
        var recipePage = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(Recipe: "PREVIEW-RECIPE"), CancellationToken.None).ConfigureAwait(false);
        var statusPage = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(ProcessingStatus: "completed"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(simulated.Descriptor.Capture.CaptureId, AssertSingle(simulatedPage).CaptureId);
        Assert.AreEqual(GalleryEvidenceOrigin.Simulated, AssertSingle(simulatedPage).EvidenceOrigin);
        Assert.AreEqual(random.Descriptor.Capture.CaptureId, AssertSingle(randomPage).CaptureId);
        Assert.AreEqual(GalleryEvidenceOrigin.DeveloperFixture, AssertSingle(randomPage).EvidenceOrigin);
        Assert.AreEqual(unknown.Descriptor.Capture.CaptureId, AssertSingle(statePage).CaptureId);
        Assert.HasCount(2, timeSequencePage.Items);
        Assert.AreEqual(simulated.Descriptor.Capture.CaptureId, AssertSingle(rolePage).CaptureId);
        Assert.AreEqual(simulated.Descriptor.Capture.CaptureId, AssertSingle(recipePage).CaptureId);
        Assert.AreEqual(simulated.Descriptor.Capture.CaptureId, AssertSingle(statusPage).CaptureId);
    }

    [TestMethod]
    public async Task DetailProjectsRawAndOutputLineageWithoutInternalDataAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(
            Utc(4), "VirtualSky", CreateScene()).ConfigureAwait(false);
        var output = await fixture.AddProcessingOutputAsync(
            raw, "Preview", DurableProcessingNodeStatus.TerminalFailure).ConfigureAwait(false);

        var detail = await fixture.Gallery.GetCaptureAsync(raw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail);
        Assert.AreEqual(raw.Descriptor.Capture.RigId, detail.RigId);
        Assert.AreEqual(GalleryEvidenceOrigin.Simulated, detail.EvidenceOrigin);
        Assert.HasCount(2, detail.Artifacts);
        var rawArtifact = detail.Artifacts.Single(artifact => artifact.Role == FrameArtifactRole.Raw);
        var outputArtifact = detail.Artifacts.Single(artifact => artifact.Role == FrameArtifactRole.Preview);
        CollectionAssert.AreEqual(new[] { rawArtifact.ArtifactId }, outputArtifact.SourceArtifactIds.ToArray());
        Assert.AreEqual(output.Artifact.ArtifactId, outputArtifact.ArtifactId);
        Assert.IsNotNull(outputArtifact.Algorithms);
        Assert.IsNotEmpty(outputArtifact.Algorithms);
        Assert.AreEqual("preview", outputArtifact.ProcessingNodeId);
        Assert.AreEqual("TerminalFailure", AssertSingle(detail.ProcessingNodes).Status);
        Assert.IsNotNull(detail.Detail);
        Assert.AreEqual("Available", detail.Detail.EvidenceAvailability);
        Assert.AreEqual(ArtifactManifestV2.CurrentSchemaVersion, detail.Detail.ManifestSchemaVersion);
        Assert.AreEqual(raw.Descriptor.Layout.Width, detail.Detail.Layout!.Width);
        Assert.AreEqual(raw.Descriptor.Controls.EffectiveGain, detail.Detail.Controls!.EffectiveGain);
        Assert.AreEqual(raw.Descriptor.Timing.ExposureEndedUtc, detail.Detail.Timing!.ExposureEndedUtc);
        Assert.IsTrue(detail.Detail.RawRetentionHold);
        var legacyNode = AssertSingle(detail.Detail.ProcessingNodes);
        Assert.AreEqual(2, legacyNode.Attempt);
        Assert.AreEqual("unavailable", legacyNode.FailureCategory);
        Assert.IsNull(legacyNode.Inputs);
        Assert.IsNull(legacyNode.ProcessingProfileIdentitySha256);
        Assert.IsNull(legacyNode.StartedUtc);
        Assert.IsNull(legacyNode.DurationMilliseconds);
        Assert.IsNull(legacyNode.Outcome);
        Assert.AreEqual("Unavailable", detail.Detail.ArtifactStates[0].DeliveryAvailability);
        Assert.AreEqual("Unavailable", detail.Detail.CloudAssessment.Availability);
        Assert.AreEqual("Unavailable", detail.CanonicalSceneAvailability);
        var json = JsonSerializer.Serialize(detail);
        Assert.IsFalse(json.Contains("relativePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("lease", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("options\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("sensitive processing failure", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(fixture.Root, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GalleryProjectsTypedFactsAndOnlyV3ProjectedSceneAsCanonical()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        await fixture.AddCloudAssessmentAsync(
            raw, CreateCloudAssessment(raw, CloudAssessmentStatus.Quantified, false), "projected-scene-v1")
            .ConfigureAwait(false);

        var detail = await fixture.Gallery.GetCaptureAsync(
            raw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        var metadata = detail!.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Metadata);
        Assert.AreEqual(nameof(ProcessingProductKind.Metadata), metadata.ProductKind);
        Assert.AreEqual("projected-scene-v1", metadata.ProductSchemaVersion);
        Assert.AreEqual(new string('D', 64), metadata.ContentIdentitySha256);
        Assert.AreEqual("Available", detail.CanonicalSceneAvailability);
    }

    [TestMethod]
    public async Task EncodedDimensionsSurviveStoreAndGalleryRestartAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        var encoded = await fixture.AddProcessingOutputAsync(
            raw,
            "published-preview",
            DurableProcessingNodeStatus.Completed,
            encodedWidth: 1_280,
            encodedHeight: 720).ConfigureAwait(false);

        using var restarted = fixture.OpenRestartedGallery();
        var page = await restarted.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);

        var artifact = AssertSingle(page).Artifacts.Single(candidate =>
            candidate.ArtifactId == encoded.Artifact.ArtifactId);
        Assert.AreEqual("image/jpeg", artifact.MediaType);
        Assert.AreEqual(1_280, artifact.EncodedWidth);
        Assert.AreEqual(720, artifact.EncodedHeight);
        Assert.AreEqual(CameraPixelFormat.Mono8, artifact.PixelFormat);
    }

    [TestMethod]
    public async Task CanonicalSceneAvailabilityIsIndependentOfBoundedArtifactProjection()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        for (var index = 0; index < SqliteCameraAgentGallery.MaximumArtifactsPerCapture; index++)
        {
            await fixture.AddProcessingOutputAsync(
                raw, $"a-node-{index:D3}", DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        }
        await fixture.AddCloudAssessmentAsync(
            raw, CreateCloudAssessment(raw, CloudAssessmentStatus.Quantified, false), "projected-scene-v1",
            nodeId: "z-projected-scene").ConfigureAwait(false);

        var detail = await fixture.Gallery.GetCaptureAsync(
            raw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(detail!.ArtifactsTruncated);
        Assert.AreEqual("Available", detail.CanonicalSceneAvailability);
        Assert.IsFalse(detail.Artifacts.Any(static artifact => artifact.ProductSchemaVersion == "projected-scene-v1"));
    }

    [TestMethod]
    public async Task BoundedProjectionRetainsAvailableLowerStageBeforeUnavailableHigherStageAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        for (var index = 0; index < SqliteCameraAgentGallery.MaximumArtifactsPerCapture; index++)
        {
            await fixture.AddProcessingOutputAsync(
                raw,
                $"a-unavailable-annotated-{index:D3}",
                DurableProcessingNodeStatus.Completed,
                role: FrameArtifactRole.AnnotatedPreview).ConfigureAwait(false);
        }
        await fixture.ExecuteAsync(
            "UPDATE processing_outputs SET availability_state = 'Missing', availability_reason = 'retention.missing';")
            .ConfigureAwait(false);
        var preview = await fixture.AddProcessingOutputAsync(
            raw,
            "z-published-preview",
            DurableProcessingNodeStatus.Completed,
            encodedWidth: 640,
            encodedHeight: 480).ConfigureAwait(false);

        var page = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);

        var capture = AssertSingle(page);
        Assert.IsTrue(capture.ArtifactsTruncated);
        Assert.IsTrue(capture.ProcessingNodesTruncated);
        Assert.IsTrue(capture.Artifacts.Any(artifact => artifact.ArtifactId == preview.Artifact.ArtifactId));
    }

    [TestMethod]
    public async Task BoundedProjectionReservesLowerStageBeforeAvailableUnsupportedHigherStageAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        for (var index = 0; index < SqliteCameraAgentGallery.MaximumArtifactsPerCapture; index++)
        {
            await fixture.AddProcessingOutputAsync(
                raw,
                $"a-unsupported-annotated-{index:D3}",
                DurableProcessingNodeStatus.Completed,
                role: FrameArtifactRole.AnnotatedPreview).ConfigureAwait(false);
        }
        var preview = await fixture.AddProcessingOutputAsync(
            raw,
            "z-published-preview",
            DurableProcessingNodeStatus.Completed,
            encodedWidth: 640,
            encodedHeight: 480).ConfigureAwait(false);

        var page = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);

        var capture = AssertSingle(page);
        Assert.IsTrue(capture.ArtifactsTruncated);
        Assert.IsTrue(capture.Artifacts.Any(artifact => artifact.ArtifactId == preview.Artifact.ArtifactId));
    }

    [TestMethod]
    public async Task BoundedProjectionRetainsEncodedPreviewWithinSameRoleAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        for (var index = 0; index < SqliteCameraAgentGallery.MaximumArtifactsPerCapture; index++)
        {
            await fixture.AddProcessingOutputAsync(
                raw,
                $"a-unsupported-annotated-{index:D3}",
                DurableProcessingNodeStatus.Completed,
                role: FrameArtifactRole.AnnotatedPreview).ConfigureAwait(false);
        }
        var encoded = await fixture.AddProcessingOutputAsync(
            raw,
            "z-encoded-annotated",
            DurableProcessingNodeStatus.Completed,
            encodedWidth: 640,
            encodedHeight: 480,
            role: FrameArtifactRole.AnnotatedPreview).ConfigureAwait(false);

        var page = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);

        var capture = AssertSingle(page);
        Assert.IsTrue(capture.ArtifactsTruncated);
        Assert.IsTrue(capture.Artifacts.Any(artifact => artifact.ArtifactId == encoded.Artifact.ArtifactId));
    }

    [TestMethod]
    public async Task BoundedProjectionUsesPreviewEligibilityWithinSameRoleAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        for (var index = 0; index < SqliteCameraAgentGallery.MaximumArtifactsPerCapture; index++)
        {
            await fixture.AddProcessingOutputAsync(
                raw,
                $"a-oversized-annotated-{index:D3}",
                DurableProcessingNodeStatus.Completed,
                encodedWidth: 4_096,
                encodedHeight: 4_096,
                role: FrameArtifactRole.AnnotatedPreview).ConfigureAwait(false);
        }
        var reconstructable = await fixture.AddProcessingOutputAsync(
            raw,
            "z-reconstructable-annotated",
            DurableProcessingNodeStatus.Completed,
            role: FrameArtifactRole.AnnotatedPreview,
            mediaType: "application/x-hvo-packed-image").ConfigureAwait(false);

        var page = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);

        var capture = AssertSingle(page);
        Assert.IsTrue(capture.ArtifactsTruncated);
        Assert.IsTrue(capture.Artifacts.Any(artifact => artifact.ArtifactId == reconstructable.Artifact.ArtifactId));
    }

    [TestMethod]
    [DataRow(FrameArtifactRole.Combined, CameraAgentPresentationStage.Combined)]
    [DataRow(FrameArtifactRole.Calibrated, CameraAgentPresentationStage.Calibrated)]
    public async Task BoundedProjectionRanksTechnicalRoleEligibilityBeforeNodeLimitAsync(
        FrameArtifactRole role,
        CameraAgentPresentationStage stage)
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        for (var index = 0; index < SqliteCameraAgentGallery.MaximumProcessingNodesPerCapture - 5; index++)
        {
            await fixture.AddProcessingOutputAsync(
                raw,
                $"a-ineligible-{role}-{index:D3}",
                DurableProcessingNodeStatus.Completed,
                role: role,
                mediaType: "image/png").ConfigureAwait(false);
        }
        var eligible = await fixture.AddProcessingOutputAsync(
            raw,
            $"b-eligible-{role}",
            DurableProcessingNodeStatus.Completed,
            role: role,
            mediaType: "application/x-hvo-packed-image").ConfigureAwait(false);
        for (var index = 0; index < 5; index++)
        {
            await fixture.AddProcessingOutputAsync(
                raw,
                $"c-annotated-{index:D3}",
                DurableProcessingNodeStatus.Completed,
                role: FrameArtifactRole.AnnotatedPreview).ConfigureAwait(false);
        }

        var page = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false);

        var capture = AssertSingle(page);
        Assert.IsTrue(capture.ProcessingNodesTruncated);
        Assert.IsTrue(capture.ProcessingNodes.Any(node => node.NodeId == $"b-eligible-{role}"));
        Assert.IsTrue(capture.Artifacts.Any(artifact => artifact.ArtifactId == eligible.Artifact.ArtifactId));
        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()))
            .Project(capture);
        var slot = projection.Stages.Single(candidate => candidate.Stage == stage);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available, slot.Availability);
        Assert.AreEqual(eligible.Artifact.ArtifactId, slot.ArtifactId);
    }

    [TestMethod]
    public async Task CanonicalSceneQueryFailureReturnsProjectionUnavailable()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(5), "Physical", null).ConfigureAwait(false);
        await fixture.ExecuteAsync("DROP INDEX ix_processing_outputs_product;").ConfigureAwait(false);

        var detail = await fixture.Gallery.GetCaptureAsync(
            raw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail);
        Assert.AreEqual("ProjectionUnavailable", detail.CanonicalSceneAvailability);
    }

    [TestMethod]
    public async Task MalformedAndOrphanedEvidenceFailsClosedAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(
            Utc(5), "VirtualSky", CreateScene()).ConfigureAwait(false);
        await fixture.AddProcessingOutputAsync(raw, "Preview", DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        await fixture.ExecuteAsync("UPDATE raw_captures SET manifest_json = X'7B7D';").ConfigureAwait(false);
        await fixture.ExecuteAsync("UPDATE processing_outputs SET agent_id = 'orphan-agent';").ConfigureAwait(false);

        var detail = await fixture.Gallery.GetCaptureAsync(raw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail);
        Assert.AreEqual(GalleryEvidenceOrigin.Unknown, detail.EvidenceOrigin);
        Assert.HasCount(1, detail.Artifacts);
        Assert.IsNull(detail.Artifacts[0].SourceId);
        Assert.IsNull(detail.Artifacts[0].Recipe);
        Assert.IsEmpty(AssertSingle(detail.ProcessingNodes).ArtifactIds);
        Assert.IsNotNull(detail.Detail);
        Assert.AreEqual("Unavailable", detail.Detail.EvidenceAvailability);
        Assert.IsNull(detail.Detail.ManifestSchemaVersion);
        Assert.IsNull(detail.Detail.Layout);
        Assert.IsNull(detail.Detail.Timing);
        Assert.IsNull(detail.Detail.Controls);
    }

    [TestMethod]
    public async Task DetailProjectsRequiredOptionalFailuresAndSanitizedDeliveryStates()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var requiredRaw = await fixture.AddRawAsync(Utc(6), "Physical", null).ConfigureAwait(false);
        await fixture.AddProcessingOutputAsync(
            requiredRaw,
            "required-node",
            DurableProcessingNodeStatus.RetryableFailure,
            required: true,
            dependencies: ["source"],
            reason: "processing.step-exception").ConfigureAwait(false);
        var optionalRaw = await fixture.AddRawAsync(Utc(7), "Physical", null).ConfigureAwait(false);
        await fixture.AddProcessingOutputAsync(
            optionalRaw,
            "optional-node",
            DurableProcessingNodeStatus.TerminalFailure,
            required: false,
            dependencies: ["source"],
            reason: "calibration.library.corrupt").ConfigureAwait(false);
        await fixture.SetDeliveryStatusesAsync(
            requiredRaw.Descriptor.Artifact.ArtifactId,
            ["pending", "retry", "acknowledged", "quarantined"]).ConfigureAwait(false);

        var required = await fixture.Gallery.GetCaptureAsync(
            requiredRaw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
        var optional = await fixture.Gallery.GetCaptureAsync(
            optionalRaw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        var requiredNode = AssertSingle(required!.Detail!.ProcessingNodes);
        Assert.IsTrue(AssertSingle(required.ProcessingNodes).Required);
        CollectionAssert.AreEqual(SourceDependency, requiredNode.Dependencies.ToArray());
        Assert.AreEqual("processing.step-exception", requiredNode.FailureCategory);
        var optionalNode = AssertSingle(optional!.Detail!.ProcessingNodes);
        Assert.IsFalse(AssertSingle(optional.ProcessingNodes).Required);
        Assert.AreEqual("calibration.library.corrupt", optionalNode.FailureCategory);
        var delivery = required.Detail.ArtifactStates.Single(state =>
            state.ArtifactId == requiredRaw.Descriptor.Artifact.ArtifactId);
        CollectionAssert.AreEqual(
            ExpectedDeliveryStatuses,
            delivery.DeliveryStatuses.Select(static item => item.Status).ToArray());
        Assert.IsTrue(delivery.DeliveryStatuses.All(static item => item.StorageAlias == "raw-ingress"));
    }

    [TestMethod]
    public async Task PageAndDetailBoundOversizedProcessingProjectionBeforeSerializationAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(7), "Physical", null).ConfigureAwait(false);
        for (var index = 0; index < 140; index++)
        {
            await fixture.AddProcessingOutputAsync(
                raw,
                $"node-{index:D3}",
                DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        }
        await fixture.ExecuteAsync("""
            UPDATE processing_nodes SET input_evidence_version = 1;
            WITH RECURSIVE ordinals(value) AS (
                SELECT 0
                UNION ALL
                SELECT value + 1 FROM ordinals WHERE value < 127
            )
            INSERT INTO processing_node_inputs(
                capture_id, node_id, input_ordinal, kind, name, artifact_id, role, variant,
                recipe_identity_sha256, schema_version, identity_sha256, selected_flag)
            SELECT capture_id, node_id, value, 'CanonicalContext', 'bounded-context', NULL, NULL, NULL,
                   NULL, 'bounded-context-v1',
                   'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', 0
            FROM processing_nodes CROSS JOIN ordinals;
            """).ConfigureAwait(false);

        var page = await fixture.Gallery.GetPageAsync(
            new CameraAgentGalleryQuery(PageSize: 1), CancellationToken.None).ConfigureAwait(false);
        var pageCapture = AssertSingle(page);
        var detail = await fixture.Gallery.GetCaptureAsync(
            raw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(pageCapture.ProcessingNodesTruncated);
        Assert.IsTrue(pageCapture.ArtifactsTruncated);
        Assert.IsFalse(pageCapture.ProcessingProjectionUnavailable);
        Assert.HasCount(SqliteCameraAgentGallery.MaximumProcessingNodesPerCapture, pageCapture.ProcessingNodes);
        Assert.IsLessThanOrEqualTo(SqliteCameraAgentGallery.MaximumArtifactsPerCapture, pageCapture.Artifacts.Count);
        Assert.IsNotNull(detail);
        Assert.IsTrue(detail.ProcessingNodesTruncated);
        Assert.IsTrue(detail.ArtifactsTruncated);
        var unboundedInputs = detail.Detail!.ProcessingNodes
            .Where(static node => !node.InputsTruncated ||
                node.Inputs?.Count != SqliteCaptureProcessingStore.MaximumGalleryInputsPerNode)
            .Select(static node => $"{node.NodeId}:{node.Inputs?.Count}:{node.InputsTruncated}")
            .ToArray();
        Assert.IsEmpty(unboundedInputs, string.Join(", ", unboundedInputs));
        Assert.IsLessThanOrEqualTo(
            256 * 1024,
            JsonSerializer.SerializeToUtf8Bytes(detail).Length,
            "Oversized durable graphs must remain a bounded operator response.");

        var nodeBoundRaw = await fixture.AddRawAsync(Utc(8), "Physical", null).ConfigureAwait(false);
        for (var index = 0; index <= SqliteCameraAgentGallery.MaximumProcessingNodesPerCapture; index++)
        {
            await fixture.AddProcessingOutputAsync(
                nodeBoundRaw,
                $"bounded-node-{index:D3}",
                DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        }
        var nodeBoundDetail = await fixture.Gallery.GetCaptureAsync(
            nodeBoundRaw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(nodeBoundDetail);
        Assert.IsTrue(nodeBoundDetail.ProcessingNodesTruncated);
        Assert.IsTrue(nodeBoundDetail.ArtifactsTruncated,
            "Outputs attached to omitted nodes must mark the artifact projection as truncated.");
    }

    [TestMethod]
    public async Task DetailAggregatesDeliveryStatusAcrossConfiguredStorageAliasesAsync()
    {
        using var fixture = await GalleryFixture.CreateAsync(twoStorageRoots: true).ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(7), "Physical", null).ConfigureAwait(false);
        await GalleryFixture.SetDeliveryStatusesAsync(
            fixture.Root,
            raw.Descriptor.Artifact.ArtifactId,
            ["pending"]).ConfigureAwait(false);
        await GalleryFixture.SetDeliveryStatusesAsync(
            fixture.SecondaryRoot!,
            raw.Descriptor.Artifact.ArtifactId,
            ["acknowledged"]).ConfigureAwait(false);

        var detail = await fixture.Gallery.GetCaptureAsync(
            raw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        var delivery = detail!.Detail!.ArtifactStates.Single(state =>
            state.ArtifactId == raw.Descriptor.Artifact.ArtifactId);
        CollectionAssert.AreEqual(
            ExpectedTwoRootDeliveryStatuses,
            delivery.DeliveryStatuses.Select(static item => $"{item.StorageAlias}:{item.Status}").ToArray());
        var json = JsonSerializer.Serialize(detail);
        Assert.IsFalse(json.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(fixture.SecondaryRoot!, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(CloudAssessmentStatus.Quantified, true, "Quantified", true)]
    [DataRow(CloudAssessmentStatus.Contaminated, true, "Contaminated", true)]
    [DataRow(CloudAssessmentStatus.Quantified, false, "Quantified", false)]
    public async Task DetailProjectsValidatedCloudAssessmentAndMaskState(
        CloudAssessmentStatus status,
        bool includeMask,
        string expectedStatus,
        bool expectedMask)
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(8), "Physical", null).ConfigureAwait(false);
        var assessment = CreateCloudAssessment(raw, status, includeMask);
        await fixture.AddCloudAssessmentAsync(raw, assessment).ConfigureAwait(false);

        var detail = await fixture.Gallery.GetCaptureAsync(
            raw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        var cloud = detail!.Detail!.CloudAssessment;
        Assert.AreEqual("Available", cloud.Availability);
        Assert.AreEqual(expectedStatus, cloud.Status);
        Assert.AreEqual(expectedMask, cloud.MaskPresent);
        if (expectedMask)
        {
            Assert.AreEqual(1, cloud.MaskWidth);
            Assert.AreEqual(1, cloud.MaskHeight);
            Assert.IsNotNull(cloud.MaskChecksumSha256);
        }
        else
        {
            Assert.IsNull(cloud.MaskWidth);
            Assert.IsNull(cloud.MaskChecksumSha256);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The selected unsupported-schema fixture statements are fixed test SQL.")]
    public async Task PopulatedOlderProcessingSchemasAreRejectedWithoutMutationAsync(int schemaVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-gallery-processing-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressSqliteBusyTimeoutSeconds = 1
        });
        try
        {
            var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            var rawJournal = new SqliteRawCaptureJournal(databasePath, 1);
            await rawJournal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var legacyManifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
            var legacySources = Enumerable.Range(1, 200)
                .Select(index => Guid.Parse($"70000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            legacyManifest = legacyManifest with
            {
                Descriptor = legacyManifest.Descriptor with
                {
                    Capture = new CaptureIdentityDescriptor(
                        "legacy-agent", "legacy-rig", 1,
                        Guid.Parse("10000000-0000-0000-0000-000000000001")),
                    Artifact = legacyManifest.Descriptor.Artifact with { SourceId = "legacy-node" }
                },
                RelativeArtifactPath = "legacy.bin"
            };
            var legacyRecipeIdentity = ProcessingIdentity.CreateRecipeIdentity(
                legacyManifest.Descriptor.Artifact.Recipe).IdentitySha256;
            var legacyOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Metadata, "legacy", legacyRecipeIdentity, legacySources);
            var legacyArtifactId = ProcessingIdentity.CreateArtifactId(legacyOutputIdentity);
            var legacyArtifact = legacyManifest.Descriptor.Artifact with
            {
                ArtifactId = legacyArtifactId,
                Role = FrameArtifactRole.Metadata,
                SourceId = "legacy-node",
                Variant = "legacy",
                SourceArtifactIds = legacySources,
                MediaType = "application/json"
            };
            using var nullDocument = JsonDocument.Parse("null");
            var legacyProductManifest = new DurableProcessingProductManifestV1(
                DurableProcessingProductManifestV1.CurrentSchemaVersion,
                legacyManifest.Descriptor.Capture,
                legacyArtifact,
                legacyOutputIdentity,
                [],
                new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing"),
                1,
                1,
                "legacy.json",
                nullDocument.RootElement.Clone());
            var legacyEvidence = DurableProcessingProductManifestJson.Serialize(legacyProductManifest);
            using (var initial = new SqliteCaptureProcessingStore(options))
            {
                await initial.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            using (var connection = new SqliteConnection(
                       $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (var dropV4 = connection.CreateCommand())
                {
                    dropV4.CommandText = """
                        DROP TABLE processing_reconciliation_state;
                        DROP TABLE processing_output_diagnostics;
                        DROP TABLE processing_lifecycle_operations;
                        DROP INDEX ix_processing_outputs_retention_available;
                        DROP INDEX ix_processing_outputs_retention_unavailable;
                        DROP TABLE processing_output_sources;
                        DROP INDEX ix_processing_outputs_product;
                        ALTER TABLE processing_outputs RENAME TO processing_outputs_v4;
                        CREATE TABLE processing_outputs(
                            output_identity_sha256 TEXT PRIMARY KEY CHECK(length(output_identity_sha256) = 64),
                            capture_id TEXT NOT NULL,
                            agent_id TEXT NOT NULL,
                            node_id TEXT NOT NULL,
                            artifact_id TEXT NOT NULL UNIQUE CHECK(length(artifact_id) = 32),
                            role TEXT NOT NULL,
                            variant TEXT NOT NULL,
                            payload_relative_path TEXT NOT NULL,
                            sidecar_relative_path TEXT NOT NULL,
                            descriptor_json BLOB NOT NULL,
                            recipe_identity_sha256 TEXT NOT NULL CHECK(length(recipe_identity_sha256) = 64),
                            algorithms_json BLOB NOT NULL,
                            compatibility_json BLOB NOT NULL,
                            total_integration_ticks INTEGER NOT NULL,
                            capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
                            committed_unix_ms INTEGER NOT NULL,
                            FOREIGN KEY(capture_id, node_id) REFERENCES processing_nodes(capture_id, node_id)
                                DEFERRABLE INITIALLY DEFERRED
                        ) STRICT;
                        INSERT INTO processing_outputs
                        SELECT output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                               payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                               algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                               committed_unix_ms
                        FROM processing_outputs_v4;
                        DROP TABLE processing_outputs_v4;
                        CREATE INDEX ix_processing_outputs_capture_node ON processing_outputs(capture_id, node_id);
                        CREATE INDEX ix_processing_outputs_window ON processing_outputs(agent_id, node_id, role, capture_sequence);
                        CREATE INDEX ix_processing_outputs_role ON processing_outputs(role, capture_id);
                        CREATE INDEX ix_processing_outputs_recipe ON processing_outputs(recipe_identity_sha256, capture_id);
                        """;
                    await dropV4.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                if (schemaVersion == 1)
                {
                    using var dropV2Indexes = connection.CreateCommand();
                    dropV2Indexes.CommandText = """
                        DROP INDEX ix_processing_nodes_status;
                        DROP INDEX ix_processing_nodes_recipe;
                        DROP INDEX ix_processing_outputs_role;
                        DROP INDEX ix_processing_outputs_recipe;
                        """;
                    await dropV2Indexes.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                using var downgrade = connection.CreateCommand();
                downgrade.CommandText = """
                    DROP TABLE capture_processing_schema;
                    CREATE TABLE capture_processing_schema(
                        schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
                        version INTEGER NOT NULL CHECK(version BETWEEN 1 AND 3)
                    ) STRICT;
                    INSERT INTO capture_processing_schema(schema_key, version) VALUES (1, $version);
                    INSERT INTO processing_nodes(
                        capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                        output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
                    VALUES(
                        '10000000000000000000000000000001', 'legacy-node', 1, '[]', 'legacy-recipe',
                        'Preview', 'legacy', $plan, 'Completed', NULL, 2, 1000);
                    INSERT INTO processing_outputs(
                        output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                        payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                        algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                        committed_unix_ms)
                    VALUES(
                        $output, '10000000000000000000000000000001', 'legacy-agent', 'legacy-node',
                        $artifact, $role, $variant, 'legacy.json', 'legacy.manifest.json',
                        $evidence, $recipe, X'5B5D', $compatibility, 1, 1, 1000);
                    """;
                if (schemaVersion < 3)
                {
                    downgrade.CommandText = string.Concat("""
                        DROP TABLE processing_node_inputs;
                        ALTER TABLE processing_nodes DROP COLUMN outcome;
                        ALTER TABLE processing_nodes DROP COLUMN duration_ticks;
                        ALTER TABLE processing_nodes DROP COLUMN started_unix_ms;
                        ALTER TABLE processing_nodes DROP COLUMN processing_profile_identity_sha256;
                        ALTER TABLE processing_nodes DROP COLUMN input_evidence_version;
                        """, downgrade.CommandText);
                }
                downgrade.Parameters.AddWithValue("$version", schemaVersion);
                downgrade.Parameters.AddWithValue("$plan", new string('A', 64));
                downgrade.Parameters.AddWithValue("$output", legacyOutputIdentity);
                downgrade.Parameters.AddWithValue("$artifact", legacyArtifactId.ToString("N"));
                downgrade.Parameters.AddWithValue("$recipe", legacyRecipeIdentity);
                downgrade.Parameters.AddWithValue("$role", legacyArtifact.Role.ToString());
                downgrade.Parameters.AddWithValue("$variant", legacyArtifact.Variant);
                downgrade.Parameters.AddWithValue("$evidence", legacyEvidence);
                downgrade.Parameters.AddWithValue("$compatibility", JsonSerializer.SerializeToUtf8Bytes(
                    new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing"),
                    WebJson));
                await downgrade.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var rejected = new SqliteCaptureProcessingStore(options))
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await rejected.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }

            using var verify = new SqliteConnection(
                $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
            await verify.OpenAsync().ConfigureAwait(false);
            using var command = verify.CreateCommand();
            command.CommandText = """
                SELECT (SELECT version FROM capture_processing_schema WHERE schema_key = 1),
                       (SELECT COUNT(*) FROM processing_nodes WHERE node_id = 'legacy-node'),
                       (SELECT COUNT(*) FROM processing_outputs WHERE node_id = 'legacy-node'),
                       EXISTS(SELECT 1 FROM sqlite_master WHERE name = 'processing_output_sources');
                """;
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual((long)schemaVersion, reader.GetInt64(0));
            Assert.AreEqual(1L, reader.GetInt64(1));
            Assert.AreEqual(1L, reader.GetInt64(2));
            Assert.IsFalse(reader.GetBoolean(3));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProcessingSchemaDefinitionDriftFailsInitialization()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        await fixture.ExecuteAsync("""
            ALTER TABLE processing_output_sources RENAME TO processing_output_sources_valid;
            CREATE TABLE processing_output_sources(
                output_identity_sha256 TEXT NOT NULL,
                source_ordinal INTEGER NOT NULL,
                source_artifact_id TEXT NOT NULL,
                PRIMARY KEY(output_identity_sha256, source_ordinal)
            ) STRICT;
            DROP TABLE processing_output_sources_valid;
            CREATE INDEX ix_processing_output_sources_artifact
                ON processing_output_sources(source_artifact_id, output_identity_sha256);
            """).ConfigureAwait(false);
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            RawIngressSqliteBusyTimeoutSeconds = 1
        });
        using var drifted = new SqliteCaptureProcessingStore(options);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await drifted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("ix_processing_outputs_product", "capture_id, variant, output_identity_sha256")]
    [DataRow("ix_processing_outputs_product", "product_schema_version, capture_id, output_identity_sha256")]
    [DataRow("ix_processing_output_sources_artifact", "source_ordinal, output_identity_sha256")]
    [DataRow("ix_processing_output_sources_artifact", "output_identity_sha256, source_artifact_id")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Index names and definitions are fixed test data rows.")]
    public async Task ProcessingSchemaIndexColumnDriftFailsInitialization(
        string indexName,
        string replacementColumns)
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var tableName = indexName == "ix_processing_outputs_product"
            ? "processing_outputs"
            : "processing_output_sources";
        await fixture.ExecuteAsync($"""
            DROP INDEX {indexName};
            CREATE INDEX {indexName} ON {tableName}({replacementColumns});
            """).ConfigureAwait(false);
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            RawIngressSqliteBusyTimeoutSeconds = 1
        });
        using var drifted = new SqliteCaptureProcessingStore(options);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await drifted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("payload-path")]
    public async Task PopulatedProcessingV3IsRejectedWithoutRepairingEvidence(string mismatch)
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(6), "Physical", null).ConfigureAwait(false);
        await fixture.AddCloudAssessmentAsync(raw, CreateCloudAssessment(raw, CloudAssessmentStatus.Quantified, false))
            .ConfigureAwait(false);
        await fixture.ExecuteAsync("""
            DROP TABLE processing_output_sources;
            DROP INDEX ix_processing_outputs_product;
            DROP INDEX ix_processing_outputs_capture_node;
            DROP INDEX ix_processing_outputs_window;
            DROP INDEX ix_processing_outputs_role;
            DROP INDEX ix_processing_outputs_recipe;
            ALTER TABLE processing_outputs RENAME TO processing_outputs_v4;
            CREATE TABLE processing_outputs(
                output_identity_sha256 TEXT PRIMARY KEY CHECK(length(output_identity_sha256) = 64),
                capture_id TEXT NOT NULL, agent_id TEXT NOT NULL, node_id TEXT NOT NULL,
                artifact_id TEXT NOT NULL UNIQUE CHECK(length(artifact_id) = 32), role TEXT NOT NULL,
                variant TEXT NOT NULL, payload_relative_path TEXT NOT NULL, sidecar_relative_path TEXT NOT NULL,
                descriptor_json BLOB NOT NULL, recipe_identity_sha256 TEXT NOT NULL CHECK(length(recipe_identity_sha256) = 64),
                algorithms_json BLOB NOT NULL, compatibility_json BLOB NOT NULL,
                total_integration_ticks INTEGER NOT NULL, capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
                committed_unix_ms INTEGER NOT NULL,
                FOREIGN KEY(capture_id, node_id) REFERENCES processing_nodes(capture_id, node_id) DEFERRABLE INITIALLY DEFERRED
            ) STRICT;
            INSERT INTO processing_outputs
            SELECT output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                   payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                   algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                   committed_unix_ms FROM processing_outputs_v4;
            DROP TABLE processing_outputs_v4;
            CREATE INDEX ix_processing_outputs_capture_node ON processing_outputs(capture_id, node_id);
            CREATE INDEX ix_processing_outputs_window ON processing_outputs(agent_id, node_id, role, capture_sequence);
            CREATE INDEX ix_processing_outputs_role ON processing_outputs(role, capture_id);
            CREATE INDEX ix_processing_outputs_recipe ON processing_outputs(recipe_identity_sha256, capture_id);
            DROP TABLE capture_processing_schema;
            CREATE TABLE capture_processing_schema(
                schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1), version INTEGER NOT NULL CHECK(version = 3)
            ) STRICT;
            INSERT INTO capture_processing_schema VALUES(1, 3);
            """).ConfigureAwait(false);
        await fixture.ExecuteAsync(mismatch == "identity"
            ? "UPDATE processing_outputs SET output_identity_sha256 = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA';"
            : "UPDATE processing_outputs SET payload_relative_path = 'products/../wrong.json';").ConfigureAwait(false);
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            RawIngressSqliteBusyTimeoutSeconds = 1
        });
        using var rejected = new SqliteCaptureProcessingStore(options);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await rejected.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(fixture.Root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT (SELECT version FROM capture_processing_schema WHERE schema_key = 1),
                   EXISTS(SELECT 1 FROM sqlite_master WHERE name = 'processing_output_sources');
            """;
        using var reader = await verify.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        Assert.AreEqual(3L, reader.GetInt64(0));
        Assert.IsFalse(reader.GetBoolean(1));
    }

    private static T AssertSingle<T>(IReadOnlyList<T> values)
    {
        Assert.HasCount(1, values);
        return values[0];
    }

    private static CameraAgentGalleryCapture AssertSingle(CameraAgentGalleryPage page) => AssertSingle(page.Items);

    private static DateTimeOffset Utc(int hour) => new(2026, 7, 22, hour, 0, 0, TimeSpan.Zero);

    private static SceneProvenance CreateScene() => new(
        "scene-a",
        "rig-v1",
        "catalog",
        "v1",
        new string('A', 64),
        "projection",
        "projection-v1",
        "astronomy-v1",
        "sensor-v1");

    private static CloudAssessmentV1 CreateCloudAssessment(
        ArtifactManifestV2 raw,
        CloudAssessmentStatus status,
        bool includeMask)
    {
        var contaminated = status == CloudAssessmentStatus.Contaminated;
        var recipeIdentity = new string('D', 64);
        var environment = contaminated
            ? new CloudAssessmentEnvironmentV1(
                CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                CaptureSolarRegime.Night,
                EnvironmentalObservationMatchStatus.Fresh,
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                new string('E', 64),
                true,
                new string('F', 64))
            : new CloudAssessmentEnvironmentV1(
                CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                CaptureSolarRegime.Night,
                EnvironmentalObservationMatchStatus.Missing,
                null,
                null,
                false);
        return new CloudAssessmentV1(
            CloudAssessmentV1.CurrentSchemaVersion,
            status,
            contaminated ? CloudAssessmentQuality.Unusable : CloudAssessmentQuality.Degraded,
            contaminated
                ? [CloudAssessmentReasonCodes.PrecipitationContamination]
                : [CloudAssessmentReasonCodes.EnvironmentMissing],
            contaminated ? null : 0,
            900_000,
            new CloudAssessmentGridV1(1, 1, 850_000, 1, 0, 4, 0),
            [new CloudAssessmentRegionV1(0, 0, 0, 0, 2, 2, 4, 4, 0, 1_000_000, false)],
            includeMask
                ? new CloudAssessmentMaskV1(CloudAssessmentMaskV1.RowMajorLsbFirst, 1, 1, new byte[] { 0 })
                : null,
            new CloudAssessmentSourceV1(
                raw.Descriptor.Artifact.ArtifactId,
                FrameArtifactRole.Raw,
                raw.Descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(raw.Descriptor.Artifact.Recipe).IdentitySha256),
            new CloudAssessmentSourceV1(
                Guid.Parse("30000000-0000-0000-0000-000000000002"),
                FrameArtifactRole.Calibrated,
                "clear",
                new string('A', 64)),
            new CloudAssessmentCalibrationV1(0, ushort.MaxValue, ushort.MaxValue, "calibration", "mask", "sensor", "processing"),
            environment,
            recipeIdentity,
            [new ProcessingAlgorithmIdentity("cloud", "v1")]);
    }

    [TestMethod]
    public async Task GetCalendarAsync_CountsCapturesPerObservingDayUnderTheDeploymentTimeZone()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        // Phoenix (UTC-7): 19:00Z on 3 September is local noon, the start of observing day 2026-09-03.
        var lateNight = await fixture.AddRawAsync(new DateTimeOffset(2026, 9, 4, 2, 0, 0, TimeSpan.Zero), "Physical", null).ConfigureAwait(false);
        // Half a second before local noon: counted for the 3rd and reachable from its day link.
        var beforeNoon = await fixture.AddRawAsync(new DateTimeOffset(2026, 9, 4, 18, 59, 59, 500, TimeSpan.Zero), "Physical", null).ConfigureAwait(false);
        var afterNoon = await fixture.AddRawAsync(new DateTimeOffset(2026, 9, 4, 19, 30, 0, TimeSpan.Zero), "Physical", null).ConfigureAwait(false);
        var archive = fixture.OpenArchive(ObservingDayCalendar.Create("America/Phoenix"));

        var calendar = await archive.GetCalendarAsync(
            new CameraAgentGalleryCalendarQuery(new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 4)), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("America/Phoenix", calendar.TimeZoneId);
        Assert.IsFalse(calendar.TimeZoneFallback);
        Assert.AreEqual(3, calendar.Days.Count);
        Assert.AreEqual(0, calendar.Days[0].CaptureCount);
        Assert.IsNull(calendar.Days[0].FirstExposureUtc);
        Assert.AreEqual(new DateOnly(2026, 9, 3), calendar.Days[1].Day.Date);
        Assert.AreEqual(2, calendar.Days[1].CaptureCount);
        Assert.AreEqual(lateNight.Descriptor.Timing.ExposureStartedUtc, calendar.Days[1].FirstExposureUtc);
        Assert.AreEqual(beforeNoon.Descriptor.Timing.ExposureStartedUtc, calendar.Days[1].LastExposureUtc);
        Assert.AreEqual(1, calendar.Days[2].CaptureCount);
        Assert.AreEqual(afterNoon.Descriptor.Timing.ExposureStartedUtc, calendar.Days[2].FirstExposureUtc);
        Assert.IsTrue(calendar.Days.All(static day => day.CandidateCount == 0));

        var totals = await Task.WhenAll(calendar.Days.Select(async day =>
        {
            var page = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(FromUtc: day.Day.StartUtc, ToUtc: day.Day.EndUtc.AddMilliseconds(-1)), CancellationToken.None).ConfigureAwait(false);
            return (long)page.Items.Count;
        })).ConfigureAwait(false);
        CollectionAssert.AreEqual(calendar.Days.Select(static day => day.CaptureCount).ToArray(), totals);
    }

    [TestMethod]
    public async Task GetCalendarAsync_AppliesFiltersWithinEachDayAndBoundsTheRange()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var kept = await fixture.AddRawAsync(new DateTimeOffset(2026, 9, 4, 2, 0, 0, TimeSpan.Zero), "Physical", null).ConfigureAwait(false);
        var quarantined = await fixture.AddRawAsync(new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero), "Physical", null).ConfigureAwait(false);
        await fixture.SetRawStateAsync(quarantined.Descriptor.Capture.CaptureId, "quarantined").ConfigureAwait(false);
        var archive = fixture.OpenArchive(ObservingDayCalendar.Create("America/Phoenix"));

        var filtered = await archive.GetCalendarAsync(
            new CameraAgentGalleryCalendarQuery(
                new DateOnly(2026, 9, 3),
                new DateOnly(2026, 9, 3),
                new CameraAgentGalleryQuery(RawState: "quarantined", FromUtc: DateTimeOffset.UnixEpoch, ToUtc: DateTimeOffset.UnixEpoch)),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, filtered.Days.Count);
        Assert.AreEqual(1, filtered.Days[0].CaptureCount);
        Assert.AreEqual(quarantined.Descriptor.Timing.ExposureStartedUtc, filtered.Days[0].FirstExposureUtc);
        Assert.AreNotEqual(kept.Descriptor.Timing.ExposureStartedUtc, filtered.Days[0].FirstExposureUtc);
        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () => await archive.GetCalendarAsync(
            new CameraAgentGalleryCalendarQuery(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1).AddDays(ObservingDayCalendar.MaximumRangeDays)),
            CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () => await archive.GetCalendarAsync(
            new CameraAgentGalleryCalendarQuery(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1)),
            CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task GetCalendarAsync_ReportsTheUtcFallbackWhenNoTimeZoneIsConfigured()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        await fixture.AddRawAsync(new DateTimeOffset(2026, 9, 4, 2, 0, 0, TimeSpan.Zero), "Physical", null).ConfigureAwait(false);

        var calendar = await ((ICameraAgentArchive)fixture.Gallery).GetCalendarAsync(
            new CameraAgentGalleryCalendarQuery(new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 4)), CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(calendar.TimeZoneFallback);
        Assert.AreEqual(TimeZoneInfo.Utc.Id, calendar.TimeZoneId);
        // 02:00Z on 4 September precedes UTC noon, so it belongs to observing day 2026-09-03.
        Assert.AreEqual(1, calendar.Days[0].CaptureCount);
        Assert.AreEqual(0, calendar.Days[1].CaptureCount);
    }

    [TestMethod]
    public async Task GetNeighboursAsync_FollowsTheGalleryOrderUnderTheSameFilters()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var timestamp = Utc(1);
        var first = await fixture.AddRawAsync(timestamp, "Physical", null).ConfigureAwait(false);
        var second = await fixture.AddRawAsync(timestamp.AddMinutes(1), "Physical", null).ConfigureAwait(false);
        var third = await fixture.AddRawAsync(timestamp.AddMinutes(2), "Physical", null).ConfigureAwait(false);
        var archive = fixture.Gallery;
        var unfiltered = new CameraAgentGalleryQuery();

        var middle = await archive.GetNeighboursAsync(second.Descriptor.Capture.CaptureId, unfiltered, CancellationToken.None).ConfigureAwait(false);
        var newest = await archive.GetNeighboursAsync(third.Descriptor.Capture.CaptureId, unfiltered, CancellationToken.None).ConfigureAwait(false);
        var oldest = await archive.GetNeighboursAsync(first.Descriptor.Capture.CaptureId, unfiltered, CancellationToken.None).ConfigureAwait(false);
        var bounded = await archive.GetNeighboursAsync(
            second.Descriptor.Capture.CaptureId,
            new CameraAgentGalleryQuery(MaximumSequence: second.Descriptor.Capture.CaptureSequence),
            CancellationToken.None).ConfigureAwait(false);
        var missing = await archive.GetNeighboursAsync(Guid.NewGuid(), unfiltered, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(third.Descriptor.Capture.CaptureId, middle?.NewerCaptureId);
        Assert.AreEqual(first.Descriptor.Capture.CaptureId, middle?.OlderCaptureId);
        Assert.IsNull(newest?.NewerCaptureId);
        Assert.AreEqual(second.Descriptor.Capture.CaptureId, newest?.OlderCaptureId);
        Assert.AreEqual(second.Descriptor.Capture.CaptureId, oldest?.NewerCaptureId);
        Assert.IsNull(oldest?.OlderCaptureId);
        Assert.IsNull(bounded?.NewerCaptureId);
        Assert.AreEqual(first.Descriptor.Capture.CaptureId, bounded?.OlderCaptureId);
        Assert.IsNull(missing);
    }

    [TestMethod]
    public async Task GetProductPageAsync_PagesRetainedOutputsNewestFirstWithFilters()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var older = await fixture.AddRawAsync(Utc(1), "Physical", null).ConfigureAwait(false);
        var newer = await fixture.AddRawAsync(Utc(1).AddHours(1), "Physical", null).ConfigureAwait(false);
        var olderPreview = await fixture.AddProcessingOutputAsync(older, "Preview", DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        var newerPreview = await fixture.AddProcessingOutputAsync(newer, "Preview", DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        var newerCombined = await fixture.AddProcessingOutputAsync(newer, "Combined", DurableProcessingNodeStatus.Completed, role: FrameArtifactRole.Combined).ConfigureAwait(false);
        var archive = fixture.Gallery;

        var first = await archive.GetProductPageAsync(new CameraAgentProductQuery(PageSize: 2), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, first.Items.Count);
        Assert.IsNotNull(first.NextCursor);
        CollectionAssert.AreEquivalent(
            new[] { newerPreview.Artifact.ArtifactId, newerCombined.Artifact.ArtifactId },
            first.Items.Select(static item => item.ArtifactId).ToArray());
        Assert.IsTrue(first.Items.All(item => item.CaptureId == newer.Descriptor.Capture.CaptureId));
        Assert.IsTrue(first.Items.All(static item => item.ExecutionClass == "Unassociated"));
        Assert.IsTrue(first.Items.All(static item => !item.IsMaterialization && item.SourceCount == 1 && item.Availability == "Available"));

        var second = await archive.GetProductPageAsync(new CameraAgentProductQuery(PageSize: 2, Cursor: first.NextCursor), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, second.Items.Count);
        Assert.AreEqual(olderPreview.Artifact.ArtifactId, second.Items[0].ArtifactId);
        Assert.IsNull(second.NextCursor);
        Assert.IsTrue(first.Items.Min(static item => item.CommittedUtc) >= second.Items[0].CommittedUtc);

        var combined = await archive.GetProductPageAsync(new CameraAgentProductQuery(Role: FrameArtifactRole.Combined), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, combined.Items.Count);
        Assert.AreEqual(newerCombined.Artifact.ArtifactId, combined.Items[0].ArtifactId);
        Assert.AreEqual(FrameArtifactRole.Combined, combined.Items[0].Role);

        var byRecipe = await archive.GetProductPageAsync(new CameraAgentProductQuery(Recipe: combined.Items[0].Recipe.IdentitySha256), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3, byRecipe.Items.Count);

        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () => await archive.GetProductPageAsync(
            new CameraAgentProductQuery(Cursor: first.NextCursor, Role: FrameArtifactRole.Combined), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () => await archive.GetProductPageAsync(
            new CameraAgentProductQuery(ProductKind: "Timelapse"), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var unsupported = await archive.GetProductPageAsync(new CameraAgentProductQuery(Role: FrameArtifactRole.Metadata), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, unsupported.Items.Count);
        Assert.IsNull(unsupported.NextCursor);
    }

    [TestMethod]
    public async Task GetProductPageAsync_ListsAvailableProductsByDefaultAndUnavailableOnRequest()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(Utc(1), "Physical", null).ConfigureAwait(false);
        var kept = await fixture.AddProcessingOutputAsync(raw, "Preview", DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        var missing = await fixture.AddProcessingOutputAsync(raw, "Combined", DurableProcessingNodeStatus.Completed, role: FrameArtifactRole.Combined).ConfigureAwait(false);
        await fixture.SetOutputAvailabilityAsync(missing.Artifact.ArtifactId, "Missing").ConfigureAwait(false);
        var archive = fixture.Gallery;

        var defaults = await archive.GetProductPageAsync(new CameraAgentProductQuery(), CancellationToken.None).ConfigureAwait(false);
        var explicitAvailable = await archive.GetProductPageAsync(new CameraAgentProductQuery(Availability: "Available"), CancellationToken.None).ConfigureAwait(false);
        var missingOnly = await archive.GetProductPageAsync(new CameraAgentProductQuery(Availability: "Missing"), CancellationToken.None).ConfigureAwait(false);
        var quarantined = await archive.GetProductPageAsync(new CameraAgentProductQuery(Availability: "Quarantined"), CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { kept.Artifact.ArtifactId }, defaults.Items.Select(static item => item.ArtifactId).ToArray());
        CollectionAssert.AreEqual(new[] { kept.Artifact.ArtifactId }, explicitAvailable.Items.Select(static item => item.ArtifactId).ToArray());
        CollectionAssert.AreEqual(new[] { missing.Artifact.ArtifactId }, missingOnly.Items.Select(static item => item.ArtifactId).ToArray());
        Assert.AreEqual("Missing", missingOnly.Items[0].Availability);
        Assert.AreEqual("test-disposition", missingOnly.Items[0].AvailabilityReason);
        Assert.AreEqual(0, quarantined.Items.Count);
        // Filter values match canonical spellings case-insensitively.
        var lowercase = await archive.GetProductPageAsync(new CameraAgentProductQuery(Availability: " missing "), CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { missing.Artifact.ArtifactId }, lowercase.Items.Select(static item => item.ArtifactId).ToArray());
        Assert.AreEqual("Missing", lowercase.Items[0].Availability);
        // A cursor issued under one spelling continues under another because the hash covers the canonical value.
        var alsoMissing = await fixture.AddProcessingOutputAsync(raw, "Aligned", DurableProcessingNodeStatus.Completed).ConfigureAwait(false);
        await fixture.SetOutputAvailabilityAsync(alsoMissing.Artifact.ArtifactId, "Missing").ConfigureAwait(false);
        var firstMissingPage = await archive.GetProductPageAsync(new CameraAgentProductQuery(Availability: "Missing", PageSize: 1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(firstMissingPage.NextCursor);
        var secondMissingPage = await archive.GetProductPageAsync(new CameraAgentProductQuery(Availability: "MISSING", PageSize: 1, Cursor: firstMissingPage.NextCursor), CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, secondMissingPage.Items);
        Assert.AreNotEqual(firstMissingPage.Items[0].ArtifactId, secondMissingPage.Items[0].ArtifactId);
        Assert.IsNull(secondMissingPage.NextCursor);
        // These fixture outputs record no product kind, so a canonicalised kind filter is accepted and simply matches nothing.
        var lowercaseKind = await archive.GetProductPageAsync(new CameraAgentProductQuery(ProductKind: " pixeldata "), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, lowercaseKind.Items.Count);
        await Assert.ThrowsExactlyAsync<CameraAgentGalleryQueryException>(async () => await archive.GetProductPageAsync(
            new CameraAgentProductQuery(Availability: "gone"), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        // The detail lookup still resolves an unavailable product.
        Assert.IsNotNull(await archive.GetProductAsync(missing.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task GetProductAsync_ResolvesSourcesNodeAndObservingDay()
    {
        using var fixture = await GalleryFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync(new DateTimeOffset(2026, 9, 4, 2, 0, 0, TimeSpan.Zero), "Physical", null).ConfigureAwait(false);
        var preview = await fixture.AddProcessingOutputAsync(raw, "Preview", DurableProcessingNodeStatus.Completed, encodedWidth: 2, encodedHeight: 2).ConfigureAwait(false);
        var unretained = Guid.NewGuid();
        await fixture.AddOutputSourcesAsync(preview.Artifact.ArtifactId, raw.Descriptor.Artifact.ArtifactId, unretained).ConfigureAwait(false);
        var archive = fixture.OpenArchive(ObservingDayCalendar.Create("America/Phoenix"));

        var detail = await archive.GetProductAsync(preview.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(detail);
        Assert.AreEqual(preview.Artifact.ArtifactId, detail.Product.ArtifactId);
        Assert.AreEqual(raw.Descriptor.Capture.CaptureId, detail.Product.CaptureId);
        Assert.AreEqual("rig-gallery", detail.RigId);
        Assert.AreEqual(raw.Descriptor.Timing.ExposureStartedUtc, detail.CaptureExposureStartedUtc);
        Assert.AreEqual(new DateOnly(2026, 9, 3), detail.ObservingDay.Date);
        Assert.AreEqual(2, detail.Product.EncodedWidth);
        Assert.IsNull(detail.Product.ProductKind);
        Assert.AreEqual("preview", detail.Node?.NodeId);
        Assert.AreEqual("Completed", detail.Node?.Status);
        Assert.AreEqual(2, detail.Node?.Attempt);
        Assert.AreEqual(0, detail.Predecessors.Count);
        Assert.IsFalse(detail.SourcesTruncated);
        Assert.AreEqual(2, detail.Sources.Count);
        Assert.AreEqual(0, detail.Sources[0].Ordinal);
        Assert.AreEqual(raw.Descriptor.Artifact.ArtifactId, detail.Sources[0].ArtifactId);
        Assert.AreEqual(FrameArtifactRole.Raw, detail.Sources[0].Role);
        Assert.AreEqual(raw.Descriptor.Capture.CaptureId, detail.Sources[0].CaptureId);
        Assert.AreEqual(raw.Descriptor.Capture.CaptureSequence, detail.Sources[0].CaptureSequence);
        Assert.AreEqual(1, detail.Sources[1].Ordinal);
        Assert.AreEqual(unretained, detail.Sources[1].ArtifactId);
        Assert.IsNull(detail.Sources[1].CaptureId);
        Assert.IsNull(detail.Sources[1].Role);
        Assert.IsNull(await archive.GetProductAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(await archive.GetProductAsync(raw.Descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false));
    }

    private sealed class GalleryFixture : IDisposable
    {
        private readonly SqliteRawCaptureJournal _journal;
        private readonly SqliteCaptureProcessingStore _processingStore;

        private GalleryFixture(
            string root,
            SqliteRawCaptureJournal journal,
            SqliteCaptureProcessingStore processingStore,
            SqliteCameraAgentGallery gallery)
        {
            Root = root;
            _journal = journal;
            _processingStore = processingStore;
            Gallery = gallery;
        }

        internal string Root { get; }

        internal string? SecondaryRoot { get; private init; }

        internal SqliteCameraAgentGallery Gallery { get; }

        internal static async Task<GalleryFixture> CreateAsync(bool twoStorageRoots = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-gallery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var options = CreateOptions(root);
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var processingStore = new SqliteCaptureProcessingStore(options);
            await processingStore.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var secondaryRoot = twoStorageRoots ? Path.Combine(root, "secondary-storage") : null;
            if (secondaryRoot is not null)
            {
                Directory.CreateDirectory(secondaryRoot);
            }
            ICameraAgentStorageResolver? resolver = secondaryRoot is null
                ? null
                : new FixedStorageResolver([
                    new("raw-ingress", root),
                    new("storage-1", secondaryRoot)
                ]);
            return new GalleryFixture(
                root,
                journal,
                processingStore,
                new SqliteCameraAgentGallery(options, processingStore, resolver))
            {
                SecondaryRoot = secondaryRoot
            };
        }

        internal async Task AddOutputSourcesAsync(Guid outputArtifactId, params Guid[] sourceArtifactIds)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            for (var ordinal = 0; ordinal < sourceArtifactIds.Length; ordinal++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO processing_output_sources(output_identity_sha256, source_ordinal, source_artifact_id)
                    SELECT output_identity_sha256, $ordinal, $source FROM processing_outputs WHERE artifact_id = $artifact;
                    """;
                command.Parameters.AddWithValue("$ordinal", ordinal);
                command.Parameters.AddWithValue("$source", sourceArtifactIds[ordinal].ToString("N"));
                command.Parameters.AddWithValue("$artifact", outputArtifactId.ToString("N"));
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        internal async Task SetOutputAvailabilityAsync(Guid artifactId, string state)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE processing_outputs
                SET availability_state = $state, availability_reason = 'test-disposition', unavailable_unix_ms = 1
                WHERE artifact_id = $artifact;
                """;
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        internal SqliteCameraAgentGallery OpenArchive(ObservingDayCalendar calendar)
            => new(CreateOptions(Root), _processingStore, null, new FixedObservingDayCalendarProvider(calendar));

        internal RestartedGallery OpenRestartedGallery()
        {
            var options = CreateOptions(Root);
            var processingStore = new SqliteCaptureProcessingStore(options);
            return new(processingStore, new SqliteCameraAgentGallery(options, processingStore));
        }

        internal async Task<ArtifactManifestV2> AddRawAsync(
            DateTimeOffset exposureStartedUtc,
            string sourceId,
            SceneProvenance? scene)
        {
            var payload = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
            var captureId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var identity = await _journal.ReserveIdentityAsync(
                "agent-gallery", captureId, artifactId, CancellationToken.None).ConfigureAwait(false);
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload);
            var timing = new CaptureTimingDescriptor(
                exposureStartedUtc.AddSeconds(-1),
                exposureStartedUtc,
                exposureStartedUtc.AddSeconds(1),
                exposureStartedUtc.AddSeconds(2),
                exposureStartedUtc.AddSeconds(3));
            var descriptor = template.Descriptor with
            {
                Capture = new CaptureIdentityDescriptor(
                    identity.AgentId, "rig-gallery", identity.CaptureSequence, identity.CaptureId),
                Timing = timing,
                Artifact = template.Descriptor.Artifact with
                {
                    ArtifactId = identity.ArtifactId,
                    SourceId = sourceId,
                    CreatedUtc = timing.ReadoutCompletedUtc
                }
            };
            var relativePath = $"frames/{artifactId:N}.bin";
            var manifest = new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath, scene);
            var manifestJson = CaptureContractJson.Serialize(manifest);
            await _journal.CommitAsync(new RawIngressJournalEntry(
                identity.AgentId,
                identity.CaptureSequence,
                identity.CaptureId,
                identity.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor),
                CaptureContractJson.ComputeManifestSha256(manifestJson),
                descriptor.Artifact.ChecksumSha256,
                descriptor.Layout.ByteLength,
                relativePath,
                $"frames/{artifactId:N}.json",
                manifestJson,
                timing.ExposureStartedUtc,
                timing.DurableIngressUtc,
                EvidenceOrigin: GalleryEvidenceClassifier.Classify(manifest)), CancellationToken.None).ConfigureAwait(false);
            return manifest;
        }

        internal async Task<ReconstructionDescriptor> AddProcessingOutputAsync(
            ArtifactManifestV2 raw,
            string nodeId,
            DurableProcessingNodeStatus status,
            bool required = true,
            IReadOnlyList<string>? dependencies = null,
            string reason = "sensitive processing failure",
            int? encodedWidth = null,
            int? encodedHeight = null,
            FrameArtifactRole role = FrameArtifactRole.Preview,
            string? mediaType = null)
        {
            var recipe = RecipeIdentityDescriptor.Create(
                "preview-recipe",
                "1.2.3",
                "preview-build",
                JsonSerializer.SerializeToElement(new { stretch = "linear" }));
            var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256;
            var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
                role, nodeId, recipeIdentity, [raw.Descriptor.Artifact.ArtifactId]);
            var artifact = new ArtifactDescriptor(
                ProcessingIdentity.CreateArtifactId(outputIdentity),
                role,
                nodeId,
                nodeId,
                raw.Descriptor.Timing.ReadoutCompletedUtc,
                [raw.Descriptor.Artifact.ArtifactId],
                recipe,
                mediaType ?? (encodedWidth is null ? "image/png" : "image/jpeg"),
                new string('B', 64));
            var descriptor = raw.Descriptor with { Artifact = artifact };
            var relativePath = $"products/{artifact.ArtifactId:N}.{(encodedWidth is null ? "png" : "jpg")}";
            byte[] evidence;
            if (encodedWidth is not null && encodedHeight is not null)
            {
                evidence = DurableProcessingProductManifestJson.Serialize(new DurableEncodedProductManifestV2(
                    DurableEncodedProductManifestV2.CurrentSchemaVersion,
                    raw.Descriptor.Capture,
                    artifact,
                    outputIdentity,
                    [new ProcessingAlgorithmIdentity("preview", "v1")],
                    new ProcessingCompatibilityIdentity(
                        "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing"),
                    TimeSpan.FromSeconds(1).Ticks,
                    4,
                    relativePath,
                    null,
                    encodedWidth.Value,
                    encodedHeight.Value,
                    CameraPixelFormat.Mono8,
                    nodeId));
            }
            else
            {
                evidence = CaptureContractJson.Serialize(new ArtifactManifestV2(
                    ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath));
            }
            var compatibility = new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing");
            var persistedNodeId = string.Equals(nodeId, "Preview", StringComparison.OrdinalIgnoreCase)
                ? "preview"
                : nodeId;
            using var connection = await OpenAsync().ConfigureAwait(false);
            using (var node = connection.CreateCommand())
            {
                node.CommandText = """
                    INSERT INTO processing_nodes(
                        capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                        output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
                     VALUES ($capture, $node, $required, $dependencies, 'preview-recipe', $role, $variant, $plan,
                            $status, $reason, 2, $completed);
                    """;
                node.Parameters.AddWithValue("$capture", raw.Descriptor.Capture.CaptureId.ToString("N"));
                node.Parameters.AddWithValue("$node", persistedNodeId);
                node.Parameters.AddWithValue("$variant", nodeId);
                node.Parameters.AddWithValue("$role", role.ToString());
                node.Parameters.AddWithValue("$required", required ? 1 : 0);
                node.Parameters.AddWithValue("$dependencies", JsonSerializer.Serialize(dependencies ?? [], WebJson));
                node.Parameters.AddWithValue("$plan", new string('C', 64));
                node.Parameters.AddWithValue("$status", status.ToString());
                node.Parameters.AddWithValue("$reason", reason);
                node.Parameters.AddWithValue("$completed", raw.Descriptor.Timing.DurableIngressUtc.ToUnixTimeMilliseconds());
                await node.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var output = connection.CreateCommand())
            {
                output.CommandText = """
                    INSERT INTO processing_outputs(
                        output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                        payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                        algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                        committed_unix_ms)
                     VALUES ($identity, $capture, $agent, $node, $artifact, $role, $variant,
                            $payload, $sidecar, $descriptor, $recipe,
                            $algorithms, $compatibility, $integration, $sequence, $committed);
                    """;
                output.Parameters.AddWithValue("$identity", outputIdentity);
                output.Parameters.AddWithValue("$capture", raw.Descriptor.Capture.CaptureId.ToString("N"));
                output.Parameters.AddWithValue("$agent", raw.Descriptor.Capture.AgentId);
                output.Parameters.AddWithValue("$node", persistedNodeId);
                output.Parameters.AddWithValue("$artifact", artifact.ArtifactId.ToString("N"));
                output.Parameters.AddWithValue("$variant", nodeId);
                output.Parameters.AddWithValue("$role", role.ToString());
                output.Parameters.AddWithValue("$payload", relativePath);
                output.Parameters.AddWithValue("$sidecar", $"{relativePath}.json");
                output.Parameters.AddWithValue("$descriptor", evidence);
                output.Parameters.AddWithValue("$recipe", recipeIdentity);
                output.Parameters.AddWithValue("$algorithms", JsonSerializer.SerializeToUtf8Bytes(
                    new[] { new ProcessingAlgorithmIdentity("preview", "v1") }, WebJson));
                output.Parameters.AddWithValue("$compatibility", JsonSerializer.SerializeToUtf8Bytes(compatibility, WebJson));
                output.Parameters.AddWithValue("$integration", TimeSpan.FromSeconds(1).Ticks);
                output.Parameters.AddWithValue("$sequence", raw.Descriptor.Capture.CaptureSequence);
                output.Parameters.AddWithValue("$committed", raw.Descriptor.Timing.DurableIngressUtc.ToUnixTimeMilliseconds());
                await output.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            return descriptor;
        }

        private static IOptions<CameraAgentHostOptions> CreateOptions(string root)
            => Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });

        internal sealed class RestartedGallery(
            SqliteCaptureProcessingStore processingStore,
            SqliteCameraAgentGallery gallery) : IDisposable
        {
            internal SqliteCameraAgentGallery Gallery { get; } = gallery;

            public void Dispose()
            {
                processingStore.Dispose();
                SqliteConnection.ClearAllPools();
            }
        }

        internal async Task AddCloudAssessmentAsync(
            ArtifactManifestV2 raw,
            CloudAssessmentV1 assessment,
            string? productSchemaVersion = null,
            string nodeId = "cloud")
        {
            var payload = CloudAssessmentJson.Serialize(assessment);
            var recipe = RecipeIdentityDescriptor.Create(
                BuiltInProcessingRecipes.CloudAssessment,
                "1.0.0",
                "cloud-build",
                JsonSerializer.SerializeToElement(new { }));
            var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256;
            var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Metadata,
                "cloud-assessment-v1",
                recipeIdentity,
                [raw.Descriptor.Artifact.ArtifactId]);
            var artifact = new ArtifactDescriptor(
                ProcessingIdentity.CreateArtifactId(outputIdentity),
                FrameArtifactRole.Metadata,
                "cloud",
                "cloud-assessment-v1",
                raw.Descriptor.Timing.ReadoutCompletedUtc,
                [raw.Descriptor.Artifact.ArtifactId],
                recipe,
                "application/json",
                PayloadChecksum.ComputeSha256(payload));
            var algorithms = new[] { new ProcessingAlgorithmIdentity("cloud", "v1") };
            var compatibility = new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing");
            var relativePath = $"products/{artifact.ArtifactId:N}.json";
            using var nullDocument = JsonDocument.Parse("null");
            IDurableProcessingProductManifest manifest = productSchemaVersion is null
                ? new DurableProcessingProductManifestV1(
                    DurableProcessingProductManifestV1.CurrentSchemaVersion,
                    raw.Descriptor.Capture,
                    artifact,
                    outputIdentity,
                    algorithms,
                    compatibility,
                    TimeSpan.FromSeconds(1).Ticks,
                    payload.LongLength,
                    relativePath,
                    nullDocument.RootElement.Clone())
                : new DurableTypedMetadataProductManifestV3(
                    DurableTypedMetadataProductManifestV3.CurrentSchemaVersion,
                    raw.Descriptor.Capture,
                    artifact,
                    outputIdentity,
                    algorithms,
                    compatibility,
                    TimeSpan.FromSeconds(1).Ticks,
                    payload.LongLength,
                    relativePath,
                    nullDocument.RootElement.Clone(),
                    ProcessingProductKind.Metadata,
                    productSchemaVersion,
                    new string('D', 64));
            var evidence = DurableProcessingProductManifestJson.Serialize(manifest);
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, payload).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.ChangeExtension(path, ".manifest.json"), evidence).ConfigureAwait(false);

            using var connection = await OpenAsync().ConfigureAwait(false);
            using (var node = connection.CreateCommand())
            {
                node.CommandText = """
                    INSERT INTO processing_nodes(
                        capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                        output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
                    VALUES ($capture, $node, 0, '[]', $recipe_name, 'Metadata', 'cloud-assessment-v1',
                            $plan, 'Completed', NULL, 1, $completed);
                    """;
                node.Parameters.AddWithValue("$capture", raw.Descriptor.Capture.CaptureId.ToString("N"));
                node.Parameters.AddWithValue("$node", nodeId);
                node.Parameters.AddWithValue("$recipe_name", BuiltInProcessingRecipes.CloudAssessment);
                node.Parameters.AddWithValue("$plan", new string('C', 64));
                node.Parameters.AddWithValue("$completed", raw.Descriptor.Timing.DurableIngressUtc.ToUnixTimeMilliseconds());
                await node.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using (var output = connection.CreateCommand())
            {
                output.CommandText = """
                    INSERT INTO processing_outputs(
                        output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                        payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                        algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                            committed_unix_ms, product_kind,
                            product_schema_version, content_identity_sha256)
                    VALUES ($identity, $capture, $agent, $node, $artifact, 'Metadata', 'cloud-assessment-v1',
                            $payload, $sidecar, $descriptor, $recipe, $algorithms, $compatibility,
                            $integration, $sequence, $committed, $kind, $schema, $content);
                    """;
                output.Parameters.AddWithValue("$identity", outputIdentity);
                output.Parameters.AddWithValue("$capture", raw.Descriptor.Capture.CaptureId.ToString("N"));
                output.Parameters.AddWithValue("$agent", raw.Descriptor.Capture.AgentId);
                output.Parameters.AddWithValue("$node", nodeId);
                output.Parameters.AddWithValue("$artifact", artifact.ArtifactId.ToString("N"));
                output.Parameters.AddWithValue("$payload", relativePath);
                output.Parameters.AddWithValue("$sidecar", Path.ChangeExtension(relativePath, ".manifest.json"));
                output.Parameters.AddWithValue("$descriptor", evidence);
                output.Parameters.AddWithValue("$recipe", recipeIdentity);
                output.Parameters.AddWithValue("$algorithms", JsonSerializer.SerializeToUtf8Bytes(algorithms, WebJson));
                output.Parameters.AddWithValue("$compatibility", JsonSerializer.SerializeToUtf8Bytes(compatibility, WebJson));
                output.Parameters.AddWithValue("$integration", manifest.TotalIntegrationTicks);
                output.Parameters.AddWithValue("$sequence", raw.Descriptor.Capture.CaptureSequence);
                output.Parameters.AddWithValue("$committed", raw.Descriptor.Timing.DurableIngressUtc.ToUnixTimeMilliseconds());
                output.Parameters.AddWithValue("$kind", productSchemaVersion is null ? DBNull.Value : nameof(ProcessingProductKind.Metadata));
                output.Parameters.AddWithValue("$schema", (object?)productSchemaVersion ?? DBNull.Value);
                output.Parameters.AddWithValue("$content", productSchemaVersion is null ? DBNull.Value : new string('D', 64));
                await output.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        internal async Task SetDeliveryStatusesAsync(Guid artifactId, IReadOnlyList<string> statuses)
            => await SetDeliveryStatusesAsync(Root, artifactId, statuses).ConfigureAwait(false);

        internal static async Task SetDeliveryStatusesAsync(
            string storageRoot,
            Guid artifactId,
            IReadOnlyList<string> statuses)
        {
            var directory = Path.Combine(storageRoot, "outbox");
            Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "artifact-outbox.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE artifact_outbox_records(record_id INTEGER PRIMARY KEY, artifact_id TEXT, status TEXT);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            for (var index = 0; index < statuses.Count; index++)
            {
                command.CommandText = "INSERT INTO artifact_outbox_records(record_id, artifact_id, status) VALUES($id, $artifact, $status);";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", index + 1);
                command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
                command.Parameters.AddWithValue("$status", statuses[index]);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        private sealed class FixedStorageResolver(IReadOnlyList<CameraAgentStorageLocation> locations)
            : ICameraAgentStorageResolver
        {
            public ValueTask<IReadOnlyList<CameraAgentStorageLocation>> GetUploadLocationsAsync(
                CancellationToken cancellationToken) => ValueTask.FromResult(locations);

            public ValueTask<CameraAgentStorageLocation?> ResolveAliasAsync(
                string storageAlias,
                CancellationToken cancellationToken) => ValueTask.FromResult(
                    locations.SingleOrDefault(location => location.Alias == storageAlias));
        }

        internal async Task SetRawStateAsync(Guid captureId, string state)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE raw_captures SET state = $state WHERE capture_id = $capture;";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed test statements are supplied by this fixture.")]
        internal async Task ExecuteAsync(string sql)
        {
            using var connection = await OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private async Task<SqliteConnection> OpenAsync()
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            return connection;
        }

        public void Dispose()
        {
            _processingStore.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
