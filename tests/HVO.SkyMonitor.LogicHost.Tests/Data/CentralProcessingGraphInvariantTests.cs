using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralProcessingGraphInvariantTests
{
    [TestMethod]
    public async Task SaveChangesRejectsGraphJobIdentityRewrite()
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);

        job.TargetVariant = "rewritten";

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SaveChangesRejectsGraphWindowRequirementRewriteWithoutDependency()
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            BindingName = "window",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.Exact,
            ExpectedAgentId = "agent",
            ResolutionState = CentralDerivativeInputResolutionState.Waiting
        };
        job.InputRequirements.Add(requirement);
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);

        requirement.SelectorJson = "{\"changed\":true}";

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SaveChangesAllowsGraphRetryBudgetIncrease()
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);

        job.MaxAttempts++;

        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.AreEqual(2, job.MaxAttempts);
    }

    [TestMethod]
    public async Task SaveChangesRejectsInputAppendAfterGraphInputIdentityIsFrozen()
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            BindingName = "input",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = "{}",
            ExpectedAgentId = "agent",
            ExpectedCentralArtifactId = Guid.NewGuid(),
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = execution.CreatedAtUtc
        };
        job.InputRequirements.Add(requirement);
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var now = execution.CreatedAtUtc.AddSeconds(1);
        execution.ExpandedAtUtc = now;
        execution.UpdatedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        job.Inputs.Add(new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            CentralArtifactId = requirement.ExpectedCentralArtifactId.Value,
            CompatibilityJson = "{}",
            CompatibilitySha256 = new string('7', 64),
            SelectedAtUtc = execution.CreatedAtUtc
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SaveChangesAllowsAtomicGraphWindowFinalResolutionAfterExpansionSeal()
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        job.Status = CentralDerivativeJobStatus.Waiting;
        job.InputSetIdentitySha256 = null;
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            BindingName = "window",
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = "{}",
            ExpectedAgentId = "agent",
            ResolutionState = CentralDerivativeInputResolutionState.Waiting
        };
        job.InputRequirements.Add(requirement);
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var now = execution.CreatedAtUtc.AddSeconds(1);
        execution.ExpandedAtUtc = now;
        execution.UpdatedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.StartedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        var artifactId = Guid.NewGuid();
        requirement.ExpectedCentralArtifactId = artifactId;
        requirement.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
        requirement.ResolvedAtUtc = now;
        var input = new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            CentralArtifactId = artifactId,
            CompatibilityJson = "{}",
            CompatibilitySha256 = new string('7', 64),
            SelectedAtUtc = now
        };
        job.Inputs.Add(input);
        context.Entry(input).State = EntityState.Added;
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        job.ResolutionCompletedAtUtc = now;
        job.Status = CentralDerivativeJobStatus.Pending;

        await context.SaveChangesAsync().ConfigureAwait(false);

        Assert.HasCount(1, job.Inputs);
        Assert.AreEqual(64, job.InputSetIdentitySha256!.Length);
    }

    [TestMethod]
    [DataRow((int)CentralProcessingGraphExecutionStatus.Failed)]
    [DataRow((int)CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures)]
    public async Task SaveChangesRejectsReopeningTerminalGraphExecution(int statusValue)
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        if ((CentralProcessingGraphExecutionStatus)statusValue ==
            CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures)
        {
            job.GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Optional;
        }
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var now = execution.CreatedAtUtc.AddSeconds(1);
        execution.ExpandedAtUtc = now;
        execution.UpdatedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.StartedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        job.Status = CentralDerivativeJobStatus.TerminalFailure;
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.Status = (CentralProcessingGraphExecutionStatus)statusValue;
        execution.CompletedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        // Node recovery goes through graph replay; a terminal execution row is never reopened.
        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.CompletedAtUtc = null;
        execution.UpdatedAtUtc = now.AddSeconds(1);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow((int)CentralProcessingGraphExecutionStatus.Canceled)]
    [DataRow((int)CentralProcessingGraphExecutionStatus.Superseded)]
    public async Task SaveChangesRejectsCanceledOrSupersededGraphExecutionReopen(int statusValue)
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var now = execution.CreatedAtUtc.AddSeconds(1);
        execution.ExpandedAtUtc = now;
        execution.UpdatedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.StartedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        job.Status = CentralDerivativeJobStatus.TerminalFailure;
        await context.SaveChangesAsync().ConfigureAwait(false);
        if ((CentralProcessingGraphExecutionStatus)statusValue == CentralProcessingGraphExecutionStatus.Canceled)
        {
            execution.Status = CentralProcessingGraphExecutionStatus.CancelRequested;
            execution.CancellationRequestedAtUtc = now;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        execution.Status = (CentralProcessingGraphExecutionStatus)statusValue;
        execution.CompletedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.CompletedAtUtc = null;
        execution.CancellationRequestedAtUtc = null;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("actor")]
    [DataRow("idempotency")]
    [DataRow("reason")]
    [DataRow("replay-assignment")]
    public async Task SaveChangesRejectsInvalidGraphExecutionProvenance(string invalidField)
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 0);
        if (invalidField == "actor") execution.ActorId = " \t ";
        if (invalidField == "idempotency") execution.IdempotencyKey = "\r\n";
        if (invalidField == "reason") execution.ReasonCode = "   ";
        if (invalidField == "replay-assignment") execution.AssignmentId = Guid.NewGuid();
        context.Add(execution);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
    }

    [TestMethod]
    public void InputSetIdentityIncludesCanonicalInputsWithoutChangingArtifactOnlyIdentity()
    {
        var artifactInput = new CentralDerivativeJobInput
        {
            Ordinal = 0,
            CentralArtifactId = Guid.NewGuid(),
            CompatibilitySha256 = new string('A', 64)
        };
        var legacy = CentralDerivativeWindowIdentity.CreateInputSetIdentity([artifactInput]);
        var canonical = new CentralDerivativeJobCanonicalInput
        {
            Ordinal = 1,
            SchemaVersion = "schema-v1",
            IdentitySha256 = new string('B', 64)
        };

        Assert.AreEqual(legacy, CentralDerivativeWindowIdentity.CreateInputSetIdentity([artifactInput], []));
        Assert.AreNotEqual(
            legacy,
            CentralDerivativeWindowIdentity.CreateInputSetIdentity([artifactInput], [canonical]));
    }

    [TestMethod]
    public async Task SaveChangesRejectsTerminalExecutionWithNonterminalNode()
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var now = execution.CreatedAtUtc.AddSeconds(1);
        execution.ExpandedAtUtc = now;
        execution.UpdatedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.StartedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        execution.Status = CentralProcessingGraphExecutionStatus.Completed;
        execution.CompletedAtUtc = now;

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
        StringAssert.Contains(exception.Message, "node is nonterminal", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow((int)CentralProcessingGraphExecutionStatus.Completed)]
    [DataRow((int)CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures)]
    [DataRow((int)CentralProcessingGraphExecutionStatus.Canceled)]
    public async Task SaveChangesRejectsPendingExecutionTerminalizingWithoutIntermediateTransition(int statusValue)
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        job.Status = CentralDerivativeJobStatus.Completed;
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var now = execution.CreatedAtUtc.AddSeconds(1);
        execution.ExpandedAtUtc = now;
        execution.UpdatedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        // Every node is already terminal, yet the execution never entered Running (or CancelRequested): the guard
        // must still reject the single-save shortcut so the scheduler is forced through the legal lifecycle.
        execution.Status = (CentralProcessingGraphExecutionStatus)statusValue;
        execution.StartedAtUtc = now;
        execution.CancellationRequestedAtUtc = now;
        execution.CompletedAtUtc = now;

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
        StringAssert.Contains(exception.Message, "status transition is invalid", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SaveChangesAcceptsPendingExecutionCompletingThroughDurableRunningStep()
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        job.Status = CentralDerivativeJobStatus.Completed;
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var now = execution.CreatedAtUtc.AddSeconds(1);
        execution.ExpandedAtUtc = now;
        execution.UpdatedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        execution.Status = CentralProcessingGraphExecutionStatus.Running;
        execution.StartedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.Status = CentralProcessingGraphExecutionStatus.Completed;
        execution.CompletedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        Assert.AreEqual(CentralProcessingGraphExecutionStatus.Completed, execution.Status);
        Assert.AreEqual(now, execution.StartedAtUtc);
        Assert.AreEqual(now, execution.CompletedAtUtc);
    }

    private static ApplicationDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CentralProcessingGraphExecution CreateExecution(int expectedNodeCount)
    {
        var now = DateTimeOffset.UtcNow;
        return new CentralProcessingGraphExecution
        {
            ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
            Status = CentralProcessingGraphExecutionStatus.Pending,
            RequestIdentitySha256 = new string('A', 64),
            RevisionId = Guid.NewGuid(),
            DefinitionIdentitySha256 = new string('B', 64),
            FrozenDefinitionJson = "{}",
            CentralPlanIdentitySha256 = new string('C', 64),
            FrozenCentralPlanJson = "{}",
            ExpectedNodeCount = expectedNodeCount,
            ObservatoryId = Guid.NewGuid(),
            LogicalCameraId = Guid.NewGuid(),
            LogicalCameraInstallationId = Guid.NewGuid(),
            InstallationPublicId = Guid.NewGuid(),
            AnchorSourceCentralArtifactId = Guid.NewGuid(),
            AnchorSourceArtifactId = Guid.NewGuid(),
            AnchorSourceChecksumSha256 = new string('D', 64),
            Trigger = CentralProcessingGraphTrigger.Replay,
            ActorId = "operator",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReasonCode = "test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private static CentralDerivativeJob CreateJob(CentralProcessingGraphExecution execution)
        => new()
        {
            GraphExecution = execution,
            GraphExecutionId = execution.Id,
            GraphNodeId = "node",
            GraphNodeOrdinal = 0,
            SharedNodePlanIdentitySha256 = new string('E', 64),
            FrozenNodePlanJson = "{}",
            GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Required,
            SourceCentralArtifactId = Guid.NewGuid(),
            TargetRole = HVO.SkyMonitor.AgentCore.FrameArtifactRole.Preview,
            TargetRecipeVersion = "recipe-v1",
            TargetVariant = "default",
            RecipeName = "recipe",
            RequestedRecipeIdentitySha256 = new string('F', 64),
            ExpectedRecipeIdentitySha256 = new string('F', 64),
            RequestIdentitySha256 = new string('1', 64),
            Status = CentralDerivativeJobStatus.Pending,
            InputSetIdentitySha256 = new string('2', 64),
            MaxAttempts = 1,
            CreatedAtUtc = execution.CreatedAtUtc,
            UpdatedAtUtc = execution.CreatedAtUtc
        };
}
