using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing;

/// <summary>The existing case-sensitive layer-kind and legacy-alias validation contract.</summary>
public static class PresentationLayerKindSelection
{
    public static ImmutableArray<string> Supported { get; } =
    [
        "scene-annotation", "scene-cardinals", "scene-image-circle", "scene-constellations",
        "cloud-mask", "cloud-labels", "environment",
        "star-annotations", "cardinal-directions", "image-circle", "constellations", "corner-annotations",
        OrbitalTrackPresentation.LayerKind
    ];

    public static bool IsSupported(string kind) => Supported.Contains(kind, StringComparer.Ordinal);

    public static string Canonicalize(string kind) => kind switch
    {
        "star-annotations" => "scene-annotation",
        "cardinal-directions" => "scene-cardinals",
        "image-circle" => "scene-image-circle",
        "constellations" => "scene-constellations",
        "corner-annotations" => "environment",
        _ => kind
    };
}

/// <summary>Selection only; producer bindings, rich-facts opt-in and variants remain in authoritative JSON.</summary>
public static class PresentationMaterializerOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "presentation-materializer-options-v1", "PresentationMaterializer", "typed-presentation-materialization-v1",
        "Presentation materializer",
        "Choose existing retained layers for the materialized image. Save a draft, validate and explicitly activate it. Producer layers, retained products and viewer preferences stay unchanged.",
        [
            ProcessingOptionDescriptor.ChoiceCollection("enabledLayerKinds", "enabledLayerKinds", "Enabled layer kinds",
                "An empty list uses each manifest layer's enabled-by-default setting; it does not hide all layers. An explicit list selects matching retained kinds, including layers disabled by default. Names are case-sensitive and legacy aliases remain supported. Scene-annotation selection retains its existing grouped scene matching. Missing-layer and dependency rules still apply; selecting a kind does not add its producer or dependency.",
                PresentationLayerKindSelection.Supported, 10, PresentationLayerKindSelection.Canonicalize,
                "Using manifest defaults.", "Use manifest defaults")
        ]);
}
