using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, false)]
    [DataRow(CameraPixelFormat.BayerRggb16, true)]
    [DataRow(CameraPixelFormat.BayerRggb16, false)]
    public async Task CenteredRuntime_RegistersActualScheduledCapturesAndKeepsOtherWindowHolds(CameraPixelFormat format, bool wideField)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-live");
        var token = CancellationToken.None;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, format, size: wideField ? 224 : 160);
        var catalog = wideField ? CenteredRuntimeWideCatalog(start, profile) : CenteredRuntimeCatalog(start);
        var expectedDisposition = format == CameraPixelFormat.BayerRggb16 && !wideField
            ? CenteredStackDisposition.SingleExposure : CenteredStackDisposition.Registered;
        // Replay the request and shutter drift from the ordinary 2 s MinimumStartInterval browser trace.
        double[] requestDrift = format == CameraPixelFormat.Mono16
            ? [0, 38.697, 56.076, 71.545, 95] : [0, 34.997, 58.881, 82.823, 95];
        double[] shutterDelay = format == CameraPixelFormat.Mono16
            ? [46.372, 17.549, 40.764, 16.404, 25] : [35.046, 23.937, 49.216, 19.931, 25];
        try
        {
            var ids = new List<Guid>();
            using (var provider = CenteredRuntimeProvider(root, clock))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var resources = new CenteredSceneResources(null, null, null, null, "centered-live-test");
                var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
                for (var index = 0; index < 5; index++)
                {
                    var scheduled = start.AddSeconds(2 * index).AddMilliseconds(requestDrift[index]);
                    var shutter = scheduled.AddMilliseconds(shutterDelay[index]);
                    clock.UtcNow = scheduled;
                    deviceClock.Advance(shutter - deviceClock.GetUtcNow());
                    var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
                    var request = new CaptureRequest(scheduled, TimeSpan.FromSeconds(2), CaptureMode.Still,
                        new(TimeSpan.FromMilliseconds(250), 1, null, null))
                    {
                        BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                        BoundGraph = reservation.Revision.Pipeline
                    };
                    await ingress.DispatchCenteredAsync(reservation.Pending.Key, request.RequestedSetpoint!, null,
                        "centered-live-test", token, scheduled, request.TargetInterval).ConfigureAwait(false);
                    var captured = await module.CaptureBoundAsync(reservation.Pending.Key, request, token).ConfigureAwait(false);
                    if (wideField)
                        AssertWideFieldBelowClipping(captured.Frame);
                    clock.UtcNow = captured.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
                    Assert.AreEqual(shutter, captured.AcquisitionTiming.ExposureStartedUtc);
                    var receipt = await ingress.AcceptCenteredAsync(active, new(request, captured, scheduled, request.TargetInterval,
                        captured.AcquisitionTiming.ReadoutCompletedUtc - scheduled), reservation, token).ConfigureAwait(false);
                    ids.Add(receipt.Manifest.Descriptor.Capture.CaptureId);
                    await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
                    await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
                }

                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var closed = await store.ReadCenteredWindowByReferenceAsync(ids[2], token).ConfigureAwait(false);
                Assert.IsNotNull(closed, "The fifth committed exposure should close 103 before its original deadline.");
                Assert.IsLessThan(closed.Closure.Schedule.DeadlineUtc, closed.Closure.EvidenceCutoffUtc);
                Assert.AreEqual(start.AddSeconds(4).AddMilliseconds(requestDrift[2]).AddSeconds(9.25),
                    closed.Closure.Schedule.DeadlineUtc);
                CollectionAssert.AreEqual(ids, closed.Closure.Slots.Select(slot => slot.Input!.Key.CaptureId).ToList());
                CollectionAssert.AreEqual(requestDrift.Select((drift, index) => start.AddSeconds(2 * index).AddMilliseconds(drift)).ToArray(),
                    closed.Closure.Slots.Select(slot => slot.Time.ExpectedStartUtc).ToArray());
                Assert.AreEqual(ids[2], closed.Closure.Center.Input!.Key.CaptureId);
                Assert.AreEqual(3L, await ReadCenteredTestHoldCountAsync(root, ids[4], token).ConfigureAwait(false));
            }

            if (expectedDisposition == CenteredStackDisposition.Registered)
            {
                var interruption = new CenteredRuntimeFault { FailRegistrationPublication = true };
                using var interrupted = CenteredRuntimeProvider(root, clock,
                    services => services.AddSingleton<ICaptureProcessingFaultInjector>(interruption));
                await interrupted.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var interruptedStore = interrupted.GetRequiredService<SqliteCaptureProcessingStore>();
                var frozen = await interruptedStore.ReadCenteredWindowByReferenceAsync(ids[2], token).ConfigureAwait(false);
                Assert.IsNotNull(frozen);
                var inputs = interrupted.GetRequiredService<ICenteredWindowRegistrationSource>();
                var registration = await CenteredWindowRegistrationResult.ExecuteAsync(frozen.Closure, inputs, token).ConfigureAwait(false);
                Assert.IsNotNull(registration.Result, registration.Outcome.ReasonCode);
                Assert.HasCount(format == CameraPixelFormat.Mono16 ? 3 : 5, registration.Result.Publication.RegistrationArtifactIds);
                var center = await inputs.ReadExposureAsync(frozen.Closure.Center.Input!.Key, token).ConfigureAwait(false);
                Assert.IsNotNull(center);
                var writer = interrupted.GetRequiredService<CaptureProcessingPersistence>();
                await Assert.ThrowsExactlyAsync<IOException>(() => writer.PersistCenteredRegistrationAsync(registration.Result,
                    center.Raw, interrupted.GetRequiredService<ICenteredWindowJournal>(), token).AsTask()).ConfigureAwait(false);
                var pending = await interruptedStore.ReadCenteredWindowByReferenceAsync(ids[2], token).ConfigureAwait(false);
                Assert.IsNull(pending!.Publication);
                Assert.IsEmpty(await ReadCenteredRegistrationArtifactIdsAsync(root, ids[2], token).ConfigureAwait(false));
                Assert.AreEqual(3L, await ReadCenteredTestHoldCountAsync(root, ids[4], token).ConfigureAwait(false));
            }

            using var reopened = CenteredRuntimeProvider(root, clock,
                services => services.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning)));
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var reopenedStore = reopened.GetRequiredService<SqliteCaptureProcessingStore>();
            await reopened.GetRequiredService<CenteredWindowRegistrationWorker>().RegisterPendingAsync(token).ConfigureAwait(false);
            var disposition = await reopenedStore.ReadCenteredWindowByReferenceAsync(ids[2], token).ConfigureAwait(false);
            Assert.IsNotNull(disposition?.Publication, disposition?.FailureReason);
            CollectionAssert.AreEquivalent(disposition.Publication.RegistrationArtifactIds.ToArray(),
                (await ReadCenteredRegistrationArtifactIdsAsync(root, ids[2], token).ConfigureAwait(false)).ToArray());
            var source = reopened.GetRequiredService<ICenteredWindowRegistrationSource>();
            var result = await CenteredWindowRegistrationResult.ExecuteAsync(disposition.Closure, source, token).ConfigureAwait(false);
            var evidence = result.Result!.Evidence;
            Assert.AreEqual(expectedDisposition, disposition.Publication.StackDisposition,
                JsonSerializer.Serialize(evidence.Sources));
            var contributors = disposition.Publication.ContributingCaptureIds;
            TestContext.WriteLine($"{format}, wide field={wideField}: {evidence.Disposition}. " +
                $"Contributor offsets [{string.Join(", ", evidence.Sources.Where(slot => slot.Method is not null).Select(slot => slot.Offset))}]; " +
                $"integration {TimeSpan.FromTicks(evidence.TotalIntegrationTicks).TotalMilliseconds} ms; " +
                $"verified coverage {evidence.ValidPixelCount} pixels.");
            foreach (var slot in evidence.Sources)
                TestContext.WriteLine($"{slot.Offset}: {slot.CaptureId} {slot.Reason}, {slot.Method}, measurement {slot.MeasurementStatus}, " +
                    $"verification {slot.PairCheck?.Status}, fit points {slot.PairCheck?.Fit.Count}, withheld points {slot.PairCheck?.Verification.Count}");
            if (expectedDisposition == CenteredStackDisposition.SingleExposure)
            {
                Assert.AreEqual(ids[2], Assert.ContainsSingle(contributors));
                Assert.IsEmpty(evidence.Outputs);
                Assert.HasCount(1, result.Result.Products);
                foreach (var slot in disposition.Closure.Slots)
                {
                    var pin = slot.Products?.RegistrationPin;
                    Assert.IsNotNull(pin?.Measurement);
                    var artifact = await source.ReadArtifactAsync(pin.Key, pin.Measurement, token).ConfigureAwait(false);
                    Assert.IsNotNull(artifact);
                    Assert.AreEqual(pin.Measurement, CenteredStackArtifactPin.From(artifact));
                    Assert.AreEqual(ExposureMeasurementStatus.Usable, evidence.Sources[slot.Offset + 2].MeasurementStatus);
                }
                Assert.IsTrue(evidence.Sources.Where(slot => slot.Offset != 0).All(slot =>
                    slot.Reason == CenteredRegistrationReason.RegistrationRejected &&
                    slot.PairCheck is { Status: not DirectRegistrationStatus.Verified }));
                Assert.AreEqual(TimeSpan.FromMilliseconds(250).Ticks, evidence.TotalIntegrationTicks);
                return;
            }
            Assert.IsGreaterThanOrEqualTo(3, contributors.Length, JsonSerializer.Serialize(evidence.Sources));
            if (format == CameraPixelFormat.Mono16)
                CollectionAssert.AreEqual(ids, contributors.ToList());
            else
                CollectionAssert.AreEqual(ids.Where(id => contributors.Contains(id)).ToArray(), contributors.ToArray());
            Assert.Contains(ids[2], contributors);
            Assert.AreEqual(2L, await ReadCenteredTestHoldCountAsync(root, ids[4], token).ConfigureAwait(false));
            var slot105 = disposition.Closure.Slots[4];
            var pin105 = slot105.Products!.RegistrationPin!;
            var admitted105 = await source.ReadExposureAsync(pin105.Key, token).ConfigureAwait(false);
            Assert.IsNotNull(admitted105);
            var rawPaths = RawIngressFileStore.GetPaths(root, admitted105.Raw.Timing.ExposureStartedUtc, pin105.Raw.ArtifactId);
            var unheldPayload = rawPaths.PayloadAbsolutePath + ".retention-control";
            var unheldSidecar = rawPaths.SidecarAbsolutePath + ".retention-control";
            File.Copy(rawPaths.PayloadAbsolutePath, unheldPayload);
            File.Copy(rawPaths.SidecarAbsolutePath, unheldSidecar);
            var retentionConfiguration = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(reopened, profile)
                .ConfigureAwait(false);
            // Exercise an age-eligible sweep without expiring the independent active owner leases.
            var retentionClock = new CenteredClock { UtcNow = clock.UtcNow.AddDays(45) };
            using var retention = ActivatorUtilities.CreateInstance<RetentionBackgroundService>(reopened, retentionClock);
            await retention.ApplyRetentionAsync(retentionConfiguration, token).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(unheldPayload));
            Assert.IsFalse(File.Exists(unheldSidecar));
            var raw105 = await source.ReadArtifactAsync(pin105.Key, pin105.Raw, token).ConfigureAwait(false);
            var linear105 = await source.ReadArtifactAsync(pin105.Key, pin105.Linear, token).ConfigureAwait(false);
            Assert.IsNotNull(raw105);
            Assert.IsNotNull(linear105);
            Assert.AreEqual(pin105.Raw.PayloadSha256, ProcessingIdentity.ComputePayloadSha256(raw105.Payload));
            Assert.AreEqual(pin105.Linear, CenteredStackArtifactPin.From(linear105));
            Assert.AreEqual(pin105.Raw, CenteredStackArtifactPin.From(raw105));
            var mask105 = CenteredStackValidityMask.OpenAllValid(CenteredStackArtifactHeader.FromArtifact(raw105),
                CenteredStackArtifactHeader.FromArtifact(linear105), pin105.ValidityMask, linear105.CreatedUtc, token);
            Assert.AreEqual(pin105.ValidityMask, CenteredStackArtifactPin.From(mask105));
            Assert.IsFalse(mask105.Payload.Span.ContainsAnyExcept((byte)1));
            Assert.AreEqual(TimeSpan.FromMilliseconds(250 * contributors.Length).Ticks, evidence.TotalIntegrationTicks);
            Assert.AreEqual(CenteredRegistrationMethod.ReferenceIdentity, evidence.Sources[2].Method);
            Assert.HasCount(format == CameraPixelFormat.Mono16 ? 2 : 4, evidence.Outputs);
            Assert.IsGreaterThan(0, evidence.ValidPixelCount);
            var support = result.Result.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.SupportMediaType);
            var contributedMask = (byte)evidence.Sources.Where(item => item.Reason == CenteredRegistrationReason.Contributed)
                .Sum(item => 1 << (item.Offset + 2));
            Assert.Contains(contributedMask, support.Payload.ToArray());
            Assert.AreEqual(evidence.TotalIntegrationTicks, evidence.IntegrationTicksAt(contributedMask));
            var durableProduct = result.Result.Products.Single(product =>
                product.SchemaVersion == CenteredRegisteredStackEvidence.CurrentSchemaVersion);
            var persistence = reopened.GetRequiredService<CaptureProcessingPersistence>();
            var referenceKey = disposition.Closure.Center.Input!.Key;
            var reference = await reopened.GetRequiredService<RawCaptureIngress>().ReadCenteredRawAsync(referenceKey, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(reference);
            foreach (var plane in result.Result.Products.Where(product => product.Layout is not null))
            {
                var artifactId = ProcessingIdentity.CreateArtifactId(plane.OutputIdentitySha256);
                var descriptor = DerivativeDescriptorFactory.Create(reference.Manifest.Descriptor, artifactId,
                    CenteredWindowCoordinator.RegistrationProducer, plane);
                var retainedPlane = await reopenedStore.ReadOutputByArtifactIdAsync(artifactId, token).ConfigureAwait(false);
                Assert.IsNotNull(retainedPlane);
                var header = CenteredWindowArtifactHeader.FromOutput(retainedPlane);
                Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(descriptor), header.DescriptorIdentitySha256);
                var reopenedPlane = await persistence.ReadCenteredRetainedProductAsync(referenceKey,
                    CenteredStackArtifactPin.FromHeader(CenteredStackArtifactHeader.FromArtifact(header), plane.ChecksumSha256), token).ConfigureAwait(false);
                Assert.IsNotNull(reopenedPlane);
                Assert.AreEqual(plane.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(reopenedPlane.Payload));
            }
            var retainedEvidence = await reopenedStore.ReadOutputByArtifactIdAsync(disposition.Publication.EvidenceArtifactId, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(retainedEvidence);
            var retained = await persistence.ReadCenteredRetainedProductAsync(referenceKey,
                CenteredStackArtifactPin.FromHeader(
                    CenteredStackArtifactHeader.FromArtifact(CenteredWindowArtifactHeader.FromOutput(retainedEvidence)), durableProduct.ChecksumSha256),
                token).ConfigureAwait(false);
            Assert.IsNotNull(retained);
            Assert.AreEqual(durableProduct.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(retained.Payload));
            var replayId = Guid.NewGuid();
            var replay = await CenteredWindowRegistrationResult.RecomputeAsync(disposition.Closure, source, replayId, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(replay.Result, replay.Outcome.ReasonCode);
            Assert.AreEqual(replayId, replay.Result.RecomputationId);
            Assert.AreNotEqual(disposition.Publication.PlanSha256, replay.Result.Publication.PlanSha256);
            Assert.AreNotEqual(disposition.Publication.EvidenceArtifactId, replay.Result.Publication.EvidenceArtifactId);
            var stillLive = await reopened.GetRequiredService<ICenteredWindowReader>().ReadAsync(ids[2], token).ConfigureAwait(false);
            Assert.AreEqual(disposition.Publication.EvidenceArtifactId, stillLive!.EvidenceArtifactId);

            var reopenedIngress = reopened.GetRequiredService<RawCaptureIngress>();
            var nextModule = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                stagingStore: reopened.GetRequiredService<IProjectedSceneStagingStore>());
            await using var nextCamera = nextModule.ConfigureAwait(false);
            await nextModule.InitializeAsync(retentionConfiguration, token).ConfigureAwait(false);
            var next = await CaptureCenteredRuntimeAttemptAsync(reopenedIngress, nextModule, deviceClock, clock, retentionConfiguration,
                start.AddSeconds(10).AddMilliseconds(115), 1, token).ConfigureAwait(false);
            var receipt106 = await reopenedIngress.AcceptCenteredAsync(retentionConfiguration, next.Submission, next.Reservation, token)
                .ConfigureAwait(false);
            ids.Add(receipt106.Manifest.Descriptor.Capture.CaptureId);
            await ProcessCenteredRuntimeExposureAsync(reopened, retentionConfiguration, token).ConfigureAwait(false);
            await reopened.GetRequiredService<CenteredWindowCoordinator>().CloseReadyAsync(token).ConfigureAwait(false);
            await reopened.GetRequiredService<CenteredWindowRegistrationWorker>().RegisterPendingAsync(token).ConfigureAwait(false);
            var nextDisposition = await reopenedStore.ReadCenteredWindowByReferenceAsync(ids[3], token).ConfigureAwait(false);
            Assert.IsNotNull(nextDisposition?.Publication, nextDisposition?.FailureReason);
            Assert.AreEqual(CenteredStackDisposition.Registered, nextDisposition.Publication.StackDisposition);
            CollectionAssert.AreEqual(ids.Skip(1).ToArray(), nextDisposition.Closure.Slots.Select(slot => slot.Input!.Key.CaptureId).ToArray());
            Assert.AreEqual(ids[3], nextDisposition.Closure.Center.Input!.Key.CaptureId);
            Assert.AreNotEqual(disposition.Publication.EvidenceArtifactId, nextDisposition.Publication.EvidenceArtifactId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredRuntime_ClosesOnOutputlessMeasurementAndReopensFrozenReason(bool closeAfterDeadline)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-outputless-measurement");
        var token = CancellationToken.None;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16, size: 128);
        var fault = new CenteredRuntimeFault();
        var ids = new List<ExposureKey>();
        string? terminalReason = null;
        Guid measurementExecutionId;
        DateTimeOffset deadline;
        try
        {
            using (var provider = CenteredRuntimeProvider(root, clock, services =>
                services.AddSingleton<ICaptureProcessingFaultInjector>(fault), maximumAttempts: 2))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);

                for (var index = 0; index < 5; index++)
                {
                    var exposure = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                        start.AddSeconds(2 * index), 1, token).ConfigureAwait(false);
                    var receipt = await ingress.AcceptCenteredAsync(active, exposure.Submission, exposure.Reservation, token)
                        .ConfigureAwait(false);
                    var attempt = exposure.Reservation.Pending.Key;
                    ids.Add(new(attempt.Camera, attempt.RunId, receipt.Manifest.Descriptor.Capture.CaptureId));
                    if (index == 4)
                    {
                        fault.FailMeasurementPublication = true;
                        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
                        var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
                        var first = await laneStore.ClaimAsync(lane, "centered-live-test", active, token).ConfigureAwait(false);
                        Assert.IsNotNull(first);
                        var retry = await provider.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(first.Context, token)
                            .ConfigureAwait(false);
                        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, retry.Outcome);
                        Assert.AreEqual("measurement-publication-io", retry.Reason);
                        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure,
                            await laneStore.FailAsync(first, retry, token).ConfigureAwait(false));
                        var stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(attempt.Camera, token).ConfigureAwait(false));
                        Assert.IsNotNull(stream);
                        await provider.GetRequiredService<CenteredViewPublicationWorker>()
                            .DrainCameraAsync(attempt.Camera, stream, CancellationToken.None, token).ConfigureAwait(false);
                        var views = provider.GetRequiredService<SqliteCenteredViews>();
                        var pending = Assert.ContainsSingle(await views.ReadArchiveAsync(ids[4].CaptureId, token).ConfigureAwait(false));
                        var view = await views.ReadRevisionAsync(pending.Base.BaseId, pending.LatestRevisionId, token).ConfigureAwait(false);
                        Assert.IsNotNull(view);
                        Assert.AreEqual(CenteredViewReadiness.Waiting, view.Revision.Layers.Single(layer =>
                            layer.Key.Kind == "measured-source-markers").Readiness);
                        Assert.AreEqual(CenteredViewReadiness.NotConfigured, view.Revision.Layers.Single(layer =>
                            layer.Key.Kind == "predicted-satellites").Readiness);
                        await provider.GetRequiredService<CenteredWindowCoordinator>().CloseReadyAsync(token).ConfigureAwait(false);
                        var beforeRetry = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                        Assert.IsNull(await beforeRetry.ReadCenteredWindowByReferenceAsync(ids[2].CaptureId, token).ConfigureAwait(false));
                        clock.UtcNow = clock.UtcNow.AddSeconds(2);
                    }
                    await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
                }

                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var inputs = provider.GetRequiredService<IExposureProductReader>();
                var end = await inputs.ReadLinearAsync(ids[4], token).ConfigureAwait(false);
                Assert.IsNotNull(end?.Product);
                measurementExecutionId = end.Product.Slot.ExecutionId;
                var slot = new ExposureProductSlot(measurementExecutionId, ExposureMeasurementProducts.StepAlias,
                    ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);
                Assert.IsNull(await inputs.ReadProductAsync(ids[4], slot, token).ConfigureAwait(false));
                var failed = await inputs.ReadProductAsync(ids[4], slot with
                {
                    SchemaVersion = CenteredViewLayerBindings.DispositionPrefix + slot.SchemaVersion
                }, token).ConfigureAwait(false);
                Assert.IsNotNull(failed);
                Assert.AreEqual(ExposureProductAvailability.Failed, failed.Availability);
                Assert.IsNull(failed.Product);
                var execution = await store.ReadExecutionDetailAsync(slot.ExecutionId, token).ConfigureAwait(false);
                var terminal = execution!.Nodes.Single(node => node.NodeId == slot.Producer);
                Assert.AreEqual("TerminalFailure", terminal.Status);
                Assert.IsTrue(terminal.CompletedUtc <= clock.UtcNow, JsonSerializer.Serialize(terminal));
                terminalReason = terminal.Reason;
                Assert.AreEqual("measurement-publication-io", terminalReason);
                Assert.AreEqual(2, terminal.AttemptCount);
                Assert.IsEmpty(terminal.Outputs);
                var open = await provider.GetRequiredService<ICenteredWindowReader>().ReadAsync(ids[2].CaptureId, token)
                    .ConfigureAwait(false);
                Assert.IsNotNull(open);
                deadline = open.DeadlineUtc;
                Assert.IsTrue(terminal.CompletedUtc < deadline);
                if (!closeAfterDeadline)
                {
                    var linear = await store.ReadOutputByArtifactIdAsync(end.Product.ArtifactId, token).ConfigureAwait(false);
                    Assert.IsNotNull(linear);
                    var payload = Path.Combine(root, linear.PayloadRelativePath);
                    var temporarilyUnavailable = payload + ".closure-probe";
                    File.Move(payload, temporarilyUnavailable);
                    try
                    {
                        await provider.GetRequiredService<CenteredWindowCoordinator>().CloseReadyAsync(token).ConfigureAwait(false);
                    }
                    finally
                    {
                        File.Move(temporarilyUnavailable, payload);
                    }
                }
            }

            if (closeAfterDeadline)
                clock.UtcNow = deadline.AddTicks(1);
            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            if (closeAfterDeadline)
                await reopened.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
            var reopenedStore = reopened.GetRequiredService<SqliteCaptureProcessingStore>();
            var publisher = reopened.GetRequiredService<CenteredViewPublicationWorker>();
            await publisher.DrainCameraAsync(ids[4].Camera, null, CancellationToken.None, token).ConfigureAwait(false);
            var reopenedViews = reopened.GetRequiredService<SqliteCenteredViews>();
            var archive = Assert.ContainsSingle(await reopenedViews.ReadArchiveAsync(ids[4].CaptureId, token).ConfigureAwait(false));
            var latestView = await reopenedViews.ReadRevisionAsync(archive.Base.BaseId, archive.LatestRevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(latestView);
            var settledLayer = latestView.Revision.Layers.Single(layer => layer.Key.Kind == "measured-source-markers");
            Assert.AreEqual(CenteredViewReadiness.Incompatible, settledLayer.Readiness);
            Assert.AreEqual("centered.product-failed/measurement-publication-io", settledLayer.Reason);
            Assert.IsNull(settledLayer.Drawable);
            await publisher.DrainCameraAsync(ids[4].Camera, null, CancellationToken.None, token).ConfigureAwait(false);
            Assert.AreEqual(archive.LatestRevisionId, Assert.ContainsSingle(await reopenedViews
                .ReadArchiveAsync(ids[4].CaptureId, token).ConfigureAwait(false)).LatestRevisionId);
            var recovered = await reopenedStore.ReadCenteredWindowByReferenceAsync(ids[2].CaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(deadline, recovered.Closure.Schedule.DeadlineUtc);
            var latest = recovered.Closure.Slots[4];
            Assert.AreEqual(CenteredWindowSlotState.Eligible, latest.State);
            var frozenMeasurement = latest.Products!.Measurement?.Lookup;
            if (closeAfterDeadline)
            {
                Assert.AreEqual(deadline, recovered.Closure.EvidenceCutoffUtc);
                Assert.IsNull(frozenMeasurement, "The terminal attempt completion clock is not proof of commit before the cutoff.");
            }
            else
            {
                Assert.IsLessThan(deadline, recovered.Closure.EvidenceCutoffUtc);
                Assert.IsNotNull(frozenMeasurement);
                Assert.AreEqual(ExposureProductAvailability.Failed, frozenMeasurement.Availability);
                Assert.AreEqual(ids[4], frozenMeasurement.Exposure);
                Assert.AreEqual(terminalReason, frozenMeasurement.Reason);
                Assert.IsNull(frozenMeasurement.Product);
            }

            var detail = await reopenedStore.ReadExecutionDetailAsync(measurementExecutionId, token).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            var operations = await reopened.GetRequiredService<IExposureMeasurementRunReader>().ReadAsync(detail, token).ConfigureAwait(false);
            Assert.IsNotNull(operations);
            Assert.IsNull(operations.Measurement);
            Assert.AreEqual(terminalReason, operations.UnavailableReason);

            Assert.IsNull(latest.Products.RegistrationPin!.Measurement);
            var source = reopened.GetRequiredService<ICenteredWindowRegistrationSource>();
            var linearPin = latest.Products.RegistrationPin.Linear;
            var retained = await source.ReadArtifactAsync(ids[4], linearPin, token).ConfigureAwait(false);
            Assert.IsNotNull(retained);
            Assert.AreEqual(linearPin, CenteredStackArtifactPin.From(retained));
            var again = await reopenedStore.ReadCenteredWindowByReferenceAsync(ids[2].CaptureId, token).ConfigureAwait(false);
            Assert.AreEqual(recovered.Closure.Identity, again!.Closure.Identity);
            Assert.AreEqual(frozenMeasurement, again.Closure.Slots[4].Products?.Measurement?.Lookup);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task CenteredRuntime_RecoversOriginalDeadlineAndIsolatesLateOrUncertainAttempt(
        bool uncertain104, bool switchWith104InFlight)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-runtime-recovery");
        var token = CancellationToken.None;
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var fault = new CenteredRuntimeFault();
        var ids = new List<Guid>();
        var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore());
        await using var camera = module.ConfigureAwait(false);
        try
        {
            using (var initial = CenteredRuntimeProvider(root, clock, services =>
                services.AddSingleton<IRawIngressFaultInjector>(fault)))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(initial, profile).ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var ingress = initial.GetRequiredService<RawCaptureIngress>();
                for (var index = 0; index < 3; index++)
                {
                    var reserved = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                        start.AddSeconds(index * 2), 1, token).ConfigureAwait(false);
                    if (index == 2)
                    {
                        fault.InterruptAdmission = true;
                        await Assert.ThrowsExactlyAsync<IOException>(() =>
                            ingress.AcceptCenteredAsync(active, reserved.Submission, reserved.Reservation, token)).ConfigureAwait(false);
                        ids.Add(RawCaptureDescriptorFactory.CreateStableIds(active, reserved.Submission).CaptureId);
                        continue;
                    }

                    var receipt = await ingress.AcceptCenteredAsync(active, reserved.Submission, reserved.Reservation, token)
                        .ConfigureAwait(false);
                    ids.Add(receipt.Manifest.Descriptor.Capture.CaptureId);
                    await ProcessCenteredRuntimeExposureAsync(initial, active, token).ConfigureAwait(false);
                }
            }

            using var reopened = CenteredRuntimeProvider(root, clock,
                services => services.AddSingleton<ICaptureProcessingFaultInjector>(fault));
            var resumed = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(reopened, profile).ConfigureAwait(false);
            var live = reopened.GetRequiredService<RawCaptureIngress>();
            await ProcessCenteredRuntimeExposureAsync(reopened, resumed, token).ConfigureAwait(false);
            var reader = reopened.GetRequiredService<ICenteredWindowReader>();
            var open = await reader.ReadAsync(ids[2], token).ConfigureAwait(false);
            Assert.IsNotNull(open);
            Assert.IsNull(open.ClosureIdentity);
            fault.WindowToInterrupt = open.WindowIdentity;

            var exposure104 = await CaptureCenteredRuntimeAttemptAsync(live, module, deviceClock, clock, resumed,
                start.AddSeconds(6), uncertain104 ? 7 : 1, token).ConfigureAwait(false);
            if (switchWith104InFlight)
            {
                await live.ReconcileCenteredControlAsync(resumed with { CenteredExposure = null }, token,
                    new(resumed.AgentId!, resumed.CenteredExposure!.CameraId)).ConfigureAwait(false);
                await reopened.GetRequiredService<CenteredWindowCoordinator>().CloseReadyAsync(token).ConfigureAwait(false);
            }

            if (uncertain104)
            {
                Assert.AreEqual(7d, exposure104.Submission.Result.Frame!.Metadata.Gain);
                // The camera applied the settings and completed acquisition. Its result never reached durable admission.
                await live.ReconcileCenteredControlAsync(resumed, token).ConfigureAwait(false);
            }
            else
            {
                var receipt = await live.AcceptCenteredAsync(resumed, exposure104.Submission, exposure104.Reservation, token)
                    .ConfigureAwait(false);
                ids.Add(receipt.Manifest.Descriptor.Capture.CaptureId);
                await ProcessCenteredRuntimeExposureAsync(reopened, resumed, token).ConfigureAwait(false);
            }

            var exposure105 = await CaptureCenteredRuntimeAttemptAsync(live, module, deviceClock, clock, resumed,
                start.AddSeconds(8), 1, token).ConfigureAwait(false);
            Assert.AreEqual(1d, exposure105.Submission.Result.Frame!.Metadata.Gain);
            Assert.AreEqual(uncertain104 || switchWith104InFlight, exposure104.Reservation.Pending.Key.RunId != exposure105.Reservation.Pending.Key.RunId);
            clock.UtcNow = open.DeadlineUtc.AddMilliseconds(1);
            var coordinator = reopened.GetRequiredService<CenteredWindowCoordinator>();
            await coordinator.CloseDueAsync(token).ConfigureAwait(false);
            Assert.IsTrue(fault.InterruptedClosure);
            Assert.IsNull((await reader.ReadAsync(ids[2], token).ConfigureAwait(false))!.ClosureIdentity);

            // Reopen the process state after the interrupted close. The held 105
            // becomes visible only after the original 103 deadline.
            await reopened.DisposeAsync().ConfigureAwait(false);
            using var afterCloseFault = CenteredRuntimeProvider(root, clock);
            resumed = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(afterCloseFault, profile).ConfigureAwait(false);
            live = afterCloseFault.GetRequiredService<RawCaptureIngress>();
            reader = afterCloseFault.GetRequiredService<ICenteredWindowReader>();
            coordinator = afterCloseFault.GetRequiredService<CenteredWindowCoordinator>();
            var late = await live.AcceptCenteredAsync(resumed, exposure105.Submission, exposure105.Reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(afterCloseFault, resumed, token).ConfigureAwait(false);
            await coordinator.CloseDueAsync(token).ConfigureAwait(false);
            await afterCloseFault.GetRequiredService<CenteredWindowRegistrationWorker>().RegisterPendingAsync(token).ConfigureAwait(false);

            var closed = await reader.ReadAsync(ids[2], token).ConfigureAwait(false);
            Assert.IsNotNull(closed?.ClosureIdentity);
            Assert.AreEqual(open.DeadlineUtc, closed.DeadlineUtc);
            Assert.AreEqual(open.DeadlineUtc, closed.EvidenceCutoffUtc);
            Assert.AreEqual(ids[2], closed.Slots[2].Exposure!.CaptureId);
            Assert.IsFalse(closed.Slots.Any(slot => slot.Exposure?.CaptureId == late.Manifest.Descriptor.Capture.CaptureId));
            Assert.AreEqual(uncertain104 ? CenteredWindowSlotState.UncertainAttempt : CenteredWindowSlotState.Eligible,
                closed.Slots[3].State);
            Assert.AreEqual(uncertain104 || switchWith104InFlight ? CenteredWindowSlotState.OutsideRun : CenteredWindowSlotState.MissingExposure,
                closed.Slots[4].State);

            if (uncertain104)
            {
                var attempt = exposure104.Reservation.Pending.Key;
                var unseen = new CenteredWindowSchedule(attempt.Camera, attempt.RunId, attempt.RunId.ToString("N"),
                    open.Slots[3].ExpectedStartUtc, TimeSpan.Zero, open.DeadlineUtc,
                    CenteredRegisteredStackRecipe.FixedIdentity.IdentitySha256, open.DeadlineUtc.AddSeconds(30));
                clock.UtcNow = open.DeadlineUtc.AddSeconds(2).AddMilliseconds(1);
                await coordinator.CloseDueAsync(token).ConfigureAwait(false);
                var missing = await afterCloseFault.GetRequiredService<SqliteCaptureProcessingStore>()
                    .ReadCenteredWindowDispositionAsync(unseen.Identity, token).ConfigureAwait(false);
                Assert.IsTrue(missing!.IsTerminal);
                Assert.AreEqual(CenteredWindowSlotState.UncertainAttempt, missing.Closure.Center.State);
                Assert.IsNull(missing.Publication);
            }

            var exposure106 = await CaptureCenteredRuntimeAttemptAsync(live, module, deviceClock, clock, resumed,
                start.AddSeconds(10), 1, token).ConfigureAwait(false);
            await live.AcceptCenteredAsync(resumed, exposure106.Submission, exposure106.Reservation, token).ConfigureAwait(false);
            await live.ReconcileCenteredControlAsync(resumed with { CenteredExposure = null }, token,
                    new(resumed.AgentId!, resumed.CenteredExposure!.CameraId)).ConfigureAwait(false);
            await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
            Assert.AreEqual(closed.ClosureIdentity, (await reader.ReadAsync(ids[2], token).ConfigureAwait(false))!.ClosureIdentity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DataRow(false, true)]
    [DataRow(false, true, true)]
    public async Task CenteredRuntime_DoesNotBackdateACalendarOrRawCommitAcrossTheCutoff(
        bool dispatchCrossesCutoff, bool migrateLegacyClock = false, bool priorAvailabilityBuild = false)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-raw-cutoff");
        var token = TestContext.CancellationToken;
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var fault = new CenteredRuntimeFault();
        CenteredWindowSnapshot window;
        Guid late;
        try
        {
            using (var initial = CenteredRuntimeProvider(root, clock, services => services.AddSingleton<IRawIngressFaultInjector>(fault)))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(initial, profile).ConfigureAwait(false);
                var ingress = initial.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: initial.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var first = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token)
                    .ConfigureAwait(false);
                var reference = await ingress.AcceptCenteredAsync(active, first.Submission, first.Reservation, token).ConfigureAwait(false);
                await ProcessCenteredRuntimeExposureAsync(initial, active, token).ConfigureAwait(false);
                window = (await initial.GetRequiredService<ICenteredWindowReader>()
                    .ReadAsync(reference.Manifest.Descriptor.Capture.CaptureId, token).ConfigureAwait(false))!;
                Assert.IsNotNull(window);

                if (dispatchCrossesCutoff)
                {
                    clock.UtcNow = window.DeadlineUtc.AddMilliseconds(-1);
                    fault.BeforeDispatchCommit = () => clock.UtcNow = window.DeadlineUtc.AddMilliseconds(1);
                }
                var last = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start.AddSeconds(4).AddMilliseconds(80), 1, token)
                    .ConfigureAwait(false);
                if (!dispatchCrossesCutoff)
                {
                    clock.UtcNow = window.DeadlineUtc.AddMilliseconds(-1);
                    fault.BeforeAdmissionCommit = () => clock.UtcNow = window.DeadlineUtc.AddMilliseconds(1);
                }
                var committed = await ingress.AcceptCenteredAsync(active, last.Submission, last.Reservation, token).ConfigureAwait(false);
                late = committed.Manifest.Descriptor.Capture.CaptureId;
                var head = await ingress.ReadCurrentAsync(window.Camera, token).ConfigureAwait(false);
                Assert.AreEqual(late, head.AcquisitionHead!.Exposure.CaptureId);
                if (!dispatchCrossesCutoff)
                    Assert.IsLessThan(window.DeadlineUtc, head.AcquisitionHead.CommittedUtc);
                Assert.IsGreaterThan(window.DeadlineUtc, clock.UtcNow);
                if (migrateLegacyClock)
                {
                    using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    using var command = connection.CreateCommand();
                    command.CommandText = SqliteCaptureProcessingSchemaTests.DropCenteredRegistrationStateSql + "\n" + """
                        DROP INDEX ix_processing_outputs_astrometry_lookup;
                        UPDATE processing_centered_attempt_calendar SET admitted_ticks = $legacy WHERE attempt_id = $attempt;
                        """ + "\n" + (priorAvailabilityBuild
                        ? "DROP INDEX ix_processing_centered_output_availability_cutoff;\n" +
                          SqliteCaptureProcessingStore.LegacyCenteredAvailabilityCutoffIndexForTests
                        : "DROP TABLE processing_centered_output_availability;");
                    command.Parameters.AddWithValue("$legacy", window.DeadlineUtc.AddMilliseconds(-1).UtcTicks);
                    command.Parameters.AddWithValue("$attempt", last.Reservation.Pending.Key.AttemptId.ToString("N"));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            using var restarted = CenteredRuntimeProvider(root, clock);
            var ingressAfter = restarted.GetRequiredService<RawCaptureIngress>();
            await ingressAfter.InitializeAsync(token).ConfigureAwait(false);
            if (migrateLegacyClock)
                Assert.IsTrue(await restarted.GetRequiredService<SqliteCaptureProcessingStore>()
                    .RecoverUnobservedCenteredEvidenceAsync(token).ConfigureAwait(false));
            await restarted.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
            var closed = await restarted.GetRequiredService<ICenteredWindowReader>().ReadAsync(window.ReferenceCaptureId, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(closed?.ClosureIdentity);
            Assert.AreEqual(window.DeadlineUtc, closed.EvidenceCutoffUtc);
            Assert.AreEqual(CenteredWindowSlotState.MissingExposure, closed.Slots[4].State);
            var frozenSlotStart = closed.Slots[4].ExpectedStartUtc;
            Assert.AreEqual(start.AddSeconds(4).AddMilliseconds(dispatchCrossesCutoff || migrateLegacyClock ? 0 : 80), frozenSlotStart);
            Assert.IsFalse(closed.Slots.Any(slot => slot.Exposure?.CaptureId == late));
            if (migrateLegacyClock)
                Assert.AreEqual(CenteredWindowSlotState.MissingExposure, closed.Slots[2].State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredRuntime_OrdersRecoveredAdmissionBeforeProductsAndIsolatesAnIncompleteSettlement(bool missingPayloadHash)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-observation-recovery");
        var token = TestContext.CancellationToken;
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        Guid firstId;
        Guid nextId;
        try
        {
            using (var initial = CenteredRuntimeProvider(root, clock))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(initial, profile).ConfigureAwait(false);
                var ingress = initial.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: initial.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var first = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token)
                    .ConfigureAwait(false);
                var receipt = await ingress.AcceptCenteredAsync(active, first.Submission, first.Reservation, token).ConfigureAwait(false);
                firstId = receipt.Manifest.Descriptor.Capture.CaptureId;
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE processing_centered_attempt_calendar SET admitted_ticks = NULL WHERE attempt_id = $attempt;";
                command.Parameters.AddWithValue("$attempt", first.Reservation.Pending.Key.AttemptId.ToString("N"));
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                await ProcessCenteredRuntimeExposureAsync(initial, active, token).ConfigureAwait(false);
                if (missingPayloadHash)
                {
                    command.CommandText = """
                        UPDATE centered_exposure_products SET payload_sha256 = NULL
                        WHERE capture_id = $capture AND schema_version = $schema;
                        """;
                    command.Parameters.AddWithValue("$capture", firstId.ToString("N"));
                    command.Parameters.AddWithValue("$schema", CenteredExposureProductReader.LinearSchemaVersion);
                    Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }

                var next = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start.AddSeconds(12), 1, token)
                    .ConfigureAwait(false);
                var second = await ingress.AcceptCenteredAsync(active, next.Submission, next.Reservation, token).ConfigureAwait(false);
                nextId = second.Manifest.Descriptor.Capture.CaptureId;
                await ProcessCenteredRuntimeExposureAsync(initial, active, token).ConfigureAwait(false);
            }

            clock.UtcNow = start.AddSeconds(25);
            using var restarted = CenteredRuntimeProvider(root, clock);
            await restarted.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var store = restarted.GetRequiredService<SqliteCaptureProcessingStore>();
            Assert.IsTrue(await store.RecoverUnobservedCenteredEvidenceAsync(token).ConfigureAwait(false));
            await restarted.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
            var recovered = await store.ReadCenteredWindowByReferenceAsync(firstId, token).ConfigureAwait(false);
            var healthy = await store.ReadCenteredWindowByReferenceAsync(nextId, token).ConfigureAwait(false);
            Assert.IsNotNull(recovered);
            Assert.IsNotNull(healthy);
            Assert.AreEqual(CenteredWindowSlotState.Eligible, healthy.Closure.Center.State);
            Assert.AreEqual(missingPayloadHash ? CenteredWindowSlotState.Rejected : CenteredWindowSlotState.Eligible,
                recovered.Closure.Center.State);
            if (missingPayloadHash)
                Assert.AreEqual("product.settlement-identity-missing", recovered.Closure.Center.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(-2, -1, CenteredWindowSlotState.Eligible)]
    [DataRow(-1, 1, CenteredWindowSlotState.Rejected)]
    [DataRow(1, 2, CenteredWindowSlotState.Eligible)]
    public async Task CenteredRuntime_RestorationKeepsTheOriginalCutoffAvailability(
        int lossOffset, int restoreOffset, CenteredWindowSlotState expected)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-availability-cutoff");
        var token = TestContext.CancellationToken;
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var keys = new List<ExposureKey>();
        CenteredWindowSnapshot window;
        try
        {
            using (var initial = CenteredRuntimeProvider(root, clock))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(initial, profile).ConfigureAwait(false);
                var ingress = initial.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: initial.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                for (var index = 0; index < 3; index++)
                {
                    var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                        start.AddSeconds(2 * index), 1, token).ConfigureAwait(false);
                    var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
                    var key = attempt.Reservation.Pending.Key;
                    keys.Add(new(key.Camera, key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId));
                    await ProcessCenteredRuntimeExposureAsync(initial, active, token).ConfigureAwait(false);
                }

                window = (await initial.GetRequiredService<ICenteredWindowReader>().ReadAsync(keys[0].CaptureId, token).ConfigureAwait(false))!;
                Assert.IsNotNull(window);
                var exposures = initial.GetRequiredService<IExposureProductReader>();
                var linear = await exposures.ReadLinearAsync(keys[2], token).ConfigureAwait(false);
                Assert.IsNotNull(linear?.Product);
                var store = initial.GetRequiredService<SqliteCaptureProcessingStore>();
                var output = await store.ReadOutputByArtifactIdAsync(linear.Product.ArtifactId, token).ConfigureAwait(false);
                Assert.IsNotNull(output);
                var payload = Path.Combine(root, output.PayloadRelativePath);
                var displaced = payload + ".availability-probe";
                clock.UtcNow = window.DeadlineUtc.AddMilliseconds(lossOffset);
                File.Move(payload, displaced);
                try
                {
                    await store.TransitionOutputUnavailableAsync(output.OutputIdentitySha256, "Missing", "test.payload-unavailable", null, token)
                        .ConfigureAwait(false);
                    Assert.AreEqual("Missing", (await store.ReadOutputByArtifactIdAsync(linear.Product.ArtifactId, token).ConfigureAwait(false))!.AvailabilityState);
                }
                finally
                {
                    File.Move(displaced, payload);
                }
                clock.UtcNow = window.DeadlineUtc.AddMilliseconds(restoreOffset);
                await store.RestoreMissingOutputAvailableAsync(output.OutputIdentitySha256, token).ConfigureAwait(false);
                Assert.AreEqual("Available", (await store.ReadOutputByArtifactIdAsync(linear.Product.ArtifactId, token).ConfigureAwait(false))!.AvailabilityState);
            }

            clock.UtcNow = window.DeadlineUtc.AddSeconds(1);
            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            await reopened.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
            var retained = await reopened.GetRequiredService<SqliteCaptureProcessingStore>()
                .ReadCenteredWindowByReferenceAsync(window.ReferenceCaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(retained);
            Assert.AreEqual(window.DeadlineUtc, retained.Closure.EvidenceCutoffUtc);
            Assert.AreEqual(expected, retained.Closure.Slots[4].State, retained.Closure.Slots[4].Reason);
            if (expected == CenteredWindowSlotState.Rejected)
                Assert.AreEqual("test.payload-unavailable", retained.Closure.Slots[4].Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(-1, "Missing")]
    [DataRow(1, "Available")]
    [DataRow(1, "Missing", true)]
    public async Task CenteredRuntime_RemovedProjectedSceneRetainsItsWindowCutoffHeader(
        int lossOffset, string historicalAvailability, bool legacyLossThenRegeneration = false)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-scene-cutoff");
        var token = TestContext.CancellationToken;
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        ExposureKey key;
        Guid rawArtifactId;
        string graph;
        CenteredStackArtifactPin scenePin;
        DateTimeOffset cutoff;
        try
        {
            using (var initial = CenteredRuntimeProvider(root, clock))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(initial, profile).ConfigureAwait(false);
                var ingress = initial.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: initial.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token)
                    .ConfigureAwait(false);
                var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
                var pending = attempt.Reservation.Pending.Key;
                key = new(pending.Camera, pending.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
                graph = receipt.Manifest.CaptureGraphRevisionId!;
                rawArtifactId = receipt.Manifest.Descriptor.Artifact.ArtifactId;
                await ProcessCenteredRuntimeExposureAsync(initial, active, token).ConfigureAwait(false);

                var store = initial.GetRequiredService<SqliteCaptureProcessingStore>();
                var window = await initial.GetRequiredService<ICenteredWindowReader>().ReadAsync(key.CaptureId, token).ConfigureAwait(false);
                Assert.IsNotNull(window);
                cutoff = window.DeadlineUtc;
                var products = await store.ReadCaptureProductsAsync(key.CaptureId, ProjectedSceneV1.CurrentSchemaVersion, 2, token)
                    .ConfigureAwait(false);
                var scene = await store.ReadOutputByArtifactIdAsync(products.Single().ArtifactId, token).ConfigureAwait(false);
                Assert.IsNotNull(scene);
                scenePin = CenteredStackArtifactPin.FromHeader(CenteredStackArtifactHeader.FromArtifact(CenteredWindowArtifactHeader.FromOutput(scene)),
                    scene.Artifact.ChecksumSha256);
                using var legacy = legacyLossThenRegeneration
                    ? new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False")
                    : null;
                if (legacy is not null)
                {
                    await legacy.OpenAsync(token).ConfigureAwait(false);
                    using var save = legacy.CreateCommand();
                    save.CommandText = """
                        CREATE TEMP TABLE previous_scene AS SELECT * FROM processing_outputs WHERE output_identity_sha256 = $output;
                        CREATE TEMP TABLE previous_scene_sources AS
                            SELECT * FROM processing_output_sources WHERE output_identity_sha256 = $output;
                        """;
                    save.Parameters.AddWithValue("$output", scene.OutputIdentitySha256);
                    await save.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                clock.UtcNow = cutoff.AddMilliseconds(lossOffset);
                var transition = await store.TransitionOutputUnavailableAsync(scene.OutputIdentitySha256, "Missing", "required-file-missing", null, token)
                    .ConfigureAwait(false);
                Assert.IsTrue(transition.Reactivated);
                Assert.IsNull(await store.ReadOutputByArtifactIdAsync(scenePin.ArtifactId, token).ConfigureAwait(false));
                if (legacy is not null)
                {
                    // Model the pre-header build deleting and regenerating the exact deterministic output.
                    using var restore = legacy.CreateCommand();
                    restore.CommandText = """
                        UPDATE centered_exposure_products SET provenance_json = NULL WHERE output_identity = $output;
                        INSERT INTO processing_outputs SELECT * FROM previous_scene;
                        INSERT INTO processing_output_sources SELECT * FROM previous_scene_sources;
                        INSERT INTO processing_centered_output_availability
                            (output_identity_sha256, state, recorded_ticks, observed_ticks)
                        VALUES ($output, 'Available', $restored, $restored);
                        """;
                    restore.Parameters.AddWithValue("$output", scene.OutputIdentitySha256);
                    restore.Parameters.AddWithValue("$restored", cutoff.AddMilliseconds(2).UtcTicks);
                    await restore.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    clock.UtcNow = cutoff.AddMilliseconds(3);
                    await store.TransitionOutputUnavailableAsync(scene.OutputIdentitySha256, "Missing", "required-file-missing", null, token)
                        .ConfigureAwait(false);
                    Assert.IsNull(await store.ReadOutputByArtifactIdAsync(scenePin.ArtifactId, token).ConfigureAwait(false));
                }
            }

            clock.UtcNow = cutoff.AddSeconds(1);
            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            await reopened.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
            var closed = await reopened.GetRequiredService<SqliteCaptureProcessingStore>()
                .ReadCenteredWindowByReferenceAsync(key.CaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(closed);
            Assert.AreEqual(cutoff, closed.Closure.EvidenceCutoffUtc);
            Assert.AreEqual(CenteredWindowSlotState.Eligible, closed.Closure.Center.State);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            SqliteRawCaptureJournal.AddExposureParameters(command, key);
            SqliteCaptureProcessingStore.SetCenteredWindowProductQuery(command,
                ProcessingGraphOperationsCoordinator.GetLiveExecutionId(key.CaptureId, graph), rawArtifactId, graph, cutoff);
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var observed = false;
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (reader.GetString(7) != ProjectedSceneV1.CurrentSchemaVersion) continue;
                Assert.AreEqual(historicalAvailability, reader.GetString(16));
                var saved = await reader.GetFieldValueAsync<byte[]>(12, token).ConfigureAwait(false);
                using var custody = JsonDocument.Parse(saved);
                var header = custody.RootElement.GetProperty("Header").Deserialize<CenteredStackArtifactHeader>();
                Assert.IsNotNull(header);
                Assert.AreEqual(scenePin, CenteredStackArtifactPin.FromHeader(header, reader.GetString(14)));
                Assert.AreEqual(legacyLossThenRegeneration ? cutoff.AddMilliseconds(2).UtcTicks : 0,
                    custody.RootElement.GetProperty("TrustedFromTicks").GetInt64());
                observed = true;
            }
            Assert.IsTrue(observed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(3, 6000)]
    [DataRow(10, 6000)]
    [DataRow(2, 12000)]
    [DataRow(3, 6075)]
    [DataRow(10, 6075)]
    [DataRow(2, 6000, true)]
    public async Task CenteredRuntime_NewCadenceAfterAGapKeepsBothMissedSlotsAndObservedReference(
        int intervalSeconds, int resumeMilliseconds, bool corruptPredecessor = false)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-cadence-break");
        var token = CancellationToken.None;
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        try
        {
            using var provider = CenteredRuntimeProvider(root, clock);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider,
                CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16)).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);

            var first = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token)
                .ConfigureAwait(false);
            var firstReceipt = await ingress.AcceptCenteredAsync(active, first.Submission, first.Reservation, token)
                .ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
            if (corruptPredecessor)
            {
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE processing_centered_attempt_calendar SET start_ticks = $corrupt WHERE attempt_id = $attempt;";
                command.Parameters.AddWithValue("$corrupt", DateTimeOffset.MaxValue.UtcTicks);
                command.Parameters.AddWithValue("$attempt", first.Reservation.Pending.Key.AttemptId.ToString("N"));
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
            }

            var interval = TimeSpan.FromSeconds(intervalSeconds);
            var resume = start.AddMilliseconds(resumeMilliseconds);
            var elapsedOldCadence = start.AddSeconds(resumeMilliseconds == 12000 ? 12 : 6);
            var next = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                resume, 1, token, interval).ConfigureAwait(false);
            var nextReceipt = await ingress.AcceptCenteredAsync(active, next.Submission, next.Reservation, token)
                .ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var reader = provider.GetRequiredService<ICenteredWindowReader>();
            var original = await reader.ReadAsync(firstReceipt.Manifest.Descriptor.Capture.CaptureId, token).ConfigureAwait(false);
            var switched = await reader.ReadAsync(nextReceipt.Manifest.Descriptor.Capture.CaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(original);
            Assert.IsNotNull(switched);
            if (corruptPredecessor)
            {
                var afterDamage = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                    resume + interval, 1, token, interval).ConfigureAwait(false);
                var afterDamageReceipt = await ingress.AcceptCenteredAsync(active, afterDamage.Submission, afterDamage.Reservation, token)
                    .ConfigureAwait(false);
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
                var secondHealthyId = afterDamageReceipt.Manifest.Descriptor.Capture.CaptureId;
                var secondPending = await reader.ReadAsync(secondHealthyId, token).ConfigureAwait(false);
                Assert.IsNotNull(secondPending);
                clock.UtcNow = secondPending.DeadlineUtc.AddMilliseconds(1);
                await provider.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
                var unusable = await reader.ReadAsync(original.ReferenceCaptureId, token).ConfigureAwait(false);
                var healthy = await reader.ReadAsync(switched.ReferenceCaptureId, token).ConfigureAwait(false);
                var secondHealthy = await reader.ReadAsync(secondHealthyId, token).ConfigureAwait(false);
                Assert.AreEqual("invalid-window-evidence", unusable!.FailureReason);
                Assert.AreEqual(CenteredWindowSlotState.Eligible, healthy!.Slots[2].State);
                Assert.AreEqual(CenteredWindowSlotState.OutsideRun, healthy.Slots[1].State);
                Assert.AreEqual(CenteredWindowSlotState.Eligible, secondHealthy!.Slots[2].State);
                Assert.AreEqual(CenteredWindowSlotState.OutsideRun, secondHealthy.Slots[0].State);
                return;
            }
            CollectionAssert.AreEqual(new[] { elapsedOldCadence.AddSeconds(-4), elapsedOldCadence.AddSeconds(-2), resume },
                switched.Slots.Take(3).Select(slot => slot.ExpectedStartUtc).ToArray());
            var following = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                resume + interval, 1, token, interval).ConfigureAwait(false);
            var followingReceipt = await ingress.AcceptCenteredAsync(active, following.Submission, following.Reservation, token)
                .ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
            var followingWindow = await reader.ReadAsync(followingReceipt.Manifest.Descriptor.Capture.CaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(followingWindow);
            CollectionAssert.AreEqual(new[] { elapsedOldCadence.AddSeconds(-2), resume, resume + interval },
                followingWindow.Slots.Take(3).Select(slot => slot.ExpectedStartUtc).ToArray());

            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var initialState = await store.ReadCenteredWindowStateAsync(original.ReferenceCaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(initialState?.Checkpoint);
            var missed = initialState!.Checkpoint.Schedule with
            {
                ReferenceStartUtc = start.AddSeconds(resumeMilliseconds == 12000 ? 6 : 4)
            };
            var missedWindow = await store.ReadCenteredWindowDispositionAsync(missed.Identity, token).ConfigureAwait(false);
            Assert.IsNotNull(missedWindow);
            Assert.IsTrue(missedWindow.IsTerminal);
            Assert.IsNull(missedWindow.Publication);
            Assert.AreEqual(CenteredWindowSlotState.MissingExposure, missedWindow.Closure.Center.State);
            Assert.AreEqual("nominal-slot-missed", missedWindow.Closure.Center.Reason);
            clock.UtcNow = switched.DeadlineUtc.AddMilliseconds(1);
            await provider.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
            var firstClosed = await reader.ReadAsync(original.ReferenceCaptureId, token).ConfigureAwait(false);
            var nextClosed = await reader.ReadAsync(switched.ReferenceCaptureId, token).ConfigureAwait(false);
            Assert.AreEqual(CenteredWindowSlotState.MissingExposure, firstClosed!.Slots[3].State);
            Assert.AreEqual(CenteredWindowSlotState.MissingExposure, firstClosed.Slots[4].State);
            Assert.AreEqual(CenteredWindowSlotState.Eligible, nextClosed!.Slots[2].State);
            Assert.AreEqual(switched.ReferenceCaptureId, nextClosed.Slots[2].Exposure!.CaptureId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(CenteredExposureReservation Reservation, CaptureLoopSubmission Submission)> CaptureCenteredRuntimeAttemptAsync(
        RawCaptureIngress ingress, VirtualSkyCameraModule module, ExposureTestTimeProvider deviceClock,
        CenteredClock agentClock, CameraModuleConfig active, DateTimeOffset scheduled, double gain, CancellationToken token,
        TimeSpan? interval = null, CenteredScheduleBinding? schedule = null)
    {
        if (scheduled > agentClock.UtcNow)
            agentClock.UtcNow = scheduled;
        deviceClock.Advance(scheduled - deviceClock.GetUtcNow());
        var reservation = await ingress.ReserveCenteredAsync(active, new(null, null, null, null, "centered-live-test"),
            null, token, schedule: schedule)
            .ConfigureAwait(false);
        var request = new CaptureRequest(scheduled, interval ?? TimeSpan.FromSeconds(2), CaptureMode.Still,
            new(TimeSpan.FromMilliseconds(250), gain, null, null))
        {
            BoundGraphRevisionId = reservation.Revision.State.RevisionId,
            BoundGraph = reservation.Revision.Pipeline
        };
        await ingress.DispatchCenteredAsync(reservation.Pending.Key, request.RequestedSetpoint!, null,
            "centered-live-test", token, scheduled, request.TargetInterval).ConfigureAwait(false);
        var captured = await module.CaptureBoundAsync(reservation.Pending.Key, request, token).ConfigureAwait(false);
        var readout = captured.AcquisitionTiming!.ReadoutCompletedUtc;
        if (agentClock.UtcNow <= readout)
            agentClock.UtcNow = readout.AddMilliseconds(1);
        return (reservation, new(request, captured, scheduled, request.TargetInterval, readout - scheduled));
    }

    private sealed class CenteredRuntimeFault : IRawIngressFaultInjector, ICaptureProcessingFaultInjector
    {
        internal bool InterruptAdmission { get; set; }
        internal Action? BeforeAdmissionCommit { get; set; }
        internal Action? BeforeDispatchCommit { get; set; }
        internal bool FailMeasurementPublication { get; set; }
        internal bool FailRegistrationPublication { get; set; }
        internal string? WindowToInterrupt { get; set; }
        internal bool InterruptedClosure { get; private set; }

        public void Inject(RawIngressFaultPoint point)
        {
            if (point == RawIngressFaultPoint.BeforeJournalTransactionCommit)
                BeforeAdmissionCommit?.Invoke();
            if (point != RawIngressFaultPoint.AfterJournalCommit || !InterruptAdmission)
                return;
            InterruptAdmission = false;
            throw new IOException("Interrupt after centered raw admission.");
        }

        public void BeforeCenteredDispatchCommit() => BeforeDispatchCommit?.Invoke();

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (FailRegistrationPublication && point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit &&
                nodeId == CenteredWindowCoordinator.RegistrationProducer)
                throw new IOException("Interrupt before the registered products commit.");
            if (FailMeasurementPublication && point == CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit &&
                nodeId == ExposureMeasurementProducts.StepAlias)
                throw new IOException("Interrupt before the fixed measurement publication commits.");
            if (point != CaptureProcessingFaultPoint.BeforeCenteredWindowCloseCommit || nodeId != WindowToInterrupt || InterruptedClosure)
                return;
            InterruptedClosure = true;
            throw new IOException("Interrupt before centered closure commit.");
        }
    }

    private static ServiceProvider CenteredRuntimeProvider(string root, TimeProvider clock,
        Action<IServiceCollection>? configure = null, int? maximumAttempts = null, bool detectTransients = false,
        long? maximumPreviewSourceBytes = null)
    {
        var settings = new Dictionary<string, string?>();
        if (maximumAttempts is { } limit)
            settings["CameraAgent:CaptureDistribution:MaximumAttempts"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (detectTransients)
            settings["CameraAgent:TransientDetection:Mode"] = "Edge";
        if (maximumPreviewSourceBytes is { } sourceBytes)
            settings["CameraAgent:ArtifactRead:MaximumPreviewSourceBytes"] = sourceBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ProcessingGraphOperationsTests.CreateProvider(root, settings, timeProvider: clock, configureServices: services =>
        {
            HVO.SkyMonitor.CameraAgent.Program.AddCameraModules(services);
            services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
            if (detectTransients)
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
            configure?.Invoke(services);
        });
    }

    private static CameraModuleConfig CenteredRuntimeProfile(string root, DateTimeOffset start, CameraPixelFormat format, int size = 224)
    {
        var baseline = StoragePolicyRawIngressIntegrationTests.Profile(root);
        var camera = CreateConfig(format, size, size);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var storage = baseline.Pipeline.Steps.Single(step => step.Type == "Storage");
        var readout = format == CameraPixelFormat.Mono16
            ? new SensorReadoutProfile(new SensorCrop(0, 0, size, size), 1, 1, FrameBinningAlgorithm.IdentityV1,
                CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.RightAlignedV1,
                FrameLevelCodeSpace.NativeSample, 0, 4095, size * sizeof(ushort))
            : null;
        return camera with
        {
            AgentId = baseline.AgentId,
            Schedule = baseline.Schedule,
            Rig = camera.Rig with { ControlPolicy = baseline.Rig.ControlPolicy, Readout = readout },
            DeploymentLocation = DeploymentLocationSnapshot.Create("centered-live-test", 1, "virtual",
                null, DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix"),
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 1921,
                fixedSceneUtc = start,
                illuminationMode = "ControlledNight",
                magnitudeZeroElectronsPerSecond = 14000,
                asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16 }
            })),
            CenteredExposure = new("centered-live", CenteredExposureProfile.CurrentExecutionVersion),
            Pipeline = baseline.Pipeline with
            {
                Steps = [
                    new("Calibration", "linear", DependsOn: ["$raw"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new { strategy = "None", outputVariant = "qualified-linear" })),
                    new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false, Publication: durable),
                    storage with { DependsOn = ["$raw"] }
                ]
            }
        };
    }

    private static InMemoryCelestialCatalog CenteredRuntimeCatalog(DateTimeOffset start)
    {
        var zenith = AstronomyTime.LocalMeanSiderealDegrees(start, -113.878) / 15;
        return new(Enumerable.Range(0, 20).Select(index =>
        {
            var rightAscension = (zenith + (index % 5 - 2) * 1.65 + index / 5 * 0.12 + 24) % 24;
            return new CelestialCatalogObject($"fixture-{index}", $"Fixture {index}", rightAscension,
                2 + index / 5 * 20 + index % 3 * 1.4, 0.4 + index % 5 * 0.31, 0.4 + index % 4 * 0.2);
        }).ToArray());
    }

    private static InMemoryCelestialCatalog CenteredRuntimeWideCatalog(DateTimeOffset start, CameraModuleConfig profile)
    {
        // Fourteen positions lie inside the fit boundary and sixteen outside it.
        // Both sets span all quadrants without regular rings or repeated sectors.
        // These positions seed the simulator catalog, not the measurement or fit.
        PixelPoint[] offsets =
        [
            new(30, -53), new(62, -18), new(-16, 21), new(-45, -38), new(-16, -19), new(28, 52),
            new(-60, -22), new(-61, 18), new(-49, 43), new(-22, 45), new(15, -40), new(4, 41), new(41, 11), new(20, 6),
            new(-70, -49), new(-97, -5), new(-21, -94), new(73, 46), new(54, 74), new(-89, -23), new(36, -86), new(-96, 31),
            new(13, -98), new(-81, 52), new(-38, 79), new(-82, 16), new(58, -64), new(68, -41), new(20, 87), new(1, 98)
        ];
        var projection = RigProjectionContextFactory.CreateNative(profile.Rig);
        var projector = ProjectorFactory.Create(projection);
        return new(offsets.Select((offset, index) =>
        {
            var direction = projector.Unproject(new(projection.PrincipalPointX + offset.X, projection.PrincipalPointY + offset.Y));
            Assert.IsNotNull(direction);
            var ofDate = CoordinateTransforms.HorizontalToEquatorial(direction.Value,
                start, profile.Observatory.LatitudeDegrees, profile.Observatory.LongitudeDegrees);
            var star = EquatorialPrecession.PrecessToJ2000(ofDate, start);
            var name = $"wide-runtime-{index}";
            return new CelestialCatalogObject(name, name, star.RightAscensionHours, star.DeclinationDegrees,
                .4 + index * .025, .3 + index % 11 * .05);
        }));
    }

    private void AssertWideFieldBelowClipping(CameraFrame? frame)
    {
        Assert.IsNotNull(frame);
        Assert.IsNotNull(frame.Layout);
        Assert.AreEqual(FrameByteOrder.LittleEndian, frame.Layout.ByteOrder);
        Assert.IsTrue(FrameWhiteThreshold.TryResolveStored(frame.Layout, out var white, out var reason), reason);
        ushort peak = 0;
        for (var y = 0; y < frame.Layout.Height; y++)
        {
            var row = frame.PixelData.Span.Slice(y * frame.Layout.StrideBytes, frame.Layout.Width * sizeof(ushort));
            for (var x = 0; x < row.Length; x += sizeof(ushort))
                peak = Math.Max(peak, BinaryPrimitives.ReadUInt16LittleEndian(row[x..]));
        }
        TestContext.WriteLine($"Wide Bayer raw peak {peak} against stored white {white}.");
        Assert.IsLessThan(white, peak);
    }

    private static async Task ProcessCenteredRuntimeExposureAsync(ServiceProvider provider, CameraModuleConfig config,
        CancellationToken token)
    {
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
        var lease = await laneStore.ClaimAsync(lane, "centered-live-test", config, token).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await provider.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, token).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, token).ConfigureAwait(false);
    }

    private static async Task<long> ReadCenteredTestHoldCountAsync(string root, Guid capture, CancellationToken token)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM centered_exposure_holds WHERE capture_id = $capture;";
        command.Parameters.AddWithValue("$capture", capture.ToString("N"));
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    private static async Task<List<Guid>> ReadCenteredRegistrationArtifactIdsAsync(string root, Guid capture, CancellationToken token)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact_id FROM processing_outputs WHERE capture_id = $capture AND node_id = $producer;";
        command.Parameters.AddWithValue("$capture", capture.ToString("N"));
        command.Parameters.AddWithValue("$producer", CenteredWindowCoordinator.RegistrationProducer);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            ids.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return ids;
    }
}
