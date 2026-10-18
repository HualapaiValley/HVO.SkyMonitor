CREATE TABLE environmental_observation_metadata(
    metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1),
    stored_count INTEGER NOT NULL DEFAULT 0 CHECK(stored_count >= 0),
    stored_bytes INTEGER NOT NULL DEFAULT 0 CHECK(stored_bytes >= 0),
    overflow_count INTEGER NOT NULL DEFAULT 0 CHECK(overflow_count >= 0)) STRICT;
CREATE TABLE environmental_observation_outbox_audit(
    audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    record_id INTEGER NOT NULL,
    previous_status TEXT NOT NULL CHECK(previous_status IN ('quarantined','terminal')),
    action TEXT NOT NULL CHECK(action IN ('replay','abandon')),
    actor TEXT NOT NULL CHECK(length(actor) BETWEEN 1 AND 128),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 512),
    occurred_unix_ms INTEGER NOT NULL,
    operation_key TEXT NULL) STRICT;
CREATE TABLE environmental_observation_outbox(
    record_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
    observation_id TEXT NOT NULL,
    content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
    target_site_id TEXT NOT NULL,
    target_agent_id TEXT NOT NULL,
    payload BLOB NOT NULL,
    payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0),
    status TEXT NOT NULL CHECK(status IN ('pending','leased','retry','quarantined','terminal')),
    attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
    next_attempt_unix_ms INTEGER NOT NULL,
    lease_owner TEXT NULL,
    lease_token TEXT NULL,
    lease_expires_unix_ms INTEGER NULL,
    last_reason TEXT NULL,
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    local_record_id INTEGER NULL
        REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
    CHECK(payload_bytes = length(payload)),
    CHECK((status = 'leased' AND lease_owner IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_unix_ms IS NOT NULL)
        OR (status != 'leased' AND lease_owner IS NULL AND lease_token IS NULL AND lease_expires_unix_ms IS NULL))) STRICT;
CREATE TABLE environmental_observation_outbox_operations(
    operation_key TEXT NOT NULL PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
    record_id INTEGER NOT NULL,
    action TEXT NOT NULL CHECK(action IN ('replay','abandon')),
    actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner','system')),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 64),
    occurred_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE environmental_observation_journal_metadata(
    metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1),
    stored_count INTEGER NOT NULL DEFAULT 0 CHECK(stored_count >= 0),
    stored_bytes INTEGER NOT NULL DEFAULT 0 CHECK(stored_bytes >= 0),
    overflow_count INTEGER NOT NULL DEFAULT 0 CHECK(overflow_count >= 0)) STRICT;
CREATE TABLE environmental_observation_journal(
    record_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
    source_content_sha256 TEXT NOT NULL CHECK(length(source_content_sha256) = 64),
    observation_id TEXT NOT NULL,
    content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
    schema_version TEXT NOT NULL CHECK(length(schema_version) BETWEEN 1 AND 64),
    observation_kind TEXT NOT NULL CHECK(length(observation_kind) BETWEEN 1 AND 64),
    rig_id TEXT NULL CHECK(rig_id IS NULL OR length(rig_id) BETWEEN 1 AND 128),
    source_kind TEXT NOT NULL CHECK(source_kind IN ('Measured','Simulated','Imported','Manual','Derived')),
    quality TEXT NOT NULL CHECK(quality IN ('Unknown','Good','Suspect')),
    observed_at_unix_ms INTEGER NOT NULL,
    valid_from_unix_ms INTEGER NOT NULL,
    valid_through_unix_ms INTEGER NOT NULL,
    stale_after_unix_ms INTEGER NOT NULL,
    payload BLOB NOT NULL,
    payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0),
    recorded_unix_ms INTEGER NOT NULL,
    central_target_site_id TEXT NULL,
    central_target_agent_id TEXT NULL,
    central_target_rig_id TEXT NULL,
    CHECK(valid_from_unix_ms < valid_through_unix_ms),
    CHECK(stale_after_unix_ms BETWEEN valid_from_unix_ms AND valid_through_unix_ms),
    CHECK((central_target_site_id IS NULL AND central_target_agent_id IS NULL AND central_target_rig_id IS NULL)
        OR (central_target_site_id IS NOT NULL AND central_target_agent_id IS NOT NULL)),
    CHECK(payload_bytes = length(payload))) STRICT;
CREATE TABLE environmental_observation_local_lineage(
    local_record_id INTEGER NOT NULL
        REFERENCES environmental_observation_journal(record_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
    source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
    observation_id TEXT NOT NULL,
    referenced_record_id INTEGER NULL
        REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
    PRIMARY KEY(local_record_id, ordinal),
    UNIQUE(local_record_id, source_identity_sha256, observation_id)) STRICT;
CREATE TABLE environmental_source_runtime(
    source_id TEXT NOT NULL PRIMARY KEY CHECK(length(source_id) BETWEEN 1 AND 128),
    observation_kind TEXT NOT NULL CHECK(length(observation_kind) BETWEEN 1 AND 64),
    required INTEGER NOT NULL CHECK(required IN (0,1)),
    last_outcome TEXT NULL CHECK(last_outcome IS NULL OR last_outcome IN ('Produced','Duplicate','Missing','Failed','TimedOut','Coalesced')),
    last_reason TEXT NULL CHECK(last_reason IS NULL OR length(last_reason) BETWEEN 1 AND 128),
    last_observation_id TEXT NULL,
    last_observed_unix_ms INTEGER NULL,
    last_stale_after_unix_ms INTEGER NULL,
    last_started_unix_ms INTEGER NULL,
    last_completed_unix_ms INTEGER NULL,
    consecutive_failures INTEGER NOT NULL CHECK(consecutive_failures >= 0),
    next_poll_unix_ms INTEGER NULL,
    CHECK((last_observation_id IS NULL AND last_observed_unix_ms IS NULL AND last_stale_after_unix_ms IS NULL)
        OR (last_observation_id IS NOT NULL AND last_observed_unix_ms IS NOT NULL
            AND last_stale_after_unix_ms IS NOT NULL AND last_observed_unix_ms <= last_stale_after_unix_ms)),
    CHECK((last_outcome IS NULL AND last_reason IS NULL AND last_started_unix_ms IS NULL AND last_completed_unix_ms IS NULL)
        OR (last_outcome IS NOT NULL AND last_reason IS NOT NULL AND last_started_unix_ms IS NOT NULL
            AND last_completed_unix_ms IS NOT NULL AND last_started_unix_ms <= last_completed_unix_ms))) STRICT;
CREATE TABLE environmental_capture_regime_state(
    state_key INTEGER NOT NULL PRIMARY KEY CHECK(state_key = 1),
    capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
    capture_id TEXT NOT NULL,
    solar_regime TEXT NOT NULL CHECK(solar_regime IN ('Day','Twilight','Night')),
    observed_unix_ms INTEGER NOT NULL) STRICT;
CREATE TABLE environmental_acquisition_attempts(
    attempt_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    source_id TEXT NOT NULL CHECK(length(source_id) BETWEEN 1 AND 128),
    observation_kind TEXT NOT NULL CHECK(length(observation_kind) BETWEEN 1 AND 64),
    required INTEGER NOT NULL CHECK(required IN (0,1)),
    trigger TEXT NOT NULL CHECK(trigger IN ('Periodic','BeforeCapture','AfterCapture','EveryNthCapture','RegimeChange','OnDemand')),
    outcome TEXT NOT NULL CHECK(outcome IN ('Produced','Duplicate','Missing','Failed','TimedOut','Coalesced')),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
    observation_id TEXT NULL,
    capture_sequence INTEGER NULL CHECK(capture_sequence IS NULL OR capture_sequence > 0),
    capture_id TEXT NULL,
    started_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER NOT NULL,
    CHECK((capture_sequence IS NULL) = (capture_id IS NULL)),
    CHECK(started_unix_ms <= completed_unix_ms)) STRICT;
CREATE TABLE environmental_on_demand_commands(
    idempotency_key TEXT NOT NULL PRIMARY KEY CHECK(length(idempotency_key) BETWEEN 1 AND 128),
    payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
    source_id TEXT NOT NULL CHECK(length(source_id) BETWEEN 1 AND 128),
    actor_id TEXT NOT NULL CHECK(length(actor_id) BETWEEN 1 AND 256),
    reason TEXT NULL CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 128),
    observed_unix_ms INTEGER NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('running','completed')),
    lease_token TEXT NULL,
    lease_expires_unix_ms INTEGER NULL,
    receipt_json BLOB NULL,
    updated_unix_ms INTEGER NOT NULL,
    CHECK((status = 'running' AND lease_token IS NOT NULL AND lease_expires_unix_ms IS NOT NULL AND receipt_json IS NULL)
        OR (status = 'completed' AND lease_token IS NULL AND lease_expires_unix_ms IS NULL AND receipt_json IS NOT NULL))) STRICT;
CREATE TABLE environmental_capture_associations(
    association_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    capture_id TEXT NOT NULL,
    capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
    observation_kind TEXT NOT NULL CHECK(length(observation_kind) BETWEEN 1 AND 64),
    rig_id TEXT NULL CHECK(rig_id IS NULL OR length(rig_id) BETWEEN 1 AND 128),
    exposure_from_unix_ms INTEGER NOT NULL,
    exposure_through_unix_ms INTEGER NOT NULL,
    policy_identity_sha256 TEXT NOT NULL CHECK(length(policy_identity_sha256) = 64),
    status TEXT NOT NULL CHECK(status IN ('Fresh','Stale','Missing','Contradictory')),
    selected_record_id INTEGER NULL
        REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
    conflicting_record_ids_json BLOB NOT NULL,
    association_identity_sha256 TEXT NOT NULL CHECK(length(association_identity_sha256) = 64),
    created_unix_ms INTEGER NOT NULL,
    CHECK(exposure_from_unix_ms < exposure_through_unix_ms),
    UNIQUE(capture_id, observation_kind, policy_identity_sha256)) STRICT;
CREATE TABLE environmental_capture_association_evidence(
    association_id INTEGER NOT NULL
        REFERENCES environmental_capture_associations(association_id) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
    role TEXT NOT NULL CHECK(role IN ('selected','conflict')),
    local_record_id INTEGER NOT NULL
        REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
    source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
    observation_id TEXT NOT NULL,
    content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
    PRIMARY KEY(association_id, ordinal),
    UNIQUE(association_id, local_record_id)) STRICT;
CREATE TABLE environmental_observation_central_projection(
    projection_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    local_record_id INTEGER NOT NULL
        REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
    status TEXT NOT NULL CHECK(status IN ('waiting','staged','acknowledged')),
    outbox_record_id INTEGER NULL,
    target_site_id TEXT NOT NULL,
    target_agent_id TEXT NOT NULL,
    target_rig_id TEXT NULL,
    acknowledged_unix_ms INTEGER NULL,
    updated_unix_ms INTEGER NOT NULL,
    CHECK((status = 'staged' AND outbox_record_id IS NOT NULL AND acknowledged_unix_ms IS NULL)
        OR (status = 'waiting' AND outbox_record_id IS NULL AND acknowledged_unix_ms IS NULL)
        OR (status = 'acknowledged' AND outbox_record_id IS NULL AND acknowledged_unix_ms IS NOT NULL)),
    UNIQUE(local_record_id, target_site_id, target_agent_id, target_rig_id)) STRICT;
INSERT INTO "environmental_observation_metadata" VALUES(1, 0, 0, 0);
INSERT INTO "environmental_observation_journal_metadata" VALUES(1, 0, 0, 0);
CREATE UNIQUE INDEX ux_environment_identity
    ON environmental_observation_outbox(source_identity_sha256, observation_id);
CREATE INDEX ix_environment_operations
    ON environmental_observation_outbox(status, record_id DESC);
CREATE INDEX ix_environment_claim
    ON environmental_observation_outbox(status, next_attempt_unix_ms, created_unix_ms, record_id);
CREATE INDEX ix_environment_lease
    ON environmental_observation_outbox(status, lease_expires_unix_ms, created_unix_ms, record_id);
CREATE UNIQUE INDEX ux_environment_audit_operation
    ON environmental_observation_outbox_audit(operation_key) WHERE operation_key IS NOT NULL;
CREATE UNIQUE INDEX ux_environment_local_identity
    ON environmental_observation_journal(source_identity_sha256, observation_id);
CREATE INDEX ix_environment_local_history
    ON environmental_observation_journal(
        observation_kind, observed_at_unix_ms DESC, source_identity_sha256, observation_id, record_id DESC);
CREATE INDEX ix_environment_local_validity
    ON environmental_observation_journal(
        observation_kind, rig_id, valid_through_unix_ms, valid_from_unix_ms, stale_after_unix_ms,
        observed_at_unix_ms DESC, record_id);
CREATE INDEX ix_environment_local_retention
    ON environmental_observation_journal(recorded_unix_ms, valid_through_unix_ms, record_id);
CREATE INDEX ix_environment_local_page
    ON environmental_observation_journal(observation_kind, observed_at_unix_ms DESC, record_id DESC);
CREATE INDEX ix_environment_local_lineage_reference
    ON environmental_observation_local_lineage(referenced_record_id)
    WHERE referenced_record_id IS NOT NULL;
CREATE INDEX ix_environment_local_lineage_identity
    ON environmental_observation_local_lineage(source_identity_sha256, observation_id)
    WHERE referenced_record_id IS NULL;
CREATE INDEX ix_environment_attempt_source
    ON environmental_acquisition_attempts(source_id, completed_unix_ms DESC, attempt_id DESC);
CREATE INDEX ix_environment_attempt_capture
    ON environmental_acquisition_attempts(capture_id, source_id, attempt_id) WHERE capture_id IS NOT NULL;
CREATE INDEX ix_environment_on_demand_lease
    ON environmental_on_demand_commands(status, lease_expires_unix_ms, idempotency_key);
CREATE INDEX ix_environment_association_capture
    ON environmental_capture_associations(capture_id, observation_kind, rig_id, policy_identity_sha256);
CREATE INDEX ix_environment_association_evidence_record
    ON environmental_capture_association_evidence(local_record_id, association_id);
CREATE INDEX ix_environment_delivery_local_record
    ON environmental_observation_outbox(local_record_id) WHERE local_record_id IS NOT NULL;
CREATE INDEX ix_environment_projection_waiting
    ON environmental_observation_central_projection(status, updated_unix_ms, local_record_id);
PRAGMA user_version = 3;
