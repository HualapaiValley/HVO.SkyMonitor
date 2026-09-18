#!/usr/bin/env bash
# Deployment contract shard body: smoke. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

# This body is sourced into the harness shell after scripts/lib/deploy-test-lifecycle.sh,
# whose staging functions (set_lifecycle_paths, prepare_lifecycle_fixture) assign the
# globals it reads: deploy_case. ShellCheck cannot see
# across that source boundary, so SC2154 is suppressed for this file with that fact recorded.
# shellcheck disable=SC2154
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

if [[ "$SELECTED_SHARD" == smoke ]]; then FAKE_LIFECYCLE_TRANSIENT_MODE=Hybrid stage_lifecycle_bootstrap; fi
FAKE_DOWN_TAIL_MODE=off
[[ "$SELECTED_SHARD" != smoke ]] || FAKE_DOWN_TAIL_MODE=healthy
export FAKE_DOWN_TAIL_MODE
FAKE_SLOW_FIRST_AGENT=true FAKE_CAPTURE_TELEMETRY_EMPTY=true run_deploy_mode smoke "$deploy_case" isolated >/dev/null
jq -e '.phaseStatus == "passed" and .workload.kind == "W0" and (.targets | length) == 2 and all(.targets[];
  .status == "passed" and .captureSequence.localAfter == (.captureSequence.localBefore + 1) and
  .captureSequence.centralAfter == .captureSequence.localAfter and .profile.width == 64 and .profile.height == 48 and
  .profile.pixelFormat == "Mono16" and ([.artifacts[] | select(.role == "Raw")] | length) == 1 and
  all(.artifacts[]; if .role == "Raw" then .byteLength == 6144 else true end) and
  all(.checks[]; . == true))' \
  "$TEMP_DIR/output/$deploy_case-state/smoke-ledger.json" >/dev/null
reset_smoke_fixture
if DEPLOY_TEST_SMOKE_DURATION_SECONDS=2 FAKE_DOWN_TAIL_MODE=stale run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-stale-tail.log" 2>&1; then
  fail 'Smoke accepted a stale Hybrid temporal tail.'
fi
grep -Fq 'bounded-convergence-timeout' "$TEMP_DIR/smoke-stale-tail.log"
test "$(<"$FAKE_HTTP_STATE/east.control-state")" = Paused
reset_smoke_fixture
run_deploy_mode smoke "$deploy_case" isolated >/dev/null
test -f "$FAKE_HTTP_STATE/east.schedule-active.json"
test ! -e "$FAKE_HTTP_STATE/east.schedule-pending.json"
jq -e '.rig.sensor.widthPixels == 64 and .rig.sensor.heightPixels == 48 and .rig.sensor.pixelFormat == "Mono16"' \
  "$FAKE_HTTP_STATE/east.schedule-active.json" >/dev/null
activation_line="$(grep -n -E $'^schedule-activate\teast$' "$LOG" | cut -d: -f1)"
initial_pause_line="$(grep -n -E $'control\t.*\tdeploy-smoke-'"$deploy_case"$'-east-initial-pause-[0-9]+\t' "$LOG" | cut -d: -f1)"
resume_line="$(grep -n -E $'control\t.*\tdeploy-smoke-'"$deploy_case"$'-east-resume-[0-9]+\t' "$LOG" | cut -d: -f1)"
[[ -n "$initial_pause_line" && -n "$activation_line" && -n "$resume_line" &&
   "$initial_pause_line" -lt "$activation_line" && "$activation_line" -lt "$resume_line" ]]
exercise_phase_cleanup_recovery smoke "$deploy_case"
valid_central="$TEMP_DIR/output/$deploy_case-state/smoke-private/east-current-central.json"
initial_central_file="$TEMP_DIR/output/$deploy_case-state/smoke-private/east-initial-central.json"
expected_recipes='["central-image-quality-v1","central-preview-v1"]'
for artifact_failure in checksum recipe object dimensions format lineage derivative provenance; do
  case "$artifact_failure" in
    checksum) jq '.latestArtifacts[1].checksumSha256="invalid"' "$valid_central" ;;
    recipe) jq '.latestArtifacts[1].recipeVersion="unexpected-recipe-v9"' "$valid_central" ;;
    object) jq '.latestArtifacts[1].objectState="Pending" | .latestArtifacts[1].objectVerifiedAtUtc=null' "$valid_central" ;;
    dimensions) jq '.latestArtifacts[0].width=63' "$valid_central" ;;
    format) jq '.latestArtifacts[0].pixelFormat="Mono8"' "$valid_central" ;;
    lineage) jq '.lineageSourceCount=0' "$valid_central" ;;
    derivative) jq '.latestArtifacts[0].completedDerivativeCount=1 | .completedDerivativeCount=0' "$valid_central" ;;
    provenance) jq '.latestArtifacts[2].sources[0].checksumSha256=("D"*64)' "$valid_central" ;;
  esac > "$TEMP_DIR/smoke-$artifact_failure.json"
  if (
    source "$REPO_ROOT/scripts/deploy/smoke.sh"
    deploy_smoke_validate_artifacts "$TEMP_DIR/smoke-$artifact_failure.json" \
      "$(jq -r '.maximumCaptureSequence' "$valid_central")" \
      "$(jq -r '.lineageSourceCount' "$initial_central_file")" \
      "$(jq -r '.completedDerivativeCount' "$initial_central_file")" "$expected_recipes" 64 48 Mono16 &&
    deploy_smoke_validate_derivative_provenance "$TEMP_DIR/smoke-$artifact_failure.json" \
      "$(jq -r '.maximumCaptureSequence' "$valid_central")" \
      "$(jq -r '.completedDerivativeCount' "$initial_central_file")"
  ); then fail "Smoke accepted invalid $artifact_failure evidence."; fi
done
proof_fixture="$(
  source "$REPO_ROOT/scripts/deploy/smoke.sh"
  deploy_smoke_select_checksum_proof "$valid_central" "$TEMP_DIR/output/$deploy_case-state/smoke-private/east-proof-continuity.json" \
    "$(jq -r '.maximumCaptureSequence' "$valid_central")"
)"
test "$(jq -r '.artifactId' <<< "$proof_fixture")" = 99999999-9999-9999-9999-999999999999
test "$(jq -r '.role' <<< "$proof_fixture")" = Raw
test "$(jq -r '.artifactId' <<< "$proof_fixture")" != 11111111-1111-1111-1111-111111111111
test "$(jq -r '.artifactId' <<< "$proof_fixture")" != 77777777-7777-7777-7777-777777777777
if (
  source "$REPO_ROOT/scripts/deploy/smoke.sh"
  deploy_smoke_validate_checksum_proof "$(printf 'A%.0s' {1..64})" "$(printf 'A%.0s' {1..64})" \
    "$(printf 'B%.0s' {1..64})" "$(printf 'A%.0s' {1..64})"
); then fail 'Smoke accepted unequal local, central, and retrieved artifact hashes.'; fi
printf 'camera_agent_capture_control_cycles_total 1\nhvo_fleet_edge_reports_total 1\npassword=forbidden\n' > "$TEMP_DIR/unsafe-metrics.txt"; chmod 600 "$TEMP_DIR/unsafe-metrics.txt"
if (source "$REPO_ROOT/scripts/deploy/smoke.sh"; deploy_smoke_metrics_facts "$TEMP_DIR/unsafe-metrics.txt"); then fail 'Smoke accepted sensitive metric output.'; fi
printf 'authorization: Bearer forbidden\n' > "$TEMP_DIR/unsafe-application.log"; chmod 600 "$TEMP_DIR/unsafe-application.log"
if (source "$REPO_ROOT/scripts/deploy/smoke.sh"; deploy_smoke_log_facts "$TEMP_DIR/unsafe-application.log"); then fail 'Smoke accepted sensitive application logs.'; fi
for series in {1..33}; do printf 'camera_agent_capture_control_cycles_total{camera="%s"} 1\n' "$series"; done > "$TEMP_DIR/high-cardinality-metrics.txt"
printf 'hvo_fleet_edge_reports_total 1\n' >> "$TEMP_DIR/high-cardinality-metrics.txt"; chmod 600 "$TEMP_DIR/high-cardinality-metrics.txt"
if (source "$REPO_ROOT/scripts/deploy/smoke.sh"; deploy_smoke_metrics_facts "$TEMP_DIR/high-cardinality-metrics.txt"); then fail 'Smoke accepted high-cardinality metric output.'; fi
printf 'camera_agent_capture_control_cycles_total 0\nhvo_fleet_edge_reports_total 0\n' > "$TEMP_DIR/unavailable-metrics.txt"; chmod 600 "$TEMP_DIR/unavailable-metrics.txt"
if (source "$REPO_ROOT/scripts/deploy/smoke.sh"; deploy_smoke_metrics_facts "$TEMP_DIR/unavailable-metrics.txt"); then fail 'Smoke accepted unavailable metric evidence.'; fi
if FAKE_RETRIEVAL_HASH_MISMATCH=true run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-retrieval-mismatch.log" 2>&1; then
  fail 'Smoke accepted a retrieved artifact hash mismatch.'
fi
grep -Fq 'artifact-checksum-proof-mismatch' "$TEMP_DIR/smoke-retrieval-mismatch.log"
run_deploy_mode smoke "$deploy_case" isolated >/dev/null
if FAKE_TRACE_UNAVAILABLE=true run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-trace-missing.log" 2>&1; then
  fail 'Isolated smoke claimed success without a trace/span projection.'
fi
grep -Fq 'required-trace-unavailable' "$TEMP_DIR/smoke-trace-missing.log"
run_deploy_mode smoke "$deploy_case" isolated >/dev/null
if grep -R -Eiq 'password|authorization:|bearer[[:space:]]|client[_-]?secret|device[_-]?key|envelope|request body|response body' \
  "$TEMP_DIR/output/$deploy_case-evidence/smoke.json"; then fail 'Smoke evidence retained raw sensitive telemetry.'; fi
jq -e 'all(.targets[]; .checks.artifactHashProof and .checks.traces and .checks.boundedLogs and
  .telemetry.metrics.captureControlCycles > 0 and .telemetry.metrics.fleetReports > 0 and
  (.telemetry.trace.traceId | test("^[0-9a-f]{32}$")) and
  any(.artifacts[] | select(.localChecksumSha256 != null); (.localChecksumSha256 | ascii_downcase) == (.checksumSha256 | ascii_downcase) and
    (.retrievedChecksumSha256 | ascii_downcase) == (.checksumSha256 | ascii_downcase) and .objectVerified == true))' \
  "$TEMP_DIR/output/$deploy_case-state/smoke-ledger.json" >/dev/null
exercise_phase_publication_recovery smoke "$deploy_case"
exercise_phase_tampered_publication_rejection smoke "$deploy_case"
exercise_phase_orphan_rejection smoke "$deploy_case"
reset_smoke_fixture
jq -S --arg device device-east '.agentId=$device | .module.options.seed=999' \
  "$REPO_ROOT/deploy/split-host/workloads/w0-virtual-mono16.json" > "$FAKE_HTTP_STATE/east.schedule-active.json"
printf 'schedule-active-v1\n' > "$FAKE_HTTP_STATE/east.schedule-active-revision"
printf '1\n' > "$FAKE_HTTP_STATE/east.schedule-state-version"
run_deploy_mode smoke "$deploy_case" isolated >/dev/null
grep -Eq $'^schedule-activate\teast$' "$LOG"
jq -e '.module.options.seed == 2025' "$FAKE_HTTP_STATE/east.schedule-active.json" >/dev/null
reset_smoke_fixture
FAKE_SMOKE_CENTRAL_INITIAL_LAG=east run_deploy_mode smoke "$deploy_case" isolated >/dev/null
jq -e '.targets[] | select(.target == "east") |
  .captureSequence.localAfter == (.captureSequence.localBefore + 1) and
  .captureSequence.centralBefore == (.captureSequence.localBefore - 1) and
  .captureSequence.centralAfter == .captureSequence.localAfter' \
  "$TEMP_DIR/output/$deploy_case-state/smoke-ledger.json" >/dev/null
  for schedule_failure in revision-drift version-drift stale-active stale-draft; do
  reset_smoke_fixture
  case "$schedule_failure" in
    revision-drift)
      if FAKE_SMOKE_SCHEDULE_REVISION_DRIFT=east run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-$schedule_failure.log" 2>&1; then
        fail 'Smoke accepted schedule revision drift.'
      fi
      grep -Fq 'schedule-activate-failed' "$TEMP_DIR/smoke-$schedule_failure.log"
      ;;
    version-drift)
      if FAKE_SMOKE_SCHEDULE_VERSION_DRIFT=east run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-$schedule_failure.log" 2>&1; then
        fail 'Smoke accepted schedule state-version drift.'
      fi
      grep -Fq 'schedule-activate-failed' "$TEMP_DIR/smoke-$schedule_failure.log"
      ;;
    stale-active)
      if FAKE_SMOKE_SCHEDULE_STALE_ACTIVE=east run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-$schedule_failure.log" 2>&1; then
        fail 'Smoke accepted a stale active schedule profile.'
      fi
      grep -Fq 'schedule-activation-mismatch' "$TEMP_DIR/smoke-$schedule_failure.log"
      ;;
    stale-draft)
      if FAKE_SMOKE_SCHEDULE_STALE_DRAFT=east run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-$schedule_failure.log" 2>&1; then
        fail 'Smoke accepted a stale file-draft schedule profile.'
      fi
      grep -Fq 'canonical-schedule-draft-missing' "$TEMP_DIR/smoke-$schedule_failure.log"
      ;;
  esac
  test "$(<"$FAKE_HTTP_STATE/east.control-state")" = Paused
  grep -Eq $'control\t.*\tdeploy-smoke-'"$deploy_case"$'-east-failure-pause-[0-9]+\t' "$LOG"
  if grep -Eq $'control\t.*\tdeploy-smoke-'"$deploy_case"$'-east-resume-[0-9]+\t' "$LOG"; then
    fail "Smoke resumed capture after $schedule_failure."
  fi
done
reset_smoke_fixture
if FAKE_SMOKE_RUNNING_FAILURE=east run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-running-failure.log" 2>&1; then
  fail 'Smoke accepted an ordinary failure while capture was running.'
fi
grep -Fq 'unexpected-local-continuity-status' "$TEMP_DIR/smoke-running-failure.log"
test "$(<"$FAKE_HTTP_STATE/east.control-state")" = Paused
east_smoke_remote="$(jq -r '.cameraAgents[] | select(.name == "east") | .runtimeRoot' "$INVENTORY")/.hvo-deploy/smoke-$deploy_case"
test ! -e "$east_smoke_remote/owner.cookies"
reset_smoke_fixture
if FAKE_SMOKE_RUNNING_FAILURE=east FAKE_SMOKE_FAILURE_PAUSE_CONFLICT=true run_deploy_mode smoke "$deploy_case" isolated > "$TEMP_DIR/smoke-running-pause-failure.log" 2>&1; then
  fail 'Smoke accepted a running-window failure whose safety Pause conflicted.'
fi
test "$(<"$FAKE_HTTP_STATE/east.control-state")" = Running
test -f "$east_smoke_remote/owner.cookies"
jq -e 'length > 0' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
run_deploy_mode smoke "$deploy_case" isolated >/dev/null
test "$(<"$FAKE_HTTP_STATE/east.control-state")" = Paused
jq -e '.phaseStatus == "passed"' "$TEMP_DIR/output/$deploy_case-state/smoke-ledger.json" >/dev/null
jq -e 'length == 0' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
unset FAKE_DOWN_TAIL_MODE
