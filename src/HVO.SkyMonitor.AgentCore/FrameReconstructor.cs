using System.Security.Cryptography;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Computes payload checksums without materializing a second frame-sized buffer.</summary>
public static class PayloadChecksum
{
    public static string ComputeSha256(ReadOnlySpan<byte> payload)
        => Convert.ToHexString(SHA256.HashData(payload));

    public static async ValueTask<string> ComputeSha256Async(Stream payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Convert.ToHexString(await SHA256.HashDataAsync(payload, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>Wraps validated descriptor bytes as a frame without repacking, swapping, or copying the payload.</summary>
public static class FrameReconstructor
{
    public static CaptureContractValidationResult TryReconstruct(
        ReconstructionDescriptor descriptor,
        ReadOnlyMemory<byte> payload,
        out CameraFrame? frame,
        bool verifyChecksum = true)
    {
        frame = null;
        if (descriptor is null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidIdentity, "descriptor");
        }
        var validation = descriptor.Validate();
        if (!validation.IsValid)
        {
            return validation;
        }
        if (payload.Length != descriptor.Layout.ByteLength)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.PayloadLengthMismatch, "payload");
        }
        if (verifyChecksum && !string.Equals(
                PayloadChecksum.ComputeSha256(payload.Span),
                descriptor.Artifact.ChecksumSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.PayloadChecksumMismatch, "payload");
        }

        frame = new CameraFrame(
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Layout.Width,
            descriptor.Layout.Height,
            descriptor.Layout.PixelFormat,
            payload,
            new FrameMetadata(
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveGain,
                descriptor.Controls.EffectiveTemperatureC ?? double.NaN,
                descriptor.Artifact.SourceId,
                Offset: descriptor.Controls.EffectiveOffset),
            descriptor.Layout.StrideBytes);
        return CaptureContractValidationResult.Success;
    }
}
