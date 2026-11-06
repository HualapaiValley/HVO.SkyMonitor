using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class RetainedAstrometryRunnerTests
{
    [TestMethod]
    [DataRow("none")]
    [DataRow("producer-descriptor")]
    [DataRow("context")]
    [DataRow("raw-descriptor")]
    [DataRow("scene")]
    [DataRow("time")]
    public async Task FrozenRunnerRoundTripPreservesSolutionOrRejectsAlteredEvidence(string fault)
    {
        var f = await MeasuredAstrometryFixture.CreateAsync().ConfigureAwait(false);
        var executor = new ProcessingRecipeExecutor();
        var sceneResult = await executor.ExecuteAsync(new(BuiltInProcessingRecipes.ProjectedScene,
            JsonSerializer.SerializeToElement(new { }), ProcessingInputSelector.Raw(f.Raw.Variant), [f.Raw], "scene",
            AuxiliaryInputs: [MeasuredStellarAssociationFixtures.SceneAuxiliary(f.Scene)])).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, sceneResult.Status, sceneResult.ReasonCode);
        var scene = Artifact(sceneResult.Products.Single());
        var context = Artifact(RetainedAstrometryProducts.CreateContextProduct(f.Context, f.Raw, f.Measured, scene, "context"));
        var request = RetainedAstrometryProducts.Request(f.Raw, f.Measured, scene, context, MeasuredAstrometryFixture.Options, "astrometry");
        var first = await executor.ExecuteAsync(request).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, first.Status, first.ReasonCode);
        var expected = first.Products.Single();
        var claim = new ProcessingRunnerClaim(ProcessingRunnerProtocol.Version, Guid.NewGuid(), ProcessingRunnerJobClass.CentralRecipe,
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30), 1, 3, request.RecipeName, request.Options,
            request.Input, request.Inputs.Select(a => ProcessingRunnerProjection.ProjectArtifact(a, Guid.NewGuid(), "job-input",
                a.Payload.Length, ProcessingIdentity.ComputePayloadSha256(a.Payload))).ToArray(), request.OutputVariant, null,
            request.AuxiliaryInputs!.Select(ProcessingRunnerProjection.ProjectAuxiliaryInput).ToArray(), request.InputArtifactId,
            expected.Recipe.IdentitySha256, expected.Recipe.IdentitySha256, new(null, null, null, null));
        claim = JsonSerializer.Deserialize<ProcessingRunnerClaim>(JsonSerializer.SerializeToUtf8Bytes(claim,
            ProcessingRunnerProtocol.SerializerOptions), ProcessingRunnerProtocol.SerializerOptions)!;
        if (fault == "producer-descriptor") claim = claim with
        {
            Inputs = claim.Inputs.Select(a => a.ArtifactId == f.Measured.ArtifactId
            ? a with { RecipeDescriptor = a.RecipeDescriptor! with { ImplementationVersion = "tampered" } } : a).ToArray()
        };
        if (fault == "context" || fault == "scene") claim = claim with
        {
            AuxiliaryInputs = claim.AuxiliaryInputs!.Select(a =>
            a.Name == (fault == "scene" ? "scene" : MeasuredAstrometryJson.ContextInputName) ? a with { CanonicalJson = a.CanonicalJson + " " } : a).ToArray()
        };
        if (fault == "raw-descriptor" || fault == "time") claim = claim with
        {
            Inputs = claim.Inputs.Select(a => a.ArtifactId == f.Raw.ArtifactId
            ? fault == "time" ? a with { ObservationStartedUtc = a.ObservationStartedUtc!.Value.AddSeconds(2) }
                : a with { DescriptorIdentitySha256 = new string('A', 64) } : a).ToArray()
        };
        ProcessingExecutionRequest restored;
        try { restored = ProcessingRunnerProjection.ReconstructRequest(claim, request.Inputs.Select(a => a.Payload).ToArray()); }
        catch (ProcessingRunnerProtocolException) when (fault != "none") { return; }
        var actual = await executor.ExecuteAsync(restored).ConfigureAwait(false);
        if (fault != "none")
        {
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, actual.Status);
            Assert.IsEmpty(actual.Products);
            return;
        }
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, actual.Status, actual.ReasonCode);
        var product = actual.Products.Single();
        CollectionAssert.AreEqual(expected.Payload.ToArray(), product.Payload.ToArray());
        Assert.AreEqual(expected.OutputIdentitySha256, product.OutputIdentitySha256);
        Assert.AreEqual(expected.ContentIdentitySha256, RetainedAstrometryProducts.Verify(restored, product).SolutionIdentitySha256);
        Console.WriteLine($"runner retained solution={product.ContentIdentitySha256} output={product.OutputIdentitySha256}");
    }

    private static ProcessingArtifact Artifact(ProcessingProduct product) => new(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
        product.Role, product.Variant, product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload,
        DateTimeOffset.UnixEpoch, product.TotalIntegration, product.Compatibility, SourceArtifactIds: product.SourceArtifactIds)
    {
        ProductKind = product.Kind,
        SchemaVersion = product.SchemaVersion,
        ContentIdentitySha256 = product.ContentIdentitySha256,
        RecipeDescriptor = product.Recipe.Descriptor
    };
}
