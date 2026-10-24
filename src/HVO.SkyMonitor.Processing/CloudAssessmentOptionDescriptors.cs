namespace HVO.SkyMonitor.Processing;

/// <summary>The current cloud assessment's scalar controls over retained inputs (#1565).</summary>
public static class CloudAssessmentOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "cloud-assessment-options-v1", "CloudAssessment", "comparative-tiled-transmission-v1", "Cloud assessment",
        "Assess the current image against its retained compatible clear reference. Reference provisioning stays unchanged; missing or unusable evidence never becomes quantified clear sky. These controls do not qualify a physical cloud profile.",
        [
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable cloud assessment",
                "Run the existing assessment when this graph node is enabled. Disabled producers cannot supply enabled dependent nodes.", true),
            ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Assessment output variant",
                "Metadata output route. Update variant-specific dependent inputs separately.", "cloud-assessment-v1", 128, isIdentifier: true),
            ProcessingOptionDescriptor.WholeNumber("gridColumns", "gridColumns", "Grid columns",
                "Tile columns in the current image. The grid must fit the input dimensions; at most 4096 tiles are supported.", 16, 1, 64, "columns"),
            ProcessingOptionDescriptor.WholeNumber("gridRows", "gridRows", "Grid rows",
                "Tile rows in the current image. The grid must fit the input dimensions; reference compatibility stays unchanged.", 12, 1, 64, "rows"),
            ProcessingOptionDescriptor.WholeNumber("transmissionThresholdMillionths", "transmissionThresholdMillionths", "Transmission threshold",
                "A valid tile is cloudy below this transmission ratio. 850000 millionths means 0.85.", 850_000, 1, 999_999, "millionths"),
            ProcessingOptionDescriptor.WholeNumber("minimumReferenceSignal", "minimumReferenceSignal", "Minimum reference signal",
                "Minimum clear-reference signal above black level accepted for comparison.", 64, 0, ushort.MaxValue, "ADU"),
            ProcessingOptionDescriptor.WholeNumber("minimumSamplesPerTile", "minimumSamplesPerTile", "Minimum samples per tile",
                "Minimum accepted samples for a valid tile. Too little support yields unavailable coverage, not zero cloud.", 16, 1, int.MaxValue, "samples"),
            ProcessingOptionDescriptor.WholeNumber("maximumSaturatedFractionMillionths", "maximumSaturatedFractionMillionths", "Maximum saturated fraction",
                "Maximum saturated fraction of all considered samples. 100000 millionths means 10 percent; exceeding it marks the assessment contaminated.", 100_000, 0, 1_000_000, "millionths"),
            ProcessingOptionDescriptor.Toggle("includeMask", "includeMask", "Include cloud tile mask",
                "Include the existing row-major tile mask in assessment metadata; region evidence is retained either way.", true)
        ]);
}
