using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// The normalization freezer rejects a gain response whose sensor identity differs from the family's and from every
/// selected capture's. These pin that the live producers agree, so a configured sensor is never rejected as foreign.
/// </summary>
[TestClass]
public sealed class SensorProfileIdentityAlignmentTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void TheGainResponseNamesTheSensorExactlyAsTheRawCaptureDescriptorDoes()
    {
        var configuration = ProcessingConformanceFixture.CameraConfig;
        var descriptor = CreateRawDescriptor(configuration);
        var resolution = GainResponseResolver.Resolve(
            GainResponseSourceKind.VirtualAsi174MmModel, configuration.Rig.Sensor, configuration.Rig.Readout);

        Assert.IsTrue(resolution.IsAvailable, resolution.ReasonCode);
        Assert.AreEqual(descriptor.Profiles.Sensor.Sha256, resolution.Response!.Descriptor.SensorProfileSha256);
        Assert.AreEqual(ProcessingConformanceFixture.SensorProfileSha256, descriptor.Profiles.Sensor.Sha256);
        Assert.AreEqual(SensorProfileIdentity.ComputeSha256(configuration.Rig.Sensor), descriptor.Profiles.Sensor.Sha256);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AChangedSensorProfileStillYieldsAForeignResponseIdentity()
    {
        var configuration = ProcessingConformanceFixture.CameraConfig;
        var descriptor = CreateRawDescriptor(configuration);
        var changed = configuration.Rig.Sensor with { SensorRecipeVersion = "sensor-v2" };
        var resolution = GainResponseResolver.Resolve(
            GainResponseSourceKind.VirtualAsi174MmModel, changed, configuration.Rig.Readout);

        Assert.IsTrue(resolution.IsAvailable, resolution.ReasonCode);
        Assert.AreNotEqual(descriptor.Profiles.Sensor.Sha256, resolution.Response!.Descriptor.SensorProfileSha256);
    }

    private static ReconstructionDescriptor CreateRawDescriptor(CameraModuleConfig configuration)
    {
        var timestamp = ProcessingConformanceFixture.CapturedUtc;
        var frame = new CameraFrame(
            timestamp,
            ProcessingConformanceFixture.Layout.Width,
            ProcessingConformanceFixture.Layout.Height,
            ProcessingConformanceFixture.Layout.PixelFormat,
            ProcessingConformanceFixture.Payload,
            new FrameMetadata(TimeSpan.FromSeconds(1), 150, 0));
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(timestamp, frame.Metadata.Exposure, CaptureMode.Still),
            new CaptureResult(
                frame,
                new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null),
                TimeSpan.Zero,
                CaptureMode.Still,
                false),
            timestamp,
            frame.Metadata.Exposure,
            TimeSpan.Zero);
        return RawCaptureDescriptorFactory.Create(
            configuration,
            submission,
            new RawCaptureIdentity(configuration.AgentId!, 1, Guid.NewGuid(), Guid.NewGuid()),
            new string('F', 64),
            timestamp.AddSeconds(2));
    }
}
