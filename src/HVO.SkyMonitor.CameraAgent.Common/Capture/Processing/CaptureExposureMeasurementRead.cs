using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public sealed record ExposureMeasurementRunRead(ExposureMeasurementV1? Measurement, string? UnavailableReason, string? LinearExpiryReason = null);

/// <summary>Reads the single committed measurement linked to one recorded execution.</summary>
public interface IExposureMeasurementRunReader
{
    ValueTask<ExposureMeasurementRunRead?> ReadAsync(ProcessingGraphExecutionDetail execution, CancellationToken cancellationToken);
}

internal sealed class CaptureExposureMeasurementReader(
    CaptureProcessingPersistence persistence, RawCaptureIngress ingress, IExposureProductReader products)
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
        var item = node.Outputs.SingleOrDefault(output => output.Role == FrameArtifactRole.Metadata &&
            output.Variant == ExposureMeasurementProducts.Variant);
        if (item is null)
            return new(null, node.Reason ?? (node.Status is "Pending" or "Running" ? "measurement-pending" : "measurement-not-recorded"));
        if (item.AvailabilityState != "Available")
            return new(null, item.AvailabilityReason ?? "measurement-unavailable");

        try
        {
            var artifact = await persistence.RestoreMeasurementAsync(item.ArtifactId, cancellationToken).ConfigureAwait(false);
            var run = execution.Execution;
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
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException)
        {
            return new(null, "measurement-unreadable");
        }
    }

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
        var native = RigProjectionContextFactory.CreateNative(rig);
        ProjectionContext? aperture = layout.Readout is { } readout
            ? readout.NativeWidth == native.WidthPixels && readout.NativeHeight == native.HeightPixels
                ? RigProjectionContextFactory.CreateReadoutView(native, readout) : null
            : native.WidthPixels == layout.Width && native.HeightPixels == layout.Height ? native : null;
        return (aperture, rig.Sensor.PixelSizeMicrons > 0 ? rig.Sensor.PixelSizeMicrons : null);
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
