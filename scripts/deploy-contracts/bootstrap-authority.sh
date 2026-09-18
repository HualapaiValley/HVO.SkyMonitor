#!/usr/bin/env bash
# Deployment contract shard body: bootstrap-authority. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

# This body is sourced into the harness shell after scripts/lib/deploy-test-lifecycle.sh,
# whose staging functions (set_lifecycle_paths, prepare_lifecycle_fixture) assign the
# globals it reads: deploy_case, east_config, lifecycle_east_root. ShellCheck cannot see
# across that source boundary, so SC2154 is suppressed for this file with that fact recorded.
# shellcheck disable=SC2154
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

if [[ "$SELECTED_SHARD" == bootstrap-authority ]]; then stage_lifecycle_up; fi
# Registry publication is a hard barrier for every bootstrap credential destination.
bootstrap_state="$TEMP_DIR/output/$deploy_case-state"
bootstrap_rendered="$bootstrap_state/bootstrap-rendered"
bootstrap_private="$bootstrap_state/bootstrap-private"
logic_private="$(jq -r '.logicHost.runtimeRoot' "$INVENTORY")/.hvo-deploy/bootstrap-$deploy_case"
east_private="$(jq -r '.cameraAgents[0].runtimeRoot' "$INVENTORY")/.hvo-deploy/bootstrap-$deploy_case"
while IFS=$'\t' read -r registry_match sensitive_destination; do
  if run_deploy_mode_with_registry_failure "$registry_match" bootstrap "$deploy_case" isolated > "$TEMP_DIR/registry-publication-failure.log" 2>&1; then
    fail "Bootstrap passed after registry publication failure for $registry_match."
  fi
  test ! -e "$sensitive_destination" || fail "Bootstrap created a sensitive destination after registry publication failure: $sensitive_destination"
  jq -e 'length == 0' "$bootstrap_state/private-upload-registry.json" >/dev/null ||
    fail "Bootstrap retained registry entries after publication failure for $registry_match."
done <<EOF
$bootstrap_rendered/central.headers	$bootstrap_rendered/central.headers
$logic_private/owner.headers	$logic_private/owner.headers
$bootstrap_rendered/east-owner-password	$bootstrap_rendered/east-owner-password
$east_private/owner-password	$east_private/owner-password
$east_private/owner.cookies	$east_private/owner.cookies
$east_private/login.html	$east_private/login.html
$east_private/login-response.html	$east_private/login-response.html
$east_private/owner-verification.json	$east_private/owner-verification.json
$east_private/antiforgery.json	$east_private/antiforgery.json
$bootstrap_private/east-antiforgery.json	$bootstrap_private/east-antiforgery.json
$bootstrap_rendered/east-antiforgery.headers	$bootstrap_rendered/east-antiforgery.headers
$east_private/owner.headers	$east_private/owner.headers
EOF
owner_stage_count="$(grep -c $'^scp\t.*east-owner-password\teast@example:' "$LOG" || true)"
for prerequisite in \
  "$east_config/private/owner-password" \
  "$east_config/secrets/LocalIdentity__AdminPasswordFile"; do
  if run_deploy_mode_with_registry_failure "$prerequisite" bootstrap "$deploy_case" isolated > "$TEMP_DIR/registry-prerequisite-failure.log" 2>&1; then
    fail "Bootstrap passed after registry publication failure for $prerequisite."
  fi
  test "$(grep -c $'^scp\t.*east-owner-password\teast@example:' "$LOG" || true)" = "$owner_stage_count" ||
    fail "Bootstrap staged an owner password after prerequisite registration failed: $prerequisite"
  jq -e 'length == 0' "$bootstrap_state/private-upload-registry.json" >/dev/null
done
# Bootstrap resumes every one-time-envelope ambiguity boundary without duplicating authority.
if DEPLOY_TEST_FAILPOINT=after-registration run_deploy_mode bootstrap "$deploy_case" isolated > "$TEMP_DIR/after-registration.log" 2>&1; then
  fail 'Bootstrap failpoint after-registration passed.'
fi
jq -e '.phaseStatus == "failed"' "$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json" >/dev/null
if run_deploy_mode_with_registry_failure "$bootstrap_private/east-envelope.json" bootstrap "$deploy_case" isolated \
  > "$TEMP_DIR/registry-envelope-local-failure.log" 2>&1; then
  fail 'Bootstrap passed after local envelope registry publication failed.'
fi
test ! -e "$bootstrap_private/east-envelope.json"
if run_deploy_mode_with_registry_failure "$logic_private/east-envelope.json" bootstrap "$deploy_case" isolated \
  > "$TEMP_DIR/registry-envelope-remote-failure.log" 2>&1; then
  fail 'Bootstrap passed after remote envelope registry publication failed.'
fi
test ! -e "$logic_private/east-envelope.json"
if run_deploy_mode_with_registry_failure "$east_private/bootstrap-request.json" bootstrap "$deploy_case" isolated \
  > "$TEMP_DIR/registry-bootstrap-remote-failure.log" 2>&1; then
  fail 'Bootstrap passed after remote bootstrap-request registry publication failed.'
fi
test ! -e "$east_private/bootstrap-request.json"
if run_deploy_mode_with_registry_failure "$bootstrap_rendered/east-bootstrap-request.json" bootstrap "$deploy_case" isolated \
  > "$TEMP_DIR/registry-bootstrap-local-failure.log" 2>&1; then
  fail 'Bootstrap passed after local bootstrap-request registry publication failed.'
fi
test ! -e "$bootstrap_rendered/east-bootstrap-request.json"
jq -e 'length == 0' "$bootstrap_state/private-upload-registry.json" >/dev/null
for bootstrap_failpoint in after-envelope after-central-activation after-local-secret-save; do
  if DEPLOY_TEST_FAILPOINT="$bootstrap_failpoint" run_deploy_mode bootstrap "$deploy_case" isolated > "$TEMP_DIR/$bootstrap_failpoint.log" 2>&1; then
    fail "Bootstrap failpoint $bootstrap_failpoint passed."
  fi
  jq -e '.phaseStatus == "failed"' "$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json" >/dev/null
done
if FAKE_BOOTSTRAP_OPERATIONS_UNHEALTHY=west run_deploy_mode bootstrap "$deploy_case" isolated > "$TEMP_DIR/bootstrap-operations-unhealthy.log" 2>&1; then
  fail 'Bootstrap accepted unhealthy required processing.'
fi
grep -Fq 'first-fleet-acknowledgement-timeout' "$TEMP_DIR/bootstrap-operations-unhealthy.log"
ready_east="$(jq -c '.targets[] | select(.target == "east")' "$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json")"
jq -e '.phaseStatus == "failed" and (.targets | length) == 1 and .targets[0].target == "east" and .targets[0].status == "ready"' \
  "$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json" >/dev/null
east_ssh_before_resume="$(grep -c $'^ssh\t.*east@example' "$LOG" || true)"
west_ssh_before_resume="$(grep -c $'^ssh\t.*west@example' "$LOG" || true)"
east_context_before_resume="$(grep -Fc $'docker\tcontext\tinspect\t--format\t{{.Name}}\teast-context' "$LOG" || true)"
east_info_before_resume="$(grep -Fc $'docker\t--context\teast-context\tinfo\t--format\t' "$LOG" || true)"
east_owner_stage_before_resume="$(grep -c $'^scp\t.*east-owner-password\teast@example:' "$LOG" || true)"
east_restart_before_resume="$(grep -c $'^docker\t--context\teast-context\tcompose\t.*up\t-d\t--force-recreate\tcameraagent$' "$LOG" || true)"
west_restart_before_resume="$(grep -c $'^docker\t--context\twest-context\tcompose\t.*up\t-d\t--force-recreate\tcameraagent$' "$LOG" || true)"
logic_activation_before_resume="$(grep -c $'^docker\t--context\tlogic-context\tcompose\t.*up\t-d\t--force-recreate\tlogichost$' "$LOG" || true)"
if FAKE_COMPOSE_FAIL_MATCH='--force-recreate logichost' run_deploy_mode bootstrap "$deploy_case" isolated > "$TEMP_DIR/bootstrap-final-logic-failure.log" 2>&1; then
  fail 'Bootstrap passed after final LogicHost activation failed.'
fi
jq -e --argjson ready "$ready_east" '.phaseStatus == "failed" and (.targets | length) == 2 and
  (.targets[] | select(.target == "east")) == $ready and
  any(.targets[]; .target == "west" and .status == "ready")' "$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json" >/dev/null
(( $(grep -c $'^ssh\t.*east@example' "$LOG" || true) > east_ssh_before_resume )) || fail 'Bootstrap resume did not correlate a retained-ready CameraAgent over SSH.'
(( $(grep -Fc $'docker\tcontext\tinspect\t--format\t{{.Name}}\teast-context' "$LOG" || true) > east_context_before_resume )) ||
  fail 'Bootstrap resume did not correlate the retained-ready Docker context.'
(( $(grep -Fc $'docker\t--context\teast-context\tinfo\t--format\t' "$LOG" || true) > east_info_before_resume )) ||
  fail 'Bootstrap resume did not correlate the retained-ready Docker daemon.'
(( $(grep -c $'^ssh\t.*west@example' "$LOG" || true) > west_ssh_before_resume )) || fail 'Bootstrap resume did not process the later non-ready CameraAgent.'
test "$(grep -c $'^scp\t.*east-owner-password\teast@example:' "$LOG" || true)" = "$east_owner_stage_before_resume" ||
  fail 'Bootstrap resume restaged credentials for a retained-ready CameraAgent.'
test "$(grep -c $'^docker\t--context\teast-context\tcompose\t.*up\t-d\t--force-recreate\tcameraagent$' "$LOG" || true)" = "$east_restart_before_resume" ||
  fail 'Bootstrap resume restarted a retained-ready CameraAgent.'
(( $(grep -c $'^docker\t--context\twest-context\tcompose\t.*up\t-d\t--force-recreate\tcameraagent$' "$LOG" || true) > west_restart_before_resume )) ||
  fail 'Bootstrap resume did not restart the later non-ready CameraAgent.'
(( $(grep -c $'^docker\t--context\tlogic-context\tcompose\t.*up\t-d\t--force-recreate\tlogichost$' "$LOG" || true) == logic_activation_before_resume + 1 )) ||
  fail 'Bootstrap did not attempt final LogicHost Hybrid activation.'
east_context_before_logic_resume="$(grep -Fc $'docker\tcontext\tinspect\t--format\t{{.Name}}\teast-context' "$LOG" || true)"
west_context_before_logic_resume="$(grep -Fc $'docker\tcontext\tinspect\t--format\t{{.Name}}\twest-context' "$LOG" || true)"
east_owner_stage_before_logic_resume="$(grep -c $'^scp\t.*east-owner-password\teast@example:' "$LOG" || true)"
west_owner_stage_before_logic_resume="$(grep -c $'^scp\t.*west-owner-password\twest@example:' "$LOG" || true)"
east_restart_before_logic_resume="$(grep -c $'^docker\t--context\teast-context\tcompose\t.*up\t-d\t--force-recreate\tcameraagent$' "$LOG" || true)"
west_restart_before_logic_resume="$(grep -c $'^docker\t--context\twest-context\tcompose\t.*up\t-d\t--force-recreate\tcameraagent$' "$LOG" || true)"
logic_activation_before_logic_resume="$(grep -c $'^docker\t--context\tlogic-context\tcompose\t.*up\t-d\t--force-recreate\tlogichost$' "$LOG" || true)"
run_deploy_mode bootstrap "$deploy_case" isolated >/dev/null
(( $(grep -Fc $'docker\tcontext\tinspect\t--format\t{{.Name}}\teast-context' "$LOG" || true) > east_context_before_logic_resume )) ||
  fail 'All-ready LogicHost recovery did not correlate east.'
(( $(grep -Fc $'docker\tcontext\tinspect\t--format\t{{.Name}}\twest-context' "$LOG" || true) > west_context_before_logic_resume )) ||
  fail 'All-ready LogicHost recovery did not correlate west.'
test "$(grep -c $'^scp\t.*east-owner-password\teast@example:' "$LOG" || true)" = "$east_owner_stage_before_logic_resume"
test "$(grep -c $'^scp\t.*west-owner-password\twest@example:' "$LOG" || true)" = "$west_owner_stage_before_logic_resume"
test "$(grep -c $'^docker\t--context\teast-context\tcompose\t.*up\t-d\t--force-recreate\tcameraagent$' "$LOG" || true)" = "$east_restart_before_logic_resume"
test "$(grep -c $'^docker\t--context\twest-context\tcompose\t.*up\t-d\t--force-recreate\tcameraagent$' "$LOG" || true)" = "$west_restart_before_logic_resume"
(( $(grep -c $'^docker\t--context\tlogic-context\tcompose\t.*up\t-d\t--force-recreate\tlogichost$' "$LOG" || true) == logic_activation_before_logic_resume + 1 )) ||
  fail 'All-ready recovery did not activate LogicHost.'
jq -e '.phaseStatus == "passed" and (.targets | length) == 2 and all(.targets[];
  .status == "ready" and .continuity.deviceId == .continuity.configuredAgentId and .continuity.lastFleetAcknowledgedUtc != null and
  .continuity.captureAcknowledgement.localSequenceAfter > .continuity.captureAcknowledgement.localSequenceBefore and
  .continuity.captureAcknowledgement.centralSequenceAfter > .continuity.captureAcknowledgement.centralSequenceBefore)' \
  "$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json" >/dev/null
cp "$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json" "$TEMP_DIR/bootstrap-passed-ledger"
cp "$TEMP_DIR/output/$deploy_case-state/bootstrap-manifest.json" "$TEMP_DIR/bootstrap-passed-manifest"
cp "$TEMP_DIR/output/$deploy_case-state/bootstrap-commit.json" "$TEMP_DIR/bootstrap-passed-commit"
cp "$TEMP_DIR/output/$deploy_case-evidence/bootstrap.json" "$TEMP_DIR/bootstrap-passed-evidence"
cp "$LOG" "$TEMP_DIR/bootstrap-passed-commands"
FAKE_SSH_UNREACHABLE=logic FAKE_DOCKER_MISMATCH=east-context FAKE_BOOTSTRAP_OPERATIONS_UNHEALTHY=east \
  FAKE_COMPOSE_FAIL_MATCH=logichost run_deploy_mode bootstrap "$deploy_case" isolated >/dev/null
cmp -s "$TEMP_DIR/bootstrap-passed-ledger" "$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json" || fail 'Passed bootstrap rerun mutated its ledger.'
cmp -s "$TEMP_DIR/bootstrap-passed-manifest" "$TEMP_DIR/output/$deploy_case-state/bootstrap-manifest.json" || fail 'Passed bootstrap rerun mutated its manifest.'
cmp -s "$TEMP_DIR/bootstrap-passed-commit" "$TEMP_DIR/output/$deploy_case-state/bootstrap-commit.json" || fail 'Passed bootstrap rerun mutated its commit.'
cmp -s "$TEMP_DIR/bootstrap-passed-evidence" "$TEMP_DIR/output/$deploy_case-evidence/bootstrap.json" || fail 'Passed bootstrap rerun mutated completed evidence.'
cmp -s "$TEMP_DIR/bootstrap-passed-commands" "$LOG" || fail 'Passed bootstrap rerun contacted or mutated a runtime target.'
(
  source "$REPO_ROOT/scripts/deploy/common.sh"
  source "$REPO_ROOT/scripts/deploy/bootstrap.sh"
  valid_candidate="$TEMP_DIR/output/$deploy_case-state/bootstrap-ledger.json"
  inventory_hash="$(jq -S -c . "$INVENTORY" | sha256sum | cut -d' ' -f1)"
  deploy_bootstrap_validate_candidate "$valid_candidate" "$deploy_case" isolated "$inventory_hash" "$REVISION" "$INVENTORY"
  for invalid_candidate in retained-status null-continuity array-continuity duplicate-target unknown-target missing-ready-target \
    passed-missing-completed incomplete-with-completed invalid-started updated-before-started completed-before-updated \
    invalid-acknowledgement impossible-acknowledgement-order invalid-json; do
    candidate="$TEMP_DIR/bootstrap-candidate-$invalid_candidate.json"
    case "$invalid_candidate" in
      retained-status) jq '.phaseStatus="failed" | .targets[0].status="restarting" | del(.completedAt)' "$valid_candidate" > "$candidate" ;;
      null-continuity) jq '.targets[0].continuity=null' "$valid_candidate" > "$candidate" ;;
      array-continuity) jq '.targets[0].continuity=[]' "$valid_candidate" > "$candidate" ;;
      duplicate-target) jq '.targets += [.targets[0]]' "$valid_candidate" > "$candidate" ;;
      unknown-target) jq '.targets[0].target="north"' "$valid_candidate" > "$candidate" ;;
      missing-ready-target) jq '.targets=.targets[:1]' "$valid_candidate" > "$candidate" ;;
      passed-missing-completed) jq 'del(.completedAt)' "$valid_candidate" > "$candidate" ;;
      incomplete-with-completed) jq '.phaseStatus="failed"' "$valid_candidate" > "$candidate" ;;
      invalid-started) jq '.startedAt="2026-02-30T00:00:00Z"' "$valid_candidate" > "$candidate" ;;
      updated-before-started) jq '.startedAt="2026-08-08T00:00:00Z" | .updatedAt="2026-08-07T00:00:00Z"' "$valid_candidate" > "$candidate" ;;
      completed-before-updated) jq '.updatedAt="2026-08-08T00:00:00Z" | .completedAt="2026-08-07T00:00:00Z"' "$valid_candidate" > "$candidate" ;;
      invalid-acknowledgement) jq '.targets[0].continuity.lastFleetAcknowledgedUtc="2026-08-07 00:02:00"' "$valid_candidate" > "$candidate" ;;
      impossible-acknowledgement-order) jq '.targets[0].continuity.fleetAcknowledgement.centralReceivedAtBefore="2026-08-08T00:00:00Z" |
        .targets[0].continuity.lastFleetAcknowledgedUtc="2026-08-07T00:00:00Z"' "$valid_candidate" > "$candidate" ;;
      invalid-json) printf '{\n' > "$candidate" ;;
    esac
    if deploy_bootstrap_validate_candidate "$candidate" "$deploy_case" isolated "$inventory_hash" "$REVISION" "$INVENTORY" >/dev/null 2>&1; then
      fail "Bootstrap accepted invalid retained candidate: $invalid_candidate"
    fi
  done
)
test "$(<"$east_config/secrets/CameraAgent__AgentId")" = device-east
test "$(<"$east_config/secrets/CameraAgent__ProvisioningStartupGate__Enabled")" = false
test "$(<"$east_config/secrets/CameraAgent__CaptureDistribution__UploadEnabled")" = true
test ! -e "$east_config/private/owner-password"
test ! -e "$east_config/secrets/LocalIdentity__AdminPasswordFile"
test "$(<"$east_config/secrets/LocalIdentity__AllowMissingAdminPassword")" = true
test ! -e "$TEMP_DIR/output/$deploy_case-state/bootstrap-private/east-envelope.json"
test -f "$FAKE_HTTP_STATE/device-east.recovered"
(( $(<"$FAKE_HTTP_STATE/envelope-count") >= 3 )) || fail 'Active recovery reused the retained pre-recovery envelope.'
bound_module_sha="$(sha256sum "$east_config/camera-module.json")"
run_deploy_mode up "$deploy_case" isolated >/dev/null
test "$(sha256sum "$east_config/camera-module.json")" = "$bound_module_sha" || fail 'Post-bootstrap up reset the provisioned module identity.'
jq -e '.state == "bound" and .configuredIdentity == "east-agent" and .boundIdentity == "device-east"' "$lifecycle_east_root/application-identity.json" >/dev/null
