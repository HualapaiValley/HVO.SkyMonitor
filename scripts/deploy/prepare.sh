#!/usr/bin/env bash

deploy_prepare_marker_digest() {
    local mode="$1" run_id="$2" inventory_hash="$3" installation="$4" target="$5" root="$6" owner="$7"
    if [[ "$mode" == isolated ]]; then
        printf 'v1\nmode=%s\nrun=%s\ninventory=%s\ntarget=%s\nroot=%s\nowner=%s\n' \
          "$mode" "$run_id" "$inventory_hash" "$target" "$root" "$owner" | sha256sum | cut -d' ' -f1
    else
        printf 'v1\nmode=%s\ninstallation=%s\ntarget=%s\nroot=%s\nowner=%s\n' \
          "$mode" "$installation" "$target" "$root" "$owner" | sha256sum | cut -d' ' -f1
    fi
}

deploy_prepare_publish_ledger() {
    deploy_publish_json "$DEPLOY_PREPARE_LEDGER" "$DEPLOY_PREPARE_LEDGER_JSON"
}

deploy_validate_prepare_ledger() {
    local inventory="$1" ledger_json="$2" run_id="$3" mode="$4" inventory_hash="$5" revision="$6"
    local target_count entry target_name expected_root expected_marker actual_names unique_names installation owner
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$inventory_hash" --arg revision "$revision" '
      type == "object" and
      (((keys - ["completedAt"]) | sort) == (["schemaVersion","runId","mode","inventorySha256","sourceRevision","phaseStatus","startedAt","updatedAt","targets"] | sort)) and
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
      (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      (.startedAt | type == "string" and length > 0) and (.updatedAt | type == "string" and length > 0) and
      ((.phaseStatus == "passed" and (.completedAt | type == "string" and length > 0)) or (.phaseStatus != "passed" and (has("completedAt") | not))) and
      (.targets | type == "array" and all(.[];
        type == "object" and (keys | sort) == (["target","runtimeRoot","disposition","newlyCreated","controlDirectory","markerDigest","creatingRunId","status"] | sort) and
        (.target | type == "string") and (.runtimeRoot | type == "string") and
        (.disposition == "newly-created" or .disposition == "preexisting-or-resumed") and
        (.newlyCreated | type == "object" and (keys | sort) == (["root","controlDirectory","marker"] | sort) and all(.[]; type == "boolean")) and
        .controlDirectory == ".hvo-deploy" and (.markerDigest | type == "string" and test("^[0-9a-f]{64}$")) and
        (.creatingRunId | type == "string" and test("^[a-z0-9][a-z0-9-]{0,31}$")) and .status == "prepared" and
        (if .creatingRunId == $run then
           ((.newlyCreated.root == true and .disposition == "newly-created") or (.newlyCreated.root == false and .disposition == "preexisting-or-resumed"))
         else $mode == "persistent" and .disposition == "preexisting-or-resumed" and all(.newlyCreated[]; . == false) end)))
    ' <<< "$ledger_json" >/dev/null 2>&1 || deploy_fail prepare ledger invalid || return 1
    target_count="$(jq '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) | length' "$inventory")"
    actual_names="$(jq -c '[.targets[].target]' <<< "$ledger_json")"
    unique_names="$(jq -c '[.targets[].target] | unique' <<< "$ledger_json")"
    [[ "$(jq 'length' <<< "$actual_names")" == "$(jq 'length' <<< "$unique_names")" ]] || deploy_fail prepare ledger duplicate-target || return 1
    if [[ "$(jq -r '.phaseStatus' <<< "$ledger_json")" == passed ]]; then
        [[ "$(jq '.targets | length' <<< "$ledger_json")" == "$target_count" ]] || deploy_fail prepare ledger incomplete-passed || return 1
    fi
    installation="$(jq -r '.installationId' "$inventory")"
    while IFS= read -r entry; do
        target_name="$(jq -r '.target' <<< "$entry")"
        expected_root="$(jq -r --arg name "$target_name" '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) |
          map(select(.name == $name))[0].runtimeRoot // empty' "$inventory")"
        owner="$(jq -r --arg name "$target_name" '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) |
          map(select(.name == $name))[0].runtimeOwner // empty' "$inventory")"
        [[ -n "$expected_root" && "$(jq -r '.runtimeRoot' <<< "$entry")" == "$expected_root" ]] || deploy_fail prepare ledger target-mismatch || return 1
        expected_marker="$(deploy_prepare_marker_digest "$mode" "$run_id" "$inventory_hash" "$installation" "$target_name" "$expected_root" "$owner")"
        [[ "$(jq -r '.markerDigest' <<< "$entry")" == "$expected_marker" ]] || deploy_fail prepare ledger marker-mismatch || return 1
    done < <(jq -c '.targets[]' <<< "$ledger_json")
}

deploy_prepare_evidence_json() {
    jq -c '{schemaVersion,runId,mode,inventorySha256,phaseStatus,startedAt,updatedAt,completedAt,
      targets:[.targets[] | {target,status,disposition,controlDirectory,markerDigest,creatingRunId}]}' <<< "$1"
}

deploy_run_prepare() {
    local inventory="$1" run_id="$2" mode="$3" inventory_hash="$4" revision="$5" worktree_state="$6"
    local preflight_manifest target name ssh_host owner root expected_machine expected_host installation
    local marker_digest lock_name remote_result remote_status root_new control_new marker_new creating_run disposition now existing_entry
    local validated_digest validated_run validated_root validated_control validated_marker ledger_run ledger_root ledger_control ledger_marker ledger_disposition
    declare -A validated_completed=()
    preflight_manifest="$(jq -c . "$DEPLOY_MANIFEST" 2>/dev/null)" || deploy_fail prepare preflight-required invalid-manifest || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$inventory_hash" "$revision" "$worktree_state" || return 1
    deploy_require_completed_evidence_match "$preflight_manifest" "$DEPLOY_EVIDENCE" || return 1
    [[ "$(jq -r '.phaseStatus' <<< "$preflight_manifest")" == passed ]] || deploy_fail prepare preflight-required not-passed || return 1

    DEPLOY_PREPARE_MANIFEST="$(dirname "$DEPLOY_MANIFEST")/prepare-manifest.json"
    DEPLOY_PREPARE_LEDGER="$(dirname "$DEPLOY_MANIFEST")/prepare-ledger.json"
    DEPLOY_PREPARE_EVIDENCE="$(dirname "$DEPLOY_EVIDENCE")/prepare.json"
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    installation="$(jq -r '.installationId' "$inventory")"
    if [[ -e "$DEPLOY_PREPARE_LEDGER" || -L "$DEPLOY_PREPARE_LEDGER" ]]; then
        [[ -f "$DEPLOY_PREPARE_LEDGER" && ! -L "$DEPLOY_PREPARE_LEDGER" &&
           "$(stat -c '%u:%h:%a' "$DEPLOY_PREPARE_LEDGER" 2>/dev/null)" == "$(id -u):1:600" ]] || deploy_fail prepare ledger unsafe || return 1
        DEPLOY_PREPARE_LEDGER_JSON="$(jq -c . "$DEPLOY_PREPARE_LEDGER" 2>/dev/null)" || deploy_fail prepare ledger invalid || return 1
        deploy_validate_prepare_ledger "$inventory" "$DEPLOY_PREPARE_LEDGER_JSON" "$run_id" "$mode" "$inventory_hash" "$revision" || return 1
        DEPLOY_PREPARE_LEDGER_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_PREPARE_LEDGER_JSON")"
    else
        DEPLOY_PREPARE_LEDGER_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$inventory_hash" --arg now "$now" \
          --arg revision "$revision" '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,phaseStatus:"running",startedAt:$now,updatedAt:$now,targets:[]}' )"
    fi
    while IFS= read -r existing_entry; do
        name="$(jq -r '.target' <<< "$existing_entry")"
        target="$(jq -c --arg name "$name" '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) | map(select(.name == $name))[0]' "$inventory")"
        ssh_host="$(jq -r '.sshHost' <<< "$target")"; owner="$(jq -r '.runtimeOwner' <<< "$target")"; root="$(jq -r '.runtimeRoot' <<< "$target")"
        expected_machine="$(jq -r --arg name "$name" '.targets[] | select(.name == $name) | .hostIdentity' <<< "$preflight_manifest")"
        expected_host="$(jq -r --arg name "$name" '.targets[] | select(.name == $name) | .hostName' <<< "$preflight_manifest")"
        marker_digest="$(deploy_prepare_marker_digest "$mode" "$run_id" "$inventory_hash" "$installation" "$name" "$root" "$owner")"
        lock_name=".hvo-deploy-prepare-$(printf 'v1\ntarget=%s\nroot=%s\n' "$name" "$root" | sha256sum | cut -c1-32).lock"
        remote_result="$(deploy_transport_prepare_target "$ssh_host" "$root" "$mode" "$owner" "$run_id" "$inventory_hash" \
          "$name" "$installation" "$marker_digest" "$lock_name" "$expected_machine" "$expected_host" "" validate)"
        [[ "${remote_result%%$'\t'*}" == validated ]] || { deploy_fail prepare "$name" "${remote_result#*$'\t'}"; return 1; }
        IFS=$'\t' read -r _ validated_digest validated_run validated_root validated_control validated_marker <<< "$remote_result"
        ledger_run="$(jq -r '.creatingRunId' <<< "$existing_entry")"; ledger_root="$(jq -r '.newlyCreated.root' <<< "$existing_entry")"
        ledger_control="$(jq -r '.newlyCreated.controlDirectory' <<< "$existing_entry")"; ledger_marker="$(jq -r '.newlyCreated.marker' <<< "$existing_entry")"
        ledger_disposition="$(jq -r '.disposition' <<< "$existing_entry")"
        [[ "$validated_digest" == "$marker_digest" && "$validated_run" == "$ledger_run" && "$validated_root" == "$ledger_root" &&
           "$validated_control" == "$ledger_control" && "$validated_marker" == "$ledger_marker" &&
           ( ( "$validated_root" == true && "$ledger_disposition" == newly-created ) || ( "$validated_root" == false && "$ledger_disposition" == preexisting-or-resumed ) ) ]] ||
          { deploy_fail prepare ledger provenance-mismatch; return 1; }
        validated_completed["$name"]=1
    done < <(jq -c '.targets[]' <<< "$DEPLOY_PREPARE_LEDGER_JSON")

    DEPLOY_PREPARE_MANIFEST_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$inventory_hash" --arg revision "$revision" --arg now "$now" \
      --arg started "$(jq -r '.startedAt' <<< "$DEPLOY_PREPARE_LEDGER_JSON")" \
      --argjson targets "$(jq '.targets' <<< "$DEPLOY_PREPARE_LEDGER_JSON")" \
      '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,phaseStatus:"running",startedAt:$started,updatedAt:$now,targets:$targets}' )"
    deploy_publish_json "$DEPLOY_PREPARE_MANIFEST" "$DEPLOY_PREPARE_MANIFEST_JSON" || deploy_fail prepare manifest publication-failed || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-prepare-running-manifest ]] || exit 75
    if [[ -e "$DEPLOY_PREPARE_EVIDENCE" || -L "$DEPLOY_PREPARE_EVIDENCE" ]]; then
        [[ ! -d "$DEPLOY_PREPARE_EVIDENCE" ]] || { deploy_fail prepare evidence unsafe; return 1; }
        rm -f -- "$DEPLOY_PREPARE_EVIDENCE" 2>/dev/null || { deploy_fail prepare evidence stale-remove-failed; return 1; }
    fi

    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; ssh_host="$(jq -r '.sshHost' <<< "$target")"
        owner="$(jq -r '.runtimeOwner' <<< "$target")"; root="$(jq -r '.runtimeRoot' <<< "$target")"
        expected_machine="$(jq -r --arg name "$name" '.targets[] | select(.name == $name) | .hostIdentity' <<< "$preflight_manifest")"
        expected_host="$(jq -r --arg name "$name" '.targets[] | select(.name == $name) | .hostName' <<< "$preflight_manifest")"
        [[ -n "$expected_machine" && -n "$expected_host" ]] || { deploy_fail prepare "$name" preflight-target-missing; return 1; }
        marker_digest="$(deploy_prepare_marker_digest "$mode" "$run_id" "$inventory_hash" "$installation" "$name" "$root" "$owner")"
        lock_name=".hvo-deploy-prepare-$(printf 'v1\ntarget=%s\nroot=%s\n' "$name" "$root" | sha256sum | cut -c1-32).lock"
        existing_entry="$(jq -c --arg target "$name" '.targets[]? | select(.target == $target and .status == "prepared")' <<< "$DEPLOY_PREPARE_LEDGER_JSON")"
        if [[ -n "$existing_entry" ]]; then
            [[ "${validated_completed[$name]:-}" == 1 ]] || { deploy_fail prepare ledger validation-missing; return 1; }
            deploy_status prepare "$name" passed resumed
            continue
        fi
        remote_result="$(deploy_transport_prepare_target "$ssh_host" "$root" "$mode" "$owner" "$run_id" "$inventory_hash" \
          "$name" "$installation" "$marker_digest" "$lock_name" "$expected_machine" "$expected_host" "${DEPLOY_TEST_FAILPOINT:-}" prepare)"
        remote_status="${remote_result%%$'\t'*}"
        if [[ "$remote_status" != prepared ]]; then
            deploy_fail prepare "$name" "${remote_result#*$'\t'}"
            return 1
        fi
        IFS=$'\t' read -r _ root_new control_new marker_new _ creating_run <<< "$remote_result"
        existing_entry="$(jq -c --arg target "$name" '.targets[]? | select(.target == $target)' <<< "$DEPLOY_PREPARE_LEDGER_JSON")"
        if [[ -n "$existing_entry" ]]; then
            [[ "$(jq -r '.markerDigest' <<< "$existing_entry")" == "$marker_digest" ]] || { deploy_fail prepare "$name" ledger-mismatch; return 1; }
            disposition="$(jq -r '.disposition' <<< "$existing_entry")"
            root_new="$(jq -r '.newlyCreated.root' <<< "$existing_entry")"
            control_new="$(jq -r '.newlyCreated.controlDirectory' <<< "$existing_entry")"
            marker_new="$(jq -r '.newlyCreated.marker' <<< "$existing_entry")"
        elif [[ "$root_new" == true ]]; then disposition=newly-created
        else disposition=preexisting-or-resumed
        fi
        DEPLOY_PREPARE_LEDGER_JSON="$(jq -c --arg target "$name" --arg root "$root" --arg disposition "$disposition" --arg marker "$marker_digest" \
          --arg creatingRun "$creating_run" --argjson rootNew "$root_new" --argjson controlNew "$control_new" --argjson markerNew "$marker_new" \
          '.targets = ([.targets[] | select(.target != $target)] + [{target:$target,runtimeRoot:$root,disposition:$disposition,
            newlyCreated:{root:$rootNew,controlDirectory:$controlNew,marker:$markerNew},controlDirectory:".hvo-deploy",markerDigest:$marker,creatingRunId:$creatingRun,status:"prepared"}])' <<< "$DEPLOY_PREPARE_LEDGER_JSON")"
        deploy_prepare_publish_ledger || { deploy_fail prepare "$name" ledger-publication-failed; return 1; }
        DEPLOY_PREPARE_MANIFEST_JSON="$(jq -c --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --argjson targets "$(jq '.targets' <<< "$DEPLOY_PREPARE_LEDGER_JSON")" \
          '.updatedAt=$now | .targets=$targets' <<< "$DEPLOY_PREPARE_MANIFEST_JSON")"
        deploy_publish_json "$DEPLOY_PREPARE_EVIDENCE" "$(deploy_prepare_evidence_json "$DEPLOY_PREPARE_MANIFEST_JSON")" || return 1
        deploy_publish_json "$DEPLOY_PREPARE_MANIFEST" "$DEPLOY_PREPARE_MANIFEST_JSON" || return 1
        [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-ledger-publication ]] || exit 75
        deploy_status prepare "$name" passed "$disposition"
    done < <(deploy_inventory_targets "$inventory")

    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_PREPARE_LEDGER_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_PREPARE_LEDGER_JSON")"
    deploy_prepare_publish_ledger || return 1
    DEPLOY_PREPARE_MANIFEST_JSON="$(jq -c --arg now "$now" --argjson targets "$(jq '.targets' <<< "$DEPLOY_PREPARE_LEDGER_JSON")" \
      '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now | .targets=$targets' <<< "$DEPLOY_PREPARE_MANIFEST_JSON")"
    deploy_publish_json "$DEPLOY_PREPARE_EVIDENCE" "$(deploy_prepare_evidence_json "$DEPLOY_PREPARE_MANIFEST_JSON")" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-prepare-evidence ]] || exit 75
    deploy_publish_json "$DEPLOY_PREPARE_MANIFEST" "$DEPLOY_PREPARE_MANIFEST_JSON" || return 1
}
