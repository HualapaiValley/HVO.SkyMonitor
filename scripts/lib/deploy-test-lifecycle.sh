#!/usr/bin/env bash
# Deployment contract test lifecycle: the deploy runners, phase-recovery exercisers,
# prepare-case helpers and lifecycle staging that every shard body in
# scripts/test:deploy-environment calls. Extracted unchanged from that harness
# (#875 stage 1) so a shard can be given a home outside the monolith without
# losing the staging it depends on. This file defines functions only; it is
# sourced by the harness after the fixture globals it reads (TEMP_DIR, REPO_ROOT,
# INVENTORY, BASE_INVENTORY, LOG, HTTP_PORT, FAKE_* state roots, deploy_case) are
# established, and it must not execute anything at source time.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a library sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

run_deploy() {
    local run_id="$1" status; shift
    deploy_test_checkpoint "preflight:$run_id:starting"
    if PATH="$BIN:$PATH" "$REPO_ROOT/scripts/deploy:environment" preflight --inventory "$INVENTORY" --mode isolated \
      --run-id "$run_id" --state-root "$TEMP_DIR/output/$run_id-state" --evidence-root "$TEMP_DIR/output/$run_id-evidence" "$@"; then status=0; else status=$?; fi
    deploy_test_checkpoint "preflight:$run_id:$([[ $status -eq 0 ]] && printf completed || printf failed)"
    return "$status"
}

run_deploy_mode() {
    local phase="$1" run_id="$2" mode="$3" status object_store_owner_host; shift 3
    object_store_owner_host="${FAKE_OBJECT_STORE_OWNER_HOST:-}"
    [[ "$phase" != up ]] || object_store_owner_host="${object_store_owner_host:-logic}"
    deploy_test_checkpoint "$phase:$run_id:starting"
    if FAKE_OBJECT_STORE_OWNER_HOST="$object_store_owner_host" \
      FAKE_RUNTIME_UID="${FAKE_RUNTIME_UID:-4242}" FAKE_RUNTIME_GID="${FAKE_RUNTIME_GID:-4343}" \
      PATH="$BIN:$PATH" "$REPO_ROOT/scripts/deploy:environment" "$phase" --inventory "$INVENTORY" --mode "$mode" \
      --run-id "$run_id" --state-root "$TEMP_DIR/output/$run_id-state" --evidence-root "$TEMP_DIR/output/$run_id-evidence" "$@"; then status=0; else status=$?; fi
    deploy_test_checkpoint "$phase:$run_id:$([[ $status -eq 0 ]] && printf completed || printf failed)"
    return "$status"
}

reset_measure_fixture() {
    local east_state="$1" west_state="$2" target name root original canonical
    grep -v '^control' "$LOG" > "$LOG.reset"
    grep '^control' "$LOG" | grep $'\tdeploy-bootstrap-' >> "$LOG.reset" || true
    mv "$LOG.reset" "$LOG"
    rm -rf "$TEMP_DIR/output/$deploy_case-state/measure-rendered" "$TEMP_DIR/output/$deploy_case-state/measure-private"
    rm -f "$TEMP_DIR/output/$deploy_case-state/measure-ledger.json" "$TEMP_DIR/output/$deploy_case-state/measure-manifest.json" \
      "$TEMP_DIR/output/$deploy_case-state/measure-commit.json" "$TEMP_DIR/output/$deploy_case-evidence/measure.json"
    printf '[]\n' > "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
    chmod 600 "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; root="$(jq -r '.runtimeRoot' <<< "$target")"
        original="$TEMP_DIR/$name-original-camera-module.json"
        cp "$original" "$root/config/camera-module.json"
        cp "$original" "$FAKE_HTTP_STATE/$name.active-profile.json"
        canonical="$(jq -S -c . "$original")"
        printf '%s' "$canonical" | sha256sum | cut -d' ' -f1 > "$FAKE_HTTP_STATE/$name.active-config-sha"
        rm -rf "$root/.hvo-deploy/measure-$deploy_case"
        rm -f "$FAKE_HTTP_STATE/$name.measure-profile" "$FAKE_HTTP_STATE/$name.measure-running" \
          "$FAKE_HTTP_STATE/$name.measure-stage" "$FAKE_HTTP_STATE/$name.schedule-active.json" "$FAKE_HTTP_STATE/$name.schedule-pending.json" \
          "$FAKE_HTTP_STATE/$name.schedule-w0-draft" "$FAKE_HTTP_STATE/$name.schedule-active-revision" \
          "$FAKE_HTTP_STATE/$name.schedule-pending-revision" "$FAKE_HTTP_STATE/$name.schedule-state-version" \
          "$FAKE_HTTP_STATE/$name.schedule-prior.json" "$FAKE_HTTP_STATE/$name.schedule-prior-revision"
        rm -f "$FAKE_HTTP_STATE/$name.idempotency."*.json
        cp "$original" "$FAKE_HTTP_STATE/$name.schedule-active.json"
        printf 'schedule-active-v1\n' > "$FAKE_HTTP_STATE/$name.schedule-active-revision"
        printf '1\n' > "$FAKE_HTTP_STATE/$name.schedule-state-version"
        printf '1\n' > "$FAKE_HTTP_STATE/$name.count"
        printf '10\n' > "$FAKE_HTTP_STATE/$name.control-version"
    done < <(jq -c '.cameraAgents[]' "$INVENTORY")
    printf '%s\n' "$east_state" > "$FAKE_HTTP_STATE/east.control-state"
    printf '%s\n' "$west_state" > "$FAKE_HTTP_STATE/west.control-state"
}

reset_smoke_fixture() {
    local target name root
    grep -v -E '^(control.*deploy-smoke-|schedule-(read|activate))' "$LOG" > "$LOG.reset" || true
    mv "$LOG.reset" "$LOG"
    rm -rf "$TEMP_DIR/output/$deploy_case-state/smoke-rendered" "$TEMP_DIR/output/$deploy_case-state/smoke-private"
    rm -f "$TEMP_DIR/output/$deploy_case-state/smoke-ledger.json" "$TEMP_DIR/output/$deploy_case-state/smoke-manifest.json" \
      "$TEMP_DIR/output/$deploy_case-state/smoke-commit.json" "$TEMP_DIR/output/$deploy_case-evidence/smoke.json"
    printf '[]\n' > "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
    chmod 600 "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json"
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; root="$(jq -r '.runtimeRoot' <<< "$target")"
        rm -rf "$root/.hvo-deploy/smoke-$deploy_case"
        rm -f "$FAKE_HTTP_STATE/$name.schedule-active.json" "$FAKE_HTTP_STATE/$name.schedule-pending.json" \
          "$FAKE_HTTP_STATE/$name.schedule-w0-draft" "$FAKE_HTTP_STATE/$name.schedule-active-revision" \
          "$FAKE_HTTP_STATE/$name.schedule-pending-revision" "$FAKE_HTTP_STATE/$name.schedule-state-version" \
          "$FAKE_HTTP_STATE/$name.schedule-prior.json" "$FAKE_HTTP_STATE/$name.schedule-prior-revision" \
          "$FAKE_HTTP_STATE/$name.smoke-resumed"
        rm -f "$FAKE_HTTP_STATE/$name.idempotency."*.json
        printf 'Paused\n' > "$FAKE_HTTP_STATE/$name.control-state"
    done < <(jq -c '.cameraAgents[]' "$INVENTORY")
}

run_measure_signal() {
    local signal="$1" expected="$2" marker="$3" log="$4" pid_file progress_file="" ledger pid status
    pid_file="$TEMP_DIR/measure-signal-${signal,,}.pid"
    shift 4
    deploy_test_checkpoint "measure:signal-${signal,,}:waiting"
    [[ -z "${DEPLOY_TEST_PROCESS_RECORD_DIR:-}" ]] || progress_file="$DEPLOY_TEST_PROCESS_RECORD_DIR/$SELECTED_SHARD.progress"
    ledger="$TEMP_DIR/output/$deploy_case-state/measure-ledger.json"
    rm -f "$marker" "$pid_file"
    if setsid bash -c '
      marker=$1; pid_file=$2; signal=$3; progress_file=$4; ledger=$5; shift 5; printf "%s\n" "$$" > "$pid_file"
      (ready=false; generation=-1; deadline=$((SECONDS + 300))
       while (( SECONDS <= deadline )); do
         if [[ -f "$ledger" ]]; then
           current_generation="$(jq -r ".publicationGeneration // 0" "$ledger" 2>/dev/null || printf 0)"
           if [[ "$current_generation" =~ ^[0-9]+$ && "$current_generation" != "$generation" ]]; then
             generation=$current_generation
             if [[ -n "$progress_file" ]]; then
               sequence="$(sed -n "s/^sequence=\\([0-9][0-9]*\\) .*/\\1/p" "$progress_file" 2>/dev/null || true)"
               [[ "$sequence" =~ ^[0-9]+$ ]] || sequence=0
               temporary="$progress_file.$BASHPID"
               printf "sequence=%s checkpoint=measure-signal-%s-generation-%s\n" "$((sequence + 1))" "${signal,,}" "$generation" > "$temporary"
               chmod 600 "$temporary"; mv -fT -- "$temporary" "$progress_file"
             fi
           fi
         fi
         [[ ! -e "$marker" ]] || { ready=true; break; }
         sleep 1
       done
       if [[ "$ready" == true ]]; then kill -s "$signal" -- "-$$"; else kill -KILL -- "-$$"; fi) &
      exec "$@"
    ' signal-run "$marker" "$pid_file" "$signal" "$progress_file" "$ledger" env "$@" PATH="$BIN:$PATH" \
        "$REPO_ROOT/scripts/deploy:environment" measure --inventory "$INVENTORY" --mode isolated \
        --run-id "$deploy_case" --state-root "$TEMP_DIR/output/$deploy_case-state" --evidence-root "$TEMP_DIR/output/$deploy_case-evidence" \
        --workload W1 > "$log" 2>&1; then status=0; else status=$?; fi
    [[ -e "$marker" ]] || fail "Measure did not reach the $signal signal boundary."
    pid="$(<"$pid_file")"
    [[ "$status" == "$expected" ]] || fail "Measure $signal returned $status instead of $expected."
    ! kill -0 -- "-$pid" 2>/dev/null || fail "Measure $signal retained a process-group descendant."
    deploy_test_checkpoint "measure:signal-${signal,,}:completed"
}

run_deploy_mode_with_registry_failure() {
    local match="$1" phase="$2" run_id="$3" mode="$4" status; shift 4
    deploy_test_checkpoint "$phase:$run_id:registry-failure-starting"
    if env DEPLOY_TEST_FAILPOINT="private-registry-publication:$match" PATH="$BIN:$PATH" \
      "$REPO_ROOT/scripts/deploy:environment" "$phase" --inventory "$INVENTORY" --mode "$mode" \
      --run-id "$run_id" --state-root "$TEMP_DIR/output/$run_id-state" --evidence-root "$TEMP_DIR/output/$run_id-evidence" "$@"; then status=0; else status=$?; fi
    deploy_test_checkpoint "$phase:$run_id:registry-failure-$([[ $status -eq 0 ]] && printf completed || printf failed)"
    return "$status"
}

exercise_phase_publication_recovery() {
    local phase="$1" run_id="$2" state="$TEMP_DIR/output/$2-state" evidence="$TEMP_DIR/output/$2-evidence" boundary
    shift 2
    for boundary in ledger manifest evidence commit; do
        if [[ "$phase" == bootstrap ]]; then
            mark_bootstrap_phase_incomplete "$state" "$evidence"
        fi
        if DEPLOY_TEST_FAILPOINT="abrupt-after-$phase-$boundary" run_deploy_mode "$phase" "$run_id" isolated "$@" > "$TEMP_DIR/$phase-$boundary-crash.log" 2>&1; then
            fail "$phase publication failpoint passed after $boundary."
        fi
        jq -e 'length == 0' "$state/private-upload-registry.json" >/dev/null ||
          fail "$phase publication boundary retained private cleanup entries."
        run_deploy_mode "$phase" "$run_id" isolated "$@" >/dev/null
        jq -e --slurp '.[0] == .[1] and .[0] == .[2]' \
          "$state/$phase-ledger.json" "$state/$phase-manifest.json" "$evidence/$phase.json" >/dev/null
        generation="$(jq -r '.publicationGeneration' "$state/$phase-ledger.json")"
        test "$(jq -r '.generation' "$state/$phase-commit.json")" = "$generation"
    done
}

exercise_phase_cleanup_recovery() {
    local phase="$1" run_id="$2" state="$TEMP_DIR/output/$2-state" evidence="$TEMP_DIR/output/$2-evidence"
    shift 2
    if DEPLOY_TEST_FAILPOINT="abrupt-after-$phase-cleanup" run_deploy_mode "$phase" "$run_id" isolated "$@" > "$TEMP_DIR/$phase-cleanup-crash.log" 2>&1; then
        fail "$phase cleanup boundary failpoint passed."
    fi
    jq -e 'length == 0' "$state/private-upload-registry.json" >/dev/null || fail "$phase cleanup boundary retained private files."
    if jq -e '.phaseStatus == "passed"' "$evidence/$phase.json" >/dev/null; then
        fail "$phase published passed evidence before cleanup-boundary recovery."
    fi
    run_deploy_mode "$phase" "$run_id" isolated "$@" >/dev/null
}

exercise_phase_tampered_publication_rejection() {
    local phase="$1" run_id="$2" state="$TEMP_DIR/output/$2-state" evidence="$TEMP_DIR/output/$2-evidence"
    shift 2
    cp "$state/$phase-ledger.json" "$TEMP_DIR/$phase.valid-ledger"
    cp "$state/$phase-manifest.json" "$TEMP_DIR/$phase.valid-manifest"
    cp "$evidence/$phase.json" "$TEMP_DIR/$phase.valid-evidence"
    cp "$state/$phase-commit.json" "$TEMP_DIR/$phase.valid-commit"
    jq '.publicationGeneration += 1 | .unexpectedCrashField=true' "$TEMP_DIR/$phase.valid-ledger" > "$state/$phase-ledger.json"
    chmod 600 "$state/$phase-ledger.json"
    if run_deploy_mode "$phase" "$run_id" isolated "$@" > "$TEMP_DIR/$phase-tampered-publication.log" 2>&1; then
        fail "$phase accepted a malformed next-generation ledger."
    fi
    cmp -s "$TEMP_DIR/$phase.valid-manifest" "$state/$phase-manifest.json" || fail "$phase changed its prior manifest after rejecting a malformed ledger."
    cmp -s "$TEMP_DIR/$phase.valid-evidence" "$evidence/$phase.json" || fail "$phase changed its prior evidence after rejecting a malformed ledger."
    cmp -s "$TEMP_DIR/$phase.valid-commit" "$state/$phase-commit.json" || fail "$phase changed its prior commit after rejecting a malformed ledger."
    cp "$TEMP_DIR/$phase.valid-ledger" "$state/$phase-ledger.json"
    chmod 600 "$state/$phase-ledger.json"
}

exercise_phase_orphan_rejection() {
    local phase="$1" run_id="$2" state="$TEMP_DIR/output/$2-state"
    shift 2
    mv "$state/$phase-ledger.json" "$TEMP_DIR/$phase.orphan-ledger"
    if run_deploy_mode "$phase" "$run_id" isolated "$@" > "$TEMP_DIR/$phase-orphan.log" 2>&1; then
        fail "$phase accepted companion files without its authoritative ledger."
    fi
    grep -Fq 'orphan-phase-companion' "$TEMP_DIR/$phase-orphan.log"
    mv "$TEMP_DIR/$phase.orphan-ledger" "$state/$phase-ledger.json"
}

write_prepare_case() {
    local prefix="$1" mode="$2"
    mkdir -p "$TEMP_DIR/remote/$prefix-skymonitor/logichosts" "$TEMP_DIR/remote/$prefix-skymonitor/cameraagents" "$TEMP_DIR/remote/$prefix-skymonitor/catalogs"
    chmod 700 "$TEMP_DIR/remote/$prefix-skymonitor" "$TEMP_DIR/remote/$prefix-skymonitor/logichosts" "$TEMP_DIR/remote/$prefix-skymonitor/cameraagents" "$TEMP_DIR/remote/$prefix-skymonitor/catalogs"
    jq --arg base "$TEMP_DIR/remote" --arg prefix "$prefix" '
      .productRoot=($base+"/"+$prefix+"-skymonitor") |
      .logicHost.runtimeRoot=(.productRoot+"/logichosts/"+.logicHost.instanceId) |
      .cameraAgents[0].runtimeRoot=(.productRoot+"/cameraagents/"+.cameraAgents[0].instanceId) |
      .cameraAgents[1].runtimeRoot=(.productRoot+"/cameraagents/"+.cameraAgents[1].instanceId) |
      .productRoot as $root | .catalogs |= map(.installRoot=($root+"/catalogs/"+.catalogId))' "$BASE_INVENTORY" > "$INVENTORY"
    if [[ "$mode" == persistent ]]; then
        jq '(.logicHost.publicEndpoint, .cameraAgents[].publicEndpoint) |= sub("^http:";"https:") |
          .logicHost.trustedProxyAddresses=["127.0.0.1"] | (.cameraAgents[].trustedProxyAddresses)=["127.0.0.1"]' "$INVENTORY" > "$INVENTORY.changed"
        mv "$INVENTORY.changed" "$INVENTORY"
    fi
}

inventory_runtime_root() {
    local name="$1"
    jq -r --arg name "$name" '([.logicHost] + .cameraAgents + [select(.sharedServices != null) | .sharedServices])[] |
      select(.name == $name) | .runtimeRoot' "$INVENTORY"
}

run_prepare_preflight() {
    local run_id="$1" mode="$2"
    if [[ "$mode" == persistent ]]; then FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode preflight "$run_id" "$mode" >/dev/null
    else run_deploy_mode preflight "$run_id" "$mode" >/dev/null; fi
}

expect_prepare_failure() {
    local expected="$1" run_id="$2" mode="$3"
    if run_deploy_mode prepare "$run_id" "$mode" > "$TEMP_DIR/prepare-failure.log" 2>&1; then fail "Invalid prepare passed: $run_id"; fi
    grep -Fq "$expected" "$TEMP_DIR/prepare-failure.log" || fail "Prepare failure was not bounded: $run_id ($expected)"
    ! grep -Fq 'fixture-secret-never-print' "$TEMP_DIR/prepare-failure.log" || fail 'Prepare failure exposed a secret.'
}

create_partial_prepare_case() {
    local run_id="$1"
    write_prepare_case "$run_id" isolated
    run_prepare_preflight "$run_id" isolated
    FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' "$run_id" isolated
}

expect_failure() {
    local expected="$1" run_id="$2"
    if run_deploy "$run_id" > "$TEMP_DIR/failure.log" 2>&1; then fail "Invalid preflight passed: $expected"; fi
    grep -Fq "$expected" "$TEMP_DIR/failure.log" || fail "Missing bounded failure: $expected"
    if grep -Fq 'fixture-secret-never-print' "$TEMP_DIR/failure.log" || grep -Fq 'malicious-' "$TEMP_DIR/failure.log"; then
        fail 'Failure output exposed a fixture secret or raw diagnostic.'
    fi
    ! grep -Fq 'raw-docker-secret' "$TEMP_DIR/failure.log" || fail 'Failure output exposed malformed Docker response content.'
}

expect_persistent_invalid() {
    local run_id="$1" expected="${2:-persistent-mode-requires-production-https}"
    if PATH="$BIN:$PATH" "$REPO_ROOT/scripts/deploy:environment" preflight --inventory "$INVENTORY" --mode persistent \
      --run-id "$run_id" --state-root "$TEMP_DIR/output/$run_id-state" --evidence-root "$TEMP_DIR/output/$run_id-evidence" \
      > "$TEMP_DIR/failure.log" 2>&1; then fail "Invalid persistent inventory passed: $run_id"; fi
    grep -Fq "$expected" "$TEMP_DIR/failure.log" || fail "Persistent rejection was not bounded: $run_id"
}

prepare_lifecycle_fixture() {
  deploy_case=deploy-up
  bundle="$TEMP_DIR/catalog-bundle"
  install_root="$TEMP_DIR/remote/$deploy_case-skymonitor/catalogs/test-fixture"
  mkdir -m 700 "$bundle"
  cp "$REPO_ROOT/tests/fixtures/catalog/hyg-v42-bright-stars.sqlite" "$bundle/hyg_v42.sqlite"
  catalog_sha="$(sha256sum "$bundle/hyg_v42.sqlite" | cut -d' ' -f1)"
  catalog_length="$(wc -c < "$bundle/hyg_v42.sqlite")"
  jq -n --arg sha "$catalog_sha" --argjson length "$catalog_length" '{manifestVersion:2,
    package:{kind:"fixture",version:"fixture-v1"},catalog:{id:"test-fixture",name:"HYG bright-star test fixture",version:"4.2-fixture.1"},
    schemaVersion:"2",preprocessingVersion:"3",database:{relativePath:"hyg_v42.sqlite",sha256:$sha,length:$length,rowCount:9}}' > "$bundle/manifest.json"
  printf 'certificate-fixture\n' > "$TEMP_DIR/signing.pfx"
  printf 'certificate-fixture\n' > "$TEMP_DIR/encryption.pfx"
  chmod 600 "$TEMP_DIR/signing.pfx" "$TEMP_DIR/encryption.pfx"
  jq --arg root "$TEMP_DIR/remote/$deploy_case" --arg bundle "$bundle" --arg install "$install_root" --arg endpoint "http://127.0.0.1:$HTTP_PORT" \
    --arg public "https://public.example.test:$HTTP_PORT" \
    --arg signing "$TEMP_DIR/signing.pfx" --arg encryption "$TEMP_DIR/encryption.pfx" --arg sha "$catalog_sha" \
    --arg transientMode "${FAKE_LIFECYCLE_TRANSIENT_MODE:-Off}" --argjson port "$HTTP_PORT" --argjson length "$catalog_length" '
    .productRoot=($root+"-skymonitor") |
    .catalogs=[{catalogId:"test-fixture",displayName:"Test fixture",kind:"fixture",version:"fixture-v1",schemaVersion:"2",preprocessingVersion:"3",sha256:$sha,length:$length,rowCount:9,bundlePath:$bundle,installRoot:$install,allowFixture:true}] |
    .logicHost.catalogId="test-fixture" | (.cameraAgents[].catalogId)="test-fixture" |
    .deployment.services.sql.database="hvo-deploy-up" | .deployment.resources.sqlDatabase="hvo-deploy-up" |
    .deployment.services.redis.prefix="hvo-deploy-up:" | .deployment.resources.redisPrefix="hvo-deploy-up:" |
    .deployment.services.objectStore.artifactBucket="hvo-deploy-up-artifacts" | .deployment.resources.artifactBucket="hvo-deploy-up-artifacts" |
    .deployment.services.objectStore.diagnosticsBucket="hvo-deploy-up-diagnostics" | .deployment.resources.diagnosticsBucket="hvo-deploy-up-diagnostics" |
    .deployment.resources.project="hvo-deploy-up" |
    .deployment.transient.mode=$transientMode |
    .deployment.certificates.signingPath=$signing | .deployment.certificates.encryptionPath=$encryption |
    .logicHost.runtimeRoot=(.productRoot+"/logichosts/"+.logicHost.instanceId) | .logicHost.internalEndpoint=$endpoint | .logicHost.publicEndpoint=$public | .logicHost.trustedProxyAddresses=["127.0.0.1"] | .logicHost.ports=[$port] |
    (.cameraAgents[0].runtimeRoot)=(.productRoot+"/cameraagents/"+.cameraAgents[0].instanceId) | (.cameraAgents[0].internalEndpoint)=$endpoint | (.cameraAgents[0].publicEndpoint)=$public | (.cameraAgents[0].trustedProxyAddresses)=["127.0.0.1"] | (.cameraAgents[0].ports)=[$port] |
    (.cameraAgents[1].runtimeRoot)=(.productRoot+"/cameraagents/"+.cameraAgents[1].instanceId) | (.cameraAgents[1].internalEndpoint)=$endpoint | (.cameraAgents[1].publicEndpoint)=$public | (.cameraAgents[1].trustedProxyAddresses)=["127.0.0.1"] | (.cameraAgents[1].ports)=[$port] |
    (.deployment.services.sql.port,.deployment.services.redis.port,.deployment.services.smtp.ports[0])=$port |
    .serviceEndpoints=[{name:"test-service",host:"127.0.0.1",port:$port,fromTargets:["logic","east","west"]}]' \
    "$BASE_INVENTORY" > "$INVENTORY"
  mkdir -p "$TEMP_DIR/remote/$deploy_case-skymonitor/logichosts" "$TEMP_DIR/remote/$deploy_case-skymonitor/cameraagents" "$TEMP_DIR/remote/$deploy_case-skymonitor/catalogs"
  chmod 700 "$TEMP_DIR/remote/$deploy_case-skymonitor" "$TEMP_DIR/remote/$deploy_case-skymonitor/logichosts" "$TEMP_DIR/remote/$deploy_case-skymonitor/cameraagents" "$TEMP_DIR/remote/$deploy_case-skymonitor/catalogs"
}

# Assigns the lifecycle globals the shard bodies under scripts/deploy-contracts/ read after
# they are sourced into the same shell; the readers are in other files.
# shellcheck disable=SC2034
set_lifecycle_paths() {
  lifecycle_logic_root="$(jq -r '.logicHost.runtimeRoot' "$INVENTORY")"
  lifecycle_east_root="$(jq -r '.cameraAgents[0].runtimeRoot' "$INVENTORY")"
  lifecycle_west_root="$(jq -r '.cameraAgents[1].runtimeRoot' "$INVENTORY")"
  logic_config="$lifecycle_logic_root/config"
  east_config="$lifecycle_east_root/config"
  bootstrap_state="$TEMP_DIR/output/$deploy_case-state"
  bootstrap_rendered="$bootstrap_state/bootstrap-rendered"
  bootstrap_private="$bootstrap_state/bootstrap-private"
  logic_private="$lifecycle_logic_root/.hvo-deploy/bootstrap-$deploy_case"
  east_private="$lifecycle_east_root/.hvo-deploy/bootstrap-$deploy_case"
}

stage_lifecycle_images() {
  prepare_lifecycle_fixture
  run_deploy_mode preflight "$deploy_case" isolated >/dev/null
  run_deploy_mode prepare "$deploy_case" isolated >/dev/null
  rm -f "$FAKE_IMAGE_STATE"/*.pushed "$FAKE_IMAGE_STATE"/*.platforms "$FAKE_IMAGE_STATE"/*.labels "$FAKE_IMAGE_STATE"/*.reference "$FAKE_IMAGE_STATE"/*.config
  run_deploy_mode images "$deploy_case" isolated >/dev/null
}

stage_lifecycle_up() {
  stage_lifecycle_images
  run_deploy_mode catalog "$deploy_case" isolated >/dev/null
  run_deploy_mode up "$deploy_case" isolated >/dev/null
  set_lifecycle_paths
}

stage_lifecycle_bootstrap() {
  stage_lifecycle_up
  run_deploy_mode bootstrap "$deploy_case" isolated >/dev/null
  set_lifecycle_paths
}

stage_lifecycle_smoke() {
  stage_lifecycle_bootstrap
  run_deploy_mode smoke "$deploy_case" isolated >/dev/null
  set_lifecycle_paths
}
