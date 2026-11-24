using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static readonly JsonSerializerOptions CenteredPublicationJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task CenteredViewPublishesRawFallbackWhenThePinnedProfileHasNoCalibrationStep()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-publication-no-linear");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRawOnlyProfile(root, start);
        try
        {
            using var provider = CenteredRuntimeProvider(root, clock);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var schedule = await ReadCenteredPublicationScheduleAsync(provider, token).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var device = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = device.ConfigureAwait(false);
            await device.InitializeAsync(active, token).ConfigureAwait(false);
            var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, device, deviceClock, clock, active, start, 1, token,
                schedule: schedule)
                .ConfigureAwait(false);
            var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
            var key = attempt.Reservation.Pending.Key.Camera;
            var stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(key, token).ConfigureAwait(false));
            Assert.IsNotNull(stream);
            // No processing lane or overlay is needed for this explicit admission-time disposition.
            var worker = provider.GetRequiredService<CenteredViewPublicationWorker>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(token);
            var published = 0;
            views.Changed += InterruptAfterIssuance;
            try
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    worker.DrainCameraAsync(key, stream, CancellationToken.None, interrupt.Token).AsTask()).ConfigureAwait(false);
            }
            finally
            {
                views.Changed -= InterruptAfterIssuance;
            }
            Assert.AreEqual(0L, await views.ReadProductCursorAsync(key, token).ConfigureAwait(false));
            var issued = await views.ReadLastLiveAsync(key, token).ConfigureAwait(false);
            Assert.IsNotNull(issued);
            await worker.DrainCameraAsync(key, stream, CancellationToken.None, token).ConfigureAwait(false);
            Assert.IsGreaterThan(0L, await views.ReadProductCursorAsync(key, token).ConfigureAwait(false));
            Assert.AreEqual(issued.Interval, (await views.ReadLastLiveAsync(key, token).ConfigureAwait(false))?.Interval);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            var archive = await views.ReadArchiveAsync(captureId, token).ConfigureAwait(false);
            var viewBase = Assert.ContainsSingle(archive).Base;
            Assert.AreEqual(CenteredViewImageSource.RawFallback, viewBase.Definition.Preview.Source);
            Assert.AreEqual("linear.no-configured-step", viewBase.Definition.Preview.QualificationReason);
            Assert.IsNull(viewBase.Definition.Reference.Linear);
            Guid terminalReference = default;
            for (var ordinal = 2; ordinal <= 4; ordinal++)
            {
                var next = await CaptureCenteredRuntimeAttemptAsync(ingress, device, deviceClock, clock, active,
                    start.AddSeconds((ordinal - 1) * 2), 1, token, schedule: schedule).ConfigureAwait(false);
                var nextReceipt = await ingress.AcceptCenteredAsync(active, next.Submission, next.Reservation, token).ConfigureAwait(false);
                Assert.AreEqual(ordinal, next.Reservation.Pending.Key.Ordinal);
                terminalReference = nextReceipt.Manifest.Descriptor.Capture.CaptureId;
                await worker.DrainCameraAsync(key, stream, CancellationToken.None, token).ConfigureAwait(false);
            }
            var terminalSingle = Assert.ContainsSingle(await views.ReadArchiveAsync(terminalReference, token).ConfigureAwait(false)).Base;
            Assert.AreEqual(CenteredViewImageSource.RawFallback, terminalSingle.Definition.Preview.Source);
            var lookup = provider.GetRequiredService<ICenteredViewLookup>();
            var pending = await lookup.ReadLiveContextAsync(token).ConfigureAwait(false);
            Assert.IsNotNull(pending);
            Assert.AreEqual(stream, pending.Stream);
            Assert.AreEqual(terminalReference, pending.AdmissionHead?.CaptureId);
            Assert.AreEqual(CenteredViewLiveState.Building, pending.State);
            Assert.AreEqual("centered.awaiting-window-publication", pending.Reason);
            TestContext.WriteLine($"Before terminal publication: state={pending.State}/{pending.Reason}");

            var window = await provider.GetRequiredService<ICenteredWindowReader>().ReadAsync(terminalReference, token).ConfigureAwait(false);
            Assert.IsNotNull(window);
            clock.UtcNow = window.DeadlineUtc.AddMilliseconds(1);
            await provider.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var disposition = await store.ReadCenteredWindowByReferenceAsync(terminalReference, token).ConfigureAwait(false);
            Assert.IsNotNull(disposition);
            Assert.IsTrue(disposition.IsTerminal);
            Assert.AreEqual(CenteredWindowSlotState.Rejected, disposition.Closure.Center.State);
            Assert.AreEqual(new ExposureKey(key, stream.RunId, terminalReference), disposition.Closure.Center.Input?.Key);
            Assert.IsNull(disposition.Publication);
            Assert.IsFalse(await views.IsPublishedSingleFallbackAsync(terminalSingle, token).ConfigureAwait(false),
                "The archived Single must wait for the publisher to select it, even after the window is terminal.");
            await provider.GetRequiredService<CenteredViewWindowPublicationWorker>().PublishReadyAsync(token).ConfigureAwait(false);
            using var connection = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT live_base_id, retry_utc, terminal_reason
                FROM processing_centered_view_window_work WHERE window_sha256 = $window;
                """;
            command.Parameters.AddWithValue("$window", disposition.Closure.Schedule.Identity);
            using (var result = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                Assert.IsTrue(await result.ReadAsync(token).ConfigureAwait(false));
                Assert.AreEqual(terminalSingle.BaseId, result.GetString(0));
                Assert.IsTrue(await result.IsDBNullAsync(1, token).ConfigureAwait(false));
                Assert.IsTrue(await result.IsDBNullAsync(2, token).ConfigureAwait(false));
            }
            var candidate = await views.ReadLiveCandidateAsync(stream, token).ConfigureAwait(false);
            Assert.IsNotNull(candidate);
            Assert.AreEqual(terminalSingle.BaseId, candidate);
            var live = await views.SelectLiveAsync(stream, candidate, token).ConfigureAwait(false);
            Assert.IsNotNull(live);
            Assert.AreEqual(terminalSingle.BaseId, live.View.Base.BaseId);
            var definition = live.View.Base.Definition;
            Assert.AreEqual(CenteredViewKind.Single, definition.Kind);
            Assert.AreEqual(CenteredViewImageSource.RawFallback, definition.Preview.Source);
            Assert.AreEqual("linear.no-configured-step", definition.Preview.QualificationReason);
            Assert.AreEqual(terminalSingle.Definition.Preview.PayloadSha256, definition.Preview.PayloadSha256);
            var context = await lookup.ReadLiveContextAsync(token).ConfigureAwait(false);
            Assert.IsNotNull(context);
            Assert.AreEqual(stream, context.Stream);
            Assert.AreEqual(terminalReference, context.AdmissionHead?.CaptureId);
            TestContext.WriteLine($"terminal={disposition.IsTerminal}, center={disposition.Closure.Center.State}, receipt=absent, " +
                $"selected={definition.ReferenceOrder}, source={definition.Preview.Source}, state={context.State}/{context.Reason}");
            Assert.AreEqual(CenteredViewLiveState.Ready, context.State);
            Assert.IsNull(context.Reason);

            clock.UtcNow = clock.UtcNow.AddSeconds(16);
            CollectionAssert.DoesNotContain((await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                disposition.Closure.Schedule.Identity);
            Assert.AreEqual(viewBase.BaseId, Assert.ContainsSingle(await views.ReadArchiveAsync(captureId, token).ConfigureAwait(false)).Base.BaseId);

            void InterruptAfterIssuance(CenteredViewStream changed)
            {
                if (changed == stream && ++published == 2)
                    interrupt.Cancel();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CenteredViewRetriesAnUnavailableRawPreviewAfterAdvancingTheCursorAndRestarting()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-publication-raw-retry");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRawOnlyProfile(root, start);
        var cameraKey = new CameraKey(profile.AgentId!, profile.CenteredExposure!.CameraId);
        Guid unavailableArtifact = default;
        Guid missedCapture;
        long missedRevision;
        long cursor;
        CenteredViewCurrent issued;
        try
        {
            using (var provider = CenteredRuntimeProvider(root, clock, services =>
            {
                services.AddSingleton<CameraAgentArtifactService>();
                services.AddSingleton<ICameraAgentArtifactService>(sp =>
                {
                    var actual = sp.GetRequiredService<CameraAgentArtifactService>();
                    var fault = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
                    fault.Setup(service => service.OpenContentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                        .Returns((Guid artifact, CancellationToken ct) => artifact == unavailableArtifact
                            ? ValueTask.FromResult(new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Unavailable))
                            : actual.OpenContentAsync(artifact, ct));
                    return fault.Object;
                });
            }))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var schedule = await ReadCenteredPublicationScheduleAsync(provider, token).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var device = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = device.ConfigureAwait(false);
                await device.InitializeAsync(active, token).ConfigureAwait(false);

                var first = await CaptureCenteredRuntimeAttemptAsync(ingress, device, deviceClock, clock, active, start, 1, token,
                    schedule: schedule)
                    .ConfigureAwait(false);
                var committed = await ingress.AcceptCenteredAsync(active, first.Submission, first.Reservation, token).ConfigureAwait(false);
                unavailableArtifact = committed.Manifest.Descriptor.Artifact.ArtifactId;
                missedCapture = committed.Manifest.Descriptor.Capture.CaptureId;
                var products = provider.GetRequiredService<IExposureProductReader>();
                missedRevision = Assert.ContainsSingle(await products.ReadProductsAfterAsync(cameraKey, 0, 4, token)
                    .ConfigureAwait(false)).Revision!.Value;

                var next = await CaptureCenteredRuntimeAttemptAsync(ingress, device, deviceClock, clock, active, start.AddSeconds(2), 1, token,
                    schedule: schedule)
                    .ConfigureAwait(false);
                var nextCapture = await ingress.AcceptCenteredAsync(active, next.Submission, next.Reservation, token).ConfigureAwait(false);
                var stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(cameraKey, token).ConfigureAwait(false));
                Assert.IsNotNull(stream);
                await provider.GetRequiredService<CenteredViewPublicationWorker>()
                    .DrainCameraAsync(cameraKey, stream, CancellationToken.None, token).ConfigureAwait(false);
                var views = provider.GetRequiredService<SqliteCenteredViews>();
                cursor = await views.ReadProductCursorAsync(cameraKey, token).ConfigureAwait(false);
                Assert.IsGreaterThan(missedRevision, cursor);
                Assert.IsEmpty(await views.ReadArchiveAsync(missedCapture, token).ConfigureAwait(false));
                var live = await views.ReadLastLiveAsync(cameraKey, token).ConfigureAwait(false);
                Assert.IsNotNull(live);
                Assert.AreEqual(nextCapture.Manifest.Descriptor.Capture.CaptureId, live.View.Base.Definition.Reference.Capture.CaptureId);
                issued = live;
            }

            clock.UtcNow = clock.UtcNow.AddSeconds(16);
            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var index = reopened.GetRequiredService<SqliteCenteredViews>();
            CollectionAssert.AreEqual(new[] { missedRevision }, (await index.ReadDueProductRetriesAsync(cameraKey, 4, token)
                .ConfigureAwait(false)).ToArray());
            var worker = reopened.GetRequiredService<CenteredViewPublicationWorker>();
            await worker.DrainCameraAsync(cameraKey, null, CancellationToken.None, token).ConfigureAwait(false);
            await worker.DrainCameraAsync(cameraKey, null, CancellationToken.None, token).ConfigureAwait(false);
            var archive = Assert.ContainsSingle(await index.ReadArchiveAsync(missedCapture, token).ConfigureAwait(false));
            Assert.AreEqual(CenteredViewImageSource.RawFallback, archive.Base.Definition.Preview.Source);
            Assert.AreEqual(unavailableArtifact, archive.Base.Definition.Science.ArtifactId);
            Assert.AreEqual(archive.InitialRevisionId, archive.LatestRevisionId);
            Assert.IsNull(archive.LastLiveRevisionId);
            Assert.AreEqual(issued.Interval, (await index.ReadLastLiveAsync(cameraKey, token).ConfigureAwait(false))?.Interval);
            Assert.AreEqual(cursor, await index.ReadProductCursorAsync(cameraKey, token).ConfigureAwait(false));
            Assert.IsEmpty(await index.ReadDueProductRetriesAsync(cameraKey, 4, token).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CameraModuleConfig CenteredRawOnlyProfile(string root, DateTimeOffset start)
    {
        var configured = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        return configured with
        {
            Pipeline = configured.Pipeline with
            {
                Steps = configured.Pipeline.Steps.Where(step => step.Type != "Calibration").ToArray()
            }
        };
    }

    private static async Task<CenteredScheduleBinding> ReadCenteredPublicationScheduleAsync(
        ServiceProvider provider, CancellationToken token)
    {
        var runtime = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        var grant = (await runtime.WaitForGrantAsync(wait.Token).ConfigureAwait(false)).Evidence;
        return new(grant.ScheduleRevisionSha256, grant.LocalProfileSha256, grant.SetpointProfileId, grant.IntervalId,
            grant.OverrideId, CaptureCadenceMode.MinimumStartInterval);
    }

    [TestMethod]
    [DataRow("camera")]
    [DataRow("profile")]
    [DataRow("pause")]
    public async Task CenteredViewRejectsAnIssuedCandidateAfterTheSelectedAuthorityChanges(string change)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-profile-fence");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        try
        {
            using var provider = CenteredRuntimeProvider(root, clock);
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var schedule = await ReadCenteredPublicationScheduleAsync(provider, token).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token,
                schedule: schedule)
                .ConfigureAwait(false);
            await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
            var oldCamera = attempt.Reservation.Pending.Key.Camera;
            var stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(oldCamera, token).ConfigureAwait(false));
            Assert.IsNotNull(stream);
            await provider.GetRequiredService<CenteredViewPublicationWorker>().DrainCameraAsync(oldCamera, stream, CancellationToken.None, token)
                .ConfigureAwait(false);
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var issued = await views.ReadLastLiveAsync(oldCamera, token).ConfigureAwait(false);
            Assert.IsNotNull(issued);
            var staleCandidate = await views.ReadLiveCandidateAsync(stream, token).ConfigureAwait(false);
            Assert.IsNotNull(staleCandidate);
            Assert.AreEqual(issued.View.Base.BaseId, staleCandidate);

            var runtime = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
            var context = runtime.CaptureContext;
            var nextCamera = new CameraKey(oldCamera.AgentId, "centered-replacement");
            if (change == "pause")
            {
                var admission = provider.GetRequiredService<CaptureAdmissionCoordinator>();
                await admission.PauseAsync("pause-publication", admission.Snapshot.Version, "publication-test", null, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(CaptureAdmissionState.Paused, admission.Snapshot.State);
            }
            else
            {
                var replacement = profile with { CenteredExposure = profile.CenteredExposure! with { CameraId = nextCamera.CameraId } };
                if (change == "profile")
                {
                    var options = JsonNode.Parse(profile.Module.Options!.Value.GetRawText())!.AsObject();
                    options["seed"] = 1922;
                    replacement = profile with { Module = profile.Module with { Options = JsonSerializer.SerializeToElement(options) } };
                }
                var stored = await provider.GetRequiredService<SqliteCaptureScheduleStore>().GetSnapshotAsync(token).ConfigureAwait(false);
                var staged = await runtime.StageAsync(LocalCaptureProfileDefinition.CreateV2(replacement, replacement.Schedule!),
                    "stage-replacement", stored.Version, "publication-test", null, token).ConfigureAwait(false);
                Assert.IsNotNull(staged.PendingRevision);
                await runtime.ActivateAsync(staged.PendingRevision.RevisionId, "activate-replacement", staged.Version,
                    "publication-test", null, token).ConfigureAwait(false);
                Assert.IsTrue(context.RevisionChanged.IsCancellationRequested);
                Assert.AreNotEqual(schedule.LocalProfile, runtime.CaptureContext.Snapshot.Revision.ProfileSha256);
            }
            Assert.AreEqual(stream.RunId, ingress.RecentCenteredExposures.ReadActive(oldCamera).RunId);

            Assert.IsNull(await views.SelectLiveAsync(stream, staleCandidate, token).ConfigureAwait(false));
            var late = new CenteredViewLayer(new("test-late-reference", CenteredViewLayerScope.Reference,
                    issued.View.Base.Definition.Reference.Capture.CaptureId), 1, null, clock.GetUtcNow(),
                CenteredViewReadiness.Incompatible, "test.unavailable-layer", null);
            var enriched = await views.PublishLayerAsync(staleCandidate, late, token).ConfigureAwait(false);
            Assert.IsNotNull(enriched);
            Assert.AreNotEqual(issued.View.Revision.RevisionId, enriched.Revision.RevisionId);
            Assert.AreEqual(issued.Interval, (await views.ReadLastLiveAsync(oldCamera, token).ConfigureAwait(false))?.Interval);
            Assert.IsNull(await views.ReadLastLiveAsync(nextCamera, token).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, false, false)]
    [DataRow(CameraPixelFormat.BayerRggb16, true, false)]
    [DataRow(CameraPixelFormat.BayerRggb16, false, false)]
    [DataRow(CameraPixelFormat.Mono16, false, true)]
    [DataRow(CameraPixelFormat.Mono16, true, true)]
    [DataRow(CameraPixelFormat.Mono16, false, false, true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The historical fixture selects between constant schema statements.")]
    public async Task CenteredViewPublishesTheAcquiredSingleThenTheFrozenRegisteredStack(CameraPixelFormat format,
        bool wideField, bool detectLateTrail, bool hasTrustedCutoff = false)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-publication-live");
        var token = CancellationToken.None;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        // The wide field and injected trail use calibrated pixel positions on the 224-square sensor.
        var profile = CenteredRuntimeProfile(root, start, format, size: wideField || detectLateTrail ? 224 : 160);
        if (detectLateTrail)
            profile = AddCenteredTrailScenario(profile, start);
        var catalog = wideField ? CenteredRuntimeWideCatalog(start, profile) : CenteredRuntimeCatalog(start);
        var expectedDisposition = format == CameraPixelFormat.BayerRggb16 && !wideField
            ? CenteredStackDisposition.SingleExposure : CenteredStackDisposition.Registered;
        var expectStack = expectedDisposition == CenteredStackDisposition.Registered;
        var expectTrailMembership = detectLateTrail && wideField;
        var upgradePreviousPublisher = expectStack && !detectLateTrail;
        var recoverUnpublishedStack = format == CameraPixelFormat.Mono16 && !detectLateTrail;
        CenteredViewSavedSelection? saved = null;
        CenteredWindowDisposition? unpublished = null;
        var ids = new List<Guid>();
        CenteredViewCurrent? issued103 = null;
        CenteredViewCurrent? issued104 = null;
        try
        {
            using (var provider = CenteredRuntimeProvider(root, clock, services =>
                services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning)),
                detectTransients: detectLateTrail))
            {
                if (detectLateTrail)
                    profile = await PersistCenteredTrailLocationAsync(provider, profile, token).ConfigureAwait(false);
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var schedule = await ReadCenteredPublicationScheduleAsync(provider, token).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var products = provider.GetRequiredService<CenteredViewPublicationWorker>();
                var windows = provider.GetRequiredService<CenteredViewWindowPublicationWorker>();
                var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
                var registration = provider.GetRequiredService<CenteredWindowRegistrationWorker>();
                var processing = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
                var exposureProducts = provider.GetRequiredService<IExposureProductReader>();
                var views = provider.GetRequiredService<SqliteCenteredViews>();
                for (var index = 0; index < 6; index++)
                {
                    var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active,
                        start.AddSeconds(index * 2), 1, token, schedule: schedule).ConfigureAwait(false);
                    if (wideField && format == CameraPixelFormat.BayerRggb16)
                        AssertWideFieldBelowClipping(attempt.Submission.Result.Frame);
                    var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token)
                        .ConfigureAwait(false);
                    ids.Add(receipt.Manifest.Descriptor.Capture.CaptureId);
                    await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
                    if (detectLateTrail)
                        await StageCenteredTransientFrameAsync(provider, active, token).ConfigureAwait(false);
                    var key = attempt.Reservation.Pending.Key.Camera;
                    var stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(key, token).ConfigureAwait(false));
                    Assert.IsNotNull(stream);
                    var exposure = new ExposureKey(key, stream.RunId, ids[index]);
                    var execution = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(ids[index], stream.GenerationId);
                    var sources = await processing.ReadCenteredViewSourcesAsync(exposure,
                        execution, 0, 24, token).ConfigureAwait(false);
                    var measurementSource = Assert.ContainsSingle(sources.Where(source =>
                        source.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion));
                    Assert.AreEqual(execution, measurementSource.Slot.ExecutionId);
                    Assert.AreEqual(ExposureProductAvailability.Available, measurementSource.Availability);
                    Assert.IsNotNull(measurementSource.Product);
                    var product = measurementSource.Product;
                    var (_, measurement) = await persistence.ReadCenteredViewMeasurementAsync(exposure.CaptureId,
                        new(product.ArtifactId, product.OutputIdentitySha256, product.PayloadSha256), token).ConfigureAwait(false);
                    var linear = await exposureProducts.ReadLinearAsync(exposure, token).ConfigureAwait(false);
                    Assert.IsNotNull(linear?.Product);
                    Assert.AreEqual(exposure, measurement.Input.Exposure);
                    Assert.AreEqual(linear.Revision, measurement.Input.Step3Revision);
                    Assert.AreEqual(linear.Disposition, measurement.Input.Step3Disposition);
                    Assert.AreEqual(linear.Product.ArtifactId, measurement.Input.Linear?.ArtifactId);
                    await products.DrainCameraAsync(key, stream, CancellationToken.None, token).ConfigureAwait(false);
                    await coordinator.CloseReadyAsync(token).ConfigureAwait(false);
                    await registration.RegisterPendingAsync(token).ConfigureAwait(false);
                    CenteredWindowDisposition? disposition = null;
                    if (index >= 4)
                    {
                        disposition = await processing.ReadCenteredWindowByReferenceAsync(ids[index - 2], token)
                            .ConfigureAwait(false);
                        var slots = disposition is null ? "window open" : string.Join(", ",
                            disposition.Closure.Slots.Select(slot => $"{slot.Offset}: {slot.State} / {slot.Reason}"));
                        Assert.IsNotNull(disposition?.Publication, $"{disposition?.FailureReason} ({slots})");
                        Assert.AreEqual(expectedDisposition, disposition.Publication.StackDisposition, slots);
                        if (recoverUnpublishedStack && index == 5)
                        {
                            unpublished = disposition;
                            await AssertPublisherRejectsSubstitutedReceiptAsync(provider, stream, disposition, token)
                                .ConfigureAwait(false);
                            Assert.AreEqual(CenteredViewKind.Single, Assert.ContainsSingle(await views
                                .ReadArchiveAsync(ids[3], token).ConfigureAwait(false)).Base.Definition.Kind);
                            continue;
                        }
                    }
                    await windows.PublishReadyAsync(token).ConfigureAwait(false);
                    var candidate = await views.ReadLiveCandidateAsync(stream, token).ConfigureAwait(false);
                    Assert.IsNotNull(candidate);
                    var live = await views.SelectLiveAsync(stream, candidate, token).ConfigureAwait(false);
                    Assert.IsNotNull(live);
                    var expectedReferenceIndex = index switch { < 3 => index, 3 or 4 => 2, _ => 3 };
                    Assert.AreEqual(ids[expectedReferenceIndex], live.View.Base.Definition.Reference.Capture.CaptureId,
                        $"Wrong reference after committed frame {index + 101}.");
                    Assert.AreEqual(index >= 4 && expectStack ? CenteredViewKind.Stack : CenteredViewKind.Single,
                        live.View.Base.Definition.Kind,
                        $"Wrong kind after committed frame {index + 101}.");
                    if (!expectStack && index is 0 or 3 or 5)
                    {
                        var context = await provider.GetRequiredService<ICenteredViewLookup>().ReadLiveContextAsync(token).ConfigureAwait(false);
                        Assert.IsNotNull(context);
                        Assert.AreEqual(stream, context.Stream);
                        Assert.AreEqual(ids[index], context.AdmissionHead?.CaptureId);
                        var expectedState = index switch
                        {
                            0 => CenteredViewLiveState.Warming,
                            3 => CenteredViewLiveState.Building,
                            _ => CenteredViewLiveState.Ready
                        };
                        TestContext.WriteLine($"admission={index + 101} selected={live.View.Base.Definition.Reference.Capture.CaptureSequence} " +
                            $"receipt={disposition?.Publication?.StackDisposition} state={context.State}/{context.Reason}");
                        Assert.AreEqual(expectedState, context.State);
                        Assert.AreEqual(index == 3 ? "centered.awaiting-window-publication" : null, context.Reason);
                    }
                    Assert.IsTrue(live.View.Revision.Layers.Any(item =>
                        item.Key == new CenteredViewLayerKey("measured-source-markers", CenteredViewLayerScope.Reference, ids[expectedReferenceIndex])
                        && item.Drawable is not null), "The live revision must include its exact reference's measured markers.");
                    if (disposition?.Publication is { } terminal)
                    {
                        var definition = live.View.Base.Definition;
                        CollectionAssert.AreEqual(terminal.ContributingCaptureIds.ToArray(),
                            definition.Contributors.Select(contributor => contributor.Exposure.Capture.CaptureId).ToArray());
                        if (expectStack)
                        {
                            Assert.AreEqual(terminal.EvidenceArtifactId, definition.Science.ArtifactId);
                            Assert.AreEqual(terminal.ClosureIdentity, definition.Window?.ClosureIdentitySha256);
                            Assert.AreEqual(terminal.PlanSha256, definition.Window?.RegistrationPlanIdentitySha256);
                            Assert.IsNotNull(definition.ClosedWindow);
                            Assert.AreNotEqual(definition.Science.ArtifactId, definition.ClosedWindow.ArtifactId);
                            if (detectLateTrail)
                                await AssertCenteredTrailWindowAsync(persistence, definition, ids[4], expectTrailMembership, token)
                                    .ConfigureAwait(false);
                            else
                                Assert.HasCount(5, definition.Contributors);
                        }
                    }
                    if (index == 4)
                        issued103 = live;
                    if (index == 5)
                        issued104 = live;
                }
                if (upgradePreviousPublisher)
                {
                    Assert.IsNotNull(issued103);
                    var visibleLayers = issued103.View.Revision.Manifest.Layers;
                    Assert.IsNotEmpty(visibleLayers);
                    var layer = visibleLayers[0];
                    saved = await views.SaveAsync("publication-test", Guid.NewGuid(), issued103.View.Base.BaseId,
                        issued103.View.Revision.RevisionId, [layer.LayerIdentitySha256], token).ConfigureAwait(false);
                    Assert.IsNotNull(saved);
                }
            }

            if (upgradePreviousPublisher)
            {
                using var prior = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
                await prior.OpenAsync(token).ConfigureAwait(false);
                using var command = prior.CreateCommand();
                // Retain real windows and views under all three pre-index schema-12 layouts.
                command.CommandText = SqliteCaptureProcessingSchemaTests.DropCenteredRegistrationStateSql +
                    "\nDROP INDEX ix_processing_outputs_astrometry_lookup;\n" +
                    (hasTrustedCutoff ? string.Empty : format == CameraPixelFormat.Mono16
                        ? "DROP TABLE processing_centered_output_availability;"
                        : "DROP INDEX ix_processing_centered_output_availability_cutoff;\n" +
                          SqliteCaptureProcessingStore.LegacyCenteredAvailabilityCutoffIndexForTests);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            using var reopened = CenteredRuntimeProvider(root, clock, detectTransients: detectLateTrail);
            if (detectLateTrail)
                profile = await PersistCenteredTrailLocationAsync(reopened, profile, token).ConfigureAwait(false);
            await StoragePolicyRawIngressIntegrationTests.InitializeAsync(reopened, profile).ConfigureAwait(false);
            var archive = await reopened.GetRequiredService<ICenteredViewLookup>().ReadArchiveAsync(ids[2], token).ConfigureAwait(false);
            Assert.HasCount(expectStack ? 2 : 1, archive);
            Assert.IsNotNull(issued103);
            var archived = archive.Single(item => item.Base.BaseId == issued103.View.Base.BaseId);
            Assert.AreEqual(issued103.View.Revision.RevisionId, archived.DefaultRevisionId);
            await AssertCenteredArchiveReceiptAsync(reopened, archived, expectedDisposition, token).ConfigureAwait(false);
            if (saved is not null)
            {
                var selection = await reopened.GetRequiredService<ICenteredViewReadIndex>()
                    .ReadSavedAsync("publication-test", saved.SelectionId, token).ConfigureAwait(false);
                Assert.IsNotNull(selection);
                Assert.AreEqual(saved.BaseId, selection.BaseId);
                Assert.AreEqual(saved.RevisionId, selection.RevisionId);
                CollectionAssert.AreEqual(saved.EnabledLayerIds.ToArray(), selection.EnabledLayerIds.ToArray());
            }
            if (detectLateTrail)
            {
                Assert.IsNotNull(issued104);
                await AssertCenteredLateTrailAsync(reopened, ids[4], issued103, issued104, expectTrailMembership, token)
                    .ConfigureAwait(false);
            }
            if (recoverUnpublishedStack)
            {
                Assert.IsNotNull(unpublished?.Publication);
                var windowPublisher = reopened.GetRequiredService<CenteredViewWindowPublicationWorker>();
                await windowPublisher.PublishReadyAsync(token).ConfigureAwait(false);
                await windowPublisher.PublishReadyAsync(token).ConfigureAwait(false);
                var read = reopened.GetRequiredService<ICenteredViewLookup>();
                var recovered = Assert.ContainsSingle((await read.ReadArchiveAsync(ids[3], token).ConfigureAwait(false))
                    .Where(item => item.Base.Definition.Kind == CenteredViewKind.Stack));
                Assert.IsNull(recovered.LastLiveRevisionId);
                Assert.AreEqual(unpublished.Publication.EvidenceArtifactId, recovered.Base.Definition.Science.ArtifactId);
                Assert.AreEqual(unpublished.Publication.PlanSha256, recovered.Base.Definition.Window?.RegistrationPlanIdentitySha256);
                await AssertCenteredArchiveReceiptAsync(reopened, recovered, expectedDisposition, token).ConfigureAwait(false);
                Assert.AreEqual(issued103.Interval, (await reopened.GetRequiredService<SqliteCenteredViews>()
                    .ReadLastLiveAsync(issued103.View.Base.Definition.Stream.Camera, token).ConfigureAwait(false))?.Interval);
            }
            var endpoint = reopened.GetRequiredService<ICenteredViewPresentationReader>();
            var opened = await endpoint.OpenPreviewAsync(archived.Base.BaseId, archived.DefaultRevisionId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
            Assert.IsNotNull(opened.Content);
            await using var lease = opened.Content.ConfigureAwait(false);
            var payload = new byte[checked((int)opened.Content.ByteLength)];
            await opened.Content.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            Assert.AreEqual(archived.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(payload));
            var decoded = JpegImageCodec.DecodeJpeg(payload, token);
            Assert.AreEqual(archived.Base.Definition.Preview.Transform.PreviewWidth, decoded.Width);
            Assert.AreEqual(archived.Base.Definition.Preview.Transform.PreviewHeight, decoded.Height);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task AssertCenteredTrailWindowAsync(CaptureProcessingPersistence persistence,
        CenteredViewBaseDefinition definition, Guid trailCaptureId, bool expectMembership, CancellationToken token)
    {
        var reference = definition.Reference.Capture.CaptureId;
        var retained = await persistence.ReadCenteredViewRetainedProductAsync(reference, definition.Science, token).ConfigureAwait(false);
        var evidence = CenteredRegisteredStackEvidence.Read(retained);
        var support = evidence.Outputs.Single(output => output.Channel == "support");
        var fill = definition.Preview.Recipe.Options.GetProperty("unsupportedPixels");
        Assert.AreEqual("qualified-reference-display-v1", fill.GetProperty("algorithm").GetString());
        Assert.AreEqual(2, fill.GetProperty("minimumContributors").GetInt32());
        Assert.AreEqual(definition.Reference.Linear!.ArtifactId, fill.GetProperty("referenceArtifactId").GetGuid());
        Assert.AreEqual(support.ArtifactId, fill.GetProperty("supportArtifactId").GetGuid());
        var plane = await persistence.ReadCenteredViewRetainedProductAsync(reference,
            new(support.ArtifactId, support.OutputIdentitySha256, support.PayloadSha256), token).ConfigureAwait(false);
        var mask = plane.Payload.ToArray();
        TestContext.WriteLine($"Registered reference {reference} at sequence {definition.ReferenceOrder}, " +
            $"plan {evidence.PlanSha256}, support {support.ArtifactId}/{support.PayloadSha256}.");
        for (var index = 0; index < evidence.Sources.Count; index++)
        {
            var source = evidence.Sources[index];
            var slot = evidence.Plan.Slots[index];
            var bit = 1 << index;
            var used = mask.Count(value => (value & bit) != 0);
            Assert.AreEqual(source.Reason == CenteredRegistrationReason.Contributed, used > 0);
            TestContext.WriteLine($"Offset {slot.Offset}, source {source.CaptureId}, plan state {slot.State}, " +
                $"reason {source.Reason}, map {source.Similarity}, measurement {source.MeasurementStatus}/{source.MeasurementReason}, " +
                $"verification {source.PairCheck?.Status}, support cells {used}, " +
                $"input mask {slot.Exposure?.ValidityMask.PayloadSha256}.");
        }
        var trail = evidence.Sources.Single(source => source.CaptureId == trailCaptureId);
        Assert.AreEqual(expectMembership ? CenteredRegistrationReason.Contributed : CenteredRegistrationReason.RegistrationRejected,
            trail.Reason);
        if (!expectMembership)
            Assert.AreEqual(DirectRegistrationStatus.NoDirectMatch, trail.PairCheck?.Status);
    }

    private static async Task AssertPublisherRejectsSubstitutedReceiptAsync(ServiceProvider provider, CenteredViewStream stream,
        CenteredWindowDisposition disposition, CancellationToken token)
    {
        var receipt = disposition.Publication!;
        var identity = disposition.Closure.Schedule.Identity;
        var producer = provider.GetRequiredService<ICenteredViewProducer>();
        var products = provider.GetRequiredService<IExposureProductReader>();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            producer.PublishRegisteredStackAsync(stream, identity, Guid.NewGuid(), products, new(), token).AsTask())
            .ConfigureAwait(false);

        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        using var connection = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT publication_json FROM processing_centered_windows WHERE window_sha256 = $window;";
        command.Parameters.AddWithValue("$window", identity);
        var original = (byte[])(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        command.CommandText = "UPDATE processing_centered_windows SET publication_json = $receipt WHERE window_sha256 = $window;";
        var parameter = command.Parameters.AddWithValue("$receipt",
            JsonSerializer.SerializeToUtf8Bytes(receipt with { PlanSha256 = new string('0', 64) }, CenteredPublicationJson));
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                producer.PublishRegisteredStackAsync(stream, identity, receipt.EvidenceArtifactId, products, new(), token).AsTask())
                .ConfigureAwait(false);

            Assert.IsTrue(receipt.ContributingCaptureIds.Length > 1);
            var reordered = receipt with { ContributingCaptureIds = [.. receipt.ContributingCaptureIds.Reverse()] };
            parameter.Value = JsonSerializer.SerializeToUtf8Bytes(reordered, CenteredPublicationJson);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
            var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                producer.PublishRegisteredStackAsync(stream, identity, receipt.EvidenceArtifactId, products, new(), token).AsTask())
                .ConfigureAwait(false);
            Assert.AreEqual("The registered stack does not match the ordered, published contributors.", exception.Message);
            var archive = await provider.GetRequiredService<ICenteredViewReadIndex>()
                .ReadArchiveAsync(stream, receipt.ReferenceCaptureId, token).ConfigureAwait(false);
            Assert.AreEqual(CenteredViewKind.Single, Assert.ContainsSingle(archive).Base.Definition.Kind);
        }
        finally
        {
            parameter.Value = original;
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false));
        }
    }

    private static async Task AssertCenteredArchiveReceiptAsync(ServiceProvider provider, CenteredViewArchiveBase archive,
        CenteredStackDisposition expectedDisposition, CancellationToken token)
    {
        var definition = archive.Base.Definition;
        var reference = definition.Reference.Capture.CaptureId;
        var disposition = await provider.GetRequiredService<SqliteCaptureProcessingStore>()
            .ReadCenteredWindowByReferenceAsync(reference, token).ConfigureAwait(false);
        Assert.IsNotNull(disposition?.Publication);
        var receipt = disposition.Publication;
        Assert.AreEqual(expectedDisposition, receipt.StackDisposition);
        var revision = await provider.GetRequiredService<ICenteredViewReadIndex>()
            .ReadRevisionAsync(archive.Base.BaseId, archive.DefaultRevisionId, token).ConfigureAwait(false);
        Assert.IsNotNull(revision);
        Assert.AreEqual(definition.Preview.Product, revision.Revision.Manifest.BaseProduct);
        if (expectedDisposition == CenteredStackDisposition.SingleExposure)
        {
            Assert.AreEqual(reference, Assert.ContainsSingle(receipt.ContributingCaptureIds));
            Assert.IsNull(definition.ClosedWindow);
            return;
        }

        Assert.IsNotNull(definition.Window);
        Assert.IsNotNull(definition.ClosedWindow);
        Assert.AreEqual(receipt.ClosureIdentity, definition.Window.ClosureIdentitySha256);
        Assert.AreEqual(receipt.PlanSha256, definition.Window.RegistrationPlanIdentitySha256);
        Assert.AreEqual(disposition.Closure.EvidenceCutoffUtc, definition.Window.EvidenceCutoffUtc);
        Assert.AreEqual(receipt.EvidenceArtifactId, definition.Science.ArtifactId);
        CollectionAssert.AreEqual(receipt.ContributingCaptureIds.ToArray(),
            definition.Contributors.Select(item => item.Exposure.Capture.CaptureId).ToArray());
        var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
        var retainedClosure = await persistence.ReadCenteredViewRetainedProductAsync(reference, definition.ClosedWindow, token)
            .ConfigureAwait(false);
        Assert.AreEqual(receipt.ClosureIdentity, retainedClosure.ContentIdentitySha256);
        using var document = JsonDocument.Parse(retainedClosure.Payload);
        Assert.IsTrue(JsonElement.DeepEquals(document.RootElement.GetProperty("closure"),
            JsonSerializer.SerializeToElement(disposition.Closure, CenteredPublicationJson)));
        Assert.IsTrue(JsonElement.DeepEquals(document.RootElement.GetProperty("receipt"),
            JsonSerializer.SerializeToElement(receipt, CenteredPublicationJson)));
    }

    private static CameraModuleConfig AddCenteredTrailScenario(CameraModuleConfig profile, DateTimeOffset start)
    {
        var options = JsonNode.Parse(profile.Module.Options!.Value.GetRawText())!.AsObject();
        var projector = ProjectorFactory.Create(RigProjectionContextFactory.CreateNative(profile.Rig));
        options["transientScenario"] = JsonSerializer.SerializeToNode(new VirtualTransientScenarioDefinition
        {
            ScenarioId = "centered-late-trail",
            ScenarioVersion = "1",
            Seed = 1922,
            EpochUtc = start,
            TemporalSampleCount = 32,
            SkyTracks =
            [
                new()
                {
                    PrimitiveId = "exposure-105",
                    Keyframes = [SkyPoint(8, 64), SkyPoint(8.25, 108)]
                }
            ]
        }, CenteredPublicationJson);
        return profile with { Module = profile.Module with { Options = JsonSerializer.SerializeToElement(options) } };

        VirtualTransientSkyKeyframe SkyPoint(double offset, double x)
        {
            var direction = projector.Unproject(new(x, 112));
            Assert.IsNotNull(direction);
            return new()
            {
                OffsetSeconds = offset,
                AltitudeDegrees = direction.Value.AltitudeDegrees,
                AzimuthDegrees = direction.Value.AzimuthDegrees,
                Magnitude = -4,
                AngularWidthDegrees = 0.2
            };
        }
    }

    private static async Task StageCenteredTransientFrameAsync(ServiceProvider provider, CameraModuleConfig config, CancellationToken token)
    {
        var store = provider.GetRequiredService<ICaptureLaneStore>();
        var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(definition => definition.Name == "transient");
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(value => value.Lane == lane.Name);
        var lease = await store.ClaimAsync(lane, "centered-trail-test", config, token).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await store.CompleteAsync(lease, token).ConfigureAwait(false);
    }

    private static async Task<CameraModuleConfig> PersistCenteredTrailLocationAsync(
        ServiceProvider provider, CameraModuleConfig profile, CancellationToken token)
    {
        var location = profile.DeploymentLocation!;
        var retained = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(
            new(location.LocationId, location.Source, location.HorizontalAccuracyMeters, location.EffectiveFromUtc,
                location.EffectiveUntilUtc, profile.Observatory), token).ConfigureAwait(false);
        return profile with { DeploymentLocation = retained };
    }

    private static async Task AssertCenteredLateTrailAsync(ServiceProvider provider, Guid sourceCaptureId,
        CenteredViewCurrent issued103, CenteredViewCurrent issued104, bool expectMembership, CancellationToken token)
    {
        var worker = provider.GetRequiredService<TransientWorkerService>();
        for (var index = 0; index < 6; index++)
            Assert.IsTrue(await worker.ProcessFrameAsync(token).ConfigureAwait(false), $"Transient capture {index + 101} was not staged.");
        var source = await provider.GetRequiredService<ICameraAgentTransientOperatorProjection>()
            .GetCaptureTrailsAsync(sourceCaptureId, token).ConfigureAwait(false);
        Assert.AreEqual("MeasuredEvidence", source.State, source.Reason);
        var trail = Assert.ContainsSingle(source.Trails);

        var publication = provider.GetRequiredService<CenteredViewTrailPublication>();
        var views = provider.GetRequiredService<SqliteCenteredViews>();
        await publication.PublishReadyAsync(token).ConfigureAwait(false);
        var presentation = provider.GetRequiredService<ICenteredViewPresentationReader>();
        foreach (var issued in new[] { issued103, issued104 })
        {
            var definition = issued.View.Base.Definition;
            var member = definition.Contributors.SingleOrDefault(item => item.Exposure.Capture.CaptureId == sourceCaptureId);
            Assert.AreEqual(expectMembership, member is not null);
            var archive = (await views.ReadArchiveAsync(definition.Reference.Capture.CaptureId, token).ConfigureAwait(false))
                .Single(item => item.Base.BaseId == issued.View.Base.BaseId);
            Assert.AreEqual(issued.View.Revision.RevisionId, archive.DefaultRevisionId);
            var latest = await views.ReadRevisionAsync(archive.Base.BaseId, archive.LatestRevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(latest);
            var layer = latest.Revision.Layers.SingleOrDefault(item =>
                item.Key.Kind == TransientTrailPresentation.LayerKind && item.Key.SourceCaptureId == sourceCaptureId);
            if (member is null)
            {
                Assert.IsNull(layer, "The measured 105 trail must not appear in a stack that rejected 105.");
                continue;
            }
            Assert.AreEqual(trail.DetectorInput.Source.ObservationStartedUtc, member.Exposure.Input.Raw.Timing.ExposureStartedUtc);
            Assert.AreEqual(trail.DetectorInput.Source.ObservationEndedUtc, member.Exposure.Input.Raw.Timing.ExposureEndedUtc);
            Assert.AreNotEqual(archive.DefaultRevisionId, archive.LatestRevisionId);
            Assert.IsNotNull(layer);
            Assert.AreEqual(CenteredViewLayerScope.Contributor, layer.Key.Scope);
            Assert.IsNotNull(layer.Drawable);
            Assert.AreEqual(member.Exposure.ExposureStartedUtc,
                layer.Drawable.Options.GetProperty("ExposureStartedUtc").GetDateTimeOffset());
            Assert.AreEqual(member.Exposure.ExposureEndedUtc,
                layer.Drawable.Options.GetProperty("ExposureEndedUtc").GetDateTimeOffset());
            var original = await views.ReadRevisionAsync(archive.Base.BaseId, archive.DefaultRevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(original);
            var waiting = original.Revision.Layers.Single(item => item.Key == layer.Key);
            Assert.AreEqual(CenteredViewReadiness.Waiting, waiting.Readiness);
            Assert.IsNull(waiting.Drawable);
            Assert.AreEqual(original.Revision.Manifest.BaseProduct, latest.Revision.Manifest.BaseProduct);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found,
                (await presentation.ReadAsync(archive.Base.BaseId, archive.LatestRevisionId, token).ConfigureAwait(false)).Status);
        }
        await publication.PublishReadyAsync(token).ConfigureAwait(false);
        Assert.IsEmpty(await views.ReadDueTrailWorkAsync(16, token).ConfigureAwait(false));
        Assert.AreEqual(issued104.Interval,
            (await views.ReadLastLiveAsync(issued104.View.Base.Definition.Stream.Camera, token).ConfigureAwait(false))?.Interval);
    }

    [TestMethod]
    public async Task CenteredWindowPublicationTerminatesImmediatelyWhenOversizedRawPreventsCenterSingle()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-window-oversized-raw");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRawOnlyProfile(root, start);
        try
        {
            string blockedWindowId;
            Guid blockedCaptureId;
            // Set MaximumPreviewSourceBytes below the 224x224x2 (100,352-byte) raw frame while linear never qualifies.
            using (var provider = CenteredRuntimeProvider(root, clock, maximumPreviewSourceBytes: 1024))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var schedule = await ReadCenteredPublicationScheduleAsync(provider, token).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var device = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = device.ConfigureAwait(false);
                await device.InitializeAsync(active, token).ConfigureAwait(false);
                var singles = provider.GetRequiredService<CenteredViewPublicationWorker>();
                var windows = provider.GetRequiredService<CenteredViewWindowPublicationWorker>();
                var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var views = provider.GetRequiredService<SqliteCenteredViews>();

                Guid reference4 = default;
                CameraKey key = default!;
                CenteredViewStream? stream = null;
                for (var ordinal = 1; ordinal <= 4; ordinal++)
                {
                    var captured = await CaptureCenteredRuntimeAttemptAsync(ingress, device, deviceClock, clock, active,
                        start.AddSeconds((ordinal - 1) * 2), 1, token, schedule: schedule).ConfigureAwait(false);
                    var receipt = await ingress.AcceptCenteredAsync(active, captured.Submission, captured.Reservation, token)
                        .ConfigureAwait(false);
                    key = captured.Reservation.Pending.Key.Camera;
                    stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(key, token).ConfigureAwait(false));
                    Assert.IsNotNull(stream);
                    reference4 = receipt.Manifest.Descriptor.Capture.CaptureId;
                    await singles.DrainCameraAsync(key, stream, CancellationToken.None, token).ConfigureAwait(false);
                }
                blockedCaptureId = reference4;
                Assert.IsEmpty(await views.ReadArchiveAsync(blockedCaptureId, token).ConfigureAwait(false),
                    "Non-qualifying linear plus oversized raw must leave no Single base.");

                var window = await provider.GetRequiredService<ICenteredWindowReader>().ReadAsync(blockedCaptureId, token)
                    .ConfigureAwait(false);
                Assert.IsNotNull(window);
                blockedWindowId = window.WindowIdentity;
                clock.UtcNow = window.DeadlineUtc.AddMilliseconds(1);
                await coordinator.CloseDueAsync(token).ConfigureAwait(false);
                CollectionAssert.Contains((await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                    blockedWindowId);

                await windows.PublishReadyAsync(token).ConfigureAwait(false);
                CollectionAssert.DoesNotContain((await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                    blockedWindowId);

                clock.UtcNow = clock.UtcNow.AddMinutes(5);
                CollectionAssert.DoesNotContain((await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                    blockedWindowId);

                using var connection = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT live_base_id, retry_utc, terminal_reason, retry_count
                    FROM processing_centered_view_window_work WHERE window_sha256 = $window;
                    """;
                command.Parameters.AddWithValue("$window", blockedWindowId);
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
                Assert.IsTrue(await reader.IsDBNullAsync(0, token).ConfigureAwait(false));
                Assert.IsTrue(await reader.IsDBNullAsync(1, token).ConfigureAwait(false));
                Assert.AreEqual(CenteredViewWindowPublicationWorker.MissingCenterSingleTerminalReason, reader.GetString(2));
                Assert.AreEqual(0L, reader.GetInt64(3));
            }

            // Reopen with normal preview byte limits and verify the terminalized window stays out of the queue.
            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var reopenedViews = reopened.GetRequiredService<SqliteCenteredViews>();
            CollectionAssert.DoesNotContain((await reopenedViews.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                blockedWindowId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CenteredWindowPublicationRetriesDelayedSingleThenBoundsUnresolvedCenterRetriesAcrossRestart()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-window-retry-bound");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRawOnlyProfile(root, start);
        try
        {
            string delayedWindowId;
            Guid delayedCaptureId;
            string stuckWindowId;
            using (var provider = CenteredRuntimeProvider(root, clock))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var schedule = await ReadCenteredPublicationScheduleAsync(provider, token).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var device = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = device.ConfigureAwait(false);
                await device.InitializeAsync(active, token).ConfigureAwait(false);
                var singles = provider.GetRequiredService<CenteredViewPublicationWorker>();
                var windows = provider.GetRequiredService<CenteredViewWindowPublicationWorker>();
                var coordinator = provider.GetRequiredService<CenteredWindowCoordinator>();
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var views = provider.GetRequiredService<SqliteCenteredViews>();

                CameraKey key = default!;
                CenteredViewStream? stream = null;
                Guid capture4 = default;
                Guid capture5 = default;
                for (var ordinal = 1; ordinal <= 5; ordinal++)
                {
                    var captured = await CaptureCenteredRuntimeAttemptAsync(ingress, device, deviceClock, clock, active,
                        start.AddSeconds((ordinal - 1) * 2), 1, token, schedule: schedule).ConfigureAwait(false);
                    var receipt = await ingress.AcceptCenteredAsync(active, captured.Submission, captured.Reservation, token)
                        .ConfigureAwait(false);
                    key = captured.Reservation.Pending.Key.Camera;
                    stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(key, token).ConfigureAwait(false));
                    Assert.IsNotNull(stream);
                    if (ordinal == 4)
                        capture4 = receipt.Manifest.Descriptor.Capture.CaptureId;
                    else if (ordinal == 5)
                        capture5 = receipt.Manifest.Descriptor.Capture.CaptureId;
                    if (ordinal <= 3)
                        await singles.DrainCameraAsync(key, stream, CancellationToken.None, token).ConfigureAwait(false);
                }
                delayedCaptureId = capture4;

                // Close window 4 BEFORE its center single publishes (normal delayed publication).
                var reader = provider.GetRequiredService<ICenteredWindowReader>();
                var window4 = await reader.ReadAsync(delayedCaptureId, token).ConfigureAwait(false);
                var window5 = await reader.ReadAsync(capture5, token).ConfigureAwait(false);
                Assert.IsNotNull(window4);
                Assert.IsNotNull(window5);
                delayedWindowId = window4.WindowIdentity;
                stuckWindowId = window5.WindowIdentity;
                clock.UtcNow = window4.DeadlineUtc.AddMilliseconds(1);
                await coordinator.CloseDueAsync(token).ConfigureAwait(false);

                await windows.PublishReadyAsync(token).ConfigureAwait(false);
                Assert.IsEmpty(await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false),
                    "Window 4 should wait for its retry delay before polling again.");

                // Publish only the delayed center single for window 4 (leaving window 5's center single unpublished),
                // then advance past the retry delay and verify window 4 publishes normally.
                var products = provider.GetRequiredService<IExposureProductReader>();
                var producer = provider.GetRequiredService<ICenteredViewProducer>();
                var linear4 = await products.ReadLinearAsync(new ExposureKey(key, stream!.RunId, delayedCaptureId), token)
                    .ConfigureAwait(false);
                Assert.IsNotNull(linear4);
                var execution4 = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(delayedCaptureId, stream.GenerationId);
                var delayedSingleView = await producer.PublishSingleAsync(stream, execution4,
                    linear4.Exposure.Raw.Capture.CaptureSequence, linear4, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(delayedSingleView);
                var delayedSingle = delayedSingleView.Base;

                clock.UtcNow = clock.UtcNow.Add(CenteredViewWindowPublicationWorker.RetryDelay);
                await windows.PublishReadyAsync(token).ConfigureAwait(false);

                using (var connection = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT live_base_id, retry_utc, terminal_reason, retry_count
                        FROM processing_centered_view_window_work WHERE window_sha256 = $window;
                        """;
                    cmd.Parameters.AddWithValue("$window", delayedWindowId);
                    using var workReader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    Assert.IsTrue(await workReader.ReadAsync(token).ConfigureAwait(false));
                    Assert.AreEqual(delayedSingle.BaseId, workReader.GetString(0));
                    Assert.IsTrue(await workReader.IsDBNullAsync(1, token).ConfigureAwait(false));
                    Assert.IsTrue(await workReader.IsDBNullAsync(2, token).ConfigureAwait(false));
                    Assert.AreEqual(1L, workReader.GetInt64(3));
                }

                // Close window 5 while its center single remains unpublished, then retry across a restart.
                if (clock.UtcNow <= window5.DeadlineUtc)
                    clock.UtcNow = window5.DeadlineUtc.AddMilliseconds(1);
                await coordinator.CloseDueAsync(token).ConfigureAwait(false);

                const int attemptsBeforeRestart = 3;
                for (var attempt = 1; attempt <= attemptsBeforeRestart; attempt++)
                {
                    CollectionAssert.Contains((await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                        stuckWindowId);
                    await windows.PublishReadyAsync(token).ConfigureAwait(false);
                    clock.UtcNow = clock.UtcNow.Add(CenteredViewWindowPublicationWorker.RetryDelay);
                }
            }

            // Reopen after restart and confirm durable retry_count finishes bounding retries instead of resetting.
            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var reopenedViews = reopened.GetRequiredService<SqliteCenteredViews>();
            var reopenedWindows = reopened.GetRequiredService<CenteredViewWindowPublicationWorker>();
            var reopenedStore = reopened.GetRequiredService<SqliteCaptureProcessingStore>();

            for (var attempt = 4; attempt <= CenteredViewWindowPublicationWorker.MaximumMissingCenterRetries; attempt++)
            {
                CollectionAssert.Contains((await reopenedViews.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                    stuckWindowId);
                await reopenedWindows.PublishReadyAsync(token).ConfigureAwait(false);
                clock.UtcNow = clock.UtcNow.Add(CenteredViewWindowPublicationWorker.RetryDelay);
            }

            // Once the bound is exhausted, the window is permanently terminal and never re-queued.
            CollectionAssert.DoesNotContain((await reopenedViews.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                stuckWindowId);
            clock.UtcNow = clock.UtcNow.AddHours(1);
            CollectionAssert.DoesNotContain((await reopenedViews.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
                stuckWindowId);

            using (var connection = await reopenedStore.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT live_base_id, retry_utc, terminal_reason, retry_count
                    FROM processing_centered_view_window_work WHERE window_sha256 = $window;
                    """;
                cmd.Parameters.AddWithValue("$window", stuckWindowId);
                using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
                Assert.IsTrue(await reader.IsDBNullAsync(0, token).ConfigureAwait(false));
                Assert.IsTrue(await reader.IsDBNullAsync(1, token).ConfigureAwait(false));
                Assert.AreEqual(CenteredViewWindowPublicationWorker.MissingCenterSingleTerminalReason, reader.GetString(2));
                Assert.AreEqual((long)CenteredViewWindowPublicationWorker.MaximumMissingCenterRetries, reader.GetInt64(3));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
