#!/usr/bin/env bash

deploy_measure_mark_failed() {
    local status="${1:-1}" now cleanup_ok=true
    trap - ERR
    trap 'deploy_measure_abort_cleanup 130 INT' INT
    trap 'deploy_measure_abort_cleanup 143 TERM' TERM
    trap 'deploy_measure_abort_cleanup 129 HUP' HUP
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -n "${DEPLOY_MEASURE_JSON:-}" ]]; then
        DEPLOY_MEASURE_JSON="$(jq -c --arg now "$now" '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_MEASURE_JSON")"
        [[ -z "${DEPLOY_MEASURE_LEDGER:-}" ]] || deploy_measure_publish >/dev/null 2>&1 || true
        if [[ -n "${DEPLOY_MEASURE_INVENTORY:-}" && -n "${DEPLOY_MEASURE_RENDER_ROOT:-}" && -n "${DEPLOY_MEASURE_PRIVATE_ROOT:-}" ]]; then
            deploy_measure_restore_all || cleanup_ok=false
        fi
        [[ -z "${DEPLOY_MEASURE_LEDGER:-}" ]] || deploy_measure_publish >/dev/null 2>&1 || true
    fi
    if [[ "$cleanup_ok" == true ]]; then
        deploy_transport_reconcile_private_uploads best-effort >/dev/null 2>&1 || true
        deploy_bootstrap_cleanup_private_remote best-effort || true
    fi
    return "$status"
}

deploy_measure_abort_cleanup() {
    local status="$1" signal="$2" now
    trap - ERR INT TERM HUP
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -n "${DEPLOY_MEASURE_JSON:-}" ]]; then
        DEPLOY_MEASURE_JSON="$(jq -c --arg now "$now" '
          .phaseStatus="failed" | .updatedAt=$now | del(.completedAt) |
          .targets |= map(if .restoration.status == "completed" then . else
            .failureCleanup.status="restoration-incomplete" |
            if .restoration.status == "pending" or .restoration.status == "in-progress" then
              if .restoration.configuration != "completed" then .restoration.status="configuration-unverified"
              elif .restoration.schedule != "completed" then .restoration.status="schedule-unverified"
              else .restoration.status="capture-unverified" end
            else . end
          end)' <<< "$DEPLOY_MEASURE_JSON")"
        [[ -z "${DEPLOY_MEASURE_LEDGER:-}" ]] || deploy_measure_publish >/dev/null 2>&1 || true
    fi
    deploy_status measure interrupted failed "signal-${signal,,}-during-restoration"
    exit "$status"
}

deploy_measure_handle_signal() {
    local status="$1" signal="$2"
    trap - ERR
    trap 'deploy_measure_abort_cleanup 130 INT' INT
    trap 'deploy_measure_abort_cleanup 143 TERM' TERM
    trap 'deploy_measure_abort_cleanup 129 HUP' HUP
    deploy_measure_mark_failed "$status" || true
    deploy_status measure interrupted failed "signal-${signal,,}"
    exit "$status"
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
        (.leasedCount | type) == "number" and .leasedCount >= 0 and (.retryCount | type) == "number" and .retryCount >= 0 and
        (.quarantineCount | type) == "number" and .quarantineCount >= 0 and
        (.pressureLevel | type) == "number" and .pressureLevel >= 0 and
        (.pendingCaptures | type) == "array" and all(.pendingCaptures[];
          (keys | sort) == (["agentId","captureSequence"] | sort) and
          (.agentId | type) == "string" and (.agentId | length) > 0 and
          (.captureSequence | type) == "number" and .captureSequence >= 0 and
          (.captureSequence | floor) == .captureSequence) and
        (.oldestPendingUtc == null or (.oldestPendingUtc | type == "string"))) and
      all(.captureLanes.value.pendingCount,.captureLanes.value.pendingBytes,.captureLanes.value.leasedCount,.captureLanes.value.retryCount,
        .captureLanes.value.quarantineCount; type == "number" and . >= 0 and floor == .) and
      (.transientWorker.value.maximumCandidates | type) == "number" and
      .transientWorker.value.maximumCandidates >= 1 and .transientWorker.value.maximumCandidates <= 64 and
      (.transientWorker.value.maximumCandidates | floor) == .transientWorker.value.maximumCandidates' "$operations" >/dev/null || return 1
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

deploy_measure_prepare_directory() {
    local path="$1"
    if [[ -e "$path" || -L "$path" ]]; then
        [[ -d "$path" && ! -L "$path" && "$(stat -c '%u:%a' -- "$path" 2>/dev/null)" == "$(id -u):700" ]] ||
          { deploy_fail measure directory unsafe; return 1; }
    else
        install -d -m 700 -- "$path" 2>/dev/null || { deploy_fail measure directory create-failed; return 1; }
    fi
}

deploy_measure_validate_candidate() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" selected="$6" inventory="$7"
    local execution_run_id="${8:-$run_id}"
    jq -e --arg run "$run_id" --arg executionRun "$execution_run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg selected "$selected" --argjson inventory "$(jq -c . "$inventory")" '
      .schemaVersion == 2 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
      (($executionRun == $run and (has("executionRunId") | not)) or ($executionRun != $run and .executionRunId == $executionRun)) and
      (.publicationGeneration | numbers) >= 1 and .executionMode == "canonical" and .canonicalWorkloadConfigured == true and .workload == $selected and
      (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      ((keys - ["completedAt","elapsedSeconds","executionRunId"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","executionMode","canonicalWorkloadConfigured","workload","phaseStatus","startedAt","updatedAt","targets"] | sort)) and
      (.targets | type == "array" and length == ([.[].target] | unique | length) and all(.[];
        .target as $target | ([ $inventory.cameraAgents[].name ] | index($target) != null) and
        ((keys - ["canonicalScheduleProfileSha256"] | sort) == (["target","deviceId","status","profile","priorState","restoration","controlAttempts","warmupStart","measuredStart","warmupCompleted","measuredCompleted","warmup","measured","before","after","failureCleanup"] | sort)) and
        (.status == "running" or .status == "measured") and
        (.priorState | type == "object" and
          (keys | sort) == (["configurationSha256","configurationFileSha256","scheduleRevisionId","scheduleVersion","captureState","captureVersion"] | sort) and
          (.configurationSha256 | test("^[0-9a-f]{64}$")) and (.configurationFileSha256 | test("^[0-9a-f]{64}$")) and
          (.scheduleRevisionId | test("^[A-Za-z0-9-]+$")) and (.scheduleVersion | numbers) >= 0 and (.scheduleVersion | floor) == .scheduleVersion and
          (.captureState == "Running" or .captureState == "Paused") and (.captureVersion | numbers) >= 0 and (.captureVersion | floor) == .captureVersion) and
        (.restoration | type == "object" and
          (keys | sort) == (["status","configuration","schedule","capture"] | sort) and
          (.status == "pending" or .status == "in-progress" or .status == "completed" or
            .status == "configuration-unverified" or .status == "schedule-unverified" or .status == "capture-unverified") and
          all(.configuration,.schedule,.capture; . == "pending" or . == "intent" or . == "completed") and
          (if .status == "completed" then all(.configuration,.schedule,.capture; . == "completed")
           elif .status == "pending" then all(.configuration,.schedule,.capture; . == "pending") else true end)) and
        (.canonicalScheduleProfileSha256 == null or (.canonicalScheduleProfileSha256 | test("^[0-9a-f]{64}$"))) and
        ((.profile == null and .status == "running" and .warmupStart == null and .measuredStart == null and
          .warmupCompleted == 0 and .measuredCompleted == 0 and .warmup == null and .measured == null and .before == null and .after == null) or
         ((.profile | type) == "object" and .profile.workload == $selected)) and
        (.controlAttempts | type == "array" and all(.[];
          (keys | sort) == (["boundary","action","attempt","key","status","resultState","resultVersion"] | sort) and
          (.action == "pause" or .action == "resume") and (.attempt | numbers) >= 1 and
          (.key | test("^deploy-measure-[a-z0-9-]+-[a-z0-9-]+-[a-z0-9-]+-[0-9]+$")) and
          (.status == "intent" or .status == "completed" or .status == "reconciled" or .status == "superseded"))) and
        (.failureCleanup.status == "not-required" or .failureCleanup.status == "completed" or
          .failureCleanup.status == "capture-may-be-running" or .failureCleanup.status == "restoration-incomplete") and
        (if .failureCleanup.status == "completed" then .restoration.status == "completed"
         elif .failureCleanup.status == "restoration-incomplete" then .restoration.status != "completed" else true end) and
        (if .status == "measured" then all(.warmup,.measured;
          (keys | sort) == (["startSequence","endSequence","count","captures","drained","boundedTemporalTail","correctness"] | sort) and
          .count == (.captures | length) and (.drained | type) == "boolean" and .correctness == true and
          (.boundedTemporalTail | keys | sort) == (["count","captureSequences"] | sort) and
          .boundedTemporalTail.count == (.boundedTemporalTail.captureSequences | length) and
          (.drained == (.boundedTemporalTail.count == 0)) and
          (.boundedTemporalTail.count <= 2) and
          (.boundedTemporalTail.captureSequences ==
            [range(.endSequence - .boundedTemporalTail.count + 1; .endSequence + 1)]) and
          ([.captures[].captureSequence] | length == (unique | length)) and
          ([.captures[].captureId] | length == (unique | length))) else true end))) and
      ([.targets[].controlAttempts[].key] | length == (unique | length)) and
      (if .phaseStatus == "passed" then ([.targets[].target] | sort) == ([$inventory.cameraAgents[].name] | sort) and
        all(.targets[]; .restoration.status == "pending" and .failureCleanup.status == "not-required") else true end)' "$path" >/dev/null 2>&1 ||
      { deploy_fail measure ledger invalid; return 1; }
}

deploy_measure_project_snapshot() {
    local continuity="$1" operations="$2" stats="$3"
    jq -cn --slurpfile continuity "$continuity" --slurpfile operations "$operations" --slurpfile stats "$stats" '
      {continuity:$continuity[0].durable,telemetry:$operations[0].captureTelemetry.value,
       timings:$operations[0].captureRuntime.value.timings,queues:{raw:$operations[0].rawIngress.value,
       lanes:$operations[0].captureLanes.value,processing:$operations[0].captureProcessing.value,
       outbox:$operations[0].artifactOutbox.value,transient:$operations[0].transientWorker.value},container:$stats[0]}'
}

deploy_measure_unrecoverable_queue_reason() {
    local operations="$1"
    jq -r '(
        [ .captureLanes.value.lanes[] | select(.required == true and .quarantineCount > 0) |
            "required-lane-quarantine-" + .name ] +
        [ .captureLanes.value.lanes[] | select(.required == true and .pressureLevel >= 2) |
            "required-lane-pressure-" + .name ] +
        (if .rawIngress.value.quarantineCount > 0 then ["raw-ingress-quarantine"] else [] end) +
        (if .rawIngress.value.terminalCount > 0 then ["raw-ingress-terminal"] else [] end) +
        (if .captureProcessing.value.terminalCount > 0 then ["processing-terminal"] else [] end) +
        (if .artifactOutbox.value.quarantineCount > 0 then ["artifact-outbox-quarantine"] else [] end) +
        (if .artifactOutbox.value.terminalCount > 0 then ["artifact-outbox-terminal"] else [] end) +
        (if .captureLanes.value.availability == "Unhealthy" then ["capture-lanes-unhealthy"] else [] end) +
        (if .transientWorker.value.availability == "Unavailable" then ["transient-worker-unavailable"] else [] end) +
        (if .rawIngress.value.availability == "Unavailable" then ["raw-ingress-unavailable"] else [] end) +
        (if .captureProcessing.value.availability == "Unavailable" then ["processing-unavailable"] else [] end) +
        (if .artifactOutbox.value.availability == "Unavailable" then ["artifact-outbox-unavailable"] else [] end)
      )[0] // empty' "$operations"
}

deploy_measure_read_sequence() {
    local target="$1" target_remote="$2" private_root="$3" cookies="$4" label="$5" device="$6" status name endpoint operations queue_reason
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
      "$target_remote/$label-continuity.json" "$private_root/$name-$label-continuity.json")" || return 1
    [[ "$status" == 200 ]] || return 1
    operations="$private_root/$name-$label-operations.json"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/summary" "" "" "$cookies" \
      "$target_remote/$label-operations.json" "$operations")" || return 1
    [[ "$status" == 200 ]] || return 1
    queue_reason="$(deploy_measure_unrecoverable_queue_reason "$operations")" || return 1
    [[ -z "$queue_reason" ]] || { deploy_fail measure "$name" "$label-$queue_reason"; return 1; }
    jq -er --arg device "$device" '
      [.durable.captureSequences[] | select(.agentId == $device) | .lastSequence] as $sequences |
      if ($sequences | length) == 1 and ($sequences[0] | type) == "number" and
          $sequences[0] >= 0 and ($sequences[0] | floor) == $sequences[0] then $sequences[0]
      elif ($sequences | length) == 0 and .durable.rawIngressDatabaseExists == true then 0
      else empty end' "$private_root/$name-$label-continuity.json"
}

deploy_measure_capture_control() {
    local target="$1" target_remote="$2" private_root="$3" render_root="$4" cookies="$5" action="$6" run_id="$7" boundary="$8" desired="$9" force="${10:-false}" not_before="${11:-0}"
    local expected_state="${12:-}" expected_version="${13:-}"
    local name endpoint body base_headers request_headers status key attempt latest latest_status current_state current_version result_state result_version desired_value wait
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/summary" "" "" "$cookies" \
      "$target_remote/$boundary-control-state.json" "$private_root/$name-$boundary-control-state.json")" || return 1
    [[ "$status" == 200 ]] || return 1
    current_state="$(jq -er '.captureControl.value.state' "$private_root/$name-$boundary-control-state.json")" || return 1
    current_version="$(jq -er '.captureControl.value.version | numbers' "$private_root/$name-$boundary-control-state.json")" || return 1
    [[ -z "$expected_state" || "$current_state" == "$expected_state" ]] || { deploy_fail measure "$name" "$boundary-state-drift"; return 1; }
    [[ -z "$expected_version" || "$current_version" == "$expected_version" ]] || { deploy_fail measure "$name" "$boundary-version-drift"; return 1; }
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
    jq -cn --arg reason "canonical measurement $boundary attempt $attempt" --argjson version "$current_version" \
      '{expectedVersion:$version,reason:$reason}' > "$body"
    deploy_bootstrap_stage_json "$target" "$body" "$target_remote/$boundary-$attempt.json" || return 1
    deploy_bootstrap_register_private_remote "$target" "$request_headers" || return 1
    deploy_transport_derive_idempotent_headers "$(jq -r '.sshHost' <<< "$target")" "$base_headers" "$request_headers" "$key" || return 1
    wait=$(( not_before - $(date +%s) )); (( wait <= 0 )) || sleep "$wait"
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
    local expected_revision="${9:-}"
    local name endpoint state status pending pending_sha version activate_body activate_headers activate_response
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    state="$private_root/$name-schedule-state.json"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/schedule/" "" "" "$cookies" \
      "$target_remote/schedule-state.json" "$state")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail measure "$name" schedule-read-failed; return 1; }
    if deploy_schedule_verify_active_profile "$state"; then
        return 0
    fi
    [[ -z "$expected_revision" || "$(jq -r '.activeRevision.revisionId // ""' "$state")" == "$expected_revision" ]] ||
      { deploy_fail measure "$name" schedule-revision-drift; return 1; }
    IFS=$'\t' read -r pending pending_sha < <(deploy_schedule_select_file_draft "$state") ||
      { deploy_fail measure "$name" canonical-schedule-draft-missing; return 1; }
    version="$(jq -er '.stateVersion | numbers' "$state")" || return 1

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
    deploy_schedule_verify_active_profile "$activate_response" "$pending" "$pending_sha" ||
      { deploy_fail measure "$name" schedule-activation-mismatch; return 1; }
    deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" \
      "$target_remote/$workload-schedule-activate.json" "$activate_headers" || return 1
    deploy_transport_forget_private_path "$activate_headers" || return 1
}

deploy_measure_record_canonical_schedule_sha() {
    local target="$1" target_remote="$2" private_root="$3" cookies="$4" name endpoint state status sha
    name="$(jq -r '.name' <<< "$target")"
    if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .canonicalScheduleProfileSha256 // ""' <<< "$DEPLOY_MEASURE_JSON")" =~ ^[0-9a-f]{64}$ ]]; then
        return 0
    fi
    endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    state="$private_root/$name-canonical-schedule-state.json"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/schedule/" "" "" "$cookies" \
      "$target_remote/canonical-schedule-state.json" "$state")" || return 1
    [[ "$status" == 200 ]] || return 1
    sha="$(jq -er '.fileConfigurationProfileSha256 | ascii_downcase | select(test("^[0-9a-f]{64}$"))' "$state")" || return 1
    DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --arg sha "$sha" \
      '.targets |= map(if .target == $target then .canonicalScheduleProfileSha256=$sha else . end)' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_measure_publish
}

deploy_measure_read_prior_state() {
    local inventory="$1" target="$2" target_remote="$3" private_root="$4" cookies="$5" run_id="$6" continuity="$7"
    local name target_root active_config backup operations schedule status configuration_sha configuration_file_sha capture_state capture_version
    local schedule_revision schedule_version
    name="$(jq -r '.name' <<< "$target")"; target_root="$(jq -r '.runtimeRoot' <<< "$target")"
    active_config="$target_root/.hvo-deploy/up-$run_id/camera-module.json"
    backup="$private_root/$name-prior-camera-module.json"
    deploy_transport_fetch_private_file "$(jq -r '.sshHost' <<< "$target")" "$active_config" "$backup" || {
        deploy_fail measure "$name" prior-profile-fetch-failed
        return 1
    }
    configuration_file_sha="$(sha256sum "$backup" | cut -d' ' -f1)"
    configuration_sha="$(printf '%s' "$(jq -S -c . "$backup")" | sha256sum | cut -d' ' -f1)"
    jq -e --arg sha "$configuration_sha" '(.activeConfigurationSha256 | ascii_downcase) == $sha' "$continuity" >/dev/null || {
        deploy_fail measure "$name" prior-profile-identity-mismatch
        return 1
    }
    operations="$private_root/$name-prior-operations.json"
    status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/v1/operations/summary" "" "" "$cookies" \
      "$target_remote/prior-operations.json" "$operations")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail measure "$name" prior-control-read-failed; return 1; }
    capture_state="$(jq -er '.captureControl.value.state | select(. == "Running" or . == "Paused")' "$operations")" || return 1
    capture_version="$(jq -er '.captureControl.value.version | numbers | select(. >= 0 and floor == .)' "$operations")" || return 1
    schedule="$private_root/$name-prior-schedule.json"
    status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/v1/operations/schedule/" "" "" "$cookies" \
      "$target_remote/prior-schedule.json" "$schedule")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail measure "$name" prior-schedule-read-failed; return 1; }
    schedule_revision="$(jq -er '.activeRevision.revisionId | select(test("^[A-Za-z0-9-]+$"))' "$schedule")" || return 1
    schedule_version="$(jq -er '.stateVersion | numbers | select(. >= 0 and floor == .)' "$schedule")" || return 1
    jq -cn --arg configurationSha256 "$configuration_sha" --arg configurationFileSha256 "$configuration_file_sha" --arg scheduleRevisionId "$schedule_revision" \
      --argjson scheduleVersion "$schedule_version" --arg captureState "$capture_state" --argjson captureVersion "$capture_version" \
      '{configurationSha256:$configurationSha256,configurationFileSha256:$configurationFileSha256,scheduleRevisionId:$scheduleRevisionId,scheduleVersion:$scheduleVersion,
        captureState:$captureState,captureVersion:$captureVersion}'
}

deploy_measure_set_restoration() {
    local target="$1" status="$2" configuration="$3" schedule="$4" capture="$5" cleanup="${6:-}"
    DEPLOY_MEASURE_JSON="$(jq -c --arg target "$target" --arg status "$status" --arg configuration "$configuration" \
      --arg schedule "$schedule" --arg capture "$capture" --arg cleanup "$cleanup" '
      .targets |= map(if .target == $target then
        .restoration={status:$status,configuration:$configuration,schedule:$schedule,capture:$capture} |
        if $cleanup == "" then . else .failureCleanup.status=$cleanup end
      else . end)' <<< "$DEPLOY_MEASURE_JSON")"
    deploy_measure_publish
}

deploy_measure_pause_for_restoration() {
    local target="$1" name target_root target_remote execution_run_id response cookies
    name="$(jq -r '.name' <<< "$target")"; target_root="$(jq -r '.runtimeRoot' <<< "$target")"
    execution_run_id="$(jq -r '.executionRunId // .runId' <<< "$DEPLOY_MEASURE_JSON")"
    target_remote="$target_root/.hvo-deploy/measure-$execution_run_id"
    deploy_measure_set_restoration "$name" in-progress pending pending pending || return 1
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$target_remote" >/dev/null 2>&1 || return 1
    response="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-antiforgery.json"
    deploy_bootstrap_owner_session "$DEPLOY_MEASURE_INVENTORY" "$target" "$DEPLOY_MEASURE_RENDER_ROOT" "$target_remote" "$response" >/dev/null 2>&1 || return 1
    cookies="$target_remote/owner.cookies"
    if ! deploy_measure_capture_control "$target" "$target_remote" "$DEPLOY_MEASURE_PRIVATE_ROOT" "$DEPLOY_MEASURE_RENDER_ROOT" "$cookies" \
      pause "$execution_run_id" restoration-pause Paused true >/dev/null 2>&1; then
        deploy_measure_set_restoration "$name" capture-unverified pending pending intent restoration-incomplete >/dev/null 2>&1 || true
        return 1
    fi
}

deploy_measure_restore_target() {
    local target="$1" name target_root target_remote execution_run_id response cookies endpoint prior backup active_config
    local current_sha current_file current_file_sha current_file_config_sha canonical_sha canonical_file canonical_profile_sha context project env_file status schedule_state schedule_revision schedule_version pending_revision pending_sha body headers rollback_response activation_body activation_headers activation_response action desired
    name="$(jq -r '.name' <<< "$target")"; target_root="$(jq -r '.runtimeRoot' <<< "$target")"
    execution_run_id="$(jq -r '.executionRunId // .runId' <<< "$DEPLOY_MEASURE_JSON")"
    target_remote="$target_root/.hvo-deploy/measure-$execution_run_id"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    prior="$(jq -c --arg target "$name" '.targets[] | select(.target == $target) | .priorState' <<< "$DEPLOY_MEASURE_JSON")"
    backup="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-prior-camera-module.json"
    active_config="$target_root/.hvo-deploy/up-$(jq -r '.runId' <<< "$DEPLOY_MEASURE_JSON")/camera-module.json"
    canonical_file="$DEPLOY_MEASURE_RENDER_ROOT/$name-$(jq -r '.workload' <<< "$DEPLOY_MEASURE_JSON")-camera-module.json"
    [[ -f "$canonical_file" && ! -L "$canonical_file" ]] || return 1
    canonical_sha="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .profile.configSha256 // empty' <<< "$DEPLOY_MEASURE_JSON")"
    if [[ -z "$canonical_sha" ]]; then
        canonical_sha="$(printf '%s' "$(jq -S -c . "$canonical_file")" | sha256sum | cut -d' ' -f1)"
    fi
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$target_remote" >/dev/null 2>&1 || return 1
    response="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-antiforgery.json"
    deploy_bootstrap_owner_session "$DEPLOY_MEASURE_INVENTORY" "$target" "$DEPLOY_MEASURE_RENDER_ROOT" "$target_remote" "$response" >/dev/null 2>&1 || return 1
    cookies="$target_remote/owner.cookies"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
      "$target_remote/restoration-profile-state.json" "$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-profile-state.json")" || status=
    current_sha="$(jq -r '.activeConfigurationSha256 // "" | ascii_downcase' "$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-profile-state.json" 2>/dev/null || true)"
    current_file="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-current-camera-module.json"
    deploy_transport_fetch_private_file "$(jq -r '.sshHost' <<< "$target")" "$active_config" "$current_file" || return 1
    current_file_sha="$(sha256sum "$current_file" | cut -d' ' -f1)"
    current_file_config_sha="$(printf '%s' "$(jq -S -c . "$current_file")" | sha256sum | cut -d' ' -f1)"
    schedule_state="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-pre-restoration-schedule-state.json"
    [[ "$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/schedule/" "" "" "$cookies" \
      "$target_remote/pre-restoration-schedule-state.json" "$schedule_state")" == 200 ]] || return 1
    canonical_profile_sha="$(jq -er '.fileConfigurationProfileSha256 | ascii_downcase | select(test("^[0-9a-f]{64}$"))' "$schedule_state")" || return 1
    if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .canonicalScheduleProfileSha256 // ""' <<< "$DEPLOY_MEASURE_JSON")" =~ ^[0-9a-f]{64}$ ]]; then
        canonical_profile_sha="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .canonicalScheduleProfileSha256' <<< "$DEPLOY_MEASURE_JSON")"
    elif [[ "$current_file_config_sha" == "$canonical_sha" ]]; then
        DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --arg sha "$canonical_profile_sha" \
          '.targets |= map(if .target == $target then .canonicalScheduleProfileSha256=$sha else . end)' <<< "$DEPLOY_MEASURE_JSON")"
        deploy_measure_publish || return 1
    else
        deploy_measure_set_restoration "$name" schedule-unverified pending intent pending restoration-incomplete >/dev/null 2>&1 || true
        return 1
    fi
    if [[ "$status" != 200 || "$current_sha" != "$(jq -r '.configurationSha256' <<< "$prior")" ||
          "$current_file_sha" != "$(jq -r '.configurationFileSha256' <<< "$prior")" ]]; then
        [[ "$status" == 200 && ( "$current_sha" == "$canonical_sha" || "$current_sha" == "$(jq -r '.configurationSha256' <<< "$prior")" ) &&
           "$current_file_config_sha" == "$canonical_sha" ]] || {
            deploy_measure_set_restoration "$name" configuration-unverified intent pending pending restoration-incomplete >/dev/null 2>&1 || true
            return 1
        }
        deploy_measure_set_restoration "$name" in-progress intent pending pending || return 1
        [[ -f "$backup" && ! -L "$backup" && "$(sha256sum "$backup" | cut -d' ' -f1)" == "$(jq -r '.configurationFileSha256' <<< "$prior")" ]] || {
            deploy_measure_set_restoration "$name" configuration-unverified intent pending pending restoration-incomplete >/dev/null 2>&1 || true
            return 1
        }
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        deploy_transport_copy_private_file "$backup" "$(jq -r '.sshHost' <<< "$target")" "$active_config" || return 1
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        context="$(jq -r '.dockerContext' <<< "$target")"; project="$(jq -r '.deployment.resources.project' "$DEPLOY_MEASURE_INVENTORY")-$name"
        env_file="$(dirname "$DEPLOY_MEASURE_LEDGER")/up-rendered/$name.env"
        deploy_up_compose_mutation "$target" "$context" "$project" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" \
          up -d --force-recreate cameraagent || return 1
        deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$target")" "$endpoint/health" || return 1
        deploy_bootstrap_owner_session "$DEPLOY_MEASURE_INVENTORY" "$target" "$DEPLOY_MEASURE_RENDER_ROOT" "$target_remote" "$response" >/dev/null 2>&1 || return 1
        cookies="$target_remote/owner.cookies"
        status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
          "$target_remote/restoration-profile-verified.json" "$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-profile-verified.json")" || status=
        if [[ "$status" != 200 ]] || ! jq -e --arg sha "$(jq -r '.configurationSha256' <<< "$prior")" \
          '(.activeConfigurationSha256 | ascii_downcase) == $sha' "$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-profile-verified.json" >/dev/null; then
            deploy_measure_set_restoration "$name" configuration-unverified intent pending pending restoration-incomplete >/dev/null 2>&1 || true
            return 1
        fi
    fi
    deploy_measure_set_restoration "$name" in-progress completed pending pending || return 1

    schedule_state="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-schedule-state.json"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/schedule/" "" "" "$cookies" \
      "$target_remote/restoration-schedule-state.json" "$schedule_state")" || status=
    [[ "$status" == 200 ]] || return 1
    schedule_revision="$(jq -r '.activeRevision.revisionId // ""' "$schedule_state")"
    pending_revision="$(jq -r '.pendingRevision.revisionId // ""' "$schedule_state")"
    if [[ "$schedule_revision" == "$(jq -r '.scheduleRevisionId' <<< "$prior")" && -n "$pending_revision" ]]; then
        IFS=$'\t' read -r selected_revision pending_sha < <(jq -er --arg sha "$canonical_profile_sha" '
          .pendingRevision | select(.source == "file-draft" and (.profileSha256 | ascii_downcase) == $sha) |
          [.revisionId, (.profileSha256 | ascii_downcase)] | @tsv' "$schedule_state") || {
            deploy_measure_set_restoration "$name" schedule-unverified completed intent pending restoration-incomplete >/dev/null 2>&1 || true
            return 1
        }
        [[ "$selected_revision" == "$pending_revision" ]] || return 1
        deploy_measure_set_restoration "$name" in-progress completed intent pending || return 1
        schedule_version="$(jq -er '.stateVersion | numbers | select(. >= 0 and floor == .)' "$schedule_state")" || return 1
        activation_body="$DEPLOY_MEASURE_RENDER_ROOT/$name-restoration-schedule-activate.json"
        activation_headers="$target_remote/restoration-schedule-activate-control.headers"
        jq -cn --arg revision "$pending_revision" --argjson version "$schedule_version" \
          '{revisionId:$revision,expectedVersion:$version,reason:"clear pre-restoration canonical draft"}' > "$activation_body" || return 1
        deploy_bootstrap_stage_json "$target" "$activation_body" "$target_remote/restoration-schedule-activate.json" || return 1
        deploy_bootstrap_register_private_remote "$target" "$activation_headers" || return 1
        deploy_transport_derive_idempotent_headers "$(jq -r '.sshHost' <<< "$target")" "$target_remote/owner.headers" "$activation_headers" \
          "deploy-measure-$execution_run_id-$name-restoration-schedule-activate-1" || return 1
        activation_response="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-schedule-activate-response.json"
        status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/v1/operations/schedule/activate" \
          "$target_remote/restoration-schedule-activate.json" "$activation_headers" "$cookies" \
          "$target_remote/restoration-schedule-activate-response.json" "$activation_response")" || status=
        if [[ "$status" != 200 ]] || ! deploy_schedule_verify_active_profile \
          "$activation_response" "$pending_revision" "$pending_sha"; then
            deploy_measure_set_restoration "$name" schedule-unverified completed intent pending restoration-incomplete >/dev/null 2>&1 || true
            return 1
        fi
        deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" \
          "$target_remote/restoration-schedule-activate.json" "$activation_headers" || return 1
        deploy_transport_forget_private_path "$activation_headers" || return 1
        status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/schedule/" "" "" "$cookies" \
          "$target_remote/restoration-schedule-after-activate.json" "$schedule_state")" || status=
        [[ "$status" == 200 ]] || return 1
        schedule_revision="$(jq -r '.activeRevision.revisionId // ""' "$schedule_state")"
    fi
    if [[ "$schedule_revision" != "$(jq -r '.scheduleRevisionId' <<< "$prior")" ]]; then
        jq -e --arg prior "$(jq -r '.scheduleRevisionId' <<< "$prior")" --arg canonicalSha "$canonical_profile_sha" '
          (.activeRevision.profileSha256 | ascii_downcase) == $canonicalSha and
          (.pendingRevision == null or .pendingRevision.revisionId == $prior)' "$schedule_state" >/dev/null || {
            deploy_measure_set_restoration "$name" schedule-unverified completed intent pending restoration-incomplete >/dev/null 2>&1 || true
            return 1
        }
        deploy_measure_set_restoration "$name" in-progress completed intent pending || return 1
        schedule_version="$(jq -er '.stateVersion | numbers | select(. >= 0 and floor == .)' "$schedule_state")" || return 1
        body="$DEPLOY_MEASURE_RENDER_ROOT/$name-restoration-schedule.json"; headers="$target_remote/restoration-schedule-control.headers"
        jq -cn --arg revision "$(jq -r '.scheduleRevisionId' <<< "$prior")" --argjson version "$schedule_version" \
          '{revisionId:$revision,expectedVersion:$version,reason:"restore pre-measurement schedule"}' > "$body" || return 1
        deploy_bootstrap_stage_json "$target" "$body" "$target_remote/restoration-schedule.json" || return 1
        deploy_bootstrap_register_private_remote "$target" "$headers" || return 1
        deploy_transport_derive_idempotent_headers "$(jq -r '.sshHost' <<< "$target")" "$target_remote/owner.headers" "$headers" \
          "deploy-measure-$execution_run_id-$name-restoration-schedule-1" || return 1
        rollback_response="$DEPLOY_MEASURE_PRIVATE_ROOT/$name-restoration-schedule-response.json"
        status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/v1/operations/schedule/rollback" \
          "$target_remote/restoration-schedule.json" "$headers" "$cookies" "$target_remote/restoration-schedule-response.json" "$rollback_response")" || status=
        if [[ "$status" != 200 ]] || ! jq -e --arg revision "$(jq -r '.scheduleRevisionId' <<< "$prior")" \
          '.activeRevision.revisionId == $revision' "$rollback_response" >/dev/null; then
            deploy_measure_set_restoration "$name" schedule-unverified completed intent pending restoration-incomplete >/dev/null 2>&1 || true
            return 1
        fi
        cp "$rollback_response" "$schedule_state" || return 1
        deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" "$target_remote/restoration-schedule.json" "$headers" || return 1
        deploy_transport_forget_private_path "$headers" || return 1
    fi
    jq -e --arg revision "$(jq -r '.scheduleRevisionId' <<< "$prior")" \
      '.activeRevision.revisionId == $revision and .pendingRevision == null' "$schedule_state" >/dev/null || {
        deploy_measure_set_restoration "$name" schedule-unverified completed intent pending restoration-incomplete >/dev/null 2>&1 || true
        return 1
    }
    deploy_measure_set_restoration "$name" in-progress completed completed pending || return 1

    desired="$(jq -r '.captureState' <<< "$prior")"; action=resume; [[ "$desired" != Paused ]] || action=pause
    deploy_measure_set_restoration "$name" in-progress completed completed intent || return 1
    if ! deploy_measure_capture_control "$target" "$target_remote" "$DEPLOY_MEASURE_PRIVATE_ROOT" "$DEPLOY_MEASURE_RENDER_ROOT" "$cookies" \
      "$action" "$execution_run_id" restoration-control "$desired" true >/dev/null 2>&1; then
        deploy_measure_set_restoration "$name" capture-unverified completed completed intent restoration-incomplete >/dev/null 2>&1 || true
        return 1
    fi
    deploy_measure_set_restoration "$name" completed completed completed completed completed
}

deploy_measure_pause_delay() {
    local remaining="$1" interval="$2"
    (( remaining >= 1 && interval >= 1 )) || return 1
    if (( remaining == 1 )); then
        (( interval / 2 >= 1 )) && printf '%s\n' "$(( interval / 2 ))" || printf '1\n'
    else
        printf '%s\n' "$(( (remaining - 1) * interval ))"
    fi
}

deploy_measure_execute_exact_count() {
    local target="$1" target_remote="$2" private_root="$3" render_root="$4" cookies="$5" device="$6" run_id="$7" label="$8" start="$9" count="${10}" deadline="${11}" interval="${12}"
    local expected current name remaining delay previous pause_at
    name="$(jq -r '.name' <<< "$target")"; expected=$(( start + count ))
    current="$(deploy_measure_read_sequence "$target" "$target_remote" "$private_root" "$cookies" "$label-recovery-boundary" "$device")" || return 1
    (( current <= expected )) || { deploy_fail measure "$name" "$label-boundary-overshot"; return 1; }
    if (( current == expected )); then
        deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$run_id" "$label-pause" Paused || return 1
        return 0
    fi
    if [[ "${DEPLOY_TEST_POLL_SECONDS:-1}" != 0 ]]; then
        while (( current < expected && $(date +%s) <= deadline )); do
            previous="$current"; remaining=$(( expected - current ))
            # Leave a full cadence for a loaded agent to process the pause before another capture starts.
            delay="$(deploy_measure_pause_delay "$remaining" "$interval")" || return 1
            (( delay >= 1 )) || delay=1
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" resume "$run_id" "$label-resume" Running true || return 1
            pause_at=$(( $(date +%s) + delay )); (( pause_at <= deadline )) || { deploy_fail measure "$name" "$label-count-not-reached"; return 1; }
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$run_id" "$label-pause" Paused false "$pause_at" || return 1
            current="$(deploy_measure_read_sequence "$target" "$target_remote" "$private_root" "$cookies" "$label-boundary" "$device")" || return 1
            (( current <= expected )) || { deploy_fail measure "$name" "$label-boundary-overshot"; return 1; }
            (( $(date +%s) <= deadline )) || { deploy_fail measure "$name" "$label-count-not-reached"; return 1; }
            # A conservative final pause can precede the next start; retry until progress or deadline.
            (( current >= previous )) || { deploy_fail measure "$name" "$label-boundary-regressed"; return 1; }
        done
        (( current == expected )) || { deploy_fail measure "$name" "$label-count-not-reached"; return 1; }
        return 0
    fi
    deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" resume "$run_id" "$label-resume" Running true || return 1
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

deploy_measure_restore_all() {
    local entry target name failed=false
    while IFS= read -r entry; do
        name="$(jq -r '.target' <<< "$entry")"
        [[ "$(jq -r '.restoration.status' <<< "$entry")" != completed ]] || continue
        target="$(jq -c --arg name "$name" '.cameraAgents[] | select(.name == $name)' "$DEPLOY_MEASURE_INVENTORY")"
        if ! deploy_measure_pause_for_restoration "$target"; then
            deploy_measure_set_restoration "$name" capture-unverified pending pending intent restoration-incomplete >/dev/null 2>&1 || true
            failed=true
        fi
    done < <(jq -c '.targets[]' <<< "$DEPLOY_MEASURE_JSON")
    while IFS= read -r entry; do
        name="$(jq -r '.target' <<< "$entry")"
        target="$(jq -c --arg name "$name" '.cameraAgents[] | select(.name == $name)' "$DEPLOY_MEASURE_INVENTORY")"
        if [[ "$(jq -r '.restoration.status' <<< "$entry")" == completed ]]; then continue; fi
        if [[ "$(jq -r '.restoration.status' <<< "$entry")" == capture-unverified ]]; then failed=true; continue; fi
        if ! deploy_measure_restore_target "$target"; then
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" '.targets |= map(if .target == $target then
              .failureCleanup.status="restoration-incomplete" |
              if .restoration.status == "in-progress" then
                if .restoration.configuration != "completed" then .restoration.status="configuration-unverified"
                elif .restoration.schedule != "completed" then .restoration.status="schedule-unverified"
                else .restoration.status="capture-unverified" end
              else . end
              else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish >/dev/null 2>&1 || true
            failed=true
        fi
    done < <(jq -c '.targets[]' <<< "$DEPLOY_MEASURE_JSON")
    [[ "$failed" == false ]]
}

deploy_measure_queues_converged() {
    deploy_capture_queues_converged "$@"
}

deploy_measure_wait_capture_set() {
    local target="$1" central="$2" target_remote="$3" central_remote="$4" private_root="$5" cookies="$6" central_headers="$7" device="$8" label="$9" start="${10}" count="${11}" deadline="${12}"
    local name endpoint central_endpoint first last status local_file central_file operations_file current facts expected_mode
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
        expected_mode="$(jq -er '.configuration.value.transientDetection | select(. == "Off" or . == "Hybrid")' "$operations_file")" || return 1
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
              .objectState == "Available" and
              any(.sources[]; .artifactId == $raw[0].artifactId and
                (.checksumSha256|ascii_downcase) == ($raw[0].checksumSha256|ascii_downcase))))' "$local_file" >/dev/null &&
          deploy_measure_queues_converged "$operations_file" "$last" "$device" "$expected_mode"; then
            facts="$(jq -c --argjson start "$start" --argjson end "$last" --argjson count "$count" \
              --slurpfile operations "$operations_file" '
              ([$operations[0].captureLanes.value.lanes[] |
                select(.name == "transient" and .required == true)][0].pendingCaptures // []) as $tail |
              {startSequence:$start,endSequence:$end,count:$count,
               captures:[.durable.captureWindow[] | {captureSequence,captureId,rawArtifactId,
                 rawChecksumSha256,rawByteLength}],drained:(($tail | length) == 0),
               boundedTemporalTail:{count:($tail | length),captureSequences:[$tail[].captureSequence]},correctness:true}' "$local_file")" || return 1
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
    local scope="${8:-}" state_dir evidence_dir measure_root support_evidence render_root private_root execution_run_id now started_seconds ended_seconds duration deadline target name target_root target_remote response cookies
    local device profile prior prior_sha backup_sha restoration_status reactivate_after_restoration latest_control expected_capture_state expected_capture_version warmup_requested measured_requested warmup_start warmup_completed measured_start before after measured_completed measured_end status reset_status interval_value interval_hours interval_minutes interval_seconds capture_interval_seconds expected_mode
    local logic logic_root logic_remote central_headers warmup measured
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/bootstrap-manifest.json" measure "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" "$worktree" || return 1
    [[ "$selected_workload" == W1 || "$selected_workload" == W2 ]] || { deploy_fail measure workload W1-or-W2-required; return 1; }
    [[ "$(jq -r '.deployment.workload.sustainedArmOptIn' "$inventory")" == true ]] || { deploy_fail measure workload sustained-arm-opt-in-required; return 1; }
    # shellcheck disable=SC2034 # Consumed by shared target-correlation helpers.
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"
    deploy_transport_reconcile_private_uploads strict || { deploy_fail measure private-upload-registry cleanup-failed; return 1; }
    jq -e 'length == 0' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" >/dev/null || { deploy_fail measure private-upload-registry not-empty; return 1; }
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"; execution_run_id="$run_id"
    if [[ -n "$scope" ]]; then
        [[ "$scope" == "normal-flow-$selected_workload" ]] || { deploy_fail measure scope unsupported; return 1; }
        measure_root="$state_dir/acceptance-campaign-normal-flow/$selected_workload"
        support_evidence="$evidence_dir/acceptance-support"
        deploy_measure_prepare_directory "$state_dir/acceptance-campaign-normal-flow" || return 1
        deploy_measure_prepare_directory "$measure_root" || return 1
        deploy_measure_prepare_directory "$support_evidence" || return 1
        render_root="$measure_root/rendered"; private_root="$measure_root/private"; execution_run_id="$run_id-${selected_workload,,}"
        DEPLOY_MEASURE_MANIFEST="$measure_root/measure-manifest.json"; DEPLOY_MEASURE_LEDGER="$measure_root/measure-ledger.json"
        DEPLOY_MEASURE_EVIDENCE="$support_evidence/normal-flow-$selected_workload.json"; DEPLOY_MEASURE_COMMIT="$measure_root/measure-commit.json"
    else
        measure_root="$state_dir"; render_root="$state_dir/measure-rendered"; private_root="$state_dir/measure-private"
        DEPLOY_MEASURE_MANIFEST="$state_dir/measure-manifest.json"; DEPLOY_MEASURE_LEDGER="$state_dir/measure-ledger.json"
        DEPLOY_MEASURE_EVIDENCE="$evidence_dir/measure.json"; DEPLOY_MEASURE_COMMIT="$state_dir/measure-commit.json"
    fi
    deploy_measure_prepare_directory "$render_root" || return 1
    deploy_measure_prepare_directory "$private_root" || return 1
    DEPLOY_MEASURE_INVENTORY="$inventory"; DEPLOY_MEASURE_RENDER_ROOT="$render_root"; DEPLOY_MEASURE_PRIVATE_ROOT="$private_root"
    deploy_require_no_orphan_phase_files "$DEPLOY_MEASURE_LEDGER" "$DEPLOY_MEASURE_MANIFEST" "$DEPLOY_MEASURE_EVIDENCE" "$DEPLOY_MEASURE_COMMIT" || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; duration="${DEPLOY_TEST_MEASURE_DURATION_SECONDS:-$(jq -r '.deployment.workload.durationSeconds' "$inventory")}"; started_seconds="$(date +%s)"
    if [[ -e "$DEPLOY_MEASURE_LEDGER" || -L "$DEPLOY_MEASURE_LEDGER" ]]; then
        deploy_require_phase_files_match "$DEPLOY_MEASURE_LEDGER" "$DEPLOY_MEASURE_MANIFEST" "$DEPLOY_MEASURE_EVIDENCE" "$DEPLOY_MEASURE_COMMIT" \
          deploy_measure_validate_candidate "$run_id" "$mode" "$hash" "$revision" "$selected_workload" "$inventory" "$execution_run_id" || return 1
        DEPLOY_MEASURE_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' "$DEPLOY_MEASURE_LEDGER")"
    else
        DEPLOY_MEASURE_JSON="$(jq -cn --arg run "$run_id" --arg executionRun "$execution_run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg now "$now" --arg selected "$selected_workload" '
          {schemaVersion:2,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,executionMode:"canonical",
            canonicalWorkloadConfigured:true,workload:$selected,phaseStatus:"running",startedAt:$now,updatedAt:$now,targets:[]} |
          if $executionRun == $run then . else .executionRunId=$executionRun end')"
    fi
    [[ -z "$scope" ]] || DEPLOY_NORMAL_MEASURE_ACTIVE=true
    deploy_measure_publish || return 1
    if jq -e 'any(.targets[]; .restoration.status != "pending" and .restoration.status != "completed")' <<< "$DEPLOY_MEASURE_JSON" >/dev/null; then
        deploy_measure_restore_all || return 1
    fi
    logic="$(jq -c '.logicHost' "$inventory")"; logic_root="$(jq -r '.runtimeRoot' <<< "$logic")"; logic_remote="$logic_root/.hvo-deploy/measure-$execution_run_id"
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote" || return 1
    central_headers="$logic_remote/owner.headers"; deploy_bootstrap_central_headers "$inventory" "$logic" "$render_root" "$central_headers" || return 1
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; target_root="$(jq -r '.runtimeRoot' <<< "$target")"; target_remote="$target_root/.hvo-deploy/measure-$execution_run_id"
        deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$target_remote" || return 1
        response="$private_root/$name-antiforgery.json"; deploy_bootstrap_owner_session "$inventory" "$target" "$render_root" "$target_remote" "$response" || return 1
        cookies="$target_remote/owner.cookies"
        status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/internal/deployment/continuity" "" "" "$cookies" \
          "$target_remote/profile-identity.json" "$private_root/$name-profile-identity.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail measure "$name" identity-read-failed; return 1; }
        device="$(jq -er '.deviceId' "$private_root/$name-profile-identity.json")"
        profile="$(deploy_stage_workload_profile "$inventory" "$target" "$selected_workload" "$device" "$render_root" "$state_dir" "$run_id" false)" || return 1
        if ! jq -e --arg target "$name" 'any(.targets[]; .target == $target)' <<< "$DEPLOY_MEASURE_JSON" >/dev/null; then
            prior="$(deploy_measure_read_prior_state "$inventory" "$target" "$target_remote" "$private_root" "$cookies" "$run_id" \
              "$private_root/$name-profile-identity.json")" || return 1
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --arg device "$device" --argjson prior "$prior" '.targets += [{target:$target,deviceId:$device,status:"running",profile:null,canonicalScheduleProfileSha256:null,priorState:$prior,
              restoration:{status:"pending",configuration:"pending",schedule:"pending",capture:"pending"},
              controlAttempts:[],warmupStart:null,measuredStart:null,warmupCompleted:0,measuredCompleted:0,warmup:null,measured:null,before:null,after:null,
              failureCleanup:{status:"not-required"}}]' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        else
            jq -e --arg target "$name" --arg device "$device" --argjson expected "$profile" '
              .targets[] | select(.target == $target) | .deviceId == $device and (.profile == null or .profile == $expected)' <<< "$DEPLOY_MEASURE_JSON" >/dev/null ||
              { deploy_fail measure "$name" resumed-profile-state-mismatch; return 1; }
            prior_sha="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .priorState.configurationFileSha256' <<< "$DEPLOY_MEASURE_JSON")"
            backup_sha=
            if [[ -f "$private_root/$name-prior-camera-module.json" && ! -L "$private_root/$name-prior-camera-module.json" ]]; then
                backup_sha="$(sha256sum "$private_root/$name-prior-camera-module.json" | cut -d' ' -f1)"
            fi
            [[ -f "$private_root/$name-prior-camera-module.json" && ! -L "$private_root/$name-prior-camera-module.json" && "$backup_sha" == "$prior_sha" ]] ||
              { deploy_fail measure "$name" prior-profile-backup-missing; return 1; }
        fi
        restoration_status="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .restoration.status' <<< "$DEPLOY_MEASURE_JSON")"
        if [[ "$restoration_status" != pending && "$restoration_status" != completed ]]; then
            deploy_measure_pause_for_restoration "$target" || return 1
            deploy_measure_restore_target "$target" || return 1
            restoration_status=completed
            status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/internal/deployment/continuity" "" "" "$cookies" \
              "$target_remote/profile-restored.json" "$private_root/$name-profile-identity.json")" || return 1
            [[ "$status" == 200 ]] || return 1
        fi
        expected_capture_state="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .priorState.captureState' <<< "$DEPLOY_MEASURE_JSON")"
        expected_capture_version="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .priorState.captureVersion' <<< "$DEPLOY_MEASURE_JSON")"
        latest_control="$(jq -c --arg target "$name" '[.targets[] | select(.target == $target) | .controlAttempts[] |
          select(.status == "completed" or .status == "reconciled")] | last // null' \
          <<< "$DEPLOY_MEASURE_JSON")"
        if [[ "$latest_control" != null ]]; then
            expected_capture_state="$(jq -r '.resultState' <<< "$latest_control")"
            expected_capture_version="$(jq -r '.resultVersion' <<< "$latest_control")"
        fi
        if [[ "$restoration_status" == completed ]]; then expected_capture_state="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .priorState.captureState' <<< "$DEPLOY_MEASURE_JSON")"; expected_capture_version=; fi
        deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause \
          "$execution_run_id" pre-activation-pause Paused false 0 \
          "$expected_capture_state" "$expected_capture_version" || return 1
        status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/internal/deployment/continuity" "" "" "$cookies" \
          "$target_remote/pre-activation-profile-state.json" "$private_root/$name-profile-identity.json")" || return 1
        [[ "$status" == 200 ]] || return 1
        reactivate_after_restoration=false
        if [[ "$restoration_status" == completed ]]; then
            reactivate_after_restoration=true
            deploy_measure_set_restoration "$name" pending pending pending pending not-required || return 1
            restoration_status=pending
        fi
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .profile == null' <<< "$DEPLOY_MEASURE_JSON")" == true ]]; then
            if ! jq -e --arg device "$device" --arg sha "$(jq -r '.configSha256' <<< "$profile")" '
              .deviceId == $device and (.activeConfigurationSha256|ascii_downcase) == ($sha|ascii_downcase)' "$private_root/$name-profile-identity.json" >/dev/null; then
                jq -e --arg sha "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .priorState.configurationSha256' <<< "$DEPLOY_MEASURE_JSON")" \
                  '(.activeConfigurationSha256 | ascii_downcase) == $sha' "$private_root/$name-profile-identity.json" >/dev/null ||
                  { deploy_fail measure "$name" pre-activation-profile-drift; return 1; }
                [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-before-measure-profile-stage ]] || exit 75
                profile="$(deploy_stage_workload_profile "$inventory" "$target" "$selected_workload" "$device" "$render_root" "$state_dir" "$run_id" true)" || return 1
                deploy_measure_record_canonical_schedule_sha "$target" "$target_remote" "$private_root" "$cookies" || return 1
                status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/internal/deployment/continuity" "" "" "$cookies" \
                  "$target_remote/profile-activated.json" "$private_root/$name-profile-activated.json")" || return 1
                if [[ "$status" != 200 ]] || ! jq -e --arg device "$device" --arg sha "$(jq -r '.configSha256' <<< "$profile")" '
                  .deviceId == $device and (.activeConfigurationSha256|ascii_downcase) == ($sha|ascii_downcase)' "$private_root/$name-profile-activated.json" >/dev/null; then
                    deploy_fail measure "$name" activated-profile-state-mismatch
                    return 1
                fi
                if [[ "${DEPLOY_TEST_FAILPOINT:-}" == signal-after-measure-profile-activation ]]; then
                    : > "$private_root/$name-signal-profile-ready"
                    while :; do sleep 1; done
                fi
                [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-measure-profile-activation ]] || exit 75
            fi
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson profile "$profile" '.targets |= map(if .target == $target then .profile=$profile else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        else
            if [[ "$reactivate_after_restoration" == true ]]; then
                profile="$(deploy_stage_workload_profile "$inventory" "$target" "$selected_workload" "$device" "$render_root" "$state_dir" "$run_id" true)" || return 1
                deploy_measure_record_canonical_schedule_sha "$target" "$target_remote" "$private_root" "$cookies" || return 1
                status="$(deploy_bootstrap_request "$target" GET "$(jq -r '.internalEndpoint' <<< "$target")/api/internal/deployment/continuity" "" "" "$cookies" \
                  "$target_remote/profile-reactivated.json" "$private_root/$name-profile-identity.json")" || return 1
                if [[ "$status" != 200 ]] || ! jq -e --arg device "$device" --arg sha "$(jq -r '.configSha256' <<< "$profile")" '
                  .deviceId == $device and (.activeConfigurationSha256|ascii_downcase) == ($sha|ascii_downcase)' "$private_root/$name-profile-identity.json" >/dev/null; then
                    deploy_fail measure "$name" resumed-profile-state-mismatch
                    return 1
                fi
            else
                jq -e --arg device "$device" --arg sha "$(jq -r '.configSha256' <<< "$profile")" '
                  .deviceId == $device and (.activeConfigurationSha256|ascii_downcase) == ($sha|ascii_downcase)' "$private_root/$name-profile-identity.json" >/dev/null ||
                  { deploy_fail measure "$name" resumed-profile-state-mismatch; return 1; }
            fi
        fi
        deploy_measure_record_canonical_schedule_sha "$target" "$target_remote" "$private_root" "$cookies" || return 1
        deploy_measure_activate_profile "$target" "$target_remote" "$private_root" "$render_root" "$cookies" \
          "$render_root/$name-$selected_workload-camera-module.json" "$selected_workload" "$execution_run_id" \
          "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .priorState.scheduleRevisionId' <<< "$DEPLOY_MEASURE_JSON")" || return 1
        interval_value="$(jq -er '.rig.pipeline.captureInterval | select(test("^[0-9]{2}:[0-9]{2}:[0-9]{2}([.][0-9]+)?$"))' "$render_root/$name-$selected_workload-camera-module.json")" || return 1
        IFS=: read -r interval_hours interval_minutes interval_seconds <<< "$interval_value"; interval_seconds="${interval_seconds%%.*}"
        capture_interval_seconds=$(( 10#$interval_hours * 3600 + 10#$interval_minutes * 60 + 10#$interval_seconds ))
        (( capture_interval_seconds >= 1 )) || return 1
        warmup_requested="$(jq -r '.warmupOperations' <<< "$profile")"; measured_requested="$(jq -r '.measuredOperations' <<< "$profile")"
        deadline=$(( $(date +%s) + duration ))
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .failureCleanup.status' <<< "$DEPLOY_MEASURE_JSON")" == capture-may-be-running ]]; then
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$execution_run_id" failure-pause Paused true || return 1
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" '.targets |= map(if .target == $target then .failureCleanup.status="completed" else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        fi
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .status' <<< "$DEPLOY_MEASURE_JSON")" == measured ]]; then
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$execution_run_id" completed-reconcile-pause Paused || return 1
            measured_start="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .measuredStart' <<< "$DEPLOY_MEASURE_JSON")"
            measured="$(deploy_measure_wait_capture_set "$target" "$logic" "$target_remote" "$logic_remote" "$private_root" "$cookies" "$central_headers" "$device" measured "$measured_start" "$measured_requested" "$deadline")" || return 1
            [[ "$(jq -S -c . <<< "$measured")" == "$(jq -S -c --arg target "$name" '.targets[] | select(.target == $target) | .measured' <<< "$DEPLOY_MEASURE_JSON")" ]] ||
              { deploy_fail measure "$name" completed-boundary-state-mismatch; return 1; }
            continue
        fi
        warmup_start="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .warmupStart // empty' <<< "$DEPLOY_MEASURE_JSON")"
        if [[ -z "$warmup_start" ]]; then
            deploy_measure_capture_control "$target" "$target_remote" "$private_root" "$render_root" "$cookies" pause "$execution_run_id" initial-pause Paused || return 1
            warmup_start="$(deploy_measure_read_sequence "$target" "$target_remote" "$private_root" "$cookies" warmup-start "$device")" ||
              { deploy_fail measure "$name" warmup-start-invalid; return 1; }
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson start "$warmup_start" '.targets |= map(if .target == $target then .warmupStart=$start else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
        fi
        if [[ "$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .warmup == null' <<< "$DEPLOY_MEASURE_JSON")" == true ]]; then
            deploy_measure_execute_exact_count "$target" "$target_remote" "$private_root" "$render_root" "$cookies" "$device" "$execution_run_id" warmup "$warmup_start" "$warmup_requested" "$deadline" "$capture_interval_seconds" || return 1
            warmup="$(deploy_measure_wait_capture_set "$target" "$logic" "$target_remote" "$logic_remote" "$private_root" "$cookies" "$central_headers" "$device" warmup "$warmup_start" "$warmup_requested" "$deadline")" || return 1
            if [[ "${DEPLOY_TEST_FAILPOINT:-}" == signal-after-measure-warmup-convergence ]]; then
                : > "$private_root/$name-signal-warmup-ready"
                while :; do sleep 1; done
            fi
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
            deploy_measure_execute_exact_count "$target" "$target_remote" "$private_root" "$render_root" "$cookies" "$device" "$execution_run_id" measured "$measured_start" "$measured_requested" "$deadline" "$capture_interval_seconds" || return 1
            measured="$(deploy_measure_wait_capture_set "$target" "$logic" "$target_remote" "$logic_remote" "$private_root" "$cookies" "$central_headers" "$device" measured "$measured_start" "$measured_requested" "$deadline")" || return 1
            measured_completed="$(jq -r '.count' <<< "$measured")"
            DEPLOY_MEASURE_JSON="$(jq -c --arg target "$name" --argjson facts "$measured" --argjson count "$measured_completed" '.targets |= map(if .target == $target then .measured=$facts | .measuredCompleted=$count else . end)' <<< "$DEPLOY_MEASURE_JSON")"
            deploy_measure_publish || return 1
            [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-measure-measured-complete ]] || exit 75
        fi
        deploy_measure_snapshot "$inventory" "$target" "$target_remote" "$private_root" "$cookies" after "$state_dir" "$device" "$(jq -r '.configSha256' <<< "$profile")" ||
          { deploy_fail measure "$name" final-snapshot-invalid; return 1; }
        measured_end="$(jq -r --arg target "$name" '.targets[] | select(.target == $target) | .measured.endSequence' <<< "$DEPLOY_MEASURE_JSON")"
        expected_mode="$(jq -er '.configuration.value.transientDetection | select(. == "Off" or . == "Hybrid")' "$private_root/$name-after-operations.json")" || return 1
        deploy_measure_queues_converged "$private_root/$name-after-operations.json" "$measured_end" "$device" "$expected_mode" ||
          { deploy_fail measure "$name" final-snapshot-queues-not-converged; return 1; }
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
