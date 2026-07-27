using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class ImageQualityCaptureProcessingStepTests
{
    [TestMethod]
    public async Task RawInputProducesDeterministicMetadataWithoutFabricatingFrame()
    {
        var context = CreateContext([0, 0, 1, 0, 254, 0, 255, 0]);
        context.BeginNode("quality", []);

        await CreateStep().ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        await CreateStep().ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(2, context.ProcessingOutcomes);
        var outcome = context.ProcessingOutcomes[0];
        var product = outcome.Products.Single();
        var replay = context.ProcessingOutcomes[1].Products.Single();
        using var payload = JsonDocument.Parse(product.Payload);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        Assert.AreEqual(FrameArtifactRole.Metadata, product.Role);
        Assert.AreEqual("image-quality-v1", product.Variant);
        Assert.IsNull(product.Layout);
        Assert.AreEqual(4L, payload.RootElement.GetProperty("sampleCount").GetInt64());
        Assert.AreEqual(510UL, payload.RootElement.GetProperty("sum").GetUInt64());
        Assert.HasCount(1, product.SourceArtifactIds);
        Assert.AreEqual(context.Artifacts!.Raw.ArtifactId, product.SourceArtifactIds[0]);
        Assert.AreEqual(product.OutputIdentitySha256, replay.OutputIdentitySha256);
        Assert.AreEqual(product.ChecksumSha256, replay.ChecksumSha256);
        CollectionAssert.AreEqual(product.Payload.ToArray(), replay.Payload.ToArray());
        Assert.IsFalse(context.AllArtifacts.Any(static artifact => artifact.Role == FrameArtifactRole.Metadata));
    }

    [TestMethod]
    public async Task DeclaredCalibratedDependencyUsesExactProducer()
    {
        var context = CreateContext([0, 0, 0, 0, 0, 0, 0, 0]);
        context.BeginNode("calibration", []);
        var calibrated = context.AddDerivative(
            FrameArtifactRole.Calibrated,
            CreateFrame([1, 0, 2, 0, 3, 0, 4, 0]),
            "calibration-v1");
        context.BeginNode("quality", ["calibration"]);

        await CreateStep().ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        var product = context.ProcessingOutcomes.Single().Products.Single();
        using var payload = JsonDocument.Parse(product.Payload);
        Assert.AreEqual(calibrated.ArtifactId, product.SourceArtifactIds.Single());
        Assert.AreEqual(10UL, payload.RootElement.GetProperty("sum").GetUInt64());
        var evidence = context.GetCurrentInputEvidence();
        Assert.HasCount(1, evidence);
        Assert.AreEqual(calibrated.ArtifactId, evidence[0].ArtifactId);
        Assert.IsTrue(evidence[0].Selected);
    }

    [TestMethod]
    public async Task MissingDeclaredProducerDoesNotFallBackToRaw()
    {
        var context = CreateContext([1, 0, 2, 0, 3, 0, 4, 0]);
        context.BeginNode("quality", ["calibration"]);

        await CreateStep().ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.IsEmpty(context.ProcessingOutcomes);
    }

    [TestMethod]
    public async Task InvalidEvidenceBoundsAreRejectedBeforeRecipeExecution()
    {
        var context = CreateContext([0, 0, 1, 0, 2, 0, 3, 0]);
        context.BeginNode("quality", []);
        var raw = context.Artifacts!.Raw;
        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config, raw, new string('v', 129));
        var executor = new CountingExecutor();
        var adapter = new CameraAgentRecipeExecutionAdapter(executor);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.ImageQuality,
                JsonSerializer.SerializeToElement(new { }),
                ProcessingInputSelector.Raw(input.Variant),
                [input],
                "quality",
                InputArtifactId: input.ArtifactId), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(0, executor.ExecutionCount);
        Assert.IsEmpty(context.GetCurrentInputEvidence());
    }

    private static ImageQualityCaptureProcessingStep CreateStep()
        => new(
            new CaptureProcessingStepMetadata("quality", "ImageQuality", 70),
            new ImageQualityProcessingStepOptions(),
            new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()));

    private static CaptureProcessingContext CreateContext(byte[] pixels)
    {
        var frame = CreateFrame(pixels);
        return new CaptureProcessingContext(
            ProcessingConformanceFixture.CameraConfig,
            new CaptureLoopSubmission(
                new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still),
                new CaptureResult(
                    frame,
                    new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null),
                    TimeSpan.Zero,
                    CaptureMode.Still,
                    false),
                frame.TimestampUtc,
                frame.Metadata.Exposure,
                TimeSpan.Zero));
    }

    private static CameraFrame CreateFrame(byte[] pixels)
        => new(
            new DateTimeOffset(2026, 1, 15, 8, 10, 0, TimeSpan.Zero),
            2,
            2,
            CameraPixelFormat.Mono16,
            pixels,
            new FrameMetadata(TimeSpan.FromSeconds(1), 10, 0, "quality-test"),
            4);

    private sealed class CountingExecutor : IProcessingRecipeExecutor
    {
        public int ExecutionCount { get; private set; }

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ProcessingOutcome.Produced());
        }
    }
}
