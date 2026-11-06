using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum CentralProcessingGraphNodeHandlerKind
{
    BuiltInRecipe,
    TransientValidation
}

internal sealed record CentralProcessingGraphNodeHandler(
    string StepAlias,
    string StepVersion,
    ProcessingOperationKind OperationKind,
    CentralProcessingGraphNodeHandlerKind Kind,
    CentralDerivativeRecipe Recipe,
    ProcessingRecipeDefinition? Definition);

internal interface ICentralProcessingGraphNodeRegistry
{
    ImmutableArray<string> Capabilities { get; }

    CentralProcessingGraphNodeHandler GetRequired(string stepAlias);

    bool Validate(ProcessingGraphExecutionPlan plan);

    /// <summary>
    /// The step alias of the first node this host cannot execute as declared, <see cref="UnsupportedSources"/> when
    /// the plan's sources are unsupported, or <see langword="null"/> exactly when <see cref="Validate"/> accepts it.
    /// </summary>
    string? FindUnsupported(ProcessingGraphExecutionPlan plan);

    internal const string UnsupportedSources = "$sources";
}

internal sealed class CentralProcessingGraphNodeRegistry : ICentralProcessingGraphNodeRegistry
{
    private readonly Dictionary<string, CentralProcessingGraphNodeHandler> _handlers;

    public CentralProcessingGraphNodeRegistry(ICentralDerivativeRecipeCatalog recipeCatalog)
    {
        ArgumentNullException.ThrowIfNull(recipeCatalog);
        var recipes = recipeCatalog.GetRequiredRecipes(FrameArtifactRole.Raw)
            .Concat(recipeCatalog.GetRequiredRecipes(FrameArtifactRole.Calibrated))
            .Append(CentralDerivativeRecipeCatalog.WeatherCloudOverlayRecipe)
            .Append(CentralDerivativeRecipeCatalog.MeasuredStellarAssociationsRecipe)
            .DistinctBy(static recipe => recipe.RecipeName, StringComparer.Ordinal)
            .ToArray();
        var handlers = new Dictionary<string, CentralProcessingGraphNodeHandler>(StringComparer.Ordinal);
        foreach (var recipe in recipes)
        {
            if (recipe.Transient is not null)
            {
                handlers.Add(recipe.RecipeName, new(
                    recipe.RecipeName,
                    recipe.RecipeVersion,
                    ProcessingOperationKind.Window,
                    CentralProcessingGraphNodeHandlerKind.TransientValidation,
                    recipe,
                    null));
                continue;
            }
            if (!BuiltInProcessingRecipes.TryGetDefinition(recipe.RecipeName, out var definition) || definition is null)
            {
                throw new InvalidOperationException($"Central graph recipe '{recipe.RecipeName}' has no built-in adapter.");
            }
            handlers.Add(recipe.RecipeName, new(
                recipe.RecipeName,
                recipe.RecipeVersion,
                definition.OperationKind,
                CentralProcessingGraphNodeHandlerKind.BuiltInRecipe,
                recipe,
                definition));
        }
        _handlers = handlers;
    }

    public ImmutableArray<string> Capabilities => [];

    public CentralProcessingGraphNodeHandler GetRequired(string stepAlias)
        => _handlers.TryGetValue(stepAlias, out var handler)
            ? handler
            : throw new CentralDerivativeJobStateException(
                $"Processing graph step alias '{stepAlias}' is not supported by LogicHost.");

    public bool Validate(ProcessingGraphExecutionPlan plan) => FindUnsupported(plan) is null;

    public string? FindUnsupported(ProcessingGraphExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // Live central scheduling is triggered only by Raw/Calibrated artifact ingestion
        // (CentralDerivativeJobScheduler -> ScheduleLiveAsync), so a graph sourcing any other role could never be
        // expanded live. Reject those sources at publication/assignment validation rather than accepting an
        // assignment that silently never executes.
        if (plan.Sources.Any(static source => source.Outputs.Length != 1 ||
                !IsSupportedSourceRole(source.Outputs[0].Role)) ||
            plan.Sources.Select(static source => source.Outputs[0].Role).Distinct().Count() != plan.Sources.Length)
        {
            return ICentralProcessingGraphNodeRegistry.UnsupportedSources;
        }
        foreach (var node in plan.Nodes)
        {
            if (!_handlers.TryGetValue(node.Definition.StepAlias, out var handler) ||
                !ValidateNode(plan, node, handler))
            {
                return node.Definition.StepAlias;
            }
        }
        return null;
    }

    /// <summary>Source roles whose ingestion triggers live central graph scheduling.</summary>
    internal static bool IsSupportedSourceRole(FrameArtifactRole role)
        => role is FrameArtifactRole.Raw or FrameArtifactRole.Calibrated;

    private static bool ValidateNode(
        ProcessingGraphExecutionPlan plan,
        ProcessingGraphPlanNode planNode,
        CentralProcessingGraphNodeHandler handler)
    {
        var node = planNode.Definition;
        // LogicHost freezes each node's expected recipe identity at expansion from primary and auxiliary artifact
        // bindings plus host-attached inputs: the anchor frame's scene provenance, its environmental inputs and, for
        // measured stellar associations, its resolved projected scene. Annotation and canonical-JSON graph bindings
        // would execute against a stale identity, so centrally executed graphs reject them at publication/assignment
        // validation instead of accepting them; the association node's scene is attached by the host, never declared.
        // An optional auxiliary artifact changes the expected identity when its producer yields nothing, and the
        // scheduler re-derives that identity only for the one pair it is defined for (#526 ruling Z), so every other
        // optional auxiliary artifact binding is host-incompatible.
        if (!string.Equals(node.StepVersion, handler.StepVersion, StringComparison.Ordinal) ||
            node.OperationKind != handler.OperationKind ||
            planNode.InputBindings.Count(static binding =>
                binding.BindingKind == ProcessingGraphInputBindingKind.PrimaryArtifact) != 1 ||
            planNode.InputBindings.Any(static binding =>
                binding.BindingKind is ProcessingGraphInputBindingKind.Annotation or
                    ProcessingGraphInputBindingKind.CanonicalJson) ||
            planNode.InputBindings.Any(binding =>
                binding.BindingKind == ProcessingGraphInputBindingKind.AuxiliaryArtifact &&
                !IsAllowedAuxiliaryBinding(plan, planNode, binding)) ||
            node.Dependencies.Any(static dependency =>
                dependency.Kind is ProcessingGraphDependencyKind.Annotation or
                    ProcessingGraphDependencyKind.CanonicalJson))
        {
            return false;
        }
        if (handler.Kind == CentralProcessingGraphNodeHandlerKind.TransientValidation)
        {
            if (handler.Recipe.Transient is not { } transient || node.Window is null || node.Outputs.Length != 0)
            {
                return false;
            }
            using var executionOptions = JsonDocument.Parse(transient.ExecutionOptionsJson);
            return JsonEqual(node.EffectiveOptions, executionOptions.RootElement);
        }
        try
        {
            var normalized = BuiltInProcessingRecipes.NormalizeOptions(node.StepAlias, node.EffectiveOptions);
            if (!JsonEqual(node.EffectiveOptions, normalized) || node.Outputs.Length != 1 ||
                handler.Definition is not { } definition)
            {
                return false;
            }
            var output = node.Outputs[0];
            return output.Role == ExpectedRole(node.StepAlias) &&
                output.ProductKind == (output.Role == FrameArtifactRole.Metadata
                    ? ProcessingProductKind.Metadata
                    : ProcessingProductKind.PixelData) &&
                output.Recipe == definition &&
                string.Equals(output.SchemaVersion, ExpectedSchema(node.StepAlias), StringComparison.Ordinal) &&
                string.Equals(output.MediaType, ExpectedMediaType(node.StepAlias, normalized),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Auxiliary artifact bindings LogicHost can freeze. A required binding is admitted on any node except
    /// Annotation, whose expected identity <c>CentralDerivativeJobExecutor.ResolveLeaseSceneProvenance</c> recomputes
    /// from its primary binding and measured associations only. An optional binding is admitted only as Annotation's
    /// measured stellar associations, consumed by no other node, because the scheduler re-derives the expected
    /// identity of exactly that leaf when the association product is omitted.
    /// </summary>
    internal static bool IsAllowedAuxiliaryBinding(
        ProcessingGraphExecutionPlan plan,
        ProcessingGraphPlanNode planNode,
        ProcessingGraphInputBinding binding)
    {
        var isAnnotation = string.Equals(
            planNode.Definition.StepAlias, BuiltInProcessingRecipes.Annotation, StringComparison.Ordinal);
        if (binding.Required)
        {
            return !isAnnotation;
        }
        if (!isAnnotation ||
            !string.Equals(binding.BindingName, BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName, StringComparison.Ordinal) ||
            planNode.InputBindings.Count(static item =>
                item.BindingKind == ProcessingGraphInputBindingKind.AuxiliaryArtifact) != 1 ||
            planNode.Definition.Dependencies.Count(dependency => string.Equals(
                dependency.ProducerId, binding.ProducerId, StringComparison.Ordinal) && !dependency.Required) != 1 ||
            plan.Nodes.Any(other => other.Definition.Dependencies.Any(dependency => string.Equals(
                dependency.ProducerId, planNode.Definition.Id, StringComparison.Ordinal))))
        {
            return false;
        }
        var producer = plan.Nodes.SingleOrDefault(item =>
            string.Equals(item.Definition.Id, binding.ProducerId, StringComparison.Ordinal));
        return producer is not null &&
            string.Equals(producer.Definition.StepAlias, BuiltInProcessingRecipes.MeasuredStellarAssociations,
                StringComparison.Ordinal) &&
            binding.OutputIndex == 0 && producer.Definition.Outputs.Length == 1 &&
            producer.Definition.Outputs[0] is { } output &&
            output.Role == FrameArtifactRole.Metadata &&
            output.ProductKind == ProcessingProductKind.Metadata &&
            string.Equals(output.Variant, CentralDerivativeRecipeCatalog.MeasuredStellarAssociationsVariant,
                StringComparison.Ordinal) &&
            string.Equals(output.SchemaVersion, MeasuredStellarAssociationsV1.CurrentSchemaVersion, StringComparison.Ordinal);
    }

    private static FrameArtifactRole ExpectedRole(string alias) => alias switch
    {
        BuiltInProcessingRecipes.EncodedPreview => FrameArtifactRole.Preview,
        BuiltInProcessingRecipes.Annotation or BuiltInProcessingRecipes.WeatherCloudOverlay =>
            FrameArtifactRole.AnnotatedPreview,
        BuiltInProcessingRecipes.ImageQuality or BuiltInProcessingRecipes.CloudAssessment or
            BuiltInProcessingRecipes.MeasuredStellarAssociations => FrameArtifactRole.Metadata,
        BuiltInProcessingRecipes.RollingMean => FrameArtifactRole.Combined,
        _ => throw new InvalidOperationException("The central graph built-in output role is unavailable.")
    };

    private static string? ExpectedSchema(string alias) => alias switch
    {
        BuiltInProcessingRecipes.CloudAssessment => CloudAssessmentV1.CurrentSchemaVersion,
        BuiltInProcessingRecipes.MeasuredStellarAssociations => MeasuredStellarAssociationsV1.CurrentSchemaVersion,
        _ => null
    };

    private static string ExpectedMediaType(string alias, JsonElement options) => alias switch
    {
        BuiltInProcessingRecipes.RollingMean => "application/x-hvo-linear-frame",
        BuiltInProcessingRecipes.ImageQuality => "application/json",
        BuiltInProcessingRecipes.CloudAssessment => StructuredProcessingProductContracts.CloudAssessmentMediaType,
        BuiltInProcessingRecipes.MeasuredStellarAssociations =>
            StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType,
        BuiltInProcessingRecipes.EncodedPreview =>
            options.Deserialize<EncodedPreviewOptions>()?.OutputEncoding == "Packed"
                ? "application/x-hvo-packed-image"
                : "image/jpeg",
        BuiltInProcessingRecipes.Annotation =>
            options.Deserialize<AnnotationRecipeOptions>()?.OutputEncoding == "Packed"
                ? "application/x-hvo-packed-image"
                : "image/jpeg",
        BuiltInProcessingRecipes.WeatherCloudOverlay =>
            options.Deserialize<WeatherCloudOverlayOptions>()?.OutputEncoding == "Packed"
                ? "application/x-hvo-packed-image"
                : "image/jpeg",
        _ => throw new InvalidOperationException("The central graph built-in media type is unavailable.")
    };

    private static bool JsonEqual(JsonElement left, JsonElement right)
        => string.Equals(
            CaptureContractJson.Canonicalize(left).GetRawText(),
            CaptureContractJson.Canonicalize(right).GetRawText(),
            StringComparison.Ordinal);
}
