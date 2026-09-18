#!/usr/bin/env bash
# Deployment contract shard body: bootstrap-credentials. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

if [[ "$SELECTED_SHARD" == bootstrap-credentials ]]; then stage_lifecycle_bootstrap; fi
# Every owner/central credential destination is target-bound before creation, and an interrupted run is strictly reconciled.
bootstrap_state="$TEMP_DIR/output/$deploy_case-state"
reset_bootstrap_phase_publication() {
  rm -f "$bootstrap_state/bootstrap-ledger.json" "$bootstrap_state/bootstrap-manifest.json" \
    "$bootstrap_state/bootstrap-commit.json" "$TEMP_DIR/output/$deploy_case-evidence/bootstrap.json"
}
mark_bootstrap_phase_incomplete() {
  local state="$1" evidence="$2" candidate digest generation
  candidate="$(jq -c '.phaseStatus="failed" | del(.completedAt)' "$state/bootstrap-ledger.json")"
  printf '%s\n' "$candidate" > "$state/bootstrap-ledger.json"
  printf '%s\n' "$candidate" > "$state/bootstrap-manifest.json"
  printf '%s\n' "$candidate" > "$evidence/bootstrap.json"
  chmod 600 "$state/bootstrap-ledger.json" "$state/bootstrap-manifest.json" "$evidence/bootstrap.json"
  generation="$(jq -r '.publicationGeneration' <<< "$candidate")"
  digest="$(printf '%s\n' "$(jq -S -c . <<< "$candidate")" | sha256sum | cut -d' ' -f1)"
  jq -cn --argjson generation "$generation" --arg digest "$digest" \
    '{schemaVersion:1,generation:$generation,ledgerSha256:$digest}' > "$state/bootstrap-commit.json"
  chmod 600 "$state/bootstrap-commit.json"
}
while IFS=$'\t' read -r credential_failpoint credential_target credential_path credential_exists; do
  reset_bootstrap_phase_publication
  if DEPLOY_TEST_FAILPOINT="$credential_failpoint" run_deploy_mode bootstrap "$deploy_case" isolated > "$TEMP_DIR/$credential_failpoint.log" 2>&1; then
    fail "Bootstrap credential failpoint $credential_failpoint passed."
  fi
  jq -e --arg path "$credential_path" --arg target "$credential_target" \
    'any(.[]; .path == $path and .target.name == $target)' "$bootstrap_state/private-upload-registry.json" >/dev/null ||
    fail "Bootstrap credential failpoint $credential_failpoint was not registered first."
  if [[ "$credential_exists" == true ]]; then
    test -f "$credential_path" || fail "Bootstrap credential failpoint $credential_failpoint did not create its registered file."
  else
    test ! -e "$credential_path" || fail "Bootstrap credential failpoint $credential_failpoint created its file before the boundary."
  fi
  run_deploy_mode bootstrap "$deploy_case" isolated >/dev/null
  jq -e 'length == 0' "$bootstrap_state/private-upload-registry.json" >/dev/null ||
    fail "Bootstrap credential failpoint $credential_failpoint was not reconciled."
  test ! -e "$credential_path" || fail "Bootstrap credential failpoint $credential_failpoint retained its private file."
done <<EOF
abrupt-before-central-header-create	logic	$bootstrap_state/bootstrap-rendered/central.headers	false
abrupt-after-central-header-create	logic	$bootstrap_state/bootstrap-rendered/central.headers	true
abrupt-before-central-header-stage	logic	$lifecycle_logic_root/.hvo-deploy/bootstrap-$deploy_case/owner.headers	false
abrupt-after-central-header-stage	logic	$lifecycle_logic_root/.hvo-deploy/bootstrap-$deploy_case/owner.headers	true
abrupt-before-owner-session-create	east	$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/owner.cookies	false
abrupt-after-owner-session-create	east	$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/owner.cookies	true
abrupt-before-antiforgery-response-create	east	$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/antiforgery.json	false
abrupt-after-antiforgery-response-create	east	$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/antiforgery.json	true
abrupt-before-owner-header-create	east	$bootstrap_state/bootstrap-rendered/east-antiforgery.headers	false
abrupt-after-owner-header-create	east	$bootstrap_state/bootstrap-rendered/east-antiforgery.headers	true
abrupt-before-owner-header-stage	east	$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/owner.headers	false
abrupt-after-owner-header-stage	east	$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/owner.headers	true
EOF
for rig_failure in missing mismatch; do
  reset_bootstrap_phase_publication
  if [[ "$rig_failure" == missing ]]; then rig_env=(FAKE_RIG_PROFILE_MISSING=true); else rig_env=(FAKE_RIG_PROFILE_HASH_MISMATCH=true); fi
  if env "${rig_env[@]}" DEPLOY_TEST_FAILPOINT=rig-profile-unavailable PATH="$BIN:$PATH" \
    "$REPO_ROOT/scripts/deploy:environment" bootstrap --inventory "$INVENTORY" --mode isolated --run-id "$deploy_case" \
    --state-root "$TEMP_DIR/output/$deploy_case-state" --evidence-root "$TEMP_DIR/output/$deploy_case-evidence" > "$TEMP_DIR/rig-$rig_failure.log" 2>&1; then
    fail "Bootstrap accepted $rig_failure central rig profile state."
  fi
  grep -Fq 'first-fleet-acknowledgement-timeout' "$TEMP_DIR/rig-$rig_failure.log"
  run_deploy_mode bootstrap "$deploy_case" isolated >/dev/null
done
# Cleanup binds the complete target identity before deleting a registered credential path.
(
  source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/run-state.sh"; source "$REPO_ROOT/scripts/deploy/transport.sh"
  source "$REPO_ROOT/scripts/deploy/images.sh"; source "$REPO_ROOT/scripts/deploy/catalog.sh"; source "$REPO_ROOT/scripts/deploy/bootstrap.sh"
  DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$TEMP_DIR/output/$deploy_case-state/manifest.json")"
  target="$(jq -c '.cameraAgents[] | select(.name=="east")' "$INVENTORY")"
  wrong_target="$(jq -c '.sshHost="west@example" | .dockerContext="west-context"' <<< "$target")"
  credential="$(jq -r '.runtimeRoot' <<< "$target")/.hvo-deploy/bootstrap-$deploy_case/owner.cookies"; mkdir -p "${credential%/*}"; printf 'secret\n' > "$credential"; chmod 600 "$credential"
  registry="$TEMP_DIR/cleanup-target-registry.json"; deploy_transport_initialize_private_upload_registry "$registry" "$INVENTORY" bootstrap
  jq -cn --arg path "$credential" --argjson target "$wrong_target" '[{phase:"bootstrap",kind:"remote-private",target:$target,path:$path}]' > "$registry"; chmod 600 "$registry"
  if PATH="$BIN:$PATH" deploy_bootstrap_cleanup_private_remote strict >/dev/null 2>&1; then exit 94; fi
  [[ -f "$credential" ]]
  jq -cn --arg path "$credential" --argjson target "$target" '[{phase:"bootstrap",kind:"remote-private",target:$target,path:$path}]' > "$registry"; chmod 600 "$registry"
  PATH="$BIN:$PATH" deploy_bootstrap_cleanup_private_remote strict; [[ ! -e "$credential" ]]
  exact_response="$(jq -r '.runtimeRoot' <<< "$target")/.hvo-deploy/bootstrap-$deploy_case/owner-verification.json"
  deploy_bootstrap_register_private_remote "$target" "$exact_response"
  jq -e --arg path "$exact_response" 'any(.[]; .path == $path and .target.name == "east")' "$registry" >/dev/null
  deploy_transport_forget_private_path "$exact_response"
  if deploy_bootstrap_register_private_remote "$target" "$(jq -r '.runtimeRoot' <<< "$target")/.hvo-deploy/not-a-phase/owner-verification.json"; then exit 95; fi
  logic="$(jq -c '.logicHost' "$INVENTORY")"; central_local="${registry%/*}/central.headers"
  if deploy_transport_register_local_private "$target" "$central_local"; then exit 96; fi
  deploy_transport_register_local_private "$logic" "$central_local"
  jq -e --arg path "$central_local" 'any(.[]; .path == $path and .target.name == "logic")' "$registry" >/dev/null
  deploy_transport_forget_private_path "$central_local"
)
grep -Fq $'POST\thttp://127.0.0.1:' "$LOG"
if grep -Fq -- '--request POST' "$REPO_ROOT/scripts/deploy/transport.sh"; then fail 'Owner login pins POST across its redirect.'; fi
reset_bootstrap_phase_publication
if FAKE_PRIVATE_REMOVE_FAIL_MATCH=owner.cookies run_deploy_mode bootstrap "$deploy_case" isolated > "$TEMP_DIR/bootstrap-cleanup-failure.log" 2>&1; then
  fail 'Bootstrap passed while strict credential cleanup failed.'
fi
test -e "$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/owner.cookies"
run_deploy_mode bootstrap "$deploy_case" isolated >/dev/null
reset_bootstrap_phase_publication
exercise_phase_cleanup_recovery bootstrap "$deploy_case"
exercise_phase_publication_recovery bootstrap "$deploy_case"
exercise_phase_tampered_publication_rejection bootstrap "$deploy_case"
exercise_phase_orphan_rejection bootstrap "$deploy_case"
for credential in \
  "$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/owner.cookies" \
  "$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/owner.headers" \
  "$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/login.html" \
  "$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/login-response.html" \
  "$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/owner-verification.json" \
  "$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case/antiforgery.json" \
  "$lifecycle_west_root/.hvo-deploy/bootstrap-$deploy_case/owner.cookies" \
  "$lifecycle_west_root/.hvo-deploy/bootstrap-$deploy_case/owner.headers" \
  "$lifecycle_logic_root/.hvo-deploy/bootstrap-$deploy_case/owner.headers"; do
  test ! -e "$credential"
done
