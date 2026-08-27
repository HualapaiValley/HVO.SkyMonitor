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
        var redactedConfiguration = configuration with
        {
            Observatory = new ObservatoryLocation(0, 0, 0, "UTC"),
            DeploymentLocation = null,
            DeploymentLocationRedacted = true
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new CaptureLaneEnvelope(redactedConfiguration, lightweight), SerializerOptions);
        return (json, Convert.ToHexString(SHA256.HashData(json)));
    }

    internal static CaptureLaneEnvelope Deserialize(ReadOnlySpan<byte> json, string expectedSha256)
    {
        var actual = Convert.ToHexString(SHA256.HashData(json));
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Capture lane context checksum does not match its journal record.");
        }

        var envelope = JsonSerializer.Deserialize<CaptureLaneEnvelope>(json, SerializerOptions)
            ?? throw new InvalidDataException("Capture lane context is invalid.");
        var configuration = envelope.Configuration;
        if (configuration.ProcessingSteps is null && configuration.Pipeline is not null)
        {
            return envelope;
        }

        var policy = configuration.Rig.ControlPolicy;
        var normalizedPolicy = policy is null
            ? null
            : policy with
            {
                ExposureControl = ResolveLegacyOwnership(policy.ExposureControl, policy.AutoExposure),
                GainControl = ResolveLegacyOwnership(policy.GainControl, policy.AutoGain)
            };
        return envelope with
        {
            Configuration = configuration with
            {
                Rig = configuration.Rig with { ControlPolicy = normalizedPolicy },
                ProcessingSteps = null,
                Pipeline = configuration.Pipeline ?? new CapturePipelineConfig(
                    configuration.ProcessingSteps ?? [],
                    CapturePipelineSchemaVersions.LegacyV1,
                    CapturePipelineDependencyPolicy.LegacyInference)
            }
        };
    }

    internal static (byte[] Json, string Sha256) Redact(ReadOnlySpan<byte> json, string expectedSha256)
    {
        var envelope = Deserialize(json, expectedSha256);
        return Serialize(envelope.Configuration, envelope.Submission);
    }

    private static AutomaticControlOwnership ResolveLegacyOwnership(
        AutomaticControlOwnership ownership,
        CameraFeatureDirective? legacy)
        => ownership != AutomaticControlOwnership.Unspecified
            ? ownership
            : legacy == CameraFeatureDirective.Enabled
                ? AutomaticControlOwnership.HostMetered
                : AutomaticControlOwnership.Disabled;
}
