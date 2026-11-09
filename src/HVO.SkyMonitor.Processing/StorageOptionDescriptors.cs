namespace HVO.SkyMonitor.Processing;

/// <summary>Existing local artifact storage controls; graph drafts never perform storage operations.</summary>
public static class StorageOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "capture-storage-options-v1", "Storage", "cameraagent-step-v2:Storage", "Artifact storage",
        "Storage is owned by the active capture profile. Enabled Storage nodes must match that profile's directory, retention, latest-frame/upload flags and artifact policies. Preview, save, validation, activation and rollback reject differences. Pause acquisition and finish in-flight processing before changing the profile, then reconcile, validate and activate this graph before resuming. Raw captures continue to be retained while incompatible standard processing is skipped.",
        [
            ProcessingOptionDescriptor.Text("storageRoot", "storageRoot", "Storage directory",
                "Must match the active capture profile. This directory cannot be changed independently in a graph draft; editing does not move existing artifacts.", "/tmp/camera", null, isRequired: true),
            ProcessingOptionDescriptor.WholeNumber("retentionDays", "retentionDays", "Artifact retention",
                "Must match the active capture profile, whose policy also controls retention sweeps. Artifact-specific policies may extend this period but cannot shorten it.", 30, 1, 3650, "days"),
            ProcessingOptionDescriptor.Toggle("updateLatestFrame", "updateLatestFrame", "Update latest frame",
                "Must match the active capture profile. Publish the selected preview through the existing latest-frame service.", true),
            ProcessingOptionDescriptor.Toggle("queueForUpload", "queueForUpload", "Queue artifacts for upload",
                "Must match the active capture profile, which also controls upload draining. Existing per-artifact overrides still apply.", false)
        ]);
}
