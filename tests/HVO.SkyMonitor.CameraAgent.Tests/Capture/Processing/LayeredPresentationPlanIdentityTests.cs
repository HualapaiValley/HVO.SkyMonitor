using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class LayeredPresentationPlanIdentityTests
{
    private static readonly string[] ExistingSceneOutputs =
    [
        "scene-annotation-layer-v1", "scene-cardinal-layer-v1", "scene-image-circle-layer-v1",
        "scene-constellation-layer-v1"
    ];

    [TestMethod]
    public void PlanIdentitySceneSchemaVersions_StayTheResolvedFootprintSetAndAdmitEveryLaterSceneSchema()
    {
        var pinned = LayeredPresentationCaptureProcessing.PlanIdentitySceneSchemaVersions;

        CollectionAssert.AreEqual(
            new[] { ProjectedSceneV1.CurrentSchemaVersion, ProjectedSceneV1.ResolvedFootprintSchemaVersion },
            pinned.ToArray());
        CollectionAssert.IsSubsetOf(pinned.ToArray(), ProjectedSceneV1.SupportedSchemaVersions.ToArray());
        foreach (var schema in ProjectedSceneV1.SupportedSchemaVersions.Except(pinned, StringComparer.Ordinal))
        {
            Assert.IsTrue(
                StructuredProcessingProductContracts.SatisfiesDeclaredSchema(ProjectedSceneV1.CurrentSchemaVersion, schema),
                $"Scene schema '{schema}' is outside the plan-identity set and must satisfy the declared family root.");
        }
    }

    [TestMethod]
    public void SceneAndEnvironmentLayerSteps_DeclareThePlanIdentitySceneSchemas()
    {
        var scene = CreateSceneStep(new ScenePresentationLayerProcessingStepOptions());
        var environment = new EnvironmentPresentationLayerCaptureProcessingStep(
            new CaptureProcessingStepMetadata("environment", "EnvironmentPresentationLayer", 72),
            new EnvironmentPresentationLayerProcessingStepOptions { WidthPixels = 64, HeightPixels = 64 }, null!);

        foreach (var requirements in new[] { scene.DependencyRequirements, environment.DependencyRequirements })
        {
            var sceneRequirement = requirements.Single(requirement =>
                requirement.RecipeNames?.Contains(BuiltInProcessingRecipes.ProjectedScene) == true);
            Assert.IsNotNull(sceneRequirement.SchemaVersions);
            Assert.IsTrue(sceneRequirement.SchemaVersions.SetEquals(LayeredPresentationCaptureProcessing.PlanIdentitySceneSchemaVersions));
            Assert.IsFalse(sceneRequirement.SchemaVersions.Contains(ProjectedSceneV1.DeepSkySchemaVersion));
        }
    }

    [TestMethod]
    public void ScenePresentationLayer_DeepSkyOutputIsAddedOnlyWhenConfigured()
    {
        var unset = CreateSceneStep(new ScenePresentationLayerProcessingStepOptions());
        var set = CreateSceneStep(new ScenePresentationLayerProcessingStepOptions { DeepSkyOutputVariant = "deep-sky-layer" });

        CollectionAssert.AreEqual(
            ExistingSceneOutputs,
            unset.Outputs.Select(output => output.Variant).ToArray());
        Assert.IsFalse(unset.Outputs.Any(output =>
            output.SharedRecipe?.ImplementationVersion == PresentationDeepSkyLayerProducer.ProducerVersion));
        Assert.HasCount(5, set.Outputs);
        CollectionAssert.AreEqual(unset.Outputs.ToArray(), set.Outputs.Take(4).ToArray());
        var deepSky = set.Outputs[4];
        Assert.AreEqual("deep-sky-layer", deepSky.Variant);
        Assert.AreEqual(FrameArtifactRole.Metadata, deepSky.Role);
        Assert.AreEqual(PresentationLayerPayloadV1.CurrentSchemaVersion, deepSky.SchemaVersion);
        Assert.AreEqual(PresentationDeepSkyLayerProducer.ProducerVersion, deepSky.SharedRecipe?.ImplementationVersion);
        Assert.AreEqual(PresentationProcessingProducts.LayerRecipeName, deepSky.SharedRecipe?.Name);
        foreach (var requirements in new[] { unset.DependencyRequirements, set.DependencyRequirements })
        {
            var requirement = requirements.Single();
            Assert.IsTrue(requirement.RecipeNames!.SetEquals([BuiltInProcessingRecipes.ProjectedScene]));
            Assert.IsTrue(requirement.SchemaVersions!.SetEquals(LayeredPresentationCaptureProcessing.PlanIdentitySceneSchemaVersions));
            Assert.IsNull(requirement.Variant);
        }
        Assert.AreEqual(unset.SharedStepVersion, set.SharedStepVersion);
        Assert.AreEqual(unset.OutputVariant, set.OutputVariant);
    }

    [TestMethod]
    [DataRow("scene-annotation-layer-v1")]
    [DataRow("scene-cardinal-layer-v1")]
    [DataRow("scene-image-circle-layer-v1")]
    [DataRow("scene-constellation-layer-v1")]
    [DataRow(" ")]
    public void ScenePresentationLayerOptions_RejectADeepSkyVariantThatIsNotDistinct(string variant)
    {
        var options = new ScenePresentationLayerProcessingStepOptions { DeepSkyOutputVariant = variant };

        var results = options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).ToArray();

        Assert.HasCount(1, results);
        CollectionAssert.AreEqual(
            new[] { nameof(ScenePresentationLayerProcessingStepOptions.DeepSkyOutputVariant) },
            results[0].MemberNames.ToArray());
    }

    private static ScenePresentationLayerCaptureProcessingStep CreateSceneStep(ScenePresentationLayerProcessingStepOptions options) =>
        new(new CaptureProcessingStepMetadata("scene-layers", "ScenePresentationLayer", 70), options);
}
