#!/usr/bin/env bash
# Deployment contract shard body: measure. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

# This body is sourced into the harness shell after scripts/lib/deploy-test-lifecycle.sh,
# whose staging functions (set_lifecycle_paths, prepare_lifecycle_fixture) assign the
# globals it reads: deploy_case, lifecycle_east_root. ShellCheck cannot see
# across that source boundary, so SC2154 is suppressed for this file with that fact recorded.
# shellcheck disable=SC2154
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

if [[ "$SELECTED_SHARD" == measure ]]; then stage_lifecycle_smoke; fi
measure_cleanup="$(jq -r '.cameraAgents[] | select(.name=="east") | .runtimeRoot' "$INVENTORY")/.hvo-deploy/measure-$deploy_case/owner.cookies"
mkdir -p "${measure_cleanup%/*}"; printf 'retained-credential\n' > "$measure_cleanup"; chmod 600 "$measure_cleanup"
jq -cn --arg path "$measure_cleanup" --argjson target "$(jq -c '.cameraAgents[] | select(.name=="east")' "$INVENTORY")" \
  '[{phase:"measure",kind:"remote-private",target:$target,path:$path}]' > "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
chmod 600 "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
if FAKE_SSH_UNREACHABLE=east run_deploy_mode measure "$deploy_case" isolated --workload W1 > "$TEMP_DIR/measure-entry-cleanup-failure.log" 2>&1; then
  fail 'Measure proceeded while strict entry cleanup was unreachable.'
fi
grep -Fq 'private-upload-registry' "$TEMP_DIR/measure-entry-cleanup-failure.log"
test -f "$measure_cleanup"
jq -e 'length == 1' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
test ! -e "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json"
active_before_stage="$(<"$FAKE_HTTP_STATE/east.active-config-sha")"
if DEPLOY_TEST_FAILPOINT=abrupt-before-measure-profile-stage run_deploy_mode measure "$deploy_case" isolated --workload W1 > "$TEMP_DIR/measure-before-profile-stage.log" 2>&1; then
  fail 'Measure pre-stage failpoint passed.'
fi
jq -e 'length > 0 and any(.[]; .path == $path)' --arg path "$measure_cleanup" "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
jq -e --arg sha "$active_before_stage" '.phaseStatus == "running" and (.targets | length) == 1 and .targets[0].profile == null and
  .targets[0].restoration.status == "pending" and .targets[0].failureCleanup.status == "not-required" and
  .targets[0].priorState.configurationSha256 == $sha' \
  "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
test "$(<"$FAKE_HTTP_STATE/east.active-config-sha")" = "$active_before_stage"
if FAKE_CONFIG_RUNTIME_HASH_STALE=east run_deploy_mode measure "$deploy_case" isolated --workload W1 > "$TEMP_DIR/measure-staged-file-stale-runtime.log" 2>&1; then
  fail 'Measure accepted a staged canonical file without matching runtime activation.'
fi
jq -e '.phaseStatus == "failed" and (.targets[0].canonicalScheduleProfileSha256 | test("^[0-9a-f]{64}$")) and .targets[0].restoration.status == "completed" and
  .targets[0].failureCleanup.status == "completed"' "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
east_active_module="$(jq -r '.cameraAgents[] | select(.name == "east") | .runtimeRoot' "$INVENTORY")/config/camera-module.json"
test "$(sha256sum "$east_active_module" | cut -d' ' -f1)" = \
  "$(jq -r '.targets[0].priorState.configurationFileSha256' "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json")"
test ! -e "$FAKE_HTTP_STATE/east.schedule-pending.json"
test "$(( $(grep -c $'^scp\t.*east-prior-camera-module[.]json\teast@example:' "$LOG" || true) ))" -ge 1
activation_count_before_abrupt="$(grep -c $'^scp\t.*east-W1-camera-module[.]json\teast@example:' "$LOG" || true)"
if DEPLOY_TEST_FAILPOINT=abrupt-after-measure-profile-activation run_deploy_mode measure "$deploy_case" isolated --workload W1 > "$TEMP_DIR/measure-after-profile-activation.log" 2>&1; then
  fail 'Measure post-activation failpoint passed.'
fi
jq -e '.phaseStatus == "running" and .targets[0].profile == null and
  (.targets[0].canonicalScheduleProfileSha256 | test("^[0-9a-f]{64}$")) and .targets[0].restoration.status == "pending"' \
  "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null || fail 'Measure did not retain an incomplete profile intent after abrupt activation.'
active_after_activation="$(<"$FAKE_HTTP_STATE/east.active-config-sha")"
test "$active_after_activation" != "$active_before_stage"
activation_count_after="$(grep -c $'^scp\t.*east-W1-camera-module[.]json\teast@example:' "$LOG" || true)"
(( activation_count_after == activation_count_before_abrupt + 1 )) || fail 'Measure profile activation did not occur exactly once.'
for measure_boundary in initial-pause warmup-resume warmup-pause warmup-complete measured-resume measured-pause measured-complete; do
  if DEPLOY_TEST_FAILPOINT="abrupt-after-measure-$measure_boundary" run_deploy_mode measure "$deploy_case" isolated --workload W1 > "$TEMP_DIR/measure-$measure_boundary.log" 2>&1; then
    fail "Measure boundary failpoint passed: $measure_boundary"
  fi
  jq -e '([.targets[].controlAttempts[].key] | length == (unique | length)) and
    all(.targets[].controlAttempts[]; (.attempt | type) == "number" and (.key | test("-[0-9]+$")))' \
    "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
  if [[ "$measure_boundary" == warmup-resume ]]; then
    if FAKE_MEASURE_SEQUENCE_READ_FAIL=east FAKE_MEASURE_FAILURE_PAUSE_FAIL=east run_deploy_mode measure "$deploy_case" isolated --workload W1 > "$TEMP_DIR/measure-failure-pause.log" 2>&1; then
      fail 'Measure passed after a resumed target read failure and failed safety pause.'
    fi
    jq -e '.phaseStatus == "failed" and any(.targets[]; .target == "east" and .failureCleanup.status == "restoration-incomplete" and
      .restoration.status == "capture-unverified" and any(.controlAttempts[]; .boundary == "restoration-pause" and .status == "intent"))' \
      "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
    jq -e 'length > 0' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
  fi
  if [[ "$measure_boundary" == initial-pause ]]; then
    jq -e '.targets[0].profile != null' "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
    test "$(<"$FAKE_HTTP_STATE/east.active-config-sha")" = "$active_after_activation"
  fi
done
restore_order_log="$TEMP_DIR/measure-restore-order.log"
(
  source "$REPO_ROOT/scripts/deploy/measure.sh"
  DEPLOY_MEASURE_JSON='{"targets":[{"target":"east","restoration":{"status":"pending"}},{"target":"west","restoration":{"status":"pending"}}]}'
  DEPLOY_MEASURE_INVENTORY="$INVENTORY"
  deploy_measure_pause_for_restoration() { printf 'pause-%s\n' "$(jq -r '.name' <<< "$1")" >> "$restore_order_log"; }
  deploy_measure_restore_target() { printf 'restore-%s\n' "$(jq -r '.name' <<< "$1")" >> "$restore_order_log"; }
  deploy_measure_restore_all
)
test "$(<"$restore_order_log")" = $'pause-east\npause-west\nrestore-east\nrestore-west'

if [[ -z "${DEPLOY_TEST_PROCESS_RECORD_DIR:-}" ]]; then
  # Real process-group signals run serially; nesting them under the parallel coordinator
  # would place two independent supervisors around the same child process group.
  cp "$TEMP_DIR/output/$deploy_case-state/measure-private/east-prior-camera-module.json" "$TEMP_DIR/east-original-camera-module.json"
  west_runtime="$(jq -r '.cameraAgents[] | select(.name == "west") | .runtimeRoot' "$INVENTORY")"
  cp "$west_runtime/config/camera-module.json" "$TEMP_DIR/west-original-camera-module.json"

  reset_measure_fixture Running Paused
  signal_int_marker="$TEMP_DIR/output/$deploy_case-state/measure-private/east-signal-profile-ready"
  run_measure_signal INT 130 "$signal_int_marker" "$TEMP_DIR/measure-signal-int.log" \
    DEPLOY_TEST_FAILPOINT=signal-after-measure-profile-activation
  jq -e --arg eastSha "$active_before_stage" '.phaseStatus == "failed" and (.targets | length) == 1 and
    all(.targets[]; .restoration.status == "completed" and .failureCleanup.status == "completed") and
    (.targets[] | select(.target == "east") | .priorState.captureState == "Running" and .priorState.configurationSha256 == $eastSha)' \
    "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
  test "$(<"$FAKE_HTTP_STATE/east.active-config-sha")" = "$active_before_stage"
  test "$(<"$FAKE_HTTP_STATE/east.control-state")" = Running
  cmp -s "$TEMP_DIR/east-original-camera-module.json" "$FAKE_HTTP_STATE/east.schedule-active.json"
  test ! -e "$FAKE_HTTP_STATE/east.schedule-pending.json"
  jq -e 'length == 0' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null

  reset_measure_fixture Paused Paused
  signal_term_marker="$TEMP_DIR/output/$deploy_case-state/measure-private/east-signal-warmup-ready"
  run_measure_signal TERM 143 "$signal_term_marker" "$TEMP_DIR/measure-signal-term.log" \
    DEPLOY_TEST_FAILPOINT=signal-after-measure-warmup-convergence
  jq -e '.phaseStatus == "failed" and .targets[0].priorState.captureState == "Paused" and
    .targets[0].warmupStart == 1 and .targets[0].warmup == null and .targets[0].restoration.status == "completed" and
    .targets[0].failureCleanup.status == "completed"' "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
  test "$(<"$FAKE_HTTP_STATE/east.count")" = 6
  test "$(<"$FAKE_HTTP_STATE/east.active-config-sha")" = "$active_before_stage"
  test "$(<"$FAKE_HTTP_STATE/east.control-state")" = Paused
  cmp -s "$TEMP_DIR/east-original-camera-module.json" "$FAKE_HTTP_STATE/east.schedule-active.json"
  test ! -e "$FAKE_HTTP_STATE/east.schedule-pending.json"
fi

run_deploy_mode measure "$deploy_case" isolated --workload W1 >/dev/null
jq -e --arg priorSha "$active_before_stage" '.phaseStatus == "passed" and .executionMode == "canonical" and .canonicalWorkloadConfigured == true and
  .workload == "W1" and (.targets[] | select(.target == "east") | .priorState.configurationSha256) == $priorSha and
  all(.targets[]; .status == "measured" and .warmupCompleted == 5 and .measuredCompleted == 30 and
    .warmup.count == 5 and .warmup.endSequence == (.warmup.startSequence + 5) and .warmup.drained and .warmup.correctness and
    .measured.count == 30 and .measured.endSequence == (.measured.startSequence + 30) and .measured.drained and .measured.correctness and
    ([.warmup.captures[].captureSequence] | length == 5 and length == (unique | length)) and
    ([.measured.captures[].captureSequence] | length == 30 and length == (unique | length)) and
    ([.measured.captures[].captureId] | length == 30 and length == (unique | length)) and
    all(.warmup.captures[],.measured.captures[];
      (.rawArtifactId | test("^[0-9a-fA-F-]{36}$")) and (.rawChecksumSha256 | test("^[0-9A-Fa-f]{64}$")) and .rawByteLength == 6144) and
    ([.controlAttempts[].key] | length == (unique | length)) and
    (.priorState.scheduleRevisionId == "schedule-active-v1" or .priorState.scheduleRevisionId == "schedule-pending-v2") and
    (.priorState.captureState == "Running" or .priorState.captureState == "Paused") and
    .restoration.status == "pending" and .failureCleanup.status == "not-required" and
    .before.telemetry.sampleCount == 0 and (.before.timings | length) == 0 and .after.telemetry.sampleCount > 0 and
    .profile.width == 1936 and .profile.height == 1216 and .profile.pixelFormat == "Mono16" and
    .profile.sourcePath == "src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json")' \
  "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
if [[ -z "${DEPLOY_TEST_PROCESS_RECORD_DIR:-}" ]]; then
  test "$(jq -r '.targets[] | select(.target == "east") | .warmup.startSequence' "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json")" = 1
  test "$(jq -r '.targets[] | select(.target == "east") | .warmup.endSequence' "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json")" = 6
fi
test "$(<"$FAKE_HTTP_STATE/east.active-config-sha")" != "$active_before_stage"
measure_sequence_fixture="$TEMP_DIR/measure-sequence-fixture.json"
measure_sequence_operations="$TEMP_DIR/measure-sequence-operations.json"
measure_sequence_private="$TEMP_DIR/measure-sequence-private"
mkdir "$measure_sequence_private"
printf '%s\n' '{"durable":{"rawIngressDatabaseExists":true,"captureSequences":[]}}' > "$measure_sequence_fixture"
printf '%s\n' '{"captureLanes":{"value":{"availability":"Healthy","lanes":[{"name":"standard","required":true,"retryCount":0,"quarantineCount":0,"pressureLevel":0,"pendingCaptures":[]}]}},"rawIngress":{"value":{"availability":"Accepting","retryCount":0,"quarantineCount":0,"terminalCount":0}},"captureProcessing":{"value":{"availability":"Healthy","retryCount":0,"quarantineCount":0,"terminalCount":0}},"artifactOutbox":{"value":{"availability":"Healthy","retryCount":0,"quarantineCount":0,"terminalCount":0}}}' > "$measure_sequence_operations"
(
  source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"
  deploy_bootstrap_request() {
    if [[ "$3" == */api/v1/operations/summary ]]; then cp "$measure_sequence_operations" "$8"; else cp "$measure_sequence_fixture" "$8"; fi
    printf '200\n'
  }
  target='{"name":"east","internalEndpoint":"http://127.0.0.1"}'
  test "$(deploy_measure_read_sequence "$target" /remote "$measure_sequence_private" /cookies baseline device-east)" = 0
  printf '%s\n' '{"durable":{"rawIngressDatabaseExists":false,"captureSequences":[]}}' > "$measure_sequence_fixture"
  if deploy_measure_read_sequence "$target" /remote "$measure_sequence_private" /cookies missing device-east >/dev/null; then exit 111; fi
  printf '%s\n' '{"durable":{"rawIngressDatabaseExists":true,"captureSequences":[{"agentId":"device-east","lastSequence":1},{"agentId":"device-east","lastSequence":2}]}}' > "$measure_sequence_fixture"
  if deploy_measure_read_sequence "$target" /remote "$measure_sequence_private" /cookies duplicate device-east >/dev/null; then exit 112; fi
  printf '%s\n' '{"durable":{"rawIngressDatabaseExists":true,"captureSequences":[{"agentId":"device-east","lastSequence":-1}]}}' > "$measure_sequence_fixture"
  if deploy_measure_read_sequence "$target" /remote "$measure_sequence_private" /cookies negative device-east >/dev/null; then exit 113; fi
  printf '%s\n' '{"durable":{"rawIngressDatabaseExists":true,"captureSequences":[{"agentId":"device-east","lastSequence":1.5}]}}' > "$measure_sequence_fixture"
  if deploy_measure_read_sequence "$target" /remote "$measure_sequence_private" /cookies fractional device-east >/dev/null; then exit 114; fi
  printf '%s\n' '{"durable":{"rawIngressDatabaseExists":true,"captureSequences":[{"agentId":"device-east","lastSequence":2}]}}' > "$measure_sequence_fixture"
  jq '.captureLanes.value.availability="Degraded" | .captureLanes.value.lanes[0].retryCount=1' "$measure_sequence_operations" > "$measure_sequence_operations.changed"
  mv "$measure_sequence_operations.changed" "$measure_sequence_operations"
  test "$(deploy_measure_read_sequence "$target" /remote "$measure_sequence_private" /cookies retrying device-east)" = 2
  jq '.captureLanes.value.lanes[0].quarantineCount=1' "$measure_sequence_operations" > "$measure_sequence_operations.changed"
  mv "$measure_sequence_operations.changed" "$measure_sequence_operations"
  test "$(deploy_measure_unrecoverable_queue_reason "$measure_sequence_operations")" = required-lane-quarantine-standard
  if deploy_measure_read_sequence "$target" /remote "$measure_sequence_private" /cookies quarantined device-east >/dev/null 2>&1; then exit 115; fi
  jq '.captureLanes.value.lanes[0].quarantineCount=0 | .captureLanes.value.availability="Unhealthy"' "$measure_sequence_operations" > "$measure_sequence_operations.changed"
  mv "$measure_sequence_operations.changed" "$measure_sequence_operations"
  test "$(deploy_measure_unrecoverable_queue_reason "$measure_sequence_operations")" = capture-lanes-unhealthy
  jq '.captureLanes.value.availability="Healthy" | .transientWorker.value.availability="Unavailable"' "$measure_sequence_operations" > "$measure_sequence_operations.changed"
  mv "$measure_sequence_operations.changed" "$measure_sequence_operations"
  test "$(deploy_measure_unrecoverable_queue_reason "$measure_sequence_operations")" = transient-worker-unavailable
)
measure_tail_operations="$TEMP_DIR/measure-tail-operations.json"
printf '%s\n' '{"rawIngress":{"value":{"availability":"Accepting","pendingCount":6,"leasedCount":0,"retryCount":0,"quarantineCount":0,"terminalCount":0}},"captureLanes":{"value":{"availability":"Healthy","pendingCount":10,"leasedCount":0,"retryCount":0,"quarantineCount":0,"lanes":[{"name":"standard","required":true,"pendingCount":0,"leasedCount":0,"retryCount":0,"quarantineCount":0,"pressureLevel":0,"pendingCaptures":[]},{"name":"transient","required":true,"pendingCount":10,"leasedCount":0,"retryCount":0,"quarantineCount":0,"pressureLevel":0,"pendingCaptures":[{"agentId":"device-east","captureSequence":4},{"agentId":"device-east","captureSequence":5}]}]}},"captureProcessing":{"value":{"availability":"Healthy","pendingCount":0,"leasedCount":0,"retryCount":0,"quarantineCount":0,"terminalCount":0}},"artifactOutbox":{"value":{"availability":"Healthy","pendingCount":0,"leasedCount":0,"retryCount":0,"quarantineCount":0,"terminalCount":0}},"transientWorker":{"value":{"availability":"Healthy","pendingFrames":0,"pendingCandidates":0,"maximumCandidates":32}}}' > "$measure_tail_operations"
(source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations" 5 device-east Hybrid)
(source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/smoke.sh"; deploy_smoke_queues_converged "$measure_tail_operations" 5 device-east Hybrid)
jq '.rawIngress.value.pendingCount=0 | .captureLanes.value.pendingCount=0 |
  .captureLanes.value.lanes=[.captureLanes.value.lanes[] | select(.name != "transient")] |
  .transientWorker.value.availability="Disabled"' "$measure_tail_operations" > "$measure_tail_operations.off"
(source "$REPO_ROOT/scripts/deploy/common.sh"; deploy_capture_queues_converged "$measure_tail_operations.off" 5 device-east Off)
jq '.rawIngress.value.pendingCount=1 | .captureLanes.value.pendingCount=1 |
  .captureLanes.value.lanes[] |= if .name == "transient" then .pendingCount=1 | .pendingCaptures=[{"agentId":"device-east","captureSequence":1}] else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.one"
(source "$REPO_ROOT/scripts/deploy/common.sh"; deploy_capture_queues_converged "$measure_tail_operations.one" 1 device-east Hybrid)
jq '.rawIngress.value.pendingCount=0 | .captureLanes.value.pendingCount=0 |
  .captureLanes.value.lanes[] |= if .name == "transient" then .pendingCount=0 | .pendingCaptures=[] else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.zero"
(source "$REPO_ROOT/scripts/deploy/common.sh"; deploy_capture_queues_converged "$measure_tail_operations.zero" 0 device-east Hybrid)
jq '.captureLanes.value.pendingCount=8 | .captureLanes.value.lanes[] |= if .name == "transient" then .pendingCount=8 else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
(source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid)
jq '.rawIngress.value.pendingCount=2 | .captureLanes.value.pendingCount=2 | .captureLanes.value.lanes[] |= if .name == "transient" then .pendingCount=2 else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
(source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid)
jq '.rawIngress.value.pendingCount=3 | .captureLanes.value.pendingCount=3 | .captureLanes.value.lanes[] |= if .name == "transient" then .pendingCount=3 else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
(source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid)
jq '.captureLanes.value.pendingCount=66 | .captureLanes.value.lanes[] |= if .name == "transient" then .pendingCount=66 else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
(source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid)
jq '.captureLanes.value.pendingCount=67 | .captureLanes.value.lanes[] |= if .name == "transient" then .pendingCount=67 else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
if (source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid); then
  fail 'Measure accepted unbounded transient record fan-out.'
fi
jq '.rawIngress.value.pendingCount=2.5 | .captureLanes.value.pendingCount=8.5 | .captureLanes.value.lanes[] |= if .name == "transient" then .pendingCount=8.5 else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
if (source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid); then
  fail 'Measure accepted fractional queue record counts.'
fi
jq '.rawIngress.value.pendingCount=7' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
if (source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid); then
  fail 'Measure accepted unbounded raw-ingress record fan-out.'
fi
jq '.captureLanes.value.lanes[] |= if .name == "transient" then .retryCount=2 else . end | .captureLanes.value.retryCount=2' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
if (source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid); then
  fail 'Measure accepted retrying work as a temporal tail.'
fi
jq '.captureLanes.value.lanes[] |= if .name == "transient" then .pendingCaptures=[{"agentId":"device-east","captureSequence":3},{"agentId":"device-east","captureSequence":4},{"agentId":"device-east","captureSequence":5}] else . end' \
  "$measure_tail_operations" > "$measure_tail_operations.changed"
if (source "$REPO_ROOT/scripts/deploy/common.sh"; source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid); then
  fail 'Measure accepted stale captures as the bounded temporal tail.'
fi
for unsafe_tail in wrong-identity quarantine pressure unrelated-lane busy-worker unhealthy-worker missing-field disabled-hybrid; do
  case "$unsafe_tail" in
    wrong-identity) jq '.captureLanes.value.lanes[] |= if .name == "transient" then .pendingCaptures[0].agentId="wrong-device" else . end' "$measure_tail_operations" > "$measure_tail_operations.changed" ;;
    quarantine) jq '.captureLanes.value.quarantineCount=1 | .captureLanes.value.lanes[] |= if .name == "transient" then .quarantineCount=1 else . end' "$measure_tail_operations" > "$measure_tail_operations.changed" ;;
    pressure) jq '.captureLanes.value.lanes[] |= if .name == "transient" then .pressureLevel=2 else . end' "$measure_tail_operations" > "$measure_tail_operations.changed" ;;
    unrelated-lane) jq '.captureLanes.value.pendingCount+=1 | .captureLanes.value.lanes[] |= if .name == "standard" then .pendingCount=1 | .pendingCaptures=[{"agentId":"device-east","captureSequence":5}] else . end' "$measure_tail_operations" > "$measure_tail_operations.changed" ;;
    busy-worker) jq '.transientWorker.value.pendingCandidates=1' "$measure_tail_operations" > "$measure_tail_operations.changed" ;;
    unhealthy-worker) jq '.transientWorker.value.availability="Unavailable"' "$measure_tail_operations" > "$measure_tail_operations.changed" ;;
    missing-field) jq 'del(.captureLanes.value.lanes[0].pendingCount)' "$measure_tail_operations" > "$measure_tail_operations.changed" ;;
    disabled-hybrid) jq '.transientWorker.value.availability="Disabled"' "$measure_tail_operations" > "$measure_tail_operations.changed" ;;
  esac
  if (source "$REPO_ROOT/scripts/deploy/common.sh"; deploy_capture_queues_converged "$measure_tail_operations.changed" 5 device-east Hybrid); then
    fail "Shared queue convergence accepted $unsafe_tail."
  fi
done
for missing_measure_field in continuity telemetry timing queue stats; do
  cp "$TEMP_DIR/output/$deploy_case-state/measure-private/east-after-continuity.json" "$TEMP_DIR/measure-invalid-continuity.json"
  cp "$TEMP_DIR/output/$deploy_case-state/measure-private/east-after-operations.json" "$TEMP_DIR/measure-invalid-operations.json"
  cp "$TEMP_DIR/output/$deploy_case-state/measure-private/east-after-stats.json" "$TEMP_DIR/measure-invalid-stats.json"
  case "$missing_measure_field" in
    continuity) jq 'del(.durable.fleetNextSequence)' "$TEMP_DIR/measure-invalid-continuity.json" > "$TEMP_DIR/changed" && mv "$TEMP_DIR/changed" "$TEMP_DIR/measure-invalid-continuity.json" ;;
    telemetry) jq 'del(.captureTelemetry.value.averageLoopMilliseconds)' "$TEMP_DIR/measure-invalid-operations.json" > "$TEMP_DIR/changed" && mv "$TEMP_DIR/changed" "$TEMP_DIR/measure-invalid-operations.json" ;;
    timing) jq '.captureRuntime.value.timings[0].sampleCount="one"' "$TEMP_DIR/measure-invalid-operations.json" > "$TEMP_DIR/changed" && mv "$TEMP_DIR/changed" "$TEMP_DIR/measure-invalid-operations.json" ;;
    queue) jq 'del(.artifactOutbox.value.pendingBytes)' "$TEMP_DIR/measure-invalid-operations.json" > "$TEMP_DIR/changed" && mv "$TEMP_DIR/changed" "$TEMP_DIR/measure-invalid-operations.json" ;;
    stats) jq 'del(.networkIo)' "$TEMP_DIR/measure-invalid-stats.json" > "$TEMP_DIR/changed" && mv "$TEMP_DIR/changed" "$TEMP_DIR/measure-invalid-stats.json" ;;
  esac
  if (source "$REPO_ROOT/scripts/deploy/measure.sh"; deploy_measure_validate_snapshot "$TEMP_DIR/measure-invalid-continuity.json" \
    "$TEMP_DIR/measure-invalid-operations.json" "$TEMP_DIR/measure-invalid-stats.json" device-east); then
    fail "Measure accepted missing or invalid $missing_measure_field evidence."
  fi
done
jq -e 'all(.targets[]; .failureCleanup.status != "capture-may-be-running")' "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" >/dev/null
for raw_mismatch in id checksum length; do
  if FAKE_MEASURE_RAW_MISMATCH="$raw_mismatch" DEPLOY_TEST_MEASURE_DURATION_SECONDS=2 run_deploy_mode measure "$deploy_case" isolated --workload W1 > "$TEMP_DIR/measure-raw-$raw_mismatch.log" 2>&1; then
    fail "Measure accepted a central Raw $raw_mismatch mismatch."
  fi
  grep -Fq 'measured-convergence-timeout' "$TEMP_DIR/measure-raw-$raw_mismatch.log"
  run_deploy_mode measure "$deploy_case" isolated --workload W1 >/dev/null
done
control_lines="$(grep -c '^control' "$LOG")"
(( control_lines >= 12 )) || fail 'Measure did not issue multiple capture-control commands.'
if grep '^control' "$LOG" | grep -v $'\tdeploy-bootstrap-' | grep -Ev $'^[^\t]+\t[^\t]+-control[.]headers\t[^\t]+\t1\t1\tfalse$' >/dev/null; then
  fail 'A capture-control command did not use a fresh authenticated/antiforgery header with exactly one idempotency key.'
fi
if grep '^control' "$LOG" | grep $'\tdeploy-bootstrap-' | grep -Ev $'^[^\t]+\t[^\t]+-control[.]headers\tdeploy-bootstrap-[a-z0-9-]+-resume-[0-9]+\t1\t1\t(false)$' >/dev/null; then
  fail 'A bootstrap resume command did not use authenticated, antiforgery, and idempotency headers.'
fi
test "$(grep '^control' "$LOG" | grep -c $'\tdeploy-bootstrap-' || true)" -ge 2
if [[ "$(grep '^control' "$LOG" | grep -v $'\tdeploy-bootstrap-' | cut -f2 | sort | uniq -d | wc -l)" != 0 ]]; then fail 'Capture-control header path was reused.'; fi
if [[ "$(grep '^control' "$LOG" | grep -v $'\tdeploy-bootstrap-' | cut -f3 | sort | uniq -d | wc -l)" != 0 ]]; then fail 'Capture-control idempotency key was reused.'; fi
exercise_phase_cleanup_recovery measure "$deploy_case" --workload W1
exercise_phase_publication_recovery measure "$deploy_case" --workload W1
exercise_phase_tampered_publication_rejection measure "$deploy_case" --workload W1
exercise_phase_orphan_rejection measure "$deploy_case" --workload W1
# A workload-activated module may persist the bound device ID; later up must validate and preserve it.
bound_active_module="$lifecycle_east_root/config/camera-module.json"
jq '.agentId="device-east"' "$bound_active_module" > "$bound_active_module.changed" && mv "$bound_active_module.changed" "$bound_active_module"
chmod 600 "$bound_active_module"; bound_active_sha="$(sha256sum "$bound_active_module")"
run_deploy_mode up "$deploy_case" isolated >/dev/null
test "$(sha256sum "$bound_active_module")" = "$bound_active_sha" || fail 'Up reset a workload-activated bound module identity.'
jq -e 'all(.targets[] | select(.component == "cameraAgent"); .provisioningGate == false and .uploadEnabled == true)' "$TEMP_DIR/output/$deploy_case-state/up-ledger.json" >/dev/null
if grep -R -Eq 'owner-api-key-never-print|east-owner-never-print|west-owner-never-print|fixture-envelope-[0-9]+-never-print|VERIFY1234' \
  "$TEMP_DIR/output/$deploy_case-evidence" "$LOG"; then fail 'Bootstrap or smoke logs/evidence exposed private provisioning material.'; fi
