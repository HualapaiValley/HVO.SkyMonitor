namespace HVO.SkyMonitor.AgentCore;

/// <summary>Resolves the first stored code that reaches a declared sensor white threshold.</summary>
public static class FrameWhiteThreshold
{
    public const string OpaqueAdcReason = "linear.opaque-adc-to-container";
    public const string UnavailableReason = "linear.white-threshold-unavailable";

    public static bool TryResolveStored(
        FrameLayoutDescriptor? layout, out ushort threshold, out string? reason)
    {
        threshold = 0;
        if (layout?.StoredCodeTransform == FrameStoredCodeTransform.OpaqueContainerV1)
        {
            reason = OpaqueAdcReason;
            return false;
        }

        if (layout is null || !layout.Validate().IsValid ||
            layout.WhiteLevel is not { } declared || layout.LevelCodeSpace is not { } space ||
            layout.StoredCodeTransform is not { } transform)
        {
            reason = UnavailableReason;
            return false;
        }

        // A fractional NativeSample threshold first selects an integer native code. The stored codec
        // then encodes that integer, including the rounding used by full-range sensor output.
        var code = checked((int)Math.Ceiling(declared));
        if (space == FrameLevelCodeSpace.NativeSample)
        {
            code = transform switch
            {
                FrameStoredCodeTransform.LeftShiftedV1 => code << (layout.ContainerDepthBits - layout.SampleDepthBits),
                FrameStoredCodeTransform.FullRangeScaledV1 => checked((int)Math.Round(
                    code * ((1 << layout.ContainerDepthBits) - 1d) /
                    ((1 << layout.SampleDepthBits) - 1d), MidpointRounding.AwayFromZero)),
                _ => code
            };
        }

        threshold = checked((ushort)code);
        reason = null;
        return true;
    }
}
