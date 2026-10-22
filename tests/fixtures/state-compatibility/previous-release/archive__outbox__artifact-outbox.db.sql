CREATE TABLE artifact_outbox_schema(
    schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
    version INTEGER NOT NULL CHECK(version = 2)
) STRICT;
CREATE TABLE artifact_outbox_records(
    record_id INTEGER PRIMARY KEY AUTOINCREMENT,
    idempotency_key TEXT NOT NULL UNIQUE CHECK(length(idempotency_key) = 64),
    manifest_kind TEXT NOT NULL CHECK(manifest_kind = 'v2'),
    manifest_bytes BLOB NOT NULL CHECK(length(manifest_bytes) > 0),
    artifact_id TEXT NOT NULL CHECK(length(artifact_id) = 32),
    role TEXT NOT NULL CHECK(role IN ('Raw', 'Calibrated', 'Combined', 'Preview', 'AnnotatedPreview', 'Metadata')),
    relative_artifact_path TEXT NOT NULL CHECK(length(relative_artifact_path) > 0),
    payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
    payload_length INTEGER NOT NULL CHECK(payload_length >= 0),
    media_type TEXT NOT NULL CHECK(length(media_type) > 0),
    status TEXT NOT NULL CHECK(status IN ('pending', 'leased', 'retry', 'acknowledged', 'quarantined', 'abandoned')),
    attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
    next_attempt_unix_ms INTEGER NOT NULL,
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    lease_owner TEXT NULL,
    lease_token TEXT NULL,
    lease_expires_unix_ms INTEGER NULL,
    completion_token TEXT NULL,
    last_reason TEXT NULL,
    acknowledgement BLOB NULL,
    acknowledged_unix_ms INTEGER NULL,
    terminal_actor TEXT NULL,
    terminal_reason TEXT NULL,
    terminal_unix_ms INTEGER NULL,
    CHECK((status = 'leased') = (lease_owner IS NOT NULL)),
    CHECK((status = 'leased') = (lease_token IS NOT NULL)),
    CHECK((status = 'leased') = (lease_expires_unix_ms IS NOT NULL)),
    CHECK((status = 'acknowledged') = (completion_token IS NOT NULL)),
    CHECK((status = 'acknowledged') = (acknowledgement IS NOT NULL)),
    CHECK((status = 'acknowledged') = (acknowledged_unix_ms IS NOT NULL)),
    CHECK((status = 'abandoned') = (terminal_actor IS NOT NULL)),
    CHECK((status = 'abandoned') = (terminal_reason IS NOT NULL)),
    CHECK((status = 'abandoned') = (terminal_unix_ms IS NOT NULL))
) STRICT;
CREATE TABLE artifact_outbox_audit(
    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    idempotency_key TEXT NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT NOT NULL,
    occurred_unix_ms INTEGER NOT NULL,
    operation_key TEXT NULL,
    FOREIGN KEY(idempotency_key) REFERENCES artifact_outbox_records(idempotency_key)
) STRICT;
CREATE TABLE artifact_outbox_conflicts(
    conflict_id INTEGER PRIMARY KEY AUTOINCREMENT,
    idempotency_key TEXT NOT NULL,
    conflicting_manifest_bytes BLOB NOT NULL,
    reason TEXT NOT NULL,
    observed_unix_ms INTEGER NOT NULL,
    FOREIGN KEY(idempotency_key) REFERENCES artifact_outbox_records(idempotency_key)
) STRICT;
CREATE TABLE artifact_outbox_operations(
    operation_key TEXT NOT NULL PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
    idempotency_key TEXT NOT NULL,
    action TEXT NOT NULL CHECK(action IN ('replay','abandon')),
    actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner','system')),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 64),
    occurred_unix_ms INTEGER NOT NULL,
    FOREIGN KEY(idempotency_key) REFERENCES artifact_outbox_records(idempotency_key)
) STRICT;
INSERT INTO "artifact_outbox_schema" VALUES(1, 2);
CREATE INDEX ix_artifact_outbox_claim
    ON artifact_outbox_records(status, next_attempt_unix_ms, created_unix_ms, idempotency_key);
CREATE INDEX ix_artifact_outbox_lease
    ON artifact_outbox_records(status, lease_expires_unix_ms, created_unix_ms, idempotency_key);
CREATE INDEX ix_artifact_outbox_operations
    ON artifact_outbox_records(status, record_id DESC);
CREATE UNIQUE INDEX ux_artifact_outbox_audit_operation
    ON artifact_outbox_audit(operation_key) WHERE operation_key IS NOT NULL;
PRAGMA user_version = 0;
