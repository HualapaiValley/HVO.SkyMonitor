#!/usr/bin/env bash

deploy_measure_mark_failed() {
    local status="${1:-1}" now cleanup_ok=true
    trap - ERR
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -n "${DEPLOY_MEASURE_JSON:-}" ]]; then
        if [[ -n "${DEPLOY_MEASURE_INVENTORY:-}" && -n "${DEPLOY_MEASURE_RENDER_ROOT:-}" && -n "${DEPLOY_MEASURE_PRIVATE_ROOT:-}" ]]; then
            deploy_measure_failure_pause_all || cleanup_ok=false
        fi
        DEPLOY_MEASURE_JSON="$(jq -c --arg now "$now" '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_MEASURE_JSON")"
        [[ -z "${DEPLOY_MEASURE_LEDGER:-}" ]] || deploy_measure_publish >/dev/null 2>&1 || true
    fi
    if [[ "$cleanup_ok" == true ]]; then
        deploy_transport_reconcile_private_uploads best-effort >/dev/null 2>&1 || true
        deploy_bootstrap_cleanup_private_remote best-effort || true
    fi
    return "$status"
}

deploy_measure_validate_snapshot() {
    local continuity="$1" operations="$2" stats="$3" device="$4" config_sha="${5:-}"
    jq -e --arg device "$device" --arg configSha "$config_sha" '
      .deviceId == $device and .configuredAgentId == $device and .isProvisioned == true and
      ($configSha == "" or ((.activeConfigurationSha256 | ascii_downcase) == ($configSha | ascii_downcase))) and
      (.durable | type == "object") and .durable.rawIngressDatabaseExists == true and
      (.durable.captureSequences | type == "array") and
      ([.durable.captureSequences[] | select(.agentId == $device and (.lastSequence | type) == "number" and .lastSequence >= 0)] | length) == 1 and
      .durable.artifactOutboxDatabaseExists == true and (.durable.artifactOutboxMaximumRecordId | type) == "number" and
      .durable.artifactOutboxMaximumRecordId >= 0 and (.durable.artifactOutboxMaximumAuditId | type) == "number" and
      .durable.artifactOutboxMaximumAuditId >= 0 and .durable.fleetDatabaseExists == true and
      (.durable.fleetNextSequence | type) == "number" and .durable.fleetNextSequence >= 1 and
      (.durable.fleetMaximumSequence == null or
        ((.durable.fleetMaximumSequence | type) == "number" and .durable.fleetMaximumSequence >= 0))' "$continuity" >/dev/null || return 1
    jq -e --arg device "$device" '
      .configuration.value.agentId == $device and .configuration.value.moduleType == "VirtualSky" and
      (.captureTelemetry.value.sampleCount | type) == "number" and .captureTelemetry.value.sampleCount >= 0 and
      all(.captureTelemetry.value.averageIntervalMilliseconds,.captureTelemetry.value.averageExposureMilliseconds,
        .captureTelemetry.value.averageProcessingMilliseconds,.captureTelemetry.value.averageLoopMilliseconds,
        .captureTelemetry.value.capturesPerMinute,.captureTelemetry.value.dutyCycle; type == "number") and
      (.captureTelemetry.value.framesStored | type) == "number" and .captureTelemetry.value.framesStored >= 0 and
      (.captureTelemetry.value.immediateUploadCount | type) == "number" and .captureTelemetry.value.immediateUploadCount >= 0 and
      (.captureRuntime.value.timings | type == "array") and all(.captureRuntime.value.timings[];
        (.segment | type == "string" and length > 0) and (.sampleCount | type) == "number" and .sampleCount >= 0 and
        all(.medianMilliseconds,.p95Milliseconds,.maximumMilliseconds; type == "number" and . >= 0)) and
      all(.rawIngress.value,.captureProcessing.value,.artifactOutbox.value;
        (.pendingCount | type) == "number" and .pendingCount >= 0 and (.pendingBytes | type) == "number" and .pendingBytes >= 0 and
        (.leasedCount | type) == "number" and .leasedCount >= 0 and (.retryCount | type) == "number" and .retryCount >= 0 and
        (.quarantineCount | type) == "number" and .quarantineCount >= 0 and (.terminalCount | type) == "number" and .terminalCount >= 0 and
        (.oldestPendingUtc == null or (.oldestPendingUtc | type == "string"))) and
      (.captureLanes.value.lanes | type == "array") and
      all(.captureLanes.value.lanes[]; (.name | type == "string" and length > 0) and (.required | type == "boolean") and
        (.pendingCount | type) == "number" and .pendingCount >= 0 and (.pendingBytes | type) == "number" and .pendingBytes >= 0 and
        (.leasedCount | type) == "number" and .leasedCount >= 0 and (.quarantineCount | type) == "number" and .quarantineCount >= 0 and
        (.pressureLevel | type) == "number" and .pressureLevel >= 0 and
        (.oldestPendingUtc == null or (.oldestPendingUtc | type == "string"))) and
      all(.captureLanes.value.pendingCount,.captureLanes.value.pendingBytes,.captureLanes.value.leasedCount,
        .captureLanes.value.quarantineCount; type == "number" and . >= 0)' "$operations" >/dev/null || return 1
    jq -e '(keys | sort) == (["blockIo","cpu","memory","memoryPercent","networkIo","pids"] | sort) and
      all(.[]; type == "string" and length > 0)' "$stats" >/dev/null
}

deploy_measure_snapshot() {
    local inventory="$1" target="$2" target_remote="$3" private_root="$4" cookies="$5" label="$6" state_dir="$7" device="$8" config_sha="$9"
    local name endpoint status context project env_file stats
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
      "$target_remote/$label-continuity.json" "$private_root/$name-$label-continuity.json")" || return 1
    [[ "$status" == 200 ]] || return 1
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/summary" "" "" "$cookies" \
      "$target_remote/$label-operations.json" "$private_root/$name-$label-operations.json")" || return 1
    [[ "$status" == 200 ]] || return 1
    context="$(jq -r '.dockerContext' <<< "$target")"; project="$(jq -r '.deployment.resources.project' "$inventory")-$name"; env_file="$state_dir/up-rendered/$name.env"
    stats="$(deploy_transport_compose_stats "$context" "$project" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" cameraagent)" || return 1
    deploy_publish_json "$private_root/$name-$label-stats.json" "$stats"
    deploy_measure_validate_snapshot "$private_root/$name-$label-continuity.json" "$private_root/$name-$label-operations.json" \
      "$private_root/$name-$label-stats.json" "$device" "$config_sha"
}

deploy_measure_publish() {
    deploy_phase_publish measure DEPLOY_MEASURE_JSON "$DEPLOY_MEASURE_LEDGER" "$DEPLOY_MEASURE_MANIFEST" \
      "$DEPLOY_MEASURE_EVIDENCE" "$DEPLOY_MEASURE_COMMIT"
}

deploy_measure_validate_candidate() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" selected="$6" inventory="$7"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg selected "$selected" --argjson inventory "$(jq -c . "$inventory")" '
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
      (.publicationGeneration | numbers) >= 1 and .executionMode == "canonical" and .canonicalWorkloadConfigured == true and .workload == $selected and
      (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      ((keys - ["completedAt","elapsedSeconds"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","executionMode","canonicalWorkloadConfigured","workload","phaseStatus","startedAt","updatedAt","targets"] | sort)) and
      (.targets | type == "array" and length == ([.[].target] | unique | length) and all(.[];
        .target as $target | ([ $inventory.cameraAgents[].name ] | index($target) != null) and
        (keys | sort) == (["target","deviceId","status","profile","controlAttempts","warmupStart","measuredStart","warmupCompleted","measuredCompleted","warmup","measured","before","after","failureCleanup"] | sort) and
        (.status == "running" or .status == "measured") and
        ((.profile == null and .status == "running" and .warmupStart == null and .measuredStart == null and
          .warmupCompleted == 0 and .measuredCompleted == 0 and .warmup == null and .measured == null and .before == null and .after == null) or
         ((.profile | type) == "object" and .profile.workload == $selected)) and
        (.controlAttempts | type == "array" and all(.[];
          (keys | sort) == (["boundary","action","attempt","key","status","resultState","resultVersion"] | sort) and
          (.action == "pause" or .action == "resume") and (.attempt | numbers) >= 1 and
          (.key | test("^deploy-measure-[a-z0-9-]+-[a-z0-9-]+-[a-z0-9-]+-[0-9]+$")) and
          (.status == "intent" or .status == "completed" or .status == "reconciled" or .status == "superseded"))) and
        (.failureCleanup.status == "not-required" or .failureCleanup.status == "completed" or .failureCleanup.status == "capture-may-be-running") and
        (if .status == "measured" then all(.warmup,.measured;
          (keys | sort) == (["startSequence","endSequence","count","captures","drained","correctness"] | sort) and
          .count == (.captures | length) and .drained == true and .correctness == true and
          ([.captures[].captureSequence] | length == (unique | length)) and
          ([.captures[].captureId] | length == (unique | length))) else true end))) and
      ([.targets[].controlAttempts[].key] | length == (unique | length)) and
      (if .phaseStatus == "passed" then ([.targets[].target] | sort) == ([$inventory.cameraAgents[].name] | sort) else true end)' "$path" >/dev/null 2>&1 ||
      { deploy_fail measure ledger invalid; return 1; }
}

deploy_measure_project_snapshot() {
    local continuity="$1" operations="$2" stats="$3"
    jq -cn --slurpfile continuity "$continuity" --slurpfile operations "$operations" --slurpfile stats "$stats" '
      {continuity:$continuity[0].durable,telemetry:$operations[0].captureTelemetry.value,
       timings:$operations[0].captureRuntime.value.timings,queues:{raw:$operations[0].rawIngress.value,
       lanes:$operations[0].captureLanes.value,processing:$operations[0].captureProcessing.value,
       outbox:$operations[0].artifactOutbox.value},container:$stats[0]}'
}

deploy_measure_read_sequence() {
    local target="$1" target_remote="$2" private_root="$3" cookies="$4" label="$5" device="$6" status name endpoint
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
      "$target_remote/$label-continuity.json" "$private_root/$name-$label-continuity.json")" || return 1
    [[ "$status" == 200 ]] || return 1
    jq -er --arg device "$device" '[.durable.captureSequences[] | select(.agentId == $device) | .lastSequence] |
      if length == 1 and (.[0] | type) == "number" then .[0] else empty end' "$private_root/$name-$label-continuity.json"
}

deploy_measure_capture_control() {
    local target="$1" target_remote="$2" private_root="$3" render_root="$4" cookies="$5" action="$6" run_id="$7" boundary="$8" desired="$9" force="${10:-false}"
    local name endpoint body base_headers request_headers status key attempt latest latest_status current_state current_version result_state result_version desired_value
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/summary" "" "" "$cookies" \
      "$target_remote/$boundary-control-state.json" "$private_root/$name-$boundary-control-state.json")" || return 1
    [[ "$status" == 200 ]] || return 1
    current_state="$(jq -er '.captureControl.value.state' "$private_root/$name-$boundary-control-state.json")" || return 1
    current_version="$(jq -er '.captureControl.value.version | numbers' "$private_root/$name-$boundary-control-state.json")" || return 1
    latest="$(jq -c --arg target "$name" --arg boundary "$boundary" '[.targets[] | select(.target == $target) | .controlAttempts[] | select(.boundary == $boundary)] | sort_by(.attempt) | last // null' <<< "$DEPLOY_MEASURE_JSON")"
    latest_status="$(jq -r '.status // ""' <<< "$latest")"
    if [[ "$force" != true && "$current_state" == "$desired" ]]; then
        if [[ "$latest_status" == intent ]]; then
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --arg boundary "$boundary" --arg state "$current_state" --argjson version "$current_version" '
              .targets |= map(if .target == $target then .controlAttempts |= map(if .boundary == $boundary and .status == "intent" then
                .status="reconciled" | .resultState=$state | .resultVersion=$version else . end) else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
            return 0
        elif [[ "$latest_status" == completed || "$latest_status" == reconciled ]]; then
            return 0
        fi
    fi
    attempt="$(jq -r --arg target "$name" --arg boundary "$boundary" '[.targets[] | select(.target == $target) | .controlAttempts[] | select(.boundary == $boundary) | .attempt] | max // 0 | . + 1' <<< "$DEPLOY_MEASURE_JSON")"
    key="deploy-measure-$run_id-$name-$boundary-$attempt"
    DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --arg boundary "$boundary" --arg action "$action" --arg key "$key" --arg state "$current_state" \
      --argjson version "$current_version" --argjson attempt "$attempt" '
      .targets |= map(if .target == $target then
        .controlAttempts |= map(if .boundary == $boundary and .status == "intent" then
          .status="superseded" | .resultState=$state | .resultVersion=$version else . end) |
        .controlAttempts += [{boundary:$boundary,action:$action,attempt:$attempt,key:$key,status:"intent",resultState:null,resultVersion:null}]
        else . end)' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_measure_publish || return 1
    body="$render_root/$name-$boundary-$attempt.json"; base_headers="$target_remote/owner.headers"; request_headers="$target_remote/$boundary-$attempt-control.headers"
    jq -cn --arg reason "canonical measurement $boundary attempt $attempt" '{reason:$reason}' > "$body"
    deploy_bootstrap_stage_json "$target" "$body" "$target_remote/$boundary-$attempt.json" || return 1
    deploy_bootstrap_register_private_remote "$target" "$request_headers" || return 1
    deploy_transport_derive_idempotent_headers "$(jq -r '.sshHost' <<< "$target")" "$base_headers" "$request_headers" "$key" || return 1
    status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/v1/operations/capture/$action" "$target_remote/$boundary-$attempt.json" \
      "$request_headers" "$cookies" "$target_remote/$boundary-$attempt-response.json" "$private_root/$name-$boundary-$attempt-response.json")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail measure "$name" "$action-control-failed"; return 1; }
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != "abrupt-after-measure-$boundary" ]] || exit 75
    desired_value=1; [[ "$desired" != Paused ]] || desired_value=3
    jq -e --arg desired "$desired" --argjson desiredValue "$desired_value" '.state == $desired or .state == $desiredValue' \
      "$private_root/$name-$boundary-$attempt-response.json" >/dev/null || return 1
    result_state="$desired"
    result_version="$(jq -er '.version | numbers' "$private_root/$name-$boundary-$attempt-response.json")" || return 1
    jq -e '.replayed == false' "$private_root/$name-$boundary-$attempt-response.json" >/dev/null || return 1
    DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --arg key "$key" --arg state "$result_state" --argjson version "$result_version" '
      .targets |= map(if .target == $target then .controlAttempts |= map(if .key == $key then
        .status="completed" | .resultState=$state | .resultVersion=$version else . end) else . end)' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_measure_publish || return 1
    deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" "$target_remote/$boundary-$attempt.json" "$request_headers" || return 1
    deploy_transport_forget_private_path "$request_headers" || return 1
}

deploy_measure_activate_profile() {
    local target="$1" target_remote="$2" private_root="$3" render_root="$4" cookies="$5" rendered="$6" workload="$7" run_id="$8"
    local name endpoint state status stage_body stage_headers stage_response pending version activate_body activate_headers activate_response
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    state="$private_root/$name-schedule-state.json"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/schedule/" "" "" "$cookies" \
      "$target_remote/schedule-state.json" "$state")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail measure "$name" schedule-read-failed; return 1; }
    if jq -e --slurpfile rendered "$rendered" '
      .activeRevision.profile.module.type == $rendered[0].module.type and
      .activeRevision.profile.rig == $rendered[0].rig and
      .activeRevision.profile.processingSteps == ($rendered[0].processingSteps // $rendered[0].pipeline.steps // [])' "$state" >/dev/null; then
        return 0
    fi

    stage_body="$render_root/$name-$workload-schedule-stage.json"; stage_headers="$target_remote/$workload-schedule-stage-control.headers"
    jq --slurpfile rendered "$rendered" --arg workload "$workload" '
      .activeRevision as $active |
      {profile:($active.profile |
          .module=$rendered[0].module |
          .rig=$rendered[0].rig |
          .processingSteps=($rendered[0].processingSteps // $rendered[0].pipeline.steps // [])),
       basisRevisionId:$active.revisionId,expectedVersion:.stateVersion,reason:("canonical " + $workload + " measurement")}' \
      "$state" > "$stage_body" || return 1
    deploy_bootstrap_stage_json "$target" "$stage_body" "$target_remote/$workload-schedule-stage.json" || return 1
    deploy_bootstrap_register_private_remote "$target" "$stage_headers" || return 1
    deploy_transport_derive_idempotent_headers "$(jq -r '.sshHost' <<< "$target")" "$target_remote/owner.headers" "$stage_headers" \
      "deploy-measure-$run_id-$name-${workload,,}-schedule-stage" || return 1
    stage_response="$private_root/$name-$workload-schedule-stage.json"
    status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/v1/operations/schedule/stage" \
      "$target_remote/$workload-schedule-stage.json" "$stage_headers" "$cookies" \
      "$target_remote/$workload-schedule-stage-response.json" "$stage_response")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail measure "$name" schedule-stage-failed; return 1; }
    pending="$(jq -er '.pendingRevision.revisionId' "$stage_response")" || return 1
    version="$(jq -er '.version | numbers' "$stage_response")" || return 1

    activate_body="$render_root/$name-$workload-schedule-activate.json"; activate_headers="$target_remote/$workload-schedule-activate-control.headers"
    jq -cn --arg revision "$pending" --arg workload "$workload" --argjson version "$version" \
      '{revisionId:$revision,expectedVersion:$version,reason:("canonical " + $workload + " measurement")}' > "$activate_body" || return 1
    deploy_bootstrap_stage_json "$target" "$activate_body" "$target_remote/$workload-schedule-activate.json" || return 1
    deploy_bootstrap_register_private_remote "$target" "$activate_headers" || return 1
    deploy_transport_derive_idempotent_headers "$(jq -r '.sshHost' <<< "$target")" "$target_remote/owner.headers" "$activate_headers" \
      "deploy-measure-$run_id-$name-${workload,,}-schedule-activate" || return 1
    activate_response="$private_root/$name-$workload-schedule-activate.json"
    status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/v1/operations/schedule/activate" \
      "$target_remote/$workload-schedule-activate.json" "$activate_headers" "$cookies" \
      "$target_remote/$workload-schedule-activate-response.json" "$activate_response")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail measure "$name" schedule-activate-failed; return 1; }
    jq -e --slurpfile rendered "$rendered" '
      .activeRevision.profile.module.type == $rendered[0].module.type and
      .activeRevision.profile.rig == $rendered[0].rig and
      .activeRevision.profile.processingSteps == ($rendered[0].processingSteps // $rendered[0].pipeline.steps // [])' \
      "$activate_response" >/dev/null || { deploy_fail measure "$name" schedule-activation-mismatch; return 1; }
    deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" \
      "$target_remote/$workload-schedule-stage.json" "$stage_headers" \
      "$target_remote/$workload-schedule-activate.json" "$activate_headers" || return 1
    deploy_transport_forget_private_path "$stage_headers" || return 1
    deploy_transport_forget_private_path "$activate_headers" || return 1
}

deploy_measure_execute_exact_count() {
    local target="$1" target_remote="$2" private_root="$3" render_root="$4" cookies="$5" device="$6" run_id="$7" label="$8" start="$9" count="${10}" deadline="${11}"
    local expected current name
    name="$(jq -r '.name' <<< "$target")"; expected=$(( start + count ))
    current="$(deploy_measure_read_sequence "$target" "$target_remote" "$private_root" "$cookies" "$label-recovery-boundary" "$device")" || return 1
    (( current <= expected )) || { deploy_fail measure "$name" "$label-boundary-overshot"; return 1; }
    if (( current == expected )); then
        deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$run_id" "$label-pause" Paused || return 1
        return 0
    fi
    deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" resume "$run_id" "$label-resume" Running || return 1
    while (( $(date +%s) <= deadline )); do
        current="$(deploy_measure_read_sequence "$target" "$target_remote" "$private_root" "$cookies" "$label-boundary" "$device")" || return 1
        (( current <= expected )) || { deploy_fail measure "$name" "$label-boundary-overshot"; return 1; }
        if (( current == expected )); then
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$run_id" "$label-pause" Paused || return 1
            return 0
        fi
        sleep "${DEPLOY_TEST_POLL_SECONDS:-1}"
    done
    deploy_fail measure "$name" "$label-count-not-reached"
    return 1
}

deploy_measure_failure_pause_all() {
    local entry target name target_root target_remote response cookies failed=false
    while IFS= read -r entry; do
        name="$(jq -r '.target' <<< "$entry")"
        target="$(jq -c --arg name "$name" '.cameraAgents[] | select(.name == $name)' "$DEPLOY_MEASURE_INVENTORY")"
        target_root="$(jq -r '.runtimeRoot' <<< "$target")"; target_remote="$target_root/.hvo-deploy/measure-$(jq -r '.runId' <<< "$DEPLOY_MEASURE_JSON")"
        deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$target_remote" >/dev/null 2>&1 || { failed=true; continue; }
        response="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-failure-antiforgery.json"
        if ! deploy_bootstrap_owner_session "$DEPLOY_MEASURE_INVENTORY" "$target" "$DEPLOY_MEASURE_RENDER_ROOT" "$target_remote" "$response" >/dev/null 2>&1; then
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" '.targets |= map(if .target == $target then .failureCleanup.status="capture-may-be-running" else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            failed=true
            continue
        fi
        cookies="$target_remote/owner.cookies"
        if deploy_measure_capture_control "$target" "$target_remote" "$DEPLOY_MEASURE_PRIVATE_ROOT" "$DEPLOY_MEASURE_RENDER_ROOT" "$cookies" pause \
          "$(jq -r '.runId' <<< "$DEPLOY_MEASURE_JSON")" failure-pause Paused true >/dev/null 2>&1; then
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" '.targets |= map(if .target == $target then .failureCleanup.status="completed" else . end)' <<< "$DEPLOY_MEASURE_JSON")"
        else
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" '.targets |= map(if .target == $target then .failureCleanup.status="capture-may-be-running" else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            failed=true
        fi
    done < <(jq -c '.targets[]' <<< "$DEPLOY_MEASURE_JSON")
    [[ "$failed" == false ]]
}

deploy_measure_wait_capture_set() {
    local target="$1" central="$2" target_remote="$3" central_remote="$4" private_root="$5" cookies="$6" central_headers="$7" device="$8" label="$9" start="${10}" count="${11}" deadline="${12}"
    local name endpoint central_endpoint first last status local_file central_file operations_file current facts
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"; central_endpoint="$(jq -r '.internalEndpoint' <<< "$central")"
    first=$(( start + 1 )); last=$(( start + count )); local_file="$private_root/$name-$label-local.json"
    central_file="$private_root/$name-$label-central.json"; operations_file="$private_root/$name-$label-operations.json"
    while (( $(date +%s) <= deadline )); do
        status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity?fromCaptureSequence=$first&toCaptureSequence=$last" "" "" "$cookies" \
          "$target_remote/$label-local.json" "$local_file")" || return 1
        [[ "$status" == 200 ]] || return 1
        current="$(jq -er --arg device "$device" '[.durable.captureSequences[] | select(.agentId == $device) | .lastSequence] | if length == 1 then .[0] else empty end' "$local_file")" || return 1
        (( current <= last )) || { deploy_fail measure "$name" "$label-contamination-detected"; return 1; }
        status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/summary" "" "" "$cookies" \
          "$target_remote/$label-operations.json" "$operations_file")" || return 1
        [[ "$status" == 200 ]] || return 1
        status="$(deploy_bootstrap_request "$central" GET "$central_endpoint/api/internal/devices/continuity/$device?fromCaptureSequence=$first&toCaptureSequence=$last" "" "$central_headers" "" \
          "$central_remote/$name-$label-central.json" "$central_file")" || return 1
        [[ "$status" == 200 ]] || return 1
        if jq -e --argjson first "$first" --argjson last "$last" --argjson count "$count" --argjson central "$(jq -c . "$central_file")" '
          .durable.captureWindow as $local |
          ($local | length) == $count and ([range($first;$last+1)] == [$local[].captureSequence]) and
          all($local[]; .state == "committed" and (.captureId | test("^[0-9a-fA-F-]{36}$")) and
            (.rawArtifactId | test("^[0-9a-fA-F-]{36}$")) and (.rawChecksumSha256 | test("^[0-9A-Fa-f]{64}$")) and .rawByteLength > 0) and
          ($central.captureWindow | length) == $count and ([range($first;$last+1)] == [$central.captureWindow[].captureSequence]) and
          ([ $local[] | {captureSequence,captureId:(.captureId|ascii_downcase)} ] ==
           [ $central.captureWindow[] | {captureSequence,captureId:(.captureId|ascii_downcase)} ]) and
          all($local[]; . as $capture |
            [$central.captureWindow[] | select(.captureSequence == $capture.captureSequence and
              (.captureId|ascii_downcase) == ($capture.captureId|ascii_downcase))] | first as $centralCapture |
            [$centralCapture.artifacts[] | select(.role == "Raw" and (.checksumSha256 | test("^[0-9A-Fa-f]{64}$")) and
              .objectState == "Available" and .objectVerifiedAtUtc != null and .completedDerivativeCount >= 1)] as $raw |
            ($raw | length) == 1 and ($raw[0].artifactId|ascii_downcase) == ($capture.rawArtifactId|ascii_downcase) and
            ($raw[0].checksumSha256|ascii_downcase) == ($capture.rawChecksumSha256|ascii_downcase) and
            $raw[0].byteLength == $capture.rawByteLength and
            any($centralCapture.artifacts[]; .role != "Raw" and (.checksumSha256 | test("^[0-9A-Fa-f]{64}$")) and
              .objectState == "Available" and .objectVerifiedAtUtc != null and
              any(.sources[]; .sourceArtifactId == $raw[0].artifactId and
                (.checksumSha256|ascii_downcase) == ($raw[0].checksumSha256|ascii_downcase))))' "$local_file" >/dev/null &&
          jq -e '.rawIngress.value.pendingCount == 0 and .rawIngress.value.leasedCount == 0 and
            .captureLanes.value.pendingCount == 0 and .captureLanes.value.leasedCount == 0 and
            .captureProcessing.value.pendingCount == 0 and .captureProcessing.value.leasedCount == 0 and
            .artifactOutbox.value.pendingCount == 0 and .artifactOutbox.value.leasedCount == 0' "$operations_file" >/dev/null; then
            facts="$(jq -c --argjson start "$start" --argjson end "$last" --argjson count "$count" '
              {startSequence:$start,endSequence:$end,count:$count,
               captures:[.durable.captureWindow[] | {captureSequence,captureId,rawArtifactId,
                 rawChecksumSha256,rawByteLength}],drained:true,correctness:true}' "$local_file")" || return 1
            printf '%s\n' "$facts"
            return 0
        fi
        sleep "${DEPLOY_TEST_POLL_SECONDS:-2}"
    done
    deploy_fail measure "$name" "$label-convergence-timeout"
    return 1
}

deploy_run_measure() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6" selected_workload="$7"
    local state_dir evidence_dir render_root private_root now started_seconds ended_seconds duration deadline target name target_root target_remote response cookies
    local device profile warmup_requested measured_requested warmup_start warmup_completed measured_start before after measured_completed status reset_status
    local logic logic_root logic_remote central_headers warmup measured
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/bootstrap-manifest.json" measure "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" "$worktree" || return 1
    [[ "$selected_workload" == W1 || "$selected_workload" == W2 ]] || { deploy_fail measure workload W1-or-W2-required; return 1; }
    [[ "$(jq -r '.deployment.workload.sustainedArmOptIn' "$inventory")" == true ]] || { deploy_fail measure workload sustained-arm-opt-in-required; return 1; }
    # shellcheck disable=SC2034 # Consumed by shared target-correlation helpers.
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"
    deploy_transport_reconcile_private_uploads strict || { deploy_fail measure private-upload-registry cleanup-failed; return 1; }
    jq -e 'length == 0' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" >/dev/null || { deploy_fail measure private-upload-registry not-empty; return 1; }
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"; render_root="$state_dir/measure-rendered"; private_root="$state_dir/measure-private"
    install -d -m 700 "$render_root" "$private_root"
    DEPLOY_MEASURE_MANIFEST="$state_dir/measure-manifest.json"; DEPLOY_MEASURE_LEDGER="$state_dir/measure-ledger.json"; DEPLOY_MEASURE_EVIDENCE="$evidence_dir/measure.json"
    DEPLOY_MEASURE_COMMIT="$state_dir/measure-commit.json"
    DEPLOY_MEASURE_INVENTORY="$inventory"; DEPLOY_MEASURE_RENDER_ROOT="$render_root"; DEPLOY_MEASURE_PRIVATE_ROOT="$private_root"
    deploy_require_no_orphan_phase_files "$DEPLOY_MEASURE_LEDGER" "$DEPLOY_MEASURE_MANIFEST" "$DEPLOY_MEASURE_EVIDENCE" "$DEPLOY_MEASURE_COMMIT" || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; duration="${DEPLOY_TEST_MEASURE_DURATION_SECONDS:-$(jq -r '.deployment.workload.durationSeconds' "$inventory")}"; started_seconds="$(date +%s)"
    if [[ -e "$DEPLOY_MEASURE_LEDGER" || -L "$DEPLOY_MEASURE_LEDGER" ]]; then
        deploy_require_phase_files_match "$DEPLOY_MEASURE_LEDGER" "$DEPLOY_MEASURE_MANIFEST" "$DEPLOY_MEASURE_EVIDENCE" "$DEPLOY_MEASURE_COMMIT" \
          deploy_measure_validate_candidate "$run_id" "$mode" "$hash" "$revision" "$selected_workload" "$inventory" || return 1
        DEPLOY_MEASURE_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' "$DEPLOY_MEASURE_LEDGER")"
    else
        DEPLOY_MEASURE_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg now "$now" --arg selected "$selected_workload" \
          '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,executionMode:"canonical",
            canonicalWorkloadConfigured:true,workload:$selected,phaseStatus:"running",startedAt:$now,updatedAt:$now,targets:[]}')"
    fi
    deploy_measure_publish
    logic="$(jq -c '.logicHost' "$inventory")"; logic_root="$(jq -r '.runtimeRoot' <<< "$logic")"; logic_remote="$logic_root/.hvo-deploy/measure-$run_id"
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote" || return 1
    central_headers="$logic_remote/owner.headers"; deploy_bootstrap_central_headers "$inventory" "$logic" "$render_root" "$central_headers" || return 1
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; target_root="$(jq -r '.runtimeRoot' <<< "$target")"; target_remote="$target_root/.hvo-deploy/measure-$run_id"
        deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$target_remote" || return 1
        response="$private_root/$name-antiforgery.json"; deploy_bootstrap_owner_session "$inventory" "$target" "$render_root" "$target_remote" "$response" || return 1
        cookies="$target_remote/owner.cookies"
        status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/internal/deployment/continuity" "" "" "$cookies" \
          "$target_remote/profile-identity.json" "$private_root/$name-profile-identity.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail measure "$name" identity-read-failed; return 1; }
        device="$(jq -er '.deviceId' "$private_root/$name-profile-identity.json")"
        profile="$(deploy_stage_workload_profile "$inventory" "$target" "$selected_workload" "$device" "$render_root" "$state_dir" "$run_id" false)" || return 1
        if ! jq -e --arg target "$name" 'any(.targets[]; .target == $target)' <<< "$DEPLOY_MEASURE_JSON" >/dev/null; then
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --arg device "$device" '.targets += [{target:$target,deviceId:$device,status:"running",profile:null,
              controlAttempts:[],warmupStart:null,measuredStart:null,warmupCompleted:0,measuredCompleted:0,warmup:null,measured:null,before:null,after:null,
              failureCleanup:{status:"not-required"}}]' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        else
            jq -e --arg target "$name" --arg device "$device" --argjson expected "$profile" '
              .targets[] | select(.target == $target) | .deviceId == $device and (.profile == null or .profile == $expected)' <<< "$DEPLOY_MEASURE_JSON" >/dev/null ||
              { deploy_fail measure "$name" resumed-profile-state-mismatch; return 1; }
        fi
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .profile == null' <<< "$DEPLOY_MEASURE_JSON")" == true ]]; then
            if ! jq -e --arg device "$device" --arg sha "$(jq -r '.configSha256' <<< "$profile")" '
              .deviceId == $device and (.activeConfigurationSha256|ascii_downcase) == ($sha|ascii_downcase)' "$private_root/$name-profile-identity.json" >/dev/null; then
                [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-before-measure-profile-stage ]] || exit 75
                profile="$(deploy_stage_workload_profile "$inventory" "$target" "$selected_workload" "$device" "$render_root" "$state_dir" "$run_id" true)" || return 1
                status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/internal/deployment/continuity" "" "" "$cookies" \
                  "$target_remote/profile-activated.json" "$private_root/$name-profile-activated.json")" || return 1
                if [[ "$status" != 200 ]] || ! jq -e --arg device "$device" --arg sha "$(jq -r '.configSha256' <<< "$profile")" '
                  .deviceId == $device and (.activeConfigurationSha256|ascii_downcase) == ($sha|ascii_downcase)' "$private_root/$name-profile-activated.json" >/dev/null; then
                    deploy_fail measure "$name" activated-profile-state-mismatch
                    return 1
                fi
                [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-measure-profile-activation ]] || exit 75
            fi
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson profile "$profile" '.targets |= map(if .target == $target then .profile=$profile else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        else
            jq -e --arg device "$device" --arg sha "$(jq -r '.configSha256' <<< "$profile")" '
              .deviceId == $device and (.activeConfigurationSha256|ascii_downcase) == ($sha|ascii_downcase)' "$private_root/$name-profile-identity.json" >/dev/null ||
              { deploy_fail measure "$name" resumed-profile-state-mismatch; return 1; }
        fi
        deploy_measure_activate_profile "$target" "$target_remote" "$private_root" "$render_root" "$cookies" \
          "$render_root/$name-$selected_workload-camera-module.json" "$selected_workload" "$run_id" || return 1
        warmup_requested="$(jq -r '.warmupOperations' <<< "$profile")"; measured_requested="$(jq -r '.measuredOperations' <<< "$profile")"
        deadline=$(( $(date +%s) + duration ))
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .failureCleanup.status' <<< "$DEPLOY_MEASURE_JSON")" == capture-may-be-running ]]; then
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$run_id" failure-pause Paused true || return 1
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" '.targets |= map(if .target == $target then .failureCleanup.status="completed" else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        fi
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .status' <<< "$DEPLOY_MEASURE_JSON")" == measured ]]; then
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$run_id" completed-reconcile-pause Paused || return 1
            measured_start="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .measuredStart' <<< "$DEPLOY_MEASURE_JSON")"
            measured="$(deploy_measure_wait_capture_set "$target" "$logic" "$target_remote" "$logic_remote" "$private_root" "$cookies" "$central_headers" "$device" measured "$measured_start" "$measured_requested" "$deadline")" || return 1
            [[ "$(jq -S -c . <<< "$measured")" == "$(jq -S -c --arg target "$name" '.targets[] | select(.target == $target) | .measured' <<< "$DEPLOY_MEASURE_JSON")" ]] ||
              { deploy_fail measure "$name" completed-boundary-state-mismatch; return 1; }
            continue
        fi
        warmup_start="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .warmupStart // empty' <<< "$DEPLOY_MEASURE_JSON")"
        if [[ -z "$warmup_start" ]]; then
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$run_id" initial-pause Paused || return 1
            warmup_start="$(deploy_measure_read_sequence "$target" "$target_remote" "$private_root" "$cookies" warmup-start "$device")" ||
              { deploy_fail measure "$name" warmup-start-invalid; return 1; }
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson start "$warmup_start" '.targets |= map(if .target == $target then .warmupStart=$start else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        fi
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .warmup == null' <<< "$DEPLOY_MEASURE_JSON")" == true ]]; then
            deploy_measure_execute_exact_count "$target" "$target_remote" "$private_root" "$render_root" "$cookies" "$device" "$run_id" warmup "$warmup_start" "$warmup_requested" "$deadline" || return 1
            warmup="$(deploy_measure_wait_capture_set "$target" "$logic" "$target_remote" "$logic_remote" "$private_root" "$cookies" "$central_headers" "$device" warmup "$warmup_start" "$warmup_requested" "$deadline")" || return 1
            warmup_completed="$(jq -r '.count' <<< "$warmup")"
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson facts "$warmup" --argjson count "$warmup_completed" '.targets |= map(if .target == $target then .warmup=$facts | .warmupCompleted=$count else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
            [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-measure-warmup-complete ]] || exit 75
        fi
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .before == null' <<< "$DEPLOY_MEASURE_JSON")" == true ]]; then
            reset_status="$(deploy_bootstrap_request "$target" POST "$(jq -r '.internalEndpoint' <<< "$target")/api/internal/deployment/measurement/reset" "" \
              "$target_remote/owner.headers" "$cookies" "$target_remote/measurement-reset.json" "$private_root/$name-measurement-reset.json")" || return 1
            if [[ "$reset_status" != 200 ]] || ! jq -e '.status == "reset"' "$private_root/$name-measurement-reset.json" >/dev/null; then
                deploy_fail measure "$name" measurement-window-reset-failed
                return 1
            fi
            deploy_measure_snapshot "$inventory" "$target" "$target_remote" "$private_root" "$cookies" before "$state_dir" "$device" "$(jq -r '.configSha256' <<< "$profile")" ||
              { deploy_fail measure "$name" initial-snapshot-invalid; return 1; }
            jq -e '.captureTelemetry.value.sampleCount == 0 and (.captureRuntime.value.timings | length) == 0' "$private_root/$name-before-operations.json" >/dev/null ||
              { deploy_fail measure "$name" measurement-window-not-empty-after-reset; return 1; }
            measured_start="$(jq -r --arg device "$device" '[.durable.captureSequences[] | select(.agentId == $device) | .lastSequence][0]' "$private_root/$name-before-continuity.json")"
            before="$(deploy_measure_project_snapshot "$private_root/$name-before-continuity.json" "$private_root/$name-before-operations.json" "$private_root/$name-before-stats.json")"
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson start "$measured_start" --argjson before "$before" '.targets |= map(if .target == $target then .measuredStart=$start | .before=$before else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        else
            measured_start="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .measuredStart' <<< "$DEPLOY_MEASURE_JSON")"
        fi
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .measured == null' <<< "$DEPLOY_MEASURE_JSON")" == true ]]; then
            deploy_measure_execute_exact_count "$target" "$target_remote" "$private_root" "$render_root" "$cookies" "$device" "$run_id" measured "$measured_start" "$measured_requested" "$deadline" || return 1
            measured="$(deploy_measure_wait_capture_set "$target" "$logic" "$target_remote" "$logic_remote" "$private_root" "$cookies" "$central_headers" "$device" measured "$measured_start" "$measured_requested" "$deadline")" || return 1
            measured_completed="$(jq -r '.count' <<< "$measured")"
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson facts "$measured" --argjson count "$measured_completed" '.targets |= map(if .target == $target then .measured=$facts | .measuredCompleted=$count else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
            [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-measure-measured-complete ]] || exit 75
        fi
        deploy_measure_snapshot "$inventory" "$target" "$target_remote" "$private_root" "$cookies" after "$state_dir" "$device" "$(jq -r '.configSha256' <<< "$profile")" ||
          { deploy_fail measure "$name" final-snapshot-invalid; return 1; }
        after="$(deploy_measure_project_snapshot "$private_root/$name-after-continuity.json" "$private_root/$name-after-operations.json" "$private_root/$name-after-stats.json")"
        DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson after "$after" '.targets |= map(if .target == $target then .after=$after | .status="measured" else . end)' <<< "$DEPLOY_MEASURE_JSON")"
        deploy_measure_publish
    done < <(jq -c '.cameraAgents[]' "$inventory")
    ended_seconds="$(date +%s)"; now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    deploy_transport_reconcile_private_uploads strict || return 1
    deploy_bootstrap_cleanup_private_remote
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-measure-cleanup ]] || exit 75
    DEPLOY_MEASURE_JSON="$(jq -c --arg now "$now" --argjson elapsed "$(( ended_seconds - started_seconds ))" \
      '.elapsedSeconds=$elapsed | .phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_measure_publish
}
