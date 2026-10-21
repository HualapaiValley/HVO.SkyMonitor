using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>Stable reasons why a calibration family is rejected or excludes one input.</summary>
public static class CalibrationFamilyReasonCodes
{
    public const string InvalidContract = "calibration.family.invalid-contract";
    public const string UnsupportedSchema = "calibration.family.unsupported-schema";
    public const string NonCanonicalJson = "calibration.family.non-canonical-json";
    public const string DuplicateMember = "calibration.family.duplicate-member";
    public const string OverlappingMembers = "calibration.family.overlapping-members";
    public const string InconsistentMaskFamily = "calibration.family.inconsistent-mask-family";

    public const string InvalidFamily = "calibration.family.invalid-family";
    public const string InvalidConditions = "calibration.family.invalid-conditions";
    public const string IdentityMismatch = "calibration.family.identity-mismatch";
    public const string MaskFamilyMismatch = "calibration.family.mask-family-mismatch";
    public const string MissingMember = "calibration.family.missing-member";
    public const string ReadoutMismatch = "calibration.family.readout-mismatch";
    public const string TemperatureUnavailable = "calibration.family.temperature-unavailable";
    public const string TemperatureOutOfRange = "calibration.family.temperature-out-of-range";
    public const string ExposureOutOfRange = "calibration.family.exposure-out-of-range";
    public const string Stale = "calibration.family.stale";
}

/// <summary>One approved master of a family member: its kind, immutable artifact identity and payload hash.</summary>
public sealed record CalibrationFamilyMasterV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] string PayloadSha256);

/// <summary>
/// The approved bias, dark, flat and defect masters for one gain and offset, and the exact conditions they apply to.
/// Gain, offset and input layout match exactly; temperature and light exposure are inclusive ranges. The member is in
/// effect from <see cref="EffectiveFromUtc"/> up to, but not including, <see cref="EffectiveUntilUtc"/>.
/// <see cref="MaskProfileSha256"/> names the mask family its defect master belongs to.
/// </summary>
public sealed record CalibrationFamilyMemberV1(
    [property: JsonRequired] string MemberId,
    [property: JsonRequired] double Gain,
    [property: JsonRequired] double? Offset,
    [property: JsonRequired] FrameLayoutDescriptor InputLayout,
    [property: JsonRequired] double MinimumTemperatureC,
    [property: JsonRequired] double MaximumTemperatureC,
    [property: JsonRequired] TimeSpan MinimumLightExposure,
    [property: JsonRequired] TimeSpan MaximumLightExposure,
    [property: JsonRequired] DateTimeOffset EffectiveFromUtc,
    [property: JsonRequired] DateTimeOffset? EffectiveUntilUtc,
    [property: JsonRequired] string MaskProfileSha256,
    [property: JsonRequired] IReadOnlyList<CalibrationFamilyMasterV1> Masters);

/// <summary>
/// One approved calibration family (#1475): per-gain members for one agent, rig and sensor under one mask family.
/// Its identity, with the gain-normalization policy identity, is the common stack-compatibility axis; the member
/// chosen for each input may differ by gain and stays in that input's lineage. A defect mask from another mask
/// family is a boundary, not a member.
/// </summary>
public sealed record CalibrationFamilyV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string FamilyId,
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] string RigId,
    [property: JsonRequired] string RigProfileSha256,
    [property: JsonRequired] string SensorProfileSha256,
    [property: JsonRequired] string MaskProfileSha256,
    [property: JsonRequired] IReadOnlyList<CalibrationFamilyMemberV1> Members)
{
    public const string CurrentSchemaVersion = "hvo-calibration-family-v1";
    public const int MaximumMembers = 512;
}

/// <summary>The actual capture conditions of one input frame.</summary>
public sealed record CalibrationFamilyInputV1(
    string AgentId,
    string RigId,
    string RigProfileSha256,
    string SensorProfileSha256,
    string MaskProfileSha256,
    FrameLayoutDescriptor Layout,
    double Gain,
    double? Offset,
    double? TemperatureC,
    TimeSpan Exposure,
    DateTimeOffset ObservedUtc);

/// <summary>The exact family member for one input, or the stable reason the input is excluded.</summary>
public sealed class CalibrationFamilyMemberResolution
{
    private CalibrationFamilyMemberResolution(CalibrationFamilyMemberV1? member, string? reasonCode)
    {
        Member = member;
        ReasonCode = reasonCode;
    }

    public CalibrationFamilyMemberV1? Member { get; }
    public string? ReasonCode { get; }
    public bool IsApplicable => Member is not null;

    internal static CalibrationFamilyMemberResolution Applicable(CalibrationFamilyMemberV1 member) => new(member, null);
    internal static CalibrationFamilyMemberResolution Excluded(string reasonCode) => new(null, reasonCode);
}

/// <summary>Validation and exact applicability rules of the calibration-family contract.</summary>
public static class CalibrationFamilyContract
{
    private const int MaximumTokenLength = 128;

    public static CaptureContractValidationResult Validate(CalibrationFamilyV1? family)
    {
        if (family is null ||
            !string.Equals(family.SchemaVersion, CalibrationFamilyV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure("schemaVersion");
        }
        if (!Token(family.FamilyId))
        {
            return Failure("familyId");
        }
        if (!Token(family.AgentId))
        {
            return Failure("agentId");
        }
        if (!Token(family.RigId))
        {
            return Failure("rigId");
        }
        if (!Sha256(family.RigProfileSha256))
        {
            return Failure("rigProfileSha256");
        }
        if (!Sha256(family.SensorProfileSha256))
        {
            return Failure("sensorProfileSha256");
        }
        if (!Sha256(family.MaskProfileSha256))
        {
            return Failure("maskProfileSha256");
        }
        if (family.Members is not { Count: > 0 and <= CalibrationFamilyV1.MaximumMembers } members ||
            members.Any(static member => member is null))
        {
            return Failure("members");
        }
        foreach (var member in members)
        {
            var validation = ValidateMember(member);
            if (!validation.IsValid)
            {
                return validation;
            }
        }
        if (members.Any(member => !string.Equals(
                member.MaskProfileSha256,
                family.MaskProfileSha256,
                StringComparison.OrdinalIgnoreCase)))
        {
            return CaptureContractValidationResult.Failure(
                CalibrationFamilyReasonCodes.InconsistentMaskFamily,
                "members.maskProfileSha256");
        }
        var masters = members.SelectMany(static member => member.Masters).ToArray();
        if (masters.DistinctBy(static master => master.ArtifactId).Count() !=
            masters.DistinctBy(static master => (master.ArtifactId, master.Kind, master.PayloadSha256.ToUpperInvariant())).Count())
        {
            return Failure("members.masters");
        }
        return ValidateMemberConditions(members);
    }

    /// <summary>
    /// Returns the one member whose exact gain, offset and input layout, and whose temperature, exposure and
    /// effective ranges, apply to the input, or the stable reason the input is excluded. A member at another gain is
    /// never a substitute, however close it is.
    /// </summary>
    public static CalibrationFamilyMemberResolution Evaluate(CalibrationFamilyV1 family, CalibrationFamilyInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(input);
        if (!Validate(family).IsValid)
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.InvalidFamily);
        }
        if (!ValidInput(input))
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.InvalidConditions);
        }
        if (!string.Equals(family.AgentId, input.AgentId, StringComparison.Ordinal) ||
            !string.Equals(family.RigId, input.RigId, StringComparison.Ordinal) ||
            !string.Equals(family.RigProfileSha256, input.RigProfileSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(family.SensorProfileSha256, input.SensorProfileSha256, StringComparison.OrdinalIgnoreCase))
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.IdentityMismatch);
        }
        if (!string.Equals(family.MaskProfileSha256, input.MaskProfileSha256, StringComparison.OrdinalIgnoreCase))
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.MaskFamilyMismatch);
        }

        var candidates = family.Members
            .Where(member => member.Gain == input.Gain && member.Offset == input.Offset)
            .ToArray();
        if (candidates.Length == 0)
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.MissingMember);
        }
        candidates = [.. candidates.Where(member => member.InputLayout == input.Layout)];
        if (candidates.Length == 0)
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.ReadoutMismatch);
        }
        if (input.TemperatureC is not { } temperature)
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.TemperatureUnavailable);
        }
        candidates = [.. candidates.Where(member =>
            temperature >= member.MinimumTemperatureC && temperature <= member.MaximumTemperatureC)];
        if (candidates.Length == 0)
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.TemperatureOutOfRange);
        }
        candidates = [.. candidates.Where(member =>
            input.Exposure >= member.MinimumLightExposure && input.Exposure <= member.MaximumLightExposure)];
        if (candidates.Length == 0)
        {
            return CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.ExposureOutOfRange);
        }

        // Validation rejects overlapping members, so exactly one member remains.
        var selected = candidates.Single();
        return input.ObservedUtc < selected.EffectiveFromUtc ||
               selected.EffectiveUntilUtc is { } until && input.ObservedUtc >= until
            ? CalibrationFamilyMemberResolution.Excluded(CalibrationFamilyReasonCodes.Stale)
            : CalibrationFamilyMemberResolution.Applicable(selected);
    }

    private static CaptureContractValidationResult ValidateMember(CalibrationFamilyMemberV1 member)
    {
        if (!Token(member.MemberId))
        {
            return Failure("members.memberId");
        }
        if (!double.IsFinite(member.Gain) || member.Gain < 0)
        {
            return Failure("members.gain");
        }
        if (member.Offset is { } offset && !double.IsFinite(offset))
        {
            return Failure("members.offset");
        }
        if (!SupportedInputLayout(member.InputLayout))
        {
            return Failure("members.inputLayout");
        }
        if (!double.IsFinite(member.MinimumTemperatureC) || !double.IsFinite(member.MaximumTemperatureC) ||
            member.MinimumTemperatureC > member.MaximumTemperatureC)
        {
            return Failure("members.temperatureC");
        }
        if (member.MinimumLightExposure <= TimeSpan.Zero || member.MaximumLightExposure < member.MinimumLightExposure)
        {
            return Failure("members.lightExposure");
        }
        if (member.EffectiveFromUtc.Offset != TimeSpan.Zero ||
            member.EffectiveUntilUtc is { } until && (until.Offset != TimeSpan.Zero || until <= member.EffectiveFromUtc))
        {
            return Failure("members.effectiveUtc");
        }
        if (!Sha256(member.MaskProfileSha256))
        {
            return Failure("members.maskProfileSha256");
        }
        if (member.Masters is not { Count: 4 } masters || masters.Any(static master => master is null) ||
            !CalibrationReferenceKinds.All.Order(StringComparer.Ordinal)
                .SequenceEqual(masters.Select(static master => master.Kind).Order(StringComparer.Ordinal)) ||
            masters.Any(static master => master.ArtifactId == Guid.Empty || !Sha256(master.PayloadSha256)) ||
            masters.Select(static master => master.ArtifactId).Distinct().Count() != masters.Count)
        {
            return Failure("members.masters");
        }
        return CaptureContractValidationResult.Success;
    }

    /// <summary>
    /// Rejects members that claim the same exact conditions. Members with equal gain, offset and input layout must
    /// have disjoint temperature or light-exposure ranges, so no input can apply to two of them.
    /// </summary>
    private static CaptureContractValidationResult ValidateMemberConditions(IReadOnlyList<CalibrationFamilyMemberV1> members)
    {
        if (members.Select(static member => member.MemberId).Distinct(StringComparer.Ordinal).Count() != members.Count)
        {
            return CaptureContractValidationResult.Failure(CalibrationFamilyReasonCodes.DuplicateMember, "members.memberId");
        }
        var overlapping = false;
        for (var first = 0; first < members.Count; first++)
        {
            for (var second = first + 1; second < members.Count; second++)
            {
                var left = members[first];
                var right = members[second];
                if (left.Gain != right.Gain || left.Offset != right.Offset || left.InputLayout != right.InputLayout)
                {
                    continue;
                }
                if (left.MinimumTemperatureC == right.MinimumTemperatureC &&
                    left.MaximumTemperatureC == right.MaximumTemperatureC &&
                    left.MinimumLightExposure == right.MinimumLightExposure &&
                    left.MaximumLightExposure == right.MaximumLightExposure)
                {
                    return CaptureContractValidationResult.Failure(CalibrationFamilyReasonCodes.DuplicateMember, "members");
                }
                overlapping |= left.MinimumTemperatureC <= right.MaximumTemperatureC &&
                               right.MinimumTemperatureC <= left.MaximumTemperatureC &&
                               left.MinimumLightExposure <= right.MaximumLightExposure &&
                               right.MinimumLightExposure <= left.MaximumLightExposure;
            }
        }
        return overlapping
            ? CaptureContractValidationResult.Failure(CalibrationFamilyReasonCodes.OverlappingMembers, "members")
            : CaptureContractValidationResult.Success;
    }

    /// <summary>
    /// Accepts the complete, little-endian, byte-aligned 16-bit Mono16 or RGGB readouts the reference calibration
    /// supports. A member must declare its readout, stored-code transform and level code space explicitly.
    /// </summary>
    private static bool SupportedInputLayout(FrameLayoutDescriptor? layout)
        => layout is not null && layout.Validate().IsValid &&
           layout.Readout is not null && layout.StoredCodeTransform is not null && layout.LevelCodeSpace is not null &&
           layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 &&
           layout.ByteOrder == FrameByteOrder.LittleEndian &&
           layout.ContainerDepthBits == 16 &&
           layout.Packing == FrameSamplePacking.ByteAligned;

    private static bool ValidInput(CalibrationFamilyInputV1 input)
        => !string.IsNullOrWhiteSpace(input.AgentId) && !string.IsNullOrWhiteSpace(input.RigId) &&
           Sha256(input.RigProfileSha256) && Sha256(input.SensorProfileSha256) && Sha256(input.MaskProfileSha256) &&
           input.Layout is not null && input.Layout.Validate().IsValid &&
           double.IsFinite(input.Gain) &&
           (input.Offset is null || double.IsFinite(input.Offset.Value)) &&
           (input.TemperatureC is null || double.IsFinite(input.TemperatureC.Value)) &&
           input.Exposure > TimeSpan.Zero;

    private static bool Token(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumTokenLength;

    private static bool Sha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static CaptureContractValidationResult Failure(string path)
        => CaptureContractValidationResult.Failure(CalibrationFamilyReasonCodes.InvalidContract, path);
}

/// <summary>
/// Canonical UTF-8 JSON and SHA-256 identity of a calibration family. Parsing accepts only the exact canonical bytes,
/// so a frozen family reproduces byte for byte.
/// </summary>
public static class CalibrationFamilyContractJson
{
    public const int MaximumContractBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(CalibrationFamilyV1 family)
    {
        ArgumentNullException.ThrowIfNull(family);
        var validation = CalibrationFamilyContract.Validate(family);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Calibration family is invalid at '{validation.FieldPath}' ({validation.ReasonCode}).",
                nameof(family));
        }
        return JsonSerializer.SerializeToUtf8Bytes(
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(Normalize(family), SerializerOptions)));
    }

    public static string ComputeIdentitySha256(CalibrationFamilyV1 family)
        => Convert.ToHexString(SHA256.HashData(Serialize(family)));

    public static (CalibrationFamilyV1? Value, CaptureContractValidationResult Validation) Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumContractBytes)
        {
            return (null, CaptureContractValidationResult.Failure(CalibrationFamilyReasonCodes.InvalidContract, "$"));
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                return (null, CaptureContractValidationResult.Failure(CalibrationFamilyReasonCodes.InvalidContract, "$"));
            }
            if (!string.Equals(schema.GetString(), CalibrationFamilyV1.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return (null, CaptureContractValidationResult.Failure(
                    CalibrationFamilyReasonCodes.UnsupportedSchema,
                    "schemaVersion"));
            }
            var value = JsonSerializer.Deserialize<CalibrationFamilyV1>(utf8Json.Span, SerializerOptions);
            var validation = CalibrationFamilyContract.Validate(value);
            if (!validation.IsValid)
            {
                return (null, validation);
            }
            return Serialize(value!).AsSpan().SequenceEqual(utf8Json.Span)
                ? (value, validation)
                : (null, CaptureContractValidationResult.Failure(CalibrationFamilyReasonCodes.NonCanonicalJson, "$"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return (null, CaptureContractValidationResult.Failure(CalibrationFamilyReasonCodes.InvalidContract, "$"));
        }
    }

    /// <summary>Uppercases hashes, writes negative zero as zero, and orders members and masters deterministically.</summary>
    private static CalibrationFamilyV1 Normalize(CalibrationFamilyV1 family)
        => family with
        {
            RigProfileSha256 = family.RigProfileSha256.ToUpperInvariant(),
            SensorProfileSha256 = family.SensorProfileSha256.ToUpperInvariant(),
            MaskProfileSha256 = family.MaskProfileSha256.ToUpperInvariant(),
            Members = [.. family.Members
                .Select(static member => member with
                {
                    Gain = member.Gain + 0.0,
                    Offset = member.Offset + 0.0,
                    InputLayout = member.InputLayout with
                    {
                        BlackLevel = member.InputLayout.BlackLevel + 0.0,
                        WhiteLevel = member.InputLayout.WhiteLevel + 0.0
                    },
                    MinimumTemperatureC = member.MinimumTemperatureC + 0.0,
                    MaximumTemperatureC = member.MaximumTemperatureC + 0.0,
                    MaskProfileSha256 = member.MaskProfileSha256.ToUpperInvariant(),
                    Masters = [.. member.Masters
                        .OrderBy(static master => KindRank(master.Kind))
                        .Select(static master => master with { PayloadSha256 = master.PayloadSha256.ToUpperInvariant() })]
                })
                .OrderBy(static member => member.Gain)
                .ThenBy(static member => member.Offset)
                .ThenBy(static member => member.MemberId, StringComparer.Ordinal)]
        };

    private static int KindRank(string kind)
        => kind switch
        {
            CalibrationReferenceKinds.Bias => 0,
            CalibrationReferenceKinds.Dark => 1,
            CalibrationReferenceKinds.Flat => 2,
            _ => 3
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
