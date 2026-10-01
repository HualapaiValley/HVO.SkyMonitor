using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCaptureProfileProjectorTests
{
    [TestMethod]
    public void VirtualPixelGeometryKeepsCadenceWhileUnrecordedPhysicalValuesRemainMissing()
    {
        var (descriptor, configuration, submission) = CreateCapture();
        configuration = configuration with { Rig = configuration.Rig with { Sensor = configuration.Rig.Sensor with { PixelSizeMicrons = 0 }, Optics = configuration.Rig.Optics with { FocalLengthMillimeters = 0, FieldOfViewDegrees = 0 } } };
        descriptor = descriptor with { Profiles = descriptor.Profiles with { Rig = descriptor.Profiles.Rig with { Sha256 = CameraRigProfileIdentity.ComputeSha256(configuration.Rig) } } };
        var context = CaptureLaneEnvelopeSerializer.Serialize(configuration, submission);
        var facts = CameraAgentCaptureProfileProjector.Project(descriptor, context.Json, context.Sha256);
        Assert.IsNotNull(facts);
        Assert.IsNull(facts.PixelSizeMicrons);
        Assert.IsNull(facts.FocalLengthMillimeters);
        Assert.IsNull(facts.FieldOfViewDegrees);
        Assert.AreEqual(TimeSpan.FromSeconds(5), facts.EffectiveInterval);
    }

    [TestMethod]
    public void UsesTheVerifiedCaptureEnvelopeWithoutGuessingLegacyCadenceMode()
    {
        var (descriptor, configuration, submission) = CreateCapture();
        var context = CaptureLaneEnvelopeSerializer.Serialize(configuration, submission);
        var facts = CameraAgentCaptureProfileProjector.Project(descriptor, context.Json, context.Sha256);
        Assert.IsNotNull(facts);
        Assert.AreEqual(3.75, facts.PixelSizeMicrons);
        Assert.AreEqual(2.8, facts.FocalLengthMillimeters);
        Assert.AreEqual(180, facts.FieldOfViewDegrees);
        Assert.AreEqual("Equidistant", facts.ProjectionModel);
        Assert.AreEqual(TimeSpan.FromSeconds(5), facts.EffectiveInterval);
        Assert.IsNull(facts.CadenceMode);
    }

    [TestMethod]
    [DataRow("checksum")]
    [DataRow("rig")]
    [DataRow("agent")]
    [DataRow("request")]
    [DataRow("negative-interval")]
    [DataRow("malformed")]
    public void RejectsAlteredUnrelatedOrInvalidCaptureEvidence(string fault)
    {
        var (descriptor, configuration, submission) = CreateCapture();
        if (fault == "rig") configuration = configuration with { Rig = configuration.Rig with { Optics = configuration.Rig.Optics with { FocalLengthMillimeters = 50 } } };
        if (fault == "agent") configuration = configuration with { AgentId = "other-private-device" };
        if (fault == "request") submission = submission with { Request = submission.Request with { RequestedStartUtc = submission.Request.RequestedStartUtc.AddSeconds(1) } };
        if (fault == "negative-interval") submission = submission with { EffectiveInterval = TimeSpan.FromSeconds(-1) };
        var context = CaptureLaneEnvelopeSerializer.Serialize(configuration, submission);
        if (fault == "checksum") context = (context.Json, new string('0', 64));
        if (fault == "malformed")
        {
            byte[] invalid = "{\"configuration\":null}"u8.ToArray();
            context = (invalid, Convert.ToHexString(SHA256.HashData(invalid)));
        }
        Assert.IsNull(CameraAgentCaptureProfileProjector.Project(descriptor, context.Json, context.Sha256));
    }

    private static (ReconstructionDescriptor, CameraModuleConfig, CaptureLoopSubmission) CreateCapture()
    {
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var rig = new CameraRigConfig(new SensorProfile("private-sensor-name", 2, 2, 3.75, SensorColorMode.Mono, CameraPixelFormat.Mono16),
            new OpticsProfile("Equidistant", 2.8, 180, 0), new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(99), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0));
        var configuration = new CameraModuleConfig(new ObservatoryLocation(42, -112, 1000, "America/Phoenix"),
            new CameraModuleDescriptor("private-module"), rig, CapturePipelineConfig.Empty, descriptor.Capture.AgentId);
        descriptor = descriptor with { Profiles = descriptor.Profiles with { Rig = descriptor.Profiles.Rig with { Sha256 = CameraRigProfileIdentity.ComputeSha256(rig) } } };
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(5), CaptureMode.Still),
            new CaptureResult(null, new CaptureSetpoint(TimeSpan.FromSeconds(1), 0, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            descriptor.Timing.ExposureStartedUtc, TimeSpan.FromSeconds(5), TimeSpan.Zero);
        return (descriptor, configuration, submission);
    }
}
