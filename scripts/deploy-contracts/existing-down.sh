#!/usr/bin/env bash
# Deployment contract shard body: existing-down. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

if [[ "$SELECTED_SHARD" == existing-down ]]; then stage_lifecycle_bootstrap; fi
# Teardown rejects deletion of existing services before mutation and supports an explicit preserve stop.
down_zero_continuity="$TEMP_DIR/down-zero-continuity.json"
printf '%s\n' '{"deviceId":"device-east","configuredAgentId":"device-east","isProvisioned":true,"activeConfigurationSha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","durable":{"rawIngressDatabaseExists":true,"captureSequences":[{"agentId":"device-east","lastSequence":0}]}}' > "$down_zero_continuity"
test "$(source "$REPO_ROOT/scripts/deploy/down.sh"; deploy_down_read_continuity_boundary "$down_zero_continuity")" = $'device-east\t0\taaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
jq '.durable.captureSequences={malformed:true}' "$down_zero_continuity" > "$down_zero_continuity.malformed"
if (source "$REPO_ROOT/scripts/deploy/down.sh"; deploy_down_read_continuity_boundary "$down_zero_continuity.malformed" >/dev/null 2>&1); then
  fail 'Down accepted a malformed continuity sequence container.'
fi
assert_no_down_remote_private_artifacts() {
  local target root artifact
  local -a artifacts dynamic
  shopt -s nullglob
  while IFS= read -r target; do
    root="$(jq -r '.runtimeRoot' <<< "$target")/.hvo-deploy/down-$deploy_case"
    artifacts=()
    for artifact in "$root/pause.json" "$root/pause-response.json" "$root/final-pause.json" \
      "$root/final-pause-response.json" "$root/continuity-boundary.json"; do
      [[ ! -e "$artifact" && ! -L "$artifact" ]] || artifacts+=("$artifact")
    done
    dynamic=("$root"/continuity-[0-9]*.json "$root"/summary-[0-9]*.json \
      "$root"/pause-[0-9]*-control.headers "$root"/final-pause-[0-9]*-control.headers)
    artifacts+=("${dynamic[@]}")
    ((${#artifacts[@]} == 0)) || fail "Down retained remote private artifacts for $(jq -r '.name' <<< "$target"): ${artifacts[*]}"
  done < <(jq -c '.cameraAgents[]' "$INVENTORY")
  jq -e 'length == 0' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null ||
    fail 'Down retained registered private artifacts.'
}
down_upload_root="$lifecycle_east_root/.hvo-deploy/uploads"; mkdir -p "$down_upload_root"; chmod 700 "$down_upload_root"
down_upload="$down_upload_root/hvo-upload-down-123-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp"
printf 'interrupted\n' > "$down_upload"; chmod 600 "$down_upload"
jq -cn --arg path "$down_upload" --argjson target "$(jq -c '.cameraAgents[] | select(.name=="east")' "$INVENTORY")" \
  '[{phase:"down",kind:"upload-temp",target:$target,path:$path}]' > "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
chmod 600 "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
if FAKE_SSH_UNREACHABLE=east run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-upload-cleanup-failure.log" 2>&1; then
  fail 'Down passed with an unreconciled private upload.'
fi
test -f "$down_upload"
jq -e 'length == 1' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
: > "$LOG"
if run_deploy_mode down "$deploy_case" isolated --delete-state --confirm "$deploy_case" > "$TEMP_DIR/down-failure.log" 2>&1; then
    fail 'Delete-state accepted resources not owned by the deployed shared-services stack.'
fi
grep -Fq 'existing-services-not-owned' "$TEMP_DIR/down-failure.log"
! grep -Fq $'compose\t' "$LOG" || fail 'Rejected deletion mutated a Compose project.'
if FAKE_PAUSE_CONFLICT=true run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-pause-conflict.log" 2>&1; then
  fail 'Down accepted an arbitrary pause conflict as idempotent success.'
fi
if ! grep -Fq 'unexpected-pause-status' "$TEMP_DIR/down-pause-conflict.log"; then
  while IFS= read -r diagnostic; do printf '%s\n' "$diagnostic" >&2; done < "$TEMP_DIR/down-pause-conflict.log"
  fail 'Down pause conflict did not reach the expected rejection.'
fi
stops_before="$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)"
if FAKE_DOWN_INVALID_PAUSE_RECEIPT=true run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-invalid-pause.log" 2>&1; then
  fail 'Down accepted an invalid pause receipt.'
fi
grep -Fq 'invalid-pause-receipt' "$TEMP_DIR/down-invalid-pause.log"
test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail 'Invalid pause receipt stopped a Compose service.'
for continuity_failure in mismatch duplicate change; do
  rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads
  if FAKE_DOWN_CONTINUITY_MODE="$continuity_failure" DEPLOY_TEST_DRAIN_ATTEMPTS=3 run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-continuity-$continuity_failure.log" 2>&1; then
    fail "Down accepted a $continuity_failure continuity boundary."
  fi
  test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail "Continuity $continuity_failure stopped a Compose service."
done
rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.summary-reads
if FAKE_DOWN_TAIL_MODE=off FAKE_DOWN_SUMMARY_MODE=Hybrid DEPLOY_TEST_DRAIN_ATTEMPTS=2 DEPLOY_TEST_POLL_SECONDS=0 \
  run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-mode-mismatch.log" 2>&1; then
  fail 'Down accepted a summary transient mode that differed from inventory.'
fi
grep -Fq 'drain-timeout' "$TEMP_DIR/down-mode-mismatch.log"
test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail 'Transient mode mismatch stopped a Compose service.'
rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.summary-reads
if FAKE_DOWN_TAIL_MODE=off FAKE_DOWN_CONFIG_HASH_DRIFT=observations DEPLOY_TEST_DRAIN_ATTEMPTS=2 DEPLOY_TEST_POLL_SECONDS=0 \
  run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-config-hash-drift.log" 2>&1; then
  fail 'Down accepted configuration hash drift during observations.'
fi
grep -Fq 'continuity-boundary-changed' "$TEMP_DIR/down-config-hash-drift.log"
test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail 'Configuration hash drift stopped a Compose service.'
rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.summary-reads
if FAKE_DOWN_TAIL_MODE=off FAKE_DOWN_PRE_PAUSE_SECTIONS=raw-lane DEPLOY_TEST_DRAIN_ATTEMPTS=2 DEPLOY_TEST_POLL_SECONDS=0 \
  run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-pre-pause-raw-lane.log" 2>&1; then
  fail 'Down accepted pre-pause raw/lane observations.'
fi
grep -Fq 'drain-timeout' "$TEMP_DIR/down-pre-pause-raw-lane.log"
test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail 'Pre-pause raw/lane observations stopped a Compose service.'
for unsafe_tail in wrong stale third excess fractional excess-raw fractional-raw retry quarantine pressure unrelated busy unhealthy stale-section stale-hybrid-worker config-mismatch config-not-current config-mode-missing malformed terminal equal-captured decreasing-captured pre-pause-captured; do
  rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.summary-reads
  if FAKE_DOWN_CONTINUITY_MODE=two FAKE_DOWN_TAIL_MODE="$unsafe_tail" DEPLOY_TEST_DRAIN_ATTEMPTS=2 DEPLOY_TEST_POLL_SECONDS=0 \
    run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-unsafe-tail-$unsafe_tail.log" 2>&1; then
    fail "Down accepted unsafe Hybrid tail mode $unsafe_tail."
  fi
  grep -Fq 'drain-timeout' "$TEMP_DIR/down-unsafe-tail-$unsafe_tail.log"
  test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail "Unsafe Hybrid tail $unsafe_tail stopped a Compose service."
done
rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.summary-reads
if FAKE_DOWN_CONTINUITY_MODE=two FAKE_DOWN_TAIL_MODE=changing DEPLOY_TEST_DRAIN_ATTEMPTS=3 DEPLOY_TEST_POLL_SECONDS=0 \
  run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-continuously-changing.log" 2>&1; then
  fail 'Down accepted continuously changing safe observations.'
fi
grep -Fq 'drain-timeout' "$TEMP_DIR/down-continuously-changing.log"
test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail 'Continuously changing safe observations stopped a Compose service.'
assert_no_down_remote_private_artifacts
rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.summary-reads
if FAKE_DOWN_CONTINUITY_MODE=two FAKE_DOWN_TAIL_MODE=off FAKE_DOWN_FINAL_REASSERT_RACE=true DEPLOY_TEST_DRAIN_ATTEMPTS=2 DEPLOY_TEST_POLL_SECONDS=0 \
  run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-final-reassert-race.log" 2>&1; then
  fail 'Down accepted a changed control version at final pause reassertion.'
fi
grep -Fq 'final-pause-version-mismatch' "$TEMP_DIR/down-final-reassert-race.log"
test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail 'Final pause version race stopped a Compose service.'
for final_failure in unsafe fingerprint time; do
  rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.summary-reads
  if FAKE_DOWN_TAIL_MODE=off FAKE_DOWN_FINAL_OBSERVATION="$final_failure" DEPLOY_TEST_DRAIN_ATTEMPTS=2 DEPLOY_TEST_POLL_SECONDS=0 \
    run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-final-observation-$final_failure.log" 2>&1; then
    fail "Down accepted final $final_failure observation evidence."
  fi
  grep -Eq 'final-observation-(unsafe|changed)' "$TEMP_DIR/down-final-observation-$final_failure.log"
  test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail "Final $final_failure observation stopped a Compose service."
done
rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.summary-reads
if FAKE_DOWN_TAIL_MODE=off FAKE_DOWN_CONFIG_HASH_DRIFT=final DEPLOY_TEST_DRAIN_ATTEMPTS=2 DEPLOY_TEST_POLL_SECONDS=0 \
  run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-final-config-hash-drift.log" 2>&1; then
  fail 'Down accepted configuration hash drift after final pause.'
fi
grep -Fq 'final-continuity-boundary-changed' "$TEMP_DIR/down-final-config-hash-drift.log"
test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail 'Final configuration hash drift stopped a Compose service.'
rm -f "$FAKE_HTTP_STATE"/*.down-unsafe-reads
if FAKE_DOWN_SAFE_THEN_UNSAFE=true DEPLOY_TEST_DRAIN_ATTEMPTS=2 run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-safe-unsafe.log" 2>&1; then
  fail 'Down accepted safe-then-unsafe observations without reconfirmation.'
fi
grep -Fq 'drain-timeout' "$TEMP_DIR/down-safe-unsafe.log"
test "$(grep -Ec $'docker\t.*compose\t.*stop' "$LOG" || true)" = "$stops_before" || fail 'Safe-then-unsafe observations stopped a Compose service.'
if FAKE_DOWN_LEASED_ONLY=fail DEPLOY_TEST_DRAIN_ATTEMPTS=2 DEPLOY_TEST_POLL_SECONDS=0 run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-leased-failure.log" 2>&1; then
  fail 'Down stopped an agent while work remained leased.'
fi
grep -Fq 'drain-timeout' "$TEMP_DIR/down-leased-failure.log"
if grep -Eq $'docker\t.*compose\t.*stop' "$LOG"; then fail 'Leased-only drain failure stopped a Compose service.'; fi
summary_reads_before="$(grep -c '/api/v1/operations/summary' "$LOG" || true)"
rm -f "$FAKE_HTTP_STATE"/*.down-continuity-reads "$FAKE_HTTP_STATE"/*.down-lease-reads "$FAKE_HTTP_STATE"/*.summary-reads
east_candidate_reads_before="$(grep -Ec 'downObservation=[0-9]+-[0-9]+.*cameraagents/22222222-2222-4222-8222-222222222222/[.]hvo-deploy/down-deploy-up/summary-' "$LOG" || true)"
FAKE_DOWN_LEASED_ONLY=delay-west FAKE_DOWN_CONTINUITY_MODE=one FAKE_DOWN_TAIL_MODE=changing-then-stable \
  FAKE_DOWN_CONTROL_FRESHNESS=stale FAKE_DOWN_WORKER_FRESHNESS=stale FAKE_DOWN_PRE_PAUSE_SECTIONS=async DEPLOY_TEST_POLL_SECONDS=0 \
  run_deploy_mode down "$deploy_case" isolated --preserve-state >/dev/null
east_candidate_reads_after="$(grep -Ec 'downObservation=[0-9]+-[0-9]+.*cameraagents/22222222-2222-4222-8222-222222222222/[.]hvo-deploy/down-deploy-up/summary-' "$LOG" || true)"
(( east_candidate_reads_after == east_candidate_reads_before + 3 )) || fail 'Down did not accept safe A,B,B observations in exactly three attempts.'
summary_reads_after="$(grep -c '/api/v1/operations/summary' "$LOG" || true)"
(( summary_reads_after >= summary_reads_before + 4 )) || fail 'Down did not wait for leased work to clear and confirm two safe observations.'
summary_queries="$(grep -o '/api/v1/operations/summary?downObservation=[^[:space:]]*' "$LOG" || true)"
test "$(wc -l <<< "$summary_queries")" = "$(sort -u <<< "$summary_queries" | wc -l)" || fail 'Down reused an operations-summary observation query.'
continuity_queries="$(grep -o '/api/internal/deployment/continuity?downContinuity=[^[:space:]]*' "$LOG" || true)"
test "$(wc -l <<< "$continuity_queries")" = "$(sort -u <<< "$continuity_queries" | wc -l)" || fail 'Down reused a continuity observation query.'
test ! -e "$down_upload"
assert_no_down_remote_private_artifacts
jq -e '.phaseStatus == "passed" and .policy == "preserve" and
  ([.resources[] | select(.action == "graceful-stop" and .status == "completed")] | length) == 3 and
  ([.resources[] | select(.action == "preserve" and .resource == "existing-services" and .status == "completed")] | length) == 1' \
  "$TEMP_DIR/output/$deploy_case-state/down-ledger.json" >/dev/null
completed_cleanup="$(jq -r '.cameraAgents[] | select(.name=="east") | .runtimeRoot' "$INVENTORY")/.hvo-deploy/down-$deploy_case/owner.cookies"
mkdir -p "${completed_cleanup%/*}"; printf 'credential\n' > "$completed_cleanup"; chmod 600 "$completed_cleanup"
jq -cn --arg path "$completed_cleanup" --argjson target "$(jq -c '.cameraAgents[] | select(.name=="east")' "$INVENTORY")" \
  '[{phase:"down",kind:"remote-private",target:$target,path:$path}]' > "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
chmod 600 "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
exercise_phase_cleanup_recovery down "$deploy_case" --preserve-state
test ! -e "$completed_cleanup"
if grep '^control' "$LOG" | grep -Ev $'^[^\t]+\t[^\t]+-control[.]headers\t[^\t]+\t1\t1\tfalse$' >/dev/null; then
  fail 'Down capture control did not use a fresh derived header with exactly one idempotency key.'
fi
exercise_phase_publication_recovery down "$deploy_case" --preserve-state
exercise_phase_tampered_publication_rejection down "$deploy_case" --preserve-state
exercise_phase_orphan_rejection down "$deploy_case" --preserve-state
if FAKE_SSH_UNREACHABLE=east run_deploy_mode down "$deploy_case" isolated --preserve-state > "$TEMP_DIR/down-completed-outage.log" 2>&1; then
  fail 'Completed teardown treated an unreachable target as verified absence.'
fi
run_deploy_mode down "$deploy_case" isolated --preserve-state >/dev/null

# Production startup selects the production resolver contract; fixture staging above covers both hosts and the complete root mount.
production_inventory="$TEMP_DIR/production-startup-inventory.json"
jq '.catalogs[0].kind="production"' "$INVENTORY" > "$production_inventory"
(
  # shellcheck source=scripts/deploy/product-layout.sh
  source "$REPO_ROOT/scripts/deploy/product-layout.sh"
  # shellcheck source=scripts/deploy/up.sh
  source "$REPO_ROOT/scripts/deploy/up.sh"
  test "$(deploy_up_catalog_required_kind "$production_inventory" "$(jq -c '.logicHost' "$production_inventory")")" = Production
)

# Corrupt image evidence and mutable references are rejected before startup state changes.
cp "$TEMP_DIR/output/$deploy_case-evidence/images.json" "$TEMP_DIR/images.valid.json"
jq '.images[0].reference="registry.example/logichost:latest"' "$TEMP_DIR/images.valid.json" > "$TEMP_DIR/output/$deploy_case-evidence/images.json"
if run_deploy_mode up "$deploy_case" isolated > "$TEMP_DIR/up-failure.log" 2>&1; then fail 'Corrupt image evidence passed up.'; fi
grep -Fq 'images-authority' "$TEMP_DIR/up-failure.log"
cp "$TEMP_DIR/images.valid.json" "$TEMP_DIR/output/$deploy_case-evidence/images.json"
jq -e '.phaseStatus == "passed"' "$TEMP_DIR/output/$deploy_case-state/up-ledger.json" >/dev/null
