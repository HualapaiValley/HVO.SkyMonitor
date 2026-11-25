using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Capture.Processing")]
public sealed class CaptureSatelliteBindingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DescriptorExactSourceReplaysFrozenOrbitalPlan(bool retrospective)
    {
        var fixture = new CaptureOrbitalPredictionFixture();
        var descriptor = fixture.Descriptor;
        var raw = AsFrameArtifact(descriptor, fixture.Raw.Payload);
        var availability = retrospective
            ? fixture.Availability with
            {
                Mode = CaptureOrbitalAssessmentMode.RetrospectiveReassessment,
                AssessmentCutoffUtc = fixture.Availability.OriginalCutoffUtc.AddDays(1)
            }
            : fixture.Availability;
        var input = fixture.Input(availability: availability);
        var frozen = CaptureOrbitalPredictionInput.Restore(input.SerializePlan(),
            OrbitalSnapshotReadResult.Available(fixture.Snapshot));
        var source = CaptureSatelliteTracksProcessingStep.CreateSource(
            ProcessingConformanceFixture.CameraConfig, raw, descriptor);
        var request = fixture.Request(source) with { InputArtifactId = source.ArtifactId };

        var adapter = new CameraAgentRecipeExecutionAdapter(
            new ProcessingRecipeExecutor([new CaptureOrbitalPredictionRecipe(frozen, fixture.Time)]));
        var outcome = await adapter.ExecuteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        var (expected, _) = await fixture.Run(input, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(fixture.Raw.Payload, source.Payload, "Binding should keep the input memory.");
        Assert.AreEqual(descriptor.Artifact.CreatedUtc, source.CreatedUtc);
        Assert.AreEqual(descriptor.Artifact.MediaType, source.MediaType);
        Assert.AreEqual(descriptor.Timing.ExposureStartedUtc, source.ObservationStartedUtc);
        Assert.AreEqual(descriptor.Timing.ExposureEndedUtc, source.ObservationEndedUtc);
        Assert.AreEqual(descriptor.Capture.CaptureId, source.CaptureId);
        Assert.AreEqual(descriptor.Capture.CaptureSequence, source.CaptureSequence);
        Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(descriptor), source.DescriptorIdentitySha256);
        Assert.AreEqual(descriptor.Layout, source.Layout);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var result = Assert.ContainsSingle(outcome.Products);
        CollectionAssert.AreEqual(expected.Payload.ToArray(), result.Payload.ToArray());
        CollectionAssert.AreEqual(new[] { descriptor.Artifact.ArtifactId }, result.SourceArtifactIds.ToArray());
        Assert.AreEqual(expected.ChecksumSha256, result.ChecksumSha256);
        Assert.AreEqual(expected.OutputIdentitySha256, result.OutputIdentitySha256);
        Assert.AreEqual(expected.Recipe.IdentitySha256, result.Recipe.IdentitySha256);
    }

    [TestMethod]
    public async Task ShippedPredictionRequestPreservesExactCaptureBinding()
    {
        const string rawVariant = "retained-camera-raw";
        const string outputVariant = "camera-satellite-prediction";
        var fixture = new CaptureSatelliteFixture(rawVariant: rawVariant);
        var descriptor = fixture.Descriptor;
        Assert.AreNotEqual(descriptor.Timing.RequestedStartUtc, descriptor.Timing.ExposureStartedUtc);
        Assert.AreNotEqual(descriptor.Timing.ExposureEndedUtc - descriptor.Timing.ExposureStartedUtc,
            descriptor.Controls.EffectiveExposure);
        var raw = AsFrameArtifact(descriptor, fixture.Raw.Payload);
        var source = CaptureSatelliteTracksProcessingStep.CreateSource(fixture.Configuration, raw, descriptor);
        var expected = new ProcessingExecutionRequest(CaptureOrbitalPredictionRecipe.Name,
            JsonSerializer.SerializeToElement(new { }), new(ProcessingInputKind.Raw, FrameArtifactRole.Raw, rawVariant),
            [source], outputVariant, InputArtifactId: descriptor.Artifact.ArtifactId);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var cancellationToken = cancellation.Token;
        ProcessingExecutionRequest? dispatched = null;

        var product = await CaptureSatelliteProducts.PredictAsync(fixture.Evidence, source, outputVariant,
            (recipe, request, token) =>
            {
                Assert.AreEqual(cancellationToken, token, "Prediction must forward the caller's cancellation token.");
                Assert.AreEqual(expected.InputArtifactId, request.InputArtifactId, "The request must pin the exact raw artifact.");
                var dispatchedSource = Assert.ContainsSingle(request.Inputs);
                Assert.AreEqual(descriptor.Timing.ExposureStartedUtc, dispatchedSource.ObservationStartedUtc,
                    "Prediction must use the observed exposure start.");
                Assert.AreEqual(descriptor.Timing.ExposureEndedUtc, dispatchedSource.ObservationEndedUtc,
                    "Prediction must use the observed exposure end.");
                dispatched = request;
                return new ProcessingRecipeExecutor([recipe]).ExecuteAsync(request, token);
            }, cancellationToken).ConfigureAwait(false);

        Assert.IsNotNull(dispatched);
        Assert.IsTrue(JsonElement.DeepEquals(expected.Options, dispatched.Options));
        var actualSource = Assert.ContainsSingle(dispatched.Inputs);
        Assert.AreSame(source, actualSource);
        // Record equality compares JSON and list backing storage, which the assertions above cover by content.
        Assert.AreEqual(expected, dispatched with { Options = expected.Options, Inputs = expected.Inputs });
        Assert.AreEqual(descriptor.Capture.CaptureId, actualSource.CaptureId);
        Assert.AreEqual(descriptor.Capture.CaptureSequence, actualSource.CaptureSequence);
        Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(descriptor), actualSource.DescriptorIdentitySha256);
        Assert.AreEqual(descriptor.Artifact.CreatedUtc, actualSource.CreatedUtc);
        Assert.IsNotNull(product);
        Assert.AreEqual(descriptor.Artifact.ArtifactId, Assert.ContainsSingle(product.SourceArtifactIds));

        var publicProduct = await CaptureSatelliteProducts.PredictAsync(fixture.Evidence, source, outputVariant,
            cancellationToken).ConfigureAwait(false);
        Assert.IsNotNull(publicProduct);
        Assert.AreEqual(product.OutputIdentitySha256, publicProduct.OutputIdentitySha256);
        Assert.AreEqual(product.ChecksumSha256, publicProduct.ChecksumSha256);
    }

    [TestMethod]
    public async Task GenericRawSourceCannotSubstituteForDescriptorExactSource()
    {
        var fixture = new CaptureOrbitalPredictionFixture();
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig, AsFrameArtifact(fixture.Descriptor, fixture.Raw.Payload),
            fixture.Descriptor.Artifact.Variant, reconstructionDescriptor: fixture.Descriptor);
        var adapter = new CameraAgentRecipeExecutionAdapter(
            new ProcessingRecipeExecutor([new CaptureOrbitalPredictionRecipe(fixture.Input(), fixture.Time)]));

        var outcome = await adapter.ExecuteAsync(fixture.Request(source), TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual("application/x-hvo-frame", source.MediaType);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.AreEqual("capture-orbital-input-mismatch", outcome.ReasonCode);
        Assert.IsEmpty(outcome.Products);
    }

    private static FrameArtifact AsFrameArtifact(ReconstructionDescriptor descriptor, ReadOnlyMemory<byte> payload)
    {
        var layout = descriptor.Layout;
        var frame = new CameraFrame(descriptor.Timing.ExposureStartedUtc, layout.Width, layout.Height,
            layout.PixelFormat, payload,
            new(descriptor.Controls.EffectiveExposure, descriptor.Controls.EffectiveGain, 0), layout.StrideBytes)
        { Layout = layout };
        return new(descriptor.Artifact.ArtifactId, descriptor.Artifact.Role, frame, descriptor.Artifact.SourceArtifactIds);
    }
}
