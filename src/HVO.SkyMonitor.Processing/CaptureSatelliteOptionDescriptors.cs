namespace HVO.SkyMonitor.Processing;

/// <summary>Existing capture-bound satellite routes and diagnostics, at their retained producer versions.</summary>
public static class CaptureSatelliteOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Tracks { get; } = new(
        "capture-satellite-tracks-options-v1", "CaptureSatelliteTracks", CaptureSatelliteProducts.Version,
        "Capture satellite predictions",
        "Configure the existing capture-bound prediction. Catalog selections, work bounds and retained time availability are preserved. Predictions remain distinct from detections; saving does not activate a draft.",
        [
            ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Satellite prediction output variant",
                "Prediction route, also used for the -inputs and -status products. Set the satellite layer input variant to the same value.", "capture-satellites-v1", 96, isIdentifier: true),
            ProcessingOptionDescriptor.Toggle("allowStaleDiagnostics", "allowStaleDiagnostics", "Allow stale-element diagnostics",
                "Permit explicitly marked diagnostic predictions from stale elements. Stale status and scientific qualification remain visible.", false)
        ]);

    public static ProcessingNodeOptionsDescriptor Layer { get; } = new(
        "capture-satellite-layer-options-v1", "CaptureSatelliteLayer", OrbitalTrackPresentation.ProducerVersion,
        "Predicted satellite layer",
        "Select the existing retained prediction route. The producer keeps its dashed tracks and prediction, source-validity and uncertainty captions. No orbital provider is queried by this editor.",
        [
            ProcessingOptionDescriptor.Text("inputVariant", "inputVariant", "Satellite layer input variant",
                "Must match the upstream prediction output variant, including its retained -inputs and -status products. Layer output uses this variant plus -layer.", "capture-satellites-v1", 96, isIdentifier: true)
        ]);
}
