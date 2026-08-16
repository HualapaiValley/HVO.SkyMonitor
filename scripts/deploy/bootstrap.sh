#!/usr/bin/env bash

deploy_bootstrap_mark_failed() {
    local status="${1:-1}" now
    trap - ERR
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -n "${DEPLOY_BOOTSTRAP_JSON:-}" ]]; then
        DEPLOY_BOOTSTRAP_JSON="$(jq -c --arg now "$now" '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_BOOTSTRAP_JSON")"
        [[ -z "${DEPLOY_BOOTSTRAP_LEDGER:-}" ]] || deploy_bootstrap_publish >/dev/null 2>&1 || true
    fi
    deploy_transport_reconcile_private_uploads best-effort >/dev/null 2>&1 || true
    deploy_bootstrap_cleanup_private_remote best-effort || true
    return "$status"
}

deploy_bootstrap_cleanup_private_remote() {
    deploy_transport_reconcile_private_uploads "${1:-strict}"
}

deploy_bootstrap_register_private_remote() {
    local target path="$2"
    target="$(jq -c . <<< "$1")" || return 1
    deploy_transport_register_remote_private "$target" "$path" || return 1
}

deploy_bootstrap_publish() {
    deploy_phase_publish bootstrap DEPLOY_BOOTSTRAP_JSON "$DEPLOY_BOOTSTRAP_LEDGER" "$DEPLOY_BOOTSTRAP_MANIFEST" \
      "$DEPLOY_BOOTSTRAP_EVIDENCE" "$DEPLOY_BOOTSTRAP_COMMIT"
}

deploy_bootstrap_validate_candidate() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" inventory="$6"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --argjson inventory "$(jq -c . "$inventory")" '
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
      (.publicationGeneration | numbers) >= 1 and (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      ((keys - ["completedAt"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","phaseStatus","startedAt","updatedAt","targets"] | sort)) and
      (.targets | type == "array" and length == ([.[].target] | unique | length) and
        all(.[]; .target as $target | (keys | sort) == (["target","status","continuity"] | sort) and ([ $inventory.cameraAgents[].name ] | index($target) != null))) and
      (if .phaseStatus == "passed" then ([.targets[].target] | sort) == ([$inventory.cameraAgents[].name] | sort) else true end)' "$path" >/dev/null 2>&1 ||
      { deploy_fail bootstrap ledger invalid; return 1; }
}

deploy_bootstrap_stage_json() {
    local target="$1" local_path="$2" remote_path="$3"
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    deploy_transport_copy_private_file "$local_path" "$(jq -r '.sshHost' <<< "$target")" "$remote_path" || return 1
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON"
}

deploy_bootstrap_normalize_response() {
    local path="$1" normalized
    jq -e . "$path" >/dev/null 2>&1 || return 0
    normalized="$(umask 077; mktemp "$path.normalized.XXXXXX")" || return 1
    jq 'def contract_key:
        {Id:"id",Name:"name",LatitudeDegrees:"latitudeDegrees",LongitudeDegrees:"longitudeDegrees",
         ElevationMeters:"elevationMeters",TimeZoneId:"timeZoneId",RegistrationId:"registrationId",
         Status:"status",DeviceId:"deviceId",ObservatoryId:"observatoryId",DevicePublicId:"devicePublicId",
         IsAuthoritative:"isAuthoritative",Registrations:"registrations",VerificationCode:"verificationCode",
         ExpiresAtUtc:"expiresAtUtc",Envelope:"envelope",FriendlyName:"friendlyName",IsProvisioned:"isProvisioned",
         ConfiguredAgentId:"configuredAgentId",ActiveConfigurationSha256:"activeConfigurationSha256",
         RequestToken:"requestToken",HeaderName:"headerName",State:"state",Version:"version",Replayed:"replayed",
         CentralFrameCount:"centralFrameCount",MaximumCaptureSequence:"maximumCaptureSequence",
         CaptureControl:"captureControl",Value:"value",FleetAgentInstanceId:"fleetAgentInstanceId",
         MaximumHeartbeatSequence:"maximumHeartbeatSequence",CentralArtifactCount:"centralArtifactCount",
         CurrentRigProfileVersion:"currentRigProfileVersion",CurrentRigProfileHash:"currentRigProfileHash",
         LastHeartbeatReceivedAtUtc:"lastHeartbeatReceivedAtUtc"}[.] // .;
      walk(if type == "object" then with_entries(.key |= contract_key) else . end) |
      if type == "object" and has("registrationId") and (.status | type) == "number" then
        .status = (["Pending","Active","Revoked"][.status] // .status)
      else . end' "$path" > "$normalized" || { rm -f -- "$normalized"; return 1; }
    chmod 600 "$normalized" && mv -Tf -- "$normalized" "$path" || { rm -f -- "$normalized"; return 1; }
}

deploy_bootstrap_request() {
    local target="$1" method="$2" url="$3" body="$4" headers="$5" cookies="$6" remote_output="$7" local_output="$8"
    local status ssh
    ssh="$(jq -r '.sshHost' <<< "$target")"
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    status="$(deploy_transport_http_private "$ssh" "$method" "$url" "$body" "$headers" "$cookies" "$remote_output")" || return 1
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    deploy_transport_fetch_private_file "$ssh" "$remote_output" "$local_output" || return 1
    deploy_bootstrap_normalize_response "$local_output" || return 1
    printf '%s\n' "$status"
}

deploy_bootstrap_validate_persistent_capture_continuity() {
    local mode="$1" identity="$2" central="$3" name="$4" device maximum local_sequence central_artifacts central_artifact_sequence local_artifact_sequence
    local central_fleet_instance central_fleet_maximum local_fleet_instance local_fleet_maximum local_fleet_next
    [[ "$mode" == persistent ]] || return 0
    maximum="$(jq -r '.maximumCaptureSequence // 0' "$central")" || return 1
    [[ "$maximum" =~ ^[0-9]+$ ]] || return 1
    device="$(jq -r '.deviceId' "$identity")"
    if (( maximum > 0 )) || [[ "$(jq -r '.centralFrameCount // 0' "$central")" != 0 ]]; then
        jq -e '.durable.rawIngressDatabaseExists == true' "$identity" >/dev/null ||
          { deploy_fail bootstrap "$name" persistent-local-capture-history-missing; return 1; }
        local_sequence="$(jq -r --arg device "$device" '[.durable.captureSequences[] | select(.agentId == $device) | .lastSequence] |
          if length == 1 then .[0] else 0 end' "$identity")" || return 1
        if [[ ! "$local_sequence" =~ ^[0-9]+$ ]] || (( local_sequence <= 0 || local_sequence < maximum )); then
            deploy_fail bootstrap "$name" persistent-local-capture-history-behind
            return 1
        fi
    fi

    central_artifacts="$(jq -r '.centralArtifactCount // 0' "$central")"
    central_artifact_sequence="$(jq -r '[.latestArtifacts[].captureSequence // empty] | max // 0' "$central")"
    [[ "$central_artifacts" =~ ^[0-9]+$ && "$central_artifact_sequence" =~ ^[0-9]+$ ]] || return 1
    if (( central_artifacts > 0 )); then
        jq -e '.durable.artifactOutboxDatabaseExists == true and .durable.artifactOutboxMaximumRecordId > 0 and
          .durable.artifactOutboxMaximumAuditId > 0' "$identity" >/dev/null ||
          { deploy_fail bootstrap "$name" persistent-local-artifact-history-missing; return 1; }
        local_artifact_sequence="$(jq -r --arg device "$device" '[.durable.latestArtifacts[] |
          select(.status == "acknowledged" and .agentId == $device) | .captureSequence] | max // 0' "$identity")"
        if [[ ! "$local_artifact_sequence" =~ ^[0-9]+$ ]] || (( central_artifact_sequence <= 0 || local_artifact_sequence < central_artifact_sequence )); then
            deploy_fail bootstrap "$name" persistent-local-artifact-history-behind
            return 1
        fi
    fi

    central_fleet_instance="$(jq -r '.fleetAgentInstanceId // ""' "$central")"
    central_fleet_maximum="$(jq -r '.maximumHeartbeatSequence // 0' "$central")"
    [[ "$central_fleet_maximum" =~ ^[0-9]+$ ]] || return 1
    if (( central_fleet_maximum > 0 )) || [[ -n "$central_fleet_instance" ]]; then
        jq -e '.durable.fleetDatabaseExists == true' "$identity" >/dev/null ||
          { deploy_fail bootstrap "$name" persistent-local-fleet-history-missing; return 1; }
        local_fleet_instance="$(jq -r '.durable.fleetAgentInstanceId // ""' "$identity")"
        local_fleet_maximum="$(jq -r '.durable.fleetMaximumSequence // 0' "$identity")"
        local_fleet_next="$(jq -r '.durable.fleetNextSequence // 0' "$identity")"
        [[ "$local_fleet_maximum" =~ ^[0-9]+$ && "$local_fleet_next" =~ ^[0-9]+$ ]] || return 1
        if (( local_fleet_next > 0 && local_fleet_next - 1 > local_fleet_maximum )); then
            local_fleet_maximum=$((local_fleet_next - 1))
        fi
        if [[ -z "$central_fleet_instance" || "$local_fleet_instance" != "$central_fleet_instance" ]]; then
            deploy_fail bootstrap "$name" persistent-local-fleet-identity-mismatch
            return 1
        fi
        if (( local_fleet_maximum < central_fleet_maximum || local_fleet_next <= central_fleet_maximum )); then
            deploy_fail bootstrap "$name" persistent-local-fleet-history-behind
            return 1
        fi
    fi
}

deploy_bootstrap_owner_session() {
    local inventory="$1" target="$2" render_root="$3" remote_root="$4" response="$5"
    local ssh endpoint cookies password_path seeded_password password_setting password local_password status token header_name local_headers remote_headers
    local login_page login_response login_verification remote_response
    ssh="$(jq -r '.sshHost' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"; cookies="$remote_root/owner.cookies"
    password_path="$remote_root/owner-password"
    seeded_password="$(jq -r '.runtimeRoot' <<< "$target")/.hvo-deploy/up-$(jq -r '.runId' "$DEPLOY_MANIFEST")/private/owner-password"
    password_setting="$(jq -r '.runtimeRoot' <<< "$target")/.hvo-deploy/up-$(jq -r '.runId' "$DEPLOY_MANIFEST")/secrets/LocalIdentity__AdminPasswordFile"
    password="$(deploy_secret_value "$(jq -r '.secretSource.path' "$inventory")" "$(jq -r '.ownerPasswordSecretReference' <<< "$target")")" || return 1
    local_password="$render_root/$(jq -r '.name' <<< "$target")-owner-password"
    deploy_transport_register_local_private "$target" "$local_password" || return 1
    (umask 077; printf '%s' "$password" > "$local_password") || { unset password; return 1; }
    unset password
    deploy_bootstrap_register_private_remote "$target" "$password_path" || return 1
    deploy_bootstrap_register_private_remote "$target" "$seeded_password" || return 1
    deploy_bootstrap_register_private_remote "$target" "$password_setting" || return 1
    deploy_bootstrap_stage_json "$target" "$local_password" "$password_path" || { rm -f -- "$local_password"; return 1; }
    rm -f -- "$local_password"
    deploy_transport_forget_private_path "$local_password" || return 1
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    deploy_bootstrap_register_private_remote "$target" "$cookies" || return 1
    login_page="$remote_root/login.html"; login_response="$remote_root/login-response.html"
    login_verification="$remote_root/owner-verification.json"
    deploy_bootstrap_register_private_remote "$target" "$login_page" || return 1
    deploy_bootstrap_register_private_remote "$target" "$login_response" || return 1
    deploy_bootstrap_register_private_remote "$target" "$login_verification" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-before-owner-session-create ]] || exit 75
    if ! deploy_transport_owner_login "$ssh" "$endpoint" "$(jq -r '.ownerEmail' <<< "$target")" "$password_path" "$cookies" "$remote_root"; then
        deploy_fail bootstrap "$(jq -r '.name' <<< "$target")" owner-authentication-failed
        return 1
    fi
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-owner-session-create ]] || exit 75
    deploy_transport_remove_private_files "$ssh" "$seeded_password" "$password_setting" || return 1
    deploy_up_stage_value "$target" "$render_root" \
      "$(jq -r '.runtimeRoot' <<< "$target")/.hvo-deploy/up-$(jq -r '.runId' "$DEPLOY_MANIFEST")/secrets" \
      LocalIdentity__AllowMissingAdminPassword true || return 1
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    remote_response="$remote_root/antiforgery.json"
    deploy_bootstrap_register_private_remote "$target" "$remote_response" || return 1
    deploy_transport_register_local_private "$target" "$response" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-before-antiforgery-response-create ]] || exit 75
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/antiforgery" "" "" "$cookies" \
      "$remote_response" "$response")" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-antiforgery-response-create ]] || exit 75
    [[ "$status" == 200 ]] || { deploy_fail bootstrap "$(jq -r '.name' <<< "$target")" antiforgery-token-failed; return 1; }
    token="$(jq -er '.requestToken | strings | select(length > 0)' "$response")" || return 1
    header_name="$(jq -er '.headerName | strings | select(length > 0)' "$response")" || return 1
    rm -f -- "$response"
    deploy_transport_forget_private_path "$response" || return 1
    local_headers="$render_root/$(jq -r '.name' <<< "$target")-antiforgery.headers"; remote_headers="$remote_root/owner.headers"
    deploy_transport_register_local_private "$target" "$local_headers" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-before-owner-header-create ]] || exit 75
    (umask 077; printf '%s: %s\n' "$header_name" "$token" > "$local_headers") || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-owner-header-create ]] || exit 75
    unset token
    deploy_bootstrap_register_private_remote "$target" "$remote_headers" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-before-owner-header-stage ]] || exit 75
    deploy_bootstrap_stage_json "$target" "$local_headers" "$remote_headers" || { rm -f -- "$local_headers"; return 1; }
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-owner-header-stage ]] || exit 75
    rm -f -- "$local_headers"
    deploy_transport_forget_private_path "$local_headers" || return 1
}

deploy_bootstrap_central_headers() {
    local inventory="$1" target="$2" render_root="$3" remote_path="$4" value local_path
    value="$(deploy_secret_value "$(jq -r '.secretSource.path' "$inventory")" "$(jq -r '.deployment.automation.ownerApiKeySecretReference' "$inventory")")" || return 1
    local_path="$render_root/central.headers"
    deploy_transport_register_local_private "$target" "$local_path" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-before-central-header-create ]] || exit 75
    (umask 077; printf 'X-API-Key: %s\n' "$value" > "$local_path") || { unset value; return 1; }
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-central-header-create ]] || exit 75
    unset value
    deploy_bootstrap_register_private_remote "$target" "$remote_path" || return 1
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-before-central-header-stage ]] || exit 75
    deploy_bootstrap_stage_json "$target" "$local_path" "$remote_path" || { rm -f -- "$local_path"; return 1; }
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-central-header-stage ]] || exit 75
    rm -f -- "$local_path"
    deploy_transport_forget_private_path "$local_path" || return 1
}

deploy_bootstrap_resume_capture() {
    local target="$1" target_remote="$2" private_root="$3" render_root="$4" cookies="$5" run_id="$6"
    local name endpoint body request_headers status
    name="$(jq -r '.name' <<< "$target")"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    body="$render_root/$name-resume.json"; request_headers="$target_remote/resume-1-control.headers"
    jq -cn '{reason:"deployment bootstrap completed"}' > "$body"
    deploy_bootstrap_stage_json "$target" "$body" "$target_remote/resume.json" || return 1
    deploy_bootstrap_register_private_remote "$target" "$request_headers" || return 1
    deploy_transport_derive_idempotent_headers "$(jq -r '.sshHost' <<< "$target")" "$target_remote/owner.headers" "$request_headers" \
      "deploy-bootstrap-$run_id-$name-resume" || return 1
    status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/v1/operations/capture/resume" "$target_remote/resume.json" \
      "$request_headers" "$cookies" "$target_remote/resume-response.json" "$private_root/$name-resume-response.json")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" resume-control-failed; return 1; }
    jq -e '.state == "Running" or .state == 1' "$private_root/$name-resume-response.json" >/dev/null || return 1
    deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" "$target_remote/resume.json" "$request_headers" || return 1
    deploy_transport_forget_private_path "$request_headers" || return 1
}

deploy_stage_workload_profile() {
    local inventory="$1" target="$2" workload="$3" device_id="$4" render_root="$5" state_dir="$6" run_id="$7"
    local activate="${8:-true}"
    local manifest profile source_path source source_hash source_identity options_hash rendered rendered_hash rendered_canonical width height format seed target_root destination context project env_file
    manifest="$REPO_ROOT/deploy/split-host/workloads/canonical-workloads.json"
    [[ -f "$manifest" && ! -L "$manifest" ]] || { deploy_fail workload "$workload" canonical-manifest-missing; return 1; }
    jq -e 'type == "object" and (keys | sort) == (["schemaVersion","profiles"] | sort) and .schemaVersion == 1 and
      (.profiles | type == "object" and (keys | sort) == (["W0","W1","W2"] | sort) and all(.[];
        (keys | sort) == (["sourcePath","sourceSha256","configurationIdentitySha256","optionsSha256","width","height","pixelFormat","seed",
          "warmupOperations","measuredOperations","concurrency"] | sort) and
        (.sourcePath | test("^[A-Za-z0-9._/-]+$") and (startswith("/") | not) and
          (contains("//") | not) and (contains("../") | not)) and
        (.sourceSha256 | test("^[0-9a-f]{64}$")) and (.configurationIdentitySha256 | test("^[0-9a-f]{64}$")) and
        (.optionsSha256 | test("^[0-9a-f]{64}$")) and .width > 0 and .height > 0 and
        (.pixelFormat == "Mono16" or .pixelFormat == "BayerRggb16") and .seed == 2025 and
        .warmupOperations >= 0 and .measuredOperations >= 1 and .concurrency == 1))' "$manifest" >/dev/null ||
      { deploy_fail workload "$workload" canonical-manifest-invalid; return 1; }
    profile="$(jq -c --arg workload "$workload" '.profiles[$workload]' "$manifest")" || return 1
    jq -e '.concurrency == 1 and .measuredOperations >= 1 and .warmupOperations >= 0' <<< "$profile" >/dev/null ||
      { deploy_fail workload "$workload" unsupported-operation-shape; return 1; }
    source_path="$(jq -r '.sourcePath' <<< "$profile")"; source="$REPO_ROOT/$source_path"; source_hash="$(jq -r '.sourceSha256' <<< "$profile")"
    [[ -f "$source" && ! -L "$source" && "$(sha256sum "$source" | cut -d' ' -f1)" == "$source_hash" ]] ||
      { deploy_fail workload "$workload" profile-source-invalid; return 1; }
    source_identity="$(printf '%s' "$(jq -S -c . "$source")" | sha256sum | cut -d' ' -f1)"
    options_hash="$(printf '%s' "$(jq -S -c '.module.options' "$source")" | sha256sum | cut -d' ' -f1)"
    [[ "$source_identity" == "$(jq -r '.configurationIdentitySha256' <<< "$profile")" &&
       "$options_hash" == "$(jq -r '.optionsSha256' <<< "$profile")" ]] ||
      { deploy_fail workload "$workload" canonical-identity-mismatch; return 1; }
    width="$(jq -r '.width' <<< "$profile")"; height="$(jq -r '.height' <<< "$profile")"; format="$(jq -r '.pixelFormat' <<< "$profile")"
    seed="$(jq -r '.seed' <<< "$profile")"
    jq -e --argjson width "$width" --argjson height "$height" --arg format "$format" '
      .module.type == "VirtualSky" and .module.options.seed == 2025 and
      .rig.sensor.widthPixels == $width and .rig.sensor.heightPixels == $height and
      .rig.sensor.pixelFormat == $format and .rig.sensor.strideBytes == ($width * 2) and
      .rig.sensor.byteOrder == "LittleEndian" and
      (if $width == 64 then .module.options.maximumResults == 10 and .module.options.shotNoiseEnabled == false and
        .rig.profileVersion == "w0-deterministic-mono16-v1" and
        any(.processingSteps[]; .type == "Preview" and .options.recipeVersion == "mono16-asinh-v2") else true end)' "$source" >/dev/null ||
      { deploy_fail workload "$workload" noncanonical-or-physical-profile; return 1; }
    rendered="$render_root/$(jq -r '.name' <<< "$target")-$workload-camera-module.json"
    (umask 077; jq -S --arg device "$device_id" '.agentId=$device' "$source" > "$rendered") || return 1
    rendered_canonical="$(jq -S -c . "$rendered")"; rendered_hash="$(printf '%s' "$rendered_canonical" | sha256sum | cut -d' ' -f1)"
    if [[ "$activate" == true ]]; then
        target_root="$(jq -r '.runtimeRoot' <<< "$target")"; destination="$target_root/.hvo-deploy/up-$run_id/camera-module.json"
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        deploy_transport_copy_private_file "$rendered" "$(jq -r '.sshHost' <<< "$target")" "$destination" || return 1
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        context="$(jq -r '.dockerContext' <<< "$target")"; project="$(jq -r '.deployment.resources.project' "$inventory")-$(jq -r '.name' <<< "$target")"
        env_file="$state_dir/up-rendered/$(jq -r '.name' <<< "$target").env"
        deploy_up_compose_mutation "$target" "$context" "$project" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" \
          up -d --force-recreate cameraagent || return 1
        deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$target")" "$(jq -r '.internalEndpoint' <<< "$target")/health" || return 1
    elif [[ "$activate" != false ]]; then
        return 1
    fi
    jq -cn --arg workload "$workload" --arg sourcePath "$source_path" --arg sourceSha256 "$source_hash" --arg configurationIdentitySha256 "$source_identity" \
      --arg optionsSha256 "$options_hash" --arg configSha256 "$rendered_hash" --argjson width "$width" \
      --argjson height "$height" --arg pixelFormat "$format" --argjson seed "$seed" \
      --argjson warmup "$(jq '.warmupOperations' <<< "$profile")" --argjson measured "$(jq '.measuredOperations' <<< "$profile")" \
      --argjson concurrency "$(jq '.concurrency' <<< "$profile")" \
      '{workload:$workload,sourcePath:$sourcePath,sourceSha256:$sourceSha256,configurationIdentitySha256:$configurationIdentitySha256,
        optionsSha256:$optionsSha256,configSha256:$configSha256,width:$width,height:$height,
        pixelFormat:$pixelFormat,seed:$seed,warmupOperations:$warmup,measuredOperations:$measured,concurrency:$concurrency}'
}

deploy_run_bootstrap() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6"
    local state_dir evidence_dir render_root private_root now logic logic_root logic_remote headers observatories observatory_id matching conflicting
    local target name target_root target_remote response status identity device_id verification_code registration_id envelope_file envelope central_status
    local cookies endpoint project context env_file continuity fleet_ack pre_local_ack pre_central_ack pre_ack_time current_local_ack current_central_ack local_before
    local pre_local_capture pre_central_capture current_local_capture current_central_capture
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/up-manifest.json" bootstrap "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" "$worktree" || return 1
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"
    deploy_transport_reconcile_private_uploads strict || { deploy_fail bootstrap private-upload-registry cleanup-failed; return 1; }
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    render_root="$state_dir/bootstrap-rendered"; private_root="$state_dir/bootstrap-private"
    install -d -m 700 "$render_root" "$private_root"
    DEPLOY_BOOTSTRAP_MANIFEST="$state_dir/bootstrap-manifest.json"; DEPLOY_BOOTSTRAP_LEDGER="$state_dir/bootstrap-ledger.json"; DEPLOY_BOOTSTRAP_EVIDENCE="$evidence_dir/bootstrap.json"
    DEPLOY_BOOTSTRAP_COMMIT="$state_dir/bootstrap-commit.json"
    deploy_require_no_orphan_phase_files "$DEPLOY_BOOTSTRAP_LEDGER" "$DEPLOY_BOOTSTRAP_MANIFEST" "$DEPLOY_BOOTSTRAP_EVIDENCE" "$DEPLOY_BOOTSTRAP_COMMIT" || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -e "$DEPLOY_BOOTSTRAP_LEDGER" || -L "$DEPLOY_BOOTSTRAP_LEDGER" ]]; then
        deploy_require_phase_files_match "$DEPLOY_BOOTSTRAP_LEDGER" "$DEPLOY_BOOTSTRAP_MANIFEST" "$DEPLOY_BOOTSTRAP_EVIDENCE" "$DEPLOY_BOOTSTRAP_COMMIT" \
          deploy_bootstrap_validate_candidate "$run_id" "$mode" "$hash" "$revision" "$inventory" || return 1
        jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" '
          .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
          (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
          ((keys - ["completedAt"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","phaseStatus","startedAt","updatedAt","targets"] | sort)) and
          (.targets | type == "array" and length == ([.[].target] | unique | length) and
            all(.[]; (keys | sort) == (["target","status","continuity"] | sort)))' "$DEPLOY_BOOTSTRAP_LEDGER" >/dev/null 2>&1 ||
          { deploy_fail bootstrap ledger invalid; return 1; }
        DEPLOY_BOOTSTRAP_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' "$DEPLOY_BOOTSTRAP_LEDGER")"
    else
        DEPLOY_BOOTSTRAP_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg now "$now" \
          '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,phaseStatus:"running",startedAt:$now,updatedAt:$now,targets:[]}')"
    fi
    deploy_bootstrap_publish

    logic="$(jq -c '.logicHost' "$inventory")"; logic_root="$(jq -r '.runtimeRoot' <<< "$logic")"; logic_remote="$logic_root/.hvo-deploy/bootstrap-$run_id"
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote" || return 1
    headers="$logic_remote/owner.headers"
    deploy_bootstrap_central_headers "$inventory" "$logic" "$render_root" "$headers" || return 1
    observatories="$private_root/observatories.json"
    status="$(deploy_bootstrap_request "$logic" GET "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/observatories" "" "$headers" "" \
      "$logic_remote/observatories.json" "$observatories")" || return 1
    [[ "$status" == 200 ]] || { deploy_fail bootstrap observatory list-failed; return 1; }
    matching="$(jq -c --argjson expected "$(jq -c '.observatory' "$inventory")" '[.[] | select(
      (.name // .Name) == $expected.name and (.latitudeDegrees // .LatitudeDegrees) == $expected.latitudeDegrees and
      (.longitudeDegrees // .LongitudeDegrees) == $expected.longitudeDegrees and
      (.elevationMeters // .ElevationMeters) == $expected.elevationMeters and (.timeZoneId // .TimeZoneId) == $expected.timeZoneId)]' "$observatories")"
    conflicting="$(jq -r --arg name "$(jq -r '.observatory.name' "$inventory")" '[.[] | select((.name // .Name) == $name)] | length' "$observatories")"
    if [[ "$(jq 'length' <<< "$matching")" == 1 ]]; then
        observatory_id="$(jq -r '.[0].id // .[0].Id' <<< "$matching")"
    elif [[ "$conflicting" != 0 ]]; then
        deploy_fail bootstrap observatory configured-observatory-conflict; return 1
    else
        jq -c '.observatory + {id:null,isActive:true,allowedDeploymentRadiusMeters:null}' "$inventory" > "$render_root/observatory-request.json"
        deploy_bootstrap_stage_json "$logic" "$render_root/observatory-request.json" "$logic_remote/observatory-request.json" || return 1
        status="$(deploy_bootstrap_request "$logic" POST "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/observatories" \
          "$logic_remote/observatory-request.json" "$headers" "" "$logic_remote/observatory-response.json" "$private_root/observatory-response.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail bootstrap observatory create-failed; return 1; }
        observatory_id="$(jq -er '(.id // .Id) | strings | select(test("^[0-9a-fA-F-]{36}$"))' "$private_root/observatory-response.json")" || return 1
    fi

    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; target_root="$(jq -r '.runtimeRoot' <<< "$target")"; target_remote="$target_root/.hvo-deploy/bootstrap-$run_id"
        deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$target")" "$target_remote" || return 1
        response="$private_root/$name-antiforgery.json"; deploy_bootstrap_owner_session "$inventory" "$target" "$render_root" "$target_remote" "$response" || return 1
        cookies="$target_remote/owner.cookies"; endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
        status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/internal/deployment/identity" "" "$target_remote/owner.headers" "$cookies" \
          "$target_remote/identity.json" "$private_root/$name-identity.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" identity-read-failed; return 1; }
        identity="$private_root/$name-identity.json"; device_id="$(jq -er '.deviceId | strings | select(test("^[A-Za-z0-9._-]{1,128}$"))' "$identity")" || return 1
        verification_code="$(jq -er '.verificationCode | strings | select(length >= 4 and length <= 32)' "$identity")" || return 1
        local_before="$identity"
        if [[ "$mode" == persistent ]]; then
            status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
              "$target_remote/persistent-continuity.json" "$private_root/$name-persistent-continuity.json")" || return 1
            [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" persistent-local-continuity-failed; return 1; }
            local_before="$private_root/$name-persistent-continuity.json"
        fi

        status="$(deploy_bootstrap_request "$logic" GET "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/devices/continuity/$device_id" "" "$headers" "" \
          "$logic_remote/$name-continuity.json" "$private_root/$name-central-before.json")" || return 1
        if [[ "$status" == 200 ]]; then
            central_status="$(jq -er '.status | select(. == "Pending" or . == "Active" or . == "Revoked")' "$private_root/$name-central-before.json")" || return 1
            registration_id="$(jq -er '.registrationId' "$private_root/$name-central-before.json")" || return 1
            jq -e --arg registration "$registration_id" '[.registrations[] | select(.isAuthoritative == true and .registrationId == $registration)] | length == 1' \
              "$private_root/$name-central-before.json" >/dev/null || { deploy_fail bootstrap "$name" ambiguous-authoritative-registration; return 1; }
            deploy_bootstrap_validate_persistent_capture_continuity "$mode" "$local_before" "$private_root/$name-central-before.json" "$name" || return 1
            if [[ "$(jq -r '.isProvisioned' "$identity")" == true ]]; then
                if [[ "$central_status" != Active ]] || ! jq -e --arg device "$device_id" --arg observatory "$observatory_id" --argjson local "$(jq -c . "$identity")" \
                  '.deviceId == $device and .observatoryId == $observatory and .devicePublicId == $local.devicePublicId and .observatoryId == $local.observatoryId' \
                  "$private_root/$name-central-before.json" >/dev/null; then
                    deploy_fail bootstrap "$name" central-history-edge-state-mismatch; return 1
                fi
            elif [[ "$central_status" == Pending ]]; then
                jq -e --arg observatory "$observatory_id" --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
                  '.observatoryId == $observatory and .expiresAtUtc != null and .expiresAtUtc > $now' "$private_root/$name-central-before.json" >/dev/null ||
                  { deploy_fail bootstrap "$name" pending-registration-expired-or-mismatched; return 1; }
            elif [[ "$central_status" == Active ]]; then
                jq -e '.centralFrameCount == 0 and .centralArtifactCount == 0 and .maximumCaptureSequence == null and
                  .maximumHeartbeatSequence == null and .lastHeartbeatReceivedAtUtc == null' "$private_root/$name-central-before.json" >/dev/null ||
                  { deploy_fail bootstrap "$name" active-edge-missing-requires-incident-recovery; return 1; }
                jq -cn --arg registration "$registration_id" --arg device "$device_id" --arg code "$verification_code" --arg observatory "$observatory_id" \
                  --arg friendly "$(jq -r '.friendlyName' <<< "$target")" \
                  '{registrationId:$registration,deviceId:$device,verificationCode:$code,observatoryId:$observatory,friendlyName:$friendly}' > "$render_root/$name-recovery.json"
                deploy_bootstrap_stage_json "$logic" "$render_root/$name-recovery.json" "$logic_remote/$name-recovery.json" || return 1
                status="$(deploy_bootstrap_request "$logic" POST "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/devices/recover" \
                  "$logic_remote/$name-recovery.json" "$headers" "" "$logic_remote/$name-recovery-response.json" "$private_root/$name-recovery-response.json")" || return 1
                [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" safe-recovery-rejected; return 1; }
                rm -f -- "$private_root/$name-envelope.json"
                deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote/$name-envelope.json" || return 1
            else
                deploy_fail bootstrap "$name" revoked-registration-requires-new-device-identity; return 1
            fi
        elif [[ "$status" == 404 ]]; then
            [[ "$(jq -r '.isProvisioned' "$identity")" == false ]] || { deploy_fail bootstrap "$name" edge-state-central-history-missing; return 1; }
            jq -cn --arg device "$device_id" --arg code "$verification_code" --arg observatory "$observatory_id" --arg friendly "$(jq -r '.friendlyName' <<< "$target")" \
              '{deviceId:$device,verificationCode:$code,observatoryId:$observatory,friendlyName:$friendly}' > "$render_root/$name-registration.json"
            deploy_bootstrap_stage_json "$logic" "$render_root/$name-registration.json" "$logic_remote/$name-registration.json" || return 1
            status="$(deploy_bootstrap_request "$logic" POST "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/devices/verify" \
              "$logic_remote/$name-registration.json" "$headers" "" "$logic_remote/$name-registration-response.json" "$private_root/$name-registration-response.json")" || return 1
            [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" registration-failed; return 1; }
            registration_id="$(jq -er '.registrationId' "$private_root/$name-registration-response.json")" || return 1
            [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-registration ]] || return 75
        else
            deploy_fail bootstrap "$name" unexpected-central-continuity-status; return 1
        fi
        if [[ "$(jq -r '.isProvisioned' "$identity")" == false ]]; then
            envelope_file="$private_root/$name-envelope.json"
            deploy_transport_register_local_private "$target" "$envelope_file" || return 1
            if [[ -f "$envelope_file" ]]; then
                jq -e --arg registration "$registration_id" --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
                  '.registrationId == $registration and .expiresAtUtc > $now and (.envelope | type == "string" and length > 0)' "$envelope_file" >/dev/null ||
                  { rm -f -- "$envelope_file"; deploy_fail bootstrap "$name" retained-envelope-invalid-or-expired; return 1; }
            else
                deploy_bootstrap_register_private_remote "$logic" "$logic_remote/$name-envelope.json" || return 1
                deploy_bootstrap_register_private_remote "$target" "$target_remote/bootstrap-request.json" || return 1
                jq -cn --arg registration "$registration_id" --arg device "$device_id" --arg observatory "$observatory_id" \
                  '{registrationId:$registration,deviceId:$device,observatoryId:$observatory,envelopeLifetimeMinutes:15}' > "$render_root/$name-envelope-request.json"
                deploy_bootstrap_stage_json "$logic" "$render_root/$name-envelope-request.json" "$logic_remote/$name-envelope-request.json" || return 1
                status="$(deploy_bootstrap_request "$logic" POST "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/devices/envelope" \
                  "$logic_remote/$name-envelope-request.json" "$headers" "" "$logic_remote/$name-envelope.json" "$envelope_file")" || return 1
                [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" envelope-create-failed; return 1; }
                jq -e --arg registration "$registration_id" --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
                  '.registrationId == $registration and .expiresAtUtc > $now' "$envelope_file" >/dev/null || return 1
                [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-envelope ]] || return 75
            fi
            envelope="$(jq -er '.envelope | strings | select(length > 0)' "$envelope_file")" || return 1
            deploy_transport_register_local_private "$target" "$render_root/$name-bootstrap-request.json" || return 1
            jq -cn --arg envelope "$envelope" '{envelope:$envelope}' > "$render_root/$name-bootstrap-request.json"; unset envelope
            deploy_bootstrap_register_private_remote "$target" "$target_remote/bootstrap-request.json" || return 1
            deploy_bootstrap_stage_json "$target" "$render_root/$name-bootstrap-request.json" "$target_remote/bootstrap-request.json" || return 1
            rm -f -- "$render_root/$name-bootstrap-request.json"
            deploy_transport_forget_private_path "$render_root/$name-bootstrap-request.json" || return 1
            status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/internal/deployment/bootstrap" "$target_remote/bootstrap-request.json" "$target_remote/owner.headers" "$cookies" \
              "$target_remote/bootstrap-response.json" "$private_root/$name-bootstrap-response.json")" || return 1
            [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" envelope-import-ambiguous; return 1; }
            [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-central-activation ]] || return 75
            status="$(deploy_bootstrap_request "$target" POST "$endpoint/api/internal/deployment/identity" "" "$target_remote/owner.headers" "$cookies" \
              "$target_remote/identity-after.json" "$private_root/$name-identity-after.json")" || return 1
            if [[ "$status" != 200 ]] || ! jq -e --arg device "$device_id" '.deviceId == $device and .isProvisioned == true' "$private_root/$name-identity-after.json" >/dev/null; then
                deploy_fail bootstrap "$name" local-secret-save-not-confirmed; return 1
            fi
            rm -f -- "$envelope_file"
            deploy_transport_forget_private_path "$envelope_file" || return 1
            deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote/$name-envelope.json" || return 1
            deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" "$target_remote/bootstrap-request.json" || return 1
            [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-local-secret-save ]] || return 75
        fi
        if [[ "$(jq -r '.isProvisioned' "$identity")" == true || -f "$private_root/$name-identity-after.json" ]]; then
            rm -f -- "$private_root/$name-envelope.json"
            deploy_transport_forget_private_path "$private_root/$name-envelope.json" || return 1
            deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote/$name-envelope.json" || return 1
            deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" "$target_remote/bootstrap-request.json" || return 1
        fi
        unset verification_code

        status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
          "$target_remote/pre-restart-continuity.json" "$private_root/$name-pre-restart-continuity.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" pre-restart-local-continuity-failed; return 1; }
        status="$(deploy_bootstrap_request "$logic" GET "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/devices/continuity/$device_id" "" "$headers" "" \
          "$logic_remote/$name-pre-restart-central.json" "$private_root/$name-pre-restart-central.json")" || return 1
        [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" pre-restart-central-continuity-failed; return 1; }
        pre_local_ack="$(jq -r '.durable.fleetNextSequence // -1' "$private_root/$name-pre-restart-continuity.json")"
        pre_central_ack="$(jq -r '.maximumHeartbeatSequence // -1' "$private_root/$name-pre-restart-central.json")"
        pre_ack_time="$(jq -r '.lastHeartbeatReceivedAtUtc // ""' "$private_root/$name-pre-restart-central.json")"
        pre_local_capture="$(jq -r --arg device "$device_id" '[.durable.captureSequences[] | select(.agentId == $device) | .lastSequence] | first // -1' "$private_root/$name-pre-restart-continuity.json")"
        pre_central_capture="$(jq -r '.maximumCaptureSequence // -1' "$private_root/$name-pre-restart-central.json")"

        deploy_up_stage_value "$target" "$render_root" "$target_root/.hvo-deploy/up-$run_id/secrets" CameraAgent__AgentId "$device_id" || return 1
        deploy_up_stage_value "$target" "$render_root" "$target_root/.hvo-deploy/up-$run_id/secrets" CameraAgent__ProvisioningStartupGate__Enabled false || return 1
        deploy_up_stage_value "$target" "$render_root" "$target_root/.hvo-deploy/up-$run_id/secrets" CameraAgent__CaptureDistribution__UploadEnabled true || return 1
        deploy_up_stage_value "$target" "$render_root" "$target_root/.hvo-deploy/up-$run_id/secrets" CameraAgent__TransientDetection__Mode "$(jq -r '.deployment.transient.mode' "$inventory")" || return 1
        deploy_up_stage_value "$target" "$render_root" "$target_root/.hvo-deploy/up-$run_id/secrets" CameraAgent__TransientDetection__Required "$(jq -r '.deployment.transient.mode != "Off"' "$inventory")" || return 1
        project="$(jq -r '.deployment.resources.project' "$inventory")"; context="$(jq -r '.dockerContext' <<< "$target")"; env_file="$state_dir/up-rendered/$name.env"
        deploy_up_compose_mutation "$target" "$context" "$project-$name" "$env_file" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" up -d --force-recreate cameraagent || return 1
        deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$target")" "$endpoint/health" || return 1
        deploy_bootstrap_resume_capture "$target" "$target_remote" "$private_root" "$render_root" "$cookies" "$run_id" || return 1
        fleet_ack=""
        for _ in $(seq 1 60); do
            status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/internal/deployment/continuity" "" "" "$cookies" \
              "$target_remote/continuity.json" "$private_root/$name-continuity.json")" || return 1
            [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" unexpected-local-continuity-status; return 1; }
            status="$(deploy_bootstrap_request "$logic" GET "$(jq -r '.internalEndpoint' <<< "$logic")/api/internal/devices/continuity/$device_id" "" "$headers" "" \
              "$logic_remote/$name-post-restart-central.json" "$private_root/$name-post-restart-central.json")" || return 1
            [[ "$status" == 200 ]] || { deploy_fail bootstrap "$name" unexpected-central-post-restart-status; return 1; }
            current_local_ack="$(jq -r '.durable.fleetNextSequence // -1' "$private_root/$name-continuity.json")"
            current_central_ack="$(jq -r '.maximumHeartbeatSequence // -1' "$private_root/$name-post-restart-central.json")"
            current_local_capture="$(jq -r --arg device "$device_id" '[.durable.captureSequences[] | select(.agentId == $device) | .lastSequence] | first // -1' "$private_root/$name-continuity.json")"
            current_central_capture="$(jq -r '.maximumCaptureSequence // -1' "$private_root/$name-post-restart-central.json")"
            if jq -e --arg device "$device_id" --arg beforeTime "$pre_ack_time" --argjson beforeLocal "$pre_local_ack" --argjson beforeCentral "$pre_central_ack" \
              --argjson currentLocal "$current_local_ack" --argjson currentCentral "$current_central_ack" '
              .deviceId == $device and .configuredAgentId == $device and .isProvisioned == true and .lastFleetAcknowledgedUtc != null and
              $currentLocal > $beforeLocal and $currentCentral > $beforeCentral' "$private_root/$name-continuity.json" >/dev/null &&
              (( current_local_capture > pre_local_capture && current_central_capture > pre_central_capture )) &&
              jq -e --arg before "$pre_ack_time" --arg expectedHash "$(jq -r '.expectedRigProfileHash' "$private_root/$name-continuity.json")" \
                '.lastHeartbeatReceivedAtUtc != null and ($before == "" or .lastHeartbeatReceivedAtUtc > $before) and
                 (.currentRigProfileVersion | numbers) >= 1 and .currentRigProfileHash == $expectedHash' \
                "$private_root/$name-post-restart-central.json" >/dev/null; then fleet_ack=true; break; fi
            [[ "${DEPLOY_TEST_FAILPOINT:-}" != rig-profile-unavailable ]] || break
            sleep "${DEPLOY_TEST_POLL_SECONDS:-2}"
        done
        [[ "$fleet_ack" == true ]] || { deploy_fail bootstrap "$name" first-fleet-acknowledgement-timeout; return 1; }
        continuity="$(jq -c --argjson beforeLocal "$pre_local_ack" --argjson beforeCentral "$pre_central_ack" --arg beforeTime "$pre_ack_time" \
          --argjson afterLocal "$current_local_ack" --argjson afterCentral "$current_central_ack" --argjson captureBeforeLocal "$pre_local_capture" \
          --argjson captureAfterLocal "$current_local_capture" --argjson captureBeforeCentral "$pre_central_capture" --argjson captureAfterCentral "$current_central_capture" \
          '{deviceId,configuredAgentId,devicePublicId,observatoryId,lastFleetAcknowledgedUtc,expectedRigProfileVersion,expectedRigProfileHash,
            fleetAcknowledgement:{localSequenceBefore:$beforeLocal,localSequenceAfter:$afterLocal,centralSequenceBefore:$beforeCentral,
              centralSequenceAfter:$afterCentral,centralReceivedAtBefore:(if $beforeTime == "" then null else $beforeTime end)},
            captureAcknowledgement:{localSequenceBefore:$captureBeforeLocal,localSequenceAfter:$captureAfterLocal,
              centralSequenceBefore:$captureBeforeCentral,centralSequenceAfter:$captureAfterCentral}}' "$private_root/$name-continuity.json")"
        DEPLOY_BOOTSTRAP_JSON="$(jq -c --arg target "$name" --argjson continuity "$continuity" \
          '.targets = ([.targets[] | select(.target != $target)] + [{target:$target,status:"ready",continuity:$continuity}])' <<< "$DEPLOY_BOOTSTRAP_JSON")"
        deploy_bootstrap_publish
    done < <(jq -c '.cameraAgents[]' "$inventory")

    # Central processing starts only after every agent acknowledges its final
    # identity, upload, rig, and transient configuration.
    deploy_up_stage_value "$logic" "$render_root" "$logic_root/.hvo-deploy/up-$run_id/runtime-secrets" \
      CentralTransient__Mode "$(jq -r '.deployment.transient.mode' "$inventory")" || return 1
    deploy_up_stage_value "$logic" "$render_root" "$logic_root/.hvo-deploy/up-$run_id/runtime-secrets" \
      TransientPayloadRelease__Enabled "$(jq -r '.deployment.transient.mode != "Off"' "$inventory")" || return 1
    project="$(jq -r '.deployment.resources.project' "$inventory")-logic"; context="$(jq -r '.dockerContext' <<< "$logic")"
    env_file="$state_dir/up-rendered/$(jq -r '.name' <<< "$logic").env"
    deploy_up_compose_mutation "$logic" "$context" "$project" "$env_file" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" \
      up -d --force-recreate logichost || return 1
    deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$logic")" "$(jq -r '.internalEndpoint' <<< "$logic")/health" || return 1
    deploy_transport_reconcile_private_uploads strict || return 1
    deploy_bootstrap_cleanup_private_remote strict
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-bootstrap-cleanup ]] || exit 75
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; DEPLOY_BOOTSTRAP_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_BOOTSTRAP_JSON")"
    deploy_bootstrap_publish
}
