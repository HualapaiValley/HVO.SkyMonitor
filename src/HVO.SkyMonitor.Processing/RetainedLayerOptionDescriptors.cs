namespace HVO.SkyMonitor.Processing;

/// <summary>Scalar styling for the existing retained-fact layer producers at their exact versions.</summary>
public static class RetainedLayerOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Cloud { get; } = new(
        "cloud-presentation-layer-options-v1",
        "CloudPresentationLayer",
        "cloud-presentation-v5-original-colours",
        "Cloud layer",
        "Style the existing retained cloud assessment. Dimensions, output variants and source bindings stay in pipeline JSON. Missing evidence keeps its current status. Save, validate and explicitly activate a new graph revision; retained products stay unchanged.",
        [
            ProcessingOptionDescriptor.Toggle("drawLabels", "drawLabels", "Show cloud labels",
                "Draw the existing assessment labels without changing the cloud mask or evidence status.", true),
            ProcessingOptionDescriptor.WholeNumber("lineThickness", "lineThickness", "Cloud line thickness",
                "Width of the existing cloud region outlines.", 1, 1, 8, "px")
        ]);

    public static ProcessingNodeOptionsDescriptor Environment { get; } = new(
        "environment-presentation-layer-options-v1",
        "EnvironmentPresentationLayer",
        "metadata-corner-presentation-v6-original-colours",
        "Environment layer",
        "Style the existing environment corner lines from retained facts. Dimensions, environmental selections and source/stack bindings stay in pipeline JSON. Missing and stale facts keep their current status. Save, validate and explicitly activate a new graph revision; retained products stay unchanged.",
        [
            ProcessingOptionDescriptor.NullableWholeNumber("value", "value", "Environment intensity",
                "Optional grayscale override for environment text. Clear to restore the existing palette; missing and stale facts keep their status.",
                null, 0, 255, nullValueLabel: "Default palette", nullActionLabel: "Use default palette"),
            ProcessingOptionDescriptor.WholeNumber("scale", "scale", "Environment text scale",
                "Scale of the existing corner text; the producer fits text to the configured image.", 1, 1, 4, "×"),
            ProcessingOptionDescriptor.WholeNumber("inset", "inset", "Environment inset",
                "Requested edge inset; the producer retains its image-size-dependent minimum.", 4, 0, 64, "px"),
            ProcessingOptionDescriptor.WholeNumber("lineSpacing", "lineSpacing", "Environment line spacing",
                "Requested line spacing; the producer retains its four-pixel minimum.", 2, 0, 16, "px")
        ]);
}
