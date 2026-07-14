using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services.Processing;

internal sealed record LogicHostProcessingInput(
    ReconstructionDescriptor Descriptor,
    ReadOnlyMemory<byte> Payload);

internal sealed class LogicHostRecipeExecutionAdapter(IProcessingRecipeExecutor executor)
{
    private readonly IProcessingRecipeExecutor _executor = executor;

    internal ValueTask<ProcessingOutcome> ExecuteAsync(
        ReconstructionDescriptor descriptor,
        ReadOnlyMemory<byte> payload,
        string recipeName,
        JsonElement options,
        ProcessingInputSelector selector,
        string outputVariant,
        ProcessingAnnotationInput? annotation = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            [new LogicHostProcessingInput(descriptor, payload)],
            recipeName,
            options,
            selector,
            outputVariant,
            annotation,
            cancellationToken);

    internal ValueTask<ProcessingOutcome> ExecuteAsync(
        IReadOnlyList<LogicHostProcessingInput> inputs,
        string recipeName,
        JsonElement options,
        ProcessingInputSelector selector,
        string outputVariant,
        ProcessingAnnotationInput? annotation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var artifacts = new List<ProcessingArtifact>(inputs.Count);
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(input.Descriptor);
            var reconstruction = FrameReconstructor.TryReconstruct(
                input.Descriptor, input.Payload, out _);
            if (!reconstruction.IsValid)
            {
                return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.InvalidLayout,
                    reconstruction.FieldPath));
            }
            var descriptor = input.Descriptor;
            artifacts.Add(new ProcessingArtifact(
                descriptor.Artifact.ArtifactId,
                descriptor.Artifact.Role,
                descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                descriptor.Artifact.MediaType,
                descriptor.Layout,
                input.Payload,
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Controls.EffectiveExposure,
                CreateCompatibility(descriptor)));
        }
        return _executor.ExecuteAsync(new ProcessingExecutionRequest(
            recipeName,
            options,
            selector,
            artifacts,
            outputVariant,
            annotation), cancellationToken);
    }

    private static ProcessingCompatibilityIdentity CreateCompatibility(ReconstructionDescriptor descriptor)
    {
        // Manifest v2 identifies orientation within the aggregate rig profile rather than as a separate profile.
        return new(
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Rig.Sha256,
            descriptor.Profiles.Calibration.Sha256,
            descriptor.Profiles.Mask.Sha256,
            descriptor.Profiles.Sensor.Sha256,
            string.Create(CultureInfo.InvariantCulture,
                $"exposure={descriptor.Controls.EffectiveExposure.TotalMilliseconds:R};gain={descriptor.Controls.EffectiveGain:R};offset={descriptor.Controls.EffectiveOffset:R};temperatureSetpoint={descriptor.Controls.TemperatureSetpointC:R}"),
            descriptor.Profiles.Processing.Sha256);
    }
}
