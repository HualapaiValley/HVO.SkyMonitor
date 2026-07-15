using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Exposure;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ExposureControllerTests
{
    [TestMethod]
    public void Initial_SelectsExplicitTwilightDefaults()
    {
        var setpoint = ExposureController.Initial(CreateProfile(), CaptureSolarRegime.Twilight);

        Assert.AreEqual(TimeSpan.FromSeconds(2), setpoint.Exposure);
        Assert.AreEqual(20d, setpoint.Gain);
    }

    [TestMethod]
    public void Next_WithoutMeasurement_PreservesActiveSetpoint()
    {
        var active = new CaptureSetpoint(TimeSpan.FromSeconds(3), 40, null, null);
        var decision = ExposureController.Next(
            CreateProfile(), active, null, CaptureSolarRegime.Night, true, true);

        Assert.AreEqual(CaptureControlDecisionReason.NoSample, decision.Reason);
        Assert.AreEqual(active, decision.Setpoint);
    }

    [TestMethod]
    public void Next_InsideHysteresis_DoesNotOscillate()
    {
        var active = new CaptureSetpoint(TimeSpan.FromSeconds(3), 40, null, null);
        var decision = ExposureController.Next(
            CreateProfile(), active, 0.62, CaptureSolarRegime.Night, true, true);

        Assert.AreEqual(CaptureControlDecisionReason.WithinHysteresis, decision.Reason);
        Assert.AreEqual(active, decision.Setpoint);
    }

    [TestMethod]
    public void Next_WhenDark_AdjustsFromActiveExposureRatherThanDefaults()
    {
        var active = new CaptureSetpoint(TimeSpan.FromSeconds(2), 40, null, null);
        var decision = ExposureController.Next(
            CreateProfile(), active, 0.1, CaptureSolarRegime.Night, true, true);

        Assert.AreEqual(CaptureControlDecisionReason.ExposureAdjusted, decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), decision.Setpoint.Exposure);
        Assert.AreEqual(40d, decision.Setpoint.Gain);
    }

    [TestMethod]
    public void Next_WhenDarkAtZeroExposure_AdvancesByOneTick()
    {
        var profile = CreateProfile() with
        {
            Envelope = CreateProfile().Envelope! with
            {
                MinExposure = TimeSpan.Zero,
                DayDefaults = new ExposureDefaults(TimeSpan.Zero, 1)
            }
        };
        var active = new CaptureSetpoint(TimeSpan.Zero, 40, null, null);

        var decision = ExposureController.Next(
            profile, active, 0, CaptureSolarRegime.Day, true, false);

        Assert.AreEqual(CaptureControlDecisionReason.ExposureAdjusted, decision.Reason);
        Assert.AreEqual(TimeSpan.FromTicks(1), decision.Setpoint.Exposure);
    }

    [TestMethod]
    public void Next_AtPreferredExposureLimit_AdjustsGain()
    {
        var active = new CaptureSetpoint(TimeSpan.FromSeconds(5), 40, null, null);
        var decision = ExposureController.Next(
            CreateProfile(), active, 0.1, CaptureSolarRegime.Night, true, true);

        Assert.AreEqual(CaptureControlDecisionReason.GainAdjusted, decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(5), decision.Setpoint.Exposure);
        Assert.AreEqual(50d, decision.Setpoint.Gain);
    }

    [TestMethod]
    public void Next_ExposureFirstSequence_UsesPreferredLimitThenConvergesWithoutOscillation()
    {
        var initial = new CaptureSetpoint(TimeSpan.FromSeconds(1), 10, null, null);
        var decisions = RunClosedLoop(CreateProfile(), initial, targetExposureGainProduct: 60);
        var adjustments = decisions
            .TakeWhile(decision => decision.Reason != CaptureControlDecisionReason.WithinHysteresis)
            .ToArray();
        var firstGainAdjustment = Array.FindIndex(
            adjustments,
            decision => decision.Reason == CaptureControlDecisionReason.GainAdjusted);

        Assert.IsTrue(firstGainAdjustment > 0);
        Assert.IsTrue(adjustments[..firstGainAdjustment].All(
            decision => decision.Reason == CaptureControlDecisionReason.ExposureAdjusted));
        Assert.IsTrue(adjustments[..firstGainAdjustment].All(
            decision => decision.Setpoint.Gain == initial.Gain));
        Assert.IsTrue(adjustments[firstGainAdjustment..].All(
            decision => decision.Reason == CaptureControlDecisionReason.GainAdjusted));
        Assert.IsTrue(adjustments[firstGainAdjustment..].All(
            decision => decision.Setpoint.Exposure == TimeSpan.FromSeconds(5)));
        Assert.IsTrue(decisions.All(decision =>
            decision.Setpoint.Exposure >= TimeSpan.FromSeconds(1) &&
            decision.Setpoint.Exposure <= TimeSpan.FromSeconds(5) &&
            decision.Setpoint.Gain is >= 1 and <= 200));
        AssertSettledWithoutTwoPointOscillation(decisions);
    }

    [TestMethod]
    public void Next_GainFirstSequence_UsesPreferredLimitThenConvergesWithoutOscillation()
    {
        var initial = new CaptureSetpoint(TimeSpan.FromSeconds(1), 10, null, null);
        var profile = CreateProfile(ExposureGainPreference.GainFirst, maxGain: 40);
        var decisions = RunClosedLoop(profile, initial, targetExposureGainProduct: 80);
        var adjustments = decisions
            .TakeWhile(decision => decision.Reason != CaptureControlDecisionReason.WithinHysteresis)
            .ToArray();
        var firstExposureAdjustment = Array.FindIndex(
            adjustments,
            decision => decision.Reason == CaptureControlDecisionReason.ExposureAdjusted);

        Assert.IsTrue(firstExposureAdjustment > 0);
        Assert.IsTrue(adjustments[..firstExposureAdjustment].All(
            decision => decision.Reason == CaptureControlDecisionReason.GainAdjusted));
        Assert.IsTrue(adjustments[..firstExposureAdjustment].All(
            decision => decision.Setpoint.Exposure == initial.Exposure));
        Assert.IsTrue(adjustments[firstExposureAdjustment..].All(
            decision => decision.Reason == CaptureControlDecisionReason.ExposureAdjusted));
        Assert.IsTrue(adjustments[firstExposureAdjustment..].All(
            decision => decision.Setpoint.Gain == 40d));
        Assert.IsTrue(decisions.All(decision =>
            decision.Setpoint.Exposure >= TimeSpan.FromSeconds(1) &&
            decision.Setpoint.Exposure <= TimeSpan.FromSeconds(5) &&
            decision.Setpoint.Gain is >= 1 and <= 40));
        AssertSettledWithoutTwoPointOscillation(decisions);
    }

    [TestMethod]
    [DataRow(ExposureGainPreference.ExposureFirst, 4d, 40d, 3.2d, 40d)]
    [DataRow(ExposureGainPreference.ExposureFirst, 1d, 40d, 1d, 30d)]
    [DataRow(ExposureGainPreference.GainFirst, 4d, 40d, 4d, 30d)]
    [DataRow(ExposureGainPreference.GainFirst, 4d, 1d, 3.2d, 1d)]
    public void Next_SaturationReducesPreferredAvailableControl(
        ExposureGainPreference preference,
        double activeExposureSeconds,
        double activeGain,
        double expectedExposureSeconds,
        double expectedGain)
    {
        var active = new CaptureSetpoint(TimeSpan.FromSeconds(activeExposureSeconds), activeGain, null, null);
        var decision = ExposureController.Next(
            CreateProfile(preference),
            active,
            null,
            CaptureSolarRegime.Day,
            true,
            true,
            saturationRejected: true);

        Assert.AreEqual(CaptureControlDecisionReason.SaturationRejected, decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(expectedExposureSeconds), decision.Setpoint.Exposure);
        Assert.AreEqual(expectedGain, decision.Setpoint.Gain);
    }

    [TestMethod]
    public void Next_RegimeTransitionsSelectExplicitDefaults()
    {
        var profile = CreateProfile();
        var active = new CaptureSetpoint(TimeSpan.FromSeconds(3), 40, null, null);
        var transitions = new[]
        {
            (CaptureSolarRegime.Day, TimeSpan.FromSeconds(1), 1d),
            (CaptureSolarRegime.Twilight, TimeSpan.FromSeconds(2), 20d),
            (CaptureSolarRegime.Night, TimeSpan.FromSeconds(4), 100d)
        };

        foreach (var (regime, expectedExposure, expectedGain) in transitions)
        {
            var decision = ExposureController.Next(
                profile, active, 0.65, regime, true, true, regimeChanged: true);

            Assert.AreEqual(CaptureControlDecisionReason.SolarRegimeChanged, decision.Reason);
            Assert.AreEqual(expectedExposure, decision.Setpoint.Exposure);
            Assert.AreEqual(expectedGain, decision.Setpoint.Gain);
            active = decision.Setpoint;
        }
    }

    [TestMethod]
    [DataRow(true, true, 5d, 200d, CaptureControlDecisionReason.EnvelopeClamped)]
    [DataRow(true, false, 5d, 250d, CaptureControlDecisionReason.EnvelopeClamped)]
    [DataRow(false, true, 10d, 200d, CaptureControlDecisionReason.EnvelopeClamped)]
    [DataRow(false, false, 10d, 250d, CaptureControlDecisionReason.NoSample)]
    public void Next_OutsideEnvelope_ClampsOnlyEnabledFieldsAndReportsTruthfully(
        bool adjustExposure,
        bool adjustGain,
        double expectedExposureSeconds,
        double expectedGain,
        CaptureControlDecisionReason expectedReason)
    {
        var active = new CaptureSetpoint(TimeSpan.FromSeconds(10), 250, null, null);
        var decision = ExposureController.Next(
            CreateProfile(), active, 0.65, CaptureSolarRegime.Night, adjustExposure, adjustGain);

        Assert.AreEqual(expectedReason, decision.Reason);
        Assert.AreEqual(TimeSpan.FromSeconds(expectedExposureSeconds), decision.Setpoint.Exposure);
        Assert.AreEqual(expectedGain, decision.Setpoint.Gain);
    }

    private static List<ExposureDecision> RunClosedLoop(
        PipelineExposureProfile profile,
        CaptureSetpoint initial,
        double targetExposureGainProduct)
    {
        const int maximumSteps = 32;
        const int stableDecisionCount = 3;
        var decisions = new List<ExposureDecision>();
        var active = initial;

        for (var step = 0; step < maximumSteps; step++)
        {
            var brightness = profile.Envelope!.TargetAduLevel *
                active.Exposure.TotalSeconds * active.Gain / targetExposureGainProduct;
            var decision = ExposureController.Next(
                profile, active, brightness, CaptureSolarRegime.Night, true, true);
            decisions.Add(decision);
            active = decision.Setpoint;

            if (decision.Reason == CaptureControlDecisionReason.WithinHysteresis)
            {
                for (var stableStep = 1; stableStep < stableDecisionCount; stableStep++)
                {
                    decisions.Add(ExposureController.Next(
                        profile, active, brightness, CaptureSolarRegime.Night, true, true));
                }

                return decisions;
            }
        }

        Assert.Fail($"Controller did not settle within {maximumSteps} steps.");
        return decisions;
    }

    private static void AssertSettledWithoutTwoPointOscillation(List<ExposureDecision> decisions)
    {
        for (var index = 2; index < decisions.Count; index++)
        {
            Assert.IsFalse(
                decisions[index].Setpoint == decisions[index - 2].Setpoint &&
                decisions[index].Setpoint != decisions[index - 1].Setpoint,
                $"Setpoints formed a two-point oscillation at decision {index}.");
        }

        var settled = decisions.TakeLast(3).ToArray();
        Assert.IsTrue(settled.All(
            decision => decision.Reason == CaptureControlDecisionReason.WithinHysteresis));
        Assert.IsTrue(settled.All(decision => decision.Setpoint == settled[0].Setpoint));
    }

    private static PipelineExposureProfile CreateProfile(
        ExposureGainPreference preference = ExposureGainPreference.ExposureFirst,
        double maxGain = 200)
        => new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), 1, 100,
            new ExposureEnvelope(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                1,
                maxGain,
                new ExposureDefaults(TimeSpan.FromSeconds(1), 1),
                new ExposureDefaults(TimeSpan.FromSeconds(4), 100),
                0.65,
                new ExposureDefaults(TimeSpan.FromSeconds(2), 20),
                preference));
}
