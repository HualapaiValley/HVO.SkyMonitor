using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Operator/caller read of the retained step 30b regional stellar visibility assessment for one execution.</summary>
public sealed record RegionalStellarVisibilityRunRead(
    RegionalStellarVisibilityV1? Assessment,
    string? UnavailableReason);

/// <summary>Reads and verifies the retained regional stellar visibility product bound to one execution.</summary>
public interface IRegionalStellarVisibilityRunReader
{
    ValueTask<RegionalStellarVisibilityRunRead?> ReadAsync(
        ProcessingGraphExecutionDetail execution,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional step 30b: assesses regional cloud evidence from independent catalog star predictions (step 20a)
/// and saved measured detections (step 4), optionally improved by an accepted solve (step 20b/d) and focus metrics.
/// Runs alongside clear-reference CloudAssessment (#105/#1004) with completely separate provenance.
/// </summary>
internal sealed class RegionalStellarVisibilityCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    RegionalStellarVisibilityProcessingStepOptions options,
    CaptureExposureMeasurementReader measurementReader,
    IExposureProductReader productReader,
    CameraAgentCloudEnvironment? cloudEnvironment = null,
    SqliteCalibrationLibraryStore? calibrationLibrary = null,
    CameraAgentClearReferenceLoader? referenceLoader = null,
    IOptions<CameraAgentHostOptions>? hostOptions = null,
    IServiceProvider? services = null)
    : ConfigurableCaptureProcessingStep<RegionalStellarVisibilityProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, ICenteredRawPrimaryCaptureProcessingStep, IFrozenAuxiliaryInputCaptureProcessingStep
{
    private static readonly JsonSerializerOptions EnvironmentJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public bool Enabled => true;
    public string RecipeName => RegionalStellarVisibilityProducts.RecipeName;
    public string? SharedStepVersion => RegionalStellarVisibilityProducts.ImplementationVersion;
    public ProcessingOperationKind? SharedOperationKind => ProcessingOperationKind.Analyzer;
    public ProcessingRecipeDefinition? SharedOutputRecipe => RegionalStellarVisibilityProducts.Definition;
    public IReadOnlyList<ProcessingAlgorithmIdentity> SharedOutputAlgorithms => RegionalStellarVisibilityProducts.Algorithms;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
    public string? OutputSchemaVersion => RegionalStellarVisibilityProducts.SchemaVersion;
    public string? OutputMediaType => RegionalStellarVisibilityProducts.MediaType;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Metadata };

    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string>(StringComparer.Ordinal) { BuiltInProcessingRecipes.ExposureMeasurement },
            new HashSet<string>(StringComparer.Ordinal) { ExposureMeasurementProducts.SchemaVersion },
            ExposureMeasurementProducts.Variant,
            Required: false),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string>(StringComparer.Ordinal) { BuiltInProcessingRecipes.ProjectedScene },
            new HashSet<string>(ProjectedSceneV1.SupportedSchemaVersions, StringComparer.Ordinal),
            ExposureCatalogPredictionProducts.Variant,
            Required: false),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string>(StringComparer.Ordinal) { BuiltInProcessingRecipes.MeasuredAstrometry },
            new HashSet<string>(StringComparer.Ordinal) { MeasuredAstrometrySolutionV1.CurrentSchemaVersion },
            Required: false)
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var descriptor = context.ReconstructionDescriptor
            ?? throw new InvalidDataException("Regional stellar visibility requires admitted raw.");
        var rawArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config,
            context.Artifacts!.Raw,
            descriptor.Artifact.Variant,
            context.AcquisitionTiming,
            descriptor) with
        {
            Payload = ReadOnlyMemory<byte>.Empty
        };

        var dependencies = context.GetDependencyProducts();
        var measurementProduct = dependencies.SingleOrDefault(
            static product => product.SchemaVersion == ExposureMeasurementProducts.SchemaVersion);
        var predictionProduct = dependencies.SingleOrDefault(
            static product => product.Variant == ExposureCatalogPredictionProducts.Variant &&
                              ProjectedSceneV1.IsSupportedSchemaVersion(product.SchemaVersion));
        var astrometryProduct = dependencies.SingleOrDefault(
            static product => product.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
        var astrometryContextProduct = dependencies.SingleOrDefault(
            static product => product.SchemaVersion == MeasuredAstrometryContextV1.CurrentSchemaVersion);

        ProcessingArtifact? measurementArtifact = null;
        ExposureMeasurementV1? measurement = null;
        if (measurementProduct is not null)
        {
            var (retained, verifiedMeasurement) = await measurementReader.ReadDependencyAsync(
                context,
                ProcessingIdentity.CreateArtifactId(measurementProduct.OutputIdentitySha256),
                cancellationToken).ConfigureAwait(false);
            measurementArtifact = retained;
            measurement = verifiedMeasurement;
        }

        ProcessingArtifact? predictionArtifact = null;
        ProjectedSceneV1? prediction = null;
        if (predictionProduct is not null)
        {
            predictionArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, predictionProduct);
            if (context.GetDependencyProducerStepId(predictionArtifact.ArtifactId) != ExposureCatalogPredictionProducts.StepAlias)
            {
                context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure("visibility.independent-prediction-producer-mismatch"));
                return;
            }
            var parsedScene = ProjectedSceneJson.Parse(predictionArtifact.Payload);
            if (parsedScene.IsValid && parsedScene.Scene is not null)
            {
                prediction = parsedScene.Scene;
            }
        }

        ProcessingArtifact? astrometryArtifact = null;
        MeasuredAstrometrySolutionV1? astrometrySolution = null;
        AstrometricMapping? acceptedMapping = null;
        if (astrometryProduct is not null)
        {
            astrometryArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, astrometryProduct);
            try
            {
                var parsedSolution = MeasuredAstrometryJson.Parse(astrometryArtifact.Payload);
                astrometrySolution = parsedSolution;
                if (parsedSolution.Assessment.HasMeasuredMapping && astrometryContextProduct is not null)
                {
                    var astrometryContext = MeasuredAstrometryJson.ParseContext(astrometryContextProduct.Payload);
                    var calibration = new AstrometricCalibration(
                        astrometryContext.Projection,
                        astrometryContext.CalibrationVersion,
                        astrometryContext.ReadoutIdentitySha256);
                    if (calibration.IdentitySha256 == parsedSolution.Assessment.CalibrationIdentitySha256)
                    {
                        acceptedMapping = new AstrometricMapping(calibration, parsedSolution.Assessment);
                    }
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException)
            {
                // An unreadable or incompatible optional solve falls back cleanly to fixed geometry.
                astrometryArtifact = null;
                astrometrySolution = null;
                acceptedMapping = null;
            }
        }

        CloudAssessmentEnvironmentV1 environment;
        if (cloudEnvironment is null)
        {
            environment = ParseEnvironment(CameraAgentCloudEnvironment.CreateMissingInput(context));
        }
        else
        {
            var envInput = await cloudEnvironment.CreateInputAsync(context, cancellationToken).ConfigureAwait(false);
            if (envInput is null)
            {
                context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(
                    ProcessingReasonCodes.EnvironmentAssociationPending));
                return;
            }
            environment = ParseEnvironment(envInput);
        }

        HorizonObstructionEvaluator? horizonEvaluator = null;
        if (context.Config.Rig.HorizonObstruction is { } horizonProfile)
        {
            var observer = HorizonCaptureObserver.Resolve(context, services);
            horizonEvaluator = new HorizonObstructionEvaluator(
                horizonProfile,
                context.Config.Rig,
                observer,
                descriptor.Location ?? context.Config.DeploymentLocation?.ToProvenance());
            context.RecordCanonicalInput("horizon-visibility-eligibility", "1", horizonProfile.ComputeIdentitySha256());
        }

        var defectMask = await TryLoadDefectMaskAsync(context, cancellationToken).ConfigureAwait(false);
        var photometricProfile = ResolvePhotometricProfile(context, Options);
        var recipeOptions = Options.ToRecipeOptions(photometricProfile);

        var request = new RegionalStellarVisibilityRequest(
            rawArtifact,
            measurementArtifact,
            measurement,
            predictionArtifact,
            prediction,
            descriptor.Controls.EffectiveExposure.TotalSeconds,
            descriptor.Controls.EffectiveGain,
            recipeOptions,
            astrometryArtifact,
            astrometrySolution,
            acceptedMapping,
            horizonEvaluator,
            environment,
            defectMask);

        var outcome = RegionalStellarVisibilityProducts.Assess(request, OutputVariant, cancellationToken);
        var consumedProducts = new List<ProcessingProduct>(3);
        if (measurementProduct is not null) consumedProducts.Add(measurementProduct);
        if (predictionProduct is not null) consumedProducts.Add(predictionProduct);
        if (astrometryProduct is not null) consumedProducts.Add(astrometryProduct);
        context.RecordConsumedArtifacts([context.Artifacts.Raw], consumedProducts);
        context.RecordExecutionRoute(ProcessingNodeExecutionRoute.InProcess);
        context.RecordExecutionOutcome(outcome);
        context.AddProcessingOutcome(outcome);
    }

    private static RegionalVisibilityPhotometricProfileV1? ResolvePhotometricProfile(
        CaptureProcessingContext context,
        RegionalStellarVisibilityProcessingStepOptions options)
    {
        if (options.PhotometricProfile is { } explicitProfile)
        {
            return explicitProfile;
        }

        if (context.Config.Rig.Sensor.SimulationResponse is { } simulationResponse)
        {
            return new RegionalVisibilityPhotometricProfileV1(
                ProfileName: simulationResponse.CompatibilityLabel,
                MagnitudeZeroElectronsPerSecond: options.MagnitudeZeroElectronsPerSecond ?? 10_000d,
                NominalPsfSigmaPixels: options.NominalPsfSigmaPixels ?? 1.0d,
                DarkCurrentElectronsPerSecond: options.DarkCurrentElectronsPerSecond ?? 0.0d,
                VignettingStrength: options.VignettingStrength ?? 0.0d,
                SensorResponse: simulationResponse,
                CalibrationStatus: simulationResponse.CalibrationStatus);
        }

        return null;
    }

    private async ValueTask<RegionalVisibilityDefectMaskInput?> TryLoadDefectMaskAsync(
        CaptureProcessingContext context,
        CancellationToken cancellationToken)
    {
        if (calibrationLibrary is null || referenceLoader is null || hostOptions is null ||
            context.RawCapture?.Manifest is not { CenteredExposure: not null } manifest)
        {
            return null;
        }

        var key = CaptureExposureMeasurementReader.Key(manifest);
        var linear = await productReader.ReadLinearAsync(key, cancellationToken).ConfigureAwait(false);
        if (linear?.Provenance is not { Disposition: LinearExposureDisposition.Calibrated } provenance ||
            provenance.CalibrationArtifactIds.Length != CalibrationReferenceKinds.All.Count ||
            provenance.CalibrationPayloadHashes.Length != CalibrationReferenceKinds.All.Count)
        {
            return null;
        }

        var defectIndex = -1;
        for (var i = 0; i < CalibrationReferenceKinds.All.Count; i++)
        {
            if (string.Equals(CalibrationReferenceKinds.All[i], CalibrationReferenceKinds.Defect, StringComparison.Ordinal))
            {
                defectIndex = i;
                break;
            }
        }
        if (defectIndex < 0)
        {
            return null;
        }

        var defectArtifactId = provenance.CalibrationArtifactIds[defectIndex];
        var defectSha256 = provenance.CalibrationPayloadHashes[defectIndex];
        var bundles = await calibrationLibrary.GetBundlesAsync(128, cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(hostOptions.Value.RawIngressRoot);
        foreach (var snapshot in bundles)
        {
            var match = snapshot.Bundle.Artifacts.FirstOrDefault(artifact =>
                artifact.Kind == CalibrationReferenceKinds.Defect &&
                artifact.ArtifactId == defectArtifactId &&
                string.Equals(artifact.PayloadSha256, defectSha256, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                continue;
            }

            try
            {
                var manifestPath = Path.GetFullPath(Path.Combine(root, match.ManifestRelativePath));
                var parsed = CaptureContractJson.ParseManifest(
                    await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
                if (!parsed.IsValid || parsed.Document?.Manifest is not { } defectManifest)
                {
                    return null;
                }

                var artifact = await referenceLoader.LoadAsync(defectManifest, cancellationToken).ConfigureAwait(false);
                if (artifact.ArtifactId != defectArtifactId ||
                    artifact.Layout is not { } layout ||
                    !string.Equals(PayloadChecksum.ComputeSha256(artifact.Payload.Span), defectSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new RegionalVisibilityDefectMaskInput(
                    defectArtifactId,
                    defectSha256,
                    new Linear16Frame(layout.Width, layout.Height, layout.StrideBytes, layout.PixelFormat, artifact.Payload));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return null;
            }
        }

        return null;
    }

    private static CloudAssessmentEnvironmentV1 ParseEnvironment(ProcessingAuxiliaryInput input)
    {
        var parsed = JsonSerializer.Deserialize<CloudAssessmentEnvironmentV1>(input.Payload.Span, EnvironmentJson)
            ?? throw new InvalidDataException("Invalid cloud environment auxiliary input.");
        return parsed with { InputIdentitySha256 = input.IdentitySha256 };
    }
}

/// <summary>Configuration options for step 30b regional stellar visibility.</summary>
public sealed class RegionalStellarVisibilityProcessingStepOptions : IValidatableObject
{
    [Required(AllowEmptyStrings = false), MaxLength(128)]
    public string OutputVariant { get; init; } = RegionalStellarVisibilityProducts.Variant;

    [Range(1, RegionalStellarVisibilityOptions.MaximumGridDimension)]
    public int GridColumns { get; init; } = 4;

    [Range(1, RegionalStellarVisibilityOptions.MaximumGridDimension)]
    public int GridRows { get; init; } = 4;

    [Range(1, 1000)]
    public int MinimumExpectedStarsPerTile { get; init; } = 3;

    [Range(1, 1000)]
    public int MinimumMissingStarsForDeficit { get; init; } = 2;

    [Range(1, 999_999)]
    public int DeficitVisibilityThresholdMillionths { get; init; } = 600_000;

    [Range(3.0, 1000.0)]
    public double MinimumExpectedSignalToNoise { get; init; } = 8.0;

    [Range(1.0, 10.0)]
    public double PeakMarginFactor { get; init; } = 1.25;

    [Range(0.5, 32.0)]
    public double MatchRadiusPixels { get; init; } = 3.5;

    [Range(0.5, 128.0)]
    public double OffsetSearchRadiusPixels { get; init; } = 20.0;

    [Range(0.0, 256.0)]
    public double EdgeMarginPixels { get; init; } = 10.0;

    [Range(0.0, 64.0)]
    public double CrowdingRadiusPixels { get; init; } = 8.0;

    [Range(0.0, 10.0)]
    public double CrowdingMagnitudeDifference { get; init; } = 1.5;

    [Range(1, 1_000_000)]
    public int MinimumTileSupportedAreaMillionths { get; init; } = 250_000;

    [Range(1.0, 64.0)]
    public double MaximumAcceptableHfdPixels { get; init; } = 6.0;

    [Range(1, ExposureMeasurementProducts.MaximumHfdSamples)]
    public int MinimumFocusSamples { get; init; } = 3;

    [Range(1, 1000)]
    public int MinimumTooBroadExclusionsForPoorFocus { get; init; } = 3;

    [Range(0, 1_000_000)]
    public int MaximumSaturatedFractionMillionths { get; init; } = 100_000;

    [Range(0.0, 90.0)]
    public double MoonGlareExclusionDegrees { get; init; } = 15.0;

    [Range(0.0, 90.0)]
    public double MoonWithholdAltitudeDegrees { get; init; } = 10.0;

    [Range(0.0, 1.0)]
    public double MoonWithholdIlluminatedFraction { get; init; } = 0.70;

    [Range(0.0, 1.0)]
    public double MoonInFovWithholdIlluminatedFraction { get; init; } = 0.35;

    [Range(0.25, 32.0)]
    public double MaximumPointingShiftPixels { get; init; } = 2.0;

    [Range(0.01, 1.0)]
    public double MaximumImpliedRollShiftFraction { get; init; } = 0.60;

    public bool IncludeMask { get; init; } = true;

    public IReadOnlyList<NormalizedCloudRectangle>? ExcludedRegions { get; init; }

    public RegionalVisibilityPhotometricProfileV1? PhotometricProfile { get; init; }

    public double? MagnitudeZeroElectronsPerSecond { get; init; }

    public double? NominalPsfSigmaPixels { get; init; }

    public double? DarkCurrentElectronsPerSecond { get; init; }

    public double? VignettingStrength { get; init; }

    internal RegionalStellarVisibilityOptions ToRecipeOptions(RegionalVisibilityPhotometricProfileV1? resolvedProfile)
        => new(
            GridColumns,
            GridRows,
            MinimumExpectedStarsPerTile,
            MinimumMissingStarsForDeficit,
            DeficitVisibilityThresholdMillionths,
            MinimumExpectedSignalToNoise,
            PeakMarginFactor,
            MatchRadiusPixels,
            OffsetSearchRadiusPixels,
            EdgeMarginPixels,
            CrowdingRadiusPixels,
            CrowdingMagnitudeDifference,
            MinimumTileSupportedAreaMillionths,
            MaximumAcceptableHfdPixels,
            MinimumFocusSamples,
            MinimumTooBroadExclusionsForPoorFocus,
            MaximumSaturatedFractionMillionths,
            MoonGlareExclusionDegrees,
            MoonWithholdAltitudeDegrees,
            MoonWithholdIlluminatedFraction,
            MoonInFovWithholdIlluminatedFraction,
            MaximumPointingShiftPixels,
            MaximumImpliedRollShiftFraction,
            IncludeMask,
            ExcludedRegions,
            resolvedProfile);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (GridColumns * GridRows > RegionalStellarVisibilityOptions.MaximumTileCount)
        {
            yield return new ValidationResult(
                $"Regional visibility grid cannot exceed {RegionalStellarVisibilityOptions.MaximumTileCount} tiles.",
                [nameof(GridColumns), nameof(GridRows)]);
        }

        if (MinimumMissingStarsForDeficit > MinimumExpectedStarsPerTile)
        {
            yield return new ValidationResult(
                "Minimum missing stars for deficit cannot exceed minimum expected stars per tile.",
                [nameof(MinimumMissingStarsForDeficit), nameof(MinimumExpectedStarsPerTile)]);
        }

        if (OffsetSearchRadiusPixels < MatchRadiusPixels)
        {
            yield return new ValidationResult(
                "Offset search radius must be at least the star match radius.",
                [nameof(OffsetSearchRadiusPixels), nameof(MatchRadiusPixels)]);
        }

        if (PhotometricProfile is not null)
        {
            ValidationResult? profileError = null;
            try
            {
                PhotometricProfile.Validate();
            }
            catch (ArgumentException exception)
            {
                profileError = new ValidationResult(exception.Message, [nameof(PhotometricProfile)]);
            }

            if (profileError is not null)
            {
                yield return profileError;
            }
        }
    }
}

/// <summary>Reads and verifies the retained regional stellar visibility assessment for one execution.</summary>
internal sealed class CaptureRegionalStellarVisibilityReader(
    CaptureProcessingPersistence persistence)
    : IRegionalStellarVisibilityRunReader
{
    public async ValueTask<RegionalStellarVisibilityRunRead?> ReadAsync(
        ProcessingGraphExecutionDetail execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var node = execution.Nodes.FirstOrDefault(static candidate =>
            candidate.OutputContracts.Any(static contract =>
                contract.Role == FrameArtifactRole.Metadata &&
                contract.SchemaVersion == RegionalStellarVisibilityProducts.SchemaVersion));
        if (node is null)
        {
            return null;
        }

        var output = node.Outputs.FirstOrDefault(static candidate =>
            candidate.Role == FrameArtifactRole.Metadata &&
            candidate.AvailabilityState == "Available");
        if (output is null)
        {
            return new RegionalStellarVisibilityRunRead(
                null,
                node.Reason ?? execution.Execution.FailureReason ?? "regional-visibility-unavailable");
        }

        try
        {
            var artifact = await persistence.RestoreRegionalVisibilityAsync(output.ArtifactId, cancellationToken)
                .ConfigureAwait(false);
            if (artifact.CaptureId != execution.Execution.CaptureId ||
                !output.SourceArtifactIds.SequenceEqual(artifact.SourceArtifactIds ?? []) ||
                ProcessingIdentity.CreateOutputIdentity(
                    artifact.Role, artifact.Variant, artifact.RecipeIdentitySha256, artifact.SourceArtifactIds ?? [])
                != output.OutputIdentitySha256)
            {
                return new RegionalStellarVisibilityRunRead(null, "regional-visibility-binding-mismatch");
            }

            var assessment = RegionalStellarVisibilityProducts.Read(artifact);
            return assessment.Sources.RawArtifactId == execution.Execution.PrimaryArtifactId
                ? new RegionalStellarVisibilityRunRead(assessment, null)
                : new RegionalStellarVisibilityRunRead(null, "regional-visibility-binding-mismatch");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                          UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return new RegionalStellarVisibilityRunRead(null, "regional-visibility-unreadable");
        }
    }
}

internal sealed partial class CaptureProcessingPersistence
{
    internal async ValueTask<ProcessingArtifact> RestoreRegionalVisibilityAsync(
        Guid artifactId, CancellationToken cancellationToken)
    {
        var output = await _store.ReadOutputByArtifactIdAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (output is null || output.AvailabilityState != "Available" ||
            output.ProductSchemaVersion != RegionalStellarVisibilityProducts.SchemaVersion ||
            output.Artifact.MediaType != RegionalStellarVisibilityProducts.MediaType)
        {
            throw new InvalidDataException("The requested regional stellar visibility output is unavailable.");
        }

        var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
        return CreateProcessingArtifact(output, restored);
    }
}
