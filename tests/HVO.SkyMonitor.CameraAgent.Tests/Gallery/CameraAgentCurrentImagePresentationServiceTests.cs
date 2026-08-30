using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCurrentImagePresentationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ProjectsFixedTruthfulSlotsAndDeterministicDisplayVariantAsync()
    {
        var rawId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var previewId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var finalId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var thumbnailId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var calibratedId = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var capture = Capture(
            2,
            Now.AddSeconds(-30),
            [
                Artifact(finalId, FrameArtifactRole.AnnotatedPreview, "annotated-final-jpeg", "image/jpeg"),
                Artifact(previewId, FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image"),
                Artifact(thumbnailId, FrameArtifactRole.AnnotatedPreview, "annotated-thumbnail-1024-jpeg", "image/jpeg"),
                Artifact(calibratedId, FrameArtifactRole.Calibrated, "linear", "application/x-hvo-linear-frame"),
                Artifact(rawId, FrameArtifactRole.Raw, "camera-native", "application/x-skymonitor-mono16", sources: [])
            ]);
        var gallery = new StubGallery(new CameraAgentGalleryPage([capture], null));
        var service = CreateService(gallery, structuredLayersAvailable: true);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentPresentationImageFreshness.Current, result.ImageFreshness);
        Assert.AreEqual(CameraAgentPresentationStage.Annotated, result.SelectedStage);
        Assert.HasCount(5, result.Stages);
        var annotated = result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreEqual(thumbnailId, annotated.ArtifactId);
        Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, annotated.ArtifactRole);
        Assert.AreEqual("Processed", annotated.Label);
        Assert.AreEqual(
            $"/api/v1/operations/artifacts/{thumbnailId:D}/preview",
            annotated.PreviewUrl!.OriginalString);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available,
            result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Calibrated).Availability);
        Assert.AreEqual(CameraAgentCurrentImagePresentationService.MaximumCandidateCaptures, gallery.LastQuery!.PageSize);
        Assert.IsTrue(result.StructuredLayersAvailable);

        var json = JsonSerializer.Serialize(result);
        Assert.IsFalse(json.Contains("checksum", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("relativePath", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("processingNodes", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("queue", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task UnsupportedNewestCaptureFallsBackToLatestDisplayableCaptureAsync()
    {
        var latest = Capture(
            3,
            Now.AddSeconds(-5),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Raw, "camera-native", "application/octet-stream", sources: [])]);
        var prior = Capture(
            2,
            Now.AddMinutes(-1),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")]);
        var service = CreateService(new StubGallery(new CameraAgentGalleryPage([latest, prior], null)));

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(latest.CaptureId, result.LatestCapture!.CaptureId);
        Assert.AreEqual(prior.CaptureId, result.DisplayCapture!.CaptureId);
        Assert.IsTrue(result.IsHistoricalFallback);
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Historical, result.ImageFreshness);
        Assert.AreEqual(CameraAgentPresentationStage.Preview, result.SelectedStage);
    }

    [TestMethod]
    public async Task FreshnessAndSystemStateRemainIndependentAsync()
    {
        var capture = Capture(
            1,
            Now.AddMinutes(-3),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Raw, "camera-native", "application/x-skymonitor-mono16", sources: [])]);
        var page = new CameraAgentGalleryPage([capture], null);

        var delayed = await CreateService(
            new StubGallery(page),
            CameraAgentPresentationSystemState.Capturing,
            TimeSpan.FromMinutes(1)).GetAsync(CancellationToken.None).ConfigureAwait(false);
        var standby = await CreateService(
            new StubGallery(page),
            CameraAgentPresentationSystemState.Standby,
            TimeSpan.FromMinutes(1)).GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentPresentationImageFreshness.Delayed, delayed.ImageFreshness);
        Assert.AreEqual(CameraAgentPresentationSystemState.Capturing, delayed.System.State);
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Delayed, standby.ImageFreshness);
        Assert.AreEqual(CameraAgentPresentationSystemState.Standby, standby.System.State);
        Assert.AreEqual(delayed.DisplayCapture!.CaptureId, standby.DisplayCapture!.CaptureId);
        Assert.AreEqual(delayed.DisplayCapture.AgeSeconds, standby.DisplayCapture.AgeSeconds);

        var staleCapture = Capture(
            1,
            Now.AddMinutes(-7),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Raw, "camera-native", "application/x-skymonitor-mono16", sources: [])]);
        var stale = await CreateService(
            new StubGallery(new CameraAgentGalleryPage([staleCapture], null)),
            CameraAgentPresentationSystemState.Capturing,
            TimeSpan.FromMinutes(1)).GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Stale, stale.ImageFreshness);
    }

    [TestMethod]
    public async Task FollowsBoundedCursorsToFindLatestValidRetainedImageAsync()
    {
        var unsupported = Enumerable.Range(0, CameraAgentCurrentImagePresentationService.MaximumCandidateCaptures)
            .Select(index => Capture(
                100 - index,
                Now.AddSeconds(-index),
                [Artifact(Guid.NewGuid(), FrameArtifactRole.Raw, "camera-native", "application/octet-stream", sources: [])]))
            .ToArray();
        var retained = Capture(
            88,
            Now.AddMinutes(-5),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")]);
        var gallery = new PagedStubGallery(
            new CameraAgentGalleryPage(unsupported, "page-2"),
            new CameraAgentGalleryPage([retained], null));

        var result = await CreateService(gallery).GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(retained.CaptureId, result.DisplayCapture!.CaptureId);
        Assert.IsTrue(result.IsHistoricalFallback);
        Assert.AreEqual(2, gallery.ReadCount);
        Assert.IsFalse(result.HistoryBoundReached);
    }

    [TestMethod]
    public async Task DoesNotAdvertiseOversizedOrUnknownPreviewSourcesAsync()
    {
        var oversized = Capture(
            2,
            Now.AddSeconds(-5),
            [Artifact(
                Guid.NewGuid(),
                FrameArtifactRole.Preview,
                "oversized",
                "application/x-hvo-packed-image",
                byteLength: 65L * 1024 * 1024)]);
        var oversizedResult = await CreateService(new StubGallery(new CameraAgentGalleryPage([oversized], null)))
            .GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(oversizedResult.DisplayCapture);
        var oversizedSlot = oversizedResult.Stages.Single(static slot =>
            slot.Stage == CameraAgentPresentationStage.Preview);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Unavailable, oversizedSlot.Availability);
        Assert.AreEqual("PreviewBoundsExceeded", oversizedSlot.Reason);
        Assert.IsNull(oversizedSlot.PreviewUrl);

        var unknown = Capture(
            3,
            Now.AddSeconds(-4),
            [Artifact(
                Guid.NewGuid(),
                FrameArtifactRole.Raw,
                "camera-native",
                "application/x-skymonitor-unknown",
                sources: [])]);
        var unknownResult = await CreateService(new StubGallery(new CameraAgentGalleryPage([unknown], null)))
            .GetAsync(CancellationToken.None).ConfigureAwait(false);

        var rawSlot = unknownResult.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Raw);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Unsupported, rawSlot.Availability);
        Assert.AreEqual("UnsupportedMediaType", rawSlot.Reason);
        Assert.IsNull(rawSlot.PreviewUrl);

        var unsupportedLayout = Capture(
            4,
            Now.AddSeconds(-3),
            [Artifact(
                Guid.NewGuid(),
                FrameArtifactRole.Raw,
                "camera-native",
                "application/x-skymonitor-mono16",
                sources: [],
                reconstructionSupported: false)]);
        var layoutResult = await CreateService(new StubGallery(new CameraAgentGalleryPage([unsupportedLayout], null)))
            .GetAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(
            CameraAgentPresentationSlotAvailability.Unsupported,
            layoutResult.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Raw).Availability);
    }

    [TestMethod]
    public void FailedAndSkippedProcessingStagesRemainExplicit()
    {
        var capture = Capture(4, Now.AddSeconds(-3), []) with
        {
            ProcessingNodes =
            [
                new CameraAgentGalleryProcessingNode(
                    "preview", true, "TerminalFailure", "preview", FrameArtifactRole.Preview, "display", []),
                new CameraAgentGalleryProcessingNode(
                    "calibrate", false, "Skipped", "calibrate", FrameArtifactRole.Calibrated, "linear", [])
            ]
        };
        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()))
            .Project(capture);

        Assert.AreEqual(
            "ProcessingFailed",
            projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Preview).Reason);
        Assert.AreEqual(
            "ProcessingSkipped",
            projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Calibrated).Reason);
    }

    [TestMethod]
    public void SelectsAvailableStagesInDeterministicFallbackOrder()
    {
        var artifacts = new List<CameraAgentGalleryArtifact>
        {
            Artifact(Guid.NewGuid(), FrameArtifactRole.Raw, "camera-native", "application/x-skymonitor-mono16", sources: []),
            Artifact(Guid.NewGuid(), FrameArtifactRole.Calibrated, "linear", "application/x-hvo-linear-frame"),
            Artifact(Guid.NewGuid(), FrameArtifactRole.Combined, "stack", "application/x-hvo-packed-image"),
            Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image"),
            Artifact(Guid.NewGuid(), FrameArtifactRole.AnnotatedPreview, "annotated-thumbnail-1024-jpeg", "image/jpeg")
        };
        var projector = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()));
        CameraAgentPresentationStage[] expectedOrder =
        [
            CameraAgentPresentationStage.Annotated,
            CameraAgentPresentationStage.Preview,
            CameraAgentPresentationStage.Combined,
            CameraAgentPresentationStage.Calibrated,
            CameraAgentPresentationStage.Raw
        ];

        foreach (var expected in expectedOrder)
        {
            var projection = projector.Project(Capture(5, Now, artifacts));
            Assert.AreEqual(expected, projection.SelectedStage);
            artifacts.RemoveAll(artifact => artifact.Role == projection.Stages
                .Single(slot => slot.Stage == expected).ArtifactRole);
        }
    }

    [TestMethod]
    public void RuntimeProjectionUsesCameraAvailabilityAndScheduleWithoutChangingImageAge()
    {
        var admission = new HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureAdmissionSnapshot(
            HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureAdmissionState.Running,
            1,
            Now,
            true);

        var unavailable = CameraAgentPresentationRuntime.Project(
            admission, null, TimeSpan.FromMinutes(1), FleetAvailability.Unavailable, Now);
        var starting = CameraAgentPresentationRuntime.Project(
            admission, null, TimeSpan.FromMinutes(1), FleetAvailability.Initializing, Now);
        var pausedAdmission = admission with
        {
            State = HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureAdmissionState.Paused
        };
        var pausedUnavailable = CameraAgentPresentationRuntime.Project(
            pausedAdmission, null, TimeSpan.FromMinutes(1), FleetAvailability.Unavailable, Now);

        Assert.AreEqual(CameraAgentPresentationSystemState.Unavailable, unavailable.System.State);
        Assert.AreEqual(CameraAgentPresentationSystemState.Starting, starting.System.State);
        Assert.AreEqual(CameraAgentPresentationSystemState.Unavailable, pausedUnavailable.System.State);
        Assert.AreEqual(TimeSpan.FromMinutes(1), unavailable.ExpectedCaptureInterval.GetValueOrDefault());
    }

    [TestMethod]
    public async Task EmptyAndBoundedHistoryAreExplicitAndCancellationPropagatesAsync()
    {
        var empty = await CreateService(new StubGallery(new CameraAgentGalleryPage([], "more")))
            .GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentPresentationImageFreshness.Empty, empty.ImageFreshness);
        Assert.IsTrue(empty.HistoryBoundReached);
        Assert.IsTrue(empty.Stages.All(static slot =>
            slot.Availability == CameraAgentPresentationSlotAvailability.Missing));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var canceling = CreateService(new StubGallery(cancellationOnly: true));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await canceling.GetAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static CameraAgentCurrentImagePresentationService CreateService(
        ICameraAgentGallery gallery,
        CameraAgentPresentationSystemState systemState = CameraAgentPresentationSystemState.Capturing,
        TimeSpan? expectedInterval = null,
        bool structuredLayersAvailable = false)
        => new(
            gallery,
            new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions())),
            new StubRuntime(new CameraAgentPresentationRuntimeSnapshot(
                new CameraAgentPresentationSystemStatus(systemState, "Bounded state.", Now),
                expectedInterval)),
            new StubStructuredLayers(structuredLayersAvailable),
            new FixedTimeProvider(Now));

    private static CameraAgentGalleryCapture Capture(
        long sequence,
        DateTimeOffset exposureStartedUtc,
        IReadOnlyList<CameraAgentGalleryArtifact> artifacts)
        => new(
            Guid.NewGuid(),
            "agent-1",
            "rig-1",
            sequence,
            exposureStartedUtc,
            exposureStartedUtc.AddSeconds(1),
            "committed",
            GalleryEvidenceOrigin.Simulated,
            artifacts,
            [],
            CanonicalSceneAvailability: "Unavailable");

    private static CameraAgentGalleryArtifact Artifact(
        Guid id,
        FrameArtifactRole role,
        string variant,
        string mediaType,
        IReadOnlyList<Guid>? sources = null,
        long byteLength = 128,
        bool reconstructionSupported = true)
    {
        var encoded = string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase);
        var pixelFormat = mediaType.Contains("rgb24", StringComparison.OrdinalIgnoreCase)
            ? CameraPixelFormat.Rgb24
            : mediaType.Contains("mono8", StringComparison.OrdinalIgnoreCase)
                ? CameraPixelFormat.Mono8
                : CameraPixelFormat.Mono16;
        return new(
            id,
            role,
            "source",
            variant,
            Now.AddSeconds(-1),
            mediaType,
            new string('A', 64),
            byteLength,
            new CameraAgentGalleryRecipe("recipe", "1.0.0", "recipe-v1", new string('B', 64), new string('C', 64)),
            sources ?? [Guid.NewGuid()],
            "node",
            EncodedWidth: encoded ? 1_024 : null,
            EncodedHeight: encoded ? 1_024 : null,
            PixelFormat: pixelFormat,
            PreviewReconstructionSupported: !encoded && reconstructionSupported);
    }

    private sealed class StubGallery : ICameraAgentGallery
    {
        private readonly CameraAgentGalleryPage? _page;
        private readonly bool _cancellationOnly;

        internal StubGallery(CameraAgentGalleryPage? page = null, bool cancellationOnly = false)
        {
            _page = page;
            _cancellationOnly = cancellationOnly;
        }

        internal CameraAgentGalleryQuery? LastQuery { get; private set; }

        public ValueTask<CameraAgentGalleryPage> GetPageAsync(
            CameraAgentGalleryQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            if (_cancellationOnly)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(_page ?? new CameraAgentGalleryPage([], null));
        }

        public ValueTask<CameraAgentGalleryCapture?> GetCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<CameraAgentGalleryCapture?>(null);
    }

    private sealed class PagedStubGallery(params CameraAgentGalleryPage[] pages) : ICameraAgentGallery
    {
        internal int ReadCount { get; private set; }

        public ValueTask<CameraAgentGalleryPage> GetPageAsync(
            CameraAgentGalleryQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(pages[ReadCount++]);
        }

        public ValueTask<CameraAgentGalleryCapture?> GetCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<CameraAgentGalleryCapture?>(null);
    }

    private sealed class StubRuntime(CameraAgentPresentationRuntimeSnapshot snapshot) : ICameraAgentPresentationRuntime
    {
        public CameraAgentPresentationRuntimeSnapshot GetSnapshot(DateTimeOffset observedUtc) => snapshot;
    }

    private sealed class StubStructuredLayers(bool available) : ICameraAgentStructuredLayerAvailability
    {
        public ValueTask<bool> IsAvailableAsync(Guid captureId, CancellationToken cancellationToken)
            => ValueTask.FromResult(available);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
