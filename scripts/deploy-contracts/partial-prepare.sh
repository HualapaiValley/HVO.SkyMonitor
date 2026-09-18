#!/usr/bin/env bash
# Deployment contract shard body: partial-prepare. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

# Failed prepare cleanup is a separate, no-container recovery path. Normal down remains unavailable.
(
  # Recovery validates immutable schema 7 inputs without enabling them for normal deployment phases.
  . "$REPO_ROOT/scripts/deploy/common.sh"
  . "$REPO_ROOT/scripts/deploy/product-layout.sh"
  . "$REPO_ROOT/scripts/deploy/inventory.sh"
  . "$REPO_ROOT/scripts/deploy/partial-prepare.sh"
  schema7_inventory="$TEMP_DIR/schema7-retained.inventory"
  jq '
    .schemaVersion=7 |
    del(.productRoot) |
    .logicHost |= del(.friendlyName,.instanceId,.applicationIdentity,.catalogId,.cookieName) |
    .cameraAgents |= map(del(.instanceId,.applicationIdentity,.catalogId,.cookieName)) |
    .catalog=(.catalogs[0] | {kind,version,sha256,length,rowCount}) |
    .deployment.catalog=(.catalogs[0] | {bundlePath,installRoot,allowFixture}) |
    del(.catalogs)
  ' "$BASE_INVENTORY" > "$schema7_inventory"
  deploy_validate_partial_prepare_inventory "$schema7_inventory" isolated
  TMPDIR="$TEMP_DIR/untrusted-missing-tmp" deploy_validate_partial_prepare_inventory "$schema7_inventory" isolated
  test "$(deploy_partial_prepare_project "$schema7_inventory" "$(jq -c '.logicHost' "$schema7_inventory")" logicHost)" = hvo-test-logic
  test "$(deploy_partial_prepare_project "$schema7_inventory" "$(jq -c '.cameraAgents[0]' "$schema7_inventory")" cameraAgent)" = hvo-test-east
  jq '.deployment.catalog.installRoot += ",retained"' "$schema7_inventory" > "$TEMP_DIR/schema7-historical-path.inventory"
  deploy_validate_partial_prepare_inventory "$TEMP_DIR/schema7-historical-path.inventory" isolated
  jq '.logicHost.instanceId="00000000-0000-4000-8000-000000000001"' "$schema7_inventory" > "$TEMP_DIR/schema7-invalid.inventory"
  if deploy_validate_partial_prepare_inventory "$TEMP_DIR/schema7-invalid.inventory" isolated >/dev/null 2>&1; then
    fail 'Schema 7 recovery accepted a schema-8-only target field.'
  fi
  jq '.cameraAgents[0].sshHost=.logicHost.sshHost' "$schema7_inventory" > "$TEMP_DIR/schema7-invalid.inventory"
  if deploy_validate_partial_prepare_inventory "$TEMP_DIR/schema7-invalid.inventory" isolated >/dev/null 2>&1; then
    fail 'Schema 7 recovery accepted a historically invalid target collision.'
  fi
  if PATH="$BIN:$PATH" "$REPO_ROOT/scripts/deploy:environment" preflight --inventory "$schema7_inventory" --mode isolated \
    --run-id schema7-normal-phase --state-root "$TEMP_DIR/schema7-normal-state" --evidence-root "$TEMP_DIR/schema7-normal-evidence" >/dev/null 2>&1; then
    fail 'Normal deployment phase accepted schema 7 inventory.'
  fi
)

# Rebind a retained failed prepare to its immutable schema 7 bytes and exercise the full recovery command.
schema7_case=partial-schema7
write_prepare_case "$schema7_case" isolated
run_prepare_preflight "$schema7_case" isolated
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' "$schema7_case" isolated
schema7_logic_root="$(inventory_runtime_root logic)"
schema7_logic_lock="${schema7_logic_root%/*}/.hvo-deploy-prepare-$(printf 'v1\ntarget=logic\nroot=%s\n' "$schema7_logic_root" | sha256sum | cut -c1-32).lock"
jq '
  .schemaVersion=7 |
  del(.productRoot) |
  .logicHost |= del(.friendlyName,.instanceId,.applicationIdentity,.catalogId,.cookieName) |
  .cameraAgents |= map(del(.instanceId,.applicationIdentity,.catalogId,.cookieName)) |
  .catalog=(.catalogs[0] | {kind,version,sha256,length,rowCount}) |
  .deployment.catalog=(.catalogs[0] | {bundlePath,installRoot,allowFixture}) |
  del(.catalogs)
' "$INVENTORY" > "$INVENTORY.changed"
mv "$INVENTORY.changed" "$INVENTORY"
schema7_hash="$(jq -S -c . "$INVENTORY" | sha256sum | cut -d' ' -f1)"
schema7_marker="$(printf 'v1\nmode=isolated\nrun=%s\ninventory=%s\ntarget=logic\nroot=%s\nowner=%s\n' \
  "$schema7_case" "$schema7_hash" "$schema7_logic_root" "$(id -un)" | sha256sum | cut -d' ' -f1)"
schema7_state="$TEMP_DIR/output/$schema7_case-state"
schema7_evidence="$TEMP_DIR/output/$schema7_case-evidence"
for schema7_path in "$schema7_state/manifest.json" "$schema7_evidence/preflight.json"; do
  jq --arg hash "$schema7_hash" '.inventorySha256=$hash' "$schema7_path" > "$schema7_path.changed"
  chmod 600 "$schema7_path.changed"; mv "$schema7_path.changed" "$schema7_path"
done
for schema7_path in "$schema7_state/prepare-ledger.json" "$schema7_state/prepare-manifest.json" "$schema7_evidence/prepare.json"; do
  jq --arg hash "$schema7_hash" --arg marker "$schema7_marker" \
    '.inventorySha256=$hash | (.targets[] | select(.target == "logic") | .markerDigest)=$marker' "$schema7_path" > "$schema7_path.changed"
  chmod 600 "$schema7_path.changed"; mv "$schema7_path.changed" "$schema7_path"
done
printf 'HVO-DEPLOY-ROOT\t1\nmarker\t%s\n' "$schema7_marker" > "$schema7_logic_root/.hvo-deploy/ownership"
printf 'HVO-DEPLOY-PREPARE-LOCK\t1\nmarker\t%s\n' "$schema7_marker" > "$schema7_logic_lock"
printf 'HVO-DEPLOY-PREPARE-STATE\t1\nmarker\t%s\ncreatingRun\t%s\nrootNew\ttrue\ncontrolNew\ttrue\nmarkerNew\ttrue\n' \
  "$schema7_marker" "$schema7_case" > "$schema7_logic_lock.state"
chmod 600 "$schema7_logic_root/.hvo-deploy/ownership" "$schema7_logic_lock" "$schema7_logic_lock.state"
if FAKE_PARTIAL_PREPARE_RESOURCE='logic-context:container:label=com.docker.compose.project=hvo-test-logic' \
  run_deploy_mode partial-prepare-cleanup "$schema7_case" isolated --dry-run > "$TEMP_DIR/schema7-partial.log" 2>&1; then
  fail 'Schema 7 recovery missed a historical-project Docker resource.'
fi
grep -Fq 'unexpected-deployment-resource' "$TEMP_DIR/schema7-partial.log"
if FAKE_PARTIAL_PREPARE_RESOURCE='east-context:container:label=com.docker.compose.project=hvo-test-east' \
  run_deploy_mode partial-prepare-cleanup "$schema7_case" isolated --dry-run > "$TEMP_DIR/schema7-partial.log" 2>&1; then
  fail 'Schema 7 recovery missed a historical camera-project Docker resource.'
fi
grep -Fq 'unexpected-deployment-resource' "$TEMP_DIR/schema7-partial.log"
run_deploy_mode partial-prepare-cleanup "$schema7_case" isolated --dry-run >/dev/null
jq -e --arg hash "$schema7_hash" '.phaseStatus == "passed" and .operation == "dry-run" and .inventorySha256 == $hash' \
  "$schema7_evidence/partial-prepare-inspect.json" >/dev/null

partial_case=partial-cleanup
write_prepare_case "$partial_case" isolated
cp "$INVENTORY" "$TEMP_DIR/$partial_case.inventory"
run_prepare_preflight "$partial_case" isolated
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' "$partial_case" isolated
partial_state="$TEMP_DIR/output/$partial_case-state"
partial_evidence="$TEMP_DIR/output/$partial_case-evidence"
partial_logic_root="$(inventory_runtime_root logic)"
partial_logic_lock="${partial_logic_root%/*}/.hvo-deploy-prepare-$(printf 'v1\ntarget=logic\nroot=%s\n' "$partial_logic_root" | sha256sum | cut -c1-32).lock"

run_partial_explicit() {
  local run="$1" mode="$2"; shift 2
  PATH="$BIN:$PATH" "$REPO_ROOT/scripts/deploy:environment" partial-prepare-cleanup --inventory "$INVENTORY" --mode "$mode" \
    --run-id "$run" --state-root "$partial_state" --evidence-root "$partial_evidence" "$@"
}
expect_partial_failure() {
  local expected="$1" run="$2" mode="$3"; shift 3
  if run_partial_explicit "$run" "$mode" "$@" > "$TEMP_DIR/partial-prepare-failure.log" 2>&1; then
    fail "Partial-prepare cleanup accepted an invalid boundary: $expected"
  fi
  [[ -z "$expected" ]] || grep -Fq "$expected" "$TEMP_DIR/partial-prepare-failure.log" || {
    /usr/bin/cat "$TEMP_DIR/partial-prepare-failure.log" >&2
    fail "Partial-prepare cleanup did not report $expected."
  }
  test -d "$partial_logic_root" || fail 'Rejected partial-prepare cleanup mutated the runtime root.'
}

expect_partial_failure '' "$partial_case" isolated
expect_partial_failure 'confirmation' "$partial_case" isolated --confirm wrong-run
expect_partial_failure '' "$partial_case" isolated --dry-run --confirm "$partial_case"
expect_partial_failure 'mismatch-or-not-passed' wrong-run isolated --dry-run
if run_deploy_mode down "$partial_case" isolated --delete-state --confirm "$partial_case" > "$TEMP_DIR/partial-normal-down.log" 2>&1; then
  fail 'Normal down accepted failed prepare state.'
fi
grep -Fq 'mismatch-or-not-passed' "$TEMP_DIR/partial-normal-down.log"

# Every exact inventory identity is hash-bound to retained preflight and prepare state.
for identity_case in hash installation target root owner; do
  case "$identity_case" in
    hash) jq '.catalogs[0].displayName="changed"' "$TEMP_DIR/$partial_case.inventory" > "$INVENTORY" ;;
    installation) jq '.installationId="changed-installation"' "$TEMP_DIR/$partial_case.inventory" > "$INVENTORY" ;;
    target) jq '.logicHost.expectedHostIdentity="changed-machine"' "$TEMP_DIR/$partial_case.inventory" > "$INVENTORY" ;;
    root) jq --arg root "$TEMP_DIR/remote/changed-skymonitor" '.productRoot=$root |
      .logicHost.runtimeRoot=($root+"/logichosts/"+.logicHost.instanceId) |
      .cameraAgents[0].runtimeRoot=($root+"/cameraagents/"+.cameraAgents[0].instanceId) |
      .cameraAgents[1].runtimeRoot=($root+"/cameraagents/"+.cameraAgents[1].instanceId) |
      .catalogs |= map(.installRoot=($root+"/catalogs/"+.catalogId))' "$TEMP_DIR/$partial_case.inventory" > "$INVENTORY" ;;
    owner) jq '.logicHost.runtimeOwner="missing-owner"' "$TEMP_DIR/$partial_case.inventory" > "$INVENTORY" ;;
  esac
  expect_partial_failure 'mismatch-or-not-passed' "$partial_case" isolated --dry-run
done
cp "$TEMP_DIR/$partial_case.inventory" "$INVENTORY"

FAKE_PREPARE_IDENTITY_MISMATCH_HOST=logic expect_partial_failure 'provenance-or-content-invalid' "$partial_case" isolated --dry-run
FAKE_DOCKER_MISMATCH=logic-context expect_partial_failure 'docker-daemon-correlation-mismatch' "$partial_case" isolated --dry-run

# A root absent at preflight cannot appear later without a prepare-ledger entry.
partial_east_root="$(inventory_runtime_root east)"
mkdir -m 700 "$partial_east_root"; printf 'unexpected\n' > "$partial_east_root/application-state"
expect_partial_failure 'unjournaled-prepare-artifacts' "$partial_case" isolated --dry-run
rm -rf "$partial_east_root"

cp "$partial_logic_root/.hvo-deploy/ownership" "$TEMP_DIR/partial-marker.valid"
printf 'tampered\n' > "$partial_logic_root/.hvo-deploy/ownership"
expect_partial_failure 'provenance-or-content-invalid' "$partial_case" isolated --dry-run
cp "$TEMP_DIR/partial-marker.valid" "$partial_logic_root/.hvo-deploy/ownership"; chmod 600 "$partial_logic_root/.hvo-deploy/ownership"
cp "$partial_logic_lock" "$TEMP_DIR/partial-lock.valid"
printf 'tampered\n' > "$partial_logic_lock"
expect_partial_failure 'provenance-or-content-invalid' "$partial_case" isolated --dry-run
cp "$TEMP_DIR/partial-lock.valid" "$partial_logic_lock"; chmod 600 "$partial_logic_lock"
cp "$partial_logic_lock.state" "$TEMP_DIR/partial-state.valid"
/usr/bin/sed 's/rootNew\ttrue/rootNew\tfalse/' "$TEMP_DIR/partial-state.valid" > "$partial_logic_lock.state"
expect_partial_failure 'provenance-or-content-invalid' "$partial_case" isolated --dry-run
cp "$TEMP_DIR/partial-state.valid" "$partial_logic_lock.state"; chmod 600 "$partial_logic_lock.state"

# Replacing a validated pathname with byte-identical content cannot authorize deletion.
rm -f "$FAKE_IMAGE_STATE/partial-prepare-replace-count"
FAKE_PARTIAL_PREPARE_REPLACE_PATH="$partial_logic_root/.hvo-deploy/ownership" \
  expect_partial_failure '' "$partial_case" isolated --confirm "$partial_case"
rm -f "$partial_logic_root/.hvo-deploy/ownership"
mv "$partial_logic_root/.hvo-deploy/ownership.race-original" "$partial_logic_root/.hvo-deploy/ownership"
rm -f "$FAKE_IMAGE_STATE/partial-prepare-replace-count"
FAKE_PARTIAL_PREPARE_REPLACE_PATH="$partial_logic_lock.state" \
  expect_partial_failure '' "$partial_case" isolated --confirm "$partial_case"
rm -f "$partial_logic_lock.state"
mv "$partial_logic_lock.state.race-original" "$partial_logic_lock.state"

# Replacement after the last pathname check is quarantined and rejected without deleting the replacement.
rm -f "$FAKE_IMAGE_STATE/partial-prepare-move-replaced"
FAKE_PARTIAL_PREPARE_REPLACE_ON_MOVE="$partial_logic_root/.hvo-deploy/ownership" \
  expect_partial_failure '' "$partial_case" isolated --confirm "$partial_case"
rm -f "$partial_logic_root/.hvo-deploy/ownership"
mv "$partial_logic_root/.hvo-deploy/ownership.race-original" "$partial_logic_root/.hvo-deploy/ownership"
rm -f "$FAKE_IMAGE_STATE/partial-prepare-move-replaced"

printf 'must-not-delete\n' > "$partial_logic_root/application-state"
expect_partial_failure 'provenance-or-content-invalid' "$partial_case" isolated --dry-run
rm "$partial_logic_root/application-state"
for resource_kind in container network volume; do
  rm -f "$FAKE_IMAGE_STATE/partial-prepare-resource-count"
  FAKE_PARTIAL_PREPARE_RESOURCE="logic-context:$resource_kind:label=com.docker.compose.project=hvo-test-11111111111141118111111111111111" \
    expect_partial_failure 'unexpected-deployment-resource' "$partial_case" isolated --dry-run
done
partial_container=hvo-skymonitor-11111111111141118111111111111111
printf '"unexpected-container"\n' > "$FAKE_IMAGE_STATE/container-$partial_container"
expect_partial_failure 'unexpected-deployment-resource' "$partial_case" isolated --dry-run
rm "$FAKE_IMAGE_STATE/container-$partial_container"
partial_network=hvo-test-11111111111141118111111111111111_default
printf '"unexpected-network"\n' > "$FAKE_IMAGE_STATE/network-logic-context-$partial_network"
expect_partial_failure 'unexpected-deployment-resource' "$partial_case" isolated --dry-run
rm "$FAKE_IMAGE_STATE/network-logic-context-$partial_network"

# Resource absence is rechecked after journaling and immediately before the first deletion.
rm -f "$FAKE_IMAGE_STATE/partial-prepare-resource-count"
FAKE_PARTIAL_PREPARE_RESOURCE="logic-context:container:label=io.hvoskymonitor.run-id=$partial_case" \
  FAKE_PARTIAL_PREPARE_RESOURCE_AFTER=2 \
  expect_partial_failure 'unexpected-deployment-resource' "$partial_case" isolated --confirm "$partial_case"
test -f "$partial_logic_root/.hvo-deploy/ownership" && test -f "$partial_logic_lock.state"

# Once correlated, absence checks use the pinned endpoint even if the context name is rebound.
rm -f "$FAKE_IMAGE_STATE/docker-context-rebind-logic-context" "$FAKE_IMAGE_STATE/partial-prepare-resource-count"
FAKE_DOCKER_CONTEXT_REBIND=logic-context \
  FAKE_PARTIAL_PREPARE_RESOURCE="logic-context:container:label=io.hvoskymonitor.run-id=$partial_case" \
  expect_partial_failure 'unexpected-deployment-resource' "$partial_case" isolated --dry-run

unrelated_resource="$FAKE_IMAGE_STATE/unrelated-resource"; printf 'preserve\n' > "$unrelated_resource"
run_deploy_mode partial-prepare-cleanup "$partial_case" isolated --dry-run >/dev/null
DOCKER_HOST=tcp://must-not-be-used.invalid:2376 DOCKER_TLS_VERIFY=1 DOCKER_CERT_PATH=/must-not-be-used \
  run_deploy_mode partial-prepare-cleanup "$partial_case" isolated --dry-run >/dev/null
test -d "$partial_logic_root" && test -f "$partial_logic_lock.state"
jq -e '.phaseStatus == "passed" and .operation == "dry-run" and
  (.targets | map({target,disposition}) == [{target:"logic",disposition:"cleanup-candidate"},{target:"east",disposition:"not-created"},{target:"west",disposition:"not-created"}]) and
  (.resources | length) == 0' "$partial_evidence/partial-prepare-inspect.json" >/dev/null
if grep -Fq "$TEMP_DIR/remote" "$partial_evidence/partial-prepare-inspect.json"; then fail 'Inspection evidence exposed runtime paths.'; fi

for boundary in marker control root lock-state lock; do
  if DEPLOY_TEST_FAILPOINT="after-partial-prepare-$boundary:logic" \
    run_deploy_mode partial-prepare-cleanup "$partial_case" isolated --confirm "$partial_case" > "$TEMP_DIR/partial-$boundary.log" 2>&1; then
      fail "Partial-prepare cleanup failpoint passed: $boundary"
  fi
  jq -e '.phaseStatus == "failed" and any(.resources[]; .status == "intent" or .status == "completed")' \
    "$partial_state/partial-prepare-cleanup-ledger.json" >/dev/null
done
test ! -e "$partial_logic_root" && test ! -e "$partial_logic_lock" && test ! -e "$partial_logic_lock.state"
run_deploy_mode partial-prepare-cleanup "$partial_case" isolated --confirm "$partial_case" >/dev/null
run_deploy_mode partial-prepare-cleanup "$partial_case" isolated --confirm "$partial_case" >/dev/null
grep -Fxq preserve "$unrelated_resource"
jq -e '.phaseStatus == "passed" and .operation == "cleanup" and
  ([.resources[] | select(.status == "completed")] | length) == 2' "$partial_evidence/partial-prepare-cleanup.json" >/dev/null

# A root that existed before prepare is retained while only run-created marker/control and lock provenance are removed.
partial_case=partial-preexisting
write_prepare_case "$partial_case" persistent
partial_logic_root="$(inventory_runtime_root logic)"; mkdir -m 700 "$partial_logic_root"
run_prepare_preflight "$partial_case" persistent
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' "$partial_case" persistent
partial_state="$TEMP_DIR/output/$partial_case-state"; partial_evidence="$TEMP_DIR/output/$partial_case-evidence"
run_deploy_mode partial-prepare-cleanup "$partial_case" persistent --confirm "$partial_case" >/dev/null
test -d "$partial_logic_root" && test -z "$(find -P "$partial_logic_root" -mindepth 1 -print -quit)"
jq -e '.targets[] | select(.target == "logic") | .newlyCreated.root == false and
  .newlyCreated.controlDirectory == true and .newlyCreated.marker == true' "$partial_evidence/partial-prepare-cleanup.json" >/dev/null

# The target that caused prepare to fail may have a foreign-owned parent and a pre-existing root; neither is a cleanup candidate.
partial_case=partial-parent-preserve
write_prepare_case "$partial_case" isolated
partial_logic_root="$(inventory_runtime_root logic)"; partial_east_root="$(inventory_runtime_root east)"
mkdir -m 700 "$partial_east_root"; printf 'preserve\n' > "$partial_east_root/pre-existing-state"
run_prepare_preflight "$partial_case" isolated
FAKE_PREPARE_ROOT_PARENT_HOST=east expect_prepare_failure 'owner-controlled-parent-required' "$partial_case" isolated
partial_state="$TEMP_DIR/output/$partial_case-state"; partial_evidence="$TEMP_DIR/output/$partial_case-evidence"
FAKE_PARTIAL_PREPARE_FOREIGN_PARENT_HOST=east \
  run_deploy_mode partial-prepare-cleanup "$partial_case" isolated --confirm "$partial_case" >/dev/null
test ! -e "$partial_logic_root"
grep -Fxq preserve "$partial_east_root/pre-existing-state"

# Existing-service infrastructure contexts are still scanned for run-labelled Docker resources.
partial_case=partial-shared-context
write_prepare_case "$partial_case" isolated
jq --arg owner "$(id -un)" '.sharedServices={name:"shared",sshHost:"shared@example",dockerContext:"shared-context",
  expectedArchitecture:"amd64",expectedHostName:"shared-node",expectedHostIdentity:"shared-machine",
  expectedDockerDaemonIdentity:"shared-daemon",runtimeRoot:(.productRoot+"/shared-services"),runtimeOwner:$owner,ports:[4444]}' \
  "$INVENTORY" > "$INVENTORY.changed"
mv "$INVENTORY.changed" "$INVENTORY"
partial_logic_root="$(inventory_runtime_root logic)"
run_prepare_preflight "$partial_case" isolated
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' "$partial_case" isolated
partial_state="$TEMP_DIR/output/$partial_case-state"; partial_evidence="$TEMP_DIR/output/$partial_case-evidence"
FAKE_PARTIAL_PREPARE_RESOURCE="shared-context:container:label=io.hvoskymonitor.run-id=$partial_case" \
  expect_partial_failure 'unexpected-deployment-resource' "$partial_case" isolated --dry-run

# A persistent target retained from an earlier run must still have canonical marker, lock, and state bytes.
write_prepare_case partial-foreign persistent
run_prepare_preflight partial-foreign-old persistent
run_deploy_mode prepare partial-foreign-old persistent >/dev/null
partial_logic_root="$(inventory_runtime_root logic)"
partial_logic_lock="${partial_logic_root%/*}/.hvo-deploy-prepare-$(printf 'v1\ntarget=logic\nroot=%s\n' "$partial_logic_root" | sha256sum | cut -c1-32).lock"
run_prepare_preflight partial-foreign-new persistent
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' partial-foreign-new persistent
partial_state="$TEMP_DIR/output/partial-foreign-new-state"; partial_evidence="$TEMP_DIR/output/partial-foreign-new-evidence"
cp "$partial_logic_lock" "$TEMP_DIR/partial-foreign-lock.valid"
printf '\n' >> "$partial_logic_lock"
expect_partial_failure 'preserved-provenance-invalid' partial-foreign-new persistent --dry-run
cp "$TEMP_DIR/partial-foreign-lock.valid" "$partial_logic_lock"; chmod 600 "$partial_logic_lock"
cp "$partial_logic_lock.state" "$TEMP_DIR/partial-foreign-state.valid"
root_new="$(awk -F '\t' '$1 == "rootNew" { print $2 }' "$partial_logic_lock.state")"
printf 'rootNew\t%s\n' "$root_new" >> "$partial_logic_lock.state"
expect_partial_failure 'preserved-provenance-invalid' partial-foreign-new persistent --dry-run
cp "$TEMP_DIR/partial-foreign-state.valid" "$partial_logic_lock.state"; chmod 600 "$partial_logic_lock.state"
sed 's/creatingRun\tpartial-foreign-old/creatingRun\tpartial-foreign-other/' \
  "$TEMP_DIR/partial-foreign-state.valid" > "$partial_logic_lock.state"
expect_partial_failure 'preserved-provenance-invalid' partial-foreign-new persistent --dry-run
cp "$TEMP_DIR/partial-foreign-state.valid" "$partial_logic_lock.state"; chmod 600 "$partial_logic_lock.state"

# Accepted shell metacharacters and whitespace remain literal across every SSH cleanup boundary.
partial_case=partial-quoted
write_prepare_case 'partial quoted;false' isolated
run_prepare_preflight "$partial_case" isolated
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' "$partial_case" isolated
partial_state="$TEMP_DIR/output/$partial_case-state"; partial_evidence="$TEMP_DIR/output/$partial_case-evidence"
partial_logic_root="$(inventory_runtime_root logic)"
partial_logic_lock="${partial_logic_root%/*}/.hvo-deploy-prepare-$(printf 'v1\ntarget=logic\nroot=%s\n' "$partial_logic_root" | sha256sum | cut -c1-32).lock"
run_deploy_mode partial-prepare-cleanup "$partial_case" isolated --confirm "$partial_case" >/dev/null
test ! -e "$partial_logic_root" && test ! -e "$partial_logic_lock" && test ! -e "$partial_logic_lock.state"
