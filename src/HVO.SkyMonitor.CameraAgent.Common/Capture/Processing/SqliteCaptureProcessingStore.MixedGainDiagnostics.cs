using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private async ValueTask<(MixedGainDiagnosticsV1? Value, string? Reason)> ReadMixedGainDiagnosticsAsync(
        IReadOnlyList<DurableProcessingOutput> outputs,
        FrozenNormalizationPlan? frozen,
        CancellationToken cancellationToken)
    {
        if (frozen is null) return (null, null);
        var output = outputs.SingleOrDefault(static output =>
            output.Artifact.MediaType == MixedGainDiagnosticsV1.MediaType);
        if (output is null) return (null, "Diagnostics were not recorded for this execution.");
        if (output.AvailabilityState != "Available") return (null, "Diagnostic product is no longer available.");
        if (_silo is null) return (null, "Diagnostic evidence is temporarily unavailable.");
        try
        {
            var length = output.ProductManifest?.ByteLength ?? 0;
            if (length is <= 0 or > MixedGainDiagnosticsV1.MaximumPayloadBytes)
                throw new InvalidDataException("Diagnostic length is invalid.");
            var payload = await _silo.ReadMemberAsync(output.ArtifactId, output.OutputIdentitySha256, length,
                output.Artifact.ChecksumSha256, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The diagnostic product has no settled Silo publication.");
            var value = MixedGainDiagnosticsV1.Parse(payload);
            var combined = outputs.SingleOrDefault(item => item.OutputIdentitySha256 == value?.CombinedOutputIdentitySha256);
            if (value is null || value.PlanIdentitySha256 != frozen.IdentitySha256 || combined is null ||
                value.Width != frozen.Plan.InputLayout.Width || value.Height != frozen.Plan.InputLayout.Height ||
                combined.Artifact.ChecksumSha256 != value.CombinedChecksumSha256 ||
                !value.Inputs.Select(static input => input.ArtifactId).SequenceEqual(frozen.Plan.Inputs
                    .Where(static input => input.Disposition == NormalizationPlanInputDispositions.Selected)
                    .Select(static input => input.ArtifactId)))
                throw new InvalidDataException("Diagnostic binding is invalid.");
            foreach (var map in value.Support)
            {
                var retained = outputs.SingleOrDefault(item => item.OutputIdentitySha256 == map.OutputIdentitySha256);
                if (retained?.Descriptor is not { } descriptor || descriptor.Artifact.Role != AgentCore.FrameArtifactRole.Metadata ||
                    descriptor.Artifact.MediaType != MixedGainSupportV1.MediaType ||
                    descriptor.Artifact.ChecksumSha256 != map.ChecksumSha256 ||
                    descriptor.Layout != MixedGainSupportV1.CreateLayout(value.Width, value.Height) ||
                    descriptor.Layout.ByteLength != map.ByteLength)
                    throw new InvalidDataException("Diagnostic support binding is invalid.");
            }
            return (value, null);
        }
        catch (SiloStoreUnavailableException)
        {
            return (null, "Diagnostic evidence is temporarily unavailable.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, "Diagnostic evidence is missing or corrupt; counts are unavailable.");
        }
    }
}
