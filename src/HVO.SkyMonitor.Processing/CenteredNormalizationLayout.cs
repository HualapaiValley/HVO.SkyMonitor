using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>Derives layout metadata for centered no-correction normalization and its verifiers.</summary>
public static class CenteredNormalizationLayout
{
    /// <summary>Expresses declared native levels for transformed samples in their stored code space.</summary>
    /// <remarks>The raw layout and samples are unchanged. Unresolvable declarations pass through for input validation.</remarks>
    public static FrameLayoutDescriptor CreateWorkingLayout(FrameLayoutDescriptor rawLayout)
    {
        ArgumentNullException.ThrowIfNull(rawLayout);
        if (rawLayout is not { LevelCodeSpace: FrameLevelCodeSpace.NativeSample, BlackLevel: { } black,
            StoredCodeTransform: FrameStoredCodeTransform.LeftShiftedV1 or FrameStoredCodeTransform.FullRangeScaledV1 } ||
            !FrameWhiteThreshold.TryResolveStored(rawLayout, out var white, out _))
            return rawLayout;

        var scale = rawLayout.StoredCodeTransform == FrameStoredCodeTransform.LeftShiftedV1
            ? 1 << (rawLayout.ContainerDepthBits - rawLayout.SampleDepthBits)
            : ((1 << rawLayout.ContainerDepthBits) - 1d) / ((1 << rawLayout.SampleDepthBits) - 1);
        return rawLayout with
        {
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer,
            BlackLevel = black * scale,
            WhiteLevel = white
        };
    }

    /// <summary>Returns the expected layout of a centered no-correction product after row packing.</summary>
    public static FrameLayoutDescriptor CreateExpectedPackedLayout(FrameLayoutDescriptor rawLayout)
        => ProcessingRecipeSupport.CreatePackedLayout(CreateWorkingLayout(rawLayout));
}
