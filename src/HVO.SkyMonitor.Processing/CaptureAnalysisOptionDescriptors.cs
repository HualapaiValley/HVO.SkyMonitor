namespace HVO.SkyMonitor.Processing;

/// <summary>Existing capture analysis controls, without replacing retained scientific inputs.</summary>
public static class CaptureAnalysisOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor StellarAssociations { get; } = new(
        "measured-stellar-association-options-v1", "MeasuredStellarAssociations", "measured-stellar-associations-v1",
        "Measured stellar associations",
        "Measure raw stars against this capture's retained projected scene. Association thresholds and label policy are preserved. Saving creates a draft; validate and activate it separately.",
        [
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable measured stellar associations",
                "Run this node. Enabled dependents still require their measured inputs.", true),
            ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Measured association output variant",
                "Metadata output route. Update variant-specific dependent inputs separately.", "measured-stellar-associations-v1", 128, isIdentifier: true)
        ]);

    public static ProcessingNodeOptionsDescriptor Astrometry { get; } = new(
        "measured-astrometry-options-v1", "MeasuredAstrometry", "measured-astrometry-v1", "Measured astrometry",
        "Configure the existing sampled background assessment. The retained catalog, clock, previous accepted assessment and solver settings are preserved. This does not change rig calibration or qualify measured positions.",
        [
            ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Astrometry output variant",
                "Assessment metadata output route. Its retained context uses this variant plus -context.", "measured-astrometry-v1", 96, isIdentifier: true),
            ProcessingOptionDescriptor.WholeNumber("captureStride", "captureStride", "Capture stride",
                "Assess one admitted capture in this many. The existing minimum of 60 bounds live work; replay retains its original assessment.", 60, 60, 1_000_000, "captures")
        ]);
}
