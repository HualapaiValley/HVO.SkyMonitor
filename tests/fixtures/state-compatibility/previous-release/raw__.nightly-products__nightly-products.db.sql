CREATE TABLE nightly_products (
    product_id TEXT PRIMARY KEY CHECK (length(product_id) = 32),
    output_identity_sha256 TEXT NOT NULL UNIQUE CHECK (length(output_identity_sha256) = 64),
    occurrence_json TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('Keogram', 'StarTrail')),
    scope TEXT NOT NULL CHECK (scope IN ('Segment', 'Rollup', 'Final')),
    observing_date TEXT NOT NULL CHECK (length(observing_date) = 10),
    window_start_utc_ticks INTEGER NOT NULL,
    window_end_utc_ticks INTEGER NOT NULL CHECK (window_end_utc_ticks > window_start_utc_ticks),
    part_ordinal INTEGER NOT NULL CHECK (part_ordinal >= 0),
    recipe_name TEXT NOT NULL CHECK (length(recipe_name) BETWEEN 1 AND 128),
    recipe_identity_sha256 TEXT NOT NULL CHECK (length(recipe_identity_sha256) = 64),
    recipe_json TEXT NOT NULL,
    algorithms_json TEXT NOT NULL,
    variant TEXT NOT NULL CHECK (length(variant) BETWEEN 1 AND 128),
    rig_profile_sha256 TEXT NOT NULL CHECK (length(rig_profile_sha256) BETWEEN 1 AND 128),
    compatibility_json TEXT NOT NULL,
    layout_json TEXT NOT NULL,
    pixel_format TEXT NOT NULL CHECK (pixel_format IN ('Mono8', 'Rgb24')),
    width INTEGER NOT NULL CHECK (width > 0),
    height INTEGER NOT NULL CHECK (height > 0),
    payload_relative_path TEXT NOT NULL UNIQUE,
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    payload_bytes INTEGER NOT NULL CHECK (payload_bytes > 0),
    rendition_relative_path TEXT NOT NULL UNIQUE,
    rendition_sha256 TEXT NOT NULL CHECK (length(rendition_sha256) = 64),
    rendition_bytes INTEGER NOT NULL CHECK (rendition_bytes > 0),
    provenance_relative_path TEXT NOT NULL UNIQUE,
    provenance_sha256 TEXT NOT NULL CHECK (length(provenance_sha256) = 64),
    provenance_bytes INTEGER NOT NULL CHECK (provenance_bytes BETWEEN 1 AND 8388608),
    source_count INTEGER NOT NULL CHECK (source_count > 0),
    first_observation_utc_ticks INTEGER NOT NULL,
    last_observation_utc_ticks INTEGER NOT NULL CHECK (last_observation_utc_ticks >= first_observation_utc_ticks),
    total_integration_ticks INTEGER NOT NULL CHECK (total_integration_ticks >= 0),
    created_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE nightly_product_sources (
    product_id TEXT NOT NULL REFERENCES nightly_products (product_id),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    source_kind TEXT NOT NULL CHECK (source_kind IN ('PreviewFrame', 'NightlyProduct')),
    artifact_id TEXT NOT NULL CHECK (length(artifact_id) = 32),
    output_identity_sha256 TEXT NOT NULL CHECK (length(output_identity_sha256) = 64),
    capture_id TEXT CHECK (capture_id IS NULL OR length(capture_id) = 32),
    observation_started_utc_ticks INTEGER NOT NULL CHECK (observation_started_utc_ticks > 0),
    PRIMARY KEY (product_id, ordinal)
) STRICT;
CREATE TABLE nightly_windows (
    occurrence_identity_sha256 TEXT NOT NULL CHECK (length(occurrence_identity_sha256) = 64),
    occurrence_json TEXT NOT NULL,            kind TEXT NOT NULL CHECK (kind IN ('Keogram', 'StarTrail')),
    scope TEXT NOT NULL CHECK (scope IN ('Segment', 'Final')),
    window_start_utc_ticks INTEGER NOT NULL,
    window_end_utc_ticks INTEGER NOT NULL CHECK (window_end_utc_ticks > window_start_utc_ticks),
    observing_date TEXT NOT NULL CHECK (length(observing_date) = 10),
    fingerprint_sha256 TEXT NOT NULL CHECK (length(fingerprint_sha256) = 64),
    disposition TEXT NOT NULL CHECK (disposition IN ('Produced', 'NoSources', 'Rejected')),
    reason_code TEXT CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 128),
    candidate_count INTEGER NOT NULL CHECK (candidate_count >= 0),
    admitted_count INTEGER NOT NULL CHECK (admitted_count >= 0 AND admitted_count <= candidate_count),
    exclusions_json TEXT NOT NULL,
    evaluated_unix_ms INTEGER NOT NULL,
    PRIMARY KEY (occurrence_identity_sha256, kind, scope, window_start_utc_ticks)
) STRICT;
CREATE TABLE nightly_window_products (
    occurrence_identity_sha256 TEXT NOT NULL,
    kind TEXT NOT NULL,
    scope TEXT NOT NULL,
    window_start_utc_ticks INTEGER NOT NULL,
    part_ordinal INTEGER NOT NULL CHECK (part_ordinal >= 0),
    product_id TEXT NOT NULL REFERENCES nightly_products (product_id),
    PRIMARY KEY (occurrence_identity_sha256, kind, scope, window_start_utc_ticks, part_ordinal),
    FOREIGN KEY (occurrence_identity_sha256, kind, scope, window_start_utc_ticks)
        REFERENCES nightly_windows (occurrence_identity_sha256, kind, scope, window_start_utc_ticks) ON DELETE CASCADE
) STRICT;
CREATE INDEX nightly_products_by_date ON nightly_products (observing_date, window_start_utc_ticks);
CREATE INDEX nightly_windows_by_date ON nightly_windows (observing_date, window_start_utc_ticks);
CREATE INDEX nightly_window_products_by_product ON nightly_window_products (product_id);
CREATE TRIGGER nightly_products_immutable_update BEFORE UPDATE ON nightly_products
BEGIN SELECT RAISE(ABORT, 'Published nightly products are immutable.'); END;
CREATE TRIGGER nightly_products_immutable_delete BEFORE DELETE ON nightly_products
BEGIN SELECT RAISE(ABORT, 'Published nightly products are immutable.'); END;
CREATE TRIGGER nightly_product_sources_immutable_update BEFORE UPDATE ON nightly_product_sources
BEGIN SELECT RAISE(ABORT, 'Nightly product lineage is immutable.'); END;
CREATE TRIGGER nightly_product_sources_immutable_delete BEFORE DELETE ON nightly_product_sources
BEGIN SELECT RAISE(ABORT, 'Nightly product lineage is immutable.'); END;
PRAGMA user_version = 2;
