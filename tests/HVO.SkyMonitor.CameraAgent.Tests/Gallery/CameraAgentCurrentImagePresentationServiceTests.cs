using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCurrentImagePresentationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly CameraAgentPresentationStage[] ExpectedStageOrder =
    [
        CameraAgentPresentationStage.Annotated,
        CameraAgentPresentationStage.Combined,
        CameraAgentPresentationStage.Calibrated,
        CameraAgentPresentationStage.Raw
    ];
    private static readonly string[] ExpectedStageLabels = ["Processed", "Combined", "Calibrated", "Raw"];

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
        Assert.HasCount(4, result.Stages);
        CollectionAssert.AreEqual(ExpectedStageOrder, result.Stages.Select(static slot => slot.Stage).ToArray());
        CollectionAssert.AreEqual(ExpectedStageLabels, result.Stages.Select(static slot => slot.Label).ToArray());
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
        Assert.AreEqual(CameraAgentPresentationStage.Annotated, result.SelectedStage);
        Assert.AreEqual(
            FrameArtifactRole.Preview,
            result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated).ArtifactRole);
    }

    [TestMethod]
    [DataRow(CameraAgentArtifactReadStatus.Gone)]
    [DataRow(CameraAgentArtifactReadStatus.Conflict)]
    public async Task InvalidNewestPreviewFallsBackToLatestValidatedCaptureAsync(
        CameraAgentArtifactReadStatus invalidStatus)
    {
        var latestArtifactId = Guid.NewGuid();
        var priorArtifactId = Guid.NewGuid();
        var latest = Capture(
            3,
            Now.AddSeconds(-5),
            [Artifact(latestArtifactId, FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")]);
        var prior = Capture(
            2,
            Now.AddMinutes(-1),
            [Artifact(priorArtifactId, FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")]);
        var artifacts = new StubArtifactService(new Dictionary<Guid, CameraAgentArtifactReadStatus>
        {
            [latestArtifactId] = invalidStatus,
            [priorArtifactId] = CameraAgentArtifactReadStatus.Found
        });
        var service = CreateService(
            new StubGallery(new CameraAgentGalleryPage([latest, prior], null)),
            artifacts: artifacts);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(latest.CaptureId, result.LatestCapture!.CaptureId);
        Assert.AreEqual(prior.CaptureId, result.DisplayCapture!.CaptureId);
        Assert.IsTrue(result.IsHistoricalFallback);
        Assert.AreEqual(CameraAgentPresentationImageFreshness.Historical, result.ImageFreshness);
        Assert.AreEqual(priorArtifactId, result.Stages.Single(static slot =>
            slot.Stage == CameraAgentPresentationStage.Annotated).ArtifactId);
        CollectionAssert.AreEqual(new[] { latestArtifactId, priorArtifactId }, artifacts.RequestedArtifactIds.ToArray());
    }

    [TestMethod]
    public async Task InvalidPreferredStageFallsBackWithinCaptureAndRedactsFailedSlotsAsync()
    {
        var processedId = Guid.NewGuid();
        var combinedId = Guid.NewGuid();
        var rawId = Guid.NewGuid();
        var latest = Capture(
            3,
            Now.AddSeconds(-5),
            [
                Artifact(processedId, FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image"),
                Artifact(combinedId, FrameArtifactRole.Combined, "stack", "application/x-hvo-packed-image"),
                Artifact(rawId, FrameArtifactRole.Raw, "camera-native", "application/x-skymonitor-mono16", sources: [])
            ]);
        var prior = Capture(
            2,
            Now.AddMinutes(-1),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")]);
        var artifacts = new StubArtifactService(new Dictionary<Guid, CameraAgentArtifactReadStatus>
        {
            [processedId] = CameraAgentArtifactReadStatus.Gone,
            [combinedId] = CameraAgentArtifactReadStatus.Found,
            [rawId] = CameraAgentArtifactReadStatus.Conflict
        });

        var result = await CreateService(
            new StubGallery(new CameraAgentGalleryPage([latest, prior], null)),
            artifacts: artifacts).GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(latest.CaptureId, result.DisplayCapture!.CaptureId);
        Assert.IsFalse(result.IsHistoricalFallback);
        Assert.AreEqual(CameraAgentPresentationStage.Combined, result.SelectedStage);
        var processed = result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Unavailable, processed.Availability);
        Assert.AreEqual("Gone", processed.Reason);
        Assert.IsNull(processed.ArtifactId);
        Assert.IsNull(processed.PreviewUrl);
        var raw = result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Raw);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Unavailable, raw.Availability);
        Assert.AreEqual("Conflict", raw.Reason);
        CollectionAssert.AreEqual(
            new[] { processedId, combinedId, rawId },
            artifacts.RequestedArtifactIds.ToArray());
    }

    [TestMethod]
    public async Task InvalidPreferredArtifactFallsBackWithinProcessedStageAsync()
    {
        var annotatedId = Guid.NewGuid();
        var previewId = Guid.NewGuid();
        var capture = Capture(
            3,
            Now.AddSeconds(-5),
            [
                Artifact(
                    annotatedId,
                    FrameArtifactRole.AnnotatedPreview,
                    "annotated-thumbnail-1024-jpeg",
                    "image/jpeg"),
                Artifact(previewId, FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")
            ]);
        var artifacts = new StubArtifactService(new Dictionary<Guid, CameraAgentArtifactReadStatus>
        {
            [annotatedId] = CameraAgentArtifactReadStatus.Conflict,
            [previewId] = CameraAgentArtifactReadStatus.Found
        });

        var result = await CreateService(
            new StubGallery(new CameraAgentGalleryPage([capture], null)),
            artifacts: artifacts).GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(capture.CaptureId, result.DisplayCapture!.CaptureId);
        Assert.AreEqual(CameraAgentPresentationStage.Annotated, result.SelectedStage);
        var processed = result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available, processed.Availability);
        Assert.AreEqual(previewId, processed.ArtifactId);
        Assert.AreEqual(FrameArtifactRole.Preview, processed.ArtifactRole);
        CollectionAssert.AreEqual(new[] { annotatedId, previewId }, artifacts.RequestedArtifactIds.ToArray());
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
    public async Task PreviewValidationAttemptsAreRequestBoundedAsync()
    {
        var captures = Enumerable.Range(0, CameraAgentCurrentImagePresentationService.MaximumPreviewValidationAttempts + 1)
            .Select(index => Capture(
                100 - index,
                Now.AddSeconds(-index),
                [Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")]))
            .ToArray();
        var statuses = captures.SelectMany(static capture => capture.Artifacts)
            .ToDictionary(static artifact => artifact.ArtifactId, static _ => CameraAgentArtifactReadStatus.Conflict);
        var artifacts = new StubArtifactService(statuses);

        var result = await CreateService(
            new StubGallery(new CameraAgentGalleryPage(captures, "more")),
            artifacts: artifacts).GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(result.DisplayCapture);
        Assert.IsTrue(result.HistoryBoundReached);
        Assert.HasCount(CameraAgentCurrentImagePresentationService.MaximumPreviewValidationAttempts,
            artifacts.RequestedArtifactIds);
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
            slot.Stage == CameraAgentPresentationStage.Annotated);
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
            projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated).Reason);
        Assert.AreEqual(
            "ProcessingSkipped",
            projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Calibrated).Reason);
    }

    [TestMethod]
    public void ProcessedFailureTakesPrecedenceOverInvalidBackingArtifact()
    {
        var capture = Capture(
            4,
            Now.AddSeconds(-3),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.AnnotatedPreview, "invalid", "image/jpeg", byteLength: 0)]) with
        {
            ProcessingNodes =
            [
                new CameraAgentGalleryProcessingNode(
                    "preview", true, "TerminalFailure", "preview", FrameArtifactRole.Preview, "display", [])
            ]
        };

        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()))
            .Project(capture);

        Assert.AreEqual(
            "ProcessingFailed",
            projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated).Reason);
    }

    [TestMethod]
    public void TechnicalStageKeepsSpecificArtifactFailureBeforeProcessingFailure()
    {
        var capture = Capture(
            4,
            Now.AddSeconds(-3),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Calibrated, "invalid", "application/x-hvo-packed-image", byteLength: 0)]) with
        {
            ProcessingNodes =
            [
                new CameraAgentGalleryProcessingNode(
                    "calibrate", true, "TerminalFailure", "calibrate", FrameArtifactRole.Calibrated, "linear", [])
            ]
        };

        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()))
            .Project(capture);

        Assert.AreEqual(
            "ArtifactInvalid",
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
        (CameraAgentPresentationStage Stage, FrameArtifactRole Role)[] expectedOrder =
        [
            (CameraAgentPresentationStage.Annotated, FrameArtifactRole.AnnotatedPreview),
            (CameraAgentPresentationStage.Annotated, FrameArtifactRole.Preview),
            (CameraAgentPresentationStage.Combined, FrameArtifactRole.Combined),
            (CameraAgentPresentationStage.Calibrated, FrameArtifactRole.Calibrated),
            (CameraAgentPresentationStage.Raw, FrameArtifactRole.Raw)
        ];

        foreach (var expected in expectedOrder)
        {
            var projection = projector.Project(Capture(5, Now, artifacts));
            Assert.AreEqual(expected.Stage, projection.SelectedStage);
            var selected = projection.Stages.Single(slot => slot.Stage == expected.Stage);
            Assert.AreEqual(expected.Role, selected.ArtifactRole);
            artifacts.RemoveAll(artifact => artifact.Role == selected.ArtifactRole);
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
            admission, null, null, TimeSpan.FromMinutes(1), FleetAvailability.Unavailable, Now);
        var starting = CameraAgentPresentationRuntime.Project(
            admission, null, null, TimeSpan.FromMinutes(1), FleetAvailability.Initializing, Now);
        var pausedAdmission = admission with
        {
            State = HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureAdmissionState.Paused
        };
        var pausedUnavailable = CameraAgentPresentationRuntime.Project(
            pausedAdmission, null, null, TimeSpan.FromMinutes(1), FleetAvailability.Unavailable, Now);

        Assert.AreEqual(CameraAgentPresentationSystemState.Unavailable, unavailable.System.State);
        Assert.AreEqual(CameraAgentPresentationSystemState.Starting, starting.System.State);
        Assert.AreEqual(CameraAgentPresentationSystemState.Unavailable, pausedUnavailable.System.State);
        Assert.AreEqual(TimeSpan.FromMinutes(1), unavailable.ExpectedCaptureInterval.GetValueOrDefault());
    }

    [TestMethod]
    [DataRow(1, 10, CameraAgentPresentationImageFreshness.Current)]
    [DataRow(10, 1, CameraAgentPresentationImageFreshness.Delayed)]
    public async Task RuntimeProjectionUsesAdmittedProfileCadenceForFreshnessAsync(
        int baseMinutes,
        int profileMinutes,
        CameraAgentPresentationImageFreshness expected)
    {
        var admission = new HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureAdmissionSnapshot(
            HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureAdmissionState.Running,
            1,
            Now,
            true);
        var profile = new CaptureScheduleSetpointProfile(
            "active",
            TimeSpan.FromSeconds(1),
            1,
            TimeSpan.FromMinutes(profileMinutes));
        var schedule = new CaptureScheduleDefinition("capture-schedule-v1", [profile], []);
        var decision = new CaptureScheduleDecision(
            true,
            CaptureScheduleAdmissionReason.WeeklyWindow,
            CaptureScheduleSafetyState.Available,
            Now,
            profile.Id,
            null,
            null,
            false,
            null);
        var snapshot = CameraAgentPresentationRuntime.Project(
            admission,
            decision,
            schedule,
            TimeSpan.FromMinutes(baseMinutes),
            FleetAvailability.Available,
            Now);
        var capture = Capture(
            1,
            Now.AddMinutes(-3),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")]);

        var result = await CreateService(
            new StubGallery(new CameraAgentGalleryPage([capture], null)),
            expectedInterval: snapshot.ExpectedCaptureInterval).GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TimeSpan.FromMinutes(profileMinutes), snapshot.ExpectedCaptureInterval.GetValueOrDefault());
        Assert.AreEqual(expected, result.ImageFreshness);
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

    [TestMethod]
    public async Task OptionalStructuredLayerFailurePreservesCurrentImageAsync()
    {
        var capture = Capture(
            1,
            Now.AddSeconds(-5),
            [Artifact(Guid.NewGuid(), FrameArtifactRole.Preview, "default", "application/x-hvo-packed-image")]);
        var service = CreateService(
            new StubGallery(new CameraAgentGalleryPage([capture], null)),
            structuredLayersFailure: new InvalidDataException("malformed optional layer"));

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(capture.CaptureId, result.DisplayCapture!.CaptureId);
        Assert.AreEqual(CameraAgentPresentationStage.Annotated, result.SelectedStage);
        Assert.AreEqual(
            FrameArtifactRole.Preview,
            result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated).ArtifactRole);
        Assert.IsFalse(result.StructuredLayersAvailable);
    }

    [TestMethod]
    public void CombinedStageDisplaysLineageMatchedRetainedDerivativeWhileKeepingLinearIdentity()
    {
        var rawId = Guid.NewGuid();
        var combinedId = Guid.NewGuid();
        var derivativeId = Guid.NewGuid();
        var capture = Capture(
            7,
            Now.AddSeconds(-5),
            [
                Artifact(rawId, FrameArtifactRole.Raw, "camera-native", "application/x-skymonitor-mono16", sources: []),
                Artifact(combinedId, FrameArtifactRole.Combined, "rolling-mean", "application/x-hvo-linear-frame", sources: [rawId]),
                EncodedPreview(derivativeId, "combined-preview", [combinedId])
            ]);

        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()))
            .ProjectWithRetainedDisplay(capture);

        var combined = projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available, combined.Availability);
        Assert.AreEqual(combinedId, combined.ArtifactId, "stage identity must stay the linear Combined frame");
        Assert.AreEqual(FrameArtifactRole.Combined, combined.ArtifactRole);
        Assert.AreEqual("application/x-hvo-linear-frame", combined.MediaType);
        Assert.AreEqual(derivativeId, combined.DisplayArtifactId);
        Assert.AreEqual(CameraAgentPresentationDisplayBasis.RetainedDerivative, combined.DisplayBasis);
        Assert.AreEqual($"/api/v1/operations/artifacts/{derivativeId:D}/preview?displayReference={derivativeId:D}", combined.PreviewUrl!.OriginalString);
        StringAssert.Contains(combined.DisplayPolicy!, derivativeId.ToString("D"), StringComparison.Ordinal);
        StringAssert.Contains(combined.DisplayPolicy!, "one configured display stretch", StringComparison.Ordinal);
        // Processed artifact ranking is independent of the Combined slot's display substitution.
        var processed = projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreEqual(derivativeId, processed.ArtifactId);
        Assert.AreEqual(CameraAgentPresentationDisplayBasis.OwnArtifact, processed.DisplayBasis);
        var raw = projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Raw);
        Assert.AreEqual(rawId, raw.DisplayArtifactId);
        Assert.AreEqual(CameraAgentPresentationDisplayBasis.OwnArtifact, raw.DisplayBasis);
        Assert.AreEqual(derivativeId, raw.DisplayReferenceId);
        Assert.AreEqual($"/api/v1/operations/artifacts/{rawId:D}/preview?displayReference={derivativeId:D}", raw.PreviewUrl!.OriginalString);
        StringAssert.Contains(raw.DisplayPolicy!, "per-image histogram normalization", StringComparison.Ordinal);
        StringAssert.Contains(raw.DisplayPolicy!, "not a locked transfer curve", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("wrong-lineage")]
    [DataRow("cross-capture")]
    [DataRow("ambiguous")]
    [DataRow("multi-source")]
    [DataRow("wrong-recipe")]
    [DataRow("unavailable")]
    [DataRow("missing")]
    public void CombinedStageRejectsNonMatchingDerivativesAndFallsBackToOwnArtifact(string scenario)
    {
        var rawId = Guid.NewGuid();
        var combinedId = Guid.NewGuid();
        var otherCaptureCombinedId = Guid.NewGuid();
        var artifacts = new List<CameraAgentGalleryArtifact>
        {
            Artifact(rawId, FrameArtifactRole.Raw, "camera-native", "application/x-skymonitor-mono16", sources: []),
            Artifact(combinedId, FrameArtifactRole.Combined, "rolling-mean", "application/x-hvo-linear-frame", sources: [rawId])
        };
        switch (scenario)
        {
            case "wrong-lineage":
                artifacts.Add(EncodedPreview(Guid.NewGuid(), "calibrated-preview", [rawId]));
                break;
            case "cross-capture":
                artifacts.Add(EncodedPreview(Guid.NewGuid(), "combined-preview", [otherCaptureCombinedId]));
                break;
            case "ambiguous":
                artifacts.Add(EncodedPreview(Guid.NewGuid(), "combined-preview", [combinedId]));
                artifacts.Add(EncodedPreview(Guid.NewGuid(), "combined-preview-alt", [combinedId]));
                break;
            case "multi-source":
                artifacts.Add(EncodedPreview(Guid.NewGuid(), "combined-preview", [combinedId, rawId]));
                break;
            case "wrong-recipe":
                artifacts.Add(EncodedPreview(Guid.NewGuid(), "combined-preview", [combinedId], recipeName: "annotation"));
                break;
            case "unavailable":
                artifacts.Add(EncodedPreview(Guid.NewGuid(), "combined-preview", [combinedId]) with { Availability = "Missing" });
                break;
        }

        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()))
            .ProjectWithRetainedDisplay(Capture(8, Now.AddSeconds(-5), artifacts));

        var combined = projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available, combined.Availability);
        Assert.AreEqual(combinedId, combined.ArtifactId);
        Assert.AreEqual(combinedId, combined.DisplayArtifactId, scenario);
        Assert.AreEqual(CameraAgentPresentationDisplayBasis.OwnArtifact, combined.DisplayBasis, scenario);
        Assert.AreEqual($"/api/v1/operations/artifacts/{combinedId:D}/preview", combined.PreviewUrl!.OriginalString);
        StringAssert.Contains(combined.DisplayPolicy!, "No retained lineage-matched display derivative", StringComparison.Ordinal);
    }

    [TestMethod]
    public void TruncatedArtifactListCannotProveDerivativeUniqueness()
    {
        var combinedId = Guid.NewGuid();
        var combined = Artifact(combinedId, FrameArtifactRole.Combined, "rolling-mean", "application/x-hvo-linear-frame");
        var first = EncodedPreview(Guid.NewGuid(), "combined-preview", [combinedId]);
        var second = EncodedPreview(Guid.NewGuid(), "combined-preview-alt", [combinedId]);
        var complete = Capture(11, Now, [combined, first, second]);
        var projector = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()));

        // D1 is visible but D2 is beyond the bounded projection. The visible subset must not turn ambiguity
        // into a false uniqueness proof, even though D1's own lineage and eligibility are otherwise valid.
        var truncated = complete with { Artifacts = [combined, first], ArtifactsTruncated = true };
        var boundedSlot = projector.ProjectWithRetainedDisplay(truncated).Stages.Single(static slot =>
            slot.Stage == CameraAgentPresentationStage.Combined);
        Assert.AreEqual(combinedId, boundedSlot.DisplayArtifactId);
        Assert.AreEqual(CameraAgentPresentationDisplayBasis.OwnArtifact, boundedSlot.DisplayBasis);
        StringAssert.Contains(boundedSlot.DisplayPolicy!, "bounded artifact list cannot prove", StringComparison.Ordinal);

        var ambiguousSlot = projector.ProjectWithRetainedDisplay(complete).Stages.Single(static slot =>
            slot.Stage == CameraAgentPresentationStage.Combined);
        Assert.AreEqual(combinedId, ambiguousSlot.DisplayArtifactId);
        var uniqueSlot = projector.ProjectWithRetainedDisplay(truncated with { ArtifactsTruncated = false })
            .Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        Assert.AreEqual(first.ArtifactId, uniqueSlot.DisplayArtifactId, "Complete-list control must exercise substitution.");
    }

    [TestMethod]
    [DataRow(CameraAgentArtifactReadStatus.Conflict)]
    [DataRow(CameraAgentArtifactReadStatus.Gone)]
    public async Task InvalidRetainedDerivativeFallsBackToLinearCombinedPreviewAsync(CameraAgentArtifactReadStatus failure)
    {
        var combinedId = Guid.NewGuid();
        var derivativeId = Guid.NewGuid();
        var capture = Capture(
            9,
            Now.AddSeconds(-5),
            [
                Artifact(combinedId, FrameArtifactRole.Combined, "rolling-mean", "application/x-hvo-linear-frame"),
                EncodedPreview(derivativeId, "combined-preview", [combinedId])
            ]);
        var artifacts = new StubArtifactService(new Dictionary<Guid, CameraAgentArtifactReadStatus>
        {
            [derivativeId] = failure,
            [combinedId] = CameraAgentArtifactReadStatus.Found
        });

        var result = await CreateService(
            new StubGallery(new CameraAgentGalleryPage([capture], null)),
            artifacts: artifacts).GetAsync(CancellationToken.None).ConfigureAwait(false);

        var combined = result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available, combined.Availability);
        Assert.AreEqual(combinedId, combined.ArtifactId);
        Assert.AreEqual(combinedId, combined.DisplayArtifactId);
        Assert.AreEqual(CameraAgentPresentationDisplayBasis.OwnArtifact, combined.DisplayBasis);
        // The corrupt derivative is also the Processed stage's only candidate, so that stage is redacted.
        var processed = result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Annotated);
        Assert.AreEqual(CameraAgentPresentationSlotAvailability.Unavailable, processed.Availability);
        Assert.AreEqual(failure.ToString(), processed.Reason);
        Assert.AreEqual(CameraAgentPresentationStage.Combined, result.SelectedStage);
        CollectionAssert.AreEqual(new[] { derivativeId, combinedId }, artifacts.RequestedArtifactIds.ToArray());
    }

    [TestMethod]
    public void CalibrationNoneIsDescribedAsNoCorrectionNotAsDisplayStretch()
    {
        var rawId = Guid.NewGuid();
        var calibratedId = Guid.NewGuid();
        var capture = Capture(
            10,
            Now.AddSeconds(-5),
            [
                Artifact(rawId, FrameArtifactRole.Raw, "camera-native", "application/x-skymonitor-mono16", sources: []),
                Artifact(calibratedId, FrameArtifactRole.Calibrated, "none", "application/x-hvo-linear-frame", sources: [rawId])
                    with { Recipe = new CameraAgentGalleryRecipe("linear-normalization", "1.0.0", "linear-normalization-none-v1", new string('B', 64), new string('C', 64)) }
            ]);

        var projection = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions()))
            .Project(capture);

        var calibrated = projection.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Calibrated);
        StringAssert.StartsWith(calibrated.DisplayPolicy!, "Calibration None: no correction applied; pixels equal the Raw frame.", StringComparison.Ordinal);
        StringAssert.Contains(calibrated.DisplayPolicy!, "per-image percentile normalization", StringComparison.Ordinal);
        Assert.AreEqual(calibratedId, calibrated.DisplayArtifactId);
        Assert.AreEqual(CameraAgentPresentationDisplayBasis.OwnArtifact, calibrated.DisplayBasis);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentSkySharesVerifiedReferenceOrFallsBackEveryComparisonStageAsync(bool rejectRawReference)
    {
        var rawId = Guid.NewGuid();
        var calibratedId = Guid.NewGuid();
        var combinedId = Guid.NewGuid();
        var referenceId = Guid.NewGuid();
        var annotatedId = Guid.NewGuid();
        var capture = Capture(12, Now,
        [
            Artifact(rawId, FrameArtifactRole.Raw, "native", "application/x-skymonitor-mono16", sources: []),
            Artifact(calibratedId, FrameArtifactRole.Calibrated, "none", "application/x-hvo-linear-frame", sources: [rawId]),
            Artifact(combinedId, FrameArtifactRole.Combined, "rolling-mean", "application/x-hvo-linear-frame", sources: [rawId]),
            EncodedPreview(referenceId, "combined-preview", [combinedId]),
            Artifact(annotatedId, FrameArtifactRole.AnnotatedPreview, "annotated", "image/jpeg", sources: [referenceId])
        ]);
        var artifacts = new StubArtifactService { RejectedPolicyTarget = rejectRawReference ? rawId : null };

        var result = await CreateService(new StubGallery(new CameraAgentGalleryPage([capture], null)), artifacts: artifacts)
            .GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(artifacts.Requests.Contains((referenceId, referenceId)), "Live mean must validate D's recorded policy/source, not just preview bytes.");
        Assert.IsTrue(artifacts.Requests.Contains((calibratedId, referenceId)));
        Assert.IsTrue(artifacts.Requests.Contains((rawId, referenceId)));
        foreach (var stage in result.Stages.Where(static slot => slot.Stage != CameraAgentPresentationStage.Annotated))
        {
            Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available, stage.Availability);
            if (rejectRawReference)
            {
                Assert.IsNull(stage.DisplayReferenceId);
                Assert.AreEqual(stage.ArtifactId, stage.DisplayArtifactId);
                Assert.IsFalse(stage.PreviewUrl!.OriginalString.Contains("displayReference", StringComparison.Ordinal));
                StringAssert.Contains(stage.DisplayPolicy!, "comparison reference failed validation", StringComparison.Ordinal);
            }
            else
            {
                Assert.AreEqual(referenceId, stage.DisplayReferenceId);
                Assert.AreEqual(stage.Stage == CameraAgentPresentationStage.Combined ? referenceId : stage.ArtifactId, stage.DisplayArtifactId);
            }
        }
        var gallery = new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions())).Project(capture);
        Assert.IsTrue(gallery.Stages.All(static slot => slot.DisplayReferenceId is null));
    }

    [TestMethod]
    public async Task FailedReadCannotTurnAmbiguousReferenceListIntoUniqueReferenceAsync()
    {
        var combinedId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var capture = Capture(13, Now,
        [
            Artifact(combinedId, FrameArtifactRole.Combined, "mean", "application/x-hvo-linear-frame"),
            EncodedPreview(firstId, "a", [combinedId]),
            EncodedPreview(secondId, "b", [combinedId])
        ]);
        var artifacts = new StubArtifactService(new Dictionary<Guid, CameraAgentArtifactReadStatus>
        {
            [firstId] = CameraAgentArtifactReadStatus.Gone
        });
        var result = await CreateService(new StubGallery(new CameraAgentGalleryPage([capture], null)), artifacts: artifacts)
            .GetAsync(CancellationToken.None).ConfigureAwait(false);
        var combined = result.Stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        Assert.AreEqual(combinedId, combined.DisplayArtifactId);
        Assert.IsNull(combined.DisplayReferenceId);
        Assert.IsTrue(artifacts.Requests.All(static request => request.Reference is null));
    }

    private static CameraAgentCurrentImagePresentationService CreateService(
        ICameraAgentGallery gallery,
        CameraAgentPresentationSystemState systemState = CameraAgentPresentationSystemState.Capturing,
        TimeSpan? expectedInterval = null,
        bool structuredLayersAvailable = false,
        Exception? structuredLayersFailure = null,
        ICameraAgentArtifactService? artifacts = null)
        => new(
            gallery,
            new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions())),
            artifacts ?? new StubArtifactService(),
            new StubRuntime(new CameraAgentPresentationRuntimeSnapshot(
                new CameraAgentPresentationSystemStatus(systemState, "Bounded state.", Now),
                expectedInterval)),
            new StubStructuredLayers(structuredLayersAvailable, structuredLayersFailure),
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

    private static CameraAgentGalleryArtifact EncodedPreview(
        Guid id,
        string variant,
        IReadOnlyList<Guid> sources,
        string recipeName = "encoded-preview")
        => new(
            id,
            FrameArtifactRole.Preview,
            "source",
            variant,
            Now.AddSeconds(-1),
            "application/x-hvo-packed-image",
            new string('D', 64),
            64,
            new CameraAgentGalleryRecipe(recipeName, "1.0.0", "encoded-preview-v1", new string('E', 64), new string('F', 64)),
            sources,
            "node",
            PixelFormat: CameraPixelFormat.Mono8,
            PreviewReconstructionSupported: true);

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

    private sealed class StubArtifactService(
        IReadOnlyDictionary<Guid, CameraAgentArtifactReadStatus>? statuses = null) : ICameraAgentArtifactService
    {
        internal List<Guid> RequestedArtifactIds { get; } = [];
        internal List<(Guid Artifact, Guid? Reference)> Requests { get; } = [];
        internal Guid? RejectedPolicyTarget { get; init; }

        public ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(
            Guid artifactId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.NotFound));

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(
            Guid executionId,
            Guid artifactId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.NotFound));

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(
            Guid artifactId,
            CancellationToken cancellationToken,
            Guid? displayReference = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedArtifactIds.Add(artifactId);
            Requests.Add((artifactId, displayReference));
            if (artifactId == RejectedPolicyTarget && displayReference is not null)
                return ValueTask.FromResult(new CameraAgentArtifactPreviewResult(CameraAgentArtifactReadStatus.Conflict));
            var status = statuses is not null && statuses.TryGetValue(artifactId, out var configured)
                ? configured
                : CameraAgentArtifactReadStatus.Found;
            return ValueTask.FromResult(new CameraAgentArtifactPreviewResult(status));
        }
    }

    private sealed class StubRuntime(CameraAgentPresentationRuntimeSnapshot snapshot) : ICameraAgentPresentationRuntime
    {
        public CameraAgentPresentationRuntimeSnapshot GetSnapshot(DateTimeOffset observedUtc) => snapshot;
    }

    private sealed class StubStructuredLayers(bool available, Exception? failure = null) : ICameraAgentStructuredLayerAvailability
    {
        public ValueTask<bool> IsAvailableAsync(Guid captureId, CancellationToken cancellationToken)
            => failure is null
                ? ValueTask.FromResult(available)
                : ValueTask.FromException<bool>(failure);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
