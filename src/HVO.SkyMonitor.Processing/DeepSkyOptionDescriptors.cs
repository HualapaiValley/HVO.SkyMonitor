using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing;

/// <summary>Deep-sky controls consumed by the existing immutable scene overlay revision path.</summary>
public static class DeepSkyOptionDescriptors
{
    public static ImmutableArray<ProcessingOptionDescriptor> Presentation { get; } =
    [
        ProcessingOptionDescriptor.Toggle("includeDeepSkyLayer", "includeDeepSkyLayer", "Separate deep-sky layer",
            "Publish an independent Current Sky layer for expected OpenNGC positions. Enable the deep-sky query on the Projected scene panel and retain the optional layer in the overlay manifest.", false),
        ProcessingOptionDescriptor.Toggle("deepSkyLabels", "deepSkyLabels", "Deep-sky labels", "Show bounded catalog designations with the expected-position explanation.", true),
        ProcessingOptionDescriptor.Toggle("deepSkyOutlines", "deepSkyOutlines", "Deep-sky approximate outlines", "Draw supported catalog ellipses; unknown shapes use class symbols. Source contours are not selected.", true),
        ProcessingOptionDescriptor.WholeNumber("deepSkyLabelScale", "deepSkyLabelScale", "Deep-sky label scale", "Size of catalog labels, independently of star labels.", 1, 1, 8, "×"),
        ProcessingOptionDescriptor.WholeNumber("deepSkyLineThickness", "deepSkyLineThickness", "Deep-sky line thickness", "Width of approximate extents and class symbols.", 2, 1, 8, "px"),
        ProcessingOptionDescriptor.Color("deepSkyColor", "deepSkyRed", "deepSkyGreen", "deepSkyBlue", "Deep-sky colour", "Colour of the independent catalog layer.", new(255, 196, 120)),
        ProcessingOptionDescriptor.Font("deepSkyFont", "deepSkyFont", "Deep-sky label font", "Choose one of the checksum-pinned packaged fonts.", "MonoRegular", ["MonoRegular", "MonoBold", "PlexRegular"])
    ];
}
