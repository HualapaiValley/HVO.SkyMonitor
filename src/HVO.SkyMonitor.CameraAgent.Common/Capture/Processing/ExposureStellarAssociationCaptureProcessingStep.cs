using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ExposureStellarAssociationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    MeasuredStellarAssociationProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter,
    CaptureExposureMeasurementReader reader)
    : MeasuredStellarAssociationCaptureProcessingStep(metadata, options, adapter),
        ICenteredRawPrimaryCaptureProcessingStep, ICaptureProcessingGraphStep
{
    public override string RecipeName => ExposureAssociationProducts.RecipeName;
    public ProcessingRecipeDefinition SharedOutputRecipe => ExposureAssociationProducts.Definition;
    public string SharedStepVersion => ExposureAssociationProducts.ImplementationVersion;
    public ProcessingOperationKind? SharedOperationKind => ProcessingOperationKind.Analyzer;

    public override IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Metadata };

    public override IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        .. base.DependencyRequirements,
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string>(StringComparer.Ordinal) { BuiltInProcessingRecipes.ExposureMeasurement },
            new HashSet<string>(StringComparer.Ordinal) { ExposureMeasurementProducts.SchemaVersion },
            ExposureMeasurementProducts.Variant)
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var descriptor = context.ReconstructionDescriptor ?? throw new InvalidDataException("Association requires admitted raw.");
        var dependencies = context.GetDependencyProducts();
        var scene = dependencies.SingleOrDefault(product => product.Variant == ExposureCatalogPredictionProducts.Variant &&
            ProjectedSceneV1.IsSupportedSchemaVersion(product.SchemaVersion));
        if (scene is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped("association.independent-prediction-unavailable"));
            return;
        }

        var product = dependencies.Single(item => item.SchemaVersion == ExposureMeasurementProducts.SchemaVersion);
        var (retained, measurement) = await reader.ReadDependencyAsync(context,
            ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), cancellationToken).ConfigureAwait(false);

        var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, context.Artifacts!.Raw,
            descriptor.Artifact.Variant, context.AcquisitionTiming, descriptor) with { Payload = ReadOnlyMemory<byte>.Empty };
        var predicted = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, scene);
        if (context.GetDependencyProducerStepId(predicted.ArtifactId) != ExposureCatalogPredictionProducts.StepAlias)
        {
            context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure("association.independent-prediction-producer-mismatch"));
            return;
        }
        var outcome = ExposureAssociationProducts.Associate(raw, retained, measurement.Input, predicted, OutputVariant,
            new(Options.Association, Options.LabelPolicy), cancellationToken);
        context.RecordConsumedArtifacts([context.Artifacts.Raw], [product, scene]);
        context.RecordExecutionRoute(ProcessingNodeExecutionRoute.InProcess);
        context.RecordExecutionOutcome(outcome);
        context.AddProcessingOutcome(outcome);
    }
}
