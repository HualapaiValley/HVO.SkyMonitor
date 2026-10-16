using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

internal sealed partial class SqliteCameraAgentTransientOperatorProjection
{
    public async ValueTask<CameraAgentCaptureTrails> GetCaptureTrailsAsync(
        Guid captureId, CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty)
        {
            return TrailUnavailable(captureId);
        }
        await _rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "raw_captures", cancellationToken).ConfigureAwait(false))
        {
            return TrailUnavailable(captureId);
        }
        var manifest = await ReadTrailManifestAsync(connection, captureId, cancellationToken).ConfigureAwait(false);
        if (manifest is null) return TrailUnavailable(captureId);
        if (!await TableExistsAsync(connection, "transient_candidate_sources", cancellationToken).ConfigureAwait(false))
        {
            return new(captureId, "NoRecordedCandidate", [], false);
        }

        var ids = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT s.candidate_id
                FROM raw_captures raw
                JOIN transient_candidate_sources s ON s.raw_capture_row_id = raw.raw_capture_row_id
                WHERE raw.capture_id = $capture
                ORDER BY s.candidate_id LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
            command.Parameters.AddWithValue("$limit", CameraAgentCaptureTrails.MaximumTrails + 1);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(Guid.ParseExact(reader.GetString(0), "N"));
            }
        }
        if (ids.Count == 0)
        {
            return new(captureId, "NoRecordedCandidate", [], false);
        }
        if (ids.Count > CameraAgentCaptureTrails.MaximumTrails)
        {
            return TrailUnavailable(captureId, "The capture references more candidates than the bounded evidence query supports.");
        }

        var trails = new List<CameraAgentCaptureTrail>();
        var incomplete = false;
        var runtime = await RuntimeTableExistsAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            ProjectedRow row;
            using (var command = CreateDetailCommand(connection, id, runtime))
            {
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    incomplete = true;
                    continue;
                }
                row = await ReadAndValidateRowAsync(reader, cancellationToken).ConfigureAwait(false);
            }
            var extraction = row.Centered ?? row.Causal;
            var candidate = extraction?.Candidates.SingleOrDefault(value => value.CandidateId == id);
            if (candidate?.Geometry is not { } geometry || extraction is null)
            {
                incomplete = true;
                continue;
            }
            var source = candidate.ContextSources.SingleOrDefault(value => value.EvidenceId == geometry.SourceEvidenceId);
            if (source is null || source.Locator.Artifact.Role != FrameArtifactRole.Raw)
            {
                incomplete = true;
                continue;
            }
            // Background membership does not make this candidate's measured trail belong to this capture.
            if (source.Locator.Artifact.ArtifactId != manifest.Descriptor.Artifact.ArtifactId) continue;
            if (!MatchesTrailSource(source, manifest) || geometry.SourceEvidenceId != candidate.CenterEvidenceId ||
                extraction.TargetDetectorInputIdentitySha256 != candidate.Provenance.DetectorInputIdentitySha256 ||
                manifest.Descriptor.Layout.BlackLevel is not { } black ||
                manifest.Descriptor.Layout.WhiteLevel is not { } white ||
                black < 0 || black > ushort.MaxValue || black != Math.Truncate(black) ||
                white < 0 || white > ushort.MaxValue || white != Math.Truncate(white))
            {
                incomplete = true;
                continue;
            }
            var detector = TransientDetectorInputMetadata.Restore(
                source, manifest.Descriptor.Layout,
                new TransientLinearLevelsV1((ushort)black, (ushort)white, (ushort)white),
                CameraAgentRecipeExecutionAdapter.CreateCompatibility(manifest.Descriptor),
                extraction.SaturationMaskChecksumSha256, candidate.Provenance.DetectorInputIdentitySha256);
            if (detector is null || detector.Layout.Width != geometry.CoordinateWidth ||
                detector.Layout.Height != geometry.CoordinateHeight)
            {
                incomplete = true;
                continue;
            }
            var confirmed = IsConfirmedMeteor(row.Finalization?.Event, source, geometry, candidate.Provenance);
            var state = confirmed ? "ConfirmedMeteor" : row.EventState switch
            {
                "Rejected" => "RejectedCandidate",
                "NeedsReview" => "NeedsReview",
                "Validated" => "ValidatedEvent",
                _ => "ProvisionalCandidate"
            };
            trails.Add(new(id, row.EventId, state,
                ComputeSha256(TransientContractJson.Serialize(candidate)), extraction.ExtractionIdentitySha256,
                ProjectAssessment(row.Assessment, row.Phase).IdentitySha256,
                row.Finalization?.ReceiptIdentitySha256, manifest, detector, geometry));
        }
        return new(captureId, trails.Count > 0 ? "MeasuredEvidence" : incomplete ? "Incomplete" : "NoRecordedCandidate",
            trails.ToArray(), incomplete,
            incomplete ? "Some recorded candidates lack compatible measured geometry or transform evidence." : null);
    }

    private static bool MatchesTrailSource(TransientSourceEvidenceReferenceV1 source, ArtifactManifestV2 manifest)
    {
        var artifact = manifest.Descriptor.Artifact;
        var reference = source.Locator.Artifact;
        return reference.Role == FrameArtifactRole.Raw && artifact.Role == FrameArtifactRole.Raw &&
            reference.ArtifactId == artifact.ArtifactId && reference.Variant == artifact.Variant &&
            reference.ChecksumSha256 == artifact.ChecksumSha256 &&
            reference.RecipeIdentitySha256 == ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256 &&
            source.ObservationStartedUtc == manifest.Descriptor.Timing.ExposureStartedUtc &&
            source.ObservationEndedUtc == manifest.Descriptor.Timing.ExposureEndedUtc;
    }

    private static bool IsConfirmedMeteor(TransientEventV1? transientEvent,
        TransientSourceEvidenceReferenceV1 source, TransientGeometryV1 geometry,
        TransientObservationProvenanceV1 provenance)
    {
        if (transientEvent?.State != TransientEventState.Validated) return false;
        var observation = transientEvent.Observations.SingleOrDefault(value =>
            value.Source == source && value.Provenance == provenance &&
            value.Geometry.SourceEvidenceId == geometry.SourceEvidenceId &&
            value.Geometry.CoordinateWidth == geometry.CoordinateWidth &&
            value.Geometry.CoordinateHeight == geometry.CoordinateHeight &&
            value.Geometry.Bounds == geometry.Bounds && value.Geometry.Polyline.SequenceEqual(geometry.Polyline));
        if (observation is null) return false;
        var active = transientEvent.Assessments.Where(value =>
            value.Authority == TransientAssessmentAuthority.Authoritative &&
            value.EvidenceObservationIds.Contains(observation.ObservationId) &&
            !transientEvent.Assessments.Any(next => next.SupersedesAssessmentId == value.AssessmentId)).ToArray();
        return active.Length > 0 && active.All(static value => value.Classification == TransientClassification.Meteor);
    }

    private static async ValueTask<ArtifactManifestV2?> ReadTrailManifestAsync(
        SqliteConnection connection, Guid captureId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_artifact_id, manifest_sha256, payload_sha256,
                   CASE WHEN length(manifest_json) <= 4194304 THEN manifest_json ELSE NULL END,
                   capture_sequence, exposure_started_unix_ms, descriptor_sha256
            FROM raw_captures WHERE capture_id = $capture AND state = 'committed';
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)) return null;
        var bytes = await reader.GetFieldValueAsync<byte[]>(3, cancellationToken).ConfigureAwait(false);
        var parsed = CaptureContractJson.ParseManifest(bytes);
        var manifest = parsed.Document?.Manifest;
        return parsed.IsValid && manifest is not null &&
            manifest.Descriptor.Capture.CaptureId == captureId &&
            manifest.Descriptor.Capture.CaptureSequence == reader.GetInt64(4) &&
            manifest.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds() == reader.GetInt64(5) &&
            manifest.Descriptor.Artifact.Role == FrameArtifactRole.Raw &&
            manifest.Descriptor.Artifact.ArtifactId.ToString("N") == reader.GetString(0) &&
            CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor) == reader.GetString(6) &&
            CaptureContractJson.ComputeManifestSha256(bytes) == reader.GetString(1) &&
            manifest.Descriptor.Artifact.ChecksumSha256 == reader.GetString(2)
                ? manifest : null;
    }

    private static CameraAgentCaptureTrails TrailUnavailable(Guid captureId,
        string reason = "Compatible source, timing and measured transform evidence is unavailable.")
        => new(captureId, "Unavailable", [], true, reason);
}
