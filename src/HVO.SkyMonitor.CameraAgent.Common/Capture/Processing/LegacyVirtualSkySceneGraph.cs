using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class LegacyVirtualSkySceneGraph
{
    internal static CaptureProcessingStepConfig? FindLegacyScene(CameraModuleConfig configuration, CapturePipelineConfig pipeline)
        => string.Equals(configuration.ModuleType, "VirtualSky", StringComparison.OrdinalIgnoreCase)
            ? FindLegacyScene(pipeline) : null;

    internal static CaptureProcessingStepConfig? FindLegacyScene(CapturePipelineConfig pipeline)
    {
        // Storage owns supplemental durable output retention. A scene-only graph can still commit its canonical stage.
        if (!pipeline.Steps.Any(static step => IsEnabledAlias(step, "Storage"))) return null;
        return pipeline.Steps.SingleOrDefault(static step => IsEnabledAlias(step, "ProjectedScene") &&
            step.Options is { ValueKind: JsonValueKind.Object } options && options.EnumerateObject().Any(static entry =>
                string.Equals(entry.Name, "independentPrediction", StringComparison.OrdinalIgnoreCase) &&
                entry.Value.ValueKind == JsonValueKind.True));
    }

    internal static CaptureProcessingStepConfig? FindFitInSharedBranch(CapturePipelineConfig pipeline, CaptureProcessingStepConfig canonical)
    {
        var measurements = FindMeasurements(pipeline, canonical);
        return HasExternalConsumer(pipeline, measurements)
            ? pipeline.Steps.SingleOrDefault(step => IsEnabledAlias(step, MeasuredAstrometryCaptureProcessingStep.Alias) &&
                measurements.Contains(NodeId(step)))
            : null;
    }

    internal static CapturePipelineConfig Migrate(ProcessingGraphRevisionSnapshot revision, CaptureProcessingStepConfig canonical,
        CaptureProcessingStepConfig? sharedFit)
    {
        var pipeline = revision.Pipeline;
        var sceneOptions = ReadOptions(canonical);
        sceneOptions.Remove("independentPrediction");

        var usedIds = pipeline.Steps.Select(NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedMetadataVariants = ReadMetadataVariants(revision);
        var prediction = pipeline.Steps.SingleOrDefault(static step =>
            IsEnabledAlias(step, AstrometryPredictionCaptureProcessingStep.Alias));
        if (prediction is null)
        {
            var predictionId = ReserveUnused(AstrometryPredictionCaptureProcessingStep.Alias, usedIds);
            var predictionOptions = sceneOptions.Where(static entry =>
                string.Equals(entry.Key, "maximumMagnitude", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Key, "maximumResults", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Key, "astronomyAlgorithmVersion", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            // The legacy node defaulted to 6.5, while the explicit prediction defaults to 5.
            predictionOptions.TryAdd("maximumMagnitude", JsonSerializer.SerializeToElement(6.5));
            var variant = ReserveUnused(AstrometryPredictionCaptureProcessingStep.AliasVariant, usedMetadataVariants);
            // Omit the default override to keep existing non-colliding successor identities.
            if (variant != AstrometryPredictionCaptureProcessingStep.AliasVariant)
                predictionOptions["outputVariant"] = JsonSerializer.SerializeToElement(variant);
            prediction = new(AstrometryPredictionCaptureProcessingStep.Alias, predictionId,
                Options: JsonSerializer.SerializeToElement(predictionOptions), DependsOn: ["$raw"], Required: false,
                Publication: new(CaptureProcessingPersistenceMode.DurableLocal));
        }

        var canonicalId = NodeId(canonical);
        var measuredIds = FindMeasurements(pipeline, canonical);
        var hasExternalConsumer = HasExternalConsumer(pipeline, measuredIds);

        // A consumer such as Annotation needs associations tied to the canonical scene. Give a fit its own
        // associations when that producer is shared, and preserve the fit's ID and output contract for its consumers.
        CaptureProcessingStepConfig? privateAssociations = null;
        CaptureProcessingStepConfig? fitAssociations = null;
        if (sharedFit is not null)
        {
            fitAssociations = pipeline.Steps.Single(step => IsEnabledAlias(step, MeasuredStellarAssociationCaptureProcessingStep.Alias) &&
                DependsOn(sharedFit, NodeId(step)));
            var associationsId = NodeId(fitAssociations);
            if (pipeline.Steps.Any(step => step.Enabled != false && step != sharedFit && DependsOn(step, associationsId)))
            {
                var variant = ReserveUnused("astrometry-prediction-associations-v1", usedMetadataVariants);
                privateAssociations = MoveToPrediction(fitAssociations, [NodeId(prediction)], variant) with
                {
                    Id = ReserveUnused("astrometry-prediction-associations", usedIds)
                };
            }
        }

        var migrated = pipeline.Steps.Select(step =>
        {
            if (step == canonical) return step with { Options = JsonSerializer.SerializeToElement(sceneOptions) };
            if (step == sharedFit)
            {
                var originalAssociationsId = NodeId(fitAssociations!);
                var fitAssociationsId = NodeId(privateAssociations ?? fitAssociations!);
                return MoveToPrediction(step, step.DependsOn!.Select(id =>
                    id.Equals(originalAssociationsId, StringComparison.OrdinalIgnoreCase) ? fitAssociationsId : NodeId(prediction)).ToArray());
            }
            if (step == fitAssociations && privateAssociations is null)
                return MoveToPrediction(step, [NodeId(prediction)]);
            if (hasExternalConsumer || step.Enabled == false || !measuredIds.Contains(NodeId(step))) return step;
            return MoveToPrediction(step, step.DependsOn!.Select(id => id.Equals(canonicalId, StringComparison.OrdinalIgnoreCase)
                ? NodeId(prediction) : id).ToArray());
        }).ToList();
        if (!pipeline.Steps.Contains(prediction)) migrated.Add(prediction);
        if (privateAssociations is not null) migrated.Add(privateAssociations);
        return pipeline with { Steps = migrated };
    }

    private static HashSet<string> FindMeasurements(CapturePipelineConfig pipeline, CaptureProcessingStepConfig canonical)
        => pipeline.Steps.Where(step =>
                (IsEnabledAlias(step, MeasuredStellarAssociationCaptureProcessingStep.Alias) ||
                 IsEnabledAlias(step, MeasuredAstrometryCaptureProcessingStep.Alias)) && DependsOn(step, NodeId(canonical)))
            .Select(NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasExternalConsumer(CapturePipelineConfig pipeline, HashSet<string> measuredIds)
        => pipeline.Steps.Any(step => step.Enabled != false &&
            !measuredIds.Contains(NodeId(step)) && step.DependsOn?.Any(measuredIds.Contains) == true);

    private static bool DependsOn(CaptureProcessingStepConfig step, string id)
        => step.DependsOn?.Contains(id, StringComparer.OrdinalIgnoreCase) == true;

    private static CaptureProcessingStepConfig MoveToPrediction(CaptureProcessingStepConfig step,
        IReadOnlyList<string> dependencies, string? outputVariant = null)
    {
        var options = ReadOptions(step);
        options["useAstrometryPrediction"] = JsonSerializer.SerializeToElement(true);
        if (outputVariant is not null) options["outputVariant"] = JsonSerializer.SerializeToElement(outputVariant);
        return step with
        {
            Options = JsonSerializer.SerializeToElement(options),
            DependsOn = dependencies,
            Required = false,
            Publication = new(CaptureProcessingPersistenceMode.DurableLocal)
        };
    }

    private static HashSet<string> ReadMetadataVariants(ProcessingGraphRevisionSnapshot revision)
    {
        var definition = ProcessingGraphJson.Parse(revision.DefinitionJson).Definition
            ?? throw new InvalidDataException("The validated legacy graph definition is invalid.");
        return definition.Nodes.SelectMany(static node => node.Outputs)
            .Where(static output => output.Role == FrameArtifactRole.Metadata).Select(static output => output.Variant)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ReserveUnused(string preferred, HashSet<string> used)
    {
        var selected = preferred;
        for (var suffix = 1; !used.Add(selected); suffix++)
            selected = FormattableString.Invariant($"{preferred}-{suffix}");
        return selected;
    }

    private static bool IsEnabledAlias(CaptureProcessingStepConfig step, string alias)
        => step.Enabled != false && string.Equals(step.Type, alias, StringComparison.OrdinalIgnoreCase);

    private static string NodeId(CaptureProcessingStepConfig step)
        => string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim();

    private static Dictionary<string, JsonElement> ReadOptions(CaptureProcessingStepConfig step)
        => step.Options is { ValueKind: JsonValueKind.Object } options
            ? options.EnumerateObject().ToDictionary(static entry => entry.Name, static entry => entry.Value,
                StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
}
