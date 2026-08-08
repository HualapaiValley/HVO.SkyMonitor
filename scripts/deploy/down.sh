#!/usr/bin/env bash

deploy_down_mark_failed() {
    local status="${1:-1}" now
    trap - ERR
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -n "${DEPLOY_DOWN_JSON:-}" ]]; then
        DEPLOY_DOWN_JSON="$(jq -c --arg now "$now" '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_DOWN_JSON")"
        [[ -z "${DEPLOY_DOWN_LEDGER:-}" ]] || deploy_down_publish >/dev/null 2>&1 || true
    fi
    deploy_transport_reconcile_private_uploads best-effort >/dev/null 2>&1 || true
    deploy_bootstrap_cleanup_private_remote best-effort || true
    return "$status"
}

deploy_down_publish() {
    DEPLOY_DOWN_JSON="$(jq -c --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '.updatedAt=$now' <<< "$DEPLOY_DOWN_JSON")"
    deploy_phase_publish down DEPLOY_DOWN_JSON "$DEPLOY_DOWN_LEDGER" "$DEPLOY_DOWN_MANIFEST" "$DEPLOY_DOWN_EVIDENCE" "$DEPLOY_DOWN_COMMIT"
}

deploy_down_validate_candidate() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" policy="$6"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg policy "$policy" '
      .schemaVersion == 2 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and .policy == $policy and
      (.publicationGeneration | numbers) >= 1 and (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      ((keys - ["completedAt"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","policy","phaseStatus","startedAt","updatedAt","resources"] | sort)) and
      (.resources | type == "array" and length == ([.[] | [.resource,.action]] | unique | length) and all(.[];
        (keys - ["completedAt"] | sort) == (["resource","action","status","intentAt","updatedAt"] | sort) and
        (.status == "intent" or .status == "completed")))' "$path" >/dev/null 2>&1 || { deploy_fail down ledger invalid; return 1; }
}

deploy_down_begin_action() {
    local resource="$1" action="$2" now existing
    existing="$(jq -r --arg resource "$resource" --arg action "$action" '.resources[]? | select(.resource == $resource and .action == $action) | .status' <<< "$DEPLOY_DOWN_JSON")"
    [[ "$existing" != completed ]] || return 2
    [[ -z "$existing" || "$existing" == intent ]] || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_DOWN_JSON="$(jq -c --arg resource "$resource" --arg action "$action" --arg now "$now" '
      .resources = ([.resources[] | select(.resource != $resource or .action != $action)] +
        [{resource:$resource,action:$action,status:"intent",intentAt:((.resources[]? |
          select(.resource == $resource and .action == $action) | .intentAt) // $now),updatedAt:$now}])' <<< "$DEPLOY_DOWN_JSON")"
    deploy_down_publish
}

deploy_down_complete_action() {
    local resource="$1" action="$2" now
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_DOWN_JSON="$(jq -c --arg resource "$resource" --arg action "$action" --arg now "$now" '
      .resources = [.resources[] | if .resource == $resource and .action == $action then
        .status="completed" | .completedAt=$now | .updatedAt=$now else . end]' <<< "$DEPLOY_DOWN_JSON")"
    deploy_down_publish
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != "after-$action-$resource" ]] || return 75
}

deploy_down_after_mutation() {
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != "after-$2-mutation-$1" ]] || return 75
}

deploy_down_require_service_stopped() {
    local target="$1" context="$2" project="$3" env_file="$4" compose_file="$5" service="$6" state
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    state="$(deploy_transport_compose_service_state "$context" "$project" "$env_file" "$compose_file" "$service")" || return 1
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    [[ "$state" == stopped || "$state" == absent ]] || { deploy_fail down "$project:$service" completed-container-state-invalid; return 1; }
}

deploy_down_require_service_absent() {
    local target="$1" context="$2" project="$3" env_file="$4" compose_file="$5" service="$6" state
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    state="$(deploy_transport_compose_service_state "$context" "$project" "$env_file" "$compose_file" "$service")" || return 1
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    [[ "$state" == absent ]] || { deploy_fail down "$project:$service" completed-container-state-invalid; return 1; }
}

deploy_down_require_shared_services() {
    local expected="$1" target="$2" context="$3" project="$4" env_file="$5" service
    for service in sqlserver redis minio mailpit; do
        if [[ "$expected" == stopped ]]; then
            deploy_down_require_service_stopped "$target" "$context" "$project" "$env_file" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" "$service" || return 1
        else
            deploy_down_require_service_absent "$target" "$context" "$project" "$env_file" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" "$service" || return 1
        fi
    done
}

deploy_down_pause_and_drain() {
    local inventory="$1" target="$2" render_root="$3" private_root="$4" remote_root="$5" run_id="$6" name endpoint cookies response status drained idempotency_key request_headers attempt
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    response="$private_root/$name-antiforgery.json"
    deploy_bootstrap_owner_session "$inventory" "$target" "$render_root" "$remote_root" "$response" || return 1
    cookies="$remote_root/owner.cookies"
    jq -cn '{reason:"split-host graceful shutdown"}' > "$render_root/$name-pause.json"
    deploy_bootstrap_stage_json "$target" "$render_root/$name-pause.json" "$remote_root/pause.json" || return 1
    attempt="$(jq -r '.publicationGeneration' <<< "$DEPLOY_DOWN_JSON")"; idempotency_key="deploy-down-$run_id-$name-pause-$attempt"
    request_headers="$remote_root/pause-$attempt-control.headers"
    deploy_bootstrap_register_private_remote "$target" "$request_headers" || return 1
    deploy_transport_derive_idempotent_headers "$(jq -r '.sshHost' <<< "$target")" "$remote_root/owner.headers" "$request_headers" "$idempotency_key" || return 1
    status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/v1/operations/capture/pause" "$remote_root/pause.json" "$request_headers" "$cookies" \
      "$remote_root/pause-response.json" "$private_root/$name-pause-response.json")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail down "$name" unexpected-pause-status; return 1; }
    drained=false
    for _ in $(seq 1 "${DEPLOY_TEST_DRAIN_ATTEMPTS:-60}"); do
        status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1/operations/summary" "" "" "$cookies" \
          "$remote_root/summary.json" "$private_root/$name-summary.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail down "$name" unexpected-drain-status; return 1; }
        if jq -e '.rawIngress.value.pendingCount == 0 and .rawIngress.value.leasedCount == 0 and
          .captureLanes.value.pendingCount == 0 and .captureLanes.value.leasedCount == 0 and
          .captureProcessing.value.pendingCount == 0 and .captureProcessing.value.leasedCount == 0 and
          .artifactOutbox.value.pendingCount == 0 and .artifactOutbox.value.leasedCount == 0' "$private_root/$name-summary.json" >/dev/null; then
            drained=true; break
        fi
        sleep "${DEPLOY_TEST_POLL_SECONDS:-2}"
    done
    [[ "$drained" == true ]] || { deploy_fail down "$name" drain-timeout; return 1; }
    deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" "$remote_root/pause.json" "$request_headers" || return 1
    deploy_transport_forget_private_path "$request_headers" || return 1
}

deploy_run_down() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6" policy="$7" confirm="$8"
    local state_dir evidence_dir prepare prepare_ledger prepare_evidence up now target name context env_file logic shared volume entry root marker
    local state_project render_root private_root remote_root result prior_status project network ssh
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    prepare="$state_dir/prepare-manifest.json"; prepare_ledger="$state_dir/prepare-ledger.json"; prepare_evidence="$evidence_dir/prepare.json"; up="$state_dir/up-manifest.json"
    deploy_require_passed_phase "$prepare" down "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_passed_phase "$up" down "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" "$worktree" || return 1
    for entry in "$prepare_ledger" "$prepare" "$prepare_evidence"; do
        [[ -f "$entry" && ! -L "$entry" && "$(stat -c '%u:%h:%a' -- "$entry" 2>/dev/null)" == "$(id -u):1:600" ]] ||
          { deploy_fail down prepare-evidence missing-or-unsafe; return 1; }
    done
    deploy_validate_prepare_ledger "$inventory" "$(jq -c . "$prepare_ledger")" "$run_id" "$mode" "$hash" "$revision" || return 1
    [[ "$(jq -S -c . "$prepare")" == "$(jq -S -c . "$prepare_ledger")" ]] || { deploy_fail down prepare-manifest mismatch; return 1; }
    [[ "$(jq -S -c . "$prepare_evidence")" == "$(deploy_prepare_evidence_json "$(jq -c . "$prepare")" | jq -S -c .)" ]] ||
      { deploy_fail down prepare-evidence mismatch; return 1; }
    [[ "$policy" == preserve || "$policy" == delete ]] || { deploy_fail down policy exactly-one-policy-required; return 1; }
    if [[ "$policy" == delete ]]; then
        [[ "$mode" == isolated && "$confirm" == "$run_id" ]] || { deploy_fail down deletion confirmation-mismatch; return 1; }
        [[ "$(jq -r '.deployment.services.mode' "$inventory")" == deploy ]] || { deploy_fail down deletion existing-services-not-owned; return 1; }
        jq -e --arg run "$run_id" '.targets | length > 0 and all(.[]; .creatingRunId == $run and .newlyCreated.root == true)' "$prepare_ledger" >/dev/null ||
          { deploy_fail down deletion runtime-root-not-run-owned; return 1; }
    fi

    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"; state_project="$(jq -r '.deployment.resources.project' "$inventory")"
    deploy_transport_reconcile_private_uploads strict || { deploy_fail down private-upload-registry cleanup-failed; return 1; }
    DEPLOY_DOWN_MANIFEST="$state_dir/down-manifest.json"; DEPLOY_DOWN_LEDGER="$state_dir/down-ledger.json"; DEPLOY_DOWN_EVIDENCE="$evidence_dir/down.json"
    DEPLOY_DOWN_COMMIT="$state_dir/down-commit.json"
    deploy_require_no_orphan_phase_files "$DEPLOY_DOWN_LEDGER" "$DEPLOY_DOWN_MANIFEST" "$DEPLOY_DOWN_EVIDENCE" "$DEPLOY_DOWN_COMMIT" || return 1
    render_root="$state_dir/down-rendered"; private_root="$state_dir/down-private"; install -d -m 700 "$render_root" "$private_root"
    deploy_transport_reconcile_private_uploads strict || { deploy_fail down private-upload-registry cleanup-failed; return 1; }
    jq -e 'length == 0' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" >/dev/null || { deploy_fail down private-upload-registry not-empty; return 1; }
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -e "$DEPLOY_DOWN_LEDGER" || -L "$DEPLOY_DOWN_LEDGER" ]]; then
        deploy_require_phase_files_match "$DEPLOY_DOWN_LEDGER" "$DEPLOY_DOWN_MANIFEST" "$DEPLOY_DOWN_EVIDENCE" "$DEPLOY_DOWN_COMMIT" \
          deploy_down_validate_candidate "$run_id" "$mode" "$hash" "$revision" "$policy" || return 1
        jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg policy "$policy" '
          .schemaVersion == 2 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and .policy == $policy and
          (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
          (.resources | type == "array" and length == ([.[] | [.resource,.action]] | unique | length) and
            all(.[]; (keys - ["completedAt"] | sort) == (["resource","action","status","intentAt","updatedAt"] | sort) and
              (.status == "intent" or .status == "completed")))' "$DEPLOY_DOWN_LEDGER" >/dev/null || { deploy_fail down ledger invalid; return 1; }
        DEPLOY_DOWN_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' "$DEPLOY_DOWN_LEDGER")"
    else
        DEPLOY_DOWN_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg policy "$policy" --arg now "$now" \
          '{schemaVersion:2,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,policy:$policy,phaseStatus:"running",startedAt:$now,updatedAt:$now,resources:[]}')"
    fi
    deploy_down_publish

    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; context="$(jq -r '.dockerContext' <<< "$target")"; env_file="$state_dir/up-rendered/$name.env"
        remote_root="$(jq -r '.runtimeRoot' <<< "$target")/.hvo-deploy/down-$run_id"
        if deploy_down_begin_action "cameraagent:$name" graceful-stop; then
            deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$remote_root" || return 1
            deploy_down_pause_and_drain "$inventory" "$target" "$render_root" "$private_root" "$remote_root" "$run_id" || return 1
            deploy_up_compose_mutation "$target" "$context" "$state_project-$name" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" stop -t 60 cameraagent || return 1
            deploy_down_require_service_stopped "$target" "$context" "$state_project-$name" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" cameraagent || return 1
            deploy_down_after_mutation "cameraagent:$name" graceful-stop || return 1
            deploy_down_complete_action "cameraagent:$name" graceful-stop || return 1
        else result=$?; [[ "$result" == 2 ]] || return 1
            deploy_down_require_service_stopped "$target" "$context" "$state_project-$name" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" cameraagent || return 1
        fi
    done < <(jq -c '.cameraAgents[]' "$inventory")

    logic="$(jq -c '.logicHost' "$inventory")"; name="$(jq -r '.name' <<< "$logic")"; context="$(jq -r '.dockerContext' <<< "$logic")"; env_file="$state_dir/up-rendered/$name.env"
    if deploy_down_begin_action logichost graceful-stop; then
        deploy_up_compose_mutation "$logic" "$context" "$state_project-logic" "$env_file" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" stop -t 60 logichost || return 1
        deploy_down_require_service_stopped "$logic" "$context" "$state_project-logic" "$env_file" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" logichost || return 1
        deploy_down_after_mutation logichost graceful-stop || return 1
        deploy_down_complete_action logichost graceful-stop || return 1
    else result=$?; [[ "$result" == 2 ]] || return 1
        deploy_down_require_service_stopped "$logic" "$context" "$state_project-logic" "$env_file" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" logichost || return 1
    fi

    if [[ "$(jq -r '.deployment.services.mode' "$inventory")" == deploy ]]; then
        shared="$(jq -c '.sharedServices' "$inventory")"; name="$(jq -r '.name' <<< "$shared")"; context="$(jq -r '.dockerContext' <<< "$shared")"; env_file="$state_dir/up-rendered/shared.env"
        if deploy_down_begin_action shared-services graceful-stop; then
            deploy_up_compose_mutation "$shared" "$context" "$state_project-services" "$env_file" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" \
              --profile test-smtp --profile provision stop -t 60 || return 1
            deploy_down_require_shared_services stopped "$shared" "$context" "$state_project-services" "$env_file" || return 1
            deploy_down_after_mutation shared-services graceful-stop || return 1
            deploy_down_complete_action shared-services graceful-stop || return 1
        else result=$?; [[ "$result" == 2 ]] || return 1
            deploy_down_require_shared_services stopped "$shared" "$context" "$state_project-services" "$env_file" || return 1
        fi
    else
        if deploy_down_begin_action existing-services preserve; then deploy_down_complete_action existing-services preserve || return 1
        else result=$?; [[ "$result" == 2 ]] || return 1; fi
    fi

    if [[ "$policy" == delete ]]; then
        while IFS= read -r target; do
            name="$(jq -r '.name' <<< "$target")"; context="$(jq -r '.dockerContext' <<< "$target")"; env_file="$state_dir/up-rendered/$name.env"
            if deploy_down_begin_action "cameraagent:$name" remove; then
                deploy_up_compose_mutation "$target" "$context" "$state_project-$name" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" rm -f cameraagent || return 1
                deploy_down_require_service_absent "$target" "$context" "$state_project-$name" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" cameraagent || return 1
                deploy_down_after_mutation "cameraagent:$name" remove || return 1
                deploy_down_complete_action "cameraagent:$name" remove || return 1
            else result=$?; [[ "$result" == 2 ]] || return 1
                deploy_down_require_service_absent "$target" "$context" "$state_project-$name" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" cameraagent || return 1
            fi
        done < <(jq -c '.cameraAgents[]' "$inventory")
        if deploy_down_begin_action logichost remove; then
            deploy_up_compose_mutation "$logic" "$(jq -r '.dockerContext' <<< "$logic")" "$state_project-logic" "$state_dir/up-rendered/$(jq -r '.name' <<< "$logic").env" \
              "$REPO_ROOT/deploy/split-host/compose.logichost.yml" rm -f logichost || return 1
            deploy_down_require_service_absent "$logic" "$(jq -r '.dockerContext' <<< "$logic")" "$state_project-logic" "$state_dir/up-rendered/$(jq -r '.name' <<< "$logic").env" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" logichost || return 1
            deploy_down_after_mutation logichost remove || return 1
            deploy_down_complete_action logichost remove || return 1
        else result=$?; [[ "$result" == 2 ]] || return 1
            deploy_down_require_service_absent "$logic" "$(jq -r '.dockerContext' <<< "$logic")" "$state_project-logic" "$state_dir/up-rendered/$(jq -r '.name' <<< "$logic").env" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" logichost || return 1
        fi
        shared="$(jq -c '.sharedServices' "$inventory")"; context="$(jq -r '.dockerContext' <<< "$shared")"
        if deploy_down_begin_action shared-services remove; then
            deploy_up_compose_mutation "$shared" "$context" "$state_project-services" "$state_dir/up-rendered/shared.env" \
              "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" --profile test-smtp --profile provision rm -f || return 1
            deploy_down_require_shared_services absent "$shared" "$context" "$state_project-services" "$state_dir/up-rendered/shared.env" || return 1
            deploy_down_after_mutation shared-services remove || return 1
            deploy_down_complete_action shared-services remove || return 1
        else result=$?; [[ "$result" == 2 ]] || return 1
            deploy_down_require_shared_services absent "$shared" "$context" "$state_project-services" "$state_dir/up-rendered/shared.env" || return 1
        fi
        while IFS=$'\t' read -r name context project target; do
            network="${project}_default"
            prior_status="$(jq -r --arg resource "network:$network" '.resources[]? | select(.resource == $resource and .action == "delete-network") | .status' <<< "$DEPLOY_DOWN_JSON")"
            if [[ "$prior_status" == completed ]]; then
                deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_transport_require_network_absent "$context" "$network" || { deploy_fail down "network:$network" completed-resource-recreated; return 1; }
                deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                continue
            fi
            [[ -n "$prior_status" ]] || deploy_transport_validate_network "$context" "$network" "$project" "$run_id" "$hash" || return 1
            if deploy_down_begin_action "network:$network" delete-network; then
                deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_transport_remove_network "$context" "$network" "$project" "$run_id" "$hash" true || return 1
                deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_transport_require_network_absent "$context" "$network" || return 1
                deploy_down_after_mutation "network:$network" delete-network || return 1
                deploy_down_complete_action "network:$network" delete-network || return 1
            else result=$?; [[ "$result" == 2 ]] || return 1; fi
        done < <(
          while IFS= read -r target; do name="$(jq -r '.name' <<< "$target")"; printf '%s\t%s\t%s\t%s\n' "$name" "$(jq -r '.dockerContext' <<< "$target")" "$state_project-$name" "$target"; done < <(jq -c '.cameraAgents[]' "$inventory")
          printf 'logic\t%s\t%s\t%s\n' "$(jq -r '.dockerContext' <<< "$logic")" "$state_project-logic" "$logic"
          printf 'services\t%s\t%s\t%s\n' "$context" "$state_project-services" "$shared"
        )
        context="$(jq -r '.dockerContext' <<< "$shared")"
        for entry in "sql:${state_project}-services_sql-data" "redis:${state_project}-services_redis-data" "minio:${state_project}-services_minio-data"; do
            name="${entry%%:*}"; volume="${entry#*:}"
            prior_status="$(jq -r --arg resource "$name:$volume" '.resources[]? | select(.resource == $resource and .action == "delete-volume") | .status' <<< "$DEPLOY_DOWN_JSON")"
            if [[ "$prior_status" == completed ]]; then
                deploy_phase_correlate_target "$shared" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_transport_require_volume_absent "$context" "$volume" || { deploy_fail down "$name:$volume" completed-resource-recreated; return 1; }
                deploy_phase_correlate_target "$shared" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                continue
            fi
            [[ -n "$prior_status" ]] || deploy_transport_validate_volume "$context" "$volume" "$run_id" "$hash" || return 1
            if deploy_down_begin_action "$name:$volume" delete-volume; then
                deploy_phase_correlate_target "$shared" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_transport_remove_volume "$context" "$volume" "$run_id" "$hash" true || return 1
                deploy_transport_require_volume_absent "$context" "$volume" || return 1
                deploy_phase_correlate_target "$shared" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_down_after_mutation "$name:$volume" delete-volume || return 1
                deploy_down_complete_action "$name:$volume" delete-volume || return 1
            else result=$?; [[ "$result" == 2 ]] || return 1; fi
        done
        while IFS= read -r entry; do
            name="$(jq -r '.target' <<< "$entry")"; root="$(jq -r '.runtimeRoot' <<< "$entry")"; marker="$(jq -r '.markerDigest' <<< "$entry")"
            target="$(jq -c --arg name "$name" '([.logicHost] + .cameraAgents + [.sharedServices]) | map(select(.name == $name))[0]' "$inventory")"
            prior_status="$(jq -r --arg resource "runtime-root:$name" '.resources[]? | select(.resource == $resource and .action == "delete") | .status' <<< "$DEPLOY_DOWN_JSON")"
            ssh="$(jq -r '.sshHost' <<< "$target")"
            if [[ "$prior_status" == completed ]]; then
                deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_transport_require_runtime_root_absent "$ssh" "$root" || { deploy_fail down "runtime-root:$name" completed-resource-recreated; return 1; }
                deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                continue
            fi
            [[ -n "$prior_status" ]] || deploy_transport_validate_runtime_root "$(jq -r '.sshHost' <<< "$target")" "$root" "$marker" || return 1
            if deploy_down_begin_action "runtime-root:$name" delete; then
                deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_transport_remove_runtime_root "$(jq -r '.sshHost' <<< "$target")" "$root" "$marker" true || return 1
                deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
                deploy_down_after_mutation "runtime-root:$name" delete || return 1
                deploy_down_complete_action "runtime-root:$name" delete || return 1
            else result=$?; [[ "$result" == 2 ]] || return 1; fi
        done < <(jq -c '.targets[]' "$prepare_ledger")
    fi
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    deploy_bootstrap_cleanup_private_remote
    deploy_transport_reconcile_private_uploads strict || return 1
    jq -e 'length == 0' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" >/dev/null
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-down-cleanup ]] || exit 75
    DEPLOY_DOWN_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_DOWN_JSON")"
    deploy_down_publish
}
