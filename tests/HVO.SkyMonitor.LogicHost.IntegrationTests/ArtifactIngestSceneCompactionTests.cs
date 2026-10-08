using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ArtifactIngestTests
{
    private static readonly JsonSerializerOptions SceneSelectorOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [TestMethod]
    public async Task LegacyLeaseReadsCompactCapturePixelsWithItsFrozenSceneAndRejectsCanceledAuthority()
    {
        var data = await CreateSceneIngestCaseAsync(graph: false).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));
        using (var response = await PostAsync(client, data.Raw, data.Pixels).ConfigureAwait(false))
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var response = await PostAsync(client, data.SceneManifest, data.SceneBytes).ConfigureAwait(false))
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var response = await PostAsync(client, data.Derivative, data.Pixels).ConfigureAwait(false))
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobId = await db.CentralDerivativeJobs.Where(job =>
                job.SourceArtifact!.Frame!.FrameId == data.Raw.Descriptor.Capture.CaptureId &&
                job.RecipeName == BuiltInProcessingRecipes.Annotation)
            .Select(job => job.Id).SingleAsync().ConfigureAwait(false);
        await db.CentralDerivativeJobs.Where(job => job.Id != jobId &&
                (job.Status == CentralDerivativeJobStatus.Pending || job.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        var jobs = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>();
        var lease = await jobs.ClaimNextAsync("legacy-scene-reader", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        lease.Should().NotBeNull();
        lease!.JobId.Should().Be(jobId);
        lease.ProjectedScene.Should().NotBeNull();
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        var reader = new CentralDerivativeJobInputReader(db,
            scope.ServiceProvider.GetRequiredService<ICentralArtifactObjectReader>(), jobs, telemetry, TimeProvider.System);
        var legacy = lease with { Inputs = null };
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() => reader.DescribeAsync(
            legacy with { WorkerId = "foreign-worker" }, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() => reader.DescribeAsync(
            legacy with { LeaseToken = Guid.NewGuid() }, CancellationToken.None)).ConfigureAwait(false);
        var description = await reader.DescribeAsync(legacy, CancellationToken.None).ConfigureAwait(false);
        description.References.Should().ContainSingle();
        description.References.Single().ArtifactId.Should().Be(lease.SourceArtifactId);
        description.ProcessingInputs.Single().Payload.IsEmpty.Should().BeTrue();
        var loaded = await reader.ReadAsync(legacy, CancellationToken.None).ConfigureAwait(false);
        loaded.ProcessingInputs.Should().ContainSingle();
        loaded.ProcessingInputs.Single().Payload.ToArray().Should().Equal(data.Pixels);
        loaded.ProcessingInputs.Single().Descriptor!.Capture.CaptureId.Should().Be(data.Raw.Descriptor.Capture.CaptureId);
        legacy.ProjectedScene.Should().Be(lease.ProjectedScene, "fallback input loading retains the separate frozen scene identity");
        await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, CentralDerivativeJobStatus.Canceled))
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() => reader.ReadAsync(
            legacy, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("raw,scene,derivative", false)]
    [DataRow("derivative,raw,scene", false)]
    [DataRow("scene,derivative,raw", false)]
    [DataRow("raw,derivative,scene", false)]
    [DataRow("raw,scene,derivative", true)]
    [DataRow("derivative,raw,scene", true)]
    [DataRow("scene,derivative,raw", true)]
    [DataRow("raw,derivative,scene", true)]
    public async Task CompactSceneArrivalsFreezeOneSourceBoundAnnotationAndPreservePixels(string order, bool graph)
    {
        ArgumentNullException.ThrowIfNull(order);
        var fixture = AssemblyHooks.Fixture;
        var data = await CreateSceneIngestCaseAsync(graph).ConfigureAwait(false);
        var raw = data.Raw;
        var derivative = data.Derivative;
        var full = data.Full;
        var pixels = data.Pixels;
        var sceneManifest = data.SceneManifest;
        var sceneBytes = data.SceneBytes;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));
        var received = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in order.Split(','))
        {
            using var response = item == "scene"
                ? await PostAsync(client, sceneManifest, sceneBytes).ConfigureAwait(false)
                : await PostAsync(client, item == "raw" ? raw : derivative, pixels).ConfigureAwait(false);
            response.StatusCode.Should().Be(
                item != "raw" && !received.Contains("raw") ? (HttpStatusCode)425 : HttpStatusCode.Accepted,
                $"arrival {item}: {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            received.Add(item);
            if (!received.Contains("scene") || !received.Contains("raw"))
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                (await db.CentralDerivativeJobs.CountAsync(job =>
                    job.SourceArtifact!.Frame!.FrameId == raw.Descriptor.Capture.CaptureId &&
                    job.RecipeName == BuiltInProcessingRecipes.Annotation).ConfigureAwait(false)).Should().Be(0);
            }
        }
        // Durable 425 arrivals become acknowledgable once their exact raw source arrives.
        using (var duplicate = await PostAsync(client, sceneManifest, sceneBytes).ConfigureAwait(false))
            duplicate.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var duplicate = await PostAsync(client, derivative, pixels).ConfigureAwait(false))
            duplicate.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // A late legacy representation cannot replace compact authority or its scene hold.
        using (var lateInline = await PostAsync(client, raw with { Scene = full }, pixels).ConfigureAwait(false))
            lateInline.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var conflictingInline = await PostAsync(client, raw with { Scene = full with { Objects = [] } }, pixels)
            .ConfigureAwait(false))
            conflictingInline.StatusCode.Should().Be(HttpStatusCode.Conflict);

        Guid jobId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.Include(item => item.InputRequirements).Include(item => item.Inputs)
                .SingleAsync(item => item.SourceArtifact!.Frame!.FrameId == raw.Descriptor.Capture.CaptureId &&
                    item.RecipeName == BuiltInProcessingRecipes.Annotation).ConfigureAwait(false);
            jobId = job.Id;
            job.GraphExecutionId.HasValue.Should().Be(graph);
            var reference = CentralProjectedSceneResolver.ReadReference(job);
            reference.Should().NotBeNull();
            reference!.Source.ArtifactIdentitySha256.Should().Be(raw.IdempotencyKey);
            job.InputRequirements.Single(item => item.BindingName == CentralProjectedSceneResolver.BindingName)
                .SelectorJson.Length.Should().BeLessThanOrEqualTo(2048);
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>()
                .IsHeldAsync(reference.CentralArtifactId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            await db.CentralDerivativeJobs.Where(item => item.Id != jobId &&
                    (item.Status == CentralDerivativeJobStatus.Pending || item.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        }
        await using var workerScope = fixture.Factory.Services.CreateAsyncScope();
        var lease = await workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync("compact-scene-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        lease.Should().NotBeNull();
        lease!.JobId.Should().Be(jobId);
        lease.ProjectedScene.Should().NotBeNull();
        using var options = JsonDocument.Parse(lease.RecipeOptionsJson);
        var expected = await workerScope.ServiceProvider.GetRequiredService<LogicHostRecipeExecutionAdapter>()
            .ExecuteAsync(raw.Descriptor, pixels, lease.RecipeName, options.RootElement, ProcessingInputSelector.Raw(),
                lease.TargetVariant, CreateExpectedAnnotation(full)).ConfigureAwait(false);
        expected.Products.Should().ContainSingle();
        lease.ExpectedRecipeIdentitySha256.Should().Be(expected.Products[0].Recipe.IdentitySha256);
        var result = await workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
            .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        result.Status.Should().Be(ProcessingOutcomeStatus.Produced, result.ReasonCode);
        using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password)
            .ConfigureAwait(false);
        var actual = await ReadDerivativeAsync(owner, lease.SourceDevicePublicId, result.ArtifactId!.Value).ConfigureAwait(false);
        actual.Should().Equal(expected.Products[0].Payload.ToArray());
        // Completion drops the job hold, but retained compact images still require this sole scene.
        if (lease.GraphExecutionId is { } graphExecutionId)
            await workerScope.ServiceProvider.GetRequiredService<ICentralProcessingGraphScheduler>()
                .ConvergeAsync(graphExecutionId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        var completedDb = workerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await completedDb.CentralDerivativeJobs.AsNoTracking().SingleAsync(job => job.Id == jobId).ConfigureAwait(false))
            .Status.Should().Be(CentralDerivativeJobStatus.Completed);
        if (lease.GraphExecutionId is { } completedGraphId)
            (await completedDb.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleAsync(execution => execution.Id == completedGraphId).ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingGraphExecutionStatus.Completed);
        (await workerScope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
            .ReleaseAsync(lease.ProjectedScene!.CentralArtifactId, CancellationToken.None).ConfigureAwait(false))
            .Should().Be(CentralArtifactRetentionResult.Held);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task MixedSceneProvenanceWaitsAuthenticatesAndPreservesEstablishedAuthority(bool compactFirst, bool emptyConflict)
    {
        var data = await CreateSceneIngestCaseAsync(graph: false).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));
        var first = data.Raw with { Scene = compactFirst ? data.Raw.Scene : data.Full };
        var second = data.Derivative with { Scene = compactFirst ? data.Full : data.Raw.Scene };
        using (var response = await PostAsync(client, first, data.Pixels).ConfigureAwait(false))
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        string established;
        Dictionary<Guid, string> frozen;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            established = (await db.CentralFrames.SingleAsync(frame => frame.FrameId == data.Raw.Descriptor.Capture.CaptureId)
                .ConfigureAwait(false)).SceneProvenanceJson!;
            frozen = await db.CentralDerivativeJobs.Where(job => job.SourceArtifact!.Frame!.FrameId == data.Raw.Descriptor.Capture.CaptureId)
                .ToDictionaryAsync(job => job.Id, job => job.ExpectedRecipeIdentitySha256).ConfigureAwait(false);
        }
        using (var waiting = await PostAsync(client, second, data.Pixels).ConfigureAwait(false))
        {
            waiting.StatusCode.Should().Be((HttpStatusCode)425);
            waiting.Headers.RetryAfter.Should().NotBeNull();
        }
        using (var scene = await PostAsync(client, data.SceneManifest, data.SceneBytes).ConfigureAwait(false))
            scene.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var wrong = data.Full with
        {
            Objects = emptyConflict ? [] : data.Full.Objects!.Select(item => item with { PixelX = item.PixelX + 1 }).ToArray()
        };
        using (var rejected = await PostAsync(client, data.Raw with { Scene = wrong }, data.Pixels).ConfigureAwait(false))
            rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using (var accepted = await PostAsync(client, second, data.Pixels).ConfigureAwait(false))
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var assertion = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var context = assertion.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var retained = await context.CentralFrames.SingleAsync(frame => frame.FrameId == data.Raw.Descriptor.Capture.CaptureId)
            .ConfigureAwait(false);
        retained.SceneProvenanceJson.Should().Be(established, "later representation cannot change frozen authority");
        foreach (var identity in frozen)
            (await context.CentralDerivativeJobs.SingleAsync(job => job.Id == identity.Key).ConfigureAwait(false))
                .ExpectedRecipeIdentitySha256.Should().Be(identity.Value);
        if (compactFirst)
        {
            var scene = await context.CentralArtifacts.SingleAsync(artifact => artifact.ArtifactId == data.SceneManifest.Descriptor.Artifact.ArtifactId)
                .ConfigureAwait(false);
            (await assertion.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>()
                .IsHeldAsync(scene.Id, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task CalibratedAnnotationReplaySelectsAuthenticatedSceneAfterRawRetention()
    {
        var data = await CreateSceneIngestCaseAsync(graph: true, FrameArtifactRole.Calibrated).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));
        using (var raw = await PostAsync(client, data.Raw, data.Pixels).ConfigureAwait(false))
            raw.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var scene = await PostAsync(client, data.SceneManifest, data.SceneBytes).ConfigureAwait(false))
            scene.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var calibrated = await PostAsync(client, data.Derivative, data.Pixels).ConfigureAwait(false))
            calibrated.StatusCode.Should().Be(HttpStatusCode.Accepted);
        Guid firstJobId;
        Guid rawId;
        Guid calibratedId;
        Guid revisionId;
        string actor;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.SingleAsync(item => item.GraphExecutionId != null &&
                item.SourceArtifact!.ArtifactId == data.Derivative.Descriptor.Artifact.ArtifactId &&
                item.RecipeName == BuiltInProcessingRecipes.Annotation).ConfigureAwait(false);
            firstJobId = job.Id;
            calibratedId = job.SourceCentralArtifactId;
            rawId = await db.CentralArtifacts.Where(item => item.ArtifactId == data.Raw.Descriptor.Artifact.ArtifactId)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            revisionId = await db.CentralProcessingGraphExecutions.Where(item => item.Id == job.GraphExecutionId)
                .Select(item => item.RevisionId).SingleAsync().ConfigureAwait(false);
            actor = await db.DeviceRegistrations.Where(item => item.Id == data.RegistrationId)
                .Select(item => item.OwnerUserId).SingleAsync().ConfigureAwait(false);
            await db.CentralDerivativeJobs.Where(item => item.Id != firstJobId &&
                    (item.Status == CentralDerivativeJobStatus.Pending || item.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        }
        var first = await ExecuteSceneAnnotationAsync(data, firstJobId).ConfigureAwait(false);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // The default rolling window still owns raw pixels until explicitly canceled.
            var waitingRawJobs = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == rawId && job.Status == CentralDerivativeJobStatus.Waiting)
                .Select(job => job.Id).ToArrayAsync().ConfigureAwait(false);
            waitingRawJobs.Should().NotBeEmpty();
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
                .ReleaseAsync(rawId, CancellationToken.None).ConfigureAwait(false)).Should().Be(CentralArtifactRetentionResult.Held);
            foreach (var waitingJob in waitingRawJobs)
                await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                    .CancelAsync(waitingJob, actor, CancellationToken.None).ConfigureAwait(false);
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
                .ReleaseAsync(rawId, CancellationToken.None).ConfigureAwait(false)).Should().Be(CentralArtifactRetentionResult.Released);
            var retired = await db.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == rawId).ConfigureAwait(false);
            retired.ObjectState.Should().Be(CentralArtifactObjectState.Expired);
            retired.RetentionDeletionToken.Should().NotBeNull();
            using (var sceneStatus = await PostStatusAsync(client, data.SceneManifest).ConfigureAwait(false))
                sceneStatus.StatusCode.Should().Be(HttpStatusCode.Accepted,
                    "a scene status retry needs retained source identity, not expired raw pixels");
            // Recover the durable state produced by the pre-correction status-retry regression.
            var sceneRow = await db.CentralArtifacts.Include(item => item.Sources)
                .SingleAsync(item => item.Id == first.Scene.CentralArtifactId).ConfigureAwait(false);
            sceneRow.ReconstructionState = CentralReconstructionState.PendingReference;
            sceneRow.StateReasonCode = "lineage.source-unavailable";
            sceneRow.Sources.Single().ResolvedCentralArtifactId = null;
            sceneRow.Sources.Single().ResolvedArtifact = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
            using (var telemetry = new CentralIngestTelemetry())
                await new CentralArtifactReconciliationService(
                    AssemblyHooks.Fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                    TimeProvider.System, telemetry, NullLogger<CentralArtifactReconciliationService>.Instance)
                    .ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            var recoveredScene = await db.CentralArtifacts.AsNoTracking().Include(item => item.Sources)
                .SingleAsync(item => item.Id == first.Scene.CentralArtifactId).ConfigureAwait(false);
            recoveredScene.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
            recoveredScene.Sources.Single().ResolvedCentralArtifactId.Should().Be(rawId);
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
                .ReleaseAsync(first.Scene.CentralArtifactId, CancellationToken.None).ConfigureAwait(false))
                .Should().Be(CentralArtifactRetentionResult.Held);
            var replay = await scope.ServiceProvider.GetRequiredService<ICentralProcessingGraphScheduler>()
                .ScheduleReplayAsync(new(revisionId, [calibratedId], actor, $"expired-raw-{Guid.NewGuid():N}", "scene-expiry-test"),
                    DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            replay.Outcome.Should().Be(CentralProcessingGraphScheduleOutcome.Created, replay.ReasonCode);
            var replayJobId = await db.CentralDerivativeJobs.Where(item => item.GraphExecutionId == replay.Execution!.Id)
                .Select(item => item.Id).SingleAsync().ConfigureAwait(false);
            var second = await ExecuteSceneAnnotationAsync(data, replayJobId).ConfigureAwait(false);
            second.RecipeIdentity.Should().Be(first.RecipeIdentity);
            second.Scene.Should().Be(first.Scene);
            second.Pixels.Should().Equal(first.Pixels);
        }

    }

    [TestMethod]
    public async Task SceneMetadataArrivingAfterAuthenticatedRawExpiryUnblocksCalibratedAnnotation()
    {
        var data = await CreateSceneIngestCaseAsync(graph: true, FrameArtifactRole.Calibrated).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));
        using (var raw = await PostAsync(client, data.Raw, data.Pixels).ConfigureAwait(false))
            raw.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var calibrated = await PostAsync(client, data.Derivative, data.Pixels).ConfigureAwait(false))
            calibrated.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var raw = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == data.Raw.Descriptor.Artifact.ArtifactId)
                .ConfigureAwait(false);
            // Legacy successful publication could mark Available before recording this timestamp.
            raw.ObjectVerifiedAtUtc = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
            var actor = await db.DeviceRegistrations.Where(item => item.Id == data.RegistrationId)
                .Select(item => item.OwnerUserId).SingleAsync().ConfigureAwait(false);
            var jobs = await db.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == raw.Id &&
                    (job.Status == CentralDerivativeJobStatus.Waiting || job.Status == CentralDerivativeJobStatus.Pending ||
                        job.Status == CentralDerivativeJobStatus.RetryableFailure))
                .Select(job => job.Id).ToArrayAsync().ConfigureAwait(false);
            foreach (var job in jobs)
                await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                    .CancelAsync(job, actor, CancellationToken.None).ConfigureAwait(false);
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
                .ReleaseAsync(raw.Id, CancellationToken.None).ConfigureAwait(false)).Should().Be(CentralArtifactRetentionResult.Released);
            var retired = await db.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false);
            retired.ObjectVerifiedAtUtc.Should().NotBeNull("retirement preserves prior successful publication evidence");
        }
        using (var scene = await PostAsync(client, data.SceneManifest, data.SceneBytes).ConfigureAwait(false))
            scene.StatusCode.Should().Be(HttpStatusCode.Accepted);
        Guid jobId;
        await using (var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobId = await db.CentralDerivativeJobs.Where(job => job.GraphExecutionId != null &&
                    job.SourceArtifact!.ArtifactId == data.Derivative.Descriptor.Artifact.ArtifactId &&
                    job.RecipeName == BuiltInProcessingRecipes.Annotation)
                .Select(job => job.Id).SingleAsync().ConfigureAwait(false);
            await db.CentralDerivativeJobs.Where(job => job.Id != jobId &&
                    (job.Status == CentralDerivativeJobStatus.Pending || job.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        }
        _ = await ExecuteSceneAnnotationAsync(data, jobId).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RetiringUnpublishedRawIntentDoesNotAuthenticateProjectedSceneSource()
    {
        var data = await CreateSceneIngestCaseAsync(graph: false).ConfigureAwait(false);
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        var token = await GetSystemTokenAsync(client).ConfigureAwait(false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await using var objectScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var fault = new RejectSceneRawCopyStore(objectScope.ServiceProvider.GetRequiredService<IObjectStore>());
        using var faultFactory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObjectStore>();
            services.AddSingleton<IObjectStore>(fault);
        }));
        using var faultClient = faultFactory.CreateClient();
        faultClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var failed = await PostAsync(faultClient, data.Raw, data.Pixels).ConfigureAwait(false))
            failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        fault.Failures.Should().BeGreaterThan(0);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var raw = await db.CentralArtifacts.SingleAsync(item => item.ArtifactId == data.Raw.Descriptor.Artifact.ArtifactId)
            .ConfigureAwait(false);
        raw.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
        raw.ObjectVerifiedAtUtc.Should().BeNull();
        using (var waiting = await PostAsync(client, data.SceneManifest, data.SceneBytes).ConfigureAwait(false))
            waiting.StatusCode.Should().Be((HttpStatusCode)425);
        (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
            .ReleaseAsync(raw.Id, CancellationToken.None).ConfigureAwait(false)).Should().Be(CentralArtifactRetentionResult.Released);
        var retired = await db.CentralArtifacts.AsNoTracking().SingleAsync(item => item.Id == raw.Id).ConfigureAwait(false);
        retired.ObjectState.Should().Be(CentralArtifactObjectState.Expired);
        retired.RetentionDeletionToken.Should().NotBeNull();
        retired.ObjectVerifiedAtUtc.Should().BeNull("retention cannot invent successful publication");
        using (var waiting = await PostStatusAsync(client, data.SceneManifest).ConfigureAwait(false))
            waiting.StatusCode.Should().Be((HttpStatusCode)425);
        var frame = await db.CentralFrames.SingleAsync(item => item.Id == raw.CentralFrameId).ConfigureAwait(false);
        (await scope.ServiceProvider.GetRequiredService<CentralProjectedSceneResolver>()
            .SelectAsync(frame, CancellationToken.None).ConfigureAwait(false)).Should().BeNull();
    }

    // Every shipped edge configuration selects ["ORI","UMA","UMI","CAS","CYG","LYR"], declared in that order while the
    // scene records it sorted. The upload used to fail scheduling with projected-scene.source-mismatch, a 500 the
    // edge retries without end; the declared selection now selects the scene and schedules its annotation.
    [TestMethod]
    public async Task SceneDeclaringItsConstellationsInConfigurationOrderIsAcceptedAndSelected()
    {
        var data = await CreateSceneIngestCaseAsync(graph: false,
            constellationIds: ["ORI", "UMA", "UMI", "CAS", "CYG", "LYR"]).ConfigureAwait(false);
        data.Raw.Scene!.ConstellationIds.Should().Equal("ORI", "UMA", "UMI", "CAS", "CYG", "LYR");
        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));

        using var raw = await PostAsync(client, data.Raw, data.Pixels).ConfigureAwait(false);
        using var scene = await PostAsync(client, data.SceneManifest, data.SceneBytes).ConfigureAwait(false);
        // The status probe is the edge's retry path; it reschedules, so it must not fail where the upload did.
        using var status = await PostStatusAsync(client, data.SceneManifest).ConfigureAwait(false);
        using (new AssertionScope())
        {
            raw.StatusCode.Should().Be(HttpStatusCode.Accepted, await raw.Content.ReadAsStringAsync().ConfigureAwait(false));
            scene.StatusCode.Should().Be(HttpStatusCode.Accepted, await scene.Content.ReadAsStringAsync().ConfigureAwait(false));
            ((int)status.StatusCode).Should().BeInRange(200, 299, await status.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var captureId = data.Raw.Descriptor.Capture.CaptureId;
        try
        {
            var job = await db.CentralDerivativeJobs.Include(item => item.InputRequirements).Include(item => item.Inputs)
                .SingleAsync(item => item.SourceArtifact!.Frame!.FrameId == captureId &&
                    item.RecipeName == BuiltInProcessingRecipes.Annotation).ConfigureAwait(false);
            var reference = CentralProjectedSceneResolver.ReadReference(job);
            reference.Should().NotBeNull();
            reference!.Source.ArtifactIdentitySha256.Should().Be(data.Raw.IdempotencyKey);
        }
        finally
        {
            // Tests in this assembly claim the next job from the shared database; leave none of this capture's claimable.
            await db.CentralDerivativeJobs.Where(item => item.SourceArtifact!.Frame!.FrameId == captureId &&
                    (item.Status == CentralDerivativeJobStatus.Waiting || item.Status == CentralDerivativeJobStatus.Pending ||
                        item.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        }
    }

    private sealed class RejectSceneRawCopyStore(IObjectStore inner) : PerformanceObjectStoreDecorator(inner)
    {
        public int Failures { get; private set; }
        protected override ValueTask BeforeOperationAsync(string operation, string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation == "copy")
            {
                Failures++;
                throw new ObjectStoreException(ObjectStoreFailureKind.Transient, "copy");
            }
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<(string RecipeIdentity, CentralProjectedSceneReference Scene, byte[] Pixels)> ExecuteSceneAnnotationAsync(SceneIngestCase data, Guid expectedJobId)
    {
        await using var worker = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var lease = await worker.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync("expired-raw-scene-replay", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        lease.Should().NotBeNull();
        lease!.JobId.Should().Be(expectedJobId);
        lease.ProjectedScene.Should().NotBeNull();
        using var options = JsonDocument.Parse(lease.RecipeOptionsJson);
        var selector = JsonSerializer.Deserialize<ProcessingInputSelector>(lease.InputSelectorJson, SceneSelectorOptions);
        selector.Should().NotBeNull();
        var expected = await worker.ServiceProvider.GetRequiredService<LogicHostRecipeExecutionAdapter>()
            .ExecuteAsync(data.Derivative.Descriptor, data.Pixels, lease.RecipeName, options.RootElement,
                selector!, lease.TargetVariant, CreateExpectedAnnotation(data.Full)).ConfigureAwait(false);
        lease.ExpectedRecipeIdentitySha256.Should().Be(expected.Products.Single().Recipe.IdentitySha256);
        var result = await worker.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
            .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        result.Status.Should().Be(ProcessingOutcomeStatus.Produced, result.ReasonCode);
        using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password)
            .ConfigureAwait(false);
        var pixels = await ReadDerivativeAsync(owner, lease.SourceDevicePublicId, result.ArtifactId!.Value).ConfigureAwait(false);
        pixels.Should().Equal(expected.Products.Single().Payload.ToArray());
        await worker.ServiceProvider.GetRequiredService<ICentralProcessingGraphScheduler>()
            .ConvergeAsync(lease.GraphExecutionId!.Value, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        return (lease.ExpectedRecipeIdentitySha256, lease.ProjectedScene!, pixels);
    }

    private sealed record SceneIngestCase(
        ArtifactManifestV2 Raw, ArtifactManifestV2 Derivative, SceneProvenance Full,
        byte[] Pixels, StructuredProcessingProductManifestV1 SceneManifest, byte[] SceneBytes,
        Guid RegistrationId);

    private static async Task<SceneIngestCase> CreateSceneIngestCaseAsync(
        bool graph, FrameArtifactRole derivativeRole = FrameArtifactRole.Preview,
        IReadOnlyList<string>? constellationIds = null)
    {
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig($"compact-scene-{Guid.NewGuid():N}");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        if (graph) await AssignCompactSceneAnnotationGraphAsync(registrationId, derivativeRole).ConfigureAwait(false);
        var utc = DateTimeOffset.UnixEpoch;
        var pixels = new byte[] { 1, 32, 128, 255 };
        var raw = CreateManifestV2(deviceId, rig, pixels, 1055);
        // A selection needs topology provenance; the scene needs no segments to record the selection.
        var topology = constellationIds is null ? null : new InMemoryConstellationTopology([],
            new ConstellationTopologyMetadata("fixture", "1", new Uri("https://example.test/constellations"),
                new string('E', 64), "test", "v1"));
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 0, 1)
        ]), topology).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 1, 1, 1, 1, 2, 2,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                new string('C', 64), "test", "v1"), projectionVersion: "perspective-v1",
            constellationIds: constellationIds)).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(raw.Descriptor.Capture.CaptureId, raw.Descriptor.Artifact.ArtifactId,
                raw.IdempotencyKey), "calibration-v1", visible.Request.ProjectionVersion);
        var full = new SceneProvenance(new string('D', 64), rig.ProfileVersion, scene.Catalog.Name,
            scene.Catalog.Version, scene.Catalog.ChecksumSha256, "Perspective", scene.Projection.AlgorithmVersion,
            scene.AstronomyAlgorithmVersion, "sensor-v1",
            Objects: scene.Objects.Select(item => new ProjectedObjectProvenance(
                item.Id, item.DisplayName, item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray(),
            Segments: [], ConstellationTopologyVersion: scene.ConstellationTopology?.Version,
            ConstellationTopologySourceUrl: scene.ConstellationTopology?.SourceUrl,
            ConstellationTopologySha256: scene.ConstellationTopology?.SourceSha256,
            ConstellationTopologyLicense: scene.ConstellationTopology?.License,
            ConstellationTopologyPreprocessingVersion: scene.ConstellationTopology?.PreprocessingVersion,
            ConstellationIds: constellationIds ?? [], RigProfileHashSha256: CameraRigProfileIdentity.ComputeSha256(rig),
            ProjectionCalibrationVersion: "calibration-v1", SceneUtc: utc,
            ProjectedSceneStageSchemaVersion: "projected-scene-stage-v1", ProjectedSceneStageKey: new string('E', 64));
        raw = raw with { Scene = full.WithoutProjectedGeometry() };
        var derivative = CreateManifestV2(deviceId, rig, pixels, 1055, role: derivativeRole,
            sourceArtifactIds: [raw.Descriptor.Artifact.ArtifactId], captureId: raw.Descriptor.Capture.CaptureId)
            with
        { Scene = raw.Scene };
        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var recipe = RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.ProjectedScene, "1.0.0", "integration-v1",
            JsonSerializer.SerializeToElement(new { }));
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, "projected-scene",
            ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256, [raw.Descriptor.Artifact.ArtifactId]);
        var sceneArtifact = new ArtifactDescriptor(ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.Metadata, "projected-scene-step", "projected-scene", raw.Descriptor.Timing.ReadoutCompletedUtc,
            [raw.Descriptor.Artifact.ArtifactId], recipe, StructuredProcessingProductContracts.ProjectedSceneMediaType,
            Convert.ToHexString(SHA256.HashData(sceneBytes)));
        var sceneManifest = new StructuredProcessingProductManifestV1(StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(raw.Descriptor, sceneArtifact, outputIdentity,
                [new("projected-scene", "integration-v1")], new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                raw.Descriptor.Controls.EffectiveExposure.Ticks, sceneBytes.Length, ProcessingProductKind.Metadata,
                scene.SchemaVersion, scene.SceneIdentitySha256), "derived/projected-scene.json", "projected-scene-step");
        return new(raw, derivative, full, pixels, sceneManifest, sceneBytes, registrationId);
    }

    private static async Task<Guid> AssignCompactSceneAnnotationGraphAsync(
        Guid registrationId, FrameArtifactRole derivativeRole = FrameArtifactRole.Preview)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
        var camera = new LogicalCamera
        {
            ObservatoryId = registration.ObservatoryId,
            Slug = $"compact-{Guid.NewGuid():N}",
            Name = "Compact scene camera",
            CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-2),
            CreatedByUserId = registration.OwnerUserId
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = registration.Id,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            AssignedByUserId = registration.OwnerUserId,
            AssignmentReasonCode = "compact-scene-test"
        };
        camera.Installations.Add(installation);
        var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingGraphNodeRegistry>();
        var seeded = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        var annotation = seeded.Nodes.Single(node => node.Id == "Annotation");
        var sourceRole = derivativeRole == FrameArtifactRole.Calibrated ? FrameArtifactRole.Calibrated : FrameArtifactRole.Raw;
        var sourceId = sourceRole == FrameArtifactRole.Calibrated ? "$calibrated" : "$raw";
        var definition = seeded with
        {
            Name = $"compact-annotation-{Guid.NewGuid():N}",
            Sources = [new(sourceId, [new(sourceRole, "source", ProcessingProductKind.PixelData)])],
            Nodes = [new(annotation.Id, annotation.StepAlias, annotation.StepVersion, annotation.OperationKind,
                annotation.Enabled, annotation.FailurePolicy, annotation.Order, annotation.EffectiveOptions,
                [new(sourceId)], [new([sourceRole], [ProcessingProductKind.PixelData], [], [], [])],
                annotation.Outputs, annotation.Window, annotation.CapabilityLabels, annotation.HostApplicability)]
        };
        var portable = ProcessingGraphCompiler.Compile(definition);
        var central = ProcessingGraphCompiler.Compile(definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
        central.IsValid.Should().BeTrue();
        var revision = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256,
            CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-2),
            CreatedByUserId = registration.OwnerUserId,
            PublishedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            PublishedByUserId = registration.OwnerUserId
        };
        var assignment = new CentralProcessingGraphAssignment
        {
            Revision = revision,
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = registration.ObservatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            EffectiveFromUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            ActorUserId = registration.OwnerUserId,
            ReasonCode = "compact-scene-test"
        };
        revision.Assignments.Add(assignment);
        db.AddRange(camera, installation, revision, assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return revision.Id;
    }
}
