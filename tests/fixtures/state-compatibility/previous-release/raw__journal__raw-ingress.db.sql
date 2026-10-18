CREATE TABLE raw_capture_sequences (
    agent_id TEXT PRIMARY KEY,
    last_sequence INTEGER NOT NULL CHECK (last_sequence >= 0)
) STRICT;
CREATE TABLE raw_capture_assignments (
    capture_id TEXT PRIMARY KEY,
    raw_artifact_id TEXT NOT NULL UNIQUE,
    agent_id TEXT NOT NULL,
    capture_sequence INTEGER NOT NULL CHECK (capture_sequence > 0),
    UNIQUE (agent_id, capture_sequence)
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
CREATE TABLE capture_lane_contexts (
    raw_capture_row_id INTEGER PRIMARY KEY,
    context_json BLOB NOT NULL,
    context_sha256 TEXT NOT NULL CHECK (length(context_sha256) = 64),
    context_source TEXT NOT NULL CHECK (context_source IN ('capture', 'manifest-fallback')),
    FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id) ON DELETE CASCADE
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
CREATE TABLE capture_control_state (
    state_key INTEGER PRIMARY KEY CHECK (state_key = 1),
    state TEXT NOT NULL CHECK (state IN ('running', 'pause_requested', 'paused')),
    version INTEGER NOT NULL CHECK (version >= 0),
    updated_unix_ms INTEGER NOT NULL
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
CREATE TABLE transient_runtime_policy (
    policy_key INTEGER PRIMARY KEY CHECK (policy_key = 1),
    mode TEXT NOT NULL CHECK (mode IN ('off', 'edge', 'central', 'hybrid')),
    required INTEGER NOT NULL CHECK (required IN (0, 1)),
    candidate_timeout_minutes INTEGER NOT NULL CHECK (candidate_timeout_minutes > 0),
    updated_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE transient_event_identities (
    event_id TEXT PRIMARY KEY,
    agent_id TEXT NOT NULL,
    created_unix_ms INTEGER NOT NULL
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
CREATE TABLE transient_candidate_conflicts (
    conflict_id INTEGER PRIMARY KEY,
    candidate_id TEXT,
    event_id TEXT,
    reason TEXT NOT NULL,
    observed_unix_ms INTEGER NOT NULL
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
CREATE TABLE capture_schedule_admissions (
    admission_id TEXT PRIMARY KEY CHECK (length(admission_id) BETWEEN 1 AND 128),
    revision_id TEXT NOT NULL,
    override_id TEXT,
    decision_unix_ms INTEGER NOT NULL,
    created_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (revision_id) REFERENCES capture_schedule_revisions(revision_id),
    FOREIGN KEY (override_id) REFERENCES capture_schedule_overrides(override_id)
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
CREATE TABLE capture_processing_schema(
    schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
    version INTEGER NOT NULL CHECK(version = 8)
) STRICT;
CREATE TABLE processing_graph_revisions(
    revision_id TEXT PRIMARY KEY CHECK(length(revision_id) = 64),
    graph_name TEXT NOT NULL CHECK(length(graph_name) BETWEEN 1 AND 128),
    revision_name TEXT NOT NULL CHECK(length(revision_name) BETWEEN 1 AND 128),
    lifecycle TEXT NOT NULL CHECK(lifecycle IN ('Draft', 'Validated', 'Active', 'Retired')),
    definition_identity_sha256 TEXT NOT NULL CHECK(length(definition_identity_sha256) = 64),
    shared_plan_identity_sha256 TEXT NOT NULL CHECK(length(shared_plan_identity_sha256) = 64),
    local_plan_identity_sha256 TEXT NOT NULL CHECK(length(local_plan_identity_sha256) = 64),
    pipeline_json BLOB NOT NULL CHECK(length(pipeline_json) BETWEEN 2 AND 2097152),
    definition_json BLOB NOT NULL CHECK(length(definition_json) BETWEEN 2 AND 2097152),
    frozen_plan_json BLOB NOT NULL CHECK(length(frozen_plan_json) BETWEEN 2 AND 2097152),
    nodes_json BLOB NOT NULL CHECK(length(nodes_json) BETWEEN 2 AND 2097152),
    created_unix_ms INTEGER NOT NULL,
    validated_unix_ms INTEGER NULL,
    activated_unix_ms INTEGER NULL,
    retired_unix_ms INTEGER NULL,
    UNIQUE(graph_name, revision_name)
) STRICT;
CREATE TABLE processing_graph_registry_state(
    state_key INTEGER PRIMARY KEY CHECK(state_key = 1),
    selection_mode TEXT NOT NULL CHECK(selection_mode IN ('ConfiguredBasic', 'Named')),
    active_revision_id TEXT NOT NULL CHECK(length(active_revision_id) = 64),
    configured_basic_revision_id TEXT NOT NULL CHECK(length(configured_basic_revision_id) = 64),
    state_version INTEGER NOT NULL CHECK(state_version > 0),
    updated_unix_ms INTEGER NOT NULL,
    FOREIGN KEY(active_revision_id) REFERENCES processing_graph_revisions(revision_id),
    FOREIGN KEY(configured_basic_revision_id) REFERENCES processing_graph_revisions(revision_id)
) STRICT;
CREATE TABLE processing_graph_commands(
    idempotency_key TEXT PRIMARY KEY CHECK(length(idempotency_key) BETWEEN 1 AND 128),
    command_kind TEXT NOT NULL CHECK(length(command_kind) BETWEEN 1 AND 64),
    command_sha256 TEXT NOT NULL CHECK(length(command_sha256) = 64),
    actor TEXT NOT NULL CHECK(length(actor) BETWEEN 1 AND 128),
    reason TEXT NULL CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 256),
    result_reference TEXT NOT NULL CHECK(length(result_reference) BETWEEN 1 AND 128),
    completed_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE processing_graph_delivery_proposals(
    proposal_id TEXT PRIMARY KEY CHECK(length(proposal_id) = 32),
    catalog_revision_id TEXT NOT NULL CHECK(length(catalog_revision_id) = 32),
    assignment_id TEXT NOT NULL CHECK(length(assignment_id) = 32),
    registration_id TEXT NOT NULL CHECK(length(registration_id) = 32),
    installation_id TEXT NOT NULL CHECK(length(installation_id) = 32),
    installation_public_id TEXT NOT NULL CHECK(length(installation_public_id) = 32),
    expected_active_revision_id TEXT NULL CHECK(expected_active_revision_id IS NULL OR length(expected_active_revision_id) = 64),
    capability_snapshot_sha256 TEXT NOT NULL CHECK(length(capability_snapshot_sha256) = 64),
    definition_identity_sha256 TEXT NOT NULL CHECK(length(definition_identity_sha256) = 64),
    shared_plan_identity_sha256 TEXT NOT NULL CHECK(length(shared_plan_identity_sha256) = 64),
    definition_json BLOB NOT NULL CHECK(length(definition_json) BETWEEN 2 AND 2097152),
    issued_unix_ms INTEGER NOT NULL,
    expires_unix_ms INTEGER NOT NULL CHECK(expires_unix_ms > issued_unix_ms),
    disposition TEXT NOT NULL CHECK(disposition IN ('Pending', 'Accepted', 'Rejected', 'Expired', 'Superseded')),
    disposition_reason TEXT NULL CHECK(disposition_reason IS NULL OR length(disposition_reason) BETWEEN 1 AND 128),
    local_revision_id TEXT NULL CHECK(local_revision_id IS NULL OR length(local_revision_id) = 64),
    local_plan_identity_sha256 TEXT NULL CHECK(local_plan_identity_sha256 IS NULL OR length(local_plan_identity_sha256) = 64),
    received_unix_ms INTEGER NOT NULL,
    settled_unix_ms INTEGER NULL,
    FOREIGN KEY(local_revision_id) REFERENCES processing_graph_revisions(revision_id)
) STRICT;
CREATE TABLE processing_graph_delivery_facts(
    fact_id TEXT PRIMARY KEY CHECK(length(fact_id) = 32),
    proposal_id TEXT NOT NULL CHECK(length(proposal_id) = 32),
    fact_kind TEXT NOT NULL CHECK(fact_kind IN ('Accepted', 'Rejected', 'Activated', 'RolledBack', 'Expired')),
    occurred_unix_ms INTEGER NOT NULL,
    local_revision_id TEXT NULL CHECK(local_revision_id IS NULL OR length(local_revision_id) = 64),
    definition_identity_sha256 TEXT NULL CHECK(definition_identity_sha256 IS NULL OR length(definition_identity_sha256) = 64),
    shared_plan_identity_sha256 TEXT NULL CHECK(shared_plan_identity_sha256 IS NULL OR length(shared_plan_identity_sha256) = 64),
    local_plan_identity_sha256 TEXT NULL CHECK(local_plan_identity_sha256 IS NULL OR length(local_plan_identity_sha256) = 64),
    reason_code TEXT NULL CHECK(reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 128),
    delivery_state TEXT NOT NULL CHECK(delivery_state IN ('Pending', 'Acknowledged', 'Rejected')),
    attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
    next_attempt_unix_ms INTEGER NOT NULL,
    acknowledged_unix_ms INTEGER NULL,
    last_reason_code TEXT NULL CHECK(last_reason_code IS NULL OR length(last_reason_code) BETWEEN 1 AND 128),
    FOREIGN KEY(proposal_id) REFERENCES processing_graph_delivery_proposals(proposal_id)
) STRICT;
CREATE TABLE processing_executions(
    execution_id TEXT PRIMARY KEY CHECK(length(execution_id) = 32),
    execution_class TEXT NOT NULL CHECK(execution_class IN ('Live', 'Replay')),
    status TEXT NOT NULL CHECK(status IN ('Pending', 'Running', 'Completed', 'Failed', 'Cancelled', 'Expired')),
    capture_id TEXT NOT NULL CHECK(length(capture_id) = 32),
    primary_artifact_id TEXT NOT NULL CHECK(length(primary_artifact_id) = 32),
    graph_revision_id TEXT NOT NULL CHECK(length(graph_revision_id) = 64),
    definition_identity_sha256 TEXT NOT NULL CHECK(length(definition_identity_sha256) = 64),
    shared_plan_identity_sha256 TEXT NOT NULL CHECK(length(shared_plan_identity_sha256) = 64),
    local_plan_identity_sha256 TEXT NOT NULL CHECK(length(local_plan_identity_sha256) = 64),
    frozen_plan_json BLOB NOT NULL CHECK(length(frozen_plan_json) BETWEEN 2 AND 2097152),
    configuration_json BLOB NOT NULL CHECK(length(configuration_json) BETWEEN 2 AND 2097152),
    trigger_kind TEXT NOT NULL CHECK(length(trigger_kind) BETWEEN 1 AND 64),
    trigger_reference TEXT NULL CHECK(trigger_reference IS NULL OR length(trigger_reference) BETWEEN 1 AND 128),
    priority INTEGER NOT NULL CHECK(priority BETWEEN -1000 AND 1000),
    payload_bytes INTEGER NOT NULL CHECK(payload_bytes >= 0),
    accepted_unix_ms INTEGER NOT NULL,
    available_unix_ms INTEGER NOT NULL,
    deadline_unix_ms INTEGER NOT NULL,
    maximum_age_unix_ms INTEGER NOT NULL,
    started_unix_ms INTEGER NULL,
    completed_unix_ms INTEGER NULL,
    failure_reason TEXT NULL CHECK(failure_reason IS NULL OR length(failure_reason) BETWEEN 1 AND 128),
    cancellation_requested INTEGER NOT NULL DEFAULT 0 CHECK(cancellation_requested IN (0, 1)),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
    allow_automatic_publication INTEGER NOT NULL CHECK(allow_automatic_publication IN (0, 1)),
    FOREIGN KEY(graph_revision_id) REFERENCES processing_graph_revisions(revision_id),
    UNIQUE(execution_class, capture_id, graph_revision_id, trigger_kind, trigger_reference)
) STRICT;
CREATE TABLE processing_execution_nodes(
    execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
    node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
    required INTEGER NOT NULL CHECK(required IN (0, 1)),
    plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256) = 64),
    shared_plan_node_identity_sha256 TEXT NOT NULL CHECK(length(shared_plan_node_identity_sha256) = 64),
    dependencies_json TEXT NOT NULL,
    inputs_json TEXT NOT NULL,
    outputs_json TEXT NOT NULL,
    window_json TEXT NULL,
    status TEXT NOT NULL CHECK(status IN ('Pending', 'Running', 'Completed', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
    reason TEXT NULL CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 128),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
    started_unix_ms INTEGER NULL,
    completed_unix_ms INTEGER NULL,
    PRIMARY KEY(execution_id, node_id),
    FOREIGN KEY(execution_id) REFERENCES processing_executions(execution_id) ON DELETE CASCADE
) STRICT;
CREATE TABLE processing_node_attempts(
    execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
    node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
    attempt_number INTEGER NOT NULL CHECK(attempt_number > 0),
    lease_owner TEXT NOT NULL CHECK(length(lease_owner) BETWEEN 1 AND 128),
    lease_token TEXT NOT NULL CHECK(length(lease_token) BETWEEN 1 AND 128),
    started_unix_ms INTEGER NOT NULL,
    completed_unix_ms INTEGER NULL,
    status TEXT NOT NULL CHECK(status IN ('Running', 'Completed', 'Skipped', 'RetryableFailure', 'TerminalFailure', 'Interrupted')),
    outcome TEXT NULL CHECK(outcome IS NULL OR outcome IN ('Produced', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
    reason TEXT NULL CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 128),
    duration_ticks INTEGER NULL CHECK(duration_ticks IS NULL OR duration_ticks >= 0),
    execution_route TEXT NOT NULL DEFAULT 'Unknown' CHECK(execution_route IN ('Unknown', 'InProcess', 'LocalRunner')),
    PRIMARY KEY(execution_id, node_id, attempt_number),
    FOREIGN KEY(execution_id, node_id) REFERENCES processing_execution_nodes(execution_id, node_id) ON DELETE CASCADE
) STRICT;
CREATE TABLE processing_execution_inputs(
    execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
    node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
    input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0 AND input_ordinal < 512),
    window_position INTEGER NOT NULL,
    capture_id TEXT NOT NULL CHECK(length(capture_id) = 32),
    artifact_id TEXT NOT NULL CHECK(length(artifact_id) = 32),
    descriptor_sha256 TEXT NOT NULL CHECK(length(descriptor_sha256) = 64),
    payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
    selected_flag INTEGER NOT NULL CHECK(selected_flag IN (0, 1)),
    PRIMARY KEY(execution_id, node_id, input_ordinal),
    FOREIGN KEY(execution_id) REFERENCES processing_executions(execution_id) ON DELETE CASCADE
) STRICT;
CREATE TABLE processing_execution_input_pins(
    execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
    raw_capture_row_id INTEGER NOT NULL,
    artifact_id TEXT NOT NULL CHECK(length(artifact_id) = 32),
    released_flag INTEGER NOT NULL DEFAULT 0 CHECK(released_flag IN (0, 1)),
    released_unix_ms INTEGER NULL,
    PRIMARY KEY(execution_id, artifact_id),
    FOREIGN KEY(execution_id) REFERENCES processing_executions(execution_id) ON DELETE CASCADE,
    FOREIGN KEY(raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
) STRICT;
CREATE TABLE processing_execution_output_input_pins(
    execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
    node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
    input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0 AND input_ordinal < 512),
    window_position INTEGER NOT NULL,
    output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
    released_flag INTEGER NOT NULL DEFAULT 0 CHECK(released_flag IN (0, 1)),
    released_unix_ms INTEGER NULL,
    PRIMARY KEY(execution_id, node_id, input_ordinal),
    UNIQUE(execution_id, node_id, output_identity_sha256),
    FOREIGN KEY(execution_id, node_id) REFERENCES processing_execution_nodes(execution_id, node_id) ON DELETE CASCADE,
    FOREIGN KEY(output_identity_sha256) REFERENCES processing_outputs(output_identity_sha256) ON DELETE CASCADE
) STRICT;
CREATE TABLE processing_execution_outputs(
    execution_id TEXT NOT NULL CHECK(length(execution_id) = 32),
    node_id TEXT NOT NULL CHECK(length(node_id) BETWEEN 1 AND 128),
    output_ordinal INTEGER NOT NULL CHECK(output_ordinal >= 0 AND output_ordinal < 128),
    output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
    published_flag INTEGER NOT NULL CHECK(published_flag IN (0, 1)),
    PRIMARY KEY(execution_id, node_id, output_ordinal),
    UNIQUE(execution_id, output_identity_sha256),
    FOREIGN KEY(execution_id, node_id) REFERENCES processing_execution_nodes(execution_id, node_id) ON DELETE CASCADE,
    FOREIGN KEY(output_identity_sha256) REFERENCES processing_outputs(output_identity_sha256)
        ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
) STRICT;
CREATE TABLE processing_replay_work(
    work_id INTEGER PRIMARY KEY AUTOINCREMENT,
    execution_id TEXT NOT NULL UNIQUE CHECK(length(execution_id) = 32),
    state TEXT NOT NULL CHECK(state IN ('Pending', 'Leased', 'RetryWait', 'Completed', 'Failed', 'Cancelled', 'Expired')),
    priority INTEGER NOT NULL CHECK(priority BETWEEN -1000 AND 1000),
    available_unix_ms INTEGER NOT NULL,
    lease_token TEXT NULL,
    lease_owner TEXT NULL,
    lease_expires_unix_ms INTEGER NULL,
    claim_count INTEGER NOT NULL DEFAULT 0 CHECK(claim_count >= 0),
    updated_unix_ms INTEGER NOT NULL,
    FOREIGN KEY(execution_id) REFERENCES processing_executions(execution_id) ON DELETE CASCADE
) STRICT;
CREATE TABLE processing_nodes(
    capture_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    required INTEGER NOT NULL CHECK(required IN (0, 1)),
    dependencies_json TEXT NOT NULL,
    recipe_name TEXT NULL,
    output_role TEXT NULL,
    output_variant TEXT NULL,
    plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256) = 64),
    status TEXT NOT NULL CHECK(status IN ('Completed', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
    reason TEXT NULL,
    attempt INTEGER NOT NULL,
    input_evidence_version INTEGER NULL CHECK(input_evidence_version IS NULL OR input_evidence_version = 1),
    processing_profile_identity_sha256 TEXT NULL CHECK(processing_profile_identity_sha256 IS NULL OR length(processing_profile_identity_sha256) = 64),
    started_unix_ms INTEGER NULL,
    completed_unix_ms INTEGER NOT NULL,
    duration_ticks INTEGER NULL CHECK(duration_ticks IS NULL OR duration_ticks >= 0),
    outcome TEXT NULL CHECK(outcome IS NULL OR outcome IN ('Produced', 'Skipped', 'RetryableFailure', 'TerminalFailure')),
    PRIMARY KEY(capture_id, node_id)
) STRICT;
CREATE TABLE processing_node_inputs(
    capture_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    input_ordinal INTEGER NOT NULL CHECK(input_ordinal >= 0 AND input_ordinal < 128),
    kind TEXT NOT NULL CHECK(kind IN ('Artifact', 'AuxiliaryArtifact', 'CanonicalContext')),
    name TEXT NULL CHECK(name IS NULL OR length(name) BETWEEN 1 AND 64),
    artifact_id TEXT NULL CHECK(artifact_id IS NULL OR length(artifact_id) = 32),
    role TEXT NULL,
    variant TEXT NULL CHECK(variant IS NULL OR length(variant) BETWEEN 1 AND 128),
    recipe_identity_sha256 TEXT NULL CHECK(recipe_identity_sha256 IS NULL OR length(recipe_identity_sha256) = 64),
    schema_version TEXT NULL CHECK(schema_version IS NULL OR length(schema_version) BETWEEN 1 AND 128),
    identity_sha256 TEXT NULL CHECK(identity_sha256 IS NULL OR length(identity_sha256) = 64),
    selected_flag INTEGER NOT NULL CHECK(selected_flag IN (0, 1)),
    PRIMARY KEY(capture_id, node_id, input_ordinal),
    FOREIGN KEY(capture_id, node_id) REFERENCES processing_nodes(capture_id, node_id)
        ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
) STRICT;
CREATE TABLE processing_outputs(
    output_identity_sha256 TEXT PRIMARY KEY CHECK(length(output_identity_sha256) = 64),
    capture_id TEXT NOT NULL,
    agent_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    artifact_id TEXT NOT NULL UNIQUE CHECK(length(artifact_id) = 32),
    role TEXT NOT NULL,
    variant TEXT NOT NULL,
    payload_relative_path TEXT NOT NULL,
    sidecar_relative_path TEXT NOT NULL,
    descriptor_json BLOB NOT NULL,
    recipe_identity_sha256 TEXT NOT NULL CHECK(length(recipe_identity_sha256) = 64),
    frame_artifact_recipe_version TEXT NULL,
    algorithms_json BLOB NOT NULL,
    compatibility_json BLOB NOT NULL,
    total_integration_ticks INTEGER NOT NULL,
    capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
    committed_unix_ms INTEGER NOT NULL,
    product_kind TEXT NULL CHECK(product_kind IS NULL OR product_kind IN ('PixelData', 'Metadata')),
    product_schema_version TEXT NULL CHECK(product_schema_version IS NULL OR length(product_schema_version) BETWEEN 1 AND 128),
    content_identity_sha256 TEXT NULL CHECK(content_identity_sha256 IS NULL OR length(content_identity_sha256) = 64),
    availability_state TEXT NOT NULL DEFAULT 'Available' CHECK(availability_state IN ('Available', 'Missing', 'Quarantined')),
    availability_reason TEXT NULL CHECK(availability_reason IS NULL OR length(availability_reason) BETWEEN 1 AND 128),
    unavailable_unix_ms INTEGER NULL,
    quarantine_relative_path TEXT NULL,
    CHECK((product_kind IS NULL AND product_schema_version IS NULL AND content_identity_sha256 IS NULL) OR
          (product_kind IS NOT NULL AND product_schema_version IS NOT NULL AND content_identity_sha256 IS NOT NULL))
) STRICT;
CREATE TABLE processing_output_sources(
    output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
    source_ordinal INTEGER NOT NULL CHECK(source_ordinal >= 0 AND source_ordinal < 512),
    source_artifact_id TEXT NOT NULL CHECK(length(source_artifact_id) = 32),
    PRIMARY KEY(output_identity_sha256, source_ordinal),
    UNIQUE(output_identity_sha256, source_artifact_id),
    FOREIGN KEY(output_identity_sha256) REFERENCES processing_outputs(output_identity_sha256)
        ON DELETE CASCADE
) STRICT;
CREATE TABLE processing_lifecycle_operations(
    operation_id TEXT PRIMARY KEY CHECK(length(operation_id) BETWEEN 1 AND 128),
    kind TEXT NOT NULL CHECK(kind IN ('quarantine', 'delete', 'orphan')),
    output_identity_sha256 TEXT NULL CHECK(output_identity_sha256 IS NULL OR length(output_identity_sha256) = 64),
    source_relative_path TEXT NULL,
    companion_relative_path TEXT NULL,
    destination_relative_path TEXT NOT NULL,
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
    observed_bytes INTEGER NOT NULL CHECK(observed_bytes >= 0),
    planned_unix_ms INTEGER NOT NULL
    ,phase TEXT NOT NULL DEFAULT 'planned' CHECK(phase IN ('planned', 'moved', 'database-completed', 'files-deleted')),
    CHECK((kind = 'quarantine' AND source_relative_path IS NOT NULL) OR kind IN ('orphan', 'delete'))
) STRICT;
CREATE TABLE processing_reconciliation_state(
    state_key INTEGER PRIMARY KEY CHECK(state_key = 1),
    output_identity_sha256 TEXT NULL CHECK(output_identity_sha256 IS NULL OR length(output_identity_sha256) = 64),
    modern_sidecar_relative_path TEXT NULL,
    legacy_sidecar_relative_path TEXT NULL,
    payload_relative_path TEXT NULL
) STRICT;
CREATE TABLE processing_output_diagnostics(
    diagnostic_id INTEGER PRIMARY KEY AUTOINCREMENT,
    output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
    capture_id TEXT NOT NULL CHECK(length(capture_id) = 32),
    node_id TEXT NOT NULL,
    availability_state TEXT NOT NULL CHECK(availability_state IN ('Missing', 'Quarantined')),
    availability_reason TEXT NULL,
    descriptor_json BLOB NOT NULL,
    quarantine_relative_path TEXT NULL,
    recorded_unix_ms INTEGER NOT NULL
) STRICT;
CREATE TABLE transient_worker_frames (
    raw_capture_row_id INTEGER PRIMARY KEY,
    state TEXT NOT NULL CHECK (state IN ('queued', 'history', 'retry_wait', 'completed', 'quarantined', 'abandoned')),
    attempt_count INTEGER NOT NULL CHECK (attempt_count >= 0),
    available_unix_ms INTEGER NOT NULL,
    causal_succeeded INTEGER NOT NULL DEFAULT 0 CHECK (causal_succeeded IN (0, 1)),
    failure_reason TEXT,
    created_unix_ms INTEGER NOT NULL,
    updated_unix_ms INTEGER NOT NULL,
    FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
) STRICT;
CREATE TABLE transient_worker_candidates (
    candidate_id TEXT PRIMARY KEY,
    event_id TEXT NOT NULL,
    target_raw_capture_row_id INTEGER NOT NULL,
    slot_ordinal INTEGER NOT NULL CHECK (slot_ordinal >= 0),
    observation_id TEXT NOT NULL,
    assessment_id TEXT NOT NULL,
    event_version_id TEXT NOT NULL,
    allocated_unix_ms INTEGER NOT NULL,
    association_ambiguous INTEGER NOT NULL CHECK (association_ambiguous IN (0, 1)),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    available_unix_ms INTEGER NOT NULL,
    causal_extraction_json BLOB,
    causal_extraction_sha256 TEXT CHECK (causal_extraction_sha256 IS NULL OR length(causal_extraction_sha256) = 64),
    observation_extraction_json BLOB,
    observation_extraction_sha256 TEXT CHECK (observation_extraction_sha256 IS NULL OR length(observation_extraction_sha256) = 64),
    assessment_execution_json BLOB,
    assessment_execution_sha256 TEXT CHECK (assessment_execution_sha256 IS NULL OR length(assessment_execution_sha256) = 64),
    failure_reason TEXT,
    state TEXT NOT NULL CHECK (state IN ('pending', 'completed', 'quarantined')),
    updated_unix_ms INTEGER,
    UNIQUE (target_raw_capture_row_id, slot_ordinal),
    FOREIGN KEY (target_raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
) STRICT;
INSERT INTO "capture_lane_definitions" VALUES('standard', 1, 1, 1, '78063DFC12AB3698E07B0D736FD10222717FDF6F9CD3E13D9D4E45C552E1022D', 0, 1792245360057, 1792245360057);
INSERT INTO "capture_control_state" VALUES(1, 'running', 0, 1792245360017);
INSERT INTO "transient_runtime_policy" VALUES(1, 'off', 0, 10, 1792245360052);
INSERT INTO "calibration_library_state" VALUES(1, NULL, 0, NULL, NULL, NULL, NULL, 1792245360018);
INSERT INTO "named_rig_selection" VALUES(1, NULL, NULL, NULL, NULL, NULL, 0, 1792245360019);
INSERT INTO "capture_processing_schema" VALUES(1, 8);
CREATE INDEX ix_raw_captures_discovery ON raw_captures(state, agent_id, capture_sequence);
CREATE INDEX ix_raw_captures_backlog ON raw_captures(state, durable_ingress_unix_ms);
CREATE INDEX ix_raw_captures_retention ON raw_captures(retention_hold, exposure_started_unix_ms);
CREATE INDEX ix_raw_captures_gallery_time
    ON raw_captures(exposure_started_unix_ms DESC, capture_sequence DESC, raw_capture_row_id DESC);
CREATE INDEX ix_raw_captures_gallery_sequence
    ON raw_captures(capture_sequence DESC, raw_capture_row_id DESC);
CREATE INDEX ix_raw_captures_gallery_state
    ON raw_captures(state, capture_sequence DESC, raw_capture_row_id DESC);
CREATE INDEX ix_raw_captures_gallery_origin
    ON raw_captures(evidence_origin, capture_sequence DESC, raw_capture_row_id DESC);
CREATE INDEX ix_capture_lane_work_claim
    ON capture_lane_work(lane_name, state, available_unix_ms, work_id);
CREATE INDEX ix_capture_lane_work_lease
    ON capture_lane_work(state, lease_expires_unix_ms);
CREATE INDEX ix_capture_lane_work_backlog
    ON capture_lane_work(lane_name, state, created_unix_ms);
CREATE INDEX ix_capture_lane_work_raw
    ON capture_lane_work(raw_capture_row_id, required, state);
CREATE INDEX ix_capture_lane_work_ordered
    ON capture_lane_work(lane_name, agent_id, capture_sequence)
    WHERE state NOT IN ('completed', 'abandoned');
CREATE INDEX ix_transient_candidates_backlog
    ON transient_candidates(state, created_unix_ms, candidate_id);
CREATE INDEX ix_transient_candidates_operator
    ON transient_candidates(created_unix_ms DESC, candidate_id DESC);
CREATE INDEX ix_transient_candidate_sources_raw
    ON transient_candidate_sources(raw_capture_row_id, candidate_id);
CREATE INDEX ix_transient_capture_work_backlog
    ON transient_capture_work(state, created_unix_ms, raw_capture_row_id);
CREATE INDEX ix_transient_candidate_conflicts_observed
    ON transient_candidate_conflicts(observed_unix_ms, conflict_id);
CREATE INDEX ix_transient_candidate_conflicts_candidate
    ON transient_candidate_conflicts(candidate_id, conflict_id);
CREATE INDEX ix_transient_runtime_operations_target
    ON transient_runtime_operations(raw_capture_row_id, operation_id DESC);
CREATE INDEX ix_capture_schedule_revisions_created
    ON capture_schedule_revisions(created_unix_ms, revision_number);
CREATE INDEX ix_capture_schedule_overrides_active
    ON capture_schedule_overrides(cleared_unix_ms, end_unix_ms, mode, override_id);
CREATE INDEX ix_capture_schedule_expansions_lookup
    ON capture_schedule_expansions(
        revision_id, deployment_location_id, deployment_location_version,
        preview_start_unix_ms, preview_end_unix_ms);
CREATE INDEX ix_capture_schedule_intervals_bounds
    ON capture_schedule_intervals(expansion_key, start_unix_ms, end_unix_ms, ordinal);
CREATE INDEX ix_calibration_library_bundles_selection
    ON calibration_library_bundles(
        publication_state, agent_id, rig_id, rig_profile_sha256, sensor_profile_sha256,
        effective_from_unix_ms, effective_until_unix_ms, bundle_id);
CREATE INDEX ix_calibration_library_bundles_created
    ON calibration_library_bundles(created_unix_ms DESC, bundle_id);
CREATE INDEX ix_calibration_library_artifacts_role
    ON calibration_library_artifacts(bundle_id, role, reference_kind, source_index, ordinal);
CREATE INDEX ix_calibration_library_activations_history
    ON calibration_library_activations(activated_unix_ms DESC, activation_id DESC);
CREATE INDEX ix_calibration_acquisition_jobs_camera
    ON calibration_acquisition_jobs(camera_key, state, created_unix_ms, job_id);
CREATE UNIQUE INDEX ux_calibration_acquisition_jobs_camera_nonterminal
    ON calibration_acquisition_jobs(camera_key)
    WHERE state NOT IN ('published', 'failed', 'cancelled');
CREATE INDEX ix_calibration_library_reconciliation_state
    ON calibration_library_reconciliation(operation_state, observed_unix_ms, reconciliation_id);
CREATE TRIGGER tr_named_equipment_revisions_immutable
BEFORE UPDATE ON named_equipment_revisions
BEGIN SELECT RAISE(ABORT, 'equipment revisions are immutable'); END;
CREATE TRIGGER tr_named_equipment_revisions_no_delete
BEFORE DELETE ON named_equipment_revisions
BEGIN SELECT RAISE(ABORT, 'equipment revisions are immutable'); END;
CREATE TRIGGER tr_named_rig_revisions_immutable
BEFORE UPDATE ON named_rig_revisions
BEGIN SELECT RAISE(ABORT, 'rig revisions are immutable'); END;
CREATE TRIGGER tr_named_rig_revisions_no_delete
BEFORE DELETE ON named_rig_revisions
BEGIN SELECT RAISE(ABORT, 'rig revisions are immutable'); END;
CREATE UNIQUE INDEX ix_processing_graph_revisions_active
    ON processing_graph_revisions(lifecycle) WHERE lifecycle = 'Active';
CREATE INDEX ix_processing_outputs_capture_node
    ON processing_outputs(capture_id, node_id);
CREATE INDEX ix_processing_outputs_window
    ON processing_outputs(agent_id, node_id, role, capture_sequence);
CREATE INDEX ix_processing_nodes_status
    ON processing_nodes(status, capture_id);
CREATE INDEX ix_processing_nodes_recipe
    ON processing_nodes(upper(recipe_name), capture_id);
CREATE INDEX ix_processing_outputs_role
    ON processing_outputs(role, capture_id);
CREATE INDEX ix_processing_outputs_recipe
    ON processing_outputs(recipe_identity_sha256, capture_id);
CREATE INDEX ix_processing_outputs_product
    ON processing_outputs(capture_id, product_schema_version, output_identity_sha256);
CREATE INDEX ix_processing_outputs_retention_available
    ON processing_outputs(committed_unix_ms, output_identity_sha256) WHERE availability_state = 'Available';
CREATE INDEX ix_processing_outputs_retention_unavailable
    ON processing_outputs(unavailable_unix_ms, output_identity_sha256) WHERE availability_state <> 'Available';
CREATE INDEX ix_processing_output_sources_artifact
    ON processing_output_sources(source_artifact_id, output_identity_sha256);
CREATE INDEX ix_processing_node_inputs_artifact
    ON processing_node_inputs(artifact_id, capture_id, node_id);
CREATE INDEX ix_processing_graph_revisions_name
    ON processing_graph_revisions(graph_name, created_unix_ms DESC);
CREATE UNIQUE INDEX ix_processing_executions_live_capture
    ON processing_executions(capture_id) WHERE execution_class = 'Live';
CREATE INDEX ix_processing_executions_status
    ON processing_executions(execution_class, status, accepted_unix_ms, execution_id);
CREATE INDEX ix_processing_replay_work_claim
    ON processing_replay_work(state, priority DESC, available_unix_ms, work_id);
CREATE INDEX ix_processing_node_attempts_history
    ON processing_node_attempts(execution_id, node_id, attempt_number DESC);
CREATE INDEX ix_processing_execution_inputs_window
    ON processing_execution_inputs(capture_id, window_position, execution_id);
CREATE INDEX ix_processing_execution_pins_active
    ON processing_execution_input_pins(raw_capture_row_id, execution_id) WHERE released_flag = 0;
CREATE INDEX ix_processing_execution_output_pins_active
    ON processing_execution_output_input_pins(output_identity_sha256, execution_id) WHERE released_flag = 0;
CREATE INDEX ix_processing_execution_outputs_publication
    ON processing_execution_outputs(output_identity_sha256, published_flag);
CREATE INDEX ix_processing_graph_delivery_proposals_lookup
    ON processing_graph_delivery_proposals(disposition, local_revision_id, issued_unix_ms);
CREATE INDEX ix_processing_graph_delivery_facts_pending
    ON processing_graph_delivery_facts(delivery_state, next_attempt_unix_ms, occurred_unix_ms);
CREATE INDEX ix_processing_graph_delivery_facts_lifecycle
    ON processing_graph_delivery_facts(proposal_id, fact_kind)
    WHERE fact_kind IN ('Activated', 'RolledBack');
CREATE UNIQUE INDEX ix_processing_graph_delivery_facts_settlement
    ON processing_graph_delivery_facts(proposal_id)
    WHERE fact_kind IN ('Accepted', 'Rejected', 'Expired');
CREATE INDEX ix_transient_worker_frames_ready
    ON transient_worker_frames(state, available_unix_ms, raw_capture_row_id);
CREATE INDEX ix_transient_worker_candidates_pending
    ON transient_worker_candidates(state, allocated_unix_ms, candidate_id);
PRAGMA user_version = 15;
