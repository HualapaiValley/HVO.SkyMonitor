#!/usr/bin/env bash

deploy_acceptance_campaign_runtime_publish() {
    deploy_phase_publish acceptance-campaign DEPLOY_MEASURE_JSON "$DEPLOY_MEASURE_LEDGER" "$DEPLOY_MEASURE_MANIFEST" \
      "$DEPLOY_MEASURE_EVIDENCE" "$DEPLOY_MEASURE_COMMIT"
}

deploy_acceptance_campaign_validate_runtime() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" scenario="$6"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg scenario "$scenario" '
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and
      .sourceRevision == $revision and .scenarioId == $scenario and .workload == "W2" and
      .outageCaptureCount == 10 and .captureIntervalSeconds == 25 and
      (.publicationGeneration | numbers) >= 1 and (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      (.logicState == "running" or .logicState == "stop-intent" or .logicState == "stopped" or .logicState == "restored") and
      (.targets | type == "array" and length == 1 and all(.[].controlAttempts; type == "array")) and
      ((keys - ["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","scenarioId","workload",
        "outageCaptureCount","captureIntervalSeconds","logicState","phaseStatus","startedAt","updatedAt","completedAt","targets",
        "baselineSequence","expectedEnd","outageStartedAt","facts","outagePendingCount","outagePendingBytes","outageSeconds",
        "recoverySeconds","drainRate","artifactByteLength","artifactSha256"] | length) == 0) and
      (if .phaseStatus == "passed" then .logicState == "restored" and (.baselineSequence | numbers) >= 0 and
        .expectedEnd == (.baselineSequence + 10) and .facts.startSequence == .baselineSequence and .facts.endSequence == .expectedEnd and
        .facts.count == 10 and .facts.drained == true and .facts.correctness == true and
        (.outagePendingCount | numbers) > 0 and (.outagePendingBytes | numbers) > 0 and (.outageSeconds | numbers) >= 0 and
        (.recoverySeconds | numbers) >= 1 and (.drainRate | numbers) > 0.04 and
        (.artifactByteLength | numbers) > 0 and (.artifactSha256 | test("^[0-9a-f]{64}$"))
       else true end)
    ' "$path" >/dev/null 2>&1 || { deploy_fail acceptance-campaign ledger invalid-or-tampered; return 1; }
}

deploy_acceptance_campaign_prepare_directory() {
    local path="$1"
    if [[ -e "$path" || -L "$path" ]]; then
        [[ -d "$path" && ! -L "$path" && "$(stat -c '%u:%a' -- "$path" 2>/dev/null)" == "$(id -u):700" ]] ||
          { deploy_fail acceptance-campaign directory unsafe; return 1; }
    else
        install -d -m 700 -- "$path" 2>/dev/null || { deploy_fail acceptance-campaign directory create-failed; return 1; }
    fi
}

deploy_acceptance_campaign_control_cleanup() {
    local target="${DEPLOY_CAMPAIGN_TARGET:-}" logic="${DEPLOY_CAMPAIGN_LOGIC:-}"
    trap - ERR
    if [[ -n "$target" && -n "${DEPLOY_CAMPAIGN_TARGET_REMOTE:-}" && -n "${DEPLOY_CAMPAIGN_COOKIES:-}" ]]; then
        deploy_measure_capture_control "$target" "$DEPLOY_CAMPAIGN_TARGET_REMOTE" "$DEPLOY_CAMPAIGN_PRIVATE_ROOT" \
          "$DEPLOY_CAMPAIGN_RENDER_ROOT" "$DEPLOY_CAMPAIGN_COOKIES" pause "$DEPLOY_CAMPAIGN_RUN_ID" failure-pause Paused true >/dev/null 2>&1 || true
    fi
    if [[ -n "$logic" && "${DEPLOY_CAMPAIGN_LOGIC_STOPPED:-false}" == true ]]; then
        if deploy_up_compose_mutation "$logic" "$(jq -r '.dockerContext' <<< "$logic")" "$DEPLOY_CAMPAIGN_LOGIC_PROJECT" \
          "$DEPLOY_CAMPAIGN_LOGIC_ENV" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" start logichost >/dev/null 2>&1; then
            DEPLOY_CAMPAIGN_LOGIC_STOPPED=false
            [[ -z "${DEPLOY_MEASURE_JSON:-}" ]] || DEPLOY_MEASURE_JSON="$(jq -c '.logicState="restored"' <<< "$DEPLOY_MEASURE_JSON")"
        fi
    fi
    deploy_bootstrap_cleanup_private_remote best-effort >/dev/null 2>&1 || true
}

deploy_acceptance_campaign_mark_failed() {
    local status="${1:-$?}" now
    deploy_acceptance_campaign_control_cleanup
    if [[ -n "${DEPLOY_MEASURE_JSON:-}" && -n "${DEPLOY_MEASURE_LEDGER:-}" ]]; then
        [[ "$(jq -r '.phaseStatus' <<< "$DEPLOY_MEASURE_JSON")" != passed ]] || return "$status"
        now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        DEPLOY_MEASURE_JSON="$(jq -c --arg now "$now" '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_MEASURE_JSON")"
        deploy_acceptance_campaign_runtime_publish >/dev/null 2>&1 || true
    fi
    return "$status"
}

deploy_acceptance_campaign_build_artifact() {
    local acceptance_ledger="$1" scenario="$2" facts="$3" started="$4" completed="$5" outage_seconds="$6" recovery_seconds="$7"
    local pending="$8" pending_bytes="$9" drain_rate="${10}" raw_bytes
    jq -e '
      (keys | sort) == (["captures","correctness","count","drained","endSequence","startSequence"] | sort) and
      .count == 10 and .endSequence == (.startSequence + 10) and .drained == true and .correctness == true and
      (.captures | type == "array" and length == 10) and
      all(range(0; 10); . as $index | $facts.captures[$index] as $capture |
        $capture.captureSequence == ($facts.startSequence + $index + 1) and
        ($capture.captureId | test("^[0-9a-fA-F-]{36}$")) and
        ($capture.rawArtifactId | test("^[0-9a-fA-F-]{36}$")) and
        ($capture.rawChecksumSha256 | test("^[0-9a-fA-F]{64}$")) and
        ($capture.rawByteLength | numbers) > 0) and
      ([.captures[].captureId | ascii_downcase] | length == (unique | length)) and
      ([.captures[].rawArtifactId | ascii_downcase] | length == (unique | length))
    ' --argjson facts "$facts" <<< "$facts" >/dev/null || { deploy_fail acceptance-campaign artifact invalid-capture-facts; return 1; }
    raw_bytes="$(jq '[.captures[].rawByteLength] | add' <<< "$facts")" || return 1
    jq -S -cn --argjson ledger "$acceptance_ledger" --argjson scenario "$scenario" --argjson facts "$facts" \
      --arg started "$started" --arg completed "$completed" --argjson outageSeconds "$outage_seconds" \
      --argjson recoverySeconds "$recovery_seconds" --argjson pending "$pending" --argjson pendingBytes "$pending_bytes" \
      --argjson drainRate "$drain_rate" --argjson rawBytes "$raw_bytes" '
      {schemaVersion:1,runId:$ledger.runId,scenarioId:$scenario.id,inventorySha256:$ledger.inventorySha256,
       sourceRevision:$ledger.sourceRevision,sourceTree:$ledger.sourceTree,classification:$scenario.classification,
       executionClass:$scenario.executionClass,evidenceSource:$scenario.evidenceSource,workloads:$scenario.workloads,
       outcome:"passed",startedAt:$started,completedAt:$completed,
       assertions:[
         {id:"cameraagent-captured-during-outage",passed:true},
         {id:"outage-backlog-observed",passed:true},
         {id:"logic-restored",passed:true},
         {id:"exact-capture-window-converged",passed:true},
         {id:"raw-checksums-and-lineage-match",passed:true},
         {id:"durable-queues-drained",passed:true},
         {id:"drain-capacity-exceeds-arrival",passed:true}],
       outputs:[$facts.captures[] | {id:("capture-" + (.captureSequence|tostring)),byteLength:.rawByteLength,sha256:(.rawChecksumSha256|ascii_downcase)}],
       measurements:[
         {id:"outage-captures",value:$facts.count,unit:"captures"},
         {id:"outage-raw-bytes",value:$rawBytes,unit:"bytes"},
         {id:"outage-seconds",value:$outageSeconds,unit:"seconds"},
         {id:"peak-pending-records",value:$pending,unit:"count"},
         {id:"peak-pending-bytes",value:$pendingBytes,unit:"bytes"},
         {id:"recovery-seconds",value:$recoverySeconds,unit:"seconds"},
         {id:"configured-arrival-rate",value:0.04,unit:"captures-per-second"},
         {id:"recovered-drain-rate",value:$drainRate,unit:"captures-per-second"}]}'
}

deploy_run_acceptance_campaign() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6" tree="$7" scenario_id="$8"
    local state_dir evidence_dir runtime_root render_root private_root now target name target_root target_remote response cookies device profile
    local logic logic_root logic_remote central_headers logic_context logic_project logic_env start_sequence expected_baseline measured_start deadline interval facts baseline_facts
    local operations status pending pending_bytes outage_started outage_started_elapsed outage_ended outage_seconds recovery_started recovery_seconds drain_rate
    local artifact_input artifact_json artifact_length artifact_sha acceptance_ledger scenario completed runtime_status runtime_logic_state recover_runtime=false
    [[ "$scenario_id" == logichost-network-outage ]] || { deploy_fail acceptance-campaign scenario unsupported; return 1; }
    [[ "$worktree" == clean ]] || { deploy_fail acceptance-campaign source dirty-worktree-rejected; return 1; }
    deploy_run_acceptance "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" || return 1
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    deploy_require_phase_files_match "$state_dir/measure-ledger.json" "$state_dir/measure-manifest.json" "$evidence_dir/measure.json" \
      "$state_dir/measure-commit.json" deploy_measure_validate_candidate "$run_id" "$mode" "$hash" "$revision" W2 "$inventory" || return 1
    jq -e '.phaseStatus == "passed" and .workload == "W2" and
      all(.targets[]; .status == "measured" and .warmupCompleted == 5 and .measuredCompleted == 30)' \
      "$state_dir/measure-ledger.json" >/dev/null 2>&1 || { deploy_fail acceptance-campaign measure canonical-W2-required; return 1; }

    runtime_root="$state_dir/acceptance-campaign-$scenario_id"; render_root="$runtime_root/rendered"; private_root="$runtime_root/private"
    deploy_acceptance_campaign_prepare_directory "$runtime_root" || return 1
    deploy_acceptance_campaign_prepare_directory "$render_root" || return 1
    deploy_acceptance_campaign_prepare_directory "$private_root" || return 1
    DEPLOY_MEASURE_LEDGER="$runtime_root/ledger.json"; DEPLOY_MEASURE_MANIFEST="$runtime_root/manifest.json"
    DEPLOY_MEASURE_EVIDENCE="$runtime_root/evidence.json"; DEPLOY_MEASURE_COMMIT="$runtime_root/commit.json"
    DEPLOY_CAMPAIGN_PRIVATE_ROOT="$private_root"; DEPLOY_CAMPAIGN_RENDER_ROOT="$render_root"; DEPLOY_CAMPAIGN_RUN_ID="$run_id"
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"
    logic="$(jq -c '.logicHost' "$inventory")"; logic_root="$(jq -r '.runtimeRoot' <<< "$logic")"; logic_remote="$logic_root/.hvo-deploy/campaign-$run_id"
    target="$(jq -c '.cameraAgents[0]' "$inventory")"; name="$(jq -r '.name' <<< "$target")"; target_root="$(jq -r '.runtimeRoot' <<< "$target")"
    target_remote="$target_root/.hvo-deploy/campaign-$run_id"
    [[ "$(jq '.cameraAgents | length' "$inventory")" == 1 ]] || { deploy_fail acceptance-campaign topology one-cameraagent-required; return 1; }
    DEPLOY_CAMPAIGN_TARGET="$target"; DEPLOY_CAMPAIGN_LOGIC="$logic"
    device="$(jq -r '.targets[0].deviceId' "$state_dir/measure-ledger.json")"; profile="$(jq -c '.targets[0].profile' "$state_dir/measure-ledger.json")"
    interval=25; deadline=$(( $(date +%s) + 900 ))
    logic_context="$(jq -r '.dockerContext' <<< "$logic")"; logic_project="$(jq -r '.deployment.resources.project' "$inventory")-logic"
    logic_env="$state_dir/up-rendered/$(jq -r '.name' <<< "$logic").env"
    DEPLOY_CAMPAIGN_LOGIC_PROJECT="$logic_project"; DEPLOY_CAMPAIGN_LOGIC_ENV="$logic_env"
    function deploy_measure_publish() { deploy_acceptance_campaign_runtime_publish; }
    deploy_require_no_orphan_phase_files "$DEPLOY_MEASURE_LEDGER" "$DEPLOY_MEASURE_MANIFEST" "$DEPLOY_MEASURE_EVIDENCE" "$DEPLOY_MEASURE_COMMIT" || return 1
    if [[ -e "$DEPLOY_MEASURE_LEDGER" || -L "$DEPLOY_MEASURE_LEDGER" ]]; then
        deploy_require_phase_files_match "$DEPLOY_MEASURE_LEDGER" "$DEPLOY_MEASURE_MANIFEST" "$DEPLOY_MEASURE_EVIDENCE" "$DEPLOY_MEASURE_COMMIT" \
          deploy_acceptance_campaign_validate_runtime "$run_id" "$mode" "$hash" "$revision" "$scenario_id" || return 1
        DEPLOY_MEASURE_JSON="$(jq -c . "$DEPLOY_MEASURE_LEDGER")" || return 1
        runtime_status="$(jq -r '.phaseStatus' <<< "$DEPLOY_MEASURE_JSON")"; runtime_logic_state="$(jq -r '.logicState' <<< "$DEPLOY_MEASURE_JSON")"
        if [[ "$runtime_status" == passed ]]; then
            artifact_input="$runtime_root/$scenario_id.json"
            deploy_acceptance_artifact_file_safe "$artifact_input" || { deploy_fail acceptance-campaign artifact missing-or-unsafe; return 1; }
            [[ "$(stat -c %s -- "$artifact_input")" == "$(jq -r '.artifactByteLength' <<< "$DEPLOY_MEASURE_JSON")" ]] || return 1
            artifact_sha="$(sha256sum "$artifact_input")"; artifact_sha="${artifact_sha%% *}"
            [[ "$artifact_sha" == "$(jq -r '.artifactSha256' <<< "$DEPLOY_MEASURE_JSON")" ]] || return 1
            deploy_run_acceptance_record "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" "$scenario_id" "$artifact_input"
            return
        fi
        recover_runtime=true
        [[ "$runtime_logic_state" != stop-intent && "$runtime_logic_state" != stopped ]] || DEPLOY_CAMPAIGN_LOGIC_STOPPED=true
    fi

    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote" || return 1
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$target_remote" || return 1
    central_headers="$logic_remote/owner.headers"; deploy_bootstrap_central_headers "$inventory" "$logic" "$render_root" "$central_headers" || return 1
    response="$private_root/$name-antiforgery.json"; deploy_bootstrap_owner_session "$inventory" "$target" "$render_root" "$target_remote" "$response" || return 1
    cookies="$target_remote/owner.cookies"; DEPLOY_CAMPAIGN_COOKIES="$cookies"; DEPLOY_CAMPAIGN_TARGET_REMOTE="$target_remote"
    if [[ "$recover_runtime" == true ]]; then
        deploy_acceptance_campaign_control_cleanup
        now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        DEPLOY_MEASURE_JSON="$(jq -c --arg now "$now" '.logicState="restored" | .phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_MEASURE_JSON")"
        deploy_acceptance_campaign_runtime_publish || return 1
        { deploy_fail acceptance-campaign recovery fresh-run-required; return 1; }
    fi

    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_MEASURE_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg scenario "$scenario_id" \
      --arg now "$now" --arg target "$name" --arg device "$device" --argjson profile "$profile" '
      {schemaVersion:1,publicationGeneration:0,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,
       scenarioId:$scenario,workload:"W2",outageCaptureCount:10,captureIntervalSeconds:25,logicState:"running",
       phaseStatus:"running",startedAt:$now,updatedAt:$now,targets:[{target:$target,deviceId:$device,profile:$profile,controlAttempts:[]}]}')"
    deploy_acceptance_campaign_runtime_publish || return 1
    deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$run_id" initial-pause Paused || return 1
    start_sequence="$(deploy_measure_read_sequence "$target" "$target_remote" "$private_root" "$cookies" outage-start "$device")" || return 1
    expected_baseline="$(jq -er '.targets[0].measured.endSequence | numbers' "$state_dir/measure-ledger.json")" || return 1
    [[ "$start_sequence" == "$expected_baseline" ]] || { deploy_fail acceptance-campaign baseline capture-contamination; return 1; }
    measured_start="$(jq -er '.targets[0].measuredStart | numbers' "$state_dir/measure-ledger.json")" || return 1
    baseline_facts="$(deploy_measure_wait_capture_set "$target" "$logic" "$target_remote" "$logic_remote" "$private_root" "$cookies" "$central_headers" \
      "$device" baseline "$measured_start" 30 "$deadline")" || return 1
    [[ "$(jq -S -c . <<< "$baseline_facts")" == "$(jq -S -c '.targets[0].measured' "$state_dir/measure-ledger.json")" ]] ||
      { deploy_fail acceptance-campaign baseline measured-window-mismatch; return 1; }
    DEPLOY_MEASURE_JSON="$(jq -c --argjson start "$start_sequence" '.baselineSequence=$start | .expectedEnd=($start + 10)' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_acceptance_campaign_runtime_publish || return 1

    outage_started_elapsed=$SECONDS; outage_started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_CAMPAIGN_LOGIC_STOPPED=true
    DEPLOY_MEASURE_JSON="$(jq -c --arg now "$outage_started" '.logicState="stop-intent" | .outageStartedAt=$now | .updatedAt=$now' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_acceptance_campaign_runtime_publish || return 1
    deploy_up_compose_mutation "$logic" "$logic_context" "$logic_project" "$logic_env" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" stop logichost || return 1
    DEPLOY_MEASURE_JSON="$(jq -c --arg now "$outage_started" '.logicState="stopped" | .outageStartedAt=$now | .updatedAt=$now' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_acceptance_campaign_runtime_publish || return 1
    deploy_measure_execute_exact_count "$target" "$target_remote" "$private_root" "$render_root" "$cookies" "$device" "$run_id" outage \
      "$start_sequence" 10 "$deadline" "$interval" || return 1
    outage_ended="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; outage_seconds=$(( SECONDS - outage_started_elapsed ))
    status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/v1/operations/summary" "" "" "$cookies" \
      "$target_remote/outage-operations.json" "$private_root/$name-outage-operations.json")" || return 1
    [[ "$status" == 200 ]] || return 1
    operations="$private_root/$name-outage-operations.json"
    pending="$(jq -er '.artifactOutbox.value.pendingCount | numbers | select(. > 0)' "$operations")" || { deploy_fail acceptance-campaign outage backlog-not-observed; return 1; }
    pending_bytes="$(jq -er '.artifactOutbox.value.pendingBytes | numbers | select(. > 0)' "$operations")" || return 1
    jq -e '.rawIngress.value.pendingCount == 0 and .captureLanes.value.pendingCount == 0 and
      .captureProcessing.value.pendingCount == 0 and .artifactOutbox.value.quarantineCount == 0 and
      .artifactOutbox.value.terminalCount == 0' "$operations" >/dev/null || { deploy_fail acceptance-campaign outage invalid-queue-state; return 1; }

    recovery_started=$SECONDS
    deploy_up_compose_mutation "$logic" "$logic_context" "$logic_project" "$logic_env" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" start logichost || return 1
    DEPLOY_CAMPAIGN_LOGIC_STOPPED=false
    deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$logic")" "$(jq -r '.internalEndpoint' <<< "$logic")/alive" || return 1
    deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$logic")" "$(jq -r '.internalEndpoint' <<< "$logic")/health" || return 1
    deploy_transport_oidc_ready "$(jq -r '.sshHost' <<< "$target")" "$(jq -r '.publicEndpoint' <<< "$logic")" || return 1
    DEPLOY_MEASURE_JSON="$(jq -c --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '.logicState="restored" | .updatedAt=$now' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_acceptance_campaign_runtime_publish || return 1
    facts="$(deploy_measure_wait_capture_set "$target" "$logic" "$target_remote" "$logic_remote" "$private_root" "$cookies" "$central_headers" \
      "$device" outage "$start_sequence" 10 "$deadline")" || return 1
    recovery_seconds=$(( SECONDS - recovery_started )); (( recovery_seconds >= 1 )) || recovery_seconds=1
    drain_rate="$(jq -cn --argjson count 10 --argjson seconds "$recovery_seconds" '$count / $seconds')" || return 1
    jq -e '. > 0.04' <<< "$drain_rate" >/dev/null || { deploy_fail acceptance-campaign recovery drain-not-faster-than-arrival; return 1; }
    completed="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    acceptance_ledger="$(jq -c . "$state_dir/acceptance-ledger.json")"; scenario="$(jq -c --arg id "$scenario_id" '.scenarios[] | select(.id == $id)' <<< "$acceptance_ledger")"
    artifact_input="$runtime_root/$scenario_id.json"
    artifact_json="$(deploy_acceptance_campaign_build_artifact "$acceptance_ledger" "$scenario" "$facts" "$outage_started" "$completed" "$outage_seconds" \
      "$recovery_seconds" "$pending" "$pending_bytes" "$drain_rate")" || return 1
    deploy_publish_json "$artifact_input" "$artifact_json" || return 1
    artifact_length="$(stat -c %s -- "$artifact_input")" || return 1
    artifact_sha="$(sha256sum "$artifact_input")"; artifact_sha="${artifact_sha%% *}"
    DEPLOY_MEASURE_JSON="$(jq -c --arg now "$completed" --argjson facts "$facts" --argjson recovery "$recovery_seconds" \
      --argjson rate "$drain_rate" --argjson pending "$pending" --argjson pendingBytes "$pending_bytes" --argjson outageSeconds "$outage_seconds" \
      --argjson artifactLength "$artifact_length" --arg artifactSha "$artifact_sha" \
      '.facts=$facts | .outagePendingCount=$pending | .outagePendingBytes=$pendingBytes | .outageSeconds=$outageSeconds |
       .recoverySeconds=$recovery | .drainRate=$rate | .artifactByteLength=$artifactLength | .artifactSha256=$artifactSha |
       .phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_bootstrap_cleanup_private_remote || return 1
    deploy_acceptance_campaign_runtime_publish || return 1

    deploy_run_acceptance_record "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" "$scenario_id" "$artifact_input"
}
