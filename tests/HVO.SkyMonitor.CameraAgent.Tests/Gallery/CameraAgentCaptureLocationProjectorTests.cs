using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCaptureLocationProjectorTests
{
    [TestMethod]
    [DataRow("exact", "Available")]
    [DataRow("no-manifest", "Unavailable")]
    [DataRow("no-provenance", "NotRetained")]
    [DataRow("no-history", "Unavailable")]
    [DataRow("history-unavailable", "Unavailable")]
    [DataRow("wrong-provenance", "Unavailable")]
    [DataRow("invalid-snapshot", "Unavailable")]
    [DataRow("outside-interval", "Unavailable")]
    public void UsesOnlyExactValidatedHistoryAtExposureTime(string scenario, string expected)
    {
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, [1, 0, 2, 0, 3, 0, 4, 0]).Descriptor;
        var instant = descriptor.Timing.ExposureStartedUtc;
        var captured = DeploymentLocationSnapshot.Create("private-location-identity", 7, "manual", null,
            instant.AddHours(-2), scenario == "outside-interval" ? instant.AddHours(-1) : instant.AddHours(1),
            35.33, -113.99, 1100, "America/Phoenix");
        descriptor = descriptor with { Location = captured.ToProvenance() };
        var history = new Mock<IDeploymentLocationStore>(MockBehavior.Strict);
        history.SetupGet(store => store.Active).Returns(DeploymentLocationSnapshot.Create(
            "private-location-identity", 42, "manual", null, instant, null, 52, 10, 12, "Europe/Berlin"));
        if (scenario is not ("no-manifest" or "no-provenance" or "no-history"))
        {
            var read = history.Setup(store => store.Resolve(captured.ToProvenance(), instant));
            if (scenario == "history-unavailable") read.Throws(new InvalidDataException("private-history-file"));
            else read.Returns(scenario switch
            {
                "wrong-provenance" => DeploymentLocationSnapshot.Create("private-location-identity", 8, "manual", null,
                    captured.EffectiveFromUtc, captured.EffectiveUntilUtc, 52, 10, 12, "Europe/Berlin"),
                "invalid-snapshot" => captured with { CanonicalSha256 = new string('0', 64) },
                _ => captured
            });
        }

        var facts = CameraAgentCaptureLocationProjector.Project(
            scenario == "no-manifest" ? null : scenario == "no-provenance" ? descriptor with { Location = null } : descriptor,
            scenario == "no-history" ? null : history.Object);

        Assert.AreEqual(expected, facts.Availability);
        if (scenario == "exact")
        {
            Assert.AreEqual(35.33, facts.LatitudeDegrees);
            Assert.AreEqual(-113.99, facts.LongitudeDegrees);
            Assert.AreEqual(1100d, facts.ElevationMeters);
            Assert.AreEqual("America/Phoenix", facts.TimeZoneId);
            Assert.AreEqual(7L, facts.DeploymentVersion);
        }
        else
        {
            Assert.IsNull(facts.LatitudeDegrees);
            Assert.IsNull(facts.LongitudeDegrees);
            Assert.IsNull(facts.TimeZoneId);
            Assert.IsNull(facts.DeploymentVersion);
        }
        history.VerifyGet(store => store.Active, Times.Never);
        history.Verify(store => store.Resolve(It.IsAny<CaptureLocationProvenance>(), It.IsAny<DateTimeOffset?>()),
            scenario is "no-manifest" or "no-provenance" or "no-history" ? Times.Never() : Times.Once());
        history.VerifyNoOtherCalls();
    }
}
