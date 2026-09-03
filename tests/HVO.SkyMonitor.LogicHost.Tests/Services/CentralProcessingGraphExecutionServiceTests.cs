using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Reflection;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralProcessingGraphExecutionServiceTests
{
    [TestMethod]
    public void ControllerUsesCanonicalReadAndWritePoliciesPerRoute()
    {
        var controller = typeof(ProcessingGraphExecutionsController);

        Assert.IsNull(controller.GetCustomAttribute<AuthorizeAttribute>());
        Assert.AreEqual(
            AuthorizationPolicyNames.ApiKeyReadWrite,
            controller.GetMethod(nameof(ProcessingGraphExecutionsController.ScheduleAsync))!
                .GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.AreEqual(
            AuthorizationPolicyNames.ApiKeyRead,
            controller.GetMethod(nameof(ProcessingGraphExecutionsController.ListAsync))!
                .GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.AreEqual(
            AuthorizationPolicyNames.ApiKeyRead,
            controller.GetMethod(nameof(ProcessingGraphExecutionsController.GetAsync))!
                .GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.AreEqual(
            AuthorizationPolicyNames.ApiKeyReadWrite,
            controller.GetMethod(nameof(ProcessingGraphExecutionsController.CancelAsync))!
                .GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [TestMethod]
    public async Task ReplayResolvesAuthorizedExternalArtifactIdsToUnambiguousCentralIds()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var now = DateTimeOffset.UtcNow;
        const string ActorId = "operator";
        var actor = new ApplicationUser { Id = ActorId, UserName = ActorId, AccountType = AccountType.User };
        var observatory = CreateObservatory(ActorId, "allowed", now);
        var execution = CreateExecution(observatory.Id, now);
        context.AddRange(actor, observatory, execution.AnchorSourceArtifact!, execution.AnchorSourceArtifact!.Frame!);
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            User = actor,
            UserId = actor.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = now
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
        var scheduler = new CapturingScheduler();
        var service = new CentralProcessingGraphExecutionService(
            context, scheduler, new CentralProcessingGraphConvergenceSignal());

        _ = await service.ScheduleReplayAsync(
            Guid.NewGuid(), [execution.AnchorSourceArtifactId], ActorId, null, "replay", "test", now,
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(scheduler.Request);
        Assert.AreEqual(execution.AnchorSourceCentralArtifactId, scheduler.Request.SourceCentralArtifactIds.Single());

        var invalidSources = new IReadOnlyList<Guid>[]
        {
            [],
            [execution.AnchorSourceArtifactId, execution.AnchorSourceArtifactId],
            [Guid.NewGuid()]
        };
        foreach (var sources in invalidSources)
        {
            var invalid = await service.ScheduleReplayAsync(
                Guid.NewGuid(), sources, ActorId, null, "invalid", "test", now,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Invalid, invalid.Outcome);
            Assert.AreEqual("sources-not-found-or-denied", invalid.ReasonCode);
        }
        var blankActor = await service.ScheduleReplayAsync(
            Guid.NewGuid(), [execution.AnchorSourceArtifactId], " ", null, "invalid", "test", now,
            CancellationToken.None).ConfigureAwait(false);
        var wrongScope = await service.ScheduleReplayAsync(
            Guid.NewGuid(), [execution.AnchorSourceArtifactId], ActorId, Guid.NewGuid(), "invalid", "test", now,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Invalid, blankActor.Outcome);
        Assert.AreEqual(CentralProcessingGraphScheduleOutcome.Invalid, wrongScope.Outcome);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => service.ScheduleReplayAsync(
            Guid.NewGuid(), null!, ActorId, null, "invalid", "test", now,
            CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReadAndCancellationRemainMembershipAndCredentialScopeBounded()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var now = DateTimeOffset.UtcNow;
        const string ActorId = "operator";
        var actor = new ApplicationUser { Id = ActorId, UserName = ActorId, AccountType = AccountType.User };
        var allowedObservatory = CreateObservatory(ActorId, "allowed", now);
        var deniedObservatory = CreateObservatory("different-owner", "denied", now);
        var allowed = CreateExecution(allowedObservatory.Id, now);
        var denied = CreateExecution(deniedObservatory.Id, now);
        AddExecutionDetail(allowed, now);
        var terminal = CreateExecution(allowedObservatory.Id, now.AddSeconds(1));
        context.AddRange(actor, allowedObservatory, deniedObservatory, allowed, denied, terminal);
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = allowedObservatory,
            User = actor,
            UserId = actor.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = now
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
        allowed.Status = CentralProcessingGraphExecutionStatus.Running;
        allowed.ExpandedAtUtc = now;
        allowed.StartedAtUtc = now;
        denied.Status = CentralProcessingGraphExecutionStatus.Running;
        denied.ExpandedAtUtc = now;
        denied.StartedAtUtc = now;
        terminal.Status = CentralProcessingGraphExecutionStatus.Running;
        terminal.ExpandedAtUtc = now.AddSeconds(1);
        terminal.StartedAtUtc = now.AddSeconds(1);
        var boundOutput = allowed.Jobs.Single().Outputs.Single();
        boundOutput.ResultCentralArtifactId = allowed.AnchorSourceCentralArtifactId;
        boundOutput.ResultArtifact = allowed.AnchorSourceArtifact;
        boundOutput.ResultOutputIdentitySha256 = new string('F', 64);
        boundOutput.BoundAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        terminal.Status = CentralProcessingGraphExecutionStatus.Completed;
        terminal.CompletedAtUtc = now.AddSeconds(2);
        terminal.UpdatedAtUtc = now.AddSeconds(2);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var signal = new CentralProcessingGraphConvergenceSignal();
        var service = new CentralProcessingGraphExecutionService(context, new UnusedScheduler(), signal);

        var visible = await service.ListAsync(ActorId, null, 100, CancellationToken.None).ConfigureAwait(false);
        var allowedDetail = await service.GetAsync(allowed.Id, ActorId, allowedObservatory.Id, CancellationToken.None)
            .ConfigureAwait(false);
        var deniedDetail = await service.GetAsync(denied.Id, ActorId, null, CancellationToken.None).ConfigureAwait(false);
        var deniedCancellation = await service.CancelAsync(
            denied.Id, ActorId, null, now, CancellationToken.None).ConfigureAwait(false);
        var allowedCancellation = await service.CancelAsync(
            allowed.Id, ActorId, allowedObservatory.Id, now, CancellationToken.None).ConfigureAwait(false);
        var duplicateCancellation = await service.CancelAsync(
            allowed.Id, ActorId, allowedObservatory.Id, now, CancellationToken.None).ConfigureAwait(false);
        var terminalCancellation = await service.CancelAsync(
            terminal.Id, ActorId, allowedObservatory.Id, now, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(2, visible);
        var summary = visible.Single(item => item.Id == allowed.Id);
        Assert.AreEqual(CentralProcessingGraphExecutionClass.Replay.ToString(), summary.ExecutionClass);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Running.ToString(), summary.Status);
        Assert.AreEqual(allowed.RevisionId, summary.RevisionId);
        Assert.AreEqual(allowed.ObservatoryId, summary.ObservatoryId);
        Assert.AreEqual(allowed.LogicalCameraId, summary.LogicalCameraId);
        Assert.AreEqual(allowed.InstallationPublicId, summary.InstallationPublicId);
        Assert.AreEqual(allowed.AnchorSourceArtifactId, summary.AnchorSourceArtifactId);
        Assert.AreEqual(allowed.Trigger.ToString(), summary.Trigger);
        Assert.AreEqual(allowed.ActorId, summary.ActorId);
        Assert.AreEqual(allowed.ReasonCode, summary.ReasonCode);
        Assert.AreEqual(1, summary.SourceCount);
        Assert.AreEqual(1, summary.NodeCount);
        Assert.AreEqual(allowed.CreatedAtUtc, summary.CreatedAtUtc);
        Assert.AreEqual(allowed.UpdatedAtUtc, summary.UpdatedAtUtc);
        Assert.AreEqual(allowed.CompletedAtUtc, summary.CompletedAtUtc);
        Assert.IsNotNull(allowedDetail);
        Assert.AreEqual(summary.Id, allowedDetail.Execution.Id);
        var source = allowedDetail.Sources.Single();
        Assert.AreEqual(0, source.Ordinal);
        Assert.AreEqual("$raw", source.SourceId);
        Assert.AreEqual(allowed.AnchorSourceArtifactId, source.ArtifactId);
        Assert.AreEqual(allowed.AnchorSourceChecksumSha256, source.ArtifactChecksumSha256);
        Assert.AreEqual(allowed.AnchorSourceArtifact!.ByteLength, source.ArtifactByteLength);
        Assert.AreEqual(now, source.SelectedAtUtc);
        var node = allowedDetail.Nodes.Single();
        Assert.AreEqual(0, node.Ordinal);
        Assert.AreEqual("Preview", node.NodeId);
        Assert.AreEqual(CentralDerivativeJobStatus.Completed.ToString(), node.Status);
        Assert.AreEqual(ProcessingGraphNodeFailurePolicy.Required.ToString(), node.FailurePolicy);
        Assert.AreEqual("completed", node.ReasonCode);
        Assert.AreEqual(1, node.AttemptCount);
        Assert.AreEqual(1, node.InputCount);
        Assert.AreEqual(1, node.RequiredInputCount);
        Assert.AreEqual(1, node.ResolvedInputCount);
        var output = node.Outputs.Single();
        Assert.AreEqual(0, output.Ordinal);
        Assert.AreEqual(FrameArtifactRole.Preview.ToString(), output.Role);
        Assert.AreEqual("preview", output.Variant);
        Assert.AreEqual(ProcessingProductKind.PixelData.ToString(), output.ProductKind);
        Assert.AreEqual(allowed.AnchorSourceArtifactId, output.ArtifactId);
        Assert.AreEqual(new string('F', 64), output.OutputIdentitySha256);
        Assert.AreEqual(now, output.BoundAtUtc);
        Assert.IsNull(deniedDetail);
        Assert.AreEqual(CentralProcessingGraphCancellationOutcome.NotFoundOrDenied, deniedCancellation);
        Assert.AreEqual(CentralProcessingGraphCancellationOutcome.Applied, allowedCancellation);
        Assert.AreEqual(CentralProcessingGraphCancellationOutcome.Unchanged, duplicateCancellation);
        Assert.AreEqual(CentralProcessingGraphCancellationOutcome.Unchanged, terminalCancellation);
        Assert.IsTrue(signal.TryRead(out var signaledExecutionId));
        Assert.AreEqual(allowed.Id, signaledExecutionId);
    }

    [TestMethod]
    public async Task CancellationRecordsRequestingActorOnNonterminalNodesWithoutChangingTheirStatus()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var now = DateTimeOffset.UtcNow;
        const string ActorId = "operator";
        var actor = new ApplicationUser { Id = ActorId, UserName = ActorId, AccountType = AccountType.User };
        var observatory = CreateObservatory(ActorId, "allowed", now);
        var execution = CreateExecution(observatory.Id, now);
        AddExecutionDetail(execution, now);
        var completed = execution.Jobs.Single();
        var leased = new CentralDerivativeJob
        {
            GraphExecution = execution,
            GraphExecutionId = execution.Id,
            GraphNodeId = "Leased",
            GraphNodeOrdinal = 1,
            SharedNodePlanIdentitySha256 = new string('E', 64),
            FrozenNodePlanJson = "{}",
            GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Required,
            SourceArtifact = execution.AnchorSourceArtifact,
            SourceCentralArtifactId = execution.AnchorSourceCentralArtifactId,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "preview-v1",
            TargetVariant = "leased-preview",
            RecipeName = "preview",
            RequestedRecipeIdentitySha256 = new string('C', 64),
            ExpectedRecipeIdentitySha256 = new string('C', 64),
            RequestIdentitySha256 = new string('1', 64),
            Status = CentralDerivativeJobStatus.Leased,
            AttemptCount = 1,
            MaxAttempts = 3,
            LeaseOwner = "worker",
            LeaseToken = Guid.NewGuid(),
            LeaseAcquiredAtUtc = now,
            LeaseExpiresAtUtc = now.AddMinutes(1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        execution.Jobs.Add(leased);
        execution.ExpectedNodeCount = 2;
        context.AddRange(actor, observatory, execution);
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = observatory.Id,
            Observatory = observatory,
            User = actor,
            UserId = actor.Id,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = now
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.ExpandedAtUtc = now;
        execution.StartedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
        var service = new CentralProcessingGraphExecutionService(
            context, new UnusedScheduler(), new CentralProcessingGraphConvergenceSignal());

        var outcome = await service.CancelAsync(
            execution.Id, ActorId, observatory.Id, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphCancellationOutcome.Applied, outcome);
        var persistedLeased = await context.CentralDerivativeJobs.AsNoTracking()
            .SingleAsync(job => job.Id == leased.Id).ConfigureAwait(false);
        // Only CentralDerivativeJob lease/convergence may finalize the node; cancellation records the actor only.
        Assert.AreEqual(CentralDerivativeJobStatus.Leased, persistedLeased.Status);
        Assert.AreEqual("worker", persistedLeased.LeaseOwner);
        Assert.AreEqual(ActorId, persistedLeased.CancellationRequestedBy);
        Assert.AreEqual(now.AddSeconds(1), persistedLeased.CancellationRequestedAtUtc);
        var persistedCompleted = await context.CentralDerivativeJobs.AsNoTracking()
            .SingleAsync(job => job.Id == completed.Id).ConfigureAwait(false);
        Assert.IsNull(persistedCompleted.CancellationRequestedBy);
        Assert.IsNull(persistedCompleted.CancellationRequestedAtUtc);
    }

    private static Observatory CreateObservatory(string ownerId, string name, DateTimeOffset now) => new()
    {
        OwnerUserId = ownerId,
        Name = name,
        CreatedAtUtc = now
    };

    private static CentralProcessingGraphExecution CreateExecution(Guid observatoryId, DateTimeOffset now)
    {
        var frame = new CentralFrame
        {
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = observatoryId,
            AgentId = "agent",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = now,
            FirstReceivedAtUtc = now
        };
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            DevicePublicId = frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Raw,
            Variant = "raw",
            RecipeVersion = "raw-v1",
            ManifestSchemaVersion = "v1",
            MediaType = "application/octet-stream",
            ByteLength = 1,
            ChecksumSha256 = new string('A', 64),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = now,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        return new CentralProcessingGraphExecution
        {
            ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
            Status = CentralProcessingGraphExecutionStatus.Pending,
            RequestIdentitySha256 = new string('B', 64),
            RevisionId = Guid.NewGuid(),
            DefinitionIdentitySha256 = new string('C', 64),
            FrozenDefinitionJson = "{}",
            CentralPlanIdentitySha256 = new string('D', 64),
            FrozenCentralPlanJson = "{}",
            ObservatoryId = observatoryId,
            LogicalCameraId = Guid.NewGuid(),
            LogicalCameraInstallationId = Guid.NewGuid(),
            InstallationPublicId = Guid.NewGuid(),
            AnchorSourceArtifact = artifact,
            AnchorSourceCentralArtifactId = artifact.Id,
            AnchorSourceArtifactId = artifact.ArtifactId,
            AnchorSourceChecksumSha256 = artifact.ChecksumSha256,
            Trigger = CentralProcessingGraphTrigger.Replay,
            ActorId = "operator",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReasonCode = "test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private static void AddExecutionDetail(CentralProcessingGraphExecution execution, DateTimeOffset now)
    {
        var artifact = execution.AnchorSourceArtifact!;
        execution.ExpectedSourceCount = 1;
        execution.ExpectedNodeCount = 1;
        execution.ExpectedDependencyCount = 0;
        execution.ExpectedOutputCount = 1;
        execution.Sources.Add(new CentralProcessingGraphExecutionSource
        {
            Execution = execution,
            ExecutionId = execution.Id,
            SourceId = "$raw",
            Ordinal = 0,
            CentralArtifactId = artifact.Id,
            Artifact = artifact,
            ArtifactId = artifact.ArtifactId,
            ArtifactChecksumSha256 = artifact.ChecksumSha256,
            ArtifactByteLength = artifact.ByteLength,
            SelectionEvidenceJson = "{}",
            SelectionEvidenceSha256 = new string('E', 64),
            SelectedAtUtc = now
        });
        var job = new CentralDerivativeJob
        {
            GraphExecution = execution,
            GraphExecutionId = execution.Id,
            GraphNodeId = "Preview",
            GraphNodeOrdinal = 0,
            SharedNodePlanIdentitySha256 = new string('E', 64),
            FrozenNodePlanJson = "{}",
            GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Required,
            SourceArtifact = artifact,
            SourceCentralArtifactId = artifact.Id,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "preview-v1",
            TargetVariant = "preview",
            RecipeName = "preview",
            RequestedRecipeIdentitySha256 = new string('C', 64),
            ExpectedRecipeIdentitySha256 = new string('C', 64),
            RequestIdentitySha256 = new string('D', 64),
            Status = CentralDerivativeJobStatus.Completed,
            StateReasonCode = "completed",
            AttemptCount = 1,
            MaxAttempts = 3,
            InputSetIdentitySha256 = new string('E', 64),
            ResolutionCompletedAtUtc = now,
            CompletedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = 0,
            BindingName = "input",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedCentralArtifactId = artifact.Id,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        });
        var output = CentralDerivativeJobOutput.CreateFromFrozenPlan(
            job,
            0,
            new ProcessingGraphProductContract(
                FrameArtifactRole.Preview,
                "preview",
                ProcessingProductKind.PixelData));
        job.Outputs.Add(output);
        execution.Jobs.Add(job);
    }

    private sealed class UnusedScheduler : ICentralProcessingGraphScheduler
    {
        public Task<CentralProcessingGraphScheduleResult> ScheduleLiveAsync(
            Guid centralArtifactId, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
            CentralProcessingGraphReplayRequest request, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ConvergeAsync(Guid executionId, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ConvergeBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class CapturingScheduler : ICentralProcessingGraphScheduler
    {
        public CentralProcessingGraphReplayRequest? Request { get; private set; }

        public Task<CentralProcessingGraphScheduleResult> ScheduleLiveAsync(
            Guid centralArtifactId, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
            CentralProcessingGraphReplayRequest request, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new CentralProcessingGraphScheduleResult(
                CentralProcessingGraphScheduleOutcome.NotApplicable));
        }

        public Task ConvergeAsync(Guid executionId, DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ConvergeBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
