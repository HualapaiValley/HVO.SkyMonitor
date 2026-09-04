using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.LogicHost.Tests.Configuration;

[TestClass]
public sealed class CentralProcessingRunnerOptionsTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void DefaultsAreDisabledAndValid()
    {
        var options = new CentralProcessingRunnerOptions();
        Assert.IsTrue(options.Validate(out var error), error);
        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(0, options.ResolveRunnerPlacedRecipes().Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PlacementRequiresEnabledAndRunnerCapableRecipes()
    {
        var disabled = new CentralProcessingRunnerOptions
        {
            Placement = { [BuiltInProcessingRecipes.EncodedPreview] = CentralProcessingRunnerPlacement.Runner }
        };
        Assert.IsFalse(disabled.Validate(out var error));
        StringAssert.Contains(error, "Enabled", StringComparison.Ordinal);

        var transient = new CentralProcessingRunnerOptions
        {
            Enabled = true,
            Placement = { ["central-transient-validation"] = CentralProcessingRunnerPlacement.Runner }
        };
        Assert.IsFalse(transient.Validate(out error));
        StringAssert.Contains(error, "runner-capable", StringComparison.Ordinal);
        Assert.IsFalse(CentralProcessingRunnerOptions.IsRunnerCapableRecipe("central-transient-validation"));
        Assert.IsTrue(CentralProcessingRunnerOptions.IsRunnerCapableRecipe(BuiltInProcessingRecipes.EncodedPreview));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void TimingAndLimitsAreValidated()
    {
        Assert.IsFalse(new CentralProcessingRunnerOptions { StaleAfter = TimeSpan.FromSeconds(10), HeartbeatInterval = TimeSpan.FromSeconds(30) }.Validate(out _));
        Assert.IsFalse(new CentralProcessingRunnerOptions { RenewalInterval = TimeSpan.FromMinutes(3) }.Validate(out _));
        Assert.IsFalse(new CentralProcessingRunnerOptions { MaximumClaimCandidatesPerRequest = 0 }.Validate(out _));
        Assert.IsFalse(new CentralProcessingRunnerOptions { MaximumProductBytes = ProcessingRunnerProtocol.MaximumTransferBytes + 1 }.Validate(out _));
        Assert.IsFalse(new CentralProcessingRunnerOptions
        {
            Enabled = true,
            Requirements = { [BuiltInProcessingRecipes.EncodedPreview] = new CentralProcessingRunnerRequirementOptions { ResourceClass = "Bad Class" } }
        }.Validate(out _));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EligibleRecipesAreRunnerPlacedVersionMatchedAndRequirementSatisfied()
    {
        var options = new CentralProcessingRunnerOptions
        {
            Enabled = true,
            Placement =
            {
                [BuiltInProcessingRecipes.EncodedPreview] = CentralProcessingRunnerPlacement.Runner,
                [BuiltInProcessingRecipes.ImageQuality] = CentralProcessingRunnerPlacement.Runner,
                [BuiltInProcessingRecipes.Annotation] = CentralProcessingRunnerPlacement.InProcess
            },
            Requirements =
            {
                [BuiltInProcessingRecipes.ImageQuality] = new CentralProcessingRunnerRequirementOptions { RequiresGpu = true }
            }
        };
        var capabilities = ProcessingRunnerCapabilities.CreateForCurrentProcess(1, 1024, null, null, null, null);

        var eligible = options.ResolveEligibleRecipes(capabilities);

        CollectionAssert.AreEqual(new[] { BuiltInProcessingRecipes.EncodedPreview }, eligible.ToArray());
        var gpu = options.ResolveEligibleRecipes(
            ProcessingRunnerCapabilities.CreateForCurrentProcess(1, 1024, null, null, null, null, gpuAvailable: true));
        CollectionAssert.AreEqual(
            new[] { BuiltInProcessingRecipes.EncodedPreview, BuiltInProcessingRecipes.ImageQuality }, gpu.ToArray());
        var forked = capabilities with
        {
            BuiltInRecipes = capabilities.BuiltInRecipes.Select(recipe => recipe.Name == BuiltInProcessingRecipes.EncodedPreview
                ? recipe with { SemanticVersion = "999.0.0" }
                : recipe).ToArray()
        };
        Assert.AreEqual(0, options.ResolveEligibleRecipes(forked).Count);
    }
}
