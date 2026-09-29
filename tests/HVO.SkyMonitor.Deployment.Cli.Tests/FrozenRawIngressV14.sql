-- Frozen v13 journal schema with the v14 named-profile migration DDL.
-- Extracted from the candidate schema; the runtime fingerprint is checked in CameraAgent.Tests.
CREATE TABLE calibration_acquisition_jobs (
    job_id TEXT PRIMARY KEY CHECK (length(job_id) BETWEEN 1 AND 128),
    camera_key TEXT NOT NULL CHECK (length(camera_key) BETWEEN 1 AND 256),
    idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    plan_json BLOB NOT NULL CHECK (length(plan_json) BETWEEN 1 AND 1048576),
    plan_sha256 TEXT NOT NULL CHECK (length(plan_sha256) = 64),
    state TEXT NOT NULL CHECK (state IN ('planned', 'acquiring', 'building', 'publishing', 'published', 'failed', 'cancelled')),
    phase TEXT NOT NULL CHECK (length(phase) BETWEEN 1 AND 64),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    bundle_id TEXT,
    failure_reason TEXT,
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER,
    FOREIGN KEY (bundle_id) REFERENCES calibration_library_bundles(bundle_id)
) STRICT;
CREATE TABLE calibration_library_activations (
    activation_id INTEGER PRIMARY KEY,
    idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    from_bundle_id TEXT,
    to_bundle_id TEXT NOT NULL,
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
    state_version INTEGER NOT NULL CHECK (state_version > 0),
    activated_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (from_bundle_id) REFERENCES calibration_library_bundles(bundle_id),
    FOREIGN KEY (to_bundle_id) REFERENCES calibration_library_bundles(bundle_id)
) STRICT;
CREATE TABLE calibration_library_artifacts (
    bundle_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    artifact_id TEXT NOT NULL UNIQUE,
    reference_kind TEXT NOT NULL CHECK (reference_kind IN ('bias', 'dark', 'flat', 'defect')),
    role TEXT NOT NULL CHECK (role IN ('source', 'master')),
    source_index INTEGER CHECK (source_index IS NULL OR source_index BETWEEN 0 AND 2),
    manifest_relative_path TEXT COLLATE NOCASE NOT NULL UNIQUE,
    manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
    payload_relative_path TEXT COLLATE NOCASE NOT NULL UNIQUE,
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    ordered_source_artifact_ids_json BLOB NOT NULL,
    master_recipe_json BLOB,
    PRIMARY KEY (bundle_id, ordinal),
    CHECK ((role = 'source' AND source_index IS NOT NULL AND master_recipe_json IS NULL) OR
           (role = 'master' AND source_index IS NULL)),
    FOREIGN KEY (bundle_id) REFERENCES calibration_library_bundles(bundle_id) ON DELETE CASCADE
) STRICT;
CREATE TABLE calibration_library_bundles (
    bundle_id TEXT PRIMARY KEY CHECK (length(bundle_id) BETWEEN 1 AND 128),
    bundle_identity_sha256 TEXT NOT NULL UNIQUE CHECK (length(bundle_identity_sha256) = 64),
    source TEXT NOT NULL CHECK (source IN ('synthetic-references-v1', 'virtual-acquisition-v1')),
    bundle_json BLOB NOT NULL CHECK (length(bundle_json) BETWEEN 1 AND 1048576),
    profile_relative_path TEXT COLLATE NOCASE NOT NULL UNIQUE,
    profile_identity_sha256 TEXT NOT NULL CHECK (length(profile_identity_sha256) = 64),
    acquisition_model_identity_sha256 TEXT NOT NULL CHECK (length(acquisition_model_identity_sha256) = 64),
    agent_id TEXT NOT NULL CHECK (length(agent_id) BETWEEN 1 AND 128),
    rig_id TEXT NOT NULL CHECK (length(rig_id) BETWEEN 1 AND 128),
    rig_profile_sha256 TEXT NOT NULL CHECK (length(rig_profile_sha256) = 64),
    sensor_profile_sha256 TEXT NOT NULL CHECK (length(sensor_profile_sha256) = 64),
    input_layout_sha256 TEXT NOT NULL CHECK (length(input_layout_sha256) = 64),
    output_layout_sha256 TEXT NOT NULL CHECK (length(output_layout_sha256) = 64),
    minimum_gain REAL NOT NULL,
    maximum_gain REAL NOT NULL CHECK (maximum_gain >= minimum_gain),
    minimum_offset REAL,
    maximum_offset REAL,
    minimum_light_exposure_ticks INTEGER,
    maximum_light_exposure_ticks INTEGER,
    minimum_temperature_c REAL,
    maximum_temperature_c REAL,
    effective_from_unix_ms INTEGER NOT NULL,
    effective_until_unix_ms INTEGER,
    publication_state TEXT NOT NULL CHECK (publication_state IN ('published', 'incomplete', 'corrupt', 'quarantined')),
    retention_hold INTEGER NOT NULL DEFAULT 1 CHECK (retention_hold IN (0, 1)),
    failure_reason TEXT,
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    CHECK ((minimum_offset IS NULL AND maximum_offset IS NULL) OR
           (minimum_offset IS NOT NULL AND maximum_offset IS NOT NULL AND maximum_offset >= minimum_offset)),
    CHECK ((minimum_light_exposure_ticks IS NULL AND maximum_light_exposure_ticks IS NULL) OR
           (minimum_light_exposure_ticks > 0 AND maximum_light_exposure_ticks >= minimum_light_exposure_ticks)),
    CHECK ((minimum_temperature_c IS NULL AND maximum_temperature_c IS NULL) OR
           (minimum_temperature_c IS NOT NULL AND maximum_temperature_c IS NOT NULL AND maximum_temperature_c >= minimum_temperature_c)),
    CHECK (effective_until_unix_ms IS NULL OR effective_until_unix_ms > effective_from_unix_ms)
) STRICT;
CREATE TABLE calibration_library_commands (
    idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    command_kind TEXT NOT NULL CHECK (command_kind IN ('acquire', 'cancel', 'activate', 'rollback')),
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    result_bundle_id TEXT,
    result_job_id TEXT,
    result_state_version INTEGER CHECK (result_state_version IS NULL OR result_state_version >= 0),
    result_json BLOB NOT NULL CHECK (length(result_json) BETWEEN 1 AND 1048576),
    created_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER,
    FOREIGN KEY (result_bundle_id) REFERENCES calibration_library_bundles(bundle_id),
    FOREIGN KEY (result_job_id) REFERENCES calibration_acquisition_jobs(job_id)
) STRICT;
CREATE TABLE calibration_library_reconciliation (
    reconciliation_id INTEGER PRIMARY KEY,
    evidence_key TEXT NOT NULL UNIQUE CHECK (length(evidence_key) = 64),
    source_relative_path TEXT NOT NULL,
    quarantine_relative_path TEXT,
    outcome TEXT NOT NULL CHECK (outcome IN ('adopted', 'failed', 'quarantined')),
    reason TEXT NOT NULL,
    operation_state TEXT NOT NULL CHECK (operation_state IN ('planned', 'completed')),
    observed_bytes INTEGER NOT NULL DEFAULT 0 CHECK (observed_bytes >= 0),
    observed_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER
) STRICT;
CREATE TABLE calibration_library_state (
    state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
    active_bundle_id TEXT,
    version INTEGER NOT NULL CHECK (version >= 0),
    last_selection_reason TEXT,
    last_selection_unix_ms INTEGER,
    last_reconciliation_reason TEXT,
    last_reconciliation_unix_ms INTEGER,
    updated_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (active_bundle_id) REFERENCES calibration_library_bundles(bundle_id)
) STRICT;
CREATE TABLE capture_control_commands (
    idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    target_state TEXT NOT NULL CHECK (target_state IN ('running', 'paused')),
    expected_version INTEGER CHECK (expected_version IS NULL OR expected_version >= 0),
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    status TEXT NOT NULL CHECK (status IN ('pending', 'completed')),
    result_state TEXT CHECK (result_state IS NULL OR result_state IN ('running', 'paused')),
    result_version INTEGER CHECK (result_version IS NULL OR result_version >= 0),
    changed INTEGER NOT NULL CHECK (changed IN (0, 1)),
    requested_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER
) STRICT;
CREATE TABLE capture_control_state (
    state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
    state TEXT NOT NULL CHECK (state IN ('running', 'pause_requested', 'paused')),
    version INTEGER NOT NULL CHECK (version >= 0),
    updated_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE capture_lane_contexts (
    raw_capture_row_id INTEGER PRIMARY KEY,
    context_json BLOB NOT NULL,
    context_sha256 TEXT NOT NULL CHECK (length(context_sha256) = 64),
    context_source TEXT NOT NULL CHECK (context_source IN ('capture', 'manifest-fallback')),
    FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE
) STRICT;
CREATE TABLE capture_lane_definitions (
    lane_name TEXT PRIMARY KEY,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    required INTEGER NOT NULL CHECK (required IN (0, 1)),
    ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
    policy_sha256 TEXT NOT NULL CHECK (length(policy_sha256) = 64),
    pressure_state INTEGER NOT NULL DEFAULT 0 CHECK (pressure_state IN (0, 1, 2)),
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE capture_lane_work (
    work_id INTEGER PRIMARY KEY,
     raw_capture_row_id INTEGER NOT NULL,
     lane_name TEXT NOT NULL,
     agent_id TEXT NOT NULL,
     capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
    required INTEGER NOT NULL CHECK (required IN (0, 1)),
    ordered INTEGER NOT NULL CHECK (ordered IN (0, 1)),
    state TEXT NOT NULL CHECK (state IN ('pending', 'leased', 'retry_wait', 'completed', 'quarantined', 'abandoned')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    available_unix_ms INTEGER NOT NULL,
    lease_token TEXT,
    lease_owner TEXT,
    lease_expires_unix_ms INTEGER,
    completion_token TEXT,
    completed_unix_ms INTEGER,
    failure_reason TEXT,
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    UNIQUE (raw_capture_row_id, lane_name),
    FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE,
    FOREIGN KEY (lane_name) REFERENCES capture_lane_definitions(lane_name)
) STRICT;
CREATE TABLE capture_schedule_activations (
    activation_id INTEGER PRIMARY KEY,
    idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    from_revision_id TEXT,
    to_revision_id TEXT NOT NULL,
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
    state_version INTEGER NOT NULL CHECK (state_version > 0),
    activated_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (from_revision_id) REFERENCES capture_schedule_revisions(revision_id),
    FOREIGN KEY (to_revision_id) REFERENCES capture_schedule_revisions(revision_id)
) STRICT;
CREATE TABLE capture_schedule_admissions (
    admission_id TEXT PRIMARY KEY CHECK (length(admission_id) BETWEEN 1 AND 128),
    revision_id TEXT NOT NULL,
    override_id TEXT,
    decision_unix_ms INTEGER NOT NULL,
    created_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (revision_id) REFERENCES capture_schedule_revisions(revision_id),
    FOREIGN KEY (override_id) REFERENCES capture_schedule_overrides(override_id)
) STRICT;
CREATE TABLE capture_schedule_commands (
    idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    command_kind TEXT NOT NULL CHECK (length(command_kind) BETWEEN 1 AND 32),
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    result_active_revision_id TEXT NOT NULL,
    result_pending_revision_id TEXT,
    result_state_version INTEGER CHECK (result_state_version IS NULL OR result_state_version >= 0),
    result_last_evaluated_unix_ms INTEGER,
    created_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER,
    FOREIGN KEY (result_active_revision_id) REFERENCES capture_schedule_revisions(revision_id),
    FOREIGN KEY (result_pending_revision_id) REFERENCES capture_schedule_revisions(revision_id)
) STRICT;
CREATE TABLE capture_schedule_expansions (
    expansion_key TEXT PRIMARY KEY CHECK (length(expansion_key) = 64),
    expansion_sha256 TEXT NOT NULL CHECK (length(expansion_sha256) = 64),
    revision_id TEXT NOT NULL,
    deployment_location_id TEXT NOT NULL CHECK (length(deployment_location_id) BETWEEN 1 AND 128),
    deployment_location_version INTEGER NOT NULL CHECK (deployment_location_version > 0),
    preview_start_unix_ms INTEGER NOT NULL,
    preview_end_unix_ms INTEGER NOT NULL CHECK (preview_end_unix_ms > preview_start_unix_ms),
    expansion_algorithm_version TEXT NOT NULL,
    time_zone_rule_sha256 TEXT NOT NULL CHECK (length(time_zone_rule_sha256) = 64),
    solar_algorithm_version TEXT NOT NULL,
    created_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (revision_id) REFERENCES capture_schedule_revisions(revision_id)
) STRICT;
CREATE TABLE capture_schedule_intervals (
    expansion_key TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    interval_id TEXT NOT NULL,
    source TEXT NOT NULL,
    disposition TEXT NOT NULL CHECK (disposition IN ('open', 'closed')),
    start_unix_ms INTEGER NOT NULL,
    end_unix_ms INTEGER NOT NULL CHECK (end_unix_ms > start_unix_ms),
    local_date TEXT,
    setpoint_profile_id TEXT,
    solar_algorithm_version TEXT,
    PRIMARY KEY (expansion_key, ordinal),
    FOREIGN KEY (expansion_key) REFERENCES capture_schedule_expansions(expansion_key) ON DELETE CASCADE
) STRICT;
CREATE TABLE capture_schedule_override_events (
    event_id TEXT PRIMARY KEY CHECK (length(event_id) BETWEEN 1 AND 128),
    override_id TEXT NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('created', 'consumed', 'cleared')),
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
    occurred_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (override_id) REFERENCES capture_schedule_overrides(override_id)
) STRICT;
CREATE TABLE capture_schedule_overrides (
    override_id TEXT PRIMARY KEY CHECK (length(override_id) BETWEEN 1 AND 128),
    schedule_revision_id TEXT NOT NULL,
    mode TEXT NOT NULL CHECK (mode IN ('force_closed', 'force_open')),
    start_unix_ms INTEGER NOT NULL,
    end_unix_ms INTEGER NOT NULL CHECK (end_unix_ms > start_unix_ms),
    setpoint_profile_id TEXT,
    one_shot INTEGER NOT NULL CHECK (one_shot IN (0, 1)),
    consumed_unix_ms INTEGER,
    cleared_unix_ms INTEGER,
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
    created_unix_ms INTEGER NOT NULL,
    CHECK ((mode = 'force_open' AND setpoint_profile_id IS NOT NULL) OR
           (mode = 'force_closed' AND setpoint_profile_id IS NULL)),
    CHECK (one_shot = 0 OR mode = 'force_open'),
    FOREIGN KEY (schedule_revision_id) REFERENCES capture_schedule_revisions(revision_id)
) STRICT;
CREATE TABLE capture_schedule_revisions (
    revision_id TEXT PRIMARY KEY CHECK (length(revision_id) BETWEEN 1 AND 128),
    revision_number INTEGER NOT NULL UNIQUE CHECK (revision_number > 0),
    profile_json BLOB NOT NULL,
    profile_sha256 TEXT NOT NULL CHECK (length(profile_sha256) = 64),
    schedule_sha256 TEXT NOT NULL CHECK (length(schedule_sha256) = 64),
    source TEXT NOT NULL CHECK (length(source) BETWEEN 1 AND 32),
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
    created_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE capture_schedule_state (
    state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
    active_revision_id TEXT NOT NULL,
    pending_revision_id TEXT,
    version INTEGER NOT NULL CHECK (version >= 0),
    last_evaluated_unix_ms INTEGER,
    last_decision_unix_ms INTEGER,
    last_decision_admitted INTEGER CHECK (last_decision_admitted IS NULL OR last_decision_admitted IN (0, 1)),
    last_decision_reason TEXT,
    last_decision_profile_id TEXT,
    last_decision_interval_id TEXT,
    next_transition_unix_ms INTEGER,
    updated_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (active_revision_id) REFERENCES capture_schedule_revisions(revision_id),
    FOREIGN KEY (pending_revision_id) REFERENCES capture_schedule_revisions(revision_id)
) STRICT;
CREATE TABLE capture_schedule_unavailable (
    expansion_key TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    window_id TEXT NOT NULL,
    source TEXT NOT NULL,
    local_date TEXT NOT NULL,
    start_unix_ms INTEGER NOT NULL,
    end_unix_ms INTEGER NOT NULL CHECK (end_unix_ms > start_unix_ms),
    reason_code TEXT NOT NULL,
    solar_algorithm_version TEXT NOT NULL,
    PRIMARY KEY (expansion_key, ordinal),
    FOREIGN KEY (expansion_key) REFERENCES capture_schedule_expansions(expansion_key) ON DELETE CASCADE
) STRICT;
CREATE TABLE raw_capture_assignments (
    capture_id TEXT PRIMARY KEY,
    raw_artifact_id TEXT NOT NULL UNIQUE,
    agent_id TEXT NOT NULL,
    capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
    UNIQUE (agent_id, capture_sequence)
) STRICT;
CREATE TABLE raw_capture_sequences (
    agent_id TEXT PRIMARY KEY,
    last_sequence INTEGER NOT NULL CHECK (last_sequence >= 0)
) STRICT;
CREATE TABLE raw_capture_stage_events (
    raw_capture_row_id INTEGER NOT NULL,
    stage_key TEXT NOT NULL CHECK (length(stage_key) BETWEEN 1 AND 128),
    candidate_id TEXT NOT NULL DEFAULT '' CHECK (candidate_id = '' OR length(candidate_id) = 32),
    state TEXT NOT NULL CHECK (length(state) BETWEEN 1 AND 128),
    source TEXT NOT NULL CHECK (length(source) BETWEEN 1 AND 128),
    event_unix_ms INTEGER NOT NULL,
    PRIMARY KEY (raw_capture_row_id, candidate_id, stage_key),
    FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE
) STRICT;
CREATE TABLE raw_captures (
    raw_capture_row_id INTEGER PRIMARY KEY,
    capture_id TEXT NOT NULL UNIQUE,
    raw_artifact_id TEXT NOT NULL UNIQUE,
    agent_id TEXT NOT NULL,
    capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
    descriptor_sha256 TEXT NOT NULL UNIQUE CHECK (length(descriptor_sha256) = 64),
    manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    payload_length INTEGER NOT NULL CHECK (payload_length >= 0),
    payload_relative_path TEXT NOT NULL UNIQUE,
    sidecar_relative_path TEXT NOT NULL UNIQUE,
    manifest_json BLOB NOT NULL,
    exposure_started_unix_ms INTEGER NOT NULL,
    durable_ingress_unix_ms INTEGER NOT NULL,
    committed_unix_ms INTEGER NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('committed', 'missing_evidence', 'quarantined')),
    retention_hold INTEGER NOT NULL DEFAULT 1 CHECK (retention_hold IN (0, 1)),
    failure_reason TEXT,
    evidence_origin TEXT NOT NULL DEFAULT 'Unknown' CHECK (evidence_origin IN ('Unknown', 'Simulated', 'DeveloperFixture')),
    UNIQUE (agent_id, capture_sequence),
    FOREIGN KEY (capture_id) REFERENCES raw_capture_assignments(capture_id)
) STRICT;
CREATE TABLE raw_ingress_reconciliation (
    reconciliation_id INTEGER PRIMARY KEY,
    evidence_key TEXT NOT NULL UNIQUE,
    source_relative_path TEXT NOT NULL,
    companion_relative_path TEXT,
    quarantine_relative_path TEXT,
    outcome TEXT NOT NULL CHECK (outcome IN ('cleaned', 'quarantined')),
    reason TEXT NOT NULL,
    operation_state TEXT NOT NULL CHECK (operation_state IN ('planned', 'completed')),
    observed_bytes INTEGER NOT NULL DEFAULT 0,
    observed_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER
) STRICT;
CREATE TABLE transient_candidate_conflicts (
    conflict_id INTEGER PRIMARY KEY,
    candidate_id TEXT,
    event_id TEXT,
    reason TEXT NOT NULL,
    observed_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE transient_candidate_sources (
    candidate_id TEXT NOT NULL,
    source_ordinal INTEGER NOT NULL CHECK (source_ordinal >= 0),
    evidence_id TEXT NOT NULL,
    raw_capture_row_id INTEGER NOT NULL,
    source_schema TEXT NOT NULL,
    locator_schema TEXT NOT NULL,
    locator_kind INTEGER NOT NULL,
    artifact_id TEXT NOT NULL,
    artifact_role INTEGER NOT NULL,
    artifact_variant TEXT NOT NULL,
    recipe_identity_sha256 TEXT NOT NULL CHECK (length(recipe_identity_sha256) = 64),
    checksum_sha256 TEXT NOT NULL CHECK (length(checksum_sha256) = 64),
    observation_started_utc_ticks INTEGER NOT NULL,
    observation_ended_utc_ticks INTEGER NOT NULL,
    timing_quality INTEGER NOT NULL,
    timing_source TEXT NOT NULL,
    timing_version TEXT NOT NULL,
    PRIMARY KEY (candidate_id, source_ordinal),
    UNIQUE (candidate_id, evidence_id),
    FOREIGN KEY (candidate_id) REFERENCES transient_candidates(candidate_id) ON DELETE CASCADE,
    FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
) STRICT;
CREATE TABLE transient_candidates (
    candidate_id TEXT PRIMARY KEY,
    event_id TEXT NOT NULL,
    agent_id TEXT NOT NULL,
    mode TEXT NOT NULL CHECK (mode IN ('edge', 'hybrid')),
    required INTEGER NOT NULL CHECK (required IN (0, 1)),
    reservation_identity_sha256 TEXT NOT NULL CHECK (length(reservation_identity_sha256) = 64),
    state TEXT NOT NULL CHECK (state IN ('pending', 'provisional', 'validated', 'rejected', 'needs_review')),
    phase TEXT NOT NULL CHECK (phase IN ('reserved', 'candidate_persisted', 'finalized', 'handoff_pending', 'acknowledged', 'quarantined')),
    candidate_payload BLOB,
    candidate_payload_sha256 TEXT CHECK (candidate_payload_sha256 IS NULL OR length(candidate_payload_sha256) = 64),
    finalization_payload BLOB,
    finalization_receipt_identity_sha256 TEXT CHECK (finalization_receipt_identity_sha256 IS NULL OR length(finalization_receipt_identity_sha256) = 64),
    submission_payload BLOB,
    submission_identity_sha256 TEXT CHECK (submission_identity_sha256 IS NULL OR length(submission_identity_sha256) = 64),
    acknowledgement_payload BLOB,
    acknowledgement_payload_sha256 TEXT CHECK (acknowledgement_payload_sha256 IS NULL OR length(acknowledgement_payload_sha256) = 64),
    source_hold_released INTEGER NOT NULL DEFAULT 0 CHECK (source_hold_released IN (0, 1)),
    quarantine_reason TEXT,
    timeout_unix_ms INTEGER NOT NULL,
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    candidate_state TEXT CHECK (candidate_state IS NULL OR candidate_state IN ('PendingContext', 'Provisional', 'Complete', 'Rejected')),
    FOREIGN KEY (event_id) REFERENCES transient_event_identities(event_id)
) STRICT;
CREATE TABLE transient_capture_work (
    raw_capture_row_id INTEGER PRIMARY KEY,
    lane_work_id INTEGER NOT NULL UNIQUE,
    mode TEXT NOT NULL CHECK (mode IN ('edge', 'hybrid')),
    required INTEGER NOT NULL CHECK (required IN (0, 1)),
    state TEXT NOT NULL CHECK (state IN ('pending', 'candidate_persisted', 'completed', 'quarantined', 'abandoned')),
    artifact_id TEXT NOT NULL UNIQUE,
    manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id),
    FOREIGN KEY (lane_work_id) REFERENCES capture_lane_work(work_id)
) STRICT;
CREATE TABLE transient_event_identities (
    event_id TEXT PRIMARY KEY,
    agent_id TEXT NOT NULL,
    created_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE transient_runtime_operations (
    operation_id INTEGER PRIMARY KEY,
    idempotency_key TEXT NOT NULL UNIQUE CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    raw_capture_row_id INTEGER NOT NULL,
    lane_work_id INTEGER NOT NULL,
    outer_lane_work_id INTEGER NOT NULL,
    agent_id TEXT NOT NULL CHECK (length(agent_id) BETWEEN 1 AND 128),
    capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
    capture_id TEXT NOT NULL CHECK (length(capture_id) = 32),
    artifact_id TEXT NOT NULL CHECK (length(artifact_id) = 32),
    manifest_sha256 TEXT NOT NULL CHECK (length(manifest_sha256) = 64),
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    processing_profile_sha256 TEXT NOT NULL CHECK (length(processing_profile_sha256) = 64),
    mode TEXT NOT NULL CHECK (mode IN ('edge', 'hybrid')),
    required INTEGER NOT NULL CHECK (required IN (0, 1)),
    expected_outer_lane_state TEXT NOT NULL CHECK (expected_outer_lane_state = 'completed'),
    expected_work_state TEXT NOT NULL CHECK (expected_work_state = 'quarantined'),
    expected_frame_state TEXT NOT NULL CHECK (expected_frame_state = 'quarantined'),
    expected_failure_reason TEXT NOT NULL CHECK (length(expected_failure_reason) BETWEEN 1 AND 128),
    expected_outer_lane_updated_unix_ms INTEGER NOT NULL,
    expected_work_updated_unix_ms INTEGER NOT NULL,
    expected_frame_updated_unix_ms INTEGER NOT NULL,
    deployment_run_id TEXT NOT NULL CHECK (length(deployment_run_id) BETWEEN 1 AND 128),
    inventory_sha256 TEXT NOT NULL CHECK (
        length(inventory_sha256) = 64 AND inventory_sha256 NOT GLOB '*[^0-9A-F]*'),
    legacy_ownership_externally_established INTEGER NOT NULL
        CHECK (legacy_ownership_externally_established = 1),
    action TEXT NOT NULL CHECK (action = 'abandon'),
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    reason_code TEXT NOT NULL CHECK (length(reason_code) BETWEEN 1 AND 64),
    result_state TEXT NOT NULL CHECK (result_state = 'abandoned'),
    completed_unix_ms INTEGER NOT NULL,
    receipt_identity_sha256 TEXT NOT NULL CHECK (
        length(receipt_identity_sha256) = 64 AND receipt_identity_sha256 NOT GLOB '*[^0-9A-F]*'),
    FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id),
    FOREIGN KEY (lane_work_id) REFERENCES transient_capture_work(lane_work_id),
    FOREIGN KEY (outer_lane_work_id) REFERENCES capture_lane_work(work_id)
) STRICT;
CREATE TABLE transient_runtime_policy (
    policy_key INTEGER PRIMARY KEY CHECK (policy_key = 1),
    mode TEXT NOT NULL CHECK (mode IN ('off', 'edge', 'central', 'hybrid')),
    required INTEGER NOT NULL CHECK (required IN (0, 1)),
    candidate_timeout_minutes INTEGER NOT NULL CHECK (candidate_timeout_minutes > 0),
    updated_unix_ms INTEGER NOT NULL
) STRICT;
CREATE INDEX ix_calibration_acquisition_jobs_camera
    ON calibration_acquisition_jobs(camera_key, state, created_unix_ms, job_id);
CREATE INDEX ix_calibration_library_activations_history
    ON calibration_library_activations(activated_unix_ms DESC, activation_id DESC);
CREATE INDEX ix_calibration_library_artifacts_role
    ON calibration_library_artifacts(bundle_id, role, reference_kind, source_index, ordinal);
CREATE INDEX ix_calibration_library_bundles_created
    ON calibration_library_bundles(created_unix_ms DESC, bundle_id);
CREATE INDEX ix_calibration_library_bundles_selection
    ON calibration_library_bundles(
        publication_state, agent_id, rig_id, rig_profile_sha256, sensor_profile_sha256,
        effective_from_unix_ms, effective_until_unix_ms, bundle_id);
CREATE INDEX ix_calibration_library_reconciliation_state
    ON calibration_library_reconciliation(operation_state, observed_unix_ms, reconciliation_id);
CREATE INDEX ix_capture_lane_work_backlog
    ON capture_lane_work(lane_name, state, created_unix_ms);
CREATE INDEX ix_capture_lane_work_claim
    ON capture_lane_work(lane_name, state, available_unix_ms, work_id);
CREATE INDEX ix_capture_lane_work_lease
    ON capture_lane_work(state, lease_expires_unix_ms);
CREATE INDEX ix_capture_lane_work_ordered
    ON capture_lane_work(lane_name, agent_id, capture_sequence)
    WHERE state NOT IN ('completed', 'abandoned');
CREATE INDEX ix_capture_lane_work_raw
    ON capture_lane_work(raw_capture_row_id, required, state);
CREATE INDEX ix_capture_schedule_expansions_lookup
    ON capture_schedule_expansions(
        revision_id, deployment_location_id, deployment_location_version,
        preview_start_unix_ms, preview_end_unix_ms);
CREATE INDEX ix_capture_schedule_intervals_bounds
    ON capture_schedule_intervals(expansion_key, start_unix_ms, end_unix_ms, ordinal);
CREATE INDEX ix_capture_schedule_overrides_active
    ON capture_schedule_overrides(cleared_unix_ms, end_unix_ms, mode, override_id);
CREATE INDEX ix_capture_schedule_revisions_created
    ON capture_schedule_revisions(created_unix_ms, revision_number);
CREATE INDEX ix_raw_captures_backlog ON raw_captures(state, durable_ingress_unix_ms);
CREATE INDEX ix_raw_captures_discovery ON raw_captures(state, agent_id, capture_sequence);
CREATE INDEX ix_raw_captures_gallery_origin
    ON raw_captures(evidence_origin, capture_sequence DESC, raw_capture_row_id DESC);
CREATE INDEX ix_raw_captures_gallery_sequence
    ON raw_captures(capture_sequence DESC, raw_capture_row_id DESC);
CREATE INDEX ix_raw_captures_gallery_state
    ON raw_captures(state, capture_sequence DESC, raw_capture_row_id DESC);
CREATE INDEX ix_raw_captures_gallery_time
    ON raw_captures(exposure_started_unix_ms DESC, capture_sequence DESC, raw_capture_row_id DESC);
CREATE INDEX ix_raw_captures_retention ON raw_captures(retention_hold, exposure_started_unix_ms);
CREATE INDEX ix_transient_candidate_conflicts_candidate
    ON transient_candidate_conflicts(candidate_id, conflict_id);
CREATE INDEX ix_transient_candidate_conflicts_observed
    ON transient_candidate_conflicts(observed_unix_ms, conflict_id);
CREATE INDEX ix_transient_candidate_sources_raw
    ON transient_candidate_sources(raw_capture_row_id, candidate_id);
CREATE INDEX ix_transient_candidates_backlog
    ON transient_candidates(state, created_unix_ms, candidate_id);
CREATE INDEX ix_transient_candidates_operator
    ON transient_candidates(created_unix_ms DESC, candidate_id DESC);
CREATE INDEX ix_transient_capture_work_backlog
    ON transient_capture_work(state, created_unix_ms, raw_capture_row_id);
CREATE INDEX ix_transient_runtime_operations_target
    ON transient_runtime_operations(raw_capture_row_id, operation_id DESC);
CREATE UNIQUE INDEX ux_calibration_acquisition_jobs_camera_nonterminal
    ON calibration_acquisition_jobs(camera_key)
    WHERE state NOT IN ('published', 'failed', 'cancelled');

CREATE TABLE named_rig_profiles (
    profile_id TEXT PRIMARY KEY CHECK (length(profile_id) BETWEEN 1 AND 128),
    display_name TEXT COLLATE NOCASE NOT NULL UNIQUE CHECK (length(display_name) BETWEEN 1 AND 128),
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE named_equipment_definitions (
    definition_id TEXT PRIMARY KEY CHECK (length(definition_id) BETWEEN 1 AND 128),
    kind TEXT NOT NULL CHECK (kind IN ('camera', 'optics', 'mount')),
    display_name TEXT COLLATE NOCASE NOT NULL CHECK (length(display_name) BETWEEN 1 AND 128),
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    UNIQUE (kind, display_name),
    UNIQUE (definition_id, kind)
) STRICT;
CREATE TABLE named_equipment_revisions (
    revision_id TEXT PRIMARY KEY CHECK (length(revision_id) BETWEEN 1 AND 128),
    definition_id TEXT NOT NULL,
    revision_number INTEGER NOT NULL CHECK (revision_number > 0),
    definition_json BLOB NOT NULL CHECK (length(definition_json) BETWEEN 1 AND 1048576),
    definition_sha256 TEXT NOT NULL CHECK (length(definition_sha256) = 64),
    created_unix_ms INTEGER NOT NULL,
    UNIQUE (definition_id, revision_number),
    FOREIGN KEY (definition_id) REFERENCES named_equipment_definitions(definition_id)
) STRICT;
CREATE TRIGGER tr_named_equipment_revisions_immutable
BEFORE UPDATE ON named_equipment_revisions
BEGIN SELECT RAISE(ABORT, 'equipment revisions are immutable'); END;
CREATE TRIGGER tr_named_equipment_revisions_no_delete
BEFORE DELETE ON named_equipment_revisions
BEGIN SELECT RAISE(ABORT, 'equipment revisions are immutable'); END;
CREATE TABLE named_rig_revisions (
    revision_id TEXT PRIMARY KEY CHECK (length(revision_id) BETWEEN 1 AND 128),
    profile_id TEXT NOT NULL,
    revision_number INTEGER NOT NULL CHECK (revision_number > 0),
    camera_revision_id TEXT NOT NULL,
    optics_revision_id TEXT NOT NULL,
    mount_revision_id TEXT NOT NULL,
    rig_json BLOB NOT NULL CHECK (length(rig_json) BETWEEN 1 AND 1048576),
    rig_sha256 TEXT NOT NULL CHECK (length(rig_sha256) = 64),
    source_schedule_revision_id TEXT,
    created_unix_ms INTEGER NOT NULL,
    UNIQUE (profile_id, revision_number),
    FOREIGN KEY (profile_id) REFERENCES named_rig_profiles(profile_id),
    FOREIGN KEY (camera_revision_id) REFERENCES named_equipment_revisions(revision_id),
    FOREIGN KEY (optics_revision_id) REFERENCES named_equipment_revisions(revision_id),
    FOREIGN KEY (mount_revision_id) REFERENCES named_equipment_revisions(revision_id),
    FOREIGN KEY (source_schedule_revision_id) REFERENCES capture_schedule_revisions(revision_id)
) STRICT;
CREATE TRIGGER tr_named_rig_revisions_immutable
BEFORE UPDATE ON named_rig_revisions
BEGIN SELECT RAISE(ABORT, 'rig revisions are immutable'); END;
CREATE TRIGGER tr_named_rig_revisions_no_delete
BEFORE DELETE ON named_rig_revisions
BEGIN SELECT RAISE(ABORT, 'rig revisions are immutable'); END;
CREATE TABLE named_rig_selection (
    state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
    active_revision_id TEXT,
    pending_revision_id TEXT,
    pending_command_key TEXT CHECK (pending_command_key IS NULL OR length(pending_command_key) BETWEEN 1 AND 128),
    pending_schedule_revision_id TEXT,
    pending_schedule_version INTEGER CHECK (pending_schedule_version >= 0),
    version INTEGER NOT NULL CHECK (version >= 0),
    updated_unix_ms INTEGER NOT NULL,
    CHECK ((pending_revision_id IS NULL AND pending_command_key IS NULL AND pending_schedule_revision_id IS NULL AND pending_schedule_version IS NULL)
        OR (pending_revision_id IS NOT NULL AND pending_command_key IS NOT NULL AND pending_schedule_revision_id IS NOT NULL AND pending_schedule_version IS NOT NULL)),
    FOREIGN KEY (active_revision_id) REFERENCES named_rig_revisions(revision_id),
    FOREIGN KEY (pending_revision_id) REFERENCES named_rig_revisions(revision_id),
    FOREIGN KEY (pending_schedule_revision_id) REFERENCES capture_schedule_revisions(revision_id)
) STRICT;
INSERT INTO named_rig_selection(state_key, active_revision_id, pending_revision_id, version, updated_unix_ms)
VALUES (1, NULL, NULL, 0, unixepoch('subsec') * 1000);
CREATE TABLE named_rig_selection_commands (
    idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    command_kind TEXT NOT NULL CHECK (command_kind IN ('stage', 'activate', 'cancel', 'rollback')),
    expected_version INTEGER NOT NULL CHECK (expected_version >= 0),
    target_revision_id TEXT,
    actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
    request_sha256 TEXT NOT NULL CHECK (length(request_sha256) = 64),
    acknowledged_unvalidated INTEGER NOT NULL CHECK (acknowledged_unvalidated IN (0, 1)),
    result_json BLOB NOT NULL CHECK (length(result_json) BETWEEN 1 AND 1048576),
    created_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER,
    FOREIGN KEY (target_revision_id) REFERENCES named_rig_revisions(revision_id)
) STRICT;
CREATE TABLE named_rig_selection_receipts (
    receipt_id TEXT PRIMARY KEY CHECK (length(receipt_id) BETWEEN 1 AND 128),
    idempotency_key TEXT NOT NULL UNIQUE,
    from_revision_id TEXT,
    to_revision_id TEXT,
    disposition TEXT NOT NULL CHECK (disposition IN ('staged', 'active', 'restart_required', 'cancelled', 'rolled_back')),
    state_version INTEGER NOT NULL CHECK (state_version > 0),
    created_unix_ms INTEGER NOT NULL,
    stage_command_key TEXT,
    FOREIGN KEY (idempotency_key) REFERENCES named_rig_selection_commands(idempotency_key),
    FOREIGN KEY (stage_command_key) REFERENCES named_rig_selection_commands(idempotency_key),
    FOREIGN KEY (from_revision_id) REFERENCES named_rig_revisions(revision_id),
    FOREIGN KEY (to_revision_id) REFERENCES named_rig_revisions(revision_id)
) STRICT;
PRAGMA user_version = 14;
