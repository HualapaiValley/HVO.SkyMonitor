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

        recipes.Should().HaveCount(4);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.Preview
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.PreviewRecipeVersion);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.AnnotatedPreview
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.AnnotatedPreviewRecipeVersion);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.Metadata
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.ImageQualityRecipeVersion);
        var rolling = recipes.Single(recipe => recipe.TargetRole == FrameArtifactRole.Combined);
        rolling.RecipeVersion.Should().Be(CentralDerivativeRecipeCatalog.RollingMeanRecipeVersion);
        rolling.Window!.Positions.Select(position => position.SequenceOffset).Should().Equal(-2, -1, 0, 1, 2);
        rolling.Window.Positions.Should().OnlyContain(position => position.IsRequired);
        recipes.Should().OnlyContain(recipe => recipe.RequestedRecipeIdentitySha256.Length == 64);
        recipes.Should().OnlyHaveUniqueItems(recipe => recipe.TargetVariant);
        recipes.Should().OnlyHaveUniqueItems(recipe =>
            CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.Empty, Guid.Empty, recipe));
        recipes.Single(recipe => recipe.TargetRole == FrameArtifactRole.Preview)
            .RequestedRecipeIdentitySha256.Should().Be(CentralDerivativeRecipeCatalog.PreviewRequestedRecipeIdentity);
        recipes.Single(recipe => recipe.TargetRole == FrameArtifactRole.AnnotatedPreview)
            .RequestedRecipeIdentitySha256.Should().Be(CentralDerivativeRecipeCatalog.AnnotatedPreviewRequestedRecipeIdentity);
        recipes.Single(recipe => recipe.TargetRole == FrameArtifactRole.Metadata)
            .RequestedRecipeIdentitySha256.Should().Be(CentralDerivativeRecipeCatalog.ImageQualityRequestedRecipeIdentity);
        catalog.GetRequiredRecipes(FrameArtifactRole.Preview).Should().BeEmpty();
        var preview = recipes.Single(recipe => recipe.TargetRole == FrameArtifactRole.Preview);
        CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.NewGuid(), Guid.Empty, preview)
            .Should().NotBe(CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.NewGuid(), Guid.Empty, preview));

        var changedWindow = rolling with
        {
            Window = rolling.Window with
            {
                Positions = rolling.Window.Positions.Skip(1).ToArray()
            }
        };
        CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.Empty, Guid.Empty, changedWindow)
            .Should().NotBe(CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.Empty, Guid.Empty, rolling));
        CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.Empty, Guid.Empty,
                rolling with { Window = rolling.Window with { Positions = rolling.Window.Positions.Reverse().ToArray() } })
            .Should().Be(CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.Empty, Guid.Empty, rolling));
        CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.Empty, Guid.Empty,
                rolling with { RecipeVersion = "central-rolling-mean-v2" })
            .Should().NotBe(CentralDerivativeJobIdentity.CreateRequestIdentity(Guid.Empty, Guid.Empty, rolling));
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
