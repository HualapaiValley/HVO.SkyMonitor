using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class CenteredLinearQualification
{
    internal static bool TryResolveLevels(
        FrameLayoutDescriptor? layout, out double black, out double white, out string? reason)
    {
        black = white = 0;
        if (!FrameWhiteThreshold.TryResolveStored(layout, out var storedWhite, out reason))
            return false;

        if (layout is null ||
            layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            layout.ByteOrder != FrameByteOrder.LittleEndian || layout.Packing != FrameSamplePacking.ByteAligned ||
            layout.ContainerDepthBits != 16 || !layout.Validate().IsValid ||
            layout.BlackLevel is null ||
            layout.PixelFormat == CameraPixelFormat.BayerRggb16 &&
                layout.Readout is not { BinX: 1, BinY: 1, CfaOriginX: not null, CfaOriginY: not null })
        {
            reason = "linear.unsupported-or-unverified-layout";
            return false;
        }

        black = CenteredNormalizationLayout.CreateWorkingLayout(layout).BlackLevel!.Value;
        white = storedWhite;
        if (black >= 0 && white <= ushort.MaxValue && white > black)
        {
            reason = null;
            return true;
        }
        reason = "linear.invalid-levels";
        return false;
    }

    internal static BayerMeteringPhotosites ToDeliveredPhotosites(
        CaptureMeteringCfaSelection selection, FrameReadoutDescriptor? readout)
    {
        var local = BayerMeteringPhotosites.None;
        var phase = (readout?.CfaOriginX ?? 0) + 2 * (readout?.CfaOriginY ?? 0);
        for (var i = 0; i < 4; i++)
        {
            if (((int)selection & (1 << i)) != 0)
                local |= (BayerMeteringPhotosites)(1 << (i ^ phase));
        }
        return local;
    }
}
