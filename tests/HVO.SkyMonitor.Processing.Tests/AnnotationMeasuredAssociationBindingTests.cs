using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class AnnotationMeasuredAssociationBindingTests
{
    [TestMethod]
    [DataRow("malformed")]
    [DataRow("identity")]
    [DataRow("media")]
    [DataRow("schema")]
    [DataRow("capture")]
    [DataRow("scene")]
    [DataRow("neither")]
    [DataRow("capture-only")]
    [DataRow("scene-only")]
    public async Task AnnotationRequiresValidAssociationsBoundToItsCaptureOrScene(string scenario)
    {
        var scene = await MeasuredStellarAssociationFixtures.StarFieldAsync().ConfigureAwait(false);
        var primary = MeasuredStellarAssociationFixtures.RawArtifact(MeasuredStellarAssociationFixtures.Field());
        var product = MeasuredStellarAssociationFixtures.AllEligible(scene);
        var measured = primary with
        {
            ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Metadata, Variant = "measured",
            RecipeIdentitySha256 = new string('B', 64), Layout = null, ProductKind = ProcessingProductKind.Metadata,
            MediaType = StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType,
            SchemaVersion = MeasuredStellarAssociationsV1.CurrentSchemaVersion,
            Payload = MeasuredStellarAssociationJson.Serialize(product),
            ContentIdentitySha256 = MeasuredStellarAssociationJson.ComputeIdentitySha256(product)
        };
        measured = scenario switch
        {
            "malformed" => measured with { Payload = "{"u8.ToArray() },
            "identity" => measured with { ContentIdentitySha256 = new string('0', 64) },
            "media" => measured with { MediaType = "application/octet-stream" },
            "schema" => measured with { SchemaVersion = "measured-stellar-associations-v99" },
            _ => measured
        };
        if (scenario == "capture") primary = primary with { CaptureId = Guid.NewGuid() };
        if (scenario is "neither" or "scene-only") primary = primary with { CaptureId = null };
        var sceneArtifact = measured with
        {
            ArtifactId = Guid.NewGuid(), Variant = "scene", RecipeIdentitySha256 = new string('C', 64),
            MediaType = StructuredProcessingProductContracts.ProjectedSceneMediaType,
            SchemaVersion = scene.SchemaVersion, Payload = ProjectedSceneJson.Serialize(scene),
            ContentIdentitySha256 = scenario == "scene" ? new string('0', 64) : scene.SceneIdentitySha256
        };
        List<ProcessingArtifact> inputs = [primary, measured];
        List<ProcessingAuxiliaryInput> auxiliaries = [Auxiliary("measured-stellar-associations", measured)];
        if (scenario is not ("neither" or "capture-only"))
        {
            inputs.Add(sceneArtifact);
            auxiliaries.Add(Auxiliary("projected-scene", sceneArtifact));
        }
        var request = new ProcessingExecutionRequest(BuiltInProcessingRecipes.Annotation,
            JsonSerializer.SerializeToElement(new AnnotationRecipeOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw("source"), inputs, "annotated",
            new ProcessingAnnotationInput([], [], new PreviewTransform(1, 1), null, scene.SceneIdentitySha256),
            AuxiliaryInputs: auxiliaries);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        if (scenario is "capture-only" or "scene-only")
        {
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
            Assert.HasCount(1, outcome.Products);
        }
        else
        {
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status, outcome.ReasonCode);
            Assert.IsEmpty(outcome.Products, "invalid association metadata must never publish annotated pixels");
        }
    }

    private static ProcessingAuxiliaryInput Auxiliary(string name, ProcessingArtifact artifact) => new(
        name, ProcessingAuxiliaryInputKind.Artifact,
        ProcessingInputSelector.RecipeResult(artifact.Role, artifact.Variant, artifact.RecipeIdentitySha256),
        ArtifactId: artifact.ArtifactId);
}
