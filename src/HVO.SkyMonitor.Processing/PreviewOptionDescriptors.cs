using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Existing preview modes and JPEG scalar controls, without a transfer-mode or recipe upgrade (#1567).</summary>
public static class PreviewOptionDescriptors
{
    public const string PercentileVersion = "encoded-preview-v1";
    public const string FixedVersion = "fixed-preview-v1";

    public static ImmutableArray<ProcessingNodeOptionsDescriptor> All { get; } =
    [
        Preview("preview-options-v1", "Preview", "Preview", "default", false),
        Preview("fixed-preview-options-v1", "Preview", "Preview", "default", true),
        Preview("calibrated-preview-options-v1", "CalibratedPreview", "Calibrated preview", "calibrated-preview", false),
        Preview("fixed-calibrated-preview-options-v1", "CalibratedPreview", "Calibrated preview", "calibrated-preview", true),
        Preview("combined-preview-options-v1", "CombinedPreview", "Combined preview", "combined-preview", false),
        Preview("fixed-combined-preview-options-v1", "CombinedPreview", "Combined preview", "combined-preview", true),
        new("jpeg-encoding-options-v1", "JpegEncoding", "packed-jpeg-v1", "JPEG encoding",
            "Encode the existing packed annotated preview with JPEG encoding recipe 1.0.0 (packed-jpeg-v1). Keep original dimensions or explicitly set a maximum dimension; quality, routing and preview transfer remain separate settings.",
            [
                ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "JPEG output variant",
                    "Final JPEG output route. Update variant-specific dependent inputs separately.", "annotated-final-jpeg", 128, isIdentifier: true),
                ProcessingOptionDescriptor.WholeNumber("jpegQuality", "jpegQuality", "JPEG quality",
                    "Quality of the existing JPEG encoder; higher values retain more detail and usually use more bytes.", JpegImageCodec.DefaultQuality, 1, 100),
                ProcessingOptionDescriptor.NullableWholeNumber("maximumDimension", "maximumDimension", "Maximum JPEG dimension",
                    "Optional limit on the longest side. Leave blank or use original dimensions to clear the limit; smaller images are not enlarged.",
                    null, 1, 16384, "pixels", nullValueLabel: "No limit", nullActionLabel: "Use original dimensions")
            ])
    ];

    public static bool IsPreviewAlias(string alias) =>
        string.Equals(alias, "Preview", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(alias, "CalibratedPreview", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(alias, "CombinedPreview", StringComparison.OrdinalIgnoreCase);

    /// <summary>Match the existing case-insensitive options binder: the last FixedTransfer member selects the mode.</summary>
    public static string VersionFor(JsonElement? options)
    {
        var transfer = options is { ValueKind: JsonValueKind.Object } source
            ? source.EnumerateObject().LastOrDefault(static member =>
                string.Equals(member.Name, "fixedTransfer", StringComparison.OrdinalIgnoreCase)).Value
            : default;
        return transfer.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? PercentileVersion : FixedVersion;
    }

    private static ProcessingNodeOptionsDescriptor Preview(string id, string alias, string title, string variant, bool fixedTransfer)
    {
        var version = fixedTransfer ? FixedVersion : PercentileVersion;
        var mode = fixedTransfer
            ? "Fixed transfer is active. Edit the existing native black/white levels and gamma. Black level must be less than white level. Percentile/asinh controls do not affect this mode and are not offered; this panel cannot change transfer modes."
            : "Percentile/asinh transfer is active. Black percentile must be less than white percentile. FixedTransfer remains absent or null; this panel cannot change transfer modes.";
        return new(id, alias, version, title,
            $"{mode} The existing shared recipe remains semantic version 1.0.0, implementation {version}, with packed output.",
            [
                ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable preview",
                    "Produce this preview when the graph node is enabled. Enabled dependent nodes still require their producer.", true),
                ProcessingOptionDescriptor.Text("recipeVersion", "recipeVersion", "Artifact recipe label",
                    "Retained artifact recipe label, not an implementation or transfer-mode selector. Existing custom labels are preserved.", "mono16-asinh-v2", null, isRequired: true),
                ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Preview output variant",
                    "Packed-preview output route. Update variant-specific dependent inputs separately.", variant, 128, isIdentifier: true),
                .. fixedTransfer ?
                (ImmutableArray<ProcessingOptionDescriptor>)
                [
                    ProcessingOptionDescriptor.NestedRealNumber("blackLevel", "fixedTransfer", "blackLevel", "Fixed black level",
                        "Native sample level mapped to black; must be less than white level. Use the input's native sample scale.", new FixedDisplayTransferOptions().BlackLevel, 0, ushort.MaxValue, "native levels"),
                    ProcessingOptionDescriptor.NestedRealNumber("whiteLevel", "fixedTransfer", "whiteLevel", "Fixed white level",
                        "Native sample level mapped to white; must exceed black level. Use the input's native sample scale.", new FixedDisplayTransferOptions().WhiteLevel, 0, ushort.MaxValue, "native levels"),
                    ProcessingOptionDescriptor.NestedRealNumber("gamma", "fixedTransfer", "gamma", "Fixed gamma",
                        "Gamma of the existing fixed display transfer.", new FixedDisplayTransferOptions().Gamma, 0.1, 10)
                ] :
                [
                    ProcessingOptionDescriptor.RealNumber("blackPercentile", "blackPercentile", "Black percentile",
                        "Per-frame black-point percentile, expressed as a fraction; must be less than the white percentile.", 0.5, 0, 0.999999),
                    ProcessingOptionDescriptor.RealNumber("whitePercentile", "whitePercentile", "White percentile",
                        "Per-frame white-point percentile, expressed as a fraction; must exceed the black percentile.", 0.9999, 0.000001, 1),
                    ProcessingOptionDescriptor.RealNumber("asinhStrength", "asinhStrength", "Asinh strength",
                        "Strength of the existing asinh display stretch.", 4, 0.01, 1000)
                ]
            ]);
    }
}
