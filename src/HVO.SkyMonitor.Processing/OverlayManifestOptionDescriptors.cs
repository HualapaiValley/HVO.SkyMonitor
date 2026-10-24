namespace HVO.SkyMonitor.Processing;

/// <summary>Overrides for the existing manifest producer, without changing producer selection or identity.</summary>
public static class OverlayManifestOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Layer { get; } = new(
        "overlay-manifest-layer-options-v1", "OverlayManifest.Layer", "overlay-manifest-v1",
        "Layer override", "Override an existing manifest layer. This does not add a producer or dependency.",
        [
            ProcessingOptionDescriptor.Choice("kind", "kind", "Layer kind",
                "Case-sensitive existing kind or legacy alias. Aliases of the same kind cannot be repeated. Scene-annotation also matches grouped scene layers; specific overrides take precedence.",
                "scene-annotation", PresentationLayerKindSelection.Supported),
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enabled by default",
                "The materializer uses this flag when its explicit selection is empty. Satellite tracks remain disabled unless explicitly enabled.", true),
            ProcessingOptionDescriptor.NullableWholeNumber("zOrder", "zOrder", "Layer order",
                "Clear to retain producer ordering. Smaller orders draw first. Grouped scene overrides retain existing relative offsets; row order does not set drawing order.",
                null, -1024, 1024, nullValueLabel: "Producer order", nullActionLabel: "Use producer order")
        ]);

    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "overlay-manifest-options-v1", "OverlayManifest", "overlay-manifest-v1", "Overlay manifest",
        "Edit existing layer defaults and constellation opacity. Save a draft, validate and explicitly activate it. Variants, producer bindings and rich Frame Facts opt-in remain in pipeline JSON. Retained manifests stay unchanged.",
        [
            ProcessingOptionDescriptor.RowCollection("layers", "layers", "Layer overrides",
                "Empty overrides retain producer defaults. Up to ten unique canonical kinds. Overrides do not supply missing layers or relax required dependencies.",
                10, Layer, "kind", PresentationLayerKindSelection.Canonicalize,
                "Using producer defaults.", "Use producer defaults"),
            ProcessingOptionDescriptor.WholeNumber("constellationOpacityMillionths", "constellationOpacityMillionths", "Constellation opacity",
                "Opacity of the retained constellation layer in millionths: zero is transparent and 1000000 is opaque.",
                800_000, 0, 1_000_000)
        ]);
}
