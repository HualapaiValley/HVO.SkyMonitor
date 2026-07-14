using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralDerivativeJobServiceTests
{
    [TestMethod]
    public void RequiredRecipes_ForRaw_ReturnsStableCentralRecipes()
    {
        var catalog = new CentralDerivativeRecipeCatalog();

        var recipes = catalog.GetRequiredRecipes(FrameArtifactRole.Raw);

        recipes.Should().HaveCount(2);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.Preview
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.PreviewRecipeVersion);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.AnnotatedPreview
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.AnnotatedPreviewRecipeVersion);
        catalog.GetRequiredRecipes(FrameArtifactRole.Preview).Should().BeEmpty();
    }

    [TestMethod]
    public void CalculateRetryDelay_UsesBoundedExponentialBackoff()
    {
        CentralDerivativeJobService.CalculateRetryDelay(1, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5))
            .Should().Be(TimeSpan.FromSeconds(10));
        CentralDerivativeJobService.CalculateRetryDelay(4, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5))
            .Should().Be(TimeSpan.FromSeconds(80));
        CentralDerivativeJobService.CalculateRetryDelay(20, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5))
            .Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public void CalculateRetryDelay_WithInvalidAttempt_Throws()
    {
        var action = () => CentralDerivativeJobService.CalculateRetryDelay(
            0, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void CalculateRetryDelay_WithInvalidMaximum_ReportsMaximumParameter()
    {
        var action = () => CentralDerivativeJobService.CalculateRetryDelay(
            1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

        action.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("maximumDelay");
    }

    [TestMethod]
    public void CalculateRetryDelay_WithLargeTicks_DoesNotOverflow()
    {
        var initial = TimeSpan.FromTicks(long.MaxValue / 2);

        CentralDerivativeJobService.CalculateRetryDelay(3, initial, TimeSpan.MaxValue)
            .Should().Be(TimeSpan.MaxValue);
    }
}
