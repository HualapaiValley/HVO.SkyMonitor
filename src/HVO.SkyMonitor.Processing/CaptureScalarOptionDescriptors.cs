namespace HVO.SkyMonitor.Processing;

/// <summary>The existing CameraAgent scalar/toggle family, at its unchanged shared graph versions (#1563).</summary>
public static class CaptureScalarOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor ImageQuality { get; } = new(
        "image-quality-options-v1", "ImageQuality", "integer-image-statistics-v1", "Image quality",
        "Existing image statistics and their metadata output route.",
        [
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable image quality",
                "Run the existing image-quality analysis when this graph node is enabled.", true),
            ProcessingOptionDescriptor.Text("outputVariant", "outputVariant", "Quality output variant",
                "Metadata output route: a non-empty, trimmed identifier without control characters. Update dependent input routes separately.",
                "image-quality-v1", 128, isIdentifier: true)
        ]);

    // These steps have no shared recipe: their graph versions use the factory's existing alias-based fallback.
    public static ProcessingNodeOptionsDescriptor Telemetry { get; } = new(
        "telemetry-options-v1", "Telemetry", "cameraagent-step-v2:Telemetry", "Capture telemetry",
        "CameraAgent's existing local capture telemetry policy; it remains outside shared recipe execution.",
        [ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable telemetry",
            "Report capture samples, metrics and the completion log when this graph node is enabled. Archived replay does not publish telemetry.", true)]);

    public static ProcessingNodeOptionsDescriptor VirtualSkyCloudObservation { get; } = new(
        "virtual-cloud-observation-options-v1", "VirtualSkyCloudObservation", "cameraagent-step-v2:VirtualSkyCloudObservation",
        "Simulated cloud observations",
        "VirtualSky simulation only. This does not qualify a physical cloud sensor or make an unavailable step executable.",
        [ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable simulated cloud observations",
            "Publish simulated cloud cover only when the captured VirtualSky frame supplies valid cloud-scenario provenance. Archived replay does not publish observations.", true)]);
}
