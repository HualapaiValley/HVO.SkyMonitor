using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Stable reasons why mixed-gain normalization is unavailable or a coefficient set is not admitted.</summary>
public static class GainNormalizationReasonCodes
{
    public const string PhysicalResponseNotEnabled = "gain.normalization.physical-response-not-enabled";
    public const string UnsupportedResponseSource = "gain.normalization.unsupported-response-source";
    public const string MissingResponse = "gain.normalization.missing-response";
    public const string InvalidResponse = "gain.normalization.invalid-response";
    public const string UnsupportedCodeMapping = "gain.normalization.unsupported-code-mapping";
    public const string GainOutOfRange = "gain.normalization.gain-out-of-range";
    public const string InvalidCoefficient = "gain.normalization.invalid-coefficient";
    public const string ProvenanceNotPermitted = "gain.normalization.provenance-not-permitted";
    public const string ReferenceGainOutOfRange = "gain.normalization.reference-gain-out-of-range";
    public const string MixedResponse = "gain.normalization.mixed-response";
    public const string MixedConversionRegime = "gain.normalization.mixed-conversion-regime";
    public const string CoefficientRatioExceeded = "gain.normalization.coefficient-ratio-exceeded";
    public const string InvalidContract = "gain.normalization.invalid-contract";
    public const string UnsupportedSchema = "gain.normalization.unsupported-schema";
    public const string NonCanonicalJson = "gain.normalization.non-canonical-json";
}

/// <summary>Trust boundary of a gain-response source.</summary>
public static class GainResponseProvenance
{
    /// <summary>The exact pinned model that rendered a virtual frame; not measured installed-camera truth.</summary>
    public const string VirtualSimulationTruth = "virtual-simulation-truth";
}

/// <summary>Readout conversion regime of one resolved coefficient. Coefficients from different regimes never mix.</summary>
public static class GainConversionRegimes
{
    public const string FixedConversionGain = "fixed-conversion-gain";
    public const string LowConversionGain = "low-conversion-gain";
    public const string HighConversionGain = "high-conversion-gain";
}

/// <summary>Which response produced the frames, as declared by the host that owns the camera module.</summary>
public enum GainResponseSourceKind
{
    Unspecified,
    PhysicalCamera,

    /// <summary>VirtualSky rendered the frame with <see cref="Asi174MmSensorModel"/>.</summary>
    VirtualAsi174MmModel,

    /// <summary>VirtualSky rendered the frame with the sensor's pinned <see cref="SensorProfile.SimulationResponse"/>.</summary>
    VirtualConfiguredResponse
}

/// <summary>
/// Versioned identity of one sensor/readout gain response: <c>k(g)</c> in electrons per stored container code, the
/// gain range it is valid for, and the declared mapping from native samples to stored codes.
/// </summary>
public sealed record GainResponseDescriptorV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string Provenance,
    [property: JsonRequired] string SensorProfileSha256,
    [property: JsonRequired] string ResponseModelVersion,
    [property: JsonRequired] string GainUnits,
    [property: JsonRequired] double MinimumGainControl,
    [property: JsonRequired] double MaximumGainControl,
    [property: JsonRequired] double? HighConversionGainControl,
    [property: JsonRequired] int NativeSampleDepthBits,
    [property: JsonRequired] int ContainerDepthBits,
    [property: JsonRequired] FrameStoredCodeTransform StoredCodeTransform,
    [property: JsonRequired] string CoefficientRepresentation)
{
    public const string CurrentSchemaVersion = "hvo-gain-response-v1";
}

/// <summary>
/// One resolved conversion coefficient. <see cref="ElectronsPerAduE12"/> is the
/// <see cref="ElectronsPerAduFixedPoint"/> value in electrons per stored container code.
/// </summary>
public sealed record GainCoefficientV1(
    [property: JsonRequired] string ResponseIdentitySha256,
    [property: JsonRequired] double GainControl,
    [property: JsonRequired] string ConversionRegime,
    [property: JsonRequired] long ElectronsPerAduE12);

/// <summary>
/// The owner-approved mixed-gain normalization choices (#1475): calibrated electron-space combination with a fixed
/// reference-gain output scale, a bounded coefficient ratio, equal weights per valid sample, strict exclusion when
/// calibration is unavailable, and responses limited to the permitted provenance.
/// </summary>
public sealed record GainNormalizationPolicyV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string CombinationSpace,
    [property: JsonRequired] string OutputScale,
    [property: JsonRequired] double ReferenceGainControl,
    [property: JsonRequired] int MaximumCoefficientRatioMilli,
    [property: JsonRequired] string Weighting,
    [property: JsonRequired] string CalibrationAvailability,
    [property: JsonRequired] IReadOnlyList<string> PermittedResponseProvenance,
    [property: JsonRequired] string CoefficientRepresentation)
{
    public const string CurrentSchemaVersion = "hvo-gain-normalization-policy-v1";
    public const string CalibratedElectronsV1 = "calibrated-electrons-v1";
    public const string FixedReferenceGainV1 = "fixed-reference-gain-v1";
    public const string EqualValidSampleWeightsV1 = "equal-valid-sample-weights-v1";
    public const string StrictExclusionV1 = "strict-exclusion-v1";
    public const int RatioMilliScale = 1000;
    public const int ApprovedMaximumCoefficientRatioMilli = 4000;

    /// <summary>The approved v1 policy: reference gain 0, ratio at most 4.0, and virtual simulation truth only.</summary>
    public static GainNormalizationPolicyV1 Approved { get; } = new(
        CurrentSchemaVersion,
        CalibratedElectronsV1,
        FixedReferenceGainV1,
        0,
        ApprovedMaximumCoefficientRatioMilli,
        EqualValidSampleWeightsV1,
        StrictExclusionV1,
        [GainResponseProvenance.VirtualSimulationTruth],
        ElectronsPerAduFixedPoint.RepresentationVersion);
}

/// <summary>
/// An available gain response, or the stable reason it is unavailable. Unavailable means mixed-gain normalization
/// does not apply and the identical-gain stacking rule remains in force.
/// </summary>
public sealed class GainResponseResolution
{
    private GainResponseResolution(GainResponse? response, string? reasonCode)
    {
        Response = response;
        ReasonCode = reasonCode;
    }

    public GainResponse? Response { get; }
    public string? ReasonCode { get; }
    public bool IsAvailable => Response is not null;

    internal static GainResponseResolution Available(GainResponse response) => new(response, null);
    internal static GainResponseResolution Unavailable(string reasonCode) => new(null, reasonCode);
}

/// <summary>A resolved coefficient, or the stable reason none could be resolved.</summary>
public sealed class GainCoefficientResolution
{
    private GainCoefficientResolution(GainCoefficientV1? coefficient, string? reasonCode)
    {
        Coefficient = coefficient;
        ReasonCode = reasonCode;
    }

    public GainCoefficientV1? Coefficient { get; }
    public string? ReasonCode { get; }
    public bool IsAvailable => Coefficient is not null;

    internal static GainCoefficientResolution Available(GainCoefficientV1 coefficient) => new(coefficient, null);
    internal static GainCoefficientResolution Unavailable(string reasonCode) => new(null, reasonCode);
}

/// <summary>A validated response that evaluates <c>k(g)</c> with the exact model that rendered the frames.</summary>
public sealed class GainResponse
{
    private readonly Func<double, double> _nativeElectronsPerAdu;
    private readonly int _storedCodeNumerator;
    private readonly int _storedCodeDenominator;

    internal GainResponse(
        GainResponseDescriptorV1 descriptor,
        Func<double, double> nativeElectronsPerAdu,
        int storedCodeNumerator,
        int storedCodeDenominator)
    {
        Descriptor = descriptor;
        IdentitySha256 = GainNormalizationContractJson.ComputeIdentitySha256(descriptor);
        _nativeElectronsPerAdu = nativeElectronsPerAdu;
        _storedCodeNumerator = storedCodeNumerator;
        _storedCodeDenominator = storedCodeDenominator;
    }

    public GainResponseDescriptorV1 Descriptor { get; }
    public string IdentitySha256 { get; }

    /// <summary>
    /// Resolves the coefficient for one gain-control value inside the validity range. The model's native
    /// electrons per ADU and the declared stored-code mapping are combined with one rounding.
    /// </summary>
    public GainCoefficientResolution ResolveCoefficient(double gainControl)
    {
        if (!double.IsFinite(gainControl) || gainControl < Descriptor.MinimumGainControl ||
            gainControl > Descriptor.MaximumGainControl)
        {
            return GainCoefficientResolution.Unavailable(GainNormalizationReasonCodes.GainOutOfRange);
        }
        if (!ElectronsPerAduFixedPoint.TryFromDouble(
                _nativeElectronsPerAdu(gainControl),
                _storedCodeNumerator,
                _storedCodeDenominator,
                out var electronsPerAdu))
        {
            return GainCoefficientResolution.Unavailable(GainNormalizationReasonCodes.InvalidCoefficient);
        }
        var regime = Descriptor.HighConversionGainControl is { } highConversionGain
            ? gainControl >= highConversionGain
                ? GainConversionRegimes.HighConversionGain
                : GainConversionRegimes.LowConversionGain
            : GainConversionRegimes.FixedConversionGain;
        return GainCoefficientResolution.Available(
            new GainCoefficientV1(IdentitySha256, gainControl + 0.0, regime, electronsPerAdu));
    }
}

/// <summary>Resolves the gain response for frames from a declared source, sensor, and readout.</summary>
public static class GainResponseResolver
{
    private const string Asi174MmGainUnits = "ZWO 0.1 dB";
    private const int Asi174MmNativeSampleDepthBits = 12;
    private const int SupportedContainerDepthBits = 16;

    public static GainResponseResolution Resolve(
        GainResponseSourceKind source,
        SensorProfile sensor,
        SensorReadoutProfile? readout)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        return source switch
        {
            GainResponseSourceKind.PhysicalCamera =>
                GainResponseResolution.Unavailable(GainNormalizationReasonCodes.PhysicalResponseNotEnabled),
            GainResponseSourceKind.VirtualAsi174MmModel => ResolveAsi174Mm(sensor, readout),
            GainResponseSourceKind.VirtualConfiguredResponse => ResolveConfigured(sensor, readout),
            _ => GainResponseResolution.Unavailable(GainNormalizationReasonCodes.UnsupportedResponseSource)
        };
    }

    private static GainResponseResolution ResolveAsi174Mm(SensorProfile sensor, SensorReadoutProfile? readout)
    {
        // VirtualSky renders a configured response in preference to the built-in model.
        if (sensor.SimulationResponse is not null)
        {
            return GainResponseResolution.Unavailable(GainNormalizationReasonCodes.UnsupportedResponseSource);
        }
        if (sensor.PixelFormat != CameraPixelFormat.Mono16 || sensor.ColorMode != SensorColorMode.Mono)
        {
            return GainResponseResolution.Unavailable(GainNormalizationReasonCodes.UnsupportedCodeMapping);
        }
        var transform = FrameStoredCodeTransform.RightAlignedV1;
        if (readout is not null && !TryResolveCodeMapping(sensor, readout, Asi174MmNativeSampleDepthBits, out transform))
        {
            return GainResponseResolution.Unavailable(GainNormalizationReasonCodes.UnsupportedCodeMapping);
        }
        return Available(
            sensor,
            Asi174MmSensorModel.Version,
            Asi174MmGainUnits,
            0,
            400,
            null,
            Asi174MmNativeSampleDepthBits,
            transform,
            static gain => Asi174MmSensorModel.Resolve(gain).ElectronsPerAdu);
    }

    private static GainResponseResolution ResolveConfigured(SensorProfile sensor, SensorReadoutProfile? readout)
    {
        if (sensor.SimulationResponse is not { } profile)
        {
            return GainResponseResolution.Unavailable(GainNormalizationReasonCodes.MissingResponse);
        }
        try
        {
            ConfiguredSensorResponseResolver.Validate(profile);
        }
        catch (ArgumentException)
        {
            return GainResponseResolution.Unavailable(GainNormalizationReasonCodes.InvalidResponse);
        }
        if (readout is null || !TryResolveCodeMapping(sensor, readout, profile.AdcBitDepth, out var transform))
        {
            return GainResponseResolution.Unavailable(GainNormalizationReasonCodes.UnsupportedCodeMapping);
        }
        return Available(
            sensor,
            profile.ModelVersion,
            profile.GainUnits,
            profile.MinimumGainControl,
            profile.MaximumGainControl,
            profile.HighConversionGainControl,
            profile.AdcBitDepth,
            transform,
            gain => ConfiguredSensorResponseResolver.Resolve(profile, gain).ElectronsPerAdu);
    }

    /// <summary>
    /// Accepts only full-resolution, byte-aligned 16-bit readouts at the native sample depth whose stored codes are
    /// an exact linear function of native samples. Binning, packing, reduced depth and opaque or shifted codes
    /// change the per-code response and stay unsupported until a later version declares them.
    /// </summary>
    private static bool TryResolveCodeMapping(
        SensorProfile sensor,
        SensorReadoutProfile readout,
        int nativeSampleDepthBits,
        out FrameStoredCodeTransform transform)
    {
        transform = readout.StoredCodeTransform;
        try
        {
            _ = SensorReadoutResolver.Resolve(sensor, readout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return readout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 &&
            readout.BinX == 1 && readout.BinY == 1 &&
            readout.BinningAlgorithm == FrameBinningAlgorithm.IdentityV1 &&
            readout.Packing == FrameSamplePacking.ByteAligned &&
            readout.SampleDepthBits == nativeSampleDepthBits &&
            readout.ContainerDepthBits == SupportedContainerDepthBits &&
            transform switch
            {
                FrameStoredCodeTransform.IdentityV1 => nativeSampleDepthBits == SupportedContainerDepthBits,
                FrameStoredCodeTransform.RightAlignedV1 => true,
                FrameStoredCodeTransform.FullRangeScaledV1 => nativeSampleDepthBits < SupportedContainerDepthBits,
                _ => false
            };
    }

    private static GainResponseResolution Available(
        SensorProfile sensor,
        string modelVersion,
        string gainUnits,
        double minimumGain,
        double maximumGain,
        double? highConversionGain,
        int nativeSampleDepthBits,
        FrameStoredCodeTransform transform,
        Func<double, double> nativeElectronsPerAdu)
    {
        var descriptor = GainNormalizationContractJson.Normalize(new GainResponseDescriptorV1(
            GainResponseDescriptorV1.CurrentSchemaVersion,
            GainResponseProvenance.VirtualSimulationTruth,
            CaptureContractJson.ComputeCanonicalJsonSha256(sensor),
            modelVersion,
            gainUnits,
            minimumGain,
            maximumGain,
            highConversionGain,
            nativeSampleDepthBits,
            SupportedContainerDepthBits,
            transform,
            ElectronsPerAduFixedPoint.RepresentationVersion));
        if (!GainNormalizationContract.Validate(descriptor).IsValid)
        {
            return GainResponseResolution.Unavailable(GainNormalizationReasonCodes.InvalidResponse);
        }
        // A full-range scale stores native code n as n × (2^c − 1) / (2^n − 1), so one stored code carries
        // (2^n − 1) / (2^c − 1) of a native code's electrons. Identity and right-aligned codes are unchanged.
        var (numerator, denominator) = transform == FrameStoredCodeTransform.FullRangeScaledV1
            ? ((1 << nativeSampleDepthBits) - 1, (1 << SupportedContainerDepthBits) - 1)
            : (1, 1);
        return GainResponseResolution.Available(
            new GainResponse(descriptor, nativeElectronsPerAdu, numerator, denominator));
    }
}

/// <summary>Validation and admission rules of the gain-normalization contracts.</summary>
public static class GainNormalizationContract
{
    private const int MaximumTokenLength = 128;

    public static CaptureContractValidationResult Validate(GainNormalizationPolicyV1? policy)
    {
        if (policy is null ||
            !string.Equals(policy.SchemaVersion, GainNormalizationPolicyV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure("schemaVersion");
        }
        if (!string.Equals(policy.CombinationSpace, GainNormalizationPolicyV1.CalibratedElectronsV1, StringComparison.Ordinal))
        {
            return Failure("combinationSpace");
        }
        if (!string.Equals(policy.OutputScale, GainNormalizationPolicyV1.FixedReferenceGainV1, StringComparison.Ordinal))
        {
            return Failure("outputScale");
        }
        if (!double.IsFinite(policy.ReferenceGainControl))
        {
            return Failure("referenceGainControl");
        }
        if (policy.MaximumCoefficientRatioMilli is < GainNormalizationPolicyV1.RatioMilliScale or
            > GainNormalizationPolicyV1.ApprovedMaximumCoefficientRatioMilli)
        {
            return Failure("maximumCoefficientRatioMilli");
        }
        if (!string.Equals(policy.Weighting, GainNormalizationPolicyV1.EqualValidSampleWeightsV1, StringComparison.Ordinal))
        {
            return Failure("weighting");
        }
        if (!string.Equals(policy.CalibrationAvailability, GainNormalizationPolicyV1.StrictExclusionV1, StringComparison.Ordinal))
        {
            return Failure("calibrationAvailability");
        }
        if (policy.PermittedResponseProvenance is not { Count: > 0 } provenance ||
            provenance.Any(static value => !KnownProvenance(value)) ||
            provenance.Distinct(StringComparer.Ordinal).Count() != provenance.Count)
        {
            return Failure("permittedResponseProvenance");
        }
        return string.Equals(
            policy.CoefficientRepresentation,
            ElectronsPerAduFixedPoint.RepresentationVersion,
            StringComparison.Ordinal)
            ? CaptureContractValidationResult.Success
            : Failure("coefficientRepresentation");
    }

    public static CaptureContractValidationResult Validate(GainResponseDescriptorV1? descriptor)
    {
        if (descriptor is null ||
            !string.Equals(descriptor.SchemaVersion, GainResponseDescriptorV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure("schemaVersion");
        }
        if (!KnownProvenance(descriptor.Provenance))
        {
            return Failure("provenance");
        }
        if (descriptor.SensorProfileSha256 is not { Length: 64 } sensorSha256 || !sensorSha256.All(Uri.IsHexDigit))
        {
            return Failure("sensorProfileSha256");
        }
        if (!Token(descriptor.ResponseModelVersion))
        {
            return Failure("responseModelVersion");
        }
        if (!Token(descriptor.GainUnits))
        {
            return Failure("gainUnits");
        }
        if (!double.IsFinite(descriptor.MinimumGainControl) || !double.IsFinite(descriptor.MaximumGainControl) ||
            descriptor.MinimumGainControl > descriptor.MaximumGainControl)
        {
            return Failure("maximumGainControl");
        }
        if (descriptor.HighConversionGainControl is { } highConversionGain &&
            (!double.IsFinite(highConversionGain) || highConversionGain < descriptor.MinimumGainControl ||
                highConversionGain > descriptor.MaximumGainControl))
        {
            return Failure("highConversionGainControl");
        }
        if (descriptor.ContainerDepthBits != 16 || descriptor.NativeSampleDepthBits is < 1 or > 16)
        {
            return Failure("nativeSampleDepthBits");
        }
        var transformIsValid = descriptor.StoredCodeTransform switch
        {
            FrameStoredCodeTransform.IdentityV1 => descriptor.NativeSampleDepthBits == descriptor.ContainerDepthBits,
            FrameStoredCodeTransform.RightAlignedV1 => true,
            FrameStoredCodeTransform.FullRangeScaledV1 => descriptor.NativeSampleDepthBits < descriptor.ContainerDepthBits,
            _ => false
        };
        if (!transformIsValid)
        {
            return Failure("storedCodeTransform");
        }
        return string.Equals(
            descriptor.CoefficientRepresentation,
            ElectronsPerAduFixedPoint.RepresentationVersion,
            StringComparison.Ordinal)
            ? CaptureContractValidationResult.Success
            : Failure("coefficientRepresentation");
    }

    /// <summary>
    /// Resolves the policy's fixed reference-gain coefficient, which sets the output scale. It never follows the
    /// gain of any input frame.
    /// </summary>
    public static GainCoefficientResolution ResolveReference(GainNormalizationPolicyV1 policy, GainResponse response)
    {
        RequireValid(policy);
        ArgumentNullException.ThrowIfNull(response);
        if (!policy.PermittedResponseProvenance.Contains(response.Descriptor.Provenance, StringComparer.Ordinal))
        {
            return GainCoefficientResolution.Unavailable(GainNormalizationReasonCodes.ProvenanceNotPermitted);
        }
        var reference = response.ResolveCoefficient(policy.ReferenceGainControl);
        return reference.IsAvailable ||
            !string.Equals(reference.ReasonCode, GainNormalizationReasonCodes.GainOutOfRange, StringComparison.Ordinal)
            ? reference
            : GainCoefficientResolution.Unavailable(GainNormalizationReasonCodes.ReferenceGainOutOfRange);
    }

    /// <summary>
    /// Returns null when the coefficients may share one stack, or the stable reason they may not. The bound compares
    /// <c>max(k) / min(k)</c> exactly against the policy limit; gain-control numbers are never divided.
    /// </summary>
    public static string? EvaluateCoefficientBound(
        GainNormalizationPolicyV1 policy,
        IReadOnlyList<GainCoefficientV1> coefficients)
    {
        RequireValid(policy);
        ArgumentNullException.ThrowIfNull(coefficients);
        if (coefficients.Count == 0 || coefficients.Any(static coefficient => coefficient is null))
        {
            throw new ArgumentException("At least one coefficient is required.", nameof(coefficients));
        }
        var first = coefficients[0];
        var minimum = long.MaxValue;
        var maximum = long.MinValue;
        foreach (var coefficient in coefficients)
        {
            if (!ElectronsPerAduFixedPoint.IsValid(coefficient.ElectronsPerAduE12))
            {
                return GainNormalizationReasonCodes.InvalidCoefficient;
            }
            if (!string.Equals(coefficient.ResponseIdentitySha256, first.ResponseIdentitySha256, StringComparison.OrdinalIgnoreCase))
            {
                return GainNormalizationReasonCodes.MixedResponse;
            }
            if (!string.Equals(coefficient.ConversionRegime, first.ConversionRegime, StringComparison.Ordinal))
            {
                return GainNormalizationReasonCodes.MixedConversionRegime;
            }
            minimum = Math.Min(minimum, coefficient.ElectronsPerAduE12);
            maximum = Math.Max(maximum, coefficient.ElectronsPerAduE12);
        }
        return ElectronsPerAduFixedPoint.IsRatioWithin(
            minimum,
            maximum,
            policy.MaximumCoefficientRatioMilli,
            GainNormalizationPolicyV1.RatioMilliScale)
            ? null
            : GainNormalizationReasonCodes.CoefficientRatioExceeded;
    }

    internal static void RequireValid(GainNormalizationPolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var validation = Validate(policy);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Gain-normalization policy is invalid at '{validation.FieldPath}'.", nameof(policy));
        }
    }

    private static bool KnownProvenance(string? value)
        => string.Equals(value, GainResponseProvenance.VirtualSimulationTruth, StringComparison.Ordinal);

    private static bool Token(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumTokenLength;

    private static CaptureContractValidationResult Failure(string path)
        => CaptureContractValidationResult.Failure(GainNormalizationReasonCodes.InvalidContract, path);
}

/// <summary>
/// Canonical UTF-8 JSON and SHA-256 identities for the gain-normalization contracts. Parsing accepts only the exact
/// canonical bytes, so a frozen contract reproduces byte for byte.
/// </summary>
public static class GainNormalizationContractJson
{
    public const int MaximumContractBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(GainNormalizationPolicyV1 policy)
    {
        GainNormalizationContract.RequireValid(policy);
        return SerializeCanonical(Normalize(policy));
    }

    public static byte[] Serialize(GainResponseDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var validation = GainNormalizationContract.Validate(descriptor);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Gain-response descriptor is invalid at '{validation.FieldPath}'.", nameof(descriptor));
        }
        return SerializeCanonical(Normalize(descriptor));
    }

    public static string ComputeIdentitySha256(GainNormalizationPolicyV1 policy)
        => Convert.ToHexString(SHA256.HashData(Serialize(policy)));

    public static string ComputeIdentitySha256(GainResponseDescriptorV1 descriptor)
        => Convert.ToHexString(SHA256.HashData(Serialize(descriptor)));

    public static (GainNormalizationPolicyV1? Value, CaptureContractValidationResult Validation) ParsePolicy(
        ReadOnlyMemory<byte> utf8Json)
        => Parse<GainNormalizationPolicyV1>(
            utf8Json,
            GainNormalizationPolicyV1.CurrentSchemaVersion,
            GainNormalizationContract.Validate,
            Serialize);

    public static (GainResponseDescriptorV1? Value, CaptureContractValidationResult Validation) ParseDescriptor(
        ReadOnlyMemory<byte> utf8Json)
        => Parse<GainResponseDescriptorV1>(
            utf8Json,
            GainResponseDescriptorV1.CurrentSchemaVersion,
            GainNormalizationContract.Validate,
            Serialize);

    internal static GainNormalizationPolicyV1 Normalize(GainNormalizationPolicyV1 policy)
        => policy with
        {
            ReferenceGainControl = policy.ReferenceGainControl + 0.0,
            PermittedResponseProvenance = [.. policy.PermittedResponseProvenance.Order(StringComparer.Ordinal)]
        };

    internal static GainResponseDescriptorV1 Normalize(GainResponseDescriptorV1 descriptor)
        => descriptor with
        {
            SensorProfileSha256 = descriptor.SensorProfileSha256.ToUpperInvariant(),
            MinimumGainControl = descriptor.MinimumGainControl + 0.0,
            MaximumGainControl = descriptor.MaximumGainControl + 0.0,
            HighConversionGainControl = descriptor.HighConversionGainControl + 0.0
        };

    private static (T? Value, CaptureContractValidationResult Validation) Parse<T>(
        ReadOnlyMemory<byte> utf8Json,
        string schemaVersion,
        Func<T?, CaptureContractValidationResult> validate,
        Func<T, byte[]> serialize)
        where T : class
    {
        if (utf8Json.Length > MaximumContractBytes)
        {
            return (null, CaptureContractValidationResult.Failure(GainNormalizationReasonCodes.InvalidContract, "$"));
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                return (null, CaptureContractValidationResult.Failure(GainNormalizationReasonCodes.InvalidContract, "$"));
            }
            if (!string.Equals(schema.GetString(), schemaVersion, StringComparison.Ordinal))
            {
                return (null, CaptureContractValidationResult.Failure(
                    GainNormalizationReasonCodes.UnsupportedSchema,
                    "schemaVersion"));
            }
            var value = JsonSerializer.Deserialize<T>(utf8Json.Span, SerializerOptions);
            var validation = validate(value);
            if (!validation.IsValid)
            {
                return (null, validation);
            }
            return serialize(value!).AsSpan().SequenceEqual(utf8Json.Span)
                ? (value, validation)
                : (null, CaptureContractValidationResult.Failure(GainNormalizationReasonCodes.NonCanonicalJson, "$"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return (null, CaptureContractValidationResult.Failure(GainNormalizationReasonCodes.InvalidContract, "$"));
        }
    }

    private static byte[] SerializeCanonical<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, SerializerOptions)));

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
