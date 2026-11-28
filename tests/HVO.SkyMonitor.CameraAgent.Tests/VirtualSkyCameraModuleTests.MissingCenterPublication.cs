using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(2, false, false, false, DisplayName = "FrozenMissingCenterArchivesLateStartupSingleWithoutIssuingIt")]
    [DataRow(4, false, false, false, DisplayName = "FrozenMissingCenterArchivesLateSteadySingleWithoutIssuingIt")]
    [DataRow(2, true, false, false, DisplayName = "FrozenMissingCenterRejectsPostCutoffPreFreezeStartupAdmissionWithoutRetry")]
    [DataRow(4, true, false, false, DisplayName = "FrozenMissingCenterRejectsPostCutoffPreFreezeAdmissionWithoutRetry")]
    [DataRow(2, true, true, false, DisplayName = "FrozenMissingCenterRejectsStartupSingleDrainedBeforeFreeze")]
    [DataRow(2, true, true, true, DisplayName = "FrozenMissingCenterAllowsOnTimeStartupSingleRenderedAfterCutoff")]
    public async Task CenteredViewKeepsLateMissingCenterArchiveOnly(int lateOrdinal, bool admitBeforeFreeze,
        bool drainBeforeFreeze, bool onTime)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-late-center-view");
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
            var device = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = device.ConfigureAwait(false);
            await device.InitializeAsync(active, token).ConfigureAwait(false);

            var windows = provider.GetRequiredService<CenteredViewWindowPublicationWorker>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();

            Guid normalCapture = default;
            for (var ordinal = 1; ordinal < lateOrdinal; ordinal++)
            {
                var normal = await CaptureCenteredRuntimeAttemptAsync(ingress, device, deviceClock, clock, active,
                    start.AddSeconds((ordinal - 1) * 2), 1, token, schedule: schedule).ConfigureAwait(false);
                var receipt = await ingress.AcceptCenteredAsync(active, normal.Submission, normal.Reservation, token).ConfigureAwait(false);
                normalCapture = receipt.Manifest.Descriptor.Capture.CaptureId;
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
            }

            var late = await CaptureCenteredRuntimeAttemptAsync(ingress, device, deviceClock, clock, active,
                start.AddSeconds((lateOrdinal - 1) * 2), 1, token, schedule: schedule).ConfigureAwait(false);
            var attempt = late.Reservation.Pending.Key;
            Assert.AreEqual(lateOrdinal, attempt.Ordinal);
            var stream = CenteredViewLookup.ResolveStream(await ingress.ReadCurrentAsync(attempt.Camera, token).ConfigureAwait(false));
            Assert.IsNotNull(stream);
            await DrainCenteredCameraAsync(provider, attempt.Camera, stream, token).ConfigureAwait(false);
            var issued = await views.ReadLastLiveAsync(attempt.Camera, token).ConfigureAwait(false);
            Assert.IsNotNull(issued, "A ready, on-time startup single must remain eligible.");
            Assert.AreEqual(normalCapture, issued.View.Base.Definition.Reference.Capture.CaptureId);
            Assert.AreEqual(CenteredViewKind.Single, issued.View.Base.Definition.Kind);
            Assert.AreEqual(CenteredViewImageSource.QualifiedLinear, issued.View.Base.Definition.Preview.Source);

            using var connection = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT window.window_sha256, window.deadline_utc_ticks
                FROM processing_centered_attempt_calendar calendar
                JOIN processing_centered_windows window ON window.window_sha256 = calendar.window_sha256
                WHERE calendar.attempt_id = $attempt;
                """;
            command.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
            string windowId;
            DateTimeOffset deadline;
            using (var read = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                Assert.IsTrue(await read.ReadAsync(token).ConfigureAwait(false));
                windowId = read.GetString(0);
                deadline = new DateTimeOffset(read.GetInt64(1), TimeSpan.Zero);
            }
            clock.UtcNow = onTime ? deadline : deadline.AddTicks(1);
            // The existing pre-close binder can publish a reference ID even when admission
            // occurred after the deadline and the later freeze excludes that capture.
            var lateReceipt = admitBeforeFreeze
                ? await ingress.AcceptCenteredAsync(active, late.Submission, late.Reservation, token).ConfigureAwait(false)
                : null;
            string? beforeFreezeBase = null;
            string? beforeFreezeCandidate = null;
            if (drainBeforeFreeze)
            {
                Assert.IsNotNull(lateReceipt);
                command.CommandText = "SELECT admitted_ticks FROM processing_centered_attempt_calendar WHERE attempt_id = $attempt;";
                Assert.AreEqual(clock.UtcNow.UtcTicks, await command.ExecuteScalarAsync(token).ConfigureAwait(false));
                clock.UtcNow = deadline.AddTicks(1);
                Assert.AreEqual(issued.View.Base.BaseId, (await views.ReadLastLiveAsync(attempt.Camera, token)
                    .ConfigureAwait(false))?.View.Base.BaseId);
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
                await DrainCenteredCameraAsync(provider, attempt.Camera, stream, token).ConfigureAwait(false);
                Assert.IsNull(await store.ReadCenteredWindowDispositionAsync(windowId, token).ConfigureAwait(false));
                beforeFreezeBase = (await views.ReadLastLiveAsync(attempt.Camera, token).ConfigureAwait(false))?.View.Base.BaseId;
                beforeFreezeCandidate = await views.ReadLiveCandidateAsync(stream, token).ConfigureAwait(false);
            }

            await provider.GetRequiredService<CenteredWindowCoordinator>().CloseDueAsync(token).ConfigureAwait(false);
            var frozen = await store.ReadCenteredWindowDispositionAsync(windowId, token).ConfigureAwait(false);
            Assert.IsNotNull(frozen);
            Assert.IsTrue(frozen.IsTerminal);
            if (onTime)
            {
                Assert.IsNotNull(frozen.Closure.Center.Input);
                Assert.AreEqual(lateReceipt!.Manifest.Descriptor.Capture.CaptureId, frozen.Closure.Center.Input.Key.CaptureId);
                Assert.AreEqual(deadline, frozen.Closure.Center.Input.AdmittedUtc);
            }
            else
            {
                Assert.AreEqual(CenteredWindowSlotState.MissingExposure, frozen.Closure.Center.State);
                Assert.IsNull(frozen.Closure.Center.Input);
            }
            Assert.IsNull(frozen.Publication);
            Assert.AreEqual(deadline, frozen.Closure.EvidenceCutoffUtc);

            command.CommandText = "SELECT closure_json FROM processing_centered_windows WHERE window_sha256 = $window;";
            command.Parameters.AddWithValue("$window", windowId);
            var frozenBytes = (byte[])(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            lateReceipt ??= await ingress.AcceptCenteredAsync(active, late.Submission, late.Reservation, token).ConfigureAwait(false);
            var lateCapture = lateReceipt.Manifest.Descriptor.Capture.CaptureId;
            command.CommandText = "SELECT reference_capture_id FROM processing_centered_windows WHERE window_sha256 = $window;";
            var storedReference = (await command.ExecuteScalarAsync(token).ConfigureAwait(false)) as string;
            Assert.AreEqual(admitBeforeFreeze ? lateCapture.ToString("N") : null, storedReference);
            var lookup = AssertFrozenLookup(await provider.GetRequiredService<ICenteredWindowReader>()
                .ReadAsync(lateCapture, token).ConfigureAwait(false));
            var queued = (await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).Contains(windowId);
            Assert.AreEqual(admitBeforeFreeze, queued);

            if (!drainBeforeFreeze)
            {
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
                await DrainCenteredCameraAsync(provider, attempt.Camera, stream, token).ConfigureAwait(false);
            }
            await windows.PublishReadyAsync(token).ConfigureAwait(false);
            await SettleSiloViewChangesAsync(provider, token).ConfigureAwait(false);
            var archive = Assert.ContainsSingle(await views.ReadArchiveAsync(lateCapture, token).ConfigureAwait(false));
            Assert.AreEqual(CenteredViewKind.Single, archive.Base.Definition.Kind);
            Assert.AreEqual(CenteredViewImageSource.QualifiedLinear, archive.Base.Definition.Preview.Source);
            var live = await views.ReadLastLiveAsync(attempt.Camera, token).ConfigureAwait(false);

            clock.UtcNow = clock.UtcNow.AddSeconds(16);
            await windows.PublishReadyAsync(token).ConfigureAwait(false);
            await SettleSiloViewChangesAsync(provider, token).ConfigureAwait(false);
            CollectionAssert.DoesNotContain((await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(), windowId);
            command.CommandText = "SELECT closure_json FROM processing_centered_windows WHERE window_sha256 = $window;";
            CollectionAssert.AreEqual(frozenBytes, (byte[])(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);
            command.CommandText = """
                SELECT terminal_reason FROM processing_centered_view_window_work
                WHERE window_sha256 = $window AND live_base_id IS NULL AND retry_utc IS NULL;
                """;
            var terminalReason = (await command.ExecuteScalarAsync(token).ConfigureAwait(false)) as string;
            Assert.AreEqual(admitBeforeFreeze && !onTime ? "centered.window-evidence-rejected" : null, terminalReason);
            await connection.CloseAsync().ConfigureAwait(false);
            await provider.DisposeAsync().ConfigureAwait(false);

            using var reopened = CenteredRuntimeProvider(root, clock);
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var reopenedIndex = reopened.GetRequiredService<ICenteredViewReadIndex>();
            var reopenedArchive = Assert.ContainsSingle(await reopenedIndex.ReadArchiveAsync(stream, lateCapture, token)
                .ConfigureAwait(false));
            Assert.AreEqual(archive.Base.BaseId, reopenedArchive.Base.BaseId);
            var reopenedLive = await reopenedIndex.ReadLastLiveAsync(attempt.Camera, token).ConfigureAwait(false);
            var reopenedViews = reopened.GetRequiredService<SqliteCenteredViews>();
            var recoveredCandidate = await reopenedViews.ReadLiveCandidateAsync(stream, token).ConfigureAwait(false);
            CollectionAssert.DoesNotContain((await reopenedViews.ReadDueWindowPublicationsAsync(8, token)
                .ConfigureAwait(false)).ToArray(), windowId);
            var reopenedLookup = AssertFrozenLookup(await reopened.GetRequiredService<ICenteredWindowReader>()
                .ReadAsync(lateCapture, token).ConfigureAwait(false));
            CollectionAssert.AreEqual(lookup.Slots.ToArray(), reopenedLookup.Slots.ToArray());
            TestContext.WriteLine($"Late ordinal {lateOrdinal}: lookup={lookup.ClosureIdentity}, storedReference={storedReference ?? "absent"}, queued={queued}, " +
                $"terminal={terminalReason ?? "none"}, archive={archive.Base.BaseId}, priorLive={issued.View.Base.BaseId}, " +
                $"deadline={deadline:O}, cutoff={lookup.EvidenceCutoffUtc:O}, beforeFreeze={beforeFreezeBase}, " +
                $"beforeFreezeCandidate={beforeFreezeCandidate}, live={live?.View.Base.BaseId}, " +
                $"reopenedLive={reopenedLive?.View.Base.BaseId}, recoveredCandidate={recoveredCandidate}.");
            var expectedLive = onTime ? archive.Base.BaseId : issued.View.Base.BaseId;
            if (drainBeforeFreeze)
                Assert.AreEqual((expectedLive, expectedLive), (beforeFreezeBase, beforeFreezeCandidate));
            Assert.AreEqual((expectedLive, expectedLive, expectedLive),
                (live?.View.Base.BaseId, reopenedLive?.View.Base.BaseId, recoveredCandidate));
            Assert.AreEqual(onTime, reopenedArchive.LastLiveRevisionId is not null);

            CenteredWindowSnapshot AssertFrozenLookup(CenteredWindowSnapshot? result)
            {
                Assert.IsNotNull(result, "The admitted capture must resolve its original frozen window.");
                Assert.AreEqual(lateCapture, result.ReferenceCaptureId);
                Assert.AreEqual(attempt.Camera, result.Camera);
                Assert.AreEqual(attempt.RunId, result.RunId);
                Assert.AreEqual(windowId, result.WindowIdentity);
                Assert.AreEqual(frozen.Closure.Identity, result.ClosureIdentity);
                Assert.AreEqual(deadline, result.DeadlineUtc);
                Assert.AreEqual(deadline, result.EvidenceCutoffUtc);
                Assert.IsTrue(result.IsTerminal);
                Assert.AreEqual(frozen.FailureReason, result.FailureReason);
                Assert.HasCount(CenteredWindowScheduler.SlotCount, result.Slots);
                var center = result.Slots[CenteredWindowScheduler.CenterIndex];
                Assert.AreEqual(frozen.Closure.Center.State, center.State);
                Assert.AreEqual(frozen.Closure.Center.Input?.Key, center.Exposure);
                Assert.IsNull(result.EvidenceArtifactId);
                Assert.IsNull(result.PlanSha256);
                Assert.IsNull(result.StackDisposition);
                Assert.IsEmpty(result.ContributingCaptureIds);
                return result;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
