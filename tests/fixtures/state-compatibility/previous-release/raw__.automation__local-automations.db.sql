CREATE TABLE automation_definitions (
    definition_id TEXT PRIMARY KEY CHECK (length(definition_id) BETWEEN 1 AND 64),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 96),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    task_kind TEXT NOT NULL CHECK (length(task_kind) BETWEEN 1 AND 64),
    task_target TEXT NOT NULL CHECK (length(task_target) BETWEEN 1 AND 128),
    trigger_kind TEXT NOT NULL CHECK (length(trigger_kind) BETWEEN 1 AND 64),
    trigger_interval INTEGER NOT NULL CHECK (trigger_interval > 0),
    trigger_epoch_unix_ms INTEGER NOT NULL,
    version INTEGER NOT NULL CHECK (version > 0),
    revision_sha256 TEXT NOT NULL CHECK (length(revision_sha256) = 64),
    updated_unix_ms INTEGER NOT NULL,
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 256),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
    source_window_json TEXT
) STRICT;
CREATE TABLE automation_definition_revisions (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    definition_id TEXT NOT NULL CHECK (length(definition_id) BETWEEN 1 AND 64),
    version INTEGER NOT NULL CHECK (version > 0),
    revision_sha256 TEXT NOT NULL CHECK (length(revision_sha256) = 64),
    definition_json TEXT NOT NULL,
    removed INTEGER NOT NULL CHECK (removed IN (0, 1)),
    recorded_unix_ms INTEGER NOT NULL,
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 256),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512)
) STRICT;
CREATE TABLE automation_commands (
    idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    command_kind TEXT NOT NULL CHECK (length(command_kind) BETWEEN 1 AND 32),
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    definition_id TEXT NOT NULL CHECK (length(definition_id) BETWEEN 1 AND 64),
    recorded_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE automation_runs (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    definition_id TEXT NOT NULL CHECK (length(definition_id) BETWEEN 1 AND 64),
    run_key TEXT NOT NULL UNIQUE CHECK (length(run_key) BETWEEN 1 AND 128),
    revision_sha256 TEXT NOT NULL CHECK (length(revision_sha256) = 64),
    trigger_kind TEXT NOT NULL CHECK (length(trigger_kind) BETWEEN 1 AND 64),
    scheduled_for_unix_ms INTEGER NOT NULL,
    started_unix_ms INTEGER,
    completed_unix_ms INTEGER,
    outcome TEXT NOT NULL CHECK (length(outcome) BETWEEN 1 AND 32),
    detail TEXT NOT NULL CHECK (length(detail) <= 512),
    observed_capture_sequence INTEGER,
    claimant TEXT NOT NULL CHECK (length(claimant) BETWEEN 1 AND 64),
    occurrence_json TEXT,
    occurrence_id TEXT CHECK (occurrence_id IS NULL OR length(occurrence_id) = 64),
    attempt INTEGER NOT NULL DEFAULT 1 CHECK (attempt BETWEEN 1 AND 3),
    preparation_json TEXT
) STRICT;
CREATE TABLE automation_progress (
    definition_id TEXT PRIMARY KEY CHECK (length(definition_id) BETWEEN 1 AND 64),
    last_occurrence_unix_ms INTEGER,
    last_occurrence_utc_ticks INTEGER,
    last_capture_sequence INTEGER,
    updated_unix_ms INTEGER NOT NULL
) STRICT;
CREATE UNIQUE INDEX ux_automation_revisions_version
    ON automation_definition_revisions(definition_id, version);
CREATE INDEX ix_automation_commands_recorded
    ON automation_commands(recorded_unix_ms);
CREATE INDEX ix_automation_runs_definition
    ON automation_runs(definition_id, sequence);
PRAGMA user_version = 3;
