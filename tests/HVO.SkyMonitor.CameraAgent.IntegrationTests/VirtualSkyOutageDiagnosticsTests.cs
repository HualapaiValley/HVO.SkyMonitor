using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class VirtualSkyOutageDiagnosticsTests
{
    [TestMethod]
    public void StorageFormattingBoundsSamplesAndPreservesCaptureFilteringAndRoles()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var now = DateTimeOffset.UnixEpoch;
        var cases = new[]
        {
            new VirtualSkyOutageDiagnostics.Observation(Guid.Parse("22222222-2222-2222-2222-222222222222"), 10, FrameArtifactRole.Raw, FrameArtifactRole.Raw, now, true, false, VirtualSkyOutageDiagnostics.ManifestState.Valid, true, CaptureMeteringOutcome.Measured),
            new VirtualSkyOutageDiagnostics.Observation(id, 11, FrameArtifactRole.Raw, FrameArtifactRole.Raw, now.AddSeconds(1), false, true, VirtualSkyOutageDiagnostics.ManifestState.Valid, true, null),
            new VirtualSkyOutageDiagnostics.Observation(id, 11, FrameArtifactRole.Preview, FrameArtifactRole.Preview, now.AddSeconds(1), false, true, VirtualSkyOutageDiagnostics.ManifestState.Valid, true, null),
            new VirtualSkyOutageDiagnostics.Observation(null, null, FrameArtifactRole.Preview, null, null, null, null, VirtualSkyOutageDiagnostics.ManifestState.Invalid, false, null)
        };
        using var json = JsonDocument.Parse(VirtualSkyOutageDiagnostics.FormatStorage(10_000, Enumerable.Range(0, 100).Select(i => cases[i % cases.Length]), true));
        Assert.IsTrue(json.RootElement.GetProperty("ListCapReached").GetBoolean());
        var samples = json.RootElement.GetProperty("Observations").EnumerateArray().ToArray();
        Assert.HasCount(VirtualSkyOutageDiagnostics.SampleLimit, samples);
        Assert.IsTrue(samples[0].GetProperty("AtCheckpoint").GetBoolean());
        Assert.IsFalse(samples[0].GetProperty("AfterCheckpoint").GetBoolean());
        Assert.AreEqual("Measured", samples[0].GetProperty("Metering").GetString());
        Assert.IsTrue(samples[1].GetProperty("AfterCheckpoint").GetBoolean());
        Assert.AreEqual("Raw", samples[1].GetProperty("ManifestRole").GetString());
        Assert.AreEqual("Preview", samples[2].GetProperty("ManifestRole").GetString());
        Assert.AreEqual(samples[1].GetProperty("CaptureId").GetGuid(), samples[2].GetProperty("CaptureId").GetGuid());
        Assert.AreEqual("Invalid", samples[3].GetProperty("Manifest").GetString());
        using var empty = JsonDocument.Parse(VirtualSkyOutageDiagnostics.FormatStorage(null, []));
        Assert.AreEqual(JsonValueKind.Null, empty.RootElement.GetProperty("ListedCount").ValueKind);
        Assert.AreEqual(0, empty.RootElement.GetProperty("Observations").GetArrayLength());
    }

    [TestMethod]
    public void FailedDiagnosticSectionDoesNotExposeExceptionMessagesOrPaths()
    {
        var text = VirtualSkyOutageDiagnostics.Section("storage", () => throw new IOException("secret-credential /private/location"));
        Assert.AreEqual("storage: unavailable(IOException)", text);
        Assert.IsFalse(text.Contains("secret-credential", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("/private/location", StringComparison.Ordinal));
    }
}
