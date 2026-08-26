using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal interface IDurableProcessingProductManifest
{
    string SchemaVersion { get; }
    CaptureIdentityDescriptor Capture { get; }
    ArtifactDescriptor Artifact { get; }
    string OutputIdentitySha256 { get; }
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms { get; }
    ProcessingCompatibilityIdentity Compatibility { get; }
    long TotalIntegrationTicks { get; }
    long ByteLength { get; }
    string RelativeArtifactPath { get; }
    JsonElement? Layout { get; }
    string? ProducerStepId { get; }
    ProcessingProductKind Kind { get; }
    string? ProductSchemaVersion { get; }
    string? ContentIdentitySha256 { get; }
}

internal sealed record DurableProcessingProductManifestV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] CaptureIdentityDescriptor Capture,
    [property: JsonRequired] ArtifactDescriptor Artifact,
    [property: JsonRequired] string OutputIdentitySha256,
    [property: JsonRequired] IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    [property: JsonRequired] ProcessingCompatibilityIdentity Compatibility,
    [property: JsonRequired] long TotalIntegrationTicks,
    [property: JsonRequired] long ByteLength,
    [property: JsonRequired] string RelativeArtifactPath,
    [property: JsonRequired] JsonElement? Layout) : IDurableProcessingProductManifest
{
    internal const string CurrentSchemaVersion = "hvo-cameraagent-processing-product-v1";
    string? IDurableProcessingProductManifest.ProducerStepId => null;
    ProcessingProductKind IDurableProcessingProductManifest.Kind => ProcessingProductKind.Metadata;
    string? IDurableProcessingProductManifest.ProductSchemaVersion => null;
    string? IDurableProcessingProductManifest.ContentIdentitySha256 => null;
}

internal sealed record DurableEncodedProductManifestV2(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] CaptureIdentityDescriptor Capture,
    [property: JsonRequired] ArtifactDescriptor Artifact,
    [property: JsonRequired] string OutputIdentitySha256,
    [property: JsonRequired] IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    [property: JsonRequired] ProcessingCompatibilityIdentity Compatibility,
    [property: JsonRequired] long TotalIntegrationTicks,
    [property: JsonRequired] long ByteLength,
    [property: JsonRequired] string RelativeArtifactPath,
    [property: JsonRequired] JsonElement? Layout,
    [property: JsonRequired] int EncodedWidth,
    [property: JsonRequired] int EncodedHeight,
    [property: JsonRequired] CameraPixelFormat EncodedPixelFormat,
    [property: JsonRequired] string ProducerStepId) : IDurableProcessingProductManifest
{
    internal const string CurrentSchemaVersion = "hvo-cameraagent-encoded-product-v2";
    ProcessingProductKind IDurableProcessingProductManifest.Kind => ProcessingProductKind.PixelData;
    string? IDurableProcessingProductManifest.ProductSchemaVersion => null;
    string? IDurableProcessingProductManifest.ContentIdentitySha256 => null;
}

internal sealed record DurableTypedMetadataProductManifestV3(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] CaptureIdentityDescriptor Capture,
    [property: JsonRequired] ArtifactDescriptor Artifact,
    [property: JsonRequired] string OutputIdentitySha256,
    [property: JsonRequired] IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    [property: JsonRequired] ProcessingCompatibilityIdentity Compatibility,
    [property: JsonRequired] long TotalIntegrationTicks,
    [property: JsonRequired] long ByteLength,
    [property: JsonRequired] string RelativeArtifactPath,
    [property: JsonRequired] JsonElement? Layout,
    [property: JsonRequired] ProcessingProductKind Kind,
    [property: JsonRequired] string ProductSchemaVersion,
    [property: JsonRequired] string ContentIdentitySha256) : IDurableProcessingProductManifest
{
    internal const string CurrentSchemaVersion = "hvo-cameraagent-typed-metadata-product-v3";
    string? IDurableProcessingProductManifest.ProducerStepId => null;
}

internal static class DurableProcessingProductManifestJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    internal static byte[] Serialize(IDurableProcessingProductManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        var element = manifest switch
        {
            DurableProcessingProductManifestV1 metadata => JsonSerializer.SerializeToElement(metadata, SerializerOptions),
            DurableEncodedProductManifestV2 encoded => JsonSerializer.SerializeToElement(encoded, SerializerOptions),
            DurableTypedMetadataProductManifestV3 typed => JsonSerializer.SerializeToElement(typed, SerializerOptions),
            _ => throw new InvalidDataException("Durable processing product manifest type is unsupported.")
        };
        return JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
    }

    internal static IDurableProcessingProductManifest Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("layout", out var layout) ||
                layout.ValueKind != JsonValueKind.Null ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Durable processing product manifest shape is invalid.");
            }
            IDurableProcessingProductManifest manifest = schema.GetString() switch
            {
                DurableProcessingProductManifestV1.CurrentSchemaVersion =>
                    (IDurableProcessingProductManifest?)JsonSerializer.Deserialize<DurableProcessingProductManifestV1>(json.Span, SerializerOptions),
                DurableEncodedProductManifestV2.CurrentSchemaVersion =>
                    JsonSerializer.Deserialize<DurableEncodedProductManifestV2>(json.Span, SerializerOptions),
                DurableTypedMetadataProductManifestV3.CurrentSchemaVersion =>
                    JsonSerializer.Deserialize<DurableTypedMetadataProductManifestV3>(json.Span, SerializerOptions),
                _ => throw new InvalidDataException("Durable processing product manifest schema is unsupported.")
            } ?? throw new InvalidDataException("Durable processing product manifest is empty.");
            Validate(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Durable processing product manifest is invalid.", exception);
        }
    }

    private static void Validate(IDurableProcessingProductManifest manifest)
    {
        var isMetadataV1 = manifest is DurableProcessingProductManifestV1 &&
            string.Equals(manifest.SchemaVersion, DurableProcessingProductManifestV1.CurrentSchemaVersion, StringComparison.Ordinal);
        var isEncodedV2 = manifest is DurableEncodedProductManifestV2 &&
            string.Equals(manifest.SchemaVersion, DurableEncodedProductManifestV2.CurrentSchemaVersion, StringComparison.Ordinal);
        var isTypedMetadataV3 = manifest is DurableTypedMetadataProductManifestV3 &&
            string.Equals(manifest.SchemaVersion, DurableTypedMetadataProductManifestV3.CurrentSchemaVersion, StringComparison.Ordinal);
        if (!isMetadataV1 && !isEncodedV2 && !isTypedMetadataV3)
        {
            throw new InvalidDataException("Durable processing product manifest schema and type disagree.");
        }
        if (manifest.Capture is null || string.IsNullOrWhiteSpace(manifest.Capture.AgentId) ||
            string.IsNullOrWhiteSpace(manifest.Capture.RigId) || manifest.Capture.CaptureSequence < 1 ||
            manifest.Capture.CaptureId == Guid.Empty)
        {
            throw new InvalidDataException("Durable processing product capture identity is invalid.");
        }
        if (manifest.Artifact is null || manifest.Artifact.ArtifactId == Guid.Empty ||
            manifest.Artifact.ArtifactId == manifest.Capture.CaptureId ||
            (isMetadataV1 || isTypedMetadataV3) &&
                (manifest.Artifact.Role != FrameArtifactRole.Metadata || !IsJsonMediaType(manifest.Artifact.MediaType)) ||
            isEncodedV2 && (manifest.Artifact.Role is not (FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview) ||
                !string.Equals(manifest.Artifact.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(manifest.Artifact.SourceId) ||
            string.IsNullOrWhiteSpace(manifest.Artifact.Variant) ||
            manifest.Artifact.CreatedUtc.Offset != TimeSpan.Zero ||
            manifest.Artifact.SourceArtifactIds is null || manifest.Artifact.SourceArtifactIds.Count == 0 ||
            manifest.Artifact.SourceArtifactIds.Count > LayeredPresentationJson.MaximumSourceArtifactCount ||
            manifest.Artifact.SourceArtifactIds.Any(static id => id == Guid.Empty) ||
            manifest.Artifact.SourceArtifactIds.Distinct().Count() != manifest.Artifact.SourceArtifactIds.Count ||
            manifest.Artifact.SourceArtifactIds.Contains(manifest.Artifact.ArtifactId) ||
            manifest.Artifact.SourceArtifactIds.Contains(manifest.Capture.CaptureId) ||
            manifest.Artifact.Recipe is null ||
            string.IsNullOrWhiteSpace(manifest.Artifact.Recipe.Name) ||
            string.IsNullOrWhiteSpace(manifest.Artifact.Recipe.SemanticVersion) ||
            string.IsNullOrWhiteSpace(manifest.Artifact.Recipe.ImplementationVersion) ||
            manifest.Artifact.Recipe.Options.ValueKind != JsonValueKind.Object ||
            !IsSha256(manifest.Artifact.Recipe.OptionsSha256) ||
            !IsSha256(manifest.Artifact.ChecksumSha256))
        {
            throw new InvalidDataException("Durable processing product artifact descriptor is invalid.");
        }
        if (manifest.Layout is { ValueKind: not JsonValueKind.Null } ||
            manifest is DurableEncodedProductManifestV2 encoded &&
            (encoded.EncodedWidth < 1 || encoded.EncodedHeight < 1 ||
             encoded.EncodedPixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24) ||
             string.IsNullOrWhiteSpace(encoded.ProducerStepId) || encoded.ProducerStepId.Length > 128))
        {
            throw new InvalidDataException("Durable processing product encoded layout is invalid.");
        }
        if (isTypedMetadataV3 &&
            (manifest.Kind != ProcessingProductKind.Metadata ||
             string.IsNullOrWhiteSpace(manifest.ProductSchemaVersion) || manifest.ProductSchemaVersion.Length > 128 ||
             !IsCanonicalSha256(manifest.ContentIdentitySha256)))
        {
            throw new InvalidDataException("Durable typed metadata facts are invalid.");
        }
        if (!IsSha256(manifest.OutputIdentitySha256) || manifest.ByteLength < 0 ||
            manifest.TotalIntegrationTicks < 0 || !IsSafeRelativePath(manifest.RelativeArtifactPath))
        {
            throw new InvalidDataException("Durable processing product immutable facts are invalid.");
        }
        if (manifest.Algorithms is null || manifest.Algorithms.Any(static algorithm =>
                algorithm is null || string.IsNullOrWhiteSpace(algorithm.Name) || string.IsNullOrWhiteSpace(algorithm.Version)) ||
            manifest.Compatibility is null ||
            string.IsNullOrWhiteSpace(manifest.Compatibility.Rig) ||
            string.IsNullOrWhiteSpace(manifest.Compatibility.Orientation) ||
            string.IsNullOrWhiteSpace(manifest.Compatibility.Calibration) ||
            string.IsNullOrWhiteSpace(manifest.Compatibility.Mask) ||
            string.IsNullOrWhiteSpace(manifest.Compatibility.Sensor) ||
            string.IsNullOrWhiteSpace(manifest.Compatibility.SetpointRegime) ||
            string.IsNullOrWhiteSpace(manifest.Compatibility.ProcessingProfile) ||
            manifest.Compatibility.LocationIdentitySha256 is { } locationSha256 && !IsSha256(locationSha256))
        {
            throw new InvalidDataException("Durable processing product provenance is invalid.");
        }

        var recipe = ProcessingIdentity.CreateRecipeIdentity(manifest.Artifact.Recipe);
        var expectedOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            manifest.Artifact.Role,
            manifest.Artifact.Variant,
            recipe.IdentitySha256,
            manifest.Artifact.SourceArtifactIds);
        if (!string.Equals(manifest.Artifact.Recipe.OptionsSha256,
                CaptureContractJson.ComputeCanonicalJsonSha256(manifest.Artifact.Recipe.Options),
                StringComparison.Ordinal) ||
            !string.Equals(manifest.OutputIdentitySha256, expectedOutputIdentity, StringComparison.Ordinal) ||
            manifest.Artifact.ArtifactId != ProcessingIdentity.CreateArtifactId(expectedOutputIdentity))
        {
            throw new InvalidDataException("Durable processing product identities are inconsistent.");
        }
    }

    private static bool IsJsonMediaType(string mediaType)
        => string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
           mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) &&
           mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal) ||
            Path.IsPathRooted(path) || path.Length >= 2 && path[1] == ':')
        {
            return false;
        }
        return !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or "..");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Durable processing product manifest contains duplicate properties.");
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
