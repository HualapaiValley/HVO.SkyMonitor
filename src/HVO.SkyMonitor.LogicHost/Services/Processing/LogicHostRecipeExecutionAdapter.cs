using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services.Processing;

internal sealed record LogicHostProcessingInput(
    ReconstructionDescriptor? Descriptor,
    ReadOnlyMemory<byte> Payload,
    string BindingName = "input",
    ProcessingArtifact? Artifact = null);

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
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliaryInputs = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            [new LogicHostProcessingInput(descriptor, payload)],
            recipeName,
            options,
            selector,
            outputVariant,
            annotation,
            auxiliaryInputs,
            cancellationToken);

    internal ValueTask<ProcessingOutcome> ExecuteAsync(
        IReadOnlyList<LogicHostProcessingInput> inputs,
        string recipeName,
        JsonElement options,
        ProcessingInputSelector selector,
        string outputVariant,
        ProcessingAnnotationInput? annotation = null,
        IReadOnlyList<ProcessingAuxiliaryInput>? auxiliaryInputs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var artifacts = new List<ProcessingArtifact>(inputs.Count);
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (input.Artifact is { } processingArtifact)
            {
                artifacts.Add(processingArtifact with { Payload = input.Payload });
            }
            else
            {
                var descriptor = input.Descriptor ?? throw new ArgumentException(
                    "A processing input must provide a reconstruction descriptor or processing artifact.", nameof(inputs));
                var reconstruction = FrameReconstructor.TryReconstruct(descriptor, input.Payload, out _);
                if (!reconstruction.IsValid)
                {
                    return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                        MapReconstructionReason(reconstruction.ReasonCode),
                        reconstruction.FieldPath));
                }
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
                    CreateCompatibility(descriptor),
                    descriptor.Capture.CaptureSequence,
                    descriptor.Artifact.SourceArtifactIds));
            }
        }
        var artifactAuxiliaryInputs = inputs
            .Where(static input => !string.Equals(input.BindingName, "input", StringComparison.Ordinal))
            .OrderBy(static input => input.BindingName, StringComparer.Ordinal)
            .Select(static input => new ProcessingAuxiliaryInput(
                input.BindingName,
                ProcessingAuxiliaryInputKind.Artifact,
                CreateSelector(ResolveArtifact(input)),
                ArtifactId: ResolveArtifact(input).ArtifactId))
            .ToArray();
        auxiliaryInputs = artifactAuxiliaryInputs
            .Concat(auxiliaryInputs ?? [])
            .OrderBy(static input => input.Name, StringComparer.Ordinal)
            .ToArray();
        var primaryInputs = inputs.Where(static input =>
            string.Equals(input.BindingName, "input", StringComparison.Ordinal)).ToArray();
        return _executor.ExecuteAsync(new ProcessingExecutionRequest(
            recipeName,
            options,
            selector,
            artifacts,
            outputVariant,
            annotation,
            auxiliaryInputs,
            primaryInputs.Length == 1
                ? ResolveArtifact(primaryInputs[0]).ArtifactId
                : null), cancellationToken);
    }

    private static ProcessingArtifact ResolveArtifact(LogicHostProcessingInput input)
        => input.Artifact ?? CreateArtifact(input.Descriptor!);

    private static ProcessingArtifact CreateArtifact(ReconstructionDescriptor descriptor)
        => new(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
            descriptor.Artifact.MediaType,
            descriptor.Layout,
            ReadOnlyMemory<byte>.Empty,
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Controls.EffectiveExposure,
            CreateCompatibility(descriptor),
            descriptor.Capture.CaptureSequence,
            descriptor.Artifact.SourceArtifactIds);

    private static ProcessingInputSelector CreateSelector(ProcessingArtifact artifact)
        => artifact.Role switch
        {
            FrameArtifactRole.Raw => ProcessingInputSelector.Raw(artifact.Variant),
            FrameArtifactRole.Calibrated => ProcessingInputSelector.Calibrated(artifact.Variant),
            FrameArtifactRole.Combined => ProcessingInputSelector.Combined(artifact.Variant),
            _ => ProcessingInputSelector.RecipeResult(
                artifact.Role,
                artifact.Variant,
                artifact.RecipeIdentitySha256)
        };

    private static string MapReconstructionReason(string? reasonCode) => reasonCode switch
    {
        CaptureContractReasonCodes.InvalidDimensions or
        CaptureContractReasonCodes.InvalidStride or
        CaptureContractReasonCodes.InvalidByteOrder or
        CaptureContractReasonCodes.InvalidSampleDepth or
        CaptureContractReasonCodes.InvalidPacking or
        CaptureContractReasonCodes.InvalidCfa or
        CaptureContractReasonCodes.InvalidLevels or
        CaptureContractReasonCodes.PayloadLengthMismatch => ProcessingReasonCodes.InvalidLayout,
        CaptureContractReasonCodes.UnsupportedFormat => ProcessingReasonCodes.UnsupportedFormat,
        CaptureContractReasonCodes.InvalidLineage => ProcessingReasonCodes.InvalidLineage,
        _ => ProcessingReasonCodes.InvalidInput
    };

    internal static ProcessingCompatibilityIdentity CreateCompatibility(ReconstructionDescriptor descriptor)
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
