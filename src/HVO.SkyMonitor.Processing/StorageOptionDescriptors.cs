namespace HVO.SkyMonitor.Processing;

/// <summary>Existing local artifact storage controls; graph drafts never perform storage operations.</summary>
public static class StorageOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "capture-storage-options-v1", "Storage", "cameraagent-step-v2:Storage", "Artifact storage",
        "Storage is owned by the active capture profile. Enabled Storage nodes must match that profile's directory, retention, latest-frame/upload flags and artifact policies. Preview, save, validation, activation and rollback reject differences. Pause acquisition and drain standard processing before changing the profile, then refresh Graphs to obtain its matching configured-basic source, reconcile, validate and activate before resuming. Raw and capture-time scene evidence are retained while other incompatible standard processing is skipped; explicitly replay retained captures to recover their results.",
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
