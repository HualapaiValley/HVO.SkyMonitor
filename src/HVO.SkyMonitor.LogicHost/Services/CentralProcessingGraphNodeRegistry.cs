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

    public bool Validate(ProcessingGraphExecutionPlan plan)
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
            return false;
        }
        foreach (var node in plan.Nodes)
        {
            if (!_handlers.TryGetValue(node.Definition.StepAlias, out var handler) ||
                !ValidateNode(node, handler))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Source roles whose ingestion triggers live central graph scheduling.</summary>
    internal static bool IsSupportedSourceRole(FrameArtifactRole role)
        => role is FrameArtifactRole.Raw or FrameArtifactRole.Calibrated;

    private static bool ValidateNode(
        ProcessingGraphPlanNode planNode,
        CentralProcessingGraphNodeHandler handler)
    {
        var node = planNode.Definition;
        // LogicHost freezes each node's expected recipe identity at expansion from primary and auxiliary artifact
        // bindings plus the anchor frame's own scene provenance. Annotation and canonical-JSON graph bindings would
        // execute against a stale identity, so centrally executed graphs reject them at publication/assignment
        // validation instead of accepting them.
        if (!string.Equals(node.StepVersion, handler.StepVersion, StringComparison.Ordinal) ||
            node.OperationKind != handler.OperationKind ||
            planNode.InputBindings.Count(static binding =>
                binding.BindingKind == ProcessingGraphInputBindingKind.PrimaryArtifact) != 1 ||
            planNode.InputBindings.Any(static binding =>
                binding.BindingKind is ProcessingGraphInputBindingKind.Annotation or
                    ProcessingGraphInputBindingKind.CanonicalJson) ||
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

    private static FrameArtifactRole ExpectedRole(string alias) => alias switch
    {
        BuiltInProcessingRecipes.EncodedPreview => FrameArtifactRole.Preview,
        BuiltInProcessingRecipes.Annotation or BuiltInProcessingRecipes.WeatherCloudOverlay =>
            FrameArtifactRole.AnnotatedPreview,
        BuiltInProcessingRecipes.ImageQuality or BuiltInProcessingRecipes.CloudAssessment => FrameArtifactRole.Metadata,
        BuiltInProcessingRecipes.RollingMean => FrameArtifactRole.Combined,
        _ => throw new InvalidOperationException("The central graph built-in output role is unavailable.")
    };

    private static string? ExpectedSchema(string alias)
        => string.Equals(alias, BuiltInProcessingRecipes.CloudAssessment, StringComparison.Ordinal)
            ? CloudAssessmentV1.CurrentSchemaVersion
            : null;

    private static string ExpectedMediaType(string alias, JsonElement options) => alias switch
    {
        BuiltInProcessingRecipes.RollingMean => "application/x-hvo-linear-frame",
        BuiltInProcessingRecipes.ImageQuality => "application/json",
        BuiltInProcessingRecipes.CloudAssessment => StructuredProcessingProductContracts.CloudAssessmentMediaType,
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
