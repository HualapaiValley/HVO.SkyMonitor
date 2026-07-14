using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates an annotated preview through the shared projector contract.</summary>
internal sealed class AnnotationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    AnnotationProcessingStepOptions options,
    IProjectedSceneStore sceneStore,
    IAnnotationSceneProvider annotationSceneProvider,
    CameraAgentRecipeExecutionAdapter adapter)
    : ConfigurableCaptureProcessingStep<AnnotationProcessingStepOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => Options.Enabled;

    public string RecipeName => BuiltInProcessingRecipes.Annotation;

    public FrameArtifactRole OutputRole => FrameArtifactRole.AnnotatedPreview;

    public string OutputVariant => Options.OutputVariant;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var preview = context.GetDependencyArtifacts()
            .LastOrDefault(static artifact => artifact.Role == FrameArtifactRole.Preview)
            ?? context.Artifacts?.Artifacts.GetValueOrDefault(FrameArtifactRole.Preview);
        if (!Options.Enabled || context.Artifacts is not { } artifacts || preview is null ||
            preview.Frame.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24))
        {
            return;
        }

        var frame = preview.Frame;
        var provenance = artifacts.Raw.Frame.Metadata.Scene;
        AnnotationSceneResult? generatedScene = null;
        ProjectionContext? projectionOnly = null;
        if (provenance is null && Options.DrawConstellationLines && Options.ConstellationIds.Count > 0)
        {
            generatedScene = await annotationSceneProvider.BuildAsync(
                context.Config, artifacts.Raw.Frame, Options.ConstellationIds, cancellationToken).ConfigureAwait(false);
            provenance = generatedScene.Provenance;
        }
        if (provenance is null && (Options.DrawImageCircle || Options.DrawCardinalDirections))
        {
            projectionOnly = RigProjectionContextFactory.Create(context.Config.Rig);
        }
        if (provenance is null && projectionOnly is null)
        {
            return;
        }

        var transform = new PreviewTransform(
            (double)frame.Width / artifacts.Raw.Frame.Width,
            (double)frame.Height / artifacts.Raw.Frame.Height);
        IReadOnlyList<ProjectedAnnotationObject> objects;
        IReadOnlyList<ProjectedAnnotationSegment> segments;
        ProjectedAnnotationOverlay? projectionOverlay;
        if (projectionOnly is { } projection)
        {
            objects = [];
            segments = [];
            projectionOverlay = CreateProjectionOverlay(projection);
        }
        else if (generatedScene is not null)
        {
            objects = [];
            segments = generatedScene.Scene.Segments.Select(static item => new ProjectedAnnotationSegment(
                item.ConstellationId, item.FromPixel, item.ToPixel)).ToArray();
            projectionOverlay = CreateProjectionOverlay(generatedScene.Scene.Request.Projection);
        }
        else if (sceneStore.TryGet(provenance!.SceneId, out var scene) && scene is not null)
        {
            objects = scene.Objects.Select(item =>
            {
                var annotate = IsNamed(item.Id, item.DisplayName) &&
                    (item.Kind == CelestialObjectKind.SolarSystemBody || item.Magnitude <= Options.MaximumLabelMagnitude);
                return new ProjectedAnnotationObject(item.Id, item.DisplayName, item.Pixel, annotate, annotate);
            }).ToArray();
            segments = Options.DrawConstellationLines
                ? scene.Segments.Where(item => IsSelectedConstellation(item.ConstellationId))
                    .Select(static item => new ProjectedAnnotationSegment(
                        item.ConstellationId, item.FromPixel, item.ToPixel)).ToArray()
                : [];
            projectionOverlay = CreateProjectionOverlay(scene.Request.Projection);
        }
        else if (provenance.Objects is { } persistedObjects)
        {
            objects = persistedObjects.Select(item =>
            {
                var annotate = IsNamed(item.Id, item.DisplayName) &&
                    (item.Id.StartsWith("solar-system:", StringComparison.Ordinal) ||
                     item.Magnitude <= Options.MaximumLabelMagnitude);
                return new ProjectedAnnotationObject(
                    item.Id, item.DisplayName, new PixelPoint(item.PixelX, item.PixelY), annotate, annotate);
            }).ToArray();
            segments = Options.DrawConstellationLines
                ? provenance.Segments?.Where(item => IsSelectedConstellation(item.ConstellationId))
                    .Select(static item => new ProjectedAnnotationSegment(
                        item.ConstellationId, new PixelPoint(item.FromPixelX, item.FromPixelY),
                        new PixelPoint(item.ToPixelX, item.ToPixelY))).ToArray() ?? []
                : [];
            projectionOverlay = null;
        }
        else
        {
            throw new InvalidOperationException("The projected scene required for annotation is unavailable.");
        }

        var previewProduct = context.GetProcessingProduct(preview.ArtifactId);
        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config,
            preview,
            previewProduct?.Variant ?? preview.RecipeVersion ?? "legacy-preview");
        if (previewProduct is not null)
        {
            input = input with { RecipeIdentitySha256 = previewProduct.Recipe.IdentitySha256 };
        }
        var annotationInput = new ProcessingAnnotationInput(
            objects,
            segments,
            transform,
            projectionOverlay,
            CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new
            {
                sceneId = provenance?.SceneId,
                objects,
                segments,
                projectionOverlay
            })));
        var recipeOptions = JsonSerializer.SerializeToElement(new AnnotationRecipeOptions(
            MarkRadius: Options.MarkRadius,
            MarkerValue: Options.MarkerValue,
            DrawLabels: Options.DrawLabels,
            LabelScale: Options.LabelScale,
            DrawImageCircle: Options.DrawImageCircle,
            DrawCardinalDirections: Options.DrawCardinalDirections,
            ImageCircleValue: Options.ImageCircleValue,
            CardinalValue: Options.CardinalValue,
            CardinalScale: Options.CardinalScale,
            ConstellationLineValue: Options.ConstellationLineValue,
            ConstellationLineRed: Options.ConstellationLineRed,
            ConstellationLineGreen: Options.ConstellationLineGreen,
            ConstellationLineBlue: Options.ConstellationLineBlue,
            ConstellationLineThickness: Options.ConstellationLineThickness,
            ConstellationLineOpacity: Options.ConstellationLineOpacity,
            OutputEncoding: "Packed"));
        var outcome = await adapter.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.Annotation,
            recipeOptions,
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                input.Variant,
                input.RecipeIdentitySha256),
            [input],
            Options.OutputVariant,
            annotationInput), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
        if (outcome.Status != ProcessingOutcomeStatus.Produced)
        {
            return;
        }

        var product = outcome.Products[0];
        var annotated = CameraAgentRecipeExecutionAdapter.CreateFrame(product, frame, "AnnotatedPreview");
        var artifact = context.AddDerivative(FrameArtifactRole.AnnotatedPreview,
            annotated with
            {
                Metadata = CreateAnnotationMetadata(
                    annotated.Metadata,
                    generatedScene?.Provenance,
                    product.Recipe.IdentitySha256)
            },
            Options.RecipeVersion,
            [preview.ArtifactId],
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        context.AssociateProcessingProduct(artifact, product);
    }

    private static bool IsNamed(string id, string displayName)
        => !string.IsNullOrWhiteSpace(displayName) && !string.Equals(id, displayName, StringComparison.Ordinal);

    private bool IsSelectedConstellation(string id)
        => Options.ConstellationIds.Count == 0 ||
           Options.ConstellationIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    private FrameMetadata CreateAnnotationMetadata(
        FrameMetadata metadata,
        SceneProvenance? generatedProvenance,
        string recipeIdentity)
    {
        var extra = metadata.Extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata.Extra, StringComparer.Ordinal);
        extra["annotationRecipeVersion"] = Options.RecipeVersion;
        extra["annotationRecipeIdentitySha256"] = recipeIdentity;
        extra["constellationLineValue"] = Options.ConstellationLineValue.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        extra["constellationLineRgb"] = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Options.ConstellationLineRed},{Options.ConstellationLineGreen},{Options.ConstellationLineBlue}");
        extra["constellationLineThickness"] = Options.ConstellationLineThickness.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        extra["constellationLineOpacity"] = Options.ConstellationLineOpacity.ToString(
            "R", System.Globalization.CultureInfo.InvariantCulture);
        extra["annotationConstellationIds"] = string.Join(",", Options.ConstellationIds);
        return metadata with
        {
            SourceId = "AnnotatedPreview",
            Extra = extra,
            Scene = generatedProvenance ?? metadata.Scene
        };
    }

    private static ProjectedAnnotationOverlay? CreateProjectionOverlay(ProjectionContext projection)
    {
        if (projection.ImageCircleRadiusPixels is not { } radius)
        {
            return null;
        }

        var projector = ProjectorFactory.Create(projection with { EnforceSensorBounds = false });
        var north = projector.Project(new AltAzPoint(0, 0));
        var east = projector.Project(new AltAzPoint(0, 90));
        var south = projector.Project(new AltAzPoint(0, 180));
        var west = projector.Project(new AltAzPoint(0, 270));
        return north is null || east is null || south is null || west is null
            ? null
            : new ProjectedAnnotationOverlay(
                new PixelPoint(projection.PrincipalPointX, projection.PrincipalPointY), radius,
                north.Value, east.Value, south.Value, west.Value);
    }
}

public sealed class AnnotationProcessingStepOptions : IValidatableObject
{
    public bool Enabled { get; init; } = true;

    [Range(0, 32)]
    public int MarkRadius { get; init; } = 6;

    [Range(0, 255)]
    public byte MarkerValue { get; init; } = 144;

    public bool DrawLabels { get; init; } = true;

    public bool DrawConstellationLines { get; init; } = true;

    public IReadOnlyList<string> ConstellationIds { get; init; } = Array.Empty<string>();

    [Range(0, 255)]
    public byte ConstellationLineValue { get; init; } = 160;

    [Range(0, 255)]
    public byte ConstellationLineRed { get; init; } = 96;

    [Range(0, 255)]
    public byte ConstellationLineGreen { get; init; } = 160;

    [Range(0, 255)]
    public byte ConstellationLineBlue { get; init; } = byte.MaxValue;

    [Range(1, 8)]
    public int ConstellationLineThickness { get; init; } = 1;

    [Range(0, 1)]
    public double ConstellationLineOpacity { get; init; } = 0.8;

    [Range(-30, 30)]
    public double MaximumLabelMagnitude { get; init; } = 2.5;

    [Range(1, 8)]
    public int LabelScale { get; init; } = 1;

    public bool DrawImageCircle { get; init; }

    public bool DrawCardinalDirections { get; init; }

    [Range(0, 255)]
    public byte ImageCircleValue { get; init; } = 96;

    [Range(0, 255)]
    public byte CardinalValue { get; init; } = byte.MaxValue;

    [Range(1, 8)]
    public int CardinalScale { get; init; } = 2;

    [Required(AllowEmptyStrings = false)]
    public string RecipeVersion { get; init; } = "projected-scene-annotation-v2";

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "default";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ConstellationIds is null || ConstellationIds.Any(string.IsNullOrWhiteSpace))
        {
            yield return new ValidationResult(
                "ConstellationIds cannot be null or contain blank identifiers.",
                [nameof(ConstellationIds)]);
        }
    }
}
