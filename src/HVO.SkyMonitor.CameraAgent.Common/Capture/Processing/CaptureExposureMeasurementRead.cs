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
    async ValueTask<ExposureMeasurementRunRead?> IExposureMeasurementRunReader.ReadAsync(
        ProcessingGraphExecutionDetail execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var node = execution.Nodes.SingleOrDefault(candidate => candidate.NodeId == ExposureMeasurementProducts.StepAlias);
        if (node is null)
            return null;
        var item = node.Outputs.SingleOrDefault(output => output.Role == FrameArtifactRole.Metadata &&
            output.Variant == ExposureMeasurementProducts.Variant);
        if (item is null)
            return new(null, node.Reason ?? (node.Status is "Pending" or "Running" ? "measurement-pending" : "measurement-not-recorded"));
        if (item.AvailabilityState != "Available")
            return new(null, item.AvailabilityReason ?? "measurement-unavailable");

        try
        {
            var output = await _store.ReadOutputByArtifactIdAsync(item.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (output is null || output.AvailabilityState != "Available")
                return new(null, "measurement-unavailable");
            if (output.ProductSchemaVersion != ExposureMeasurementProducts.SchemaVersion ||
                output.Capture.CaptureId != execution.Execution.CaptureId ||
                output.OutputIdentitySha256 != item.OutputIdentitySha256)
                return new(null, "measurement-binding-mismatch");

            var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
            var measurement = ExposureMeasurementProducts.Read(restored.Product);
            var input = measurement.Input;
            if (input.Raw.ArtifactId != execution.Execution.PrimaryArtifactId ||
                !node.Inputs.Any(raw => raw.Kind == ProcessingGraphExecutionInputKind.RawCapture && Matches(raw, input.Raw)) ||
                input.Linear is { } linear && !node.Inputs.Any(source =>
                    source.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput && Matches(source, linear)))
                return new(null, "measurement-binding-mismatch");
            return new(measurement, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, "measurement-unreadable");
        }
    }

    private static bool Matches(ProcessingGraphExecutionInputState input, ExposureMeasurementSourceV1 source)
        => input.CaptureId == source.CaptureId && input.ArtifactId == source.ArtifactId &&
           (input.DescriptorSha256 is null || string.Equals(input.DescriptorSha256, source.DescriptorSha256, StringComparison.OrdinalIgnoreCase)) &&
           (input.PayloadSha256 is null || string.Equals(input.PayloadSha256, source.PayloadSha256, StringComparison.OrdinalIgnoreCase)) &&
           string.Equals(input.OutputIdentitySha256, source.OutputIdentitySha256, StringComparison.OrdinalIgnoreCase);
}
