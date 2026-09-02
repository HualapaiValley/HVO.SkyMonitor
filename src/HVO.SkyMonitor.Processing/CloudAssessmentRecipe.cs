using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public sealed record CloudAssessmentOptions(
    int GridColumns = 16,
    int GridRows = 12,
    int TransmissionThresholdMillionths = 850_000,
    ushort MinimumReferenceSignal = 64,
    int MinimumSamplesPerTile = 16,
    int MaximumSaturatedFractionMillionths = 100_000,
    bool IncludeMask = true,
    NormalizedCloudCircle? ImageCircle = null,
    int? HorizonRadiusMillionths = null,
    IReadOnlyList<NormalizedCloudRectangle>? ExcludedRegions = null);

internal sealed class CloudAssessmentRecipe : IProcessingRecipe
{
    private const string ClearReferenceInputName = "clear-reference";
    private const string EnvironmentInputName = "environment";
    internal static readonly ProcessingAlgorithmIdentity PolicyAlgorithm = new("cloud-assessment-policy", "v1");
    internal static readonly ProcessingAlgorithmIdentity TransmissionAlgorithm =
        new("cloud-transmission", Linear16CloudTransmissionEstimator.AlgorithmVersion);
    private static readonly JsonSerializerOptions EnvironmentSerializerOptions = CreateEnvironmentSerializerOptions();

    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.CloudAssessment,
        "1.0.0",
        "comparative-tiled-transmission-v1",
        ProcessingOperationKind.Analyzer);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<CloudAssessmentOptions>(options);
        if (parsed.GridColumns < 1 || parsed.GridRows < 1 ||
            (long)parsed.GridColumns * parsed.GridRows > Linear16CloudTransmissionEstimator.MaximumTileCount ||
            parsed.TransmissionThresholdMillionths is <= 0 or >= 1_000_000 ||
            parsed.MinimumSamplesPerTile < 1 ||
            parsed.MaximumSaturatedFractionMillionths is < 0 or > 1_000_000 ||
            parsed.ExcludedRegions?.Count > Linear16CloudTransmissionEstimator.MaximumExcludedRegionCount ||
            parsed.ImageCircle is { } circle &&
                (circle.CenterXMillionths is < 0 or > 1_000_000 ||
                 circle.CenterYMillionths is < 0 or > 1_000_000 ||
                 circle.RadiusMillionths is <= 0 or > 1_000_000) ||
            parsed.HorizonRadiusMillionths is <= 0 or > 1_000_000 ||
            parsed.ExcludedRegions?.Any(static region =>
                region.LeftMillionths < 0 || region.TopMillionths < 0 ||
                region.RightMillionths > 1_000_000 || region.BottomMillionths > 1_000_000 ||
                region.LeftMillionths >= region.RightMillionths ||
                region.TopMillionths >= region.BottomMillionths) == true)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed with { ExcludedRegions = parsed.ExcludedRegions ?? [] });
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (current is null)
        {
            return ValueTask.FromResult(failure!);
        }
        if (!ProcessingRecipeSupport.TryValidateFrame(current, out var currentLayout, out var layoutFailure))
        {
            return ValueTask.FromResult(layoutFailure!);
        }
        if (currentLayout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.UnsupportedFormat,
                nameof(current.Layout)));
        }

        var options = ProcessingRecipeSupport.ParseOptions<CloudAssessmentOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var environment = ResolveEnvironment(request, out var environmentFailure);
        if (environmentFailure is not null)
        {
            return ValueTask.FromResult(environmentFailure);
        }
        var clearReference = ResolveClearReference(request, current, out var referenceFailure);
        if (referenceFailure is not null)
        {
            return ValueTask.FromResult(referenceFailure);
        }
        var missingReference = clearReference is null;
        var incompatibleReference = clearReference is not null &&
            !InputsAreCompatible(current, currentLayout, clearReference);
        var calibration = CreateCalibration(currentLayout, current.Compatibility);
        var missingCalibration = calibration is null;
        CloudTransmissionResult? transmission = null;
        if (!missingReference && !incompatibleReference && !missingCalibration)
        {
            var referenceLayout = clearReference!.Layout!;
            transmission = Linear16CloudTransmissionEstimator.Assess(
                CreateFrame(current, currentLayout, calibration!),
                CreateFrame(clearReference, referenceLayout, calibration!),
                new CloudTransmissionEstimatorOptions(
                    options.GridColumns,
                    options.GridRows,
                    options.TransmissionThresholdMillionths,
                    options.MinimumReferenceSignal,
                    options.MinimumSamplesPerTile,
                    options.MaximumSaturatedFractionMillionths,
                    options.IncludeMask,
                    options.ImageCircle,
                    options.HorizonRadiusMillionths,
                    options.ExcludedRegions),
                cancellationToken);
        }

        var insufficientSupport = transmission is not null && transmission.CoverageMillionths is null;
        var saturation = transmission?.IsSaturationContaminated == true;
        var precipitation = environment.PrecipitationDetected;
        var daylight = environment.SolarRegime == CaptureSolarRegime.Day;
        var twilight = environment.SolarRegime == CaptureSolarRegime.Twilight;
        var environmentMissing = environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Missing ||
            environment.SolarRegime is null;
        var environmentStale = environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Stale;
        var environmentContradictory =
            environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Contradictory;
        var reasons = BuildReasons(
            missingReference,
            incompatibleReference,
            missingCalibration,
            insufficientSupport,
            saturation,
            precipitation,
            daylight,
            twilight,
            environmentMissing,
            environmentStale,
            environmentContradictory);
        var unusable = missingReference || incompatibleReference || missingCalibration || insufficientSupport ||
            saturation || precipitation || daylight;
        var status = saturation || precipitation
            ? CloudAssessmentStatus.Contaminated
            : unusable
                ? CloudAssessmentStatus.InsufficientEvidence
                : CloudAssessmentStatus.Quantified;
        var quality = unusable
            ? CloudAssessmentQuality.Unusable
            : twilight || environmentMissing || environmentStale || environmentContradictory
                ? CloudAssessmentQuality.Degraded
                : CloudAssessmentQuality.Good;
        var regions = transmission is null
            ? CreateEmptyRegions(currentLayout, options.GridColumns, options.GridRows)
            : transmission.Regions.Select(static region => new CloudAssessmentRegionV1(
                region.Column,
                region.Row,
                region.X,
                region.Y,
                region.Width,
                region.Height,
                region.ConsideredSampleCount,
                region.AcceptedSampleCount,
                region.SaturatedSampleCount,
                region.TransmissionMillionths,
                region.IsCloudy)).ToArray();
        var algorithms = transmission is null
            ? new[] { PolicyAlgorithm }
            : new[] { PolicyAlgorithm, TransmissionAlgorithm };
        var assessment = new CloudAssessmentV1(
            CloudAssessmentV1.CurrentSchemaVersion,
            status,
            quality,
            reasons,
            unusable ? null : transmission!.CoverageMillionths,
            unusable ? 0 : transmission!.ConfidenceMillionths,
            new CloudAssessmentGridV1(
                options.GridColumns,
                options.GridRows,
                options.TransmissionThresholdMillionths,
                transmission?.ValidTileCount ?? 0,
                transmission?.CloudyTileCount ?? 0,
                transmission?.ValidSampleCount ?? 0,
                transmission?.CloudySampleCount ?? 0),
            regions,
            !unusable && transmission?.Mask is not null
                ? new CloudAssessmentMaskV1(
                    CloudAssessmentMaskV1.RowMajorLsbFirst,
                    options.GridColumns,
                    options.GridRows,
                    transmission.Mask.ToArray())
                : null,
            CreateSource(current),
            clearReference is null ? null : CreateSource(clearReference),
            calibration,
            environment,
            identity.IdentitySha256,
            algorithms);
        var payload = CloudAssessmentJson.Serialize(assessment);
        var sources = clearReference is null
            ? new[] { current }
            : new[] { current, clearReference };
        return ValueTask.FromResult(ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Metadata,
            request.OutputVariant,
            "application/vnd.hvo.cloud-assessment+json",
            null,
            payload,
            identity,
            algorithms,
            sources,
            sources.Aggregate(TimeSpan.Zero, static (total, source) => total + source.Integration),
            current.Compatibility,
            ProcessingProductKind.Metadata,
            CloudAssessmentV1.CurrentSchemaVersion,
            assessment.AssessmentIdentitySha256)));
    }

    private static ProcessingArtifact? ResolveClearReference(
        ProcessingExecutionRequest request,
        ProcessingArtifact current,
        out ProcessingOutcome? failure)
    {
        var auxiliary = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, ClearReferenceInputName, StringComparison.Ordinal));
        if (auxiliary is null)
        {
            failure = null;
            return null;
        }
        if (auxiliary.Kind != ProcessingAuxiliaryInputKind.Artifact || auxiliary.Selector is null ||
            !ProcessingRecipeSupport.SelectorIsValid(auxiliary.Selector))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.AuxiliaryInputs));
            return null;
        }
        var matches = request.Inputs.Where(input =>
            input.ArtifactId != current.ArtifactId &&
            (auxiliary.ArtifactId is null || input.ArtifactId == auxiliary.ArtifactId) &&
            ProcessingRecipeSupport.Matches(input, auxiliary.Selector)).ToArray();
        if (matches.Length > 1)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.AmbiguousInput,
                nameof(request.AuxiliaryInputs));
            return null;
        }
        failure = null;
        return matches.SingleOrDefault();
    }

    private static CloudAssessmentEnvironmentV1 ResolveEnvironment(
        ProcessingExecutionRequest request,
        out ProcessingOutcome? failure)
    {
        var auxiliary = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, EnvironmentInputName, StringComparison.Ordinal));
        if (auxiliary is null)
        {
            failure = null;
            return MissingEnvironment();
        }
        if (auxiliary.Kind == ProcessingAuxiliaryInputKind.CanonicalJson &&
            string.Equals(auxiliary.SchemaVersion, CloudAssessmentEnvironmentV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            try
            {
                var environment = JsonSerializer.Deserialize<CloudAssessmentEnvironmentV1>(
                    auxiliary.Payload.Span,
                    EnvironmentSerializerOptions);
                if (environment is not null && EnvironmentIsValid(environment))
                {
                    failure = null;
                    return environment with { InputIdentitySha256 = auxiliary.IdentitySha256 };
                }
            }
            catch (JsonException)
            {
            }
        }
        failure = ProcessingOutcome.TerminalFailure(
            ProcessingReasonCodes.InvalidInput,
            nameof(request.AuxiliaryInputs));
        return MissingEnvironment();

        static CloudAssessmentEnvironmentV1 MissingEnvironment() => new(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            null,
            EnvironmentalObservationMatchStatus.Missing,
            null,
            null,
            false);
    }

    private static bool EnvironmentIsValid(CloudAssessmentEnvironmentV1 environment)
        => string.Equals(
               environment.SchemaVersion,
               CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
               StringComparison.Ordinal) &&
           Enum.IsDefined(environment.PrecipitationStatus) &&
           (environment.PrecipitationStatus is
                EnvironmentalObservationMatchStatus.Missing or EnvironmentalObservationMatchStatus.Contradictory
               ? environment.PrecipitationObservationId is null &&
                 environment.PrecipitationContentSha256 is null && !environment.PrecipitationDetected
               : environment.PrecipitationObservationId is not null &&
                 environment.PrecipitationContentSha256 is { Length: 64 } checksum && checksum.All(Uri.IsHexDigit));

    internal static bool InputsAreCompatible(
        ProcessingArtifact current,
        FrameLayoutDescriptor currentLayout,
        ProcessingArtifact clearReference)
    {
        if (!ProcessingRecipeSupport.TryValidateFrame(clearReference, out var referenceLayout, out _))
        {
            return false;
        }
        return current.Compatibility == clearReference.Compatibility &&
            current.Integration == clearReference.Integration &&
            clearReference.SourceArtifactIds?.Contains(current.ArtifactId) != true &&
            currentLayout.Width == referenceLayout.Width &&
            currentLayout.Height == referenceLayout.Height &&
            currentLayout.PixelFormat == referenceLayout.PixelFormat &&
            currentLayout.ByteOrder == referenceLayout.ByteOrder &&
            currentLayout.SampleDepthBits == referenceLayout.SampleDepthBits &&
            currentLayout.ContainerDepthBits == referenceLayout.ContainerDepthBits &&
            currentLayout.Packing == referenceLayout.Packing &&
            currentLayout.CfaPattern == referenceLayout.CfaPattern &&
            currentLayout.BlackLevel == referenceLayout.BlackLevel &&
            currentLayout.WhiteLevel == referenceLayout.WhiteLevel &&
            currentLayout.StoredCodeTransform == referenceLayout.StoredCodeTransform &&
            currentLayout.LevelCodeSpace == referenceLayout.LevelCodeSpace &&
            currentLayout.Readout == referenceLayout.Readout;
    }

    internal static CloudAssessmentCalibrationV1? CreateCalibration(
        FrameLayoutDescriptor layout,
        ProcessingCompatibilityIdentity compatibility)
    {
        if (layout.BlackLevel is not { } black || layout.WhiteLevel is not { } white ||
            black < 0 || white > ushort.MaxValue || black != Math.Truncate(black) ||
            white != Math.Truncate(white) || white <= black)
        {
            return null;
        }
        return new CloudAssessmentCalibrationV1(
            (ushort)black,
            (ushort)white,
            (ushort)white,
            compatibility.Calibration,
            compatibility.Mask,
            compatibility.Sensor,
            compatibility.ProcessingProfile);
    }

    private static Linear16CloudFrame CreateFrame(
        ProcessingArtifact artifact,
        FrameLayoutDescriptor layout,
        CloudAssessmentCalibrationV1 calibration)
        => new(
            new ImageLayout(layout.Width, layout.Height, layout.PixelFormat, layout.StrideBytes),
            artifact.Payload,
            calibration.BlackLevel,
            calibration.WhiteLevel,
            calibration.SaturationLevel);

    private static CloudAssessmentSourceV1 CreateSource(ProcessingArtifact artifact)
        => new(artifact.ArtifactId, artifact.Role, artifact.Variant, artifact.RecipeIdentitySha256);

    private static CloudAssessmentRegionV1[] CreateEmptyRegions(
        FrameLayoutDescriptor layout,
        int columns,
        int rows)
    {
        var regions = new CloudAssessmentRegionV1[checked(columns * rows)];
        for (var row = 0; row < rows; row++)
        {
            var y0 = (int)((long)row * layout.Height / rows);
            var y1 = (int)((long)(row + 1) * layout.Height / rows);
            for (var column = 0; column < columns; column++)
            {
                var x0 = (int)((long)column * layout.Width / columns);
                var x1 = (int)((long)(column + 1) * layout.Width / columns);
                regions[row * columns + column] = new CloudAssessmentRegionV1(
                    column, row, x0, y0, x1 - x0, y1 - y0, 0, 0, 0, null, false);
            }
        }
        return regions;
    }

    private static string[] BuildReasons(
        bool missingReference,
        bool incompatibleReference,
        bool missingCalibration,
        bool insufficientSupport,
        bool saturation,
        bool precipitation,
        bool daylight,
        bool twilight,
        bool environmentMissing,
        bool environmentStale,
        bool environmentContradictory)
    {
        var reasons = new List<string>(10);
        Add(missingReference, CloudAssessmentReasonCodes.MissingClearReference);
        Add(incompatibleReference, CloudAssessmentReasonCodes.IncompatibleClearReference);
        Add(missingCalibration, CloudAssessmentReasonCodes.MissingCalibration);
        Add(insufficientSupport, CloudAssessmentReasonCodes.InsufficientValidSupport);
        Add(saturation, CloudAssessmentReasonCodes.SaturationContamination);
        Add(precipitation, CloudAssessmentReasonCodes.PrecipitationContamination);
        Add(daylight, CloudAssessmentReasonCodes.Daylight);
        Add(twilight, CloudAssessmentReasonCodes.Twilight);
        Add(environmentMissing, CloudAssessmentReasonCodes.EnvironmentMissing);
        Add(environmentStale, CloudAssessmentReasonCodes.EnvironmentStale);
        Add(environmentContradictory, CloudAssessmentReasonCodes.EnvironmentContradictory);
        return reasons.ToArray();

        void Add(bool condition, string reason)
        {
            if (condition)
            {
                reasons.Add(reason);
            }
        }
    }

    private static JsonSerializerOptions CreateEnvironmentSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
