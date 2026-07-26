using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public static class CalibrationLibraryBundleSources
{
    public const string LegacySyntheticV1 = "legacy-synthetic-v1";
    public const string VirtualAcquisitionV1 = "virtual-acquisition-v1";
}

public static class CalibrationLibraryArtifactRoles
{
    public const string Source = "source";
    public const string Master = "master";
}

public static class CalibrationMasterBuildAlgorithms
{
    public const string MedianV1 = "calibration-median-v1";
    public const string BitwiseOrV1 = "calibration-bitwise-or-v1";
}

public static class CalibrationLibraryReasonCodes
{
    public const string InvalidJson = "calibration.library.invalid-json";
    public const string PayloadTooLarge = "calibration.library.payload-too-large";
    public const string UnsupportedSchema = "calibration.library.unsupported-schema";
    public const string InvalidBundle = "calibration.library.invalid-bundle";
    public const string Missing = "calibration.library.missing";
    public const string Stale = "calibration.library.stale";
    public const string Corrupt = "calibration.library.corrupt";
    public const string Incomplete = "calibration.library.incomplete";
    public const string Ambiguous = "calibration.library.ambiguous";
    public const string IncompatibleIdentity = "calibration.library.incompatible-identity";
    public const string IncompatibleReadout = "calibration.library.incompatible-readout";
    public const string IncompatibleConditions = "calibration.library.incompatible-conditions";
    public const string IncompatibleExposure = "calibration.library.incompatible-exposure";
    public const string IncompatibleCodeSpace = "calibration.library.incompatible-code-space";
    public const string Inactive = "calibration.library.inactive";
    public const string PublicationConflict = "calibration.library.publication-conflict";
    public const string AcquisitionFailure = "calibration.library.acquisition-failure";
    public const string MasterBuildFailure = "calibration.library.master-build-failure";
}

public sealed record CalibrationMasterBuildOptionsV1(
    [property: JsonRequired] string ReferenceKind,
    [property: JsonRequired] int SourceCount);

public sealed record CalibrationApplicabilityV1(
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] string RigId,
    [property: JsonRequired] string RigProfileSha256,
    [property: JsonRequired] string SensorProfileSha256,
    [property: JsonRequired] FrameLayoutDescriptor InputLayout,
    [property: JsonRequired] FrameLayoutDescriptor OutputLayout,
    [property: JsonRequired] double MinimumGain,
    [property: JsonRequired] double MaximumGain,
    double? MinimumOffset,
    double? MaximumOffset,
    TimeSpan? MinimumLightExposure,
    TimeSpan? MaximumLightExposure,
    double? MinimumTemperatureC,
    double? MaximumTemperatureC,
    [property: JsonRequired] DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc);

public sealed record CalibrationLibraryArtifactV1(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string Role,
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] string ManifestRelativePath,
    [property: JsonRequired] string PayloadSha256,
    [property: JsonRequired] TimeSpan Exposure,
    [property: JsonRequired] double Gain,
    double? Offset,
    double? TemperatureC,
    int? SourceIndex,
    [property: JsonRequired] IReadOnlyList<Guid> OrderedSourceArtifactIds,
    RecipeIdentityDescriptor? MasterBuildRecipe);

public sealed record CalibrationLibraryBundleV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string BundleId,
    [property: JsonRequired] string Source,
    [property: JsonRequired] DateTimeOffset CreatedUtc,
    [property: JsonRequired] string ProfileRelativePath,
    [property: JsonRequired] string ProfileIdentitySha256,
    [property: JsonRequired] string AcquisitionModelIdentitySha256,
    [property: JsonRequired] CalibrationApplicabilityV1 Applicability,
    [property: JsonRequired] IReadOnlyList<CalibrationLibraryArtifactV1> Artifacts)
{
    public const string CurrentSchemaVersion = "calibration-library-bundle-v1";
}

public sealed record CalibrationLibraryBundleParseResult(
    CalibrationLibraryBundleV1? Value,
    CaptureContractValidationResult Validation);

public static class CalibrationLibraryContract
{
    private static readonly string[] ReservedPathStems =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public static CaptureContractValidationResult Validate(CalibrationLibraryBundleV1? bundle)
    {
        if (bundle is null ||
            !string.Equals(bundle.SchemaVersion, CalibrationLibraryBundleV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(bundle.BundleId) || bundle.BundleId.Length > 128 ||
            bundle.Source is not (CalibrationLibraryBundleSources.LegacySyntheticV1 or CalibrationLibraryBundleSources.VirtualAcquisitionV1) ||
            bundle.CreatedUtc.Offset != TimeSpan.Zero ||
            !ValidRelativePath(bundle.ProfileRelativePath) ||
            !Sha256(bundle.ProfileIdentitySha256) || !Sha256(bundle.AcquisitionModelIdentitySha256) ||
            bundle.Applicability is null || bundle.Artifacts is null ||
            bundle.Artifacts.Any(static artifact => artifact is null))
        {
            return Failure("bundle");
        }

        var applicability = ValidateApplicability(bundle.Applicability, bundle.Source);
        if (!applicability.IsValid)
        {
            return applicability;
        }
        if (bundle.Artifacts.Count == 0 ||
            bundle.Artifacts.Select(static artifact => artifact.ArtifactId).Distinct().Count() != bundle.Artifacts.Count ||
            bundle.Artifacts.Select(static artifact => artifact.ManifestRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bundle.Artifacts.Count ||
            bundle.Artifacts.Any(artifact => string.Equals(
                artifact.ManifestRelativePath,
                bundle.ProfileRelativePath,
                StringComparison.OrdinalIgnoreCase)))
        {
            return Failure("bundle.artifacts");
        }
        foreach (var artifact in bundle.Artifacts)
        {
            var artifactValidation = ValidateArtifact(artifact);
            if (!artifactValidation.IsValid)
            {
                return artifactValidation;
            }
        }

        var masters = bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master).ToArray();
        var masterKinds = masters.Select(static artifact => artifact.Kind).ToArray();
        if (masters.Length != CalibrationReferenceKinds.All.Count ||
            masterKinds.Distinct(StringComparer.Ordinal).Count() != CalibrationReferenceKinds.All.Count ||
            CalibrationReferenceKinds.All.Any(kind => !masterKinds.Contains(kind, StringComparer.Ordinal)))
        {
            return Failure("bundle.artifacts.masters");
        }

        var sources = bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Source).ToArray();
        if (bundle.Source == CalibrationLibraryBundleSources.LegacySyntheticV1)
        {
            return sources.Length == 0 && masters.All(static master =>
                    master.OrderedSourceArtifactIds.Count == 0 && master.MasterBuildRecipe is null && master.Offset is null) &&
                   bundle.Applicability.MinimumOffset is null && bundle.Applicability.MaximumOffset is null &&
                   bundle.Applicability.MinimumLightExposure is null && bundle.Applicability.MaximumLightExposure is null &&
                   LegacyFactsMatch(bundle.Applicability, masters)
                ? CaptureContractValidationResult.Success
                : Failure("bundle.artifacts.legacy");
        }

        foreach (var kind in CalibrationReferenceKinds.All)
        {
            var kindSources = sources.Where(source => source.Kind == kind).OrderBy(static source => source.SourceIndex).ToArray();
            var master = masters.Single(candidate => candidate.Kind == kind);
            if (kindSources.Length != 3 || !kindSources.Select(static source => source.SourceIndex).SequenceEqual([0, 1, 2]) ||
                !CompleteVirtualConditions(master) ||
                kindSources.Any(source => !CompleteVirtualConditions(source) || !ConditionsMatch(master, source)) ||
                !ConditionsMatchApplicability(master, bundle.Applicability) ||
                master.MasterBuildRecipe is null ||
                !master.OrderedSourceArtifactIds.SequenceEqual(kindSources.Select(static source => source.ArtifactId)) ||
                !ValidMasterRecipe(kind, master.MasterBuildRecipe))
            {
                return Failure($"bundle.artifacts.{kind}");
            }
        }
        return sources.Length == CalibrationReferenceKinds.All.Count * 3
            ? CaptureContractValidationResult.Success
            : Failure("bundle.artifacts.sources");
    }

    private static CaptureContractValidationResult ValidateApplicability(
        CalibrationApplicabilityV1 applicability,
        string source)
    {
        if (string.IsNullOrWhiteSpace(applicability.AgentId) || string.IsNullOrWhiteSpace(applicability.RigId) ||
            applicability.AgentId.Length > 128 || applicability.RigId.Length > 128 ||
            !Sha256(applicability.RigProfileSha256) || !Sha256(applicability.SensorProfileSha256) ||
            applicability.InputLayout is null || applicability.OutputLayout is null ||
            !applicability.InputLayout.Validate().IsValid || !applicability.OutputLayout.Validate().IsValid ||
            applicability.InputLayout.Width != applicability.OutputLayout.Width ||
            applicability.InputLayout.Height != applicability.OutputLayout.Height ||
            applicability.InputLayout.PixelFormat != applicability.OutputLayout.PixelFormat ||
            applicability.InputLayout.CfaPattern != applicability.OutputLayout.CfaPattern ||
            applicability.InputLayout.Readout != applicability.OutputLayout.Readout ||
            applicability.InputLayout.ByteOrder != FrameByteOrder.LittleEndian ||
            applicability.OutputLayout.ByteOrder != FrameByteOrder.LittleEndian ||
            applicability.InputLayout.ContainerDepthBits != 16 ||
            applicability.InputLayout.Packing != FrameSamplePacking.ByteAligned ||
            applicability.InputLayout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            applicability.OutputLayout.SampleDepthBits != 16 || applicability.OutputLayout.ContainerDepthBits != 16 ||
            applicability.OutputLayout.Packing != FrameSamplePacking.ByteAligned ||
            applicability.OutputLayout.StrideBytes != (long)applicability.OutputLayout.Width * 2 ||
            applicability.OutputLayout.BlackLevel != 0 || applicability.OutputLayout.WhiteLevel != ushort.MaxValue ||
            applicability.OutputLayout.StoredCodeTransform != FrameStoredCodeTransform.IdentityV1 ||
            applicability.OutputLayout.LevelCodeSpace != FrameLevelCodeSpace.StoredContainer ||
            !Range(applicability.MinimumGain, applicability.MaximumGain, allowNegative: false) ||
            !NullableRange(applicability.MinimumOffset, applicability.MaximumOffset, allowNegative: true) ||
            !NullableRange(applicability.MinimumTemperatureC, applicability.MaximumTemperatureC, allowNegative: true) ||
            !ExposureRange(applicability.MinimumLightExposure, applicability.MaximumLightExposure) ||
            applicability.EffectiveFromUtc.Offset != TimeSpan.Zero ||
            applicability.EffectiveUntilUtc is { } effectiveUntil && effectiveUntil.Offset != TimeSpan.Zero ||
            applicability.EffectiveUntilUtc is { } until && until <= applicability.EffectiveFromUtc)
        {
            return Failure("bundle.applicability");
        }
        if (source == CalibrationLibraryBundleSources.VirtualAcquisitionV1 &&
            (applicability.MinimumOffset is null || applicability.MaximumOffset is null ||
             applicability.MinimumTemperatureC is null || applicability.MaximumTemperatureC is null ||
             applicability.MinimumLightExposure is null || applicability.MaximumLightExposure is null ||
             applicability.MinimumGain != applicability.MaximumGain ||
             applicability.MinimumOffset != applicability.MaximumOffset ||
             applicability.MinimumTemperatureC != applicability.MaximumTemperatureC ||
             applicability.InputLayout.Readout is null || applicability.InputLayout.StoredCodeTransform is null ||
             applicability.InputLayout.LevelCodeSpace is null))
        {
            return Failure("bundle.applicability.virtual");
        }
        return CaptureContractValidationResult.Success;
    }

    private static CaptureContractValidationResult ValidateArtifact(CalibrationLibraryArtifactV1 artifact)
    {
        if (!CalibrationReferenceKinds.All.Contains(artifact.Kind, StringComparer.Ordinal) ||
            artifact.Role is not (CalibrationLibraryArtifactRoles.Source or CalibrationLibraryArtifactRoles.Master) ||
            artifact.ArtifactId == Guid.Empty || !ValidRelativePath(artifact.ManifestRelativePath) ||
            !Sha256(artifact.PayloadSha256) || artifact.Exposure <= TimeSpan.Zero ||
            !double.IsFinite(artifact.Gain) || artifact.Gain < 0 ||
            artifact.Offset is { } offset && !double.IsFinite(offset) ||
            artifact.TemperatureC is { } temperature && !double.IsFinite(temperature) ||
            artifact.OrderedSourceArtifactIds is null || artifact.OrderedSourceArtifactIds.Any(static id => id == Guid.Empty) ||
            artifact.OrderedSourceArtifactIds.Distinct().Count() != artifact.OrderedSourceArtifactIds.Count)
        {
            return Failure("bundle.artifacts");
        }
        if (artifact.Role == CalibrationLibraryArtifactRoles.Source)
        {
            return artifact.SourceIndex is >= 0 and < 3 && artifact.OrderedSourceArtifactIds.Count == 0 &&
                   artifact.MasterBuildRecipe is null
                ? CaptureContractValidationResult.Success
                : Failure("bundle.artifacts.source");
        }
        return artifact.SourceIndex is null
            ? CaptureContractValidationResult.Success
            : Failure("bundle.artifacts.master");
    }

    private static bool ValidMasterRecipe(string kind, RecipeIdentityDescriptor recipe)
    {
        var expected = kind == CalibrationReferenceKinds.Defect
            ? CalibrationMasterBuildAlgorithms.BitwiseOrV1
            : CalibrationMasterBuildAlgorithms.MedianV1;
        if (recipe.Options.ValueKind != JsonValueKind.Object ||
            !string.Equals(recipe.Name, "calibration-master-build", StringComparison.Ordinal) ||
            !string.Equals(recipe.SemanticVersion, "1.0.0", StringComparison.Ordinal) ||
            !string.Equals(recipe.ImplementationVersion, expected, StringComparison.Ordinal) ||
            !Sha256(recipe.OptionsSha256) ||
            !string.Equals(
                CaptureContractJson.ComputeCanonicalJsonSha256(recipe.Options),
                recipe.OptionsSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            var properties = recipe.Options.EnumerateObject().ToArray();
            if (properties.Length != 2 ||
                !recipe.Options.TryGetProperty("referenceKind", out _) ||
                !recipe.Options.TryGetProperty("sourceCount", out _) ||
                properties.Any(static property => property.Name is not ("referenceKind" or "sourceCount")))
            {
                return false;
            }
            var options = recipe.Options.Deserialize<CalibrationMasterBuildOptionsV1>(ContractOptions);
            return options is not null && options.SourceCount == 3 &&
                   string.Equals(options.ReferenceKind, kind, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions ContractOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static bool CompleteVirtualConditions(CalibrationLibraryArtifactV1 artifact)
        => artifact.Offset is not null && artifact.TemperatureC is not null;

    private static bool ConditionsMatch(CalibrationLibraryArtifactV1 expected, CalibrationLibraryArtifactV1 actual)
        => expected.Exposure == actual.Exposure && expected.Gain == actual.Gain &&
           expected.Offset == actual.Offset && expected.TemperatureC == actual.TemperatureC;

    private static bool ConditionsMatchApplicability(
        CalibrationLibraryArtifactV1 artifact,
        CalibrationApplicabilityV1 applicability)
        => artifact.Gain == applicability.MinimumGain && artifact.Gain == applicability.MaximumGain &&
           artifact.Offset == applicability.MinimumOffset && artifact.Offset == applicability.MaximumOffset &&
           artifact.TemperatureC == applicability.MinimumTemperatureC &&
           artifact.TemperatureC == applicability.MaximumTemperatureC;

    private static bool LegacyFactsMatch(
        CalibrationApplicabilityV1 applicability,
        IReadOnlyList<CalibrationLibraryArtifactV1> masters)
    {
        return applicability.MinimumTemperatureC is { } minimum && applicability.MaximumTemperatureC is { } maximum &&
               masters.All(master => master.Gain >= applicability.MinimumGain && master.Gain <= applicability.MaximumGain &&
                   master.TemperatureC is { } temperature && temperature >= minimum && temperature <= maximum);
    }

    private static bool Range(double minimum, double maximum, bool allowNegative)
        => double.IsFinite(minimum) && double.IsFinite(maximum) && maximum >= minimum && (allowNegative || minimum >= 0);

    private static bool NullableRange(double? minimum, double? maximum, bool allowNegative)
        => minimum is null && maximum is null ||
           minimum is { } lower && maximum is { } upper && Range(lower, upper, allowNegative);

    private static bool ExposureRange(TimeSpan? minimum, TimeSpan? maximum)
        => minimum is null && maximum is null ||
           minimum is { } lower && maximum is { } upper && lower > TimeSpan.Zero && upper >= lower;

    private static bool Sha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool ValidRelativePath(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && value[0] != '/' &&
           !value.Contains('\\', StringComparison.Ordinal) && !value.Contains(':', StringComparison.Ordinal) &&
           value.Split('/').All(ValidPathSegment);

    private static bool ValidPathSegment(string segment)
    {
        if (segment.Length is 0 or > 128 || segment[0] == '.' || segment[^1] == '.' ||
            segment.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            return false;
        }
        var stem = segment.Split('.')[0];
        return !ReservedPathStems.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static CaptureContractValidationResult Failure(string path)
        => CaptureContractValidationResult.Failure(CalibrationLibraryReasonCodes.InvalidBundle, path);
}

public static class CalibrationLibraryContractJson
{
    public const int MaximumBundleBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(CalibrationLibraryBundleV1 bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var validation = CalibrationLibraryContract.Validate(bundle);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Calibration bundle is invalid at '{validation.FieldPath}'.", nameof(bundle));
        }
        var normalized = Normalize(bundle);
        var element = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(normalized, SerializerOptions));
        return JsonSerializer.SerializeToUtf8Bytes(element);
    }

    public static string ComputeIdentitySha256(CalibrationLibraryBundleV1 bundle)
        => Convert.ToHexString(SHA256.HashData(Serialize(bundle)));

    public static CalibrationLibraryBundleParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumBundleBytes)
        {
            return Failure(CalibrationLibraryReasonCodes.PayloadTooLarge, "$");
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperty(document.RootElement) ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                return Failure(CalibrationLibraryReasonCodes.InvalidJson, "$");
            }
            if (!string.Equals(schema.GetString(), CalibrationLibraryBundleV1.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return Failure(CalibrationLibraryReasonCodes.UnsupportedSchema, "schemaVersion");
            }
            var value = JsonSerializer.Deserialize<CalibrationLibraryBundleV1>(utf8Json.Span, SerializerOptions);
            var validation = CalibrationLibraryContract.Validate(value);
            return validation.IsValid ? new(Normalize(value!), validation) : new(null, validation);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Failure(CalibrationLibraryReasonCodes.InvalidJson, "$");
        }
    }

    private static CalibrationLibraryBundleParseResult Failure(string reasonCode, string path)
        => new(null, CaptureContractValidationResult.Failure(reasonCode, path));

    private static bool HasDuplicateProperty(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (HasDuplicateProperty(item))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static CalibrationLibraryBundleV1 Normalize(CalibrationLibraryBundleV1 bundle)
        => bundle with
        {
            ProfileIdentitySha256 = bundle.ProfileIdentitySha256.ToUpperInvariant(),
            AcquisitionModelIdentitySha256 = bundle.AcquisitionModelIdentitySha256.ToUpperInvariant(),
            Applicability = bundle.Applicability with
            {
                RigProfileSha256 = bundle.Applicability.RigProfileSha256.ToUpperInvariant(),
                SensorProfileSha256 = bundle.Applicability.SensorProfileSha256.ToUpperInvariant()
            },
            Artifacts = bundle.Artifacts
                .OrderBy(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Source ? 0 : 1)
                .ThenBy(static artifact => KindRank(artifact.Kind))
                .ThenBy(static artifact => artifact.SourceIndex)
                .Select(static artifact => artifact with
                {
                    PayloadSha256 = artifact.PayloadSha256.ToUpperInvariant(),
                    MasterBuildRecipe = artifact.MasterBuildRecipe is null
                        ? null
                        : artifact.MasterBuildRecipe with
                        {
                            Options = CaptureContractJson.Canonicalize(artifact.MasterBuildRecipe.Options),
                            OptionsSha256 = artifact.MasterBuildRecipe.OptionsSha256.ToUpperInvariant()
                        }
                })
                .ToArray()
        };

    private static int KindRank(string kind)
        => kind switch
        {
            CalibrationReferenceKinds.Bias => 0,
            CalibrationReferenceKinds.Dark => 1,
            CalibrationReferenceKinds.Flat => 2,
            CalibrationReferenceKinds.Defect => 3,
            _ => int.MaxValue
        };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
