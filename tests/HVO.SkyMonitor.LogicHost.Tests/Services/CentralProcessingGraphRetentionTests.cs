using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralProcessingGraphRetentionTests
{
    [TestMethod]
    [DataRow((int)CentralProcessingGraphExecutionStatus.Pending, true)]
    [DataRow((int)CentralProcessingGraphExecutionStatus.Running, true)]
    [DataRow((int)CentralProcessingGraphExecutionStatus.CancelRequested, true)]
    [DataRow((int)CentralProcessingGraphExecutionStatus.Completed, false)]
    public async Task ExecutionLifecycleHoldsOnlyActiveGraphReferences(int statusValue, bool expected)
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var now = DateTimeOffset.UtcNow;
        var target = CreateArtifact(FrameArtifactRole.Raw);
        var execution = CreateExecution(target, now);
        context.AddRange(target, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var status = (CentralProcessingGraphExecutionStatus)statusValue;
        if (status != CentralProcessingGraphExecutionStatus.Pending)
        {
            execution.ExpandedAtUtc = now;
            await context.SaveChangesAsync().ConfigureAwait(false);
            execution.Status = CentralProcessingGraphExecutionStatus.Running;
            execution.StartedAtUtc = now;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        if (status == CentralProcessingGraphExecutionStatus.CancelRequested)
        {
            execution.Status = status;
            execution.CancellationRequestedAtUtc = now;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        else if (status == CentralProcessingGraphExecutionStatus.Completed)
        {
            execution.Status = status;
            execution.CompletedAtUtc = now;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        var references = new CentralArtifactRetentionReferences(context);

        Assert.AreEqual(
            expected,
            await references.IsHeldAsync(target.Id, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("anchor")]
    [DataRow("source")]
    [DataRow("job-source")]
    [DataRow("selected-input")]
    [DataRow("expected-input")]
    [DataRow("output")]
    public async Task ActiveExecutionHoldsEveryDurableArtifactReferenceUntilTerminal(string referenceKind)
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var now = DateTimeOffset.UtcNow;
        var target = CreateArtifact(FrameArtifactRole.Preview);
        var anchor = referenceKind == "anchor" ? target : CreateArtifact(FrameArtifactRole.Raw);
        var execution = CreateExecution(anchor, now);
        CentralDerivativeJob? job = null;
        CentralDerivativeJobOutput? output = null;
        CentralDerivativeJobInputRequirement? inputRequirement = null;
        if (referenceKind == "source")
        {
            execution.ExpectedSourceCount = 1;
            execution.Sources.Add(new CentralProcessingGraphExecutionSource
            {
                Execution = execution,
                ExecutionId = execution.Id,
                SourceId = "source",
                CentralArtifactId = target.Id,
                ArtifactId = target.ArtifactId,
                ArtifactChecksumSha256 = target.ChecksumSha256,
                ArtifactByteLength = target.ByteLength,
                SelectionEvidenceJson = "{}",
                SelectionEvidenceSha256 = new string('5', 64),
                SelectedAtUtc = now
            });
        }
        if (referenceKind is "job-source" or "selected-input" or "expected-input" or "output")
        {
            execution.ExpectedNodeCount = 1;
            execution.ExpectedOutputCount = 1;
            job = CreateJob(execution, referenceKind == "job-source" ? target : anchor, now);
            output = new CentralDerivativeJobOutput
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Role = target.Role,
                Variant = target.Variant ?? string.Empty,
                ProductKind = ProcessingProductKind.PixelData,
                ContractJson = "{}",
                ContractIdentitySha256 = new string('6', 64)
            };
            job.Outputs.Add(output);
            execution.Jobs.Add(job);
            if (referenceKind is "selected-input" or "expected-input")
            {
                inputRequirement = new CentralDerivativeJobInputRequirement
                {
                    Job = job,
                    CentralDerivativeJobId = job.Id,
                    BindingName = "input",
                    SourceKind = CentralDerivativeInputSourceKind.Artifact,
                    IsRequired = true,
                    SelectorJson = "{}",
                    ExpectedAgentId = "agent",
                    ExpectedCentralArtifactId = target.Id,
                    ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                    ResolvedAtUtc = now
                };
                job.InputRequirements.Add(inputRequirement);
                if (referenceKind == "selected-input")
                {
                    job.InputSetIdentitySha256 = null;
                }
            }
        }
        context.AddRange(target, anchor, execution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.ExpandedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        if (referenceKind == "selected-input")
        {
            var input = new CentralDerivativeJobInput
            {
                Job = job,
                CentralDerivativeJobId = job!.Id,
                Requirement = inputRequirement,
                CentralDerivativeJobInputRequirementId = inputRequirement!.Id,
                CentralArtifactId = target.Id,
                Artifact = target,
                CompatibilityJson = "{}",
                CompatibilitySha256 = new string('8', 64),
                ByteLength = target.ByteLength,
                SelectedAtUtc = now
            };
            job.Inputs.Add(input);
            context.Entry(input).State = EntityState.Added;
            job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        if (referenceKind == "output")
        {
            output!.ResultCentralArtifactId = target.Id;
            output.ResultOutputIdentitySha256 = new string('7', 64);
            output.BoundAtUtc = now;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        var references = new CentralArtifactRetentionReferences(context);

        Assert.IsTrue(await references.IsHeldAsync(target.Id, CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(await references.IsHeldOutsideTransientEventAsync(
            target.Id, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false));

        if (job is not null)
        {
            job.Status = CentralDerivativeJobStatus.TerminalFailure;
            job.CompletedAtUtc = now;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        execution.Status = CentralProcessingGraphExecutionStatus.Failed;
        execution.CompletedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        Assert.IsFalse(await references.IsHeldAsync(target.Id, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(await references.IsHeldOutsideTransientEventAsync(
            target.Id, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false));
    }

    private static CentralArtifact CreateArtifact(FrameArtifactRole role)
        => new()
        {
            DevicePublicId = Guid.NewGuid(),
            ArtifactId = Guid.NewGuid(),
            Role = role,
            Variant = "default",
            RecipeVersion = "recipe-v1",
            ManifestSchemaVersion = "v1",
            MediaType = "application/octet-stream",
            ByteLength = 1,
            ChecksumSha256 = new string('A', 64),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };

    private static CentralProcessingGraphExecution CreateExecution(CentralArtifact anchor, DateTimeOffset now)
        => new()
        {
            ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
            Status = CentralProcessingGraphExecutionStatus.Pending,
            RequestIdentitySha256 = new string('B', 64),
            RevisionId = Guid.NewGuid(),
            DefinitionIdentitySha256 = new string('C', 64),
            FrozenDefinitionJson = "{}",
            CentralPlanIdentitySha256 = new string('D', 64),
            FrozenCentralPlanJson = "{}",
            ObservatoryId = Guid.NewGuid(),
            LogicalCameraId = Guid.NewGuid(),
            LogicalCameraInstallationId = Guid.NewGuid(),
            InstallationPublicId = Guid.NewGuid(),
            AnchorSourceArtifact = anchor,
            AnchorSourceCentralArtifactId = anchor.Id,
            AnchorSourceArtifactId = anchor.ArtifactId,
            AnchorSourceChecksumSha256 = anchor.ChecksumSha256,
            Trigger = CentralProcessingGraphTrigger.Replay,
            ActorId = "operator",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReasonCode = "test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

    private static CentralDerivativeJob CreateJob(
        CentralProcessingGraphExecution execution,
        CentralArtifact anchor,
        DateTimeOffset now)
        => new()
        {
            GraphExecution = execution,
            GraphExecutionId = execution.Id,
            GraphNodeId = "node",
            GraphNodeOrdinal = 0,
            SharedNodePlanIdentitySha256 = new string('E', 64),
            FrozenNodePlanJson = "{}",
            GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Required,
            SourceArtifact = anchor,
            SourceCentralArtifactId = anchor.Id,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "recipe-v1",
            TargetVariant = "default",
            RecipeName = "recipe",
            RequestedRecipeIdentitySha256 = new string('F', 64),
            ExpectedRecipeIdentitySha256 = new string('F', 64),
            RequestIdentitySha256 = new string('1', 64),
            Status = CentralDerivativeJobStatus.Pending,
            InputSetIdentitySha256 = new string('2', 64),
            MaxAttempts = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
}
