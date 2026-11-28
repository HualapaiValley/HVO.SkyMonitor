using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
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
            using var silo = SiloInfrastructureFixture.OpenStore(root);
            using (var first = new CenteredFixture(root, siloProvider: silo))
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

            using var restarted = new CenteredFixture(root, siloProvider: silo);
            await restarted.Ingress.InitializeAsync(token).ConfigureAwait(false);
            var index = new SqliteCenteredViews(restarted.Store, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance, silo: restarted.InstalledSilo);
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
            await RecordCenteredTrailScanAsync(restarted.Store, oldExposure, observation, token).ConfigureAwait(false);
            await RecordCenteredTrailScanAsync(restarted.Store, oldExposure, observation, token).ConfigureAwait(false);

            // The durable transient completion can be consumed by a fresh publication reader.
            using var reopenedProcessing = new SqliteCaptureProcessingStore(restarted.Options, silo: restarted.Silo);
            var afterRestart = new SqliteCenteredViews(reopenedProcessing, TimeProvider.System, NullLogger<SqliteCenteredViews>.Instance, silo: restarted.InstalledSilo);
            Assert.HasCount(1, await afterRestart.ReadDueTrailWorkAsync(16, token).ConfigureAwait(false));
            var persistence = new CaptureProcessingPersistence(restarted.Options, reopenedProcessing, restarted.Telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance, silo: restarted.Silo);
            using var trailPublication = CreateCenteredTrailPublisher(restarted, afterRestart, persistence);
            await trailPublication.PublishReadyAsync(token).ConfigureAwait(false);
            await trailPublication.PublishReadyAsync(token).ConfigureAwait(false);
            await SettleViewsAsync(afterRestart, token).ConfigureAwait(false);
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

            using var artifacts = new CameraAgentArtifactService(restarted.Options, restarted.Store, new CameraAgentPreviewEncoder(), silo: restarted.Silo);
            var reader = new CenteredViewPresentationReader(index, artifacts);
            var reopened = await reader.OpenPreviewAsync(archived.Base.BaseId, archived.InitialRevisionId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, reopened.Status);
            Assert.IsNotNull(reopened.Content);
            await using var content = reopened.Content.ConfigureAwait(false);
            var bytes = new byte[checked((int)reopened.Content.ByteLength)];
            await reopened.Content.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            Assert.AreEqual(initial.View.Base.Definition.Preview.PayloadSha256, PayloadChecksum.ComputeSha256(bytes));

            await VerifyExpiredInitialCenteredRevisionAsync(restarted.InstalledSilo, silo.GetRequiredService<SiloTestStore>(),
                reopenedProcessing, afterRestart, reader, enriched, token).ConfigureAwait(false);
        }
        finally { DeleteRoot(root); }
    }

    private static async Task VerifyExpiredInitialCenteredRevisionAsync(SiloInstalledStorage storage, SiloTestStore store,
        SqliteCaptureProcessingStore processing, SqliteCenteredViews views, CenteredViewPresentationReader reader,
        CenteredViewArchiveBase archive, CancellationToken token)
    {
        const string owner = "saved-archive-reader";
        var selectionId = Guid.NewGuid();
        var baseId = archive.Base.BaseId;
        var captureId = archive.Base.Definition.Reference.Capture.CaptureId;
        var initialId = archive.InitialRevisionId;
        var latestId = archive.LatestRevisionId;
        Assert.IsNotNull(await views.SaveAsync(owner, selectionId, baseId, latestId, [], token).ConfigureAwait(false));
        var manifest = await storage.Index.ReadRoleAsync(new(SiloIdentityKind.Manifest, baseId, initialId), token).ConfigureAwait(false);
        Assert.IsNotNull(manifest);
        var initialLocator = Assert.ContainsSingle(manifest.Locators).Locator;
        var original = await storage.Catalog.ReopenAsync(baseId, initialId, token).ConfigureAwait(false);
        var preview = original.Items.Single(item => item.Required &&
            item.Role.Id == archive.Base.Definition.Preview.Product.ArtifactId.ToString("D"));
        Assert.IsNotNull(preview.ServedFrom);

        var retention = SiloRetentionTests.OpenForFixture(storage, store, TimeProvider.System);
        await retention.SetPolicyAsync(new(false, [new("manifest", TimeSpan.Zero, false, true)]), token).ConfigureAwait(false);
        var holds = await processing.ReadSavedSiloViewHoldsAsync(token).ConfigureAwait(false);
        var candidates = await retention.DryRunAsync(TimeProvider.System.GetUtcNow(), false, holds, token).ConfigureAwait(false);
        Assert.AreEqual("held-reference", candidates.Single(candidate => candidate.References.Any(reference =>
            reference.Identity.Kind == SiloIdentityKind.Manifest && reference.Identity.Id == baseId &&
            reference.Identity.Revision == latestId)).Decision);
        Assert.AreEqual("deleted", await retention.SweepAsync(true, processing.ReadSavedSiloViewHoldsAsync,
            null, token, select: target => target == initialLocator).ConfigureAwait(false));
        var remaining = Assert.ContainsSingle(await views.ReadArchiveAsync(archive.Base.Definition.Stream, captureId, token).ConfigureAwait(false));
        Assert.AreEqual(initialId, remaining.InitialRevisionId);
        Assert.AreEqual(latestId, remaining.LatestRevisionId);
        Assert.AreEqual(latestId, remaining.DefaultRevisionId);
        Assert.IsNull(remaining.LastLiveRevisionId);
        var saved = await views.ReadSavedAsync(owner, selectionId, token).ConfigureAwait(false);
        Assert.IsNotNull(saved);
        Assert.AreEqual(latestId, (await views.ReadRevisionAsync(saved.BaseId, saved.RevisionId, token).ConfigureAwait(false))?.Revision.RevisionId);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorizationBuilder().AddPolicy(CameraAgentAuthorizationPolicyNames.OperationsReadV1,
            policy => policy.RequireClaim(ClaimTypes.NameIdentifier, owner));
        builder.Services.AddSingleton<ICenteredViewReadIndex>(views);
        builder.Services.AddSingleton<ICenteredViewPresentationReader>(reader);
        var app = builder.Build();
        await using var appLease = app.ConfigureAwait(false);
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, owner)], "test"));
            return next(context);
        });
        app.UseAuthorization();
        app.MapCameraAgentCenteredViewEndpoints();
        await app.StartAsync(token).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var uri = new Uri(CenteredViewLinks.Revision(captureId, baseId, initialId), UriKind.Relative);
        using var expired = await client.GetAsync(uri, token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Gone, expired.StatusCode);
        var problem = await expired.Content.ReadFromJsonAsync<ProblemDetails>(token).ConfigureAwait(false);
        Assert.AreEqual("Centered view expired by storage policy.", problem?.Title);

        store.Delete(preview.ServedFrom);
        var mixed = await storage.Catalog.ReopenAsync(baseId, initialId, token).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(new[] { SiloDisposition.PolicyExpired, SiloDisposition.Missing },
            mixed.Blocking.Select(static item => item.Disposition).ToArray());
        using var missing = await client.GetAsync(uri, token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Gone, missing.StatusCode);
        Assert.AreEqual(string.Empty, await missing.Content.ReadAsStringAsync(token).ConfigureAwait(false));
    }

    private static CenteredViewTrailPublication CreateCenteredTrailPublisher(CenteredFixture fixture,
        SqliteCenteredViews views, CaptureProcessingPersistence persistence)
        => new(fixture.Ingress, views, persistence, new SqliteCameraAgentTransientOperatorProjection(fixture.Options, fixture.Ingress),
            new CenteredViewLayerProducer(views, persistence), fixture.Options, TimeProvider.System,
            NullLogger<CenteredViewTrailPublication>.Instance);

    private static async Task RecordCenteredTrailScanAsync(SqliteCaptureProcessingStore store, ExposureKey exposure,
        DateTimeOffset observation, CancellationToken token)
    {
        using var connection = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var transactionLease = transaction.ConfigureAwait(false);
        using var raw = connection.CreateCommand();
        raw.Transaction = transaction;
        raw.CommandText = "SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture;";
        raw.Parameters.AddWithValue("$capture", exposure.CaptureId.ToString("N"));
        var rowId = (long)(await raw.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        await TransientStageEventWriter.RecordAsync(connection, transaction, rowId, "causal-scan", null,
            "succeeded", "transient_worker_frames", observation, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async Task<(CenteredViewCurrent Issued, long Cursor)> ProcessAndDrainAsync(
        CenteredFixture setup, CameraModuleConfig config, CameraKey camera, CancellationToken token)
    {
        var clock = TimeProvider.System;
        await SiloInfrastructureFixture.SettleRawAdmissionsAsync(setup.Ingress, token).ConfigureAwait(false);
        using var artifacts = new CameraAgentArtifactService(setup.Options, setup.Store, new CameraAgentPreviewEncoder(), silo: setup.Silo);
        using var telemetry = new CaptureControlTelemetry();
        using var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, clock, telemetry);
        using var scheduleStore = new SqliteCaptureScheduleStore(setup.Ingress, setup.Options, clock);
        using var schedule = new CaptureScheduleRuntimeCoordinator(scheduleStore, admission, new RawIngressState(clock),
            new CaptureLaneState(clock, setup.Options), setup.Factory, clock);
        var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, setup.Telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance, silo: setup.Silo);
        var views = new SqliteCenteredViews(setup.Store, clock, NullLogger<SqliteCenteredViews>.Instance, silo: setup.InstalledSilo);
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
        await SettleViewsAsync(views, token).ConfigureAwait(false);
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

    private static async Task SettleViewsAsync(SqliteCenteredViews views, CancellationToken token)
    {
        while (true)
        {
            var publication = await views.PublishSiloChangesAsync(16, token).ConfigureAwait(false);
            Assert.IsNull(publication.Status);
            if (!publication.More) return;
        }
    }
}
