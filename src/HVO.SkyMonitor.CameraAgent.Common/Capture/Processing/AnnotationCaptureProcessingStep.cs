using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates an annotated preview through the shared projector contract.</summary>
internal sealed class AnnotationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    AnnotationProcessingStepOptions options,
    IProjectedSceneStore sceneStore) : ConfigurableCaptureProcessingStep<AnnotationProcessingStepOptions>(metadata, options)
{
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.Artifacts is not { } artifacts ||
            !artifacts.Artifacts.TryGetValue(FrameArtifactRole.Preview, out var preview) ||
            preview.Frame.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24))
        {
            return ValueTask.CompletedTask;
        }

        var frame = preview.Frame;
        var provenance = artifacts.Raw.Frame.Metadata.Scene;
        if (provenance is null)
        {
            throw new InvalidOperationException("The projected scene required for annotation is unavailable.");
        }

        var transform = new PreviewTransform(
            (double)frame.Width / artifacts.Raw.Frame.Width,
            (double)frame.Height / artifacts.Raw.Frame.Height);
        var annotationOptions = new AnnotationOptions
        {
            MarkRadius = Options.MarkRadius,
            MarkerValue = Options.MarkerValue,
            DrawLabels = Options.DrawLabels,
            LabelScale = Options.LabelScale,
            DrawImageCircle = Options.DrawImageCircle,
            DrawCardinalDirections = Options.DrawCardinalDirections,
            ImageCircleValue = Options.ImageCircleValue,
            CardinalValue = Options.CardinalValue,
            CardinalScale = Options.CardinalScale,
            ConstellationLineValue = Options.ConstellationLineValue,
            ConstellationLineRed = Options.ConstellationLineRed,
            ConstellationLineGreen = Options.ConstellationLineGreen,
            ConstellationLineBlue = Options.ConstellationLineBlue,
            ConstellationLineThickness = Options.ConstellationLineThickness,
            ConstellationLineOpacity = Options.ConstellationLineOpacity
        };
        AnnotationResult annotation;
        if (sceneStore.TryGet(provenance.SceneId, out var scene) && scene is not null)
        {
            annotation = Annotate(
                frame.PixelData, frame.Width, frame.Height,
                scene.Objects.Select(item =>
                {
                    var annotate = IsNamed(item.Id, item.DisplayName) &&
                        (item.Kind == CelestialObjectKind.SolarSystemBody || item.Magnitude <= Options.MaximumLabelMagnitude);
                    return new ProjectedAnnotationObject(item.Id, item.DisplayName, item.Pixel, annotate, annotate);
                }),
                Options.DrawConstellationLines
                    ? scene.Segments.Select(static item => new ProjectedAnnotationSegment(
                        item.ConstellationId, item.FromPixel, item.ToPixel))
                    : [],
                transform, annotationOptions, CreateProjectionOverlay(scene.Request.Projection));
        }
        else if (provenance.Objects is { } objects)
        {
            annotation = Annotate(
                frame.PixelData, frame.Width, frame.Height,
                objects.Select(item =>
                {
                    var annotate = IsNamed(item.Id, item.DisplayName) &&
                        (item.Id.StartsWith("solar-system:", StringComparison.Ordinal) ||
                         item.Magnitude <= Options.MaximumLabelMagnitude);
                    return new ProjectedAnnotationObject(
                        item.Id, item.DisplayName, new PixelPoint(item.PixelX, item.PixelY), annotate, annotate);
                }),
                Options.DrawConstellationLines
                    ? provenance.Segments?.Select(static item => new ProjectedAnnotationSegment(
                        item.ConstellationId, new PixelPoint(item.FromPixelX, item.FromPixelY),
                        new PixelPoint(item.ToPixelX, item.ToPixelY))) ?? []
                    : [],
                transform, annotationOptions, null);
        }
        else
        {
            throw new InvalidOperationException("The projected scene required for annotation is unavailable.");
        }
        context.AddDerivative(FrameArtifactRole.AnnotatedPreview,
            new CameraFrame(frame.TimestampUtc, frame.Width, frame.Height, frame.PixelFormat, annotation.Pixels,
                CreateAnnotationMetadata(frame.Metadata)), Options.RecipeVersion, [preview.ArtifactId]);
        return ValueTask.CompletedTask;
    }

    private static bool IsNamed(string id, string displayName)
        => !string.IsNullOrWhiteSpace(displayName) && !string.Equals(id, displayName, StringComparison.Ordinal);

    private FrameMetadata CreateAnnotationMetadata(FrameMetadata metadata)
    {
        var extra = metadata.Extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata.Extra, StringComparer.Ordinal);
        extra["annotationRecipeVersion"] = Options.RecipeVersion;
        extra["constellationLineValue"] = Options.ConstellationLineValue.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        extra["constellationLineRgb"] = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Options.ConstellationLineRed},{Options.ConstellationLineGreen},{Options.ConstellationLineBlue}");
        extra["constellationLineThickness"] = Options.ConstellationLineThickness.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        extra["constellationLineOpacity"] = Options.ConstellationLineOpacity.ToString(
            "R", System.Globalization.CultureInfo.InvariantCulture);
        return metadata with { SourceId = "AnnotatedPreview", Extra = extra };
    }

    private static AnnotationResult Annotate(
        ReadOnlyMemory<byte> preview,
        int width,
        int height,
        IEnumerable<ProjectedAnnotationObject> objects,
        IEnumerable<ProjectedAnnotationSegment> segments,
        PreviewTransform transform,
        AnnotationOptions options,
        ProjectedAnnotationOverlay? projectionOverlay)
        => preview.Length == checked(width * height * 3)
            ? AnnotationRenderer.AnnotateRgb24WithSegments(
                preview, width, height, objects, segments, transform, options, projectionOverlay)
            : AnnotationRenderer.AnnotateMono8WithSegments(
                preview, width, height, objects, segments, transform, options, projectionOverlay);

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

public sealed class AnnotationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Range(0, 32)]
    public int MarkRadius { get; init; } = 6;

    [Range(0, 255)]
    public byte MarkerValue { get; init; } = 144;

    public bool DrawLabels { get; init; } = true;

    public bool DrawConstellationLines { get; init; } = true;

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
}
