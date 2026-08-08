#!/usr/bin/env bash

deploy_append_check() {
    local manifest_json="$1" stage="$2" target="$3" check="$4" status="$5" reason="$6" now
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    jq -c --arg stage "$stage" --arg target "$target" --arg check "$check" --arg status "$status" --arg reason "$reason" --arg now "$now" \
      '.checks += [{stage:$stage,target:$target,check:$check,status:$status,reason:$reason,timestamp:$now}] | .updatedAt=$now' <<< "$manifest_json"
}

deploy_record() {
    local stage="$1" target="$2" check="$3" status="$4" reason="$5"
    DEPLOY_MANIFEST_JSON="$(deploy_append_check "$DEPLOY_MANIFEST_JSON" "$stage" "$target" "$check" "$status" "$reason")"
    deploy_publish_json "$DEPLOY_MANIFEST" "$DEPLOY_MANIFEST_JSON" || return 1
    deploy_status "$stage" "$target/$check" "$status" "$reason"
    [[ "$status" != failed ]]
}

deploy_preflight_target() {
    local target="$1" name ssh_host context expected_arch expected_name expected_host expected_daemon root ports probe
    name="$(jq -r '.name' <<< "$target")"
    ssh_host="$(jq -r '.sshHost' <<< "$target")"
    context="$(jq -r '.dockerContext' <<< "$target")"
    expected_arch="$(jq -r '.expectedArchitecture' <<< "$target")"
    expected_name="$(jq -r '.expectedHostName' <<< "$target")"
    expected_host="$(jq -r '.expectedHostIdentity' <<< "$target")"
    expected_daemon="$(jq -r '.expectedDockerDaemonIdentity' <<< "$target")"
    root="$(jq -r '.runtimeRoot' <<< "$target")"
    ports="$(jq -r '(.ports // []) | join(",")' <<< "$target")"

    probe="$(deploy_transport_ssh_probe "$ssh_host" "$root" "$ports")" || {
        if [[ "$probe" == tool-unavailable ]]; then deploy_record preflight "$name" ssh failed tool-unavailable
        else deploy_record preflight "$name" ssh failed unreachable-or-invalid-response; fi
        return 1
    }
    local host_id host_name arch cpu memory free_space host_clock thermal throttle safety path_status socket_status control_clock clock_offset
    IFS=$'\t' read -r host_id host_name arch cpu memory free_space host_clock thermal throttle safety <<< "$probe"
    [[ "$host_id" == "$expected_host" ]] || { deploy_record preflight "$name" host-identity failed mismatch; return 1; }
    [[ "$host_name" == "$expected_name" ]] || { deploy_record preflight "$name" host-name failed mismatch; return 1; }
    [[ "$cpu" =~ ^[0-9]+$ && "$memory" =~ ^[0-9]+$ && "$free_space" =~ ^[0-9]+$ && "$host_clock" =~ ^[0-9]+$ &&
       ( "$thermal" == unsupported || "$thermal" =~ ^reported:-?[0-9]+$ ) &&
       ( "$throttle" == unsupported || "$throttle" =~ ^reported:0x[0-9a-fA-F]+$ ) &&
       "$safety" =~ ^(ready|needs_prepare|unsafe):(none|present|unsupported)$ ]] ||
        { deploy_record preflight "$name" ssh failed invalid-response; return 1; }
    deploy_record preflight "$name" ssh passed reachable || return 1
    [[ "$arch" == "$expected_arch" ]] || { deploy_record preflight "$name" host-architecture failed mismatch; return 1; }
    deploy_record preflight "$name" host-capacity passed observed || return 1
    control_clock="$(date +%s)"
    clock_offset=$((host_clock - control_clock))
    deploy_record preflight "$name" clock passed observed || return 1
    deploy_record preflight "$name" thermal "$([[ "$thermal" == reported:* ]] && printf passed || printf unsupported)" "$([[ "$thermal" == reported:* ]] && printf reported || printf unsupported)" || return 1
    deploy_record preflight "$name" throttle "$([[ "$throttle" == reported:* ]] && printf passed || printf unsupported)" "$([[ "$throttle" == reported:* ]] && printf reported || printf unsupported)" || return 1
    path_status="${safety%%:*}"
    socket_status="${safety##*:}"
    [[ "$path_status" != unsafe ]] || { deploy_record preflight "$name" runtime-root failed unsafe; return 1; }
    deploy_record preflight "$name" runtime-root passed "$([[ "$path_status" == ready ]] && printf ready || printf needs-prepare)" || return 1
    [[ "$socket_status" != unsupported ]] || { deploy_record preflight "$name" ports failed inspector-unavailable; return 1; }
    [[ "$socket_status" != present ]] || { deploy_record preflight "$name" ports failed conflict; return 1; }
    deploy_record preflight "$name" ports passed no-conflict || return 1

    local inspected docker_info daemon_id daemon_name daemon_arch daemon_arch_raw daemon_os daemon_version compose_version conflicts port
    inspected="$(deploy_transport_docker_context "$context")" || { deploy_record preflight "$name" docker-context failed unavailable; return 1; }
    [[ "$inspected" == "$context" ]] || { deploy_record preflight "$name" docker-context failed mismatch; return 1; }
    docker_info="$(deploy_transport_docker_info "$context")" || { deploy_record preflight "$name" docker-daemon failed unreachable; return 1; }
    daemon_id="$(jq -er '.ID' <<< "$docker_info" 2>/dev/null)" || { deploy_record preflight "$name" docker-daemon failed invalid-response; return 1; }
    daemon_name="$(jq -er '.Name' <<< "$docker_info" 2>/dev/null)" || { deploy_record preflight "$name" docker-daemon failed invalid-response; return 1; }
    daemon_arch_raw="$(jq -er '.Architecture | strings | select(length > 0)' <<< "$docker_info" 2>/dev/null)" || { deploy_record preflight "$name" docker-daemon failed invalid-response; return 1; }
    daemon_arch="$(deploy_normalize_docker_architecture "$daemon_arch_raw")" || { deploy_record preflight "$name" docker-daemon failed unsupported-architecture; return 1; }
    daemon_os="$(jq -er '.OSType | strings | select(length > 0)' <<< "$docker_info" 2>/dev/null)" || { deploy_record preflight "$name" docker-daemon failed invalid-response; return 1; }
    daemon_version="$(jq -er '.ServerVersion | strings | select(length > 0)' <<< "$docker_info" 2>/dev/null)" || { deploy_record preflight "$name" docker-daemon failed invalid-response; return 1; }
    [[ "$daemon_version" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$ ]] || { deploy_record preflight "$name" docker-daemon failed invalid-response; return 1; }
    [[ "$daemon_id" == "$expected_daemon" && "$daemon_name" == "$host_name" && "$daemon_name" == "$expected_name" &&
       "$daemon_arch" == "$expected_arch" && "$daemon_os" == linux ]] || {
        deploy_record preflight "$name" ssh-docker-correlation failed mismatch
        return 1
    }
    deploy_record preflight "$name" docker-daemon passed reachable || return 1
    compose_version="$(deploy_transport_docker_compose_version "$context")" || { deploy_record preflight "$name" docker-compose failed unavailable; return 1; }
    [[ "$compose_version" =~ ^v?[0-9]+\.[0-9]+\.[0-9]+([._+-][A-Za-z0-9.-]+)?$ ]] || { deploy_record preflight "$name" docker-compose failed invalid-version; return 1; }
    deploy_record preflight "$name" docker-compose passed available || return 1
    conflicts="$(deploy_transport_docker_conflicts "$context" "$ports")" || { deploy_record preflight "$name" containers failed query-failed; return 1; }
    [[ "$conflicts" == none ]] || { deploy_record preflight "$name" containers failed port-conflict; return 1; }
    deploy_record preflight "$name" containers passed no-conflict || return 1
    DEPLOY_TARGETS_JSON="$(jq -c --arg name "$name" --arg host "$host_id" --arg daemon "$daemon_id" --arg arch "$arch" \
      --arg hostName "$host_name" --arg root "$root" --arg version "$daemon_version" --arg compose "$compose_version" --arg cpu "$cpu" --arg memory "$memory" --arg free "$free_space" \
      --arg offset "$clock_offset" --arg thermal "$thermal" --arg throttle "$throttle" --arg deployment "$path_status" '. + [{name:$name,hostIdentity:$host,hostName:$hostName,dockerDaemonIdentity:$daemon,architecture:$arch,
        runtimeRoot:$root,deploymentState:$deployment,dockerVersion:$version,dockerComposeVersion:$compose,cpuCount:$cpu,memoryKiB:$memory,freeSpaceKiB:$free,clockOffsetSeconds:$offset,
        thermalReport:$thermal,throttleReport:$throttle}]' <<< "$DEPLOY_TARGETS_JSON")"
}

deploy_run_preflight() {
    local inventory="$1" old_manifest="${2:-}" target endpoint name host port route agent logic_url connectivity_status
    DEPLOY_TARGETS_JSON='[]'
    while IFS= read -r target; do deploy_preflight_target "$target" || return 1; done < <(deploy_inventory_targets "$inventory")

    while IFS= read -r endpoint; do
        name="$(jq -r '.name' <<< "$endpoint")"
        host="$(jq -r '.host' <<< "$endpoint")"
        port="$(jq -r '.port' <<< "$endpoint")"
        while IFS= read -r route; do
            target="$(jq -c --arg name "$route" '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) | map(select(.name == $name))[0]' "$inventory")"
            connectivity_status="$(deploy_transport_ssh_tcp "$(jq -r '.sshHost' <<< "$target")" "$host" "$port")"
            [[ "$connectivity_status" == reachable ]] || {
                deploy_record connectivity "$route" "$name" failed "$connectivity_status"
                return 1
            }
            deploy_record connectivity "$route" "$name" passed reachable || return 1
        done < <(jq -r '.fromTargets[]' <<< "$endpoint")
    done < <(jq -c '.serviceEndpoints[]' "$inventory")

    logic_url="$(jq -r '.logicHost.publicEndpoint' "$inventory")"
    while IFS= read -r agent; do
        name="$(jq -r '.name' <<< "$agent")"
        connectivity_status="$(deploy_transport_ssh_http "$(jq -r '.sshHost' <<< "$agent")" "$logic_url")"
        [[ "$connectivity_status" == reachable ]] || {
            deploy_record connectivity "$name" logic-public-authority failed "$connectivity_status"
            return 1
        }
        deploy_record connectivity "$name" logic-public-authority passed reachable || return 1
    done < <(jq -c '.cameraAgents[]' "$inventory")

    DEPLOY_MANIFEST_JSON="$(jq -c --argjson targets "$DEPLOY_TARGETS_JSON" --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
      '.targets=$targets | .phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_MANIFEST_JSON")"
    if [[ -n "$old_manifest" ]]; then
        jq -e --argjson current "$DEPLOY_MANIFEST_JSON" '
          ([.targets[] | {name,hostIdentity,hostName,dockerDaemonIdentity,architecture,runtimeRoot}] ==
           [$current.targets[] | {name,hostIdentity,hostName,dockerDaemonIdentity,architecture,runtimeRoot}]) and
          ([.checks[] | del(.timestamp)] == [$current.checks[] | del(.timestamp)]) and .phaseStatus == "passed"' <<< "$old_manifest" >/dev/null ||
            { deploy_fail state resume "completed-check-or-target-mismatch"; return 1; }
    fi
    deploy_publish_json "$DEPLOY_EVIDENCE" "$(deploy_evidence_json "$DEPLOY_MANIFEST_JSON")" || return 1
    if [[ "${DEPLOY_TEST_FAILPOINT:-}" == after-evidence-before-passed-manifest ]]; then
        exit 75
    fi
    deploy_publish_json "$DEPLOY_MANIFEST" "$DEPLOY_MANIFEST_JSON" || return 1
}
