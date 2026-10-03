using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
#if COMBINED_TESTS
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
#endif
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

// The same contract corpus runs through the reconstruction adapter and, in the
// combined lane, through the edge adapter. Assertions bind bytes and provenance,
// rather than merely checking that the shared executor can be constructed.
[TestClass]
[TestCategory("Unit")]
public sealed partial class HostRecipeContractConformanceTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono8)]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.Rgb24)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task ReconstructedPreviewPipelinePreservesContractsAndRejectsCorruptEncodedResults(CameraPixelFormat format)
    {
        var source = Source(format);
        var original = source.Payload.ToArray();
        var descriptor = ProcessingConformanceFixture.CreateDescriptor() with
        {
            Layout = source.Layout!,
            Artifact = ProcessingConformanceFixture.CreateDescriptor().Artifact with
            { ChecksumSha256 = PayloadChecksum.ComputeSha256(source.Payload.Span) }
        };
        var recording = new RecordingExecutor();
        var reconstructed = await new LogicHostRecipeExecutionAdapter(recording).ExecuteAsync(
            descriptor, source.Payload, BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw("source"), "reconstructed");
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, reconstructed.Status, reconstructed.ReasonCode);
        AssertContract(recording.Request!, reconstructed.Products.Single());
        Assert.AreEqual(descriptor.Timing.ExposureStartedUtc, recording.Request!.Inputs[0].ObservationStartedUtc);
        Assert.AreEqual(descriptor.Timing.ExposureEndedUtc, recording.Request.Inputs[0].ObservationEndedUtc);

        var packed = await Execute(source, BuiltInProcessingRecipes.EncodedPreview,
            new EncodedPreviewOptions(OutputEncoding: "Packed"));
        foreach (var bound in new int?[] { null, 4 })
        {
            var encoded = await Execute(AsArtifact(packed), BuiltInProcessingRecipes.JpegEncoding,
                new JpegEncodingOptions(MaximumDimension: bound));
            var dimensions = JpegImageCodec.InspectJpeg(encoded.Payload);
            Assert.AreEqual(bound ?? 8, dimensions.Width);
            Assert.AreEqual(bound ?? 8, dimensions.Height);
        }
        _ = await Execute(source, BuiltInProcessingRecipes.EncodedPreview, new EncodedPreviewOptions());
        var annotation = new ProcessingAnnotationInput(
            [new("star", "Star", new PixelPoint(4, 4), true, true)],
            [new("line", new PixelPoint(1, 1), new PixelPoint(6, 6))],
            new PreviewTransform(1, 1), null, new string('A', 64));
        _ = await Execute(source, BuiltInProcessingRecipes.Annotation,
            new AnnotationRecipeOptions(OutputEncoding: "Packed", MarkRadius: 1), annotation);
        _ = await Execute(AsArtifact(packed), BuiltInProcessingRecipes.Annotation,
            new AnnotationRecipeOptions(OutputEncoding: "Jpeg", MarkRadius: 1), annotation);
        _ = await Execute(source, BuiltInProcessingRecipes.NoOpAnalyzer, new { });
        if (format is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16)
        {
            var normalized = await Execute(source, BuiltInProcessingRecipes.LinearNormalization, new { });
            CollectionAssert.AreEqual(original, normalized.Payload.ToArray());
            _ = await Execute(source, BuiltInProcessingRecipes.ImageQuality, new { });
        }
        CollectionAssert.AreEqual(original, source.Payload.ToArray(), "Host reconstruction and recipes must not mutate ingress bytes.");
    }

    [TestMethod]
    [DataRow(BuiltInProcessingRecipes.EncodedPreview, "{\"jpegQuality\":0}")]
    [DataRow(BuiltInProcessingRecipes.EncodedPreview, "{\"outputEncoding\":\"unknown\"}")]
    [DataRow(BuiltInProcessingRecipes.JpegEncoding, "{\"maximumDimension\":0}")]
    [DataRow(BuiltInProcessingRecipes.LinearNormalization, "{\"mode\":\"unknown\"}")]
    [DataRow(BuiltInProcessingRecipes.RollingMean, "{\"maximumFrameCount\":0}")]
    [DataRow(BuiltInProcessingRecipes.RollingMean, "{\"maximumAgeMilliseconds\":-1}")]
    [DataRow(BuiltInProcessingRecipes.Annotation, "{\"markRadius\":-1}")]
    public async Task HostsRejectInvalidFrozenRecipeOptions(string recipe, string json)
    {
        var source = Source(CameraPixelFormat.Mono16);
        var executor = new RecordingExecutor();
        var outcome = await new LogicHostRecipeExecutionAdapter(executor).ExecuteAsync(
            [new LogicHostProcessingInput(null, source.Payload, Artifact: source)], recipe,
            JsonSerializer.Deserialize<JsonElement>(json), ProcessingInputSelector.Raw("source"), "rejected");
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidOptions, outcome.ReasonCode);
        Assert.HasCount(0, outcome.Products);
#if COMBINED_TESTS
        var edge = await new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor())
            .ExecuteAsync(executor.Request!, CancellationToken.None);
        Assert.AreEqual(outcome.Status, edge.Status);
        Assert.AreEqual(outcome.ReasonCode, edge.ReasonCode);
#endif
    }

    private static async Task<ProcessingProduct> Execute<T>(ProcessingArtifact source, string recipe, T options,
        ProcessingAnnotationInput? annotation = null)
    {
        var recording = new RecordingExecutor();
        var selector = source.Role == FrameArtifactRole.Raw ? ProcessingInputSelector.Raw(source.Variant)
            : ProcessingInputSelector.RecipeResult(source.Role, source.Variant, source.RecipeIdentitySha256);
        var result = await new LogicHostRecipeExecutionAdapter(recording).ExecuteAsync(
            [new LogicHostProcessingInput(null, source.Payload, Artifact: source)], recipe,
            JsonSerializer.SerializeToElement(options), selector, "contract-output", annotation);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, $"{recipe}: {result.ReasonCode}");
        var product = Assert.ContainsSingle(result.Products);
        AssertContract(recording.Request!, product);
#if COMBINED_TESTS
        var edge = await new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor())
            .ExecuteAsync(recording.Request!, CancellationToken.None);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, edge.Status);
        var edgeProduct = Assert.ContainsSingle(edge.Products);
        CollectionAssert.AreEqual(product.Payload.ToArray(), edgeProduct.Payload.ToArray());
        Assert.AreEqual(product.OutputIdentitySha256, edgeProduct.OutputIdentitySha256);
        CollectionAssert.AreEqual(product.SourceArtifactIds.ToArray(), edgeProduct.SourceArtifactIds.ToArray());
#endif
        return product;
    }

    private static void AssertContract(ProcessingExecutionRequest request, ProcessingProduct product)
    {
        var contract = BuiltInProcessingRecipes.CreateProductContract(request, product.Recipe);
        Assert.AreEqual(product.Role, contract.Role);
        Assert.AreEqual(product.MediaType, contract.MediaType);
        Assert.AreEqual(product.Layout, contract.ExactLayout);
        Assert.AreEqual(product.Kind, contract.Kind);
        Assert.AreEqual(product.SchemaVersion, contract.SchemaVersion);
        Assert.AreEqual(product.TotalIntegration, contract.TotalIntegration);
        Assert.AreEqual(product.Compatibility, contract.Compatibility);
        CollectionAssert.AreEqual(product.SourceArtifactIds.ToArray(), contract.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(product.Algorithms.ToArray(), contract.Algorithms.ToArray());
        Assert.AreEqual(PayloadChecksum.ComputeSha256(product.Payload.Span), product.ChecksumSha256);
        Assert.IsTrue(BuiltInProcessingRecipes.ProductPayloadMatchesContract(request, contract, product.Payload,
            product.ContentIdentitySha256, product.Recipe.IdentitySha256, product.Algorithms));
        if (contract.EncodedLayout is not null || product.Role == FrameArtifactRole.Metadata)
            Assert.IsFalse(BuiltInProcessingRecipes.ProductPayloadMatchesContract(request, contract, "{"u8.ToArray(),
                product.ContentIdentitySha256, product.Recipe.IdentitySha256, product.Algorithms),
                "Malformed encoded/structured results must not satisfy the frozen contract.");
    }

    private static ProcessingArtifact Source(CameraPixelFormat format)
    {
        var bytesPerPixel = ImageLayout.BytesPerPixel(format);
        var wide = format is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16;
        var bytes = Enumerable.Range(0, 64 * bytesPerPixel).Select(i => (byte)(i * 17 % 256)).ToArray();
        return ProcessingConformanceFixture.CreateProcessingArtifact() with
        {
            Layout = new FrameLayoutDescriptor(8, 8, 8 * bytesPerPixel, format,
                wide ? FrameByteOrder.LittleEndian : FrameByteOrder.NotApplicable, wide ? 16 : 8, wide ? 16 : 8,
                FrameSamplePacking.ByteAligned, format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
                0, wide ? ushort.MaxValue : byte.MaxValue, bytes.Length),
            Payload = bytes
        };
    }

    private static ProcessingArtifact AsArtifact(ProcessingProduct product) => new(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role,
        product.Variant, product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload,
        ProcessingConformanceFixture.CapturedUtc, product.TotalIntegration, product.Compatibility);

    private sealed class RecordingExecutor : IProcessingRecipeExecutor
    {
        public ProcessingExecutionRequest? Request { get; private set; }
        public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return new ProcessingRecipeExecutor().ExecuteAsync(request, cancellationToken);
        }
    }
}
