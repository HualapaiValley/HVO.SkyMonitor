using System.Collections.Immutable;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Deep-sky controls consumed by the existing immutable scene overlay revision path.</summary>
public static class DeepSkyOptionDescriptors
{
    /// <summary>Legacy deep-sky font value accepted by pre-upgrade graphs; omitted from new UI choices.</summary>
    internal const string LegacyDeepSkyFont = "PlexRegular";

    public static ImmutableArray<ProcessingOptionDescriptor> Presentation { get; } =
    [
        ProcessingOptionDescriptor.Toggle("includeDeepSkyLayer", "includeDeepSkyLayer", "Separate deep-sky layer",
            "Publish an independent Current Sky layer for expected OpenNGC positions. Enable the deep-sky query on the Projected scene panel and retain the optional layer in the overlay manifest.", false),
        ProcessingOptionDescriptor.Toggle("applyInlineDeepSkyStyle", "applyInlineDeepSkyStyle", "Apply deep-sky style inline",
            "Apply these deep-sky style and label controls to inline deep-sky annotations when the separate deep-sky layer is off. Saved graphs that keep this off preserve their existing inline rendering and plan identity.", false),
        ProcessingOptionDescriptor.Toggle("deepSkyLabels", "deepSkyLabels", "Deep-sky labels", "Show bounded catalog designations with the expected-position explanation.", true),
        ProcessingOptionDescriptor.WholeNumber("deepSkyMaximumLabels", "deepSkyMaximumLabels", "Deep-sky maximum labels", "Maximum deep-sky object labels drawn; zero hides object labels while keeping the expected-position caption.", 24, 0, 24, "labels"),
        ProcessingOptionDescriptor.WholeNumber("deepSkyMaximumLabelCharacters", "deepSkyMaximumLabelCharacters", "Deep-sky maximum label length", "Maximum characters per deep-sky label; zero hides object labels while keeping the expected-position caption.", 24, 0, PresentationLayerPayloadV1.MaximumLineCharacters, "characters"),
        ProcessingOptionDescriptor.Toggle("deepSkyLabelCatalogObjects", "deepSkyLabelCatalogObjects", "Label non-featured NGC and IC objects", "Label NGC and IC designations beyond featured Messier, Caldwell and named objects; labels prefer Messier, then NGC, then IC.", true),
        ProcessingOptionDescriptor.Toggle("deepSkyOutlines", "deepSkyOutlines", "Deep-sky approximate outlines", "Draw supported catalog ellipses; unknown shapes use class symbols. Source contours are not selected.", true),
        ProcessingOptionDescriptor.WholeNumber("deepSkyLabelScale", "deepSkyLabelScale", "Deep-sky label scale", "Size of catalog labels, independently of star labels.", 1, 1, 8, "×"),
        ProcessingOptionDescriptor.WholeNumber("deepSkyLineThickness", "deepSkyLineThickness", "Deep-sky line thickness", "Width of approximate extents and class symbols.", 2, 1, 8, "px"),
        ProcessingOptionDescriptor.WholeNumber("deepSkyMinimumGlyphRadius", "deepSkyMinimumGlyphRadius", "Deep-sky minimum glyph radius", "Minimum radius of symbolic deep-sky class glyphs in output pixels.", 7, 2, 32, "px"),
        ProcessingOptionDescriptor.Color("deepSkyColor", "deepSkyRed", "deepSkyGreen", "deepSkyBlue", "Deep-sky colour", "Colour of the independent catalog layer.", new(255, 196, 120)),
        ProcessingOptionDescriptor.Font("deepSkyFont", "deepSkyFont", "Deep-sky label font", "Choose one of the checksum-pinned packaged monospace fonts supported by deep-sky payloads.", "MonoRegular", ["MonoRegular", "MonoBold"])
    ];
}
