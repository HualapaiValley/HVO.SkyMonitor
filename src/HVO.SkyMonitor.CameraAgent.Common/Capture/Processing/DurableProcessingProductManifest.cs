using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

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
    [property: JsonRequired] JsonElement? Layout)
{
    internal const string CurrentSchemaVersion = "hvo-cameraagent-processing-product-v1";
}

internal static class DurableProcessingProductManifestJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    internal static byte[] Serialize(DurableProcessingProductManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        var element = JsonSerializer.SerializeToElement(manifest, SerializerOptions);
        return JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
    }

    internal static DurableProcessingProductManifestV1 Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("layout", out var layout) ||
                layout.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidDataException("Durable processing product layout must be explicitly null.");
            }
            var manifest = JsonSerializer.Deserialize<DurableProcessingProductManifestV1>(json.Span, SerializerOptions)
                ?? throw new InvalidDataException("Durable processing product manifest is empty.");
            Validate(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Durable processing product manifest is invalid.", exception);
        }
    }

    private static void Validate(DurableProcessingProductManifestV1 manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, DurableProcessingProductManifestV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Durable processing product manifest schema is unsupported.");
        }
        if (manifest.Capture is null || string.IsNullOrWhiteSpace(manifest.Capture.AgentId) ||
            string.IsNullOrWhiteSpace(manifest.Capture.RigId) || manifest.Capture.CaptureSequence < 1 ||
            manifest.Capture.CaptureId == Guid.Empty)
        {
            throw new InvalidDataException("Durable processing product capture identity is invalid.");
        }
        if (manifest.Artifact is null || manifest.Artifact.ArtifactId == Guid.Empty ||
            manifest.Artifact.ArtifactId == manifest.Capture.CaptureId ||
            manifest.Artifact.Role != FrameArtifactRole.Metadata ||
            string.IsNullOrWhiteSpace(manifest.Artifact.SourceId) ||
            string.IsNullOrWhiteSpace(manifest.Artifact.Variant) ||
            manifest.Artifact.CreatedUtc.Offset != TimeSpan.Zero ||
            manifest.Artifact.SourceArtifactIds is null || manifest.Artifact.SourceArtifactIds.Count == 0 ||
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
            !IsJsonMediaType(manifest.Artifact.MediaType) ||
            !IsSha256(manifest.Artifact.ChecksumSha256))
        {
            throw new InvalidDataException("Durable processing product artifact descriptor is invalid.");
        }
        if (manifest.Layout is { ValueKind: not JsonValueKind.Null })
        {
            throw new InvalidDataException("Durable processing product layout must be explicitly null.");
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
        => value is { Length: 64 } && value.All(static character => Uri.IsHexDigit(character));

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
            var names = new HashSet<string>(StringComparer.Ordinal);
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
