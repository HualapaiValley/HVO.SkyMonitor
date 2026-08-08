#!/usr/bin/env bash

deploy_smoke_mark_failed() {
    local status="${1:-1}" now
    trap - ERR
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -n "${DEPLOY_SMOKE_JSON:-}" ]]; then
        DEPLOY_SMOKE_JSON="$(jq -c --arg now "$now" '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_SMOKE_JSON")"
        [[ -z "${DEPLOY_SMOKE_LEDGER:-}" ]] || deploy_smoke_publish >/dev/null 2>&1 || true
    fi
    deploy_transport_reconcile_private_uploads best-effort >/dev/null 2>&1 || true
    deploy_bootstrap_cleanup_private_remote best-effort || true
    return "$status"
}

deploy_smoke_validate_artifacts() {
    local central="$1" before="$2" initial_lineage="$3" initial_derivatives="$4" recipes="$5" width="$6" height="$7" format="$8"
    jq -e --argjson before "$before" --argjson initialLineage "$initial_lineage" --argjson initialDerivatives "$initial_derivatives" \
      --argjson recipes "$recipes" --argjson width "$width" --argjson height "$height" --arg format "$format" '. as $root |
      [.latestArtifacts[] | select((.captureSequence // -1) > $before)] as $window |
      ($window | length) > 0 and
      all($window[]; . as $artifact |
        $artifact.byteLength > 0 and ($artifact.checksumSha256 | test("^[0-9A-Fa-f]{64}$")) and
        $artifact.objectState == "Available" and $artifact.objectVerifiedAtUtc != null and
        ($artifact.recipeName | type == "string" and length > 0) and
        ($artifact.recipeSemanticVersion | type == "string" and length > 0) and
        ($artifact.recipeImplementationVersion | type == "string" and length > 0) and
        ($artifact.role == "Raw" or ($recipes | index($artifact.recipeVersion)) != null) and
        (if $artifact.role == "Raw" then $artifact.width == $width and $artifact.height == $height and
          $artifact.pixelFormat == $format and $artifact.byteLength == ($width * $height * 2) else true end) and
        all($artifact.sources[]; . as $source |
          ($source.checksumSha256 | test("^[0-9A-Fa-f]{64}$")) and
          ([$root.latestArtifacts[] | select(.artifactId == $source.artifactId) | .checksumSha256] == [$source.checksumSha256]))) and
      (.lineageSourceCount - $initialLineage) >= ([$window[].sources | length] | add)' "$central" >/dev/null
}

deploy_smoke_validate_derivative_provenance() {
    local central="$1" before="$2" initial_derivatives="$3"
    jq -e --argjson before "$before" --argjson initialDerivatives "$initial_derivatives" '. as $root |
      [.latestArtifacts[] | select((.captureSequence // -1) > $before and (.sources | length) > 0)] as $derivatives |
      (.completedDerivativeCount - $initialDerivatives) > 0 and ($derivatives | length) > 0 and
      all($derivatives[]; . as $derivative |
        .role != "Raw" and all(.sources[]; . as $source |
          ($source.artifactId | type == "string") and ($source.checksumSha256 | test("^[0-9A-Fa-f]{64}$")) and
          any($root.latestArtifacts[]; .artifactId == $source.artifactId and
            (.checksumSha256 | ascii_downcase) == ($source.checksumSha256 | ascii_downcase))))' "$central" >/dev/null
}

deploy_smoke_select_checksum_proof() {
    local central="$1" local_continuity="$2" before="$3"
    jq -c --argjson before "$before" --slurpfile local "$local_continuity" '
      [.latestArtifacts[] | select((.captureSequence // -1) > $before) as $central |
        select($central.role == "Raw") |
        $local[0].durable.latestArtifacts[] |
        select(.status == "acknowledged" and .role == "Raw" and .artifactId == $central.artifactId) |
        $central + {localChecksumSha256:.checksumSha256}] |
      sort_by(.captureSequence,.artifactId) | first // empty' "$central"
}

deploy_smoke_validate_checksum_proof() {
    local local_checksum="$1" central_checksum="$2" retrieved_checksum="$3" declared_checksum="$4"
    [[ "$local_checksum" =~ ^[0-9A-Fa-f]{64}$ && "${local_checksum^^}" == "${central_checksum^^}" &&
       "${local_checksum^^}" == "${retrieved_checksum^^}" && "${local_checksum^^}" == "${declared_checksum^^}" ]]
}

deploy_smoke_metrics_facts() {
    local path="$1" facts
    [[ -f "$path" && ! -L "$path" && "$(stat -c '%h:%a' "$path")" == "1:600" && "$(stat -c %s "$path")" -le 1048576 ]] || return 1
    ! grep -Eiq 'password|authorization|bearer|client[_-]?secret|device[_-]?key|envelope|payload' "$path" || return 1
    facts="$(awk '
      BEGIN { capture=0; fleet=0; captureSeries=0; fleetSeries=0 }
      /^camera_agent_capture_control_cycles_total(\{[^}]{0,256}\})?[[:space:]]+[0-9]+([.][0-9]+)?$/ { capture += $NF; captureSeries++; next }
      /^hvo_fleet_edge_reports_total(\{[^}]{0,256}\})?[[:space:]]+[0-9]+([.][0-9]+)?$/ { fleet += $NF; fleetSeries++; next }
      END { if (capture > 0 && fleet > 0 && captureSeries <= 32 && fleetSeries <= 32)
        printf "%.0f\t%.0f\t%d\t%d\n", capture, fleet, captureSeries, fleetSeries; else exit 1 }
    ' "$path")" || return 1
    IFS=$'\t' read -r capture fleet capture_series fleet_series <<< "$facts"
    jq -cn --argjson capture "$capture" --argjson fleet "$fleet" --argjson captureSeries "$capture_series" --argjson fleetSeries "$fleet_series" \
      '{captureControlCycles:$capture,fleetReports:$fleet,series:{captureControl:$captureSeries,fleet:$fleetSeries}}'
}

deploy_smoke_log_facts() {
    local path="$1" lines
    [[ -f "$path" && ! -L "$path" && "$(stat -c '%h:%a' "$path")" == "1:600" && "$(stat -c %s "$path")" -le 65536 ]] || return 1
    lines="$(wc -l < "$path")"
    (( lines >= 1 && lines <= 200 )) || return 1
    ! grep -Eiq 'password|authorization:|bearer[[:space:]]|client[_-]?secret|device[_-]?key|envelope|request body|response body' "$path" || return 1
    jq -cn --argjson lines "$lines" '{boundedLineCount:$lines,sensitiveContentRejected:true}'
}

deploy_smoke_publish() {
    deploy_phase_publish smoke DEPLOY_SMOKE_JSON "$DEPLOY_SMOKE_LEDGER" "$DEPLOY_SMOKE_MANIFEST" \
      "$DEPLOY_SMOKE_EVIDENCE" "$DEPLOY_SMOKE_COMMIT"
}

deploy_smoke_validate_candidate() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" inventory="$6"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --argjson inventory "$(jq -c . "$inventory")" '
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
      (.publicationGeneration | numbers) >= 1 and (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      ((keys - ["completedAt"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","workload","phaseStatus","startedAt","updatedAt","targets"] | sort)) and
      (.targets | type == "array" and length == ([.[].target] | unique | length) and
        all(.[]; .target as $target | (keys | sort) == (["target","deviceId","status","profile","captureSequence","checks","artifacts","telemetry"] | sort) and
          ([ $inventory.cameraAgents[].name ] | index($target) != null))) and
      (if .phaseStatus == "passed" then ([.targets[].target] | sort) == ([$inventory.cameraAgents[].name] | sort) else true end)' "$path" >/dev/null 2>&1 ||
      { deploy_fail smoke ledger invalid; return 1; }
}

deploy_run_smoke() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6"
    local state_dir evidence_dir render_root private_root now duration deadline logic logic_root logic_remote headers target name target_root target_remote
    local endpoint cookies response status initial_local initial_central current_local current_central operations device_id checks deadline
    local initial_lineage initial_derivatives expected_recipes artifact_facts proof_artifact proof_id proof_device local_checksum retrieval_result retrieval_status retrieved_checksum retrieved_bytes declared_checksum
    local metrics_status metrics_facts log_facts telemetry_status telemetry_facts telemetry_evidence context project env_file workload_profile
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/bootstrap-manifest.json" smoke "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" "$worktree" || return 1
    [[ "$(jq -r '.deployment.workload.kind' "$inventory")" == W0 ]] || { deploy_fail smoke workload explicit-measure-required; return 1; }
    # shellcheck disable=SC2034 # Consumed by shared bootstrap/transport helpers.
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"
    deploy_transport_reconcile_private_uploads strict || { deploy_fail smoke private-upload-registry cleanup-failed; return 1; }
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    render_root="$state_dir/smoke-rendered"; private_root="$state_dir/smoke-private"
    install -d -m 700 "$render_root" "$private_root"
    DEPLOY_SMOKE_MANIFEST="$state_dir/smoke-manifest.json"; DEPLOY_SMOKE_LEDGER="$state_dir/smoke-ledger.json"; DEPLOY_SMOKE_EVIDENCE="$evidence_dir/smoke.json"
    DEPLOY_SMOKE_COMMIT="$state_dir/smoke-commit.json"
    deploy_require_no_orphan_phase_files "$DEPLOY_SMOKE_LEDGER" "$DEPLOY_SMOKE_MANIFEST" "$DEPLOY_SMOKE_EVIDENCE" "$DEPLOY_SMOKE_COMMIT" || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; duration="$(jq -r '.deployment.workload.durationSeconds' "$inventory")"
    if [[ -e "$DEPLOY_SMOKE_LEDGER" || -L "$DEPLOY_SMOKE_LEDGER" ]]; then
        deploy_require_phase_files_match "$DEPLOY_SMOKE_LEDGER" "$DEPLOY_SMOKE_MANIFEST" "$DEPLOY_SMOKE_EVIDENCE" "$DEPLOY_SMOKE_COMMIT" \
          deploy_smoke_validate_candidate "$run_id" "$mode" "$hash" "$revision" "$inventory" || return 1
        jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" '
          .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
          (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
          ((keys - ["completedAt"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","workload","phaseStatus","startedAt","updatedAt","targets"] | sort)) and
          (.targets | type == "array" and length == ([.[].target] | unique | length) and
            all(.[]; (keys | sort) == (["target","deviceId","status","profile","captureSequence","checks","artifacts","telemetry"] | sort)))' "$DEPLOY_SMOKE_LEDGER" >/dev/null 2>&1 ||
          { deploy_fail smoke ledger invalid; return 1; }
        DEPLOY_SMOKE_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' "$DEPLOY_SMOKE_LEDGER")"
    else
        DEPLOY_SMOKE_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg now "$now" \
          --argjson workload "$(jq -c '.deployment.workload' "$inventory")" \
          '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,workload:$workload,
            phaseStatus:"running",startedAt:$now,updatedAt:$now,targets:[]}')"
    fi
    deploy_smoke_publish

    logic="$(jq -c '.logicHost' "$inventory")"; logic_root="$(jq -r '.runtimeRoot' <<< "$logic")"; logic_remote="$logic_root/.hvo-deploy/smoke-$run_id"
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote" || return 1
    headers="$logic_remote/owner.headers"; deploy_bootstrap_central_headers "$inventory" "$logic" "$render_root" "$headers" || return 1
    for path in /alive /health /metrics; do
        deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$logic")" "$(jq -r '.internalEndpoint' <<< "$logic")$path" ||
          { deploy_fail smoke logic "${path#/}-failed"; return 1; }
    done

    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; target_root="$(jq -r '.runtimeRoot' <<< "$target")"; target_remote="$target_root/.hvo-deploy/smoke-$run_id"
        deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$target_remote" || return 1
        deadline=$(( $(date +%s) + duration ))
        response="$private_root/$name-antiforgery.json"; deploy_bootstrap_owner_session "$inventory" "$target" "$render_root" "$target_remote" "$response" || return 1
        cookies="$target_remote/owner.cookies"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
        for path in /alive /health /metrics; do
            deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$target")" "$endpoint$path" ||
              { deploy_fail smoke "$name" "${path#/}-failed"; return 1; }
        done
        status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
          "$target_remote/initial-continuity.json" "$private_root/$name-initial-continuity.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail smoke "$name" local-continuity-failed; return 1; }
        device_id="$(jq -er '.deviceId' "$private_root/$name-initial-continuity.json")" || return 1
        initial_local="$(jq -r --arg device "$device_id" '[.durable.captureSequences[] | select(.agentId == $device) | .lastSequence][0] // 0' "$private_root/$name-initial-continuity.json")"
        status="$(deploy_bootstrap_request "$logic" GET "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/devices/continuity/$device_id" "" "$headers" "" \
          "$logic_remote/$name-initial-central.json" "$private_root/$name-initial-central.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail smoke "$name" central-continuity-failed; return 1; }
        initial_central="$(jq -r '.maximumCaptureSequence // 0' "$private_root/$name-initial-central.json")"
        initial_lineage="$(jq -r '.lineageSourceCount' "$private_root/$name-initial-central.json")"
        initial_derivatives="$(jq -r '.completedDerivativeCount' "$private_root/$name-initial-central.json")"
        workload_profile="$(deploy_stage_workload_profile "$inventory" "$target" W0 "$device_id" "$render_root" "$state_dir" "$run_id")" || return 1
        expected_recipes="$(jq -c '[.. | objects | .recipeVersion? // empty] | unique' "$render_root/$name-W0-camera-module.json")" || return 1
        deadline=$(( $(date +%s) + duration ))
        checks=""
        while (( $(date +%s) <= deadline )); do
            status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
              "$target_remote/current-continuity.json" "$private_root/$name-current-continuity.json")" || return 1
            [[ "$status" == 200 ]] || { deploy_fail smoke "$name" unexpected-local-continuity-status; return 1; }
            status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/summary" "" "" "$cookies" \
              "$target_remote/operations.json" "$private_root/$name-operations.json")" || return 1
            [[ "$status" == 200 ]] || { deploy_fail smoke "$name" unexpected-operations-status; return 1; }
            status="$(deploy_bootstrap_request "$logic" GET "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/devices/continuity/$device_id" "" "$headers" "" \
              "$logic_remote/$name-current-central.json" "$private_root/$name-current-central.json")" || return 1
            [[ "$status" == 200 ]] || { deploy_fail smoke "$name" unexpected-central-continuity-status; return 1; }
            current_local="$(jq -r --arg device "$device_id" '[.durable.captureSequences[] | select(.agentId == $device) | .lastSequence][0] // 0' "$private_root/$name-current-continuity.json")"
            current_central="$(jq -r '.maximumCaptureSequence // 0' "$private_root/$name-current-central.json")"
            operations="$private_root/$name-operations.json"
            if (( current_local > initial_local && current_central > initial_central )) &&
              jq -e --arg expected "$(jq -r '.configSha256' <<< "$workload_profile")" '
                (.activeConfigurationSha256 | ascii_downcase) == ($expected | ascii_downcase)' "$private_root/$name-current-continuity.json" >/dev/null &&
              deploy_smoke_validate_artifacts "$private_root/$name-current-central.json" "$initial_central" "$initial_lineage" "$initial_derivatives" "$expected_recipes" \
                "$(jq -r '.width' <<< "$workload_profile")" "$(jq -r '.height' <<< "$workload_profile")" "$(jq -r '.pixelFormat' <<< "$workload_profile")" &&
              deploy_smoke_validate_derivative_provenance "$private_root/$name-current-central.json" "$initial_central" "$initial_derivatives" &&
              jq -e --arg device "$device_id" '
              .configuration.value.agentId == $device and .configuration.value.centralIntegration == "Enabled" and
              .captureTelemetry.value.sampleCount > 0 and .rawIngress.value.pendingCount == 0 and
              .captureLanes.value.pendingCount == 0 and .captureProcessing.value.pendingCount == 0 and
              .artifactOutbox.value.pendingCount == 0 and .rawIngress.value.quarantineCount == 0 and
              .captureLanes.value.quarantineCount == 0 and .artifactOutbox.value.quarantineCount == 0 and
              .heartbeat.value.lastAcknowledgedUtc != null' "$operations" >/dev/null; then checks=true; break; fi
            sleep 2
        done
        [[ "$checks" == true ]] || { deploy_fail smoke "$name" bounded-convergence-timeout; return 1; }
        proof_artifact="$(deploy_smoke_select_checksum_proof "$private_root/$name-current-central.json" \
          "$private_root/$name-current-continuity.json" "$initial_central")"
        [[ -n "$proof_artifact" ]] || { deploy_fail smoke "$name" retrievable-artifact-missing; return 1; }
        proof_id="$(jq -er '.artifactId' <<< "$proof_artifact")"; proof_device="$(jq -er '.devicePublicId' "$private_root/$name-current-central.json")"
        local_checksum="$(jq -er '.localChecksumSha256' <<< "$proof_artifact")"
        deploy_phase_correlate_target "$logic" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        retrieval_result="$(deploy_transport_http_hash_private "$(jq -r '.sshHost' <<< "$logic")" \
          "$(jq -r '.internalEndpoint' <<< "$logic")/api/v1.0/devices/$proof_device/artifacts/$proof_id/content" "$headers" "$logic_remote")" ||
          { deploy_fail smoke "$name" artifact-retrieval-failed; return 1; }
        deploy_phase_correlate_target "$logic" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        IFS=$'\t' read -r retrieval_status retrieved_checksum retrieved_bytes declared_checksum <<< "$retrieval_result"
        if [[ "$retrieval_status" != 200 || "$retrieved_bytes" != "$(jq -r '.byteLength' <<< "$proof_artifact")" ]] ||
          ! deploy_smoke_validate_checksum_proof "$local_checksum" "$(jq -r '.checksumSha256' <<< "$proof_artifact")" "$retrieved_checksum" "$declared_checksum"; then
            deploy_fail smoke "$name" artifact-checksum-proof-mismatch
            return 1
        fi
        metrics_status="$(deploy_bootstrap_request "$target" GET "$endpoint/metrics" "" "" "" \
          "$target_remote/metrics.txt" "$private_root/$name-metrics.txt")" || return 1
        if [[ "$metrics_status" != 200 ]] || ! metrics_facts="$(deploy_smoke_metrics_facts "$private_root/$name-metrics.txt")"; then
            deploy_fail smoke "$name" bounded-metrics-invalid
            return 1
        fi
        context="$(jq -r '.dockerContext' <<< "$target")"; project="$(jq -r '.deployment.resources.project' "$inventory")-$name"; env_file="$state_dir/up-rendered/$name.env"
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        deploy_transport_compose_logs "$context" "$project" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" cameraagent \
          "$(jq -r '.startedAt' <<< "$DEPLOY_SMOKE_JSON")" "$private_root/$name-application.log" || return 1
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        log_facts="$(deploy_smoke_log_facts "$private_root/$name-application.log")" ||
          { deploy_fail smoke "$name" bounded-logs-invalid; return 1; }
        telemetry_status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/telemetry?captureSequence=$(jq -r '.captureSequence' <<< "$proof_artifact")&artifactId=$proof_id" "" "" "$cookies" \
          "$target_remote/telemetry.json" "$private_root/$name-telemetry.json")" || return 1
        if [[ "$telemetry_status" != 200 ]] || ! telemetry_facts="$(jq -c --arg artifact "$proof_id" --argjson sequence "$(jq '.captureSequence' <<< "$proof_artifact")" '
          select(.artifactId == $artifact and .captureSequence == $sequence and
            (.captureId | test("^[0-9a-fA-F-]{36}$")) and (.traceId | test("^[0-9a-f]{32}$")) and (.spanId | test("^[0-9a-f]{16}$"))) |
          {captureSequence,captureId,artifactId,traceId,spanId,source:"capture-pipeline"}' "$private_root/$name-telemetry.json")" || [[ -z "$telemetry_facts" ]]; then
            deploy_fail smoke "$name" required-trace-unavailable
            return 1
        fi
        telemetry_evidence="$(jq -cn --argjson metrics "$metrics_facts" --argjson logs "$log_facts" --argjson trace "$telemetry_facts" \
          --arg started "$(jq -r '.startedAt' <<< "$DEPLOY_SMOKE_JSON")" '{windowStartedAt:$started,metrics:$metrics,logs:$logs,trace:$trace}')"
        artifact_facts="$(jq -c --argjson before "$initial_central" '[.latestArtifacts[] | select((.captureSequence // -1) > $before) |
          {artifactId,role,captureSequence,width,height,pixelFormat,byteLength,checksumSha256,objectState,objectVerifiedAtUtc,recipeVersion,recipeName,
            recipeSemanticVersion,recipeImplementationVersion,sourceCount:(.sources|length),completedDerivativeCount}]' "$private_root/$name-current-central.json")"
        artifact_facts="$(jq -c --arg artifact "$proof_id" --arg local "${local_checksum^^}" --arg retrieved "$retrieved_checksum" \
          'map(if .artifactId == $artifact then . + {localChecksumSha256:$local,retrievedChecksumSha256:$retrieved,objectVerified:true} else . end)' <<< "$artifact_facts")"
        DEPLOY_SMOKE_JSON="$(jq -c --arg target "$name" --arg device "$device_id" --argjson profile "$workload_profile" --argjson localBefore "$initial_local" --argjson localAfter "$current_local" \
          --argjson centralBefore "$initial_central" --argjson centralAfter "$current_central" --argjson artifacts "$artifact_facts" \
          --argjson telemetry "$telemetry_evidence" '.targets = ([.targets[] | select(.target != $target)] + [{target:$target,deviceId:$device,status:"passed",profile:$profile,
            captureSequence:{localBefore:$localBefore,localAfter:$localAfter,centralBefore:$centralBefore,centralAfter:$centralAfter},
            checks:{health:true,metrics:true,identity:true,capture:true,queuesConverged:true,fleetAcknowledged:true,
            centralArtifacts:true,derivativeProvenance:true,artifactHashProof:true,traces:true,boundedLogs:true},artifacts:$artifacts,telemetry:$telemetry}])' <<< "$DEPLOY_SMOKE_JSON")"
        deploy_smoke_publish
    done < <(jq -c '.cameraAgents[]' "$inventory")
    deploy_transport_reconcile_private_uploads strict || return 1
    deploy_bootstrap_cleanup_private_remote
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-smoke-cleanup ]] || exit 75
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; DEPLOY_SMOKE_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_SMOKE_JSON")"
    deploy_smoke_publish
}
