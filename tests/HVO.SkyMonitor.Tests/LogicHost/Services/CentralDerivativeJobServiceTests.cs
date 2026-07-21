using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;

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

        recipes.Should().HaveCount(5);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.Preview
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.PreviewRecipeVersion);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.AnnotatedPreview
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.AnnotatedPreviewRecipeVersion);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.Metadata
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.ImageQualityRecipeVersion);
        recipes.Should().Contain(recipe => recipe.TargetRole == FrameArtifactRole.Metadata
            && recipe.RecipeVersion == CentralDerivativeRecipeCatalog.CloudAssessmentRecipeVersion
            && recipe.RecipeName == HVO.SkyMonitor.Processing.BuiltInProcessingRecipes.CloudAssessment);
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
        recipes.Single(recipe => recipe.RecipeName == HVO.SkyMonitor.Processing.BuiltInProcessingRecipes.ImageQuality)
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
    public void CentralTransientOptions_CreatePinnedDurableWindowRecipe()
    {
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central
        };

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Should().BeEmpty();
        var recipe = new CentralDerivativeRecipeCatalog(options).GetRequiredRecipes(FrameArtifactRole.Raw)
            .Single(item => item.RecipeName == CentralTransientRuntime.RecipeName);

        recipe.Window!.Positions.Select(position => position.SequenceOffset).Should().Equal(-2, -1, 0, 1, 2);
        recipe.Window.Positions.Should().OnlyContain(position => position.IsRequired
            && position.Selector.Role == FrameArtifactRole.Raw);
        recipe.Transient!.IdentitySlotCount.Should().Be(32);
        recipe.RequestedRecipeIdentitySha256.Should().Be(
            TransientCandidateExtractionFactory.ComputeRecipeIdentitySha256(options.Extraction.ToContract()));
        CentralTransientExecutionOptionsJson.Deserialize(recipe.Transient.ExecutionOptionsJson)
            .Should().Match<CentralTransientExecutionOptionsV1>(value =>
                value.Assessment == options.Assessment.ToContract() &&
                value.MaskPolicy == CentralTransientMaskPolicyV1.ProfileBoundProjectedStarsV1 &&
                value.StarMaximumMagnitude == options.StarMaximumMagnitude &&
                value.StarMaximumResults == options.StarMaximumResults &&
                value.StarSourceSupportRadiusPixels == options.StarSourceSupportRadiusPixels);

        var calibrated = new CentralDerivativeRecipeCatalog(new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            SourceRole = FrameArtifactRole.Calibrated
        });
        calibrated.GetRequiredRecipes(FrameArtifactRole.Raw).Should().HaveCount(5);
        calibrated.GetRequiredRecipes(FrameArtifactRole.Calibrated).Should().ContainSingle(item =>
            item.RecipeName == CentralTransientRuntime.RecipeName
            && item.InputSelector.Role == FrameArtifactRole.Calibrated);
    }

    [TestMethod]
    public void CentralTransientOptions_HybridExposesEdgeRecipeWithoutAutomaticScheduling()
    {
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Hybrid
        };

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Should().BeEmpty();
        var catalog = new CentralDerivativeRecipeCatalog(options);

        catalog.GetRequiredRecipes(FrameArtifactRole.Raw).Should().HaveCount(5);
        var recipe = catalog.GetTransientRecipe(FrameArtifactRole.Raw);
        recipe.Should().NotBeNull();
        recipe!.RequestedRecipeIdentitySha256.Should().Be(
            TransientCandidateExtractionFactory.ComputeRecipeIdentitySha256(
                TransientCandidateExtractionProfiles.EdgeV1));
        CentralTransientExecutionOptionsJson.Deserialize(recipe.Transient!.ExecutionOptionsJson).Extraction
            .Should().Be(TransientCandidateExtractionProfiles.EdgeV1);
    }

    [TestMethod]
    public void CentralTransientOptions_RejectUnsupportedSourceRole()
    {
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Hybrid,
            SourceRole = FrameArtifactRole.Preview
        };

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Should().ContainSingle();
    }

    [TestMethod]
    [DataRow("candidates")]
    [DataRow("bridge")]
    [DataRow("foreground")]
    [DataRow("gap")]
    [DataRow("negative-zero")]
    [DataRow("step-ratio")]
    public void CentralTransientOptions_RejectValuesOutsideSharedContractBounds(string invalidValue)
    {
        var extraction = invalidValue switch
        {
            "candidates" => new CentralTransientExtractionOptions { MaximumCandidates = 65 },
            "bridge" => new CentralTransientExtractionOptions { MaximumSaturationBridgePixels = 0 },
            "foreground" => new CentralTransientExtractionOptions { MaximumForegroundPixels = 10_000_001 },
            "gap" => new CentralTransientExtractionOptions { MaximumFragmentGapPixels = 1025 },
            "negative-zero" => new CentralTransientExtractionOptions { MaximumFragmentGapPixels = -0d },
            _ => new CentralTransientExtractionOptions()
        };
        var assessment = invalidValue == "step-ratio"
            ? new CentralTransientAssessmentOptions { SmoothMotionMaximumStepRatio = 0.99 }
            : new CentralTransientAssessmentOptions();
        var options = new CentralTransientOptions { Extraction = extraction, Assessment = assessment };

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Should().NotBeEmpty();
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

    [TestMethod]
    public void BoundExpectedIdentity_IsEnforcedOnlyWhenAuxiliariesChangedIt()
    {
        var requested = new string('a', 64);

        CentralDerivativeJobExecutor.RequiresBoundExpectedIdentity(requested, requested.ToUpperInvariant())
            .Should().BeFalse();
        CentralDerivativeJobExecutor.RequiresBoundExpectedIdentity(requested, new string('B', 64))
            .Should().BeTrue();
    }
}
