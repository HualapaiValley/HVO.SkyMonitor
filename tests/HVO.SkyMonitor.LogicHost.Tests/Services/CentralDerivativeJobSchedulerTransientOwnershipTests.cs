using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

/// <summary>
/// The seeded <c>logic-host-basic</c> graph is config-independent and carries no transient node, so on
/// <c>CentralTransient:Mode=Central</c> the legacy scheduler must still own exactly the transient recipe for the
/// configured source role while every graph-covered recipe stays graph-owned.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CentralDerivativeJobSchedulerTransientOwnershipTests
{
    [TestMethod]
    [DataRow((int)FrameArtifactRole.Raw)]
    [DataRow((int)FrameArtifactRole.Calibrated)]
    public async Task TransientOnlySchedulingCreatesExactlyOneTransientJobAndIsIdempotent(int sourceRoleValue)
    {
        var sourceRole = (FrameArtifactRole)sourceRoleValue;
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var catalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            SourceRole = sourceRole
        });
        var transientRecipe = catalog.GetTransientRecipe(sourceRole);
        Assert.IsNotNull(transientRecipe);
        Assert.IsNull(catalog.GetTransientRecipe(
            sourceRole == FrameArtifactRole.Raw ? FrameArtifactRole.Calibrated : FrameArtifactRole.Raw));
        var artifact = CreateArtifact(sourceRole);
        context.AddRange(artifact.Frame!, artifact);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var scheduler = new CentralDerivativeJobScheduler(context, catalog, new NoopWindowResolver());

        await scheduler.EnsureRequiredJobsCoreAsync(artifact, now, transientOnly: true, CancellationToken.None)
            .ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        // Re-ingest of the same artifact must not duplicate the transient job.
        await scheduler.EnsureRequiredJobsCoreAsync(artifact, now.AddSeconds(1), transientOnly: true, CancellationToken.None)
            .ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();

        var jobs = await context.CentralDerivativeJobs.AsNoTracking().ToListAsync().ConfigureAwait(false);
        Assert.HasCount(1, jobs);
        var job = jobs.Single();
        Assert.AreEqual(CentralTransientRuntime.RecipeName, job.RecipeName);
        Assert.AreEqual(artifact.Id, job.SourceCentralArtifactId);
        Assert.IsNull(job.GraphExecutionId);
        Assert.AreEqual(CentralDerivativeJobStatus.Waiting, job.Status);
        Assert.AreEqual(CentralDerivativeWaitKind.Window, job.WaitKind);
        Assert.AreEqual(
            CentralDerivativeJobIdentity.CreateRequestIdentity(artifact.DevicePublicId, artifact.ArtifactId, transientRecipe),
            job.RequestIdentitySha256);
        Assert.AreEqual(1, await context.CentralTransientValidationJobs.CountAsync().ConfigureAwait(false));
        Assert.AreEqual(0, await context.CentralDerivativeJobs.CountAsync(candidate =>
            candidate.RecipeName != CentralTransientRuntime.RecipeName).ConfigureAwait(false),
            "no graph-covered legacy recipe (preview, annotation, image quality, cloud, rolling mean, overlay) may be scheduled");
    }

    [TestMethod]
    public async Task TransientOnlySchedulingIsANoOpWhenTheDeploymentHasNoTransientRecipe()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var artifact = CreateArtifact(FrameArtifactRole.Raw);
        context.AddRange(artifact.Frame!, artifact);
        await context.SaveChangesAsync().ConfigureAwait(false);
        foreach (var catalog in new[]
                 {
                     new CentralDerivativeRecipeCatalog(),
                     new CentralDerivativeRecipeCatalog(new CentralTransientOptions
                     {
                         Mode = TransientDetectorExecutionMode.Hybrid,
                         SourceRole = FrameArtifactRole.Raw
                     }),
                     new CentralDerivativeRecipeCatalog(new CentralTransientOptions
                     {
                         Mode = TransientDetectorExecutionMode.Central,
                         SourceRole = FrameArtifactRole.Calibrated
                     })
                 })
        {
            await new CentralDerivativeJobScheduler(context, catalog, new NoopWindowResolver())
                .EnsureRequiredJobsCoreAsync(artifact, now, transientOnly: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        await context.SaveChangesAsync().ConfigureAwait(false);

        Assert.AreEqual(0, await context.CentralDerivativeJobs.CountAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task FullLegacySchedulingStillCoversEveryRequiredRecipe()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var catalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            SourceRole = FrameArtifactRole.Raw
        });
        var artifact = CreateArtifact(FrameArtifactRole.Raw);
        context.AddRange(artifact.Frame!, artifact);
        await context.SaveChangesAsync().ConfigureAwait(false);

        await new CentralDerivativeJobScheduler(context, catalog, new NoopWindowResolver())
            .EnsureRequiredJobsCoreAsync(artifact, now, transientOnly: false, CancellationToken.None)
            .ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var recipes = await context.CentralDerivativeJobs.AsNoTracking()
            .Select(job => job.RecipeName).ToListAsync().ConfigureAwait(false);
        // Cloud assessment needs a clear reference and environmental query; the rest of the catalog is scheduled.
        CollectionAssert.AreEquivalent(
            new[]
            {
                BuiltInProcessingRecipes.EncodedPreview,
                BuiltInProcessingRecipes.Annotation,
                BuiltInProcessingRecipes.ImageQuality,
                BuiltInProcessingRecipes.RollingMean,
                CentralTransientRuntime.RecipeName
            },
            recipes);
    }

    /// <summary>
    /// The graph-covered transient-only branch acquires payload holds and a serializable transaction only when the
    /// policy-resolved legacy transient recipe exists. The in-memory provider cannot take SQL application locks, so
    /// reaching the hold fence is observable as an <see cref="InvalidOperationException"/>.
    /// </summary>
    [TestMethod]
    [DataRow((int)CentralProcessingGraphScheduleOutcome.AwaitingSources)]
    [DataRow((int)CentralProcessingGraphScheduleOutcome.Created)]
    public async Task TransientOnlyBranchSkipsHoldsWhenPolicyOrModeLeavesNoLegacyTransientRecipe(int outcomeValue)
    {
        var outcome = (CentralProcessingGraphScheduleOutcome)outcomeValue;
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var centralCatalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            SourceRole = FrameArtifactRole.Raw
        });
        var hybridCatalog = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Hybrid,
            SourceRole = FrameArtifactRole.Raw
        });
        var graphScheduler = new StubGraphScheduler(new CentralProcessingGraphScheduleResult(outcome));

        async Task<RecordingWindowResolver> RunAsync(
            CentralDerivativeRecipeCatalog catalog,
            ICentralProcessingPolicyService? policy)
        {
            await using var context = CreateContext();
            var artifact = CreateArtifact(FrameArtifactRole.Raw);
            context.AddRange(artifact.Frame!, artifact);
            await context.SaveChangesAsync().ConfigureAwait(false);
            var resolver = new RecordingWindowResolver();
            await new CentralDerivativeJobScheduler(
                    context, catalog, resolver, processingPolicy: policy, graphScheduler: graphScheduler)
                .EnsureRequiredJobsAsync(artifact, now, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, await context.CentralDerivativeJobs.CountAsync().ConfigureAwait(false));
            return resolver;
        }

        // Central mode with validation enabled owns the legacy transient recipe: the branch reaches the hold fence.
        var enabled = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            RunAsync(centralCatalog, new StubProcessingPolicy(centralCatalog, centralValidationEnabled: true)))
            .ConfigureAwait(false);
        // The hold fence asks the relational connection string for sp_getapplock; the in-memory provider has none.
        Assert.Contains("relational database provider", enabled.Message);
        var withoutPolicy = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            RunAsync(centralCatalog, policy: null)).ConfigureAwait(false);
        Assert.Contains("relational database provider", withoutPolicy.Message);
        // An observatory override disabling central validation, or Hybrid mode (whose transient recipe exists only for
        // graph coverage), leaves nothing to schedule: no holds, no transaction, and the window notification the
        // branch used to issue is preserved.
        var disabled = await RunAsync(centralCatalog, new StubProcessingPolicy(centralCatalog, centralValidationEnabled: false))
            .ConfigureAwait(false);
        Assert.HasCount(1, disabled.Affected);
        var hybrid = await RunAsync(hybridCatalog, new StubProcessingPolicy(hybridCatalog, centralValidationEnabled: true))
            .ConfigureAwait(false);
        Assert.HasCount(1, hybrid.Affected);
        var hybridWithoutPolicy = await RunAsync(hybridCatalog, policy: null).ConfigureAwait(false);
        Assert.HasCount(1, hybridWithoutPolicy.Affected);
    }

    /// <summary>
    /// An explicit graph refusal (retired revision without an eligible fallback, or a source expired between
    /// selection and seal) means no graph owns the frame: full legacy scheduling proceeds exactly as for
    /// <c>NotApplicable</c>. The in-memory provider proves the legacy path was entered by failing at its hold fence,
    /// the same signal the transient-only path above relies on; a graph-owned outcome never reaches that fence.
    /// </summary>
    [TestMethod]
    [DataRow((int)CentralProcessingGraphScheduleOutcome.NotApplicable)]
    [DataRow((int)CentralProcessingGraphScheduleOutcome.Invalid)]
    public async Task InvalidLiveOutcomeFallsThroughToFullLegacyScheduling(int outcomeValue)
    {
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var outcome = (CentralProcessingGraphScheduleOutcome)outcomeValue;
        await using var context = CreateContext();
        var artifact = CreateArtifact(FrameArtifactRole.Raw);
        context.AddRange(artifact.Frame!, artifact);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var scheduler = new CentralDerivativeJobScheduler(
            context,
            new CentralDerivativeRecipeCatalog(),
            new RecordingWindowResolver(),
            graphScheduler: new StubGraphScheduler(new CentralProcessingGraphScheduleResult(
                outcome, ReasonCode: outcome == CentralProcessingGraphScheduleOutcome.Invalid ? "revision-retired" : null)));

        var legacyPath = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            scheduler.EnsureRequiredJobsAsync(artifact, now, CancellationToken.None)).ConfigureAwait(false);

        Assert.Contains("relational database provider", legacyPath.Message);
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static CentralArtifact CreateArtifact(FrameArtifactRole role)
    {
        var frame = new CentralFrame
        {
            Id = Guid.NewGuid(),
            FrameId = Guid.NewGuid(),
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = Guid.NewGuid(),
            AgentId = "agent",
            RigId = "rig",
            CaptureSequence = 100,
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            DevicePublicId = frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = role,
            Variant = role == FrameArtifactRole.Calibrated ? "calibrated" : null,
            RecipeVersion = $"{role}-v1",
            ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
            MediaType = "application/octet-stream",
            ByteLength = 1,
            ChecksumSha256 = new string('A', 64),
            StorageReference = $"object://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = frame.CapturedAtUtc,
            CreatedUtc = frame.CapturedAtUtc,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        frame.Artifacts.Add(artifact);
        return artifact;
    }

    private sealed class RecordingWindowResolver : ICentralDerivativeWindowResolver
    {
        public List<Guid> Affected { get; } = [];

        public Task ResolveAffectedAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Affected.Add(artifact.Id);
            return Task.CompletedTask;
        }

        public Task ResolveWaitingAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubGraphScheduler(CentralProcessingGraphScheduleResult result) : ICentralProcessingGraphScheduler
    {
        public Task<CentralProcessingGraphScheduleResult> ScheduleLiveAsync(
            Guid centralArtifactId, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
            CentralProcessingGraphReplayRequest request, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ConvergeAsync(Guid executionId, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ConvergeBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubProcessingPolicy(
        ICentralDerivativeRecipeCatalog catalog,
        bool centralValidationEnabled) : ICentralProcessingPolicyService
    {
        public Task<CentralProcessingPolicySummary?> GetAsync(
            Guid observatoryId, string actorUserId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CentralProcessingPolicyMutationOutcome> SetAsync(
            Guid observatoryId,
            string actorUserId,
            CentralProcessingPolicyRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CentralDerivativeRecipe>> ResolveRequiredRecipesAsync(
            Guid observatoryId, FrameArtifactRole sourceRole, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CentralDerivativeRecipe>>(centralValidationEnabled
                ? catalog.GetRequiredRecipes(sourceRole)
                : catalog.GetRequiredRecipes(sourceRole).Where(recipe => recipe.Transient is null).ToArray());

        public Task<CentralDerivativeRecipe?> ResolveTransientRecipeAsync(
            Guid observatoryId, FrameArtifactRole sourceRole, CancellationToken cancellationToken)
            => Task.FromResult(centralValidationEnabled ? catalog.GetTransientRecipe(sourceRole) : null);
    }

    private sealed class NoopWindowResolver : ICentralDerivativeWindowResolver
    {
        public Task ResolveAffectedAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveWaitingAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ResolveAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
