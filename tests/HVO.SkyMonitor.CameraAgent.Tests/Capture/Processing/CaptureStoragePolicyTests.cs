using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Capture.Processing")]
public sealed class CaptureStoragePolicyTests
{
    [TestMethod]
    [DataRow(false, false, "UpdateLatestFrame")]
    [DataRow(true, true, "PublishArchive")]
    public void EverySiloPublicationOptionParticipatesInProfileComparison(bool latest, bool archive, string field)
    {
        var requested = Pipeline(new { UpdateLatestFrame = latest, PublishArchive = archive });
        var failure = Assert.ThrowsExactly<ValidationException>(() =>
            CaptureStoragePolicy.EnsureMatchesProfile(requested, Pipeline(new SiloCaptureProcessingStepOptions())));
        Assert.AreEqual(field, failure.ValidationResult!.MemberNames.Single());
    }

    [TestMethod]
    public void EquivalentDefaultsKeepTheSourceAndMissingStorageNodeFailsClosed()
    {
        var requested = Pipeline(new { updateLatestFrame = true });
        var before = JsonSerializer.Serialize(requested);
        CaptureStoragePolicy.EnsureMatchesProfile(requested, Pipeline(new SiloCaptureProcessingStepOptions()));
        Assert.AreEqual(before, JsonSerializer.Serialize(requested));
        var failure = Assert.ThrowsExactly<ValidationException>(() =>
            CaptureStoragePolicy.EnsureMatchesProfile(requested, null));
        Assert.AreEqual("StorageNode", failure.ValidationResult!.MemberNames.Single());
        CaptureStoragePolicy.EnsureMatchesProfile(requested with { Steps = [requested.Steps[0] with { Enabled = false }] }, null);
    }

    [TestMethod]
    public void DiagnosticProjectionDropsUntrustedMemberNames()
    {
        var failure = new ValidationException(new ValidationResult(CaptureStoragePolicy.RejectionMessage,
            ["/private/operator/path", "PublishArchive"]), null, null);
        var message = CaptureStoragePolicy.DescribeMismatch(failure);
        StringAssert.Contains(message, "PublishArchive", StringComparison.Ordinal);
        Assert.IsFalse(message.Contains("/private/", StringComparison.Ordinal));
    }

    private static CapturePipelineConfig Pipeline<T>(T options)
        => new([new("Storage", "storage", Options: JsonSerializer.SerializeToElement(options))]);
}
