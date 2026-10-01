using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal static class CameraAgentCaptureProfileProjector
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal static CameraAgentCaptureProfileFacts? Project(
        ReconstructionDescriptor descriptor, ReadOnlySpan<byte> contextJson, string contextSha256)
    {
        try
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(contextJson)), contextSha256, StringComparison.Ordinal))
                return null;
            // Read the original snapshot, without replay's legacy normalization:
            // its exact rig content must agree with the committed raw manifest.
            var envelope = JsonSerializer.Deserialize<CaptureLaneEnvelope>(contextJson, WebJson);
            var configuration = envelope?.Configuration;
            var rig = configuration?.Rig;
            var submission = envelope?.Submission;
            if (rig?.Sensor is null || rig.Optics is null || submission?.Request is null ||
                configuration!.AgentId != descriptor.Capture.AgentId ||
                submission.Request.RequestedStartUtc.ToUnixTimeMilliseconds() != descriptor.Timing.RequestedStartUtc.ToUnixTimeMilliseconds() ||
                !string.Equals(CameraRigProfileIdentity.ComputeSha256(rig), descriptor.Profiles.Rig.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !double.IsFinite(rig.Sensor.PixelSizeMicrons) || rig.Sensor.PixelSizeMicrons <= 0 ||
                !double.IsFinite(rig.Optics.FocalLengthMillimeters) || rig.Optics.FocalLengthMillimeters <= 0 ||
                !double.IsFinite(rig.Optics.FieldOfViewDegrees) || rig.Optics.FieldOfViewDegrees <= 0 ||
                string.IsNullOrWhiteSpace(rig.Optics.ProjectionModel) || rig.Optics.ProjectionModel.Length > 64 ||
                submission.EffectiveInterval < TimeSpan.Zero)
                return null;
            return new(rig.Sensor.PixelSizeMicrons, rig.Optics.FocalLengthMillimeters,
                rig.Optics.FieldOfViewDegrees, rig.Optics.ProjectionModel,
                submission.EffectiveInterval, descriptor.CycleEvidence?.CadenceMode);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
