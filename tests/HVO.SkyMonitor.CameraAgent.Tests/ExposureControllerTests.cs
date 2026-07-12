using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Exposure;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class ExposureControllerTests
{
    [TestMethod]
    public void ApplyControlPolicy_PreservesDisabledExposureAndGain()
    {
        var current = new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null);
        var automatic = new CaptureSetpoint(TimeSpan.FromSeconds(25), 187.5, null, null);

        var result = CameraModuleRunner.ApplyControlPolicy(new CameraControlPolicy
        {
            AutoExposure = CameraFeatureDirective.Disabled,
            AutoGain = CameraFeatureDirective.Disabled
        }, current, automatic);

        Assert.AreEqual(current.Exposure, result.Exposure);
        Assert.AreEqual(current.Gain, result.Gain);
    }

    [TestMethod]
    public void Next_WithoutMeasurement_UsesClampedNightDefault()
    {
        var decision = ExposureController.Next(CreateProfile(), null, night: true);
        Assert.AreEqual(ExposureAdjustmentReason.InitialDefault, decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(4), decision.Setpoint.Exposure);
        Assert.AreEqual(100d, decision.Setpoint.Gain);
    }

    [TestMethod]
    public void Next_WhenTooDark_IncreasesExposureByBoundedStep()
    {
        var decision = ExposureController.Next(CreateProfile(), 0.1, night: true);
        Assert.AreEqual(ExposureAdjustmentReason.TooDark, decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(5), decision.Setpoint.Exposure);
    }

    [TestMethod]
    public void Next_WhenTooBright_ClampsExposureToMinimum()
    {
        var profile = CreateProfile() with { Envelope = CreateProfile().Envelope! with { NightDefaults = new ExposureDefaults(TimeSpan.FromSeconds(1), 100) } };
        var decision = ExposureController.Next(profile, 0.9, night: true);
        Assert.AreEqual(ExposureAdjustmentReason.TooBright, decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(1), decision.Setpoint.Exposure);
    }

    private static PipelineExposureProfile CreateProfile()
        => new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), 1, 100,
            new ExposureEnvelope(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1, 200,
                new ExposureDefaults(TimeSpan.FromSeconds(1), 1), new ExposureDefaults(TimeSpan.FromSeconds(4), 100), 0.65));
}
