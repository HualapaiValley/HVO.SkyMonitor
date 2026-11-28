using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    [DataRow(CaptureScheduleIntervalSource.WeeklyWindow, "morning")]
    [DataRow(CaptureScheduleIntervalSource.DateExceptionWindow, "morning")]
    [DataRow(CaptureScheduleIntervalSource.LegacyCompatibility, null)]
    [DataRow(CaptureScheduleIntervalSource.ForceOpenOverride, null)]
    public void CaptureConditionUsesOnlyNamedScheduleWindows(CaptureScheduleIntervalSource source, string? expectedWindow)
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            var descriptor = CaptureConditionDescriptor(source);
            Assert.AreEqual(expectedWindow is null ? null : new SiloCaptureCondition(expectedWindow),
                ingress.ReadCaptureCondition(descriptor));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(true, true, false, "Day")]
    [DataRow(false, true, false, null)]
    [DataRow(true, false, false, null)]
    [DataRow(true, true, true, null)]
    public void CaptureConditionUsesVerifiedSolarFallbackOrKeepsOnlyWindow(
        bool hasLocationStore, bool hasEphemeris, bool resolveFails, string? expectedSky)
    {
        var root = CreateRoot();
        try
        {
            var descriptor = CaptureConditionDescriptor(CaptureScheduleIntervalSource.WeeklyWindow);
            var utc = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
            var location = DeploymentLocationSnapshot.Create("recorded", 1, "fixture", null, utc.AddDays(-1), null, 0, 0, 0, "UTC");
            descriptor = descriptor with
            {
                Location = location.ToProvenance(),
                Timing = descriptor.Timing with { ExposureStartedUtc = utc },
                CycleEvidence = null
            };
            var services = new ServiceCollection();
            var locations = new Mock<IDeploymentLocationStore>(MockBehavior.Strict);
            var resolve = locations.Setup(store => store.Resolve(location.ToProvenance(), utc));
            if (resolveFails) resolve.Throws(new KeyNotFoundException());
            else resolve.Returns(location);
            if (hasLocationStore) services.AddSingleton(locations.Object);
            if (hasEphemeris) services.AddSingleton<IPlanetEphemeris, AstronomyEnginePlanetEphemeris>();
            using var provider = services.BuildServiceProvider();
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System), storagePolicyServices: provider);

            Assert.AreEqual(new SiloCaptureCondition("morning", expectedSky), ingress.ReadCaptureCondition(descriptor));
            locations.Verify(store => store.Resolve(location.ToProvenance(), utc),
                hasLocationStore && hasEphemeris ? Times.Once() : Times.Never());
        }
        finally { DeleteRoot(root); }
    }

    private static ReconstructionDescriptor CaptureConditionDescriptor(CaptureScheduleIntervalSource source)
    {
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono8, 2, 2, 2, [1, 2, 3, 4]).Descriptor;
        var utc = descriptor.Timing.ExposureStartedUtc;
        var hash = new string('a', 64);
        var admission = new CaptureScheduleAdmissionEvidence(CaptureScheduleAdmissionEvidence.CurrentSchemaVersion,
            "revision", hash, hash, "setpoint", CaptureScheduleAdmissionReason.WeeklyWindow, source,
            utc, utc.AddHours(-1), utc.AddHours(1), "morning", "v1", hash, hash, "recorded", 1);
        return descriptor with
        {
            CenteredAcquisition = new(CaptureCadenceMode.MinimumStartInterval, TimeSpan.FromSeconds(1), utc,
                TimeSpan.Zero, null, false, null, admission)
        };
    }
}
