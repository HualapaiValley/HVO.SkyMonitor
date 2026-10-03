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
                string.IsNullOrWhiteSpace(rig.Optics.ProjectionModel) || rig.Optics.ProjectionModel.Length > 64 ||
                submission.EffectiveInterval < TimeSpan.Zero)
                return null;
            return new(Positive(rig.Sensor.PixelSizeMicrons), Positive(rig.Optics.FocalLengthMillimeters),
                Positive(rig.Optics.FieldOfViewDegrees), rig.Optics.ProjectionModel,
                submission.EffectiveInterval, descriptor.CycleEvidence?.CadenceMode);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static double? Positive(double value) => double.IsFinite(value) && value > 0 ? value : null;
}
