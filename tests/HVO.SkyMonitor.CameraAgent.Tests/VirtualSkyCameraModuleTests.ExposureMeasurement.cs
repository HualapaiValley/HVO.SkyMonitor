using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static readonly string[] MeasurementDependencies = ["$raw", "linear"];

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, true,
        DisplayName = "ExposureMeasurement_ReplaysHistoricalMonoMeasurementAndRejectedFitAfterRestart")]
    [DataRow(CameraPixelFormat.BayerRggb16, "None", true, 1, false, true)]
    [DataRow(CameraPixelFormat.BayerRggb16, "None", true, 1, false, true, "star", false,
        FrameStoredCodeTransform.RightAlignedV1, 0, "", false, "2026-11-21T13:27:27.3434303+00:00",
        DisplayName = "BayerHfdFormerFailureOne")]
    [DataRow(CameraPixelFormat.BayerRggb16, "None", true, 1, false, true, "star", false,
        FrameStoredCodeTransform.RightAlignedV1, 0, "", false, "2026-11-21T14:10:09.7993480+00:00",
        DisplayName = "BayerHfdFormerFailureTwo")]
    [DataRow(CameraPixelFormat.BayerRggb16, "None", true, 1, false, true, "star", false,
        FrameStoredCodeTransform.RightAlignedV1, 0, "", false, "2026-11-21T14:40:09.9363480+00:00",
        DisplayName = "BayerHfdFormerFailureThree")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "star", false,
        FrameStoredCodeTransform.RightAlignedV1, 0, "", false, "2026-11-21T13:27:27.3434303+00:00",
        DisplayName = "MonoHfdSameFailureAnchor")]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences", DisplayName = "ExposureMeasurement_ReferenceCalibratedMonoReplaysMeasuredStar")]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences", true, 1, false, false, "blank",
        DisplayName = "ExposureMeasurement_ReferenceCalibratedMonoReplaysAssessedEmpty")]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences", true, 1, false, false, "blank", false,
        FrameStoredCodeTransform.RightAlignedV1, 0, "historical-reference",
        DisplayName = "ExposureMeasurement_HistoricalSyntheticReferenceReplaysWithoutStoredOption")]
    [DataRow(CameraPixelFormat.Mono16, "SyntheticReferences", false, 10)]
    [DataRow(CameraPixelFormat.Mono16, "None", false)]
    [DataRow(CameraPixelFormat.Mono16, "CalibrationLibrary")]
    [DataRow(CameraPixelFormat.Mono16, "Omitted")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "blank")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "clipped")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "star", true,
        DisplayName = "ExposureMeasurement_OperationsReopensOriginalMeasurementAfterLinearRetentionDeletion")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "star", false, FrameStoredCodeTransform.LeftShiftedV1,
        DisplayName = "ExposureMeasurement_CenteredLeftShiftedKeepsQualifiedStoredLevels")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "star", false, FrameStoredCodeTransform.FullRangeScaledV1,
        DisplayName = "ExposureMeasurement_CenteredFullRangeScaledKeepsQualifiedStoredLevels")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, true, false, "star", false, FrameStoredCodeTransform.RightAlignedV1, 1,
        DisplayName = "ExposureMeasurement_PublicationIoRetriesOnceAndFencesThePreviousLease")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, true, false, "star", false, FrameStoredCodeTransform.RightAlignedV1, 2,
        DisplayName = "ExposureMeasurement_PublicationIoStopsAtTheExistingAttemptBound")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "star", false, FrameStoredCodeTransform.RightAlignedV1,
        0, "expired", DisplayName = "ExposureMeasurement_OperationsRecognizesDeletedMeasurementSlot")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, false, false, "star", false, FrameStoredCodeTransform.RightAlignedV1,
        0, "corrupt", DisplayName = "ExposureMeasurement_OperationsRecognizesCorruptCommittedJournal")]
    [DataRow(CameraPixelFormat.Mono16, "None", true, 1, true, false, "star", false, FrameStoredCodeTransform.RightAlignedV1,
        0, "", true, DisplayName = "ExposureMeasurement_RemainsAvailableWhenIndependentCatalogIsUnavailable")]
    public async Task ExposureMeasurement_OrdinaryCapturePersistsAndReopensThroughStandardLane(
        CameraPixelFormat format, string calibration, bool declareReadout = true, int referenceGain = 1, bool fit = false,
        bool interrupt = false, string signal = "star", bool expireLinear = false,
        FrameStoredCodeTransform transform = FrameStoredCodeTransform.RightAlignedV1, int publicationFailures = 0,
        string archiveFault = "", bool noPredictionCatalog = false, string? fixtureUtc = null)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-exposure-measurement");
        try
        {
            fixtureUtc ??= format == CameraPixelFormat.BayerRggb16 && calibration == "None" && interrupt && signal == "star"
                ? "2026-11-21T07:21:09.2513482+00:00" : null;
            var now = fixtureUtc is null ? DateTimeOffset.UtcNow.AddMinutes(-1)
                : DateTimeOffset.ParseExact(fixtureUtc, "O", CultureInfo.InvariantCulture);
            var verifyBayerReplay = format == CameraPixelFormat.BayerRggb16 &&
                fixtureUtc == "2026-11-21T13:27:27.3434303+00:00";
            var publishHistoricalMeasurement = format == CameraPixelFormat.Mono16 &&
                fit && publicationFailures == 0 && !noPredictionCatalog;
            var profile = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
            var camera = CreateConfig(format, 224, 224);
            var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(now, -113.878) / 15d;
            var predictionStar = new CelestialCatalogObject("independent-star", "Independent Star", rightAscension, 35.347, 0, 0.65);
            var predictions = new MeasurementPredictionCatalog(predictionStar);
            ICelestialCatalog predictionCatalog = noPredictionCatalog ? new InMemoryCelestialCatalog([]) : predictions;
            var location = DeploymentLocationSnapshot.Create("measurement-test", 1, "virtual", null,
                DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix");
            var references = calibration == "SyntheticReferences" ? new SyntheticCalibrationModelV1 { Gain = referenceGain } : null;
            (double Black, ushort White)? storedLevels = transform switch
            {
                FrameStoredCodeTransform.LeftShiftedV1 => (1024d, (ushort)44800),
                FrameStoredCodeTransform.FullRangeScaledV1 => (1024.2344322344322d, (ushort)44810),
                _ => null
            };
            var nativeWhite = transform is FrameStoredCodeTransform.LeftShiftedV1 or FrameStoredCodeTransform.FullRangeScaledV1 ? 2800 : 4095;
            var readout = format == CameraPixelFormat.Mono16 && declareReadout && calibration != "SyntheticReferences"
                ? new SensorReadoutProfile(new SensorCrop(0, 0, 224, 224), 1, 1, FrameBinningAlgorithm.IdentityV1,
                    CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned, transform,
                    FrameLevelCodeSpace.NativeSample, 64, nativeWhite)
                : null;
            var steps = new List<CaptureProcessingStepConfig>
            {
                new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false, Publication: durable),
                new("Storage", "storage", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(
                    new FileStorageCaptureProcessingStepOptions
                    {
                        StorageRoot = Path.Combine(root, "storage"), QueueForUpload = false, UpdateLatestFrame = false
                    }))
            };
            if (calibration != "Omitted")
            {
                steps.Insert(0, new("Calibration", "linear", DependsOn: ["$raw"],
                    Publication: new(CaptureProcessingPersistenceMode.MemoryOnly), Options: JsonSerializer.SerializeToElement(new
                    { strategy = calibration, outputVariant = "qualified-linear", syntheticCalibration = references })));
            }
            if (fit)
            {
                var snapshot = new AstrometryCatalogSnapshotV1(predictions.Metadata, [predictionStar],
                    new("fixture", "1", "fixture", "unspecified"), true, 7);
                steps.Add(new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable));
                steps.Add(new("MeasuredAstrometry", "fit", DependsOn: ["scene", "associated"], Required: false,
                    Publication: durable, Options: JsonSerializer.SerializeToElement(new MeasuredAstrometryCaptureOptions { Catalog = snapshot })));
            }
            var file = camera with
            {
                AgentId = profile.AgentId,
                Schedule = profile.Schedule,
                Rig = camera.Rig with { ControlPolicy = profile.Rig.ControlPolicy, Readout = readout },
                DeploymentLocation = location,
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 1920,
                    fixedSequenceStartUtc = now,
                    illuminationMode = "ControlledNight",
                    asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16 },
                    syntheticCalibration = references,
                    backgroundElectronsPerSecond = signal == "clipped" ? 1_000_000d : (double?)null
                })),
                CenteredExposure = new("fixture-camera", CenteredExposureProfile.CurrentExecutionVersion),
                Pipeline = profile.Pipeline with { Steps = steps }
            };
            ExposureKey exposure;
            ProcessingGraphExecutionState originalExecution;
            ExposureProductLookup? originalPublication = null;
            ExposureMeasurementV1? originalMeasurement = null;
            MonoReferenceReplayWitness? referenceReplay = null;
            string? historicalReferenceId = null;
            using var cancellation = new CancellationTokenSource();
            var interruption = interrupt ? new MeasurementCommitInterruption(cancellation) : null;
            var publication = publicationFailures > 0 ? new MeasurementPublicationIo(publicationFailures) : null;
            var laneClock = publication is not null ? new ExposureTestTimeProvider(now.AddMinutes(1)) : null;
            using (var provider = MeasurementProvider(root, location, predictionCatalog, (ICaptureProcessingFaultInjector?)interruption ?? publication,
                laneClock, publishHistoricalMeasurement
                    ? static services => services.AddSingleton<IProcessingRecipeExecutor>(new HistoricalMonoMeasurementExecutor())
                    : null))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, file).ConfigureAwait(false);
                var operations = provider.GetRequiredService<IProcessingGraphOperations>();
                var ui = MeasurementOperations(provider, operations);
                var configured = await ui.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(configured.IsSuccess, configured.Message);
                var revision = await ui.GetRevisionDetailAsync(configured.Value!.ConfiguredBasicRevisionId, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.IsTrue(revision.IsSuccess, revision.Message);
                var plan = revision.Value!.Plan;
                Assert.IsNotNull(plan);
                var node = plan.EffectiveNodes.Single(n => n.Id == ExposureMeasurementProducts.StepAlias);
                Assert.IsTrue(node.Enabled);
                Assert.IsFalse(node.Required);
                CollectionAssert.AreEqual(calibration == "Omitted" ? ["$raw"] : MeasurementDependencies, node.Dependencies!.ToArray());
                if (calibration != "Omitted")
                {
                    var linearNode = plan.EffectiveNodes.Single(n => n.Id == "linear");
                    Assert.IsFalse(linearNode.Required);
                    Assert.AreEqual(durable, linearNode.Publication);
                }
                else
                {
                    await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                        await operations.CreateRevisionAsync("measurement-bypass", "no-step-four",
                            active.Pipeline, "no-step-four", "measurement-test", null, CancellationToken.None)
                            .ConfigureAwait(false)).ConfigureAwait(false);
                }

                if (archiveFault == "historical-reference")
                {
                    var authoredOptions = file.Pipeline.Steps.Single(step => step.Id == "linear").Options;
                    var currentPipeline = await operations.ReadRevisionPipelineAsync(configured.Value!.ConfiguredBasicRevisionId,
                        CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(currentPipeline);
                    var historicalPipeline = currentPipeline with
                    {
                        Steps = currentPipeline.Steps.Select(step => step.Id == "linear" ? step with { Options = authoredOptions } : step).ToArray()
                    };
                    historicalReferenceId = await StoragePolicyRawIngressIntegrationTests.SeedLegacyRevisionAsync(provider, active,
                        historicalPipeline, "historical-reference-replay").ConfigureAwait(false);
                    var historicalRegistry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                    await operations.ActivateRevisionAsync(historicalReferenceId, historicalRegistry.StateVersion,
                        "use-historical-reference-revision", "measurement-test", null, CancellationToken.None).ConfigureAwait(false);
                }

                var catalog = new InMemoryCelestialCatalog(signal == "star" ? [
                    new CelestialCatalogObject("fixture-star", "Fixture Star", rightAscension, 35.347, 0, 0.65)
                ] : []);
                var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(now, autoAdvance: true), catalog, new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(active, CancellationToken.None).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
                async Task<(CaptureResult Capture, RawCaptureReceipt Receipt)> CaptureAndAdmitAsync(
                    DateTimeOffset requestedUtc, CancellationToken token)
                {
                    var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
                    await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "fixture", token).ConfigureAwait(false);
                    var request = new CaptureRequest(requestedUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
                    {
                        BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                        BoundGraph = reservation.Revision.Pipeline
                    };
                    var result = await module.CaptureBoundAsync(reservation.Pending.Key, request, token).ConfigureAwait(false);
                    Assert.IsNotNull(result.Frame);
                    var accepted = await ingress.AcceptCenteredAsync(active, new(request, result, requestedUtc, request.TargetInterval,
                        request.TargetInterval), reservation, token).ConfigureAwait(false);
                    return (result, accepted);
                }

                var (capture, receipt) = await CaptureAndAdmitAsync(now, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(capture.Frame);
                exposure = CaptureExposureMeasurementReader.Key(receipt.Manifest);
                if (references is not null)
                {
                    var rawCalibration = receipt.Manifest.Descriptor.Profiles.Calibration;
                    Assert.IsTrue(CalibrationCaptureProcessingStep.MatchesSyntheticCalibrationModel(receipt.Manifest.Descriptor, references),
                        $"Raw calibration profile {rawCalibration.Name}/{rawCalibration.Version}/{rawCalibration.Sha256}");
                }

                var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
                await laneStore.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
                var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
                var lease = await laneStore.ClaimAsync(lane, "exposure-measurement-test", active, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var reader = provider.GetRequiredService<IExposureProductReader>();
                var slot = new ExposureProductSlot(lease.Context.Execution!.ExecutionId, ExposureMeasurementProducts.StepAlias,
                    ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);
                if (calibration != "Omitted")
                {
                    var unsettled = await reader.ReadLinearAsync(exposure, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(LinearExposureDisposition.Pending, unsettled?.Disposition);
                    Assert.IsNull(await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false));
                    var pending = await ui.GetExecutionDetailAsync(slot.ExecutionId, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual("measurement-pending", pending.Value?.ExposureMeasurement?.UnavailableReason);
                }
                var handler = provider.GetRequiredService<StandardCaptureLaneHandler>();
                if (interruption is not null)
                {
                    var work = lease.Context;
                    var running = Task.Run(async () => await handler.HandleAsync(work, cancellation.Token).ConfigureAwait(false));
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await interruption.Entered.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                        var (_, next) = await CaptureAndAdmitAsync(now.AddSeconds(1), timeout.Token).ConfigureAwait(false);
                        Assert.AreEqual(RawIngressOutcome.Committed, next.Outcome);
                        var nextKey = CaptureExposureMeasurementReader.Key(next.Manifest);
                        Assert.AreNotEqual(exposure.CaptureId, nextKey.CaptureId);
                        Assert.AreEqual(exposure.RunId, nextKey.RunId);
                        var current = await ingress.ReadCurrentAsync(exposure.Camera, timeout.Token).ConfigureAwait(false);
                        Assert.AreEqual(nextKey, current.AcquisitionHead?.Exposure);
                        Assert.IsNull(await reader.ReadProductAsync(exposure, slot, timeout.Token).ConfigureAwait(false),
                            "N+1 must commit while step four for N is still blocked.");
                    }
                    finally
                    {
                        interruption.Resume.TrySetResult();
                        await Assert.ThrowsAsync<OperationCanceledException>(async () => await running.ConfigureAwait(false))
                            .ConfigureAwait(false);
                    }
                    Assert.IsNull(await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false),
                        "Files alone cannot advertise an uncommitted measurement.");
                    await laneStore.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                    lease = await laneStore.ClaimAsync(lane, "exposure-measurement-retry", active, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(lease);
                    Assert.AreEqual(slot.ExecutionId, lease.Context.Execution!.ExecutionId);
                }
                var outcome = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                if (publication is not null)
                {
                    Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, outcome.Outcome, outcome.Reason);
                    Assert.AreEqual("measurement-publication-io", outcome.Reason);
                    Assert.IsNull(await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false));
                    var failedAttempt = await operations.ReadExecutionDetailAsync(slot.ExecutionId, CancellationToken.None).ConfigureAwait(false);
                    var retry = failedAttempt!.Nodes.Single(item => item.NodeId == ExposureMeasurementProducts.StepAlias);
                    Assert.AreEqual("RetryableFailure", retry.Status);
                    Assert.IsEmpty(retry.Outputs);
                    Assert.AreEqual("Completed", failedAttempt.Nodes.Single(item => item.NodeId == "scene").Status);
                    foreach (var dependent in failedAttempt.Nodes.Where(item => item.NodeId is "associated" or "fit"))
                    {
                        Assert.AreEqual("Pending", dependent.Status);
                        Assert.AreEqual(0, dependent.AttemptCount);
                        Assert.IsEmpty(dependent.Outputs);
                    }
                    Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure,
                        await laneStore.FailAsync(lease, outcome, CancellationToken.None).ConfigureAwait(false));
                    Assert.IsNull(await laneStore.ClaimAsync(lane, "publication-retry", active, CancellationToken.None).ConfigureAwait(false));
                    laneClock!.Advance(TimeSpan.FromSeconds(2));

                    var stale = lease;
                    lease = await laneStore.ClaimAsync(lane, "publication-retry", active, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(lease);
                    Assert.AreEqual(2, lease.Attempt);
                    Assert.AreEqual(slot.ExecutionId, lease.Context.Execution!.ExecutionId);
                    Assert.AreNotEqual(stale.LeaseToken, lease.LeaseToken);
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                        await handler.HandleAsync(stale.Context, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                    Assert.AreEqual(1, publication.Attempts);
                    Assert.IsNull(await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false));
                    outcome = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                }
                if (storedLevels is { } expectedLevels)
                    await AssertMappedLinearAsync(provider, reader, receipt.Manifest.Descriptor, exposure, slot,
                        transform, expectedLevels).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, outcome.Outcome, outcome.Reason);
                await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                if (verifyBayerReplay)
                {
                    // Live work has priority over archived replay. Settle the real N+1 admitted during the interruption.
                    var next = await laneStore.ClaimAsync(lane, "exposure-measurement-next", active, CancellationToken.None)
                        .ConfigureAwait(false);
                    Assert.IsNotNull(next);
                    Assert.AreNotEqual(exposure.CaptureId, next.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId);
                    var settledNext = await handler.HandleAsync(next.Context, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, settledNext.Outcome, settledNext.Reason);
                    await laneStore.CompleteAsync(next, CancellationToken.None).ConfigureAwait(false);
                }

                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.IsNotNull(id);
                var execution = await operations.ReadExecutionDetailAsync(id.Value, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(execution);
                originalExecution = execution.Execution;
                Assert.AreEqual(receipt.Manifest.CaptureGraphRevisionId, originalExecution.GraphRevisionId);
                if (historicalReferenceId is not null)
                    Assert.AreEqual(historicalReferenceId, originalExecution.GraphRevisionId);
                if (!fit)
                    Assert.IsFalse(execution.Nodes.Any(item => item.NodeId == "fit"));
                var recordedMeasurement = execution.Nodes.Single(item => item.NodeId == ExposureMeasurementProducts.StepAlias);
                if (publication is not null)
                {
                    Assert.AreEqual(2, publication.Attempts);
                    Assert.AreEqual(2, recordedMeasurement.AttemptCount);
                    Assert.AreEqual("RetryableFailure", recordedMeasurement.Attempts[0].Status);
                }
                if (publicationFailures == 2)
                {
                    Assert.AreEqual("TerminalFailure", recordedMeasurement.Status);
                    Assert.AreEqual("measurement-publication-io", recordedMeasurement.Reason);
                    Assert.IsEmpty(recordedMeasurement.Outputs);
                    foreach (var dependent in execution.Nodes.Where(item => item.NodeId is "associated" or "fit"))
                    {
                        Assert.AreEqual("Skipped", dependent.Status);
                        Assert.AreEqual("processing.dependency-unavailable", dependent.Reason);
                        Assert.IsEmpty(dependent.Outputs);
                    }
                    Assert.IsNull(await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false));
                    Assert.IsNull(await laneStore.ClaimAsync(lane, "unexpected-third-attempt", active, CancellationToken.None)
                        .ConfigureAwait(false));
                    var stillQualified = await reader.ReadLinearAsync(exposure, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly, stillQualified?.Disposition);
                    var unavailable = await ui.GetExecutionDetailAsync(id.Value, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual("measurement-publication-io", unavailable.Value?.ExposureMeasurement?.UnavailableReason);
                }
                else
                {
                    var recordedScene = execution.Nodes.Single(item => item.NodeId == "scene");
                    var settledUtc = publication is null ? recordedMeasurement.CompletedUtc : recordedMeasurement.Attempts[0].CompletedUtc;
                    Assert.IsNotNull(settledUtc);
                    Assert.IsNotNull(recordedScene.StartedUtc);
                    Assert.IsTrue(settledUtc <= recordedScene.StartedUtc, "Step four must get its first attempt before the unrelated scene.");
                    var detail = await ui.GetExecutionDetailAsync(id.Value, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsTrue(detail.IsSuccess, detail.Message);
                    var run = detail.Value!.ExposureMeasurement;
                    Assert.IsNotNull(run?.Measurement, run?.UnavailableReason);
                    Assert.IsNull(run.LinearExpiryReason);
                    var measurement = run.Measurement;
                    Assert.AreEqual(receipt.Manifest.Descriptor.Layout, measurement.Input.Raw.Layout);
                    Assert.AreEqual(receipt.Manifest.Descriptor.Capture.CaptureId, measurement.Input.Raw.CaptureId);
                    Assert.AreEqual(format, measurement.Input.Raw.Layout.PixelFormat);
                    Assert.AreEqual(224L * 224, measurement.RawBrightness!.SampleCount);
                    var pixels = capture.Frame.PixelData;
                    var codes = Enumerable.Range(0, pixels.Length / sizeof(ushort))
                        .Select(index => BinaryPrimitives.ReadUInt16LittleEndian(pixels.Span[(index * sizeof(ushort))..])).ToArray();
                    Assert.AreEqual((ulong)codes.Sum(value => (long)value), measurement.RawBrightness.Sum);
                    Assert.AreEqual(codes.Min(), measurement.RawBrightness.Minimum);
                    Assert.AreEqual(codes.Max(), measurement.RawBrightness.Maximum);
                    Assert.AreEqual(codes.LongCount(value => value == 0), measurement.RawBrightness.ZeroCount);
                    if (measurement.RawBrightness.SensorClipping.StoredWhite is { } white)
                    {
                        Assert.AreEqual(codes.LongCount(value => value >= white), measurement.RawBrightness.SensorClipping.Count);
                        if (signal == "star")
                        {
                            var image = new ImageLayout(224, 224, format, capture.Frame.Layout!.StrideBytes);
                            var corner = SparseLinear16Meter.Measure(image, pixels.Span,
                                new(16, 16, whiteLevel: white, region: new(64, 64, 32, 32)));
                            var center = SparseLinear16Meter.Measure(image, pixels.Span,
                                new(4, 4, whiteLevel: white, region: new(96, 96, 32, 32)));
                            Assert.AreNotEqual(corner.ConsideredSampleCount, center.ConsideredSampleCount);
                            Assert.IsGreaterThan(center.ConsideredSampleCount, measurement.RawBrightness.SampleCount);
                            Assert.IsNull(receipt.Manifest.Descriptor.CycleEvidence?.Metering,
                                "The admitted exposure and fixed statistics do not require a metering result.");
                        }
                    }
                    if (storedLevels is { } expectedStoredLevels)
                    {
                        Assert.AreEqual(expectedStoredLevels.White, measurement.RawBrightness.SensorClipping.StoredWhite);
                        Assert.AreEqual(expectedStoredLevels.White, measurement.LinearOutputClipping?.StoredWhite);
                        Assert.AreEqual(codes.LongCount(value => value >= expectedStoredLevels.White),
                            measurement.LinearOutputClipping?.Count);
                        Assert.AreEqual(expectedStoredLevels.White, measurement.Input.Linear?.Layout.WhiteLevel);
                    }
                    if (calibration is not ("CalibrationLibrary" or "Omitted") && referenceGain == 1 && declareReadout)
                    {
                        Assert.AreEqual(calibration == "None" ? ExposureCalibrationDisposition.NormalizationOnly :
                            ExposureCalibrationDisposition.ReferenceCalibrated, measurement.Input.Calibration,
                            measurement.Input.CalibrationReason);
                        Assert.IsNotNull(measurement.Input.Linear);
                        if (signal == "star")
                        {
                            Assert.AreEqual(ExposureMeasurementStatus.Usable, measurement.Sources.Status, measurement.Sources.Reason +
                                ": " + string.Join(", ", measurement.Sources.Exclusions.Select(item => item.ReasonCode)));
                            Assert.IsTrue(measurement.Sources.Detections.Any(detection =>
                                Math.Abs(detection.Pixel.X - 112) < 2 && Math.Abs(detection.Pixel.Y - 112) < 2),
                                "The independently placed zenith star must be measured near the optical center.");
                            Assert.IsGreaterThan(0, measurement.Hfd.ValidCount ?? 0, measurement.Hfd.Reason);
                            if (fixtureUtc is not null)
                            {
                                var detection = measurement.Sources.Detections.Single(value =>
                                    Math.Abs(value.Pixel.X - 112) < 2 && Math.Abs(value.Pixel.Y - 112) < 2);
                                var focus = measurement.Hfd.Samples.Single(sample => sample.DetectionIndex == detection.Index);
                                Assert.AreEqual(0, detection.SaturatedSampleCount);
                                Assert.AreEqual(FocusStarReasonCodes.Valid, focus.Reason);
                                Assert.IsTrue(focus.DiameterPixels is > 2.4 and < 3.3);
                                var wasFlagged = format == CameraPixelFormat.BayerRggb16 &&
                                    fixtureUtc != "2026-11-21T07:21:09.2513482+00:00";
                                Assert.AreEqual(wasFlagged, detection.Conditions.HasFlag(StellarSourceConditions.Trailed));
                                Assert.AreEqual(wasFlagged, focus.BayerCoreTrailUpperPixels is >= 0 and < 3);
                                TestContext.WriteLine(JsonSerializer.Serialize(new { now, format, exposure, detection, focus }));
                            }
                        }
                        else if (signal == "blank")
                        {
                            Assert.AreEqual(ExposureMeasurementStatus.Empty, measurement.Sources.Status, measurement.Sources.Reason);
                            Assert.AreEqual(0, measurement.Hfd.ValidCount);
                            Assert.IsNull(measurement.Hfd.MeanPixels);
                        }
                        else
                        {
                            Assert.IsGreaterThan(0L, measurement.RawBrightness.SensorClipping.Count ?? 0);
                            Assert.AreEqual(ExposureMeasurementStatus.Unsuitable, measurement.Sources.Status, measurement.Sources.Reason);
                            Assert.IsNull(measurement.Hfd.MeanPixels);
                        }
                        if (format == CameraPixelFormat.Mono16 && references is not null && (signal is "star" or "blank"))
                        {
                            Assert.AreEqual(0L, measurement.RawBrightness.SensorClipping.Count);
                            Assert.AreEqual(0L, measurement.LinearOutputClipping?.Count);
                            if (signal == "star")
                            {
                                var detection = measurement.Sources.Detections.Single(item =>
                                    Math.Abs(item.Pixel.X - 112) < 2 && Math.Abs(item.Pixel.Y - 112) < 2);
                                Assert.AreEqual(0, detection.SaturatedSampleCount);
                                Assert.IsTrue(measurement.Hfd.Samples.Any(sample =>
                                    sample.DetectionIndex == detection.Index && sample.DiameterPixels > 0));
                            }
                            var linearId = Assert.ContainsSingle(execution.Nodes.Single(item => item.NodeId == "linear").Outputs).ArtifactId;
                            var bytes = await ReadReferenceReplayPayloadAsync(provider, root, linearId).ConfigureAwait(false);
                            var replacement = references with { Seed = references.Seed + 1 };
                            referenceReplay = new(receipt.Manifest.Descriptor, replacement, execution, bytes);
                        }
                    }
                    else
                    {
                        var expected = referenceGain != 1 || !declareReadout
                            ? ExposureCalibrationDisposition.Incompatible : ExposureCalibrationDisposition.Missing;
                        Assert.AreEqual(expected, measurement.Input.Calibration, measurement.Input.CalibrationReason);
                        if (referenceGain != 1)
                            Assert.AreEqual(ProcessingReasonCodes.CalibrationReferenceConditionsMismatch, measurement.Input.CalibrationReason);
                        else if (calibration == "Omitted")
                            Assert.AreEqual("linear.no-configured-step", measurement.Input.CalibrationReason);
                        else if (!declareReadout)
                        {
                            Assert.IsNull(measurement.RawBrightness.SensorClipping.Count);
                            Assert.AreEqual(FrameWhiteThreshold.UnavailableReason, measurement.Sources.Reason);
                        }
                        Assert.IsNull(measurement.Input.Linear);
                        Assert.AreEqual(ExposureMeasurementStatus.Unavailable, measurement.Sources.Status);
                    }
                    var output = Assert.ContainsSingle(recordedMeasurement.Outputs);
                    var artifactId = output.ArtifactId;
                    var published = await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(published);
                    Assert.AreEqual(ExposureProductAvailability.Available, published.Availability);
                    Assert.IsNotNull(published.Product);
                    Assert.AreEqual(artifactId, published.Product?.ArtifactId);
                    Assert.AreEqual(output.OutputIdentitySha256, published.Product?.OutputIdentitySha256);
                    if (publishHistoricalMeasurement)
                    {
                        var stored = await provider.GetRequiredService<SqliteCaptureProcessingStore>()
                            .ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
                        Assert.IsNotNull(stored);
                        var retained = Assert.ContainsSingle(await provider.GetRequiredService<CaptureProcessingPersistence>()
                            .RestoreWindowInputsAsync([stored], CancellationToken.None).ConfigureAwait(false));
                        Assert.IsNotNull(retained.RecipeDescriptor);
                        var parameters = retained.RecipeDescriptor.Options.GetProperty("parameters");
                        Assert.IsTrue(parameters.TryGetProperty("raw", out _), "The live fixture must retain the historical plain input.");
                        Assert.IsFalse(parameters.TryGetProperty("hfdAllocation", out _));
                    }
                    var notifications = await reader.ReadProductsAfterAsync(exposure.Camera, 0, 128, CancellationToken.None)
                        .ConfigureAwait(false);
                    Assert.AreEqual(published, Assert.ContainsSingle(notifications.Where(product => product.Slot == slot)));
                    if (fit && noPredictionCatalog)
                    {
                        var prediction = execution.Nodes.Single(item => item.NodeId == ExposureCatalogPredictionProducts.StepAlias);
                        Assert.AreEqual("Skipped", prediction.Status);
                        Assert.AreEqual("scene.frozen-prediction-input-unavailable", prediction.Reason);
                        foreach (var dependent in execution.Nodes.Where(item => item.NodeId is "associated" or "fit"))
                        {
                            Assert.AreEqual("Skipped", dependent.Status);
                            Assert.AreEqual("processing.dependency-unavailable", dependent.Reason);
                            Assert.IsEmpty(dependent.Outputs);
                        }
                    }
                    else if (fit)
                    {
                        var association = execution.Nodes.Single(item => item.NodeId == "associated");
                        Assert.AreEqual("Completed", association.Status, association.Reason);
                        var associationOutput = Assert.ContainsSingle(association.Outputs);
                        if (publicationFailures == 0)
                        {
                            var prediction = execution.Nodes.Single(item => item.NodeId == ExposureCatalogPredictionProducts.StepAlias);
                            Assert.Contains(Assert.ContainsSingle(prediction.Outputs).ArtifactId, associationOutput.SourceArtifactIds);
                            await AssertAuthoritativeSceneUploadAsync(provider, lease.Context, execution).ConfigureAwait(false);
                        }
                        Assert.Contains(artifactId, associationOutput.SourceArtifactIds);
                        var fitting = execution.Nodes.Single(item => item.NodeId == "fit");
                        Assert.AreEqual("Completed", fitting.Status, fitting.Reason);
                        var solutionOutput = fitting.Outputs.Single(item => item.Variant == "measured-astrometry-v1");
                        var stored = await provider.GetRequiredService<SqliteCaptureProcessingStore>().ReadOutputByArtifactIdAsync(
                            solutionOutput.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                        Assert.IsNotNull(stored);
                        var solution = MeasuredAstrometryJson.Parse(await File.ReadAllBytesAsync(Path.Combine(root, stored.PayloadRelativePath))
                            .ConfigureAwait(false));
                        Assert.AreEqual(AstrometricAssessmentStatus.Rejected, solution.Assessment.Status, solution.Assessment.Reason);
                        Assert.AreEqual(associationOutput.ArtifactId, solution.MeasuredArtifactId);
                        if (publicationFailures == 0)
                        {
                            await AssertCenteredRejectedFitAndMarkersAsync(provider, exposure, solutionOutput.ArtifactId).ConfigureAwait(false);
                            await AssertNamedPredictionDependenciesAsync(provider, active, operations, originalExecution.GraphRevisionId).ConfigureAwait(false);
                        }
                    }

                    if (expireLinear)
                        await ExpireQualifiedLinearAsync(provider, root, reader, exposure, slot, measurement).ConfigureAwait(false);
                    if (archiveFault == "expired")
                    {
                        await ExpireOnlyProductAsync(provider, root, artifactId).ConfigureAwait(false);
                        var expired = await reader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false);
                        Assert.AreEqual(ExposureProductAvailability.Expired, expired?.Availability);
                        Assert.AreEqual("retention-expired", expired?.Reason);
                        Assert.AreEqual(published.Revision, expired?.Revision);
                        Assert.AreEqual(published.Exposure, expired?.Exposure);

                        var snapshotReader = provider.GetRequiredService<IExposureMeasurementRunReader>();
                        var raced = await snapshotReader.ReadAsync(execution, CancellationToken.None).ConfigureAwait(false);
                        Assert.AreEqual("retention-expired", raced?.UnavailableReason,
                            "Retention completed after Operations took the available snapshot.");
                        var otherRevision = execution with { Execution = execution.Execution with { GraphRevisionId = new string('F', 64) } };
                        var unrelated = await snapshotReader.ReadAsync(otherRevision, CancellationToken.None).ConfigureAwait(false);
                        Assert.AreEqual("measurement-unreadable", unrelated?.UnavailableReason);
                    }
                    else if (archiveFault == "corrupt")
                    {
                        // Mutate the journaled run snapshot, not the current configuration or the committed step-four payload.
                        await CorruptMeasurementRunSnapshotAsync(root, exposure).ConfigureAwait(false);
                    }
                    originalPublication = published;
                    originalMeasurement = measurement;
                }
            }

            using var reopened = MeasurementProvider(root, location, new InMemoryCelestialCatalog([]));
            var restartedIngress = reopened.GetRequiredService<RawCaptureIngress>();
            await restartedIngress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var replacementHostProfile = referenceReplay is null ? file with { CenteredExposure = null } :
                WithReplacementReferences(file, referenceReplay.Replacement);
            var switched = replacementHostProfile with { Rig = replacementHostProfile.Rig with { Orientation = new RigOrientation(80, 15, 5) } };
            reopened.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(switched);
            await restartedIngress.ReconcileCenteredControlAsync(switched, CancellationToken.None, exposure.Camera)
                .ConfigureAwait(false);
            var reopenedOperations = reopened.GetRequiredService<IProcessingGraphOperations>();
            var reopenedUi = MeasurementOperations(reopened, reopenedOperations);
            var reopenedDetail = await reopenedUi.GetExecutionDetailAsync(originalExecution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(reopenedDetail.IsSuccess, reopenedDetail.Message);
            var reopenedView = reopenedDetail.Value!;
            Assert.AreEqual(originalExecution.ExecutionId, reopenedView.Execution.ExecutionId);
            Assert.AreEqual(exposure.CaptureId, reopenedView.Execution.CaptureId);
            Assert.AreEqual(originalExecution.PrimaryArtifactId, reopenedView.Execution.PrimaryArtifactId);
            Assert.AreEqual(originalExecution.GraphRevisionId, reopenedView.Execution.GraphRevisionId);
            var reopenedReader = reopened.GetRequiredService<IExposureProductReader>();
            var fixedStage = reopenedView.Nodes.Single(item => item.NodeId == ExposureMeasurementProducts.StepAlias);
            if (publicationFailures == 2)
            {
                Assert.AreEqual(originalExecution.ExecutionId, await reopenedOperations.ReadLiveExecutionIdAsync(exposure.CaptureId,
                    CancellationToken.None).ConfigureAwait(false));
                Assert.AreEqual("TerminalFailure", fixedStage.Status);
                Assert.AreEqual("measurement-publication-io", fixedStage.Reason);
                Assert.HasCount(2, fixedStage.Attempts);
                Assert.AreEqual("RetryableFailure", fixedStage.Attempts[0].Status);
                Assert.AreEqual("TerminalFailure", fixedStage.Attempts[1].Status);
                Assert.IsTrue(fixedStage.Attempts.All(attempt => attempt.Reason == "measurement-publication-io"));
                Assert.IsEmpty(fixedStage.Outputs);
                Assert.IsNotNull(reopenedView.ExposureMeasurement);
                Assert.IsNull(reopenedView.ExposureMeasurement.Measurement);
                Assert.AreEqual("measurement-publication-io", reopenedView.ExposureMeasurement.UnavailableReason);
                var journal = await reopenedOperations.ReadRunManifestAsync(originalExecution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(journal);
                Assert.AreEqual(0, journal.Nodes.Single(item => item.NodeId == fixedStage.NodeId).CommittedOutputCount);
                var slot = new ExposureProductSlot(originalExecution.ExecutionId, fixedStage.NodeId,
                    ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);
                Assert.IsNull(await reopenedReader.ReadProductAsync(exposure, slot, CancellationToken.None).ConfigureAwait(false));
                var notifications = await reopenedReader.ReadProductsAfterAsync(exposure.Camera, 0, 128, CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(notifications.Any(notification => notification.Slot == slot));
                return;
            }
            Assert.IsNotNull(originalPublication?.Product);
            Assert.IsNotNull(originalMeasurement);
            if (archiveFault is "expired" or "corrupt")
            {
                var unavailable = reopenedView.ExposureMeasurement;
                Assert.IsNotNull(unavailable);
                Assert.IsNull(unavailable.Measurement);
                Assert.AreEqual(archiveFault == "expired" ? "retention-expired" : "measurement-unreadable",
                    unavailable.UnavailableReason);
                if (archiveFault == "expired")
                    Assert.IsEmpty(fixedStage.Outputs, "The deleted output row can no longer supply a joined result.");
                else
                    Assert.AreEqual(originalPublication.Product.ArtifactId, Assert.ContainsSingle(fixedStage.Outputs).ArtifactId);
                return;
            }
            var reopenedOutput = Assert.ContainsSingle(fixedStage.Outputs);
            Assert.AreEqual(originalPublication.Product.ArtifactId, reopenedOutput.ArtifactId);
            Assert.AreEqual(originalPublication.Product.OutputIdentitySha256, reopenedOutput.OutputIdentitySha256);
            var restored = reopenedView.ExposureMeasurement;
            Assert.IsNotNull(restored?.Measurement, restored?.UnavailableReason);
            Assert.AreEqual(originalPublication, await reopenedReader.ReadProductAsync(originalPublication.Exposure,
                originalPublication.Slot, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(expireLinear ? "retention-expired" : null, restored.LinearExpiryReason);
            Assert.AreNotEqual(switched.Rig.Orientation, restored.Measurement.Input.RigOrientation);
            Assert.AreEqual(JsonSerializer.Serialize(originalMeasurement), JsonSerializer.Serialize(restored.Measurement),
                "The original source, centroid positions/flux, and HFD samples and summary must survive unchanged.");
            if (expireLinear || storedLevels is not null)
                Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly, restored.Measurement.Input.Step3Disposition);
            if (referenceReplay is not null)
                await AssertMonoReferenceReplayAsync(reopened, switched, referenceReplay, originalMeasurement, root).ConfigureAwait(false);
            if (historicalReferenceId is not null)
            {
                var historical = await reopenedOperations.ReadRevisionPipelineAsync(historicalReferenceId, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.IsNotNull(historical);
                var options = historical.Steps.Single(step => step.Id == "linear").Options!.Value;
                Assert.IsFalse(options.TryGetProperty("useStoredLightCodes", out _));
            }
            if (fit && publicationFailures == 0 && !noPredictionCatalog || verifyBayerReplay)
                await AssertMeasurementReplayAsync(reopened, originalExecution, originalMeasurement, fit).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task AssertAuthoritativeSceneUploadAsync(ServiceProvider provider, CaptureLaneHandlerContext context,
        ProcessingGraphExecutionDetail execution)
    {
        var token = CancellationToken.None;
        var raw = context.RawCapture.Manifest;
        Assert.IsNotNull(raw.Scene);
        Assert.IsTrue(raw.Scene.RequiresProjectedScene);
        Assert.IsNotNull(raw.Scene.ProjectedSceneStageKey);
        var ownedStages = await provider.GetRequiredService<RawCaptureIngress>().GetOwnedStageKeysAsync(token).ConfigureAwait(false);
        Assert.DoesNotContain(raw.Scene.ProjectedSceneStageKey, ownedStages);
        var canonicalNode = execution.Nodes.Single(node => node.NodeId == "scene");
        var predictionNode = execution.Nodes.Single(node => node.NodeId == ExposureCatalogPredictionProducts.StepAlias);
        var canonical = Assert.ContainsSingle(canonicalNode.Outputs);
        var prediction = Assert.ContainsSingle(predictionNode.Outputs);
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var canonicalOutput = await store.ReadOutputByArtifactIdAsync(canonical.ArtifactId, token).ConfigureAwait(false);
        var predictionOutput = await store.ReadOutputByArtifactIdAsync(prediction.ArtifactId, token).ConfigureAwait(false);
        Assert.IsNotNull(canonicalOutput);
        Assert.IsNotNull(predictionOutput);
        Assert.AreEqual("projected-scene-v1", canonical.Variant);
        Assert.AreEqual(ExposureCatalogPredictionProducts.Variant, prediction.Variant);
        Assert.AreEqual(BuiltInProcessingRecipes.ProjectedScene, canonicalOutput.Artifact.Recipe.Name);
        Assert.AreEqual(canonicalOutput.Artifact.Recipe.Name, predictionOutput.Artifact.Recipe.Name);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, canonicalOutput.ProductSchemaVersion);
        Assert.AreEqual(canonicalOutput.ProductSchemaVersion, predictionOutput.ProductSchemaVersion);
        Assert.AreNotEqual(canonical.ArtifactId, prediction.ArtifactId);
        CollectionAssert.AreEqual(new[] { raw.Descriptor.Artifact.ArtifactId }, canonical.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(canonical.SourceArtifactIds.ToArray(), prediction.SourceArtifactIds.ToArray());

        var upload = provider.GetRequiredService<UploadCaptureLaneHandler>();
        var outbox = provider.GetRequiredService<IArtifactOutbox>();
        var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
        var root = persistence.StorageRoot;
        var result = await upload.HandleAsync(context with { Lane = "upload" }, token).ConfigureAwait(false);
        var queued = await outbox.GetRetentionHoldsAsync(root, token).ConfigureAwait(false);
        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            raw.Descriptor.Capture.CaptureId,
            Raw = raw.Descriptor.Artifact.ArtifactId,
            Canonical = new
            {
                canonicalNode.NodeId,
                canonical.ArtifactId,
                canonical.Variant,
                canonical.OutputIdentitySha256,
                canonicalOutput.ProductSchemaVersion,
                canonicalOutput.Artifact.Recipe.Name
            },
            Prediction = new
            {
                predictionNode.NodeId,
                prediction.ArtifactId,
                prediction.Variant,
                prediction.OutputIdentitySha256,
                predictionOutput.ProductSchemaVersion,
                predictionOutput.Artifact.Recipe.Name
            },
            Outcome = result.Outcome.ToString(),
            result.Reason,
            EnqueuedCount = queued.Count,
            Enqueued = queued.Select(hold => hold.ArtifactId)
        }));
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        CollectionAssert.AreEquivalent(new[] { raw.Descriptor.Artifact.ArtifactId, canonical.ArtifactId },
            queued.Select(hold => hold.ArtifactId).ToArray());
        var manifest = await persistence.FindCommittedSceneUploadAsync(raw.Descriptor, token).ConfigureAwait(false);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(canonical.ArtifactId, manifest.Descriptor.Artifact.ArtifactId);
        Assert.AreEqual(canonical.OutputIdentitySha256, manifest.Descriptor.OutputIdentitySha256);
        Assert.AreEqual(canonicalNode.NodeId, manifest.ProducerStepId);
        var enqueued = await outbox.ReadAsync(root, manifest.IdempotencyKey, token).ConfigureAwait(false);
        Assert.IsNotNull(enqueued);
        CollectionAssert.AreEqual(StructuredProcessingProductManifestJson.Serialize(manifest), enqueued.ManifestBytes.ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await persistence.CreateSceneUploadManifestAsync(raw.Descriptor, predictionOutput, token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static async Task AssertNamedPredictionDependenciesAsync(ServiceProvider provider, CameraModuleConfig active,
        IProcessingGraphOperations graphs, string revisionId)
    {
        var pipeline = await graphs.ReadRevisionPipelineAsync(revisionId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pipeline);
        const string prediction = ExposureCatalogPredictionProducts.StepAlias;
        var legitimate = await graphs.CreateRevisionAsync("independent-science", "1", pipeline,
            "independent-science-create", "measurement-test", null, CancellationToken.None).ConfigureAwait(false);
        await graphs.ValidateRevisionAsync(legitimate.RevisionId, "independent-science-validate", "measurement-test", null,
            CancellationToken.None).ConfigureAwait(false);
        var original = await graphs.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        var activated = await graphs.ActivateRevisionAsync(legitimate.RevisionId, original.StateVersion,
            "independent-science-activate", "measurement-test", null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(legitimate.RevisionId, activated.ActiveRevisionId);
        await graphs.RollbackRevisionAsync(original.ActiveRevisionId, activated.StateVersion, "independent-science-rollback",
            "measurement-test", null, CancellationToken.None).ConfigureAwait(false);

        foreach (var consumerId in new[] { "associated", "fit" })
        {
            var consumer = pipeline.Steps.Single(step => step.Id == consumerId);
            Assert.Contains(prediction, consumer.DependsOn!);
            var wrong = pipeline with
            {
                Steps = [.. pipeline.Steps.Select(step => step == consumer
                    ? step with { DependsOn = [.. consumer.DependsOn!.Select(id => id == prediction ? "scene" : id)] }
                    : step)]
            };
            await AssertRejectedAsync(wrong, "presentation-as-" + consumerId).ConfigureAwait(false);
        }
        var impostor = pipeline with
        {
            Steps = [.. pipeline.Steps.Where(step => step.Id != "scene").Select(step => step.Id == prediction
                ? step with { Type = "ProjectedScene" } : step)]
        };
        await AssertRejectedAsync(impostor, "presentation-fixed-id").ConfigureAwait(false);
        var before = await graphs.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        var historical = await StoragePolicyRawIngressIntegrationTests.SeedLegacyRevisionAsync(provider, active, impostor)
            .ConfigureAwait(false);
        var state = await graphs.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before.ActiveRevisionId, state.ActiveRevisionId);
        var failedActivation = await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
            await graphs.ActivateRevisionAsync(historical, state.StateVersion, "invalid-science-activation", "measurement-test",
                null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.Contains("fixed, durable exposure catalog prediction", failedActivation.Message);
        var unchanged = await graphs.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(state.ActiveRevisionId, unchanged.ActiveRevisionId);
        Assert.AreEqual(state.StateVersion, unchanged.StateVersion);

        async Task AssertRejectedAsync(CapturePipelineConfig wrong, string name)
        {
            var failure = await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await graphs.CreateRevisionAsync(name, "1", wrong, name, "measurement-test", null, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            Assert.Contains("fixed, durable exposure catalog prediction", failure.Message);
        }
    }

    private static async Task AssertMeasurementReplayAsync(ServiceProvider provider, ProcessingGraphExecutionState original,
        ExposureMeasurementV1 originalMeasurement, bool fit)
    {
        var token = CancellationToken.None;
        var graphs = provider.GetRequiredService<IProcessingGraphOperations>();
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
        var live = await graphs.ReadExecutionDetailAsync(original.ExecutionId, token).ConfigureAwait(false);
        Assert.IsNotNull(live);
        var requested = await graphs.SubmitReplayAsync(new(original.CaptureId, original.GraphRevisionId, original.PrimaryArtifactId),
            "measurement-archived-replay", "measurement-test", token).ConfigureAwait(false);
        Assert.AreNotEqual(original.ExecutionId, requested.Execution.ExecutionId);
        var lease = await store.ClaimReplayAsync("measurement-rejected-fit", token).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var execution = new ProcessingExecutionContext(lease.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
            lease.Execution.GraphRevisionId, lease.Execution.LocalPlanIdentitySha256, false, lease.WorkId, lease.LeaseToken,
            lease.LeaseOwner, lease.Execution.DeadlineUtc, lease.ClaimCount);
        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateRetainedGraph(lease.Configuration);
        try
        {
            var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                new(lease.Configuration, lease.Submission, lease.RawCapture, lease.WorkId, lease.LeaseToken, execution), graph,
                persistence, provider.GetRequiredService<CaptureProcessingTelemetry>(), lease.Execution.AttemptCount,
                NullLogger.Instance, token, durableAttempt: lease.ClaimCount,
                frozenPlanSuperseded: !lease.Revision.IsCompiledBy(graph)).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            var completed = await store.CompleteReplayAsync(lease, result, token).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Status);
        }
        finally
        {
            graph.DisposeSteps();
        }

        var replay = await graphs.ReadExecutionDetailAsync(requested.Execution.ExecutionId, token).ConfigureAwait(false);
        Assert.IsNotNull(replay);
        Assert.AreEqual(original.GraphRevisionId, replay.Execution.GraphRevisionId);
        Assert.AreEqual(original.LocalPlanIdentitySha256, replay.Execution.LocalPlanIdentitySha256);
        string[] steps = fit ? [ExposureMeasurementProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias, "associated", "fit"]
            : [ExposureMeasurementProducts.StepAlias, "linear", "scene"];
        foreach (var step in steps)
        {
            var liveNode = live.Nodes.Single(node => node.NodeId == step);
            var replayed = replay.Nodes.Single(node => node.NodeId == step);
            Assert.AreEqual("Completed", replayed.Status, step + " " + replayed.Reason);
            Assert.HasCount(liveNode.Outputs.Count, replayed.Outputs);
            foreach (var output in replayed.Outputs)
            {
                Assert.IsFalse(output.Published);
                var expected = liveNode.Outputs.Single(item => item.Variant == output.Variant);
                Assert.AreEqual(expected.ArtifactId, output.ArtifactId);
                Assert.AreEqual(expected.OutputIdentitySha256, output.OutputIdentitySha256);
                Assert.AreEqual(expected.PayloadSha256, output.PayloadSha256);
                CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), output.SourceArtifactIds.ToArray());
            }
        }

        var ui = MeasurementOperations(provider, graphs);
        var reopened = await ui.GetExecutionDetailAsync(replay.Execution.ExecutionId, token).ConfigureAwait(false);
        Assert.IsTrue(reopened.IsSuccess, reopened.Message);
        Assert.AreEqual(JsonSerializer.Serialize(originalMeasurement), JsonSerializer.Serialize(reopened.Value?.ExposureMeasurement?.Measurement));
        if (!fit) return;

        var associationId = Assert.ContainsSingle(replay.Nodes.Single(node => node.NodeId == "associated").Outputs).ArtifactId;
        var associationRow = await store.ReadOutputByArtifactIdAsync(associationId, token).ConfigureAwait(false);
        Assert.IsNotNull(associationRow);
        var association = Assert.ContainsSingle(await persistence.RestoreWindowInputsAsync([associationRow], token).ConfigureAwait(false));
        var members = MeasuredStellarAssociationJson.Parse(association.Payload).Associations!;
        var detectionIds = members.Associations.Select(item => item.DetectionIndex)
            .Concat(members.UnassociatedDetections.Select(item => item.DetectionIndex)).Order().ToArray();
        CollectionAssert.AreEqual(originalMeasurement.Sources.Detections.Select(item => item.Index).ToArray(), detectionIds);
        var solutionId = replay.Nodes.Single(node => node.NodeId == "fit").Outputs.Single(output => output.Variant == "measured-astrometry-v1").ArtifactId;
        var solutionRow = await store.ReadOutputByArtifactIdAsync(solutionId, token).ConfigureAwait(false);
        Assert.IsNotNull(solutionRow);
        var restored = Assert.ContainsSingle(await persistence.RestoreWindowInputsAsync([solutionRow], token).ConfigureAwait(false));
        var solution = MeasuredAstrometryJson.Parse(restored.Payload);
        Assert.AreEqual(AstrometricAssessmentStatus.Rejected, solution.Assessment.Status, solution.Assessment.Reason);
        Assert.AreEqual(associationId, solution.MeasuredArtifactId);
    }

    private static async Task AssertMappedLinearAsync(ServiceProvider provider, IExposureProductReader reader,
        ReconstructionDescriptor raw, ExposureKey exposure, ExposureProductSlot measurementSlot,
        FrameStoredCodeTransform transform, (double Black, ushort White) expected)
    {
        var qualified = await reader.ReadLinearAsync(exposure, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(qualified?.Product, qualified?.Reason);
        Assert.IsNotNull(qualified.Provenance);
        Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly, qualified.Disposition);
        Assert.AreEqual(measurementSlot.ExecutionId, qualified.Product.Slot.ExecutionId);
        Assert.AreEqual(raw.Layout, qualified.Exposure.Geometry.Layout);
        Assert.AreEqual(transform, raw.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.NativeSample, raw.Layout.LevelCodeSpace);
        Assert.AreEqual(2800d, raw.Layout.WhiteLevel);
        Assert.AreEqual((double)expected.White, qualified.Provenance.RawWhiteThreshold?.StoredWhiteLevel);
        Assert.AreEqual((double)expected.White, qualified.Provenance.LinearWhiteThreshold?.StoredWhiteLevel);
        Assert.AreEqual(raw.Artifact.ChecksumSha256, qualified.Provenance.LinearPayloadSha256,
            "Normalization only must not change tight-row samples.");

        var input = await provider.GetRequiredService<CenteredExposureInputCustody>()
            .OpenLinearAsync(qualified.Product, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(input);
        await using (input.ConfigureAwait(false))
        {
            var layout = input.Input.Linear.Layout;
            Assert.IsNotNull(layout);
            Assert.AreEqual(expected.Black, layout.BlackLevel!.Value, 1e-10);
            Assert.AreEqual(raw.Layout with
            {
                LevelCodeSpace = FrameLevelCodeSpace.StoredContainer,
                BlackLevel = layout.BlackLevel,
                WhiteLevel = expected.White
            }, layout, "Only the level representation can differ on an already packed frame.");
            Assert.AreEqual(raw.Artifact.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(input.Input.Linear.Payload));
        }
    }

    private static async Task ExpireQualifiedLinearAsync(ServiceProvider provider, string root, IExposureProductReader reader,
        ExposureKey exposure, ExposureProductSlot measurementSlot, ExposureMeasurementV1 measurement)
    {
        var custody = provider.GetRequiredService<CenteredExposureInputCustody>();
        var qualified = await reader.ReadLinearAsync(exposure, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(qualified?.Product);
        Assert.IsNotNull(qualified.Provenance);
        Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly, qualified.Disposition);
        Assert.AreEqual(measurementSlot.ExecutionId, qualified.Product.Slot.ExecutionId);
        Assert.AreEqual(qualified.Product.ArtifactId, measurement.Input.Linear!.ArtifactId);
        Assert.AreEqual(qualified.Revision, measurement.Input.Step3Revision);
        var provenance = JsonSerializer.Serialize(qualified.Provenance);

        var input = await custody.OpenLinearAsync(qualified.Product, TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(input);
        await using (input.ConfigureAwait(false))
        {
            Assert.AreEqual(qualified.Provenance.LinearPayloadSha256,
                ProcessingIdentity.ComputePayloadSha256(input.Input.Linear.Payload));
        }

        await ExpireOnlyProductAsync(provider, root, qualified.Product.ArtifactId).ConfigureAwait(false);
        var expired = await reader.ReadLinearAsync(exposure, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(expired);
        Assert.AreEqual(LinearExposureDisposition.Expired, expired.Disposition);
        Assert.AreEqual("retention-expired", expired.Reason);
        Assert.AreEqual(qualified.Product, expired.Product);
        Assert.AreEqual(qualified.Revision, expired.Revision);
        Assert.AreEqual(provenance, JsonSerializer.Serialize(expired.Provenance));
        var reopened = await custody.OpenLinearAsync(qualified.Product, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        if (reopened is not null)
            await reopened.DisposeAsync().ConfigureAwait(false);
        Assert.IsNull(reopened, "An archived identity must not permit new pixel custody.");
    }

    private static async Task ExpireOnlyProductAsync(ServiceProvider provider, string root, Guid artifactId)
    {
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var stored = await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(stored);
        var payload = Path.Combine(root, stored.PayloadRelativePath);
        var sidecar = Path.Combine(root, stored.SidecarRelativePath);
        Assert.IsTrue(File.Exists(payload));
        Assert.IsTrue(File.Exists(sidecar));

        // Expire the named product while keeping the rest of the isolated test archive.
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
        var outputs = await store.ReadAvailableExpirationPageAsync(cutoff, null, null, 32, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(outputs.NextTimestamp);
        var retainedPaths = outputs.Items.Where(item => item.ArtifactId != artifactId)
            .SelectMany(item => new[] { Path.Combine(root, item.PayloadRelativePath), Path.Combine(root, item.SidecarRelativePath) })
            .ToHashSet(StringComparer.Ordinal);
        var deleted = await provider.GetRequiredService<CaptureProcessingPersistence>()
            .ExpireOutputsAsync(root, cutoff, retainedPaths, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, deleted);
        Assert.IsFalse(File.Exists(payload));
        Assert.IsFalse(File.Exists(sidecar));
        Assert.IsNull(await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false));
    }

    private static ServiceProvider MeasurementProvider(string root, DeploymentLocationSnapshot location, ICelestialCatalog catalog,
        ICaptureProcessingFaultInjector? fault = null, TimeProvider? timeProvider = null,
        Action<IServiceCollection>? configureServices = null)
        => ProcessingGraphOperationsTests.CreateProvider(root,
            timeProvider is null ? null : new Dictionary<string, string?> { ["CameraAgent:CaptureDistribution:MaximumAttempts"] = "2" },
            timeProvider, configureServices: services =>
        {
            HVO.SkyMonitor.CameraAgent.Program.AddCameraModules(services);
            services.AddSingleton(catalog);
            var locations = new Mock<IDeploymentLocationStore>();
            locations.Setup(store => store.Resolve(location.ToProvenance(), It.IsAny<DateTimeOffset?>())).Returns(location);
            services.AddSingleton(locations.Object);
            if (fault is not null)
                services.AddSingleton(fault);
            configureServices?.Invoke(services);
        });

    private static async Task CorruptMeasurementRunSnapshotAsync(string root, ExposureKey exposure)
    {
        using var database = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "journal", "raw-ingress.db") }.ToString());
        await database.OpenAsync().ConfigureAwait(false);
        using var update = database.CreateCommand();
        update.CommandText = "UPDATE centered_exposure_runs SET snapshot_json = X'7B7D' WHERE run_id = $run;";
        update.Parameters.AddWithValue("$run", exposure.RunId.ToString("N"));
        Assert.AreEqual(1, await update.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private sealed class HistoricalMonoMeasurementExecutor : IProcessingRecipeExecutor
    {
        private readonly ProcessingRecipeExecutor _executor = new();

        public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request, CancellationToken cancellationToken = default)
        {
            if (request.RecipeName == BuiltInProcessingRecipes.ExposureMeasurement)
                request = request with { Options = request.Options.GetProperty("input") };
            return _executor.ExecuteAsync(request, cancellationToken);
        }
    }

    private sealed class MeasurementPublicationIo(int failures) : ICaptureProcessingFaultInjector
    {
        public int Attempts { get; private set; }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (nodeId == ExposureMeasurementProducts.StepAlias && point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit &&
                ++Attempts <= failures)
                throw new IOException("Injected transient step-four publication I/O.");
        }
    }

    private sealed class MeasurementCommitInterruption(CancellationTokenSource cancellation) : ICaptureProcessingFaultInjector
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (nodeId != ExposureMeasurementProducts.StepAlias || cancellation.IsCancellationRequested)
                return;
            if (point == CaptureProcessingFaultPoint.BeforeNodeExecution)
            {
                Entered.TrySetResult();
                Resume.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            }
            if (point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
    }

    private sealed class MeasurementPredictionCatalog(CelestialCatalogObject star) : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _catalog = new([star]);

        public CatalogMetadata Metadata { get; } = new("measurement-prediction", "fixture", new Uri("https://example.invalid/catalog"),
            new string('A', 64), "fixture", "2");

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _catalog.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(CatalogCandidateQuery query,
            CancellationToken cancellationToken = default) => _catalog.QueryCandidatesAsync(query, cancellationToken);
    }

    private static CameraAgentProcessingGraphUiService MeasurementOperations(ServiceProvider provider, IProcessingGraphOperations operations)
    {
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(service => service.GetAuthenticationStateAsync()).ReturnsAsync(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity("test"))));
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());
        return new(authentication.Object, authorization.Object, operations,
            provider.GetRequiredService<ICaptureProcessingPipelineFactory>(),
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
            provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>(), TimeProvider.System,
            NullLogger<CameraAgentProcessingGraphUiService>.Instance, provider.GetRequiredService<IExposureMeasurementRunReader>());
    }
}
