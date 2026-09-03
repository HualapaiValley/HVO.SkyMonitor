using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class WeatherCloudOverlayCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    WeatherCloudOverlayProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter,
    CameraAgentCloudEnvironment? cloudEnvironment = null)
    : ConfigurableCaptureProcessingStep<WeatherCloudOverlayProcessingStepOptions>(metadata, options),
        ICaptureProcessingGraphStep,
        ICompoundCaptureProcessingGraphStep
{
    public bool Enabled => Options.Enabled;

    public string RecipeName => BuiltInProcessingRecipes.WeatherCloudOverlay;

    public FrameArtifactRole OutputRole => FrameArtifactRole.AnnotatedPreview;

    public string OutputVariant => Options.OutputVariant;

    public string? OutputMediaType => "application/x-hvo-packed-image";

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole>
        {
            FrameArtifactRole.Preview,
            FrameArtifactRole.AnnotatedPreview,
            FrameArtifactRole.Metadata
        };

    public IReadOnlyList<IReadOnlySet<FrameArtifactRole>> RequiredDependencyRoleGroups { get; } =
    [
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview, FrameArtifactRole.AnnotatedPreview },
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }
    ];

    public IReadOnlyDictionary<FrameArtifactRole, IReadOnlySet<string>> RequiredDependencyRecipes { get; } =
        new Dictionary<FrameArtifactRole, IReadOnlySet<string>>
        {
            [FrameArtifactRole.Metadata] = new HashSet<string>(StringComparer.Ordinal)
            {
                BuiltInProcessingRecipes.CloudAssessment
            }
        };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled)
        {
            return;
        }
        var previewArtifact = context.GetDependencyArtifacts().SingleOrDefault(static artifact =>
            artifact.Role is FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview);
        var products = context.GetDependencyProducts();
        var previewProduct = products.SingleOrDefault(static product =>
            product.Role is FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview);
        var assessmentProduct = products.SingleOrDefault(static product => product.Role == FrameArtifactRole.Metadata);
        if (previewArtifact is null || previewProduct is null || assessmentProduct is null)
        {
            return;
        }
        var preview = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config,
            previewArtifact,
            previewProduct.Variant,
            context.AcquisitionTiming,
            context.ReconstructionDescriptor,
            previewProduct) with
        {
            RecipeIdentitySha256 = previewProduct.Recipe.IdentitySha256
        };
        var assessmentArtifactId = CaptureProcessingContext.CreateArtifactId(assessmentProduct.OutputIdentitySha256);
        var assessment = new ProcessingArtifact(
            assessmentArtifactId,
            assessmentProduct.Role,
            assessmentProduct.Variant,
            assessmentProduct.Recipe.IdentitySha256,
            assessmentProduct.MediaType,
            null,
            assessmentProduct.Payload,
            preview.CreatedUtc,
            assessmentProduct.TotalIntegration,
            assessmentProduct.Compatibility,
            ObservationStartedUtc: preview.ObservationStartedUtc,
            ObservationEndedUtc: preview.ObservationEndedUtc)
        {
            ProductKind = assessmentProduct.Kind,
            SchemaVersion = assessmentProduct.SchemaVersion,
            ContentIdentitySha256 = assessmentProduct.ContentIdentitySha256
        };
        var environment = cloudEnvironment is null
            ? CameraAgentCloudEnvironment.CreateMissingInput(context)
            : await cloudEnvironment.CreateInputAsync(context, cancellationToken).ConfigureAwait(false);
        if (environment is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(
                ProcessingReasonCodes.EnvironmentAssociationPending));
            return;
        }
        var auxiliary = new ProcessingAuxiliaryInput[]
        {
            new(
                "assessment",
                ProcessingAuxiliaryInputKind.Artifact,
                ProcessingInputSelector.RecipeResult(
                    assessment.Role,
                    assessment.Variant,
                    assessment.RecipeIdentitySha256),
                ArtifactId: assessment.ArtifactId),
            environment
        };
        var outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.WeatherCloudOverlay,
            JsonSerializer.SerializeToElement(new WeatherCloudOverlayOptions(
                Options.LineThickness,
                Options.DrawLabels,
                Options.MaximumLabelCharacters,
                OutputEncoding: "Packed")),
            ProcessingInputSelector.RecipeResult(
                preview.Role,
                preview.Variant,
                preview.RecipeIdentitySha256),
            [preview, assessment],
            Options.OutputVariant,
            AuxiliaryInputs: auxiliary,
            InputArtifactId: preview.ArtifactId), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
        if (outcome.Status != ProcessingOutcomeStatus.Produced)
        {
            return;
        }
        var product = outcome.Products.Single();
        var artifact = context.AddDerivative(
            FrameArtifactRole.AnnotatedPreview,
            CameraAgentRecipeExecutionAdapter.CreateFrame(product, previewArtifact.Frame, "WeatherCloudOverlay"),
            Options.RecipeVersion,
            product.SourceArtifactIds,
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        context.AssociateProcessingProduct(artifact, product);
    }
}

public sealed class WeatherCloudOverlayProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Required(AllowEmptyStrings = false)]
    public string RecipeVersion { get; init; } = "weather-cloud-overlay-v1";

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "weather-cloud-overlay-v1";

    [Range(1, 8)]
    public int LineThickness { get; init; } = 1;

    public bool DrawLabels { get; init; } = true;

    [Range(0, 128)]
    public int MaximumLabelCharacters { get; init; } = 32;
}
