#!/usr/bin/env bash
# Deployment contract shard body: preflight (part 3 of 3). Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

# A tampered registry path outside the approved upload subtree is rejected without deleting it.
tampered_state="$TEMP_DIR/output/tampered-upload-state"; tampered_evidence="$TEMP_DIR/output/tampered-upload-evidence"
mkdir -p "$tampered_state" "$tampered_evidence"; chmod 700 "$tampered_state" "$tampered_evidence"
tampered_path="$TEMP_DIR/remote/east/arbitrary-do-not-delete"; printf 'retain\n' > "$tampered_path"; chmod 600 "$tampered_path"
jq -cn --arg path "$tampered_path" --argjson target "$(jq -c '.cameraAgents[0]' "$INVENTORY")" \
  '[{phase:"up",kind:"upload-temp",target:$target,path:$path}]' > "$tampered_state/private-upload-registry.json"; chmod 600 "$tampered_state/private-upload-registry.json"
if PATH="$BIN:$PATH" "$REPO_ROOT/scripts/deploy:environment" preflight --inventory "$INVENTORY" --mode isolated --run-id tampered-upload \
  --state-root "$tampered_state" --evidence-root "$tampered_evidence" > "$TEMP_DIR/tampered-upload.log" 2>&1; then
  fail 'A registry path outside the approved private-upload subtree passed validation.'
fi
test -f "$tampered_path"

# Persistent bootstrap never accepts missing, reset, or behind local capture continuity when central history exists.
(
  source "$REPO_ROOT/scripts/deploy/common.sh"
  source "$REPO_ROOT/scripts/deploy/bootstrap.sh"
  local_continuity="$TEMP_DIR/local-continuity.json"; central_continuity="$TEMP_DIR/central-continuity.json"
  jq -n '{deviceId:"device-east",durable:{rawIngressDatabaseExists:true,captureSequences:[{agentId:"device-east",lastSequence:12}],
    artifactOutboxDatabaseExists:true,artifactOutboxMaximumRecordId:12,artifactOutboxMaximumAuditId:12,
    latestArtifacts:[{artifactId:"66666666-6666-6666-6666-666666666666",agentId:"device-east",captureSequence:12,status:"acknowledged"}],
    fleetDatabaseExists:true,fleetAgentInstanceId:"44444444-4444-4444-4444-444444444444",fleetMaximumSequence:12,fleetNextSequence:13}}' > "$local_continuity"
  jq -n '{maximumCaptureSequence:12,centralFrameCount:12,centralArtifactCount:1,
    latestArtifacts:[{artifactId:"66666666-6666-6666-6666-666666666666",captureSequence:12}],
    fleetAgentInstanceId:"44444444-4444-4444-4444-444444444444",maximumHeartbeatSequence:12}' > "$central_continuity"
  deploy_bootstrap_validate_persistent_capture_continuity persistent "$local_continuity" "$central_continuity" east
  jq '.durable.rawIngressDatabaseExists=false | .durable.captureSequences=[]' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 91; fi
  jq '.durable.captureSequences[0].lastSequence=0' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 92; fi
  jq '.durable.captureSequences[0].lastSequence=11' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 93; fi
  jq '.durable.artifactOutboxDatabaseExists=false | .durable.latestArtifacts=[]' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 94; fi
  jq '.durable.artifactOutboxMaximumRecordId=0 | .durable.artifactOutboxMaximumAuditId=0' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 95; fi
  jq '.durable.latestArtifacts[0].captureSequence=11' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 96; fi
  jq '.durable.latestArtifacts[0].agentId="device-west"' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 100; fi
  jq '.durable.fleetDatabaseExists=false | .durable.fleetAgentInstanceId=null' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 97; fi
  jq '.durable.fleetAgentInstanceId="55555555-5555-5555-5555-555555555555"' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 98; fi
  jq '.durable.fleetMaximumSequence=11 | .durable.fleetNextSequence=12' "$local_continuity" > "$TEMP_DIR/local-invalid.json"
  if deploy_bootstrap_validate_persistent_capture_continuity persistent "$TEMP_DIR/local-invalid.json" "$central_continuity" east >/dev/null 2>&1; then exit 99; fi
  deploy_bootstrap_validate_persistent_capture_continuity isolated "$TEMP_DIR/local-invalid.json" "$central_continuity" east
)

# The checked-in shape reference, example, and executable jq contract stay aligned.
jq -e '."$defs".applicationTarget.required == ["name","friendlyName","instanceId","applicationIdentity","catalogId","cookieName","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","internalEndpoint","publicEndpoint","trustedProxyAddresses","ports"] and
  .properties.schemaVersion.const == 8 and
  .properties.productRoot."$ref" == "#/$defs/runtimeRoot" and
  (.properties.catalogs.items."$ref" | endswith("/catalogInstallation")) and
  .properties.deployment.properties.limits.required == ["cpus","memory","sqlMemory","sqlMemoryLimitMb"] and
  .properties.deployment.allOf[0].then.properties.limits.properties.sqlMemory.type == "string" and
  .properties.images.required == ["distributionMode","artifactRoot","tag","builder","registryImmutableTags","logicHost","cameraAgent"] and
  (.properties.sharedServices.oneOf[1]."$ref" | endswith("/infrastructureTarget")) and
  (.properties.logicHost."$ref" | endswith("/applicationTarget")) and
  (."$defs".applicationTarget.allOf[0].properties.runtimeRoot."$ref" | endswith("/runtimeRoot")) and
  (."$defs".infrastructureTarget.properties.runtimeRoot."$ref" | endswith("/runtimeRoot")) and
  (."$defs".cameraAgentTarget.properties.runtimeRoot."$ref" | endswith("/runtimeRoot")) and
  ."$defs".cameraAgentTarget.properties.moduleConfigPath.pattern == "^/" and
  (."$defs".applicationTarget.properties.publicEndpoint.pattern | contains(":"))' "$REPO_ROOT/deploy/split-host/inventory.schema.json" >/dev/null
canonical_workloads="$REPO_ROOT/deploy/split-host/workloads/canonical-workloads.json"
jq -e '.schemaVersion == 1 and .profiles.W0.sourcePath == "deploy/split-host/workloads/w0-virtual-mono16.json" and
  .profiles.W0.warmupOperations == 0 and .profiles.W0.measuredOperations == 1 and .profiles.W0.width == 64 and
  .profiles.W0.height == 48 and .profiles.W0.pixelFormat == "Mono16" and .profiles.W0.seed == 2025 and
  .profiles.W1.sourcePath == "src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json" and
  .profiles.W1.warmupOperations == 5 and .profiles.W1.measuredOperations == 30 and .profiles.W1.width == 1936 and
  .profiles.W1.height == 1216 and .profiles.W1.pixelFormat == "Mono16" and .profiles.W1.seed == 2025 and
  .profiles.W2.sourcePath == "src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json" and
  .profiles.W2.warmupOperations == 5 and .profiles.W2.measuredOperations == 30 and .profiles.W2.width == 3096 and
  .profiles.W2.height == 2080 and .profiles.W2.pixelFormat == "BayerRggb16" and .profiles.W2.seed == 2025' "$canonical_workloads" >/dev/null
w0_profile="$REPO_ROOT/deploy/split-host/workloads/w0-virtual-mono16.json"
jq -e '.rig.readout == {
  roi:{x:0,y:0,width:64,height:48},binX:1,binY:1,binningAlgorithm:"IdentityV1",pixelFormat:"Mono16",
  sampleDepthBits:16,containerDepthBits:16,packing:"ByteAligned",storedCodeTransform:"IdentityV1",
  levelCodeSpace:"StoredContainer",blackLevel:0,whiteLevel:65535,strideBytes:128,
  byteOrder:"LittleEndian",cfaPattern:"None"}' "$w0_profile" >/dev/null
while IFS=$'\t' read -r source expected_file expected_config expected_options; do
  test "$(sha256sum "$REPO_ROOT/$source" | cut -d' ' -f1)" = "$expected_file"
  test "$(printf '%s' "$(jq -S -c . "$REPO_ROOT/$source")" | sha256sum | cut -d' ' -f1)" = "$expected_config"
  test "$(printf '%s' "$(jq -S -c '.module.options' "$REPO_ROOT/$source")" | sha256sum | cut -d' ' -f1)" = "$expected_options"
done < <(jq -r '.profiles[] | [.sourcePath,.sourceSha256,.configurationIdentitySha256,.optionsSha256] | @tsv' "$canonical_workloads")
(
  # shellcheck source=scripts/deploy/common.sh
  . "$REPO_ROOT/scripts/deploy/common.sh"
  # shellcheck source=scripts/deploy/inventory.sh
  . "$REPO_ROOT/scripts/deploy/inventory.sh"
  # shellcheck source=scripts/deploy/preflight.sh
  . "$REPO_ROOT/scripts/deploy/preflight.sh"
  # shellcheck source=scripts/deploy/run-state.sh
  . "$REPO_ROOT/scripts/deploy/run-state.sh"
  # shellcheck source=scripts/deploy/product-layout.sh
  . "$REPO_ROOT/scripts/deploy/product-layout.sh"
  # shellcheck source=scripts/deploy/transient-confirm.sh
  . "$REPO_ROOT/scripts/deploy/transient-confirm.sh"
  layout_inventory="$TEMP_DIR/canonical-layout.json"
  jq '
    .catalogs += [(.catalogs[0] | .catalogId="alternate" | .installRoot=(.installRoot | sub("test-production$"; "alternate")))] |
    .cameraAgents[1].catalogId="alternate"
  ' "$BASE_INVENTORY" > "$layout_inventory"
  deploy_validate_product_layout "$layout_inventory" isolated
  test "$(deploy_catalog_root "$layout_inventory" "$(jq -c '.cameraAgents[0]' "$layout_inventory")")" = \
    "$(jq -r '.productRoot' "$layout_inventory")/catalogs/test-production"
  test "$(deploy_catalog_root "$layout_inventory" "$(jq -c '.cameraAgents[1]' "$layout_inventory")")" = \
    "$(jq -r '.productRoot' "$layout_inventory")/catalogs/alternate"
  before_root="$(jq -r '.cameraAgents[0].runtimeRoot' "$layout_inventory")"
  before_project="$(deploy_compose_project "$layout_inventory" "$(jq -c '.cameraAgents[0]' "$layout_inventory")")"
  before_manifest="$(deploy_instance_manifest_json "$layout_inventory" "$(jq -c '.cameraAgents[0]' "$layout_inventory")" cameraAgent)"
  jq '.cameraAgents[0].friendlyName="Renamed camera"' "$layout_inventory" > "$TEMP_DIR/layout-renamed.json"
  deploy_validate_product_layout "$TEMP_DIR/layout-renamed.json" isolated
  test "$(jq -r '.cameraAgents[0].runtimeRoot' "$TEMP_DIR/layout-renamed.json")" = "$before_root"
  test "$(deploy_compose_project "$TEMP_DIR/layout-renamed.json" "$(jq -c '.cameraAgents[0]' "$TEMP_DIR/layout-renamed.json")")" = "$before_project"
  test "$(deploy_instance_manifest_json "$TEMP_DIR/layout-renamed.json" "$(jq -c '.cameraAgents[0]' "$TEMP_DIR/layout-renamed.json")" cameraAgent)" = "$before_manifest"
  reject_layout() {
    if deploy_validate_product_layout "$1" "${3:-isolated}" >/dev/null 2>&1; then
      fail "Invalid product layout was accepted: $2"
    fi
  }
  jq '.cameraAgents[1].instanceId=.cameraAgents[0].instanceId' "$layout_inventory" > "$TEMP_DIR/layout-invalid.json"; reject_layout "$TEMP_DIR/layout-invalid.json" uuid-collision
  jq '.cameraAgents[1].runtimeRoot=.cameraAgents[0].runtimeRoot' "$layout_inventory" > "$TEMP_DIR/layout-invalid.json"; reject_layout "$TEMP_DIR/layout-invalid.json" root-collision
  jq '.cameraAgents[1].catalogId="missing"' "$layout_inventory" > "$TEMP_DIR/layout-invalid.json"; reject_layout "$TEMP_DIR/layout-invalid.json" unknown-catalog
  jq '.catalogs[1].catalogId="Alternate"' "$layout_inventory" > "$TEMP_DIR/layout-invalid.json"; reject_layout "$TEMP_DIR/layout-invalid.json" invalid-catalog-id
  jq '.catalogs[1].catalogId=("a" * 33) | .cameraAgents[1].catalogId=("a" * 33) | .productRoot as $root | .catalogs[1].installRoot=($root+"/catalogs/"+.catalogs[1].catalogId)' "$layout_inventory" > "$TEMP_DIR/layout-invalid.json"; reject_layout "$TEMP_DIR/layout-invalid.json" overlong-catalog-id
  jq '.catalogs[1].installRoot=.cameraAgents[0].runtimeRoot+"/catalog"' "$layout_inventory" > "$TEMP_DIR/layout-invalid.json"; reject_layout "$TEMP_DIR/layout-invalid.json" unsafe-ancestry
  jq '.productRoot="/var/lib/hvo/skymonitor" | .productRoot as $root |
    .logicHost.runtimeRoot=($root+"/logichosts/"+.logicHost.instanceId) |
    .cameraAgents |= map(.runtimeRoot=($root+"/cameraagents/"+.instanceId)) |
    .catalogs |= map(.installRoot=($root+"/catalogs/"+.catalogId))' "$layout_inventory" > "$TEMP_DIR/layout-persistent.json"
  deploy_validate_product_layout "$TEMP_DIR/layout-persistent.json" persistent
  jq '.productRoot="/opt/not-skymonitor" | .productRoot as $root |
    .logicHost.runtimeRoot=($root+"/logichosts/"+.logicHost.instanceId) |
    .cameraAgents |= map(.runtimeRoot=($root+"/cameraagents/"+.instanceId)) |
    .catalogs |= map(.installRoot=($root+"/catalogs/"+.catalogId))' "$layout_inventory" > "$TEMP_DIR/layout-noncanonical-persistent.json"
  reject_layout "$TEMP_DIR/layout-noncanonical-persistent.json" persistent-root persistent
  jq -n '{schemaVersion:1,runId:"test-run",events:[{centralTransientEventId:"11111111-1111-4111-8111-111111111111",agentId:"synthetic-east"}]}' > "$TEMP_DIR/transient-allowlist.json"
  chmod 600 "$TEMP_DIR/transient-allowlist.json"
  deploy_transient_confirm_validate_allowlist "$TEMP_DIR/transient-allowlist.json" test-run
  jq '.events += [.events[0]]' "$TEMP_DIR/transient-allowlist.json" > "$TEMP_DIR/transient-allowlist-duplicate.json"
  chmod 600 "$TEMP_DIR/transient-allowlist-duplicate.json"
  if deploy_transient_confirm_validate_allowlist "$TEMP_DIR/transient-allowlist-duplicate.json" test-run >/dev/null 2>&1; then exit 101; fi
  jq '.events[0].unexpected=true' "$TEMP_DIR/transient-allowlist.json" > "$TEMP_DIR/transient-allowlist-extra.json"
  chmod 600 "$TEMP_DIR/transient-allowlist-extra.json"
  if deploy_transient_confirm_validate_allowlist "$TEMP_DIR/transient-allowlist-extra.json" test-run >/dev/null 2>&1; then exit 102; fi
  jq -n '{summary:{centralTransientEventId:"11111111-1111-4111-8111-111111111111",agentId:"synthetic-east",reviewState:1,
    effectiveClassification:1,effectiveMeteorSeverity:1,activeAssessmentId:"22222222-2222-4222-8222-222222222222",eTag:"\"etag\""},
    event:{reviews:[{reviewId:"33333333-3333-4333-8333-333333333333",createdUtc:"2026-08-15T00:00:10Z",assessmentId:"22222222-2222-4222-8222-222222222222",
      disposition:1,reviewerIdentity:"redacted",reasonCodes:["synthetic-campaign-test-run"]}],
      notifications:[{notificationId:"44444444-4444-4444-8444-444444444444",createdUtc:"2026-08-15T00:00:20Z",assessmentId:"22222222-2222-4222-8222-222222222222",state:1}]}}' \
    > "$TEMP_DIR/transient-detail.json"
  deploy_transient_confirm_detail_eligible "$TEMP_DIR/transient-detail.json" "11111111-1111-4111-8111-111111111111" synthetic-east synthetic-campaign-test-run
  deploy_transient_confirm_audit_ready "$TEMP_DIR/transient-detail.json" "33333333-3333-4333-8333-333333333333" \
    "22222222-2222-4222-8222-222222222222" synthetic-campaign-test-run
  jq '.event.notifications[0].assessmentId="55555555-5555-4555-8555-555555555555"' "$TEMP_DIR/transient-detail.json" > "$TEMP_DIR/transient-wrong-notification.json"
  if deploy_transient_confirm_audit_ready "$TEMP_DIR/transient-wrong-notification.json" "33333333-3333-4333-8333-333333333333" \
    "22222222-2222-4222-8222-222222222222" synthetic-campaign-test-run; then exit 103; fi
  jq '.event.notifications[0].createdUtc="2026-08-15T00:00:00Z"' "$TEMP_DIR/transient-detail.json" > "$TEMP_DIR/transient-old-notification.json"
  if deploy_transient_confirm_audit_ready "$TEMP_DIR/transient-old-notification.json" "33333333-3333-4333-8333-333333333333" \
    "22222222-2222-4222-8222-222222222222" synthetic-campaign-test-run; then exit 104; fi
  jq -n '{messages:[{iD:"existing-message",subject:"SkyMonitor meteor review",created:"2026-08-15T00:01:00Z",
    to:[{address:"split-host-owner@hvo.local"}],snippet:"Reviewed meteor event 11111111-1111-4111-8111-111111111111 is available in SkyMonitor."}]}' \
    > "$TEMP_DIR/transient-mailpit.json"
  test -z "$(deploy_transient_confirm_mailpit_id "$TEMP_DIR/transient-mailpit.json" '["existing-message"]' \
    "11111111-1111-4111-8111-111111111111" split-host-owner@hvo.local 2026-08-15T00:00:00Z false)"
  test "$(deploy_transient_confirm_mailpit_id "$TEMP_DIR/transient-mailpit.json" '["existing-message"]' \
    "11111111-1111-4111-8111-111111111111" split-host-owner@hvo.local 2026-08-15T00:00:00Z true)" = existing-message
  jq -n '{schemaVersion:1,runId:"test-run",mode:"isolated",inventorySha256:("a"*64),sourceRevision:("b"*40),allowlistSha256:("c"*64),
    phaseStatus:"passed",startedAt:"2026-08-15T00:00:00Z",completedAt:"2026-08-15T00:01:00Z",
    events:[{centralTransientEventId:"11111111-1111-4111-8111-111111111111",agentId:"synthetic-east",
      reviewId:"33333333-3333-4333-8333-333333333333",notificationId:"44444444-4444-4444-8444-444444444444",
      requestSha256:("d"*64),mailpitMessageId:"message-1",recipientSha256:("e"*64),messageSnippetSha256:("f"*64)}]}' \
    > "$TEMP_DIR/transient-evidence.json"
  chmod 600 "$TEMP_DIR/transient-evidence.json"
  deploy_transient_confirm_validate_evidence "$TEMP_DIR/transient-evidence.json" test-run "$(printf 'a%.0s' {1..64})" \
    "$(printf 'b%.0s' {1..40})" "$(printf 'c%.0s' {1..64})" "$TEMP_DIR/transient-allowlist.json"
  jq 'del(.events[0].notificationId)' "$TEMP_DIR/transient-evidence.json" > "$TEMP_DIR/transient-evidence-tampered.json"
  chmod 600 "$TEMP_DIR/transient-evidence-tampered.json"
  if deploy_transient_confirm_validate_evidence "$TEMP_DIR/transient-evidence-tampered.json" test-run "$(printf 'a%.0s' {1..64})" \
    "$(printf 'b%.0s' {1..40})" "$(printf 'c%.0s' {1..64})" "$TEMP_DIR/transient-allowlist.json"; then exit 105; fi
  deploy_validate_inventory "$REPO_ROOT/deploy/split-host/inventory.example.yml" isolated
  jq '.deployment.workload.profiles={W1:{path:"/tmp/arbitrary-same-shape.json",sha256:("a"*64)}}' "$BASE_INVENTORY" > "$TEMP_DIR/arbitrary-workload-inventory.json"
  if deploy_validate_inventory "$TEMP_DIR/arbitrary-workload-inventory.json" isolated >/dev/null 2>&1; then exit 100; fi
  jq --arg owner "$(id -un)" '.sharedServices={name:"shared",sshHost:"shared@example",dockerContext:"shared-context",expectedArchitecture:"amd64",
    expectedHostName:"shared-node",expectedHostIdentity:"shared-machine",expectedDockerDaemonIdentity:"shared-daemon",runtimeRoot:"/srv/hvo/shared",
    runtimeOwner:$owner,ports:[1433,6379,2525,8025]} |
    .deployment.services.mode="deploy" | .deployment.services.smtp.kind="mailpit" | .deployment.services.smtp.ports=[2525,8025] |
    .deployment.services.images={sqlServer:("registry.example/sql@sha256:"+("a"*64)),redis:("registry.example/redis@sha256:"+("b"*64)),
      mailpit:("registry.example/mailpit@sha256:"+("e"*64))} |
    .deployment.limits.sqlMemory="4G" | .deployment.limits.sqlMemoryLimitMb=3072' "$BASE_INVENTORY" > "$TEMP_DIR/shared-valid.json"
  deploy_validate_inventory "$TEMP_DIR/shared-valid.json" isolated
  jq '.deployment.limits.sqlMemory=null' "$TEMP_DIR/shared-valid.json" > "$TEMP_DIR/shared-memory-invalid.json"
  if deploy_validate_inventory "$TEMP_DIR/shared-memory-invalid.json" isolated >/dev/null 2>&1; then exit 107; fi
  jq '.deployment.limits.sqlMemoryLimitMb=4096' "$TEMP_DIR/shared-valid.json" > "$TEMP_DIR/shared-memory-invalid.json"
  if deploy_validate_inventory "$TEMP_DIR/shared-memory-invalid.json" isolated >/dev/null 2>&1; then exit 108; fi
  jq '.deployment.limits.sqlMemory="010G"' "$TEMP_DIR/shared-valid.json" > "$TEMP_DIR/shared-memory-invalid.json"
  if deploy_validate_inventory "$TEMP_DIR/shared-memory-invalid.json" isolated >/dev/null 2>&1; then exit 110; fi
  test "$(deploy_memory_to_kib 4096M)" -eq 4194304
  test "$(deploy_memory_to_kib 4G)" -eq 4194304
  if deploy_memory_to_kib 08G >/dev/null 2>&1; then exit 111; fi
  jq '.deployment.limits.sqlMemory="9G" | .deployment.limits.sqlMemoryLimitMb=8192' \
    "$TEMP_DIR/shared-valid.json" > "$INVENTORY"
  if run_deploy sql-memory-insufficient > "$TEMP_DIR/sql-memory-insufficient.log" 2>&1; then exit 109; fi
  grep -Fq 'stage=preflight check=shared/host-capacity status=failed reason=below-sql-memory-limit' \
    "$TEMP_DIR/sql-memory-insufficient.log"
  cp "$BASE_INVENTORY" "$INVENTORY"
  jq '.sharedServices.expectedHostName=.logicHost.expectedHostName |
    .sharedServices.expectedHostIdentity=.logicHost.expectedHostIdentity |
    .sharedServices.expectedDockerDaemonIdentity=.logicHost.expectedDockerDaemonIdentity' \
    "$TEMP_DIR/shared-valid.json" > "$TEMP_DIR/shared-colocated.json"
  deploy_validate_inventory "$TEMP_DIR/shared-colocated.json" isolated
  jq '.cameraAgents[0].expectedHostIdentity=.logicHost.expectedHostIdentity' "$TEMP_DIR/shared-colocated.json" > "$TEMP_DIR/shared-colocated-invalid.json"
  if deploy_validate_inventory "$TEMP_DIR/shared-colocated-invalid.json" isolated >/dev/null 2>&1; then exit 106; fi
  jq 'del(.sharedServices.sshHost)' "$TEMP_DIR/shared-valid.json" > "$TEMP_DIR/shared-invalid.json"
  if deploy_validate_inventory "$TEMP_DIR/shared-invalid.json" isolated >/dev/null 2>&1; then exit 91; fi
  jq '.sharedServices.ports=[1433,6379,9000,2525]' "$TEMP_DIR/shared-valid.json" > "$TEMP_DIR/shared-invalid.json"
  if deploy_validate_inventory "$TEMP_DIR/shared-invalid.json" isolated >/dev/null 2>&1; then exit 94; fi
  jq '.deployment.services.redis.port=.deployment.services.sql.port | .sharedServices.ports=[1433,9000,2525,8025]' "$TEMP_DIR/shared-valid.json" > "$TEMP_DIR/shared-invalid.json"
  if deploy_validate_inventory "$TEMP_DIR/shared-invalid.json" isolated >/dev/null 2>&1; then exit 95; fi
  for accepted_root in /srv/hvo '/opt/hvo data/runtime'; do
    jq --arg root "$accepted_root" '.logicHost.runtimeRoot=$root' "$BASE_INVENTORY" > "$TEMP_DIR/root.json"
    deploy_validate_inventory "$TEMP_DIR/root.json" isolated
  done
  for rejected_root in /./srv /../srv /srv/./hvo /srv/../hvo /srv//hvo '/srv/hvo\root' '/srv/hvo,root' '/srv/hvo"root' $'/srv/hvo\tdata' $'/srv/hvo\ndata'; do
    jq --arg root "$rejected_root" '.logicHost.runtimeRoot=$root' "$BASE_INVENTORY" > "$TEMP_DIR/root.json"
    if deploy_validate_inventory "$TEMP_DIR/root.json" isolated >/dev/null 2>&1; then exit 92; fi
  done
  jq '.cameraAgents[0].moduleConfigPath="/tmp/module,config\\\".json"' "$BASE_INVENTORY" > "$TEMP_DIR/module-path.json"
  deploy_validate_inventory "$TEMP_DIR/module-path.json" isolated
  for accepted_catalog in 4.2 hyg-v4.2-p3-s2-r1 hyg-v42-fixture-1; do
    jq --arg version "$accepted_catalog" '.catalogs[0].version=$version' "$BASE_INVENTORY" > "$TEMP_DIR/catalog.json"
    deploy_validate_inventory "$TEMP_DIR/catalog.json" isolated
  done
  jq '.catalogs[0].version="catalog:v1"' "$BASE_INVENTORY" > "$TEMP_DIR/catalog.json"
  if deploy_validate_inventory "$TEMP_DIR/catalog.json" isolated >/dev/null 2>&1; then exit 93; fi
  # The filesystem provider takes no transport credentials, so routing one is a rejection.
  jq '.deployment.secretMappings += [{reference:"SQL_PASSWORD",key:"ObjectStorage__AccessKey"}]' "$BASE_INVENTORY" > "$TEMP_DIR/mapping-invalid.json"
  if deploy_validate_inventory "$TEMP_DIR/mapping-invalid.json" isolated >/dev/null 2>&1; then exit 96; fi
)
runtime_pattern="$(jq -r '."$defs".runtimeRoot.pattern' "$REPO_ROOT/deploy/split-host/inventory.schema.json")"
for accepted_root in /srv/hvo '/opt/hvo data/runtime'; do
    jq -en --arg value "$accepted_root" --arg pattern "$runtime_pattern" '$value | test($pattern)' >/dev/null || fail "Schema pattern rejected $accepted_root"
done
for rejected_root in /./srv /../srv /srv/./hvo /srv/../hvo /srv//hvo '/srv/hvo\root' '/srv/hvo,root' '/srv/hvo"root' $'/srv/hvo\tdata' $'/srv/hvo\ndata'; do
    if jq -en --arg value "$rejected_root" --arg pattern "$runtime_pattern" '$value | test($pattern)' >/dev/null; then fail "Schema pattern accepted $rejected_root"; fi
done
catalog_pattern="$(jq -r '."$defs".catalogInstallation.properties.version.pattern' "$REPO_ROOT/deploy/split-host/inventory.schema.json")"
for accepted_catalog in 4.2 hyg-v4.2-p3-s2-r1 hyg-v42-fixture-1; do
    jq -en --arg value "$accepted_catalog" --arg pattern "$catalog_pattern" '$value | test($pattern)' >/dev/null || fail "Schema pattern rejected catalog $accepted_catalog"
done
if jq -en --arg value 'catalog:v1' --arg pattern "$catalog_pattern" '$value | test($pattern)' >/dev/null; then fail 'Schema pattern accepted colon catalog version.'; fi

: > "$LOG"; : > "$REMOTE_LOG"
output="$(run_deploy valid)"
[[ "$output" == *'stage=preflight check=complete status=passed reason=read-only'* ]] || fail 'Valid preflight did not pass.'
manifest="$TEMP_DIR/output/valid-state/manifest.json"
evidence="$TEMP_DIR/output/valid-evidence/preflight.json"
jq -e '.phaseStatus == "passed" and (.targets | length) == 3 and
  ([.targets[].dockerComposeVersion] | all(. == "v2.39.1")) and
  (.targets[] | select(.name == "logic") | .architecture == "amd64") and
  (.targets[] | select(.name == "east") | .architecture == "arm64") and
  (.targets[] | select(.name == "east") | .deploymentState == "needs_prepare")' "$manifest" >/dev/null
test "$(stat -c %a "$TEMP_DIR/output/valid-state")" = 700
test "$(stat -c %a "$manifest")" = 600
test "$(stat -c %a "$evidence")" = 600
grep -Fq $'docker\t--context\teast-context\tinfo' "$LOG"
grep -Fq $'docker\t--context\teast-context\tcompose\tversion\t--short' "$LOG"
grep -Fq $'east@example\tbash\t-s\t--\t127.0.0.1' "$LOG"
! grep -Fq $'east@example\tbash\t-s\t--\thttps://public.example.test:' "$LOG"
grep -Fq 'timeout 8 bash -c' "$REMOTE_LOG"
! grep -Fq 'curl --silent --show-error' "$REMOTE_LOG"
if grep -Eq '(^|[[:space:]])(build|load|up|start|stop|down|rm|create|mkdir|install|touch)([[:space:]]|$)' "$REMOTE_LOG"; then fail 'Remote preflight script contains mutation commands.'; fi
if grep -Fq 'fixture-secret-never-print' "$LOG" || grep -Fq 'fixture-secret-never-print' "$REMOTE_LOG" ||
   grep -R -Fq 'fixture-secret-never-print' "$TEMP_DIR/output"; then fail 'Logs or evidence exposed a secret.'; fi
export FAKE_CAPTURE_REMOTE_SCRIPTS=false

mkdir -p "$TEMP_DIR/remote/east space"
jq --arg product "$TEMP_DIR/remote/east space/skymonitor" '.productRoot=$product |
  .logicHost.runtimeRoot=($product+"/logichosts/"+.logicHost.instanceId) |
  .cameraAgents[0].runtimeRoot=($product+"/cameraagents/"+.cameraAgents[0].instanceId) |
  .cameraAgents[1].runtimeRoot=($product+"/cameraagents/"+.cameraAgents[1].instanceId) |
  .catalogs |= map(.installRoot=($product+"/catalogs/"+.catalogId))' "$BASE_INVENTORY" > "$INVENTORY"
run_deploy spaced-runtime >/dev/null
jq -e --arg root "$TEMP_DIR/remote/east space/skymonitor/cameraagents/22222222-2222-4222-8222-222222222222" '.targets[] | select(.name == "east") | .runtimeRoot == $root' \
  "$TEMP_DIR/output/spaced-runtime-state/manifest.json" >/dev/null
cp "$BASE_INVENTORY" "$INVENTORY"

# One correlated host/daemon may carry LogicHost and multiple UUID-isolated CameraAgents.
jq '.logicHost as $logic | .cameraAgents |= map(.sshHost=$logic.sshHost | .dockerContext=$logic.dockerContext |
  .expectedArchitecture=$logic.expectedArchitecture | .expectedHostName=$logic.expectedHostName |
  .expectedHostIdentity=$logic.expectedHostIdentity | .expectedDockerDaemonIdentity=$logic.expectedDockerDaemonIdentity)' \
  "$BASE_INVENTORY" > "$INVENTORY"
run_deploy same-host-topology >/dev/null
jq -e '.phaseStatus == "passed" and (.targets | length) == 3' "$TEMP_DIR/output/same-host-topology-state/manifest.json" >/dev/null
jq '.cameraAgents[1].ownerPasswordSecretReference=.cameraAgents[0].ownerPasswordSecretReference' "$BASE_INVENTORY" > "$INVENTORY"
expect_failure 'schema-or-value-invalid' duplicate-owner-password-reference
cp "$BASE_INVENTORY" "$INVENTORY"

# Context selection must correlate the Docker daemon host name with the SSH hostname.
FAKE_DOCKER_WRONG_HOST_CONTEXT=east-context expect_failure 'ssh-docker-correlation status=failed reason=mismatch' wrong-context-host
FAKE_DOCKER_MISMATCH=east-context expect_failure 'ssh-docker-correlation status=failed reason=mismatch' daemon-mismatch
FAKE_DOCKER_INFO_MALFORMED=architecture expect_failure 'docker-daemon status=failed reason=invalid-response' docker-architecture-malformed
FAKE_DOCKER_INFO_MALFORMED=os expect_failure 'docker-daemon status=failed reason=invalid-response' docker-os-malformed
FAKE_DOCKER_INFO_MALFORMED=version expect_failure 'docker-daemon status=failed reason=invalid-response' docker-version-malformed
FAKE_DOCKER_UNSUPPORTED_ARCH=east-context expect_failure 'docker-daemon status=failed reason=unsupported-architecture' docker-architecture-unsupported
! grep -Fq 'ppc64le' "$TEMP_DIR/failure.log" || fail 'Unsupported Docker architecture leaked raw output.'
FAKE_COMPOSE_MISSING=east-context expect_failure 'docker-compose status=failed reason=unavailable' compose-missing
FAKE_SSH_UNREACHABLE=east expect_failure 'ssh status=failed reason=unreachable-or-invalid-response' ssh-unreachable
FAKE_MISSING_BASH_HOST=east expect_failure 'ssh status=failed reason=tool-unavailable' missing-bash

# The actual remote Bash scripts exercise reachable and unreachable TCP/HTTP paths.
jq --argjson port "$UNREACHABLE_PORT" '.serviceEndpoints[0].port=$port' "$BASE_INVENTORY" > "$INVENTORY"
expect_failure 'test-service status=failed reason=unreachable' tcp-unreachable
jq --argjson port "$UNREACHABLE_PORT" '.logicHost.publicEndpoint=("https://127.0.0.1:"+($port|tostring))' "$BASE_INVENTORY" > "$INVENTORY"
run_deploy http-deferred >/dev/null
jq -e '.checks[] | select(.target == "east") | select(.check == "logic-public-authority") |
  .status == "passed" and .reason == "deferred-until-up"' "$TEMP_DIR/output/http-deferred-state/manifest.json" >/dev/null
cp "$BASE_INVENTORY" "$INVENTORY"

# Missing socket inspection and both host/Docker port conflicts fail closed.
FAKE_MISSING_SS_HOST=east expect_failure 'ports status=failed reason=inspector-unavailable' missing-ss
FAKE_MISSING_TIMEOUT_HOST=east expect_failure 'test-service status=failed reason=tool-unavailable' missing-timeout
for oidc_failure in issuer token malformed oversized status; do
  (
    case "$oidc_failure" in
      issuer) export FAKE_OIDC_ISSUER_MISMATCH=true ;;
      token) export FAKE_OIDC_TOKEN_MISMATCH=true ;;
      malformed) export FAKE_OIDC_MALFORMED=true ;;
      oversized) export FAKE_OIDC_OVERSIZED=true ;;
      status) export FAKE_OIDC_STATUS=302 ;;
    esac
    source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/transport.sh"
    if deploy_transport_oidc_ready east@example "https://public.example.test:$HTTP_PORT"; then exit 91; fi
  )
done
FAKE_PORT_CONFLICT_HOST=east expect_failure 'ports status=failed reason=conflict' host-port-conflict
FAKE_DOCKER_PORT_CONFLICT="$EAST_PORT" expect_failure 'containers status=failed reason=port-conflict' docker-port-conflict

# Every existing runtime component is lstat-checked; trusted root ownership may require preparation.
mkdir -m 700 "$TEMP_DIR/remote/skymonitor"
mv "$TEMP_DIR/remote/skymonitor" "$TEMP_DIR/remote/skymonitor-real"
ln -s "$TEMP_DIR/remote/skymonitor-real" "$TEMP_DIR/remote/skymonitor"
expect_failure 'noncanonical-product-root' runtime-symlink
rm "$TEMP_DIR/remote/skymonitor"; mv "$TEMP_DIR/remote/skymonitor-real" "$TEMP_DIR/remote/skymonitor"
mkdir -m 700 "$TEMP_DIR/remote/skymonitor/logichosts" "$TEMP_DIR/remote/skymonitor/cameraagents"

# Secret diagnostics are bounded, and unsafe local lock entries are never followed or truncated.
chmod 644 "$SECRET_FILE"; : > "$LOG"; expect_failure 'secret-source-must-be-owner-only' secret-mode; test ! -s "$LOG"; chmod 600 "$SECRET_FILE"
FAKE_STAT_ERROR_PATH="$SECRET_FILE" expect_failure 'secret-source-stat-failed' secret-stat-error
FAKE_GREP_ERROR=true expect_failure 'required-reference-scan-failed' secret-grep-error
mkdir -m 700 "$TEMP_DIR/output/lock-symlink-state" "$TEMP_DIR/output/lock-symlink-evidence"
printf 'preserve-lock-target\n' > "$TEMP_DIR/lock-target"; ln -s "$TEMP_DIR/lock-target" "$TEMP_DIR/output/lock-symlink-state/run.lock"
expect_failure 'unsafe-lock-entry' lock-symlink; test "$(<"$TEMP_DIR/lock-target")" = preserve-lock-target
mkdir -m 700 "$TEMP_DIR/output/lock-hardlink-state" "$TEMP_DIR/output/lock-hardlink-evidence"
printf 'preserve-hardlink-target\n' > "$TEMP_DIR/hardlink-target"; ln "$TEMP_DIR/hardlink-target" "$TEMP_DIR/output/lock-hardlink-state/run.lock"
expect_failure 'unsafe-lock-entry' lock-hardlink; test "$(<"$TEMP_DIR/hardlink-target")" = preserve-hardlink-target

# Static strictness includes explicit endpoint ports and shared target shape.
jq '.logicHost.publicEndpoint="http://127.0.0.1"' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' missing-public-port
: > "$LOG"
jq '.logicHost.publicEndpoint |= sub("^https:";"http:")' "$BASE_INVENTORY" > "$INVENTORY"
run_deploy isolated-logic-http >/dev/null
jq -e '.phaseStatus == "passed"' "$TEMP_DIR/output/isolated-logic-http-state/manifest.json" >/dev/null
jq '.logicHost.internalEndpoint="http://127.0.0.1:12345"' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' internal-port-mismatch
jq '.logicHost.runtimeRoot="/srv/../private"' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' traversal
jq '.logicHost.extra="rejected"' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' unknown
jq '.images.tag="latest"' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' mutable-image-tag
jq '.images.logicHost.repository="https://user:password@registry.example/logichost"' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' credential-image-url
jq '.images.distributionMode="archive" | .images.registryImmutableTags=false | .images.artifactRoot="relative/artifacts"' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' relative-artifact-root
jq '.images.registryImmutableTags=false' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' mutable-registry-policy
jq '.images.builder.endpoint="remote-context"' "$BASE_INVENTORY" > "$INVENTORY"; expect_failure 'schema-or-value-invalid' declared-remote-builder

# Duplicate secret entries are rejected globally before transport for every controlled secret class.
for secret_case in sql redis certificate owner bootstrap; do
    duplicate_reference="DUPLICATE_${secret_case^^}"
    cp "$SECRET_FILE" "$TEMP_DIR/duplicate-secret.base"
    printf '%s=first\n%s=second\n' "$duplicate_reference" "$duplicate_reference" >> "$SECRET_FILE"
    case "$secret_case" in
      sql) jq --arg reference "$duplicate_reference" '.secretSource.requiredReferences += [$reference] | .deployment.services.sql.adminSecretReference=$reference' "$BASE_INVENTORY" > "$INVENTORY" ;;
      redis) jq --arg reference "$duplicate_reference" '.secretSource.requiredReferences += [$reference] | .deployment.services.redis.adminSecretReference=$reference' "$BASE_INVENTORY" > "$INVENTORY" ;;
      certificate) jq --arg reference "$duplicate_reference" '.secretSource.requiredReferences += [$reference] |
        .deployment.certificates.signingPasswordReference=$reference |
        .deployment.secretMappings += [{reference:$reference,key:"OpenIddictCertificates__SigningPassword"}]' "$BASE_INVENTORY" > "$INVENTORY" ;;
      owner) jq --arg reference "$duplicate_reference" '.secretSource.requiredReferences += [$reference] | .cameraAgents[0].ownerPasswordSecretReference=$reference' "$BASE_INVENTORY" > "$INVENTORY" ;;
      bootstrap) jq --arg reference "$duplicate_reference" '.secretSource.requiredReferences += [$reference] | .deployment.deviceBootstrap.clientSecretReference=$reference' "$BASE_INVENTORY" > "$INVENTORY" ;;
    esac
    : > "$LOG"
    expect_failure 'duplicate-secret-reference' "duplicate-$secret_case"
    test ! -s "$LOG" || fail "Duplicate $secret_case secret reached transport."
    mv "$TEMP_DIR/duplicate-secret.base" "$SECRET_FILE"
done
cp "$BASE_INVENTORY" "$INVENTORY"
cp "$SECRET_FILE" "$TEMP_DIR/duplicate-secret.base"
printf 'UNUSED_DUPLICATE=first\nUNUSED_DUPLICATE=second\n' >> "$SECRET_FILE"
: > "$LOG"
expect_failure 'duplicate-secret-reference' duplicate-unrequired
test ! -s "$LOG" || fail 'Unrequired duplicate secret reached transport.'
mv "$TEMP_DIR/duplicate-secret.base" "$SECRET_FILE"
(
  # shellcheck source=scripts/deploy/up.sh
  source "$REPO_ROOT/scripts/deploy/up.sh"
  printf 'EXACT=one\nEXACT=two\n' > "$TEMP_DIR/extraction-duplicate.env"
  printf 'CONTROL=before\rafter\n' > "$TEMP_DIR/extraction-control.env"
  chmod 600 "$TEMP_DIR/extraction-duplicate.env" "$TEMP_DIR/extraction-control.env"
  if deploy_secret_value "$TEMP_DIR/extraction-duplicate.env" EXACT >/dev/null 2>&1; then exit 91; fi
  if deploy_secret_value "$TEMP_DIR/extraction-duplicate.env" MISSING >/dev/null 2>&1; then exit 92; fi
  if deploy_secret_value "$TEMP_DIR/extraction-control.env" CONTROL >/dev/null 2>&1; then exit 93; fi
)

# Every secret class rejects carriage returns and other control bytes before rendering.
for secret_case in sql redis client certificate; do
    control_reference="CONTROL_${secret_case^^}"
    cp "$SECRET_FILE" "$TEMP_DIR/control-secret.base"
    printf '%s=before\rafter\n' "$control_reference" >> "$SECRET_FILE"
    case "$secret_case" in
      sql) jq --arg reference "$control_reference" '.secretSource.requiredReferences += [$reference] | .deployment.services.sql.adminSecretReference=$reference' "$BASE_INVENTORY" > "$INVENTORY" ;;
      redis) jq --arg reference "$control_reference" '.secretSource.requiredReferences += [$reference] | .deployment.services.redis.adminSecretReference=$reference' "$BASE_INVENTORY" > "$INVENTORY" ;;
      client) jq --arg reference "$control_reference" '.secretSource.requiredReferences += [$reference] | .deployment.deviceBootstrap.clientSecretReference=$reference' "$BASE_INVENTORY" > "$INVENTORY" ;;
      certificate) jq --arg reference "$control_reference" '.secretSource.requiredReferences += [$reference] |
        .deployment.certificates.signingPasswordReference=$reference |
        .deployment.secretMappings += [{reference:$reference,key:"OpenIddictCertificates__SigningPassword"}]' "$BASE_INVENTORY" > "$INVENTORY" ;;
    esac
    expect_failure 'control-character-rejected' "control-$secret_case"
    mv "$TEMP_DIR/control-secret.base" "$SECRET_FILE"
done
cp "$BASE_INVENTORY" "$INVENTORY"

# Persistent mode requires a production catalog and HTTPS on LogicHost, every CameraAgent, and shared services.
jq '(.logicHost.publicEndpoint, .cameraAgents[].publicEndpoint) |= sub("^http:";"https:") |
  .logicHost.trustedProxyAddresses=["127.0.0.1"] | (.cameraAgents[].trustedProxyAddresses)=["127.0.0.1"]' "$BASE_INVENTORY" > "$TEMP_DIR/persistent-base.json"
jq '.logicHost.publicEndpoint |= sub("^https:";"http:")' "$TEMP_DIR/persistent-base.json" > "$INVENTORY"; expect_persistent_invalid persistent-logic-http
jq '.cameraAgents[0].publicEndpoint |= sub("^https:";"http:")' "$TEMP_DIR/persistent-base.json" > "$INVENTORY"; expect_persistent_invalid persistent-east-http
jq '.cameraAgents[1].publicEndpoint |= sub("^https:";"http:")' "$TEMP_DIR/persistent-base.json" > "$INVENTORY"; expect_persistent_invalid persistent-west-http
jq '.logicHost.trustedProxyAddresses=[]' "$TEMP_DIR/persistent-base.json" > "$INVENTORY"; expect_persistent_invalid persistent-proxy-missing
jq '.catalogs[0].kind="fixture"' "$TEMP_DIR/persistent-base.json" > "$INVENTORY"; expect_persistent_invalid persistent-fixture-catalog
jq '.catalogs[0].kind="production" | .catalogs[0].allowFixture=false | .deployment.services.smtp.kind="production"' \
  "$TEMP_DIR/persistent-base.json" > "$TEMP_DIR/persistent-connectivity.json"
cp "$TEMP_DIR/persistent-connectivity.json" "$INVENTORY"
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode preflight persistent-connectivity-valid persistent >/dev/null
jq --argjson port "$UNREACHABLE_PORT" '.logicHost.publicEndpoint=("https://127.0.0.1:"+($port|tostring))' \
  "$TEMP_DIR/persistent-connectivity.json" > "$INVENTORY"
if run_deploy_mode preflight persistent-authority-unreachable persistent > "$TEMP_DIR/persistent-authority-unreachable.log" 2>&1; then
  fail 'Persistent preflight accepted an unreachable public authority.'
fi
grep -Fq 'logic-public-authority status=failed reason=unreachable' "$TEMP_DIR/persistent-authority-unreachable.log"
cp "$TEMP_DIR/persistent-connectivity.json" "$INVENTORY"
if FAKE_MISSING_CURL_HOST=east FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode preflight persistent-authority-no-curl persistent \
  > "$TEMP_DIR/persistent-authority-no-curl.log" 2>&1; then
  fail 'Persistent preflight accepted a CameraAgent host without curl.'
fi
grep -Fq 'logic-public-authority status=failed reason=tool-unavailable' "$TEMP_DIR/persistent-authority-no-curl.log"

# A successful run followed by a transport failure publishes failed, not stale passed, evidence.
cp "$BASE_INVENTORY" "$INVENTORY"; run_deploy stale-evidence >/dev/null
FAKE_SSH_UNREACHABLE=east expect_failure 'ssh status=failed reason=unreachable-or-invalid-response' stale-evidence
jq -e '.phaseStatus == "failed"' "$TEMP_DIR/output/stale-evidence-state/manifest.json" >/dev/null
jq -e '.phaseStatus == "failed"' "$TEMP_DIR/output/stale-evidence-evidence/preflight.json" >/dev/null
if grep -Fq '"phaseStatus":"passed"' "$TEMP_DIR/output/stale-evidence-evidence/preflight.json"; then fail 'Failed rerun retained passed evidence.'; fi

run_deploy evidence-symlink >/dev/null
printf 'preserve-evidence-target\n' > "$TEMP_DIR/evidence-target"
rm "$TEMP_DIR/output/evidence-symlink-evidence/preflight.json"
ln -s "$TEMP_DIR/evidence-target" "$TEMP_DIR/output/evidence-symlink-evidence/preflight.json"
FAKE_SSH_UNREACHABLE=east expect_failure 'completed-check-or-target-mismatch' evidence-symlink
test "$(<"$TEMP_DIR/evidence-target")" = preserve-evidence-target
test -L "$TEMP_DIR/output/evidence-symlink-evidence/preflight.json"

# Rejected inventory, resume, and corrupt-manifest invocations preserve prior valid evidence.
cp "$BASE_INVENTORY" "$INVENTORY"
run_deploy completed-check >/dev/null
cp "$TEMP_DIR/output/completed-check-evidence/preflight.json" "$TEMP_DIR/completed-check.evidence"
jq '.checks[0].status="failed"' "$TEMP_DIR/output/completed-check-state/manifest.json" > "$TEMP_DIR/completed-check.manifest"
mv "$TEMP_DIR/completed-check.manifest" "$TEMP_DIR/output/completed-check-state/manifest.json"; chmod 600 "$TEMP_DIR/output/completed-check-state/manifest.json"
cp "$TEMP_DIR/output/completed-check-state/manifest.json" "$TEMP_DIR/completed-check.tampered"
expect_failure 'completed-check-or-target-mismatch' completed-check
cmp -s "$TEMP_DIR/completed-check.tampered" "$TEMP_DIR/output/completed-check-state/manifest.json" || fail 'Rejected completed-check resume rewrote manifest.'
cmp -s "$TEMP_DIR/completed-check.evidence" "$TEMP_DIR/output/completed-check-evidence/preflight.json" || fail 'Rejected completed-check resume rewrote evidence.'
run_deploy completed-target >/dev/null
cp "$TEMP_DIR/output/completed-target-evidence/preflight.json" "$TEMP_DIR/completed-target.evidence"
jq '.targets[0].hostIdentity="tampered-machine"' "$TEMP_DIR/output/completed-target-state/manifest.json" > "$TEMP_DIR/completed-target.manifest"
mv "$TEMP_DIR/completed-target.manifest" "$TEMP_DIR/output/completed-target-state/manifest.json"; chmod 600 "$TEMP_DIR/output/completed-target-state/manifest.json"
cp "$TEMP_DIR/output/completed-target-state/manifest.json" "$TEMP_DIR/completed-target.tampered"
expect_failure 'completed-check-or-target-mismatch' completed-target
cmp -s "$TEMP_DIR/completed-target.tampered" "$TEMP_DIR/output/completed-target-state/manifest.json" || fail 'Rejected completed-target resume rewrote manifest.'
cmp -s "$TEMP_DIR/completed-target.evidence" "$TEMP_DIR/output/completed-target-evidence/preflight.json" || fail 'Rejected completed-target resume rewrote evidence.'
run_deploy resume-hash >/dev/null
cp "$TEMP_DIR/output/resume-hash-evidence/preflight.json" "$TEMP_DIR/resume-hash.evidence"
jq '.catalogs[0].version="catalog-v2"' "$INVENTORY" > "$INVENTORY.changed" && mv "$INVENTORY.changed" "$INVENTORY"
expect_failure 'resume-contract-mismatch' resume-hash
cmp -s "$TEMP_DIR/resume-hash.evidence" "$TEMP_DIR/output/resume-hash-evidence/preflight.json" || fail 'Rejected resume changed prior evidence.'
cp "$BASE_INVENTORY" "$INVENTORY"; run_deploy invalid-inventory >/dev/null
cp "$TEMP_DIR/output/invalid-inventory-evidence/preflight.json" "$TEMP_DIR/invalid-inventory.evidence"
jq '.unexpected="invalid"' "$INVENTORY" > "$INVENTORY.changed" && mv "$INVENTORY.changed" "$INVENTORY"
expect_failure 'schema-or-value-invalid' invalid-inventory
cmp -s "$TEMP_DIR/invalid-inventory.evidence" "$TEMP_DIR/output/invalid-inventory-evidence/preflight.json" || fail 'Rejected inventory changed prior evidence.'
cp "$BASE_INVENTORY" "$INVENTORY"; run_deploy corrupt-manifest >/dev/null
cp "$TEMP_DIR/output/corrupt-manifest-evidence/preflight.json" "$TEMP_DIR/corrupt-manifest.evidence"
printf 'not-json\n' > "$TEMP_DIR/output/corrupt-manifest-state/manifest.json"
expect_failure 'invalid-existing-manifest' corrupt-manifest
cmp -s "$TEMP_DIR/corrupt-manifest.evidence" "$TEMP_DIR/output/corrupt-manifest-evidence/preflight.json" || fail 'Corrupt manifest invocation changed prior evidence.'
