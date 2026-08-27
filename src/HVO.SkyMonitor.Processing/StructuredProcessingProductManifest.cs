using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Immutable transport facts for one layoutless structured processing product.</summary>
public sealed record StructuredProcessingProductDescriptorV1(
    [property: JsonRequired] ReconstructionDescriptor SourceCapture,
    [property: JsonRequired] ArtifactDescriptor Artifact,
    [property: JsonRequired] string OutputIdentitySha256,
    [property: JsonRequired] IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    [property: JsonRequired] ProcessingCompatibilityIdentity Compatibility,
    [property: JsonRequired] long TotalIntegrationTicks,
    [property: JsonRequired] long ByteLength,
    [property: JsonRequired] ProcessingProductKind Kind,
    [property: JsonRequired] string ProductSchemaVersion,
    [property: JsonRequired] string ContentIdentitySha256)
{
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumAlgorithmsJsonCharacters = 4000;
    public const int MaximumCompatibilityJsonCharacters = 4000;
    public const int MaximumDescriptorJsonCharacters = 65_536;

    public CaptureContractValidationResult Validate()
    {
        var sourceCapture = SourceCapture;
        var artifact = Artifact;
        if (sourceCapture is null || artifact is null)
        {
            return Failure("descriptor");
        }
        var sourceValidation = sourceCapture.Validate();
        if (!sourceValidation.IsValid)
        {
            return sourceValidation;
        }
        var artifactValidation = artifact.Validate();
        if (!artifactValidation.IsValid)
        {
            return artifactValidation;
        }
        if (artifact.Role != FrameArtifactRole.Metadata || Kind != ProcessingProductKind.Metadata ||
            !StructuredProcessingProductContracts.IsSupported(artifact.MediaType, ProductSchemaVersion))
        {
            return Failure("artifact.mediaType");
        }
        if (artifact.ArtifactId == sourceCapture.Capture.CaptureId ||
            artifact.SourceArtifactIds.Contains(sourceCapture.Capture.CaptureId) ||
            artifact.SourceArtifactIds.Count is < 1 or > LayeredPresentationJson.MaximumSourceArtifactCount)
        {
            return Failure("artifact.sourceArtifactIds", CaptureContractReasonCodes.InvalidLineage);
        }
        if (!IsCanonicalSha256(OutputIdentitySha256) ||
            !IsCanonicalSha256(ContentIdentitySha256) ||
            string.IsNullOrWhiteSpace(ProductSchemaVersion) || ProductSchemaVersion.Length > 128 ||
            ByteLength is < 1 or > MaximumPayloadBytes || TotalIntegrationTicks < 0)
        {
            return Failure("product");
        }
        if (Algorithms is null || Algorithms.Count > 64 || Algorithms.Any(static algorithm =>
                algorithm is null || string.IsNullOrWhiteSpace(algorithm.Name) || algorithm.Name.Length > 128 ||
                string.IsNullOrWhiteSpace(algorithm.Version) || algorithm.Version.Length > 128) ||
            !IsValidCompatibility(Compatibility) ||
            JsonSerializer.Serialize(Algorithms).Length > MaximumAlgorithmsJsonCharacters ||
            JsonSerializer.Serialize(Compatibility).Length > MaximumCompatibilityJsonCharacters ||
            JsonSerializer.Serialize(this).Length > MaximumDescriptorJsonCharacters)
        {
            return Failure("provenance");
        }

        var recipe = ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe);
        var expectedOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            artifact.Role,
            artifact.Variant,
            recipe.IdentitySha256,
            artifact.SourceArtifactIds);
        return string.Equals(OutputIdentitySha256, expectedOutputIdentity, StringComparison.Ordinal) &&
            artifact.ArtifactId == ProcessingIdentity.CreateArtifactId(expectedOutputIdentity)
            ? CaptureContractValidationResult.Success
            : Failure("outputIdentitySha256", CaptureContractReasonCodes.InvalidIdentity);
    }

    private static bool IsValidCompatibility(ProcessingCompatibilityIdentity? compatibility)
        => compatibility is not null &&
           IsBounded(compatibility.Rig) && IsBounded(compatibility.Orientation) &&
           IsBounded(compatibility.Calibration) && IsBounded(compatibility.Mask) &&
           IsBounded(compatibility.Sensor) && IsBounded(compatibility.SetpointRegime) &&
           IsBounded(compatibility.ProcessingProfile) &&
           (compatibility.LocationIdentitySha256 is null || IsCanonicalSha256(compatibility.LocationIdentitySha256));

    private static bool IsBounded(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

    private static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static CaptureContractValidationResult Failure(
        string path,
        string reasonCode = CaptureContractReasonCodes.InvalidIdentity)
        => CaptureContractValidationResult.Failure(reasonCode, path);
}

/// <summary>Structured product media types and schema versions supported end to end by edge and central hosts.</summary>
public static class StructuredProcessingProductContracts
{
    public const string ProjectedSceneMediaType = "application/vnd.hvo.projected-scene+json";
    public const string CloudAssessmentMediaType = "application/vnd.hvo.cloud-assessment+json";

    public static bool IsSupported(string mediaType, string schemaVersion)
        => (mediaType, schemaVersion) switch
        {
            (ProjectedSceneMediaType, ProjectedSceneV1.CurrentSchemaVersion) => true,
            (CloudAssessmentMediaType, CloudAssessmentV1.CurrentSchemaVersion) => true,
            (PresentationLayerPayloadJson.MediaType, PresentationLayerPayloadV1.CurrentSchemaVersion) => true,
            (PresentationProcessingProducts.ManifestMediaType, OverlayManifestV1.CurrentSchemaVersion) => true,
            (PresentationMetadataFactsProductV1.MediaType,
                PresentationMetadataFactsProductV1.LegacySchemaVersion or
                PresentationMetadataFactsProductV1.CurrentSchemaVersion) => true,
            _ => false
        };

    public static bool IsSupportedMediaType(string mediaType)
        => mediaType is ProjectedSceneMediaType or CloudAssessmentMediaType or
            PresentationLayerPayloadJson.MediaType or PresentationProcessingProducts.ManifestMediaType or
            PresentationMetadataFactsProductV1.MediaType;
}

/// <summary>Versioned envelope for ordinary outbox delivery of one structured product.</summary>
public sealed record StructuredProcessingProductManifestV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] StructuredProcessingProductDescriptorV1 Descriptor,
    [property: JsonRequired] string RelativeArtifactPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProducerStepId = null)
{
    public const string CurrentSchemaVersion = "hvo-structured-processing-product-v1";

    public string IdempotencyKey => StructuredProcessingProductManifestJson.ComputeDescriptorSha256(Descriptor);

    public CaptureContractValidationResult Validate()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return CaptureContractValidationResult.Failure(CaptureContractReasonCodes.UnsupportedSchema, "schemaVersion");
        }
        if (!IsSafeRelativePath(RelativeArtifactPath))
        {
            return CaptureContractValidationResult.Failure(CaptureContractReasonCodes.InvalidPath, "relativeArtifactPath");
        }
        if (ProducerStepId is not null &&
            (string.IsNullOrWhiteSpace(ProducerStepId) || ProducerStepId.Length > 128))
        {
            return CaptureContractValidationResult.Failure(CaptureContractReasonCodes.InvalidIdentity, "producerStepId");
        }
        var descriptorValidation = Descriptor?.Validate() ?? CaptureContractValidationResult.Failure(
            CaptureContractReasonCodes.InvalidIdentity, "descriptor");
        if (!descriptorValidation.IsValid)
        {
            return descriptorValidation;
        }
        return CaptureContractValidationResult.Success;
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal) ||
            path.StartsWith('/') || path.StartsWith('\\') || path.Length >= 2 && path[1] == ':')
        {
            return false;
        }
        return !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or "..");
    }
}

public sealed record StructuredProcessingProductManifestParseResult(
    StructuredProcessingProductManifestV1? Manifest,
    CaptureContractValidationResult Validation)
{
    public bool IsValid => Manifest is not null && Validation.IsValid;
}

/// <summary>Canonical JSON operations for structured product delivery.</summary>
public static class StructuredProcessingProductManifestJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly JsonSerializerOptions LegacyDescriptorSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Serialize(StructuredProcessingProductManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var validation = manifest.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Structured processing product manifest is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(manifest));
        }
        return JsonSerializer.SerializeToUtf8Bytes(Canonicalize(manifest), SerializerOptions);
    }

    public static byte[] SerializeDescriptor(StructuredProcessingProductDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var validation = descriptor.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Structured processing product descriptor is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(descriptor));
        }
        return JsonSerializer.SerializeToUtf8Bytes(Canonicalize(descriptor), SerializerOptions);
    }

    public static StructuredProcessingProductDescriptorV1 ParseDescriptor(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        RejectDuplicateProperties(document.RootElement);
        StructuredProcessingProductDescriptorV1? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<StructuredProcessingProductDescriptorV1>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            // DescriptorJson written before canonical transport persistence used numeric enums.
            descriptor = JsonSerializer.Deserialize<StructuredProcessingProductDescriptorV1>(
                json, LegacyDescriptorSerializerOptions);
        }
        descriptor = descriptor ?? throw new InvalidDataException(
            "The structured processing product descriptor is empty.");
        var validation = descriptor.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"The structured processing product descriptor is invalid ({validation.ReasonCode}:{validation.FieldPath}).");
        }
        return descriptor;
    }

    public static StructuredProcessingProductManifestParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            RejectDuplicateProperties(document.RootElement);
            var manifest = JsonSerializer.Deserialize<StructuredProcessingProductManifestV1>(utf8Json.Span, SerializerOptions);
            if (manifest is null)
            {
                return Failure(CaptureContractReasonCodes.InvalidJson, "$");
            }
            var validation = manifest.Validate();
            return validation.IsValid ? new(manifest, validation) : new(null, validation);
        }
        catch (JsonException)
        {
            return Failure(CaptureContractReasonCodes.InvalidJson, "$");
        }
    }

    public static string ComputeDescriptorSha256(StructuredProcessingProductDescriptorV1 descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return CaptureContractJson.ComputeCanonicalJsonSha256(Canonicalize(descriptor));
    }

    private static StructuredProcessingProductManifestV1 Canonicalize(StructuredProcessingProductManifestV1 manifest)
        => manifest with { Descriptor = Canonicalize(manifest.Descriptor) };

    private static StructuredProcessingProductDescriptorV1 Canonicalize(StructuredProcessingProductDescriptorV1 descriptor)
        => descriptor with
        {
            SourceCapture = descriptor.SourceCapture with
            {
                Artifact = NormalizeArtifact(descriptor.SourceCapture.Artifact),
                Profiles = descriptor.SourceCapture.Profiles with
                {
                    Rig = NormalizeProfile(descriptor.SourceCapture.Profiles.Rig),
                    Calibration = NormalizeProfile(descriptor.SourceCapture.Profiles.Calibration),
                    Mask = NormalizeProfile(descriptor.SourceCapture.Profiles.Mask),
                    Sensor = NormalizeProfile(descriptor.SourceCapture.Profiles.Sensor),
                    Processing = NormalizeProfile(descriptor.SourceCapture.Profiles.Processing)
                }
            },
            Artifact = NormalizeArtifact(descriptor.Artifact),
            OutputIdentitySha256 = descriptor.OutputIdentitySha256.ToUpperInvariant(),
            ContentIdentitySha256 = descriptor.ContentIdentitySha256.ToUpperInvariant(),
            Compatibility = descriptor.Compatibility with
            {
                LocationIdentitySha256 = descriptor.Compatibility.LocationIdentitySha256?.ToUpperInvariant()
            }
        };

    private static ArtifactDescriptor NormalizeArtifact(ArtifactDescriptor artifact)
        => artifact with
        {
            ChecksumSha256 = artifact.ChecksumSha256.ToUpperInvariant(),
            Recipe = artifact.Recipe with
            {
                Options = CaptureContractJson.Canonicalize(artifact.Recipe.Options),
                OptionsSha256 = artifact.Recipe.OptionsSha256.ToUpperInvariant()
            }
        };

    private static ProfileIdentityDescriptor NormalizeProfile(ProfileIdentityDescriptor profile)
        => profile with { Sha256 = profile.Sha256.ToUpperInvariant() };

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Structured processing product manifest contains duplicate properties.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static StructuredProcessingProductManifestParseResult Failure(string reasonCode, string path)
        => new(null, CaptureContractValidationResult.Failure(reasonCode, path));

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
