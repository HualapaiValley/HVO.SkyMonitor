using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// The CameraAgent window gates of the mixed-gain recipe (#1475 slice F2): the raw selector, the adapter's setpoint
/// compatibility, the rolling window boundary and the output selector admit a gain difference only for the mixed-gain
/// recipe at the version that defines the normalization, and every other window keeps gain as a boundary.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class MixedGainWindowGateTests
{
    private static readonly string FamilySha256 = new('A', 64);
    private static readonly GainResponse Response = GainResponseResolver.Resolve(
        GainResponseSourceKind.VirtualAsi174MmModel,
        ProcessingConformanceFixture.CameraConfig.Rig.Sensor,
        ProcessingConformanceFixture.CameraConfig.Rig.Readout).Response!;

    [TestMethod]
    public void RawSelector_CrossesAGainChangeOnlyUnderTheGainNormalizationLabel()
    {
        var low = Light(10);
        var high = Light(20);

        Assert.IsFalse(ProcessingRawWindowSelector.IsCompatible(low, high, admitGain: false), "a gain change breaks an ordinary window");
        Assert.IsTrue(ProcessingRawWindowSelector.IsCompatible(low, high, admitGain: true));
        Assert.IsTrue(ProcessingRawWindowSelector.IsCompatible(low, Light(10), admitGain: false));
        foreach (var other in new[]
                 {
                     high with { Controls = high.Controls with { EffectiveExposure = TimeSpan.FromSeconds(7) } },
                     high with { Controls = high.Controls with { EffectiveOffset = 99 } },
                     high with { Controls = high.Controls with { TemperatureSetpointC = -25 } },
                     high with { Profiles = high.Profiles with { Calibration = high.Profiles.Calibration with { Sha256 = new string('B', 64) } } },
                     high with { Layout = high.Layout with { WhiteLevel = 1234 } }
                 })
        {
            Assert.IsFalse(ProcessingRawWindowSelector.IsCompatible(low, other, admitGain: true), "only gain is admitted");
            Assert.IsFalse(ProcessingRawWindowSelector.IsCompatible(low, other, admitGain: false));
        }
    }

    [TestMethod]
    public void Adapter_GivesCapturesThatDifferOnlyInGainOneCompatibilityUnderAPlan()
    {
        var plan = Plan(FamilySha256);
        var low = Light(10);
        var high = Light(20);
        var normalizedLow = CameraAgentRecipeExecutionAdapter.CreateCompatibility(low, plan);

        Assert.AreNotEqual(CameraAgentRecipeExecutionAdapter.CreateCompatibility(low), CameraAgentRecipeExecutionAdapter.CreateCompatibility(high),
            "without a plan gain stays part of the setpoint regime");
        Assert.AreEqual(normalizedLow, CameraAgentRecipeExecutionAdapter.CreateCompatibility(high, plan));
        Assert.AreEqual(GainNormalizationCompatibility.Normalize(CameraAgentRecipeExecutionAdapter.CreateCompatibility(low), plan), normalizedLow);
        Assert.AreEqual(FamilySha256, normalizedLow.Calibration);
        Assert.IsTrue(GainNormalizationCompatibility.IsNormalized(normalizedLow));
        Assert.IsFalse(GainNormalizationCompatibility.IsNormalized(CameraAgentRecipeExecutionAdapter.CreateCompatibility(low)));
        Assert.Contains("exposure=", normalizedLow.SetpointRegime, StringComparison.Ordinal);
        Assert.AreNotEqual(normalizedLow, CameraAgentRecipeExecutionAdapter.CreateCompatibility(
            high with { Controls = high.Controls with { EffectiveExposure = TimeSpan.FromSeconds(7) } }, plan));
        Assert.AreNotEqual(normalizedLow, CameraAgentRecipeExecutionAdapter.CreateCompatibility(low, Plan(new string('C', 64))),
            "another family's plan is another window");
    }

    [TestMethod]
    public void RollingWindow_KeepsGainAsABoundaryUnlessAFrozenPlanNormalizesIt()
    {
        var plan = Plan(FamilySha256);
        var low = Artifact(Light(10));
        var high = Artifact(Light(20));

        Assert.IsFalse(RollingCombinationCaptureProcessingStep.IsCompatible(high, low, plan: null), "a gain change breaks the rolling mean");
        Assert.IsTrue(RollingCombinationCaptureProcessingStep.IsCompatible(Artifact(Light(10)), low, plan: null));
        Assert.IsTrue(RollingCombinationCaptureProcessingStep.IsCompatible(high, low, plan));
        Assert.IsFalse(RollingCombinationCaptureProcessingStep.IsCompatible(
            Artifact(Light(20) with { Controls = Light(20).Controls with { EffectiveExposure = TimeSpan.FromSeconds(7) } }), low, plan),
            "an exposure change still breaks a mixed-gain window");
        Assert.IsFalse(RollingCombinationCaptureProcessingStep.IsCompatible(high with { Variant = "other" }, low, plan));
        Assert.IsFalse(RollingCombinationCaptureProcessingStep.IsCompatible(
            high with { Role = FrameArtifactRole.Calibrated }, low with { Role = FrameArtifactRole.Calibrated }, plan),
            "the mixed-gain recipe stacks raw frames only");
    }

    [TestMethod]
    public void OutputSelector_AdmitsANormalizedCompatibilityOnlyFromTheMixedGainRecipeOrItsRollingMean()
    {
        var ordinary = CameraAgentRecipeExecutionAdapter.CreateCompatibility(Light(10));
        var normalized = CameraAgentRecipeExecutionAdapter.CreateCompatibility(Light(10), Plan(FamilySha256));
        var mixedGain = Recipe(BuiltInProcessingRecipes.MixedGainNormalizedMean, GainNormalizationCompatibility.RecipeSemanticVersion);
        var rollingMean = Recipe(BuiltInProcessingRecipes.RollingMean, "1.0.0");

        Assert.IsTrue(ProcessingOutputWindowSelector.IsCompatible(normalized, normalized, mixedGain));
        Assert.IsTrue(ProcessingOutputWindowSelector.IsCompatible(normalized, normalized, rollingMean),
            "the rolling mean averages normalized products only when every one shares the identity, so it propagates it");
        Assert.IsFalse(ProcessingOutputWindowSelector.IsCompatible(
            normalized, normalized, Recipe(BuiltInProcessingRecipes.RollingMean, "1.0.1")));
        Assert.IsFalse(ProcessingOutputWindowSelector.IsCompatible(
            normalized, normalized, Recipe(BuiltInProcessingRecipes.ReferenceCalibration, "1.0.0")),
            "another recipe cannot claim a normalized gain");
        Assert.IsFalse(ProcessingOutputWindowSelector.IsCompatible(
            normalized, normalized, Recipe(BuiltInProcessingRecipes.MixedGainNormalizedMean, "1.0.1")));
        Assert.IsTrue(ProcessingOutputWindowSelector.IsCompatible(ordinary, ordinary, rollingMean));
        Assert.IsFalse(ProcessingOutputWindowSelector.IsCompatible(
            ordinary, CameraAgentRecipeExecutionAdapter.CreateCompatibility(Light(20)), rollingMean), "a gain change still breaks the window");
        Assert.IsFalse(ProcessingOutputWindowSelector.IsCompatible(ordinary, normalized, mixedGain));
    }

    [TestMethod]
    public void PipelineFactory_DeclaresTheGainNormalizationLabelOnlyForTheMixedGainRecipe()
    {
        var mixedGain = CaptureProcessingPipelineFactory.CreateWindowCompatibilityLabels(BuiltInProcessingRecipes.MixedGainNormalizedMean);

        Assert.Contains(GainNormalizationCompatibility.WindowLabel, mixedGain);
        foreach (var other in new[] { BuiltInProcessingRecipes.RollingMean, BuiltInProcessingRecipes.ReferenceCalibration, null })
        {
            var labels = CaptureProcessingPipelineFactory.CreateWindowCompatibilityLabels(other);
            Assert.DoesNotContain(GainNormalizationCompatibility.WindowLabel, labels);
            CollectionAssert.AreEqual(labels.ToArray(), mixedGain.Remove(GainNormalizationCompatibility.WindowLabel).ToArray());
        }
        Assert.IsTrue(CaptureProcessingPipelineFactory.RequiresLinear16(BuiltInProcessingRecipes.MixedGainNormalizedMean));
    }

    [TestMethod]
    public void RollingCombinationOptions_AcceptOnlyTheMixedGainRecipeAndKeepTheirSerializedIdentity()
    {
        Assert.IsTrue(IsValid(new RollingCombinationProcessingStepOptions()));
        Assert.IsTrue(IsValid(new RollingCombinationProcessingStepOptions { Recipe = BuiltInProcessingRecipes.MixedGainNormalizedMean }));
        Assert.IsFalse(IsValid(new RollingCombinationProcessingStepOptions { Recipe = BuiltInProcessingRecipes.RollingMean }));
        Assert.IsFalse(IsValid(new RollingCombinationProcessingStepOptions { Recipe = "mixed-gain-normalized-mean-v2" }));
        // An unset recipe is omitted, so every existing rolling-combination configuration keeps its options hash.
        Assert.DoesNotContain(
            nameof(RollingCombinationProcessingStepOptions.Recipe),
            JsonSerializer.Serialize(new RollingCombinationProcessingStepOptions()),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValid(RollingCombinationProcessingStepOptions options)
        => Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);

    private static ReconstructionDescriptor Light(double gain)
    {
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        return descriptor with { Controls = descriptor.Controls with { RequestedGain = gain, EffectiveGain = gain } };
    }

    private static ProcessingArtifact Artifact(ReconstructionDescriptor descriptor)
        => new(
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            "primary",
            new string('D', 64),
            "application/x-hvo-linear-frame",
            descriptor.Layout,
            new byte[8],
            DateTimeOffset.UnixEpoch,
            descriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor));

    private static RecipeIdentityDescriptor Recipe(string name, string semanticVersion)
        => RecipeIdentityDescriptor.Create(name, semanticVersion, "test-v1", JsonSerializer.SerializeToElement(new { }));

    private static NormalizationPlanV1 Plan(string familySha256)
        => new(
            NormalizationPlanV1.CurrentSchemaVersion,
            familySha256,
            GainNormalizationPolicyV1.Approved,
            Response.Descriptor,
            Response.IdentitySha256,
            Response.ResolveCoefficient(GainNormalizationPolicyV1.Approved.ReferenceGainControl).Coefficient!,
            Light(10).Layout,
            NormalizationKernelV1.Linear16(Response.Descriptor),
            []);
}
