#!/usr/bin/env bash

DEPLOY_NORMAL_MEASURE_ACTIVE=false

deploy_acceptance_normal_mark_failed() {
    local status="${1:-1}"
    trap - ERR
    if [[ "${DEPLOY_NORMAL_MEASURE_ACTIVE:-false}" == true ]]; then
        deploy_measure_mark_failed "$status" >/dev/null 2>&1 || true
        DEPLOY_NORMAL_MEASURE_ACTIVE=false
    fi
    return "$status"
}

deploy_acceptance_normal_measure_paths() {
    local state_dir="$1" evidence_dir="$2" workload="$3" root
    root="$state_dir/acceptance-campaign-normal-flow/$workload"
    DEPLOY_NORMAL_LEDGER="$root/measure-ledger.json"
    DEPLOY_NORMAL_MANIFEST="$root/measure-manifest.json"
    DEPLOY_NORMAL_EVIDENCE="$evidence_dir/acceptance-support/normal-flow-$workload.json"
    DEPLOY_NORMAL_COMMIT="$root/measure-commit.json"
}

deploy_acceptance_normal_validate_measure_directories() {
    local state_dir="$1" evidence_dir="$2" workload="$3" path
    for path in "$state_dir/acceptance-campaign-normal-flow" "$state_dir/acceptance-campaign-normal-flow/$workload" \
      "$evidence_dir/acceptance-support"; do
        [[ -d "$path" && ! -L "$path" && "$(stat -c '%u:%a' -- "$path" 2>/dev/null)" == "$(id -u):700" ]] ||
          { deploy_fail acceptance-normal directory unsafe; return 1; }
    done
}

deploy_acceptance_normal_validate_measure() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" workload="$6" inventory="$7" expected_profile="$8"
    local width height format bytes
    case "$workload" in
        W1) width=1936; height=1216; format=Mono16; bytes=4708352 ;;
        W2) width=3096; height=2080; format=BayerRggb16; bytes=12879360 ;;
        *) return 1 ;;
    esac
    deploy_measure_validate_candidate "$path" "$run_id" "$mode" "$hash" "$revision" "$workload" "$inventory" "$run_id-${workload,,}" || return 1
    jq -e --arg workload "$workload" --arg format "$format" --argjson width "$width" --argjson height "$height" --argjson bytes "$bytes" --argjson expected "$expected_profile" '
      .phaseStatus == "passed" and (.targets | length) == 1 and
      all(.targets[];
        .status == "measured" and .profile.workload == $workload and .profile.width == $width and .profile.height == $height and
        (.profile | del(.workload,.configSha256)) == $expected and (.profile.configSha256 | test("^[0-9a-f]{64}$")) and
        .profile.pixelFormat == $format and .profile.seed == 2025 and .profile.warmupOperations == 5 and
        .profile.measuredOperations == 30 and .profile.concurrency == 1 and
        .warmupCompleted == 5 and .measuredCompleted == 30 and .warmupStart == .warmup.startSequence and
        .measuredStart == .measured.startSequence and .warmup.endSequence == .measuredStart and
        .warmup.count == 5 and .measured.count == 30 and
        [.warmup.captures[].captureSequence] == [range(.warmup.startSequence + 1; .warmup.endSequence + 1)] and
        [.measured.captures[].captureSequence] == [range(.measured.startSequence + 1; .measured.endSequence + 1)] and
        all(.warmup.captures[],.measured.captures[];
          (.captureId | test("^[0-9a-fA-F-]{36}$")) and (.rawArtifactId | test("^[0-9a-fA-F-]{36}$")) and
          (.rawChecksumSha256 | test("^[0-9a-fA-F]{64}$")) and .rawByteLength == $bytes) and
        ([.warmup.captures[].captureId,.measured.captures[].captureId] | length == (unique | length)) and
        ([.warmup.captures[].rawArtifactId,.measured.captures[].rawArtifactId] | length == (unique | length)) and
        .before.telemetry.sampleCount == 0 and (.before.timings | length) == 0 and
        (.after.timings | length) > 0 and all(.after.timings[]; .sampleCount > 0) and
        all(.after.queues.raw,.after.queues.processing,.after.queues.outbox;
          .pendingCount == 0 and .pendingBytes == 0 and .leasedCount == 0 and .quarantineCount == 0 and .terminalCount == 0) and
        .after.queues.lanes.pendingCount == 0 and .after.queues.lanes.pendingBytes == 0 and
        .after.queues.lanes.leasedCount == 0 and .after.queues.lanes.quarantineCount == 0 and
        all(.after.queues.lanes.lanes[]; .pendingCount == 0 and .pendingBytes == 0 and .leasedCount == 0 and .quarantineCount == 0))
    ' "$path" >/dev/null 2>&1 || { deploy_fail acceptance-normal measure invalid-or-noncanonical; return 1; }
}

deploy_acceptance_normal_require_measure() {
    local state_dir="$1" evidence_dir="$2" run_id="$3" mode="$4" hash="$5" revision="$6" workload="$7" inventory="$8" expected_profile
    expected_profile="$(jq -ce --arg workload "$workload" '.workloadIdentities.profiles[$workload]' "$state_dir/acceptance-ledger.json")" || return 1
    deploy_acceptance_normal_measure_paths "$state_dir" "$evidence_dir" "$workload"
    deploy_require_phase_files_match "$DEPLOY_NORMAL_LEDGER" "$DEPLOY_NORMAL_MANIFEST" "$DEPLOY_NORMAL_EVIDENCE" "$DEPLOY_NORMAL_COMMIT" \
      deploy_acceptance_normal_validate_measure "$run_id" "$mode" "$hash" "$revision" "$workload" "$inventory" "$expected_profile"
}

deploy_acceptance_normal_run_measure() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6" tree="$7" workload="$8" state_dir="$9" evidence_dir="${10}"
    deploy_acceptance_normal_measure_paths "$state_dir" "$evidence_dir" "$workload"
    deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
    if [[ -e "$DEPLOY_NORMAL_LEDGER" || -L "$DEPLOY_NORMAL_LEDGER" ]]; then
        deploy_acceptance_normal_validate_measure_directories "$state_dir" "$evidence_dir" "$workload" || return 1
        deploy_require_phase_files_match "$DEPLOY_NORMAL_LEDGER" "$DEPLOY_NORMAL_MANIFEST" "$DEPLOY_NORMAL_EVIDENCE" "$DEPLOY_NORMAL_COMMIT" \
          deploy_measure_validate_candidate "$run_id" "$mode" "$hash" "$revision" "$workload" "$inventory" "$run_id-${workload,,}" || return 1
        if [[ "$(jq -r '.phaseStatus' "$DEPLOY_NORMAL_LEDGER")" == passed ]]; then
            deploy_acceptance_normal_require_measure "$state_dir" "$evidence_dir" "$run_id" "$mode" "$hash" "$revision" "$workload" "$inventory"
            return
        fi
    fi
    DEPLOY_MEASURE_JSON=""
    if ! deploy_run_measure "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$workload" "normal-flow-$workload"; then
        deploy_acceptance_normal_mark_failed 1 >/dev/null 2>&1 || true
        return 1
    fi
    if ! deploy_acceptance_verify_source_snapshot "$revision" "$tree" ||
       ! deploy_acceptance_normal_require_measure "$state_dir" "$evidence_dir" "$run_id" "$mode" "$hash" "$revision" "$workload" "$inventory"; then
        deploy_acceptance_normal_mark_failed 1 >/dev/null 2>&1 || true
        return 1
    fi
    DEPLOY_NORMAL_MEASURE_ACTIVE=false
}

deploy_acceptance_normal_build_artifact() {
    local acceptance_ledger="$1" scenario="$2" w1_path="$3" w2_path="$4" w1_evidence="$5" w2_evidence="$6"
    local w1 w2 w1_length w2_length w1_sha w2_sha
    w1="$(jq -c . "$w1_path")"; w2="$(jq -c . "$w2_path")"
    jq -e --argjson w1 "$w1" '
      .targets[0].target == $w1.targets[0].target and .targets[0].deviceId == $w1.targets[0].deviceId and
      .targets[0].warmupStart == $w1.targets[0].measured.endSequence and
      ([($w1.targets[0].warmup.captures + $w1.targets[0].measured.captures + .targets[0].warmup.captures + .targets[0].measured.captures)[].captureId | ascii_downcase] |
        length == (unique | length)) and
      ([($w1.targets[0].warmup.captures + $w1.targets[0].measured.captures + .targets[0].warmup.captures + .targets[0].measured.captures)[].rawArtifactId | ascii_downcase] |
        length == (unique | length))
    ' "$w2_path" >/dev/null 2>&1 || { deploy_fail acceptance-normal measure continuity-mismatch; return 1; }
    w1_length="$(stat -c %s -- "$w1_evidence")"; w2_length="$(stat -c %s -- "$w2_evidence")"
    w1_sha="$(sha256sum "$w1_evidence")"; w1_sha="${w1_sha%% *}"
    w2_sha="$(sha256sum "$w2_evidence")"; w2_sha="${w2_sha%% *}"
    jq -S -cn --argjson ledger "$acceptance_ledger" --argjson scenario "$scenario" --argjson w1 "$w1" --argjson w2 "$w2" \
      --arg w1Sha "$w1_sha" --arg w2Sha "$w2_sha" --argjson w1Length "$w1_length" --argjson w2Length "$w2_length" '
      def rawOutputs($prefix; $target):
        [$target.measured.captures | to_entries[] |
          {id:($prefix + "-raw-" + ((.key + 1) | tostring)),byteLength:.value.rawByteLength,sha256:(.value.rawChecksumSha256 | ascii_downcase)}];
      {schemaVersion:1,runId:$ledger.runId,scenarioId:$scenario.id,inventorySha256:$ledger.inventorySha256,
       sourceRevision:$ledger.sourceRevision,sourceTree:$ledger.sourceTree,classification:$scenario.classification,
       executionClass:$scenario.executionClass,evidenceSource:$scenario.evidenceSource,workloads:$scenario.workloads,
       outcome:"passed",startedAt:$w1.startedAt,completedAt:$w2.completedAt,
       assertions:[
         {id:"committed-measure-evidence",passed:true},{id:"canonical-w1-w2-profiles",passed:true},
         {id:"exact-warmup-counts",passed:true},{id:"exact-measured-counts",passed:true},
         {id:"ordered-capture-windows",passed:true},{id:"unique-capture-output-identities",passed:true},
         {id:"raw-byte-lengths-canonical",passed:true},{id:"measured-capture-correctness",passed:true},
         {id:"durable-queues-drained",passed:true},{id:"fresh-measurement-windows",passed:true},
         {id:"runtime-timings-recorded",passed:true}],
       outputs:([{id:"w1-measure-evidence",byteLength:$w1Length,sha256:$w1Sha},
                 {id:"w2-measure-evidence",byteLength:$w2Length,sha256:$w2Sha}] +
                rawOutputs("w1";$w1.targets[0]) + rawOutputs("w2";$w2.targets[0])),
       measurements:[
         {id:"w1-warmup-captures",value:5,unit:"captures"},{id:"w1-measured-captures",value:30,unit:"captures"},
         {id:"w1-measured-raw-bytes",value:([$w1.targets[0].measured.captures[].rawByteLength]|add),unit:"bytes"},
         {id:"w2-warmup-captures",value:5,unit:"captures"},{id:"w2-measured-captures",value:30,unit:"captures"},
         {id:"w2-measured-raw-bytes",value:([$w2.targets[0].measured.captures[].rawByteLength]|add),unit:"bytes"},
         {id:"total-measured-captures",value:60,unit:"captures"},
         {id:"total-measured-raw-bytes",value:([$w1.targets[0].measured.captures[].rawByteLength,$w2.targets[0].measured.captures[].rawByteLength]|add),unit:"bytes"}]}
    '
}

deploy_run_acceptance_normal() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6" tree="$7" scenario_id="$8"
    local state_dir evidence_dir runtime artifact ledger scenario artifact_json canonical
    [[ "$scenario_id" == normal-flow ]] || { deploy_fail acceptance-normal scenario unsupported; return 1; }
    [[ "$worktree" == clean ]] || { deploy_fail acceptance-normal source dirty-worktree-rejected; return 1; }
    [[ "$(jq '.cameraAgents | length' "$inventory")" == 1 ]] || { deploy_fail acceptance-normal topology one-cameraagent-required; return 1; }
    deploy_run_acceptance "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" || return 1
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    deploy_acceptance_normal_run_measure "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" W1 "$state_dir" "$evidence_dir" || return 1
    deploy_acceptance_normal_run_measure "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" W2 "$state_dir" "$evidence_dir" || return 1
    deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
    deploy_acceptance_normal_measure_paths "$state_dir" "$evidence_dir" W1
    local w1_ledger="$DEPLOY_NORMAL_LEDGER" w1_evidence="$DEPLOY_NORMAL_EVIDENCE"
    deploy_acceptance_normal_measure_paths "$state_dir" "$evidence_dir" W2
    local w2_ledger="$DEPLOY_NORMAL_LEDGER" w2_evidence="$DEPLOY_NORMAL_EVIDENCE"
    ledger="$(jq -c . "$state_dir/acceptance-ledger.json")"; scenario="$(jq -c --arg id "$scenario_id" '.scenarios[] | select(.id == $id)' <<< "$ledger")"
    artifact_json="$(deploy_acceptance_normal_build_artifact "$ledger" "$scenario" "$w1_ledger" "$w2_ledger" "$w1_evidence" "$w2_evidence")" || return 1
    runtime="$state_dir/acceptance-campaign-normal-flow"; artifact="$runtime/normal-flow.json"; canonical="$(jq -S -c . <<< "$artifact_json")"
    if [[ -e "$artifact" || -L "$artifact" ]]; then
        if ! deploy_acceptance_artifact_file_safe "$artifact" || [[ "$(jq -S -c . "$artifact" 2>/dev/null)" != "$canonical" ]]; then
            deploy_fail acceptance-normal artifact immutable-mismatch
            return 1
        fi
    else
        deploy_acceptance_publish_artifact "$artifact" "$canonical" || return 1
    fi
    deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
    deploy_run_acceptance_record "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" "$scenario_id" "$artifact"
}
