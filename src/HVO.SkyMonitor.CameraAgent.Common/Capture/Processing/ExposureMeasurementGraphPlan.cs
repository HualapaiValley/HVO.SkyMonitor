using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Contributes the fixed measurement node and translates the selected optional association branch.</summary>
internal sealed class ExposureMeasurementGraphPlan : ICenteredExposureGraphExtension
{
    internal const int MeasurementOrder = 4;
    internal const int PredictionDefaultOrder = 20;
    private const string RawInput = "$raw";
    private static readonly JsonNodeOptions CalibrationOptionNames = new() { PropertyNameCaseInsensitive = true };
    private static readonly CaptureProcessingPublicationPolicy Durable = new(CaptureProcessingPersistenceMode.DurableLocal);

    internal static void EnsureMatchesProfile(CapturePipelineConfig pipeline, CenteredExposureProfile? centered)
    {
        if (centered is null && !pipeline.Steps.Any(step => ExposureMeasurementProducts.IsSystemManagedStepAlias(step.Type) ||
            Matches(step.Type, ExposureAssociationProducts.StepAlias) ||
            Matches(step.Type, ExposureAstrometryCaptureProcessingStep.CenteredAlias)))
            return;

        var fixedSteps = pipeline.Steps.Where(step => Matches(step.Type, ExposureMeasurementProducts.StepAlias) ||
            Matches(NodeId(step), ExposureMeasurementProducts.StepAlias)).ToArray();
        if (centered is not null && fixedSteps is [var measurement])
        {
            var producers = pipeline.Steps.Where(step => step.Enabled != false && Matches(step.Type, "Calibration")).ToArray();
            var calibrationId = producers is [var calibration] ? NodeId(calibration) : null;
            if (producers.Length <= 1 &&
                producers.All(step => !step.Required && step.Publication == Durable &&
                    step.DependsOn is [var input] && Matches(input, RawInput)) &&
                MatchesGeneratedStep(measurement, CreateMeasurement(calibrationId), MeasurementOrder))
            {
                EnsureIndependentPrediction(pipeline);
                return;
            }
        }

        throw new ProcessingGraphStoreConflictException(
            "The graph does not match the capture profile's fixed exposure measurement. Select a compatible graph or roll back to configured-basic.");
    }

    private static void EnsureIndependentPrediction(CapturePipelineConfig pipeline)
    {
        const string prediction = ProjectedSceneCaptureProcessingStep.ExposurePredictionAlias;
        var producers = pipeline.Steps.Where(step => Matches(step.Type, prediction) || Matches(NodeId(step), prediction)).ToArray();
        var consumers = pipeline.Steps.Where(step => step.Enabled != false &&
            (Matches(step.Type, ExposureAssociationProducts.StepAlias) || Matches(step.Type, ExposureAstrometryCaptureProcessingStep.CenteredAlias))).ToArray();
        if (producers.Length == 0 && consumers.Length == 0)
            return;
        if (producers is [var producer] && MatchesGeneratedStep(producer, CreatePrediction(), PredictionDefaultOrder) &&
            consumers.All(step => step.DependsOn?.Contains(prediction, StringComparer.OrdinalIgnoreCase) == true))
            return;

        throw new ProcessingGraphStoreConflictException(
            "Centered association and astrometry require the fixed, durable exposure catalog prediction from admitted raw.");
    }

    public CapturePipelineConfig Configure(CapturePipelineConfig pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var fixedStep = ExposureMeasurementProducts.StepAlias;
        var prediction = ProjectedSceneCaptureProcessingStep.ExposurePredictionAlias;
        if (pipeline.Steps.Any(step => ExposureMeasurementProducts.IsSystemManagedStepAlias(step.Type) ||
            ExposureMeasurementProducts.IsSystemManagedStepAlias(NodeId(step))))
            throw new InvalidOperationException("Exposure measurement and catalog prediction are supplied by the centered pipeline.");
        var calibration = pipeline.Steps.SingleOrDefault(static step => step.Enabled != false && Matches(step.Type, "Calibration"));
        if (calibration is not null && !(calibration.DependsOn is [var input] && Matches(input, RawInput)))
            throw new InvalidOperationException("Centered calibration must depend only on admitted raw.");

        var sceneIds = pipeline.Steps.Where(step => Matches(step.Type, "ProjectedScene")).Select(NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> IndependentDependencies(CaptureProcessingStepConfig step) =>
            (step.DependsOn ?? []).Where(id => !sceneIds.Contains(id)).Concat(step.Enabled == false ? [] : [prediction]);

        var steps = pipeline.Steps.Select(step =>
        {
            if (step == calibration)
            {
                // Step 3 must settle durably, but a terminal failure cannot prevent step 4a.
                return step with { Required = false, Publication = Durable };
            }

            if (Matches(step.Type, "MeasuredStellarAssociations"))
            {
                return step with
                {
                    Type = ExposureAssociationProducts.StepAlias,
                    Id = NodeId(step),
                    Required = false,
                    DependsOn = IndependentDependencies(step).Concat([RawInput, fixedStep]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Publication = Durable
                };
            }
            return Matches(step.Type, MeasuredAstrometryCaptureProcessingStep.Alias)
                ? step with
                {
                    Type = ExposureAstrometryCaptureProcessingStep.CenteredAlias,
                    Id = NodeId(step),
                    Required = false,
                    DependsOn = IndependentDependencies(step).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                }
                : step;
        }).Append(CreateMeasurement(calibration is null ? null : NodeId(calibration)));
        if (pipeline.Steps.Any(step => step.Enabled != false && (Matches(step.Type, "MeasuredStellarAssociations") ||
            Matches(step.Type, MeasuredAstrometryCaptureProcessingStep.Alias))))
            steps = steps.Append(CreatePrediction());

        return pipeline with { Steps = steps.ToArray() };
    }

    private static CaptureProcessingStepConfig CreateMeasurement(string? calibrationId)
        => new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, Order: MeasurementOrder,
            DependsOn: calibrationId is null ? [RawInput] : [RawInput, calibrationId], Required: false, Enabled: true, Publication: Durable);

    internal static CapturePipelineConfig FreezeSyntheticReferenceCodeDomain(CapturePipelineConfig pipeline)
        => pipeline with { Steps = pipeline.Steps.Select(FreezeSyntheticReferenceCodeDomain).ToArray() };

    private static CaptureProcessingStepConfig FreezeSyntheticReferenceCodeDomain(CaptureProcessingStepConfig step)
    {
        if (step.Enabled == false || !Matches(step.Type, "Calibration") || step.Options is not { ValueKind: JsonValueKind.Object } element ||
            JsonNode.Parse(element.GetRawText(), CalibrationOptionNames) is not JsonObject options)
            return step;

        if (options["strategy"] is JsonValue strategy && strategy.TryGetValue<string>(out var value) && value == "SyntheticReferences")
            options["useStoredLightCodes"] = true;
        else if (!options.Remove("useStoredLightCodes"))
            return step;
        return step with { Options = JsonSerializer.SerializeToElement(options) };
    }

    private static CaptureProcessingStepConfig CreatePrediction()
        => new(ExposureCatalogPredictionProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias,
            DependsOn: [RawInput], Required: false, Publication: Durable);

    private static bool MatchesGeneratedStep(CaptureProcessingStepConfig candidate, CaptureProcessingStepConfig generated, int defaultOrder)
        => Matches(candidate.Type, generated.Type) && NodeId(candidate) == NodeId(generated) &&
            (candidate.Enabled != false) == (generated.Enabled != false) && candidate.Required == generated.Required &&
            (candidate.Order ?? defaultOrder) == (generated.Order ?? defaultOrder) && candidate.Publication == generated.Publication &&
            candidate.DependsOn is { } dependencies && dependencies.SequenceEqual(generated.DependsOn!, StringComparer.OrdinalIgnoreCase) &&
            (candidate.Options is null or { ValueKind: JsonValueKind.Null } ||
                candidate.Options is { ValueKind: JsonValueKind.Object } options && !options.EnumerateObject().Any());

    private static string NodeId(CaptureProcessingStepConfig step) =>
        string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim();

    private static bool Matches(string? value, string alias) =>
        string.Equals(value, alias, StringComparison.OrdinalIgnoreCase);
}
