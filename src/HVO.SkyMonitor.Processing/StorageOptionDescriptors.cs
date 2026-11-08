namespace HVO.SkyMonitor.Processing;

/// <summary>Existing local artifact storage controls; graph drafts never perform storage operations.</summary>
public static class StorageOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "capture-storage-options-v1", "Storage", "cameraagent-step-v2:Storage", "Artifact storage",
        "Configure this node's existing local publication policy. Artifact-specific policies remain preserved. Saving a draft does not move files or change retention; changes take effect only after validation and activation.",
        [
            ProcessingOptionDescriptor.Text("storageRoot", "storageRoot", "Storage directory",
                "Local artifact directory used by this node. This does not change the host's raw-ingress directory or move existing artifacts.", "/tmp/camera", null, isRequired: true),
            ProcessingOptionDescriptor.WholeNumber("retentionDays", "retentionDays", "Artifact retention",
                "Existing root retention period. Artifact-specific policies may extend this period but cannot shorten it.", 30, 1, 3650, "days"),
            ProcessingOptionDescriptor.Toggle("updateLatestFrame", "updateLatestFrame", "Update latest frame",
                "Publish the selected preview through the existing latest-frame service.", true),
            ProcessingOptionDescriptor.Toggle("queueForUpload", "queueForUpload", "Queue artifacts for upload",
                "Use the existing upload outbox when central integration is enabled. Existing per-artifact overrides still apply.", false)
        ]);
}
