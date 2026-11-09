namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void StoragePolicyPanelExplainsTheActiveCaptureProfileOwner()
    {
        var descriptor = HVO.SkyMonitor.Processing.StorageOptionDescriptors.Descriptor;
        StringAssert.Contains(descriptor.Help, "active capture profile", StringComparison.Ordinal);
        foreach (var option in descriptor.Options)
            StringAssert.Contains(option.Help, "active capture profile", StringComparison.Ordinal);
    }
}
