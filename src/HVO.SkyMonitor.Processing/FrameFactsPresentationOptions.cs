using System.Collections.Immutable;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Durable graph options for the explicitly selected rich-text alias. No legacy defaults are converted.</summary>
public class FrameFactsPresentationOptions
{
    public int WidthPixels { get; init; }
    public int HeightPixels { get; init; }
    public int Inset { get; init; } = 12;
    public int LineSpacing { get; init; } = 2;
    public string TopLeftTemplate { get; init; } = string.Empty;
    public string TopLeftFont { get; init; } = "MonoRegular";
    public int TopLeftSizeMilliPixels { get; init; } = 16_000;
    public byte TopLeftRed { get; init; } = 255;
    public byte TopLeftGreen { get; init; } = 255;
    public byte TopLeftBlue { get; init; } = 255;
    public string TopLeftMissingPolicy { get; init; } = "Placeholder";
    public string TopRightTemplate { get; init; } = string.Empty;
    public string TopRightFont { get; init; } = "MonoRegular";
    public int TopRightSizeMilliPixels { get; init; } = 16_000;
    public byte TopRightRed { get; init; } = 255;
    public byte TopRightGreen { get; init; } = 255;
    public byte TopRightBlue { get; init; } = 255;
    public string TopRightMissingPolicy { get; init; } = "Placeholder";
    public string BottomLeftTemplate { get; init; } = string.Empty;
    public string BottomLeftFont { get; init; } = "MonoRegular";
    public int BottomLeftSizeMilliPixels { get; init; } = 16_000;
    public byte BottomLeftRed { get; init; } = 255;
    public byte BottomLeftGreen { get; init; } = 255;
    public byte BottomLeftBlue { get; init; } = 255;
    public string BottomLeftMissingPolicy { get; init; } = "Placeholder";
    public string BottomRightTemplate { get; init; } = string.Empty;
    public string BottomRightFont { get; init; } = "MonoRegular";
    public int BottomRightSizeMilliPixels { get; init; } = 16_000;
    public byte BottomRightRed { get; init; } = 255;
    public byte BottomRightGreen { get; init; } = 255;
    public byte BottomRightBlue { get; init; } = 255;
    public string BottomRightMissingPolicy { get; init; } = "Placeholder";

    internal IReadOnlyList<PresentationRichCornerV1> Corners() =>
    [
        new(PresentationTextAnchor.TopLeft, TopLeftTemplate, TopLeftMissingPolicy, ParseFont(TopLeftFont), TopLeftSizeMilliPixels, Inset, LineSpacing, new(TopLeftRed, TopLeftGreen, TopLeftBlue), []),
        new(PresentationTextAnchor.TopRight, TopRightTemplate, TopRightMissingPolicy, ParseFont(TopRightFont), TopRightSizeMilliPixels, Inset, LineSpacing, new(TopRightRed, TopRightGreen, TopRightBlue), []),
        new(PresentationTextAnchor.BottomLeft, BottomLeftTemplate, BottomLeftMissingPolicy, ParseFont(BottomLeftFont), BottomLeftSizeMilliPixels, Inset, LineSpacing, new(BottomLeftRed, BottomLeftGreen, BottomLeftBlue), []),
        new(PresentationTextAnchor.BottomRight, BottomRightTemplate, BottomRightMissingPolicy, ParseFont(BottomRightFont), BottomRightSizeMilliPixels, Inset, LineSpacing, new(BottomRightRed, BottomRightGreen, BottomRightBlue), []),
    ];

    private static PresentationRichFontFaceV1 ParseFont(string value) => value switch
    {
        "MonoRegular" => PresentationRichFontFaceV1.MonoRegular,
        "MonoBold" => PresentationRichFontFaceV1.MonoBold,
        "PlexRegular" => PresentationRichFontFaceV1.PlexRegular,
        _ => throw new ArgumentException("Only pinned packaged Frame Facts fonts are supported.", nameof(value))
    };
}

/// <summary>Descriptions consumed by the existing typed graph codec and server-side validation.</summary>
public static class FrameFactsPresentationOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "frame-facts-presentation-options-v1", FrameFactsPresentation.StepAlias, FrameFactsPresentation.ProducerVersion,
        "Frame Facts", "Explicitly selected retained templates and packaged corner typography.", CreateOptions());

    private static ImmutableArray<ProcessingOptionDescriptor> CreateOptions()
    {
        var options = ImmutableArray.CreateBuilder<ProcessingOptionDescriptor>();
        options.Add(ProcessingOptionDescriptor.WholeNumber("widthPixels", "widthPixels", "Presentation width", "Set the source presentation width; zero requires an explicit value before rendering.", 0, 0, 65_536, "px"));
        options.Add(ProcessingOptionDescriptor.WholeNumber("heightPixels", "heightPixels", "Presentation height", "Set the source presentation height; zero requires an explicit value before rendering.", 0, 0, 65_536, "px"));
        options.Add(ProcessingOptionDescriptor.WholeNumber("inset", "inset", "Corner inset", "Distance from the image edge.", 12, 0, 64, "px"));
        options.Add(ProcessingOptionDescriptor.WholeNumber("lineSpacing", "lineSpacing", "Line spacing", "Extra spacing between baselines.", 2, 0, 16, "px"));
        foreach (var prefix in new[] { "topLeft", "topRight", "bottomLeft", "bottomRight" })
        {
            options.Add(ProcessingOptionDescriptor.Text(prefix + "Template", prefix + "Template", prefix + " template",
                "Bounded hvo-frame-facts-template-v1 source; empty hides this corner.", string.Empty, 4096));
            options.Add(ProcessingOptionDescriptor.Font(prefix + "Font", prefix + "Font", prefix + " font",
                "Pinned Mono regular/bold or Plex regular. Plex has no bold face.", "MonoRegular", ["MonoRegular", "MonoBold", "PlexRegular"]));
            options.Add(ProcessingOptionDescriptor.WholeNumber(prefix + "SizeMilliPixels", prefix + "SizeMilliPixels", prefix + " size",
                "Font size in thousandths of an image pixel.", 16_000, 4_000, 64_000, "milli-px"));
            options.Add(ProcessingOptionDescriptor.Color(prefix + "Color", prefix + "Red", prefix + "Green", prefix + "Blue",
                prefix + " colour", "Text colour; the dark panel remains visible.", new(255, 255, 255)));
            options.Add(ProcessingOptionDescriptor.Choice(prefix + "MissingPolicy", prefix + "MissingPolicy", prefix + " missing facts",
                "Show UNAVAILABLE or drop the complete affected line.", "Placeholder", ["Placeholder", "DropLine"]));
        }
        return options.ToImmutable();
    }
}
