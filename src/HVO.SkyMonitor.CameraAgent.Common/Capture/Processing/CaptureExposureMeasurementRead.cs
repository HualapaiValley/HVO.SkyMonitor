using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed record ExposureMeasurementRunRead(ExposureMeasurementV1? Measurement, string? UnavailableReason, string? LinearExpiryReason = null)
{
    public const string PublicationIoReason = "measurement-publication-io";
}

/// <summary>Reads the single committed measurement linked to one recorded execution.</summary>
public interface IExposureMeasurementRunReader
{
    ValueTask<ExposureMeasurementRunRead?> ReadAsync(ProcessingGraphExecutionDetail execution, CancellationToken cancellationToken);
}

internal sealed class CaptureExposureMeasurementReader(
    CaptureProcessingPersistence persistence, RawCaptureIngress ingress, IExposureProductReader products,
    SqliteCaptureProcessingStore store)
    : IExposureMeasurementRunReader
{
    internal async ValueTask<(ProcessingArtifact Artifact, ExposureMeasurementV1 Measurement)> ReadDependencyAsync(
        CaptureProcessingContext context, Guid artifactId, CancellationToken cancellationToken)
    {
        var manifest = context.RawCapture?.Manifest ?? throw new InvalidDataException("The admitted raw is unavailable.");
        var execution = context.ProcessingExecution ?? throw new InvalidDataException("The durable execution is unavailable.");
        var key = Key(manifest);
        var artifact = await persistence.RestoreMeasurementAsync(artifactId, cancellationToken).ConfigureAwait(false);
        var (measurement, _) = await ReadExactAsync(key, execution.GraphRevisionId, execution.ExecutionClass, execution.ExecutionId,
            artifact, cancellationToken).ConfigureAwait(false);
        return (artifact, measurement);
    }

    public async ValueTask<ExposureMeasurementRunRead?> ReadAsync(
        ProcessingGraphExecutionDetail execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var node = execution.Nodes.SingleOrDefault(candidate => candidate.NodeId == ExposureMeasurementProducts.StepAlias);
        if (node is null)
            return null;
        var run = execution.Execution;
        var item = node.Outputs.SingleOrDefault(output => output.Role == FrameArtifactRole.Metadata &&
            output.Variant == ExposureMeasurementProducts.Variant);

        try
        {
            if (item is null || item.AvailabilityState != "Available")
            {
                var slot = await store.ReadLiveMeasurementSlotAsync(run, cancellationToken).ConfigureAwait(false);
                if (slot is not null)
                    return Unavailable(slot);
                if (item is not null)
                    return new(null, item.AvailabilityReason ?? "measurement-unavailable");
                if (node.Status == "Completed" || node.CommittedOutputCount > 0)
                    return new(null, "measurement-unreadable");
                return run.Status is ProcessingGraphExecutionStatus.Pending or ProcessingGraphExecutionStatus.Running &&
                    node.Status is "Pending" or "Running" or "RetryableFailure"
                    ? new(null, "measurement-pending")
                    : new(null, node.Reason ?? run.FailureReason ?? "measurement-not-recorded");
            }

            var artifact = await persistence.RestoreMeasurementAsync(item.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (artifact.CaptureId != run.CaptureId ||
                !item.SourceArtifactIds.SequenceEqual(artifact.SourceArtifactIds ?? []) ||
                ProcessingIdentity.CreateOutputIdentity(artifact.Role, artifact.Variant,
                    artifact.RecipeIdentitySha256, artifact.SourceArtifactIds ?? []) != item.OutputIdentitySha256)
                return new(null, "measurement-binding-mismatch");

            // The key in the authenticated product is a lookup hint. The journal supplies all expected input facts.
            var key = ExposureMeasurementProducts.ReadExposureKey(artifact);
            if (key.CaptureId != run.CaptureId)
                return new(null, "measurement-binding-mismatch");
            var (measurement, expiry) = await ReadExactAsync(key, run.GraphRevisionId, run.ExecutionClass, run.ExecutionId,
                artifact, cancellationToken).ConfigureAwait(false);
            return measurement.Input.Raw.ArtifactId == run.PrimaryArtifactId
                ? new(measurement, null, expiry) : new(null, "measurement-binding-mismatch");
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            if (item is { AvailabilityState: "Available" })
            {
                // Retention can remove the payload and output row after the execution snapshot was read.
                // Only the settlement for this exact output can distinguish that race from damage.
                try
                {
                    var slot = await store.ReadLiveMeasurementSlotAsync(run, cancellationToken, item.OutputIdentitySha256).ConfigureAwait(false);
                    if (slot is { Availability: ExposureProductAvailability.Expired, Reason: "retention-expired" })
                        return Unavailable(slot);
                }
                catch (Exception recheckFailure) when (IsUnreadable(recheckFailure))
                {
                    // The journal could not establish expiry. Preserve the unreadable result.
                }
            }
            return new(null, "measurement-unreadable");
        }
    }

    private static bool IsUnreadable(Exception exception) => exception is InvalidDataException or IOException or
        UnauthorizedAccessException or ArgumentException or RawIngressConflictException or System.Text.Json.JsonException;

    private static ExposureMeasurementRunRead Unavailable(ExposureProductLookup slot) => slot.Availability switch
    {
        ExposureProductAvailability.Expired => new(null, slot.Reason ?? "measurement-expired"),
        ExposureProductAvailability.Pending => new(null, "measurement-pending"),
        ExposureProductAvailability.Failed or ExposureProductAvailability.Incompatible or ExposureProductAvailability.Unavailable
            => new(null, slot.Reason ?? "measurement-unavailable"),
        // A settled Available slot without a readable output is missing evidence, not an empty measurement.
        _ => new(null, "measurement-unreadable")
    };

    private async ValueTask<(ExposureMeasurementV1 Measurement, string? LinearExpiryReason)> ReadExactAsync(ExposureKey key, string revisionId,
        ProcessingGraphExecutionClass executionClass, Guid executionId, ProcessingArtifact artifact, CancellationToken cancellationToken)
    {
        await ingress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var evidence = await ingress.ReadCenteredRawAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The admitted exposure containing this measurement is unavailable.");
        var live = LiveExecution(key, evidence);
        if (revisionId != evidence.Run.GraphRevisionId || executionClass == ProcessingGraphExecutionClass.Live && executionId != live)
            throw new InvalidDataException("The measurement execution differs from the admitted graph.");
        var (aperture, pitch) = Geometry(evidence, evidence.Manifest.Descriptor.Layout);
        return await ExposureMeasurementBindings.ReadCommittedWithSourceAsync(products, key, live, artifact, cancellationToken,
            aperture, pitch).ConfigureAwait(false);
    }

    internal static ExposureKey Key(ArtifactManifestV2 manifest)
        => manifest.CenteredExposure is { Source: var source }
            ? new(source.Camera, source.RunId, manifest.Descriptor.Capture.CaptureId)
            : throw new InvalidDataException("The capture has no centered admission evidence.");

    internal static Guid LiveExecution(ExposureKey key, CenteredRawEvidence evidence)
        => ProcessingGraphOperationsCoordinator.GetLiveExecutionId(key.CaptureId, evidence.Run.GraphRevisionId);

    internal static (ProjectionContext? Aperture, double? PixelPitch) Geometry(CenteredRawEvidence evidence, FrameLayoutDescriptor layout)
    {
        var rig = evidence.Run.Configuration.Rig;
        if (!string.Equals(RigProjectionContextFactory.CreateProfileHashSha256(rig), evidence.Manifest.Descriptor.Profiles.Rig.Sha256,
                StringComparison.OrdinalIgnoreCase) || layout != evidence.Manifest.Descriptor.Layout)
            throw new InvalidDataException("The measurement geometry differs from the admitted rig or readout.");
        var aperture = RigProjectionContextFactory.CreateDeliveredView(rig, layout);
        return (aperture, rig.Sensor.PixelSizeMicrons > 0 ? rig.Sensor.PixelSizeMicrons : null);
    }
}

internal sealed partial class SqliteCaptureProcessingStore
{
    internal async ValueTask<ExposureProductLookup?> ReadLiveMeasurementSlotAsync(
        ProcessingGraphExecutionState execution, CancellationToken cancellationToken, string? outputIdentity = null)
    {
        if (execution.ExecutionClass != ProcessingGraphExecutionClass.Live ||
            execution.ExecutionId != ProcessingGraphOperationsCoordinator.GetLiveExecutionId(execution.CaptureId, execution.GraphRevisionId))
            return null;

        // Once retention removes the output row, only the exact execution-owned product settlement knows it expired.
        // Do not recover a key from a different execution's artifact, and never select by capture alone.
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = CenteredProductProjection + """
             JOIN processing_executions owning_execution ON owning_execution.execution_id = product.execution_id
             WHERE product.capture_id = $capture AND product.execution_id = $execution AND raw.raw_artifact_id = $raw
               AND owning_execution.capture_id = $capture AND owning_execution.primary_artifact_id = $raw
               AND owning_execution.graph_revision_id = $graph AND owning_execution.execution_class = 'Live'
               AND owning_execution.trigger_kind = 'capture' AND owning_execution.allow_automatic_publication = 1
               AND product.producer = $producer AND product.schema_version = $schema AND product.variant = $variant
               AND ($output IS NULL OR product.output_identity = $output);
            """;
        command.Parameters.AddWithValue("$capture", execution.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$raw", execution.PrimaryArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$graph", execution.GraphRevisionId);
        command.Parameters.AddWithValue("$producer", ExposureMeasurementProducts.StepAlias);
        command.Parameters.AddWithValue("$schema", ExposureMeasurementProducts.SchemaVersion);
        command.Parameters.AddWithValue("$variant", ExposureMeasurementProducts.Variant);
        command.Parameters.AddWithValue("$output", (object?)outputIdentity ?? DBNull.Value);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCenteredLookup(reader, out _) : null;
    }
}

internal sealed partial class CaptureProcessingPersistence
{
    internal async ValueTask<ProcessingArtifact> RestoreMeasurementAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var output = await _store.ReadOutputByArtifactIdAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (output is null || output.AvailabilityState != "Available" ||
            output.ProductSchemaVersion != ExposureMeasurementProducts.SchemaVersion ||
            output.Artifact.Variant != ExposureMeasurementProducts.Variant ||
            output.Artifact.MediaType != ExposureMeasurementProducts.MediaType)
            throw new InvalidDataException("The requested exposure measurement output is unavailable.");
        var restored = await RestoreOutputAsync(output, cancellationToken).ConfigureAwait(false);
        return CreateProcessingArtifact(output, restored);
    }
}
