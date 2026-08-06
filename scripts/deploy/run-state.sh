#!/usr/bin/env bash

deploy_prepare_run_state() {
    local state_root="$1" evidence_root="$2" lock_path current_uid lock_identity
    deploy_validate_output_root "$state_root" state-root || return 1
    deploy_validate_output_root "$evidence_root" evidence-root || return 1
    [[ "$state_root" != "$evidence_root" && "$state_root" != "$evidence_root/"* && "$evidence_root" != "$state_root/"* ]] ||
        deploy_fail validate output-roots "state-evidence-overlap" || return 1
    umask 077
    install -d -m 700 -- "$state_root" "$evidence_root" 2>/dev/null || deploy_fail state roots "output-root-create-failed" || return 1
    lock_path="$state_root/run.lock"
    current_uid="$(id -u)"
    if [[ -L "$lock_path" ]]; then deploy_fail state lock "unsafe-lock-entry"; return 1; fi
    if [[ ! -e "$lock_path" ]]; then
        if ! (set -o noclobber; umask 077; : > "$lock_path") 2>/dev/null; then
            [[ -e "$lock_path" && ! -L "$lock_path" ]] || { deploy_fail state lock "lock-create-failed"; return 1; }
        fi
    fi
    [[ -f "$lock_path" && ! -L "$lock_path" && "$(stat -c '%u:%h' -- "$lock_path" 2>/dev/null)" == "$current_uid:1" ]] ||
        { deploy_fail state lock "unsafe-lock-entry"; return 1; }
    { exec 201<>"$lock_path"; } 2>/dev/null || { deploy_fail state lock "lock-open-failed"; return 1; }
    lock_identity="$(stat -Lc '%d:%i' "/proc/$$/fd/201" 2>/dev/null)" || { deploy_fail state lock "lock-identity-failed"; return 1; }
    [[ "$lock_identity" == "$(stat -Lc '%d:%i' -- "$lock_path" 2>/dev/null)" ]] || { deploy_fail state lock "lock-entry-changed"; return 1; }
    flock -n 201 2>/dev/null || deploy_fail state lock "run-already-active" || return 1
    chmod 600 "$lock_path" 2>/dev/null || deploy_fail state lock "lock-mode-failed" || return 1
    DEPLOY_MANIFEST="$state_root/manifest.json"
    # shellcheck disable=SC2034 # Used by preflight.sh after this library is sourced.
    DEPLOY_EVIDENCE="$evidence_root/preflight.json"
}

deploy_publish_json() {
    local destination="$1" json="$2" temporary
    temporary="$(mktemp "${destination}.tmp.XXXXXX" 2>/dev/null)" || return 1
    chmod 600 "$temporary" 2>/dev/null || { rm -f -- "$temporary" 2>/dev/null; return 1; }
    printf '%s\n' "$json" > "$temporary"
    jq -e . "$temporary" >/dev/null 2>&1 || { rm -f -- "$temporary" 2>/dev/null; return 1; }
    if [[ "${DEPLOY_TEST_FAILPOINT:-}" == before-manifest-rename && "$destination" == "$DEPLOY_MANIFEST" ]]; then
        rm -f "$temporary" 2>/dev/null
        return 75
    fi
    mv -fT -- "$temporary" "$destination" 2>/dev/null
}

deploy_clear_evidence() {
    if [[ -e "$DEPLOY_EVIDENCE" || -L "$DEPLOY_EVIDENCE" ]]; then
        [[ ! -d "$DEPLOY_EVIDENCE" ]] || { deploy_fail state evidence "unsafe-evidence-entry"; return 1; }
        rm -f -- "$DEPLOY_EVIDENCE" 2>/dev/null || deploy_fail state evidence "stale-evidence-remove-failed" || return 1
    fi
}

deploy_evidence_json() {
    jq -c '{schemaVersion,runId,mode,inventorySha256,source,targets,checks,startedAt,
      updatedAt,completedAt,phaseStatus}' <<< "$1"
}

deploy_initial_manifest() {
    local run_id="$1" mode="$2" inventory_hash="$3" revision="$4" dirty="$5" started="$6"
    jq -n --arg run "$run_id" --arg mode "$mode" --arg hash "$inventory_hash" --arg revision "$revision" \
        --arg dirty "$dirty" --arg started "$started" \
        '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,source:{revision:$revision,dirtyDisposition:$dirty},
          targets:[],checks:[],startedAt:$started,updatedAt:$started,phaseStatus:"running"}'
}

deploy_require_resume_match() {
    local manifest="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree_state="$6"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg worktree "$worktree_state" \
      '.schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and
       .source.revision == $revision and .source.worktreeState == $worktree' \
      "$manifest" >/dev/null 2>&1 || deploy_fail state resume "resume-contract-mismatch" || return 1
}

deploy_require_completed_evidence_match() {
    local manifest="$1" evidence="$2" current_uid
    [[ "$(jq -r '.phaseStatus' <<< "$manifest")" == passed ]] || return 0
    current_uid="$(id -u)"
    [[ -f "$evidence" && ! -L "$evidence" && "$(stat -c '%u:%h:%a' -- "$evidence" 2>/dev/null)" == "$current_uid:1:600" ]] ||
        { deploy_fail state resume "completed-check-or-target-mismatch"; return 1; }
    jq -e --argjson manifest "$manifest" '
      .schemaVersion == $manifest.schemaVersion and .runId == $manifest.runId and .mode == $manifest.mode and
      .inventorySha256 == $manifest.inventorySha256 and .source == $manifest.source and
      .phaseStatus == "passed" and .targets == $manifest.targets and .checks == $manifest.checks' \
      "$evidence" >/dev/null 2>&1 || deploy_fail state resume "completed-check-or-target-mismatch" || return 1
}
