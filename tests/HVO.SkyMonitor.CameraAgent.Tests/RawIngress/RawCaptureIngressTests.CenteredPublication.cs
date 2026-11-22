using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CenteredViewDrainsRealLinearCommitAndReopensAcrossRunChange()
    {
        var root = CreateRoot();
        var token = TestContext.CancellationToken;
        var config = CenteredConfiguration("camera");
        var camera = new CameraKey(config.AgentId!, config.CenteredExposure!.CameraId);
        CenteredViewCurrent initial;
        long initialCursor;
        try
        {
            using (var first = new CenteredFixture(root))
            {
                var start = Timestamp(1);
                var submission = CenteredSubmission(start);
                submission = submission with
                {
                    CenteredAcquisition = new(CaptureCadenceMode.MinimumStartInterval, submission.EffectiveInterval,
                        start, submission.LoopDuration, null, false, null, null)
                };
                var reservation = await first.DispatchAsync(config).ConfigureAwait(false);
                var capture = await first.Ingress.AcceptCenteredAsync(config, submission, reservation, token).ConfigureAwait(false);
                var actual = await ProcessAndDrainAsync(first, config, camera, token).ConfigureAwait(false);
                initial = actual.Issued;
                initialCursor = actual.Cursor;
                Assert.AreEqual(capture.Manifest.Descriptor.Capture.CaptureId, initial.View.Base.Definition.Reference.Capture.CaptureId);
                Assert.AreEqual(CenteredViewImageSource.QualifiedLinear, initial.View.Base.Definition.Preview.Source);
                var acquisition = capture.Manifest.Descriptor.CenteredAcquisition;
                Assert.IsNotNull(acquisition);
                Assert.AreEqual(start, acquisition.ModuleCallStartedUtc);
                Assert.AreEqual(submission.Result.NextSetpoint, acquisition.Module?.ReportedSetpoint);
                Assert.AreEqual(acquisition, initial.View.Base.Definition.Reference.Input.Raw.CenteredAcquisition);
            }

            using var restarted = new CenteredFixture(root);
            await restarted.Ingress.InitializeAsync(token).ConfigureAwait(false);
            var index = new SqliteCenteredViews(restarted.Store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
            Assert.AreEqual(initialCursor, await index.ReadProductCursorAsync(camera, token).ConfigureAwait(false));
            var reloaded = await index.ReadLastLiveAsync(camera, token).ConfigureAwait(false);
            Assert.AreEqual(initial.View.Revision.RevisionId, reloaded?.View.Revision.RevisionId);
            Assert.AreEqual(initial.View.Base.Definition.Reference.Input.Raw.CenteredAcquisition,
                reloaded?.View.Base.Definition.Reference.Input.Raw.CenteredAcquisition);

            await restarted.Ingress.ReconcileCenteredControlAsync(config with { CenteredExposure = null }, token, camera)
                .ConfigureAwait(false);
            var next = await restarted.AdmitAsync(config, Timestamp(2)).ConfigureAwait(false);
            var scope = CenteredViewLookup.ResolveStream(await restarted.Ingress.ReadCurrentAsync(camera, token).ConfigureAwait(false));
            Assert.IsNotNull(scope);
            Assert.AreNotEqual(initial.View.Base.Definition.Stream, scope);
            Assert.IsNull(await index.ReadCurrentAsync(scope, token).ConfigureAwait(false));
            Assert.AreEqual(initial.View.Base.BaseId, (await index.ReadLastLiveAsync(camera, token).ConfigureAwait(false))?.View.Base.BaseId);

            var second = await ProcessAndDrainAsync(restarted, config, camera, token).ConfigureAwait(false);
            Assert.AreEqual(next.Manifest.Descriptor.Capture.CaptureId, second.Issued.View.Base.Definition.Reference.Capture.CaptureId);
            Assert.IsGreaterThan(initialCursor, second.Cursor);
            Assert.IsNull(await index.SelectLiveAsync(initial.View.Base.Definition.Stream, initial.View.Base.BaseId, token).ConfigureAwait(false));
            var archived = (await index.ReadArchiveAsync(initial.View.Base.Definition.Reference.Capture.CaptureId, token)
                .ConfigureAwait(false)).Single();
            Assert.AreEqual(initial.View.Revision.RevisionId, archived.InitialRevisionId);

            var oldExposure = initial.View.Base.Definition.Reference.Input.Key;
            var observation = Timestamp(3);
            using (var connection = await restarted.Store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionLease = transaction.ConfigureAwait(false);
                using var raw = connection.CreateCommand();
                raw.Transaction = transaction;
                raw.CommandText = "SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture;";
                raw.Parameters.AddWithValue("$capture", oldExposure.CaptureId.ToString("N"));
                var rowId = (long)(await raw.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                await TransientStageEventWriter.RecordAsync(connection, transaction, rowId, "causal-scan", null,
                    "succeeded", "transient_worker_frames", observation, token).ConfigureAwait(false);
                await TransientStageEventWriter.RecordAsync(connection, transaction, rowId, "causal-scan", null,
                    "succeeded", "transient_worker_frames", observation, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }

            // The durable transient completion can be consumed by a fresh publication reader.
            using var reopenedProcessing = new SqliteCaptureProcessingStore(restarted.Options);
            var afterRestart = new SqliteCenteredViews(reopenedProcessing, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance);
            Assert.HasCount(1, await afterRestart.ReadDueTrailWorkAsync(16, token).ConfigureAwait(false));
            using var faultStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = new CaptureProcessingPersistence(restarted.Options, reopenedProcessing, faultStorage, restarted.Telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            using var trailPublication = new CenteredViewTrailPublication(restarted.Ingress, afterRestart, persistence,
                new SqliteCameraAgentTransientOperatorProjection(restarted.Options, restarted.Ingress),
                new CenteredViewLayerProducer(afterRestart, persistence), restarted.Options,
                TimeProvider.System, NullLogger<CenteredViewTrailPublication>.Instance);
            await trailPublication.PublishReadyAsync(token).ConfigureAwait(false);
            await trailPublication.PublishReadyAsync(token).ConfigureAwait(false);
            Assert.IsEmpty(await afterRestart.ReadDueTrailWorkAsync(16, token).ConfigureAwait(false));
            var enriched = (await afterRestart.ReadArchiveAsync(oldExposure.CaptureId, token).ConfigureAwait(false)).Single();
            Assert.AreEqual(archived.DefaultRevisionId, enriched.DefaultRevisionId);
            Assert.AreNotEqual(archived.LatestRevisionId, enriched.LatestRevisionId);
            var latest = await afterRestart.ReadRevisionAsync(enriched.Base.BaseId, enriched.LatestRevisionId, token).ConfigureAwait(false);
            var assessment = latest!.Revision.Layers.Single(layer => layer.Key.Kind == TransientTrailPresentation.LayerKind);
            Assert.AreEqual(CenteredViewReadiness.AssessedEmpty, assessment.Readiness);
            Assert.AreEqual(oldExposure.CaptureId, assessment.Key.SourceCaptureId);
            Assert.AreEqual(observation, assessment.ObservedUtc);
            Assert.AreEqual(second.Issued.Interval, (await afterRestart.ReadLastLiveAsync(camera, token).ConfigureAwait(false))?.Interval);

            using var artifacts = new CameraAgentArtifactService(restarted.Options, restarted.Store, new CameraAgentPreviewEncoder());
            var reader = new CenteredViewPresentationReader(index, artifacts);
            var reopened = await reader.OpenPreviewAsync(archived.Base.BaseId, archived.InitialRevisionId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, reopened.Status);
            Assert.IsNotNull(reopened.Content);
            await using var content = reopened.Content.ConfigureAwait(false);
            var bytes = new byte[checked((int)reopened.Content.ByteLength)];
            await reopened.Content.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            Assert.AreEqual(initial.View.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(bytes));
        }
        finally { DeleteRoot(root); }
    }

    private static async Task<(CenteredViewCurrent Issued, long Cursor)> ProcessAndDrainAsync(
        CenteredFixture setup, CameraModuleConfig config, CameraKey camera, CancellationToken token)
    {
        var clock = TimeProvider.System;
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        using var artifacts = new CameraAgentArtifactService(setup.Options, setup.Store, new CameraAgentPreviewEncoder());
        using var telemetry = new CaptureControlTelemetry();
        using var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, clock, telemetry);
        using var scheduleStore = new SqliteCaptureScheduleStore(setup.Ingress, setup.Options, clock);
        using var schedule = new CaptureScheduleRuntimeCoordinator(scheduleStore, admission, new RawIngressState(clock),
            new CaptureLaneState(clock, setup.Options), setup.Factory, clock);
        var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, storage, setup.Telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        var views = new SqliteCenteredViews(setup.Store, clock, NullLogger<SqliteCenteredViews>.Instance);
        var products = new CenteredExposureProductReader(setup.Ingress, setup.Store);
        var producer = new CenteredViewProducer(persistence, artifacts, views, setup.Options, setup.Store);
        var layers = new CenteredViewSourceLayers(setup.Store, views, new CenteredViewLayerProducer(views, persistence));
        using var worker = new CenteredViewPublicationWorker(schedule, setup.Ingress, setup.Ingress, products, setup.Changes,
            views, producer, layers, clock, NullLogger<CenteredViewPublicationWorker>.Instance);
        using var standard = new StandardCaptureLaneHandler(setup.Factory, persistence, setup.Telemetry,
            NullLogger<StandardCaptureLaneHandler>.Instance, setup.Ingress, setup.Options);
        var lease = await setup.Ingress.ClaimAsync(setup.Policy.Definitions.Single(lane => lane.Name == "standard"),
            "centered-view-test", config, token).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await standard.HandleAsync(lease.Context, token).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);

        // No notification listener was running. Drain the real committed product
        // journal before acknowledging the lane and repeat the read after commit.
        var stream = CenteredViewLookup.ResolveStream(await setup.Ingress.ReadCurrentAsync(camera, token).ConfigureAwait(false));
        Assert.IsNotNull(stream);
        Assert.IsFalse(await worker.DrainCameraAsync(camera, stream, CancellationToken.None, token).ConfigureAwait(false));
        var selected = await views.ReadCurrentAsync(stream, token).ConfigureAwait(false);
        Assert.IsNotNull(selected);
        Assert.AreEqual(selected.View.Base.BaseId, await views.ReadLiveCandidateAsync(stream, token).ConfigureAwait(false));
        var cursor = await views.ReadProductCursorAsync(camera, token).ConfigureAwait(false);
        Assert.IsGreaterThan(0L, cursor);
        Assert.IsFalse(await worker.DrainCameraAsync(camera, stream, CancellationToken.None, token).ConfigureAwait(false));
        Assert.AreEqual(selected.Interval, (await views.ReadCurrentAsync(stream, token).ConfigureAwait(false))?.Interval);
        await setup.Ingress.CompleteAsync(lease, token).ConfigureAwait(false);
        return (selected, cursor);
    }
}
