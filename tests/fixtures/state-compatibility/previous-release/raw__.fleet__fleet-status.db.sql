CREATE TABLE fleet_status_metadata(
    metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1),
    schema_version INTEGER NOT NULL,
    agent_instance_id TEXT NULL,
    next_sequence INTEGER NOT NULL CHECK(next_sequence > 0),
    overflow_count INTEGER NOT NULL DEFAULT 0 CHECK(overflow_count >= 0));
CREATE TABLE fleet_status_records(
    record_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    agent_instance_id TEXT NOT NULL,
    boot_session_id TEXT NOT NULL,
    sequence INTEGER NOT NULL UNIQUE CHECK(sequence > 0),
    observed_unix_ms INTEGER NOT NULL,
    payload BLOB NOT NULL,
    payload_sha256 TEXT NOT NULL,
    state_fingerprint TEXT NOT NULL,
    is_transition INTEGER NOT NULL CHECK(is_transition IN (0,1)),
    status TEXT NOT NULL CHECK(status IN ('pending','leased','retry','quarantined')),
    attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
    next_attempt_unix_ms INTEGER NOT NULL,
    lease_owner TEXT NULL,
    lease_token TEXT NULL,
    lease_expires_unix_ms INTEGER NULL,
    last_reason TEXT NULL,
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL);
INSERT INTO "fleet_status_metadata" VALUES(1, 1, NULL, 1, 0);
CREATE INDEX ix_fleet_status_claim
    ON fleet_status_records(status, next_attempt_unix_ms, sequence);
CREATE INDEX ix_fleet_status_lease
    ON fleet_status_records(status, lease_expires_unix_ms, sequence);
PRAGMA user_version = 0;
