using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Contributes the fixed measurement node and translates the selected optional association branch.</summary>
internal sealed class ExposureMeasurementGraphPlan : ICenteredExposureGraphExtension
{
    public CapturePipelineConfig Configure(CapturePipelineConfig pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        const string raw = "$raw";
        var fixedStep = ExposureMeasurementProducts.StepAlias;
        if (pipeline.Steps.Any(step => Matches(step.Type, fixedStep) || Matches(NodeId(step), fixedStep)))
            throw new InvalidOperationException("Exposure measurement is supplied by the centered pipeline.");
        var calibration = pipeline.Steps.SingleOrDefault(static step => step.Enabled != false && Matches(step.Type, "Calibration"));
        IReadOnlyList<string> measurementInputs = calibration is null ? [raw] : [raw, NodeId(calibration)];
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var measured = new CaptureProcessingStepConfig(fixedStep, fixedStep, Order: 4,
            DependsOn: measurementInputs, Required: false, Enabled: true, Publication: durable);

        var steps = pipeline.Steps.Select(step =>
        {
            if (Matches(step.Type, "MeasuredStellarAssociations"))
            {
                return step with
                {
                    Type = ExposureAssociationProducts.StepAlias,
                    Id = NodeId(step),
                    Required = false,
                    DependsOn = (step.DependsOn ?? []).Concat([raw, fixedStep]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Publication = durable
                };
            }
            return Matches(step.Type, MeasuredAstrometryCaptureProcessingStep.Alias)
                ? step with { Type = ExposureAstrometryCaptureProcessingStep.CenteredAlias, Id = NodeId(step) }
                : step;
        }).Append(measured).ToArray();
        return pipeline with { Steps = steps };
    }

    private static string NodeId(CaptureProcessingStepConfig step) =>
        string.IsNullOrWhiteSpace(step.Id) ? step.Type : step.Id.Trim();

    private static bool Matches(string? value, string alias) =>
        string.Equals(value, alias, StringComparison.OrdinalIgnoreCase);
}
