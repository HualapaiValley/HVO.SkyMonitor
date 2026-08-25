using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

internal sealed class ProjectedSceneRecipe : IProcessingRecipe
{
    internal const string AuxiliaryInputName = "scene";
    internal const string MediaType = "application/vnd.hvo.projected-scene+json";

    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.ProjectedScene,
        "1.0.0",
        "canonical-projected-scene-v1",
        ProcessingOperationKind.Analyzer);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<Dictionary<string, JsonElement>>(options);
        if (parsed.Count != 0)
        {
            throw new ArgumentException("Projected-scene options must be empty.", nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ProcessingRecipeSupport.ResolveSingle(request, out var sourceFailure);
        if (source is null)
        {
            return ValueTask.FromResult(sourceFailure!);
        }
        if (source.Role != FrameArtifactRole.Raw || source.Layout is not { } layout)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(request.Input)));
        }

        var scenes = (request.AuxiliaryInputs ?? []).Where(input =>
            string.Equals(input.Name, AuxiliaryInputName, StringComparison.Ordinal)).ToArray();
        if (scenes.Length == 0)
        {
            return ValueTask.FromResult(ProcessingOutcome.Skipped(
                ProcessingReasonCodes.MissingProjectedScene,
                nameof(request.AuxiliaryInputs)));
        }
        if (scenes.Length != 1 || scenes[0].Kind != ProcessingAuxiliaryInputKind.CanonicalJson ||
            !string.Equals(scenes[0].SchemaVersion, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidProjectedScene,
                nameof(request.AuxiliaryInputs)));
        }

        var auxiliary = scenes[0];
        var parsed = ProjectedSceneJson.Parse(auxiliary.Payload);
        if (!parsed.IsValid || parsed.Scene is not { } scene ||
            !string.Equals(scene.SceneIdentitySha256, auxiliary.IdentitySha256, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidProjectedScene,
                parsed.ErrorPath ?? nameof(auxiliary.IdentitySha256)));
        }
        if (scene.Source.ArtifactId != source.ArtifactId ||
            source.CaptureId is not { } captureId || scene.Source.CaptureId != captureId)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.ProjectedSceneSourceMismatch,
                nameof(scene.Source)));
        }
        if (source.DescriptorIdentitySha256 is not { } descriptorIdentity ||
            !string.Equals(scene.Source.ArtifactIdentitySha256, descriptorIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.ProjectedSceneDescriptorMismatch,
                nameof(scene.Source.ArtifactIdentitySha256)));
        }
        if (scene.ImageTransform.OutputWidthPixels != layout.Width ||
            scene.ImageTransform.OutputHeightPixels != layout.Height)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.ProjectedSceneDimensionMismatch,
                nameof(scene.ImageTransform)));
        }

        return ValueTask.FromResult(ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Metadata,
            request.OutputVariant,
            MediaType,
            null,
            auxiliary.Payload.ToArray(),
            identity,
            [new ProcessingAlgorithmIdentity("projected-scene-contract", "1.0.0")],
            [source],
            source.Integration,
            source.Compatibility,
            ProcessingProductKind.Metadata,
            ProjectedSceneV1.CurrentSchemaVersion,
            scene.SceneIdentitySha256)));
    }
}
