using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Stable reasons why a normalization plan is rejected.</summary>
public static class NormalizationPlanReasonCodes
{
    public const string InvalidContract = "normalization.plan.invalid-contract";
    public const string UnsupportedSchema = "normalization.plan.unsupported-schema";
    public const string NonCanonicalJson = "normalization.plan.non-canonical-json";
}

/// <summary>Whether a window input contributes to the normalized mean.</summary>
public static class NormalizationPlanInputDispositions
{
    public const string Selected = "selected";
    public const string Excluded = "excluded";
}

/// <summary>
/// The arithmetic a plan is reproduced with: the kernel version and the precision, rounding, reduction order,
/// saturation, no-coverage and output-encoding rules it applies. <see cref="SaturationCode"/> is the lowest stored
/// light code treated as saturated, before any correction. <see cref="OutputScale"/> multiplies the reference-gain
/// code before the single output rounding, so one output code is <c>1/OutputScale</c> of a reference-gain code.
/// </summary>
public sealed record NormalizationKernelV1(
    [property: JsonRequired] string AlgorithmVersion,
    [property: JsonRequired] string FlatRepresentation,
    [property: JsonRequired] string Precision,
    [property: JsonRequired] string Rounding,
    [property: JsonRequired] string ReductionOrder,
    [property: JsonRequired] string SaturationPolicy,
    [property: JsonRequired] int SaturationCode,
    [property: JsonRequired] string NoCoveragePolicy,
    [property: JsonRequired] string OutputEncoding,
    [property: JsonRequired] int OutputScale)
{
    public const string SignedElectronsE12Int128V1 = "signed-electrons-e12-int128-v1";
    public const string HalfEvenPerSampleAndOutputV1 = "half-even-per-sample-and-output-v1";
    public const string ExactIntegerSumInOrdinalOrderV1 = "exact-integer-sum-in-ordinal-order-v1";
    public const string NativeFullScaleStoredCodeV1 = "native-full-scale-stored-code-v1";
    public const string ZeroCodeWithMaskV1 = "zero-code-with-mask-v1";
    public const string Linear16ReferenceCoefficientV1 = "linear16-reference-coefficient-v1";

    /// <summary>The fixed output scale of the mixed-gain recipe: sixteen output codes per reference-gain code.</summary>
    public const int ReferenceGainOutputScale = 16;

    /// <summary>
    /// The <see cref="Linear16GainNormalizedMean"/> kernel for frames with this response's code mapping. A stored code
    /// at or above the native full-scale sample is saturated: <c>2^n − 1</c> for right-aligned codes and 65535 for
    /// identity and full-range-scaled codes.
    /// </summary>
    public static NormalizationKernelV1 Linear16(GainResponseDescriptorV1 response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(
            Linear16GainNormalizedMean.AlgorithmVersion,
            Linear16GainNormalizedMean.FlatRepresentationVersion,
            SignedElectronsE12Int128V1,
            HalfEvenPerSampleAndOutputV1,
            ExactIntegerSumInOrdinalOrderV1,
            NativeFullScaleStoredCodeV1,
            response.StoredCodeTransform == FrameStoredCodeTransform.RightAlignedV1
                ? (1 << response.NativeSampleDepthBits) - 1
                : ushort.MaxValue,
            ZeroCodeWithMaskV1,
            Linear16ReferenceCoefficientV1,
            ReferenceGainOutputScale);
    }
}

/// <summary>
/// One input of the frozen window, in window order: its frozen identities and hashes, its actual capture conditions,
/// and either the family member, masters and coefficient it is normalized with, or the stable reason it is excluded.
/// <see cref="Masters"/> are the bias, dark, flat and defect masters of the per-input selection, in that order.
/// </summary>
public sealed record NormalizationPlanInputV1(
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] int WindowPosition,
    [property: JsonRequired] Guid CaptureId,
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] string DescriptorSha256,
    [property: JsonRequired] string PayloadSha256,
    [property: JsonRequired] double Gain,
    [property: JsonRequired] TimeSpan Exposure,
    [property: JsonRequired] double? Offset,
    [property: JsonRequired] double? TemperatureC,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] string? ExclusionReason,
    [property: JsonRequired] string? MemberId,
    [property: JsonRequired] IReadOnlyList<CalibrationFamilyMasterV1>? Masters,
    [property: JsonRequired] GainCoefficientV1? Coefficient);

/// <summary>
/// The frozen normalization plan of one mixed-gain stack window (#1475). With the frozen input and master payloads
/// it names, it reproduces the output byte for byte without consulting live profiles, masters or gain control: the
/// common calibration family, the policy (reference gain, ratio bound and weights), the gain response and its content
/// identity, the resolved reference and per-input fixed-point coefficients, the input layout that fixes the decode
/// transform, the kernel rules, and every input's selection or exclusion.
/// </summary>
public sealed record NormalizationPlanV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string CalibrationFamilyIdentitySha256,
    [property: JsonRequired] GainNormalizationPolicyV1 Policy,
    [property: JsonRequired] GainResponseDescriptorV1 Response,
    [property: JsonRequired] string ResponseIdentitySha256,
    [property: JsonRequired] GainCoefficientV1 ReferenceCoefficient,
    [property: JsonRequired] FrameLayoutDescriptor InputLayout,
    [property: JsonRequired] NormalizationKernelV1 Kernel,
    [property: JsonRequired] IReadOnlyList<NormalizationPlanInputV1> Inputs)
{
    public const string CurrentSchemaVersion = "hvo-normalization-plan-v1";

    /// <summary>The largest live or replay window (<c>MaximumWindowInputs</c> is at most 128).</summary>
    public const int MaximumInputs = 128;
}

/// <summary>Validation rules of the normalization-plan contract.</summary>
public static class NormalizationPlanContract
{
    private const int MaximumTokenLength = 128;

    public static CaptureContractValidationResult Validate(NormalizationPlanV1? plan)
    {
        if (plan is null ||
            !string.Equals(plan.SchemaVersion, NormalizationPlanV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure("schemaVersion");
        }
        if (!Sha256(plan.CalibrationFamilyIdentitySha256))
        {
            return Failure("calibrationFamilyIdentitySha256");
        }
        if (!GainNormalizationContract.Validate(plan.Policy).IsValid)
        {
            return Failure("policy");
        }
        if (!GainNormalizationContract.Validate(plan.Response).IsValid ||
            !plan.Policy.PermittedResponseProvenance.Contains(plan.Response.Provenance, StringComparer.Ordinal))
        {
            return Failure("response");
        }
        if (!Sha256(plan.ResponseIdentitySha256) || !string.Equals(
                plan.ResponseIdentitySha256,
                GainNormalizationContractJson.ComputeIdentitySha256(plan.Response),
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure("responseIdentitySha256");
        }
        if (!ValidCoefficient(plan.ReferenceCoefficient, plan.ResponseIdentitySha256, plan.Policy.ReferenceGainControl))
        {
            return Failure("referenceCoefficient");
        }
        if (!SupportedInputLayout(plan.InputLayout, plan.Response))
        {
            return Failure("inputLayout");
        }
        if (plan.Kernel is null || plan.Kernel != NormalizationKernelV1.Linear16(plan.Response))
        {
            return Failure("kernel");
        }
        if (plan.Inputs is not { Count: > 0 and <= NormalizationPlanV1.MaximumInputs } inputs ||
            inputs.Any(static input => input is null))
        {
            return Failure("inputs");
        }
        for (var ordinal = 0; ordinal < inputs.Count; ordinal++)
        {
            var validation = ValidateInput(inputs[ordinal], ordinal, plan.ResponseIdentitySha256);
            if (!validation.IsValid)
            {
                return validation;
            }
        }
        if (inputs.Select(static input => input.WindowPosition).Distinct().Count() != inputs.Count ||
            inputs.Select(static input => input.CaptureId).Distinct().Count() != inputs.Count ||
            inputs.Select(static input => input.ArtifactId).Distinct().Count() != inputs.Count)
        {
            return Failure("inputs");
        }
        var coefficients = inputs
            .Where(static input => input.Disposition == NormalizationPlanInputDispositions.Selected)
            .Select(static input => input.Coefficient!)
            .ToArray();
        if (coefficients.Length == 0)
        {
            return Failure("inputs.disposition");
        }
        return GainNormalizationContract.EvaluateCoefficientBound(plan.Policy, coefficients) is null
            ? CaptureContractValidationResult.Success
            : Failure("inputs.coefficient");
    }

    private static CaptureContractValidationResult ValidateInput(
        NormalizationPlanInputV1 input,
        int ordinal,
        string responseIdentitySha256)
    {
        if (input.Ordinal != ordinal)
        {
            return Failure("inputs.ordinal");
        }
        if (input.CaptureId == Guid.Empty || input.ArtifactId == Guid.Empty ||
            !Sha256(input.DescriptorSha256) || !Sha256(input.PayloadSha256))
        {
            return Failure("inputs.identity");
        }
        if (!double.IsFinite(input.Gain) || input.Exposure <= TimeSpan.Zero ||
            input.Offset is { } offset && !double.IsFinite(offset) ||
            input.TemperatureC is { } temperature && !double.IsFinite(temperature))
        {
            return Failure("inputs.conditions");
        }
        switch (input.Disposition)
        {
            case NormalizationPlanInputDispositions.Selected:
                if (input.ExclusionReason is not null || !Token(input.MemberId) ||
                    input.Masters is not { Count: 4 } masters || masters.Any(static master => master is null) ||
                    !masters.Select(static master => master.Kind).SequenceEqual(MasterKindOrder) ||
                    masters.Any(static master => master.ArtifactId == Guid.Empty || !Sha256(master.PayloadSha256)))
                {
                    return Failure("inputs.selection");
                }
                return ValidCoefficient(input.Coefficient, responseIdentitySha256, input.Gain)
                    ? CaptureContractValidationResult.Success
                    : Failure("inputs.coefficient");
            case NormalizationPlanInputDispositions.Excluded:
                return ReasonCode(input.ExclusionReason) && input.MemberId is null && input.Masters is null &&
                       input.Coefficient is null
                    ? CaptureContractValidationResult.Success
                    : Failure("inputs.exclusionReason");
            default:
                return Failure("inputs.disposition");
        }
    }

    /// <summary>The bias, dark, flat and defect order of a per-input selection.</summary>
    internal static IReadOnlyList<string> MasterKindOrder { get; } =
    [
        CalibrationReferenceKinds.Bias,
        CalibrationReferenceKinds.Dark,
        CalibrationReferenceKinds.Flat,
        CalibrationReferenceKinds.Defect
    ];

    private static bool ValidCoefficient(GainCoefficientV1? coefficient, string responseIdentitySha256, double gain)
        => coefficient is not null &&
           string.Equals(coefficient.ResponseIdentitySha256, responseIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
           coefficient.GainControl == gain &&
           coefficient.ConversionRegime is GainConversionRegimes.FixedConversionGain or
               GainConversionRegimes.LowConversionGain or GainConversionRegimes.HighConversionGain &&
           ElectronsPerAduFixedPoint.IsValid(coefficient.ElectronsPerAduE12);

    /// <summary>
    /// The complete little-endian, byte-aligned 16-bit Mono16 or RGGB readout whose stored-code transform and sample
    /// depth are the response's.
    /// </summary>
    private static bool SupportedInputLayout(FrameLayoutDescriptor? layout, GainResponseDescriptorV1 response)
        => layout is not null && layout.Validate().IsValid &&
           layout.Readout is not null && layout.LevelCodeSpace is not null &&
           layout.StoredCodeTransform == response.StoredCodeTransform &&
           layout.SampleDepthBits == response.NativeSampleDepthBits &&
           layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 &&
           layout.ByteOrder == FrameByteOrder.LittleEndian &&
           layout.ContainerDepthBits == response.ContainerDepthBits &&
           layout.Packing == FrameSamplePacking.ByteAligned;

    /// <summary>A member ID is a non-blank token of at most 128 UTF-16 units, as the family contract requires.</summary>
    private static bool Token(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumTokenLength;

    /// <summary>A reason code is a stable lowercase ASCII code such as <c>calibration.family.master-corrupt</c>.</summary>
    private static bool ReasonCode(string? value)
        => value is { Length: > 0 and <= MaximumTokenLength } &&
           value.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    private static bool Sha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static CaptureContractValidationResult Failure(string path)
        => CaptureContractValidationResult.Failure(NormalizationPlanReasonCodes.InvalidContract, path);
}

/// <summary>
/// Canonical UTF-8 JSON and SHA-256 identity of a normalization plan. Parsing accepts only the exact canonical bytes,
/// so a frozen plan reproduces byte for byte.
/// </summary>
public static class NormalizationPlanContractJson
{
    /// <summary>
    /// Bounds both parsing and serialization. The largest valid plan, 128 selected inputs whose member IDs escape every
    /// character to six bytes, is under 320 KiB, so every valid plan round-trips and several fit one frozen execution.
    /// </summary>
    public const int MaximumContractBytes = 512 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(NormalizationPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validation = NormalizationPlanContract.Validate(plan);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Normalization plan is invalid at '{validation.FieldPath}' ({validation.ReasonCode}).",
                nameof(plan));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(Normalize(plan), SerializerOptions)));
        return bytes.Length <= MaximumContractBytes
            ? bytes
            : throw new InvalidOperationException("The canonical normalization plan exceeds the contract bound.");
    }

    public static string ComputeIdentitySha256(NormalizationPlanV1 plan)
        => Convert.ToHexString(SHA256.HashData(Serialize(plan)));

    public static (NormalizationPlanV1? Value, CaptureContractValidationResult Validation) Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumContractBytes)
        {
            return (null, CaptureContractValidationResult.Failure(NormalizationPlanReasonCodes.InvalidContract, "$"));
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                return (null, CaptureContractValidationResult.Failure(NormalizationPlanReasonCodes.InvalidContract, "$"));
            }
            if (!string.Equals(schema.GetString(), NormalizationPlanV1.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return (null, CaptureContractValidationResult.Failure(
                    NormalizationPlanReasonCodes.UnsupportedSchema,
                    "schemaVersion"));
            }
            var value = JsonSerializer.Deserialize<NormalizationPlanV1>(utf8Json.Span, SerializerOptions);
            var validation = NormalizationPlanContract.Validate(value);
            if (!validation.IsValid)
            {
                return (null, validation);
            }
            return Serialize(value!).AsSpan().SequenceEqual(utf8Json.Span)
                ? (value, validation)
                : (null, CaptureContractValidationResult.Failure(NormalizationPlanReasonCodes.NonCanonicalJson, "$"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return (null, CaptureContractValidationResult.Failure(NormalizationPlanReasonCodes.InvalidContract, "$"));
        }
    }

    /// <summary>Uppercases hashes and writes negative zero as zero; inputs keep their validated window order.</summary>
    private static NormalizationPlanV1 Normalize(NormalizationPlanV1 plan)
        => plan with
        {
            CalibrationFamilyIdentitySha256 = plan.CalibrationFamilyIdentitySha256.ToUpperInvariant(),
            Policy = GainNormalizationContractJson.Normalize(plan.Policy),
            Response = GainNormalizationContractJson.Normalize(plan.Response),
            ResponseIdentitySha256 = plan.ResponseIdentitySha256.ToUpperInvariant(),
            ReferenceCoefficient = Normalize(plan.ReferenceCoefficient),
            InputLayout = plan.InputLayout with
            {
                BlackLevel = plan.InputLayout.BlackLevel + 0.0,
                WhiteLevel = plan.InputLayout.WhiteLevel + 0.0
            },
            Inputs = [.. plan.Inputs.Select(static input => input with
            {
                DescriptorSha256 = input.DescriptorSha256.ToUpperInvariant(),
                PayloadSha256 = input.PayloadSha256.ToUpperInvariant(),
                Gain = input.Gain + 0.0,
                Offset = input.Offset + 0.0,
                TemperatureC = input.TemperatureC + 0.0,
                Masters = input.Masters is null
                    ? null
                    : [.. input.Masters.Select(static master => master with
                    {
                        PayloadSha256 = master.PayloadSha256.ToUpperInvariant()
                    })],
                Coefficient = input.Coefficient is null ? null : Normalize(input.Coefficient)
            })]
        };

    private static GainCoefficientV1 Normalize(GainCoefficientV1 coefficient)
        => coefficient with
        {
            ResponseIdentitySha256 = coefficient.ResponseIdentitySha256.ToUpperInvariant(),
            GainControl = coefficient.GainControl + 0.0
        };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
