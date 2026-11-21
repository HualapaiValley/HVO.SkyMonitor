-- Raw schema 17 additions from 1dea5e36beb9955dd480fcd7ba6038cbd42b1617. Apply after FrozenRawIngressV14.sql.
CREATE TABLE centered_acquisition_owners (
    agent_id TEXT PRIMARY KEY,
    camera_id TEXT,
    attachment_id TEXT NOT NULL,
    profile_identity TEXT,
    device_identity TEXT,
    active_flag INTEGER NOT NULL CHECK (active_flag IN (0, 1))
);
CREATE TABLE centered_exposure_runs (
    run_id TEXT PRIMARY KEY,
    agent_id TEXT NOT NULL,
    camera_id TEXT NOT NULL,
    snapshot_json BLOB NOT NULL,
    snapshot_sha256 TEXT NOT NULL,
    calibration_bundle_id TEXT,
    device_identity TEXT,
    linear_node_id TEXT,
    linear_variant TEXT,
    closed_reason TEXT
);
CREATE UNIQUE INDEX ux_centered_open_run ON centered_exposure_runs(agent_id, camera_id)
    WHERE closed_reason IS NULL;
CREATE TABLE centered_exposure_attempts (
    attempt_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES centered_exposure_runs(run_id),
    ordinal INTEGER NOT NULL CHECK (ordinal > 0),
    source_capture_id TEXT,
    capture_id TEXT UNIQUE,
    status TEXT NOT NULL CHECK (status IN ('pending','dispatched','observed','expired')),
    requested_json BLOB,
    metering_json BLOB,
    reason TEXT,
    UNIQUE (run_id, ordinal)
);
CREATE INDEX ix_centered_pending_source ON centered_exposure_attempts(source_capture_id)
    WHERE status = 'pending';
CREATE TABLE centered_exposure_products (
    revision INTEGER PRIMARY KEY AUTOINCREMENT,
    agent_id TEXT NOT NULL,
    camera_id TEXT NOT NULL,
    capture_id TEXT NOT NULL,
    execution_id TEXT NOT NULL,
    producer TEXT NOT NULL,
    schema_version TEXT NOT NULL,
    variant TEXT NOT NULL,
    output_identity TEXT,
    artifact_id TEXT,
    payload_sha256 TEXT,
    content_identity_sha256 TEXT,
    disposition TEXT NOT NULL,
    reason TEXT,
    provenance_json BLOB,
    UNIQUE (capture_id, execution_id, producer, schema_version, variant)
);
CREATE INDEX ix_centered_product_capture ON centered_exposure_products(capture_id, revision);
CREATE INDEX ix_centered_product_camera ON centered_exposure_products(agent_id, camera_id, revision);
CREATE TABLE centered_exposure_holds (
    owner_id TEXT PRIMARY KEY,
    capture_id TEXT NOT NULL,
    output_identity TEXT NOT NULL,
    bytes INTEGER NOT NULL CHECK (bytes > 0),
    expires_unix_ms INTEGER NOT NULL
);
CREATE INDEX ix_centered_hold_capture ON centered_exposure_holds(capture_id, expires_unix_ms);
