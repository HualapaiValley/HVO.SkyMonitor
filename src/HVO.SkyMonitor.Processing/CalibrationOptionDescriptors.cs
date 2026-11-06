namespace HVO.SkyMonitor.Processing;

/// <summary>The existing calibration scalar controls; retained models and masters remain opaque (#1566).</summary>
public static class CalibrationOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Normalization { get; } = Create(
        "calibration-normalization-options-v1", "linear-normalization-none-v1");

    public static ProcessingNodeOptionsDescriptor References { get; } = Create(
        "calibration-reference-options-v1", "reference-calibration-linear16-v2");

    private static ProcessingNodeOptionsDescriptor Create(string id, string version) => new(
        id, "Calibration", version, "Calibration",
        "Configure existing calibration behavior. Nested synthetic models and retained library masters are preserved; model editing, master acquisition and physical qualification are separate operations.",
        [
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable calibration",
                "Run calibration when this graph node is enabled. Enabled dependent nodes still require their producer.", true),
            ProcessingOptionDescriptor.Choice("strategy", "strategy", "Calibration strategy",
                "None normalizes raw samples without reference correction. SyntheticReferences requires an existing matching SyntheticCalibration model; a retained model cannot be used with None or CalibrationLibrary. CalibrationLibrary uses the existing active compatible masters and fails closed when unavailable. This panel does not create or remove models.",
                "None", ["None", "SyntheticReferences", "CalibrationLibrary"]),
            ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Calibration output variant",
                "Calibrated output route. Update variant-specific dependent inputs separately.", "none", 128, isIdentifier: true)
        ]);
}
