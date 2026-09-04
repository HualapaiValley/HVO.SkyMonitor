using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralDerivativeExecutionResult(
    ProcessingOutcomeStatus Status,
    Guid? ArtifactId,
    string? ReasonCode);

/// <summary>
/// The outcome of the pre-execution checks shared by the in-process slot and the runner claim path. When
/// <see cref="Resolved"/> is set the job already reached a durable state (integrity failure, recovered output, skip)
/// and nothing must execute; when <see cref="InProcessOnly"/> is set the recipe needs LogicHost state and cannot leave
/// the process.
/// </summary>
internal sealed record CentralDerivativeExecutionPreparation(
    CentralDerivativeExecutionResult? Resolved,
    bool InProcessOnly,
    ProcessingInputSelector? Selector,
    JsonElement Options,
    ProcessingAnnotationInput? Annotation,
    IReadOnlyList<ProcessingAuxiliaryInput>? CanonicalInputs);

internal interface ICentralDerivativeJobExecutor
{
    Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);
}

/// <summary>
/// The two durable halves of derivative execution shared by the in-process slot and the runner protocol: the checks
/// that run before any recipe executes and the validation/publication that runs after. A remote runner only ever
/// executes the recipe kernel between them.
/// </summary>
internal interface ICentralDerivativeExecutionPipeline
{
    /// <summary>Runs the durable pre-execution checks (frozen plan, identities, canonical inputs, recovery, annotation).</summary>
    Task<CentralDerivativeExecutionPreparation> PrepareAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);

    /// <summary>Validates and publishes a recipe outcome under the lease exactly as the in-process path does.</summary>
    Task<CentralDerivativeExecutionResult> PublishAsync(
        CentralDerivativeJobLease lease,
        ProcessingOutcome outcome,
        long inputBytes,
        TimeSpan recipeDuration,
        CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobExecutor(
    ICentralDerivativeJobInputReader inputReader,
    LogicHostRecipeExecutionAdapter recipeAdapter,
    ICentralDerivativeOutputWriter outputWriter,
    ICentralDerivativeJobService jobService,
    ICentralDerivativeJobScheduler jobScheduler,
    ICentralTransientValidationExecutor transientExecutor,
    ICentralTransientDerivativeExecutor transientDerivativeExecutor,
    ICentralTransientReprocessingExecutor transientReprocessingExecutor,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider) : ICentralDerivativeJobExecutor, ICentralDerivativeExecutionPipeline
{
    private const double MaximumLabelMagnitude = 2.5;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task<CentralDerivativeExecutionResult> ExecuteAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var preparation = await PrepareAsync(lease, cancellationToken).ConfigureAwait(false);
        if (preparation.Resolved is { } resolved)
        {
            return resolved;
        }
        if (preparation.InProcessOnly)
        {
            if (string.Equals(lease.RecipeName, CentralTransientRuntime.RecipeName, StringComparison.Ordinal))
            {
                return await transientExecutor.ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            if (string.Equals(lease.RecipeName, CentralTransientDerivativeRuntime.RecipeName, StringComparison.Ordinal))
            {
                return await transientDerivativeExecutor.ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            return await transientReprocessingExecutor.ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        var input = await inputReader.ReadAsync(lease, cancellationToken).ConfigureAwait(false);
        if (string.Equals(lease.RecipeName, BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal)
            && preparation.Annotation is null)
        {
            await jobService.SkipAsync(
                lease.JobId, lease.LeaseToken, ProcessingReasonCodes.MissingAnnotation, cancellationToken)
                .ConfigureAwait(false);
            RecordPinRelease(lease, "skipped");
            return new CentralDerivativeExecutionResult(
                ProcessingOutcomeStatus.Skipped, null, ProcessingReasonCodes.MissingAnnotation);
        }
        var started = timeProvider.GetTimestamp();
        ProcessingOutcome outcome;
        using (telemetry.StartStage("execute", lease.RecipeName))
        {
            outcome = await recipeAdapter.ExecuteAsync(
                input.ProcessingInputs,
                lease.RecipeName,
                preparation.Options,
                preparation.Selector!,
                lease.TargetVariant,
                preparation.Annotation,
                preparation.CanonicalInputs,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        var duration = timeProvider.GetElapsedTime(started);
        telemetry.RecordStage("execute", lease.RecipeName, GetOutcome(outcome.Status), duration);
        return await PublishAsync(lease, outcome, input.ByteLength, duration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CentralDerivativeExecutionPreparation> PrepareAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!HasValidFrozenGraphPlan(lease))
        {
            const string reason = "processing.graph.frozen-plan-integrity-failed";
            await jobService.FailAsync(
                lease.JobId, lease.LeaseToken, reason, retryable: false, cancellationToken).ConfigureAwait(false);
            return Resolved(new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.TerminalFailure, null, reason));
        }
        if (IsInProcessOnlyRecipe(lease.RecipeName))
        {
            return new CentralDerivativeExecutionPreparation(null, true, null, default, null, null);
        }
        using var optionsDocument = JsonDocument.Parse(lease.RecipeOptionsJson);
        var selector = JsonSerializer.Deserialize<ProcessingInputSelector>(lease.InputSelectorJson, SerializerOptions)
            ?? throw new CentralDerivativeJobStateException("The derivative input selector is invalid.");
        var currentRequestedIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
            lease.RecipeName,
            optionsDocument.RootElement,
            selector).IdentitySha256;
        if (!string.Equals(
            currentRequestedIdentity,
            lease.RequestedRecipeIdentitySha256,
            StringComparison.OrdinalIgnoreCase))
        {
            const string reason = "processing.recipe-identity-mismatch";
            await jobService.FailAsync(
                lease.JobId, lease.LeaseToken, reason, retryable: false, cancellationToken).ConfigureAwait(false);
            RecordPinRelease(lease, "terminal");
            return Resolved(new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.TerminalFailure, null, reason));
        }
        var canonicalInputs = CreateCanonicalInputs(lease);
        if (canonicalInputs is null)
        {
            const string reason = "processing.canonical-input-integrity-failed";
            await jobService.FailAsync(
                lease.JobId, lease.LeaseToken, reason, retryable: false, cancellationToken).ConfigureAwait(false);
            RecordPinRelease(lease, "terminal");
            return Resolved(new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.TerminalFailure, null, reason));
        }
        IReadOnlyList<Guid>? recoveredArtifactIds;
        var recoveryStarted = timeProvider.GetTimestamp();
        using (telemetry.StartStage("recover", lease.RecipeName))
        {
            recoveredArtifactIds = await outputWriter.TryCompletePendingSetAsync(lease, cancellationToken)
                .ConfigureAwait(false);
        }
        if (recoveredArtifactIds is { Count: > 0 })
        {
            foreach (var recoveredArtifactId in recoveredArtifactIds)
            {
                await jobScheduler.EnsureRequiredJobsAsync(
                    lease.SourceDevicePublicId,
                    recoveredArtifactId,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }
            telemetry.RecordStage(
                "recover", lease.RecipeName, "adopted", timeProvider.GetElapsedTime(recoveryStarted));
            telemetry.RecordRecovery("adopted");
            return Resolved(new CentralDerivativeExecutionResult(
                ProcessingOutcomeStatus.Produced, recoveredArtifactIds[0], "derivative.output-recovered"));
        }
        telemetry.RecordStage(
            "recover", lease.RecipeName, "empty", timeProvider.GetElapsedTime(recoveryStarted));
        // A missing annotation is decided only after the inputs were read (in process) or fetched (runner), so an
        // unavailable input is surfaced and suspends the job before the recipe can skip; the kernel skips a null
        // annotation on the runner path with the same reason code.
        var annotation = string.Equals(lease.RecipeName, BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal)
            ? CreateAnnotation(lease.SceneProvenanceJson)
            : null;
        return new CentralDerivativeExecutionPreparation(
            null, false, selector, optionsDocument.RootElement.Clone(), annotation, canonicalInputs);
    }

    public async Task<CentralDerivativeExecutionResult> PublishAsync(
        CentralDerivativeJobLease lease,
        ProcessingOutcome outcome,
        long inputBytes,
        TimeSpan recipeDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(outcome);
        if (string.Equals(lease.RecipeName, BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal)
            && CreateAnnotation(lease.SceneProvenanceJson) is null)
        {
            // Authoritative on both paths: an annotation job without frozen provenance is skipped, whatever the
            // kernel reported (the kernel fails a null annotation terminally). Inputs were already read or fetched.
            outcome = ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingAnnotation);
        }
        switch (outcome.Status)
        {
            case ProcessingOutcomeStatus.Produced:
                if (outcome.Products.Count == 0)
                {
                    throw new CentralDerivativeJobStateException("A produced derivative outcome must contain a product.");
                }
                if (RequiresBoundExpectedIdentity(lease) && outcome.Products.Any(product => !string.Equals(
                        product.Recipe.IdentitySha256,
                        lease.ExpectedRecipeIdentitySha256,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    const string reason = "processing.recipe-identity-mismatch";
                    await jobService.FailAsync(
                        lease.JobId, lease.LeaseToken, reason, retryable: false, cancellationToken).ConfigureAwait(false);
                    RecordPinRelease(lease, "terminal");
                    return new CentralDerivativeExecutionResult(ProcessingOutcomeStatus.TerminalFailure, null, reason);
                }
                var publishStarted = timeProvider.GetTimestamp();
                IReadOnlyList<Guid> artifactIds;
                using (telemetry.StartStage("publish", lease.RecipeName))
                {
                    artifactIds = await outputWriter.PersistSetAsync(
                        lease, outcome.Products, inputBytes, recipeDuration, cancellationToken).ConfigureAwait(false);
                }
                foreach (var artifactId in artifactIds)
                {
                    await jobScheduler.EnsureRequiredJobsAsync(
                        lease.SourceDevicePublicId,
                        artifactId,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                }
                telemetry.RecordStage(
                    "publish",
                    lease.RecipeName,
                    "completed",
                    timeProvider.GetElapsedTime(publishStarted),
                    outcome.Products.Sum(product => (long)product.Payload.Length));
                return new CentralDerivativeExecutionResult(outcome.Status, artifactIds[0], null);
            case ProcessingOutcomeStatus.Skipped:
                await jobService.SkipAsync(
                    lease.JobId, lease.LeaseToken, outcome.ReasonCode!, cancellationToken).ConfigureAwait(false);
                RecordPinRelease(lease, "skipped");
                return new CentralDerivativeExecutionResult(outcome.Status, null, outcome.ReasonCode);
            case ProcessingOutcomeStatus.RetryableFailure:
                await jobService.FailAsync(
                    lease.JobId, lease.LeaseToken, outcome.ReasonCode!, retryable: true, cancellationToken)
                    .ConfigureAwait(false);
                return new CentralDerivativeExecutionResult(outcome.Status, null, outcome.ReasonCode);
            case ProcessingOutcomeStatus.TerminalFailure:
                await jobService.FailAsync(
                    lease.JobId, lease.LeaseToken, outcome.ReasonCode!, retryable: false, cancellationToken)
                    .ConfigureAwait(false);
                RecordPinRelease(lease, "terminal");
                return new CentralDerivativeExecutionResult(outcome.Status, null, outcome.ReasonCode);
            default:
                throw new InvalidOperationException("The processing outcome status is unsupported.");
        }
    }

    /// <summary>Transient runtime recipes read and write LogicHost transient state and never leave the process.</summary>
    internal static bool IsInProcessOnlyRecipe(string recipeName)
        => string.Equals(recipeName, CentralTransientRuntime.RecipeName, StringComparison.Ordinal)
            || string.Equals(recipeName, CentralTransientDerivativeRuntime.RecipeName, StringComparison.Ordinal)
            || string.Equals(recipeName, CentralTransientReprocessingRuntime.RecipeName, StringComparison.Ordinal);

    private static CentralDerivativeExecutionPreparation Resolved(CentralDerivativeExecutionResult result)
        => new(result, false, null, default, null, null);

    private static List<ProcessingAuxiliaryInput>? CreateCanonicalInputs(CentralDerivativeJobLease lease)
    {
        if (lease.CanonicalInputs is not { Count: > 0 })
        {
            return [];
        }
        var inputs = new List<ProcessingAuxiliaryInput>(lease.CanonicalInputs.Count);
        foreach (var input in lease.CanonicalInputs.OrderBy(item => item.Ordinal))
        {
            var payload = Encoding.UTF8.GetBytes(input.CanonicalJson);
            if (payload.Length != input.ByteLength ||
                !string.Equals(
                    ProcessingIdentity.ComputePayloadSha256(payload),
                    input.IdentitySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            inputs.Add(new ProcessingAuxiliaryInput(
                input.BindingName,
                ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: input.SchemaVersion,
                IdentitySha256: input.IdentitySha256,
                Payload: payload));
        }
        return inputs;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "All malformed durable frozen-plan data must produce a terminal integrity result.")]
    internal static bool HasValidFrozenGraphPlan(CentralDerivativeJobLease lease)
    {
        if (lease.GraphExecutionId is null)
        {
            return true;
        }
        if (lease.GraphRevisionId is null || string.IsNullOrWhiteSpace(lease.GraphNodeId) ||
            lease.GraphNodeOrdinal is null || string.IsNullOrWhiteSpace(lease.SharedNodePlanIdentitySha256) ||
            string.IsNullOrWhiteSpace(lease.FrozenNodePlanJson) ||
            string.IsNullOrWhiteSpace(lease.CentralPlanIdentitySha256) ||
            string.IsNullOrWhiteSpace(lease.FrozenCentralPlanJson) ||
            string.IsNullOrWhiteSpace(lease.GraphDefinitionIdentitySha256) ||
            string.IsNullOrWhiteSpace(lease.FrozenDefinitionJson))
        {
            return false;
        }
        try
        {
            using var frozenNode = JsonDocument.Parse(lease.FrozenNodePlanJson);
            using var frozenPlan = JsonDocument.Parse(lease.FrozenCentralPlanJson);
            var parsedDefinition = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(lease.FrozenDefinitionJson));
            if (!string.Equals(
                    CaptureContractJson.Canonicalize(frozenNode.RootElement).GetRawText(),
                    lease.FrozenNodePlanJson,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    CaptureContractJson.Canonicalize(frozenPlan.RootElement).GetRawText(),
                    lease.FrozenCentralPlanJson,
                    StringComparison.Ordinal) ||
                !TryReadString(frozenNode.RootElement, "schema", out var nodeSchema) ||
                nodeSchema != "hvo-logic-host-processing-graph-node-v1" ||
                !TryReadString(frozenNode.RootElement, "identitySha256", out var nodeIdentity) ||
                !string.Equals(nodeIdentity, lease.SharedNodePlanIdentitySha256, StringComparison.Ordinal) ||
                !frozenNode.RootElement.TryGetProperty("definition", out var definition) ||
                !TryReadString(definition, "id", out var nodeId) || nodeId != lease.GraphNodeId ||
                !TryReadString(frozenPlan.RootElement, "schema", out var planSchema) ||
                planSchema != "hvo-logic-host-processing-graph-plan-v1" ||
                !TryReadString(frozenPlan.RootElement, "definitionIdentitySha256", out var definitionIdentity) ||
                !string.Equals(definitionIdentity, lease.GraphDefinitionIdentitySha256, StringComparison.Ordinal) ||
                !TryReadString(frozenPlan.RootElement, "planIdentitySha256", out var planIdentity) ||
                !string.Equals(planIdentity, lease.CentralPlanIdentitySha256, StringComparison.Ordinal) ||
                !frozenPlan.RootElement.TryGetProperty("nodes", out var nodes) ||
                nodes.ValueKind != JsonValueKind.Array || lease.GraphNodeOrdinal < 0 ||
                lease.GraphNodeOrdinal >= nodes.GetArrayLength() ||
                !parsedDefinition.IsValid || parsedDefinition.Definition is not { } graphDefinition ||
                !string.Equals(
                    Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(graphDefinition)),
                    lease.FrozenDefinitionJson,
                    StringComparison.Ordinal))
            {
                return false;
            }
            var planNode = nodes[lease.GraphNodeOrdinal.Value];
            if (!TryReadString(planNode, "identitySha256", out var planNodeIdentity) ||
                !string.Equals(planNodeIdentity, nodeIdentity, StringComparison.Ordinal) ||
                !planNode.TryGetProperty("definition", out var planDefinition) ||
                !TryReadString(planDefinition, "id", out var planNodeId) || planNodeId != nodeId ||
                !frozenPlan.RootElement.TryGetProperty("sources", out var sourcesElement))
            {
                return false;
            }
            var capabilities = graphDefinition.Nodes.SelectMany(definition => definition.CapabilityLabels)
                .Distinct(StringComparer.Ordinal).ToImmutableArray();
            var compiled = ProcessingGraphCompiler.Compile(
                graphDefinition, new(ProcessingGraphHosts.LogicHost, capabilities));
            if (!compiled.IsValid || compiled.Plan is not { } compiledPlan ||
                !string.Equals(compiledPlan.DefinitionIdentitySha256, definitionIdentity, StringComparison.Ordinal) ||
                !string.Equals(compiledPlan.PlanIdentitySha256, planIdentity, StringComparison.Ordinal) ||
                lease.GraphNodeOrdinal >= compiledPlan.Nodes.Length)
            {
                return false;
            }
            var compiledNode = compiledPlan.Nodes[lease.GraphNodeOrdinal.Value];
            return string.Equals(compiledNode.IdentitySha256, nodeIdentity, StringComparison.Ordinal) &&
                compiledNode.Definition.Id == nodeId &&
                string.Equals(planNodeIdentity, nodeIdentity, StringComparison.Ordinal) &&
                string.Equals(
                    LogicHostProcessingGraphAdapter.FreezePlan(compiledPlan),
                    lease.FrozenCentralPlanJson,
                    StringComparison.Ordinal) &&
                string.Equals(
                    LogicHostProcessingGraphAdapter.FreezeNode(compiledNode),
                    lease.FrozenNodePlanJson,
                    StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            (value = property.GetString() ?? string.Empty).Length > 0;
    }

    internal static bool RequiresBoundExpectedIdentity(CentralDerivativeJobLease lease)
        => RequiresBoundExpectedIdentity(
            lease.RequestedRecipeIdentitySha256,
            lease.ExpectedRecipeIdentitySha256);

    internal static bool RequiresBoundExpectedIdentity(string requestedIdentity, string? expectedIdentity)
        => !string.IsNullOrWhiteSpace(expectedIdentity)
            && !string.Equals(
                expectedIdentity,
                requestedIdentity,
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The scene provenance a lease carries to the executor. A graph annotation node executes against the annotation
    /// decision frozen at expansion (<see cref="CentralProcessingGraphScheduler.CreateExpectedRecipeIdentity"/>): an
    /// expected identity left at the requested identity durably records that no annotation was frozen, so provenance
    /// the frame acquires between expansion and lease is withheld and the node runs (and skips) exactly as frozen
    /// instead of producing evidence whose recipe identity the frozen expectation and the evidence trigger reject.
    /// The marker is unambiguous because <see cref="CentralProcessingGraphNodeRegistry"/> admits annotation nodes with
    /// only a primary binding, so nothing but a frozen annotation can move the expected identity off the requested
    /// one. A bound expected identity was derived from the frame's write-once provenance, which is therefore the
    /// frozen value itself. Legacy jobs keep the live frame provenance.
    /// </summary>
    internal static string? ResolveLeaseSceneProvenance(
        Guid? graphExecutionId,
        string recipeName,
        string requestedRecipeIdentitySha256,
        string? expectedRecipeIdentitySha256,
        string? frameSceneProvenanceJson)
        => graphExecutionId is not null
            && string.Equals(recipeName, BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal)
            && !RequiresBoundExpectedIdentity(requestedRecipeIdentitySha256, expectedRecipeIdentitySha256)
                ? null
                : frameSceneProvenanceJson;

    internal static ProcessingAnnotationInput? CreateAnnotation(string? sceneProvenanceJson)
    {
        if (string.IsNullOrWhiteSpace(sceneProvenanceJson))
        {
            return null;
        }
        var provenance = JsonSerializer.Deserialize<SceneProvenance>(sceneProvenanceJson, SerializerOptions);
        if (provenance?.Objects is null && provenance?.Segments is null)
        {
            return null;
        }
        var objects = provenance.Objects?.Select(item =>
        {
            var annotate = ShouldAnnotate(item);
            return new ProjectedAnnotationObject(
                item.Id,
                item.DisplayName,
                new PixelPoint(item.PixelX, item.PixelY),
                annotate,
                annotate);
        }).ToArray() ?? [];
        var segments = provenance.Segments?.Select(item => new ProjectedAnnotationSegment(
            item.ConstellationId,
            new PixelPoint(item.FromPixelX, item.FromPixelY),
            new PixelPoint(item.ToPixelX, item.ToPixelY))).ToArray() ?? [];
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(
            CaptureContractJson.SerializeToElement(new { provenance.SceneId, objects, segments }));
        return new ProcessingAnnotationInput(
            objects,
            segments,
            new PreviewTransform(1, 1),
            ProjectionOverlay: null,
            identity);
    }

    private void RecordPinRelease(CentralDerivativeJobLease lease, string outcome)
    {
        if (lease.Inputs is not { Count: > 1 })
        {
            return;
        }
        var selectedAtUtc = lease.Inputs.Min(input => input.SelectedAtUtc);
        if (selectedAtUtc != default)
        {
            telemetry.RecordWindowPinDuration(lease.RecipeName, timeProvider.GetUtcNow() - selectedAtUtc, outcome);
        }
    }

    private static bool ShouldAnnotate(ProjectedObjectProvenance item)
        => !string.IsNullOrWhiteSpace(item.DisplayName)
            && !string.Equals(item.Id, item.DisplayName, StringComparison.Ordinal)
            && (item.Id.StartsWith("solar-system:", StringComparison.Ordinal)
                || item.Magnitude <= MaximumLabelMagnitude);

    private static string GetOutcome(ProcessingOutcomeStatus status) => status switch
    {
        ProcessingOutcomeStatus.Produced => "produced",
        ProcessingOutcomeStatus.Skipped => "skipped",
        ProcessingOutcomeStatus.RetryableFailure => "retryable",
        ProcessingOutcomeStatus.TerminalFailure => "terminal",
        _ => "other"
    };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
