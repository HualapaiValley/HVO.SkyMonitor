using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ExposureMeasurementCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    ExposureMeasurementStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter)
    : ConfigurableCaptureProcessingStep<ExposureMeasurementStepOptions>(metadata, options),
        ICaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    public bool Enabled => true;
    public string RecipeName => BuiltInProcessingRecipes.ExposureMeasurement;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => ExposureMeasurementProducts.Variant;
    public string OutputSchemaVersion => ExposureMeasurementProducts.SchemaVersion;
    public string OutputMediaType => ExposureMeasurementProducts.MediaType;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Calibrated };

    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements { get; } =
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Calibrated },
            new HashSet<string>(StringComparer.Ordinal)
            {
                BuiltInProcessingRecipes.LinearNormalization,
                BuiltInProcessingRecipes.ReferenceCalibration
            }, Required: false)
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rawDescriptor = context.ReconstructionDescriptor ??
            throw new InvalidDataException("Exposure measurement requires admitted raw evidence.");
        await context.EnsureRawFrameAsync(cancellationToken).ConfigureAwait(false);
        var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, context.Artifacts!.Raw,
            rawDescriptor.Artifact.Variant, context.AcquisitionTiming, rawDescriptor);

        var output = context.GetDependencyProducts().SingleOrDefault(product => product.Role == FrameArtifactRole.Calibrated);
        var (linear, linearDescriptor) = output is null ? (null, null) : BindLinear(context, rawDescriptor, output);
        var reason = output is null ? context.GetDependencyStepTelemetry()
            .SingleOrDefault(static step => !step.Succeeded)?.ErrorMessage ?? "linear-output-unavailable" : null;

        var disposition = output?.Recipe.Descriptor.Name switch
        {
            BuiltInProcessingRecipes.ReferenceCalibration => ExposureCalibrationDisposition.ReferenceCalibrated,
            BuiltInProcessingRecipes.LinearNormalization => ExposureCalibrationDisposition.NormalizationOnly,
            null when reason is CalibrationLibraryReasonCodes.IncompatibleIdentity or
                ProcessingReasonCodes.CalibrationReferenceLayoutMismatch or
                ProcessingReasonCodes.CalibrationReferenceConditionsMismatch =>
                ExposureCalibrationDisposition.Incompatible,
            null when reason is CalibrationLibraryReasonCodes.Missing or CalibrationLibraryReasonCodes.Inactive or
                ProcessingReasonCodes.MissingCalibrationProfile or ProcessingReasonCodes.MissingCalibrationReference or
                "linear-output-unavailable" =>
                ExposureCalibrationDisposition.Missing,
            null => ExposureCalibrationDisposition.Failed,
            _ => throw new InvalidDataException("The declared linear producer is unsupported.")
        };

        var rig = context.Config.Rig;
        var projection = RigProjectionContextFactory.Create(rig);
        var layout = rawDescriptor.Layout;
        ProjectionContext? aperture = projection.WidthPixels == layout.Width && projection.HeightPixels == layout.Height
            ? projection : null;
        var input = new ExposureMeasurementInputV1(ExposureMeasurementSourceV1.FromDescriptor(rawDescriptor),
            linearDescriptor is null ? null : ExposureMeasurementSourceV1.FromDescriptor(linearDescriptor),
            disposition, reason, raw.Compatibility.Orientation,
            rig.Sensor.PixelSizeMicrons > 0 ? rig.Sensor.PixelSizeMicrons : null, rig.Orientation, aperture);

        var outcome = await adapter.ExecuteAsync(context,
            ExposureMeasurementProducts.Request(input, raw, linear), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
    }

    internal static (ProcessingArtifact Artifact, ReconstructionDescriptor Descriptor) BindLinear(CaptureProcessingContext context,
        ReconstructionDescriptor rawDescriptor, ProcessingProduct product)
    {
        var artifact = context.FindArtifact(product);
        var sourceId = artifact.Frame.Metadata.SourceId ?? context.GetDependencyProducerStepId(artifact.ArtifactId) ??
            throw new InvalidDataException("The linear output has no recorded producer.");
        var descriptor = DerivativeDescriptorFactory.Create(rawDescriptor, artifact.ArtifactId, sourceId, product);
        return (CameraAgentRecipeExecutionAdapter.CreateArtifact(context, product) with
        {
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor)
        }, descriptor);
    }
}

internal sealed class ExposureMeasurementStepOptions;
