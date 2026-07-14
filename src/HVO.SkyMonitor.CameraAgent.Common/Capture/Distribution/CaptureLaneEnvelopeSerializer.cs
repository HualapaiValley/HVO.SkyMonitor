using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal static class CaptureLaneEnvelopeSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static (byte[] Json, string Sha256) Serialize(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission)
    {
        var lightweight = submission with
        {
            Result = submission.Result with { Frame = null, Artifacts = null }
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new CaptureLaneEnvelope(configuration, lightweight), SerializerOptions);
        return (json, Convert.ToHexString(SHA256.HashData(json)));
    }

    internal static CaptureLaneEnvelope Deserialize(ReadOnlySpan<byte> json, string expectedSha256)
    {
        var actual = Convert.ToHexString(SHA256.HashData(json));
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Capture lane context checksum does not match its journal record.");
        }

        return JsonSerializer.Deserialize<CaptureLaneEnvelope>(json, SerializerOptions)
            ?? throw new InvalidDataException("Capture lane context is invalid.");
    }
}
