using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class FrameFactsPresentationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    FrameFactsPresentationProcessingStepOptions options,
    PresentationMetadataFactsBuilder factsBuilder)
    : ConfigurableCaptureProcessingStep<FrameFactsPresentationProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, IMultiOutputCaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    internal const string Recipe = "environment-presentation-layer";
    private static readonly ProcessingRecipeDefinition SharedFactsRecipe = new(
        PresentationProcessingProducts.MetadataFactsRecipeName,
        "1.0.0",
        FrameFactsPresentation.ProducerVersion,
        ProcessingOperationKind.Transform);
    private static readonly ProcessingRecipeDefinition SharedLayerRecipe = new(
        PresentationProcessingProducts.LayerRecipeName,
        "1.0.0",
        FrameFactsPresentation.ProducerVersion,
        ProcessingOperationKind.Transform);
    public bool Enabled => true;
    public string RecipeName => Recipe;
    public string? SharedStepVersion => FrameFactsPresentation.ProducerVersion;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
    public string? OutputSchemaVersion => PresentationLayerPayloadV1.RichTextSchemaVersion;
    public string? OutputMediaType => PresentationLayerPayloadJson.MediaType;
    public IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(FrameArtifactRole.Metadata, Options.FactsOutputVariant,
            PresentationProcessingProducts.MetadataFactsRecipeName, PresentationMetadataFactsProductV1.RichTextSchemaVersion,
            SharedFactsRecipe, MediaType: PresentationMetadataFactsProductV1.MediaType),
        new(FrameArtifactRole.Metadata, Options.OutputVariant, RecipeName, OutputSchemaVersion, SharedLayerRecipe,
            MediaType: OutputMediaType)
    ];
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole>
    {
        FrameArtifactRole.Metadata,
        FrameArtifactRole.Preview
    };
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string>(StringComparer.Ordinal) { BuiltInProcessingRecipes.ProjectedScene },
            new HashSet<string>(ProjectedSceneV1.SupportedSchemaVersions, StringComparer.Ordinal)),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview },
            Variant: Options.StackPreviewVariant)
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        var descriptor = context.ReconstructionDescriptor
            ?? throw new InvalidOperationException("Environment presentation requires a reconstruction descriptor.");
        var dependencyProducts = context.GetDependencyProducts();
        var sceneProduct = dependencyProducts.Single(product => ProjectedSceneV1.IsSupportedSchemaVersion(product.SchemaVersion));
        var scene = ProjectedSceneJson.Parse(sceneProduct.Payload).Scene
            ?? throw new InvalidDataException("The projected-scene dependency is invalid.");
        var previewProduct = dependencyProducts.Single(product => product.Role == FrameArtifactRole.Preview);
        var stackProduct = ResolveStackProduct(context, previewProduct) ?? throw new InvalidDataException(
            "Environment presentation requires combined-preview stack lineage.");
        var sceneArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, sceneProduct);
        var stackArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, stackProduct);
        var facts = await factsBuilder.BuildAsync(
            context, scene, stackProduct, [sceneArtifact.ArtifactId, stackArtifact.ArtifactId],
            Options.EnvironmentalKinds, cancellationToken, retainFrameFacts: true).ConfigureAwait(false);
        if (facts is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(ProcessingReasonCodes.EnvironmentAssociationPending));
            return;
        }
        var factsProduct = PresentationProcessingProducts.CreateMetadataFactsProduct(
            facts, Options.FactsOutputVariant, [sceneArtifact, stackArtifact]);
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, factsProduct);
        var payload = FrameFactsPresentation.Create(facts.FactsIdentitySha256, facts.FrameFacts!, Options);
        var product = PresentationProcessingProducts.CreateLayerProduct(
            payload, Options.OutputVariant, [source], FrameFactsPresentation.ProducerVersion);
        LayeredPresentationCaptureProcessing.Add(context, factsProduct, product);

        static ProcessingProduct? ResolveStackProduct(CaptureProcessingContext context, ProcessingProduct previewProduct)
        {
            var current = previewProduct;
            for (var depth = 0; depth < 4; depth++)
            {
                if (string.Equals(current.Recipe.Descriptor.Name, BuiltInProcessingRecipes.RollingMean, StringComparison.Ordinal))
                    return current;
                if (current.SourceArtifactIds.Count != 1 || context.GetProcessingProduct(current.SourceArtifactIds[0]) is not { } source)
                    return null;
                current = source;
            }
            return null;
        }
    }
}

internal sealed class FrameFactsPresentationProcessingStepOptions : FrameFactsPresentationOptions, IValidatableObject
{
    [Required, MaxLength(128)] public string FactsOutputVariant { get; init; } = "presentation-metadata-facts-v3";
    [Required, MaxLength(128)] public string OutputVariant { get; init; } = "environment-layer-v2";
    [Required, MaxLength(128)] public string StackPreviewVariant { get; init; } = "combined-preview";
    public IReadOnlyList<EnvironmentalObservationKind> EnvironmentalKinds { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        string? error = null;
        try { FrameFactsPresentation.ValidateOptions(this); }
        catch (ArgumentException exception) { error = exception.Message; }
        if (error is not null) yield return new ValidationResult(error);
        if (EnvironmentalKinds is null || EnvironmentalKinds.Count > 12 ||
            EnvironmentalKinds.Any(kind => !Enum.IsDefined(kind)) || EnvironmentalKinds.Distinct().Count() != EnvironmentalKinds.Count)
            yield return new ValidationResult("Environmental kinds must be bounded, defined and unique.", [nameof(EnvironmentalKinds)]);
    }
}
