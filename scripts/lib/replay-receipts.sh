#!/usr/bin/env bash

declare -a REPLAY_RECEIPT_TEMPORARIES=()

replay_receipt_track() {
    REPLAY_RECEIPT_TEMPORARIES+=("$1")
}

replay_receipt_remove() {
    [[ -n "$1" ]] || return 0
    rm -rf -- "$1"
}

replay_receipt_cleanup() {
    local path
    for path in "${REPLAY_RECEIPT_TEMPORARIES[@]}"; do
        replay_receipt_remove "$path"
    done
}

replay_atomic_copy() {
    local source="$1" output_file="$2" pending_file source_identity source_bytes source_hash
    local copied_bytes copied_hash final_identity final_bytes final_hash
    [[ -f "$source" ]] || return 1
    source_identity="$(stat --format='%d:%i' -- "$source")" || return 1
    source_bytes="$(stat --format='%s' -- "$source")" || return 1
    read -r source_hash _ < <(sha256sum "$source")
    pending_file="$(mktemp "${output_file}.pending.XXXXXX")" || return 1
    replay_receipt_track "$pending_file"
    if ! cp -- "$source" "$pending_file"; then
        replay_receipt_remove "$pending_file"
        return 1
    fi
    copied_bytes="$(stat --format='%s' -- "$pending_file")" \
        || { replay_receipt_remove "$pending_file"; return 1; }
    read -r copied_hash _ < <(sha256sum "$pending_file")
    final_identity="$(stat --format='%d:%i' -- "$source")" \
        || { replay_receipt_remove "$pending_file"; return 1; }
    final_bytes="$(stat --format='%s' -- "$source")" \
        || { replay_receipt_remove "$pending_file"; return 1; }
    read -r final_hash _ < <(sha256sum "$source")
    if [[ "$copied_bytes" != "$source_bytes" || "$copied_hash" != "$source_hash" ||
        "$final_identity" != "$source_identity" || "$final_bytes" != "$source_bytes" ||
        "$final_hash" != "$source_hash" ]] || ! mv -- "$pending_file" "$output_file"; then
        replay_receipt_remove "$pending_file"
        return 1
    fi
}

# Call replay_snapshot_begin before collecting a related set of files. Every source is
# rechecked immediately before publication so the generated receipt binds the bytes jq read.
replay_snapshot_begin() {
    REPLAY_SNAPSHOT_SOURCES=()
    REPLAY_SNAPSHOT_IDENTITIES=()
    REPLAY_SNAPSHOT_BYTES=()
    REPLAY_SNAPSHOT_HASHES=()
}

replay_snapshot_file() {
    local source="$1" snapshot="$2"
    local pre_identity pre_bytes pre_hash snapshot_bytes snapshot_hash
    local post_identity post_bytes post_hash

    [[ -f "$source" && ! -L "$source" ]] || return 1
    pre_identity="$(stat --format='%d:%i' -- "$source")" || return 1
    pre_bytes="$(stat --format='%s' -- "$source")" || return 1
    read -r pre_hash _ < <(sha256sum "$source")
    cp -- "$source" "$snapshot" || return 1
    snapshot_bytes="$(stat --format='%s' -- "$snapshot")" || return 1
    read -r snapshot_hash _ < <(sha256sum "$snapshot")
    post_identity="$(stat --format='%d:%i' -- "$source")" || return 1
    post_bytes="$(stat --format='%s' -- "$source")" || return 1
    read -r post_hash _ < <(sha256sum "$source")

    [[ -f "$source" && ! -L "$source" &&
        "$snapshot_bytes" == "$pre_bytes" && "$snapshot_hash" == "$pre_hash" &&
        "$post_identity" == "$pre_identity" && "$post_bytes" == "$pre_bytes" &&
        "$post_hash" == "$pre_hash" ]] || return 1

    REPLAY_SNAPSHOT_SOURCES+=("$source")
    REPLAY_SNAPSHOT_IDENTITIES+=("$pre_identity")
    REPLAY_SNAPSHOT_BYTES+=("$pre_bytes")
    REPLAY_SNAPSHOT_HASHES+=("$pre_hash")
    REPLAY_SNAPSHOT_SHA256="$pre_hash"
    REPLAY_SNAPSHOT_SIZE="$pre_bytes"
}

replay_snapshot_sources_unchanged() {
    local index identity bytes hash
    for index in "${!REPLAY_SNAPSHOT_SOURCES[@]}"; do
        [[ -f "${REPLAY_SNAPSHOT_SOURCES[$index]}" &&
            ! -L "${REPLAY_SNAPSHOT_SOURCES[$index]}" ]] || return 1
        identity="$(stat --format='%d:%i' -- "${REPLAY_SNAPSHOT_SOURCES[$index]}")" || return 1
        bytes="$(stat --format='%s' -- "${REPLAY_SNAPSHOT_SOURCES[$index]}")" || return 1
        read -r hash _ < <(sha256sum "${REPLAY_SNAPSHOT_SOURCES[$index]}")
        [[ "$identity" == "${REPLAY_SNAPSHOT_IDENTITIES[$index]}" &&
            "$bytes" == "${REPLAY_SNAPSHOT_BYTES[$index]}" &&
            "$hash" == "${REPLAY_SNAPSHOT_HASHES[$index]}" ]] || return 1
    done
}

replay_snapshot_tree() {
    local root="$1" output="$2" excluded_pending="${3:-}" entry type
    local -a find_arguments=("$root" -mindepth 1)
    if [[ -n "$excluded_pending" ]]; then
        find_arguments+=(! -path "$excluded_pending")
    fi
    find "${find_arguments[@]}" -printf '%y %P\0' | sort -z > "$output" || return 1
    while IFS= read -r -d '' entry; do
        type="${entry%% *}"
        [[ "$type" == d || "$type" == f ]] || return 1
    done < "$output"
}

replay_regular_paths_from_tree() {
    local tree="$1" output="$2" excluded_path="${3:-}" entry path
    : > "$output"
    while IFS= read -r -d '' entry; do
        [[ "${entry%% *}" == f ]] || continue
        path="${entry#* }"
        [[ "$path" == "$excluded_path" ]] || printf '%s\0' "$path" >> "$output"
    done < "$tree"
}

emit_replay_profile_receipt() {
    local evidence_file="$1" output_file="$2" trial="$3" replay_profile="$4"
    local state_reused="$5" evidence_path="$6" measured_capture_count="$7"
    local temporary pending_file evidence_sha validator_sha
    temporary="$(mktemp -d)" || return 1
    replay_receipt_track "$temporary"
    pending_file="$(mktemp "${output_file}.pending.XXXXXX")" \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_receipt_track "$pending_file"
    replay_snapshot_begin
    replay_snapshot_file "$evidence_file" "$temporary/evidence.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    evidence_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$REPLAY_EVIDENCE_PROGRAM" "$temporary/validator.jq" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    validator_sha="$REPLAY_SNAPSHOT_SHA256"

    if ! jq --exit-status -f "$temporary/validator.jq" \
        --arg trial "$trial" \
        --arg replayProfile "$replay_profile" \
        --arg stateKey replay-719 \
        --argjson stateReused "$state_reused" \
        --argjson measuredCaptureCount "$measured_capture_count" \
        --arg evidencePath "$evidence_path" \
        --arg evidenceSha256 "$evidence_sha" \
        --arg validatorPath 'scripts/issue-719-replay-evidence.jq' \
        --arg validatorSha256 "$validator_sha" \
        "$temporary/evidence.json" > "$pending_file" ||
        ! replay_snapshot_sources_unchanged; then
        replay_receipt_remove "$temporary"
        replay_receipt_remove "$pending_file"
        return 1
    fi
    mv -- "$pending_file" "$output_file" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    replay_receipt_remove "$temporary"
}

replay_profile_post_test_gate() {
    local evidence_file="$1" output_file="$2" trial="$3" replay_profile="$4"
    local state_reused="$5" evidence_path="$6" measured_capture_count="$7"
    if ! emit_replay_profile_receipt "$evidence_file" "$output_file" "$trial" \
            "$replay_profile" "$state_reused" "$evidence_path" "$measured_capture_count" ||
        ! verify_replay_profile_receipt "$output_file" "$evidence_file" "$trial" \
            "$replay_profile" "$state_reused" "$evidence_path" "$measured_capture_count"; then
        rm -f -- "$output_file"
        return 1
    fi
}

verify_replay_profile_receipt() {
    local receipt="$1" evidence="$2" trial="$3" profile="$4" reused="$5" evidence_path="$6"
    local measured_capture_count="$7" temporary evidence_sha validator_sha
    temporary="$(mktemp -d)" || return 1
    replay_receipt_track "$temporary"
    replay_snapshot_begin
    replay_snapshot_file "$receipt" "$temporary/receipt.json" \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_snapshot_file "$evidence" "$temporary/evidence.json" \
        || { replay_receipt_remove "$temporary"; return 1; }
    evidence_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$REPLAY_EVIDENCE_PROGRAM" "$temporary/evidence-validator.jq" \
        || { replay_receipt_remove "$temporary"; return 1; }
    validator_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$REPLAY_PROFILE_GATE_PROGRAM" "$temporary/profile-validator.jq" \
        || { replay_receipt_remove "$temporary"; return 1; }

    if ! jq --exit-status -f "$temporary/evidence-validator.jq" \
        --arg replayProfile "$profile" --arg stateKey replay-719 \
        --argjson stateReused "$reused" --argjson measuredCaptureCount "$measured_capture_count" \
        "$temporary/evidence.json" >/dev/null ||
        ! jq --exit-status -f "$temporary/profile-validator.jq" \
        --arg trial "$trial" --arg replayProfile "$profile" --arg stateKey replay-719 \
        --argjson stateReused "$reused" --arg evidencePath "$evidence_path" \
        --arg evidenceSha256 "$evidence_sha" --arg validatorPath scripts/issue-719-replay-evidence.jq \
        --arg validatorSha256 "$validator_sha" "$temporary/receipt.json" >/dev/null ||
        ! replay_snapshot_sources_unchanged; then
        replay_receipt_remove "$temporary"
        return 1
    fi
    replay_receipt_remove "$temporary"
}

emit_replay_pair_summary() {
    local in_process_evidence="$1" local_runner_evidence="$2"
    local in_process_gate="$3" local_runner_gate="$4" output_file="$5"
    local temporary pending_file
    local in_process_evidence_sha local_runner_evidence_sha in_process_gate_sha local_runner_gate_sha
    temporary="$(mktemp -d)" || return 1
    replay_receipt_track "$temporary"
    pending_file="$(mktemp "${output_file}.pending.XXXXXX")" \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_receipt_track "$pending_file"
    replay_snapshot_begin
    replay_snapshot_file "$in_process_evidence" "$temporary/in-process-evidence.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    in_process_evidence_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$local_runner_evidence" "$temporary/local-runner-evidence.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    local_runner_evidence_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$in_process_gate" "$temporary/in-process-gate.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    in_process_gate_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$local_runner_gate" "$temporary/local-runner-gate.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    local_runner_gate_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$REPLAY_PAIR_PROGRAM" "$temporary/validator.jq" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }

    if ! jq -n -f "$temporary/validator.jq" \
        --slurpfile inProcess "$temporary/in-process-evidence.json" \
        --slurpfile localRunner "$temporary/local-runner-evidence.json" \
        --arg stateKey replay-719 \
        --arg inProcessPath 'replay-inprocess/evidence/issue-719-replay-InProcess.json' \
        --arg inProcessSha256 "$in_process_evidence_sha" \
        --arg localRunnerPath 'replay-localrunner/evidence/issue-719-replay-LocalRunner.json' \
        --arg localRunnerSha256 "$local_runner_evidence_sha" \
        --arg inProcessGatePath 'replay-inprocess/evidence/replay-profile-gate.json' \
        --arg inProcessGateSha256 "$in_process_gate_sha" \
        --arg localRunnerGatePath 'replay-localrunner/evidence/replay-profile-gate.json' \
        --arg localRunnerGateSha256 "$local_runner_gate_sha" > "$pending_file" ||
        ! jq --exit-status '.passed == true' "$pending_file" >/dev/null ||
        ! replay_snapshot_sources_unchanged; then
        replay_receipt_remove "$temporary"
        replay_receipt_remove "$pending_file"
        return 1
    fi
    mv -- "$pending_file" "$output_file" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    replay_receipt_remove "$temporary"
}

emit_replay_final_summary() {
    local in_process_evidence="$1" local_runner_evidence="$2"
    local in_process_gate="$3" local_runner_gate="$4" pair="$5" output_file="$6"
    local temporary pending_file
    local in_process_evidence_sha local_runner_evidence_sha in_process_gate_sha local_runner_gate_sha
    local pair_sha validator_sha
    temporary="$(mktemp -d)" || return 1
    replay_receipt_track "$temporary"
    pending_file="$(mktemp "${output_file}.pending.XXXXXX")" \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_receipt_track "$pending_file"
    replay_snapshot_begin
    replay_snapshot_file "$in_process_evidence" "$temporary/in-process-evidence.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    in_process_evidence_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$local_runner_evidence" "$temporary/local-runner-evidence.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    local_runner_evidence_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$in_process_gate" "$temporary/in-process-gate.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    in_process_gate_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$local_runner_gate" "$temporary/local-runner-gate.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    local_runner_gate_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$pair" "$temporary/pair.json" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    pair_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$REPLAY_EVIDENCE_PROGRAM" "$temporary/evidence-validator.jq" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    validator_sha="$REPLAY_SNAPSHOT_SHA256"
    replay_snapshot_file "$REPLAY_FINAL_PROGRAM" "$temporary/final-validator.jq" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }

    if ! jq -n -f "$temporary/final-validator.jq" \
        --slurpfile inProcessEvidence "$temporary/in-process-evidence.json" \
        --slurpfile localRunnerEvidence "$temporary/local-runner-evidence.json" \
        --slurpfile inProcessGate "$temporary/in-process-gate.json" \
        --slurpfile localRunnerGate "$temporary/local-runner-gate.json" \
        --slurpfile pair "$temporary/pair.json" \
        --arg stateKey replay-719 \
        --arg inProcessEvidencePath 'replay-inprocess/evidence/issue-719-replay-InProcess.json' \
        --arg inProcessEvidenceSha256 "$in_process_evidence_sha" \
        --arg localRunnerEvidencePath 'replay-localrunner/evidence/issue-719-replay-LocalRunner.json' \
        --arg localRunnerEvidenceSha256 "$local_runner_evidence_sha" \
        --arg inProcessGatePath 'replay-inprocess/evidence/replay-profile-gate.json' \
        --arg inProcessGateSha256 "$in_process_gate_sha" \
        --arg localRunnerGatePath 'replay-localrunner/evidence/replay-profile-gate.json' \
        --arg localRunnerGateSha256 "$local_runner_gate_sha" \
        --arg pairPath replay-pair-summary.json --arg pairSha256 "$pair_sha" \
        --arg validatorPath 'scripts/issue-719-replay-evidence.jq' \
        --arg validatorSha256 "$validator_sha" > "$pending_file" ||
        ! jq --exit-status '.passed == true' "$pending_file" >/dev/null ||
        ! replay_snapshot_sources_unchanged; then
        replay_receipt_remove "$temporary"
        replay_receipt_remove "$pending_file"
        return 1
    fi
    mv -- "$pending_file" "$output_file" \
        || { replay_receipt_remove "$temporary"; replay_receipt_remove "$pending_file"; return 1; }
    replay_receipt_remove "$temporary"
}

emit_replay_run_manifest() {
    local inventory="$1" output_file="$2" fingerprint="$3" run_id="$4"
    local revision="$5" dirty_sha256="$6" camera_image="$7" pending_file
    pending_file="$(mktemp "${output_file}.pending.XXXXXX")" || return 1
    replay_receipt_track "$pending_file"
    if ! jq -s \
        --arg fingerprint "$fingerprint" --arg runId "$run_id" \
        --arg revision "$revision" --arg dirtyStateSha256 "$dirty_sha256" \
        --arg cameraImage "$camera_image" '
          {
            schemaVersion: "issue-211-run-manifest-v1",
            worktreeFingerprint: $fingerprint,
            runId: $runId,
            revision: $revision,
            dirtyStateSha256: ($dirtyStateSha256 | ascii_upcase),
            cameraImage: $cameraImage,
            excludedSelf: "run-manifest.json",
            retainedOutputs: sort_by(.path),
            totalBytes: (map(.bytes) | add // 0)
          }
        ' "$inventory" > "$pending_file" ||
        ! jq --exit-status '
          type == "object" and
          (keys | sort) == (["cameraImage", "dirtyStateSha256", "excludedSelf", "retainedOutputs",
            "revision", "runId", "schemaVersion", "totalBytes", "worktreeFingerprint"] | sort) and
          .schemaVersion == "issue-211-run-manifest-v1" and .excludedSelf == "run-manifest.json" and
          (.worktreeFingerprint | type == "string" and length > 0) and
          (.runId | type == "string" and length > 0) and
          (.revision | type == "string" and length > 0) and
          (.dirtyStateSha256 | type == "string" and test("^[0-9A-F]{64}$")) and
          (.cameraImage | type == "string" and length > 0) and
          (.retainedOutputs | type == "array") and
          all(.retainedOutputs[]; type == "object" and
            (keys | sort) == (["bytes", "path", "sha256"] | sort) and
            (.path | type == "string" and length > 0) and
            (.bytes | type == "number" and . >= 0 and . == floor) and
            (.sha256 | type == "string" and test("^[0-9A-F]{64}$"))) and
          ([.retainedOutputs[].path] | unique | length) == (.retainedOutputs | length) and
          (.totalBytes | type == "number" and . >= 0 and . == floor) and
          .totalBytes == ([.retainedOutputs[].bytes] | add // 0)
        ' "$pending_file" >/dev/null ||
        ! mv -- "$pending_file" "$output_file"; then
        replay_receipt_remove "$pending_file"
        return 1
    fi
}

publish_replay_run_manifest() {
    local root="$1" fingerprint="$2" run_id="$3" revision="$4"
    local dirty_sha256="$5" camera_image="$6" temporary pending_file path index=0
    temporary="$(mktemp -d)" || return 1
    replay_receipt_track "$temporary"
    : > "$temporary/inventory.jsonl"
    replay_snapshot_tree "$root" "$temporary/tree.before" \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_regular_paths_from_tree "$temporary/tree.before" "$temporary/paths.before" \
        run-manifest.json \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_snapshot_begin
    while IFS= read -r -d '' path; do
        replay_snapshot_file "$root/$path" "$temporary/output-$index" \
            || { replay_receipt_remove "$temporary"; return 1; }
        jq -cn --arg path "$path" --arg sha256 "$REPLAY_SNAPSHOT_SHA256" \
            --argjson bytes "$REPLAY_SNAPSHOT_SIZE" \
            '{path: $path, bytes: $bytes, sha256: ($sha256 | ascii_upcase)}' \
            >> "$temporary/inventory.jsonl" \
            || { replay_receipt_remove "$temporary"; return 1; }
        index=$((index + 1))
    done < "$temporary/paths.before"
    if ! emit_replay_run_manifest "$temporary/inventory.jsonl" "$temporary/run-manifest.json" \
            "$fingerprint" "$run_id" "$revision" "$dirty_sha256" "$camera_image"; then
        replay_receipt_remove "$temporary"
        return 1
    fi
    pending_file="$(mktemp "$root/.run-manifest.json.pending.XXXXXX")" \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_receipt_track "$pending_file"
    if ! cp -- "$temporary/run-manifest.json" "$pending_file" ||
        ! cmp --silent "$temporary/run-manifest.json" "$pending_file"; then
        replay_receipt_remove "$pending_file"
        replay_receipt_remove "$temporary"
        return 1
    fi
    replay_snapshot_tree "$root" "$temporary/tree.after" "$pending_file" \
        || { replay_receipt_remove "$pending_file"; replay_receipt_remove "$temporary"; return 1; }
    if ! cmp --silent "$temporary/tree.before" "$temporary/tree.after" ||
        ! replay_snapshot_sources_unchanged ||
        ! mv -- "$pending_file" "$root/run-manifest.json"; then
        replay_receipt_remove "$pending_file"
        replay_receipt_remove "$temporary"
        return 1
    fi
    replay_receipt_remove "$temporary"
}

verify_replay_run_manifest() {
    local root="$1"
    shift
    local temporary manifest_snapshot path index
    local -a required_paths=("$@")
    temporary="$(mktemp -d)" || return 1
    replay_receipt_track "$temporary"
    manifest_snapshot="$temporary/run-manifest.json"
    replay_snapshot_begin
    replay_snapshot_file "$root/run-manifest.json" "$manifest_snapshot" \
        || { replay_receipt_remove "$temporary"; return 1; }

    : > "$temporary/inventory.jsonl"
    replay_snapshot_tree "$root" "$temporary/tree.before" \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_regular_paths_from_tree "$temporary/tree.before" "$temporary/paths.before" \
        run-manifest.json \
        || { replay_receipt_remove "$temporary"; return 1; }
    index=0
    while IFS= read -r -d '' path; do
        replay_snapshot_file "$root/$path" "$temporary/output-$index" \
            || { replay_receipt_remove "$temporary"; return 1; }
        jq -cn --arg path "$path" --arg sha256 "$REPLAY_SNAPSHOT_SHA256" \
            --argjson bytes "$REPLAY_SNAPSHOT_SIZE" \
            '{path: $path, bytes: $bytes, sha256: ($sha256 | ascii_upcase)}' \
            >> "$temporary/inventory.jsonl" \
            || { replay_receipt_remove "$temporary"; return 1; }
        index=$((index + 1))
    done < "$temporary/paths.before"
    jq -s 'sort_by(.path)' "$temporary/inventory.jsonl" > "$temporary/inventory.json" \
        || { replay_receipt_remove "$temporary"; return 1; }

    jq --exit-status '
      type == "object" and
      (keys | sort) == (["cameraImage", "dirtyStateSha256", "excludedSelf", "retainedOutputs",
        "revision", "runId", "schemaVersion", "totalBytes", "worktreeFingerprint"] | sort) and
      .schemaVersion == "issue-211-run-manifest-v1" and
      .excludedSelf == "run-manifest.json" and
      (.worktreeFingerprint | type == "string" and length > 0) and
      (.runId | type == "string" and length > 0) and
      (.revision | type == "string" and length > 0) and
      (.dirtyStateSha256 | type == "string" and test("^[0-9A-F]{64}$")) and
      (.cameraImage | type == "string" and length > 0) and
      (.retainedOutputs | type == "array") and
      all(.retainedOutputs[];
        type == "object" and (keys | sort) == (["bytes", "path", "sha256"] | sort) and
        (.path | type == "string" and length > 0) and
        (.bytes | type == "number" and . >= 0 and . == floor) and
        (.sha256 | type == "string" and test("^[0-9A-F]{64}$"))) and
      ([.retainedOutputs[].path] | unique | length) == (.retainedOutputs | length) and
      (.totalBytes | type == "number" and . >= 0 and . == floor) and
      .totalBytes == ([.retainedOutputs[].bytes] | add // 0)
    ' "$manifest_snapshot" >/dev/null || { replay_receipt_remove "$temporary"; return 1; }

    jq -cS '.retainedOutputs | sort_by(.path)' "$manifest_snapshot" \
        > "$temporary/manifest-inventory.json" \
        || { replay_receipt_remove "$temporary"; return 1; }
    jq -cS '.' "$temporary/inventory.json" > "$temporary/actual-inventory.json" \
        || { replay_receipt_remove "$temporary"; return 1; }
    cmp --silent "$temporary/manifest-inventory.json" "$temporary/actual-inventory.json" \
        || { replay_receipt_remove "$temporary"; return 1; }

    for index in "${!required_paths[@]}"; do
        path="${required_paths[$index]}"
        jq --exit-status --arg path "$path" \
            '[.retainedOutputs[] | select(.path == $path)] | length == 1' \
            "$manifest_snapshot" >/dev/null \
            || { replay_receipt_remove "$temporary"; return 1; }
    done
    replay_snapshot_sources_unchanged || { replay_receipt_remove "$temporary"; return 1; }
    replay_snapshot_tree "$root" "$temporary/tree.after" \
        || { replay_receipt_remove "$temporary"; return 1; }
    cmp --silent "$temporary/tree.before" "$temporary/tree.after" \
        || { replay_receipt_remove "$temporary"; return 1; }
    replay_snapshot_sources_unchanged || { replay_receipt_remove "$temporary"; return 1; }
    replay_receipt_remove "$temporary"
}
