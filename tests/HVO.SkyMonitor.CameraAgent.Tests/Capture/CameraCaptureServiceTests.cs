using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Logging.Abstractions;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.CameraAgent.Tests.SkyMap;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraCaptureServiceTests
{
    [TestMethod]
    public async Task Shutdown_LeaseDrainTimeoutKeepsTheOccupiedModuleAliveUntilRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-occupied-module-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var ingress = new PassthroughRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress,
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressSqliteBusyTimeoutSeconds = 1 }),
                TimeProvider.System, telemetry);
            using var ownership = new CameraModuleOwnership();
            using var lifetime = new TestHostApplicationLifetime();
            var module = new GatedCameraModule();
            using var service = new CameraCaptureService(new ConfigurationAccessor(CreateConfig()), new ModuleFactory(module),
                ingress, new RecordingDistributor(), TimeProvider.System, new AstronomyEnginePlanetEphemeris(), telemetry,
                admission, new FleetRuntimeState(TimeProvider.System), lifetime, NullLogger<CameraCaptureService>.Instance,
                moduleOwnership: ownership)
            { ModuleLeaseDrainTimeout = TimeSpan.FromMilliseconds(50) };
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            lifetime.NotifyStarted();
            await module.SecondCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsTrue(ownership.TryAcquire(out var occupied));
            Task stopping;
            using (occupied)
            {
                stopping = service.StopAsync(CancellationToken.None);
                await module.CaptureCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await Task.Delay(150).ConfigureAwait(false);
                Assert.IsTrue(occupied.Revoked.IsCancellationRequested);
                Assert.IsFalse(module.IsDisposed, "A drain timeout must never authorize disposal of an occupied module.");
                Assert.IsFalse(stopping.IsCompleted, "Shutdown remains pending while the lease owns the module.");
                Assert.IsFalse(ownership.TryAcquire(out _), "The occupied module is unavailable for new work.");
            }
            await stopping.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsTrue(module.IsDisposed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task NamedRig_HostedRestartPublishesNewRigAndRawCaptureWithoutChangingSchedule()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-named-rig-hosted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero));
            var config = CreateConfig() with
            {
                AgentId = "rig-hosted-test",
                DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                    DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC"),
                Schedule = new CaptureScheduleDefinition("capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile("night", TimeSpan.FromMilliseconds(1), 0, TimeSpan.FromSeconds(1))],
                    [new CaptureWeeklyScheduleWindow("monday", DayOfWeek.Monday,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(0, 0)),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(23, 59)), "night")])
            };
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 5
            });
            var pipeline = new EmptyPipelineFactory();
            string originalId;
            string alternateId;
            string stagedScheduleId;
            string initialScheduleId;
            string scheduleHash;
            RawCaptureReceipt before;
            using (var ingress = CreateDurableIngress(options, clock))
            using (var store = new SqliteCaptureScheduleStore(ingress, options, clock))
            using (var telemetry = new CaptureControlTelemetry())
            using (var admission = new CaptureAdmissionCoordinator(ingress, options, clock, telemetry))
            using (var runtime = new CaptureScheduleRuntimeCoordinator(store, admission,
                ingress.State, HealthyLanes(clock, options), pipeline, clock))
            using (var lifetime = new TestHostApplicationLifetime())
            {
                var named = new SqliteNamedRigProfileStore(ingress, options, clock, store, runtime);
                var accessor = new CameraAgentConfigurationAccessor();
                await new CameraAgentConfigurationInitializer(new StaticLoader(config), accessor, pipeline,
                    NullLogger<CameraAgentConfigurationInitializer>.Instance, store, namedRigProfiles: named)
                    .StartAsync(CancellationToken.None).ConfigureAwait(false);
                var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                initialScheduleId = initial.ActiveRevision.RevisionId;
                scheduleHash = initial.ActiveRevision.ScheduleSha256;
                var catalog = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
                originalId = catalog.Selection.ActiveRevisionId!;
                var inventory = await named.GetInventoryAsync(CancellationToken.None).ConfigureAwait(false);
                var profile = await named.SaveProfileNameAsync(null, "Alternate named rig", CancellationToken.None).ConfigureAwait(false);
                var camera = await named.SaveEquipmentAsync(null, "camera", "Same virtual camera",
                    JsonSerializer.SerializeToElement(new NamedCameraEquipment(config.Module, config.Rig.Sensor, config.Rig.Readout)),
                    CancellationToken.None).ConfigureAwait(false);
                var optics = await named.SaveEquipmentAsync(null, "optics", "Alternate optics",
                    JsonSerializer.SerializeToElement(config.Rig.Optics with { FocalLengthMillimeters = 1 }), CancellationToken.None)
                    .ConfigureAwait(false);
                var mount = await named.SaveEquipmentAsync(null, "mount", "Alternate mount",
                    JsonSerializer.SerializeToElement(config.Rig.Orientation with { BoresightAzimuthDegrees = 100 }), CancellationToken.None)
                    .ConfigureAwait(false);
                var alternate = await named.ComposeAsync(profile.ProfileId, camera.RevisionId, optics.RevisionId,
                    mount.RevisionId, CancellationToken.None).ConfigureAwait(false);
                alternateId = alternate.RevisionId;
                Assert.AreEqual(config.ModuleType, alternate.Module.Type);
                Assert.AreEqual(1, inventory.Profiles.Count);
                var module = new RigRecordingModule(clock);
                var service = CreateHostedService(accessor, module, ingress, telemetry, admission, runtime, named, lifetime, clock);
                await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                lifetime.NotifyStarted();
                try { before = await ingress.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException exception)
                {
                    throw new AssertFailedException($"No initial capture: initialized={module.InitializedConfig is not null}, " +
                        $"decision={runtime.Snapshot?.CurrentDecision?.Reason}, ingress={ingress.State.Snapshot.Availability}, " +
                        $"admission={admission.Snapshot.State}, failure={ingress.Failure}", exception);
                }
                Assert.AreEqual(CameraRigProfileIdentity.ComputeSha256(config.Rig), before.Manifest.Descriptor.Profiles.Rig.Sha256);
                var receipt = await named.StageAsync(alternateId, catalog.Selection.Version, "hosted-stage", "owner", true,
                    initial.ActiveRevision.RevisionId, initial.ActiveRevision.ProfileSha256, CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(receipt.AcknowledgedUnvalidated);
                Assert.AreEqual("restart_required", receipt.Disposition);
                stagedScheduleId = receipt.ScheduleRevisionId!;
                Assert.AreEqual(originalId, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.ActiveRevisionId);
                Assert.AreEqual(initialScheduleId, (await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.RevisionId);
                Assert.AreEqual(config.Rig, module.InitializedConfig!.Rig);
                Assert.AreEqual(originalId, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.ActiveRevisionId);
                Assert.AreEqual(alternateId, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            using (var ingress = CreateDurableIngress(options, clock))
            using (var store = new SqliteCaptureScheduleStore(ingress, options, clock))
            using (var telemetry = new CaptureControlTelemetry())
            using (var admission = new CaptureAdmissionCoordinator(ingress, options, clock, telemetry))
            using (var runtime = new CaptureScheduleRuntimeCoordinator(store, admission,
                ingress.State, HealthyLanes(clock, options), pipeline, clock))
            using (var lifetime = new TestHostApplicationLifetime())
            {
                var named = new SqliteNamedRigProfileStore(ingress, options, clock, store, runtime);
                var accessor = new CameraAgentConfigurationAccessor();
                await new CameraAgentConfigurationInitializer(new StaticLoader(config), accessor, pipeline,
                    NullLogger<CameraAgentConfigurationInitializer>.Instance, store, namedRigProfiles: named)
                    .StartAsync(CancellationToken.None).ConfigureAwait(false);
                var module = new RigRecordingModule(clock);
                var service = CreateHostedService(accessor, module, ingress, telemetry, admission, runtime, named, lifetime, clock);
                await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                lifetime.NotifyStarted();
                var after = await ingress.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var selection = (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection;
                var schedule = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(alternateId, selection.ActiveRevisionId);
                Assert.IsNull(selection.PendingRevisionId);
                Assert.AreEqual(stagedScheduleId, schedule.ActiveRevision.RevisionId);
                Assert.IsNull(schedule.PendingRevision);
                Assert.AreEqual(scheduleHash, schedule.ActiveRevision.ScheduleSha256);
                Assert.AreEqual(CaptureScheduleContract.ComputeSha256(config.Schedule!),
                    CaptureScheduleContract.ComputeSha256(schedule.ActiveRevision.Profile.Schedule));
                Assert.AreEqual(config.Rig.Pipeline, schedule.ActiveRevision.Profile.Rig.Pipeline);
                Assert.AreEqual(config.ModuleType, module.InitializedConfig!.ModuleType);
                Assert.AreEqual(schedule.ActiveRevision.Profile.Rig, module.InitializedConfig.Rig);
                Assert.AreNotEqual(before.Manifest.Descriptor.Profiles.Rig.Sha256, after.Manifest.Descriptor.Profiles.Rig.Sha256);
                Assert.AreEqual(CameraRigProfileIdentity.ComputeSha256(module.InitializedConfig.Rig),
                    after.Manifest.Descriptor.Profiles.Rig.Sha256);
                Assert.AreEqual(schedule.ActiveRevision.ProfileSha256,
                    after.Manifest.Descriptor.CycleEvidence!.ScheduleAdmission!.LocalProfileSha256);
                Assert.AreEqual(scheduleHash,
                    after.Manifest.Descriptor.CycleEvidence.ScheduleAdmission.ScheduleRevisionSha256);
                Assert.AreEqual(config.Schedule!.SetpointProfiles[0].Exposure,
                    after.Manifest.Descriptor.Controls.RequestedExposure);
                Assert.AreEqual(config.Schedule.SetpointProfiles[0].Gain,
                    after.Manifest.Descriptor.Controls.RequestedGain);
                var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5);
                var committed = await journal.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(2, committed.Count);
                Assert.IsTrue(committed.All(entry => entry.State == "committed"));
                Assert.IsTrue(committed.Any(entry => entry.DescriptorSha256 ==
                    CaptureContractJson.ComputeDescriptorSha256(before.Manifest.Descriptor)));
                Assert.IsTrue(committed.Any(entry => entry.DescriptorSha256 ==
                    CaptureContractJson.ComputeDescriptorSha256(after.Manifest.Descriptor)));
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var rollback = await named.StageAsync(originalId, selection.Version, "hosted-rollback", "owner", true,
                    schedule.ActiveRevision.RevisionId, schedule.ActiveRevision.ProfileSha256, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("restart_required", rollback.Disposition);
                Assert.AreEqual(alternateId, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.ActiveRevisionId);
                var cancelled = await named.CancelPendingAsync(originalId, rollback.Version, "hosted-cancel", "owner",
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("cancelled", cancelled.Disposition);
                Assert.IsNull((await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision);
                Assert.AreEqual(stagedScheduleId, (await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.RevisionId);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NamedRig_QuickEditFlipIsRespectedAfterHostedRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-named-rig-flip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero));
            // A sensor large enough that east and west land on visibly different pixels.
            var config = CreateConfig() with
            {
                AgentId = "rig-flip-test",
                Rig = CreateConfig().Rig with
                {
                    Sensor = new SensorProfile("Test", 64, 64, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8)
                },
                DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                    DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC"),
                Schedule = new CaptureScheduleDefinition("capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile("night", TimeSpan.FromMilliseconds(1), 0, TimeSpan.FromSeconds(1))],
                    [new CaptureWeeklyScheduleWindow("monday", DayOfWeek.Monday,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(23, 59)), "night")])
            };
            Assert.IsFalse(config.Rig.Optics.HorizontalFlip);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 5
            });
            var pipeline = new EmptyPipelineFactory();
            string composedId;
            using (var ingress = CreateDurableIngress(options, clock))
            using (var store = new SqliteCaptureScheduleStore(ingress, options, clock))
            using (var telemetry = new CaptureControlTelemetry())
            using (var admission = new CaptureAdmissionCoordinator(ingress, options, clock, telemetry))
            using (var runtime = new CaptureScheduleRuntimeCoordinator(store, admission,
                ingress.State, HealthyLanes(clock, options), pipeline, clock))
            {
                var named = new SqliteNamedRigProfileStore(ingress, options, clock, store, runtime);
                await new CameraAgentConfigurationInitializer(new StaticLoader(config), new CameraAgentConfigurationAccessor(),
                    pipeline, NullLogger<CameraAgentConfigurationInitializer>.Instance, store, namedRigProfiles: named)
                    .StartAsync(CancellationToken.None).ConfigureAwait(false);
                _ = await runtime.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
                var catalog = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
                var active = catalog.Revisions.Single(revision => revision.RevisionId == catalog.Selection.ActiveRevisionId);
                var edit = await NamedRigQuickEditServiceTests.OwnerService(named, runtime).ApplyActiveRigEditAsync(
                    new ActiveRigEditRequest(active.RevisionId, catalog.Selection.Version, HorizontalFlip: true,
                        active.Rig.Optics.FieldOfViewDegrees, active.Rig.Optics.FocalLengthMillimeters,
                        active.Rig.Orientation.BoresightAltitudeDegrees, active.Rig.Orientation.BoresightAzimuthDegrees,
                        active.Rig.Orientation.RollAdjustmentDegrees), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(ActiveRigEditStatus.Staged, edit.Value?.Status, edit.Value?.Failure ?? edit.Message);
                composedId = edit.Value!.ComposedRevisionId!;
                Assert.IsFalse(runtime.Snapshot!.Configuration.Rig.Optics.HorizontalFlip,
                    "A staged flip must not change the running rig before restart.");
            }

            using (var ingress = CreateDurableIngress(options, clock))
            using (var store = new SqliteCaptureScheduleStore(ingress, options, clock))
            using (var telemetry = new CaptureControlTelemetry())
            using (var admission = new CaptureAdmissionCoordinator(ingress, options, clock, telemetry))
            using (var runtime = new CaptureScheduleRuntimeCoordinator(store, admission,
                ingress.State, HealthyLanes(clock, options), pipeline, clock))
            using (var lifetime = new TestHostApplicationLifetime())
            {
                var named = new SqliteNamedRigProfileStore(ingress, options, clock, store, runtime);
                var accessor = new CameraAgentConfigurationAccessor();
                await new CameraAgentConfigurationInitializer(new StaticLoader(config), accessor, pipeline,
                    NullLogger<CameraAgentConfigurationInitializer>.Instance, store, namedRigProfiles: named)
                    .StartAsync(CancellationToken.None).ConfigureAwait(false);
                var module = new RigRecordingModule(clock);
                var service = CreateHostedService(accessor, module, ingress, telemetry, admission, runtime, named, lifetime, clock);
                await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                lifetime.NotifyStarted();
                var captured = await ingress.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                var catalog = await named.GetAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(composedId, catalog.Selection.ActiveRevisionId);
                Assert.IsNull(catalog.Selection.PendingRevisionId);
                Assert.IsTrue(module.InitializedConfig!.Rig.Optics.HorizontalFlip, "The camera module was initialized unflipped.");
                var running = runtime.Snapshot!.Configuration;
                Assert.IsTrue(running.Rig.Optics.HorizontalFlip, "The runtime configuration read by the sky map is unflipped.");
                Assert.IsTrue(RigProjectionContextFactory.Create(running.Rig).HorizontalFlip, "The rig projection is unflipped.");
                Assert.IsTrue((await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false))
                    .ActiveRevision.Profile.Rig.Optics.HorizontalFlip);

                // The capture records the flipped rig rather than the one the camera was configured with.
                Assert.AreEqual(CameraRigProfileIdentity.ComputeSha256(running.Rig), captured.Manifest.Descriptor.Profiles.Rig.Sha256);
                Assert.AreNotEqual(CameraRigProfileIdentity.ComputeSha256(config.Rig), captured.Manifest.Descriptor.Profiles.Rig.Sha256);

                // The annotation step, given the configuration carried alongside the capture as the processing worker
                // gives it, draws east and west mirrored against the rig the camera was configured with.
                var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5);
                var entry = (await journal.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    .Single(item => item.CaptureId == captured.Manifest.Descriptor.Capture.CaptureId);
                var envelope = await journal.ReadRecoveredLaneEnvelopeAsync(entry, CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(envelope!.Configuration.Rig.Optics.HorizontalFlip, "The capture carries an unflipped rig.");
                var configuredAnnotation = await AnnotateAsync(config, envelope.Submission, captured).ConfigureAwait(false);
                var shownAnnotation = await AnnotateAsync(envelope.Configuration, envelope.Submission, captured)
                    .ConfigureAwait(false);
                Assert.AreNotEqual(configuredAnnotation.Overlay.East.X, shownAnnotation.Overlay.East.X, 1,
                    "The annotation east mark did not move with the flip.");
                AssertMirrored(configuredAnnotation.Overlay.East.X, configuredAnnotation.Overlay.East.Y,
                    shownAnnotation.Overlay.East.X, shownAnnotation.Overlay.East.Y, "annotation east");
                AssertMirrored(configuredAnnotation.Overlay.West.X, configuredAnnotation.Overlay.West.Y,
                    shownAnnotation.Overlay.West.X, shownAnnotation.Overlay.West.Y, "annotation west");
                // The drawn letters trade sides: the one at the right edge of the configured image is the one at the left
                // edge of the annotated capture, and the other way round.
                var configuredLeft = LetterAt(configuredAnnotation.Drawn, 0, 16);
                var configuredRight = LetterAt(configuredAnnotation.Drawn, 48, 64);
                Assert.IsFalse(string.IsNullOrEmpty(configuredLeft), "No letter is drawn at the left edge.");
                Assert.IsFalse(string.IsNullOrEmpty(configuredRight), "No letter is drawn at the right edge.");
                Assert.AreNotEqual(configuredLeft, configuredRight, "East and west are drawn as the same letter.");
                Assert.AreEqual(configuredRight, LetterAt(shownAnnotation.Drawn, 0, 16), "The left edge letter was not flipped.");
                Assert.AreEqual(configuredLeft, LetterAt(shownAnnotation.Drawn, 48, 64), "The right edge letter was not flipped.");

                // The sky map projects the running rig, so its stars and compass points are mirrored too. Orion is
                // overhead at this instant, which puts the fixture stars inside the image.
                var stars = CameraAgentSkyMapProjectionTests.CreateCatalog(CameraAgentSkyMapProjectionTests.BrightStars());
                var orionOverhead = new DateTimeOffset(2026, 9, 29, 5, 0, 0, TimeSpan.Zero);
                var configuredSky = await new CameraAgentSkyMapProjection(new ConfigurationAccessor(config), stars, clock)
                    .ProjectAsync(orionOverhead, CancellationToken.None).ConfigureAwait(false);
                var shownSky = await new CameraAgentSkyMapProjection(accessor, stars, clock, scheduleRuntime: runtime)
                    .ProjectAsync(orionOverhead, CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(shownSky.Geometry.HorizontalFlip, "The sky map reports an unflipped rig.");
                Assert.IsNotEmpty(shownSky.Objects);
                Assert.HasCount(configuredSky.Objects.Count, shownSky.Objects);
                foreach (var shown in shownSky.Objects)
                {
                    var configured = configuredSky.Objects.Single(item => item.Id == shown.Id);
                    AssertMirrored(configured.PixelX, configured.PixelY, shown.PixelX, shown.PixelY, shown.DisplayName);
                }
                var shownEastWest = shownSky.Geometry.Cardinals.Where(item => item.Name is "East" or "West").ToArray();
                Assert.HasCount(2, shownEastWest);
                foreach (var shown in shownEastWest)
                {
                    var configured = configuredSky.Geometry.Cardinals.Single(item => item.Name == shown.Name);
                    AssertMirrored(configured.PixelX!.Value, configured.PixelY!.Value, shown.PixelX!.Value, shown.PixelY!.Value,
                        $"sky map {shown.Name}");
                }

                // The copied optics now belong only to the operator's rig, so a further edit revises them in place.
                var active = catalog.Revisions.Single(revision => revision.RevisionId == composedId);
                var revise = await NamedRigQuickEditServiceTests.OwnerService(named, runtime).ApplyActiveRigEditAsync(
                    new ActiveRigEditRequest(active.RevisionId, catalog.Selection.Version, HorizontalFlip: true, 170,
                        active.Rig.Optics.FocalLengthMillimeters, active.Rig.Orientation.BoresightAltitudeDegrees,
                        active.Rig.Orientation.BoresightAzimuthDegrees, active.Rig.Orientation.RollAdjustmentDegrees),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(ActiveRigEditStatus.Staged, revise.Value?.Status, revise.Value?.Failure ?? revise.Message);
                Assert.AreEqual("Saved optics \"Installed optics copy\" revision 2.", revise.Value!.Recorded[0]);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    // A horizontal flip mirrors the image about its vertical centre line, x = 32 on the flip test's 64-pixel sensor.
    private static void AssertMirrored(double configuredX, double configuredY, double shownX, double shownY, string what)
    {
        Assert.AreEqual(64 - configuredX, shownX, 1e-6, $"The {what} is not mirrored east to west.");
        Assert.AreEqual(configuredY, shownY, 1e-6, $"The {what} moved vertically.");
    }

    // Runs the annotation step over the committed capture the way the processing worker does: the raw frame is rebuilt
    // from the stored evidence and the step reads its rig from the configuration it is given.
    private static async Task<(ProjectedAnnotationOverlay Overlay, bool[] Drawn)> AnnotateAsync(
        CameraModuleConfig configuration, CaptureLoopSubmission submission, RawCaptureReceipt capture)
    {
        var payload = await File.ReadAllBytesAsync(capture.StoredFrame.AbsolutePath).ConfigureAwait(false);
        var reconstruction = FrameReconstructor.TryReconstruct(capture.Manifest.Descriptor, payload, out var raw);
        Assert.IsTrue(reconstruction.IsValid, reconstruction.ReasonCode);
        var artifact = new FrameArtifact(capture.Manifest.Descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, raw!,
            recipeVersion: ProcessingIdentity.CreateRecipeIdentity(capture.Manifest.Descriptor.Artifact.Recipe).IdentitySha256);
        var context = new CaptureProcessingContext(configuration,
            submission with { Result = submission.Result with { Frame = raw, Artifacts = new FrameArtifactSet(artifact) } },
            capture);
        var executor = new RecordingRecipeExecutor();
        var adapter = new CameraAgentRecipeExecutionAdapter(executor);
        await new PreviewCaptureProcessingStep(new CaptureProcessingStepMetadata("Preview", "Preview", 0),
            new PreviewProcessingStepOptions(), adapter).ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var step = new AnnotationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Annotation", "Annotation", 1),
            new AnnotationProcessingStepOptions { DrawLabels = false, DrawCardinalDirections = true, CardinalScale = 1 },
            new ProjectedSceneStore(), new NoAnnotationSceneProvider(), adapter);

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        var overlay = executor.Requests.Single(request => request.Annotation is not null).Annotation!.ProjectionOverlay;
        Assert.IsNotNull(overlay, "The annotation step drew no projection overlay.");
        var preview = context.Artifacts![FrameArtifactRole.Preview].Frame.PixelData.ToArray();
        var annotated = context.Artifacts[FrameArtifactRole.AnnotatedPreview].Frame.PixelData.ToArray();
        Assert.HasCount(preview.Length, annotated);
        // Only what annotation drew over the preview.
        return (overlay, annotated.Select((value, index) => value != preview[index]).ToArray());
    }

    // The shape of the mark drawn between two columns of the 64-pixel annotation, independent of where it sits.
    private static string LetterAt(bool[] drawn, int fromX, int toX)
    {
        var lit = Enumerable.Range(0, 64)
            .SelectMany(y => Enumerable.Range(fromX, toX - fromX).Select(x => (X: x, Y: y)))
            .Where(point => drawn[point.Y * 64 + point.X])
            .ToArray();
        if (lit.Length == 0)
        {
            return string.Empty;
        }
        var left = lit.Min(point => point.X);
        var top = lit.Min(point => point.Y);
        var rows = lit.Max(point => point.Y) - top + 1;
        var columns = lit.Max(point => point.X) - left + 1;
        return string.Join('/', Enumerable.Range(top, rows).Select(y => new string(Enumerable.Range(left, columns)
            .Select(x => drawn[y * 64 + x] ? '#' : '.').ToArray())));
    }

    private sealed class RecordingRecipeExecutor : IProcessingRecipeExecutor
    {
        private readonly ProcessingRecipeExecutor _inner = new();

        public List<ProcessingExecutionRequest> Requests { get; } = [];

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _inner.ExecuteAsync(request, cancellationToken);
        }
    }

    private sealed class NoAnnotationSceneProvider : IAnnotationSceneProvider
    {
        public ValueTask<AnnotationSceneResult> BuildAsync(
            CameraModuleConfig config,
            ReconstructionDescriptor? descriptor,
            CameraFrame rawFrame,
            IReadOnlyList<string> constellationIds,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<AnnotationSceneResult>(
                new InvalidOperationException("The cardinal annotation should not build a sky scene."));
    }

    private static RecordingIngress CreateDurableIngress(IOptions<CameraAgentHostOptions> options, TimeProvider clock)
    {
        var state = new RawIngressState(clock);
        return new RecordingIngress(new RawCaptureIngress(options, new UnlimitedCapacity(), state, clock,
            new RawIngressTelemetry(state), NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector()), state);
    }

    private static CaptureLaneState HealthyLanes(TimeProvider clock, IOptions<CameraAgentHostOptions> options)
    {
        var state = new CaptureLaneState(clock, options);
        state.Update([]);
        return state;
    }

    private static CameraCaptureService CreateHostedService(CameraAgentConfigurationAccessor accessor,
        ICameraModule module, RecordingIngress ingress, CaptureControlTelemetry telemetry,
        CaptureAdmissionCoordinator admission, CaptureScheduleRuntimeCoordinator runtime,
        SqliteNamedRigProfileStore named, TestHostApplicationLifetime lifetime, TimeProvider clock)
        => new(accessor, new ModuleFactory(module), ingress, new RecordingDistributor(), clock,
            new AstronomyEnginePlanetEphemeris(), telemetry, admission, new FleetRuntimeState(clock), lifetime,
            NullLogger<CameraCaptureService>.Instance, scheduleRuntimeCoordinator: runtime, namedRigProfiles: named);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class UnlimitedCapacity : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) => new(long.MaxValue, long.MaxValue);
    }

    private sealed class StaticLoader(CameraModuleConfig config) : ICameraAgentConfigurationLoader
    {
        public Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(config);
    }

    private sealed class RecordingIngress(RawCaptureIngress inner, RawIngressState state) : IRawCaptureIngress, IDisposable
    {
        public RawIngressState State => state;
        public TaskCompletionSource<RawCaptureReceipt> Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; private set; }
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);
        public async ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig config, CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            try
            {
                var receipt = await inner.AcceptAsync(config, submission, cancellationToken).ConfigureAwait(false);
                if (receipt is not null) Captured.TrySetResult(receipt);
                return receipt;
            }
            catch (Exception exception)
            {
                Failure = exception;
                throw;
            }
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class RigRecordingModule(TimeProvider clock) : ICameraModule
    {
        private int _captures;
        public CameraModuleConfig? InitializedConfig { get; private set; }
        public string Id => "test";
        public string DisplayName => "Test";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
        {
            InitializedConfig = config;
            return Task.CompletedTask;
        }
        public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _captures) != 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Capture was not cancelled.");
            }
            // The frame is the size of the configured sensor, so annotation can draw on it.
            var sensor = InitializedConfig!.Rig.Sensor;
            var pixels = new byte[sensor.WidthPixels * sensor.HeightPixels];
            Array.Fill(pixels, (byte)1);
            var frame = new CameraFrame(clock.GetUtcNow().AddSeconds(-2), sensor.WidthPixels, sensor.HeightPixels,
                CameraPixelFormat.Mono8, pixels, new FrameMetadata(TimeSpan.FromMilliseconds(1), 0, 0, Guid.NewGuid().ToString("N")));
            return new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromMilliseconds(1), 0, null, null),
                TimeSpan.Zero, CaptureMode.Still, false);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public async Task PendingRig_OverrideDuringModuleInitializationRetriesWithoutMixedCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-pending-override-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero));
            var config = CreateConfig() with
            {
                AgentId = "pending-override-test",
                DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                    DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC"),
                Schedule = new CaptureScheduleDefinition("capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile("night", TimeSpan.FromMilliseconds(1), 0, TimeSpan.FromSeconds(1))],
                    [new CaptureWeeklyScheduleWindow("monday", DayOfWeek.Monday,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(23, 59)), "night")])
            };
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
            using var ingress = CreateDurableIngress(options, clock);
            using var store = new SqliteCaptureScheduleStore(ingress, options, clock);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress, options, clock, telemetry);
            var pipeline = new EmptyPipelineFactory();
            using var runtime = new CaptureScheduleRuntimeCoordinator(store, admission,
                ingress.State, HealthyLanes(clock, options), pipeline, clock);
            using var lifetime = new TestHostApplicationLifetime();
            var named = new SqliteNamedRigProfileStore(ingress, options, clock, store, runtime);
            var accessor = new CameraAgentConfigurationAccessor();
            await new CameraAgentConfigurationInitializer(new StaticLoader(config), accessor, pipeline,
                NullLogger<CameraAgentConfigurationInitializer>.Instance, store, namedRigProfiles: named)
                .StartAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await runtime.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var active = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var selection = (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection;
            var profile = await named.SaveProfileNameAsync(null, "Pending rig", CancellationToken.None).ConfigureAwait(false);
            var camera = await named.SaveEquipmentAsync(null, "camera", "Camera",
                JsonSerializer.SerializeToElement(new NamedCameraEquipment(config.Module, config.Rig.Sensor, config.Rig.Readout)),
                CancellationToken.None).ConfigureAwait(false);
            var optics = await named.SaveEquipmentAsync(null, "optics", "New optics",
                JsonSerializer.SerializeToElement(config.Rig.Optics with { FocalLengthMillimeters = 1 }),
                CancellationToken.None).ConfigureAwait(false);
            var mount = await named.SaveEquipmentAsync(null, "mount", "Mount",
                JsonSerializer.SerializeToElement(config.Rig.Orientation), CancellationToken.None).ConfigureAwait(false);
            var target = await named.ComposeAsync(profile.ProfileId, camera.RevisionId, optics.RevisionId,
                mount.RevisionId, CancellationToken.None).ConfigureAwait(false);
            var staged = await named.StageAsync(target.RevisionId, selection.Version, "override-race-stage", "owner", true,
                active.ActiveRevision.RevisionId, active.ActiveRevision.ProfileSha256, CancellationToken.None).ConfigureAwait(false);
            named.SnapshotStartupPendingSelection((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection);
            var stale = new GatedInitializationModule();
            var fresh = new RigRecordingModule(clock);
            var factory = new SequenceModuleFactory(stale, fresh);
            var service = new CameraCaptureService(accessor, factory, ingress, new RecordingDistributor(), clock,
                new AstronomyEnginePlanetEphemeris(), telemetry, admission, new FleetRuntimeState(clock), lifetime,
                NullLogger<CameraCaptureService>.Instance, scheduleRuntimeCoordinator: runtime, namedRigProfiles: named);
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lifetime.NotifyStarted();
            try
            {
                await stale.InitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var scheduleBeforeOverride = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                _ = await store.AddOverrideAsync(new CaptureScheduleOverride(
                    "override-during-init", active.ActiveRevision.RevisionId, active.ActiveRevision.ScheduleSha256,
                    CaptureScheduleOverrideMode.ForceOpen, clock.GetUtcNow().AddMinutes(-1),
                    clock.GetUtcNow().AddMinutes(1), "night", false, null),
                    "override-during-init", scheduleBeforeOverride.Version, "owner", null, CancellationToken.None)
                    .ConfigureAwait(false);
                stale.ReleaseInitialization.TrySetResult();
                await stale.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(0, stale.CaptureCalls);
                Assert.AreEqual(CaptureAdmissionState.Running, admission.Snapshot.State);
                Assert.AreEqual(active.ActiveRevision.RevisionId, runtime.Snapshot!.Revision.RevisionId);
                Assert.IsNull(named.PendingRuntimeFailure);
                Assert.IsNull(named.ActiveRuntimeFailure);
                var captured = await ingress.Captured.Task.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                Assert.AreEqual(2, factory.CreateCalls);
                Assert.AreEqual(target.Rig, fresh.InitializedConfig!.Rig);
                Assert.AreEqual(staged.ScheduleRevisionId, runtime.Snapshot!.Revision.RevisionId);
                Assert.AreEqual(staged.ScheduleRevisionId,
                    (await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.RevisionId);
                Assert.AreEqual(target.RevisionId,
                    (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.ActiveRevisionId);
                Assert.AreEqual(CameraRigProfileIdentity.ComputeSha256(target.Rig),
                    captured.Manifest.Descriptor.Profiles.Rig.Sha256);
                Assert.AreEqual(runtime.Snapshot.Revision.ProfileSha256,
                    captured.Manifest.Descriptor.CycleEvidence!.ScheduleAdmission!.LocalProfileSha256);
            }
            finally
            {
                stale.ReleaseInitialization.TrySetResult();
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PendingRig_CancelAndRestageSameRigInProcessWaitsForAnotherRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-restage-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero));
            var config = CreateConfig() with
            {
                AgentId = "restage-startup-test",
                DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                    DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC"),
                Schedule = new CaptureScheduleDefinition("capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile("night", TimeSpan.FromMilliseconds(1), 0, TimeSpan.FromSeconds(1))],
                    [new CaptureWeeklyScheduleWindow("monday", DayOfWeek.Monday,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(23, 59)), "night")])
            };
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
            using var ingress = CreateDurableIngress(options, clock);
            using var store = new SqliteCaptureScheduleStore(ingress, options, clock);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress, options, clock, telemetry);
            using var runtime = new CaptureScheduleRuntimeCoordinator(store, admission,
                ingress.State, HealthyLanes(clock, options), new EmptyPipelineFactory(), clock);
            using var lifetime = new TestHostApplicationLifetime();
            var named = new SqliteNamedRigProfileStore(ingress, options, clock, store, runtime);
            var accessor = new CameraAgentConfigurationAccessor();
            await new CameraAgentConfigurationInitializer(new StaticLoader(config), accessor, new EmptyPipelineFactory(),
                NullLogger<CameraAgentConfigurationInitializer>.Instance, store, namedRigProfiles: named)
                .StartAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await runtime.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var selection = (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection;
            var id = selection.ActiveRevisionId!;
            var first = await named.StageAsync(id, selection.Version, "startup-first", "owner", true,
                initial.ActiveRevision.RevisionId, initial.ActiveRevision.ProfileSha256, CancellationToken.None).ConfigureAwait(false);
            named.SnapshotStartupPendingSelection((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection);
            var stale = new GatedInitializationModule();
            var activeModule = new RigRecordingModule(clock);
            var factory = new SequenceModuleFactory(stale, activeModule);
            var service = new CameraCaptureService(accessor, factory, ingress, new RecordingDistributor(), clock,
                new AstronomyEnginePlanetEphemeris(), telemetry, admission, new FleetRuntimeState(clock), lifetime,
                NullLogger<CameraCaptureService>.Instance, scheduleRuntimeCoordinator: runtime, namedRigProfiles: named);
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lifetime.NotifyStarted();
            try
            {
                await stale.InitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var cancelled = await named.CancelPendingAsync(id, first.Version, "startup-cancel", "owner",
                    CancellationToken.None).ConfigureAwait(false);
                var second = await named.StageAsync(id, cancelled.Version, "startup-second", "owner", true,
                    initial.ActiveRevision.RevisionId, initial.ActiveRevision.ProfileSha256, CancellationToken.None).ConfigureAwait(false);
                stale.ReleaseInitialization.TrySetResult();
                await stale.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                _ = await ingress.Captured.Task.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                Assert.AreEqual(initial.ActiveRevision.RevisionId, runtime.Snapshot!.Revision.RevisionId);
                Assert.AreEqual(second.ScheduleRevisionId,
                    (await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision?.RevisionId);
                Assert.AreEqual("startup-second", (await named.GetAsync(CancellationToken.None).ConfigureAwait(false))
                    .Selection.PendingCommandKey);
                Assert.AreEqual(0, stale.CaptureCalls);
            }
            finally
            {
                stale.ReleaseInitialization.TrySetResult();
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class GatedInitializationModule : ICameraModule
    {
        public TaskCompletionSource InitializationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseInitialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CaptureCalls { get; private set; }
        public string Id => "test";
        public string DisplayName => "Test";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public async Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
        {
            InitializationStarted.TrySetResult();
            await ReleaseInitialization.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            CaptureCalls++;
            throw new InvalidOperationException("Stale module must not capture.");
        }
        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task PendingRig_PreparationFailureCapturesOnlyWithActiveRig()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-pending-preparation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero));
            var config = CreateConfig() with
            {
                AgentId = "pending-preparation-test",
                DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                    DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC"),
                Schedule = new CaptureScheduleDefinition("capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile("night", TimeSpan.FromMilliseconds(1), 0, TimeSpan.FromSeconds(1))],
                    [new CaptureWeeklyScheduleWindow("monday", DayOfWeek.Monday,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(23, 59)), "night")])
            };
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressReserveBytes = 0 });
            using var ingress = CreateDurableIngress(options, clock);
            using var store = new SqliteCaptureScheduleStore(ingress, options, clock);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress, options, clock, telemetry);
            var validator = new RejectPendingOpticsValidator();
            using var runtime = new CaptureScheduleRuntimeCoordinator(store, admission,
                ingress.State, HealthyLanes(clock, options), new EmptyPipelineFactory(), clock,
                moduleConfigurationValidator: validator);
            using var lifetime = new TestHostApplicationLifetime();
            var named = new SqliteNamedRigProfileStore(ingress, options, clock, store, runtime);
            var accessor = new CameraAgentConfigurationAccessor();
            await new CameraAgentConfigurationInitializer(new StaticLoader(config), accessor, new EmptyPipelineFactory(),
                NullLogger<CameraAgentConfigurationInitializer>.Instance, store, namedRigProfiles: named)
                .StartAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await runtime.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var selection = (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection;
            var activeSchedule = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var profile = await named.SaveProfileNameAsync(null, "Pending rig", CancellationToken.None).ConfigureAwait(false);
            var camera = await named.SaveEquipmentAsync(null, "camera", "Camera",
                JsonSerializer.SerializeToElement(new NamedCameraEquipment(config.Module, config.Rig.Sensor, config.Rig.Readout)),
                CancellationToken.None).ConfigureAwait(false);
            var optics = await named.SaveEquipmentAsync(null, "optics", "Pending optics",
                JsonSerializer.SerializeToElement(config.Rig.Optics with { FocalLengthMillimeters = 1 }),
                CancellationToken.None).ConfigureAwait(false);
            var mount = await named.SaveEquipmentAsync(null, "mount", "Mount",
                JsonSerializer.SerializeToElement(config.Rig.Orientation), CancellationToken.None).ConfigureAwait(false);
            var pending = await named.ComposeAsync(profile.ProfileId, camera.RevisionId, optics.RevisionId,
                mount.RevisionId, CancellationToken.None).ConfigureAwait(false);
            _ = await named.StageAsync(pending.RevisionId, selection.Version, "pending-preparation", "owner", true,
                activeSchedule.ActiveRevision.RevisionId, activeSchedule.ActiveRevision.ProfileSha256,
                CancellationToken.None).ConfigureAwait(false);
            named.SnapshotStartupPendingSelection((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection);
            validator.Reject = true;

            var module = new RigRecordingModule(clock);
            var service = CreateHostedService(accessor, module, ingress, telemetry, admission, runtime, named, lifetime, clock);
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lifetime.NotifyStarted();
            try
            {
                var captured = await ingress.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Assert.AreEqual(config.Rig, module.InitializedConfig!.Rig);
                Assert.AreEqual(CameraRigProfileIdentity.ComputeSha256(config.Rig),
                    captured.Manifest.Descriptor.Profiles.Rig.Sha256);
                Assert.IsNotNull(named.PendingRuntimeFailure);
                Assert.AreEqual(pending.RevisionId,
                    (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RejectPendingOpticsValidator : ICameraModuleConfigurationValidator
    {
        public bool Reject { get; set; }

        public void Validate(CameraModuleConfig configuration)
        {
            if (Reject && configuration.Rig.Optics.FocalLengthMillimeters == 1)
                throw new ArgumentException("Pending optics cannot be initialized.", nameof(configuration));
        }
    }

    [TestMethod]
    public async Task PendingRig_HostedModuleInitializationFailureLeavesSelectionCancelable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-capture-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = CreateConfig() with
            {
                AgentId = "test-agent",
                DeploymentLocation = DeploymentLocationSnapshot.Create("test-location", 1, "test", null,
                    DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC"),
                Schedule = new CaptureScheduleDefinition("capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile("test", TimeSpan.FromMilliseconds(1), 0,
                        TimeSpan.FromSeconds(1))],
                    [new CaptureWeeklyScheduleWindow("monday", DayOfWeek.Monday,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(18, 0)),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(6, 0), DayOffset: 1),
                        "test")])
            };
            var ingress = new PassthroughRawIngress(root);
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressSqliteBusyTimeoutSeconds = 1 });
            using var store = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await store.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, store);
            var catalog = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var selected = catalog.Selection.ActiveRevisionId!;
            var staged = await named.StageAsync(selected, catalog.Selection.Version, "pending-hosted", "owner", true,
                initial.ActiveRevision.RevisionId, initial.ActiveRevision.ProfileSha256, CancellationToken.None).ConfigureAwait(false);
            named.SnapshotStartupPendingSelection((await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(ingress, options, TimeProvider.System, telemetry);
            using var runtime = new CaptureScheduleRuntimeCoordinator(store, admission,
                new RawIngressState(TimeProvider.System), new CaptureLaneState(TimeProvider.System, options),
                new EmptyPipelineFactory(), TimeProvider.System);
            var failure = new InitializationFailingModule();
            var factory = new SequenceModuleFactory(failure, new GatedCameraModule());
            using var lifetime = new TestHostApplicationLifetime();
            var service = new CameraCaptureService(new ConfigurationAccessor(config), factory, ingress,
                new RecordingDistributor(), TimeProvider.System, new AstronomyEnginePlanetEphemeris(),
                telemetry, admission, new FleetRuntimeState(TimeProvider.System), lifetime,
                NullLogger<CameraCaptureService>.Instance, scheduleRuntimeCoordinator: runtime, namedRigProfiles: named);
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lifetime.NotifyStarted();
            await Task.WhenAny(failure.DisposalAttempted.Task, Task.Delay(TimeSpan.FromSeconds(12))).ConfigureAwait(false);
            Assert.IsTrue(failure.DisposalAttempted.Task.IsCompleted,
                $"Hosted module did not initialize; create calls: {factory.CreateCalls}; pending failure: {named.PendingRuntimeFailure}; runtime: {runtime.Snapshot?.Revision.RevisionId}");
            Assert.AreEqual(selected, (await named.GetAsync(CancellationToken.None).ConfigureAwait(false)).Selection.PendingRevisionId);
            Assert.AreEqual(staged.ScheduleRevisionId,
                (await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision?.RevisionId);
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual("cancelled", (await named.CancelPendingAsync(selected, catalog.Selection.Version + 1,
                "cancel-hosted", "owner", CancellationToken.None).ConfigureAwait(false)).Disposition);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);
        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => throw new NotSupportedException();
    }

    private sealed class InitializationFailingModule : ICameraModule
    {
        public TaskCompletionSource DisposalAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "test";
        public string DisplayName => "Test";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Injected pending module failure.");
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            DisposalAttempted.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task DisposalFailure_FailsClosedWithoutOpeningReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-capture-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = CreateConfig();
            var ingress = new PassthroughRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = new CaptureAdmissionCoordinator(
                ingress,
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    RawIngressSqliteBusyTimeoutSeconds = 1
                }),
                TimeProvider.System,
                telemetry);
            var failing = new FailingModule();
            var factory = new SequenceModuleFactory(failing, new GatedCameraModule());
            using var applicationLifetime = new TestHostApplicationLifetime();
            var service = new CameraCaptureService(
                new ConfigurationAccessor(config),
                factory,
                ingress,
                new RecordingDistributor(),
                TimeProvider.System,
                new AstronomyEnginePlanetEphemeris(),
                telemetry,
                coordinator,
                new FleetRuntimeState(TimeProvider.System),
                applicationLifetime,
                NullLogger<CameraCaptureService>.Instance);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await applicationLifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            applicationLifetime.NotifyStarted();

            await failing.DisposalAttempted.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            Assert.AreEqual(1, factory.CreateCalls);
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task StopAsync_CancelsCaptureAndDisposesModule()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-capture-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = CreateConfig();
            var distributor = new RecordingDistributor();
            var ingress = new PassthroughRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = new CaptureAdmissionCoordinator(
                ingress,
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    RawIngressSqliteBusyTimeoutSeconds = 1
                }),
                TimeProvider.System,
                telemetry);
            var waitingModule = new GatedCameraModule();
            using (var waitingLifetime = new TestHostApplicationLifetime())
            {
                var waitingService = new CameraCaptureService(
                    new ConfigurationAccessor(config),
                    new ModuleFactory(waitingModule),
                    ingress,
                    distributor,
                    TimeProvider.System,
                    new AstronomyEnginePlanetEphemeris(),
                    telemetry,
                    coordinator,
                    new FleetRuntimeState(TimeProvider.System),
                    waitingLifetime,
                    NullLogger<CameraCaptureService>.Instance);

                await waitingService.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await waitingLifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                await waitingService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                Assert.IsFalse(waitingModule.InitializationStarted.Task.IsCompleted,
                    "Stopping before host startup must not initialize the camera.");
            }

            var module = new GatedCameraModule();
            using var applicationLifetime = new TestHostApplicationLifetime();
            var service = new CameraCaptureService(
                new ConfigurationAccessor(config),
                new ModuleFactory(module),
                ingress,
                distributor,
                TimeProvider.System,
                new AstronomyEnginePlanetEphemeris(),
                telemetry,
                coordinator,
                new FleetRuntimeState(TimeProvider.System),
                applicationLifetime,
                NullLogger<CameraCaptureService>.Instance);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await applicationLifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.IsFalse(module.InitializationStarted.Task.IsCompleted,
                "Camera initialization must wait until the host has fully started.");
            applicationLifetime.NotifyStarted();
            await module.SecondCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await module.CaptureCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.AreEqual(1, distributor.EphemeralCount);
            Assert.IsTrue(module.IsDisposed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task UnpublishedConfiguration_DoesNotInitializeOrCaptureAfterHostStarts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-capture-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var ingress = new PassthroughRawIngress(root);
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = new CaptureAdmissionCoordinator(ingress,
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root, RawIngressSqliteBusyTimeoutSeconds = 1 }),
                TimeProvider.System, telemetry);
            var module = new GatedCameraModule();
            var factory = new SequenceModuleFactory(module);
            using var lifetime = new TestHostApplicationLifetime();
            var service = new CameraCaptureService(new CameraAgentConfigurationAccessor(), factory, ingress,
                new RecordingDistributor(), TimeProvider.System, new AstronomyEnginePlanetEphemeris(),
                telemetry, coordinator, new FleetRuntimeState(TimeProvider.System), lifetime,
                NullLogger<CameraCaptureService>.Instance);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await lifetime.ApplicationStartedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            lifetime.NotifyStarted();
            await Task.Delay(100).ConfigureAwait(false);
            Assert.AreEqual(0, factory.CreateCalls);
            Assert.IsFalse(module.InitializationStarted.Task.IsCompleted);
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, true); }
    }

    private static CameraModuleConfig CreateConfig()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1), 0, 0),
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }),
            CapturePipelineConfig.Empty);

    private sealed class ConfigurationAccessor(CameraModuleConfig config) : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => true;

        public void SetConfiguration(CameraModuleConfig value) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(config);
    }

    private sealed class ModuleFactory(ICameraModule module) : ICameraModuleFactory
    {
        public ICameraModule Create(string moduleType) => module;
    }

    private sealed class SequenceModuleFactory(params ICameraModule[] modules) : ICameraModuleFactory
    {
        private readonly Queue<ICameraModule> _modules = new(modules);

        public int CreateCalls { get; private set; }

        public ICameraModule Create(string moduleType)
        {
            CreateCalls++;
            return _modules.Dequeue();
        }
    }

    private sealed class FailingModule : ICameraModule
    {
        public TaskCompletionSource DisposalAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "failing";
        public string DisplayName => "Failing";
        public string ModuleType => "Test";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Injected initialization failure.");

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposalAttempted.TrySetResult();
            return ValueTask.FromException(new InvalidOperationException("Injected disposal failure."));
        }
    }

    private sealed class PassthroughRawIngress(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class GatedCameraModule : ICameraModule
    {
        private int _captureCount;

        public TaskCompletionSource SecondCaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CaptureCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InitializationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "test";

        public string DisplayName => "Test";

        public string ModuleType => "Test";

        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
        {
            InitializationStarted.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _captureCount) == 1)
            {
                var frame = new CameraFrame(
                    DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                    new FrameMetadata(TimeSpan.FromMilliseconds(1), 0, 0));
                return new CaptureResult(
                    frame, new CaptureSetpoint(TimeSpan.FromMilliseconds(1), 0, null, null),
                    TimeSpan.Zero, CaptureMode.Still, false);
            }

            SecondCaptureStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Infinite delay completed without cancellation.");
            }
            catch (OperationCanceledException)
            {
                CaptureCancellationObserved.TrySetResult();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();

        public TaskCompletionSource ApplicationStartedObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted
        {
            get
            {
                ApplicationStartedObserved.TrySetResult();
                return _started.Token;
            }
        }

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }

        public void NotifyStarted() => _started.Cancel();

        public void Dispose() => _started.Dispose();
    }

    private sealed class RecordingDistributor : ICaptureDistributor
    {
        public int EphemeralCount { get; private set; }

        public void NotifyCommittedCapture()
        {
        }

        public ValueTask ProcessEphemeralAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            EphemeralCount++;
            return ValueTask.CompletedTask;
        }
    }
}
