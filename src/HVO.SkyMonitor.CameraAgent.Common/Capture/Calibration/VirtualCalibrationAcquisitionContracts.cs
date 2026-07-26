using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

public sealed record VirtualCalibrationAcquisitionRequestV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] double Gain,
    [property: JsonRequired] double Offset,
    [property: JsonRequired] double TemperatureC,
    [property: JsonRequired] TimeSpan BiasExposure,
    [property: JsonRequired] TimeSpan DarkExposure,
    [property: JsonRequired] TimeSpan FlatExposure,
    [property: JsonRequired] TimeSpan DefectExposure,
    [property: JsonRequired] TimeSpan ApplicableLightExposure,
    [property: JsonRequired] DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    [property: JsonRequired] VirtualCalibrationSourceModelV1 SourceModel,
    [property: JsonRequired] string Actor,
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpectedVersion = null)
{
    public const string CurrentSchemaVersion = "virtual-calibration-acquisition-request-v1";
}

public sealed record VirtualCalibrationAcquisitionPlanV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string JobId,
    [property: JsonRequired] string ModuleType,
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] string RigId,
    [property: JsonRequired] ProfileIdentityDescriptor RigProfile,
    [property: JsonRequired] ProfileIdentityDescriptor SensorProfile,
    [property: JsonRequired] FrameLayoutDescriptor InputLayout,
    [property: JsonRequired] FrameLayoutDescriptor OutputLayout,
    [property: JsonRequired] double Gain,
    [property: JsonRequired] double Offset,
    [property: JsonRequired] double TemperatureC,
    [property: JsonRequired] TimeSpan BiasExposure,
    [property: JsonRequired] TimeSpan DarkExposure,
    [property: JsonRequired] TimeSpan FlatExposure,
    [property: JsonRequired] TimeSpan DefectExposure,
    [property: JsonRequired] TimeSpan ApplicableLightExposure,
    [property: JsonRequired] DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    [property: JsonRequired] DateTimeOffset CreatedUtc,
    [property: JsonRequired] VirtualCalibrationSourceModelV1 SourceModel,
    [property: JsonRequired] string SourceModelIdentitySha256)
{
    public const string CurrentSchemaVersion = "virtual-calibration-acquisition-plan-v1";

    [JsonIgnore]
    public string CameraKey => string.Concat(AgentId, "/", RigId);

    public TimeSpan ExposureFor(string kind)
        => kind switch
        {
            CalibrationReferenceKinds.Bias => BiasExposure,
            CalibrationReferenceKinds.Dark => DarkExposure,
            CalibrationReferenceKinds.Flat => FlatExposure,
            CalibrationReferenceKinds.Defect => DefectExposure,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}

public sealed record CalibrationAcquisitionJobSnapshot(
    VirtualCalibrationAcquisitionPlanV1 Plan,
    string PlanIdentitySha256,
    string State,
    string Phase,
    int AttemptCount,
    string? BundleId,
    string? FailureReason,
    string Actor,
    string? Reason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc)
{
    public bool IsTerminal => State is CalibrationAcquisitionStates.Published or
        CalibrationAcquisitionStates.Failed or CalibrationAcquisitionStates.Cancelled;
}

public static class CalibrationAcquisitionStates
{
    public const string Planned = "planned";
    public const string Acquiring = "acquiring";
    public const string Building = "building";
    public const string Publishing = "publishing";
    public const string Published = "published";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class CalibrationLibraryAcquisitionException : InvalidOperationException
{
    public CalibrationLibraryAcquisitionException()
        : this(CalibrationLibraryReasonCodes.AcquisitionFailure, "Calibration acquisition failed.")
    {
    }

    public CalibrationLibraryAcquisitionException(string message)
        : this(CalibrationLibraryReasonCodes.AcquisitionFailure, message)
    {
    }

    public CalibrationLibraryAcquisitionException(string message, Exception innerException)
        : this(CalibrationLibraryReasonCodes.AcquisitionFailure, message, innerException)
    {
    }

    public CalibrationLibraryAcquisitionException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public CalibrationLibraryAcquisitionException(string reasonCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

internal static class VirtualCalibrationAcquisitionContractJson
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions ParserOptions = CreateParserOptions();

    internal static byte[] SerializePlan(VirtualCalibrationAcquisitionPlanV1 plan)
    {
        ValidatePlan(plan);
        var canonical = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(plan));
        return JsonSerializer.SerializeToUtf8Bytes(canonical);
    }

    internal static VirtualCalibrationAcquisitionPlanV1 ParsePlan(ReadOnlyMemory<byte> json)
    {
        if (json.Length is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException("The durable calibration acquisition plan size is invalid.");
        }
        try
        {
            var plan = JsonSerializer.Deserialize<VirtualCalibrationAcquisitionPlanV1>(json.Span, ParserOptions)
                ?? throw new InvalidDataException("The durable calibration acquisition plan is empty.");
            ValidatePlan(plan);
            if (!json.Span.SequenceEqual(SerializePlan(plan)))
            {
                throw new InvalidDataException("The durable calibration acquisition plan is not canonical JSON.");
            }
            return plan;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The durable calibration acquisition plan is invalid JSON.", exception);
        }
    }

    internal static string ComputePlanIdentitySha256(VirtualCalibrationAcquisitionPlanV1 plan)
        => Convert.ToHexString(SHA256.HashData(SerializePlan(plan)));

    internal static string ComputeRequestIdentitySha256(VirtualCalibrationAcquisitionRequestV1 request)
    {
        ValidateRequest(request);
        return CaptureContractJson.ComputeCanonicalJsonSha256(request);
    }

    internal static void ValidateRequest(VirtualCalibrationAcquisitionRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SchemaVersion, VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !ValidText(request.IdempotencyKey, 128) || !ValidText(request.Actor, 128) || request.Reason?.Length > 512 ||
            request.ExpectedVersion is < 0 ||
            !FiniteNonnegative(request.Gain) || !double.IsFinite(request.Offset) || !double.IsFinite(request.TemperatureC) ||
            !Positive(request.BiasExposure) || !Positive(request.DarkExposure) || !Positive(request.FlatExposure) ||
            !Positive(request.DefectExposure) || !Positive(request.ApplicableLightExposure) ||
            request.EffectiveFromUtc.Offset != TimeSpan.Zero ||
            request.EffectiveUntilUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            request.EffectiveUntilUtc is { } until && until <= request.EffectiveFromUtc || request.SourceModel is null)
        {
            throw new ArgumentException("The virtual calibration acquisition request is invalid.", nameof(request));
        }
        request.SourceModel.Validate();
    }

    internal static void ValidatePlan(VirtualCalibrationAcquisitionPlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.SchemaVersion, VirtualCalibrationAcquisitionPlanV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !ValidText(plan.JobId, 128) || !string.Equals(plan.ModuleType, "VirtualSky", StringComparison.Ordinal) ||
            !ValidText(plan.AgentId, 128) || !ValidText(plan.RigId, 128) ||
            !ValidProfile(plan.RigProfile) || !ValidProfile(plan.SensorProfile) ||
            !ValidNativeLayout(plan.InputLayout) || !ExecutableLayout(plan.InputLayout, plan.SourceModel) ||
            plan.OutputLayout != CalibrationMasterBuilder.CreateNormalizedLayout(plan.InputLayout) ||
            !FiniteNonnegative(plan.Gain) || !double.IsFinite(plan.Offset) || !double.IsFinite(plan.TemperatureC) ||
            !Positive(plan.BiasExposure) || !Positive(plan.DarkExposure) || !Positive(plan.FlatExposure) ||
            !Positive(plan.DefectExposure) || !Positive(plan.ApplicableLightExposure) ||
            plan.EffectiveFromUtc.Offset != TimeSpan.Zero || plan.CreatedUtc.Offset != TimeSpan.Zero ||
            plan.EffectiveUntilUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            plan.EffectiveUntilUtc is { } until && until <= plan.EffectiveFromUtc || plan.SourceModel is null ||
            !Sha256(plan.SourceModelIdentitySha256) ||
            !ValidCaptureTime(plan.CreatedUtc, plan.BiasExposure) ||
            !ValidCaptureTime(plan.CreatedUtc, plan.DarkExposure) ||
            !ValidCaptureTime(plan.CreatedUtc, plan.FlatExposure) ||
            !ValidCaptureTime(plan.CreatedUtc, plan.DefectExposure))
        {
            throw new ArgumentException("The frozen virtual calibration acquisition plan is invalid.", nameof(plan));
        }
        plan.SourceModel.Validate();
        if (!string.Equals(
                VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(plan.SourceModel),
                plan.SourceModelIdentitySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The frozen virtual calibration source model identity is invalid.", nameof(plan));
        }
    }

    private static bool ValidNativeLayout(FrameLayoutDescriptor? layout)
        => layout is not null && layout.Validate().IsValid &&
           layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 &&
           layout.ByteOrder == FrameByteOrder.LittleEndian && layout.ContainerDepthBits == 16 &&
           layout.Packing == FrameSamplePacking.ByteAligned &&
           layout.StoredCodeTransform == FrameStoredCodeTransform.RightAlignedV1 &&
           layout.LevelCodeSpace == FrameLevelCodeSpace.NativeSample && layout.Readout is not null &&
           layout.BlackLevel is { } black && layout.WhiteLevel is { } white &&
           double.IsInteger(black) && double.IsInteger(white) && white > black;

    private static bool ExecutableLayout(
        FrameLayoutDescriptor layout,
        VirtualCalibrationSourceModelV1? model)
    {
        if (model is null || layout.ByteLength > int.MaxValue)
        {
            return false;
        }
        var pixelCount = (long)layout.Width * layout.Height;
        return pixelCount is > 0 and <= int.MaxValue / 2 &&
               (long)model.PersistentDefectCount + model.SourceSpecificDefectCount <= pixelCount &&
               (layout.WhiteLevel!.Value - layout.BlackLevel!.Value) * model.SourceNoiseAmplitudeFraction >= 2;
    }

    private static bool ValidCaptureTime(DateTimeOffset createdUtc, TimeSpan exposure)
    {
        try
        {
            _ = createdUtc.AddMilliseconds(16).Add(exposure);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool ValidProfile(ProfileIdentityDescriptor? profile)
        => profile is not null && ValidText(profile.Name, 128) && ValidText(profile.Version, 128) && Sha256(profile.Sha256);

    private static bool ValidText(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static bool Sha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool Positive(TimeSpan value) => value > TimeSpan.Zero;

    private static bool FiniteNonnegative(double value) => double.IsFinite(value) && value >= 0;

    private static JsonSerializerOptions CreateParserOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
