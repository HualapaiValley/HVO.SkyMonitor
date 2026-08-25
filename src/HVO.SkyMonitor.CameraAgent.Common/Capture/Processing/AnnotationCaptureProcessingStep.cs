using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates an annotated preview through the shared projector contract.</summary>
internal sealed class AnnotationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    AnnotationProcessingStepOptions options,
    IProjectedSceneStore sceneStore,
    IAnnotationSceneProvider annotationSceneProvider,
    CameraAgentRecipeExecutionAdapter adapter,
    IServiceProvider? serviceProvider = null)
    : ConfigurableCaptureProcessingStep<AnnotationProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, ICompoundCaptureProcessingGraphStep
{
    public bool Enabled => Options.Enabled;

    public string RecipeName => BuiltInProcessingRecipes.Annotation;

    public FrameArtifactRole OutputRole => FrameArtifactRole.AnnotatedPreview;

    public string OutputVariant => Options.OutputVariant;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview, FrameArtifactRole.Metadata };

    public IReadOnlyList<IReadOnlySet<FrameArtifactRole>> RequiredDependencyRoleGroups =>
        Options.RequireProjectedSceneDependency
            ?
            [
                new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview },
                new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }
            ]
            : [new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview }];

    public IReadOnlyDictionary<FrameArtifactRole, IReadOnlySet<string>> RequiredDependencyRecipes =>
        Options.RequireProjectedSceneDependency
            ? new Dictionary<FrameArtifactRole, IReadOnlySet<string>>
            {
                [FrameArtifactRole.Metadata] = new HashSet<string> { BuiltInProcessingRecipes.ProjectedScene }
            }
            : new Dictionary<FrameArtifactRole, IReadOnlySet<string>>();

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var preview = context.GetDependencyArtifacts()
            .LastOrDefault(static artifact => artifact.Role == FrameArtifactRole.Preview)
            ?? (!context.HasDeclaredDependencies
                ? context.Artifacts?.Artifacts.GetValueOrDefault(FrameArtifactRole.Preview)
                : null);
        if (!Options.Enabled || context.Artifacts is not { } artifacts || preview is null ||
            preview.Frame.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24))
        {
            return;
        }

        var frame = preview.Frame;
        var provenance = artifacts.Raw.Frame.Metadata.Scene;
        AnnotationSceneResult? generatedScene = null;
        ProjectionContext? projectionOnly = null;
        var projectedSceneProduct = context.GetDependencyProducts().SingleOrDefault(static product =>
            product.Kind == ProcessingProductKind.Metadata &&
            string.Equals(product.SchemaVersion, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal));
        ProjectedSceneV1? projectedScene = null;
        if (projectedSceneProduct is not null)
        {
            var parsed = ProjectedSceneJson.Parse(projectedSceneProduct.Payload);
            projectedScene = parsed.Scene ?? throw new InvalidDataException(
                $"The declared projected-scene dependency is invalid at '{parsed.ErrorPath}'.");
            ValidateProjectedScene(context, artifacts.Raw.Frame, projectedSceneProduct, projectedScene);
        }
        else if (Options.RequireProjectedSceneDependency)
        {
            throw new InvalidOperationException("The declared projected-scene dependency is unavailable.");
        }
        if (projectedScene is null && provenance is null && Options.DrawConstellationLines && Options.ConstellationIds.Count > 0)
        {
            generatedScene = await annotationSceneProvider.BuildAsync(
                context.Config, context.ReconstructionDescriptor, artifacts.Raw.Frame,
                Options.ConstellationIds, cancellationToken).ConfigureAwait(false);
            provenance = generatedScene.Provenance;
        }
        if (provenance is null && (Options.DrawImageCircle || Options.DrawCardinalDirections))
        {
            projectionOnly = RigProjectionContextFactory.Create(context.Config.Rig);
        }
        if (projectedScene is null && provenance is null && projectionOnly is null && !Options.DrawMetadataCorners)
        {
            return;
        }

        var transform = new PreviewTransform(
            (double)frame.Width / artifacts.Raw.Frame.Width,
            (double)frame.Height / artifacts.Raw.Frame.Height);
        IReadOnlyList<ProjectedAnnotationObject> objects;
        IReadOnlyList<ProjectedAnnotationSegment> segments;
        ProjectedAnnotationOverlay? projectionOverlay;
        if (projectedScene is not null)
        {
            objects = projectedScene.Objects.Select(item =>
            {
                var annotate = IsNamed(item.Id, item.DisplayName) &&
                    (item.Kind == CelestialObjectKind.SolarSystemBody || item.Magnitude <= Options.MaximumLabelMagnitude);
                return new ProjectedAnnotationObject(item.Id, item.DisplayName, item.Pixel, annotate, annotate);
            }).ToArray();
            segments = Options.DrawConstellationLines
                ? projectedScene.Segments.Where(item => IsSelectedConstellation(item.ConstellationId))
                    .Select(static item => new ProjectedAnnotationSegment(
                        item.ConstellationId, item.FromPixel, item.ToPixel)).ToArray()
                : [];
            projectionOverlay = CreateProjectionOverlay(projectedScene.Projection);
        }
        else if (projectionOnly is { } projection)
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
            var rigHash = RigProjectionContextFactory.CreateProfileHashSha256(context.Config.Rig);
            if (!string.IsNullOrWhiteSpace(provenance.RigProfileHashSha256) &&
                !string.Equals(provenance.RigProfileHashSha256, rigHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Persisted annotation geometry does not match the capture-time rig profile.");
            }
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
            projectionOverlay = CreateProjectionOverlay(RigProjectionContextFactory.Create(context.Config.Rig));
        }
        else if (provenance is null && Options.DrawMetadataCorners)
        {
            objects = [];
            segments = [];
            projectionOverlay = null;
        }
        else
        {
            throw new InvalidOperationException("The projected scene required for annotation is unavailable.");
        }

        var previewProduct = context.GetProcessingProduct(preview.ArtifactId);
        MetadataCornerOverlay? metadataOverlay = null;
        if (Options.DrawMetadataCorners)
        {
            metadataOverlay = await CreateMetadataOverlayAsync(
                context, provenance, projectedScene, previewProduct, cancellationToken).ConfigureAwait(false);
            if (metadataOverlay is null)
            {
                context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(
                    ProcessingReasonCodes.EnvironmentAssociationPending));
                return;
            }
        }
        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config,
            preview,
            previewProduct?.Variant ?? preview.RecipeVersion ?? "legacy-preview",
            context.AcquisitionTiming,
            context.ReconstructionDescriptor,
            previewProduct);
        if (previewProduct is not null)
        {
            input = input with { RecipeIdentitySha256 = previewProduct.Recipe.IdentitySha256 };
        }
        var executionInputs = new List<ProcessingArtifact> { input };
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliaryInputs = null;
        if (projectedSceneProduct is not null)
        {
            var projectedSceneArtifactId = CaptureProcessingContext.CreateArtifactId(
                projectedSceneProduct.OutputIdentitySha256);
            executionInputs.Add(new ProcessingArtifact(
                projectedSceneArtifactId,
                projectedSceneProduct.Role,
                projectedSceneProduct.Variant,
                projectedSceneProduct.Recipe.IdentitySha256,
                projectedSceneProduct.MediaType,
                projectedSceneProduct.Layout,
                projectedSceneProduct.Payload,
                frame.TimestampUtc,
                projectedSceneProduct.TotalIntegration,
                projectedSceneProduct.Compatibility,
                SourceArtifactIds: projectedSceneProduct.SourceArtifactIds));
            auxiliaryInputs =
            [
                new ProcessingAuxiliaryInput(
                    "projected-scene",
                    ProcessingAuxiliaryInputKind.Artifact,
                    ProcessingInputSelector.RecipeResult(
                        projectedSceneProduct.Role,
                        projectedSceneProduct.Variant,
                        projectedSceneProduct.Recipe.IdentitySha256),
                    ArtifactId: projectedSceneArtifactId)
            ];
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
                projectionOverlay,
                metadataOverlay
            })));
        annotationInput = annotationInput with { MetadataOverlay = metadataOverlay };
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
        var outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.Annotation,
            recipeOptions,
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                input.Variant,
                input.RecipeIdentitySha256),
            executionInputs,
            Options.OutputVariant,
            annotationInput,
            AuxiliaryInputs: auxiliaryInputs,
            InputArtifactId: input.ArtifactId), cancellationToken).ConfigureAwait(false);
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
            product.SourceArtifactIds,
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        context.AssociateProcessingProduct(artifact, product);
    }

    private static bool IsNamed(string id, string displayName)
        => !string.IsNullOrWhiteSpace(displayName) && !string.Equals(id, displayName, StringComparison.Ordinal);

    private bool IsSelectedConstellation(string id)
        => Options.ConstellationIds.Count == 0 ||
           Options.ConstellationIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    private static void ValidateProjectedScene(
        CaptureProcessingContext context,
        CameraFrame raw,
        ProcessingProduct product,
        ProjectedSceneV1 scene)
    {
        var descriptor = context.ReconstructionDescriptor;
        var expectedRawId = descriptor?.Artifact.ArtifactId ?? context.Artifacts?.Raw.ArtifactId;
        if (expectedRawId is null || scene.Source.ArtifactId != expectedRawId ||
            product.SourceArtifactIds.Count != 1 || product.SourceArtifactIds[0] != expectedRawId ||
            descriptor is not null && (scene.Source.CaptureId != descriptor.Capture.CaptureId ||
                !string.Equals(scene.Source.ArtifactIdentitySha256,
                    CaptureContractJson.ComputeDescriptorSha256(descriptor), StringComparison.OrdinalIgnoreCase)) ||
            scene.ImageTransform.OutputWidthPixels != raw.Width ||
            scene.ImageTransform.OutputHeightPixels != raw.Height)
        {
            throw new InvalidDataException("The projected-scene dependency does not match the captured raw source.");
        }
    }

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
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(projection);
        return landmarks is null
            ? null
            : new ProjectedAnnotationOverlay(
                landmarks.Center,
                landmarks.ImageCircleRadius,
                landmarks.North,
                landmarks.East,
                landmarks.South,
                landmarks.West);
    }

    private static ProjectedAnnotationOverlay? CreateProjectionOverlay(ProjectedSceneProjection projection)
        => CreateProjectionOverlay(new ProjectionContext(
            projection.Model,
            projection.PrincipalPointX,
            projection.PrincipalPointY,
            projection.FocalLengthXPixels,
            projection.FocalLengthYPixels,
            projection.WidthPixels,
            projection.HeightPixels,
            projection.Aperture,
            projection.ImageCircleRadiusPixels,
            projection.BoresightAltitudeDegrees,
            projection.BoresightAzimuthDegrees,
            projection.RollDegrees,
            projection.HorizontalFlip,
            projection.EnforceSensorBounds));

    private async ValueTask<MetadataCornerOverlay?> CreateMetadataOverlayAsync(
        CaptureProcessingContext context,
        SceneProvenance? provenance,
        ProjectedSceneV1? projectedScene,
        ProcessingProduct? previewProduct,
        CancellationToken cancellationToken)
    {
        var descriptor = context.ReconstructionDescriptor;
        var schedule = descriptor?.CycleEvidence?.ScheduleAdmission;
        var scheduledProfile = schedule is null
            ? null
            : context.Config.Schedule?.SetpointProfiles.SingleOrDefault(profile => string.Equals(
                profile.Id,
                schedule.SetpointProfileId,
                StringComparison.Ordinal));
        var stackProduct = ResolveStackProduct(context, previewProduct);
        var environmentLines = UsesEnvironmentToken()
            ? await CreateEnvironmentLinesAsync(descriptor, cancellationToken).ConfigureAwait(false)
            : [];
        if (environmentLines is null)
        {
            return null;
        }
        return new MetadataCornerOverlay(
            FormatTokens(Options.TopLeftTokens),
            FormatTokens(Options.TopRightTokens),
            FormatTokens(Options.BottomLeftTokens),
            FormatTokens(Options.BottomRightTokens),
            Options.MetadataValue,
            Options.MetadataScale,
            Options.MetadataInset,
            Options.MetadataLineSpacing);

        IReadOnlyList<string> FormatTokens(IReadOnlyList<string> tokens)
            => tokens.SelectMany(token => token == AnnotationMetadataTokens.Environment
                    ? environmentLines
                    : [FormatToken(token)])
                .Select(line => TruncateLine(line, MaximumCornerCharacters(context.Frame?.Width)))
                .ToArray();

        string FormatToken(string token)
            => token switch
            {
                AnnotationMetadataTokens.AgentIdentity => $"AGENT {Visible(descriptor?.Capture.AgentId ?? context.Config.AgentId)}",
                AnnotationMetadataTokens.CaptureSequence => descriptor is null
                    ? "CAPTURE UNAVAILABLE"
                    : $"CAPTURE {descriptor.Capture.CaptureSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                AnnotationMetadataTokens.Utc => $"UTC {(descriptor?.Timing.ExposureStartedUtc ?? context.Frame?.TimestampUtc)?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) ?? "UNAVAILABLE"}",
                AnnotationMetadataTokens.ScheduleProfile => $"SCHEDULE {Visible(schedule?.SetpointProfileId)}",
                AnnotationMetadataTokens.Exposure => $"EXPOSURE {FormatNumber((descriptor?.Controls.EffectiveExposure ?? context.Frame?.Metadata.Exposure)?.TotalSeconds, 3)} S",
                AnnotationMetadataTokens.Cadence => $"CADENCE {FormatNumber((scheduledProfile?.CaptureInterval ?? context.Config.Rig.Pipeline.CaptureInterval).TotalSeconds, 3)} S",
                AnnotationMetadataTokens.Gain => $"GAIN {FormatNumber(descriptor?.Controls.EffectiveGain ?? context.Frame?.Metadata.Gain, 3)}",
                AnnotationMetadataTokens.Offset => $"OFFSET {FormatNumber(descriptor?.Controls.EffectiveOffset ?? context.Frame?.Metadata.Offset, 1)}",
                AnnotationMetadataTokens.SensorSetpoint => $"SETPOINT {FormatNumber(descriptor?.Controls.TemperatureSetpointC, 1)} C",
                AnnotationMetadataTokens.Environment => throw new InvalidOperationException(
                    "Environment metadata must be expanded from frozen associations."),
                AnnotationMetadataTokens.Catalog => projectedScene is not null
                    ? $"CATALOG {Visible(projectedScene.Catalog.Name)} {Visible(projectedScene.Catalog.Version)} {HashPrefix(projectedScene.Catalog.ChecksumSha256)}"
                    : provenance is null
                        ? "CATALOG UNAVAILABLE"
                        : $"CATALOG {Visible(provenance.CatalogName)} {Visible(provenance.CatalogVersion)} {HashPrefix(provenance.CatalogChecksumSha256)}",
                AnnotationMetadataTokens.Calibration => FormatProfile("CALIBRATION", descriptor?.Profiles.Calibration),
                AnnotationMetadataTokens.Stack => stackProduct is null
                    ? "STACK UNAVAILABLE"
                    : $"STACK {stackProduct.SourceArtifactIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} {stackProduct.TotalIntegration.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} S",
                AnnotationMetadataTokens.ProcessingProfile => FormatProfile("PROFILE", descriptor?.Profiles.Processing),
                _ => throw new InvalidOperationException($"Unsupported annotation metadata token '{token}'.")
            };
    }

    private async ValueTask<IReadOnlyList<string>?> CreateEnvironmentLinesAsync(
        ReconstructionDescriptor? descriptor,
        CancellationToken cancellationToken)
    {
        var environmentalAssociations = serviceProvider?.GetService<EnvironmentalAssociationService>();
        var environmentalObservations = serviceProvider?.GetService<ILocalEnvironmentalObservationStore>();
        var hostOptions = serviceProvider?.GetService<IOptions<CameraAgentHostOptions>>();
        if (descriptor is null || environmentalAssociations is null || environmentalObservations is null ||
            hostOptions is null || !hostOptions.Value.EnvironmentalAcquisition.Enabled ||
            Options.EnvironmentalKinds.Count == 0)
        {
            return ["ENVIRONMENT MISSING"];
        }

        var fromUtc = descriptor.Timing.ExposureStartedUtc.ToUniversalTime();
        var throughUtc = descriptor.Timing.ExposureEndedUtc.ToUniversalTime();
        if (throughUtc <= fromUtc)
        {
            throughUtc = fromUtc.AddMilliseconds(1);
        }
        var associations = await environmentalAssociations.ReadCompletedAsync(
            descriptor.Capture.CaptureId,
            descriptor.Capture.CaptureSequence,
            fromUtc,
            throughUtc,
            descriptor.Capture.RigId,
            Options.EnvironmentalKinds,
            cancellationToken).ConfigureAwait(false);
        if (associations is null)
        {
            return null;
        }
        var lines = new List<string>(associations.Count);
        foreach (var association in associations)
        {
            LocalEnvironmentalObservationRecord? selected = null;
            if (association.SelectedRecordId is { } recordId)
            {
                selected = await environmentalObservations.ReadLocalDetailAsync(
                    hostOptions.Value.RawIngressRoot,
                    recordId,
                    cancellationToken).ConfigureAwait(false);
                if (selected is null)
                {
                    throw new InvalidDataException("Selected environmental annotation evidence is unavailable.");
                }
            }
            lines.Add(FormatEnvironmentLine(association, selected, throughUtc));
        }
        return lines.Count == 0 ? ["ENVIRONMENT MISSING"] : lines;
    }

    private bool UsesEnvironmentToken()
        => Options.TopLeftTokens.Contains(AnnotationMetadataTokens.Environment, StringComparer.Ordinal) ||
            Options.TopRightTokens.Contains(AnnotationMetadataTokens.Environment, StringComparer.Ordinal) ||
            Options.BottomLeftTokens.Contains(AnnotationMetadataTokens.Environment, StringComparer.Ordinal) ||
            Options.BottomRightTokens.Contains(AnnotationMetadataTokens.Environment, StringComparer.Ordinal);

    private static string FormatEnvironmentLine(
        LocalEnvironmentalCaptureAssociation association,
        LocalEnvironmentalObservationRecord? selected,
        DateTimeOffset evaluatedUtc)
    {
        var kind = association.Kind.ToString().ToUpperInvariant();
        var status = association.Status.ToString().ToUpperInvariant();
        if (selected is null)
        {
            return $"{kind} {status}";
        }
        var value = selected.Fact.Value;
        var formattedValue = value.BooleanValue is { } boolean
            ? boolean ? "TRUE" : "FALSE"
            : FormatEnvironmentalNumber(value.NumericValue, value.Unit);
        var age = Math.Max(0, Math.Round((evaluatedUtc - selected.Fact.ObservedAtUtc).TotalSeconds));
        var quality = value.Quality == EnvironmentalObservationQuality.Good
            ? string.Empty
            : $" {value.Quality.ToString().ToUpperInvariant()}";
        return $"{kind} {formattedValue} {status}{quality} AGE {age.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} S";
    }

    private static string FormatEnvironmentalNumber(double? value, EnvironmentalObservationUnit unit)
    {
        if (value is not { } number || !double.IsFinite(number))
        {
            return "UNAVAILABLE";
        }
        return unit switch
        {
            EnvironmentalObservationUnit.DegreesCelsius => $"{number.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} C",
            EnvironmentalObservationUnit.Percent => $"{number.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} %",
            EnvironmentalObservationUnit.Pascals => $"{(number / 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} HPA",
            EnvironmentalObservationUnit.MetersPerSecond => $"{number.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} M/S",
            EnvironmentalObservationUnit.DegreesTrue => $"{number.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} DEG",
            EnvironmentalObservationUnit.MillimetersPerHour => $"{number.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} MM/H",
            EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond => $"{number.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} MAG",
            EnvironmentalObservationUnit.Fraction => number.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            _ => "UNAVAILABLE"
        };
    }

    private static ProcessingProduct? ResolveStackProduct(
        CaptureProcessingContext context,
        ProcessingProduct? previewProduct)
    {
        var current = previewProduct;
        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            if (string.Equals(
                current.Recipe.Descriptor.Name,
                BuiltInProcessingRecipes.RollingMean,
                StringComparison.Ordinal))
            {
                return current;
            }
            current = current.SourceArtifactIds.Count == 1
                ? context.GetProcessingProduct(current.SourceArtifactIds[0])
                : null;
        }
        return null;
    }

    private static string FormatProfile(string label, ProfileIdentityDescriptor? profile)
        => profile is null
            ? $"{label} UNAVAILABLE"
            : $"{label} {Visible(profile.Name)} {Visible(profile.Version)} {HashPrefix(profile.Sha256)}";

    private static string FormatNumber(double? value, int decimalPlaces)
        => value is { } number && double.IsFinite(number)
            ? number.ToString($"F{decimalPlaces}", System.Globalization.CultureInfo.InvariantCulture)
            : "UNAVAILABLE";

    private static string Visible(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "UNAVAILABLE"
            : new string(value.Trim().Select(static character =>
                char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '.' or ':' or '/' or '%'
                    ? character
                    : '-').ToArray());

    private static string HashPrefix(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length < 12
            ? "UNAVAILABLE"
            : value[..12].ToUpperInvariant();

    private int MaximumCornerCharacters(int? width)
    {
        var halfWidth = Math.Max(1, (width ?? 2048) / 2 - Options.MetadataInset);
        return Math.Clamp((halfWidth + Options.MetadataScale) / (6 * Options.MetadataScale), 1, 64);
    }

    private static string TruncateLine(string value, int maximumCharacters)
    {
        maximumCharacters = Math.Clamp(maximumCharacters, 1, 64);
        if (value.Length <= maximumCharacters)
        {
            return value;
        }
        return maximumCharacters < 4
            ? value[..maximumCharacters]
            : string.Concat(value.AsSpan(0, maximumCharacters - 3), "...");
    }
}

public static class AnnotationMetadataTokens
{
    public const string AgentIdentity = "agent-identity";
    public const string CaptureSequence = "capture-sequence";
    public const string Utc = "utc";
    public const string ScheduleProfile = "schedule-profile";
    public const string Exposure = "exposure";
    public const string Cadence = "cadence";
    public const string Gain = "gain";
    public const string Offset = "offset";
    public const string SensorSetpoint = "sensor-setpoint";
    public const string Environment = "environment";
    public const string Catalog = "catalog";
    public const string Calibration = "calibration";
    public const string Stack = "stack";
    public const string ProcessingProfile = "processing-profile";

    internal static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AgentIdentity,
        CaptureSequence,
        Utc,
        ScheduleProfile,
        Exposure,
        Cadence,
        Gain,
        Offset,
        SensorSetpoint,
        Environment,
        Catalog,
        Calibration,
        Stack,
        ProcessingProfile
    };
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

    public bool DrawMetadataCorners { get; init; }

    public bool RequireProjectedSceneDependency { get; init; }

    public IReadOnlyList<string> TopLeftTokens { get; init; } =
    [
        AnnotationMetadataTokens.AgentIdentity,
        AnnotationMetadataTokens.CaptureSequence,
        AnnotationMetadataTokens.Utc
    ];

    public IReadOnlyList<string> TopRightTokens { get; init; } =
    [
        AnnotationMetadataTokens.ScheduleProfile,
        AnnotationMetadataTokens.Exposure,
        AnnotationMetadataTokens.Cadence,
        AnnotationMetadataTokens.Gain,
        AnnotationMetadataTokens.Offset,
        AnnotationMetadataTokens.SensorSetpoint
    ];

    public IReadOnlyList<string> BottomLeftTokens { get; init; } = [AnnotationMetadataTokens.Environment];

    public IReadOnlyList<string> BottomRightTokens { get; init; } =
    [
        AnnotationMetadataTokens.Catalog,
        AnnotationMetadataTokens.Calibration,
        AnnotationMetadataTokens.Stack,
        AnnotationMetadataTokens.ProcessingProfile
    ];

    public IReadOnlyList<EnvironmentalObservationKind> EnvironmentalKinds { get; init; } =
    [
        EnvironmentalObservationKind.AirTemperature,
        EnvironmentalObservationKind.RelativeHumidity,
        EnvironmentalObservationKind.AtmosphericPressure,
        EnvironmentalObservationKind.WindSpeed,
        EnvironmentalObservationKind.RainState,
        EnvironmentalObservationKind.CloudCover
    ];

    [Range(0, 255)]
    public byte MetadataValue { get; init; } = byte.MaxValue;

    [Range(1, 4)]
    public int MetadataScale { get; init; } = 1;

    [Range(0, 64)]
    public int MetadataInset { get; init; } = 4;

    [Range(0, 16)]
    public int MetadataLineSpacing { get; init; } = 2;

    [Range(0, 255)]
    public byte ImageCircleValue { get; init; } = 96;

    [Range(0, 255)]
    public byte CardinalValue { get; init; } = byte.MaxValue;

    [Range(1, 8)]
    public int CardinalScale { get; init; } = 2;

    [Required(AllowEmptyStrings = false)]
    public string RecipeVersion { get; init; } = "projected-scene-annotation-v3";

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
        foreach (var (tokens, name) in new[]
        {
            (TopLeftTokens, nameof(TopLeftTokens)),
            (TopRightTokens, nameof(TopRightTokens)),
            (BottomLeftTokens, nameof(BottomLeftTokens)),
            (BottomRightTokens, nameof(BottomRightTokens))
        })
        {
            if (tokens is null || tokens.Count > 8 || tokens.Any(token => !AnnotationMetadataTokens.Allowed.Contains(token)) ||
                tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Count)
            {
                yield return new ValidationResult(
                    "Metadata corner tokens must be unique allowlisted values with no more than eight lines.",
                    [name]);
            }
        }
        if (EnvironmentalKinds is null || EnvironmentalKinds.Count > 8 ||
            EnvironmentalKinds.Any(static kind => !Enum.IsDefined(kind)) ||
            EnvironmentalKinds.Distinct().Count() != EnvironmentalKinds.Count)
        {
            yield return new ValidationResult(
                "EnvironmentalKinds must contain no more than eight unique supported values.",
                [nameof(EnvironmentalKinds)]);
        }
        foreach (var (tokens, name) in new[]
        {
            (TopLeftTokens, nameof(TopLeftTokens)),
            (TopRightTokens, nameof(TopRightTokens)),
            (BottomLeftTokens, nameof(BottomLeftTokens)),
            (BottomRightTokens, nameof(BottomRightTokens))
        })
        {
            var expandedCount = tokens?.Count(token => token != AnnotationMetadataTokens.Environment) ?? 0;
            if (tokens?.Contains(AnnotationMetadataTokens.Environment, StringComparer.Ordinal) == true)
            {
                expandedCount += Math.Max(1, EnvironmentalKinds?.Count ?? 0);
            }
            if (expandedCount > 8)
            {
                yield return new ValidationResult(
                    "Expanded metadata corner content cannot exceed eight lines.",
                    [name]);
            }
        }
    }
}
