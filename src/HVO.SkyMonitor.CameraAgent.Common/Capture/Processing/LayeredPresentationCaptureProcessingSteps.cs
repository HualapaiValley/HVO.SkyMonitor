using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class LayeredPresentationCaptureProcessing
{
    internal static void Add(CaptureProcessingContext context, params ProcessingProduct[] products)
    {
        var outcome = ProcessingOutcome.Produced(products);
        context.AddProcessingOutcome(outcome);
        foreach (var product in products)
        {
            context.RegisterProcessingProduct(product);
        }
    }

    internal static PresentationLayerProductInput Layer(
        ProcessingProduct product,
        string kind,
        string? sceneIdentity,
        int zOrder,
        PresentationBlendMode blendMode,
        int opacityMillionths,
        bool enabled,
        CaptureProcessingContext context,
        PresentationCompatibilityDescriptor compatibility)
    {
        var artifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, product);
        var payload = PresentationLayerPayloadJson.Parse(product.Payload).Payload
            ?? throw new InvalidDataException($"Presentation layer '{product.Variant}' is invalid.");
        var reference = new PresentationProductReference(
            artifact.ArtifactId, payload.ContentIdentitySha256, artifact.MediaType, compatibility);
        var layer = LayeredPresentationJson.CreateLayer(
            kind, reference, sceneIdentity, PresentationCoordinateSpace.ScenePixels,
            PresentationLayerCompositor.AlgorithmVersion, product.Recipe.Descriptor.ImplementationVersion,
            zOrder, blendMode, opacityMillionths, enabled, JsonSerializer.SerializeToElement(new { product.Variant }));
        return new(layer, artifact);
    }
}

internal sealed class ScenePresentationLayerCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    ScenePresentationLayerProcessingStepOptions options)
    : ConfigurableCaptureProcessingStep<ScenePresentationLayerProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, IMultiOutputCaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    internal const string Recipe = "scene-presentation-layer";
    public bool Enabled => true;
    public string RecipeName => Recipe;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.AnnotationOutputVariant;
    public string? OutputSchemaVersion => PresentationLayerPayloadV1.CurrentSchemaVersion;
    public IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(OutputRole, Options.AnnotationOutputVariant, RecipeName, OutputSchemaVersion),
        new(OutputRole, Options.ConstellationOutputVariant, RecipeName, OutputSchemaVersion)
    ];
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata };
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string>(StringComparer.Ordinal) { BuiltInProcessingRecipes.ProjectedScene },
            new HashSet<string>(StringComparer.Ordinal) { ProjectedSceneV1.CurrentSchemaVersion })
    ];

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dependencyProducts = context.GetDependencyProducts();
        var sceneProduct = dependencyProducts.Single(product => product.SchemaVersion == ProjectedSceneV1.CurrentSchemaVersion);
        var scene = ProjectedSceneJson.Parse(sceneProduct.Payload).Scene
            ?? throw new InvalidDataException("The projected-scene dependency is invalid.");
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, sceneProduct);
        var style = new PresentationAnnotationStyleV1(
            Options.MarkerRadius, Options.LabelScale, Options.MaximumLabelCharacters, Options.MaximumLabelMagnitude,
            Options.ConstellationLineThickness, Options.ConstellationIds,
            new(Options.MarkerValue, Options.MarkerValue, Options.MarkerValue),
            new(Options.LabelValue, Options.LabelValue, Options.LabelValue),
            new(Options.ConstellationLineRed, Options.ConstellationLineGreen, Options.ConstellationLineBlue),
            new(Options.ImageCircleValue, Options.ImageCircleValue, Options.ImageCircleValue),
            new(Options.CardinalValue, Options.CardinalValue, Options.CardinalValue), Options.CardinalScale);
        var payloads = PresentationLayerProducers.FromProjectedSceneGroups(
            scene, style, Options.DrawMarkers, Options.DrawLabels, Options.DrawConstellationLines,
            Options.DrawImageCircle || Options.DrawCardinalDirections);
        var annotation = PresentationProcessingProducts.CreateLayerProduct(
            payloads.AnnotationAndGeometry, Options.AnnotationOutputVariant, [source], PresentationLayerProducers.SceneProducerVersion);
        var constellations = PresentationProcessingProducts.CreateLayerProduct(
            payloads.Constellations, Options.ConstellationOutputVariant, [source], PresentationLayerProducers.SceneProducerVersion);
        LayeredPresentationCaptureProcessing.Add(context, annotation, constellations);
        return ValueTask.CompletedTask;
    }
}

internal sealed class CloudPresentationLayerCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    CloudPresentationLayerProcessingStepOptions options)
    : ConfigurableCaptureProcessingStep<CloudPresentationLayerProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, IMultiOutputCaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    internal const string Recipe = "cloud-presentation-layer";
    public bool Enabled => true;
    public string RecipeName => Recipe;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.MaskOutputVariant;
    public string? OutputSchemaVersion => PresentationLayerPayloadV1.CurrentSchemaVersion;
    public IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(OutputRole, Options.MaskOutputVariant, RecipeName, OutputSchemaVersion),
        new(OutputRole, Options.LabelOutputVariant, RecipeName, OutputSchemaVersion)
    ];
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata };
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string>(StringComparer.Ordinal) { BuiltInProcessingRecipes.CloudAssessment },
            new HashSet<string>(StringComparer.Ordinal) { CloudAssessmentV1.CurrentSchemaVersion })
    ];

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assessmentProduct = context.GetDependencyProducts().Single();
        var assessment = CloudAssessmentJson.Parse(assessmentProduct.Payload).Assessment
            ?? throw new InvalidDataException("The cloud-assessment dependency is invalid.");
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, assessmentProduct);
        var payloads = PresentationLayerProducers.FromCloudAssessmentGroups(
            assessment, Options.WidthPixels, Options.HeightPixels, Options.DrawLabels, Options.LineThickness);
        var mask = PresentationProcessingProducts.CreateLayerProduct(
            payloads.Mask, Options.MaskOutputVariant, [source], PresentationLayerProducers.CloudProducerVersion);
        var labels = PresentationProcessingProducts.CreateLayerProduct(
            payloads.Labels, Options.LabelOutputVariant, [source], PresentationLayerProducers.CloudProducerVersion);
        LayeredPresentationCaptureProcessing.Add(context, mask, labels);
        return ValueTask.CompletedTask;
    }
}

internal sealed class EnvironmentPresentationLayerCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    EnvironmentPresentationLayerProcessingStepOptions options,
    PresentationMetadataFactsBuilder factsBuilder)
    : ConfigurableCaptureProcessingStep<EnvironmentPresentationLayerProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, IMultiOutputCaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    internal const string Recipe = "environment-presentation-layer";
    public bool Enabled => true;
    public string RecipeName => Recipe;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
    public string? OutputSchemaVersion => PresentationLayerPayloadV1.CurrentSchemaVersion;
    public IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(FrameArtifactRole.Metadata, Options.FactsOutputVariant,
            PresentationProcessingProducts.MetadataFactsRecipeName, PresentationMetadataFactsProductV1.CurrentSchemaVersion),
        new(FrameArtifactRole.Metadata, Options.OutputVariant, RecipeName, OutputSchemaVersion)
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
            new HashSet<string>(StringComparer.Ordinal) { ProjectedSceneV1.CurrentSchemaVersion }),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview },
            Variant: Options.StackPreviewVariant)
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        var descriptor = context.ReconstructionDescriptor
            ?? throw new InvalidOperationException("Environment presentation requires a reconstruction descriptor.");
        var dependencyProducts = context.GetDependencyProducts();
        var sceneProduct = dependencyProducts.Single(product => product.SchemaVersion == ProjectedSceneV1.CurrentSchemaVersion);
        var scene = ProjectedSceneJson.Parse(sceneProduct.Payload).Scene
            ?? throw new InvalidDataException("The projected-scene dependency is invalid.");
        var previewProduct = dependencyProducts.Single(product => product.Role == FrameArtifactRole.Preview);
        var stackProduct = ResolveStackProduct(context, previewProduct) ?? throw new InvalidDataException(
            "Environment presentation requires combined-preview stack lineage.");
        var sceneArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, sceneProduct);
        var stackArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, stackProduct);
        var facts = await factsBuilder.BuildAsync(
            context, scene, stackProduct, [sceneArtifact.ArtifactId, stackArtifact.ArtifactId],
            Options.EnvironmentalKinds, cancellationToken).ConfigureAwait(false);
        if (facts is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(ProcessingReasonCodes.EnvironmentAssociationPending));
            return;
        }
        var factsProduct = PresentationProcessingProducts.CreateMetadataFactsProduct(
            facts, Options.FactsOutputVariant, [sceneArtifact, stackArtifact]);
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, factsProduct);
        var payload = PresentationLayerProducers.FromMetadataFacts(
            facts.Corners, Options.WidthPixels, Options.HeightPixels,
            new PresentationColor(Options.Value, Options.Value, Options.Value),
            Options.Scale, Options.Inset, Options.LineSpacing);
        var product = PresentationProcessingProducts.CreateLayerProduct(
            payload, Options.OutputVariant, [source], PresentationLayerProducers.MetadataProducerVersion);
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

internal sealed class OverlayManifestCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    OverlayManifestProcessingStepOptions options)
    : ConfigurableCaptureProcessingStep<OverlayManifestProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    public bool Enabled => true;
    public string RecipeName => PresentationProcessingProducts.ManifestRecipeName;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
    public string? OutputSchemaVersion => OverlayManifestV1.CurrentSchemaVersion;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview, FrameArtifactRole.Metadata };
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview },
            new HashSet<string> { BuiltInProcessingRecipes.EncodedPreview }, Variant: Options.BasePreviewVariant),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { ScenePresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.SceneAnnotationVariant),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { ScenePresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.SceneConstellationVariant),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { CloudPresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.CloudMaskVariant, Required: false),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { CloudPresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.CloudLabelVariant, Required: false),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { PresentationProcessingProducts.MetadataFactsRecipeName }, new HashSet<string> { PresentationMetadataFactsProductV1.CurrentSchemaVersion }, Options.EnvironmentFactsVariant),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { EnvironmentPresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.EnvironmentVariant)
    ];

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var products = context.GetDependencyProducts();
        var baseProduct = products.Single(product => product.Role == FrameArtifactRole.Preview &&
            string.Equals(product.Variant, Options.BasePreviewVariant, StringComparison.Ordinal));
        var baseArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, baseProduct);
        var sceneProduct = products.Single(product => product.Variant == Options.SceneAnnotationVariant);
        var scenePayload = PresentationLayerPayloadJson.Parse(sceneProduct.Payload).Payload!;
        var reference = PresentationProcessingProducts.CreateReference(baseArtifact, scenePayload.SourceIdentitySha256);
        var selections = Options.Layers.ToDictionary(static item => item.Kind, StringComparer.Ordinal);
        var layerProducts = new List<PresentationLayerProductInput>
        {
            CreateLayer(sceneProduct, "scene-annotation", 20, PresentationBlendMode.Normal, 1_000_000),
            CreateLayer(products.Single(product => product.Variant == Options.SceneConstellationVariant), "scene-constellations", 10, PresentationBlendMode.Normal, Options.ConstellationOpacityMillionths),
            CreateLayer(products.Single(product => product.Variant == Options.EnvironmentVariant), "environment", 50, PresentationBlendMode.Normal, 1_000_000)
        };
        if (products.SingleOrDefault(product => product.Variant == Options.CloudMaskVariant) is { } cloudMask &&
            products.SingleOrDefault(product => product.Variant == Options.CloudLabelVariant) is { } cloudLabels)
        {
            layerProducts.Add(CreateLayer(cloudMask, "cloud-mask", 30, PresentationBlendMode.Normal, 1_000_000));
            layerProducts.Add(CreateLayer(cloudLabels, "cloud-labels", 40, PresentationBlendMode.Lighten, 1_000_000));
        }
        var manifest = PresentationProcessingProducts.CreateManifestProduct(
            reference, scenePayload.SourceIdentitySha256, layerProducts, Options.OutputVariant, baseArtifact);
        LayeredPresentationCaptureProcessing.Add(context, manifest);
        return ValueTask.CompletedTask;

        PresentationLayerProductInput CreateLayer(
            ProcessingProduct product, string kind, int defaultZOrder,
            PresentationBlendMode blendMode, int opacityMillionths)
        {
            var selection = selections.GetValueOrDefault(kind);
            return LayeredPresentationCaptureProcessing.Layer(product, kind, scenePayload.SourceIdentitySha256,
                selection?.ZOrder ?? defaultZOrder, blendMode, opacityMillionths, selection?.Enabled ?? true,
                context, reference.Compatibility);
        }
    }
}

internal sealed class PresentationMaterializerCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    PresentationMaterializerProcessingStepOptions options)
    : ConfigurableCaptureProcessingStep<PresentationMaterializerProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    public bool Enabled => true;
    public string RecipeName => PresentationProcessingProducts.MaterializationRecipeName;
    public FrameArtifactRole OutputRole => FrameArtifactRole.AnnotatedPreview;
    public string OutputVariant => Options.OutputVariant;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview, FrameArtifactRole.Metadata };
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview },
            new HashSet<string> { BuiltInProcessingRecipes.EncodedPreview }, Variant: Options.BasePreviewVariant),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { PresentationProcessingProducts.ManifestRecipeName }, new HashSet<string> { OverlayManifestV1.CurrentSchemaVersion }),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { ScenePresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.SceneAnnotationVariant),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { ScenePresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.SceneConstellationVariant),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { CloudPresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.CloudMaskVariant, Required: false),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { CloudPresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.CloudLabelVariant, Required: false),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { PresentationProcessingProducts.MetadataFactsRecipeName }, new HashSet<string> { PresentationMetadataFactsProductV1.CurrentSchemaVersion }, Options.EnvironmentFactsVariant),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { EnvironmentPresentationLayerCaptureProcessingStep.Recipe }, new HashSet<string> { PresentationLayerPayloadV1.CurrentSchemaVersion }, Options.EnvironmentVariant)
    ];

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        var products = context.GetDependencyProducts();
        var baseProduct = products.Single(product => product.Role == FrameArtifactRole.Preview &&
            string.Equals(product.Variant, Options.BasePreviewVariant, StringComparison.Ordinal));
        var manifestProduct = products.Single(product => product.SchemaVersion == OverlayManifestV1.CurrentSchemaVersion);
        var manifest = LayeredPresentationJson.ParseManifest(manifestProduct.Payload).Document
            ?? throw new InvalidDataException("The overlay-manifest dependency is invalid.");
        var suppliedLayers = products.Where(product => product.SchemaVersion == PresentationLayerPayloadV1.CurrentSchemaVersion)
            .ToDictionary(product => CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        if (suppliedLayers.Count != products.Count(product => product.SchemaVersion == PresentationLayerPayloadV1.CurrentSchemaVersion) ||
            suppliedLayers.Keys.Any(id => manifest.Layers.All(layer => layer.SourceProduct.ArtifactId != id)))
            throw new InvalidDataException("Materializer dependencies contain duplicate or undeclared layer products.");
        var layers = manifest.Layers.Where(layer => suppliedLayers.ContainsKey(layer.SourceProduct.ArtifactId))
            .Select(layer => new PresentationLayerProductInput(layer,
                CameraAgentRecipeExecutionAdapter.CreateArtifact(context, suppliedLayers[layer.SourceProduct.ArtifactId])))
            .ToArray();
        var product = PresentationMaterializationExecutor.MaterializePacked(
            CameraAgentRecipeExecutionAdapter.CreateArtifact(context, baseProduct),
            CameraAgentRecipeExecutionAdapter.CreateArtifact(context, manifestProduct), manifest, layers,
            manifest.Layers.Where(layer => Options.EnabledLayerKinds.Count == 0
                    ? layer.EnabledByDefault
                    : Options.EnabledLayerKinds.Contains(layer.LayerKind, StringComparer.Ordinal))
                .Select(static layer => layer.LayerIdentitySha256),
            Options.OutputVariant, cancellationToken: cancellationToken);
        LayeredPresentationCaptureProcessing.Add(context, product);
        var baseFrame = context.GetDependencyArtifacts().Single(static artifact => artifact.Role == FrameArtifactRole.Preview).Frame;
        var artifact = context.AddDerivative(FrameArtifactRole.AnnotatedPreview,
            CameraAgentRecipeExecutionAdapter.CreateFrame(product, baseFrame, "PresentationMaterializer"),
            product.Recipe.IdentitySha256, product.SourceArtifactIds,
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        context.AssociateProcessingProduct(artifact, product);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScenePresentationLayerProcessingStepOptions : IValidatableObject
{
    [Required, MaxLength(128)] public string AnnotationOutputVariant { get; init; } = "scene-annotation-layer-v1";
    [Required, MaxLength(128)] public string ConstellationOutputVariant { get; init; } = "scene-constellation-layer-v1";
    [Range(0, 32)] public int MarkerRadius { get; init; } = 6;
    [Range(1, 8)] public int LabelScale { get; init; } = 2;
    [Range(0, 64)] public int MaximumLabelCharacters { get; init; } = 24;
    [Range(-30, 30)] public double MaximumLabelMagnitude { get; init; } = 2.5;
    [Range(1, 8)] public int ConstellationLineThickness { get; init; } = 2;
    public IReadOnlyList<string> ConstellationIds { get; init; } = [];
    public bool DrawMarkers { get; init; } = true;
    public bool DrawLabels { get; init; } = true;
    public bool DrawConstellationLines { get; init; } = true;
    public bool DrawImageCircle { get; init; } = true;
    public bool DrawCardinalDirections { get; init; } = true;
    [Range(0, 255)] public byte MarkerValue { get; init; } = 144;
    [Range(0, 255)] public byte LabelValue { get; init; } = 255;
    [Range(0, 255)] public byte ConstellationLineRed { get; init; } = 96;
    [Range(0, 255)] public byte ConstellationLineGreen { get; init; } = 160;
    [Range(0, 255)] public byte ConstellationLineBlue { get; init; } = 255;
    [Range(0, 255)] public byte ImageCircleValue { get; init; } = 96;
    [Range(0, 255)] public byte CardinalValue { get; init; } = 255;
    [Range(1, 8)] public int CardinalScale { get; init; } = 2;
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ConstellationIds.Count > 256 || ConstellationIds.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 16))
            yield return new ValidationResult("Constellation selections exceed their bounds.", [nameof(ConstellationIds)]);
    }
}

internal sealed class CloudPresentationLayerProcessingStepOptions
{
    [Required, MaxLength(128)] public string MaskOutputVariant { get; init; } = "cloud-mask-layer-v1";
    [Required, MaxLength(128)] public string LabelOutputVariant { get; init; } = "cloud-label-layer-v1";
    [Range(1, 65536)] public int WidthPixels { get; init; }
    [Range(1, 65536)] public int HeightPixels { get; init; }
    [Range(1, 8)] public int LineThickness { get; init; } = 1;
    public bool DrawLabels { get; init; } = true;
}

internal sealed class EnvironmentPresentationLayerProcessingStepOptions
{
    [Required, MaxLength(128)] public string FactsOutputVariant { get; init; } = "presentation-metadata-facts-v1";
    [Required, MaxLength(128)] public string OutputVariant { get; init; } = "environment-layer-v1";
    [Required, MaxLength(128)] public string StackPreviewVariant { get; init; } = "combined-preview";
    [Range(1, 65536)] public int WidthPixels { get; init; }
    [Range(1, 65536)] public int HeightPixels { get; init; }
    [Range(0, 255)] public byte Value { get; init; } = 255;
    [Range(1, 4)] public int Scale { get; init; } = 1;
    [Range(0, 64)] public int Inset { get; init; } = 4;
    [Range(0, 16)] public int LineSpacing { get; init; } = 2;
    public IReadOnlyList<HVO.SkyMonitor.Processing.EnvironmentalObservationKind> EnvironmentalKinds { get; init; } =
    [
        HVO.SkyMonitor.Processing.EnvironmentalObservationKind.AirTemperature,
        HVO.SkyMonitor.Processing.EnvironmentalObservationKind.RelativeHumidity,
        HVO.SkyMonitor.Processing.EnvironmentalObservationKind.AtmosphericPressure,
        HVO.SkyMonitor.Processing.EnvironmentalObservationKind.WindSpeed,
        HVO.SkyMonitor.Processing.EnvironmentalObservationKind.RainState,
        HVO.SkyMonitor.Processing.EnvironmentalObservationKind.CloudCover
    ];
}

internal sealed class OverlayManifestProcessingStepOptions : IValidatableObject
{
    [Required, MaxLength(128)] public string OutputVariant { get; init; } = "overlay-manifest-v1";
    [Required, MaxLength(128)] public string BasePreviewVariant { get; init; } = "combined-preview";
    [Required, MaxLength(128)] public string SceneAnnotationVariant { get; init; } = "scene-annotation-layer-v1";
    [Required, MaxLength(128)] public string SceneConstellationVariant { get; init; } = "scene-constellation-layer-v1";
    [Required, MaxLength(128)] public string CloudMaskVariant { get; init; } = "cloud-mask-layer-v1";
    [Required, MaxLength(128)] public string CloudLabelVariant { get; init; } = "cloud-label-layer-v1";
    [Required, MaxLength(128)] public string EnvironmentVariant { get; init; } = "environment-layer-v1";
    [Required, MaxLength(128)] public string EnvironmentFactsVariant { get; init; } = "presentation-metadata-facts-v1";
    [Range(0, 1_000_000)] public int ConstellationOpacityMillionths { get; init; } = 800_000;
    public IReadOnlyList<PresentationLayerSelectionOptions> Layers { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var allowed = new HashSet<string>(["scene-annotation", "scene-constellations", "cloud-mask", "cloud-labels", "environment"], StringComparer.Ordinal);
        if (Layers.Count > allowed.Count || Layers.Any(item => item is null || !allowed.Contains(item.Kind)) ||
            Layers.Select(static item => item.Kind).Distinct(StringComparer.Ordinal).Count() != Layers.Count)
            yield return new ValidationResult("Layer overrides must be unique supported layer kinds.", [nameof(Layers)]);
    }
}

internal sealed class PresentationMaterializerProcessingStepOptions : IValidatableObject
{
    [Required, MaxLength(128)] public string OutputVariant { get; init; } = "w6-annotated-preview";
    [Required, MaxLength(128)] public string BasePreviewVariant { get; init; } = "combined-preview";
    [Required, MaxLength(128)] public string SceneAnnotationVariant { get; init; } = "scene-annotation-layer-v1";
    [Required, MaxLength(128)] public string SceneConstellationVariant { get; init; } = "scene-constellation-layer-v1";
    [Required, MaxLength(128)] public string CloudMaskVariant { get; init; } = "cloud-mask-layer-v1";
    [Required, MaxLength(128)] public string CloudLabelVariant { get; init; } = "cloud-label-layer-v1";
    [Required, MaxLength(128)] public string EnvironmentVariant { get; init; } = "environment-layer-v1";
    [Required, MaxLength(128)] public string EnvironmentFactsVariant { get; init; } = "presentation-metadata-facts-v1";
    public IReadOnlyList<string> EnabledLayerKinds { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var allowed = new HashSet<string>(["scene-annotation", "scene-constellations", "cloud-mask", "cloud-labels", "environment"], StringComparer.Ordinal);
        if (EnabledLayerKinds.Count > allowed.Count || EnabledLayerKinds.Any(kind => !allowed.Contains(kind)) ||
            EnabledLayerKinds.Distinct(StringComparer.Ordinal).Count() != EnabledLayerKinds.Count)
            yield return new ValidationResult("Enabled layers must be unique supported layer kinds.", [nameof(EnabledLayerKinds)]);
    }
}

internal sealed class PresentationLayerSelectionOptions
{
    [Required, MaxLength(64)] public string Kind { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    [Range(-1024, 1024)] public int? ZOrder { get; init; }
}
