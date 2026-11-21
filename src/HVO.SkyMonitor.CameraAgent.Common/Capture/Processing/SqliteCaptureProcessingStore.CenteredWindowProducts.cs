using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    internal static async Task<CenteredWindowSlotProducts?> ReadCenteredWindowProductsAsync(
        SqliteTransaction transaction, CenteredWindowSchedule schedule, ExposureKey key, CenteredRawEvidence raw,
        DateTimeOffset admitted, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (raw.Run.LinearNodeId is null)
            return new(admitted, LinearExposureDisposition.Unavailable, null, null, null, "linear.no-configured-step");

        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM processing_centered_window_holds owners
                JOIN centered_exposure_holds hold ON hold.owner_id = owners.owner_id
                WHERE owners.window_sha256 = $window AND hold.capture_id = $capture
            );
            """;
        command.Parameters.AddWithValue("$window", schedule.Identity);
        SqliteRawCaptureJournal.AddExposureParameters(command, key);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 1L)
            return new(admitted, LinearExposureDisposition.Unavailable, null, null, null, "window.input-custody-unavailable");

        var execution = ProcessingGraphOperationsCoordinator.GetLiveExecutionId(key.CaptureId, raw.Run.GraphRevisionId);
        SetCenteredWindowProductQuery(command, execution, raw.Manifest.Descriptor.Artifact.ArtifactId,
            raw.Run.GraphRevisionId, cutoff);
        LinearInputProvenanceV1? provenance = null;
        CenteredWindowProductEvidence? linear = null;
        CenteredWindowProductEvidence? measurement = null;
        var available = new List<(CenteredWindowProductEvidence Evidence, byte[]? SceneHeader)>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var lookup = ReadCenteredLookup(reader, out var committedProvenance);
                var evidence = new CenteredWindowProductEvidence(new(reader.GetInt64(21), TimeSpan.Zero), lookup);
                if (lookup.Slot.SchemaVersion == CenteredExposureProductReader.LinearSchemaVersion)
                {
                    linear = evidence;
                    provenance = lookup.Availability == ExposureProductAvailability.Available ? committedProvenance : null;
                    continue;
                }

                if (lookup.Slot.Producer == ExposureMeasurementProducts.StepAlias &&
                    lookup.Slot.Variant == ExposureMeasurementProducts.Variant && lookup.Slot.SchemaVersion == ExposureMeasurementProducts.SchemaVersion)
                    measurement = evidence;
                if (lookup is { Availability: ExposureProductAvailability.Available, Product: not null })
                {
                    var sceneHeader = ProjectedSceneV1.IsSupportedSchemaVersion(lookup.Slot.SchemaVersion) &&
                        !await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false)
                        ? await reader.GetFieldValueAsync<byte[]>(12, cancellationToken).ConfigureAwait(false)
                        : null;
                    available.Add((evidence, sceneHeader));
                }
            }
        }
        if (linear is null)
            return null;

        // The immediate close transaction owns the snapshot before cutoff is sampled.
        // A pre-commit node completion timestamp cannot prove visibility at a past cutoff.
        if (measurement is null && cutoff < schedule.DeadlineUtc)
            measurement = await ReadOutputlessCenteredMeasurementAsync(command, key, execution, cutoff, cancellationToken)
                .ConfigureAwait(false);

        if (linear.Lookup.Product is not { } product || provenance is null ||
            provenance.Disposition is not (LinearExposureDisposition.Calibrated or LinearExposureDisposition.QualifiedNormalizationOnly))
        {
            var disposition = linear.Lookup.Availability switch
            {
                ExposureProductAvailability.Incompatible => LinearExposureDisposition.Unsupported,
                ExposureProductAvailability.Failed => LinearExposureDisposition.Failed,
                ExposureProductAvailability.Expired => LinearExposureDisposition.Expired,
                _ => LinearExposureDisposition.Unavailable
            };
            return new(linear.SettledUtc, disposition, null, measurement, null,
                linear.Lookup.Reason ?? "linear.exact-product-unavailable");
        }

        var descriptor = raw.Manifest.Descriptor;
        var rawSha = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        if (provenance.RawArtifactId != descriptor.Artifact.ArtifactId || provenance.RawDescriptorSha256 != rawSha ||
            provenance.RawPayloadSha256 != descriptor.Artifact.ChecksumSha256 || provenance.LinearArtifactId != product.ArtifactId ||
            provenance.OutputIdentitySha256 != product.OutputIdentitySha256 || provenance.LinearPayloadSha256 != product.PayloadSha256)
            throw new InvalidDataException("The centered linear product does not match the admitted raw exposure.");

        var area = (long)descriptor.Layout.Width * descriptor.Layout.Height;
        if (area is <= 0 or > LinearRegisteredMean.MaximumPixels)
            return new(linear.SettledUtc, LinearExposureDisposition.Unsupported, null, measurement, null, "window.registration-pixel-limit");

        var rawHeader = CenteredStackArtifactHeader.FromArtifact(CenteredWindowArtifactHeader.FromRaw(descriptor));
        var rawPin = CenteredStackArtifactPin.FromHeader(rawHeader, provenance.RawPayloadSha256);
        var linearHeader = await ReadCenteredProductHeaderAsync(transaction, product, cancellationToken).ConfigureAwait(false);
        var linearPin = CenteredStackArtifactPin.FromHeader(linearHeader, product.PayloadSha256);
        if (linearPin.DescriptorSha256 != provenance.LinearDescriptorSha256 || linearPin.ProducerSha256 != provenance.Recipe.IdentitySha256)
            throw new InvalidDataException("The committed centered linear header differs from its qualified provenance.");
        var artifacts = new List<(CenteredWindowProductEvidence Evidence, CenteredStackArtifactPin Pin)>();
        foreach (var (evidence, sceneHeader) in available)
        {
            var reference = evidence.Lookup.Product!;
            var header = sceneHeader is null
                ? await ReadCenteredProductHeaderAsync(transaction, reference, cancellationToken).ConfigureAwait(false)
                : ReadHistoricalCenteredSceneHeader(sceneHeader, reference);
            artifacts.Add((evidence, CenteredStackArtifactPin.FromHeader(header, reference.PayloadSha256)));
        }
        var measurementPin = artifacts.Find(item => item.Evidence == measurement).Pin;
        var plate = artifacts.Find(item => item.Evidence.Lookup.Slot.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
        CenteredStackPlatePins? platePins = null;
        if (plate.Pin is not null)
        {
            command.CommandText = """
                SELECT source_artifact_id FROM processing_output_sources WHERE output_identity_sha256 = $solutionOutput
                ORDER BY source_ordinal LIMIT 5;
                """;
            command.Parameters.AddWithValue("$solutionOutput", plate.Evidence.Lookup.Product!.OutputIdentitySha256);
            var parents = new List<Guid>(4);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                parents.Add(Guid.ParseExact(reader.GetString(0), "N"));
            if (parents.Count == 4 && parents[0] == rawPin.ArtifactId)
            {
                var associations = artifacts.Find(item => item.Pin.ArtifactId == parents[1] &&
                    item.Evidence.Lookup.Slot.SchemaVersion == MeasuredStellarAssociationsV1.CurrentSchemaVersion).Pin;
                var scene = artifacts.Find(item => item.Pin.ArtifactId == parents[2] &&
                    ProjectedSceneV1.IsSupportedSchemaVersion(item.Evidence.Lookup.Slot.SchemaVersion)).Pin;
                var context = artifacts.Find(item => item.Pin.ArtifactId == parents[3] &&
                    item.Evidence.Lookup.Slot.SchemaVersion == MeasuredAstrometryContextV1.CurrentSchemaVersion).Pin;
                if (associations is not null && scene is not null && context is not null)
                    platePins = new(associations, scene, context, plate.Pin);
            }
        }

        var admittedExposure = CenteredExposureProductReader.CreateExposure(key, raw);
        var settlement = new LinearExposureLookup(admittedExposure, provenance.Disposition, linear.Lookup.Revision,
            product, provenance, linear.Lookup.Reason);
        var measurementInput = CenteredStackRetainedInputReader.BindCommittedMeasurement(settlement,
            linearHeader.Layout ?? throw new InvalidDataException("The centered linear header has no committed layout."),
            CenteredWindowRetainedRegistrationSource.ResolveGeometry(raw));
        var mask = CenteredStackValidityMask.PinAllValid(rawHeader, linearHeader, cancellationToken);
        var pin = CenteredStackExposurePin.FromCommitted(admittedExposure, provenance, measurementInput,
            rawPin, linearPin, mask, measurementPin, platePins);
        return new(linear.SettledUtc, provenance.Disposition, product, measurement, platePins is null ? null : plate.Evidence,
            RegistrationPin: pin);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The projection is a fixed internal constant with parameterized filters.")]
    internal static void SetCenteredWindowProductQuery(
        SqliteCommand command, Guid execution, Guid rawArtifactId, string graphRevisionId, DateTimeOffset cutoff)
    {
        command.CommandText = CenteredProductProjection + $$"""
             JOIN processing_centered_product_clock clock ON clock.revision = product.revision
             JOIN processing_executions execution ON execution.execution_id = product.execution_id
             {{CenteredLinearNodeJoin}}
             LEFT JOIN processing_centered_output_availability availability ON availability.change_id = (
                SELECT change_id FROM processing_centered_output_availability history
                WHERE history.output_identity_sha256 = product.output_identity AND history.recorded_ticks <= $cutoff
                ORDER BY change_id DESC LIMIT 1)
             WHERE product.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
               AND product.execution_id = $execution AND execution.execution_class = 'Live'
               AND execution.allow_automatic_publication = 1 AND execution.capture_id = $capture
               AND execution.primary_artifact_id = $raw AND execution.graph_revision_id = $graph
               AND clock.settled_ticks <= $cutoff
               AND ((product.schema_version = $linear AND product.producer = COALESCE(node.node_id, run.linear_node_id)
                      AND product.variant = run.linear_variant)
                    OR product.schema_version IN ($measurement, $solution, $context, $associations)
                    OR product.schema_version IN (SELECT value FROM json_each($scenes)))
             ORDER BY product.revision;
            """;
        // Keep the #1919 projection offsets intact for its provenance verifier.
        command.CommandText = command.CommandText.Replace("output.availability_state, output.availability_reason",
            """
            CASE WHEN product.schema_version IN (SELECT value FROM json_each($scenes)) AND product.provenance_json IS NULL
                      AND EXISTS (SELECT 1 FROM processing_centered_output_availability loss
                          WHERE loss.output_identity_sha256 = product.output_identity AND loss.state <> 'Available'
                            AND loss.recorded_ticks > $cutoff) THEN 'Missing'
                 WHEN product.schema_version IN (SELECT value FROM json_each($scenes))
                      AND CASE WHEN json_valid(CAST(product.provenance_json AS TEXT))
                          THEN json_extract(CAST(product.provenance_json AS TEXT), '$.TrustedFromTicks') > $cutoff END THEN 'Missing'
                 WHEN output.artifact_id IS NULL AND (product.provenance_json IS NULL OR
                      product.schema_version NOT IN (SELECT value FROM json_each($scenes))) THEN NULL
                 WHEN availability.change_id IS NULL THEN 'Available'
                 WHEN availability.observed_ticks IS NULL OR availability.observed_ticks > $cutoff THEN 'Missing'
                 ELSE availability.state END,
            CASE WHEN availability.change_id IS NULL THEN NULL
                 WHEN availability.observed_ticks IS NULL OR availability.observed_ticks > $cutoff
                    THEN 'product.availability-visibility-unproven'
                 ELSE availability.reason END
            """, StringComparison.Ordinal).Replace("raw.payload_sha256", "raw.payload_sha256, clock.settled_ticks", StringComparison.Ordinal);
        command.Parameters.AddWithValue("$execution", execution.ToString("N"));
        command.Parameters.AddWithValue("$raw", rawArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$graph", graphRevisionId);
        command.Parameters.AddWithValue("$cutoff", cutoff.UtcTicks);
        command.Parameters.AddWithValue("$cutoff_ms", cutoff.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$linear", CenteredExposureProductReader.LinearSchemaVersion);
        command.Parameters.AddWithValue("$measurement", ExposureMeasurementProducts.SchemaVersion);
        command.Parameters.AddWithValue("$solution", MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
        command.Parameters.AddWithValue("$context", MeasuredAstrometryContextV1.CurrentSchemaVersion);
        command.Parameters.AddWithValue("$associations", MeasuredStellarAssociationsV1.CurrentSchemaVersion);
        command.Parameters.AddWithValue("$scenes", JsonSerializer.Serialize(ProjectedSceneV1.SupportedSchemaVersions));
    }

    private static async ValueTask<CenteredWindowProductEvidence?> ReadOutputlessCenteredMeasurementAsync(
        SqliteCommand command, ExposureKey key, Guid execution, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        command.CommandText = """
            SELECT node.status, node.reason
            FROM processing_execution_nodes node
            JOIN processing_executions execution ON execution.execution_id = node.execution_id
            JOIN centered_exposure_attempts attempt ON attempt.capture_id = execution.capture_id
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            JOIN processing_node_attempts terminal ON terminal.execution_id = node.execution_id AND terminal.node_id = node.node_id
                AND terminal.attempt_number = node.attempt_count AND terminal.status = node.status
                AND terminal.completed_unix_ms = node.completed_unix_ms AND terminal.reason IS node.reason
            WHERE execution.execution_id = $execution AND execution.execution_class = 'Live'
                AND execution.allow_automatic_publication = 1 AND execution.capture_id = $capture
                AND execution.primary_artifact_id = $raw AND execution.graph_revision_id = $graph
                AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
                AND node.node_id = $producer AND node.status IN ('Skipped', 'TerminalFailure')
                AND node.reason IS NOT NULL AND node.completed_unix_ms <= $cutoff_ms
                AND NOT EXISTS (SELECT 1 FROM processing_execution_outputs output
                    WHERE output.execution_id = node.execution_id AND output.node_id = node.node_id)
                AND NOT EXISTS (SELECT 1 FROM centered_exposure_products product
                    WHERE product.capture_id = $capture AND product.execution_id = $execution
                        AND product.producer = $producer AND product.schema_version = $measurement AND product.variant = $variant);
            """;
        command.Parameters.AddWithValue("$producer", ExposureMeasurementProducts.StepAlias);
        command.Parameters.AddWithValue("$variant", ExposureMeasurementProducts.Variant);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var slot = new ExposureProductSlot(execution, ExposureMeasurementProducts.StepAlias,
            ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);
        var availability = reader.GetString(0) == nameof(DurableProcessingNodeStatus.TerminalFailure)
            ? ExposureProductAvailability.Failed : ExposureProductAvailability.Unavailable;
        return new(cutoff, new(key, slot, availability, null, null, reader.GetString(1)));
    }

    private static async ValueTask<CenteredStackArtifactHeader> ReadCenteredProductHeaderAsync(
        SqliteTransaction transaction, ExposureProductRef reference, CancellationToken cancellationToken)
    {
        var output = await ReadOutputByArtifactIdAsync(transaction.Connection!, transaction, reference.ArtifactId, cancellationToken)
            .ConfigureAwait(false);
        if (output is null || output.Capture.CaptureId != reference.Exposure.CaptureId ||
            output.Capture.AgentId != reference.Exposure.Camera.AgentId || output.OutputIdentitySha256 != reference.OutputIdentitySha256 ||
            output.Artifact.ChecksumSha256 != reference.PayloadSha256 || output.ContentIdentitySha256 != reference.ContentIdentitySha256)
            throw new InvalidDataException("The committed centered header differs from its admitted product reference.");
        return CenteredStackArtifactHeader.FromArtifact(CenteredWindowArtifactHeader.FromOutput(output));
    }

    private static CenteredStackArtifactHeader ReadHistoricalCenteredSceneHeader(byte[] saved, ExposureProductRef reference)
    {
        var historical = JsonSerializer.Deserialize<HistoricalCenteredScene>(saved);
        if (historical is not { Header: { RecipeDescriptor: not null, Compatibility: not null, SourceArtifactIds: not null } header } ||
            historical.TrustedFromTicks < 0 || historical.TrustedFromTicks > DateTimeOffset.MaxValue.UtcTicks ||
            header.ArtifactId != reference.ArtifactId || header.CaptureId != reference.Exposure.CaptureId ||
            header.SchemaVersion != reference.Slot.SchemaVersion || header.ContentIdentitySha256 != reference.ContentIdentitySha256)
            throw new InvalidDataException("The historical centered scene header differs from its committed product.");
        return header;
    }
}
