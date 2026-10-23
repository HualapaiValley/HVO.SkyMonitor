using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
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
        try
        {
            var path = Path.GetFullPath(Path.Combine(_root, output.PayloadRelativePath));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Diagnostic path is outside storage.");
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
            var length = output.ProductManifest?.ByteLength ?? 0;
            if (length is <= 0 or > MixedGainDiagnosticsV1.MaximumPayloadBytes)
                throw new InvalidDataException("Diagnostic length is invalid.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != length) throw new InvalidDataException("Diagnostic length changed.");
            var payload = new byte[(int)length];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ProcessingIdentity.ComputePayloadSha256(payload), output.Artifact.ChecksumSha256,
                    StringComparison.Ordinal)) throw new InvalidDataException("Diagnostic checksum changed.");
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, "Diagnostic evidence is missing or corrupt; counts are unavailable.");
        }
    }
}
