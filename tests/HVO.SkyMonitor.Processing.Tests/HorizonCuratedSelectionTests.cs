using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProjectedSceneAnnotationTests
{
    [TestMethod]
    public async Task CuratedHorizonSelectionFreezesIdsSurvivesTransportAndBindsHorizonIdentity()
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var ids = scene.DeepSky!.Objects.Select(static item => item.Id).Order(StringComparer.Ordinal).ToArray();
        var firstId = ids[0];
        var selection = new HorizonObstructionSceneSelection(HorizonObstructionSceneSelection.CurrentSchemaVersion,
            scene.SceneIdentitySha256, new string('E', 64), ids);
        ids[0] = "mutated";
        Assert.IsTrue(selection.Contains(firstId));
        Assert.IsFalse(selection.Contains("mutated"));
        var auxiliary = selection.ToAuxiliaryInput();
        var transported = JsonSerializer.Deserialize<ProcessingAuxiliaryInput>(JsonSerializer.Serialize(auxiliary))!;
        var restored = HorizonObstructionSceneSelection.Parse(transported);
        restored.ValidateScene(scene);
        CollectionAssert.AreEqual(selection.EligibleObjectIds.ToArray(), restored.EligibleObjectIds.ToArray());
        Assert.AreEqual(auxiliary.IdentitySha256, restored.ToAuxiliaryInput().IdentitySha256);
        var changed = new HorizonObstructionSceneSelection(selection.SchemaVersion, scene.SceneIdentitySha256,
            new string('F', 64), selection.EligibleObjectIds);
        Assert.AreNotEqual(auxiliary.IdentitySha256, changed.ToAuxiliaryInput().IdentitySha256);
        var (primary, sceneArtifact) = Artifacts(scene);
        var request = Request(primary, sceneArtifact);
        var firstRequest = request with { AuxiliaryInputs = request.AuxiliaryInputs!.Append(auxiliary).ToArray() };
        var secondRequest = request with { AuxiliaryInputs = request.AuxiliaryInputs!.Append(changed.ToAuxiliaryInput()).ToArray() };
        var first = await new ProcessingRecipeExecutor().ExecuteAsync(firstRequest).ConfigureAwait(false);
        var second = await new ProcessingRecipeExecutor().ExecuteAsync(secondRequest).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, first.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, second.Status);
        ProcessingRecipeTests.AssertProductMatchesContract(firstRequest, first.Products.Single());
        ProcessingRecipeTests.AssertProductMatchesContract(secondRequest, second.Products.Single());
        Assert.AreEqual(first.Products.Single().ChecksumSha256, second.Products.Single().ChecksumSha256);
        Assert.AreNotEqual(first.Products.Single().OutputIdentitySha256, second.Products.Single().OutputIdentitySha256);
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("object")]
    [DataRow("hash")]
    [DataRow("schema")]
    [DataRow("missing-scene")]
    public async Task ActualCuratedExecutorRejectsMismatchedOrMalformedHorizonSelection(string mismatch)
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var ids = mismatch == "object" ? ["unknown-object"] : scene.DeepSky!.Objects
            .Select(static item => item.Id).Order(StringComparer.Ordinal).ToArray();
        var selection = new HorizonObstructionSceneSelection(HorizonObstructionSceneSelection.CurrentSchemaVersion,
            mismatch == "source" ? new string('F', 64) : scene.SceneIdentitySha256, new string('E', 64), ids);
        var auxiliary = selection.ToAuxiliaryInput();
        if (mismatch == "hash") auxiliary = auxiliary with { IdentitySha256 = new string('F', 64) };
        if (mismatch == "schema") auxiliary = auxiliary with { SchemaVersion = "other-schema" };
        var (primary, sceneArtifact) = Artifacts(scene);
        var request = Request(primary, sceneArtifact);
        request = mismatch == "missing-scene" ? request with { Inputs = [primary], AuxiliaryInputs = [auxiliary] }
            : request with { AuxiliaryInputs = request.AuxiliaryInputs!.Append(auxiliary).ToArray() };
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.IsEmpty(outcome.Products);
    }

    [TestMethod]
    public void CuratedHorizonSelectionRejectsUnboundedUnorderedDuplicateOrInvalidIdentity()
    {
        HorizonObstructionSceneSelection Select(string[] ids, string identity = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
            => new(HorizonObstructionSceneSelection.CurrentSchemaVersion, identity, new string('E', 64), ids);
        Assert.ThrowsExactly<ArgumentException>(() => Select(Enumerable.Range(0, 129).Select(i => $"id-{i:D3}").ToArray()));
        Assert.ThrowsExactly<ArgumentException>(() => Select(["a", "a"]));
        Assert.ThrowsExactly<ArgumentException>(() => Select(["b", "a"]));
        Assert.ThrowsExactly<ArgumentException>(() => Select([new string('x', 129)]));
        Assert.ThrowsExactly<ArgumentException>(() => Select(["control\n"]));
        Assert.ThrowsExactly<ArgumentException>(() => Select([], "invalid"));
    }
}
