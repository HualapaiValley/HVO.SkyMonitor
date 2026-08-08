#!/usr/bin/env bash

deploy_secret_value() {
    local file="$1" reference="$2" line count
    if LC_ALL=C grep -q '[[:cntrl:]]' "$file" 2>/dev/null; then
        return 1
    fi
    count="$(grep -Ec "^${reference}=" "$file" 2>/dev/null)" || return 1
    [[ "$count" == 1 ]] || return 1
    line="$(grep -E "^${reference}=" "$file" | cut -d= -f2-)" || return 1
    [[ -n "$line" && "$line" != *$'\n'* && ${#line} -le 16384 ]] || return 1
    if printf '%s' "$line" | LC_ALL=C grep -q '[[:cntrl:]]'; then
        return 1
    fi
    printf '%s' "$line"
}

deploy_up_catalog_required_kind() {
    case "$(jq -r '.catalog.kind' "$1")" in
      fixture) printf 'Fixture\n' ;;
      production) printf 'Production\n' ;;
      *) return 1 ;;
    esac
}

deploy_up_stage_named_secret() {
    local inventory="$1" target="$2" render_root="$3" remote_root="$4" reference="$5" name="$6"
    local value local_secret ssh
    ssh="$(jq -r '.sshHost' <<< "$target")"
    value="$(deploy_secret_value "$(jq -r '.secretSource.path' "$inventory")" "$reference")" || return 1
    local_secret="$render_root/service-secret-$name"
    (umask 077; printf '%s' "$value" > "$local_secret") || { rm -f -- "$local_secret"; return 1; }
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || { rm -f -- "$local_secret"; return 1; }
    deploy_transport_copy_private_file "$local_secret" "$ssh" "$remote_root/$name" || { rm -f -- "$local_secret"; return 1; }
    rm -f -- "$local_secret"
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON"
}

deploy_up_stage_value() {
    local target="$1" render_root="$2" remote_root="$3" name="$4" value="$5"
    local local_file ssh
    ssh="$(jq -r '.sshHost' <<< "$target")"; local_file="$render_root/config-$name"
    (umask 077; printf '%s' "$value" > "$local_file") || { rm -f -- "$local_file"; return 1; }
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || { rm -f -- "$local_file"; return 1; }
    deploy_transport_copy_private_file "$local_file" "$ssh" "$remote_root/$name" || { rm -f -- "$local_file"; return 1; }
    rm -f -- "$local_file"
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON"
}

deploy_up_validate_local_inputs() {
    local inventory="$1" path owner mode links length target
    local signing encryption
    signing="$(jq -r '.deployment.certificates.signingPath' "$inventory")"
    encryption="$(jq -r '.deployment.certificates.encryptionPath' "$inventory")"
    [[ "$signing" != "$encryption" ]] || { deploy_fail up certificates paths-must-be-distinct; return 1; }
    for path in "$signing" "$encryption"; do
        [[ -f "$path" && ! -L "$path" ]] || { deploy_fail up certificates missing-or-unsafe; return 1; }
        if ! owner="$(stat -c %u -- "$path" 2>/dev/null)" || ! mode="$(stat -c %a -- "$path" 2>/dev/null)" ||
          ! links="$(stat -c %h -- "$path" 2>/dev/null)" || ! length="$(stat -c %s -- "$path" 2>/dev/null)"; then
            deploy_fail up certificates stat-failed
            return 1
        fi
        [[ "$owner" == "$(id -u)" && "$links" == 1 && ( "$mode" == 400 || "$mode" == 600 ) && "$length" -ge 1 && "$length" -le 1048576 ]] ||
          { deploy_fail up certificates owner-mode-or-length-invalid; return 1; }
    done
    while IFS= read -r target; do
        path="$(jq -r '.moduleConfigPath' <<< "$target")"
        [[ -f "$path" && ! -L "$path" && "$(jq -r 'has("agentId") and has("module") and has("rig")' "$path" 2>/dev/null)" == true ]] ||
          { deploy_fail up camera-module missing-unsafe-or-invalid; return 1; }
    done < <(jq -c '.cameraAgents[]' "$inventory")
}

deploy_up_validate_namespaces() {
    local inventory="$1" mode="$2" run_id="$3"
    [[ "$mode" != isolated ]] || jq -e --arg run "$run_id" '
      [.deployment.resources.project,.deployment.resources.sqlDatabase,.deployment.resources.redisPrefix,
       .deployment.resources.artifactBucket,.deployment.resources.diagnosticsBucket] |
      all(.[]; contains($run))' "$inventory" >/dev/null 2>&1 ||
      { deploy_fail up namespaces isolated-run-id-required; return 1; }
    if [[ "$(jq -r '.deployment.services.mode' "$inventory")" == deploy ]]; then
        [[ "$(jq -r '.deployment.services.sql.adminUser' "$inventory")" == sa ]] ||
          { deploy_fail up services sql-deploy-admin-must-be-sa; return 1; }
    fi
}

deploy_up_compose_mutation() {
    local target="$1" context="$2" project="$3" env_file="$4" compose_file="$5"
    shift 5
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    deploy_transport_compose "$context" "$project" "$env_file" "$compose_file" "$@" || return 1
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON"
}

deploy_up_mark_failed() {
    local status="${1:-1}" now
    trap - ERR
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -n "${DEPLOY_UP_JSON:-}" ]]; then
        DEPLOY_UP_JSON="$(jq -c --arg now "$now" '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_UP_JSON")"
        [[ -z "${DEPLOY_UP_LEDGER:-}" ]] || deploy_publish_json "$DEPLOY_UP_LEDGER" "$DEPLOY_UP_JSON" >/dev/null 2>&1 || true
        [[ -z "${DEPLOY_UP_EVIDENCE:-}" ]] || deploy_publish_json "$DEPLOY_UP_EVIDENCE" "$DEPLOY_UP_JSON" >/dev/null 2>&1 || true
        [[ -z "${DEPLOY_UP_MANIFEST:-}" ]] || deploy_publish_json "$DEPLOY_UP_MANIFEST" "$DEPLOY_UP_JSON" >/dev/null 2>&1 || true
    fi
    deploy_transport_reconcile_private_uploads best-effort >/dev/null 2>&1 || true
    return "$status"
}

deploy_up_stage_target() {
    local inventory="$1" target="$2" run_id="$3" render_root="$4" image="$5" component="$6"
    local name root ssh config_root secrets_root state_root catalog_root env_file mapping reference key value local_secret port image_key destination
    local -a secret_destinations
    name="$(jq -r '.name' <<< "$target")"; root="$(jq -r '.runtimeRoot' <<< "$target")"; ssh="$(jq -r '.sshHost' <<< "$target")"
    config_root="$root/.hvo-deploy/up-$run_id"; secrets_root="$config_root/secrets"; state_root="$root/application"; catalog_root="$(jq -r '.deployment.catalog.installRoot' "$inventory")"
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    deploy_transport_remote_directories "$ssh" "$config_root" "$secrets_root" "$config_root/initializer-secrets" "$config_root/runtime-secrets" \
      "$config_root/certificates" "$config_root/sql" "$config_root/minio" "$config_root/minio-output" "$config_root/private" "$config_root/private/mc" "$state_root" \
      "$state_root/data-protection" "$state_root/identity" "$state_root/provisioning" "$state_root/raw" "$state_root/archive" \
      "$state_root/sql" "$state_root/redis" "$state_root/minio" || return 1
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    if [[ "$component" == logicHost ]]; then while IFS= read -r mapping; do
        reference="$(jq -r '.reference' <<< "$mapping")"; key="$(jq -r '.key' <<< "$mapping")"
        value="$(deploy_secret_value "$(jq -r '.secretSource.path' "$inventory")" "$reference")" || { deploy_fail up "$name" secret-reference-invalid; return 1; }
        local_secret="$render_root/$name-secret-$key"
        (umask 077; printf '%s' "$value" > "$local_secret") || { rm -f -- "$local_secret"; return 1; }
        secret_destinations=("$secrets_root")
        if [[ "$component" == logicHost ]]; then
            secret_destinations=("$config_root/initializer-secrets" "$config_root/runtime-secrets")
            [[ "$key" != ConnectionStrings__skymonitordb-migrations ]] || secret_destinations=("$config_root/initializer-secrets")
            [[ "$key" != ConnectionStrings__skymonitordb ]] || secret_destinations=("$config_root/runtime-secrets")
        fi
        for destination in "${secret_destinations[@]}"; do
            deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || { rm -f -- "$local_secret"; return 1; }
            deploy_transport_copy_private_file "$local_secret" "$ssh" "$destination/$key" || { rm -f -- "$local_secret"; deploy_fail up "$name" secret-transfer-failed; return 1; }
        done
        rm -f -- "$local_secret"
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
    done < <(jq -c '.deployment.secretMappings[]' "$inventory"); fi
    if [[ "$component" == logicHost ]]; then
        deploy_transport_copy_private_file "$(jq -r '.deployment.certificates.signingPath' "$inventory")" "$ssh" "$config_root/certificates/signing.pfx" || return 1
        deploy_transport_copy_private_file "$(jq -r '.deployment.certificates.encryptionPath' "$inventory")" "$ssh" "$config_root/certificates/encryption.pfx" || return 1
        for destination in "$config_root/initializer-secrets" "$config_root/runtime-secrets"; do
            deploy_up_stage_value "$target" "$render_root" "$destination" OpenIddictCertificates__SigningPath /run/hvo-certificates/signing.pfx || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" OpenIddictCertificates__EncryptionPath /run/hvo-certificates/encryption.pfx || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Redis__InstanceName "$(jq -r '.deployment.services.redis.prefix' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Minio__Endpoint "$(jq -r '.deployment.services.minio.host' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Minio__Port "$(jq -r '.deployment.services.minio.port' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Minio__UseSsl "$(jq -r '.deployment.services.minio.useSsl' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" ObjectStorage__ArtifactBucket "$(jq -r '.deployment.resources.artifactBucket' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" ObjectStorage__DiagnosticsBucket "$(jq -r '.deployment.resources.diagnosticsBucket' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Catalog__Root /app/catalog || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Catalog__RequiredPackageKind "$(deploy_up_catalog_required_kind "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Smtp__Host "$(jq -r '.deployment.services.smtp.host' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Smtp__Port "$(jq -r '.deployment.services.smtp.port' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" DeviceBootstrap__CentralIdentity__Mode ClientCredentials || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" DeviceBootstrap__CentralIdentity__ServiceUrl "$(jq -r '.logicHost.publicEndpoint' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" DeviceBootstrap__CentralIdentity__ClientCredentials__ClientId "$(jq -r '.deployment.deviceBootstrap.clientId' "$inventory")" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" Deployment__Mode "$mode" || return 1
            deploy_up_stage_value "$target" "$render_root" "$destination" ReverseProxy__Enabled "$(jq -r '(.trustedProxyAddresses | length) > 0' <<< "$target")" || return 1
            while IFS= read -r value; do
                key="ReverseProxy__TrustedProxies__$(jq -r '.index' <<< "$value")"
                deploy_up_stage_value "$target" "$render_root" "$destination" "$key" "$(jq -r '.proxy' <<< "$value")" || return 1
            done < <(jq -c '.trustedProxyAddresses | to_entries[] | {index:.key,proxy:.value}' <<< "$target")
        done
        for destination in "$config_root/initializer-secrets" "$config_root/runtime-secrets"; do
            deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$destination" \
              "$(jq -r '.deployment.deviceBootstrap.clientSecretReference' "$inventory")" DeviceBootstrap__CentralIdentity__ClientCredentials__ClientSecret || return 1
        done
        for destination in "$config_root/initializer-secrets" "$config_root/runtime-secrets"; do
            while IFS= read -r value; do
                key="DeviceBootstrap__CentralIdentity__ClientCredentials__Scopes__$(jq -r '.index' <<< "$value")"
                deploy_up_stage_value "$target" "$render_root" "$destination" "$key" "$(jq -r '.scope' <<< "$value")" || return 1
            done < <(jq -c '.deployment.deviceBootstrap.scopes | to_entries[] | {index:.key,scope:.value}' "$inventory")
        done
        value="$(deploy_secret_value "$(jq -r '.secretSource.path' "$inventory")" "$(jq -r '.deployment.services.redis.secretReference' "$inventory")")" || return 1
        deploy_up_stage_value "$target" "$render_root" "$config_root/runtime-secrets" Redis__Configuration \
          "$(jq -r '.deployment.services.redis.host' "$inventory"):$(jq -r '.deployment.services.redis.port' "$inventory"),user=$(jq -r '.deployment.services.redis.user' "$inventory"),password=$value" || return 1
        unset value
    elif [[ "$component" == cameraAgent ]]; then
        deploy_transport_copy_private_file "$(jq -r '.moduleConfigPath' <<< "$target")" "$ssh" "$config_root/camera-module.json" || return 1
        deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$config_root/private" \
          "$(jq -r '.ownerPasswordSecretReference' <<< "$target")" owner-password || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" LocalIdentity__AdminPasswordFile /run/hvo-private/owner-password || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" LocalIdentity__AdminEmail "$(jq -r '.ownerEmail' <<< "$target")" || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" CameraAgent__ConfigFilePath /app/cameraagent.deploy.json || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" CameraAgent__RawIngressRoot /app/data/raw || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" CameraAgent__ProvisioningStartupGate__Enabled true || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" CameraAgent__CaptureDistribution__UploadEnabled false || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" CameraAgent__CentralIntegration__Mode Enabled || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" SkyMonitor__BaseUrl "$(jq -r '.logicHost.publicEndpoint' "$inventory")" || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" Catalog__Root /app/catalog || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" Catalog__RequiredPackageKind "$(deploy_up_catalog_required_kind "$inventory")" || return 1
        deploy_up_stage_value "$target" "$render_root" "$secrets_root" ReverseProxy__Enabled "$(jq -r '(.trustedProxyAddresses | length) > 0' <<< "$target")" || return 1
        while IFS= read -r value; do
            key="ReverseProxy__TrustedProxies__$(jq -r '.index' <<< "$value")"
            deploy_up_stage_value "$target" "$render_root" "$secrets_root" "$key" "$(jq -r '.proxy' <<< "$value")" || return 1
        done < <(jq -c '.trustedProxyAddresses | to_entries[] | {index:.key,proxy:.value}' <<< "$target")
    fi
    [[ "$component" != shared ]] || { DEPLOY_UP_CONFIG_ROOT="$config_root"; DEPLOY_UP_STATE_ROOT="$state_root"; return 0; }
    port="$(jq -r '.internalEndpoint | capture("^http://[^/:]+:(?<port>[0-9]+)").port' <<< "$target")"; env_file="$render_root/$name.env"
    if [[ "$component" == logicHost ]]; then image_key=LOGICHOST_IMAGE; else image_key=CAMERAAGENT_IMAGE; fi
    (umask 077; printf 'HVO_CONFIG_ROOT=%s\nHVO_STATE_ROOT=%s\nHVO_CATALOG_ROOT=%s\nHVO_PUBLIC_PORT=%s\nHVO_CPUS=%s\nHVO_MEMORY=%s\nHVO_RUN_ID=%s\nHVO_INVENTORY_SHA256=%s\n%s=%s\n' \
      "$config_root" "$state_root" "$catalog_root" "$port" "$(jq -r '.deployment.limits.cpus // "1"' "$inventory")" \
      "$(jq -r '.deployment.limits.memory // "1G"' "$inventory")" "$run_id" "$(jq -S -c . "$inventory" | sha256sum | cut -d' ' -f1)" \
      "$image_key" "$image" > "$env_file")
    DEPLOY_UP_ENV_FILE="$env_file"; DEPLOY_UP_CONFIG_ROOT="$config_root"; DEPLOY_UP_STATE_ROOT="$state_root"
    deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
}

deploy_run_up() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6"
    local state_dir evidence_dir render_root images mode_services project now target agent name context ssh component image endpoint path shared_context shared_env shared_target minio_response minio_runtime_access minio_runtime_secret runtime_identity runtime_uid runtime_gid value root_access root_secret mc_config
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/prepare-manifest.json" up "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/catalog-manifest.json" up "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" "$worktree" || return 1
    deploy_up_validate_local_inputs "$inventory" || return 1
    deploy_up_validate_namespaces "$inventory" "$mode" "$run_id" || return 1
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"
    deploy_transport_reconcile_private_uploads strict || { deploy_fail up private-upload-registry cleanup-failed; return 1; }
    deploy_require_committed_images "$inventory" "$run_id" "$mode" "$hash" "$revision" || return 1
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"; render_root="$state_dir/up-rendered"
    install -d -m 700 "$render_root"
    images="$DEPLOY_COMMITTED_IMAGES_JSON"; mode_services="$(jq -r '.deployment.services.mode' "$inventory")"; project="$(jq -r '.deployment.resources.project' "$inventory")"
    DEPLOY_UP_MANIFEST="$state_dir/up-manifest.json"; DEPLOY_UP_LEDGER="$state_dir/up-ledger.json"; DEPLOY_UP_EVIDENCE="$evidence_dir/up.json"
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -f "$DEPLOY_UP_LEDGER" && ! -L "$DEPLOY_UP_LEDGER" ]]; then
        jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg services "$mode_services" --arg project "$project" --argjson inventory "$(jq -c . "$inventory")" '
          .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
          .servicesMode == $services and .project == $project and
          (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
          (.resources | type == "array" and length == ([.[].kind] | unique | length) and all(.[];
            (keys | sort) == (["kind","status"] | sort) and
            ((.kind == "shared-services" and .status == "started" and $services == "deploy") or
             (.kind == "existing-services" and .status == "validated" and $services == "existing") or
             (.kind == "logic-initializer" and .status == "completed") or
             (.kind == "runtime-role" and .status == "applied" and $services == "deploy")))) and
          (.targets | type == "array" and length == ([.[].target] | unique | length) and all(.[];
            ((.component == "logicHost" and (keys | sort) == (["component","status","target"] | sort)) or
             (.component == "cameraAgent" and (keys | sort) == (["component","provisioningGate","status","target","uploadEnabled"] | sort) and
               .provisioningGate == true and .uploadEnabled == false)) and .status == "ready" and
            ((.component == "logicHost" and .target == $inventory.logicHost.name) or
             (.component == "cameraAgent" and (.target as $target | [$inventory.cameraAgents[].name] | index($target) != null))))) and
          (if .phaseStatus == "passed" then
             ([.targets[].target] | sort) == ([$inventory.logicHost.name] + [$inventory.cameraAgents[].name] | sort) and
             ([.resources[].kind] | index("logic-initializer") != null) and
             (if $services == "deploy" then ([.resources[].kind] | index("shared-services") != null and index("runtime-role") != null)
              else ([.resources[].kind] | index("existing-services") != null) end)
           else true end)' "$DEPLOY_UP_LEDGER" >/dev/null 2>&1 ||
          { deploy_fail up ledger invalid; return 1; }
        [[ -f "$DEPLOY_UP_MANIFEST" && ! -L "$DEPLOY_UP_MANIFEST" && "$(stat -c '%u:%h:%a' "$DEPLOY_UP_MANIFEST" 2>/dev/null)" == "$(id -u):1:600" ]] ||
          { deploy_fail up manifest missing-or-unsafe; return 1; }
        if [[ "$(jq -r '.phaseStatus' "$DEPLOY_UP_LEDGER")" != running ]]; then
            [[ "$(jq -c . "$DEPLOY_UP_MANIFEST" 2>/dev/null)" == "$(jq -c . "$DEPLOY_UP_LEDGER")" && -f "$DEPLOY_UP_EVIDENCE" && ! -L "$DEPLOY_UP_EVIDENCE" &&
               "$(stat -c '%u:%h:%a' "$DEPLOY_UP_EVIDENCE" 2>/dev/null)" == "$(id -u):1:600" && "$(jq -c . "$DEPLOY_UP_EVIDENCE" 2>/dev/null)" == "$(jq -c . "$DEPLOY_UP_LEDGER")" ]] ||
              { deploy_fail up resume committed-state-mismatch; return 1; }
        fi
        DEPLOY_UP_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' "$DEPLOY_UP_LEDGER")"
    else
        DEPLOY_UP_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg services "$mode_services" --arg project "$project" --arg now "$now" \
          '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,servicesMode:$services,project:$project,
            phaseStatus:"running",startedAt:$now,updatedAt:$now,resources:[],targets:[]}')"
    fi
    deploy_publish_json "$DEPLOY_UP_LEDGER" "$DEPLOY_UP_JSON"; deploy_publish_json "$DEPLOY_UP_MANIFEST" "$DEPLOY_UP_JSON"
    if [[ "$mode_services" == deploy ]]; then
        target="$(jq -c '.sharedServices' "$inventory")"; shared_target="$target"; name="$(jq -r '.name' <<< "$target")"; context="$(jq -r '.dockerContext' <<< "$target")"; ssh="$(jq -r '.sshHost' <<< "$target")"
        deploy_up_stage_target "$inventory" "$target" "$run_id" "$render_root" "" shared || return 1
        deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/secrets" \
          "$(jq -r '.deployment.services.sql.adminSecretReference' "$inventory")" SQLSERVER_PASSWORD || { deploy_fail up services secret-stage-failed; return 1; }
        deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/secrets" \
          "$(jq -r '.deployment.services.redis.adminSecretReference' "$inventory")" REDIS_PASSWORD || { deploy_fail up services secret-stage-failed; return 1; }
        deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/secrets" \
          "$(jq -r '.deployment.services.minio.rootAccessKeyReference' "$inventory")" MINIO_ACCESS_KEY || { deploy_fail up services secret-stage-failed; return 1; }
        deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/secrets" \
          "$(jq -r '.deployment.services.minio.rootSecretKeyReference' "$inventory")" MINIO_SECRET_KEY || { deploy_fail up services secret-stage-failed; return 1; }
        deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/secrets" \
          "$(jq -r '.deployment.services.sql.initializerSecretReference' "$inventory")" SQL_INITIALIZER_PASSWORD || { deploy_fail up services secret-stage-failed; return 1; }
        deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/secrets" \
          "$(jq -r '.deployment.services.sql.runtimeSecretReference' "$inventory")" SQL_RUNTIME_PASSWORD || { deploy_fail up services secret-stage-failed; return 1; }
        deploy_up_stage_named_secret "$inventory" "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/secrets" \
          "$(jq -r '.deployment.services.redis.secretReference' "$inventory")" REDIS_RUNTIME_PASSWORD || { deploy_fail up services secret-stage-failed; return 1; }
        value="$(deploy_secret_value "$(jq -r '.secretSource.path' "$inventory")" "$(jq -r '.deployment.services.redis.adminSecretReference' "$inventory")")" || return 1
        value="${value//\\/\\\\}"; value="${value//\"/\\\"}"
        deploy_up_stage_value "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/private" redis.conf $'appendonly yes\n'"requirepass \"$value\""$'\n' || return 1
        unset value
        deploy_transport_copy_private_file "$REPO_ROOT/deploy/split-host/provision-sql.sh" "$ssh" "$DEPLOY_UP_CONFIG_ROOT/sql/provision.sh" || return 1
        deploy_transport_copy_private_file "$REPO_ROOT/deploy/sql/logichost-migration-role.sql" "$ssh" "$DEPLOY_UP_CONFIG_ROOT/sql/migration-role.sql" || return 1
        deploy_transport_copy_private_file "$REPO_ROOT/deploy/sql/logichost-runtime-role.sql" "$ssh" "$DEPLOY_UP_CONFIG_ROOT/sql/runtime-role.sql" || return 1
        deploy_transport_copy_private_file "$REPO_ROOT/deploy/split-host/provision-minio.sh" "$ssh" "$DEPLOY_UP_CONFIG_ROOT/minio/provision.sh" || return 1
        deploy_transport_copy_private_file "$REPO_ROOT/deploy/split-host/minio-policy.template.json" "$ssh" "$DEPLOY_UP_CONFIG_ROOT/minio/policy.template.json" || return 1
        root_access="$(deploy_secret_value "$(jq -r '.secretSource.path' "$inventory")" "$(jq -r '.deployment.services.minio.rootAccessKeyReference' "$inventory")")" || return 1
        root_secret="$(deploy_secret_value "$(jq -r '.secretSource.path' "$inventory")" "$(jq -r '.deployment.services.minio.rootSecretKeyReference' "$inventory")")" || { unset root_access; return 1; }
        mc_config="$(jq -cn --arg access "$root_access" --arg secret "$root_secret" \
          '{version:"10",aliases:{local:{url:"http://minio:9000",accessKey:$access,secretKey:$secret,api:"S3v4",path:"auto"}}}')" || { unset root_access root_secret; return 1; }
        unset root_access root_secret
        deploy_up_stage_value "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/private/mc" config.json "$mc_config" || { unset mc_config; return 1; }
        unset mc_config
        runtime_identity="$(deploy_transport_owner_identity "$ssh" "$(jq -r '.runtimeOwner' <<< "$target")")" || { deploy_fail up services runtime-owner-identity-failed; return 1; }
        IFS=$'\t' read -r runtime_uid runtime_gid <<< "$runtime_identity"
        [[ "$runtime_uid" =~ ^[0-9]+$ && "$runtime_gid" =~ ^[0-9]+$ ]] || { deploy_fail up services runtime-owner-identity-invalid; return 1; }
        deploy_transport_validate_private_file_identity "$ssh" "$DEPLOY_UP_CONFIG_ROOT/private/mc/config.json" "$runtime_uid" "$runtime_gid" ||
          { deploy_fail up services minio-client-config-ownership-invalid; return 1; }
        shared_env="$render_root/shared.env"
        (umask 077; jq -r --arg config "$DEPLOY_UP_CONFIG_ROOT" --arg state "$DEPLOY_UP_STATE_ROOT" '
          ["HVO_CONFIG_ROOT="+$config,"HVO_STATE_ROOT="+$state,
           "SQLSERVER_PORT="+(.deployment.services.sql.port|tostring),"REDIS_PORT="+(.deployment.services.redis.port|tostring),
           "MINIO_PORT="+(.deployment.services.minio.port|tostring),"SMTP_PORT="+(.deployment.services.smtp.port|tostring),
           "SQLSERVER_IMAGE="+.deployment.services.images.sqlServer,"REDIS_IMAGE="+.deployment.services.images.redis,
            "MINIO_IMAGE="+.deployment.services.images.minio,"MINIO_CLIENT_IMAGE="+.deployment.services.images.minioClient,"MAILPIT_IMAGE="+(.deployment.services.images.mailpit // "unused"),
            "SQL_DATABASE="+.deployment.services.sql.database,"SQL_ADMIN_USER="+.deployment.services.sql.adminUser,
            "SQL_INITIALIZER_USER="+.deployment.services.sql.initializerUser,"SQL_RUNTIME_USER="+.deployment.services.sql.runtimeUser,
            "REDIS_RUNTIME_USER="+.deployment.services.redis.user,"REDIS_PREFIX="+.deployment.services.redis.prefix,
             "MINIO_ARTIFACT_BUCKET="+.deployment.services.minio.artifactBucket,"MINIO_DIAGNOSTICS_BUCKET="+.deployment.services.minio.diagnosticsBucket,
              "HVO_CPUS="+(.deployment.limits.cpus // "2"),"HVO_MEMORY="+(.deployment.limits.memory // "2G")][]' "$inventory" > "$shared_env")
        printf 'HVO_RUNTIME_UID=%s\nHVO_RUNTIME_GID=%s\nHVO_RUN_ID=%s\nHVO_INVENTORY_SHA256=%s\n' \
          "$runtime_uid" "$runtime_gid" "$run_id" "$hash" >> "$shared_env"
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        if [[ "$(jq -r '.deployment.services.smtp.kind' "$inventory")" == mailpit ]]; then
            deploy_up_compose_mutation "$target" "$context" "$project-services" "$shared_env" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" --profile test-smtp up -d --wait || return 1
        else
            deploy_up_compose_mutation "$target" "$context" "$project-services" "$shared_env" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" up -d --wait sqlserver redis minio || return 1
        fi
        deploy_up_compose_mutation "$target" "$context" "$project-services" "$shared_env" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" --profile provision run --rm -e SQL_PROVISION_PHASE=before sql-provision || { deploy_fail up services sql-provision-failed; return 1; }
        deploy_up_compose_mutation "$target" "$context" "$project-services" "$shared_env" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" --profile provision run --rm redis-provision || { deploy_fail up services redis-provision-failed; return 1; }
        deploy_up_compose_mutation "$target" "$context" "$project-services" "$shared_env" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" --profile provision run --rm minio-provision || { deploy_fail up services minio-provision-failed; return 1; }
        minio_response="$render_root/minio-runtime.json"
        deploy_transport_fetch_private_file "$ssh" "$DEPLOY_UP_CONFIG_ROOT/minio-output/minio-runtime.json" "$minio_response" "$runtime_uid" "$runtime_gid" || { deploy_fail up services minio-credential-fetch-failed; return 1; }
        if ! minio_runtime_access="$(jq -er '.accessKey | strings | select(test("^[A-Za-z0-9][A-Za-z0-9._-]{2,127}$"))' "$minio_response" 2>/dev/null)" ||
          ! minio_runtime_secret="$(jq -er '.secretKey | strings | select(length >= 8 and length <= 256 and (test("[[:cntrl:]]") | not))' "$minio_response" 2>/dev/null)"; then
            rm -f -- "$minio_response"; deploy_fail up services minio-credential-response-invalid; return 1
        fi
        rm -f -- "$minio_response"
        shared_context="$context"
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        DEPLOY_UP_JSON="$(jq -c '.resources = ([.resources[] | select(.kind != "shared-services")] + [{kind:"shared-services",status:"started"}])' <<< "$DEPLOY_UP_JSON")"; deploy_publish_json "$DEPLOY_UP_LEDGER" "$DEPLOY_UP_JSON"
    else
        while IFS= read -r endpoint; do
            target="$(jq -c '.logicHost' "$inventory")"; ssh="$(jq -r '.sshHost' <<< "$target")"
            [[ "$(deploy_transport_ssh_tcp "$ssh" "$(jq -r '.host' <<< "$endpoint")" "$(jq -r '.port' <<< "$endpoint")")" == reachable ]] ||
              { deploy_fail up existing-service unreachable; return 1; }
        done < <(jq -c '.deployment.services | [.sql,.redis,.minio,.smtp][]' "$inventory")
        DEPLOY_UP_JSON="$(jq -c '.resources = ([.resources[] | select(.kind != "existing-services")] + [{kind:"existing-services",status:"validated"}])' <<< "$DEPLOY_UP_JSON")"
    fi
    target="$(jq -c '.logicHost' "$inventory")"; name="$(jq -r '.name' <<< "$target")"; context="$(jq -r '.dockerContext' <<< "$target")"; ssh="$(jq -r '.sshHost' <<< "$target")"
    image="$(jq -r '.images[] | select(.component == "logicHost") | .reference' <<< "$images")"
    deploy_up_stage_target "$inventory" "$target" "$run_id" "$render_root" "$image" logicHost || return 1
    if [[ "$mode_services" == deploy ]]; then
        deploy_up_stage_value "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/runtime-secrets" Minio__AccessKey "$minio_runtime_access" || return 1
        deploy_up_stage_value "$target" "$render_root" "$DEPLOY_UP_CONFIG_ROOT/runtime-secrets" Minio__SecretKey "$minio_runtime_secret" || return 1
        unset minio_runtime_access minio_runtime_secret
    fi
    deploy_up_compose_mutation "$target" "$context" "$project-logic" "$DEPLOY_UP_ENV_FILE" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" --profile initialize run --rm logic-init || { deploy_fail up logic initializer-failed; return 1; }
    DEPLOY_UP_JSON="$(jq -c '.resources = ([.resources[] | select(.kind != "logic-initializer")] + [{kind:"logic-initializer",status:"completed"}])' <<< "$DEPLOY_UP_JSON")"; deploy_publish_json "$DEPLOY_UP_LEDGER" "$DEPLOY_UP_JSON"
    if [[ "$mode_services" == deploy ]]; then
        deploy_up_compose_mutation "$shared_target" "$shared_context" "$project-services" "$shared_env" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" --profile provision run --rm -e SQL_PROVISION_PHASE=after sql-provision || { deploy_fail up services runtime-role-failed; return 1; }
        DEPLOY_UP_JSON="$(jq -c '.resources = ([.resources[] | select(.kind != "runtime-role")] + [{kind:"runtime-role",status:"applied"}])' <<< "$DEPLOY_UP_JSON")"; deploy_publish_json "$DEPLOY_UP_LEDGER" "$DEPLOY_UP_JSON"
    fi
    deploy_up_compose_mutation "$target" "$context" "$project-logic" "$DEPLOY_UP_ENV_FILE" "$REPO_ROOT/deploy/split-host/compose.logichost.yml" up -d logichost || return 1
    endpoint="$(jq -r '.internalEndpoint' <<< "$target")"
    for path in /alive /health /metrics; do deploy_transport_http_ready "$ssh" "${endpoint%/}$path" || { deploy_fail up logic readiness-failed; return 1; }; done
    endpoint="$(jq -r '.logicHost.publicEndpoint' "$inventory")"
    [[ "$mode" == isolated || "$endpoint" == https://* ]] || { deploy_fail up logic public-authority-https-required; return 1; }
    while IFS= read -r agent; do
        deploy_transport_http_ready "$(jq -r '.sshHost' <<< "$agent")" "${endpoint%/}/.well-known/openid-configuration" ||
          { deploy_fail up logic public-authority-readiness-failed; return 1; }
    done < <(jq -c '.cameraAgents[]' "$inventory")
    DEPLOY_UP_JSON="$(jq -c --arg target "$name" '.targets = ([.targets[] | select(.target != $target)] + [{target:$target,component:"logicHost",status:"ready"}])' <<< "$DEPLOY_UP_JSON")"; deploy_publish_json "$DEPLOY_UP_LEDGER" "$DEPLOY_UP_JSON"
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; context="$(jq -r '.dockerContext' <<< "$target")"; ssh="$(jq -r '.sshHost' <<< "$target")"
        image="$(jq -r '.images[] | select(.component == "cameraAgent") | .reference' <<< "$images")"
        deploy_up_stage_target "$inventory" "$target" "$run_id" "$render_root" "$image" cameraAgent || return 1
        deploy_up_compose_mutation "$target" "$context" "$project-$name" "$DEPLOY_UP_ENV_FILE" "$REPO_ROOT/deploy/split-host/compose.cameraagent.yml" up -d cameraagent || return 1
        endpoint="$(jq -r '.internalEndpoint' <<< "$target")"; deploy_transport_http_ready "$ssh" "${endpoint%/}/alive" || return 1
        deploy_transport_http_ready "$ssh" "${endpoint%/}/health" || return 1
        DEPLOY_UP_JSON="$(jq -c --arg target "$name" '.targets = ([.targets[] | select(.target != $target)] + [{target:$target,component:"cameraAgent",provisioningGate:true,uploadEnabled:false,status:"ready"}])' <<< "$DEPLOY_UP_JSON")"; deploy_publish_json "$DEPLOY_UP_LEDGER" "$DEPLOY_UP_JSON"
    done < <(jq -c '.cameraAgents[]' "$inventory")
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; DEPLOY_UP_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_UP_JSON")"
    deploy_publish_json "$DEPLOY_UP_LEDGER" "$DEPLOY_UP_JSON" && deploy_publish_json "$DEPLOY_UP_EVIDENCE" "$DEPLOY_UP_JSON" && deploy_publish_json "$DEPLOY_UP_MANIFEST" "$DEPLOY_UP_JSON"
}
