using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Contributes the fixed measurement node and translates the selected optional association branch.</summary>
internal sealed class ExposureMeasurementGraphPlan : ICenteredExposureGraphExtension
{
    internal static void EnsureMatchesProfile(CapturePipelineConfig pipeline, CenteredExposureProfile? centered)
    {
        if (centered is null && !pipeline.Steps.Any(step => Matches(step.Type, ExposureMeasurementProducts.StepAlias) ||
            Matches(step.Type, ProjectedSceneCaptureProcessingStep.ExposurePredictionAlias) ||
            Matches(step.Type, ExposureAssociationProducts.StepAlias) ||
            Matches(step.Type, ExposureAstrometryCaptureProcessingStep.CenteredAlias)))
            return;

        var fixedSteps = pipeline.Steps.Where(step => Matches(step.Type, ExposureMeasurementProducts.StepAlias) ||
            Matches(NodeId(step), ExposureMeasurementProducts.StepAlias)).ToArray();
        if (centered is not null && fixedSteps is [var measurement] && measurement.Enabled != false &&
            !measurement.Required && measurement.Order == 4 &&
            Matches(measurement.Type, ExposureMeasurementProducts.StepAlias) &&
            NodeId(measurement) == ExposureMeasurementProducts.StepAlias &&
            measurement.Publication?.Persistence == CaptureProcessingPersistenceMode.DurableLocal)
        {
            var producers = pipeline.Steps.Where(step => step.Enabled != false && Matches(step.Type, "Calibration")).ToArray();
            string[] expected = producers.Length == 0 ? ["$raw"] : ["$raw", NodeId(producers[0])];
            if (producers.Length <= 1 &&
                producers.All(step => !step.Required && step.Publication?.Persistence == CaptureProcessingPersistenceMode.DurableLocal &&
                    step.DependsOn is [var input] && Matches(input, "$raw")) &&
                measurement.DependsOn is { } dependencies && dependencies.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
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
        if (producers is [var producer] && Matches(producer.Type, prediction) && NodeId(producer) == prediction &&
            producer.Enabled != false && !producer.Required && producer.DependsOn is [var raw] && Matches(raw, "$raw") &&
            producer.Publication?.Persistence == CaptureProcessingPersistenceMode.DurableLocal &&
            consumers.All(step => step.DependsOn?.Contains(prediction, StringComparer.OrdinalIgnoreCase) == true))
            return;

        throw new ProcessingGraphStoreConflictException(
            "Centered association and astrometry require the fixed, durable exposure catalog prediction from admitted raw.");
    }

    public CapturePipelineConfig Configure(CapturePipelineConfig pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        const string raw = "$raw";
        var fixedStep = ExposureMeasurementProducts.StepAlias;
        var prediction = ProjectedSceneCaptureProcessingStep.ExposurePredictionAlias;
        if (pipeline.Steps.Any(step => Matches(step.Type, fixedStep) || Matches(NodeId(step), fixedStep) ||
            Matches(step.Type, prediction) || Matches(NodeId(step), prediction)))
            throw new InvalidOperationException("Exposure measurement and catalog prediction are supplied by the centered pipeline.");
        var calibration = pipeline.Steps.SingleOrDefault(static step => step.Enabled != false && Matches(step.Type, "Calibration"));
        if (calibration is not null && !(calibration.DependsOn is [var input] && Matches(input, raw)))
            throw new InvalidOperationException("Centered calibration must depend only on admitted raw.");
        IReadOnlyList<string> measurementInputs = calibration is null ? [raw] : [raw, NodeId(calibration)];
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var measured = new CaptureProcessingStepConfig(fixedStep, fixedStep, Order: 4,
            DependsOn: measurementInputs, Required: false, Enabled: true, Publication: durable);

        var sceneIds = pipeline.Steps.Where(step => Matches(step.Type, "ProjectedScene")).Select(NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> IndependentDependencies(CaptureProcessingStepConfig step) =>
            (step.DependsOn ?? []).Where(id => !sceneIds.Contains(id)).Concat(step.Enabled == false ? [] : [prediction]);

        var steps = pipeline.Steps.Select(step =>
        {
            if (step == calibration)
            {
                // Step 3 must settle durably, but a terminal failure cannot prevent step 4a.
                return step with { Required = false, Publication = durable };
            }

            if (Matches(step.Type, "MeasuredStellarAssociations"))
            {
                return step with
                {
                    Type = ExposureAssociationProducts.StepAlias,
                    Id = NodeId(step),
                    Required = false,
                    DependsOn = IndependentDependencies(step).Concat([raw, fixedStep]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Publication = durable
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
        }).Append(measured);
        if (pipeline.Steps.Any(step => step.Enabled != false && (Matches(step.Type, "MeasuredStellarAssociations") ||
            Matches(step.Type, MeasuredAstrometryCaptureProcessingStep.Alias))))
            steps = steps.Append(new(prediction, prediction, DependsOn: [raw], Required: false, Publication: durable));

        return pipeline with { Steps = steps.ToArray() };
    }

    private static string NodeId(CaptureProcessingStepConfig step) =>
        string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim();

    private static bool Matches(string? value, string alias) =>
        string.Equals(value, alias, StringComparison.OrdinalIgnoreCase);
}
