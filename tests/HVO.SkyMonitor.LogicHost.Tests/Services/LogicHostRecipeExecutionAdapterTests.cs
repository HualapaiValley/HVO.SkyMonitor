using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class LogicHostRecipeExecutionAdapterTests
{
    [TestMethod]
    public async Task FrozenBindingKindClassifiesPrimaryAndAuxiliaryInputsRegardlessOfName()
    {
        var executor = new RecordingExecutor();
        var adapter = new LogicHostRecipeExecutionAdapter(executor);
        var primary = CreateArtifact(FrameArtifactRole.Raw, "source");
        var auxiliary = CreateArtifact(FrameArtifactRole.Preview, "preview");
        var options = CaptureContractJson.SerializeToElement(new { });

        // The frozen graph binding names are deliberately swapped relative to the legacy "input" convention.
        _ = await adapter.ExecuteAsync(
            [
                new LogicHostProcessingInput(
                    null, primary.Payload, "frame", primary, ProcessingGraphInputBindingKind.PrimaryArtifact),
                new LogicHostProcessingInput(
                    null, auxiliary.Payload, "input", auxiliary, ProcessingGraphInputBindingKind.AuxiliaryArtifact)
            ],
            BuiltInProcessingRecipes.EncodedPreview,
            options,
            ProcessingInputSelector.Raw(),
            "variant",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        var request = executor.Requests.Single();
        Assert.AreEqual(primary.ArtifactId, request.InputArtifactId);
        var auxiliaryInput = request.AuxiliaryInputs!.Single();
        Assert.AreEqual("input", auxiliaryInput.Name);
        Assert.AreEqual(auxiliary.ArtifactId, auxiliaryInput.ArtifactId);
        Assert.AreEqual(ProcessingAuxiliaryInputKind.Artifact, auxiliaryInput.Kind);
    }

    [TestMethod]
    public async Task LegacyInputsWithoutBindingKindFallBackToTheInputNameConvention()
    {
        var executor = new RecordingExecutor();
        var adapter = new LogicHostRecipeExecutionAdapter(executor);
        var primary = CreateArtifact(FrameArtifactRole.Raw, "source");
        var auxiliary = CreateArtifact(FrameArtifactRole.Preview, "preview");

        _ = await adapter.ExecuteAsync(
            [
                new LogicHostProcessingInput(null, primary.Payload, "input", primary),
                new LogicHostProcessingInput(null, auxiliary.Payload, "assessment", auxiliary)
            ],
            BuiltInProcessingRecipes.EncodedPreview,
            CaptureContractJson.SerializeToElement(new { }),
            ProcessingInputSelector.Raw(),
            "variant",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        var request = executor.Requests.Single();
        Assert.AreEqual(primary.ArtifactId, request.InputArtifactId);
        Assert.AreEqual("assessment", request.AuxiliaryInputs!.Single().Name);
    }

    [TestMethod]
    public async Task AmbiguousPrimaryClassificationLeavesInputArtifactUnresolved()
    {
        var executor = new RecordingExecutor();
        var adapter = new LogicHostRecipeExecutionAdapter(executor);
        var first = CreateArtifact(FrameArtifactRole.Raw, "source");
        var second = CreateArtifact(FrameArtifactRole.Raw, "other");

        _ = await adapter.ExecuteAsync(
            [
                new LogicHostProcessingInput(
                    null, first.Payload, "a", first, ProcessingGraphInputBindingKind.PrimaryArtifact),
                new LogicHostProcessingInput(
                    null, second.Payload, "b", second, ProcessingGraphInputBindingKind.PrimaryArtifact)
            ],
            BuiltInProcessingRecipes.EncodedPreview,
            CaptureContractJson.SerializeToElement(new { }),
            ProcessingInputSelector.Raw(),
            "variant",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(executor.Requests.Single().InputArtifactId);
        Assert.IsTrue(executor.Requests.Single().AuxiliaryInputs is null or { Count: 0 });
    }

    /// <summary>
    /// The capture and descriptor identity the central primary artifact now carries (#526) are execution facts only:
    /// a central preview and a central annotation bind the same execution inputs, recipe identity, output identity
    /// and bytes with both fields set as with both cleared, so no existing central pin moves with them.
    /// </summary>
    [TestMethod]
    [DataRow(BuiltInProcessingRecipes.EncodedPreview)]
    [DataRow(BuiltInProcessingRecipes.Annotation)]
    public async Task CaptureAndDescriptorIdentityDoNotEnterCentralIdentities(string recipeName)
    {
        var descriptor = ProcessingConformanceFixture.CreateDescriptor();
        var annotation = recipeName == BuiltInProcessingRecipes.Annotation
            ? new ProcessingAnnotationInput(
                [new ProjectedAnnotationObject("fixture", "Fixture", new PixelPoint(1, 1), true, true)],
                [],
                new PreviewTransform(1, 1),
                null,
                new string('A', 64))
            : null;
        var options = recipeName == BuiltInProcessingRecipes.Annotation
            ? CaptureContractJson.SerializeToElement(new AnnotationRecipeOptions(OutputEncoding: "Packed"))
            : CaptureContractJson.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed"));
        var selector = ProcessingInputSelector.Raw("source");
        var (request, failure) = LogicHostRecipeExecutionAdapter.CreateRequest(
            [new LogicHostProcessingInput(descriptor, ProcessingConformanceFixture.Payload)],
            recipeName, options, selector, "neutral", annotation, null, reconstructPayloads: true);
        Assert.IsNull(failure);
        var bound = request!.Inputs.Single();
        Assert.AreEqual(descriptor.Capture.CaptureId, bound.CaptureId);
        Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(descriptor), bound.DescriptorIdentitySha256);
        var cleared = request with
        {
            Inputs = [.. request.Inputs.Select(static input => input with { CaptureId = null, DescriptorIdentitySha256 = null })]
        };

        var executor = new ProcessingRecipeExecutor();
        var withFields = await executor.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
        var withoutFields = await executor.ExecuteAsync(cleared, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, withFields.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, withoutFields.Status);
        var expected = withoutFields.Products.Single();
        var actual = withFields.Products.Single();
        // The bound execution inputs enter only the recipe identity, and both match the request-free expectation.
        var expectedIdentity = BuiltInProcessingRecipes.CreateExecutionIdentity(recipeName, options, selector, annotation);
        Assert.AreEqual(expectedIdentity.IdentitySha256, actual.Recipe.IdentitySha256);
        Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
        Assert.AreEqual(expected.Recipe.Descriptor.OptionsSha256, actual.Recipe.Descriptor.OptionsSha256);
        Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
        Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
        CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
        CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
    }

    private static ProcessingArtifact CreateArtifact(FrameArtifactRole role, string variant)
        => new(
            Guid.NewGuid(),
            role,
            variant,
            new string('A', 64),
            "application/octet-stream",
            null,
            new byte[] { 1, 2, 3 },
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"));

    private sealed class RecordingExecutor : IProcessingRecipeExecutor
    {
        public List<ProcessingExecutionRequest> Requests { get; } = [];

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(ProcessingOutcome.Skipped("test"));
        }
    }
}
