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

deploy_phase_file_safe() {
    local path="$1"
    [[ -f "$path" && ! -L "$path" && "$(stat -c '%u:%h:%a' -- "$path" 2>/dev/null)" == "$(id -u):1:600" ]]
}

deploy_phase_commit_json() {
    local json="$1" generation digest
    generation="$(jq -er '.publicationGeneration | numbers | select(. >= 1 and floor == .)' <<< "$json")" || return 1
    digest="$(printf '%s\n' "$(jq -S -c . <<< "$json")" | sha256sum)"; digest="${digest%% *}"
    jq -cn --argjson generation "$generation" --arg digest "$digest" \
      '{schemaVersion:1,generation:$generation,ledgerSha256:$digest}'
}

# The ledger rename is authoritative. The commit records when all mirrors for that
# generation are durable; an interrupted next generation can therefore repair them.
deploy_phase_publish() {
    local phase="$1" json_name="$2" ledger="$3" manifest="$4" evidence="$5" commit="$6"
    local json commit_json
    json="${!json_name}"
    if [[ "$(jq -r '.phaseStatus' <<< "$json")" == passed && -n "${DEPLOY_PRIVATE_UPLOAD_REGISTRY:-}" ]]; then
        jq -e 'length == 0' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" >/dev/null 2>&1 || {
            deploy_fail "$phase" cleanup "private-cleanup-incomplete"
            return 1
        }
    fi
    json="$(jq -c '.publicationGeneration = ((.publicationGeneration // 0) + 1)' <<< "$json")" || return 1
    printf -v "$json_name" '%s' "$json"
    commit_json="$(deploy_phase_commit_json "$json")" || return 1
    deploy_publish_json "$ledger" "$json" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != "abrupt-after-$phase-ledger" ]] || exit 75
    deploy_publish_json "$manifest" "$json" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != "abrupt-after-$phase-manifest" ]] || exit 75
    deploy_publish_json "$evidence" "$json" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != "abrupt-after-$phase-evidence" ]] || exit 75
    deploy_publish_json "$commit" "$commit_json" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != "abrupt-after-$phase-commit" ]] || exit 75
}

deploy_phase_recover_publication() {
    local ledger="$1" manifest="$2" evidence="$3" commit="$4" validator="$5" ledger_json expected_commit ledger_generation commit_generation path
    shift 5
    deploy_phase_file_safe "$ledger" || { deploy_fail state phase "unsafe-phase-ledger"; return 1; }
    ledger_json="$(jq -c . "$ledger" 2>/dev/null)" || { deploy_fail state phase "invalid-phase-json"; return 1; }
    "$validator" "$ledger" "$@" || return 1
    expected_commit="$(deploy_phase_commit_json "$ledger_json")" || { deploy_fail state phase "invalid-publication-generation"; return 1; }
    ledger_generation="$(jq -r '.generation' <<< "$expected_commit")"
    if [[ -e "$commit" || -L "$commit" ]]; then
        deploy_phase_file_safe "$commit" || { deploy_fail state phase "unsafe-phase-commit"; return 1; }
        jq -e '.schemaVersion == 1 and (.generation | numbers) and (.ledgerSha256 | test("^[0-9a-f]{64}$"))' "$commit" >/dev/null 2>&1 ||
          { deploy_fail state phase "invalid-phase-commit"; return 1; }
        commit_generation="$(jq -r '.generation' "$commit")"
        if [[ "$commit_generation" == "$ledger_generation" ]]; then
            [[ "$(jq -S -c . "$commit")" == "$(jq -S -c . <<< "$expected_commit")" ]] ||
              { deploy_fail state phase "committed-ledger-mismatch"; return 1; }
            for path in "$manifest" "$evidence"; do
                if ! deploy_phase_file_safe "$path" || [[ "$(jq -S -c . "$path" 2>/dev/null)" != "$(jq -S -c . <<< "$ledger_json")" ]]; then
                    deploy_fail state phase "committed-phase-mirror-mismatch"
                    return 1
                fi
            done
            return 0
        fi
        [[ "$commit_generation" =~ ^[0-9]+$ && "$ledger_generation" == "$((commit_generation + 1))" ]] ||
          { deploy_fail state phase "publication-generation-gap"; return 1; }
    else
        [[ "$ledger_generation" == 1 ]] || { deploy_fail state phase "phase-commit-missing"; return 1; }
    fi
    if ! deploy_publish_json "$manifest" "$ledger_json" || ! deploy_publish_json "$evidence" "$ledger_json" ||
      ! deploy_publish_json "$commit" "$expected_commit"; then
        deploy_fail state phase "publication-recovery-failed"
        return 1
    fi
}

deploy_require_no_orphan_phase_files() {
    local ledger="$1"
    shift
    [[ -e "$ledger" || -L "$ledger" ]] && return 0
    local path
    for path in "$@"; do
        [[ ! -e "$path" && ! -L "$path" ]] || { deploy_fail state phase "orphan-phase-companion"; return 1; }
    done
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

deploy_require_phase_files_match() {
    deploy_phase_recover_publication "$@"
}
