namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

// Shared verbatim with installer preflight to verify the published astrometry lookup index.
internal static class AstrometryJournalSchema
{
    internal const string LookupIndexName = "ix_processing_outputs_astrometry_lookup";
    internal const string LookupIndexDefinition = """
        CREATE INDEX ix_processing_outputs_astrometry_lookup
            ON processing_outputs(role, product_schema_version, capture_id, artifact_id, content_identity_sha256, availability_state)
            WHERE role = 'Metadata' AND product_schema_version = 'measured-astrometry-solution-v1'
        """;
}
