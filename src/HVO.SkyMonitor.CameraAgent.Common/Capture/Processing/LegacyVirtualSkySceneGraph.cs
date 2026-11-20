using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class LegacyVirtualSkySceneGraph
{
    internal static CaptureProcessingStepConfig? FindLegacyScene(CameraModuleConfig configuration, CapturePipelineConfig pipeline)
    {
        // Storage owns supplemental durable output retention. A scene-only graph can still commit its canonical stage.
        if (!string.Equals(configuration.ModuleType, "VirtualSky", StringComparison.OrdinalIgnoreCase) ||
            !pipeline.Steps.Any(static step => IsEnabledAlias(step, "Storage"))) return null;
        return pipeline.Steps.SingleOrDefault(static step => IsEnabledAlias(step, "ProjectedScene") &&
            step.Options is { ValueKind: JsonValueKind.Object } options && options.EnumerateObject().Any(static entry =>
                string.Equals(entry.Name, "independentPrediction", StringComparison.OrdinalIgnoreCase) &&
                entry.Value.ValueKind == JsonValueKind.True));
    }

    internal static CapturePipelineConfig Migrate(CapturePipelineConfig pipeline, CaptureProcessingStepConfig canonical)
    {
        var sceneOptions = ReadOptions(canonical);
        sceneOptions.Remove("independentPrediction");

        var prediction = pipeline.Steps.SingleOrDefault(static step =>
            IsEnabledAlias(step, AstrometryPredictionCaptureProcessingStep.Alias));
        if (prediction is null)
        {
            var usedIds = pipeline.Steps.Select(NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var predictionId = AstrometryPredictionCaptureProcessingStep.Alias;
            for (var suffix = 1; usedIds.Contains(predictionId); suffix++)
                predictionId = FormattableString.Invariant($"{AstrometryPredictionCaptureProcessingStep.Alias}-{suffix}");
            var predictionOptions = sceneOptions.Where(static entry =>
                string.Equals(entry.Key, "maximumMagnitude", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Key, "maximumResults", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Key, "astronomyAlgorithmVersion", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            // The legacy node defaulted to 6.5, while the explicit prediction defaults to 5.
            predictionOptions.TryAdd("maximumMagnitude", JsonSerializer.SerializeToElement(6.5));
            prediction = new(AstrometryPredictionCaptureProcessingStep.Alias, predictionId,
                Options: JsonSerializer.SerializeToElement(predictionOptions), DependsOn: ["$raw"], Required: false,
                Publication: new(CaptureProcessingPersistenceMode.DurableLocal));
        }

        var canonicalId = NodeId(canonical);
        var measuredIds = pipeline.Steps.Where(step =>
                (IsEnabledAlias(step, MeasuredStellarAssociationCaptureProcessingStep.Alias) ||
                 IsEnabledAlias(step, MeasuredAstrometryCaptureProcessingStep.Alias)) &&
                step.DependsOn?.Contains(canonicalId, StringComparer.OrdinalIgnoreCase) == true)
            .Select(NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Presentation and publication can still consume the canonical measurement plan. Move only an isolated
        // measurement branch so a previously valid presentation dependency never becomes an analysis dependency.
        var hasExternalConsumer = pipeline.Steps.Any(step => step.Enabled != false &&
            !measuredIds.Contains(NodeId(step)) && step.DependsOn?.Any(measuredIds.Contains) == true);
        var migrated = pipeline.Steps.Select(step =>
        {
            if (step == canonical) return step with { Options = JsonSerializer.SerializeToElement(sceneOptions) };
            if (hasExternalConsumer || step.Enabled == false || !measuredIds.Contains(NodeId(step))) return step;

            var options = ReadOptions(step);
            options["useAstrometryPrediction"] = JsonSerializer.SerializeToElement(true);
            return step with
            {
                Options = JsonSerializer.SerializeToElement(options),
                DependsOn = step.DependsOn!.Select(id => string.Equals(id, canonicalId, StringComparison.OrdinalIgnoreCase)
                    ? NodeId(prediction) : id).ToArray(),
                Required = false,
                Publication = new(CaptureProcessingPersistenceMode.DurableLocal)
            };
        }).ToList();
        if (!pipeline.Steps.Contains(prediction)) migrated.Add(prediction);
        return pipeline with { Steps = migrated };
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
