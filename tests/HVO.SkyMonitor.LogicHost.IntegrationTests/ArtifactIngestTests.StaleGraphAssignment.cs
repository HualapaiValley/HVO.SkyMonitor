using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// A central assignment whose revision embeds a built-in recipe definition from an earlier
/// <c>ImplementationVersion</c> cannot be expanded by this binary. Ingest answers HTTP 500 for its frames; these tests
/// pin what reconciliation does when it reaches the same frame (an adopted stranded verification): it must back the
/// one artifact off and keep the cycle, the lease and the hosted service alive, then recover once the camera is
/// reassigned.
/// </summary>
public sealed partial class ArtifactIngestTests
{
    [TestMethod]
    public async Task Reconciliation_StaleGraphAssignmentBacksOffOneArtifactAndRecoversAfterReassignment()
    {
        var fixture = AssemblyHooks.Fixture;
        var stale = await SeedStaleAssignmentArtifactAsync("stale-graph-direct", captureSequence: 5201)
            .ConfigureAwait(false);
        var healthyArtifactId = await SeedHealthyPendingArtifactAsync("stale-graph-direct-healthy", 5202)
            .ConfigureAwait(false);
        try
        {
            var logger = new RecordingLogger<CentralArtifactReconciliationService>();
            using var telemetry = new CentralIngestTelemetry();
            var reconciler = new CentralArtifactReconciliationService(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                telemetry,
                logger);

            var firstCycleAt = DateTimeOffset.UtcNow;
            var firstCycle = () => reconciler.ReconcileAsync(CancellationToken.None);
            await firstCycle.Should().NotThrowAsync(
                "an unexpandable graph revision is one artifact's scheduling failure, not a recovery cycle failure")
                .ConfigureAwait(false);

            var afterFirst = await ReadSchedulingStateAsync(stale.CentralArtifactId).ConfigureAwait(false);
            afterFirst.ObjectState.Should().Be(CentralArtifactObjectState.Pending);
            afterFirst.StateReasonCode.Should().Be("object.derivative-scheduling-interrupted");
            afterFirst.ObjectVerificationToken.Should().NotBeNull();
            afterFirst.ObjectVerificationRetryCount.Should().Be(1);
            afterFirst.ObjectVerificationRetryAtUtc.Should().BeOnOrAfter(
                firstCycleAt + CentralArtifactReconciliationService.CalculateVerificationRetryDelay(1));
            (await ReadSchedulingStateAsync(healthyArtifactId).ConfigureAwait(false)).Should().Match<SchedulingState>(
                state => state.ObjectState == CentralArtifactObjectState.Available &&
                    state.ObjectVerificationToken == null,
                "the cycle continues past the stale artifact to the next one");
            (await ReadRecoveryLeaseTokenAsync().ConfigureAwait(false)).Should().BeNull("the cycle completed");
            (await CountGraphExecutionsAsync(stale.CentralArtifactId).ConfigureAwait(false)).Should().Be(0);
            logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error &&
                entry.Message.Contains(stale.AssignmentId.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains(stale.RevisionId.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains(BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal));

            // Still stale once the backoff elapses: the next attempt backs off further rather than re-looping.
            await MakeSchedulingRetryDueAsync(stale.CentralArtifactId).ConfigureAwait(false);
            var secondCycleAt = DateTimeOffset.UtcNow;
            _ = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            var afterSecond = await ReadSchedulingStateAsync(stale.CentralArtifactId).ConfigureAwait(false);
            afterSecond.ObjectVerificationRetryCount.Should().Be(2);
            afterSecond.ObjectVerificationRetryAtUtc.Should().BeOnOrAfter(
                secondCycleAt + CentralArtifactReconciliationService.CalculateVerificationRetryDelay(2));

            await ReassignToCurrentSeedRevisionAsync(stale).ConfigureAwait(false);
            await MakeSchedulingRetryDueAsync(stale.CentralArtifactId).ConfigureAwait(false);
            _ = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

            var recovered = await ReadSchedulingStateAsync(stale.CentralArtifactId).ConfigureAwait(false);
            recovered.ObjectState.Should().Be(CentralArtifactObjectState.Available);
            recovered.ObjectVerificationToken.Should().BeNull();
            recovered.StateReasonCode.Should().BeNull();
            (await CountGraphExecutionsAsync(stale.CentralArtifactId).ConfigureAwait(false)).Should().Be(1);
        }
        finally
        {
            await ClearStaleReconciliationStateAsync(stale.CentralArtifactId).ConfigureAwait(false);
            await RetireStaleGraphCameraAsync(stale).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task Reconciliation_HostedLoopSurvivesStaleGraphAssignmentAndReachesTheNextArtifact()
    {
        var fixture = AssemblyHooks.Fixture;
        var stale = await SeedStaleAssignmentArtifactAsync("stale-graph-hosted", captureSequence: 5211)
            .ConfigureAwait(false);
        var healthyArtifactId = await SeedHealthyPendingArtifactAsync("stale-graph-hosted-healthy", 5212)
            .ConfigureAwait(false);
        try
        {
            using var telemetry = new CentralIngestTelemetry();
            var reconciler = new CentralArtifactReconciliationService(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                telemetry,
                new RecordingLogger<CentralArtifactReconciliationService>());
            // The default BackgroundServiceExceptionBehavior (StopHost), exactly as Program.cs registers the service.
            using var host = new HostBuilder()
                .ConfigureServices(services => services.AddHostedService(_ => reconciler))
                .Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            await host.StartAsync().ConfigureAwait(false);
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
                while (DateTimeOffset.UtcNow < deadline && !lifetime.ApplicationStopping.IsCancellationRequested &&
                    (await ReadSchedulingStateAsync(healthyArtifactId).ConfigureAwait(false)).ObjectVerificationToken
                        is not null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }

                lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse(
                    $"the hosted reconciliation loop must not stop the host (ExecuteTask: {reconciler.ExecuteTask?.Status}, " +
                    $"{reconciler.ExecuteTask?.Exception?.GetBaseException().GetType().Name}: " +
                    $"{reconciler.ExecuteTask?.Exception?.GetBaseException().Message})");
                reconciler.ExecuteTask.Should().NotBeNull();
                reconciler.ExecuteTask!.IsCompleted.Should().BeFalse("the loop keeps running after the cycle");
                (await ReadSchedulingStateAsync(healthyArtifactId).ConfigureAwait(false)).Should()
                    .Match<SchedulingState>(state => state.ObjectState == CentralArtifactObjectState.Available &&
                        state.ObjectVerificationToken == null, "the cycle reaches the next artifact");
                var backedOff = await ReadSchedulingStateAsync(stale.CentralArtifactId).ConfigureAwait(false);
                backedOff.ObjectVerificationRetryCount.Should().Be(1);
                backedOff.ObjectVerificationRetryAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
            }
            finally
            {
                await host.StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await ClearStaleReconciliationStateAsync(stale.CentralArtifactId).ConfigureAwait(false);
            await RetireStaleGraphCameraAsync(stale).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Reconciliation_UntaggedJobStateFailureFromSchedulingIsNotContained()
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("untagged-job-state-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 1, 4, 1, 4 };
        var manifest = CreateManifestV2(deviceId, rig, payload, 5221);
        using (var client = fixture.Factory.CreateClient())
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
            using var accepted = await PostAsync(client, manifest, payload).ConfigureAwait(false);
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        Guid centralArtifactId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await db.CentralArtifacts
                .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
            artifact.ObjectState = CentralArtifactObjectState.Pending;
            artifact.ObjectVerificationToken = Guid.NewGuid();
            artifact.ObjectVerificationRequestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync().ConfigureAwait(false);
            centralArtifactId = artifact.Id;
        }
        var injection = new SchedulerFaultInjection(
            manifest.Descriptor.Artifact.ArtifactId,
            static _ => new CentralDerivativeJobStateException("The derivative job lease is stale or invalid."));
        using var faultFactory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICentralDerivativeJobScheduler>();
            services.AddScoped<CentralDerivativeJobScheduler>();
            services.AddScoped<ICentralDerivativeJobScheduler>(provider => new FaultInjectingScheduler(
                provider.GetRequiredService<CentralDerivativeJobScheduler>(), injection));
        }));
        try
        {
            using var telemetry = new CentralIngestTelemetry();
            var reconciler = new CentralArtifactReconciliationService(
                faultFactory.Services.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                telemetry,
                new RecordingLogger<CentralArtifactReconciliationService>());

            Func<Task> reconcile = () => reconciler.ReconcileAsync(CancellationToken.None);

            // Containment is decided by CentralProcessingGraphPlanVerification.IsUnexpandableRevision alone; any other
            // job-state failure keeps the behavior it had before the unexpandable-revision containment existed.
            await reconcile.Should().ThrowExactlyAsync<CentralDerivativeJobStateException>()
                .WithMessage("The derivative job lease is stale or invalid.").ConfigureAwait(false);
            injection.InjectionCount.Should().Be(1);
        }
        finally
        {
            await ClearStaleReconciliationStateAsync(centralArtifactId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Older than every other test's sources (several use the Unix epoch), so these tests' sources are the oldest
    /// retrospective candidates in the shared database and lead every batch.
    /// </summary>
    private static readonly DateTimeOffset RetrospectiveCandidateEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DoNotParallelize]
    public async Task RetrospectiveTransientScheduling_DefersAStaleGraphSourceAndSchedulesTheRestOfTheBatch()
    {
        var fixture = AssemblyHooks.Fixture;
        var stale = await SeedStaleAssignmentArtifactAsync("stale-graph-retrospective", captureSequence: 5203)
            .ConfigureAwait(false);
        var healthyArtifactId = await SeedHealthyPendingArtifactAsync("stale-graph-retrospective-healthy", 5204)
            .ConfigureAwait(false);
        var restoreReceivedAt = new Dictionary<Guid, DateTimeOffset>();
        try
        {
            // Retrospective candidates are verified sources without a current transient job, oldest first; make these
            // two the oldest, the stale one ahead of the healthy one.
            await ClearStaleReconciliationStateAsync(stale.CentralArtifactId).ConfigureAwait(false);
            await ClearStaleReconciliationStateAsync(healthyArtifactId).ConfigureAwait(false);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                foreach (var (artifactId, offset) in new[] { (stale.CentralArtifactId, 1), (healthyArtifactId, 2) })
                {
                    var artifact = await db.CentralArtifacts.SingleAsync(item => item.Id == artifactId)
                        .ConfigureAwait(false);
                    artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
                    restoreReceivedAt[artifactId] = artifact.ReceivedAtUtc;
                    artifact.ReceivedAtUtc = RetrospectiveCandidateEpoch.AddMinutes(offset);
                }
                await db.SaveChangesAsync().ConfigureAwait(false);
            }

            var options = new CentralTransientOptions { Mode = TransientDetectorExecutionMode.Central };
            var logger = new RecordingLogger<CentralTransientRetrospectiveScheduler>();
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var scheduler = new BatchRecordingScheduler(
                    scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>(),
                    [stale.CentralArtifactId, healthyArtifactId]);
                var retrospective = new CentralTransientRetrospectiveScheduler(
                    db,
                    scheduler,
                    new CentralDerivativeRecipeCatalog(options),
                    Options.Create(options),
                    scope.ServiceProvider.GetRequiredService<CentralDerivativeWorkerTelemetry>(),
                    logger);

                var batch = () => retrospective.ScheduleBatchAsync(DateTimeOffset.UtcNow, CancellationToken.None);

                await batch.Should().NotThrowAsync(
                    "an unexpandable graph revision is one source's scheduling failure, not the batch's")
                    .ConfigureAwait(false);
                scheduler.Attempted.Should().Equal(stale.CentralArtifactId, healthyArtifactId);
                scheduler.Completed.Should().Equal(healthyArtifactId);
            }
            (await CountGraphExecutionsAsync(stale.CentralArtifactId).ConfigureAwait(false)).Should().Be(0);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralDerivativeJobs
                    .CountAsync(job => job.SourceCentralArtifactId == stale.CentralArtifactId).ConfigureAwait(false))
                    .Should().Be(0, "nothing is written for a source whose revision cannot be expanded");
            }
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error &&
                entry.Message.Contains(stale.AssignmentId.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains(stale.RevisionId.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains(BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal));
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var (artifactId, receivedAt) in restoreReceivedAt)
            {
                await db.CentralArtifacts.Where(item => item.Id == artifactId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReceivedAtUtc, receivedAt))
                    .ConfigureAwait(false);
            }
            await ClearStaleReconciliationStateAsync(stale.CentralArtifactId).ConfigureAwait(false);
            await ClearStaleReconciliationStateAsync(healthyArtifactId).ConfigureAwait(false);
            await RetireStaleGraphCameraAsync(stale).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task RetrospectiveTransientScheduling_ExcludesAStaleCameraFillingTheBatchAndReachesTheHealthySource()
    {
        var fixture = AssemblyHooks.Fixture;
        // More stale sources than the batch holds, every one older than the healthy source.
        var stale = await SeedStaleAssignmentArtifactsAsync("stale-graph-retrospective-full", [5231, 5232, 5233])
            .ConfigureAwait(false);
        var healthyArtifactId = await SeedHealthyPendingArtifactAsync("stale-graph-retrospective-full-healthy", 5234)
            .ConfigureAwait(false);
        var ordered = stale.Select(static item => item.CentralArtifactId).Append(healthyArtifactId).ToArray();
        var restoreReceivedAt = new Dictionary<Guid, DateTimeOffset>();
        try
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                for (var index = 0; index < ordered.Length; index++)
                {
                    await ClearStaleReconciliationStateAsync(ordered[index]).ConfigureAwait(false);
                    var artifactId = ordered[index];
                    var artifact = await db.CentralArtifacts.SingleAsync(item => item.Id == artifactId)
                        .ConfigureAwait(false);
                    artifact.ReconstructionState.Should().Be(CentralReconstructionState.Complete);
                    restoreReceivedAt[artifactId] = artifact.ReceivedAtUtc;
                    artifact.ReceivedAtUtc = RetrospectiveCandidateEpoch.AddMinutes(index + 1);
                }
                await db.SaveChangesAsync().ConfigureAwait(false);
            }

            var options = new CentralTransientOptions { Mode = TransientDetectorExecutionMode.Central };
            var logger = new RecordingLogger<CentralTransientRetrospectiveScheduler>();
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var scheduler = new BatchRecordingScheduler(
                    scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>(), ordered);
                var retrospective = new CentralTransientRetrospectiveScheduler(
                    db,
                    scheduler,
                    new CentralDerivativeRecipeCatalog(options),
                    Options.Create(options),
                    scope.ServiceProvider.GetRequiredService<CentralDerivativeWorkerTelemetry>(),
                    logger)
                {
                    // Test seam: two candidates per query, so three stale sources overfill the first batch.
                    CandidateBatchSize = 2
                };

                await retrospective.ScheduleBatchAsync(DateTimeOffset.UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);

                // The first batch is the two oldest stale sources: the first fails verification and names the camera,
                // the second is deferred without another attempt. The re-query excludes the camera, so its third
                // source never returns and the healthy source is reached in the same pass.
                scheduler.Attempted.Should().Equal(stale[0].CentralArtifactId, healthyArtifactId);
                scheduler.Completed.Should().Equal(healthyArtifactId);
            }
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var staleIds = stale.Select(static item => item.CentralArtifactId).ToArray();
                (await db.CentralDerivativeJobs.CountAsync(job => staleIds.Contains(job.SourceCentralArtifactId))
                    .ConfigureAwait(false)).Should().Be(0, "nothing is written for a source whose revision cannot be expanded");
                (await db.CentralProcessingGraphExecutions.CountAsync(execution =>
                        staleIds.Contains(execution.AnchorSourceCentralArtifactId))
                    .ConfigureAwait(false)).Should().Be(0);
                (await db.CentralDerivativeJobs.CountAsync(job => job.SourceCentralArtifactId == healthyArtifactId)
                    .ConfigureAwait(false)).Should().BePositive("the healthy source behind the stale camera is scheduled");
            }
            logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error &&
                entry.Message.Contains("deferred 2 source artifacts", StringComparison.Ordinal) &&
                entry.Message.Contains(stale[0].AssignmentId.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains(stale[0].RevisionId.ToString(), StringComparison.Ordinal));
            logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning,
                "one re-query reaches the healthy source, far below the per-pass limit");
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var (artifactId, receivedAt) in restoreReceivedAt)
            {
                await db.CentralArtifacts.Where(item => item.Id == artifactId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReceivedAtUtc, receivedAt))
                    .ConfigureAwait(false);
            }
            foreach (var artifactId in ordered)
            {
                await ClearStaleReconciliationStateAsync(artifactId).ConfigureAwait(false);
            }
            await RetireStaleGraphCameraAsync(stale[0]).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Forwards retrospective scheduling only for this test's sources, so a batch over the shared database schedules
    /// nothing for other tests' artifacts, and records the order in which the batch reached them.
    /// </summary>
    private sealed class BatchRecordingScheduler(ICentralDerivativeJobScheduler inner, IReadOnlyCollection<Guid> forwarded)
        : ICentralDerivativeJobScheduler
    {
        private readonly HashSet<Guid> forwardedIds = [.. forwarded];

        public List<Guid> Attempted { get; } = [];

        public List<Guid> Completed { get; } = [];

        public Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException("Retrospective scheduling uses durable artifact identities.");

        public async Task EnsureRequiredJobsAsync(
            Guid devicePublicId,
            Guid artifactId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var centralArtifactId = await ResolveAsync(devicePublicId, artifactId).ConfigureAwait(false);
            if (!forwardedIds.Contains(centralArtifactId))
            {
                return;
            }
            Attempted.Add(centralArtifactId);
            await inner.EnsureRequiredJobsAsync(devicePublicId, artifactId, now, cancellationToken).ConfigureAwait(false);
            Completed.Add(centralArtifactId);
        }

        public Task<Guid?> EnsureTransientContextConvergenceAsync(
            Guid provisionalCentralDerivativeJobId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
            => Task.FromResult<Guid?>(null);

        private static async Task<Guid> ResolveAsync(Guid devicePublicId, Guid artifactId)
        {
            await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
                .AsNoTracking()
                .Where(item => item.DevicePublicId == devicePublicId && item.ArtifactId == artifactId)
                .Select(item => item.Id)
                .SingleAsync().ConfigureAwait(false);
        }
    }

    private sealed record StaleAssignmentArtifact(
        Guid CentralArtifactId,
        Guid ObservatoryId,
        Guid LogicalCameraId,
        Guid RevisionId,
        Guid AssignmentId,
        string OwnerUserId);

    private sealed record SchedulingState(
        CentralArtifactObjectState ObjectState,
        string? StateReasonCode,
        Guid? ObjectVerificationToken,
        int ObjectVerificationRetryCount,
        DateTimeOffset? ObjectVerificationRetryAtUtc);

    /// <summary>
    /// An installed camera assigned, at camera scope, a published revision of the canonical graph whose Annotation
    /// node embeds an earlier recipe <c>ImplementationVersion</c>, with identities computed as publication would have
    /// computed them then. Its raw is ingested (HTTP 500, committed without derivatives) and then left as a stranded
    /// verification reservation, the state reconciliation adopts.
    /// </summary>
    private static async Task<StaleAssignmentArtifact> SeedStaleAssignmentArtifactAsync(
        string name, long captureSequence)
        => (await SeedStaleAssignmentArtifactsAsync(name, [captureSequence]).ConfigureAwait(false))[0];

    /// <summary>Several raws from the one stale camera, ingested in <paramref name="captureSequences"/> order.</summary>
    private static async Task<IReadOnlyList<StaleAssignmentArtifact>> SeedStaleAssignmentArtifactsAsync(
        string name, IReadOnlyList<long> captureSequences)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig($"{name}-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        StaleAssignmentArtifact seeded;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingGraphNodeRegistry>();
            var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId)
                .ConfigureAwait(false);
            var camera = new LogicalCamera
            {
                ObservatoryId = registration.ObservatoryId,
                Slug = $"{name}-{Guid.NewGuid():N}",
                Name = "Stale Graph Camera",
                Description = "Camera assigned a revision this binary cannot expand",
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
                AssignmentReasonCode = "stale-graph-test"
            };
            camera.Installations.Add(installation);
            var definitionJson = WithStaleAnnotationImplementationVersion(Encoding.UTF8.GetString(
                ProcessingGraphJson.SerializeCanonical(DatabaseSeeder.CreateBasicCentralProcessingGraph() with
                {
                    Name = $"{name}-{Guid.NewGuid():N}"
                })));
            var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(definitionJson));
            parsed.IsValid.Should().BeTrue();
            var portable = ProcessingGraphCompiler.Compile(parsed.Definition!);
            portable.IsValid.Should().BeTrue(string.Join(Environment.NewLine, portable.Diagnostics));
            var central = LogicHostProcessingGraphAdapter.Compile(parsed.Definition!, registry.Capabilities);
            central.IsValid.Should().BeTrue(string.Join(Environment.NewLine, central.Diagnostics));
            registry.FindUnsupported(central.Plan!).Should().Be(BuiltInProcessingRecipes.Annotation,
                "only the node registry can reject this revision, exactly as after an ImplementationVersion bump");
            var revision = new CentralProcessingGraphRevision
            {
                Name = parsed.Definition!.Name,
                Revision = parsed.Definition.Revision,
                DefinitionJson = definitionJson,
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
                ReasonCode = "stale-graph-test"
            };
            revision.Assignments.Add(assignment);
            db.AddRange(camera, installation, revision, assignment);
            await db.SaveChangesAsync().ConfigureAwait(false);
            seeded = new(Guid.Empty, registration.ObservatoryId, camera.Id, revision.Id, assignment.Id,
                registration.OwnerUserId);
        }

        var results = new List<StaleAssignmentArtifact>();
        for (var index = 0; index < captureSequences.Count; index++)
        {
            var payload = new byte[] { 3, 1, 4, (byte)(1 + index) };
            var manifest = CreateManifestV2(deviceId, rig, payload, captureSequences[index]);
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
            using (var response = await PostAsync(client, manifest, payload).ConfigureAwait(false))
            {
                response.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
                    "live scheduling cannot expand the assigned revision ({0})",
                    await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }

            await using var markScope = fixture.Factory.Services.CreateAsyncScope();
            var markDb = markScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var artifact = await markDb.CentralArtifacts.Include(item => item.Frame)
                .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
            artifact.Frame!.LogicalCameraInstallationId.Should().NotBeNull("ingest binds the installed camera");
            artifact.ObjectState.Should().Be(CentralArtifactObjectState.Available,
                "the upload committed before scheduling");
            (await markDb.CentralDerivativeJobs.CountAsync(job => job.SourceCentralArtifactId == artifact.Id)
                .ConfigureAwait(false)).Should().Be(0);
            // Earlier than the healthy artifact, so the stale one is attempted first within the cycle.
            artifact.ObjectState = CentralArtifactObjectState.Pending;
            artifact.ObjectVerificationToken = Guid.NewGuid();
            artifact.ObjectVerificationRequestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
            await markDb.SaveChangesAsync().ConfigureAwait(false);
            results.Add(seeded with { CentralArtifactId = artifact.Id });
        }
        return results;
    }

    /// <summary>A raw from a device without a camera installation (no graph applies), left as a stranded verification.</summary>
    private static async Task<Guid> SeedHealthyPendingArtifactAsync(string name, long captureSequence)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig($"{name}-rig");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        var payload = new byte[] { 2, 7, 1, 8 };
        var manifest = CreateManifestV2(deviceId, rig, payload, captureSequence);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        using (var response = await PostAsync(client, manifest, payload).ConfigureAwait(false))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted,
                await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifact = await db.CentralArtifacts
            .SingleAsync(item => item.ArtifactId == manifest.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
        artifact.ObjectState = CentralArtifactObjectState.Pending;
        artifact.ObjectVerificationToken = Guid.NewGuid();
        artifact.ObjectVerificationRequestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return artifact.Id;
    }

    /// <summary>The operator correction: a later camera-scoped assignment of a revision published on current versions.</summary>
    private static async Task ReassignToCurrentSeedRevisionAsync(StaleAssignmentArtifact stale)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        db.CentralProcessingGraphAssignments.Add(new CentralProcessingGraphAssignment
        {
            RevisionId = CanonicalCentralGraphSeedChain.Current.RevisionId,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = stale.ObservatoryId,
            LogicalCameraId = stale.LogicalCameraId,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            ActorUserId = stale.OwnerUserId,
            ReasonCode = "stale-graph-reassigned"
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<SchedulingState> ReadSchedulingStateAsync(Guid centralArtifactId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts.AsNoTracking()
            .Where(item => item.Id == centralArtifactId)
            .Select(item => new SchedulingState(
                item.ObjectState,
                item.StateReasonCode,
                item.ObjectVerificationToken,
                item.ObjectVerificationRetryCount,
                item.ObjectVerificationRetryAtUtc))
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task MakeSchedulingRetryDueAsync(Guid centralArtifactId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var due = DateTimeOffset.UtcNow.AddSeconds(-1);
        (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
            .Where(item => item.Id == centralArtifactId && item.ObjectVerificationToken != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ObjectVerificationRetryAtUtc, due))
            .ConfigureAwait(false)).Should().Be(1);
    }

    private static async Task<Guid?> ReadRecoveryLeaseTokenAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralRecoveryCheckpoints
            .AsNoTracking()
            .Where(item => item.Id == CentralRecoveryCheckpoint.SingletonId)
            .Select(item => item.LeaseToken)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<int> CountGraphExecutionsAsync(Guid centralArtifactId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralProcessingGraphExecutions
            .CountAsync(item => item.AnchorSourceCentralArtifactId == centralArtifactId).ConfigureAwait(false);
    }

    /// <summary>
    /// Leaves no adoptable stale reservation or held recovery lease behind for later tests, whichever way this one
    /// ended (an escaped scheduling failure leaves both).
    /// </summary>
    private static async Task ClearStaleReconciliationStateAsync(Guid centralArtifactId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (centralArtifactId != Guid.Empty)
        {
            await db.CentralArtifacts
                .Where(item => item.Id == centralArtifactId && item.ObjectVerificationToken != null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Available)
                    .SetProperty(item => item.ObjectVerificationToken, (Guid?)null)
                    .SetProperty(item => item.ObjectVerificationRequestedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(item => item.ObjectVerificationRetryCount, 0)
                    .SetProperty(item => item.ObjectVerificationRetryAtUtc, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }
        await db.CentralRecoveryCheckpoints
            .Where(item => item.Id == CentralRecoveryCheckpoint.SingletonId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Leaves nothing of the stale camera live for later tests. The fixture's hosted worker is off, so an execution
    /// that recovery expanded never runs: its open jobs are terminalized and the execution superseded, and every
    /// installation of the camera is retired so no later ingest or reconciliation reaches its assignments. A live
    /// expanded execution left behind is converged to Failed by the next test that terminalizes foreign jobs and
    /// starts a worker, concurrently with that worker's own claims.
    /// </summary>
    private static async Task RetireStaleGraphCameraAsync(StaleAssignmentArtifact stale)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var installationIds = db.LogicalCameraInstallations
            .Where(item => item.LogicalCameraId == stale.LogicalCameraId)
            .Select(item => item.Id);
        var executionIds = db.CentralProcessingGraphExecutions
            .Where(item => installationIds.Contains(item.LogicalCameraInstallationId))
            .Select(item => item.Id);
        await db.CentralDerivativeJobs
            .Where(job => job.GraphExecutionId != null && executionIds.Contains(job.GraphExecutionId.Value) &&
                job.Status != CentralDerivativeJobStatus.Completed &&
                job.Status != CentralDerivativeJobStatus.TerminalFailure &&
                job.Status != CentralDerivativeJobStatus.Canceled &&
                job.Status != CentralDerivativeJobStatus.Skipped &&
                job.Status != CentralDerivativeJobStatus.Quarantined &&
                job.Status != CentralDerivativeJobStatus.Superseded)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        // Superseded requires a CompletedAtUtc, and UpdatedAtUtc must not precede ExpandedAtUtc.
        var retiredAtUtc = DateTimeOffset.UtcNow;
        await db.CentralProcessingGraphExecutions
            .Where(item => executionIds.Contains(item.Id) && item.ExpandedAtUtc != null &&
                item.Status != CentralProcessingGraphExecutionStatus.Completed &&
                item.Status != CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures &&
                item.Status != CentralProcessingGraphExecutionStatus.Failed &&
                item.Status != CentralProcessingGraphExecutionStatus.Canceled &&
                item.Status != CentralProcessingGraphExecutionStatus.Superseded)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, CentralProcessingGraphExecutionStatus.Superseded)
                .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)retiredAtUtc)
                .SetProperty(item => item.UpdatedAtUtc, retiredAtUtc))
            .ConfigureAwait(false);
        foreach (var installation in await db.LogicalCameraInstallations
            .Where(item => item.LogicalCameraId == stale.LogicalCameraId && item.RetiredAtUtc == null)
            .ToListAsync().ConfigureAwait(false))
        {
            installation.RetiredAtUtc = retiredAtUtc;
            installation.RetiredByUserId = stale.OwnerUserId;
            installation.RetirementReasonCode = "stale-graph-test-cleanup";
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal static string WithStaleAnnotationImplementationVersion(string definitionJson)
    {
        BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.Annotation, out var annotation)
            .Should().BeTrue();
        var current = $"\"{annotation!.ImplementationVersion}\"";
        definitionJson.Should().Contain(current);
        return definitionJson.Replace(current, "\"projected-annotation-v0\"", StringComparison.Ordinal);
    }
}
