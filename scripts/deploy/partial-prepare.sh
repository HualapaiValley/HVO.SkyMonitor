#!/usr/bin/env bash

deploy_partial_prepare_validate_candidate() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" operation="$6"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg operation "$operation" '
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and
      .sourceRevision == $revision and .operation == $operation and
      (.publicationGeneration | numbers) >= 1 and (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      ((keys - ["completedAt"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","operation","phaseStatus","startedAt","updatedAt","targets","resources"] | sort)) and
      (.targets | type == "array" and ([.[].target] | length) == ([.[].target] | unique | length) and all(.[];
        (keys | sort) == (["target","disposition","newlyCreated","status"] | sort) and
        (.target | type == "string") and (.disposition == "cleanup-candidate" or .disposition == "preserved" or .disposition == "not-created") and
        (.newlyCreated | type == "object" and (keys | sort) == (["root","controlDirectory","marker"] | sort) and all(.[]; type == "boolean")) and
        .status == "verified")) and
      (.resources | type == "array" and ([.[] | [.resource,.action]] | length) == ([.[] | [.resource,.action]] | unique | length) and all(.[];
        ((keys - ["completedAt"] | sort) == (["resource","action","status","intentAt","updatedAt"] | sort)) and
        (.status == "intent" or .status == "completed")))' "$path" >/dev/null 2>&1 || {
        deploy_fail partial-prepare-cleanup journal invalid
        return 1
    }
}

deploy_partial_prepare_publish() {
    DEPLOY_PARTIAL_PREPARE_JSON="$(jq -c --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '.updatedAt=$now' <<< "$DEPLOY_PARTIAL_PREPARE_JSON")"
    deploy_phase_publish "$DEPLOY_PARTIAL_PREPARE_PHASE" DEPLOY_PARTIAL_PREPARE_JSON \
      "$DEPLOY_PARTIAL_PREPARE_LEDGER" "$DEPLOY_PARTIAL_PREPARE_MANIFEST" "$DEPLOY_PARTIAL_PREPARE_EVIDENCE" "$DEPLOY_PARTIAL_PREPARE_COMMIT"
}

deploy_partial_prepare_mark_failed() {
    local status="${1:-1}"
    trap - ERR
    if [[ -n "${DEPLOY_PARTIAL_PREPARE_JSON:-}" ]]; then
        DEPLOY_PARTIAL_PREPARE_JSON="$(jq -c --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
          '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_PARTIAL_PREPARE_JSON")"
        deploy_partial_prepare_publish >/dev/null 2>&1 || true
    fi
    return "$status"
}

deploy_partial_prepare_begin_action() {
    local resource="$1" action="$2" existing now
    existing="$(jq -r --arg resource "$resource" --arg action "$action" \
      '.resources[]? | select(.resource == $resource and .action == $action) | .status' <<< "$DEPLOY_PARTIAL_PREPARE_JSON")"
    [[ "$existing" != completed ]] || return 2
    [[ -z "$existing" || "$existing" == intent ]] || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_PARTIAL_PREPARE_JSON="$(jq -c --arg resource "$resource" --arg action "$action" --arg now "$now" '
      .resources = ([.resources[] | select(.resource != $resource or .action != $action)] +
        [{resource:$resource,action:$action,status:"intent",intentAt:((.resources[]? |
          select(.resource == $resource and .action == $action) | .intentAt) // $now),updatedAt:$now}])' <<< "$DEPLOY_PARTIAL_PREPARE_JSON")"
    deploy_partial_prepare_publish
}

deploy_partial_prepare_complete_action() {
    local resource="$1" action="$2" now
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_PARTIAL_PREPARE_JSON="$(jq -c --arg resource "$resource" --arg action "$action" --arg now "$now" '
      .resources = [.resources[] | if .resource == $resource and .action == $action then
        .status="completed" | .completedAt=$now | .updatedAt=$now else . end]' <<< "$DEPLOY_PARTIAL_PREPARE_JSON")"
    deploy_partial_prepare_publish
}

deploy_partial_prepare_require_local_state() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5"
    local state_dir evidence_dir preflight prepare_ledger prepare_manifest prepare_evidence path root
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    deploy_phase_file_safe "$DEPLOY_MANIFEST" && deploy_phase_file_safe "$DEPLOY_EVIDENCE" || {
      deploy_fail partial-prepare-cleanup preflight missing-or-unsafe; return 1;
    }
    preflight="$(jq -c . "$DEPLOY_MANIFEST" 2>/dev/null)" || { deploy_fail partial-prepare-cleanup preflight invalid; return 1; }
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" '
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and
      .source.revision == $revision and .phaseStatus == "passed"' <<< "$preflight" >/dev/null || {
      deploy_fail partial-prepare-cleanup preflight mismatch-or-not-passed; return 1;
    }
    deploy_require_completed_evidence_match "$preflight" "$DEPLOY_EVIDENCE" || return 1
    prepare_ledger="$state_dir/prepare-ledger.json"; prepare_manifest="$state_dir/prepare-manifest.json"; prepare_evidence="$evidence_dir/prepare.json"
    for path in "$prepare_ledger" "$prepare_manifest" "$prepare_evidence"; do
        deploy_phase_file_safe "$path" || { deploy_fail partial-prepare-cleanup prepare missing-or-unsafe; return 1; }
    done
    DEPLOY_PARTIAL_PREPARE_SOURCE="$(jq -c . "$prepare_ledger" 2>/dev/null)" || {
      deploy_fail partial-prepare-cleanup prepare invalid; return 1;
    }
    deploy_validate_prepare_ledger "$inventory" "$DEPLOY_PARTIAL_PREPARE_SOURCE" "$run_id" "$mode" "$hash" "$revision" || return 1
    [[ "$(jq -r '.phaseStatus' <<< "$DEPLOY_PARTIAL_PREPARE_SOURCE")" == failed ]] || {
      deploy_fail partial-prepare-cleanup prepare failed-state-required; return 1;
    }
    [[ "$(jq -S -c . "$prepare_manifest")" == "$(jq -S -c . <<< "$DEPLOY_PARTIAL_PREPARE_SOURCE")" ]] || {
      deploy_fail partial-prepare-cleanup prepare manifest-mismatch; return 1;
    }
    [[ "$(jq -S -c . "$prepare_evidence")" == "$(deploy_prepare_evidence_json "$DEPLOY_PARTIAL_PREPARE_SOURCE" | jq -S -c .)" ]] || {
      deploy_fail partial-prepare-cleanup prepare evidence-mismatch; return 1;
    }
    jq -e '(.targets | length) > 0' <<< "$DEPLOY_PARTIAL_PREPARE_SOURCE" >/dev/null || {
      deploy_fail partial-prepare-cleanup prepare no-created-targets; return 1;
    }
    for path in "$state_dir/up-ledger.json" "$state_dir/up-manifest.json" "$state_dir/up-commit.json" "$evidence_dir/up.json" "$state_dir/up-rendered" \
      "$state_dir/down-ledger.json" "$state_dir/down-manifest.json" "$state_dir/down-commit.json" "$evidence_dir/down.json"; do
        [[ ! -e "$path" && ! -L "$path" ]] || { deploy_fail partial-prepare-cleanup lifecycle later-phase-present; return 1; }
    done
    jq -e 'length == 0' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" >/dev/null || {
      deploy_fail partial-prepare-cleanup lifecycle private-artifacts-present; return 1;
    }
    while IFS= read -r root; do
        [[ "$state_dir" != "$root" && "$state_dir" != "$root/"* && "$evidence_dir" != "$root" && "$evidence_dir" != "$root/"* ]] || {
          deploy_fail partial-prepare-cleanup journal inside-runtime-root; return 1;
        }
    done < <(deploy_inventory_targets "$inventory" | jq -r '.runtimeRoot')
}

deploy_partial_prepare_target_projection() {
    local inventory="$1" run_id="$2" target name entry creating_run flags disposition
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"
        entry="$(jq -c --arg target "$name" '.targets[]? | select(.target == $target)' <<< "$DEPLOY_PARTIAL_PREPARE_SOURCE")"
        if [[ -z "$entry" ]]; then
            jq -cn --arg target "$name" '{target:$target,disposition:"not-created",newlyCreated:{root:false,controlDirectory:false,marker:false},status:"verified"}'
            continue
        fi
        creating_run="$(jq -r '.creatingRunId' <<< "$entry")"; flags="$(jq -c '.newlyCreated' <<< "$entry")"
        if [[ "$creating_run" == "$run_id" ]]; then
            jq -e 'any(.[]; . == true)' <<< "$flags" >/dev/null || { deploy_fail partial-prepare-cleanup "$name" invalid-creation-flags; return 1; }
            disposition=cleanup-candidate
        else
            jq -e 'all(.[]; . == false)' <<< "$flags" >/dev/null || { deploy_fail partial-prepare-cleanup "$name" foreign-creation-flags; return 1; }
            disposition=preserved
        fi
        jq -cn --arg target "$name" --arg disposition "$disposition" --argjson flags "$flags" \
          '{target:$target,disposition:$disposition,newlyCreated:$flags,status:"verified"}'
    done < <(deploy_inventory_targets "$inventory")
}

deploy_partial_prepare_resource_status() {
    local target="$1" action="$2"
    jq -r --arg resource "target:$target" --arg action "$action" \
      '.resources[]? | select(.resource == $resource and .action == $action) | .status // empty' <<< "$DEPLOY_PARTIAL_PREPARE_JSON"
}

deploy_partial_prepare_project() {
    local inventory="$1" target="$2" component="$3" base name
    if [[ "$(jq -r '.schemaVersion' "$inventory")" == 7 ]]; then
        base="$(jq -r '.deployment.resources.project' "$inventory")"
        if [[ "$component" == logicHost ]]; then name=logic; else name="$(jq -r '.name' <<< "$target")"; fi
        printf '%s-%s\n' "$base" "$name"
    else
        deploy_compose_project "$inventory" "$target"
    fi
}

deploy_partial_prepare_verify_docker() {
    local inventory="$1" run_id="$2" preflight="$3"
    local target name context binding endpoint project container component
    local -A endpoints=() context_endpoints=()
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; context="$(jq -r '.dockerContext' <<< "$target")"
        binding="$(deploy_transport_docker_context_binding "$context")" || { deploy_fail images "$name" docker-context-unavailable; return 1; }
        jq -e --arg context "$context" '.name == $context and (.host | type == "string" and length > 0)' <<< "$binding" >/dev/null 2>&1 || {
          deploy_fail images "$name" docker-context-mismatch; return 1;
        }
        endpoint="$(jq -r '.host' <<< "$binding")"
        deploy_images_correlate_target_endpoint "$target" "$preflight" "$endpoint" || return 1
        context_endpoints["$context"]="$endpoint"
        endpoints["$endpoint"]="${endpoints[$endpoint]:-}"
        if component="$(deploy_target_component "$inventory" "$name" 2>/dev/null)"; then
            project="$(deploy_partial_prepare_project "$inventory" "$target" "$component")"
            endpoints["$endpoint"]+="${endpoints[$endpoint]:+ }$project"
            if [[ "$(jq -r '.schemaVersion' "$inventory")" == 8 ]]; then
                container="hvo-skymonitor-$(jq -r '.instanceId | gsub("-"; "")' <<< "$target")"
                deploy_transport_require_container_absent_host "$endpoint" "$container" || {
                  deploy_fail partial-prepare-cleanup docker unexpected-deployment-resource; return 1;
                }
                if [[ "$component" == logicHost ]]; then
                    deploy_transport_require_container_absent_host "$endpoint" "$container-init" || {
                      deploy_fail partial-prepare-cleanup docker unexpected-deployment-resource; return 1;
                    }
                fi
            fi
            deploy_transport_require_network_absent_host "$endpoint" "${project}_default" || {
              deploy_fail partial-prepare-cleanup docker unexpected-deployment-resource; return 1;
            }
        fi
    done < <(deploy_inventory_targets "$inventory")
    if [[ "$(jq -r '.deployment.services.mode' "$inventory")" == deploy ]]; then
        context="$(jq -r '.sharedServices.dockerContext' "$inventory")"
        endpoint="${context_endpoints[$context]:-}"
        [[ -n "$endpoint" ]] || { deploy_fail images shared docker-context-mismatch; return 1; }
        endpoints["$endpoint"]+="${endpoints[$endpoint]:+ }$(jq -r '.deployment.resources.project' "$inventory")-services"
    fi
    for endpoint in "${!endpoints[@]}"; do
        read -r -a projects <<< "${endpoints[$endpoint]}"
        deploy_transport_require_partial_prepare_resources_absent_host "$endpoint" "$run_id" "${projects[@]}" || {
            deploy_fail partial-prepare-cleanup docker unexpected-deployment-resource; return 1;
        }
    done
}

deploy_partial_prepare_verify_all() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" preflight="$5"
    local target name ssh root owner expected_machine expected_host deployment_state entry marker lock_name creating_run root_status lock_status result expected_result
    deploy_partial_prepare_verify_docker "$inventory" "$run_id" "$preflight" || return 1
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; ssh="$(jq -r '.sshHost' <<< "$target")"; root="$(jq -r '.runtimeRoot' <<< "$target")"
        owner="$(jq -r '.runtimeOwner' <<< "$target")"; expected_machine="$(jq -r '.expectedHostIdentity' <<< "$target")"; expected_host="$(jq -r '.expectedHostName' <<< "$target")"
        marker="$(deploy_prepare_marker_digest "$mode" "$run_id" "$hash" "$(jq -r '.installationId' "$inventory")" "$name" "$root" "$owner")"
        lock_name=".hvo-deploy-prepare-$(printf 'v1\ntarget=%s\nroot=%s\n' "$name" "$root" | sha256sum | cut -c1-32).lock"
        entry="$(jq -c --arg target "$name" '.targets[]? | select(.target == $target)' <<< "$DEPLOY_PARTIAL_PREPARE_SOURCE")"
        if [[ -z "$entry" ]]; then
            deployment_state="$(jq -r --arg target "$name" '.targets[] | select(.name == $target) | .deploymentState' <<< "$preflight")"
            deploy_transport_require_unprepared_target_absent "$ssh" "$root" "$lock_name" "$expected_machine" "$expected_host" "$owner" "$deployment_state" || {
              deploy_fail partial-prepare-cleanup "$name" unjournaled-prepare-artifacts; return 1;
            }
            continue
        fi
        creating_run="$(jq -r '.creatingRunId' <<< "$entry")"
        if [[ "$creating_run" != "$run_id" ]]; then
            result="$(deploy_transport_prepare_target "$ssh" "$root" "$mode" "$owner" "$run_id" "$hash" "$name" \
              "$(jq -r '.installationId' "$inventory")" "$marker" "$lock_name" "$expected_machine" "$expected_host" "" validate)"
            expected_result="$(printf 'validated\t%s\t%s\t%s\t%s\t%s' "$marker" "$creating_run" \
              "$(jq -r '.newlyCreated.root' <<< "$entry")" "$(jq -r '.newlyCreated.controlDirectory' <<< "$entry")" \
              "$(jq -r '.newlyCreated.marker' <<< "$entry")")"
            [[ "$result" == "$expected_result" ]] || {
              deploy_fail partial-prepare-cleanup "$name" preserved-provenance-invalid; return 1;
            }
            continue
        fi
        root_status="$(deploy_partial_prepare_resource_status "$name" cleanup-runtime-artifacts)"; root_status="${root_status:-none}"
        lock_status="$(deploy_partial_prepare_resource_status "$name" cleanup-prepare-lock)"; lock_status="${lock_status:-none}"
        deploy_transport_partial_prepare_target "$ssh" "$root" "$marker" "$run_id" "$lock_name" \
          "$(jq -r '.newlyCreated.root' <<< "$entry")" "$(jq -r '.newlyCreated.controlDirectory' <<< "$entry")" \
          "$(jq -r '.newlyCreated.marker' <<< "$entry")" "$root_status" "$lock_status" verify none "$name" \
          "$expected_machine" "$expected_host" "$owner" || { deploy_fail partial-prepare-cleanup "$name" provenance-or-content-invalid; return 1; }
    done < <(deploy_inventory_targets "$inventory")
}

deploy_run_partial_prepare_cleanup() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" dry_run="$6" confirm="$7"
    local state_dir evidence_dir operation prefix now projection preflight target name entry ssh root owner marker lock_name expected_machine expected_host root_status lock_status result
    [[ "$dry_run" == true || "$confirm" == "$run_id" ]] || { deploy_fail partial-prepare-cleanup confirmation mismatch; return 1; }
    [[ "$dry_run" != true || -z "$confirm" ]] || { deploy_fail partial-prepare-cleanup confirmation dry-run-with-confirmation; return 1; }
    deploy_partial_prepare_require_local_state "$inventory" "$run_id" "$mode" "$hash" "$revision" || return 1
    projection="$(deploy_partial_prepare_target_projection "$inventory" "$run_id")" || return 1
    projection="$(jq -sc '.' <<< "$projection")"
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    if [[ "$dry_run" == true ]]; then operation=dry-run; prefix=partial-prepare-inspect; else operation=cleanup; prefix=partial-prepare-cleanup; fi
    DEPLOY_PARTIAL_PREPARE_PHASE="$prefix"; DEPLOY_PARTIAL_PREPARE_LEDGER="$state_dir/$prefix-ledger.json"
    DEPLOY_PARTIAL_PREPARE_MANIFEST="$state_dir/$prefix-manifest.json"; DEPLOY_PARTIAL_PREPARE_COMMIT="$state_dir/$prefix-commit.json"
    DEPLOY_PARTIAL_PREPARE_EVIDENCE="$evidence_dir/$prefix.json"
    deploy_require_no_orphan_phase_files "$DEPLOY_PARTIAL_PREPARE_LEDGER" "$DEPLOY_PARTIAL_PREPARE_MANIFEST" \
      "$DEPLOY_PARTIAL_PREPARE_EVIDENCE" "$DEPLOY_PARTIAL_PREPARE_COMMIT" || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -e "$DEPLOY_PARTIAL_PREPARE_LEDGER" || -L "$DEPLOY_PARTIAL_PREPARE_LEDGER" ]]; then
        deploy_require_phase_files_match "$DEPLOY_PARTIAL_PREPARE_LEDGER" "$DEPLOY_PARTIAL_PREPARE_MANIFEST" \
          "$DEPLOY_PARTIAL_PREPARE_EVIDENCE" "$DEPLOY_PARTIAL_PREPARE_COMMIT" deploy_partial_prepare_validate_candidate \
          "$run_id" "$mode" "$hash" "$revision" "$operation" || return 1
        DEPLOY_PARTIAL_PREPARE_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' "$DEPLOY_PARTIAL_PREPARE_LEDGER")"
        [[ "$(jq -S -c '.targets' <<< "$DEPLOY_PARTIAL_PREPARE_JSON")" == "$(jq -S -c . <<< "$projection")" ]] || {
          deploy_fail partial-prepare-cleanup journal target-mismatch; return 1;
        }
    else
        DEPLOY_PARTIAL_PREPARE_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" \
          --arg operation "$operation" --arg now "$now" --argjson targets "$projection" \
          '{schemaVersion:1,publicationGeneration:0,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,
            operation:$operation,phaseStatus:"running",startedAt:$now,updatedAt:$now,targets:$targets,resources:[]}')"
    fi
    preflight="$(jq -c . "$DEPLOY_MANIFEST")"
    deploy_partial_prepare_verify_all "$inventory" "$run_id" "$mode" "$hash" "$preflight" || return 1
    deploy_partial_prepare_publish || return 1
    if [[ "$dry_run" == true ]]; then
        now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        DEPLOY_PARTIAL_PREPARE_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_PARTIAL_PREPARE_JSON")"
        deploy_partial_prepare_publish
        return
    fi
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"
        entry="$(jq -c --arg target "$name" --arg run "$run_id" '.targets[]? | select(.target == $target and .creatingRunId == $run)' <<< "$DEPLOY_PARTIAL_PREPARE_SOURCE")"
        [[ -n "$entry" ]] || continue
        ssh="$(jq -r '.sshHost' <<< "$target")"; root="$(jq -r '.runtimeRoot' <<< "$target")"; owner="$(jq -r '.runtimeOwner' <<< "$target")"
        expected_machine="$(jq -r '.expectedHostIdentity' <<< "$target")"; expected_host="$(jq -r '.expectedHostName' <<< "$target")"
        marker="$(jq -r '.markerDigest' <<< "$entry")"; lock_name=".hvo-deploy-prepare-$(printf 'v1\ntarget=%s\nroot=%s\n' "$name" "$root" | sha256sum | cut -c1-32).lock"
        if deploy_partial_prepare_begin_action "target:$name" cleanup-runtime-artifacts; then
            root_status=intent; lock_status="$(deploy_partial_prepare_resource_status "$name" cleanup-prepare-lock)"; lock_status="${lock_status:-none}"
            deploy_phase_correlate_target "$target" "$preflight" || return 1
            deploy_partial_prepare_verify_docker "$inventory" "$run_id" "$preflight" || return 1
            deploy_transport_partial_prepare_target "$ssh" "$root" "$marker" "$run_id" "$lock_name" \
              "$(jq -r '.newlyCreated.root' <<< "$entry")" "$(jq -r '.newlyCreated.controlDirectory' <<< "$entry")" \
              "$(jq -r '.newlyCreated.marker' <<< "$entry")" "$root_status" "$lock_status" cleanup-root "${DEPLOY_TEST_FAILPOINT:-none}" "$name" \
              "$expected_machine" "$expected_host" "$owner" || return 1
            deploy_phase_correlate_target "$target" "$preflight" || return 1
            deploy_partial_prepare_complete_action "target:$name" cleanup-runtime-artifacts || return 1
        else result=$?; [[ "$result" == 2 ]] || return 1; fi
        if deploy_partial_prepare_begin_action "target:$name" cleanup-prepare-lock; then
            root_status=completed; lock_status=intent
            deploy_phase_correlate_target "$target" "$preflight" || return 1
            deploy_partial_prepare_verify_docker "$inventory" "$run_id" "$preflight" || return 1
            deploy_transport_partial_prepare_target "$ssh" "$root" "$marker" "$run_id" "$lock_name" \
              "$(jq -r '.newlyCreated.root' <<< "$entry")" "$(jq -r '.newlyCreated.controlDirectory' <<< "$entry")" \
              "$(jq -r '.newlyCreated.marker' <<< "$entry")" "$root_status" "$lock_status" cleanup-lock "${DEPLOY_TEST_FAILPOINT:-none}" "$name" \
              "$expected_machine" "$expected_host" "$owner" || return 1
            deploy_phase_correlate_target "$target" "$preflight" || return 1
            deploy_partial_prepare_complete_action "target:$name" cleanup-prepare-lock || return 1
        else result=$?; [[ "$result" == 2 ]] || return 1; fi
    done < <(deploy_inventory_targets "$inventory")
    deploy_partial_prepare_verify_all "$inventory" "$run_id" "$mode" "$hash" "$preflight" || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_PARTIAL_PREPARE_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_PARTIAL_PREPARE_JSON")"
    deploy_partial_prepare_publish
}
