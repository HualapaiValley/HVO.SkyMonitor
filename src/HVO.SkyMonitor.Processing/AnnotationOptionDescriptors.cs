using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Effective legacy Annotation controls at the exact registered recipe implementation.</summary>
public static class AnnotationOptionDescriptors
{
    private static readonly string[] CornerKeys = ["topLeftTokens", "topRightTokens", "bottomLeftTokens", "bottomRightTokens"];
    private static readonly string[] CornerChoices =
    [
        "agent-identity", "capture-sequence", "utc", "schedule-profile", "exposure", "cadence", "gain", "offset",
        "sensor-setpoint", "environment", "catalog", "calibration", "stack", "processing-profile"
    ];

    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "annotation-options-v1",
        "Annotation",
        "projected-annotation-v4",
        "Legacy annotation",
        "Select and style the existing Annotation output and retained-fact corner lines. Constellation intensity affects Mono8 previews; constellation color affects RGB previews. Rich templates use the explicit Frame Facts conversion. Save, validate and explicitly activate a new graph revision; retained images stay unchanged.",
        [
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable legacy annotation",
                "Run this Annotation node. In an explicit v2 graph this edits the node's enabled state.", true),
            ProcessingOptionDescriptor.WholeNumber("markRadius", "markRadius", "Marker radius",
                "Radius of existing dotted point markers; zero draws one pixel. Resolved footprints keep their existing outline/point selection rules.",
                6, 0, 32, "px"),
            ProcessingOptionDescriptor.WholeNumber("markerValue", "markerValue", "Marker intensity",
                "Grayscale intensity of markers and resolved outlines, also applied to all RGB channels. Label intensity remains fixed.",
                144, 0, 255),
            ProcessingOptionDescriptor.Toggle("drawLabels", "drawLabels", "Show labels",
                "Draw eligible labels. Existing measured-association qualification and expected-position diagnostics still apply.", true),
            ProcessingOptionDescriptor.WholeNumber("labelScale", "labelScale", "Label scale",
                "Integer scale of the existing bitmap labels; fonts and glyphs stay unchanged.", 1, 1, 8),
            ProcessingOptionDescriptor.RealNumber("maximumLabelMagnitude", "maximumLabelMagnitude", "Maximum label magnitude",
                "Magnitude ceiling for named stellar labels and markers. Solar-system bodies keep their existing selection rules; this does not qualify stellar positions.",
                2.5, -30, 30, "mag"),
            ProcessingOptionDescriptor.Toggle("drawConstellationLines", "drawConstellationLines", "Show constellation lines",
                "Draw segments from the existing scene and constellation selection.", true),
            ProcessingOptionDescriptor.WholeNumber("constellationLineValue", "constellationLineValue", "Constellation intensity (Mono8 only)",
                "Intensity of constellation lines on Mono8 previews. RGB previews use constellation color; this value has no RGB effect.",
                160, 0, 255),
            ProcessingOptionDescriptor.Color("constellationLineColor", "constellationLineRed", "constellationLineGreen", "constellationLineBlue",
                "Constellation color (RGB only)", "Color of constellation lines on RGB previews. Mono8 previews use constellation intensity; these channels have no Mono8 effect.",
                new PresentationColor(96, 160, 255)),
            ProcessingOptionDescriptor.WholeNumber("constellationLineThickness", "constellationLineThickness", "Constellation line thickness",
                "Thickness of existing constellation segments in output pixels.", 1, 1, 8, "px"),
            ProcessingOptionDescriptor.RealNumber("constellationLineOpacity", "constellationLineOpacity", "Constellation line opacity",
                "Blend existing constellation lines over the preview, from transparent to opaque.", 0.8, 0, 1),
            ProcessingOptionDescriptor.Toggle("drawImageCircle", "drawImageCircle", "Show image circle",
                "Draw the existing rig projection's image-circle border when projection landmarks are available.", false),
            ProcessingOptionDescriptor.WholeNumber("imageCircleValue", "imageCircleValue", "Image circle intensity",
                "Grayscale intensity of the image-circle border, also applied to all RGB channels.", 96, 0, 255),
            ProcessingOptionDescriptor.Toggle("drawCardinalDirections", "drawCardinalDirections", "Show cardinal directions",
                "Draw the existing N/E/S/W labels when projection landmarks are available.", false),
            ProcessingOptionDescriptor.WholeNumber("cardinalValue", "cardinalValue", "Cardinal intensity",
                "Grayscale intensity of the cardinal labels, also applied to all RGB channels.", 255, 0, 255),
            ProcessingOptionDescriptor.WholeNumber("cardinalScale", "cardinalScale", "Cardinal scale",
                "Integer scale of the existing bitmap cardinal labels.", 2, 1, 8),
            ProcessingOptionDescriptor.Toggle("drawMetadataCorners", "drawMetadataCorners", "Show metadata corners",
                "Draw the graph's existing corner token and environmental selections; this does not change their content.", false),
            ProcessingOptionDescriptor.WholeNumber("metadataValue", "metadataValue", "Metadata intensity",
                "Grayscale intensity of existing metadata corner lines, also applied to all RGB channels.", 255, 0, 255),
            ProcessingOptionDescriptor.WholeNumber("metadataScale", "metadataScale", "Metadata scale",
                "Integer scale of the existing bitmap metadata lines.", 1, 1, 4),
            ProcessingOptionDescriptor.WholeNumber("metadataInset", "metadataInset", "Metadata inset",
                "Inset of metadata lines from the image edges.", 4, 0, 64, "px"),
            ProcessingOptionDescriptor.WholeNumber("metadataLineSpacing", "metadataLineSpacing", "Metadata line spacing",
                "Additional spacing between existing metadata lines.", 2, 0, 16, "px"),
            ProcessingOptionDescriptor.TextCollection("constellationIds", "constellationIds", "Constellation IDs",
                "Draw these retained constellation figures. Empty draws all retained figures. Unknown IDs, duplicates and source order are preserved.", 256, 16),
            Corner("topLeftTokens", "Top left corner", ["agent-identity", "capture-sequence", "utc"]),
            Corner("topRightTokens", "Top right corner", ["schedule-profile", "exposure", "cadence", "gain", "offset", "sensor-setpoint"]),
            Corner("bottomLeftTokens", "Bottom left corner", ["environment"]),
            Corner("bottomRightTokens", "Bottom right corner", ["catalog", "calibration", "stack", "processing-profile"]),
            ProcessingOptionDescriptor.EnumCollection<EnvironmentalObservationKind>("environmentalKinds", "environmentalKinds", "Environmental observations",
                "Existing retained observations expanded by the environment token. Select at most eight unique kinds. Missing or unavailable observations keep their status; selection does not enable a sensor or provider.",
                [EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RelativeHumidity,
                    EnvironmentalObservationKind.AtmosphericPressure, EnvironmentalObservationKind.WindSpeed,
                    EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.CloudCover], 8)
        ]);

    private static ProcessingOptionDescriptor Corner(string key, string label, IEnumerable<string> defaults)
        => ProcessingOptionDescriptor.ChoiceCollection(key, key, label,
            "Ordered retained-fact lines, with at most eight unique tokens and eight expanded lines. The environment token expands to the selected observation count, or one unavailable line when empty. Clear the selection for an empty corner.",
            CornerChoices, defaults, 8, uniqueItemIdentity: static value => value,
            emptyCollectionLabel: "No lines in this corner.", clearCollectionLabel: "Clear corner");

    internal static IEnumerable<ProcessingGraphDiagnostic> ValidateSelections(ProcessingNodeOptionsDescriptor descriptor,
        IEnumerable<ProcessingDecodedOption> decoded, string path)
    {
        if (!descriptor.StepAlias.Equals(Descriptor.StepAlias, StringComparison.OrdinalIgnoreCase) ||
            descriptor.StepVersion != Descriptor.StepVersion)
            yield break;
        var options = decoded.ToDictionary(static option => option.Descriptor.Key, StringComparer.Ordinal);
        if (!options.TryGetValue("environmentalKinds", out var environment)) yield break;
        var kinds = environment.Value.AsCollection().Items;
        if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Length)
            yield return new(ProcessingOptionReasonCodes.NotAllowed, path + ".environmentalKinds",
                "Select each environmental kind at most once.");
        foreach (var key in CornerKeys)
        {
            if (!options.TryGetValue(key, out var corner)) continue;
            var tokens = corner.Value.AsCollection().Items;
            var expanded = tokens.Count(static token => token != "environment") +
                (tokens.Contains("environment", StringComparer.Ordinal) ? Math.Max(1, kinds.Length) : 0);
            if (expanded > 8)
                yield return new(ProcessingOptionReasonCodes.NotAllowed, path + "." + key,
                    "Expanded metadata corner content cannot exceed eight lines. Remove tokens or environmental selections.");
        }
    }
}
