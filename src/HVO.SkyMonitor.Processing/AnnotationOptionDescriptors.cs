using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Effective legacy Annotation controls at the exact registered recipe implementation.</summary>
public static class AnnotationOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "annotation-options-v1",
        "Annotation",
        "projected-annotation-v4",
        "Legacy annotation",
        "Style the existing Annotation output. Constellation intensity affects Mono8 previews; constellation color affects RGB previews. Collections, label qualification and recipe/output identities stay in the pipeline JSON. Save, validate and explicitly activate a new graph revision; retained images stay unchanged.",
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
                "Additional spacing between existing metadata lines.", 2, 0, 16, "px")
        ]);
}
