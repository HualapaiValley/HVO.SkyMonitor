namespace HVO.SkyMonitor.Processing;

/// <summary>Typed controls for optional step 30b regional stellar visibility assessment (#1925).</summary>
public static class RegionalStellarVisibilityOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "regional-stellar-visibility-options-v1",
        RegionalStellarVisibilityProducts.StepAlias,
        RegionalStellarVisibilityProducts.ImplementationVersion,
        "Regional stellar visibility",
        "Estimate regional cloud evidence by comparing independent expected detectable catalog stars against saved measured detections. Runs alongside and keeps separate provenance from clear-reference CloudAssessment; reports tile support and deficits without a cloud-cover percentage.",
        [
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable regional stellar visibility",
                "Run optional step 30b when enabled in a centered pipeline.", true),
            ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Visibility output variant",
                "Metadata output route for regional stellar visibility evidence.", RegionalStellarVisibilityProducts.Variant, 128, isIdentifier: true),
            ProcessingOptionDescriptor.WholeNumber("gridColumns", "gridColumns", "Grid columns",
                "Horizontal tiles for regional star visibility support.", 4, 1, RegionalStellarVisibilityOptions.MaximumGridDimension, "columns"),
            ProcessingOptionDescriptor.WholeNumber("gridRows", "gridRows", "Grid rows",
                "Vertical tiles for regional star visibility support.", 4, 1, RegionalStellarVisibilityOptions.MaximumGridDimension, "rows"),
            ProcessingOptionDescriptor.WholeNumber("minimumExpectedStarsPerTile", "minimumExpectedStarsPerTile", "Minimum expected stars per tile",
                "Minimum expected detectable stars required before a tile can be classified as clear or deficit.", 3, 1, 1000, "stars"),
            ProcessingOptionDescriptor.WholeNumber("minimumMissingStarsForDeficit", "minimumMissingStarsForDeficit", "Minimum missing stars for deficit",
                "Minimum missing expected detectable stars required to flag a regional detection deficit.", 2, 1, 1000, "stars"),
            ProcessingOptionDescriptor.WholeNumber("deficitVisibilityThresholdMillionths", "deficitVisibilityThresholdMillionths", "Deficit visibility threshold",
                "An assessed tile is marked as detection deficit below this measured-to-expected visibility ratio (600000 = 0.60).", 600_000, 1, 999_999, "millionths"),
            ProcessingOptionDescriptor.RealNumber("minimumExpectedSignalToNoise", "minimumExpectedSignalToNoise", "Minimum expected SNR",
                "Minimum predicted aperture SNR required to count a catalog star as expected detectable.", 8.0, 3.0, 1000.0, "SNR"),
            ProcessingOptionDescriptor.RealNumber("matchRadiusPixels", "matchRadiusPixels", "Star match radius",
                "Maximum distance in delivered-image pixels between an expected star and a measured detection.", 3.5, 0.5, 32.0, "px"),
            ProcessingOptionDescriptor.RealNumber("maximumAcceptableHfdPixels", "maximumAcceptableHfdPixels", "Maximum usable HFD",
                "Withhold assessment when median measured HFD exceeds this focus bound.", 6.0, 1.0, 64.0, "px"),
            ProcessingOptionDescriptor.RealNumber("moonGlareExclusionDegrees", "moonGlareExclusionDegrees", "Moon glare exclusion radius",
                "Exclude stars and tile samples within this angular separation of an above-horizon Moon.", 15.0, 0.0, 90.0, "deg"),
            ProcessingOptionDescriptor.Toggle("includeMask", "includeMask", "Include regional tile mask",
                "Include compact row-major deficit and assessed tile masks in the metadata artifact.", true)
        ]);
}
