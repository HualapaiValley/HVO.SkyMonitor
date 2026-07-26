using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public static class CalibrationReferenceKinds
{
    public const string Bias = "bias";
    public const string Dark = "dark";
    public const string Flat = "flat";
    public const string Defect = "defect";

    public static IReadOnlyList<string> All { get; } = [Bias, Dark, Flat, Defect];
}

public sealed record CalibrationReferenceDescriptorV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] string PayloadSha256,
    [property: JsonRequired] TimeSpan Exposure,
    [property: JsonRequired] double Gain,
    double? TemperatureC);

public sealed record ReferenceCalibrationProfileV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string ProfileId,
    [property: JsonRequired] string ProfileVersion,
    [property: JsonRequired] string Source,
    [property: JsonRequired] DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    [property: JsonRequired] int Width,
    [property: JsonRequired] int Height,
    [property: JsonRequired] CameraPixelFormat PixelFormat,
    [property: JsonRequired] ushort FlatNormalizationAdu,
    [property: JsonRequired] double MinimumGain,
    [property: JsonRequired] double MaximumGain,
    double? MinimumTemperatureC,
    double? MaximumTemperatureC,
    [property: JsonRequired] IReadOnlyList<CalibrationReferenceDescriptorV1> References)
{
    public const string CurrentSchemaVersion = "reference-calibration-profile-v1";
}

public static class ReferenceCalibrationProfileJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new();
    private static readonly JsonSerializerOptions ParserOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Serialize(ReferenceCalibrationProfileV1 profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var element = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(profile, SerializerOptions));
        return Encoding.UTF8.GetBytes(element.GetRawText());
    }

    public static string ComputeIdentitySha256(ReferenceCalibrationProfileV1 profile)
        => ProcessingIdentity.ComputePayloadSha256(Serialize(profile));

    public static ReferenceCalibrationProfileV1? Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReferenceCalibrationProfileV1>(utf8Json, ParserOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ReferenceCalibrationOptions();

internal sealed class ReferenceCalibrationRecipe : IProcessingRecipe
{
    private const string ProfileInput = "calibration-profile";

    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.ReferenceCalibration,
        "1.0.0",
        "reference-calibration-linear16-v1",
        ProcessingOperationKind.Transform);

    public JsonElement NormalizeOptions(JsonElement options)
        => ProcessingRecipeSupport.Normalize(ProcessingRecipeSupport.ParseOptions<ReferenceCalibrationOptions>(options));

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var light = ProcessingRecipeSupport.ResolveSingle(request, out var inputFailure);
        if (light is null)
        {
            return ValueTask.FromResult(inputFailure!);
        }
        if (light.Role != FrameArtifactRole.Raw)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector, nameof(request.Input)));
        }
        if (!ProcessingRecipeSupport.TryValidateFrame(light, out var lightLayout, out var layoutFailure))
        {
            return ValueTask.FromResult(layoutFailure!);
        }
        if (lightLayout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.UnsupportedFormat, nameof(light.Layout)));
        }

        var profileInput = request.AuxiliaryInputs?.SingleOrDefault(input =>
            string.Equals(input.Name, ProfileInput, StringComparison.OrdinalIgnoreCase));
        if (profileInput is null)
        {
            return ValueTask.FromResult(ProcessingOutcome.Skipped(
                ProcessingReasonCodes.MissingCalibrationProfile, ProfileInput));
        }
        if (profileInput.Kind != ProcessingAuxiliaryInputKind.CanonicalJson ||
            !string.Equals(profileInput.SchemaVersion, ReferenceCalibrationProfileV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidCalibrationProfile, ProfileInput));
        }

        var profile = ReferenceCalibrationProfileJson.Parse(profileInput.Payload.Span);
        if (!TryValidateProfile(profile, light, lightLayout, out var profileFailure))
        {
            return ValueTask.FromResult(profileFailure!);
        }

        var references = new Dictionary<string, ProcessingArtifact>(StringComparer.Ordinal);
        foreach (var descriptor in profile!.References)
        {
            var auxiliaryName = $"{descriptor.Kind}-reference";
            var auxiliary = request.AuxiliaryInputs?.SingleOrDefault(input =>
                string.Equals(input.Name, auxiliaryName, StringComparison.OrdinalIgnoreCase));
            if (auxiliary is null)
            {
                return ValueTask.FromResult(ProcessingOutcome.Skipped(
                    ProcessingReasonCodes.MissingCalibrationReference, auxiliaryName));
            }
            var matches = request.Inputs.Where(input =>
                input.ArtifactId == auxiliary.ArtifactId && input.ArtifactId == descriptor.ArtifactId).ToArray();
            if (matches.Length == 0)
            {
                return ValueTask.FromResult(ProcessingOutcome.Skipped(
                    ProcessingReasonCodes.MissingCalibrationReference, auxiliaryName));
            }
            if (matches.Length > 1)
            {
                return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.AmbiguousCalibrationReference, auxiliaryName));
            }
            var reference = matches[0];
            if (!ProcessingRecipeSupport.TryValidateFrame(reference, out var referenceLayout, out _) ||
                !LayoutsMatch(lightLayout, referenceLayout))
            {
                return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.CalibrationReferenceLayoutMismatch, auxiliaryName));
            }
            if (!string.Equals(
                    ProcessingIdentity.ComputePayloadSha256(reference.Payload),
                    descriptor.PayloadSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.CalibrationReferenceChecksumMismatch, auxiliaryName));
            }
            if (reference.Integration != descriptor.Exposure || reference.Conditions is not { } conditions ||
                conditions.Gain != descriptor.Gain || conditions.TemperatureC != descriptor.TemperatureC)
            {
                return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.CalibrationReferenceConditionsMismatch, auxiliaryName));
            }
            references.Add(descriptor.Kind, reference);
        }

        Linear16CalibrationResult correction;
        try
        {
            correction = Linear16ReferenceCalibration.Correct(
                ToFrame(light),
                ToFrame(references[CalibrationReferenceKinds.Bias]),
                ToFrame(references[CalibrationReferenceKinds.Dark]),
                ToFrame(references[CalibrationReferenceKinds.Flat]),
                ToFrame(references[CalibrationReferenceKinds.Defect]),
                new Linear16CalibrationParameters(
                    light.Integration,
                    references[CalibrationReferenceKinds.Dark].Integration,
                    references[CalibrationReferenceKinds.Flat].Integration,
                    profile.FlatNormalizationAdu),
                cancellationToken);
        }
        catch (UnrepairableCalibrationDefectException)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.UnrepairableCalibrationDefect,
                $"{CalibrationReferenceKinds.Defect}-reference"));
        }
        catch (InvalidDataException)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidCalibrationFlat,
                $"{CalibrationReferenceKinds.Flat}-reference"));
        }

        var orderedSources = new[]
        {
            light,
            references[CalibrationReferenceKinds.Bias],
            references[CalibrationReferenceKinds.Dark],
            references[CalibrationReferenceKinds.Flat],
            references[CalibrationReferenceKinds.Defect]
        };
        var compatibility = light.Compatibility with
        {
            Calibration = profileInput.IdentitySha256!,
            Mask = profile.References.Single(item => item.Kind == CalibrationReferenceKinds.Defect).PayloadSha256
        };
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Calibrated,
            request.OutputVariant,
            "application/x-hvo-linear-frame",
            ProcessingRecipeSupport.CreatePackedLayout(lightLayout) with
            {
                SampleDepthBits = 16,
                BlackLevel = 0,
                WhiteLevel = ushort.MaxValue,
                StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
                LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
            },
            correction.PixelData,
            identity,
            [new("linear16-reference-calibration", correction.AlgorithmVersion)],
            orderedSources,
            light.Integration,
            compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }

    private static bool TryValidateProfile(
        ReferenceCalibrationProfileV1? profile,
        ProcessingArtifact light,
        FrameLayoutDescriptor layout,
        out ProcessingOutcome? failure)
    {
        var conditions = light.Conditions;
        var kinds = profile?.References?.Select(static reference => reference.Kind).ToArray();
        if (profile is null ||
            !string.Equals(profile.SchemaVersion, ReferenceCalibrationProfileV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(profile.ProfileId) || string.IsNullOrWhiteSpace(profile.ProfileVersion) ||
            string.IsNullOrWhiteSpace(profile.Source) || profile.EffectiveFromUtc.Offset != TimeSpan.Zero ||
            profile.EffectiveUntilUtc is { } utcUntil && utcUntil.Offset != TimeSpan.Zero ||
            profile.EffectiveUntilUtc is { } until && until <= profile.EffectiveFromUtc ||
            profile.Width != layout.Width || profile.Height != layout.Height || profile.PixelFormat != layout.PixelFormat ||
            profile.FlatNormalizationAdu == 0 || !double.IsFinite(profile.MinimumGain) ||
            !double.IsFinite(profile.MaximumGain) || profile.MinimumGain < 0 || profile.MaximumGain < profile.MinimumGain ||
            profile.MinimumTemperatureC is { } minimumTemperature && !double.IsFinite(minimumTemperature) ||
            profile.MaximumTemperatureC is { } maximumTemperature && !double.IsFinite(maximumTemperature) ||
            profile.MinimumTemperatureC > profile.MaximumTemperatureC || kinds is null || kinds.Length != 4 ||
            kinds.Distinct(StringComparer.Ordinal).Count() != 4 ||
            !CalibrationReferenceKinds.All.Order(StringComparer.Ordinal).SequenceEqual(kinds.Order(StringComparer.Ordinal)) ||
            profile.References.Any(static reference => reference.ArtifactId == Guid.Empty ||
                reference.PayloadSha256 is not { Length: 64 } || !reference.PayloadSha256.All(Uri.IsHexDigit) ||
                reference.Exposure <= TimeSpan.Zero || !double.IsFinite(reference.Gain) || reference.Gain < 0 ||
                reference.TemperatureC is { } temperature && !double.IsFinite(temperature)) ||
            profile.References.Select(static reference => reference.ArtifactId).Distinct().Count() != 4)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidCalibrationProfile, ProfileInput);
            return false;
        }

        var observation = light.ObservationStartedUtc ?? light.CreatedUtc;
        if (observation < profile.EffectiveFromUtc || profile.EffectiveUntilUtc is { } effectiveUntil && observation >= effectiveUntil)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.StaleCalibrationProfile, ProfileInput);
            return false;
        }
        if (conditions is null || conditions.Gain < profile.MinimumGain || conditions.Gain > profile.MaximumGain ||
            profile.MinimumTemperatureC is { } minimum && (conditions.TemperatureC is null || conditions.TemperatureC < minimum) ||
            profile.MaximumTemperatureC is { } maximum && (conditions.TemperatureC is null || conditions.TemperatureC > maximum))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.CalibrationReferenceConditionsMismatch, nameof(light.Conditions));
            return false;
        }
        failure = null;
        return true;
    }

    private static bool LayoutsMatch(FrameLayoutDescriptor expected, FrameLayoutDescriptor actual)
        => expected.Width == actual.Width && expected.Height == actual.Height &&
           expected.PixelFormat == actual.PixelFormat && expected.ByteOrder == actual.ByteOrder &&
           expected.SampleDepthBits == actual.SampleDepthBits && expected.ContainerDepthBits == actual.ContainerDepthBits &&
           expected.Packing == actual.Packing && expected.CfaPattern == actual.CfaPattern &&
           expected.StoredCodeTransform == actual.StoredCodeTransform &&
           expected.LevelCodeSpace == actual.LevelCodeSpace && expected.Readout == actual.Readout;

    private static Linear16Frame ToFrame(ProcessingArtifact artifact)
        => new(
            artifact.Layout!.Width,
            artifact.Layout.Height,
            artifact.Layout.StrideBytes,
            artifact.Layout.PixelFormat,
            artifact.Payload);
}
