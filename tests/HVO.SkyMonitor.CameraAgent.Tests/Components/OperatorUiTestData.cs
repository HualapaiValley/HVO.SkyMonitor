using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

internal static class OperatorUiTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    internal static CameraAgentOperationsView Operations(
        int samples = 1,
        string heartbeat = "Available",
        string centralIntegration = "Enabled",
        int lanePressure = 0,
        bool storagePressure = false,
        IReadOnlyList<OperatorOutboxItem>? artifactQuarantine = null)
    {
        var queue = new OperationsQueueState("Healthy", 0, 0, 0, 0, 0, 0, null);
        var lanes = new OperationsCaptureLanesState(
            lanePressure == 0 ? "Healthy" : "Degraded",
            [new OperationsLaneState("standard", true, lanePressure == 0 ? 0 : 12, 4096, 0, 0, 0, lanePressure, null, [])],
            lanePressure == 0 ? 0 : 12,
            4096,
            0,
            0,
            0,
            null);
        var summary = new CameraAgentOperationsSummary(
            Now,
            Section(new OperationsCaptureControlState("Running", 7, true)),
            Section(queue with { Availability = "Accepting" }),
            Section(lanes),
            Section(queue),
            Section(queue),
            Section<IReadOnlyList<OperationsStorageState>>([
                new OperationsStorageState("raw-ingress", 1_000_000, 500_000, storagePressure, 3, true)
            ]),
            Section(new OperationsCaptureRuntimeState("Available", Now.AddSeconds(-1), null, null, [])),
            Section(new OperationsHeartbeatState(heartbeat, Now.AddSeconds(-2), 0, 0, 0, 0, 0, 0, 0, null)),
            Section(new OperationsEnvironmentalDeliveryState("Available", Now.AddSeconds(-2), 0, 0, 0, 0, 0, 0, 0, 0, 0, null)),
            Section(new OperationsTransientWorkerState("Available", 0, 0, 32)),
            Section(new OperationsCaptureTelemetryState(
                samples,
                samples == 0 ? null : Now.AddSeconds(-1),
                samples == 0 ? null : "Still",
                samples == 0 ? null : 1000,
                samples == 0 ? null : 2,
                5000,
                1000,
                50,
                1200,
                samples == 0 ? 0 : 12,
                .2,
                samples,
                0)),
            Section(new OperationsConfigurationState(true, "validated", "agent-test", "VirtualSky", centralIntegration, "Off")));
        return new CameraAgentOperationsView(summary, artifactQuarantine ?? [], []);
    }

    internal static CameraAgentGalleryCapture Capture(
        Guid? captureId = null,
        GalleryEvidenceOrigin origin = GalleryEvidenceOrigin.Simulated,
        bool annotated = true)
    {
        var rawId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var previewId = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var artifacts = new List<CameraAgentGalleryArtifact>
        {
            new(rawId, FrameArtifactRole.Raw, "source-1", null, Now, "application/x-skymonitor-mono16", new string('A', 64), 2048, null, [], null,
                PixelFormat: CameraPixelFormat.Mono16, PreviewReconstructionSupported: true),
            new(previewId, annotated ? FrameArtifactRole.AnnotatedPreview : FrameArtifactRole.Preview, "source-2", "display", Now,
                "application/x-hvo-packed-image", new string('B', 64), 1024,
                new CameraAgentGalleryRecipe("preview", "1.0.0", "build-7", new string('C', 64), new string('D', 64)),
                [rawId], "preview-node", PixelFormat: CameraPixelFormat.Mono16,
                PreviewReconstructionSupported: true)
        };
        var nodeCompleted = Now.AddSeconds(-3);
        var detail = new CameraAgentGalleryCaptureDetail(
            "Available",
            "artifact-manifest-v2",
            new CameraAgentGalleryLayout(640, 480, 1280, "Mono16", "LittleEndian", 16, 16, "Unpacked", "None", 0, 65535, 614400),
            new CameraAgentGalleryTiming(Now.AddSeconds(-6), Now.AddSeconds(-5), Now.AddSeconds(-4), Now.AddSeconds(-4), Now.AddSeconds(-4), null),
            new CameraAgentGalleryControls(1000, 1000, 2, 2, null, null, null, 8.5),
            true,
            [
                new CameraAgentGalleryArtifactState(rawId, "Held", "Available", [new("raw-ingress", "Pending")]),
                new CameraAgentGalleryArtifactState(previewId, "Retained", "Available", [new("raw-ingress", "Acknowledged")])
            ],
            [new CameraAgentGalleryProcessingNodeDetail(
                "preview-node", ["source"], 1, nodeCompleted, null, new string('F', 64),
                nodeCompleted.AddMilliseconds(-5), 5, "Produced",
                [new(0, "Artifact", "raw", rawId, FrameArtifactRole.Raw, "native", new string('G', 64), null, null, true)])],
            new CameraAgentGalleryCloudAssessment(
                "Available", "Quantified", "Degraded", 250000, 900000, ["environment-missing"], true, 640, 480, new string('E', 64)));
        return new CameraAgentGalleryCapture(
            captureId ?? Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "agent-test",
            "rig-test",
            42,
            Now.AddSeconds(-5),
            Now.AddSeconds(-4),
            "durable",
            origin,
            artifacts,
            [new CameraAgentGalleryProcessingNode("preview-node", true, "completed", "preview", FrameArtifactRole.Preview, "display", [previewId])],
            detail);
    }

    internal static CameraAgentCurrentImagePresentation CurrentImage(
        CameraAgentPresentationImageFreshness freshness = CameraAgentPresentationImageFreshness.Current,
        CameraAgentPresentationSystemState systemState = CameraAgentPresentationSystemState.Capturing,
        bool historicalFallback = false)
    {
        var captureId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var rawArtifactId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var annotatedArtifactId = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var capture = new CameraAgentPresentationCapture(
            captureId,
            42,
            Now.AddSeconds(-5),
            5,
            GalleryEvidenceOrigin.Simulated);
        CameraAgentPresentationSlot[] stages =
            [
                new CameraAgentPresentationSlot(CameraAgentPresentationStage.Raw, "Raw", CameraAgentPresentationSlotAvailability.Available,
                    "Available.", rawArtifactId, FrameArtifactRole.Raw, null, "image/jpeg",
                    new Uri("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000101/preview", UriKind.Relative)),
                new CameraAgentPresentationSlot(CameraAgentPresentationStage.Calibrated, "Calibrated", CameraAgentPresentationSlotAvailability.Missing,
                    "NotProduced"),
                new CameraAgentPresentationSlot(CameraAgentPresentationStage.Combined, "Combined", CameraAgentPresentationSlotAvailability.Missing,
                    "NotProduced"),
                new CameraAgentPresentationSlot(CameraAgentPresentationStage.Annotated, "Processed", CameraAgentPresentationSlotAvailability.Available,
                    "Available.", annotatedArtifactId, FrameArtifactRole.AnnotatedPreview, "display", "image/jpeg",
                    new Uri("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000102/preview", UriKind.Relative))
            ];
        return new CameraAgentCurrentImagePresentation(
            Now,
            freshness,
            new CameraAgentPresentationSystemStatus(
                systemState,
                systemState == CameraAgentPresentationSystemState.Capturing ? "Capture is running." : "Capture is not running.",
                Now,
                systemState == CameraAgentPresentationSystemState.Standby ? Now.AddHours(1) : null),
            capture,
            capture,
            historicalFallback,
            CameraAgentPresentationStage.Annotated,
            stages,
            false,
            false);
    }

    internal static CameraAgentSystemStatus SystemStatus() => new(
        "Unversioned startup snapshot",
        "Unavailable",
        "Validated at startup",
        "agent-test",
        "VirtualSky",
        "Enabled",
        new CameraAgentSensorStatus("Virtual sensor", 640, 480, 5.86, "Mono", "Mono16", "Monochrome", "unversioned", "test-v1"),
        new CameraAgentOpticsStatus("Fisheye", "Equidistant", 3, 180, 140, "cal-v1", false, "Full sensor"),
        new CameraAgentCapturePolicyStatus("MinimumStartInterval", 5, 10, 1000, 1, 2, 1, 2000, 0, 10, "ExposureFirst"),
        [new CameraAgentPipelineNodeStatus("preview", "Preview", true, ["calibrate"])],
        new CameraAgentRetentionStatus(30, 10, 15, 1, 1024),
        new CameraAgentUploadStatus(true, 10, 10, 10, 300, 0, 100, 1000, 50, 500),
        new CameraAgentEnvironmentalPolicyStatus(true, 100, 10, 10, 1000, 4096),
        new CameraAgentTransientPolicyStatus("Edge", true, 10, 1000, 5, 30, 2000));

    private static OperationsSection<T> Section<T>(T value) => new("test-source", Now, "fresh", value);
}

internal sealed class TestOperatorUiService : ICameraAgentOperatorUiService, ICameraAgentCapturePresentationProjector
{
    internal Func<CancellationToken, ValueTask<OperatorUiResult<CameraAgentOperationsView>>> OperationsHandler { get; set; } =
        _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations()));
    internal Func<CameraAgentGalleryQuery, CancellationToken, ValueTask<OperatorUiResult<CameraAgentGalleryPage>>> GalleryHandler { get; set; } =
        (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryPage>.Success(new CameraAgentGalleryPage([], null)));
    internal Func<CancellationToken, ValueTask<OperatorUiResult<CameraAgentCurrentImagePresentation>>> CurrentImageHandler { get; set; } =
        _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(new(
            OperatorUiTestData.Now,
            CameraAgentPresentationImageFreshness.Empty,
            new CameraAgentPresentationSystemStatus(
                CameraAgentPresentationSystemState.Capturing, "CameraAgent is capturing normally.", OperatorUiTestData.Now),
            null,
            null,
            false,
            null,
            [],
            false,
            false)));
    internal Func<Guid, CancellationToken, ValueTask<OperatorUiResult<CameraAgentGalleryCapture>>> DetailHandler { get; set; } =
        (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCapture>.Success(OperatorUiTestData.Capture()));
    internal Func<Guid, CancellationToken, ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>>> PresentationHandler { get; set; } =
        (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Failure(
            OperatorUiResultKind.NotFound, "Structured layers were not retained for this capture."));
    internal Func<Guid, IReadOnlyList<string>, CancellationToken, ValueTask<OperatorUiResult<CameraAgentPresentationMaterializationReceipt>>> MaterializationHandler { get; set; } =
        (captureId, _, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Success(new(
            captureId, Guid.Parse("00000000-0000-0000-0000-000000000104"), new string('F', 64),
            new string('A', 64), 1024, false)));
    internal Func<string, string?, string?, int, CancellationToken, ValueTask<OperatorUiResult<OperatorOutboxPage>>> QuarantineHandler { get; set; } =
        (kind, alias, _, _, _) => ValueTask.FromResult(OperatorUiResult<OperatorOutboxPage>.Success(new(
            kind, alias is null ? [] : [alias], alias, [], null)));
    internal Func<CancellationToken, ValueTask<OperatorUiResult<CameraAgentSystemStatus>>> SystemHandler { get; set; } =
        _ => ValueTask.FromResult(OperatorUiResult<CameraAgentSystemStatus>.Success(OperatorUiTestData.SystemStatus()));
    internal Func<bool, long, string, CancellationToken, Task<OperatorUiResult<OperatorCommandReceipt>>> CaptureHandler { get; set; } =
        (paused, _, _, _) => Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Success(new(
            paused ? "Pause capture" : "Resume capture", "Applied", paused ? "Paused" : "Running", 8, OperatorUiTestData.Now)));
    internal Func<string, OutboxOperationAction, string, string, string, CancellationToken, ValueTask<OperatorUiResult<OperatorCommandReceipt>>> OutboxHandler { get; set; } =
        (kind, action, _, _, _, _) => ValueTask.FromResult(OperatorUiResult<OperatorCommandReceipt>.Success(new(
            $"{action} {kind}", "Applied", action == OutboxOperationAction.Replay ? "Pending" : "Abandoned", null, OperatorUiTestData.Now)));
    internal Func<string, string, string, bool, CancellationToken, ValueTask<OperatorUiResult<OperatorTransientOwnershipBinding>>> OwnershipHandler { get; set; } =
        (_, run, hash, acknowledged, _) => ValueTask.FromResult(
            acknowledged
                ? OperatorUiResult<OperatorTransientOwnershipBinding>.Success(new("bound-action-token", run, hash.ToUpperInvariant()))
                : OperatorUiResult<OperatorTransientOwnershipBinding>.Failure(OperatorUiResultKind.Invalid, "Ownership acknowledgment required."));

    public ValueTask<OperatorUiResult<CameraAgentOperationsView>> GetOperationsAsync(CancellationToken cancellationToken) => OperationsHandler(cancellationToken);
    public ValueTask<OperatorUiResult<CameraAgentGalleryPage>> GetGalleryPageAsync(CameraAgentGalleryQuery query, CancellationToken cancellationToken) => GalleryHandler(query, cancellationToken);
    public ValueTask<OperatorUiResult<CameraAgentCurrentImagePresentation>> GetCurrentImagePresentationAsync(CancellationToken cancellationToken) => CurrentImageHandler(cancellationToken);
    public ValueTask<OperatorUiResult<CameraAgentGalleryCapture>> GetGalleryCaptureAsync(Guid captureId, CancellationToken cancellationToken) => DetailHandler(captureId, cancellationToken);
    public async ValueTask<OperatorUiResult<CameraAgentCaptureDetailView>> GetCaptureDetailViewAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var result = await DetailHandler(captureId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            return OperatorUiResult<CameraAgentCaptureDetailView>.Failure(
                result.Kind,
                result.Message ?? "The capture detail is unavailable.");
        }
        var capture = result.Value;
        return OperatorUiResult<CameraAgentCaptureDetailView>.Success(new(
            capture,
            CameraAgentOperatorUiService.ProjectCaptureDetailPresentation(Project(capture))));
    }

    public CameraAgentCapturePresentation Project(CameraAgentGalleryCapture? capture)
    {
        CameraAgentPresentationStage[] presentationStages =
        [
            CameraAgentPresentationStage.Raw,
            CameraAgentPresentationStage.Calibrated,
            CameraAgentPresentationStage.Combined,
            CameraAgentPresentationStage.Annotated
        ];
        if (capture is null)
        {
            return new(null, presentationStages
                .Select(static stage => new CameraAgentPresentationSlot(
                    stage,
                    stage == CameraAgentPresentationStage.Annotated ? "Processed" : stage.ToString(),
                    CameraAgentPresentationSlotAvailability.Missing,
                    "NoCapture"))
                .ToArray());
        }
        var stages = presentationStages
            .Select(stage => TestSlot(capture, stage))
            .ToArray();
        var selected = new[]
        {
            CameraAgentPresentationStage.Annotated,
            CameraAgentPresentationStage.Combined,
            CameraAgentPresentationStage.Calibrated,
            CameraAgentPresentationStage.Raw
        }.FirstOrDefault(stage => stages.Single(slot => slot.Stage == stage).Availability == CameraAgentPresentationSlotAvailability.Available);
        var hasSelected = stages.Any(slot => slot.Stage == selected && slot.Availability == CameraAgentPresentationSlotAvailability.Available);
        return new CameraAgentCapturePresentation(hasSelected ? selected : null, stages);
    }
    public ValueTask<OperatorUiResult<CameraAgentLayeredPresentation>> GetLayeredPresentationAsync(Guid captureId, CancellationToken cancellationToken) => PresentationHandler(captureId, cancellationToken);
    public ValueTask<OperatorUiResult<CameraAgentPresentationMaterializationReceipt>> SaveLayeredPresentationAsync(Guid captureId, IReadOnlyList<string> enabledLayerIdentitySha256, CancellationToken cancellationToken) => MaterializationHandler(captureId, enabledLayerIdentitySha256, cancellationToken);
    public ValueTask<OperatorUiResult<OperatorOutboxPage>> GetQuarantinePageAsync(string kind, string? storageAlias, string? cursor, int pageSize, CancellationToken cancellationToken) => QuarantineHandler(kind, storageAlias, cursor, pageSize, cancellationToken);
    public ValueTask<OperatorUiResult<CameraAgentSystemStatus>> GetSystemStatusAsync(CancellationToken cancellationToken) => SystemHandler(cancellationToken);
    public Task<OperatorUiResult<OperatorCommandReceipt>> SetCapturePausedAsync(bool paused, long expectedVersion, string idempotencyKey, CancellationToken cancellationToken) => CaptureHandler(paused, expectedVersion, idempotencyKey, cancellationToken);
    public ValueTask<OperatorUiResult<OperatorTransientOwnershipBinding>> BindTransientRuntimeOwnershipAsync(string referenceToken, string deploymentRunId, string inventorySha256, bool legacyOwnershipExternallyEstablished, CancellationToken cancellationToken) => OwnershipHandler(referenceToken, deploymentRunId, inventorySha256, legacyOwnershipExternallyEstablished, cancellationToken);
    public ValueTask<OperatorUiResult<OperatorCommandReceipt>> ResolveOutboxAsync(string kind, OutboxOperationAction action, string actionToken, string reasonCode, string idempotencyKey, CancellationToken cancellationToken) => OutboxHandler(kind, action, actionToken, reasonCode, idempotencyKey, cancellationToken);

    private static CameraAgentPresentationSlot TestSlot(
        CameraAgentGalleryCapture capture,
        CameraAgentPresentationStage stage)
    {
        var roles = stage switch
        {
            CameraAgentPresentationStage.Raw => new[] { FrameArtifactRole.Raw },
            CameraAgentPresentationStage.Calibrated => [FrameArtifactRole.Calibrated],
            CameraAgentPresentationStage.Combined => [FrameArtifactRole.Combined],
            _ => [FrameArtifactRole.AnnotatedPreview, FrameArtifactRole.Preview]
        };
        var artifact = roles
            .Select(role => capture.Artifacts.FirstOrDefault(candidate =>
                candidate.Role == role && TestPreviewEligible(candidate)))
            .FirstOrDefault(static candidate => candidate is not null);
        var label = stage == CameraAgentPresentationStage.Annotated ? "Processed" : stage.ToString();
        return artifact is null
            ? new(stage, label, CameraAgentPresentationSlotAvailability.Missing, "NotProduced")
            : new(stage, label, CameraAgentPresentationSlotAvailability.Available, "Available", artifact.ArtifactId,
                artifact.Role, artifact.Variant, artifact.MediaType,
                new Uri(FormattableString.Invariant($"/api/v1/operations/artifacts/{artifact.ArtifactId:D}/preview"), UriKind.Relative));
    }

    private static bool TestPreviewEligible(CameraAgentGalleryArtifact artifact)
    {
        if (!string.Equals(artifact.Availability, "Available", StringComparison.Ordinal) || artifact.ByteLength is null or < 1)
        {
            return false;
        }
        if (artifact.Role is FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview &&
            string.Equals(artifact.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return artifact.EncodedWidth is > 0 && artifact.EncodedHeight is > 0;
        }
        return artifact.PreviewReconstructionSupported && artifact.PixelFormat is not null &&
            artifact.MediaType is "application/x-hvo-packed-image" or "application/x-hvo-linear-frame" or
                "application/x-skymonitor-mono8" or "application/x-skymonitor-mono16" or
                "application/x-skymonitor-rgb24" or "application/x-skymonitor-bayer-rggb16";
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
