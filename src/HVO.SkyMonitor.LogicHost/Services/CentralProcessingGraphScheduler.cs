using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum CentralProcessingGraphScheduleOutcome
{
    Created,
    Existing,
    AwaitingSources,
    NotApplicable,
    Conflict,
    Invalid
}

/// <param name="CoversTransientValidation">
/// True when the resolved effective graph contains a transient-validation handler node, so the graph owns the
/// deployment's transient recipe work for this source role. False for every non-graph outcome and for graphs that
/// carry no transient node; the legacy scheduler then still owns exactly the transient recipe.
/// </param>
internal sealed record CentralProcessingGraphScheduleResult(
    CentralProcessingGraphScheduleOutcome Outcome,
    CentralProcessingGraphExecution? Execution = null,
    string? ReasonCode = null,
    bool CoversTransientValidation = false);

internal sealed record CentralProcessingGraphReplayRequest(
    Guid RevisionId,
    IReadOnlyList<Guid> SourceCentralArtifactIds,
    string ActorId,
    string IdempotencyKey,
    string ReasonCode);

internal interface ICentralProcessingGraphScheduler
{
    Task<CentralProcessingGraphScheduleResult> ScheduleLiveAsync(
        Guid centralArtifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
        CentralProcessingGraphReplayRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task ConvergeAsync(Guid executionId, DateTimeOffset now, CancellationToken cancellationToken);

    Task ConvergeBatchAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed partial class CentralProcessingGraphScheduler(
    ApplicationDbContext dbContext,
    ProcessingGraphCatalogService catalog,
    ICentralProcessingGraphNodeRegistry nodeRegistry,
    ICentralDerivativeWindowResolver windowResolver,
    ICentralArtifactObjectReader objectReader,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider,
    IEnvironmentalObservationQueryService? environmentalQuery = null,
    ILogger<CentralProcessingGraphScheduler>? logger = null) : ICentralProcessingGraphScheduler
{
    private const int ConvergenceBatchSize = 100;
    private const string LiveActor = "logic-host";
    private static readonly EnvironmentalObservationSourceKind[] EnvironmentalSourcePriority =
        Enum.GetValues<EnvironmentalObservationSourceKind>();
    private static readonly EnvironmentalObservationQuality[] EnvironmentalQualities =
        Enum.GetValues<EnvironmentalObservationQuality>();
    private static readonly JsonSerializerOptions CycleEvidenceSerializerOptions = CreateCycleEvidenceSerializerOptions();

    public async Task<CentralProcessingGraphScheduleResult> ScheduleLiveAsync(
        Guid centralArtifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var artifact = await LoadArtifactAsync(centralArtifactId, cancellationToken).ConfigureAwait(false);
        if (!IsUsable(artifact) || artifact.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated) ||
            artifact.Frame?.LogicalCameraInstallation is not { RetiredAtUtc: null } installation ||
            installation.LogicalCamera is not { DeactivatedAtUtc: null } camera ||
            camera.ObservatoryId != artifact.Frame.ObservatoryId)
        {
            return new(CentralProcessingGraphScheduleOutcome.NotApplicable);
        }
        var assignment = await catalog.ResolveAsync(
            CentralProcessingGraphTargetHost.Central,
            artifact.Frame.ObservatoryId,
            installation.LogicalCameraId,
            now,
            cancellationToken).ConfigureAwait(false);
        if (assignment?.Revision is null)
        {
            return new(CentralProcessingGraphScheduleOutcome.NotApplicable);
        }
        var plan = CompileAndVerify(assignment.Revision);
        if (!plan.Sources.Any(source => source.Outputs.Any(output => output.Role == artifact.Role)))
        {
            return new(CentralProcessingGraphScheduleOutcome.NotApplicable);
        }
        var coversTransientValidation = ContainsTransientValidationNode(plan);
        var sources = SelectLiveSources(plan, artifact);
        if (sources is null)
        {
            return new(CentralProcessingGraphScheduleOutcome.AwaitingSources,
                CoversTransientValidation: coversTransientValidation);
        }
        var result = await ExpandAsync(
            assignment.Revision,
            assignment,
            plan,
            sources,
            CentralProcessingGraphExecutionClass.Live,
            CentralProcessingGraphTrigger.Ingest,
            LiveActor,
            idempotencyKey: string.Empty,
            "automatic-ingest",
            now,
            cancellationToken).ConfigureAwait(false);
        return result with { CoversTransientValidation = coversTransientValidation };
    }

    /// <summary>
    /// The plan was validated against the node registry, so every step alias resolves to a registered handler.
    /// </summary>
    private bool ContainsTransientValidationNode(ProcessingGraphExecutionPlan plan)
        => plan.Nodes.Any(node => nodeRegistry.GetRequired(node.Definition.StepAlias).Kind ==
            CentralProcessingGraphNodeHandlerKind.TransientValidation);

    public async Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
        CentralProcessingGraphReplayRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceCentralArtifactIds is not { Count: > 0 } ||
            request.SourceCentralArtifactIds.Count > ProcessingGraphCompiler.MaximumSources ||
            request.SourceCentralArtifactIds.Distinct().Count() != request.SourceCentralArtifactIds.Count ||
            string.IsNullOrWhiteSpace(request.ActorId) || request.ActorId.Length > 450 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256 ||
            string.IsNullOrWhiteSpace(request.ReasonCode) || request.ReasonCode.Length > 128)
        {
            return new(CentralProcessingGraphScheduleOutcome.Invalid, ReasonCode: "invalid-replay");
        }
        var revision = await dbContext.CentralProcessingGraphRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.RevisionId && item.PublishedAtUtc != null,
                cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            return new(CentralProcessingGraphScheduleOutcome.Invalid, ReasonCode: "revision-not-published");
        }
        ProcessingGraphExecutionPlan plan;
        try
        {
            plan = CompileAndVerify(revision);
        }
        catch (CentralDerivativeJobStateException)
        {
            return new(CentralProcessingGraphScheduleOutcome.Invalid, ReasonCode: "revision-invalid");
        }
        var artifacts = await dbContext.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.LogicalCameraInstallation)!
                .ThenInclude(installation => installation!.LogicalCamera)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .Where(item => request.SourceCentralArtifactIds.Contains(item.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ordered = request.SourceCentralArtifactIds.Select(id => artifacts.SingleOrDefault(item => item.Id == id))
            .ToArray();
        if (ordered.Any(static item => item is null) || ordered.Any(item => !IsUsable(item!)) ||
            ordered.Select(item => item!.Frame!.ObservatoryId).Distinct().Count() != 1 ||
            ordered.Select(item => item!.Frame!.LogicalCameraInstallationId).Distinct().Count() != 1 ||
            !SourcesMatch(plan, ordered.Select(static item => item!).ToArray()))
        {
            return new(CentralProcessingGraphScheduleOutcome.Invalid, ReasonCode: "invalid-replay-sources");
        }
        return await ExpandAsync(
            revision,
            assignment: null,
            plan,
            ordered.Select(static item => item!).ToArray(),
            CentralProcessingGraphExecutionClass.Replay,
            CentralProcessingGraphTrigger.Replay,
            request.ActorId.Trim(),
            request.IdempotencyKey.Trim(),
            request.ReasonCode.Trim(),
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ConvergeAsync(
        Guid executionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var executionClass = "other";
        IDbContextTransaction? transaction = null;
        try
        {
            if (dbContext.Database.CurrentTransaction is null)
            {
                transaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            }
            if (dbContext.Database.IsSqlServer())
            {
                _ = await dbContext.Database.SqlQuery<int>($"""
                    SELECT CAST(1 AS int) AS [Value]
                    FROM [CentralProcessingGraphExecutions] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {executionId}
                    """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
            var execution = await LoadExecutionAsync(executionId, cancellationToken).ConfigureAwait(false);
            if (execution is null || execution.ExpandedAtUtc is null || IsTerminal(execution.Status))
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            executionClass = execution.ExecutionClass.ToString();
            await ConvergeCoreAsync(execution, now, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            telemetry.RecordGraphConvergence(
                executionClass, execution.Status.ToString(), timeProvider.GetElapsedTime(started));
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            telemetry.RecordGraphConvergence(executionClass, "failed", timeProvider.GetElapsedTime(started));
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A corrupt graph execution must not prevent convergence recovery for the rest of the bounded batch.")]
    public async Task ConvergeBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var terminal = TerminalExecutionStatuses;
        var ids = await dbContext.CentralProcessingGraphExecutions.AsNoTracking()
            .Where(item => item.ExpandedAtUtc != null && !terminal.Contains(item.Status))
            .OrderBy(item => item.UpdatedAtUtc)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(ConvergenceBatchSize)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            try
            {
                await ConvergeAsync(id, now, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Only real database faults count as dependency failures (and degrade worker health). A graph whose
                // frozen state cannot converge is recorded through the convergence "failed" outcome and the error
                // log, not as a database outage.
                if (IsDatabaseFailure(exception))
                {
                    telemetry.RecordDependencyFailure("database", now);
                }
                if (logger is not null)
                {
                    Log.ConvergenceFailed(logger, exception, id);
                }
                await RotateFailedExecutionAsync(id, now, cancellationToken).ConfigureAwait(false);
            }
        }
        telemetry.RecordGraphRecoveryPoll(now, ids.Length);
    }

    /// <summary>
    /// Classifies a convergence exception: SQL/EF persistence faults (including timeouts, optimistic-concurrency
    /// races, and the <see cref="OperationCanceledException"/>/<see cref="TaskCanceledException"/> SqlClient raises
    /// for an aborted command when the caller's token is not canceled) are dependency failures;
    /// <see cref="CentralDerivativeJobStateException"/> and other graph-state faults are not.
    /// </summary>
    internal static bool IsDatabaseFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case CentralDerivativeJobStateException:
                    return false;
                case DbException:
                case DbUpdateException:
                case TimeoutException:
                case OperationCanceledException:
                    return true;
                case AggregateException aggregate when aggregate.InnerExceptions.Count > 1:
                    return aggregate.InnerExceptions.Any(IsDatabaseFailure);
            }
        }
        return false;
    }

    /// <summary>
    /// Durable recovery is bounded and ordered by <c>UpdatedAtUtc</c>. Successful convergence rotates every
    /// non-progressing execution behind newer work; a convergence failure must rotate the same way, otherwise a
    /// single corrupt execution would pin the oldest slot and starve every execution beyond the bounded batch.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Rotation is best effort; the original convergence failure was already recorded.")]
    private async Task RotateFailedExecutionAsync(Guid executionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var execution = await dbContext.CentralProcessingGraphExecutions
                .SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken).ConfigureAwait(false);
            if (execution is null || IsTerminal(execution.Status) || execution.UpdatedAtUtc >= now)
            {
                return;
            }
            execution.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                Log.RotationFailed(logger, exception, executionId);
            }
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    private async Task<CentralProcessingGraphScheduleResult> ExpandAsync(
        CentralProcessingGraphRevision revision,
        CentralProcessingGraphAssignment? assignment,
        ProcessingGraphExecutionPlan plan,
        IReadOnlyList<CentralArtifact> sources,
        CentralProcessingGraphExecutionClass executionClass,
        CentralProcessingGraphTrigger trigger,
        string actor,
        string idempotencyKey,
        string reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var orderedSources = plan.Sources.Select((source, ordinal) => new
        {
            Definition = source,
            Artifact = sources[ordinal],
            Ordinal = ordinal
        }).ToArray();
        var requestIdentity = CreateGraphRequestIdentity(
            executionClass, revision, assignment, orderedSources.Select(item => item.Artifact), trigger,
            actor, idempotencyKey, reasonCode);
        if (executionClass == CentralProcessingGraphExecutionClass.Live)
        {
            idempotencyKey = $"live:{requestIdentity}";
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            if (dbContext.Database.IsSqlServer())
            {
                var resource = $"processing-graph-execution:{requestIdentity}";
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock
                        @Resource = {resource},
                        @LockMode = 'Exclusive',
                        @LockOwner = 'Transaction',
                        @LockTimeout = 10000;
                    IF @result < 0 THROW 51000, 'Could not lock processing graph expansion.', 1;
                    """, cancellationToken).ConfigureAwait(false);
            }
            var byKey = await dbContext.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ExecutionClass == executionClass && item.ActorId == actor &&
                    item.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (byKey is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                CentralProcessingGraphScheduleResult result = string.Equals(
                    byKey.RequestIdentitySha256, requestIdentity, StringComparison.Ordinal)
                    ? new(CentralProcessingGraphScheduleOutcome.Existing, byKey)
                    : new(CentralProcessingGraphScheduleOutcome.Conflict, ReasonCode: "idempotency-key-conflict");
                telemetry.RecordGraphExpansion(
                    executionClass.ToString(), result.Outcome.ToString(), timeProvider.GetElapsedTime(started), 0);
                return result;
            }
            var existing = await dbContext.CentralProcessingGraphExecutions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestIdentitySha256 == requestIdentity, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                telemetry.RecordGraphExpansion(
                    executionClass.ToString(), "existing", timeProvider.GetElapsedTime(started), 0);
                return new(CentralProcessingGraphScheduleOutcome.Existing, existing);
            }
            var anchor = sources[0];
            var frame = anchor.Frame!;
            var installation = frame.LogicalCameraInstallation
                ?? throw new CentralDerivativeJobStateException("The graph source installation is unavailable.");
            var environmentalInputs = await FreezeEnvironmentalInputsAsync(plan, frame, cancellationToken)
                .ConfigureAwait(false);
            var execution = new CentralProcessingGraphExecution
            {
                ExecutionClass = executionClass,
                Status = CentralProcessingGraphExecutionStatus.Pending,
                RequestIdentitySha256 = requestIdentity,
                RevisionId = revision.Id,
                AssignmentId = assignment?.Id,
                DefinitionIdentitySha256 = plan.DefinitionIdentitySha256,
                FrozenDefinitionJson = revision.DefinitionJson,
                CentralPlanIdentitySha256 = plan.PlanIdentitySha256,
                FrozenCentralPlanJson = LogicHostProcessingGraphAdapter.FreezePlan(plan),
                ExpectedSourceCount = orderedSources.Length,
                ExpectedNodeCount = plan.Nodes.Length,
                ExpectedDependencyCount = CountDependencyEdges(plan),
                ExpectedOutputCount = plan.Nodes.Sum(static node => node.Definition.Outputs.Length),
                ObservatoryId = frame.ObservatoryId,
                LogicalCameraId = installation.LogicalCameraId,
                LogicalCameraInstallationId = installation.Id,
                InstallationPublicId = installation.InstallationPublicId,
                AnchorSourceCentralArtifactId = anchor.Id,
                AnchorSourceArtifactId = anchor.ArtifactId,
                AnchorSourceChecksumSha256 = anchor.ChecksumSha256,
                Trigger = trigger,
                ActorId = actor,
                IdempotencyKey = idempotencyKey,
                ReasonCode = reasonCode,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var sourceRows = new Dictionary<string, CentralProcessingGraphExecutionSource>(StringComparer.Ordinal);
            foreach (var item in orderedSources)
            {
                var evidenceJson = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
                {
                    schema = "hvo-central-processing-graph-source-selection-v1",
                    item.Definition.Id,
                    item.Ordinal,
                    item.Artifact.ArtifactId,
                    item.Artifact.ChecksumSha256,
                    item.Artifact.ByteLength
                })).GetRawText();
                var source = new CentralProcessingGraphExecutionSource
                {
                    Execution = execution,
                    ExecutionId = execution.Id,
                    Ordinal = item.Ordinal,
                    SourceId = item.Definition.Id,
                    OutputOrdinal = 0,
                    CentralArtifactId = item.Artifact.Id,
                    Artifact = item.Artifact,
                    ArtifactId = item.Artifact.ArtifactId,
                    ArtifactChecksumSha256 = item.Artifact.ChecksumSha256,
                    ArtifactByteLength = item.Artifact.ByteLength,
                    SelectionEvidenceJson = evidenceJson,
                    SelectionEvidenceSha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(evidenceJson),
                    SelectedAtUtc = now
                };
                execution.Sources.Add(source);
                sourceRows.Add(item.Definition.Id, source);
            }
            var jobs = new Dictionary<string, CentralDerivativeJob>(StringComparer.Ordinal);
            var projectedArtifacts = new Dictionary<(string ProducerId, int Output), Guid>();
            foreach (var node in plan.Nodes)
            {
                var handler = nodeRegistry.GetRequired(node.Definition.StepAlias);
                var bindingSources = node.InputBindings.OrderBy(static binding => binding.InputIndex)
                    .Select(binding => ResolveBindingArtifactId(binding, sourceRows, projectedArtifacts))
                    .ToArray();
                var primaryBinding = node.InputBindings.FirstOrDefault(static binding =>
                    binding.BindingKind == ProcessingGraphInputBindingKind.PrimaryArtifact);
                var primarySelector = primaryBinding is null
                    ? handler.Recipe.InputSelector
                    : CreateSelector(primaryBinding, plan, jobs);
                var requestedIdentity = handler.Kind == CentralProcessingGraphNodeHandlerKind.TransientValidation
                    ? handler.Recipe.RequestedRecipeIdentitySha256
                    : BuiltInProcessingRecipes.CreateRequestedIdentity(
                        node.Definition.StepAlias, node.Definition.EffectiveOptions, primarySelector).IdentitySha256;
                var auxiliaries = node.InputBindings
                    .Where(static binding => binding.BindingKind == ProcessingGraphInputBindingKind.AuxiliaryArtifact)
                    .OrderBy(static binding => binding.InputIndex)
                    .Select(binding => new ProcessingAuxiliaryInput(
                        binding.BindingName,
                        ProcessingAuxiliaryInputKind.Artifact,
                        CreateSelector(binding, plan, jobs),
                        ArtifactId: ResolveBindingArtifactId(binding, sourceRows, projectedArtifacts)))
                    .Concat(CreateExternalAuxiliaries(node, environmentalInputs))
                    .ToArray();
                var expectedIdentity = auxiliaries.Length == 0 ||
                    handler.Kind == CentralProcessingGraphNodeHandlerKind.TransientValidation
                        ? requestedIdentity
                        : BuiltInProcessingRecipes.CreateExecutionIdentity(
                            node.Definition.StepAlias,
                            node.Definition.EffectiveOptions,
                            primarySelector,
                            auxiliaryInputs: auxiliaries).IdentitySha256;
                var firstOutput = node.Definition.Outputs.FirstOrDefault();
                var job = new CentralDerivativeJob
                {
                    GraphExecution = execution,
                    GraphExecutionId = execution.Id,
                    GraphNodeId = node.Definition.Id,
                    GraphNodeOrdinal = execution.Jobs.Count,
                    SharedNodePlanIdentitySha256 = node.IdentitySha256,
                    FrozenNodePlanJson = LogicHostProcessingGraphAdapter.FreezeNode(node),
                    GraphFailurePolicy = node.Definition.FailurePolicy,
                    SourceCentralArtifactId = anchor.Id,
                    SourceArtifact = anchor,
                    TargetRole = firstOutput?.Role ?? handler.Recipe.TargetRole,
                    TargetRecipeVersion = node.Definition.StepVersion,
                    TargetVariant = firstOutput?.Variant ?? handler.Recipe.TargetVariant,
                    RecipeName = node.Definition.StepAlias,
                    RecipeOptionsJson = CaptureContractJson.Canonicalize(
                        handler.Kind == CentralProcessingGraphNodeHandlerKind.TransientValidation
                            ? handler.Recipe.Options
                            : node.Definition.EffectiveOptions).GetRawText(),
                    InputSelectorJson = CaptureContractJson.Canonicalize(
                        CaptureContractJson.SerializeToElement(primarySelector)).GetRawText(),
                    RequestedRecipeIdentitySha256 = requestedIdentity,
                    ExpectedRecipeIdentitySha256 = expectedIdentity,
                    RequestIdentitySha256 = CreateNodeRequestIdentity(requestIdentity, node.IdentitySha256, expectedIdentity),
                    TraceParent = Activity.Current?.Id,
                    TraceState = Activity.Current?.TraceStateString,
                    Status = CentralDerivativeJobStatus.Waiting,
                    WaitKind = node.Definition.Window is null
                        ? CentralDerivativeWaitKind.Dependencies
                        : CentralDerivativeWaitKind.Window,
                    ResolutionDeadlineUtc = node.Definition.Window is null
                        ? null
                        : executionClass == CentralProcessingGraphExecutionClass.Replay
                            ? now
                            : now.AddTicks(node.Definition.Window.TimeoutTicks),
                    ResolutionStartedAtUtc = now,
                    MissingInputOutcome = MapMissingOutcome(node.Definition.Window?.MissingInputOutcome),
                    StateReasonCode = node.Definition.Window is null
                        ? "processing.graph.waiting-dependencies"
                        : CentralDerivativeWindowReasonCodes.WaitingRequiredInput,
                    AttemptCount = 0,
                    MaxAttempts = handler.Recipe.MaxAttempts,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                execution.Jobs.Add(job);
                jobs.Add(node.Definition.Id, job);
                foreach (var output in node.Definition.Outputs.Select((contract, ordinal) => (contract, ordinal)))
                {
                    job.Outputs.Add(CentralDerivativeJobOutput.CreateFromFrozenPlan(job, output.ordinal, output.contract));
                    var outputSources = string.Equals(
                        node.Definition.StepAlias, BuiltInProcessingRecipes.CloudAssessment, StringComparison.Ordinal) &&
                        environmentalInputs?.ClearReferenceArtifactId is { } clearReferenceArtifactId
                            ? bindingSources.Append(clearReferenceArtifactId).ToArray()
                            : bindingSources;
                    if (outputSources.Length > 0 && outputSources.All(static id => id != Guid.Empty))
                    {
                        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
                            output.contract.Role, output.contract.Variant, expectedIdentity, outputSources);
                        projectedArtifacts[(node.Definition.Id, output.ordinal)] =
                            ProcessingIdentity.CreateArtifactId(outputIdentity);
                    }
                }
                if (handler.Kind == CentralProcessingGraphNodeHandlerKind.TransientValidation &&
                    handler.Recipe.Transient is { } transient)
                {
                    AddTransientRuntimeState(job, handler.Recipe, transient, frame, now);
                }
            }
            foreach (var node in plan.Nodes)
            {
                AddDependencies(execution, node, jobs, sourceRows, plan, frame, now);
                AddExternalInputRequirements(jobs[node.Definition.Id], node, frame, now);
            }
            dbContext.CentralProcessingGraphExecutions.Add(execution);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await CentralProcessingGraphExpansionSeal.SealAsync(
                dbContext, execution.Id, now, cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            var persisted = await LoadExecutionAsync(execution.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new CentralDerivativeJobStateException("The expanded processing graph execution disappeared.");
            await MaterializeExternalInputsAsync(persisted, environmentalInputs, now, cancellationToken)
                .ConfigureAwait(false);
            await ConvergeCoreAsync(persisted, now, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (executionClass == CentralProcessingGraphExecutionClass.Replay)
            {
                foreach (var windowJobId in persisted.Jobs.Where(static job => job.WaitKind == CentralDerivativeWaitKind.Window)
                             .Select(static job => job.Id).ToArray())
                {
                    await windowResolver.ResolveAsync(windowJobId, now, cancellationToken).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordGraphExpansion(
                executionClass.ToString(), "created", timeProvider.GetElapsedTime(started), persisted.Jobs.Count);
            return new(CentralProcessingGraphScheduleOutcome.Created, persisted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            telemetry.RecordGraphExpansion(
                executionClass.ToString(), "failed", timeProvider.GetElapsedTime(started), 0);
            throw;
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    private void AddDependencies(
        CentralProcessingGraphExecution execution,
        ProcessingGraphPlanNode node,
        Dictionary<string, CentralDerivativeJob> jobs,
        Dictionary<string, CentralProcessingGraphExecutionSource> sources,
        ProcessingGraphExecutionPlan plan,
        CentralFrame frame,
        DateTimeOffset now)
    {
        var consumer = jobs[node.Definition.Id];
        foreach (var dependencyDefinition in node.Definition.Dependencies)
        {
            var bindings = node.InputBindings.Where(candidate => string.Equals(
                candidate.ProducerId, dependencyDefinition.ProducerId, StringComparison.Ordinal)).ToArray();
            if (IsProductDependency(dependencyDefinition.Kind) && bindings.Length == 0)
            {
                throw new CentralDerivativeJobStateException(
                    "LogicHost requires each product dependency to have a frozen input binding.");
            }
            if (bindings.Length == 0)
            {
                AddDependency(dependencyDefinition, null);
                continue;
            }
            foreach (var binding in bindings)
            {
                AddDependency(dependencyDefinition, binding);
            }
        }

        void AddDependency(
            ProcessingGraphDependencyDefinition dependencyDefinition,
            ProcessingGraphInputBinding? binding)
        {
            var dependency = new CentralDerivativeJobDependency
            {
                Execution = execution,
                ExecutionId = execution.Id,
                ConsumerJob = consumer,
                ConsumerJobId = consumer.Id,
                Ordinal = consumer.Dependencies.Count,
                Kind = dependencyDefinition.Kind,
                Required = dependencyDefinition.Required,
                ProducerJob = jobs.GetValueOrDefault(dependencyDefinition.ProducerId),
                ProducerJobId = jobs.GetValueOrDefault(dependencyDefinition.ProducerId)?.Id,
                ProducerSource = sources.GetValueOrDefault(dependencyDefinition.ProducerId),
                ProducerSourceId = sources.GetValueOrDefault(dependencyDefinition.ProducerId)?.Id,
                ProducerOutputOrdinal = binding?.OutputIndex,
                ConsumerInputOrdinal = binding?.InputIndex,
                ConsumerBindingName = binding?.BindingName,
                ConsumerBindingKind = binding?.BindingKind
            };
            execution.Dependencies.Add(dependency);
            consumer.Dependencies.Add(dependency);
            if (binding is null)
            {
                return;
            }
            var selector = CreateSelector(binding, plan, jobs);
            var offsets = node.Definition.Window is null
                ? ImmutableArray.Create(0)
                : CreateWindowOffsets(node.Definition.Window);
            foreach (var offset in offsets)
            {
                var requirement = new CentralDerivativeJobInputRequirement
                {
                    Job = consumer,
                    CentralDerivativeJobId = consumer.Id,
                    Ordinal = consumer.InputRequirements.Count,
                    BindingName = binding.BindingName,
                    GraphDependency = dependency,
                    GraphDependencyId = dependency.Id,
                    GraphInputOrdinal = binding.InputIndex,
                    GraphInputBindingKind = binding.BindingKind,
                    SourceKind = binding.BindingKind is ProcessingGraphInputBindingKind.CanonicalJson or
                        ProcessingGraphInputBindingKind.Annotation
                            ? CentralDerivativeInputSourceKind.Canonical
                            : CentralDerivativeInputSourceKind.Artifact,
                    SequenceOffset = node.Definition.Window is null ? null : offset,
                    IsRequired = node.Definition.Window is null
                        ? binding.Required
                        : node.Definition.Window.RequiredPositions.Contains(offset),
                    SelectorJson = CaptureContractJson.Canonicalize(
                        CaptureContractJson.SerializeToElement(selector)).GetRawText(),
                    CompatibilityMode = node.Definition.Window is null
                        ? CentralDerivativeCompatibilityMode.None
                        : CentralDerivativeCompatibilityMode.Exact,
                    ExpectedAgentId = frame.AgentId,
                    ExpectedRigId = frame.RigId,
                    ExpectedCaptureSequence = node.Definition.Window is null
                        ? frame.CaptureSequence
                        : AddSequenceOffset(frame.CaptureSequence, offset),
                    ResolutionState = CentralDerivativeInputResolutionState.Waiting
                };
                consumer.InputRequirements.Add(requirement);
                dependency.InputRequirements.Add(requirement);
            }
        }
    }

    private async Task ConvergeCoreAsync(
        CentralProcessingGraphExecution execution,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (execution.Status == CentralProcessingGraphExecutionStatus.CancelRequested)
        {
            var canceledJobIds = new List<Guid>();
            foreach (var job in execution.Jobs.Where(job => !IsTerminal(job.Status)))
            {
                if (job.Status == CentralDerivativeJobStatus.Leased &&
                    job.LeaseExpiresAtUtc is { } leaseExpiry && leaseExpiry > now)
                {
                    continue;
                }
                if (job.Status == CentralDerivativeJobStatus.Leased)
                {
                    var attempt = job.Attempts.SingleOrDefault(candidate =>
                        candidate.AttemptNumber == job.AttemptCount &&
                        candidate.Outcome == CentralDerivativeAttemptOutcome.Leased);
                    if (attempt is not null)
                    {
                        attempt.Outcome = CentralDerivativeAttemptOutcome.Canceled;
                        attempt.EndedAtUtc = now;
                        attempt.ReasonCode = "processing.graph.execution-canceled";
                    }
                }
                SetTerminal(job, CentralDerivativeJobStatus.Canceled, "processing.graph.execution-canceled", now);
                job.CancellationRequestedAtUtc ??= execution.CancellationRequestedAtUtc ?? now;
                job.CancellationRequestedBy ??= "processing-graph";
                canceledJobIds.Add(job.Id);
            }
            if (canceledJobIds.Count > 0)
            {
                var slots = await dbContext.CentralTransientValidationIdentitySlots
                    .Where(slot => canceledJobIds.Contains(slot.CentralDerivativeJobId) &&
                        slot.State == CentralTransientValidationIdentitySlotState.Reserved)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                foreach (var slot in slots)
                {
                    slot.State = CentralTransientValidationIdentitySlotState.Unused;
                }
            }
        }
        else if (execution.Status == CentralProcessingGraphExecutionStatus.Pending)
        {
            // Pending -> Running is the only legal way into a completion state; both the EF lifecycle guards and
            // TR_CentralProcessingGraphExecutions_IdentityImmutable reject Pending -> Completed in one row update.
            // Record the start as its own durable step inside the caller's transaction so a graph whose nodes all
            // terminalize in this same pass (skipped/no-input/pre-failed) converges through a legal Running row.
            execution.Status = CentralProcessingGraphExecutionStatus.Running;
            execution.StartedAtUtc = now;
            execution.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var transientJob in execution.Jobs.Where(job => job.Status == CentralDerivativeJobStatus.Waiting &&
                     string.Equals(job.RecipeName, CentralTransientRuntime.RecipeName, StringComparison.Ordinal)))
        {
            await MaterializeTransientOptionsAsync(transientJob, now, cancellationToken).ConfigureAwait(false);
        }
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var job in execution.Jobs.OrderBy(static job => job.GraphNodeOrdinal))
            {
                if (job.Status != CentralDerivativeJobStatus.Waiting || job.WaitKind == CentralDerivativeWaitKind.Window)
                {
                    continue;
                }
                var waiting = false;
                var failed = false;
                foreach (var dependency in job.Dependencies.OrderBy(static item => item.Ordinal))
                {
                    if (dependency.Kind is ProcessingGraphDependencyKind.Outcome or ProcessingGraphDependencyKind.Ordering)
                    {
                        if (dependency.ProducerJob is { } outcomeProducer)
                        {
                            if (!IsTerminal(outcomeProducer.Status))
                            {
                                waiting = true;
                            }
                            else if (dependency.Required && IsFailure(outcomeProducer.Status))
                            {
                                failed = true;
                            }
                        }
                        continue;
                    }
                    var requirements = job.InputRequirements.Where(requirement =>
                        requirement.GraphDependencyId == dependency.Id).ToArray();
                    if (dependency.ProducerSource?.Artifact is { } source)
                    {
                        foreach (var requirement in requirements)
                        {
                            MaterializeArtifactInput(job, requirement, source, now);
                        }
                        continue;
                    }
                    if (dependency.ProducerJob is not { } producer || !IsTerminal(producer.Status))
                    {
                        waiting = true;
                        continue;
                    }
                    var output = producer.Outputs.SingleOrDefault(slot => slot.Ordinal == dependency.ProducerOutputOrdinal);
                    if (producer.Status == CentralDerivativeJobStatus.Completed && output?.ResultArtifact is { } artifact)
                    {
                        foreach (var requirement in requirements)
                        {
                            if (requirement.SourceKind == CentralDerivativeInputSourceKind.Artifact)
                            {
                                MaterializeArtifactInput(job, requirement, artifact, now);
                            }
                            else
                            {
                                await MaterializeCanonicalInputAsync(job, requirement, artifact, now, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                    else if (dependency.Required)
                    {
                        failed = true;
                    }
                    else
                    {
                        foreach (var requirement in requirements.Where(static requirement =>
                                     requirement.ResolutionState == CentralDerivativeInputResolutionState.Waiting))
                        {
                            requirement.ResolutionState = CentralDerivativeInputResolutionState.Missing;
                            requirement.ResolutionReasonCode = "processing.graph.optional-dependency-omitted";
                            requirement.ResolvedAtUtc = now;
                        }
                    }
                }
                if (failed)
                {
                    SetTerminal(job, CentralDerivativeJobStatus.TerminalFailure,
                        "processing.graph.required-predecessor-failed", now);
                    changed = true;
                    continue;
                }
                var unavailableRequirement = job.InputRequirements.FirstOrDefault(requirement => requirement.IsRequired &&
                    requirement.ResolutionState is CentralDerivativeInputResolutionState.Missing or
                        CentralDerivativeInputResolutionState.Incompatible);
                if (unavailableRequirement is not null)
                {
                    SetTerminal(job, CentralDerivativeJobStatus.Skipped,
                        unavailableRequirement.ResolutionReasonCode ?? "processing.graph.required-input-unavailable", now);
                    changed = true;
                    continue;
                }
                if (waiting || job.InputRequirements.Any(requirement => requirement.IsRequired &&
                        requirement.ResolutionState == CentralDerivativeInputResolutionState.Waiting))
                {
                    continue;
                }
                if (job.Inputs.Count == 0 && job.CanonicalInputs.Count == 0)
                {
                    SetTerminal(job, CentralDerivativeJobStatus.Skipped, "processing.graph.no-input", now);
                    changed = true;
                    continue;
                }
                job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(
                    job.Inputs, job.CanonicalInputs);
                job.ResolutionCompletedAtUtc = now;
                job.Status = CentralDerivativeJobStatus.Pending;
                job.AvailableAtUtc = now;
                job.StateReasonCode = null;
                job.UpdatedAtUtc = now;
                changed = true;
            }
        }
        if (!execution.Jobs.All(job => IsTerminal(job.Status)))
        {
            if (execution.UpdatedAtUtc < now)
            {
                // Rotate non-progressing executions behind older durable recovery work.
                execution.UpdatedAtUtc = now;
            }
            return;
        }
        // Nodes terminalized in this pass (no-input Skipped, required-predecessor-failed) are persisted as their own
        // durable step before the execution row terminalizes. TR_CentralProcessingGraphExecutions_IdentityImmutable
        // rejects a terminal execution whose node rows are still nonterminal, so trigger acceptance must not depend on
        // the incidental statement order EF Core chooses for unrelated updates inside one SaveChanges batch.
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (execution.Status == CentralProcessingGraphExecutionStatus.CancelRequested)
        {
            execution.Status = CentralProcessingGraphExecutionStatus.Canceled;
        }
        else if (execution.Jobs.Any(job => job.GraphFailurePolicy == ProcessingGraphNodeFailurePolicy.Required &&
                     IsFailure(job.Status)))
        {
            execution.Status = CentralProcessingGraphExecutionStatus.Failed;
        }
        else if (execution.Jobs.Any(job => job.GraphFailurePolicy == ProcessingGraphNodeFailurePolicy.Optional &&
                     IsFailure(job.Status)))
        {
            execution.Status = CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures;
        }
        else
        {
            execution.Status = CentralProcessingGraphExecutionStatus.Completed;
        }
        // Completed/CompletedWithOptionalFailures/Failed are reachable only through Running, which stamped
        // StartedAtUtc durably above; an execution canceled before it started keeps StartedAtUtc null.
        execution.CompletedAtUtc = now;
        execution.UpdatedAtUtc = now;
    }

    private async Task MaterializeCanonicalInputAsync(
        CentralDerivativeJob job,
        CentralDerivativeJobInputRequirement requirement,
        CentralArtifact artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (requirement.ResolutionState != CentralDerivativeInputResolutionState.Waiting)
        {
            return;
        }
        if (artifact.ByteLength > ProcessingGraphJson.MaximumDocumentBytes)
        {
            throw new CentralDerivativeJobStateException("A graph canonical dependency exceeds the bounded payload limit.");
        }
        var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        await using var destination = new MemoryStream(checked((int)artifact.ByteLength));
        await objectReader.CopyToAsync(snapshot, destination, null, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(destination.ToArray());
        var json = CaptureContractJson.Canonicalize(document.RootElement).GetRawText();
        var bytes = Encoding.UTF8.GetBytes(json);
        var input = new CentralDerivativeJobCanonicalInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = requirement.Ordinal,
            SchemaVersion = artifact.StructuredProduct?.ProductSchemaVersion ?? "processing-graph-canonical-v1",
            IdentitySha256 = ProcessingIdentity.ComputePayloadSha256(bytes),
            CanonicalJson = json,
            ByteLength = bytes.Length,
            SelectedAtUtc = now
        };
        job.CanonicalInputs.Add(input);
        dbContext.CentralDerivativeJobCanonicalInputs.Add(input);
        requirement.CanonicalInput = input;
        requirement.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
        requirement.ResolutionReasonCode = null;
        requirement.ResolvedAtUtc = now;
    }

    private async Task MaterializeTransientOptionsAsync(
        CentralDerivativeJob job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requirement = job.InputRequirements.SingleOrDefault(item =>
            item.BindingName == "transient-extraction-options" &&
            item.ResolutionState == CentralDerivativeInputResolutionState.Waiting);
        if (requirement is null)
        {
            return;
        }
        var executionOptionsJson = await dbContext.CentralTransientValidationJobs.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == job.Id)
            .Select(item => item.ExecutionOptionsJson)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var extraction = CentralTransientExecutionOptionsJson.Deserialize(executionOptionsJson ??
            throw new CentralDerivativeJobStateException("The transient execution options are unavailable.")).Extraction;
        var canonicalJson = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            schema = CentralTransientRuntime.ExtractionOptionsSchemaVersion,
            options = extraction
        })).GetRawText();
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        var input = new CentralDerivativeJobCanonicalInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = requirement.Ordinal,
            SchemaVersion = CentralTransientRuntime.ExtractionOptionsSchemaVersion,
            IdentitySha256 = ProcessingIdentity.ComputePayloadSha256(bytes),
            CanonicalJson = canonicalJson,
            ByteLength = bytes.Length,
            SelectedAtUtc = now
        };
        job.CanonicalInputs.Add(input);
        dbContext.CentralDerivativeJobCanonicalInputs.Add(input);
        requirement.CanonicalInput = input;
        requirement.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
        requirement.ResolutionReasonCode = null;
        requirement.ResolvedAtUtc = now;
    }

    private async Task<GraphEnvironmentalInputs?> FreezeEnvironmentalInputsAsync(
        ProcessingGraphExecutionPlan plan,
        CentralFrame frame,
        CancellationToken cancellationToken)
    {
        if (!plan.Nodes.Any(node => node.Definition.StepAlias is BuiltInProcessingRecipes.CloudAssessment or
                BuiltInProcessingRecipes.WeatherCloudOverlay))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(frame.RigId))
        {
            return new(null, null, null, null, "processing.graph.clear-reference-unavailable");
        }
        var clearReference = await dbContext.CentralClearReferenceDesignations.AsNoTracking()
            .Where(item => item.RegistrationId == frame.RegistrationId && item.RigId == frame.RigId &&
                item.Artifact!.ObjectState == CentralArtifactObjectState.Available &&
                item.Artifact.ReconstructionState == CentralReconstructionState.Complete)
            .Select(item => new
            {
                item.CentralArtifactId,
                item.Artifact!.ArtifactId,
                item.Artifact.Variant
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (clearReference is null)
        {
            return new(null, null, null, null, "processing.graph.clear-reference-unavailable");
        }
        if (environmentalQuery is null)
        {
            return new(
                clearReference.CentralArtifactId,
                clearReference.ArtifactId,
                clearReference.Variant,
                null,
                "processing.graph.environment-unavailable");
        }
        await AcquireEnvironmentalRetentionLockAsync(cancellationToken).ConfigureAwait(false);
        var selection = await SelectPrecipitationAsync(frame.Id, cancellationToken).ConfigureAwait(false);
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            ParseSolarRegime(frame.CycleEvidenceJson),
            selection.Match.Status,
            selection.Match.Observation?.ObservationId,
            selection.ContentSha256,
            IsPrecipitationDetected(selection.Match.Observation));
        var canonicalJson = CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(environment)).GetRawText();
        var payload = Encoding.UTF8.GetBytes(canonicalJson);
        return new(
            clearReference.CentralArtifactId,
            clearReference.ArtifactId,
            clearReference.Variant,
            new GraphCanonicalInput(
                CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                ProcessingIdentity.ComputePayloadSha256(payload),
                canonicalJson,
                payload.Length,
                selection.RecordId),
            null);
    }

    private static List<ProcessingAuxiliaryInput> CreateExternalAuxiliaries(
        ProcessingGraphPlanNode node,
        GraphEnvironmentalInputs? inputs)
    {
        if (inputs is null)
        {
            return [];
        }
        if (string.Equals(node.Definition.StepAlias, BuiltInProcessingRecipes.CloudAssessment, StringComparison.Ordinal))
        {
            var result = new List<ProcessingAuxiliaryInput>(2);
            if (inputs.ClearReferenceArtifactId is { } clearReferenceArtifactId)
            {
                result.Add(new(
                    "clear-reference",
                    ProcessingAuxiliaryInputKind.Artifact,
                    ProcessingInputSelector.Raw(inputs.ClearReferenceVariant),
                    ArtifactId: clearReferenceArtifactId));
            }
            if (inputs.Environment is { } environment)
            {
                result.Add(new(
                    "environment",
                    ProcessingAuxiliaryInputKind.CanonicalJson,
                    SchemaVersion: environment.SchemaVersion,
                    IdentitySha256: environment.IdentitySha256));
            }
            return result;
        }
        return string.Equals(node.Definition.StepAlias, BuiltInProcessingRecipes.WeatherCloudOverlay,
                StringComparison.Ordinal) && inputs.Environment is { } overlayEnvironment
            ? [new ProcessingAuxiliaryInput(
                "environment",
                ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: overlayEnvironment.SchemaVersion,
                IdentitySha256: overlayEnvironment.IdentitySha256)]
            : [];
    }

    private static void AddExternalInputRequirements(
        CentralDerivativeJob job,
        ProcessingGraphPlanNode node,
        CentralFrame frame,
        DateTimeOffset now)
    {
        if (string.Equals(node.Definition.StepAlias, BuiltInProcessingRecipes.CloudAssessment, StringComparison.Ordinal))
        {
            AddRequirement("clear-reference", CentralDerivativeInputSourceKind.Artifact);
            AddRequirement("environment", CentralDerivativeInputSourceKind.EnvironmentalObservation);
        }
        else if (string.Equals(node.Definition.StepAlias, BuiltInProcessingRecipes.WeatherCloudOverlay,
                     StringComparison.Ordinal))
        {
            AddRequirement("environment", CentralDerivativeInputSourceKind.EnvironmentalObservation);
        }

        void AddRequirement(string bindingName, CentralDerivativeInputSourceKind sourceKind)
            => job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Ordinal = job.InputRequirements.Count,
                BindingName = bindingName,
                SourceKind = sourceKind,
                IsRequired = true,
                SelectorJson = "{}",
                CompatibilityMode = CentralDerivativeCompatibilityMode.None,
                ExpectedAgentId = frame.AgentId,
                ExpectedRigId = frame.RigId,
                ExpectedCaptureSequence = frame.CaptureSequence,
                ResolutionState = CentralDerivativeInputResolutionState.Waiting,
                ResolvedAtUtc = null
            });
    }

    private async Task MaterializeExternalInputsAsync(
        CentralProcessingGraphExecution execution,
        GraphEnvironmentalInputs? inputs,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var job in execution.Jobs.Where(job => job.Status == CentralDerivativeJobStatus.Waiting))
        {
            if (string.Equals(job.RecipeName, BuiltInProcessingRecipes.CloudAssessment, StringComparison.Ordinal))
            {
                var clearRequirement = job.InputRequirements.Single(requirement =>
                    requirement.BindingName == "clear-reference");
                if (inputs?.ClearReferenceCentralArtifactId is { } clearReferenceId)
                {
                    var clearReference = await dbContext.CentralArtifacts.Include(artifact => artifact.Frame)
                        .SingleAsync(artifact => artifact.Id == clearReferenceId, cancellationToken).ConfigureAwait(false);
                    MaterializeArtifactInput(job, clearRequirement, clearReference, now);
                }
                else
                {
                    SetMissing(clearRequirement, inputs?.MissingReasonCode ??
                        "processing.graph.clear-reference-unavailable", now);
                }
                MaterializeEnvironment(job, inputs, now);
            }
            else if (string.Equals(job.RecipeName, BuiltInProcessingRecipes.WeatherCloudOverlay,
                         StringComparison.Ordinal))
            {
                MaterializeEnvironment(job, inputs, now);
            }
        }
    }

    private void MaterializeEnvironment(
        CentralDerivativeJob job,
        GraphEnvironmentalInputs? inputs,
        DateTimeOffset now)
    {
        var requirement = job.InputRequirements.Single(item => item.BindingName == "environment");
        if (inputs?.Environment is not { } environment)
        {
            SetMissing(requirement, inputs?.MissingReasonCode ?? "processing.graph.environment-unavailable", now);
            return;
        }
        var input = new CentralDerivativeJobCanonicalInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = requirement.Ordinal,
            SchemaVersion = environment.SchemaVersion,
            IdentitySha256 = environment.IdentitySha256,
            CanonicalJson = environment.CanonicalJson,
            ByteLength = environment.ByteLength,
            EnvironmentalObservationRecordId = environment.EnvironmentalObservationRecordId,
            SelectedAtUtc = now
        };
        job.CanonicalInputs.Add(input);
        dbContext.CentralDerivativeJobCanonicalInputs.Add(input);
        requirement.CanonicalInput = input;
        requirement.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
        requirement.ResolvedAtUtc = now;
    }

    private static void SetMissing(
        CentralDerivativeJobInputRequirement requirement,
        string reasonCode,
        DateTimeOffset now)
    {
        requirement.ResolutionState = CentralDerivativeInputResolutionState.Missing;
        requirement.ResolutionReasonCode = reasonCode;
        requirement.ResolvedAtUtc = now;
    }

    private async Task<EnvironmentalObservationSelection> SelectPrecipitationAsync(
        Guid frameId,
        CancellationToken cancellationToken)
    {
        var rain = await environmentalQuery!.SelectFrameAsync(
            frameId, CreateEnvironmentalSelector(EnvironmentalObservationKind.RainState), cancellationToken)
            .ConfigureAwait(false);
        return rain.Match.Status != EnvironmentalObservationMatchStatus.Missing
            ? rain
            : await environmentalQuery.SelectFrameAsync(
                frameId,
                CreateEnvironmentalSelector(EnvironmentalObservationKind.PrecipitationRate),
                cancellationToken).ConfigureAwait(false);
    }

    private async Task AcquireEnvironmentalRetentionLockAsync(CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return;
        }
        var lockResource = EnvironmentalObservationLockNames.Retention;
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {lockResource},
                @LockMode = 'Shared',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000;
            IF @result < 0
                THROW 51007, 'Could not acquire the environmental retention lock.', 1;
            """, cancellationToken).ConfigureAwait(false);
    }

    private static EnvironmentalObservationSelector CreateEnvironmentalSelector(EnvironmentalObservationKind kind)
        => new(kind, EnvironmentalSourcePriority, EnvironmentalQualities, TimeSpan.FromHours(24));

    internal static bool IsPrecipitationDetected(EnvironmentalObservationV1? observation)
        => observation?.Value.Kind switch
        {
            EnvironmentalObservationKind.RainState => observation.Value.BooleanValue == true,
            EnvironmentalObservationKind.PrecipitationRate => observation.Value.NumericValue > 0,
            _ => false
        };

    internal static CaptureSolarRegime? ParseSolarRegime(string? cycleEvidenceJson)
        => string.IsNullOrWhiteSpace(cycleEvidenceJson)
            ? null
            : JsonSerializer.Deserialize<CaptureCycleEvidence>(
                cycleEvidenceJson, CycleEvidenceSerializerOptions)?.SolarRegime;

    private void MaterializeArtifactInput(
        CentralDerivativeJob job,
        CentralDerivativeJobInputRequirement requirement,
        CentralArtifact artifact,
        DateTimeOffset now)
    {
        if (requirement.ResolutionState != CentralDerivativeInputResolutionState.Waiting)
        {
            return;
        }
        var compatibility = requirement.CompatibilityMode == CentralDerivativeCompatibilityMode.None
            ? CentralDerivativeWindowCompatibility.EmptySnapshot
            : CentralDerivativeWindowCompatibility.CreateSnapshot(artifact);
        var input = new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = requirement.Ordinal,
            CentralArtifactId = artifact.Id,
            Artifact = artifact,
            CaptureSequence = artifact.Frame?.CaptureSequence,
            CompatibilityJson = compatibility.Json,
            CompatibilitySha256 = compatibility.Sha256,
            ByteLength = artifact.ByteLength,
            SelectedAtUtc = now
        };
        job.Inputs.Add(input);
        dbContext.CentralDerivativeJobInputs.Add(input);
        requirement.Input = input;
        requirement.ExpectedCentralArtifactId = artifact.Id;
        requirement.ResolutionState = CentralDerivativeInputResolutionState.Resolved;
        requirement.ResolutionReasonCode = null;
        requirement.ResolvedAtUtc = now;
    }

    private static void SetTerminal(
        CentralDerivativeJob job,
        CentralDerivativeJobStatus status,
        string reason,
        DateTimeOffset now)
    {
        job.Status = status;
        job.StateReasonCode = reason;
        job.LastError = status == CentralDerivativeJobStatus.TerminalFailure ? reason : null;
        job.LastFailedAtUtc = status == CentralDerivativeJobStatus.TerminalFailure ? now : null;
        job.CompletedAtUtc = now;
        job.AvailableAtUtc = null;
        job.LeaseOwner = null;
        job.LeaseToken = null;
        job.LeaseAcquiredAtUtc = null;
        job.LeaseExpiresAtUtc = null;
        job.UpdatedAtUtc = now;
    }

    private ProcessingGraphExecutionPlan CompileAndVerify(CentralProcessingGraphRevision revision)
    {
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(revision.DefinitionJson));
        if (!parsed.IsValid)
        {
            throw new CentralDerivativeJobStateException("The published processing graph definition is invalid.");
        }
        var compiled = LogicHostProcessingGraphAdapter.Compile(parsed.Definition!, nodeRegistry.Capabilities);
        if (!compiled.IsValid || compiled.Plan is not { } plan || !nodeRegistry.Validate(plan) ||
            !string.Equals(plan.DefinitionIdentitySha256, revision.DefinitionIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(plan.PlanIdentitySha256, revision.CentralPlanIdentitySha256, StringComparison.Ordinal))
        {
            throw new CentralDerivativeJobStateException("The published processing graph plan identity is invalid.");
        }
        return plan;
    }

    private async Task<CentralArtifact> LoadArtifactAsync(Guid id, CancellationToken cancellationToken)
        => await dbContext.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.LogicalCameraInstallation)!
                .ThenInclude(installation => installation!.LogicalCamera)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);

    private async Task<CentralProcessingGraphExecution?> LoadExecutionAsync(
        Guid id,
        CancellationToken cancellationToken)
        => await dbContext.CentralProcessingGraphExecutions
            .Include(item => item.Sources).ThenInclude(source => source.Artifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(item => item.Jobs).ThenInclude(job => job.InputRequirements)
            .Include(item => item.Jobs).ThenInclude(job => job.Attempts)
            .Include(item => item.Jobs).ThenInclude(job => job.Inputs)
            .Include(item => item.Jobs).ThenInclude(job => job.CanonicalInputs)
            .Include(item => item.Jobs).ThenInclude(job => job.Outputs).ThenInclude(output => output.ResultArtifact)!
                .ThenInclude(artifact => artifact!.StructuredProduct)
            .Include(item => item.Dependencies).ThenInclude(dependency => dependency.ProducerSource)!
                .ThenInclude(source => source!.Artifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(item => item.Dependencies).ThenInclude(dependency => dependency.ProducerJob)!
                .ThenInclude(job => job!.Outputs).ThenInclude(output => output.ResultArtifact)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);

    private static List<CentralArtifact>? SelectLiveSources(
        ProcessingGraphExecutionPlan plan,
        CentralArtifact incoming)
    {
        var frameArtifacts = incoming.Frame!.Artifacts.Where(IsUsable).ToArray();
        var selected = new List<CentralArtifact>(plan.Sources.Length);
        var incomingUsed = false;
        foreach (var source in plan.Sources)
        {
            var matches = frameArtifacts.Where(artifact => source.Outputs.Any(output => output.Role == artifact.Role))
                .OrderByDescending(artifact => artifact.Id == incoming.Id)
                .ThenBy(artifact => artifact.ArtifactId)
                .ToArray();
            if (matches.Length == 0)
            {
                return null;
            }
            selected.Add(matches[0]);
            incomingUsed |= matches[0].Id == incoming.Id;
        }
        return incomingUsed ? selected : null;
    }

    private static bool SourcesMatch(
        ProcessingGraphExecutionPlan plan,
        CentralArtifact[] sources)
        => plan.Sources.Length == sources.Length && plan.Sources.Select((source, index) =>
            source.Outputs.Any(output => output.Role == sources[index].Role)).All(static matched => matched);

    private static Guid ResolveBindingArtifactId(
        ProcessingGraphInputBinding binding,
        Dictionary<string, CentralProcessingGraphExecutionSource> sources,
        Dictionary<(string ProducerId, int Output), Guid> projectedArtifacts)
        => sources.TryGetValue(binding.ProducerId, out var source)
            ? source.ArtifactId
            : projectedArtifacts.TryGetValue((binding.ProducerId, binding.OutputIndex), out var artifactId)
                ? artifactId
                : Guid.Empty;

    private static ProcessingInputSelector CreateSelector(
        ProcessingGraphInputBinding binding,
        ProcessingGraphExecutionPlan plan,
        Dictionary<string, CentralDerivativeJob> jobs)
    {
        var output = plan.Sources.FirstOrDefault(source => source.Id == binding.ProducerId)?.Outputs[binding.OutputIndex]
            ?? plan.Nodes.First(node => node.Definition.Id == binding.ProducerId).Definition.Outputs[binding.OutputIndex];
        return output.Role switch
        {
            FrameArtifactRole.Raw => ProcessingInputSelector.Raw(),
            FrameArtifactRole.Calibrated => ProcessingInputSelector.Calibrated(),
            FrameArtifactRole.Combined => ProcessingInputSelector.Combined(output.Variant),
            _ => ProcessingInputSelector.RecipeResult(
                output.Role,
                output.Variant,
                jobs.TryGetValue(binding.ProducerId, out var producer)
                    ? producer.ExpectedRecipeIdentitySha256
                    : output.Recipe is null
                        ? new string('0', 64)
                        : CaptureContractJson.ComputeCanonicalJsonSha256(output.Recipe))
        };
    }

    internal static ImmutableArray<int> CreateWindowOffsets(ProcessingGraphWindowRequirement window)
    {
        if (window.Kind == ProcessingGraphWindowKind.Centered)
        {
            var before = window.MaximumInputCount / 2;
            return Enumerable.Range(-before, window.MaximumInputCount).ToImmutableArray();
        }
        return Enumerable.Range(-(window.MaximumInputCount - 1), window.MaximumInputCount).ToImmutableArray();
    }

    internal static CentralDerivativeWindowOutcome? MapMissingOutcome(ProcessingGraphMissingInputOutcome? outcome)
        => outcome switch
        {
            ProcessingGraphMissingInputOutcome.Run => CentralDerivativeWindowOutcome.Run,
            ProcessingGraphMissingInputOutcome.Skip => CentralDerivativeWindowOutcome.Skip,
            ProcessingGraphMissingInputOutcome.Fail => CentralDerivativeWindowOutcome.Fail,
            ProcessingGraphMissingInputOutcome.Quarantine => CentralDerivativeWindowOutcome.Quarantine,
            null => null,
            _ => throw new CentralDerivativeJobStateException("The graph missing-input outcome is invalid.")
        };

    private static string CreateGraphRequestIdentity(
        CentralProcessingGraphExecutionClass executionClass,
        CentralProcessingGraphRevision revision,
        CentralProcessingGraphAssignment? assignment,
        IEnumerable<CentralArtifact> sources,
        CentralProcessingGraphTrigger trigger,
        string actor,
        string idempotencyKey,
        string reasonCode)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-central-processing-graph-request-v1",
            executionClass = executionClass.ToString(),
            revision.Id,
            revision.DefinitionIdentitySha256,
            centralPlanIdentitySha256 = revision.CentralPlanIdentitySha256,
            assignmentId = assignment?.Id,
            sources = sources.Select((artifact, ordinal) => new
            {
                ordinal,
                artifact.Id,
                artifact.ArtifactId,
                artifact.ChecksumSha256
            }).ToArray(),
            trigger = trigger.ToString(),
            actor,
            idempotencyKey,
            reasonCode
        });

    private static string CreateNodeRequestIdentity(
        string graphRequestIdentity,
        string nodeIdentity,
        string recipeIdentity)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-central-processing-graph-node-request-v1",
            graphRequestIdentity,
            nodeIdentity,
            recipeIdentity
        });

    private void AddTransientRuntimeState(
        CentralDerivativeJob job,
        CentralDerivativeRecipe recipe,
        CentralTransientRecipeDefinition transient,
        CentralFrame frame,
        DateTimeOffset now)
    {
        var requirement = new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = job.InputRequirements.Count,
            BindingName = "transient-extraction-options",
            SourceKind = CentralDerivativeInputSourceKind.Canonical,
            IsRequired = true,
            SelectorJson = "{}",
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = frame.AgentId,
            ExpectedRigId = frame.RigId,
            ExpectedCaptureSequence = frame.CaptureSequence,
            ResolutionState = CentralDerivativeInputResolutionState.Waiting
        };
        job.InputRequirements.Add(requirement);
        var validation = new CentralTransientValidationJob
        {
            CentralDerivativeJobId = job.Id,
            Job = job,
            AgentId = frame.AgentId,
            SubmissionSchemaVersion = CentralTransientRuntime.SubmissionSchemaVersion,
            ExecutionOptionsJson = transient.ExecutionOptionsJson,
            ExecutionOptionsIdentitySha256 = transient.ExecutionOptionsIdentitySha256,
            CreatedAtUtc = now
        };
        for (var ordinal = 0; ordinal < transient.IdentitySlotCount; ordinal++)
        {
            validation.IdentitySlots.Add(new CentralTransientValidationIdentitySlot
            {
                CentralDerivativeJobId = job.Id,
                ValidationJob = validation,
                Ordinal = ordinal,
                State = CentralTransientValidationIdentitySlotState.Reserved,
                AgentId = frame.AgentId,
                SubmittedEventId = Guid.NewGuid(),
                CandidateId = Guid.NewGuid(),
                ObservationId = Guid.NewGuid(),
                AssessmentId = Guid.NewGuid()
            });
        }
        validation.SubmissionIdentitySha256 = CreateTransientSubmissionIdentity(job, validation);
        dbContext.CentralTransientValidationJobs.Add(validation);
    }

    private static string CreateTransientSubmissionIdentity(
        CentralDerivativeJob job,
        CentralTransientValidationJob validation)
    {
        var value = string.Join('\n',
            validation.SubmissionSchemaVersion,
            job.RequestIdentitySha256,
            validation.ExecutionOptionsIdentitySha256,
            string.Join('\n', validation.IdentitySlots.OrderBy(static item => item.Ordinal).Select(item => string.Join(':',
                item.Ordinal,
                item.SubmittedEventId.ToString("N"),
                item.CandidateId.ToString("N"),
                item.ObservationId.ToString("N"),
                item.AssessmentId.ToString("N")))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static long? AddSequenceOffset(long? sequence, int offset)
    {
        try
        {
            return sequence.HasValue ? checked(sequence.Value + offset) : null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool IsProductDependency(ProcessingGraphDependencyKind kind)
        => kind is ProcessingGraphDependencyKind.Artifact or ProcessingGraphDependencyKind.CanonicalJson or
            ProcessingGraphDependencyKind.Annotation;

    private static int CountDependencyEdges(ProcessingGraphExecutionPlan plan)
        => plan.Nodes.Sum(node => node.Definition.Dependencies.Sum(dependency =>
        {
            var bindingCount = node.InputBindings.Count(binding => string.Equals(
                binding.ProducerId, dependency.ProducerId, StringComparison.Ordinal));
            return Math.Max(1, bindingCount);
        }));

    private static bool IsUsable(CentralArtifact artifact)
        => artifact.ObjectState == CentralArtifactObjectState.Available &&
            artifact.ReconstructionState == CentralReconstructionState.Complete;

    private static bool IsTerminal(CentralProcessingGraphExecutionStatus status)
        => TerminalExecutionStatuses.Contains(status);

    private static bool IsTerminal(CentralDerivativeJobStatus status)
        => status is CentralDerivativeJobStatus.Completed or CentralDerivativeJobStatus.TerminalFailure or
            CentralDerivativeJobStatus.Canceled or CentralDerivativeJobStatus.Skipped or
            CentralDerivativeJobStatus.Quarantined or CentralDerivativeJobStatus.Superseded;

    private static bool IsFailure(CentralDerivativeJobStatus status)
        => status is CentralDerivativeJobStatus.TerminalFailure or CentralDerivativeJobStatus.Canceled or
            CentralDerivativeJobStatus.Quarantined or CentralDerivativeJobStatus.Superseded;

    private static JsonSerializerOptions CreateCycleEvidenceSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private static readonly CentralProcessingGraphExecutionStatus[] TerminalExecutionStatuses =
    [
        CentralProcessingGraphExecutionStatus.Completed,
        CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures,
        CentralProcessingGraphExecutionStatus.Failed,
        CentralProcessingGraphExecutionStatus.Canceled,
        CentralProcessingGraphExecutionStatus.Superseded
    ];

    private sealed record GraphEnvironmentalInputs(
        Guid? ClearReferenceCentralArtifactId,
        Guid? ClearReferenceArtifactId,
        string? ClearReferenceVariant,
        GraphCanonicalInput? Environment,
        string? MissingReasonCode);

    private sealed record GraphCanonicalInput(
        string SchemaVersion,
        string IdentitySha256,
        string CanonicalJson,
        int ByteLength,
        Guid? EnvironmentalObservationRecordId);

    private static partial class Log
    {
        [LoggerMessage(2150, LogLevel.Error,
            "Processing graph convergence failed for ExecutionId={ExecutionId}.")]
        public static partial void ConvergenceFailed(ILogger logger, Exception exception, Guid executionId);

        [LoggerMessage(2151, LogLevel.Warning,
            "Processing graph recovery rotation failed for ExecutionId={ExecutionId}.")]
        public static partial void RotationFailed(ILogger logger, Exception exception, Guid executionId);
    }
}
