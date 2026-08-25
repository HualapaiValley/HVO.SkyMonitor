using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Binds a presentation layer contract to the processing artifact that stores its typed payload.</summary>
public sealed record PresentationLayerProductInput(PresentationLayerV1 Layer, ProcessingArtifact Product);

/// <summary>Identifies the fixed packed-frame encoder used by host-neutral materialization.</summary>
public sealed record PresentationPackedMaterializationOptions(string EncoderName = "packed", string EncoderVersion = "packed-frame-v1");

/// <summary>Creates Metadata V3-compatible typed products for layers and overlay manifests.</summary>
public static class PresentationProcessingProducts
{
    public const string LayerRecipeName = "presentation-layer-payload";
    public const string ManifestRecipeName = "overlay-manifest";
    public const string MaterializationRecipeName = "presentation-materialization";
    public const string MetadataFactsRecipeName = "presentation-metadata-facts";
    public const string ManifestMediaType = "application/vnd.hvo.overlay-manifest+json";

    /// <summary>Creates a reference bound to an actual artifact and an explicit image coordinate identity.</summary>
    public static PresentationProductReference CreateReference(ProcessingArtifact artifact, string coordinateIdentitySha256)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var identity = ArtifactIdentity(artifact);
        var compatibility = Compatibility(artifact, coordinateIdentitySha256);
        return new PresentationProductReference(artifact.ArtifactId, identity, artifact.MediaType, compatibility);
    }

    /// <summary>Creates a typed Metadata V3-compatible layer product with explicit canonical source lineage.</summary>
    public static ProcessingProduct CreateLayerProduct(
        PresentationLayerPayloadV1 payload,
        string outputVariant,
        IReadOnlyList<ProcessingArtifact> canonicalSources,
        string producerVersion)
    {
        ArgumentNullException.ThrowIfNull(payload);
        PresentationLayerPayloadJson.Validate(payload);
        ValidateSources(canonicalSources);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        if (!canonicalSources.Any(source => string.Equals(ArtifactIdentity(source), payload.SourceIdentitySha256, StringComparison.Ordinal)))
            throw new ArgumentException("A canonical source must provide the layer source identity.", nameof(canonicalSources));
        var identity = Identity(LayerRecipeName, producerVersion, new
        {
            payload.SchemaVersion,
            payload.SourceIdentitySha256,
            payload.ContentIdentitySha256
        });
        return ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, outputVariant,
            PresentationLayerPayloadJson.MediaType, null, PresentationLayerPayloadJson.Serialize(payload), identity,
            [new("presentation-layer-producer", producerVersion)], canonicalSources, TimeSpan.Zero,
            canonicalSources[0].Compatibility, ProcessingProductKind.Metadata,
            PresentationLayerPayloadV1.CurrentSchemaVersion, payload.ContentIdentitySha256);
    }

    public static ProcessingProduct CreateMetadataFactsProduct(
        PresentationMetadataFactsProductV1 facts,
        string outputVariant,
        IReadOnlyList<ProcessingArtifact> canonicalSources)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ValidateSources(canonicalSources);
        var identitySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(
            CaptureContractJson.SerializeToElement(facts with
            {
                FactsIdentitySha256 = string.Empty,
                Corners = facts.Corners with { SourceIdentitySha256 = string.Empty }
            }));
        if (!string.Equals(identitySha256, facts.FactsIdentitySha256, StringComparison.Ordinal))
            throw new ArgumentException("Presentation metadata facts identity is invalid.", nameof(facts));
        var payload = System.Text.Encoding.UTF8.GetBytes(
            CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(facts)).GetRawText());
        var identity = Identity(MetadataFactsRecipeName, PresentationLayerProducers.MetadataProducerVersion,
            new { facts.SchemaVersion, facts.FactsIdentitySha256 });
        return ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, outputVariant,
            PresentationMetadataFactsProductV1.MediaType, null, payload, identity,
            [new("presentation-metadata-facts", PresentationLayerProducers.MetadataProducerVersion)], canonicalSources,
            canonicalSources[0].Integration, canonicalSources[0].Compatibility, ProcessingProductKind.Metadata,
            PresentationMetadataFactsProductV1.CurrentSchemaVersion, facts.FactsIdentitySha256);
    }

    /// <summary>Aggregates an actual packed base and ordered validated typed layer artifacts into an overlay manifest product.</summary>
    public static ProcessingProduct CreateManifestProduct(
        PresentationProductReference baseProduct,
        string? sceneIdentitySha256,
        IReadOnlyList<PresentationLayerProductInput> orderedLayers,
        string outputVariant,
        ProcessingArtifact baseArtifact)
    {
        ArgumentNullException.ThrowIfNull(orderedLayers);
        ArgumentNullException.ThrowIfNull(baseArtifact);
        ArgumentNullException.ThrowIfNull(baseProduct);
        ValidateReference(baseProduct, baseArtifact, requirePackedLayout: true);
        if (orderedLayers.Count > LayeredPresentationJson.MaximumLayerCount ||
            orderedLayers.Any(input => input is null || !ReferenceMatches(input.Layer.SourceProduct, input.Product) ||
                input.Layer.SourceProduct.Compatibility != baseProduct.Compatibility || !LayerPayloadMatches(input)))
            throw new ArgumentException("Manifest inputs do not match product references.", nameof(orderedLayers));
        var byLayerIdentity = new Dictionary<string, PresentationLayerProductInput>(StringComparer.Ordinal);
        foreach (var input in orderedLayers)
        {
            if (!byLayerIdentity.TryAdd(input.Layer.LayerIdentitySha256, input))
                throw new ArgumentException("Manifest layer inputs contain duplicate or ambiguous identities.", nameof(orderedLayers));
        }
        var manifest = LayeredPresentationJson.CreateManifest(baseProduct, sceneIdentitySha256,
            orderedLayers.Select(static input => input.Layer));
        var sources = new[] { baseArtifact }.Concat(manifest.Layers.Select(layer =>
            byLayerIdentity.TryGetValue(layer.LayerIdentitySha256, out var input) &&
            input.Layer.SourceProduct == layer.SourceProduct
                ? input.Product
                : throw new ArgumentException("Canonical manifest layer cannot be resolved uniquely.", nameof(orderedLayers)))).ToArray();
        var identity = Identity(ManifestRecipeName, "overlay-manifest-v1", new { manifest.ManifestIdentitySha256 });
        return ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, outputVariant, ManifestMediaType, null,
            LayeredPresentationJson.Serialize(manifest), identity, [new("overlay-manifest-contract", "1.0.0")], sources,
            baseArtifact.Integration, baseArtifact.Compatibility, ProcessingProductKind.Metadata,
            OverlayManifestV1.CurrentSchemaVersion, manifest.ManifestIdentitySha256);
    }

    internal static ProcessingRecipeIdentity Identity(string name, string implementationVersion, object parameters)
    {
        var definition = new ProcessingRecipeDefinition(name, "1.0.0", implementationVersion, ProcessingOperationKind.Transform);
        return ProcessingIdentity.CreateRecipeIdentity(definition,
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(parameters)));
    }

    internal static string ArtifactIdentity(ProcessingArtifact source) => source.ContentIdentitySha256?.ToUpperInvariant() ??
        source.DescriptorIdentitySha256?.ToUpperInvariant() ?? ProcessingIdentity.ComputePayloadSha256(source.Payload);

    internal static void ValidateReference(PresentationProductReference reference, ProcessingArtifact artifact, bool requirePackedLayout)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!ReferenceMatches(reference, artifact) || requirePackedLayout &&
            (artifact.Layout is not { } layout || reference.Compatibility.WidthPixels != layout.Width ||
             reference.Compatibility.HeightPixels != layout.Height ||
             !string.Equals(reference.Compatibility.LayoutIdentitySha256, LayoutIdentity(layout), StringComparison.Ordinal)))
            throw new ArgumentException("Product reference does not describe the actual artifact.", nameof(reference));
    }

    internal static bool ReferenceMatches(PresentationProductReference reference, ProcessingArtifact artifact) =>
        reference.ArtifactId == artifact.ArtifactId &&
        string.Equals(reference.MediaType, artifact.MediaType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(reference.ProductIdentitySha256, ArtifactIdentity(artifact), StringComparison.Ordinal);

    internal static string LayoutIdentity(FrameLayoutDescriptor layout) =>
        CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(layout));

    private static PresentationCompatibilityDescriptor Compatibility(ProcessingArtifact artifact, string coordinateIdentitySha256)
    {
        if (artifact.Layout is { } layout)
            return new(layout.Width, layout.Height, LayoutIdentity(layout), NormalizeSha256(coordinateIdentitySha256));
        if (artifact.ProductKind == ProcessingProductKind.Metadata &&
            string.Equals(artifact.SchemaVersion, PresentationLayerPayloadV1.CurrentSchemaVersion, StringComparison.Ordinal) &&
            PresentationLayerPayloadJson.Parse(artifact.Payload).Payload is { } payload)
            return new(payload.WidthPixels, payload.HeightPixels, new string('0', 64), NormalizeSha256(coordinateIdentitySha256));
        throw new ArgumentException("Artifact has no presentation dimensions.", nameof(artifact));
    }

    private static bool LayerPayloadMatches(PresentationLayerProductInput input)
    {
        var parsed = PresentationLayerPayloadJson.Parse(input.Product.Payload);
        return input.Product.ProductKind == ProcessingProductKind.Metadata &&
            string.Equals(input.Product.SchemaVersion, PresentationLayerPayloadV1.CurrentSchemaVersion, StringComparison.Ordinal) &&
            parsed.Payload is { } payload &&
            string.Equals(input.Product.ContentIdentitySha256, payload.ContentIdentitySha256, StringComparison.Ordinal) &&
            payload.WidthPixels == input.Layer.SourceProduct.Compatibility.WidthPixels &&
            payload.HeightPixels == input.Layer.SourceProduct.Compatibility.HeightPixels;
    }

    private static string NormalizeSha256(string value)
    {
        if (value is null || value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Coordinate identity must be SHA-256.", nameof(value));
        return value.ToUpperInvariant();
    }

    private static void ValidateSources(IReadOnlyList<ProcessingArtifact> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < 1 or > 512 || sources.Any(static source => source is null || source.ArtifactId == Guid.Empty))
            throw new ArgumentException("Canonical sources are invalid.", nameof(sources));
    }
}

/// <summary>Host-neutral packed-frame materialization boundary with complete explicit immediate lineage.</summary>
public static class PresentationMaterializationExecutor
{
    public const string PackedEncoderName = "packed";
    public const string PackedEncoderVersion = "packed-frame-v1";

    /// <summary>
    /// Materializes an immutable packed base and selected manifest layers into one owned packed output. The fixed
    /// encoder identity, compositor identity, manifest, base, enabled layers, and immediate artifact lineage are bound.
    /// Cancellation is propagated to compositing.
    /// </summary>
    public static ProcessingProduct MaterializePacked(
        ProcessingArtifact baseArtifact,
        ProcessingArtifact manifestArtifact,
        OverlayManifestV1 manifest,
        IReadOnlyList<PresentationLayerProductInput> layerProducts,
        IEnumerable<string> enabledLayerIdentitySha256,
        string outputVariant,
        PresentationPackedMaterializationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseArtifact);
        ArgumentNullException.ThrowIfNull(manifestArtifact);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(layerProducts);
        options ??= new(PackedEncoderName, PackedEncoderVersion);
        if (options.EncoderName != PackedEncoderName || options.EncoderVersion != PackedEncoderVersion)
            throw new ArgumentException("Packed materialization requires the fixed packed encoder identity.", nameof(options));
        LayeredPresentationJson.Validate(manifest);
        PresentationProcessingProducts.ValidateReference(manifest.BaseProduct, baseArtifact, requirePackedLayout: true);
        if (manifestArtifact.ArtifactId == Guid.Empty || manifestArtifact.ProductKind != ProcessingProductKind.Metadata ||
            !string.Equals(manifestArtifact.SchemaVersion, OverlayManifestV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(manifestArtifact.ContentIdentitySha256, manifest.ManifestIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(manifestArtifact.MediaType, PresentationProcessingProducts.ManifestMediaType, StringComparison.OrdinalIgnoreCase) ||
            !manifestArtifact.Payload.Span.SequenceEqual(LayeredPresentationJson.Serialize(manifest)))
            throw new ArgumentException("Manifest artifact does not bind the supplied manifest and base.", nameof(manifestArtifact));
        if (!ProcessingRecipeSupport.TryValidateFrame(baseArtifact, out var descriptor, out _) ||
            descriptor.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24) ||
            descriptor.StrideBytes != checked(descriptor.Width * ImageLayout.BytesPerPixel(descriptor.PixelFormat)))
            throw new ArgumentException("Materialization base must be a packed display frame.", nameof(baseArtifact));

        var byIdentity = layerProducts.ToDictionary(static item => item.Layer.LayerIdentitySha256, StringComparer.Ordinal);
        if (byIdentity.Count != layerProducts.Count || layerProducts.Any(item =>
            !PresentationProcessingProducts.ReferenceMatches(item.Layer.SourceProduct, item.Product) ||
            item.Layer.SourceProduct.Compatibility != manifest.BaseProduct.Compatibility))
            throw new ArgumentException("Layer products do not bind their references.", nameof(layerProducts));
        var enabled = enabledLayerIdentitySha256.ToHashSet(StringComparer.Ordinal);
        if (manifest.Layers.Any(layer => enabled.Contains(layer.LayerIdentitySha256) &&
            layer.CoordinateSpace != PresentationCoordinateSpace.ScenePixels))
            throw new ArgumentException("Packed materialization supports enabled ScenePixels layers only.", nameof(manifest));
        var sourceIds = new[] { baseArtifact.ArtifactId, manifestArtifact.ArtifactId }
            .Concat(manifest.Layers.Where(layer => enabled.Contains(layer.LayerIdentitySha256))
                .Select(static layer => layer.SourceProduct.ArtifactId)).Distinct().ToArray();
        var request = LayeredPresentationJson.CreateMaterializationRequest(manifest, enabled,
            PresentationLayerCompositor.AlgorithmVersion, options.EncoderName, options.EncoderVersion,
            JsonSerializer.SerializeToElement(new { format = "packed" }), sourceIds);
        var compositorLayers = new List<PresentationCompositorLayer>();
        foreach (var layer in manifest.Layers)
        {
            if (!enabled.Contains(layer.LayerIdentitySha256)) continue;
            if (!byIdentity.TryGetValue(layer.LayerIdentitySha256, out var input))
                throw new ArgumentException("An enabled layer product is missing.", nameof(layerProducts));
            var parsed = PresentationLayerPayloadJson.Parse(input.Product.Payload);
            if (!parsed.IsValid || parsed.Payload is not { } payload ||
                !string.Equals(payload.ContentIdentitySha256, input.Layer.SourceProduct.ProductIdentitySha256, StringComparison.Ordinal))
                throw new ArgumentException("Layer payload is invalid.", nameof(layerProducts));
            compositorLayers.Add(new(payload, true, layer.BlendMode switch
            {
                PresentationBlendMode.Normal => PresentationRasterBlendMode.Normal,
                PresentationBlendMode.Multiply => PresentationRasterBlendMode.Multiply,
                PresentationBlendMode.Screen => PresentationRasterBlendMode.Screen,
                PresentationBlendMode.Lighten => PresentationRasterBlendMode.Lighten,
                _ => throw new ArgumentOutOfRangeException(nameof(layerProducts))
            }, layer.OpacityMillionths));
        }
        var layout = new ImageLayout(descriptor.Width, descriptor.Height, descriptor.PixelFormat, descriptor.StrideBytes);
        var output = PresentationLayerCompositor.Composite(layout, baseArtifact.Payload, compositorLayers, cancellationToken);
        var identity = PresentationProcessingProducts.Identity(PresentationProcessingProducts.MaterializationRecipeName,
            "typed-presentation-materialization-v1", new { request.MaterializationIdentitySha256 });
        var sources = sourceIds.Select(id => id == baseArtifact.ArtifactId ? baseArtifact : id == manifestArtifact.ArtifactId
            ? manifestArtifact : layerProducts.Single(item => item.Product.ArtifactId == id).Product).ToArray();
        return ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.AnnotatedPreview, outputVariant,
            "application/x-hvo-packed-image", descriptor, output, identity,
            [new("presentation-compositor", PresentationLayerCompositor.AlgorithmVersion),
             new(options.EncoderName, options.EncoderVersion)], sources, baseArtifact.Integration,
            baseArtifact.Compatibility);
    }
}
