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
    [DataRow(nameof(CentralDerivativeJob.MinimumInputCount))]
    [DataRow(nameof(CentralDerivativeJob.MissingInputOutcome))]
    [DataRow(nameof(CentralDerivativeJob.GraphNodeOrdinal))]
    [DataRow(nameof(CentralDerivativeJob.PredecessorJobId))]
    public async Task SaveChangesRejectsFrozenGraphJobPropertyMutation(string propertyName)
    {
        await using var context = CreateContext();
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        job.MinimumInputCount = 2;
        job.MissingInputOutcome = CentralDerivativeWindowOutcome.Skip;
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);

        switch (propertyName)
        {
            case nameof(CentralDerivativeJob.MinimumInputCount):
                job.MinimumInputCount = 3;
                break;
            case nameof(CentralDerivativeJob.MissingInputOutcome):
                job.MissingInputOutcome = CentralDerivativeWindowOutcome.Fail;
                break;
            case nameof(CentralDerivativeJob.GraphNodeOrdinal):
                job.GraphNodeOrdinal = 7;
                break;
            case nameof(CentralDerivativeJob.PredecessorJobId):
                job.PredecessorJobId = Guid.NewGuid();
                break;
            default:
                Assert.Fail($"Unhandled property '{propertyName}'.");
                break;
        }

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
        StringAssert.Contains(exception.Message, "executable identity is immutable", StringComparison.Ordinal);
        CollectionAssert.Contains(ApplicationDbContext.GraphJobFrozenProperties, propertyName);
    }

    [TestMethod]
    public void GraphJobFrozenPropertiesMatchBaselineTriggerColumns()
    {
        const string resourceName = "HVO.SkyMonitor.LogicHost.Data.Migrations.BaselineTriggers.sql";
        using var stream = typeof(ApplicationDbContext).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' must exist.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        // Isolate the immutable-identity predicate of TR_CentralDerivativeJobs_GraphIdentityImmutable: from the
        // "d.[GraphExecutionId] IS NOT NULL OR i.[GraphExecutionId] IS NOT NULL" guard to its THROW.
        var triggerStart = sql.IndexOf(
            "CREATE TRIGGER [TR_CentralDerivativeJobs_GraphIdentityImmutable]", StringComparison.Ordinal);
        Assert.IsGreaterThan(-1, triggerStart);
        var predicateStart = sql.IndexOf(
            "WHERE (d.[GraphExecutionId] IS NOT NULL OR i.[GraphExecutionId] IS NOT NULL)",
            triggerStart,
            StringComparison.Ordinal);
        Assert.IsGreaterThan(-1, predicateStart);
        var predicateEnd = sql.IndexOf(
            "THROW 51000, 'Derivative graph executable identity is immutable.', 1;",
            predicateStart,
            StringComparison.Ordinal);
        Assert.IsGreaterThan(-1, predicateEnd);
        var predicate = sql[predicateStart..predicateEnd];
        // Frozen columns are the compared ones (i.[X] <> d.[X] or ISNULL(i.[X], ...) <> ...); the columns the single
        // re-derivation exemption merely tests are pinned by GraphJobExpectedIdentityExemptionMatchesEfGuard.
        var triggerColumns = System.Text.RegularExpressions.Regex.Matches(
                predicate, @"(?:ISNULL\(i\.\[(\w+)\],|i\.\[(\w+)\]\s*<>)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var guardProperties = ApplicationDbContext.GraphJobFrozenProperties
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            triggerColumns,
            guardProperties,
            $"EF guard vs trigger mismatch. Only in trigger: [{string.Join(", ", triggerColumns.Except(guardProperties))}]. " +
            $"Only in EF guard: [{string.Join(", ", guardProperties.Except(triggerColumns))}].");
    }

    [TestMethod]
    public void GraphJobExpectedIdentityExemptionMatchesEfGuard()
    {
        const string resourceName = "HVO.SkyMonitor.LogicHost.Data.Migrations.BaselineTriggers.sql";
        using var stream = typeof(ApplicationDbContext).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' must exist.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        var triggerStart = sql.IndexOf(
            "CREATE TRIGGER [TR_CentralDerivativeJobs_GraphIdentityImmutable]", StringComparison.Ordinal);
        var predicateEnd = sql.IndexOf(
            "THROW 51000, 'Derivative graph executable identity is immutable.', 1;", triggerStart, StringComparison.Ordinal);
        var predicate = sql[triggerStart..predicateEnd];

        // Exactly one exempt column, and it is the one the EF guard names.
        var exemptions = System.Text.RegularExpressions.Regex.Matches(
                predicate, @"OR \(i\.\[(\w+)\] <> d\.\[\1\]\s+AND NOT \(")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        CollectionAssert.AreEqual(new[] { ApplicationDbContext.GraphJobRederivableProperty }, exemptions);
        var exemptionStart = predicate.IndexOf("AND NOT (", StringComparison.Ordinal);
        var exemption = System.Text.RegularExpressions.Regex.Replace(predicate[exemptionStart..], @"\s+", " ");
        string[] elements =
        [
            "d.[Status] = N'Waiting' AND i.[Status] = N'Waiting'",
            "d.[AttemptCount] = 0 AND i.[AttemptCount] = 0",
            "d.[LeaseOwner] IS NULL AND i.[LeaseOwner] IS NULL",
            "d.[LeaseToken] IS NULL AND i.[LeaseToken] IS NULL",
            "d.[LeaseAcquiredAtUtc] IS NULL AND i.[LeaseAcquiredAtUtc] IS NULL",
            "d.[LeaseExpiresAtUtc] IS NULL AND i.[LeaseExpiresAtUtc] IS NULL",
            $"d.[RecipeName] = N'{ApplicationDbContext.GraphJobRederivableRecipeName}' AND i.[RecipeName] = N'{ApplicationDbContext.GraphJobRederivableRecipeName}'",
            "requirement.[CentralDerivativeJobId] = i.[Id]",
            $"requirement.[BindingName] = N'{ApplicationDbContext.GraphJobRederivationBindingName}'",
            $"requirement.[ResolutionState] = N'{nameof(CentralDerivativeInputResolutionState.Missing)}'"
        ];
        foreach (var element in elements)
        {
            StringAssert.Contains(exemption, element, StringComparison.Ordinal);
        }
        StringAssert.Contains(exemption, CentralProcessingGraphScheduler.ExpectedIdentityRederivationTriggerMarker,
            StringComparison.Ordinal);
        // The trigger spells these as literals; the elements above interpolate the EF guard's constants into them.
        StringAssert.Contains(exemption, "N'annotation'", StringComparison.Ordinal);
        StringAssert.Contains(exemption, "N'measured-stellar-associations'", StringComparison.Ordinal);
    }

    [TestMethod]
    public void GraphIdentityTriggerDefinitionClassificationFailsClosedOnEachUnprovenSchema()
    {
        var sql = ReadBaselineTriggerSql();
        var current = sql[sql.IndexOf("CREATE TRIGGER [TR_CentralDerivativeJobs_GraphIdentityImmutable]", StringComparison.Ordinal)..];
        var earlier = current.Replace(
            CentralProcessingGraphScheduler.ExpectedIdentityRederivationTriggerMarker, string.Empty, StringComparison.Ordinal);

        Assert.IsNull(CentralProcessingGraphScheduler.ClassifyGraphIdentityTriggerDefinition(current));
        Assert.AreEqual(CentralProcessingGraphScheduler.AnnotationRederivationUnsupportedSchemaReasonCode,
            CentralProcessingGraphScheduler.ClassifyGraphIdentityTriggerDefinition(earlier), "earlier baseline");
        Assert.AreEqual(CentralProcessingGraphScheduler.AnnotationRederivationUnverifiableSchemaReasonCode,
            CentralProcessingGraphScheduler.ClassifyGraphIdentityTriggerDefinition(null),
            "a null definition, as a login without VIEW DEFINITION reads it, is not an earlier baseline");
    }

    [TestMethod]
    public async Task SaveChangesAdmitsMeasuredAssociationExpectedIdentityRederivation()
    {
        await using var context = CreateContext();
        var (job, _) = await CreateRederivationCaseAsync(context, "exact").ConfigureAwait(false);

        var expectedIdentity = new string('9', 64);
        job.ExpectedRecipeIdentitySha256 = expectedIdentity;

        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.AreEqual(expectedIdentity, job.ExpectedRecipeIdentitySha256);
    }

    [TestMethod]
    [DataRow("persisted-pending")]
    [DataRow("transitions-to-pending")]
    [DataRow("attempted")]
    [DataRow("leased")]
    [DataRow("lease-acquired")]
    [DataRow("other-recipe")]
    [DataRow("requirement-waiting")]
    [DataRow("requirement-same-batch")]
    [DataRow("other-binding")]
    [DataRow("other-frozen-column")]
    [DataRow("requested-identity")]
    public async Task SaveChangesRejectsExpectedIdentityRederivationOutsideTheExemption(string boundary)
    {
        await using var context = CreateContext();
        var (job, requirement) = await CreateRederivationCaseAsync(context, boundary).ConfigureAwait(false);

        job.ExpectedRecipeIdentitySha256 = new string('9', 64);
        switch (boundary)
        {
            case "transitions-to-pending":
                job.Status = CentralDerivativeJobStatus.Pending;
                break;
            case "requirement-same-batch":
                requirement.ResolutionState = CentralDerivativeInputResolutionState.Missing;
                requirement.ResolutionReasonCode = "processing.graph.optional-dependency-omitted";
                requirement.ResolvedAtUtc = DateTimeOffset.UtcNow;
                break;
            case "other-frozen-column":
                job.TargetVariant = "rewritten";
                break;
            case "requested-identity":
                job.RequestedRecipeIdentitySha256 = new string('9', 64);
                break;
        }

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.SaveChangesAsync()).ConfigureAwait(false);
        StringAssert.Contains(exception.Message, "executable identity is immutable", StringComparison.Ordinal);
    }

    private static async Task<(CentralDerivativeJob Job, CentralDerivativeJobInputRequirement Requirement)>
        CreateRederivationCaseAsync(ApplicationDbContext context, string boundary)
    {
        var execution = CreateExecution(expectedNodeCount: 1);
        var job = CreateJob(execution);
        job.RecipeName = boundary == "other-recipe" ? "recipe" : BuiltInProcessingRecipes.Annotation;
        job.Status = boundary == "persisted-pending" ? CentralDerivativeJobStatus.Pending : CentralDerivativeJobStatus.Waiting;
        job.InputSetIdentitySha256 = boundary == "persisted-pending" ? job.InputSetIdentitySha256 : null;
        job.AttemptCount = boundary == "attempted" ? 1 : 0;
        if (boundary == "leased")
        {
            job.LeaseOwner = "worker";
            job.LeaseToken = Guid.NewGuid();
            job.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        if (boundary == "lease-acquired")
        {
            job.LeaseAcquiredAtUtc = DateTimeOffset.UtcNow;
        }
        var missing = boundary is not ("requirement-waiting" or "requirement-same-batch");
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            BindingName = boundary == "other-binding" ? "assessment" : BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName,
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = false,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.Exact,
            ExpectedAgentId = "agent",
            ResolutionState = missing ? CentralDerivativeInputResolutionState.Missing : CentralDerivativeInputResolutionState.Waiting,
            ResolutionReasonCode = missing ? "processing.graph.optional-dependency-omitted" : null,
            ResolvedAtUtc = missing ? DateTimeOffset.UtcNow : null
        };
        job.InputRequirements.Add(requirement);
        context.AddRange(execution, job);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return (job, requirement);
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

    private static string ReadBaselineTriggerSql()
    {
        const string resourceName = "HVO.SkyMonitor.LogicHost.Data.Migrations.BaselineTriggers.sql";
        using var stream = typeof(ApplicationDbContext).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' must exist.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
