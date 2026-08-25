using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

[JsonConverter(typeof(JsonStringEnumConverter<PresentationCoordinateSpace>))]
/// <summary>Identifies whether primitive coordinates are image pixels or normalized image units.</summary>
public enum PresentationCoordinateSpace { ScenePixels, NormalizedImage }

[JsonConverter(typeof(JsonStringEnumConverter<PresentationBlendMode>))]
/// <summary>Selects portable layer blending; <see cref="Lighten"/> is channel-wise maximum.</summary>
public enum PresentationBlendMode { Normal, Multiply, Screen, Lighten }

/// <summary>Geometry, sample layout, and coordinate identity required for products to share a presentation stack.</summary>
public sealed record PresentationCompatibilityDescriptor(
    [property: JsonRequired] int WidthPixels,
    [property: JsonRequired] int HeightPixels,
    [property: JsonRequired] string LayoutIdentitySha256,
    [property: JsonRequired] string CoordinateIdentitySha256);

/// <summary>A reference to an immutable product. Product bytes are never embedded.</summary>
public sealed record PresentationProductReference(
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] string ProductIdentitySha256,
    [property: JsonRequired] string MediaType,
    [property: JsonRequired] PresentationCompatibilityDescriptor Compatibility);

public sealed record PresentationLayerV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string LayerIdentitySha256,
    [property: JsonRequired] string LayerKind,
    [property: JsonRequired] PresentationProductReference SourceProduct,
    [property: JsonRequired] string? SceneIdentitySha256,
    [property: JsonRequired] PresentationCoordinateSpace CoordinateSpace,
    [property: JsonRequired] string RendererVersion,
    [property: JsonRequired] string StyleVersion,
    [property: JsonRequired] int ZOrder,
    [property: JsonRequired] PresentationBlendMode BlendMode,
    [property: JsonRequired] int OpacityMillionths,
    [property: JsonRequired] bool EnabledByDefault,
    [property: JsonRequired] JsonElement Options)
{
    public const string CurrentSchemaVersion = "presentation-layer-v1";
}

public sealed record OverlayManifestV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string ManifestIdentitySha256,
    [property: JsonRequired] PresentationProductReference BaseProduct,
    [property: JsonRequired] string? SceneIdentitySha256,
    [property: JsonRequired] IReadOnlyList<PresentationLayerV1> Layers)
{
    public const string CurrentSchemaVersion = "overlay-manifest-v1";
}

public sealed record PresentationMaterializationRequestV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string MaterializationIdentitySha256,
    [property: JsonRequired] string ManifestIdentitySha256,
    [property: JsonRequired] PresentationProductReference BaseProduct,
    [property: JsonRequired] IReadOnlyList<string> EnabledLayerIdentitySha256,
    [property: JsonRequired] string CompositorVersion,
    [property: JsonRequired] string EncoderName,
    [property: JsonRequired] string EncoderVersion,
    [property: JsonRequired] JsonElement EncoderOptions,
    [property: JsonRequired] IReadOnlyList<Guid> SourceArtifactIds)
{
    public const string CurrentSchemaVersion = "presentation-materialization-request-v1";
}

public sealed record PresentationMaterializationResultV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string MaterializationIdentitySha256,
    [property: JsonRequired] PresentationProductReference OutputProduct,
    [property: JsonRequired] string PayloadSha256,
    [property: JsonRequired] long PayloadLength)
{
    public const string CurrentSchemaVersion = "presentation-materialization-result-v1";
}

/// <summary>A strict parse result; valid documents are canonical and defensively copied.</summary>
public sealed record LayeredPresentationParseResult<T>(T? Document, string? ErrorPath) where T : class
{
    public bool IsValid => Document is not null;
}

/// <summary>Strict canonical serialization, typed parsing, validation, and independent layered-product identities.</summary>
public static class LayeredPresentationJson
{
    /// <summary>Maximum accepted presentation width or height.</summary>
    public const int MaximumDimensionPixels = 65_536;
    /// <summary>Maximum checked presentation pixel area.</summary>
    public const long MaximumPixelArea = 268_435_456;
    /// <summary>Maximum canonical uncompressed UTF-8 document size.</summary>
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumLayerCount = 256;
    public const int MaximumSourceArtifactCount = 512;
    /// <summary>Recommendation for storage; identities cover uncompressed canonical JSON.</summary>
    public const string PersistenceRecommendation = "canonical-json-utf8-compress-at-rest-v1";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static PresentationLayerV1 CreateLayer(
        string layerKind, PresentationProductReference sourceProduct, string? sceneIdentitySha256,
        PresentationCoordinateSpace coordinateSpace, string rendererVersion, string styleVersion, int zOrder,
        PresentationBlendMode blendMode, int opacityMillionths, bool enabledByDefault, JsonElement options)
    {
        var layer = new PresentationLayerV1(
            PresentationLayerV1.CurrentSchemaVersion, string.Empty, layerKind, Normalize(sourceProduct),
            NormalizeOptionalSha256(sceneIdentitySha256), coordinateSpace, rendererVersion, styleVersion, zOrder,
            blendMode, opacityMillionths, enabledByDefault, CanonicalOptions(options));
        layer = layer with { LayerIdentitySha256 = Identity(layer, static value => value with { LayerIdentitySha256 = string.Empty }) };
        Validate(layer);
        return layer;
    }

    public static OverlayManifestV1 CreateManifest(
        PresentationProductReference baseProduct, string? sceneIdentitySha256, IEnumerable<PresentationLayerV1> layers)
    {
        var bounded = MaterializeBounded(layers, MaximumLayerCount, nameof(layers));
        var ordered = Freeze(bounded.OrderBy(static layer => layer.ZOrder)
            .ThenBy(static layer => layer.LayerIdentitySha256, StringComparer.Ordinal));
        var manifest = new OverlayManifestV1(
            OverlayManifestV1.CurrentSchemaVersion, string.Empty, Normalize(baseProduct),
            NormalizeOptionalSha256(sceneIdentitySha256), ordered);
        manifest = manifest with { ManifestIdentitySha256 = Identity(manifest, static value => value with { ManifestIdentitySha256 = string.Empty }) };
        Validate(manifest);
        return manifest;
    }

    public static PresentationMaterializationRequestV1 CreateMaterializationRequest(
        OverlayManifestV1 manifest, IEnumerable<string> enabledLayerIdentitySha256, string compositorVersion,
        string encoderName, string encoderVersion, JsonElement encoderOptions, IEnumerable<Guid> sourceArtifactIds)
    {
        Validate(manifest);
        var requested = MaterializeBounded(enabledLayerIdentitySha256, MaximumLayerCount, nameof(enabledLayerIdentitySha256));
        var enabledSet = requested.Select(NormalizeSha256).ToHashSet(StringComparer.Ordinal);
        var enabled = Freeze(manifest.Layers.Where(layer => enabledSet.Contains(layer.LayerIdentitySha256))
            .Select(static layer => layer.LayerIdentitySha256));
        if (enabled.Count != enabledSet.Count)
            throw new ArgumentException("Every enabled layer must belong to the manifest.", nameof(enabledLayerIdentitySha256));
        var suppliedSources = MaterializeBounded(sourceArtifactIds, MaximumSourceArtifactCount, nameof(sourceArtifactIds));
        var selectedSources = manifest.Layers.Where(layer => enabledSet.Contains(layer.LayerIdentitySha256))
            .Select(static layer => layer.SourceProduct.ArtifactId);
        var sources = Freeze(suppliedSources.Append(manifest.BaseProduct.ArtifactId).Concat(selectedSources).Distinct());
        if (sources.Count > MaximumSourceArtifactCount)
            throw new ArgumentException("Source artifact count exceeds its bound.", nameof(sourceArtifactIds));
        var request = new PresentationMaterializationRequestV1(
            PresentationMaterializationRequestV1.CurrentSchemaVersion, string.Empty, manifest.ManifestIdentitySha256,
            manifest.BaseProduct, enabled, compositorVersion, encoderName, encoderVersion,
            CanonicalOptions(encoderOptions), sources);
        request = request with { MaterializationIdentitySha256 = Identity(request, static value => value with { MaterializationIdentitySha256 = string.Empty }) };
        Validate(request);
        return request;
    }

    public static byte[] Serialize<T>(T value) where T : class
    {
        ValidateContract(value);
        var bytes = CanonicalBytes(value);
        if (bytes.Length > MaximumPayloadBytes)
            throw new ArgumentException("Layered presentation payload exceeds 4 MiB uncompressed.", nameof(value));
        return bytes;
    }

    public static LayeredPresentationParseResult<PresentationLayerV1> ParseLayer(ReadOnlyMemory<byte> json) => Parse<PresentationLayerV1>(json);
    public static LayeredPresentationParseResult<OverlayManifestV1> ParseManifest(ReadOnlyMemory<byte> json) => Parse<OverlayManifestV1>(json);
    public static LayeredPresentationParseResult<PresentationMaterializationRequestV1> ParseMaterializationRequest(ReadOnlyMemory<byte> json) => Parse<PresentationMaterializationRequestV1>(json);
    public static LayeredPresentationParseResult<PresentationMaterializationResultV1> ParseMaterializationResult(ReadOnlyMemory<byte> json) => Parse<PresentationMaterializationResultV1>(json);

    public static void Validate(PresentationLayerV1 layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.SchemaVersion != PresentationLayerV1.CurrentSchemaVersion || !Enum.IsDefined(layer.CoordinateSpace) ||
            !Enum.IsDefined(layer.BlendMode) || layer.OpacityMillionths is < 0 or > 1_000_000)
            throw new ArgumentException("Layer semantics are invalid.", nameof(layer));
        ValidateText(layer.LayerKind, nameof(layer));
        ValidateText(layer.RendererVersion, nameof(layer));
        ValidateText(layer.StyleVersion, nameof(layer));
        ValidateProduct(layer.SourceProduct);
        ValidateOptionalSha256(layer.SceneIdentitySha256, nameof(layer));
        ValidateOptions(layer.Options, nameof(layer));
        ValidateIdentity(layer.LayerIdentitySha256, Identity(layer, static value => value with { LayerIdentitySha256 = string.Empty }), nameof(layer));
    }

    public static void Validate(OverlayManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != OverlayManifestV1.CurrentSchemaVersion || manifest.Layers is null ||
            manifest.Layers.Count > MaximumLayerCount || manifest.Layers.Any(static layer => layer is null))
            throw new ArgumentException("Manifest structure is invalid.", nameof(manifest));
        ValidateProduct(manifest.BaseProduct);
        ValidateOptionalSha256(manifest.SceneIdentitySha256, nameof(manifest));
        for (var index = 0; index < manifest.Layers.Count; index++)
        {
            var layer = manifest.Layers[index];
            Validate(layer);
            if (layer.SourceProduct.Compatibility != manifest.BaseProduct.Compatibility ||
                !string.Equals(layer.SceneIdentitySha256, manifest.SceneIdentitySha256, StringComparison.Ordinal))
                throw new ArgumentException("Layer is incompatible with the base product or scene.", nameof(manifest));
            if (index > 0 && CompareLayers(manifest.Layers[index - 1], layer) >= 0)
                throw new ArgumentException("Layers must be unique and canonically ordered.", nameof(manifest));
        }
        ValidateIdentity(manifest.ManifestIdentitySha256,
            Identity(manifest, static value => value with { ManifestIdentitySha256 = string.Empty }), nameof(manifest));
    }

    public static void Validate(PresentationMaterializationRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != PresentationMaterializationRequestV1.CurrentSchemaVersion ||
            request.EnabledLayerIdentitySha256 is null || request.EnabledLayerIdentitySha256.Count > MaximumLayerCount ||
            request.EnabledLayerIdentitySha256.Any(static value => value is null) ||
            request.SourceArtifactIds is null || request.SourceArtifactIds.Count > MaximumSourceArtifactCount ||
            request.SourceArtifactIds.Any(static id => id == Guid.Empty) ||
            !request.SourceArtifactIds.SequenceEqual(request.SourceArtifactIds.Distinct()))
            throw new ArgumentException("Materialization structure is invalid.", nameof(request));
        ValidateProduct(request.BaseProduct);
        ValidateSha256(request.ManifestIdentitySha256, nameof(request));
        ValidateText(request.CompositorVersion, nameof(request));
        ValidateText(request.EncoderName, nameof(request));
        ValidateText(request.EncoderVersion, nameof(request));
        ValidateOptions(request.EncoderOptions, nameof(request));
        if (request.EnabledLayerIdentitySha256.Any(value => !IsCanonicalSha256(value)) ||
            request.EnabledLayerIdentitySha256.Distinct(StringComparer.Ordinal).Count() != request.EnabledLayerIdentitySha256.Count)
            throw new ArgumentException("Materialization layers are invalid.", nameof(request));
        ValidateIdentity(request.MaterializationIdentitySha256,
            Identity(request, static value => value with { MaterializationIdentitySha256 = string.Empty }), nameof(request));
    }

    public static void Validate(PresentationMaterializationResultV1 result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SchemaVersion != PresentationMaterializationResultV1.CurrentSchemaVersion || result.PayloadLength < 0)
            throw new ArgumentException("Materialization result is invalid.", nameof(result));
        ValidateSha256(result.MaterializationIdentitySha256, nameof(result));
        ValidateSha256(result.PayloadSha256, nameof(result));
        ValidateProduct(result.OutputProduct);
    }

    private static LayeredPresentationParseResult<T> Parse<T>(ReadOnlyMemory<byte> json) where T : class
    {
        if (json.Length > MaximumPayloadBytes) return new(null, "$payload");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (HasDuplicateProperties(document.RootElement)) return new(null, "$json");
            var value = JsonSerializer.Deserialize<T>(json.Span, SerializerOptions);
            if (value is null) return new(null, "$document");
            ValidateContract(value);
            value = FreezeDocument(value);
            if (!json.Span.SequenceEqual(CanonicalBytes(value))) return new(null, "$canonical");
            return new(value, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or
            NullReferenceException or FormatException or OverflowException)
        {
            return new(null, "$document");
        }
    }

    private static T FreezeDocument<T>(T value) where T : class => value switch
    {
        OverlayManifestV1 manifest => (T)(object)(manifest with { Layers = Freeze(manifest.Layers) }),
        PresentationMaterializationRequestV1 request => (T)(object)(request with
        {
            EnabledLayerIdentitySha256 = Freeze(request.EnabledLayerIdentitySha256),
            SourceArtifactIds = Freeze(request.SourceArtifactIds)
        }),
        _ => value
    };

    private static void ValidateContract<T>(T value) where T : class
    {
        switch (value)
        {
            case PresentationLayerV1 layer: Validate(layer); break;
            case OverlayManifestV1 manifest: Validate(manifest); break;
            case PresentationMaterializationRequestV1 request: Validate(request); break;
            case PresentationMaterializationResultV1 result: Validate(result); break;
            default: throw new ArgumentException("Unsupported layered presentation contract.", nameof(value));
        }
    }

    private static byte[] CanonicalBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(
        CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, SerializerOptions)));

    private static string Identity<T>(T value, Func<T, T> withoutIdentity) where T : class =>
        CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(withoutIdentity(value), SerializerOptions));

    private static JsonElement CanonicalOptions(JsonElement options)
    {
        if (options.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return JsonSerializer.SerializeToElement(new { });
        ValidateOptions(options, nameof(options));
        return CaptureContractJson.Canonicalize(options);
    }

    private static void ValidateOptions(JsonElement options, string path)
    {
        if (options.ValueKind != JsonValueKind.Object || HasDuplicateProperties(options) || HasNonCanonicalNumber(options))
            throw new ArgumentException("Options must be an object with unique properties and canonical JSON numbers.", path);
    }

    private static bool HasNonCanonicalNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            var raw = value.GetRawText();
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ||
                !double.IsFinite(number)) return true;
            var canonical = JsonSerializer.Serialize(number);
            return !string.Equals(raw, canonical, StringComparison.Ordinal);
        }
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(static property => HasNonCanonicalNumber(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().Any(HasNonCanonicalNumber),
            _ => false
        };
    }

    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(HasDuplicateProperties)) return true;
        return false;
    }

    private static void ValidateProduct(PresentationProductReference product)
    {
        if (product is null || product.Compatibility is null || product.ArtifactId == Guid.Empty)
            throw new ArgumentException("Product reference is invalid.", nameof(product));
        ValidateSha256(product.ProductIdentitySha256, nameof(product));
        ValidateText(product.MediaType, nameof(product));
        var compatibility = product.Compatibility;
        if (compatibility.WidthPixels is < 1 or > MaximumDimensionPixels ||
            compatibility.HeightPixels is < 1 or > MaximumDimensionPixels ||
            (long)compatibility.WidthPixels * compatibility.HeightPixels > MaximumPixelArea)
            throw new ArgumentException("Product dimensions are invalid.", nameof(product));
        ValidateSha256(compatibility.LayoutIdentitySha256, nameof(product));
        ValidateSha256(compatibility.CoordinateIdentitySha256, nameof(product));
    }

    private static PresentationProductReference Normalize(PresentationProductReference product)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(product.Compatibility);
        return product with
        {
            ProductIdentitySha256 = NormalizeSha256(product.ProductIdentitySha256),
            Compatibility = product.Compatibility with
            {
                LayoutIdentitySha256 = NormalizeSha256(product.Compatibility.LayoutIdentitySha256),
                CoordinateIdentitySha256 = NormalizeSha256(product.Compatibility.CoordinateIdentitySha256)
            }
        };
    }

    private static int CompareLayers(PresentationLayerV1 left, PresentationLayerV1 right)
    {
        var order = left.ZOrder.CompareTo(right.ZOrder);
        return order != 0 ? order : StringComparer.Ordinal.Compare(left.LayerIdentitySha256, right.LayerIdentitySha256);
    }

    private static List<T> MaterializeBounded<T>(IEnumerable<T> source, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = new List<T>(Math.Min(maximum, source.TryGetNonEnumeratedCount(out var count) ? count : 4));
        foreach (var value in source)
        {
            if (values.Count == maximum) throw new ArgumentException("Enumerable exceeds its structural bound.", parameterName);
            if (value is null) throw new ArgumentException("Enumerable cannot contain null values.", parameterName);
            values.Add(value);
        }
        return values;
    }

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T> values) => new(values.ToArray());

    private static void ValidateIdentity(string actual, string expected, string path)
    {
        ValidateSha256(actual, path);
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new ArgumentException("Identity does not match content.", path);
    }

    private static void ValidateOptionalSha256(string? value, string path) { if (value is not null) ValidateSha256(value, path); }
    private static void ValidateSha256(string value, string path) { if (!IsCanonicalSha256(value)) throw new ArgumentException("Canonical uppercase SHA-256 required.", path); }
    private static bool IsCanonicalSha256(string? value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static string NormalizeSha256(string value) { if (value is null || value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f'))) throw new ArgumentException("ASCII SHA-256 required.", nameof(value)); return value.ToUpperInvariant(); }
    private static string? NormalizeOptionalSha256(string? value) => value is null ? null : NormalizeSha256(value);
    private static void ValidateText(string value, string path) { if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new ArgumentException("Text is blank or too long.", path); }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
