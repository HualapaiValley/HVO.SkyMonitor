using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>A packed display derivative whose native levels and gamma never depend on frame content.</summary>
internal sealed class FixedPreviewRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.FixedPreview, "1.0.0", "fixed-preview-v1", ProcessingOperationKind.Transform);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<FixedDisplayTransferOptions>(options);
        parsed.Validate();
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request, ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var input = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (input is null) return ValueTask.FromResult(failure!);
        if (!ProcessingRecipeSupport.TryValidateFrame(input, out var layout, out failure))
            return ValueTask.FromResult(failure!);
        if (input.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated or FrameArtifactRole.Combined) ||
            layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24 or CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.UnsupportedFormat, nameof(input)));
        var options = ProcessingRecipeSupport.ParseOptions<FixedDisplayTransferOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var pattern = layout.PixelFormat == CameraPixelFormat.BayerRggb16
            ? BayerRggb16Demosaicer.ResolvePattern(layout)
            : BayerPattern.Rggb;
        var pixels = layout.PixelFormat == CameraPixelFormat.BayerRggb16 && pattern != BayerPattern.Rggb
            ? FixedDisplayTransfer.ApplyWithDeliveredPhase(layout, input.Payload, options, pattern, cancellationToken)
            : FixedDisplayTransfer.Apply(layout, input.Payload, options, cancellationToken);
        var product = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Preview, request.OutputVariant,
            "application/x-hvo-packed-image", ProcessingRecipeSupport.CreatePackedLayout(layout.Width, layout.Height,
                layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.Mono8 ? CameraPixelFormat.Mono8 : CameraPixelFormat.Rgb24),
            pixels, identity, Algorithms(layout), [input], input.Integration, input.Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }

    internal static IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms(FrameLayoutDescriptor layout)
    {
        if (layout.PixelFormat != CameraPixelFormat.BayerRggb16)
        {
            return [new("fixed-display-transfer", FixedDisplayTransfer.AlgorithmVersion)];
        }

        var pattern = BayerRggb16Demosaicer.ResolvePattern(layout);
        var reconstructionVersion = pattern switch
        {
            BayerPattern.Rggb => LinearBayerReconstruction.AlgorithmVersion,
            BayerPattern.Grbg => $"{LinearBayerReconstruction.AlgorithmVersion}-grbg",
            BayerPattern.Gbrg => $"{LinearBayerReconstruction.AlgorithmVersion}-gbrg",
            BayerPattern.Bggr => $"{LinearBayerReconstruction.AlgorithmVersion}-bggr",
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };
        return [new("fixed-display-transfer", FixedDisplayTransfer.AlgorithmVersion),
            new("linear-rggb-reconstruction", reconstructionVersion)];
    }
}
