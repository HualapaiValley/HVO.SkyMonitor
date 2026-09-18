#!/usr/bin/env bash
# Deployment contract shard body: prepare-images. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

# Prepare requires the exact passed preflight and mutates only declared roots/control metadata.
mkdir -p "$TEMP_DIR/remote/prepare-skymonitor/logichosts" "$TEMP_DIR/remote/prepare-skymonitor/cameraagents" "$TEMP_DIR/remote/prepare-skymonitor/catalogs"
chmod 700 "$TEMP_DIR/remote/prepare-skymonitor" "$TEMP_DIR/remote/prepare-skymonitor/logichosts" "$TEMP_DIR/remote/prepare-skymonitor/cameraagents" "$TEMP_DIR/remote/prepare-skymonitor/catalogs"
jq --arg base "$TEMP_DIR/remote/prepare-skymonitor" --arg owner "$(id -un)" '
  .productRoot=$base |
  .logicHost.runtimeRoot=($base+"/logichosts/"+.logicHost.instanceId) |
  .cameraAgents[0].runtimeRoot=($base+"/cameraagents/"+.cameraAgents[0].instanceId) |
  .cameraAgents[1].runtimeRoot=($base+"/cameraagents/"+.cameraAgents[1].instanceId) |
  .catalogs |= map(.installRoot=($base+"/catalogs/"+.catalogId)) |
  .logicHost.runtimeOwner=$owner' "$BASE_INVENTORY" > "$INVENTORY"
cp "$INVENTORY" "$TEMP_DIR/prepare-happy.inventory"
run_deploy prepare-happy >/dev/null
docker_count_before_prepare="$(grep -c '^docker' "$LOG")"
run_deploy_mode prepare prepare-happy isolated > "$TEMP_DIR/prepare.out"
docker_count_after_prepare="$(grep -c '^docker' "$LOG")"
[[ "$docker_count_before_prepare" == "$docker_count_after_prepare" ]] || fail 'Prepare invoked Docker.'
grep -Fq $'logic@example\tbash\t-s\t--' "$LOG"
grep -Fq $'east@example\tbash\t-s\t--' "$LOG"
while IFS= read -r prepared_target; do
    test -d "$prepared_target/.hvo-deploy"
    test "$(stat -c %a "$prepared_target")" = 700
    test "$(stat -c %u "$prepared_target")" = "$(id -u)"
    test "$(stat -c %u "$prepared_target/.hvo-deploy")" = "$(id -u)"
    test "$(stat -c %a "$prepared_target/.hvo-deploy/ownership")" = 600
    test "$(stat -c %u "$prepared_target/.hvo-deploy/ownership")" = "$(id -u)"
done < <(jq -r '([.logicHost] + .cameraAgents)[].runtimeRoot' "$INVENTORY")
# shellcheck disable=SC2016 # Assert literal commands in the captured remote script.
grep -Fq '[[ "$(id -u)" == "$runtime_uid" ]]' "$PREPARE_REMOTE_LOG"
# shellcheck disable=SC2016
grep -Fq 'chmod 700 "$root" "$control"' "$PREPARE_REMOTE_LOG"
# shellcheck disable=SC2016
grep -Fq 'chmod 600 "$marker"' "$PREPARE_REMOTE_LOG"
if grep -Fq 'chown ' "$PREPARE_REMOTE_LOG"; then fail 'Prepare retained privileged ownership mutation.'; fi
prepare_ledger="$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"
prepare_manifest="$TEMP_DIR/output/prepare-happy-state/prepare-manifest.json"
prepare_evidence="$TEMP_DIR/output/prepare-happy-evidence/prepare.json"
jq -e '.phaseStatus == "passed" and (.targets | length) == 3 and all(.targets[]; .disposition == "newly-created")' "$prepare_ledger" >/dev/null
jq -e --slurp '.[0].phaseStatus == "passed" and .[1].phaseStatus == "passed" and
  ([.[0].targets[].target] | sort) == ([.[1].targets[].target] | sort)' "$prepare_manifest" "$prepare_evidence" >/dev/null
run_deploy_mode prepare prepare-happy isolated >/dev/null
jq -e 'all(.targets[]; .disposition == "newly-created" and .newlyCreated.root == true)' "$prepare_ledger" >/dev/null

# Images requires exact prepare state, builds only on the control host, and verifies every target through its declared context.
: > "$LOG"
if DEPLOY_TEST_FAILPOINT=abrupt-after-registry-push run_deploy_mode images prepare-happy isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Abrupt post-push failpoint passed.'; fi
jq -e '.phaseStatus == "running" and [.images[].status] == ["push-intended"]' "$TEMP_DIR/output/prepare-happy-state/images-ledger.json" >/dev/null
run_deploy_mode images prepare-happy isolated > "$TEMP_DIR/images-registry.out"
images_ledger="$TEMP_DIR/output/prepare-happy-state/images-ledger.json"
images_evidence="$TEMP_DIR/output/prepare-happy-evidence/images.json"
jq -e '.phaseStatus == "passed" and .distributionMode == "registry" and (.images | length) == 2 and (.targets | length) == 3 and
  all(.images[]; (.reference | test("@sha256:[0-9a-f]{64}$"))) and all(.targets[]; .status == "verified")' "$images_ledger" >/dev/null
jq -e '.sourceTree | test("^[0-9a-f]{40,64}$")' "$images_ledger" >/dev/null
jq -e '(.targets[] | select(.target == "east") | .architecture) == "arm64" and
  (.targets[] | select(.target == "west") | .architecture) == "amd64"' "$images_ledger" >/dev/null
jq -e '.phaseStatus == "passed" and (.targets | length) == 3' "$images_evidence" >/dev/null
grep -Fq $'docker\tbuildx\tbuild\t--builder\tcontrol-builder\t--platform\tlinux/amd64' "$LOG"
grep -Fq $'docker\tbuildx\tbuild\t--builder\tcontrol-builder\t--platform\tlinux/amd64,linux/arm64' "$LOG"
grep -Fq $'docker\t--context\teast-context\timage\tpull\tregistry.example/cameraagent@sha256:' "$LOG"
grep -Fq $'docker\tbuildx\timagetools\tinspect\t--builder\tcontrol-builder' "$LOG"
jq -e '.images[] | select(.component == "cameraAgent") | [.platforms[].architecture] == ["amd64","arm64"]' "$images_ledger" >/dev/null
jq -e --arg a "sha256:$(printf 'a%.0s' {1..64})" --arg b "sha256:$(printf 'b%.0s' {1..64})" --arg c "sha256:$(printf 'c%.0s' {1..64})" '
  (.images[] | select(.component == "logicHost") | .platforms) == [{architecture:"amd64",digest:$a}] and
  (.images[] | select(.component == "cameraAgent") | .platforms) == [{architecture:"amd64",digest:$c},{architecture:"arm64",digest:$b}]' "$images_ledger" >/dev/null
while IFS= read -r target_reference; do grep -Fq $'\timage\tpull\t'"$target_reference" "$LOG"; done < <(jq -r '.targets[].reference' "$images_ledger" | sort -u)
(( $(grep -c $'^docker\t--context\tlogic-context\tinfo' "$LOG") >= 3 )) || fail 'Logic target was not re-correlated around pull and validation.'
build_count_before_resume="$(grep -c $'^docker\tbuildx\tbuild' "$LOG")"
if FAKE_IMAGE_INSPECT_DRIFT=east-context run_deploy_mode images prepare-happy isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Completed image architecture drift passed.'; fi
grep -Fq 'completed-image-drift' "$TEMP_DIR/images-failure.log"
if FAKE_DOCKER_MISMATCH=west-context run_deploy_mode images prepare-happy isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Same-architecture wrong daemon passed.'; fi
grep -Fq 'docker-daemon-correlation-mismatch' "$TEMP_DIR/images-failure.log"
if FAKE_DOCKER_UNSUPPORTED_ARCH=west-context run_deploy_mode images prepare-happy isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Unsupported target daemon architecture passed.'; fi
grep -Fq 'docker-daemon-unsupported-architecture' "$TEMP_DIR/images-failure.log"
! grep -Fq 'ppc64le' "$TEMP_DIR/images-failure.log" || fail 'Image correlation leaked raw unsupported architecture.'
run_deploy_mode images prepare-happy isolated >/dev/null
build_count_after_resume="$(grep -c $'^docker\tbuildx\tbuild' "$LOG")"
[[ "$build_count_before_resume" == "$build_count_after_resume" ]] || fail 'Images resume rebuilt completed registry images.'
if grep -Eq $'docker\t.*\t(compose|container|run|start|up|down)(\t|$)' "$LOG"; then fail 'Images phase started or managed applications.'; fi
if grep -Eq $'docker\tbuildx\t(rm|stop)' "$LOG"; then fail 'Images phase destructively managed the buildx builder.'; fi

: > "$FAKE_IMAGE_STATE/git-status-count"
if FAKE_SOURCE_DIRTY_AT_STATUS=1 run_deploy_mode images prepare-happy isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Dirty image source passed.'; fi
grep -Fq 'images-require-clean-source' "$TEMP_DIR/images-failure.log"

write_prepare_case images-registry-duplicate isolated
run_prepare_preflight images-registry-duplicate isolated
run_deploy_mode prepare images-registry-duplicate isolated >/dev/null
registry_builds_before_adopt="$(grep -c $'^docker\tbuildx\tbuild' "$LOG")"
run_deploy_mode images images-registry-duplicate isolated >/dev/null
registry_builds_after_adopt="$(grep -c $'^docker\tbuildx\tbuild' "$LOG")"
[[ "$registry_builds_before_adopt" == "$registry_builds_after_adopt" ]] || fail 'Exact registry adoption rebuilt or overwrote the tag.'

write_prepare_case images-registry-mismatch isolated
run_prepare_preflight images-registry-mismatch isolated
run_deploy_mode prepare images-registry-mismatch isolated >/dev/null
if FAKE_REGISTRY_MISMATCH_COMPONENT=logicHost run_deploy_mode images images-registry-mismatch isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Mismatched registry tag was adopted or overwritten.'; fi
grep -Fq 'registry-config-mismatch' "$TEMP_DIR/images-failure.log"

write_prepare_case images-wrong-builder isolated
run_prepare_preflight images-wrong-builder isolated
run_deploy_mode prepare images-wrong-builder isolated >/dev/null
if FAKE_BUILDER_REMOTE=true run_deploy_mode images images-wrong-builder isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Remote buildx builder passed.'; fi
grep -Fq 'mismatch-or-remote' "$TEMP_DIR/images-failure.log"

write_prepare_case images-canonical-local-builder isolated
run_prepare_preflight images-canonical-local-builder isolated
run_deploy_mode prepare images-canonical-local-builder isolated >/dev/null
FAKE_BUILDER_CANONICAL_LOCAL=true FAKE_BUILD_ARCHIVE_BLOB_CONFIG=true FAKE_CONTAINERD_IMAGE_STORE=true \
  run_deploy_mode images images-canonical-local-builder isolated >/dev/null

write_prepare_case images-native-multi-node-builder isolated
jq '.images.builder.driver="docker-container"' "$INVENTORY" > "$INVENTORY.changed" && mv "$INVENTORY.changed" "$INVENTORY"
run_prepare_preflight images-native-multi-node-builder isolated
run_deploy_mode prepare images-native-multi-node-builder isolated >/dev/null
FAKE_BUILDER_MULTI_NODE=true run_deploy_mode images images-native-multi-node-builder isolated >/dev/null

write_prepare_case images-target-wrong-daemon isolated
run_prepare_preflight images-target-wrong-daemon isolated
run_deploy_mode prepare images-target-wrong-daemon isolated >/dev/null
if FAKE_DOCKER_MISMATCH=logic-context run_deploy_mode images images-target-wrong-daemon isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Wrong same-architecture daemon passed before pull.'; fi
grep -Fq 'docker-daemon-correlation-mismatch' "$TEMP_DIR/images-failure.log"
jq -e '(.targets | length) == 0' "$TEMP_DIR/output/images-target-wrong-daemon-state/images-ledger.json" >/dev/null

write_prepare_case images-without-prepare isolated
if run_deploy_mode images images-without-prepare isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Images passed without prepare.'; fi
grep -Fq 'prepare-required' "$TEMP_DIR/images-failure.log"

write_prepare_case images-archive isolated
archive_root="$TEMP_DIR/artifacts/images-archive"
mkdir -m 700 "$TEMP_DIR/artifacts"
jq --arg root "$archive_root" '.images.distributionMode="archive" | .images.registryImmutableTags=false | .images.artifactRoot=$root' "$INVENTORY" > "$INVENTORY.changed"
mv "$INVENTORY.changed" "$INVENTORY"
run_prepare_preflight images-archive isolated
run_deploy_mode prepare images-archive isolated >/dev/null
: > "$LOG"
if DEPLOY_TEST_FAILPOINT=after-image-build run_deploy_mode images images-archive isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Image build failpoint passed.'; fi
jq -e '.phaseStatus == "failed" and (.images | length) == 1 and (.targets | length) == 0' "$TEMP_DIR/output/images-archive-state/images-ledger.json" >/dev/null
if DEPLOY_TEST_FAILPOINT=after-image-transfer run_deploy_mode images images-archive isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Image transfer failpoint passed.'; fi
jq -e '.phaseStatus == "failed" and (.images | length) == 3 and (.targets | length) == 0' "$TEMP_DIR/output/images-archive-state/images-ledger.json" >/dev/null
if DEPLOY_TEST_FAILPOINT=abrupt-after-images-ledger run_deploy_mode images images-archive isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Image ledger publication failpoint passed.'; fi
jq -e '.phaseStatus == "passed" and (.images | length) == 3 and (.targets | length) == 3' "$TEMP_DIR/output/images-archive-state/images-ledger.json" >/dev/null
jq -e '.phaseStatus == "running"' "$TEMP_DIR/output/images-archive-state/images-manifest.json" >/dev/null
jq -e '.phaseStatus == "running"' "$TEMP_DIR/output/images-archive-evidence/images.json" >/dev/null
run_deploy_mode images images-archive isolated >/dev/null
jq -e --slurp '.[0] == .[1] and .[0].phaseStatus == "passed"' \
  "$TEMP_DIR/output/images-archive-state/images-ledger.json" "$TEMP_DIR/output/images-archive-state/images-manifest.json" >/dev/null
if DEPLOY_TEST_FAILPOINT=abrupt-after-images-ledger run_deploy_mode images images-archive isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Second image ledger publication failpoint passed.'; fi
rm -f "$TEMP_DIR/output/images-archive-evidence/images.json"
if DEPLOY_TEST_FAILPOINT=abrupt-after-images-evidence run_deploy_mode images images-archive isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Image final commit failpoint passed.'; fi
jq -e '.phaseStatus == "passed" and (.images | length) == 3 and (.targets | length) == 3' "$TEMP_DIR/output/images-archive-state/images-ledger.json" >/dev/null
jq -e '.phaseStatus == "running"' "$TEMP_DIR/output/images-archive-state/images-manifest.json" >/dev/null
jq -e '.phaseStatus == "passed"' "$TEMP_DIR/output/images-archive-evidence/images.json" >/dev/null
run_deploy_mode images images-archive isolated > "$TEMP_DIR/images-archive.out"
archive_ledger="$TEMP_DIR/output/images-archive-state/images-ledger.json"
jq -e '.phaseStatus == "passed" and .distributionMode == "archive" and (.images | length) == 3 and (.targets | length) == 3 and
  all(.images[]; .archivePath != null and (.archiveSha256 | test("^[0-9a-f]{64}$")) and (.configDigest | test("^sha256:[0-9a-f]{64}$")))' "$archive_ledger" >/dev/null
jq -e --slurp '.[0] == .[1] and
  (.[0] | {schemaVersion,runId,mode,inventorySha256,sourceRevision,sourceTree,builder,distributionMode,phaseStatus,startedAt,updatedAt,completedAt,
    images:[.images[] | del(.archivePath)],targets}) == .[2]' \
  "$archive_ledger" "$TEMP_DIR/output/images-archive-state/images-manifest.json" "$TEMP_DIR/output/images-archive-evidence/images.json" >/dev/null
while IFS= read -r archive_entry; do
    archive_path="$(jq -r '.archivePath' <<< "$archive_entry")"
    test -f "$archive_path"; test "$(stat -c %a "$archive_path")" = 600
    test "$(sha256sum "$archive_path" | cut -d' ' -f1)" = "$(jq -r '.archiveSha256' <<< "$archive_entry")"
done < <(jq -c '.images[]' "$archive_ledger")
grep -Fq $'docker\t--context\teast-context\timage\tload\t--input' "$LOG"
if grep -Eq $'docker\tbuildx\tbuild.*--push' "$LOG"; then fail 'Archive mode pushed to a registry.'; fi

write_prepare_case images-source-drift isolated
source_drift_root="$TEMP_DIR/artifacts/images-source-drift"
jq --arg root "$source_drift_root" '.images.distributionMode="archive" | .images.registryImmutableTags=false | .images.artifactRoot=$root' "$INVENTORY" > "$INVENTORY.changed"
mv "$INVENTORY.changed" "$INVENTORY"
run_prepare_preflight images-source-drift isolated
run_deploy_mode prepare images-source-drift isolated >/dev/null
: > "$FAKE_IMAGE_STATE/git-status-count"
if FAKE_SOURCE_DIRTY_AT_STATUS=4 run_deploy_mode images images-source-drift isolated > "$TEMP_DIR/images-failure.log" 2>&1; then fail 'Source change between component builds passed.'; fi
grep -Fq 'source-changed-or-dirty' "$TEMP_DIR/images-failure.log"
jq -e '.phaseStatus == "failed" and (.images | length) == 1' "$TEMP_DIR/output/images-source-drift-state/images-ledger.json" >/dev/null
run_deploy_mode images images-source-drift isolated >/dev/null
if grep -Fq 'fixture-secret-never-print' "$TEMP_DIR/images-failure.log" ||
   grep -R -Fq 'fixture-secret-never-print' "$TEMP_DIR/output"/*-evidence/images.json 2>/dev/null; then
    fail 'Image failure or evidence exposed a secret.'
fi
cp "$BASE_INVENTORY" "$INVENTORY"

write_prepare_case no-preflight isolated
expect_prepare_failure 'preflight-required' prepare-without-preflight isolated

write_prepare_case isolated-unmarked isolated
mkdir -m 700 "$(inventory_runtime_root logic)"
run_prepare_preflight isolated-unmarked isolated
expect_prepare_failure 'unmarked-root' isolated-unmarked isolated

write_prepare_case persistent-empty persistent
persistent_empty_root="$(inventory_runtime_root logic)"
mkdir -m 700 "$persistent_empty_root"
run_prepare_preflight persistent-empty persistent
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-empty persistent >/dev/null
printf 'preserved\n' > "$persistent_empty_root/application-state"
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-empty persistent >/dev/null
test "$(<"$persistent_empty_root/application-state")" = preserved

write_prepare_case persistent-nonempty persistent
persistent_nonempty_root="$(inventory_runtime_root logic)"
mkdir -m 700 "$persistent_nonempty_root"
printf 'existing\n' > "$persistent_nonempty_root/application-state"
run_prepare_preflight persistent-nonempty persistent
expect_prepare_failure 'nonempty-unmarked-root' persistent-nonempty persistent
test "$(<"$persistent_nonempty_root/application-state")" = existing

write_prepare_case unsafe-symlink isolated
run_prepare_preflight unsafe-symlink isolated
mkdir -m 700 "$TEMP_DIR/remote/unsafe-symlink-target"
unsafe_symlink_root="$(inventory_runtime_root east)"
ln -s "$TEMP_DIR/remote/unsafe-symlink-target" "$unsafe_symlink_root"
expect_prepare_failure 'unsafe-path' unsafe-symlink isolated
rm "$unsafe_symlink_root"

write_prepare_case unsafe-mode isolated
run_prepare_preflight unsafe-mode isolated
FAKE_PREPARE_UNSAFE_MODE_HOST=east expect_prepare_failure 'unsafe-path' unsafe-mode isolated
write_prepare_case unsafe-owner isolated
run_prepare_preflight unsafe-owner isolated
FAKE_PREPARE_FOREIGN_OWNER_HOST=east expect_prepare_failure 'unsafe-owner' unsafe-owner isolated
write_prepare_case root-parent isolated
run_prepare_preflight root-parent isolated
FAKE_PREPARE_ROOT_PARENT_HOST=east expect_prepare_failure 'owner-controlled-parent-required' root-parent isolated
write_prepare_case changed-identity isolated
run_prepare_preflight changed-identity isolated
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' changed-identity isolated

# Failure on later targets publishes exact successful progress; resume skips completed targets.
write_prepare_case fail-second isolated
run_prepare_preflight fail-second isolated
logic_prepare_root="$(inventory_runtime_root logic)"
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=east expect_prepare_failure 'identity-mismatch' fail-second isolated
jq -e --slurp 'all(.[]; .phaseStatus == "failed") and
  all(.[]; [.targets[].target] == ["logic"])' \
  "$TEMP_DIR/output/fail-second-state/prepare-ledger.json" "$TEMP_DIR/output/fail-second-state/prepare-manifest.json" \
  "$TEMP_DIR/output/fail-second-evidence/prepare.json" >/dev/null || {
    jq -s '{phases:map(.phaseStatus),targets:map([.targets[].target])}' "$TEMP_DIR/output/fail-second-state/prepare-ledger.json" \
      "$TEMP_DIR/output/fail-second-state/prepare-manifest.json" "$TEMP_DIR/output/fail-second-evidence/prepare.json" >&2
    fail 'Second-target failure progress was inconsistent.'
  }
logic_calls_before_resume="$(grep -F "$logic_prepare_root" "$LOG" | grep -c 'logic@example')"
logic_marker_before_resume="$(sha256sum "$logic_prepare_root/.hvo-deploy/ownership")"
logic_metadata_before_resume="$(stat -c '%u:%a' "$logic_prepare_root" "$logic_prepare_root/.hvo-deploy" "$logic_prepare_root/.hvo-deploy/ownership")"
run_deploy_mode prepare fail-second isolated >/dev/null
logic_calls_after_resume="$(grep -F "$logic_prepare_root" "$LOG" | grep -c 'logic@example')"
(( logic_calls_after_resume == logic_calls_before_resume + 1 )) || fail 'Resume did not validate completed first target exactly once.'
grep -F "$logic_prepare_root" "$LOG" | grep -Fq "'validate'"
[[ "$(sha256sum "$logic_prepare_root/.hvo-deploy/ownership")" == "$logic_marker_before_resume" ]] || fail 'Completed-target validation changed marker.'
[[ "$(stat -c '%u:%a' "$logic_prepare_root" "$logic_prepare_root/.hvo-deploy" "$logic_prepare_root/.hvo-deploy/ownership")" == "$logic_metadata_before_resume" ]] || fail 'Completed-target validation changed ownership or modes.'
jq -e '.phaseStatus == "passed" and (.targets | length) == 3' "$TEMP_DIR/output/fail-second-state/prepare-ledger.json" >/dev/null

write_prepare_case fail-third isolated
run_prepare_preflight fail-third isolated
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=west expect_prepare_failure 'identity-mismatch' fail-third isolated
jq -e --slurp 'all(.[]; .phaseStatus == "failed") and
  all(.[]; [.targets[].target] == ["logic","east"])' \
  "$TEMP_DIR/output/fail-third-state/prepare-ledger.json" "$TEMP_DIR/output/fail-third-state/prepare-manifest.json" \
  "$TEMP_DIR/output/fail-third-evidence/prepare.json" >/dev/null
run_deploy_mode prepare fail-third isolated >/dev/null

# Completed-target validation detects drift before continuing later targets.
create_partial_prepare_case drift-deleted-root
rm -rf "$(inventory_runtime_root logic)"
expect_prepare_failure 'completed-drift' drift-deleted-root isolated

create_partial_prepare_case drift-marker
printf 'altered\n' > "$(inventory_runtime_root logic)/.hvo-deploy/ownership"
expect_prepare_failure 'completed-drift' drift-marker isolated

create_partial_prepare_case drift-mode
chmod 755 "$(inventory_runtime_root logic)"
expect_prepare_failure 'completed-drift' drift-mode isolated

create_partial_prepare_case drift-owner
FAKE_PREPARE_FOREIGN_OWNER_HOST=logic expect_prepare_failure 'unsafe-owner' drift-owner isolated

create_partial_prepare_case drift-host
FAKE_PREPARE_IDENTITY_MISMATCH_HOST=logic expect_prepare_failure 'identity-mismatch' drift-host isolated

# A root-creation interruption leaves exact lock metadata that permits recovery and enforces contention.
write_prepare_case prepare-root-fail isolated
run_prepare_preflight prepare-root-fail isolated
if DEPLOY_TEST_FAILPOINT=after-root-creation run_deploy_mode prepare prepare-root-fail isolated > "$TEMP_DIR/prepare-failure.log" 2>&1; then fail 'Root failpoint passed.'; fi
prepare_root_fail_root="$(inventory_runtime_root logic)"
lock_path=("${prepare_root_fail_root%/*}"/.hvo-deploy-prepare-*.lock)
test "${#lock_path[@]}" = 1
exec 88<>"${lock_path[0]}"; flock -n 88
expect_prepare_failure 'lock-contended' prepare-root-fail isolated
flock -u 88; exec 88>&-
run_deploy_mode prepare prepare-root-fail isolated >/dev/null

for prepare_failpoint in after-marker-creation after-ledger-publication after-prepare-evidence; do
    prepare_case="prepare-$prepare_failpoint"
    write_prepare_case "$prepare_case" isolated
    run_prepare_preflight "$prepare_case" isolated
    if DEPLOY_TEST_FAILPOINT="$prepare_failpoint" run_deploy_mode prepare "$prepare_case" isolated > "$TEMP_DIR/prepare-failure.log" 2>&1; then
        fail "Prepare failpoint passed: $prepare_failpoint"
    fi
    run_deploy_mode prepare "$prepare_case" isolated >/dev/null
    jq -e '.phaseStatus == "passed"' "$TEMP_DIR/output/$prepare_case-state/prepare-manifest.json" >/dev/null
    jq -e '.phaseStatus == "passed"' "$TEMP_DIR/output/$prepare_case-state/prepare-ledger.json" >/dev/null
    jq -e '.phaseStatus == "passed"' "$TEMP_DIR/output/$prepare_case-evidence/prepare.json" >/dev/null
done

# Persistent failpoint recovery retains pre-mutation creation provenance.
write_prepare_case persistent-root-provenance persistent
run_prepare_preflight persistent-root-provenance persistent
if DEPLOY_TEST_FAILPOINT=after-root-creation FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-root-provenance persistent > "$TEMP_DIR/prepare-failure.log" 2>&1; then
    fail 'Persistent root failpoint passed.'
fi
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-root-provenance persistent >/dev/null
jq -e 'all(.targets[]; .newlyCreated.root == true and .newlyCreated.controlDirectory == true and .newlyCreated.marker == true)' \
  "$TEMP_DIR/output/persistent-root-provenance-state/prepare-ledger.json" >/dev/null

write_prepare_case persistent-empty-provenance persistent
mkdir -m 700 "$(inventory_runtime_root logic)"
run_prepare_preflight persistent-empty-provenance persistent
if DEPLOY_TEST_FAILPOINT=after-marker-creation FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-empty-provenance persistent > "$TEMP_DIR/prepare-failure.log" 2>&1; then
    fail 'Persistent marker failpoint passed.'
fi
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-empty-provenance persistent >/dev/null
jq -e '.targets[] | select(.target == "logic") |
  .newlyCreated.root == false and .newlyCreated.controlDirectory == true and .newlyCreated.marker == true' \
  "$TEMP_DIR/output/persistent-empty-provenance-state/prepare-ledger.json" >/dev/null

# An accepted idempotent rerun commits running before clearing stale prepare evidence and recovers interruption.
cp "$TEMP_DIR/prepare-happy.inventory" "$INVENTORY"
cp "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" "$TEMP_DIR/prepare-happy.prior-evidence"
if DEPLOY_TEST_FAILPOINT=after-prepare-running-manifest run_deploy_mode prepare prepare-happy isolated > "$TEMP_DIR/prepare-failure.log" 2>&1; then
    fail 'Prepare running-manifest failpoint passed.'
fi
jq -e '.phaseStatus == "running"' "$TEMP_DIR/output/prepare-happy-state/prepare-manifest.json" >/dev/null
cmp -s "$TEMP_DIR/prepare-happy.prior-evidence" "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" || fail 'Interrupted accepted rerun changed stale evidence before clear.'
run_deploy_mode prepare prepare-happy isolated >/dev/null
jq -e '.phaseStatus == "passed"' "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" >/dev/null
cp "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" "$TEMP_DIR/prepare-happy.accepted-evidence"
jq '.installationId="rejected-installation"' "$INVENTORY" > "$INVENTORY.changed" && mv "$INVENTORY.changed" "$INVENTORY"
expect_prepare_failure 'resume-contract-mismatch' prepare-happy isolated
cmp -s "$TEMP_DIR/prepare-happy.accepted-evidence" "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" || fail 'Rejected prepare validation changed prior prepare evidence.'
cp "$TEMP_DIR/prepare-happy.inventory" "$INVENTORY"

# Corrupt, duplicate, and inventory-mismatched ledgers are rejected before prepare artifacts change.
cp "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json" "$TEMP_DIR/prepare-happy.valid-ledger"
cp "$TEMP_DIR/output/prepare-happy-state/prepare-manifest.json" "$TEMP_DIR/prepare-happy.valid-manifest"
cp "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" "$TEMP_DIR/prepare-happy.valid-prepare-evidence"
printf 'not-json\n' > "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"
expect_prepare_failure 'ledger' prepare-happy isolated
cmp -s "$TEMP_DIR/prepare-happy.valid-manifest" "$TEMP_DIR/output/prepare-happy-state/prepare-manifest.json" || fail 'Corrupt ledger invocation changed prepare manifest.'
cmp -s "$TEMP_DIR/prepare-happy.valid-prepare-evidence" "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" || fail 'Corrupt ledger invocation changed prepare evidence.'
cp "$TEMP_DIR/prepare-happy.valid-ledger" "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"
jq '.targets += [.targets[0]]' "$TEMP_DIR/prepare-happy.valid-ledger" > "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"
expect_prepare_failure 'duplicate-target' prepare-happy isolated
cmp -s "$TEMP_DIR/prepare-happy.valid-manifest" "$TEMP_DIR/output/prepare-happy-state/prepare-manifest.json" || fail 'Duplicate ledger invocation changed prepare manifest.'
cp "$TEMP_DIR/prepare-happy.valid-ledger" "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"
jq '.targets[0].runtimeRoot="/mismatched/root"' "$TEMP_DIR/prepare-happy.valid-ledger" > "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"
expect_prepare_failure 'target-mismatch' prepare-happy isolated
cmp -s "$TEMP_DIR/prepare-happy.valid-prepare-evidence" "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" || fail 'Mismatched ledger invocation changed prepare evidence.'
cp "$TEMP_DIR/prepare-happy.valid-ledger" "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"
jq '.targets[0].newlyCreated.controlDirectory = false' "$TEMP_DIR/prepare-happy.valid-ledger" > "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"
expect_prepare_failure 'provenance-mismatch' prepare-happy isolated
cmp -s "$TEMP_DIR/prepare-happy.valid-manifest" "$TEMP_DIR/output/prepare-happy-state/prepare-manifest.json" || fail 'Provenance mismatch changed prepare manifest.'
cmp -s "$TEMP_DIR/prepare-happy.valid-prepare-evidence" "$TEMP_DIR/output/prepare-happy-evidence/prepare.json" || fail 'Provenance mismatch changed prepare evidence.'
cp "$TEMP_DIR/prepare-happy.valid-ledger" "$TEMP_DIR/output/prepare-happy-state/prepare-ledger.json"

# Completed validation never initializes or repairs an empty lock.
write_prepare_case empty-lock isolated
run_prepare_preflight empty-lock isolated
run_deploy_mode prepare empty-lock isolated >/dev/null
empty_lock_root="$(inventory_runtime_root logic)"
empty_lock_path=("${empty_lock_root%/*}"/.hvo-deploy-prepare-*.lock)
test "${#empty_lock_path[@]}" = 1
cp "$TEMP_DIR/output/empty-lock-state/prepare-manifest.json" "$TEMP_DIR/empty-lock.manifest"
cp "$TEMP_DIR/output/empty-lock-evidence/prepare.json" "$TEMP_DIR/empty-lock.evidence"
: > "${empty_lock_path[0]}"
expect_prepare_failure 'completed-drift' empty-lock isolated
test ! -s "${empty_lock_path[0]}" || fail 'Completed validation repaired an empty lock.'
cmp -s "$TEMP_DIR/empty-lock.manifest" "$TEMP_DIR/output/empty-lock-state/prepare-manifest.json" || fail 'Empty-lock validation changed prepare manifest.'
cmp -s "$TEMP_DIR/empty-lock.evidence" "$TEMP_DIR/output/empty-lock-evidence/prepare.json" || fail 'Empty-lock validation changed prepare evidence.'

# Persistent ownership cannot be adopted by another installation identity.
write_prepare_case persistent-same-install persistent
run_prepare_preflight persistent-same-a persistent
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-same-a persistent >/dev/null
run_prepare_preflight persistent-same-b persistent
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-same-b persistent >/dev/null
jq -e 'all(.targets[]; .creatingRunId == "persistent-same-a" and .disposition == "preexisting-or-resumed" and
  .newlyCreated.root == false and .newlyCreated.controlDirectory == false and .newlyCreated.marker == false)' \
  "$TEMP_DIR/output/persistent-same-b-state/prepare-ledger.json" >/dev/null

write_prepare_case persistent-cross persistent
run_prepare_preflight persistent-cross-a persistent
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare persistent-cross-a persistent >/dev/null
jq '.installationId="other-installation"' "$INVENTORY" > "$INVENTORY.changed" && mv "$INVENTORY.changed" "$INVENTORY"
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode preflight persistent-cross-b persistent >/dev/null
expect_prepare_failure 'lock-content-mismatch' persistent-cross-b persistent

write_prepare_case isolated-cross isolated
run_prepare_preflight isolated-cross-a isolated
run_deploy_mode prepare isolated-cross-a isolated >/dev/null
run_prepare_preflight isolated-cross-b isolated
expect_prepare_failure 'lock-content-mismatch' isolated-cross-b isolated

if grep -Eq '(^|[[:space:]])(rm|rmdir|docker|compose|curl|timeout)([[:space:]]|$)' "$PREPARE_REMOTE_LOG"; then
    fail 'Prepare remote script contains deletion or unrelated mutation commands.'
fi
if grep -Fq 'fixture-secret-never-print' "$PREPARE_REMOTE_LOG" || grep -R -Fq 'fixture-secret-never-print' "$TEMP_DIR/output"; then
    fail 'Prepare logs or state exposed a fixture secret.'
fi
if grep -R -Eq 'logic-machine|east-machine|west-machine' "$TEMP_DIR/output"/*-evidence/prepare.json 2>/dev/null; then
    fail 'Sanitized prepare evidence exposed machine identity.'
fi
