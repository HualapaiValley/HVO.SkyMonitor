namespace HVO.SkyMonitor.Processing;

/// <summary>The existing packed weather overlay, without a producer or recipe upgrade (#1564).</summary>
public static class WeatherCloudOverlayOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "weather-cloud-overlay-options-v1", "WeatherCloudOverlay", "weather-cloud-overlay-v1", "Weather and cloud overlay",
        "Style the retained preview and cloud assessment. Missing weather and unavailable cloud evidence stay unknown; this does not qualify a physical cloud profile.",
        [
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable weather and cloud overlay",
                "Run the existing overlay when this graph node is enabled. It requires a preview and cloud-assessment metadata.", true),
            ProcessingOptionDescriptor.Text("recipeVersion", "recipeVersion", "Artifact recipe label",
                "Retained artifact recipe label, not an implementation selector. The registered weather-cloud-overlay recipe remains semantic version 1.0.0, implementation weather-cloud-overlay-v1. Existing custom labels are preserved.",
                "weather-cloud-overlay-v1", null, isRequired: true),
            ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Overlay output variant",
                "Annotated-preview output route. Update variant-specific dependent inputs separately.",
                "weather-cloud-overlay-v1", 128, isIdentifier: true),
            ProcessingOptionDescriptor.WholeNumber("lineThickness", "lineThickness", "Cloud boundary thickness",
                "Width of the existing cloud tile boundaries; grid dimensions stay unchanged.", 1, 1, 8, "px"),
            ProcessingOptionDescriptor.Toggle("drawLabels", "drawLabels", "Show weather and cloud labels",
                "Draw the existing cloud coverage, quality and precipitation-status labels; hiding labels does not change the assessment.", true),
            ProcessingOptionDescriptor.WholeNumber("maximumLabelCharacters", "maximumLabelCharacters", "Maximum label length",
                "The existing character budget for each label; zero hides label text.", 32, 0, 128, "characters")
        ]);
}
