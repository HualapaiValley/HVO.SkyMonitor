CREATE TABLE execution_evidence_schema(
    schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
    version INTEGER NOT NULL CHECK(version = 1)
) STRICT;
CREATE TABLE execution_evidence_state(
    state_key INTEGER PRIMARY KEY CHECK(state_key = 1),
    discovery_terminal_unix_ms INTEGER NOT NULL CHECK(discovery_terminal_unix_ms >= 0),
    discovery_execution_id TEXT NOT NULL CHECK(length(discovery_execution_id) IN (0, 32)),
    deferred_terminal_unix_ms INTEGER NOT NULL CHECK(deferred_terminal_unix_ms >= 0),
    source_pruned_events INTEGER NOT NULL CHECK(source_pruned_events >= 0),
    projection_rejected_events INTEGER NOT NULL CHECK(projection_rejected_events >= 0),
    conflict_events INTEGER NOT NULL CHECK(conflict_events >= 0)
) STRICT;
CREATE TABLE execution_evidence_rejections(
    execution_id TEXT PRIMARY KEY CHECK(length(execution_id) = 32),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
    observed_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE execution_evidence_origins(
    origin_identity_sha256 TEXT PRIMARY KEY CHECK(length(origin_identity_sha256) = 64),
    origin_installation_id TEXT NOT NULL CHECK(length(origin_installation_id) = 32),
    agent_instance_id TEXT NOT NULL CHECK(length(agent_instance_id) = 32),
    boot_session_id TEXT NOT NULL CHECK(length(boot_session_id) = 32),
    software_version TEXT NOT NULL CHECK(length(software_version) BETWEEN 1 AND 64),
    observatory_id TEXT NULL CHECK(observatory_id IS NULL OR length(observatory_id) = 32),
    logical_camera_installation_id TEXT NULL
        CHECK(logical_camera_installation_id IS NULL OR length(logical_camera_installation_id) = 32),
    installation_public_id TEXT NULL
        CHECK(installation_public_id IS NULL OR length(installation_public_id) = 32),
    next_sequence INTEGER NOT NULL CHECK(next_sequence >= 1),
    acknowledged_through_sequence INTEGER NOT NULL CHECK(acknowledged_through_sequence >= 0),
    created_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE execution_evidence_units(
    record_id INTEGER PRIMARY KEY AUTOINCREMENT,
    origin_identity_sha256 TEXT NOT NULL CHECK(length(origin_identity_sha256) = 64),
    origin_sequence INTEGER NOT NULL CHECK(origin_sequence >= 1),
    evidence_id TEXT NOT NULL CHECK(length(evidence_id) = 32),
    body_kind TEXT NOT NULL
        CHECK(body_kind IN ('GraphRevision', 'GraphExecution', 'ArtifactAvailability')),
    unit_key TEXT NOT NULL CHECK(length(unit_key) BETWEEN 1 AND 128),
    payload BLOB NOT NULL CHECK(length(payload) BETWEEN 2 AND 8388608),
    payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
    payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0),
    status TEXT NOT NULL
        CHECK(status IN ('pending', 'retry', 'acknowledged', 'quarantined', 'abandoned')),
    attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
    next_attempt_unix_ms INTEGER NOT NULL,
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    acknowledged_unix_ms INTEGER NULL,
    last_reason TEXT NULL CHECK(last_reason IS NULL OR length(last_reason) BETWEEN 1 AND 128),
    terminal_actor TEXT NULL CHECK(terminal_actor IS NULL OR terminal_actor IN ('owner', 'system')),
    terminal_reason TEXT NULL CHECK(terminal_reason IS NULL OR length(terminal_reason) BETWEEN 1 AND 128),
    terminal_unix_ms INTEGER NULL,
    CHECK((status = 'acknowledged') = (acknowledged_unix_ms IS NOT NULL)),
    CHECK((status = 'abandoned') = (terminal_unix_ms IS NOT NULL)),
    CHECK((terminal_unix_ms IS NULL) = (terminal_actor IS NULL)),
    CHECK((terminal_unix_ms IS NULL) = (terminal_reason IS NULL)),
    UNIQUE(origin_identity_sha256, origin_sequence),
    UNIQUE(origin_identity_sha256, unit_key),
    FOREIGN KEY(origin_identity_sha256)
        REFERENCES execution_evidence_origins(origin_identity_sha256)
) STRICT;
CREATE TABLE execution_evidence_audit(
    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    record_id INTEGER NOT NULL,
    action TEXT NOT NULL CHECK(length(action) BETWEEN 1 AND 32),
    actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner', 'system')),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
    occurred_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE execution_evidence_operations(
    operation_key TEXT PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
    record_id INTEGER NOT NULL,
    action TEXT NOT NULL CHECK(action IN ('replay', 'abandon')),
    actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner', 'system')),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
    occurred_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE execution_evidence_conflicts(
    conflict_id INTEGER PRIMARY KEY AUTOINCREMENT,
    origin_identity_sha256 TEXT NOT NULL CHECK(length(origin_identity_sha256) = 64),
    origin_sequence INTEGER NOT NULL CHECK(origin_sequence >= 1),
    local_payload_sha256 TEXT NOT NULL CHECK(length(local_payload_sha256) = 64),
    receiver_payload_sha256 TEXT NULL
        CHECK(receiver_payload_sha256 IS NULL OR length(receiver_payload_sha256) = 64),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
    observed_unix_ms INTEGER NOT NULL
) STRICT;
INSERT INTO "execution_evidence_schema" VALUES(1, 1);
INSERT INTO "execution_evidence_state" VALUES(1, 0, '', 0, 0, 0, 0);
CREATE INDEX ix_execution_evidence_units_send
    ON execution_evidence_units(origin_identity_sha256, status, next_attempt_unix_ms, origin_sequence);
CREATE INDEX ix_execution_evidence_units_status
    ON execution_evidence_units(status, created_unix_ms, record_id);
CREATE INDEX ix_execution_evidence_units_operations
    ON execution_evidence_units(status, record_id DESC);
CREATE INDEX ix_execution_evidence_units_acknowledged
    ON execution_evidence_units(status, acknowledged_unix_ms, record_id);
CREATE INDEX ix_execution_evidence_units_totals
    ON execution_evidence_units(status, payload_bytes, attempt_count, created_unix_ms, origin_sequence);
CREATE INDEX ix_execution_evidence_audit_record
    ON execution_evidence_audit(record_id, audit_id DESC);
PRAGMA user_version = 0;
