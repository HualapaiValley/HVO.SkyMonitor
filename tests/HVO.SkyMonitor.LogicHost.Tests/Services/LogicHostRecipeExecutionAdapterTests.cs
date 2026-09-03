using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;

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
