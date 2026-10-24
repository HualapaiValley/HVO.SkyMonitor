using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Opt-in versioned producer; both hosts use the retained snapshot and the same evaluator/layout.</summary>
public static class FrameFactsPresentation
{
    public const string StepAlias = "FrameFactsPresentationLayerV1";
    public const string ProducerVersion = "frame-facts-presentation-v1";

    public static void ValidateOptions(FrameFactsPresentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WidthPixels is < 1 or > 65_536 || options.HeightPixels is < 1 or > 65_536 ||
            (long)options.WidthPixels * options.HeightPixels > PresentationLayerCompositor.MaximumCompositionPixels ||
            options.Inset is < 0 or > 64 || options.LineSpacing is < 0 or > 16)
            throw new ArgumentException("Frame Facts dimensions or spacing exceed their bounds.", nameof(options));
        ValidateTemplates(options);
    }

    /// <summary>Shared semantic validation after descriptor/codec type and range validation.</summary>
    public static void ValidateTemplates(FrameFactsPresentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var corner in options.Corners())
        {
            if (corner.SizeMilliPixels is < 4_000 or > 64_000 || corner.MissingPolicy is not ("Placeholder" or "DropLine"))
                throw new ArgumentException("Frame Facts font size or missing policy is invalid.", nameof(options));
            ValidateTemplate(corner);
        }
    }

    internal static void ValidateContract(PresentationRichTextV1 rich)
    {
        if (rich.GrammarVersion != FrameFactsTemplate.GrammarId || rich.CatalogVersion != FrameFactsRetainedCatalog.Version ||
            rich.ProducerVersion != ProducerVersion)
            throw new ArgumentException("Unsupported Frame Facts rendering contract.", nameof(rich));
        foreach (var corner in rich.Corners) ValidateTemplate(corner);
    }

    private static void ValidateTemplate(PresentationRichCornerV1 corner)
    {
        var parsed = FrameFactsTemplate.Parse(corner.Template, FrameFactsRetainedCatalog.Catalog);
        if (!parsed.IsValid) throw new ArgumentException($"{corner.Anchor}: {parsed.Error!.Code} at {parsed.Error.SourceOffset}.", nameof(corner));
        if (corner.Font == PresentationRichFontFaceV1.PlexRegular && parsed.Template!.Lines.Any(line =>
            line.Parts.Any(part => part.Style.HasFlag(FrameFactsTextStyle.Bold))))
            throw new ArgumentException("Plex regular has no packaged bold face.", nameof(corner));
    }

    public static PresentationLayerPayloadV1 Create(string sourceIdentitySha256, FrameFactsSnapshotV1 snapshot,
        FrameFactsPresentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateOptions(options);
        var context = snapshot.ToContext();
        var corners = options.Corners().Select(corner =>
        {
            var parsed = FrameFactsTemplate.Parse(corner.Template, FrameFactsRetainedCatalog.Catalog).Template!;
            var result = FrameFactsEvaluator.Evaluate(parsed, context, corner.MissingPolicy == "DropLine"
                ? FrameFactsMissingPolicy.DropLine : FrameFactsMissingPolicy.Placeholder);
            if (!result.IsValid) throw new ArgumentException($"{corner.Anchor}: {result.Error!.Code} at {result.Error.SourceOffset}.", nameof(options));
            return corner with
            {
                Lines = result.Lines.Select(line => new PresentationRichLineV1(line.Runs.Select(run =>
                    new PresentationRichRunV1(run.Text, (PresentationRichEmphasisV1)run.Style)).ToArray())).ToArray()
            };
        }).ToArray();
        var rich = new PresentationRichTextV1(snapshot.IdentitySha256, FrameFactsTemplate.GrammarId,
            FrameFactsRetainedCatalog.Version, ProducerVersion, PresentationRichText.LayoutVersion,
            PresentationRichText.FontSetIdentity, corners);
        return PresentationLayerPayloadJson.CreateRichText(sourceIdentitySha256, options.WidthPixels, options.HeightPixels, rich);
    }
}
