#!/usr/bin/env bash
# Deployment contract shard body: deploy-services. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

if [[ "$SELECTED_SHARD" == deploy-services ]]; then prepare_lifecycle_fixture; fi
# Deploy-mode services use the declared shared SSH/Docker target and preserve partial progress on failure.
deploy_services=deploy-services
jq --arg root "$TEMP_DIR/remote/$deploy_services-skymonitor" --arg endpoint "http://127.0.0.1:$HTTP_PORT" --arg owner "$(id -un)" --argjson port "$HTTP_PORT" '
  .productRoot=$root | .catalogs |= map(.installRoot=($root+"/catalogs/"+.catalogId)) |
  .deployment.services.mode="deploy" | .deployment.services.smtp.kind="mailpit" |
  .deployment.transient.mode="Hybrid" |
  .deployment.services.sql.port=1433 | .deployment.services.redis.port=6379 | .deployment.services.minio.port=9000 | .deployment.services.smtp.ports=[2525,8025] |
  .deployment.services.images={sqlServer:("registry.example/sql@sha256:"+("1"*64)),redis:("registry.example/redis@sha256:"+("2"*64)),
    minio:("registry.example/minio@sha256:"+("3"*64)),minioClient:("registry.example/mc@sha256:"+("4"*64)),mailpit:("registry.example/mailpit@sha256:"+("5"*64))} |
  .deployment.services.sql.database="hvo-deploy-services" | .deployment.resources.sqlDatabase="hvo-deploy-services" |
  .deployment.services.redis.prefix="hvo-deploy-services:" | .deployment.resources.redisPrefix="hvo-deploy-services:" |
  .deployment.services.minio.artifactBucket="hvo-deploy-services-artifacts" | .deployment.resources.artifactBucket="hvo-deploy-services-artifacts" |
  .deployment.services.minio.diagnosticsBucket="hvo-deploy-services-diagnostics" | .deployment.resources.diagnosticsBucket="hvo-deploy-services-diagnostics" |
  .deployment.resources.project="hvo-deploy-services" |
  .deployment.limits.sqlMemory="4G" | .deployment.limits.sqlMemoryLimitMb=3072 |
  .sharedServices={name:"shared",sshHost:"shared@example",dockerContext:"shared-context",expectedArchitecture:"amd64",expectedHostName:"shared-node",
    expectedHostIdentity:"shared-machine",expectedDockerDaemonIdentity:"shared-daemon",runtimeRoot:($root+"/shared-services"),runtimeOwner:$owner,ports:[1433,6379,9000,2525,8025]} |
  .logicHost.runtimeRoot=($root+"/logichosts/"+.logicHost.instanceId) | .logicHost.internalEndpoint=$endpoint | .logicHost.ports=[$port] |
  (.cameraAgents[0].runtimeRoot)=($root+"/cameraagents/"+.cameraAgents[0].instanceId) | (.cameraAgents[0].internalEndpoint)=$endpoint | (.cameraAgents[0].ports)=[$port] |
  (.cameraAgents[1].runtimeRoot)=($root+"/cameraagents/"+.cameraAgents[1].instanceId) | (.cameraAgents[1].internalEndpoint)=$endpoint | (.cameraAgents[1].ports)=[$port]' \
  "$INVENTORY" > "$INVENTORY.changed" && mv "$INVENTORY.changed" "$INVENTORY"
mkdir -m 700 "$TEMP_DIR/remote/$deploy_services-skymonitor" "$TEMP_DIR/remote/$deploy_services-skymonitor/logichosts" \
  "$TEMP_DIR/remote/$deploy_services-skymonitor/cameraagents" "$TEMP_DIR/remote/$deploy_services-skymonitor/catalogs"
run_deploy_mode preflight "$deploy_services" isolated >/dev/null
run_deploy_mode prepare "$deploy_services" isolated >/dev/null
logic_runtime_root="$(inventory_runtime_root logic)"
east_runtime_root="$(inventory_runtime_root east)"
west_runtime_root="$(inventory_runtime_root west)"
shared_runtime_root="$(inventory_runtime_root shared)"
logic_project="hvo-deploy-services-11111111111141118111111111111111"
east_project="hvo-deploy-services-22222222222242228222222222222222"
west_project="hvo-deploy-services-33333333333343338333333333333333"
logic_prepare_lock="${logic_runtime_root%/*}/.hvo-deploy-prepare-$(printf 'v1\ntarget=logic\nroot=%s\n' "$logic_runtime_root" | sha256sum | cut -c1-32).lock"
east_prepare_lock="${east_runtime_root%/*}/.hvo-deploy-prepare-$(printf 'v1\ntarget=east\nroot=%s\n' "$east_runtime_root" | sha256sum | cut -c1-32).lock"
west_prepare_lock="${west_runtime_root%/*}/.hvo-deploy-prepare-$(printf 'v1\ntarget=west\nroot=%s\n' "$west_runtime_root" | sha256sum | cut -c1-32).lock"
shared_prepare_lock="${shared_runtime_root%/*}/.hvo-deploy-prepare-$(printf 'v1\ntarget=shared\nroot=%s\n' "$shared_runtime_root" | sha256sum | cut -c1-32).lock"
for prepare_lock in "$logic_prepare_lock" "$east_prepare_lock" "$west_prepare_lock" "$shared_prepare_lock"; do
  test -f "$prepare_lock" && test -f "$prepare_lock.state" || fail "Prepare did not create exact lock provenance: $prepare_lock"
done
rm -f "$FAKE_IMAGE_STATE"/*.pushed "$FAKE_IMAGE_STATE"/*.platforms "$FAKE_IMAGE_STATE"/*.labels "$FAKE_IMAGE_STATE"/*.reference "$FAKE_IMAGE_STATE"/*.config
run_deploy_mode images "$deploy_services" isolated >/dev/null
if FAKE_SCP_FAIL_DEST="33333333-3333-4333-8333-333333333333" run_deploy_mode catalog "$deploy_services" isolated > "$TEMP_DIR/catalog-failure.log" 2>&1; then fail 'Catalog partial failure passed.'; fi
jq -e '.phaseStatus == "failed" and ([.targets[].target] | sort) == ["east","logic"]' "$TEMP_DIR/output/$deploy_services-state/catalog-ledger.json" >/dev/null
jq -e '.phaseStatus == "failed"' "$TEMP_DIR/output/$deploy_services-evidence/catalog.json" >/dev/null
run_deploy_mode catalog "$deploy_services" isolated >/dev/null
export FAKE_RUNTIME_UID_HOST=shared FAKE_RUNTIME_UID=4242 FAKE_RUNTIME_GID=4343
if FAKE_COMPOSE_FAIL_MATCH="hvo-deploy-services-22222222222242228222222222222222" run_deploy_mode up "$deploy_services" isolated > "$TEMP_DIR/up-failure.log" 2>&1; then fail 'Up partial failure passed.'; fi
jq -e '.phaseStatus == "failed" and ([.targets[].target] | sort) == ["logic"] and
  ([.resources[].kind] | sort) == ["logic-initializer","runtime-role","shared-services"]' "$TEMP_DIR/output/$deploy_services-state/up-ledger.json" >/dev/null
jq -e '.phaseStatus == "failed"' "$TEMP_DIR/output/$deploy_services-evidence/up.json" >/dev/null
run_deploy_mode up "$deploy_services" isolated >/dev/null
jq -e '.phaseStatus == "passed" and ([.resources[].kind] | sort) == ["logic-initializer","runtime-role","shared-services"] and (.targets | length) == 3' \
  "$TEMP_DIR/output/$deploy_services-state/up-ledger.json" >/dev/null
object_storage_endpoint="$(jq -r '.deployment.services.minio.host + ":" + (.deployment.services.minio.port | tostring)' "$INVENTORY")"
object_storage_tls="$(jq -r '.deployment.services.minio.useSsl' "$INVENTORY")"
for object_storage_config in "$logic_runtime_root/config/initializer-secrets" "$logic_runtime_root/config/runtime-secrets"; do
  test "$(<"$object_storage_config/ObjectStorage__ServiceEndpoint")" = "$object_storage_endpoint"
  test "$(<"$object_storage_config/ObjectStorage__Region")" = us-east-1
  test "$(<"$object_storage_config/ObjectStorage__UseTls")" = "$object_storage_tls"
  test "$(<"$object_storage_config/ObjectStorage__AddressingStyle")" = Path
  test "$(<"$object_storage_config/ObjectStorage__CredentialMode")" = Static
  test "$(<"$object_storage_config/ObjectStorage__ArtifactBucket")" = hvo-deploy-services-artifacts
  test "$(<"$object_storage_config/ObjectStorage__DiagnosticsBucket")" = hvo-deploy-services-diagnostics
  test ! -e "$object_storage_config/ObjectStorage__SessionToken"
done
test ! -e "$logic_runtime_root/config/initializer-secrets/ObjectStorage__AccessKey"
test ! -e "$logic_runtime_root/config/initializer-secrets/ObjectStorage__SecretKey"
test ! -e "$logic_runtime_root/config/initializer-secrets/ObjectStorage__SessionToken"
test "$(<"$logic_runtime_root/config/runtime-secrets/ObjectStorage__AccessKey")" = generated-minio-access
test "$(<"$logic_runtime_root/config/runtime-secrets/ObjectStorage__SecretKey")" = 'generated:/@"minio-secret-never-print'
test "$(<"$logic_runtime_root/config/runtime-secrets/CentralTransient__Mode")" = Off
test "$(<"$logic_runtime_root/config/runtime-secrets/TransientPayloadRelease__Enabled")" = false
test "$(<"$east_runtime_root/config/secrets/CameraAgent__TransientDetection__Mode")" = Off
shared_render_env="$TEMP_DIR/output/$deploy_services-state/up-rendered/shared.env"
grep -Fxq 'HVO_RUNTIME_UID=4242' "$shared_render_env"
grep -Fxq 'HVO_RUNTIME_GID=4343' "$shared_render_env"
grep -Fxq "HVO_CONFIG_ROOT=$shared_runtime_root/config" "$shared_render_env"
grep -Fxq "HVO_STATE_ROOT=$shared_runtime_root/state" "$shared_render_env"
grep -Fxq 'MAILPIT_HTTP_PORT=8025' "$shared_render_env"
grep -Fxq 'HVO_MEMORY=2G' "$shared_render_env"
grep -Fxq 'HVO_SQL_MEMORY=4G' "$shared_render_env"
grep -Fxq 'MSSQL_MEMORY_LIMIT_MB=3072' "$shared_render_env"
helper_env_contract="$TEMP_DIR/helper-image.env"
cp "$shared_render_env" "$helper_env_contract"; chmod 600 "$helper_env_contract"
(
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  test "$(deploy_transport_helper_image "$helper_env_contract" REDIS_IMAGE)" = "registry.example/redis@sha256:$(printf '2%.0s' {1..64})"
)
archive_helper_id="sha256:$(printf '7%.0s' {1..64})"
printf 'REDIS_IMAGE=%s\n' "$archive_helper_id" > "$helper_env_contract"; chmod 600 "$helper_env_contract"
(source "$REPO_ROOT/scripts/deploy/transport.sh"; test "$(deploy_transport_helper_image "$helper_env_contract" REDIS_IMAGE)" = "$archive_helper_id")
repeated_separator_helper="foo--bar/image__part..v1@sha256:$(printf '6%.0s' {1..64})"
printf 'REDIS_IMAGE=%s\n' "$repeated_separator_helper" > "$helper_env_contract"; chmod 600 "$helper_env_contract"
(source "$REPO_ROOT/scripts/deploy/transport.sh"; test "$(deploy_transport_helper_image "$helper_env_contract" REDIS_IMAGE)" = "$repeated_separator_helper")
chmod 640 "$helper_env_contract"
if (source "$REPO_ROOT/scripts/deploy/transport.sh"; deploy_transport_helper_image "$helper_env_contract" REDIS_IMAGE >/dev/null); then fail 'Down accepted a non-owner-only helper env file.'; fi
chmod 600 "$helper_env_contract"; printf 'REDIS_IMAGE=registry.example/other@sha256:%s\nREDIS_IMAGE=sha256:%s\n' "$(printf '9%.0s' {1..64})" "$(printf '8%.0s' {1..64})" > "$helper_env_contract"
if (source "$REPO_ROOT/scripts/deploy/transport.sh"; deploy_transport_helper_image "$helper_env_contract" REDIS_IMAGE >/dev/null); then fail 'Down accepted duplicate helper image assignments.'; fi
for invalid_helper_ref in registry.example/redis:latest ' sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' \
  sha256:abc '--pull=always' registry.example/redis@sha256:ABCDEF \
  "foo-/image@sha256:$(printf 'a%.0s' {1..64})" "foo/image-@sha256:$(printf 'a%.0s' {1..64})" \
  "foo//image@sha256:$(printf 'a%.0s' {1..64})" "foo/./image@sha256:$(printf 'a%.0s' {1..64})" \
  "foo/../image@sha256:$(printf 'a%.0s' {1..64})" "foo:5000/image@sha256:$(printf 'a%.0s' {1..64})" \
  "Foo/image@sha256:$(printf 'a%.0s' {1..64})" "foo/_image@sha256:$(printf 'a%.0s' {1..64})" \
  "foo/image_@sha256:$(printf 'a%.0s' {1..64})" "foo/bar-/image@sha256:$(printf 'a%.0s' {1..64})" \
  "foo/_bar/image@sha256:$(printf 'a%.0s' {1..64})" "foo@sha256:$(printf 'a%.0s' {1..64})" \
  "foo/image@extra@sha256:$(printf 'a%.0s' {1..64})"; do
  printf 'REDIS_IMAGE=%s\n' "$invalid_helper_ref" > "$helper_env_contract"; chmod 600 "$helper_env_contract"
  if (source "$REPO_ROOT/scripts/deploy/transport.sh"; deploy_transport_helper_image "$helper_env_contract" REDIS_IMAGE >/dev/null); then
    fail "Down accepted invalid helper image reference: $invalid_helper_ref"
  fi
done
absent_helper_cid="$(printf 'a%.0s' {1..64})"
(
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  if PATH="$BIN:$PATH" deploy_transport_docker_inspect_json shared-context container "$absent_helper_cid" '{{json .Id}}' >/dev/null; then exit 92; else status=$?; fi
  [[ "$status" == 44 ]]
  for inspect_error in near-miss authorization; do
    if PATH="$BIN:$PATH" FAKE_DOCKER_CONTAINER_INSPECT_ERROR="$inspect_error" \
      deploy_transport_docker_inspect_json shared-context container "$absent_helper_cid" '{{json .Id}}' >/dev/null; then exit 93; else status=$?; fi
    [[ "$status" == 1 ]] || exit 94
  done
)
for service in sql redis minio; do
  test -d "$shared_runtime_root/state/$service"
  jq -e --arg device "$shared_runtime_root/state/$service" '.device == $device' \
    "$FAKE_IMAGE_STATE/volume-shared-context-hvo-deploy-services-services_$service-data" >/dev/null
done
test "$(grep -Fc 'user: "${HVO_RUNTIME_UID:?runtime uid required}:${HVO_RUNTIME_GID:?runtime gid required}"' \
  "$REPO_ROOT/deploy/split-host/compose.shared-services.yml")" -eq 6
sqlserver_block="$(sed -n '/^  sqlserver:/,/^  sql-provision:/p' "$REPO_ROOT/deploy/split-host/compose.shared-services.yml")"
grep -Fq 'user: "${HVO_RUNTIME_UID:?runtime uid required}:${HVO_RUNTIME_GID:?runtime gid required}"' <<< "$sqlserver_block"
grep -Fq 'HOME: /var/opt/mssql' <<< "$sqlserver_block"
grep -Fq 'MSSQL_MEMORY_LIMIT_MB: ${MSSQL_MEMORY_LIMIT_MB:?SQL memory ceiling required}' <<< "$sqlserver_block"
grep -Fq 'mem_limit: ${HVO_SQL_MEMORY:?SQL container memory required}' <<< "$sqlserver_block"
grep -Fq 'dir /data' "$shared_runtime_root/config/private/redis.conf"
for service in sql redis minio; do
  test -f "$shared_runtime_root/state/$service/.hvo-seed"
done
test "$(grep -Fc 'o: bind' "$REPO_ROOT/deploy/split-host/compose.shared-services.yml")" -eq 3
grep -Fq '127.0.0.1:${MAILPIT_HTTP_PORT:?Mailpit HTTP port required}:8025' "$REPO_ROOT/deploy/split-host/compose.shared-services.yml"
for service in sql redis minio; do
  grep -Fq 'device: ${HVO_STATE_ROOT:?state root required}/'"$service" "$REPO_ROOT/deploy/split-host/compose.shared-services.yml"
done
normalize_fixture="$TEMP_DIR/bootstrap-pascal-response.json"
printf '%s\n' '{"RegistrationId":"00000000-0000-0000-0000-000000000001","Status":1,"CaptureControl":{"Value":{"State":"Paused","Version":2}},"FleetAgentInstanceId":"instance-1","MaximumHeartbeatSequence":4,"CentralArtifactCount":5,"CurrentRigProfileVersion":1,"CurrentRigProfileHash":"ABC123","LastHeartbeatReceivedAtUtc":"2026-08-15T00:00:00Z","CaptureWindow":[{"CaptureSequence":4,"CaptureId":"00000000-0000-0000-0000-000000000004","Artifacts":[{"ArtifactId":"10000000-0000-0000-0000-000000000004","Role":"Raw","ChecksumSha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","ByteLength":6144,"ObjectState":"Available","ObjectVerifiedAtUtc":"2026-08-15T00:00:00Z","Sources":[],"CompletedDerivativeCount":1}]}],"Metadata":{"CaseSensitiveKey":"value","Status":"opaque"}}' > "$normalize_fixture"
chmod 600 "$normalize_fixture"
(source "$REPO_ROOT/scripts/deploy/bootstrap.sh"; deploy_bootstrap_normalize_response "$normalize_fixture")
jq -e '.registrationId == "00000000-0000-0000-0000-000000000001" and .status == "Active" and
  .captureControl.value.state == "Paused" and .captureControl.value.version == 2 and
  .fleetAgentInstanceId == "instance-1" and .maximumHeartbeatSequence == 4 and .centralArtifactCount == 5 and
  .currentRigProfileVersion == 1 and .currentRigProfileHash == "ABC123" and
  .lastHeartbeatReceivedAtUtc == "2026-08-15T00:00:00Z" and
  .captureWindow[0].captureSequence == 4 and .captureWindow[0].artifacts[0].role == "Raw" and
  .captureWindow[0].artifacts[0].sources == [] and
  .Metadata.CaseSensitiveKey == "value" and .Metadata.Status == "opaque" and (.Metadata | has("status") | not)' \
  "$normalize_fixture" >/dev/null
printf 'hvo_capture_total 1\n' > "$normalize_fixture"
(source "$REPO_ROOT/scripts/deploy/bootstrap.sh"; deploy_bootstrap_normalize_response "$normalize_fixture")
grep -Fxq 'hvo_capture_total 1' "$normalize_fixture"
grep -Fq $'SQL_PROVISION_PHASE=after\tsql-provision' "$LOG"
runtime_login_line="$(grep -nF "IF SUSER_ID(N'\$SQL_RUNTIME_USER')" "$REPO_ROOT/deploy/split-host/provision-sql.sh" | cut -d: -f1)"
batch_boundary_line="$(grep -nFx 'GO' "$REPO_ROOT/deploy/split-host/provision-sql.sh" | cut -d: -f1)"
database_use_line="$(grep -nF 'USE [$SQL_DATABASE];' "$REPO_ROOT/deploy/split-host/provision-sql.sh" | cut -d: -f1)"
test "$runtime_login_line" -lt "$batch_boundary_line" && test "$batch_boundary_line" -lt "$database_use_line"
grep -Fq $'ssh\t-o\tBatchMode=yes\t-o\tConnectTimeout=8\t--\tshared@example' "$LOG"
grep -Fq $'docker\t--context\tshared-context\tcompose' "$LOG"
if grep -Fq '+@all' "$REPO_ROOT/deploy/split-host/compose.shared-services.yml"; then fail 'Redis deploy ACL grants all commands.'; fi
if grep -Eq 'sqlcmd[^\n]*[[:space:]]-P|redis-cli[^\n]*[[:space:]]-a|mc alias set|MC_HOST_' \
  "$REPO_ROOT/deploy/split-host/compose.shared-services.yml" "$REPO_ROOT/deploy/split-host/provision-sql.sh" "$REPO_ROOT/deploy/split-host/provision-minio.sh"; then
  fail 'Provisioning places a credential in process arguments.'
fi
grep -Fq 'cp /run/hvo-mc/config.json "$config_dir/config.json"' "$REPO_ROOT/deploy/split-host/provision-minio.sh"
grep -Fq 'account=$(cat /run/hvo-mc/account)' "$REPO_ROOT/deploy/split-host/provision-minio.sh"
grep -Fq 'add local "$account" --policy "$policy"' "$REPO_ROOT/deploy/split-host/provision-minio.sh"
if grep -Fq -- '--config-dir /run/hvo-mc' "$REPO_ROOT/deploy/split-host/provision-minio.sh"; then fail 'MinIO provisioning writes auxiliary state to its read-only credential mount.'; fi
if grep -Fq '${policy_content//' "$REPO_ROOT/deploy/split-host/provision-minio.sh"; then fail 'MinIO provisioning uses non-POSIX shell substitution.'; fi
grep -Fq 'status=$?' "$REPO_ROOT/deploy/split-host/provision-minio.sh"
grep -Fq 'exit "$status"' "$REPO_ROOT/deploy/split-host/provision-minio.sh"
if grep -Fq 'sed ' "$REPO_ROOT/deploy/split-host/provision-minio.sh"; then fail 'MinIO provisioning depends on a tool absent from the pinned client image.'; fi
if grep -Eq 'fixture-secret-never-print|minio-root-never-print|minio-secret-never-print|east-owner-never-print|west-owner-never-print' "$LOG" ||
  grep -R -Eq 'fixture-secret-never-print|minio-root-never-print|minio-secret-never-print|east-owner-never-print|west-owner-never-print' \
    "$TEMP_DIR/output/$deploy_services-evidence" "$TEMP_DIR/output/$deploy_services-state/up-rendered"; then fail 'Deploy-mode command/evidence/rendering exposed a secret.'; fi
test ! -e "$TEMP_DIR/output/$deploy_services-state/up-rendered/minio-runtime.json"

# Run-owned isolated deletion removes explicit containers, named volumes, and marker-validated roots only.
FAKE_VOLUME_HASH="$(jq -S -c . "$INVENTORY" | sha256sum | cut -d' ' -f1)"
export FAKE_VOLUME_HASH
# A runtime-user-owned exact-marker root may legitimately contain UID-0 application descendants.
mixed_contract_root="$TEMP_DIR/remote/shared/mixed-runtime-root"
mixed_contract_digest="$(printf '6%.0s' {1..64})"
runtime_helper_image="registry.example/redis@sha256:$(printf '2%.0s' {1..64})"
runtime_helper_env="$TEMP_DIR/runtime-helper.env"
printf 'REDIS_IMAGE=%s\n' "$runtime_helper_image" > "$runtime_helper_env"; chmod 600 "$runtime_helper_env"
make_runtime_contract_root() {
  local root=$1 digest=$2
  rm -rf -- "$root"
  mkdir -m 700 -p "$root/.hvo-deploy" "$root/application/content"
  chmod 700 "$root" "$root/.hvo-deploy"
  printf 'HVO-DEPLOY-ROOT\t1\nmarker\t%s\n' "$digest" > "$root/.hvo-deploy/ownership"
  chmod 600 "$root/.hvo-deploy/ownership"
  printf 'state\n' > "$root/application/content/state"
}
map_runtime_contract_tree() {
  local root=$1 uid=$2 path
  : > "$FAKE_REMOTE_UID_MAP"
  while IFS= read -r -d '' path; do printf '%s\t%s\n' "$path" "$uid" >> "$FAKE_REMOTE_UID_MAP"; done < <(find -P "$root" -print0)
}
set_runtime_contract_uid() {
  local path=$1 uid=$2 temporary="$FAKE_REMOTE_UID_MAP.tmp"
  /usr/bin/awk -F '\t' -v path="$path" '$1 != path' "$FAKE_REMOTE_UID_MAP" > "$temporary"
  mv "$temporary" "$FAKE_REMOTE_UID_MAP"
  printf '%s\t%s\n' "$path" "$uid" >> "$FAKE_REMOTE_UID_MAP"
}
remove_runtime_contract_root() {
  local helper_env=${2:-$runtime_helper_env}
  (
    source "$REPO_ROOT/scripts/deploy/transport.sh"
    PATH="$BIN:$PATH" deploy_transport_remove_runtime_root shared@example "$1" "$mixed_contract_digest" true shared-context "$helper_env" REDIS_IMAGE
  )
}

# Entirely runtime-owned roots remain unprivileged.
runtime_only_root="$TEMP_DIR/remote/shared/runtime-only-root"
make_runtime_contract_root "$runtime_only_root" "$mixed_contract_digest"
map_runtime_contract_tree "$runtime_only_root" "$FAKE_RUNTIME_UID"
helper_runs_before="$(grep -Ec $'docker\t--context\tshared-context\trun\t' "$LOG" || true)"
remove_runtime_contract_root "$runtime_only_root" "$TEMP_DIR/missing-helper.env" || fail 'Entirely runtime-owned root required Docker helper metadata.'
test ! -e "$runtime_only_root"
test "$(grep -Ec $'docker\t--context\tshared-context\trun\t' "$LOG" || true)" = "$helper_runs_before" || fail 'Runtime-only deletion invoked the Docker helper.'

backslash_root="$TEMP_DIR/remote/shared/backslash\\root"
make_runtime_contract_root "$backslash_root" "$mixed_contract_digest"
map_runtime_contract_tree "$backslash_root" "$FAKE_RUNTIME_UID"
ssh_calls_before="$(grep -c '^ssh' "$LOG" || true)"
if remove_runtime_contract_root "$backslash_root"; then fail 'Runtime-root deletion accepted backslash before mountinfo comparison.'; fi
test -f "$backslash_root/.hvo-deploy/ownership" && test -f "$backslash_root/application/content/state"
test "$(grep -c '^ssh' "$LOG" || true)" = "$ssh_calls_before" || fail 'Backslash root reached SSH validation.'

initial_timeout_root="$TEMP_DIR/remote/shared/initial-ssh-timeout-root"
make_runtime_contract_root "$initial_timeout_root" "$mixed_contract_digest"
map_runtime_contract_tree "$initial_timeout_root" "$FAKE_RUNTIME_UID"
if FAKE_RUNTIME_SSH_TIMEOUT_PHASE=initial FAKE_RUNTIME_SSH_TIMEOUT_MATCH=initial-ssh-timeout-root \
  remove_runtime_contract_root "$initial_timeout_root"; then fail 'Runtime-root deletion accepted an initial SSH timeout.'; fi
test -f "$initial_timeout_root/.hvo-deploy/ownership" && test -f "$initial_timeout_root/application/content/state" ||
  fail 'Initial SSH timeout mutated runtime-root state.'
runtime_cleanup_functions="$(source "$REPO_ROOT/scripts/deploy/transport.sh"; declare -f deploy_transport_remove_runtime_root; declare -f deploy_transport_remove_prepare_lock)"
test "$(grep -Fc 'timeout --signal=TERM --kill-after=30s 3600 ssh -o BatchMode=yes -o ConnectTimeout=8' <<< "$runtime_cleanup_functions")" = 3
test "$(grep -Fc -- '-o ServerAliveInterval=10 -o ServerAliveCountMax=3' <<< "$runtime_cleanup_functions")" = 3
grep -Fq $'ssh\t-o\tBatchMode=yes\t-o\tConnectTimeout=8\t-o\tServerAliveInterval=10\t-o\tServerAliveCountMax=3' "$LOG"

# Foreign descendants, ownership drift, and mountpoints fail before mutation or helper invocation.
for unsafe_case in foreign-descendant root-owner control-owner marker-owner root-mount nested-mount same-device-bind; do
  unsafe_root="$TEMP_DIR/remote/shared/unsafe-$unsafe_case"
  make_runtime_contract_root "$unsafe_root" "$mixed_contract_digest"
  map_runtime_contract_tree "$unsafe_root" "$FAKE_RUNTIME_UID"
  case "$unsafe_case" in
    foreign-descendant) set_runtime_contract_uid "$unsafe_root/application/content/state" 99999 ;;
    root-owner) set_runtime_contract_uid "$unsafe_root" 99999 ;;
    control-owner) set_runtime_contract_uid "$unsafe_root/.hvo-deploy" 99999 ;;
    marker-owner) set_runtime_contract_uid "$unsafe_root/.hvo-deploy/ownership" 99999 ;;
    root-mount) printf '%s\n' "$unsafe_root" > "$FAKE_REMOTE_MOUNT_MAP" ;;
    nested-mount) printf '%s\n' "$unsafe_root/application" > "$FAKE_REMOTE_MOUNT_MAP" ;;
    same-device-bind) printf '%s\n' "$unsafe_root/application/content" > "$FAKE_REMOTE_MOUNT_MAP" ;;
  esac
  helper_runs_before="$(grep -Ec $'docker\t--context\tshared-context\trun\t' "$LOG" || true)"
  if remove_runtime_contract_root "$unsafe_root"; then fail "Runtime-root deletion accepted $unsafe_case."; fi
  test -f "$unsafe_root/application/content/state" || fail "$unsafe_case mutated content before rejection."
  test -f "$unsafe_root/.hvo-deploy/ownership" || fail "$unsafe_case removed the ownership marker."
  test "$(grep -Ec $'docker\t--context\tshared-context\trun\t' "$LOG" || true)" = "$helper_runs_before" || fail "$unsafe_case invoked the helper before rejection."
  : > "$FAKE_REMOTE_MOUNT_MAP"
done

unsafe_root="$TEMP_DIR/remote/shared/metadata-failure-mountinfo"
make_runtime_contract_root "$unsafe_root" "$mixed_contract_digest"
map_runtime_contract_tree "$unsafe_root" "$FAKE_RUNTIME_UID"
if FAKE_REMOTE_MOUNTINFO_FAIL=true remove_runtime_contract_root "$unsafe_root"; then fail 'Runtime-root deletion accepted a mountinfo scan error.'; fi
test -f "$unsafe_root/application/content/state" || fail 'Mountinfo metadata failure mutated content.'
test -f "$unsafe_root/.hvo-deploy/ownership" || fail 'Mountinfo metadata failure removed the ownership marker.'

# An inaccessible host ownership scan delegates to the helper, which remains the full ownership authority.
delegated_root="$TEMP_DIR/remote/shared/delegated-valid-mixed"
make_runtime_contract_root "$delegated_root" "$mixed_contract_digest"
map_runtime_contract_tree "$delegated_root" "$FAKE_RUNTIME_UID"
set_runtime_contract_uid "$delegated_root/application" 0
chmod 700 "$delegated_root/application"
helper_runs_before="$(grep -Ec $'docker\t--context\tshared-context\trun\t' "$LOG" || true)"
FAKE_REMOTE_FIND_FAIL_ONCE_MATCH=delegated-valid-mixed remove_runtime_contract_root "$delegated_root" ||
  fail 'A valid inaccessible mixed tree did not delegate to the helper.'
test ! -e "$delegated_root"
test "$(grep -Ec $'docker\t--context\tshared-context\trun\t' "$LOG" || true)" = "$((helper_runs_before + 1))" ||
  fail 'Delegated mixed ownership did not invoke exactly one helper.'

for delegated_failure in foreign find; do
  delegated_root="$TEMP_DIR/remote/shared/delegated-$delegated_failure-failure"
  make_runtime_contract_root "$delegated_root" "$mixed_contract_digest"
  map_runtime_contract_tree "$delegated_root" "$FAKE_RUNTIME_UID"
  set_runtime_contract_uid "$delegated_root/application" 0
  if [[ "$delegated_failure" == foreign ]]; then
    if FAKE_REMOTE_FIND_FAIL_ONCE_MATCH="delegated-$delegated_failure-failure" FAKE_DOCKER_HELPER_FOREIGN_UID=true \
      remove_runtime_contract_root "$delegated_root"; then fail 'Delegated helper accepted a foreign UID.'; fi
  else
    if FAKE_REMOTE_FIND_FAIL_ONCE_MATCH="delegated-$delegated_failure-failure" FAKE_DOCKER_HELPER_FIND_FAIL=true \
      remove_runtime_contract_root "$delegated_root"; then fail 'Delegated helper accepted a find failure.'; fi
  fi
  test -f "$delegated_root/application/content/state" || fail "Delegated $delegated_failure failure mutated content."
  test -f "$delegated_root/.hvo-deploy/ownership" || fail "Delegated $delegated_failure failure removed the marker."
done

# Partial helper cleanup may leave only an inaccessible empty UID-0 directory; the next intent delegates again and converges.
partial_root="$TEMP_DIR/remote/shared/partial-helper-retry"
make_runtime_contract_root "$partial_root" "$mixed_contract_digest"
mkdir -m 700 -p "$partial_root/application/root-nested/leaf"
printf 'nested\n' > "$partial_root/application/root-nested/leaf/state"
map_runtime_contract_tree "$partial_root" "$FAKE_RUNTIME_UID"
set_runtime_contract_uid "$partial_root/application" 0
set_runtime_contract_uid "$partial_root/application/root-nested" 0
set_runtime_contract_uid "$partial_root/application/root-nested/leaf" 0
if FAKE_DOCKER_HELPER_PARTIAL=true remove_runtime_contract_root "$partial_root"; then fail 'Partial helper simulation unexpectedly passed.'; fi
test -d "$partial_root/application" && test "$(stat -c %a "$partial_root/application")" = 700 || fail 'Partial helper did not leave the inaccessible empty directory.'
test -z "$(find "$partial_root/application" -mindepth 1 -print -quit)" || fail 'Partial helper left content below the empty UID-0 directory.'
test -f "$partial_root/.hvo-deploy/ownership" || fail 'Partial helper removed the marker.'
FAKE_REMOTE_FIND_FAIL_ONCE_MATCH=partial-helper-retry remove_runtime_contract_root "$partial_root" || fail 'Partial helper intent did not converge on retry.'
test ! -e "$partial_root"

# Depth-first helper cleanup removes nested mode-0700 UID-0 directories through both BusyBox and GNU-compatible find ordering.
nested_root="$TEMP_DIR/remote/shared/nested-root-owned"
make_runtime_contract_root "$nested_root" "$mixed_contract_digest"
mkdir -m 700 -p "$nested_root/application/one/two/three"
printf 'nested\n' > "$nested_root/application/one/two/three/state"
map_runtime_contract_tree "$nested_root" "$FAKE_RUNTIME_UID"
for nested_path in "$nested_root/application" "$nested_root/application/one" "$nested_root/application/one/two" "$nested_root/application/one/two/three"; do
  chmod 700 "$nested_path"; set_runtime_contract_uid "$nested_path" 0
done
remove_runtime_contract_root "$nested_root" || fail 'Depth-first helper cleanup left nested UID-0 directories.'
test ! -e "$nested_root"

# Marker type/content/mode/link and root-component symlink checks remain fail closed.
for unsafe_case in wrong-marker appended-newline appended-nul root-mode control-mode marker-mode marker-symlink marker-hardlink root-symlink ancestor-symlink; do
  unsafe_root="$TEMP_DIR/remote/shared/metadata-$unsafe_case"
  make_runtime_contract_root "$unsafe_root" "$mixed_contract_digest"
  outside_marker="$TEMP_DIR/outside-$unsafe_case"; printf 'outside\n' > "$outside_marker"
  case "$unsafe_case" in
    wrong-marker) printf 'wrong\n' > "$unsafe_root/.hvo-deploy/ownership" ;;
    appended-newline) printf '\n' >> "$unsafe_root/.hvo-deploy/ownership" ;;
    appended-nul) printf '\0' >> "$unsafe_root/.hvo-deploy/ownership" ;;
    root-mode) chmod 750 "$unsafe_root" ;;
    control-mode) chmod 750 "$unsafe_root/.hvo-deploy" ;;
    marker-mode) chmod 640 "$unsafe_root/.hvo-deploy/ownership" ;;
    marker-symlink) rm "$unsafe_root/.hvo-deploy/ownership"; ln -s "$outside_marker" "$unsafe_root/.hvo-deploy/ownership" ;;
    marker-hardlink) ln "$unsafe_root/.hvo-deploy/ownership" "$outside_marker.link" ;;
    root-symlink) mv "$unsafe_root" "$unsafe_root.real"; ln -s "$unsafe_root.real" "$unsafe_root" ;;
    ancestor-symlink) mv "$unsafe_root" "$unsafe_root.real"; mkdir "$unsafe_root.parent"; ln -s "$unsafe_root.real" "$unsafe_root.parent/root"; unsafe_root="$unsafe_root.parent/root" ;;
  esac
  map_runtime_contract_tree "$unsafe_root" "$FAKE_RUNTIME_UID"
  if remove_runtime_contract_root "$unsafe_root"; then fail "Runtime-root deletion accepted $unsafe_case."; fi
  grep -Fxq outside "$outside_marker" || fail "$unsafe_case changed an outside target."
  rm -f "$outside_marker.link"
done


# Helper-side metadata and timeout failures remove only the exact cidfile container and preserve retry state.
for helper_metadata_failure in find mountinfo timeout; do
  mixed_failure_root="$TEMP_DIR/remote/shared/helper-$helper_metadata_failure-failure"
  make_runtime_contract_root "$mixed_failure_root" "$mixed_contract_digest"
  map_runtime_contract_tree "$mixed_failure_root" "$FAKE_RUNTIME_UID"
  set_runtime_contract_uid "$mixed_failure_root/application" 0
  case "$helper_metadata_failure" in
    find) if FAKE_DOCKER_HELPER_FIND_FAIL=true remove_runtime_contract_root "$mixed_failure_root"; then fail 'Runtime helper accepted a find metadata failure.'; fi ;;
    mountinfo) if FAKE_DOCKER_HELPER_MOUNTINFO_FAIL=true remove_runtime_contract_root "$mixed_failure_root"; then fail 'Runtime helper accepted a mountinfo metadata failure.'; fi ;;
    timeout) if FAKE_DOCKER_HELPER_TIMEOUT=true remove_runtime_contract_root "$mixed_failure_root"; then fail 'Runtime helper accepted a timeout.'; fi ;;
  esac
  helper_failure_cid="$(<"$FAKE_IMAGE_STATE/helper-last-cid")"
  [[ "$helper_failure_cid" =~ ^[0-9a-f]{64}$ ]] || fail "Helper $helper_metadata_failure did not publish a valid cidfile ID."
  test ! -e "$FAKE_IMAGE_STATE/container-$helper_failure_cid" || fail "Helper $helper_metadata_failure orphan was not removed."
  test ! -e "$(<"$FAKE_IMAGE_STATE/helper-last-ciddir")" || fail "Helper $helper_metadata_failure retained its local cid directory."
  grep -Fq -- $'docker\t--context\tshared-context\tcontainer\trm\t-f\t'"$helper_failure_cid" "$LOG" ||
    fail "Helper $helper_metadata_failure did not remove its exact cidfile container."
  test -f "$mixed_failure_root/application/content/state" || fail "Helper $helper_metadata_failure failure mutated content."
  test -f "$mixed_failure_root/.hvo-deploy/ownership" || fail "Helper $helper_metadata_failure failure removed the marker."
done

make_runtime_contract_root "$mixed_contract_root" "$mixed_contract_digest"
printf 'root-owned\n' > "$mixed_contract_root/application/content/state"
: > "$FAKE_REMOTE_UID_MAP"
printf '%s\t%s\n' "$mixed_contract_root" "$FAKE_RUNTIME_UID" >> "$FAKE_REMOTE_UID_MAP"
printf '%s\t%s\n' "$mixed_contract_root/.hvo-deploy" "$FAKE_RUNTIME_UID" >> "$FAKE_REMOTE_UID_MAP"
printf '%s\t%s\n' "$mixed_contract_root/.hvo-deploy/ownership" "$FAKE_RUNTIME_UID" >> "$FAKE_REMOTE_UID_MAP"
printf '%s\t0\n' "$mixed_contract_root/application" >> "$FAKE_REMOTE_UID_MAP"
printf '%s\t0\n' "$mixed_contract_root/application/content" >> "$FAKE_REMOTE_UID_MAP"
printf '%s\t0\n' "$mixed_contract_root/application/content/state" >> "$FAKE_REMOTE_UID_MAP"
helper_runs_before="$(grep -Ec $'docker\t--context\tshared-context\trun\t' "$LOG" || true)"
(
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  PATH="$BIN:$PATH" deploy_transport_remove_runtime_root shared@example "$mixed_contract_root" "$mixed_contract_digest" true shared-context "$runtime_helper_env" REDIS_IMAGE
) || fail 'Runtime-user-owned exact-marker root with UID-0 application descendants could not be deleted.'
test ! -e "$mixed_contract_root"
test ! -e "$(<"$FAKE_IMAGE_STATE/helper-last-ciddir")" || fail 'Successful helper retained its local cid directory.'
test "$(grep -Ec $'docker\t--context\tshared-context\trun\t' "$LOG" || true)" = "$((helper_runs_before + 1))" || fail 'Mixed-UID deletion did not invoke exactly one helper.'
helper_line="$(grep $'docker\t--context\tshared-context\trun\t' "$LOG" | tail -n 1)"
grep -Fq -- $'--pull\tnever\t--rm\t--interactive\t--network\tnone\t--read-only\t--pids-limit\t64\t--user\t0:0' <<< "$helper_line"
grep -Fq -- "--mount"$'\t'"type=bind,source=$mixed_contract_root,target=/runtime-root" <<< "$helper_line"
grep -Fq -- "$runtime_helper_image" <<< "$helper_line"
grep -Fq 'timeout --signal=TERM --kill-after=30s 3600 docker --context' "$REPO_ROOT/scripts/deploy/transport.sh"
grep -Fq -- '--entrypoint /bin/sh' "$REPO_ROOT/scripts/deploy/transport.sh"
if grep -Eq '(^|[[:space:]])sudo([[:space:]]|$)' "$REPO_ROOT/scripts/deploy/transport.sh" "$LOG"; then fail 'Runtime-root deletion used sudo.'; fi

archive_contract_root="$TEMP_DIR/remote/shared/archive-id-runtime-root"
archive_contract_env="$TEMP_DIR/archive-helper.env"
make_runtime_contract_root "$archive_contract_root" "$mixed_contract_digest"
map_runtime_contract_tree "$archive_contract_root" "$FAKE_RUNTIME_UID"
set_runtime_contract_uid "$archive_contract_root/application" 0
printf 'REDIS_IMAGE=%s\n' "$archive_helper_id" > "$archive_contract_env"; chmod 600 "$archive_contract_env"
remove_runtime_contract_root "$archive_contract_root" "$archive_contract_env" || fail 'Mixed-UID cleanup rejected an archive-loaded image ID.'
test ! -e "$archive_contract_root"
: > "$FAKE_REMOTE_UID_MAP"
prepare_ledger_path="$TEMP_DIR/output/$deploy_services-state/prepare-ledger.json"
prepare_manifest_path="$TEMP_DIR/output/$deploy_services-state/prepare-manifest.json"
prepare_evidence_path="$TEMP_DIR/output/$deploy_services-evidence/prepare.json"
chmod 640 "$prepare_ledger_path"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-prepare-security.log" 2>&1; then fail 'Down accepted a non-0600 prepare ledger.'; fi
chmod 600 "$prepare_ledger_path"
ln "$prepare_evidence_path" "$TEMP_DIR/prepare-evidence-hardlink"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-prepare-security.log" 2>&1; then fail 'Down accepted a multiply-linked prepare evidence file.'; fi
rm "$TEMP_DIR/prepare-evidence-hardlink"
cp "$prepare_manifest_path" "$TEMP_DIR/prepare-manifest.valid"
jq '.updatedAt="tampered"' "$prepare_manifest_path" > "$TEMP_DIR/prepare-manifest.tampered" && mv "$TEMP_DIR/prepare-manifest.tampered" "$prepare_manifest_path"
chmod 600 "$prepare_manifest_path"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-prepare-security.log" 2>&1; then fail 'Down accepted a prepare manifest that differed from its ledger.'; fi
cp "$TEMP_DIR/prepare-manifest.valid" "$prepare_manifest_path"
chmod 600 "$prepare_manifest_path"

down_failpoint='after-graceful-stop-mutation-cameraagent:east'
export FAKE_DOWN_CONTINUITY_MODE=two FAKE_DOWN_TAIL_MODE=healthy
if DEPLOY_TEST_FAILPOINT="$down_failpoint" \
  run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-resume.log" 2>&1; then
  fail "Down failpoint $down_failpoint passed."
fi
jq -e '.phaseStatus == "failed" and any(.resources[]; .status == "intent" or .status == "completed")' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
east_network_state="$FAKE_IMAGE_STATE/network-east-context-${east_project}_default"
cp "$east_network_state" "$TEMP_DIR/east-network.valid"
jq '.inventorySha256="tampered"' "$east_network_state" > "$east_network_state.changed" && mv "$east_network_state.changed" "$east_network_state"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-network-identity.log" 2>&1; then
  fail 'Down removed a Compose network with mismatched ownership labels.'
fi
cp "$TEMP_DIR/east-network.valid" "$east_network_state"

# Same-run inventory-label drift is retained explicitly and authorizes only exact absence on retry.
sql_volume_state="$FAKE_IMAGE_STATE/volume-shared-context-hvo-deploy-services-services_sql-data"
cp "$sql_volume_state" "$TEMP_DIR/sql-volume.valid"
rm "$sql_volume_state"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-volume-absent.log" 2>&1; then
  fail 'Down accepted initially absent volume state without prior retained evidence.'
fi
grep -Fq 'reason=ownership-unproven-absence' "$TEMP_DIR/down-volume-absent.log"
cp "$TEMP_DIR/sql-volume.valid" "$sql_volume_state"; chmod 600 "$sql_volume_state"
if FAKE_DOCKER_INSPECT_OUTAGE=shared-context-volume \
  run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-volume-inspect.log" 2>&1; then
  fail 'Down treated a Docker volume inspection failure as absence or drift.'
fi
grep -Fq 'reason=identity-invalid' "$TEMP_DIR/down-volume-inspect.log"
jq -c '.inventorySha256=null' "$sql_volume_state" > "$sql_volume_state.changed" && mv "$sql_volume_state.changed" "$sql_volume_state"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-volume-malformed.log" 2>&1; then
  fail 'Down treated a malformed inventory label as retained drift.'
fi
grep -Fq 'reason=identity-invalid' "$TEMP_DIR/down-volume-malformed.log"
cp "$TEMP_DIR/sql-volume.valid" "$sql_volume_state"; chmod 600 "$sql_volume_state"
jq -c '.inventorySha256=("f" * 64)' "$sql_volume_state" > "$sql_volume_state.changed" && mv "$sql_volume_state.changed" "$sql_volume_state"
if DEPLOY_TEST_FAILPOINT='after-retain-volume-label-drift-intent:sql:hvo-deploy-services-services_sql-data' \
  run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-volume-retain-intent.log" 2>&1; then
  fail 'Down passed after interrupted volume-drift retention intent.'
fi
jq -e '.phaseStatus == "failed" and any(.resources[]; .resource == "sql:hvo-deploy-services-services_sql-data" and
  .action == "retain-volume-label-drift" and .status == "intent")' "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
cp "$TEMP_DIR/sql-volume.valid" "$sql_volume_state"; chmod 600 "$sql_volume_state"
if DEPLOY_TEST_FAILPOINT='after-delete-volume-mutation-sql:hvo-deploy-services-services_sql-data' \
  run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-volume-retain-exact.log" 2>&1; then
  fail 'Down passed the retained-intent to exact-delete interruption.'
fi
jq -e '.phaseStatus == "failed" and any(.resources[]; .resource == "sql:hvo-deploy-services-services_sql-data" and
  .action == "retain-volume-label-drift" and .status == "completed") and
  any(.resources[]; .resource == "sql:hvo-deploy-services-services_sql-data" and .action == "delete-volume" and .status == "intent")' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
test ! -e "$sql_volume_state" || fail 'Exact-label volume was not removed after retained-intent recovery.'

redis_volume_state="$FAKE_IMAGE_STATE/volume-shared-context-hvo-deploy-services-services_redis-data"
cp "$redis_volume_state" "$TEMP_DIR/redis-volume.valid"
jq -c '.inventorySha256=("f" * 64)' "$redis_volume_state" > "$redis_volume_state.changed" && mv "$redis_volume_state.changed" "$redis_volume_state"
redis_volume_removals_before="$(grep -Fc $'docker\t--context\tshared-context\tvolume\trm\thvo-deploy-services-services_redis-data' "$LOG" || true)"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-volume-drift.log" 2>&1; then
  fail 'Down accepted a drifted run-owned volume.'
fi
grep -Fq 'stage=down check=redis:hvo-deploy-services-services_redis-data status=failed reason=inventory-label-drift-retained' "$TEMP_DIR/down-volume-drift.log" || {
  /usr/bin/cat "$TEMP_DIR/down-volume-drift.log" >&2
  fail 'Down did not emit the specific retained volume-label drift failure.'
}
jq -e '.phaseStatus == "failed" and any(.resources[]; .resource == "redis:hvo-deploy-services-services_redis-data" and
  .action == "retain-volume-label-drift" and .status == "completed") and
  (any(.resources[]; .resource == "redis:hvo-deploy-services-services_redis-data" and .action == "delete-volume") | not)' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
test "$(grep -Fc $'docker\t--context\tshared-context\tvolume\trm\thvo-deploy-services-services_redis-data' "$LOG" || true)" = "$redis_volume_removals_before" ||
  fail 'Down removed a volume whose inventory label drifted.'
test -d "$logic_runtime_root" && test -f "$logic_prepare_lock.state" || fail 'Volume drift mutated later runtime-root state.'
jq -c '.runId="foreign-run"' "$redis_volume_state" > "$redis_volume_state.changed" && mv "$redis_volume_state.changed" "$redis_volume_state"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-volume-foreign.log" 2>&1; then
  fail 'Down treated a foreign volume as retained label drift.'
fi
grep -Fq 'reason=identity-invalid' "$TEMP_DIR/down-volume-foreign.log"
cp "$TEMP_DIR/redis-volume.valid" "$redis_volume_state"; chmod 600 "$redis_volume_state"
jq -c '.inventorySha256=("f" * 64)' "$redis_volume_state" > "$redis_volume_state.changed" && mv "$redis_volume_state.changed" "$redis_volume_state"
rm "$redis_volume_state"
for down_failpoint in \
  'after-delete-volume-mutation-minio:hvo-deploy-services-services_minio-data' \
  'after-delete-mutation-runtime-root:logic'; do
  if DEPLOY_TEST_FAILPOINT="$down_failpoint" run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-resume.log" 2>&1; then
    fail "Down failpoint $down_failpoint passed."
  fi
  jq -e '.phaseStatus == "failed" and any(.resources[]; .status == "intent" or .status == "completed")' \
    "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
done

chmod 640 "$logic_prepare_lock"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-prepare-lock-mode.log" 2>&1; then
  fail 'Down accepted unsafe prepare-lock metadata.'
fi
test -f "$logic_prepare_lock" && test -f "$logic_prepare_lock.state" || fail 'Unsafe prepare-lock metadata was mutated.'
jq -e 'any(.resources[]; .resource == "prepare-lock:logic" and .action == "delete" and .status == "intent")' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
chmod 600 "$logic_prepare_lock"
cp "$logic_prepare_lock.state" "$TEMP_DIR/logic-prepare-lock.valid-state"
printf 'tampered\n' >> "$logic_prepare_lock.state"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-prepare-lock-state.log" 2>&1; then
  fail 'Down accepted tampered prepare-lock state bytes.'
fi
test -f "$logic_prepare_lock" && test -f "$logic_prepare_lock.state" || fail 'Tampered prepare-lock state was mutated.'
cp "$TEMP_DIR/logic-prepare-lock.valid-state" "$logic_prepare_lock.state"; chmod 600 "$logic_prepare_lock.state"
exec 87<>"$logic_prepare_lock"; flock -n 87
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-prepare-lock-contended.log" 2>&1; then
  fail 'Down accepted a contended prepare lock.'
fi
flock -u 87; exec 87>&-
test -f "$logic_prepare_lock" && test -f "$logic_prepare_lock.state" || fail 'Contended prepare-lock state was mutated.'

# Mixed-UID helper denial and a crash after helper clearing both preserve exact-marker intent for retry.
mixed_runtime_root="$east_runtime_root"
logic_runtime_marker="$mixed_runtime_root/.hvo-deploy/ownership"
logic_runtime_uid="$(id -u)"
if FAKE_RUNTIME_SSH_TIMEOUT_PHASE=final FAKE_RUNTIME_SSH_TIMEOUT_MATCH="22222222-2222-4222-8222-222222222222" \
  run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-runtime-final-timeout.log" 2>&1; then
  fail 'Down accepted a final runtime-root SSH timeout.'
fi
test -f "$logic_runtime_marker" || fail 'Final SSH timeout removed the runtime ownership marker.'
jq -e '.phaseStatus == "failed" and any(.resources[]; .resource == "runtime-root:east" and .action == "delete" and .status == "intent")' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
: > "$FAKE_REMOTE_UID_MAP"
printf '%s\t%s\n' "$mixed_runtime_root" "$logic_runtime_uid" >> "$FAKE_REMOTE_UID_MAP"
printf '%s\t%s\n' "$mixed_runtime_root/.hvo-deploy" "$logic_runtime_uid" >> "$FAKE_REMOTE_UID_MAP"
printf '%s\t%s\n' "$logic_runtime_marker" "$logic_runtime_uid" >> "$FAKE_REMOTE_UID_MAP"
printf '%s\t0\n' "$mixed_runtime_root/state" >> "$FAKE_REMOTE_UID_MAP"
helper_runs_before="$(grep -Ec $'docker\t--context\teast-context\trun\t' "$LOG" || true)"
if FAKE_DOCKER_HELPER_FAIL=true run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-helper-denied.log" 2>&1; then
  fail 'Down passed after the constrained cleanup helper was denied.'
fi
test -f "$logic_runtime_marker" || fail 'Helper denial removed the runtime ownership marker.'
jq -e '.phaseStatus == "failed" and any(.resources[]; .resource == "runtime-root:east" and .action == "delete" and .status == "intent")' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
test "$(grep -Ec $'docker\t--context\teast-context\trun\t' "$LOG" || true)" = "$((helper_runs_before + 1))" || fail 'Helper denial did not use exactly one constrained helper attempt.'
if DEPLOY_TEST_FAILPOINT=after-mixed-runtime-helper-clear run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-helper-cleared.log" 2>&1; then
  fail 'Down passed the post-helper-clear failpoint.'
fi
test -f "$logic_runtime_marker" || fail 'Post-helper-clear failure removed the runtime ownership marker.'
test -z "$(find -P "$mixed_runtime_root" -mindepth 1 ! -path "$mixed_runtime_root/.hvo-deploy" ! -path "$logic_runtime_marker" -print -quit)" ||
  fail 'The constrained helper did not clear mixed-owned content before the injected failure.'
jq -e '.phaseStatus == "failed" and any(.resources[]; .resource == "runtime-root:east" and .action == "delete" and .status == "intent")' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
if DEPLOY_TEST_FAILPOINT='after-delete-runtime-root:east' run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-helper-resumed.log" 2>&1; then
  fail 'Down passed the post-resume completion failpoint.'
fi
test ! -e "$mixed_runtime_root" || fail 'Exact-marker deletion intent did not resume after helper clearing.'
jq -e 'any(.resources[]; .resource == "runtime-root:east" and .action == "delete" and .status == "completed")' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
test -f "$east_prepare_lock" && test -f "$east_prepare_lock.state" || fail 'Runtime-root completion removed prepare provenance before its durable action.'
if DEPLOY_TEST_FAILPOINT='after-delete-prepare-lock-state:east' \
  run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-prepare-lock-partial.log" 2>&1; then
  fail 'Down passed the partial prepare-lock cleanup failpoint.'
fi
test -f "$east_prepare_lock" && test ! -e "$east_prepare_lock.state" || {
  /usr/bin/cat "$TEMP_DIR/down-prepare-lock-partial.log" >&2
  /usr/bin/stat -c '%n %F %u:%h:%a:%s' "$east_prepare_lock" "$east_prepare_lock.state" 2>/dev/null >&2 || true
  fail 'Prepare-lock failpoint did not leave the expected resumable partial pair.'
}
jq -e 'any(.resources[]; .resource == "prepare-lock:east" and .action == "delete" and .status == "intent")' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
: > "$FAKE_REMOTE_UID_MAP"
read_only_state="$shared_runtime_root/state/catalog-read-only"
outside_read_only_state="$TEMP_DIR/catalog-read-only-outside"
mkdir "$read_only_state"
printf 'catalog\n' > "$read_only_state/manifest.json"
printf 'preserve\n' > "$outside_read_only_state"
ln -s "$outside_read_only_state" "$read_only_state/outside-link"
chmod 444 "$read_only_state/manifest.json"
chmod 555 "$read_only_state"
run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" >/dev/null
grep -Fxq preserve "$outside_read_only_state"
jq -e '.phaseStatus == "passed" and .policy == "delete" and
  ([.resources[] | select(.action == "delete-network" and .status == "completed")] | length) == 4 and
  ([.resources[] | select(.action == "delete-volume" and .status == "completed")] | length) == 2 and
  ([.resources[] | select(.action == "retain-volume-label-drift" and .status == "completed")] | length) == 2 and
  ([.resources[] | select(.action == "delete" and .status == "completed" and (.resource | startswith("runtime-root:")))] | length) == 4 and
  ([.resources[] | select(.action == "delete" and .status == "completed" and (.resource | startswith("prepare-lock:")))] | length) == 4' \
  "$TEMP_DIR/output/$deploy_services-state/down-ledger.json" >/dev/null
grep -Fq -- $'--profile\ttest-smtp\t--profile\tprovision\trm\t-f' "$LOG"
for removed_root in "$logic_runtime_root" "$east_runtime_root" "$west_runtime_root" "$shared_runtime_root"; do test ! -e "$removed_root"; done
for prepare_lock in "$logic_prepare_lock" "$east_prepare_lock" "$west_prepare_lock" "$shared_prepare_lock"; do
  test ! -e "$prepare_lock" && test ! -L "$prepare_lock" && test ! -e "$prepare_lock.state" && test ! -L "$prepare_lock.state" ||
    fail "Down retained prepare provenance after deleting its exact runtime root: $prepare_lock"
done
for network_state in \
  "network-east-context-${east_project}_default" \
  "network-west-context-${west_project}_default" \
  "network-logic-context-${logic_project}_default" \
  network-shared-context-hvo-deploy-services-services_default; do
  test ! -e "$FAKE_IMAGE_STATE/$network_state" || fail "Orphan network remained: $network_state."
done

cp "$shared_render_env" "$TEMP_DIR/shared-render.valid"
/usr/bin/awk '!/^REDIS_IMAGE=/' "$shared_render_env" > "$shared_render_env.changed" && mv "$shared_render_env.changed" "$shared_render_env"
chmod 600 "$shared_render_env"
run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" >/dev/null ||
  fail 'Completed runtime-root absence verification parsed unused helper metadata.'
cp "$TEMP_DIR/shared-render.valid" "$shared_render_env"; chmod 600 "$shared_render_env"

printf '{"runId":"%s","inventorySha256":"%s"}\n' "$deploy_services" "$FAKE_VOLUME_HASH" > \
  "$FAKE_IMAGE_STATE/volume-shared-context-hvo-deploy-services-services_sql-data"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-recreated.log" 2>&1; then fail 'Completed down accepted a recreated volume.'; fi
rm "$FAKE_IMAGE_STATE/volume-shared-context-hvo-deploy-services-services_sql-data"
printf '{"name":"%s_default","project":"tampered","network":"default","runId":"other","inventorySha256":"%s"}\n' "$east_project" "$FAKE_VOLUME_HASH" > "$east_network_state"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-recreated.log" 2>&1; then fail 'Completed down accepted a recreated network.'; fi
rm "$east_network_state"
mkdir -m 700 "$logic_runtime_root"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-recreated.log" 2>&1; then fail 'Completed down accepted a recreated runtime root.'; fi
rm -rf "$logic_runtime_root"
printf 'recreated\n' > "$east_prepare_lock"; chmod 600 "$east_prepare_lock"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-recreated.log" 2>&1; then fail 'Completed down accepted a recreated prepare lock.'; fi
rm "$east_prepare_lock"
printf 'recreated\n' > "$east_prepare_lock.state"; chmod 600 "$east_prepare_lock.state"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-recreated.log" 2>&1; then fail 'Completed down accepted a recreated prepare-lock state sidecar.'; fi
rm "$east_prepare_lock.state"
east_container_id="$(sha256sum <<< "east-context/$east_project/cameraagent" | cut -c1-64)"
printf 'true\n' > "$FAKE_IMAGE_STATE/service-east-context-$east_project-cameraagent"
printf '{"project":"%s","service":"cameraagent","running":true}\n' "$east_project" > "$FAKE_IMAGE_STATE/container-$east_container_id"
if run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" > "$TEMP_DIR/down-recreated.log" 2>&1; then fail 'Completed down accepted a recreated running container.'; fi
rm "$FAKE_IMAGE_STATE/service-east-context-$east_project-cameraagent" "$FAKE_IMAGE_STATE/container-$east_container_id"
run_deploy_mode down "$deploy_services" isolated --delete-state --confirm "$deploy_services" >/dev/null
unset FAKE_RUNTIME_UID_HOST FAKE_RUNTIME_UID FAKE_RUNTIME_GID
run_deploy_mode preflight deploy-services-next isolated >/dev/null
run_deploy_mode prepare deploy-services-next isolated >/dev/null
jq -e '.phaseStatus == "passed" and all(.targets[]; .creatingRunId == "deploy-services-next" and .newlyCreated.root == true)' \
  "$TEMP_DIR/output/deploy-services-next-state/prepare-ledger.json" >/dev/null
if grep -Eq 'compose([^\n]*)(down[[:space:]]+-v|down[[:space:]]+--volumes)|redis-cli([^\n]*)flush|mc([^\n]*)rm([^\n]*)--recursive' \
  "$REPO_ROOT/scripts/deploy/down.sh"; then fail 'Teardown contains an unscoped destructive operation.'; fi

: > "$LOG"
unset FAKE_DOWN_CONTINUITY_MODE FAKE_DOWN_TAIL_MODE
cp "$BASE_INVENTORY" "$INVENTORY"
if DEPLOY_TEST_FAILPOINT=after-evidence-before-passed-manifest run_deploy evidence-first-crash > "$TEMP_DIR/failure.log" 2>&1; then
    fail 'Evidence-first publication failpoint passed.'
fi
crash_manifest="$TEMP_DIR/output/evidence-first-crash-state/manifest.json"
crash_evidence="$TEMP_DIR/output/evidence-first-crash-evidence/preflight.json"
jq -e --slurp '.[0].phaseStatus == "running" and .[1].phaseStatus == "passed" and
  .[0].runId == .[1].runId and .[0].inventorySha256 == .[1].inventorySha256' "$crash_manifest" "$crash_evidence" >/dev/null
remote_count_before_recovery="$(grep -c '^ssh' "$LOG")"
run_deploy evidence-first-crash >/dev/null
remote_count_after_recovery="$(grep -c '^ssh' "$LOG")"
(( remote_count_after_recovery > remote_count_before_recovery )) || fail 'Interrupted publication recovery did not rerun remote checks.'
jq -e --slurp '.[0].phaseStatus == "passed" and .[1].phaseStatus == "passed" and
  .[0].runId == .[1].runId and .[0].inventorySha256 == .[1].inventorySha256 and
  .[0].source == .[1].source and .[0].targets == .[1].targets and .[0].checks == .[1].checks' \
  "$crash_manifest" "$crash_evidence" >/dev/null

if DEPLOY_TEST_FAILPOINT=before-manifest-rename run_deploy interrupted > "$TEMP_DIR/failure.log" 2>&1; then fail 'Manifest failpoint passed.'; fi
test ! -e "$TEMP_DIR/output/interrupted-state/manifest.json"
test ! -e "$TEMP_DIR/output/interrupted-evidence/preflight.json"
run_deploy interrupted >/dev/null

if grep -Eq '(^|[[:space:]])(build|load|up|start|stop|down|compose[[:space:]]+up|compose[[:space:]]+down)([[:space:]]|$)' "$LOG"; then fail 'Preflight invoked deployment mutation.'; fi
