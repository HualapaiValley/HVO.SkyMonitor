using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
#if COMBINED_TESTS
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
#endif
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
#if COMBINED_TESTS
using HVO.SkyMonitor.TestSupport;
#endif

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class HostRecipeContractConformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("iss")]
    [DataRow("starlink-56838")]
    [DataRow("starlink-65283")]
    public async Task FrozenOrbitalControlReproducesCommittedGolden(string name)
    {
        var fixture = new CaptureOrbitalPredictionFixture(name);
        var executor = OrbitalExecutor(fixture);
        var result = await executor.ExecuteAsync(fixture.Request(), TestContext.CancellationToken);
        await AssertOrbitalGolden(name, fixture, executor, result);
    }

    [TestMethod]
    [DataRow("iss")]
    [DataRow("starlink-56838")]
    [DataRow("starlink-65283")]
    public async Task FrozenOrbitalReconstructionReproducesCommittedGolden(string name)
    {
        var fixture = new CaptureOrbitalPredictionFixture(name);
        var executor = OrbitalExecutor(fixture);
        var request = fixture.Request();
        // Supply the descriptor and bytes, never an already-normalized ProcessingArtifact.
        var result = await new LogicHostRecipeExecutionAdapter(executor).ExecuteAsync(
            fixture.Descriptor, fixture.Raw.Payload, request.RecipeName, request.Options,
            request.Input, request.OutputVariant, cancellationToken: TestContext.CancellationToken);
        await AssertOrbitalGolden(name, fixture, executor, result);
    }

#if COMBINED_TESTS
    [TestMethod]
    [DataRow("iss")]
    [DataRow("starlink-56838")]
    [DataRow("starlink-65283")]
    public async Task FrozenOrbitalEdgeBindingReproducesCommittedGolden(string name)
    {
        var fixture = new CaptureOrbitalPredictionFixture(name);
        var executor = OrbitalExecutor(fixture);
        var descriptor = fixture.Descriptor;
        var frame = new CameraFrame(descriptor.Artifact.CreatedUtc, descriptor.Layout.Width,
            descriptor.Layout.Height, descriptor.Layout.PixelFormat, fixture.Raw.Payload,
            new(descriptor.Controls.EffectiveExposure, descriptor.Controls.EffectiveGain, 0),
            descriptor.Layout.StrideBytes)
        { Layout = descriptor.Layout };
        var artifact = new FrameArtifact(descriptor.Artifact.ArtifactId, descriptor.Artifact.Role,
            frame, descriptor.Artifact.SourceArtifactIds);
        var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(ProcessingConformanceFixture.CameraConfig,
            artifact, descriptor.Artifact.Variant, reconstructionDescriptor: descriptor);
        // Exercise the edge binding factory; do not replace its fields with fixture.Raw afterwards.
        var result = await new CameraAgentRecipeExecutionAdapter(executor).ExecuteAsync(
            fixture.Request(raw), TestContext.CancellationToken);
        await AssertOrbitalGolden(name, fixture, executor, result);
    }
#endif

    private static OrbitalBindingExecutor OrbitalExecutor(CaptureOrbitalPredictionFixture fixture)
    {
        var original = fixture.Input();
        var input = CaptureOrbitalPredictionInput.Restore(original.SerializePlan(),
            OrbitalSnapshotReadResult.Available(fixture.Snapshot));
        Assert.AreNotSame(original, input);
        CollectionAssert.AreEqual(original.SerializePlan(), input.SerializePlan());
        return new(input, fixture.Time);
    }

    private async Task AssertOrbitalGolden(string name, CaptureOrbitalPredictionFixture fixture,
        OrbitalBindingExecutor executor, ProcessingOutcome outcome)
    {
        var source = Assert.ContainsSingle(executor.Request!.Inputs);
        var descriptor = fixture.Descriptor;
        var binding = $"CreatedUtc expected={descriptor.Artifact.CreatedUtc:O}, actual={source.CreatedUtc:O}; " +
            $"MediaType expected={descriptor.Artifact.MediaType}, actual={source.MediaType}";
        TestContext.WriteLine($"{name}: {outcome.Status}/{outcome.ReasonCode}; {binding}");
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode}; {binding}");
        Assert.AreEqual(descriptor.Artifact.CreatedUtc, source.CreatedUtc);
        Assert.AreEqual(descriptor.Artifact.MediaType, source.MediaType);
        var product = Assert.ContainsSingle(outcome.Products);
        var expected = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            $"capture-orbital-{name}-v1.json"), TestContext.CancellationToken);
        CollectionAssert.AreEqual(expected, product.Payload.ToArray());
        Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(expected), product.ChecksumSha256);
        Assert.AreEqual(product.ChecksumSha256, product.ContentIdentitySha256);
        CollectionAssert.AreEqual(new[] { descriptor.Artifact.ArtifactId }, product.SourceArtifactIds.ToArray());
        var output = CaptureOrbitalPredictionJson.Parse(product.Payload.Span, executor.Input);
        CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(fixture.Track),
            OrbitalPredictionJson.SerializeTrack(Assert.ContainsSingle(output.Tracks)));
    }

    private sealed class OrbitalBindingExecutor(CaptureOrbitalPredictionInput input, OrbitalTimeData time)
        : IProcessingRecipeExecutor
    {
        private readonly ProcessingRecipeExecutor executor = new([new CaptureOrbitalPredictionRecipe(input, time)]);
        internal CaptureOrbitalPredictionInput Input { get; } = input;
        internal ProcessingExecutionRequest? Request { get; private set; }

        public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var source = Assert.ContainsSingle(request.Inputs);
            var descriptor = Input.Plan.Descriptor;
            // Bind capture, descriptor, interval, recipe, readout and raw bytes before numerical execution.
            Assert.AreEqual(descriptor.Capture.CaptureId, source.CaptureId);
            Assert.AreEqual(descriptor.Capture.CaptureSequence, source.CaptureSequence);
            Assert.AreEqual(descriptor.Artifact.ArtifactId, source.ArtifactId);
            Assert.AreEqual(Input.Plan.DescriptorSha256, source.DescriptorIdentitySha256);
            Assert.AreEqual(descriptor.Artifact.Role, source.Role);
            Assert.AreEqual(descriptor.Artifact.Variant, source.Variant);
            Assert.AreEqual(ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                source.RecipeIdentitySha256);
            Assert.AreEqual(descriptor.Layout, source.Layout);
            Assert.AreEqual(Input.Plan.Timing.Exposure.StartUtc, source.ObservationStartedUtc);
            Assert.AreEqual(Input.Plan.Timing.Exposure.EndUtc, source.ObservationEndedUtc);
            Assert.AreEqual(descriptor.Controls.EffectiveExposure, source.Integration);
            Assert.AreEqual(descriptor.Artifact.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(source.Payload));
            return executor.ExecuteAsync(request, cancellationToken);
        }
    }
}
