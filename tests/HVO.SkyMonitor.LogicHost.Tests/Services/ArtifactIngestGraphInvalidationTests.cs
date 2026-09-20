using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

/// <summary>
/// <see cref="ArtifactIngestService.InvalidateDependentsAsync(ApplicationDbContext, CentralArtifact, CancellationToken)"/>
/// reopens legacy derivative jobs, but graph-owned nodes carry frozen inputs and live inside an execution lifecycle
/// that never reopens. These tests pin the graph-owned branch: terminal executions stay untouched, live executions
/// terminalize only through legal outcomes, and legacy dependents of the same artifact still take the legacy path.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ArtifactIngestGraphInvalidationTests
{
    [TestMethod]
    public async Task InvalidatingAnchorOfCompletedExecutionLeavesGraphUntouchedAndReopensLegacyDependent()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var source = CreateArtifact();
        var result = CreateArtifact(source.Frame!, FrameArtifactRole.Preview);
        var execution = CreateExecution(source, now);
        var node = AddNode(execution, source, now);
        var legacy = CreateLegacyJob(source, now, CentralDerivativeJobStatus.Completed);
        context.AddRange(result, legacy);
        await PersistExecutionAsync(context, execution, CentralProcessingGraphExecutionStatus.Completed, now, () =>
        {
            SelectInput(context, node, source, now);
            Freeze(node, new string('9', 64), now);
            node.Status = CentralDerivativeJobStatus.Completed;
            node.StateReasonCode = null;
            node.ResultCentralArtifactId = result.Id;
            node.ResultArtifact = result;
            node.CompletedAtUtc = now;
        }).ConfigureAwait(false);
        var signal = new CentralProcessingGraphConvergenceSignal();

        var tracked = await context.CentralArtifacts.SingleAsync(item => item.Id == source.Id).ConfigureAwait(false);
        tracked.ObjectState = CentralArtifactObjectState.Pending;
        tracked.StateReasonCode = "object.missing";
        await ArtifactIngestService.InvalidateDependentsAsync(context, tracked, signal, CancellationToken.None)
            .ConfigureAwait(false);
        context.ChangeTracker.Clear();

        var persistedExecution = await context.CentralProcessingGraphExecutions.AsNoTracking()
            .Include(item => item.Jobs).ThenInclude(job => job.Inputs)
            .Include(item => item.Jobs).ThenInclude(job => job.InputRequirements)
            .SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Completed, persistedExecution.Status);
        var persistedNode = persistedExecution.Jobs.Single();
        Assert.AreEqual(CentralDerivativeJobStatus.Completed, persistedNode.Status);
        Assert.AreEqual(now, persistedNode.UpdatedAtUtc, "a terminal graph node is never rewritten");
        Assert.IsNull(persistedNode.LastError);
        Assert.AreEqual(new string('9', 64), persistedNode.InputSetIdentitySha256);
        Assert.AreEqual(now, persistedNode.ResolutionStartedAtUtc);
        Assert.HasCount(1, persistedNode.Inputs);
        Assert.AreEqual(source.Id, persistedNode.Inputs.Single().CentralArtifactId);
        Assert.AreEqual(CentralDerivativeInputResolutionState.Resolved,
            persistedNode.InputRequirements.Single().ResolutionState);
        Assert.IsFalse(signal.TryRead(out _), "a terminal execution has nothing to converge");

        var persistedLegacy = await context.CentralDerivativeJobs.AsNoTracking()
            .SingleAsync(job => job.Id == legacy.Id).ConfigureAwait(false);
        Assert.AreEqual(CentralDerivativeJobStatus.RetryableFailure, persistedLegacy.Status);
        Assert.AreEqual(CentralDerivativeJobScheduler.SourceInvalidatedReason, persistedLegacy.LastError);
    }

    [TestMethod]
    public async Task InvalidatingSelectedInputOfLiveNodeTerminalizesItLegallyAndSignalsConvergence()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var source = CreateArtifact();
        var execution = CreateExecution(source, now);
        var waiting = AddNode(execution, source, now, ordinal: 0);
        var leased = AddNode(execution, source, now, ordinal: 1);
        var cancelRequested = AddNode(execution, source, now, ordinal: 2);
        execution.ExpectedNodeCount = 3;
        await PersistExecutionAsync(context, execution, CentralProcessingGraphExecutionStatus.Running, now, () =>
        {
            SelectInput(context, waiting, source, now);
            SelectInput(context, leased, source, now);
            Freeze(leased, new string('8', 64), now);
            leased.Status = CentralDerivativeJobStatus.Leased;
            leased.StateReasonCode = null;
            leased.AttemptCount = 1;
            leased.LeaseOwner = "worker";
            leased.LeaseToken = Guid.NewGuid();
            leased.LeaseAcquiredAtUtc = now;
            leased.LeaseExpiresAtUtc = now.AddMinutes(1);
            var attempt = new CentralDerivativeJobAttempt
            {
                Job = leased,
                CentralDerivativeJobId = leased.Id,
                AttemptNumber = 1,
                WorkerId = "worker",
                LeaseAcquiredAtUtc = now,
                LeaseExpiresAtUtc = now.AddMinutes(1),
                Outcome = CentralDerivativeAttemptOutcome.Leased
            };
            leased.Attempts.Add(attempt);
            context.CentralDerivativeJobAttempts.Add(attempt);
            SelectInput(context, cancelRequested, source, now);
            cancelRequested.Status = CentralDerivativeJobStatus.CancelRequested;
            cancelRequested.CancellationRequestedAtUtc = now;
            cancelRequested.CancellationRequestedBy = "operator";
        }).ConfigureAwait(false);
        var signal = new CentralProcessingGraphConvergenceSignal();

        var tracked = await context.CentralArtifacts.SingleAsync(item => item.Id == source.Id).ConfigureAwait(false);
        tracked.ObjectState = CentralArtifactObjectState.Pending;
        tracked.StateReasonCode = "object.missing";
        await ArtifactIngestService.InvalidateDependentsAsync(context, tracked, signal, CancellationToken.None)
            .ConfigureAwait(false);
        context.ChangeTracker.Clear();

        var jobs = await context.CentralDerivativeJobs.AsNoTracking()
            .Include(job => job.Attempts)
            .Include(job => job.Inputs)
            .OrderBy(job => job.GraphNodeOrdinal)
            .ToListAsync().ConfigureAwait(false);
        Assert.HasCount(3, jobs);
        foreach (var job in jobs.Take(2))
        {
            Assert.AreEqual(CentralDerivativeJobStatus.TerminalFailure, job.Status);
            Assert.AreEqual(ArtifactIngestService.GraphSourceInvalidatedReason, job.StateReasonCode);
            Assert.AreEqual("object.missing", job.LastError);
            Assert.IsNotNull(job.CompletedAtUtc);
            Assert.IsNull(job.LeaseToken);
            Assert.IsNull(job.LeaseOwner);
            Assert.IsNull(job.AvailableAtUtc);
            Assert.AreEqual(now, job.ResolutionStartedAtUtc, "frozen resolution columns are untouched");
            Assert.HasCount(1, job.Inputs, "selected graph inputs are immutable");
        }
        Assert.AreEqual(new string('8', 64), jobs[1].InputSetIdentitySha256);
        var attempt = jobs[1].Attempts.Single();
        Assert.AreEqual(CentralDerivativeAttemptOutcome.TerminalFailure, attempt.Outcome);
        Assert.AreEqual(ArtifactIngestService.GraphSourceInvalidatedReason, attempt.ReasonCode);
        Assert.IsNotNull(attempt.EndedAtUtc);
        Assert.AreEqual(CentralDerivativeJobStatus.CancelRequested, jobs[2].Status,
            "an in-flight cancellation is finished by graph convergence, not by invalidation");
        Assert.AreEqual(now, jobs[2].UpdatedAtUtc);
        var persistedExecution = await context.CentralProcessingGraphExecutions.AsNoTracking().SingleAsync()
            .ConfigureAwait(false);
        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Running, persistedExecution.Status,
            "the execution row is owned by graph convergence");
        Assert.IsTrue(signal.TryRead(out var signaled));
        Assert.AreEqual(execution.Id, signaled);
        Assert.IsFalse(signal.TryRead(out _), "one execution is signaled once per invalidation pass");
    }

    [TestMethod]
    public async Task InvalidatingQuarantinedAnchorQuarantinesLiveNodeAndReleasesReservedTransientSlots()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var source = CreateArtifact();
        var execution = CreateExecution(source, now);
        var node = AddNode(execution, source, now);
        node.RecipeName = CentralTransientRuntime.RecipeName;
        var validation = new CentralTransientValidationJob
        {
            Job = node,
            CentralDerivativeJobId = node.Id,
            AgentId = source.Frame!.AgentId,
            SubmissionSchemaVersion = "test-v1",
            SubmissionIdentitySha256 = new string('A', 64),
            CreatedAtUtc = now
        };
        foreach (var (ordinal, state) in new[]
                 {
                     (0, CentralTransientValidationIdentitySlotState.Reserved),
                     (1, CentralTransientValidationIdentitySlotState.Committed)
                 })
        {
            validation.IdentitySlots.Add(new CentralTransientValidationIdentitySlot
            {
                ValidationJob = validation,
                CentralDerivativeJobId = node.Id,
                AgentId = source.Frame.AgentId,
                Ordinal = ordinal,
                State = state,
                SubmittedEventId = Guid.NewGuid(),
                CandidateId = Guid.NewGuid(),
                ObservationId = Guid.NewGuid(),
                AssessmentId = Guid.NewGuid()
            });
        }
        context.Add(validation);
        await PersistExecutionAsync(context, execution, CentralProcessingGraphExecutionStatus.Running, now, () =>
        {
            SelectInput(context, node, source, now);
            Freeze(node, new string('7', 64), now);
            node.Status = CentralDerivativeJobStatus.Pending;
            node.StateReasonCode = null;
            node.AvailableAtUtc = now;
        }).ConfigureAwait(false);

        var tracked = await context.CentralArtifacts.SingleAsync(item => item.Id == source.Id).ConfigureAwait(false);
        tracked.ObjectState = CentralArtifactObjectState.Quarantined;
        tracked.ReconstructionState = CentralReconstructionState.Quarantined;
        tracked.StateReasonCode = "object.integrity-mismatch";
        await ArtifactIngestService.InvalidateDependentsAsync(context, tracked, CancellationToken.None)
            .ConfigureAwait(false);
        context.ChangeTracker.Clear();

        var persisted = await context.CentralDerivativeJobs.AsNoTracking().SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(CentralDerivativeJobStatus.Quarantined, persisted.Status);
        Assert.AreEqual(ArtifactIngestService.GraphSourceInvalidatedReason, persisted.StateReasonCode);
        Assert.AreEqual("object.integrity-mismatch", persisted.LastError);
        var slots = await context.CentralTransientValidationIdentitySlots.AsNoTracking()
            .OrderBy(slot => slot.Ordinal).ToListAsync().ConfigureAwait(false);
        Assert.AreEqual(CentralTransientValidationIdentitySlotState.Unused, slots[0].State);
        Assert.AreEqual(CentralTransientValidationIdentitySlotState.Committed, slots[1].State);
    }

    [TestMethod]
    public async Task InvalidatingResultOfCompletedNodeInLiveExecutionLeavesNodeUntouchedAndSignals()
    {
        await using var context = CreateContext();
        var now = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var source = CreateArtifact();
        var result = CreateArtifact(source.Frame!, FrameArtifactRole.Preview);
        var execution = CreateExecution(source, now);
        var producer = AddNode(execution, source, now, ordinal: 0);
        _ = AddNode(execution, source, now, ordinal: 1);
        execution.ExpectedNodeCount = 2;
        context.Add(result);
        await PersistExecutionAsync(context, execution, CentralProcessingGraphExecutionStatus.Running, now, () =>
        {
            SelectInput(context, producer, source, now);
            Freeze(producer, new string('6', 64), now);
            producer.Status = CentralDerivativeJobStatus.Completed;
            producer.StateReasonCode = null;
            producer.ResultCentralArtifactId = result.Id;
            producer.ResultArtifact = result;
            producer.CompletedAtUtc = now;
        }).ConfigureAwait(false);
        var signal = new CentralProcessingGraphConvergenceSignal();

        var trackedResult = await context.CentralArtifacts.SingleAsync(item => item.Id == result.Id).ConfigureAwait(false);
        trackedResult.ObjectState = CentralArtifactObjectState.Pending;
        await ArtifactIngestService.InvalidateDependentsAsync(context, trackedResult, signal, CancellationToken.None)
            .ConfigureAwait(false);
        context.ChangeTracker.Clear();

        var jobs = await context.CentralDerivativeJobs.AsNoTracking().OrderBy(job => job.GraphNodeOrdinal)
            .ToListAsync().ConfigureAwait(false);
        Assert.AreEqual(CentralDerivativeJobStatus.Completed, jobs[0].Status,
            "a terminal node never reopens; graph replay is the correction path");
        Assert.AreEqual(result.Id, jobs[0].ResultCentralArtifactId);
        Assert.AreEqual(now, jobs[0].UpdatedAtUtc);
        Assert.AreEqual(CentralDerivativeJobStatus.Waiting, jobs[1].Status,
            "a node that never selected the invalidated artifact is not a dependent");
        Assert.IsTrue(signal.TryRead(out var signaled));
        Assert.AreEqual(execution.Id, signaled);
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static CentralArtifact CreateArtifact()
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
        return CreateArtifact(frame, FrameArtifactRole.Raw);
    }

    private static CentralArtifact CreateArtifact(CentralFrame frame, FrameArtifactRole role)
    {
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            DevicePublicId = frame.DevicePublicId,
            ArtifactId = Guid.NewGuid(),
            Role = role,
            Variant = role == FrameArtifactRole.Raw ? null : "variant",
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

    private static CentralProcessingGraphExecution CreateExecution(CentralArtifact source, DateTimeOffset now)
    {
        var execution = new CentralProcessingGraphExecution
        {
            ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
            Status = CentralProcessingGraphExecutionStatus.Pending,
            RequestIdentitySha256 = new string('B', 64),
            RevisionId = Guid.NewGuid(),
            DefinitionIdentitySha256 = new string('C', 64),
            FrozenDefinitionJson = "{}",
            CentralPlanIdentitySha256 = new string('D', 64),
            FrozenCentralPlanJson = "{}",
            ExpectedSourceCount = 1,
            ExpectedNodeCount = 1,
            ExpectedDependencyCount = 0,
            ExpectedOutputCount = 0,
            ObservatoryId = source.Frame!.ObservatoryId,
            LogicalCameraId = Guid.NewGuid(),
            LogicalCameraInstallationId = Guid.NewGuid(),
            InstallationPublicId = Guid.NewGuid(),
            AnchorSourceCentralArtifactId = source.Id,
            AnchorSourceArtifactId = source.ArtifactId,
            AnchorSourceChecksumSha256 = source.ChecksumSha256,
            Trigger = CentralProcessingGraphTrigger.Replay,
            ActorId = "operator",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReasonCode = "test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        execution.Sources.Add(new CentralProcessingGraphExecutionSource
        {
            Execution = execution,
            ExecutionId = execution.Id,
            SourceId = "$raw",
            CentralArtifactId = source.Id,
            Artifact = source,
            ArtifactId = source.ArtifactId,
            ArtifactChecksumSha256 = source.ChecksumSha256,
            ArtifactByteLength = source.ByteLength,
            SelectionEvidenceJson = "{}",
            SelectionEvidenceSha256 = new string('E', 64),
            SelectedAtUtc = now
        });
        return execution;
    }

    /// <summary>
    /// Walks the legal lifecycle: create unsealed with waiting nodes, seal, apply the post-expansion node state
    /// (selected inputs may only be appended to a sealed execution), start, and optionally complete.
    /// </summary>
    private static async Task PersistExecutionAsync(
        ApplicationDbContext context,
        CentralProcessingGraphExecution execution,
        CentralProcessingGraphExecutionStatus status,
        DateTimeOffset now,
        Action configureNodes)
    {
        var source = execution.Sources.Single().Artifact!;
        context.AddRange(source.Frame!, source, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.ExpandedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        configureNodes();
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.StartedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        if (status != CentralProcessingGraphExecutionStatus.Running)
        {
            execution.Status = status;
            execution.CompletedAtUtc = now;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        context.ChangeTracker.Clear();
    }

    private static CentralDerivativeJob AddNode(
        CentralProcessingGraphExecution execution,
        CentralArtifact source,
        DateTimeOffset now,
        int ordinal = 0)
    {
        var job = new CentralDerivativeJob
        {
            GraphExecution = execution,
            GraphExecutionId = execution.Id,
            GraphNodeId = $"node-{ordinal}",
            GraphNodeOrdinal = ordinal,
            SharedNodePlanIdentitySha256 = new string('F', 64),
            FrozenNodePlanJson = "{}",
            GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Required,
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "preview-v1",
            TargetVariant = $"preview-{ordinal}",
            RecipeName = BuiltInProcessingRecipes.EncodedPreview,
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = new string('1', 64),
            ExpectedRecipeIdentitySha256 = new string('1', 64),
            RequestIdentitySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Guid.NewGuid().ToByteArray())),
            Status = CentralDerivativeJobStatus.Waiting,
            WaitKind = CentralDerivativeWaitKind.Dependencies,
            ResolutionStartedAtUtc = now,
            StateReasonCode = "processing.graph.waiting-dependencies",
            MaxAttempts = 3,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = 0,
            BindingName = "input",
            GraphInputOrdinal = 0,
            GraphInputBindingKind = ProcessingGraphInputBindingKind.PrimaryArtifact,
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = "{}",
            ExpectedAgentId = "agent",
            ExpectedRigId = "rig",
            ResolutionState = CentralDerivativeInputResolutionState.Waiting
        });
        execution.Jobs.Add(job);
        return job;
    }

    private static void SelectInput(
        ApplicationDbContext context,
        CentralDerivativeJob job,
        CentralArtifact artifact,
        DateTimeOffset now)
    {
        var requirement = job.InputRequirements.Single();
        var input = new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = 0,
            CentralArtifactId = artifact.Id,
            Artifact = artifact,
            CaptureSequence = artifact.Frame?.CaptureSequence,
            CompatibilityJson = "{}",
            CompatibilitySha256 = new string('2', 64),
            ByteLength = artifact.ByteLength,
            SelectedAtUtc = now
        };
        job.Inputs.Add(input);
        context.CentralDerivativeJobInputs.Add(input);
        requirement.Input = input;
        requirement.ExpectedCentralArtifactId = artifact.Id;
        requirement.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
        requirement.ResolvedAtUtc = now;
    }

    private static void Freeze(CentralDerivativeJob job, string inputSetIdentity, DateTimeOffset now)
    {
        job.InputSetIdentitySha256 = inputSetIdentity;
        job.ResolutionCompletedAtUtc = now;
    }

    private static CentralDerivativeJob CreateLegacyJob(
        CentralArtifact source,
        DateTimeOffset now,
        CentralDerivativeJobStatus status)
        => new()
        {
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = CentralDerivativeRecipeCatalog.PreviewRecipeVersion,
            TargetVariant = CentralDerivativeRecipeCatalog.PreviewVariant,
            RecipeName = BuiltInProcessingRecipes.EncodedPreview,
            RecipeOptionsJson = "{}",
            InputSelectorJson = "{}",
            RequestedRecipeIdentitySha256 = new string('3', 64),
            ExpectedRecipeIdentitySha256 = new string('3', 64),
            RequestIdentitySha256 = new string('4', 64),
            Status = status,
            AttemptCount = 1,
            MaxAttempts = 3,
            CompletedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
}
