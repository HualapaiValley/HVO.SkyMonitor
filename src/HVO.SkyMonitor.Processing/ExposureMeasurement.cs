using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum ExposureCalibrationDisposition
{
    ReferenceCalibrated,
    NormalizationOnly,
    Missing,
    Failed,
    Incompatible
}

public enum ExposureMeasurementStatus
{
    Usable,
    Empty,
    Unsuitable,
    Unavailable
}

/// <summary>Identity facts from the committed pixel descriptor. Comparing these facts needs no pixel reads.</summary>
public sealed record ExposureMeasurementSourceV1(
    Guid CaptureId,
    Guid ArtifactId,
    string DescriptorSha256,
    string PayloadSha256,
    string RecipeSha256,
    string? OutputIdentitySha256,
    FrameLayoutDescriptor Layout)
{
    public static ExposureMeasurementSourceV1 FromDescriptor(ReconstructionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var artifact = descriptor.Artifact;
        var recipe = ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe);
        return new(descriptor.Capture.CaptureId, artifact.ArtifactId,
            CaptureContractJson.ComputeDescriptorSha256(descriptor), artifact.ChecksumSha256.ToUpperInvariant(),
            recipe.IdentitySha256, artifact.Role == FrameArtifactRole.Raw ? null :
                ProcessingIdentity.CreateOutputIdentity(artifact.Role, artifact.Variant,
                    recipe.IdentitySha256, artifact.SourceArtifactIds), descriptor.Layout);
    }
}

/// <summary>Frozen measurement inputs. Pixel pitch, when supplied, is the native sensor pitch.</summary>
public sealed record ExposureMeasurementInputV1(
    ExposureMeasurementSourceV1 Raw,
    ExposureMeasurementSourceV1? Linear,
    ExposureCalibrationDisposition Calibration,
    string? CalibrationReason,
    string OrientationIdentity,
    double? NativePixelPitchMicrometers = null,
    RigOrientation? RigOrientation = null,
    ProjectionContext? ApertureProjection = null,
    ExposureKey? Exposure = null,
    string? OpticalCalibrationVersion = null,
    double? PixelScaleArcseconds = null,
    LinearExposureDisposition? Step3Disposition = null,
    long? Step3Revision = null);

public sealed record ExposureClippingV1(ushort? StoredWhite, long? Count, string? Reason);
public sealed record ExposureBrightnessV1(
    long SampleCount, ushort Minimum, ushort Maximum, ulong Sum, long ZeroCount, ExposureClippingV1 SensorClipping)
{
    public double Mean => (double)Sum / SampleCount;
}

public sealed record ExposureSourcesV1(
    ExposureMeasurementStatus Status,
    string? Reason,
    int? CandidateCount,
    int? AvailableBackgroundTiles,
    string? SettingsSha256,
    BayerPattern? LocalBayerPattern,
    IReadOnlyList<StellarDetection> Detections,
    IReadOnlyList<StellarMeasurementExclusion> Exclusions);

public sealed record ExposureHfdSampleV1(int DetectionIndex, string Reason, double? DiameterPixels)
{
    /// <summary>Conservative core trail estimate for a Bayer focus sample whose aperture moments flagged it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BayerCoreTrailUpperPixels { get; init; }
}
public sealed record ExposureHfdV1(
    ExposureMeasurementStatus Status,
    string? Reason,
    int? ValidCount,
    double? MeanPixels,
    double? MedianPixels,
    int OmittedCount,
    string? SettingsSha256,
    IReadOnlyList<ExposureHfdSampleV1> Samples);

/// <summary>Centroids use delivered-image pixel edges, with sample centers at +0.5. HFD uses delivered-image pixels.</summary>
public sealed record ExposureMeasurementV1(
    string SchemaVersion,
    ExposureMeasurementInputV1 Input,
    ExposureBrightnessV1? RawBrightness,
    string? RawBrightnessUnavailableReason,
    ExposureClippingV1? LinearOutputClipping,
    ExposureSourcesV1 Sources,
    ExposureHfdV1 Hfd)
{
    public string CoordinateConvention { get; init; } = ExposureMeasurementProducts.CoordinateConvention;
    public string HfdUnit { get; init; } = "delivered-image-pixels";
}

internal enum ExposureFocusSamplingPolicy
{
    Brightest,
    BrightestWithBayerCoreCheck,
    EligibleFirst,
    EligibleFirstWithOccupancy
}

/// <summary>Constructs and reads the single versioned, source-bound exposure measurement product.</summary>
public static class ExposureMeasurementProducts
{
    public const string StepAlias = "ExposureMeasurement";

    /// <summary>These steps and their reserved identifiers are supplied by the centered capture profile.</summary>
    public static bool IsSystemManagedStepAlias(string alias)
        => string.Equals(alias, StepAlias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(alias, ExposureCatalogPredictionProducts.StepAlias, StringComparison.OrdinalIgnoreCase);
    public const string SchemaVersion = "exposure-measurement-v1";
    public const string Variant = SchemaVersion;
    public const string MediaType = "application/vnd.hvo.exposure-measurement+json";
    public const string CoordinateConvention = "delivered-image-pixel-edge";
    public const int MaximumHfdSamples = 32;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public static ProcessingExecutionRequest Request(
        ExposureMeasurementInputV1 input, ProcessingArtifact raw, ProcessingArtifact? linear)
        => Request(input, raw, linear, retainedOutputIdentitySha256: null);

    internal static ProcessingExecutionRequest Request(ExposureMeasurementInputV1 input, ProcessingArtifact raw,
        ProcessingArtifact? linear, string? retainedOutputIdentitySha256)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (input is null || !IsValidUtf16(raw.Variant) || !IsValidUtf16(linear?.Variant))
            throw Invalid("input");
        ValidateInput(input);
        var hasRetainedCandidate = IsSha256(retainedOutputIdentitySha256);
        if (!hasRetainedCandidate)
        {
            ValidateFreshAdmissionEnvelopeBudget(input);
        }
        else if (RawMetadataCharCount(input) > StructuredProcessingProductDescriptorV1.MaximumPayloadBytes)
        {
            throw Invalid("input envelope budget");
        }
        var request = new ProcessingExecutionRequest(BuiltInProcessingRecipes.ExposureMeasurement, SerializeInput(input),
            ProcessingInputSelector.Raw(raw.Variant), linear is null ? [raw] : [raw, linear], Variant,
            InputArtifactId: raw.ArtifactId);
        var format = input.Linear?.Layout.PixelFormat;
        if (format is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            if (hasRetainedCandidate && !MatchesRetainedOutput(request, input, retainedOutputIdentitySha256!))
                ValidateFreshAdmissionEnvelopeBudget(input);
            return request;
        }

        if (hasRetainedCandidate)
        {
            if (MatchesRetainedOutput(request, input, retainedOutputIdentitySha256!))
                return request;
            if (format == CameraPixelFormat.BayerRggb16)
            {
                var priorBayer = request with { Options = SerializeParameters(input, ExposureFocusSamplingPolicy.BrightestWithBayerCoreCheck) };
                if (MatchesRetainedOutput(priorBayer, input, retainedOutputIdentitySha256!))
                    return priorBayer;
            }
            var priorEligibleFirst = request with { Options = SerializeParameters(input, ExposureFocusSamplingPolicy.EligibleFirst) };
            if (MatchesRetainedOutput(priorEligibleFirst, input, retainedOutputIdentitySha256!))
                return priorEligibleFirst;
            var current = request with { Options = SerializeParameters(input, ExposureFocusSamplingPolicy.EligibleFirstWithOccupancy) };
            if (MatchesRetainedOutput(current, input, retainedOutputIdentitySha256!))
                return current;
            ValidateFreshAdmissionEnvelopeBudget(input);
            return current;
        }
        return request with { Options = SerializeParameters(input, ExposureFocusSamplingPolicy.EligibleFirstWithOccupancy) };
    }

    private static bool MatchesRetainedOutput(ProcessingExecutionRequest request, ExposureMeasurementInputV1 input, string retainedOutput)
    {
        var recipe = BuiltInProcessingRecipes.CreateExecutionIdentity(request.RecipeName, request.Options, request.Input);
        return Same(ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, Variant,
            recipe.IdentitySha256, SourceIds(input)), retainedOutput);
    }

    /// <summary>Gets the admission lookup key. Consumers still verify the product against independent evidence.</summary>
    public static ExposureKey ReadExposureKey(ProcessingArtifact retained)
    {
        ArgumentNullException.ThrowIfNull(retained);
        if (retained.RecipeDescriptor is not { Name: BuiltInProcessingRecipes.ExposureMeasurement } recipe ||
            !recipe.Options.TryGetProperty("parameters", out var parameters))
            throw Invalid("admission lookup identity");
        return ReadParameters(parameters).Input.Exposure ?? throw Invalid("admission lookup identity");
    }

    /// <summary>Verifies the retained envelope and its identity against the caller's expected raw and linear facts.</summary>
    public static ExposureMeasurementV1 Read(ProcessingArtifact artifact, ExposureMeasurementInputV1 expected)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(expected);
        ValidateInput(expected);
        if (artifact.Layout is not null || artifact.CaptureId != expected.Raw.CaptureId || artifact.RecipeDescriptor is not { } descriptor)
            throw Invalid("capture");
        var recipe = ProcessingIdentity.CreateRecipeIdentity(descriptor);
        var sources = artifact.SourceArtifactIds ?? [];
        if (!Same(recipe.IdentitySha256, artifact.RecipeIdentitySha256) ||
            artifact.ArtifactId != ProcessingIdentity.CreateArtifactId(ProcessingIdentity.CreateOutputIdentity(
                artifact.Role, artifact.Variant, recipe.IdentitySha256, sources)))
            throw Invalid("artifact identity");
        var measurement = Read(artifact.Role, artifact.Variant, artifact.ProductKind, artifact.MediaType,
            artifact.SchemaVersion, artifact.Payload, artifact.ContentIdentitySha256, recipe, sources);
        return VerifyBinding(measurement, expected);
    }

    public static ExposureMeasurementV1 Read(ProcessingProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (product.Layout is not null || !Same(ProcessingIdentity.CreateRecipeIdentity(product.Recipe.Descriptor).IdentitySha256,
                product.Recipe.IdentitySha256) || !Same(product.OutputIdentitySha256, ProcessingIdentity.CreateOutputIdentity(
                product.Role, product.Variant, product.Recipe.IdentitySha256, product.SourceArtifactIds)) ||
            !Same(product.ChecksumSha256, product.ContentIdentitySha256))
            throw Invalid("output identity");
        var measurement = Read(product.Role, product.Variant, product.Kind, product.MediaType, product.SchemaVersion,
            product.Payload, product.ContentIdentitySha256, product.Recipe, product.SourceArtifactIds);
        var (_, policy) = ReadParameters(product.Recipe.Descriptor.Options.GetProperty("parameters"));
        if (!product.Algorithms.SequenceEqual(ExposureMeasurementRecipe.Algorithms(measurement.Input, policy)))
            throw Invalid("algorithms");
        return measurement;
    }

    public static ExposureMeasurementV1 VerifyBinding(ExposureMeasurementV1 result, ExposureMeasurementInputV1 expected)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateInput(expected);
        if (result.Input != expected)
            throw Invalid("input binding");
        return result;
    }

    public static ExposureMeasurementV1 VerifyRawBinding(ExposureMeasurementV1 result, ReconstructionDescriptor raw)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Artifact.Role != FrameArtifactRole.Raw || result.Input.Raw != ExposureMeasurementSourceV1.FromDescriptor(raw))
            throw Invalid("raw binding");
        return result;
    }

    private const string BayerCoreTrailPolicy = "noise-corrected-bayer-core-v1";
    private const string EligibleFirstPolicy = "eligible-first-v1";

    internal static JsonElement SerializeParameters(ExposureMeasurementInputV1 input, ExposureFocusSamplingPolicy policy)
        => policy switch
        {
            ExposureFocusSamplingPolicy.EligibleFirstWithOccupancy =>
                CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(
                    new MeasurementV3Parameters(EligibleFirstPolicy, StellarSourceMeasurer.AlgorithmVersion, input), Json)),
            ExposureFocusSamplingPolicy.EligibleFirst =>
                CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new FocusAllocationParameters(EligibleFirstPolicy, input), Json)),
            ExposureFocusSamplingPolicy.BrightestWithBayerCoreCheck =>
                CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new BayerFocusParameters(BayerCoreTrailPolicy, input), Json)),
            _ => SerializeInput(input)
        };

    internal static (ExposureMeasurementInputV1 Input, ExposureFocusSamplingPolicy Policy) ReadParameters(JsonElement options)
    {
        if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("stellarMeasurement", out _))
        {
            var parameters = options.Deserialize<MeasurementV3Parameters>(Json);
            if (parameters is not
                {
                    HfdAllocation: EligibleFirstPolicy,
                    StellarMeasurement: StellarSourceMeasurer.AlgorithmVersion,
                    Input.Linear.Layout.PixelFormat: CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16
                })
                throw Invalid("measurement policy");
            ValidateInput(parameters.Input);
            return (parameters.Input, ExposureFocusSamplingPolicy.EligibleFirstWithOccupancy);
        }
        if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("hfdAllocation", out _))
        {
            var parameters = options.Deserialize<FocusAllocationParameters>(Json);
            if (parameters is not { HfdAllocation: EligibleFirstPolicy, Input.Linear.Layout.PixelFormat: CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 })
                throw Invalid("HFD allocation policy");
            ValidateInput(parameters.Input);
            return (parameters.Input, ExposureFocusSamplingPolicy.EligibleFirst);
        }
        if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("bayerFocus", out _))
        {
            var parameters = options.Deserialize<BayerFocusParameters>(Json);
            if (parameters is not { BayerFocus: BayerCoreTrailPolicy, Input.Linear.Layout.PixelFormat: CameraPixelFormat.BayerRggb16 })
                throw Invalid("Bayer focus policy");
            ValidateInput(parameters.Input);
            return (parameters.Input, ExposureFocusSamplingPolicy.BrightestWithBayerCoreCheck);
        }
        return (ReadInput(options), ExposureFocusSamplingPolicy.Brightest);
    }

    private sealed record BayerFocusParameters(string BayerFocus, ExposureMeasurementInputV1 Input);
    private sealed record FocusAllocationParameters(string HfdAllocation, ExposureMeasurementInputV1 Input);
    private sealed record MeasurementV3Parameters(string HfdAllocation, string StellarMeasurement, ExposureMeasurementInputV1 Input);

    internal static JsonElement SerializeInput(ExposureMeasurementInputV1 input)
        => CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(input, Json));

    internal static ExposureMeasurementInputV1 ReadInput(JsonElement options)
    {
        var input = options.Deserialize<ExposureMeasurementInputV1>(Json) ?? throw Invalid("input");
        ValidateInput(input);
        return input;
    }

    /// <summary>
    /// Maximum permitted worst-case non-source UTF-8 envelope bytes (serialized input/envelope shell including JSON
    /// escaping plus worst-case Sources header and HFD section) for an admitted Step 4 measurement request. Matches
    /// <see cref="StellarFootprintOccupancy.DefaultReservedEnvelopeBytes"/> (128 KiB), guaranteeing that every admitted
    /// exposure fits strictly within <see cref="StructuredProcessingProductDescriptorV1.MaximumPayloadBytes"/> (4 MiB)
    /// for up to <see cref="StellarDetector.MaximumSupportedCandidates"/> detections/exclusions with positive coarse
    /// occupancy masks even under worst-case JSON escaping.
    /// </summary>
    public const int MaximumAdmittedEnvelopeBytes = StellarFootprintOccupancy.DefaultReservedEnvelopeBytes;

    private const int MaximumSourcesHeaderAndHfdBytes = 8_192;

    private static readonly ExposureSourcesV1 EmptySourcesShell = new(
        ExposureMeasurementStatus.Empty, "shell", 0, 0, null, null, [], []);

    private static readonly ExposureHfdV1 EmptyHfdShell = new(
        ExposureMeasurementStatus.Unavailable, "shell", null, null, null, 0, null, []);

    // Worst-case brightness and clipping shells dominate every real runtime measurement (19-digit SampleCount,
    // 20-digit Sum, non-terminating 18-char Mean, and simultaneous numeric + max-length reason fields on Clipping),
    // so Serialize(realShell).Length is strictly bounded above by Serialize(worstCaseShell).Length.
    private static readonly ExposureClippingV1 WorstCaseClippingShell = new(
        ushort.MaxValue, long.MaxValue, FrameWhiteThreshold.UnavailableReason);

    private static readonly ExposureBrightnessV1 WorstCaseRawBrightnessShell = new(
        9_000_000_000_000_000_007L, ushort.MaxValue, ushort.MaxValue, 10_000_000_000_000_000_009UL,
        long.MaxValue, WorstCaseClippingShell);

    internal static int WorstCaseEnvelopeBytes(ExposureMeasurementInputV1 input)
    {
        var shell = new ExposureMeasurementV1(
            SchemaVersion, input, WorstCaseRawBrightnessShell, null,
            input.Linear is null ? null : WorstCaseClippingShell,
            EmptySourcesShell, EmptyHfdShell);
        return Serialize(shell).Length + MaximumSourcesHeaderAndHfdBytes;
    }

    internal static int ReservedEnvelopeBytes(
        ExposureMeasurementInputV1 input,
        ExposureBrightnessV1? rawBrightness,
        string? rawBrightnessUnavailableReason,
        ExposureClippingV1? linearOutputClipping)
    {
        var shell = new ExposureMeasurementV1(
            SchemaVersion, input, rawBrightness, rawBrightnessUnavailableReason, linearOutputClipping,
            EmptySourcesShell, EmptyHfdShell);
        var measured = Serialize(shell).Length + MaximumSourcesHeaderAndHfdBytes;
        return Math.Max(StellarFootprintOccupancy.DefaultReservedEnvelopeBytes, measured);
    }

    internal static byte[] Serialize(ExposureMeasurementV1 value)
        => JsonSerializer.SerializeToUtf8Bytes(
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, Json)));

    internal static ExposureMeasurementV1 Read(FrameArtifactRole role, string variant, ProcessingProductKind kind,
        string mediaType, string? schema, ReadOnlyMemory<byte> payload, string? contentIdentity,
        ProcessingRecipeIdentity recipe, IReadOnlyList<Guid> sourceIds)
    {
        if (role != FrameArtifactRole.Metadata || kind != ProcessingProductKind.Metadata ||
            mediaType != MediaType || schema != SchemaVersion || variant != Variant ||
            recipe.Descriptor is not { Name: BuiltInProcessingRecipes.ExposureMeasurement, SemanticVersion: "1.0.0", ImplementationVersion: SchemaVersion } ||
            payload.Length is < 1 or > StructuredProcessingProductDescriptorV1.MaximumPayloadBytes ||
            !Same(contentIdentity, ProcessingIdentity.ComputePayloadSha256(payload)))
            throw Invalid("envelope");

        try
        {
            var value = JsonSerializer.Deserialize<ExposureMeasurementV1>(payload.Span, Json) ?? throw Invalid("payload");
            var options = recipe.Descriptor.Options;
            if (!options.TryGetProperty("parameters", out var parameters) || value.SchemaVersion != SchemaVersion ||
                value.CoordinateConvention != CoordinateConvention || value.HfdUnit != "delivered-image-pixels" ||
                !payload.Span.SequenceEqual(Serialize(value)))
                throw Invalid("canonical payload");
            var (input, policy) = ReadParameters(parameters);
            VerifyBinding(value, input);
            if (!sourceIds.SequenceEqual(SourceIds(value.Input)))
                throw Invalid("lineage");
            ValidateResults(value, policy);
            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid exposure measurement payload.", exception);
        }
    }

    internal static Guid[] SourceIds(ExposureMeasurementInputV1 input)
        => input.Linear is { } linear ? [input.Raw.ArtifactId, linear.ArtifactId] : [input.Raw.ArtifactId];

    internal static void ValidateInput(ExposureMeasurementInputV1 input)
    {
        if (input is null || !ValidSource(input.Raw) || input.Raw.OutputIdentitySha256 is not null ||
            string.IsNullOrWhiteSpace(input.OrientationIdentity) || input.OrientationIdentity.Length > 256 ||
            !IsValidUtf16(input.OrientationIdentity) ||
            input.NativePixelPitchMicrometers is { } pitch && (!double.IsFinite(pitch) || pitch <= 0) ||
            input.PixelScaleArcseconds is { } scale && (!double.IsFinite(scale) || scale <= 0) ||
            input.RigOrientation is { } orient && (!double.IsFinite(orient.BoresightAltitudeDegrees) ||
                !double.IsFinite(orient.BoresightAzimuthDegrees) || !double.IsFinite(orient.RollAdjustmentDegrees)) ||
            !Enum.IsDefined(input.Calibration) ||
            input.CalibrationReason is { } reason && (string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || !IsValidUtf16(reason)) ||
            !IsValidUtf16(input.OpticalCalibrationVersion))
            throw Invalid("input");

        if (input.Exposure is { } exposure && (exposure.CaptureId != input.Raw.CaptureId ||
                exposure.RunId == Guid.Empty || exposure.Camera is not { } camera ||
                string.IsNullOrWhiteSpace(camera.CameraId) || !IsValidUtf16(camera.CameraId) ||
                string.IsNullOrWhiteSpace(camera.AgentId) || !IsValidUtf16(camera.AgentId) ||
                input.RigOrientation is null || string.IsNullOrWhiteSpace(input.OpticalCalibrationVersion) ||
                input.Step3Disposition is not { } step3 || !Enum.IsDefined(step3) || step3 == LinearExposureDisposition.Pending ||
                input.Step3Revision is <= 0 || input.Step3Revision is null &&
                (step3 != LinearExposureDisposition.Unavailable || input.CalibrationReason != "linear.no-configured-step")))
            throw Invalid("admitted exposure");

        if (input.ApertureProjection is { } aperture)
        {
            if (aperture.WidthPixels != input.Raw.Layout.Width || aperture.HeightPixels != input.Raw.Layout.Height)
                throw Invalid("aperture geometry");
            aperture.Validate();
        }

        var qualified = input.Calibration is ExposureCalibrationDisposition.ReferenceCalibrated or ExposureCalibrationDisposition.NormalizationOnly;
        if (qualified != (input.Linear is not null) || !qualified && input.CalibrationReason is null ||
            input.Linear is { } linear && (!ValidSource(linear) || !IsSha256(linear.OutputIdentitySha256) ||
                ProcessingIdentity.CreateArtifactId(linear.OutputIdentitySha256!) != linear.ArtifactId ||
                linear.ArtifactId == input.Raw.ArtifactId || linear.CaptureId != input.Raw.CaptureId))
            throw Invalid("linear disposition");
    }

    private static long RawMetadataCharCount(ExposureMeasurementInputV1 input)
        => (long)(input.OpticalCalibrationVersion?.Length ?? 0) +
           (input.Exposure?.Camera?.AgentId?.Length ?? 0) +
           (input.Exposure?.Camera?.CameraId?.Length ?? 0);

    private static void ValidateFreshAdmissionEnvelopeBudget(ExposureMeasurementInputV1 input)
    {
        // O(1) lower-bound rejection before canonical JSON measurement so arbitrarily large metadata cannot allocate
        // huge buffers, followed by exact canonical UTF-8 byte accounting (including 6-byte \uXXXX JSON escaping).
        if (RawMetadataCharCount(input) > MaximumAdmittedEnvelopeBytes ||
            WorstCaseEnvelopeBytes(input) > MaximumAdmittedEnvelopeBytes)
            throw Invalid("input envelope budget");
    }

    private static bool IsValidUtf16(string? value)
    {
        if (value is null)
            return true;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (!char.IsSurrogate(ch))
                continue;
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool ValidSource(ExposureMeasurementSourceV1? source)
        => source is { CaptureId: var captureId, ArtifactId: var artifactId, Layout: { } layout } &&
           captureId != Guid.Empty && artifactId != Guid.Empty && captureId != artifactId &&
           IsSha256(source.DescriptorSha256) && IsSha256(source.PayloadSha256) &&
           IsSha256(source.RecipeSha256) && layout.Validate().IsValid;

    internal static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static InvalidDataException Invalid(string field) => new($"Invalid exposure measurement {field}.");

    private static void ValidateResults(ExposureMeasurementV1 value, ExposureFocusSamplingPolicy policy)
    {
        var sources = value.Sources;
        var hfd = value.Hfd;
        if (sources is null || hfd is null || !ValidStatus(sources.Status, sources.Reason) ||
            !ValidStatus(hfd.Status, hfd.Reason) || sources.Detections is null || sources.Exclusions is null ||
            (long)sources.Detections.Count + sources.Exclusions.Count > StellarDetector.MaximumSupportedCandidates ||
            hfd.Samples is null || hfd.Samples.Count > MaximumHfdSamples ||
            (value.RawBrightness is null) != !string.IsNullOrWhiteSpace(value.RawBrightnessUnavailableReason) ||
            value.RawBrightnessUnavailableReason is { Length: > 128 } ||
            (value.LinearOutputClipping is null) != (value.Input.Linear is null))
            throw Invalid("section");
        if (value.RawBrightness is { } brightness)
        {
            var layout = value.Input.Raw.Layout;
            var count = SampleCount(layout);
            if (brightness.SampleCount != count || brightness.Minimum > brightness.Maximum ||
                brightness.Sum < (ulong)count * brightness.Minimum || brightness.Sum > (ulong)count * brightness.Maximum ||
                brightness.ZeroCount < 0 || brightness.ZeroCount > count ||
                (brightness.Minimum == 0) != (brightness.ZeroCount > 0) ||
                !ValidClipping(brightness.SensorClipping, layout, count) ||
                brightness.SensorClipping.StoredWhite is { } white &&
                ((brightness.Maximum >= white) != (brightness.SensorClipping.Count > 0) ||
                    brightness.Minimum >= white && brightness.SensorClipping.Count != count))
                throw Invalid("brightness");
        }
        if (value.Input.Linear is { Layout: var linearLayout } &&
            !ValidClipping(value.LinearOutputClipping, linearLayout, SampleCount(linearLayout)))
            throw Invalid("linear clipping");

        var measuredLayout = value.Input.Linear?.Layout;
        if ((sources.Status == ExposureMeasurementStatus.Usable) != (sources.Detections.Count > 0) ||
            measuredLayout is null && sources.Status != ExposureMeasurementStatus.Unavailable ||
            sources.Status == ExposureMeasurementStatus.Empty && (sources.CandidateCount != 0 || sources.Exclusions.Count != 0) ||
            sources.Status == ExposureMeasurementStatus.Unavailable && (sources.CandidateCount is not null || sources.Exclusions.Count != 0) ||
            sources.Status != ExposureMeasurementStatus.Unavailable && sources.CandidateCount is null ||
            sources.CandidateCount is < 0 || sources.AvailableBackgroundTiles is < 0 ||
            sources.CandidateCount < sources.Detections.Count + sources.Exclusions.Count ||
            measuredLayout is not null && sources.Status != ExposureMeasurementStatus.Unavailable &&
                sources.LocalBayerPattern != ExposureMeasurementRecipe.LocalPattern(measuredLayout) ||
            sources.Status is ExposureMeasurementStatus.Usable or ExposureMeasurementStatus.Empty &&
                (sources.AvailableBackgroundTiles is null or 0 || !IsSha256(sources.SettingsSha256)))
            throw Invalid("sources");

        for (var index = 0; index < sources.Detections.Count; index++)
        {
            var item = sources.Detections[index];
            if (item is null || item.Index != index || !Inside(item.Pixel, measuredLayout!) ||
                !double.IsFinite(item.Flux) || item.Flux <= 0 || item.SaturatedSampleCount < 0 ||
                item.Conditions.HasFlag(StellarSourceConditions.Saturated) != (item.SaturatedSampleCount > 0) ||
                item.SignalToNoise is { } snr && (!double.IsFinite(snr) || snr < 0))
                throw Invalid("detection");
        }
        var allowOccupancy = policy == ExposureFocusSamplingPolicy.EligibleFirstWithOccupancy;
        var occupancyBudget = default(StellarFootprintOccupancyBudget);
        if (allowOccupancy && measuredLayout is not null)
        {
            var nonSolidFootprints = new List<StellarFootprintBounds>();
            foreach (var item in sources.Exclusions)
            {
                if (item?.Footprint is { } fp &&
                    fp.MinX >= 0 && fp.MinY >= 0 && fp.MaxX >= fp.MinX && fp.MaxY >= fp.MinY &&
                    fp.MaxX < measuredLayout.Width && fp.MaxY < measuredLayout.Height)
                {
                    var fpWidth = fp.MaxX - fp.MinX + 1;
                    var fpHeight = fp.MaxY - fp.MinY + 1;
                    var fpArea = (long)fpWidth * fpHeight;
                    if (item.SampleCount > 0 && item.SampleCount < fpArea)
                        nonSolidFootprints.Add(fp);
                }
            }
            var measuredReservedEnvelopeBytes = ReservedEnvelopeBytes(
                value.Input, value.RawBrightness, value.RawBrightnessUnavailableReason, value.LinearOutputClipping);
            occupancyBudget = StellarFootprintOccupancy.ComputeProductBudgets(
                sources.Detections.Count,
                sources.Exclusions.Count,
                CollectionsMarshal.AsSpan(nonSolidFootprints),
                measuredReservedEnvelopeBytes);
        }
        if (sources.Exclusions.Any(item => !IsValidExclusion(item, measuredLayout, allowOccupancy, occupancyBudget)))
            throw Invalid("exclusion");

        ValidateFocus(sources, hfd, measuredLayout, policy);
    }

    private static bool IsValidExclusion(
        StellarMeasurementExclusion? item,
        FrameLayoutDescriptor? measuredLayout,
        bool allowOccupancy,
        StellarFootprintOccupancyBudget occupancyBudget)
    {
        if (item is null || measuredLayout is null || !Inside(item.Peak, measuredLayout) ||
            !StellarExclusionReasons.All.Contains(item.ReasonCode) || item.SampleCount <= 0)
            return false;
        if (item.Footprint is { } fp)
        {
            if (fp.MinX < 0 || fp.MinY < 0 || fp.MaxX < fp.MinX || fp.MaxY < fp.MinY ||
                fp.MaxX >= measuredLayout.Width || fp.MaxY >= measuredLayout.Height)
                return false;
            var fpArea = ((long)fp.MaxX - fp.MinX + 1) * ((long)fp.MaxY - fp.MinY + 1);
            if (item.SampleCount > fpArea)
                return false;
            var peakX = Math.Floor(item.Peak.X);
            var peakY = Math.Floor(item.Peak.Y);
            if (peakX < fp.MinX || peakX > fp.MaxX || peakY < fp.MinY || peakY > fp.MaxY)
                return false;
            if (allowOccupancy)
            {
                if (item.SampleCount == fpArea || occupancyBudget.CoarseCellBudget <= 0)
                    return item.Occupancy is null;
                return item.Occupancy is not null &&
                    item.Occupancy.IsValid(
                        fp, item.Peak, item.SampleCount,
                        occupancyBudget.CoarseCellBudget, occupancyBudget.SubgridBitBudget);
            }
        }
        else if (allowOccupancy)
        {
            return false;
        }
        return item.Occupancy is null;
    }

    private static void ValidateFocus(ExposureSourcesV1 sources, ExposureHfdV1 hfd, FrameLayoutDescriptor? layout,
        ExposureFocusSamplingPolicy policy)
    {
        if (ExposureMeasurementRecipe.FocusUnavailableReason(sources, layout) is { } unavailable)
        {
            if (hfd.Status != ExposureMeasurementStatus.Unavailable || hfd.Reason != unavailable ||
                hfd.ValidCount is not null || hfd.MeanPixels is not null || hfd.MedianPixels is not null ||
                hfd.SettingsSha256 is not null || hfd.OmittedCount != 0 || hfd.Samples.Count != 0)
                throw Invalid("HFD unavailable disposition");
            return;
        }

        var sampleCount = Math.Min(sources.Detections.Count, MaximumHfdSamples);
        var omitted = sources.Detections.Count - sampleCount;
        if (hfd.Samples.Count != sampleCount || hfd.OmittedCount != omitted ||
            (sampleCount == 0 ? hfd.SettingsSha256 is not null : !IsSha256(hfd.SettingsSha256)))
            throw Invalid("HFD accounting");

        var eligibleFirst = policy is ExposureFocusSamplingPolicy.EligibleFirst or ExposureFocusSamplingPolicy.EligibleFirstWithOccupancy;
        var bayerCoreTrail = policy != ExposureFocusSamplingPolicy.Brightest && layout?.PixelFormat == CameraPixelFormat.BayerRggb16;
        var valid = new List<double>(sampleCount);
        var lastIndex = -1;
        var lastRejectedIndex = -1;
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = hfd.Samples[index];
            if (sample is null || sample.DetectionIndex <= lastIndex || sample.DetectionIndex >= sources.Detections.Count ||
                !eligibleFirst && sample.DetectionIndex != index || string.IsNullOrWhiteSpace(sample.Reason) ||
                (sample.DiameterPixels is not null) != (sample.Reason == FocusStarReasonCodes.Valid))
                throw Invalid("HFD sample");
            lastIndex = sample.DetectionIndex;
            var detection = sources.Detections[lastIndex];
            var rejection = ExposureMeasurementRecipe.FocusDetectionRejection(detection.Conditions);
            if (eligibleFirst && ExposureMeasurementRecipe.FocusEligibility(detection, layout!, bayerCoreTrail).Rejection is { } fixedRejection)
            {
                if (sample.Reason != fixedRejection)
                    throw Invalid("HFD sample");
                lastRejectedIndex = lastIndex;
            }
            if (sample.BayerCoreTrailUpperPixels is { } upper)
            {
                if (!bayerCoreTrail || rejection != ExposureMeasurementRecipe.TrailedSource ||
                    sample.Reason != FocusStarReasonCodes.Valid || !ExposureMeasurementRecipe.IsRoundBayerCore(upper))
                    throw Invalid("Bayer focus shape evidence");
            }
            else if (rejection is not null && sample.Reason != rejection)
            {
                throw Invalid("HFD sample");
            }
            if (sample.DiameterPixels is { } diameter)
            {
                if (!double.IsFinite(diameter) || diameter <= 0)
                    throw Invalid("HFD diameter");
                valid.Add(diameter);
            }
        }
        if (eligibleFirst && omitted > 0)
            ValidateFocusAllocation(sources.Detections, hfd.Samples, layout!, bayerCoreTrail, lastIndex, lastRejectedIndex);
        var (status, reason) = ExposureMeasurementRecipe.FocusDisposition(sources, valid.Count, omitted);
        if (hfd.Status != status || hfd.Reason != reason || hfd.ValidCount != valid.Count ||
            hfd.MeanPixels != (valid.Count == 0 ? null : valid.Average()) ||
            hfd.MedianPixels != Median(valid))
            throw Invalid("HFD summary");
    }

    private static void ValidateFocusAllocation(IReadOnlyList<StellarDetection> detections,
        IReadOnlyList<ExposureHfdSampleV1> samples, FrameLayoutDescriptor layout, bool bayerCoreTrail,
        int lastSelected, int lastRejected)
    {
        var selected = 0;
        for (var index = 0; index < detections.Count && (index < lastSelected || lastRejected >= 0); index++)
        {
            if (selected < samples.Count && samples[selected].DetectionIndex == index)
            {
                selected++;
                continue;
            }
            // An omitted source with definite eligibility outranks every fallback and any dimmer source.
            // A Bayer trail flag needs pixel evidence, which the retained record only carries for valid samples.
            if (index < lastRejected || ExposureMeasurementRecipe.FocusEligibility(detections[index], layout, bayerCoreTrail) is (null, false))
                throw Invalid("HFD allocation");
        }
    }

    internal static double? Median(List<double> samples)
    {
        if (samples.Count == 0)
            return null;
        samples.Sort();
        return (samples[(samples.Count - 1) / 2] + samples[samples.Count / 2]) / 2;
    }

    private static bool ValidStatus(ExposureMeasurementStatus status, string? reason)
        => Enum.IsDefined(status) && (status == ExposureMeasurementStatus.Usable || !string.IsNullOrWhiteSpace(reason));

    private static long SampleCount(FrameLayoutDescriptor layout)
        => (long)layout.Width * layout.Height * ChannelCount(layout.PixelFormat);

    private static int ChannelCount(CameraPixelFormat pixelFormat) => pixelFormat switch
    {
        CameraPixelFormat.Rgb24 => 3,
        CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 => 1,
        _ => throw Invalid("pixel format")
    };

    private static bool ValidClipping(ExposureClippingV1? clipping, FrameLayoutDescriptor layout, long count)
    {
        var expected = ExposureSampleCodes.Clipping(layout);
        return clipping is not null && clipping.StoredWhite == expected.StoredWhite && clipping.Reason == expected.Reason &&
            (clipping.StoredWhite is null ? clipping.Count is null : clipping.Count >= 0 && clipping.Count <= count);
    }

    private static bool Inside(PixelPoint point, FrameLayoutDescriptor layout)
        => point.X >= 0 && point.X < layout.Width && point.Y >= 0 && point.Y < layout.Height;
}
