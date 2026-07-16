using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
public sealed class LogicHostProcessingConformanceTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task LogicHostAdapterProducesCanonicalPreviewFixture()
    {
        var descriptor = ProcessingConformanceFixture.CreateDescriptor();
        var adapter = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());

        var outcome = await adapter.ExecuteAsync(
            descriptor,
            ProcessingConformanceFixture.Payload,
            request.RecipeName,
            request.Options,
            request.Input,
            request.OutputVariant).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        ProcessingConformanceFixture.AssertProduct(outcome.Products.Single());

        var jpeg = await adapter.ExecuteAsync(
            descriptor,
            ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.EncodedPreview,
            System.Text.Json.JsonSerializer.SerializeToElement(new EncodedPreviewOptions()),
            ProcessingInputSelector.Raw("source"),
            "jpeg").ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, jpeg.Status);
        Assert.AreEqual(HVO.SkyMonitor.Imaging.JpegImageCodec.MediaType, jpeg.Products.Single().MediaType);

        var invalidChecksum = descriptor with
        {
            Artifact = descriptor.Artifact with { ChecksumSha256 = new string('0', 64) }
        };
        var invalid = await adapter.ExecuteAsync(
            invalidChecksum,
            ProcessingConformanceFixture.Payload,
            request.RecipeName,
            request.Options,
            request.Input,
            request.OutputVariant).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, invalid.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidInput, invalid.ReasonCode);
        Assert.AreEqual("payload", invalid.Field);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LogicHostAdapterExecutesOrderedRollingWindow()
    {
        var firstPayload = new byte[] { 10, 0, 20, 0, 30, 0, 40, 0 };
        var secondPayload = new byte[] { 30, 0, 40, 0, 50, 0, 60, 0 };
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var first = CreateSource(baseline, firstPayload, 1);
        var second = CreateSource(baseline, secondPayload, 2);
        var adapter = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var outcome = await adapter.ExecuteAsync(
            [new LogicHostProcessingInput(first, firstPayload), new LogicHostProcessingInput(second, secondPayload)],
            BuiltInProcessingRecipes.RollingMean,
            System.Text.Json.JsonSerializer.SerializeToElement(new RollingMeanOptions(2)),
            ProcessingInputSelector.Raw("source"),
            "mean-2").ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        CollectionAssert.AreEqual(
            new byte[] { 20, 0, 30, 0, 40, 0, 50, 0 },
            outcome.Products.Single().Payload.ToArray());
        CollectionAssert.AreEqual(
            new[] { first.Artifact.ArtifactId, second.Artifact.ArtifactId },
            outcome.Products.Single().SourceArtifactIds.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EquivalentAnnotationRequestMatchesCameraAgentAdapter()
    {
        var artifact = ProcessingConformanceFixture.CreateProcessingArtifact();
        var options = System.Text.Json.JsonSerializer.SerializeToElement(
            new AnnotationRecipeOptions(OutputEncoding: "Packed"));
        var selector = ProcessingInputSelector.Raw("source");
        var annotation = new ProcessingAnnotationInput(
            [new ProjectedAnnotationObject("fixture", "Fixture", new PixelPoint(1, 1), true, true)],
            [],
            new PreviewTransform(1, 1),
            null,
            new string('A', 64));
        var cameraAgent = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var logicHost = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var cameraOutcome = await cameraAgent.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.Annotation,
            options,
            selector,
            [artifact],
            "annotated-conformance",
            annotation), CancellationToken.None).ConfigureAwait(false);
        var logicOutcome = await logicHost.ExecuteAsync(
            ProcessingConformanceFixture.CreateDescriptor(),
            ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.Annotation,
            options,
            selector,
            "annotated-conformance",
            annotation).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, cameraOutcome.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, logicOutcome.Status);
        var expected = cameraOutcome.Products.Single();
        var actual = logicOutcome.Products.Single();
        CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
        Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
        Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
        Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
        CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
        CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
    }

    private static HVO.SkyMonitor.AgentCore.ReconstructionDescriptor CreateSource(
        HVO.SkyMonitor.AgentCore.ReconstructionDescriptor baseline,
        byte[] payload,
        int sequence)
    {
        var artifactId = new Guid($"93000000-0000-0000-0000-{sequence + 10:D12}");
        return baseline with
        {
            Capture = baseline.Capture with
            {
                CaptureId = new Guid($"93000000-0000-0000-0000-{sequence + 20:D12}"),
                CaptureSequence = sequence
            },
            Artifact = baseline.Artifact with
            {
                ArtifactId = artifactId,
                Variant = "source",
                CreatedUtc = baseline.Artifact.CreatedUtc.AddSeconds(sequence),
                ChecksumSha256 = HVO.SkyMonitor.AgentCore.PayloadChecksum.ComputeSha256(payload)
            }
        };
    }
}
