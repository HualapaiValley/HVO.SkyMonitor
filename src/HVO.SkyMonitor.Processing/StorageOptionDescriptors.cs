namespace HVO.SkyMonitor.Processing;

public static class StorageOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "capture-storage-options-v1", "Storage", "cameraagent-step-v2:Storage", "Silo publication",
        "The active capture profile sets publication for this node. Configure Silo storage and retention on the CameraAgent instance.",
        [
            ProcessingOptionDescriptor.Toggle("updateLatestFrame", "updateLatestFrame", "Update latest frame",
                "The active capture profile controls publication of the selected preview to Current Sky.", true),
            ProcessingOptionDescriptor.Toggle("publishArchive", "publishArchive", "Publish archive selection",
                "The active capture profile selects whether to record durable products as a Silo archive group.", false)
        ]);
}
