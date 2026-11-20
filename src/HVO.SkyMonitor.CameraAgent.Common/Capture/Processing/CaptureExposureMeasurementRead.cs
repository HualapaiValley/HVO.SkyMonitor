using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed record ExposureMeasurementRunRead(ExposureMeasurementV1? Measurement, string? UnavailableReason);

/// <summary>Reads the single committed measurement linked to one recorded execution.</summary>
public interface IExposureMeasurementRunReader
{
    ValueTask<ExposureMeasurementRunRead?> ReadAsync(ProcessingGraphExecutionDetail execution, CancellationToken cancellationToken);
}

internal sealed partial class CaptureProcessingPersistence
{
    internal async ValueTask<(ProcessingArtifact Artifact, ExposureMeasurementV1 Measurement)> ReadMeasurementDependencyAsync(
        ProcessingExecutionContext execution, Guid captureId, CancellationToken cancellationToken)
    {
        var detail = await _store.ReadExecutionDetailAsync(execution.ExecutionId, cancellationToken).ConfigureAwait(false);
        if (detail is null || detail.Execution.CaptureId != captureId)
            throw new InvalidDataException("The exposure measurement execution is unavailable.");
        var evidence = await ReadMeasurementAsync(detail, cancellationToken).ConfigureAwait(false);
        return evidence is { Artifact: { } artifact, Read.Measurement: { } measurement }
            ? (artifact, measurement)
            : throw new InvalidDataException(evidence?.Read.UnavailableReason ?? "measurement-not-recorded");
    }

    async ValueTask<ExposureMeasurementRunRead?> IExposureMeasurementRunReader.ReadAsync(
        ProcessingGraphExecutionDetail execution, CancellationToken cancellationToken)
        => (await ReadMeasurementAsync(execution, cancellationToken).ConfigureAwait(false))?.Read;

    private async ValueTask<MeasurementEvidence?> ReadMeasurementAsync(
        ProcessingGraphExecutionDetail execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var node = execution.Nodes.SingleOrDefault(candidate => candidate.NodeId == ExposureMeasurementProducts.StepAlias);
        if (node is null)
            return null;
        var item = node.Outputs.SingleOrDefault(output => output.Role == FrameArtifactRole.Metadata &&
            output.Variant == ExposureMeasurementProducts.Variant);
        if (item is null)
            return Unavailable(node.Reason ?? (node.Status is "Pending" or "Running" ? "measurement-pending" : "measurement-not-recorded"));
        if (item.AvailabilityState != "Available")
            return Unavailable(item.AvailabilityReason ?? "measurement-unavailable");

        try
        {
            var output = await _store.ReadOutputByArtifactIdAsync(item.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (output is null || output.AvailabilityState != "Available")
                return Unavailable("measurement-unavailable");
            if (output.ProductSchemaVersion != ExposureMeasurementProducts.SchemaVersion ||
                output.Capture.CaptureId != execution.Execution.CaptureId ||
                output.OutputIdentitySha256 != item.OutputIdentitySha256)
                return Unavailable("measurement-binding-mismatch");

            var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
            var measurement = ExposureMeasurementProducts.Read(restored.Product);
            var input = measurement.Input;
            if (input.Raw.ArtifactId != execution.Execution.PrimaryArtifactId ||
                !node.Inputs.Any(raw => raw.Kind == ProcessingGraphExecutionInputKind.RawCapture && MatchesRaw(raw, input.Raw)) ||
                input.Linear is { } linear && !await MatchesLinearDependencyAsync(execution, node, linear, cancellationToken)
                    .ConfigureAwait(false))
                return Unavailable("measurement-binding-mismatch");
            return new(new(measurement, null), CreateProcessingArtifact(output, restored));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Unavailable("measurement-unreadable");
        }
    }

    private static MeasurementEvidence Unavailable(string reason) => new(new(null, reason), null);

    private sealed record MeasurementEvidence(ExposureMeasurementRunRead Read, ProcessingArtifact? Artifact);

    private static bool MatchesRaw(ProcessingGraphExecutionInputState input, ExposureMeasurementSourceV1 source)
        => input.CaptureId == source.CaptureId && input.ArtifactId == source.ArtifactId &&
           string.Equals(input.DescriptorSha256, source.DescriptorSha256, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(input.PayloadSha256, source.PayloadSha256, StringComparison.OrdinalIgnoreCase);

    private async ValueTask<bool> MatchesLinearDependencyAsync(ProcessingGraphExecutionDetail execution,
        ProcessingGraphExecutionNodeState node, ExposureMeasurementSourceV1 source, CancellationToken cancellationToken)
    {
        // Same-capture graph products are linked by the frozen graph and the producing node's committed output.
        // The Inputs collection separately describes pinned raw or historical artifacts.
        var producers = node.Dependencies.Select(dependency => dependency.ProducerId).ToHashSet(StringComparer.Ordinal);
        var publication = execution.Nodes.Where(candidate => producers.Contains(candidate.NodeId) && candidate.Status == "Completed")
            .SelectMany(candidate => candidate.Outputs).SingleOrDefault(output => output.Role == FrameArtifactRole.Calibrated);
        if (publication is null || publication.ArtifactId != source.ArtifactId ||
            !string.Equals(publication.OutputIdentitySha256, source.OutputIdentitySha256, StringComparison.OrdinalIgnoreCase))
            return false;

        var committed = await _store.ReadOutputByArtifactIdAsync(publication.ArtifactId, cancellationToken).ConfigureAwait(false);
        return committed?.Descriptor is { } descriptor && descriptor.Capture.CaptureId == source.CaptureId &&
            descriptor.Artifact.Role == FrameArtifactRole.Calibrated &&
            string.Equals(committed.OutputIdentitySha256, publication.OutputIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
            ExposureMeasurementSourceV1.FromDescriptor(descriptor) == source;
    }
}
