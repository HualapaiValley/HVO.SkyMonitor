#!/usr/bin/env bash
# Deployment contract shard body: existing-catalog-up. Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

# This body is sourced into the harness shell after scripts/lib/deploy-test-lifecycle.sh,
# whose staging functions (set_lifecycle_paths, prepare_lifecycle_fixture) assign the
# globals it reads: catalog_sha, lifecycle_east_root, lifecycle_logic_root, lifecycle_west_root. ShellCheck cannot see
# across that source boundary, so SC2154 is suppressed for this file with that fact recorded.
# shellcheck disable=SC2154
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

  # Catalog and up execute against fake remote hosts without trusting prior phase state.
  if grep -Eq 'build-hyg-v42|bundle-hyg-v42' "$REPO_ROOT/scripts/deploy/catalog.sh"; then
    fail 'Catalog deployment invokes a production catalog builder.'
  fi
  stage_lifecycle_images

# Catalog transfer never writes into the former deterministic stage. Every historical destination may be hostile
# without redirecting script or bundle bytes, and a hostile crash remnant cannot block a fresh transaction.
set_lifecycle_paths
catalog_transport_external="$TEMP_DIR/catalog-transport-external"
mkdir -m 700 "$catalog_transport_external"
for target_root in "$lifecycle_logic_root" "$lifecycle_east_root" "$lifecycle_west_root"; do
  stale_stage="$target_root/.hvo-deploy/catalog-$deploy_case-test-fixture"
  make_private_tree "$stale_stage/bundle" "$stale_stage/scripts/catalog" "$stale_stage/scripts/infra"
  for relative in bundle/manifest.json bundle/hyg_v42.sqlite bundle/LICENSE-HYG.md bundle/ATTRIBUTION-HYG.md \
    scripts/catalog/catalog-common.sh scripts/catalog/install-hyg-v42.sh scripts/infra:operation-lock; do
    external_name="$(printf '%s/%s-%s' "$catalog_transport_external" "${target_root##*/}" "${relative//\//-}")"
    printf 'external-preserved:%s\n' "$relative" > "$external_name"
    ln -s "$external_name" "$stale_stage/$relative"
  done
  hostile_remnant="$target_root/.hvo-deploy/.catalog-transaction-$deploy_case-test-fixture.123.456.789"
  mkdir -m 700 "$hostile_remnant"
  printf 'remnant-external-preserved\n' > "$catalog_transport_external/${target_root##*/}-remnant"
  ln "$catalog_transport_external/${target_root##*/}-remnant" "$hostile_remnant/manifest.json"
done
run_deploy_mode catalog "$deploy_case" isolated >/dev/null
while IFS= read -r external_file; do
  [[ "$(<"$external_file")" == external-preserved:* || "$(<"$external_file")" == remnant-external-preserved ]] ||
    fail "Catalog transfer overwrote external file: $external_file"
done < <(find "$catalog_transport_external" -type f -print)
for target_root in "$lifecycle_logic_root" "$lifecycle_east_root" "$lifecycle_west_root"; do
  stale_stage="$target_root/.hvo-deploy/catalog-$deploy_case-test-fixture"
  test -L "$stale_stage/bundle/manifest.json"
  hostile_remnant="$target_root/.hvo-deploy/.catalog-transaction-$deploy_case-test-fixture.123.456.789"
  test -f "$hostile_remnant/manifest.json"
  rm -rf -- "$stale_stage" "$hostile_remnant"
done
chmod -R u+w "$install_root" 2>/dev/null || true
rm -rf -- "$install_root"
rm -f "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" "$TEMP_DIR/output/$deploy_case-state/catalog-manifest.json" \
  "$TEMP_DIR/output/$deploy_case-evidence/catalog.json"

# Fixture manifest identity and shape mismatches fail before SSH, SCP, or installation.
cp "$bundle/manifest.json" "$TEMP_DIR/fixture-manifest.valid"
for fixture_mismatch in id manifest-v1 manifest-unknown schema preprocessing kind version sha length rows shape; do
    case "$fixture_mismatch" in
      id) jq '.catalog.id="arbitrary-alias"' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      manifest-v1) jq '.manifestVersion=1 | del(.catalog.id)' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      manifest-unknown) jq '.manifestVersion=3' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      schema) jq '.schemaVersion="different"' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      preprocessing) jq '.preprocessingVersion="different"' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      kind) jq '.package.kind="production"' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      version) jq '.package.version="other-fixture"' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      sha) jq '.database.sha256=("0"*64)' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      length) jq '.database.length += 1' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      rows) jq '.database.rowCount += 1' "$TEMP_DIR/fixture-manifest.valid" > "$bundle/manifest.json" ;;
      shape) cp "$TEMP_DIR/fixture-manifest.valid" "$bundle/manifest.json"; printf 'unexpected\n' > "$bundle/unexpected.txt" ;;
    esac
    : > "$LOG"
    if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-mismatch.log" 2>&1; then fail "Fixture catalog $fixture_mismatch mismatch passed."; fi
    test ! -s "$LOG" || fail "Fixture catalog $fixture_mismatch mismatch reached transport."
    rm -f "$bundle/unexpected.txt"
done
cp "$TEMP_DIR/fixture-manifest.valid" "$bundle/manifest.json"

# A production-shaped bundle with inventory-mismatched identity is rejected before transport or the production installer.
production_mismatch_bundle="$TEMP_DIR/production-mismatch-bundle"
mkdir -m 700 "$production_mismatch_bundle"
cp "$bundle/hyg_v42.sqlite" "$production_mismatch_bundle/hyg_v42.sqlite"
printf 'license\n' > "$production_mismatch_bundle/LICENSE-HYG.md"
printf 'attribution\n' > "$production_mismatch_bundle/ATTRIBUTION-HYG.md"
jq '.package.kind="production" | .package.version="wrong-production"' "$TEMP_DIR/fixture-manifest.valid" > "$production_mismatch_bundle/manifest.json"
jq --arg bundle "$production_mismatch_bundle" '.catalogs[0].kind="production" | .catalogs[0].version="expected-production" |
  .catalogs[0].bundlePath=$bundle' "$INVENTORY" > "$TEMP_DIR/production-mismatch-inventory.json"
: > "$LOG"
(
  # shellcheck source=scripts/deploy/common.sh
  source "$REPO_ROOT/scripts/deploy/common.sh"
  # shellcheck source=scripts/deploy/catalog.sh
  source "$REPO_ROOT/scripts/deploy/catalog.sh"
  if deploy_validate_local_catalog "$TEMP_DIR/production-mismatch-inventory.json" > "$TEMP_DIR/production-catalog-mismatch.log" 2>&1; then
      exit 91
  fi
)
test ! -s "$LOG" || fail 'Production catalog mismatch reached transport.'
catalog_parent="$(dirname "$install_root")"
catalog_alias="$catalog_parent/catalog-alias"
rm -rf "$install_root" "$catalog_alias"
catalog_parent_alias="$TEMP_DIR/catalog-parent-alias"; mkdir -m 700 "$catalog_parent_alias"
rmdir "$catalog_parent"; ln -s "$catalog_parent_alias" "$catalog_parent"
if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-parent-symlink.log" 2>&1; then
  fail 'Catalog accepted a symlinked fixture catalog-parent ancestor.'
fi
test ! -e "$catalog_parent_alias/test-fixture"
rm "$catalog_parent"; rmdir "$catalog_parent_alias"; mkdir -m 755 "$catalog_parent"
mkdir -m 700 "$catalog_alias"
ln -s "$catalog_alias" "$install_root"
if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-install-root-symlink.log" 2>&1; then
  fail 'Catalog accepted a symlinked fixture install root.'
fi
test ! -e "$catalog_alias/current"
rm "$install_root"; rmdir "$catalog_alias"
mkdir -m 700 "$install_root" "$catalog_alias"
ln -s "$catalog_alias" "$install_root/versions"
if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-versions-symlink.log" 2>&1; then
  fail 'Catalog accepted a symlinked fixture versions directory.'
fi
test ! -e "$catalog_alias/fixture-v1"
rm "$install_root/versions"; rmdir "$catalog_alias"
mkdir -m 700 "$install_root/versions"
printf 'hostile\n' > "$install_root/current"
if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-hostile-current.log" 2>&1; then
  fail 'Catalog accepted a non-symlink fixture current pointer.'
fi
test "$(<"$install_root/current")" = hostile
rm "$install_root/current"
for unsafe_catalog_parent_mode in 770 777; do
  chmod "$unsafe_catalog_parent_mode" "$catalog_parent"
  if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-writable-parent-$unsafe_catalog_parent_mode.log" 2>&1; then
    fail "Catalog accepted unsafe fixture parent mode $unsafe_catalog_parent_mode."
  fi
  test ! -e "$install_root/current"
done
chmod 755 "$catalog_parent"
rm -rf "$install_root"

for hostile_fixture_lock in symlink hardlink mode owner type; do
  rm -f "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" "$TEMP_DIR/output/$deploy_case-state/catalog-manifest.json" \
    "$TEMP_DIR/output/$deploy_case-evidence/catalog.json"
  rm -rf "$install_root"; mkdir -m 700 "$install_root"
  fixture_lock="$install_root/.catalog.lock"
  case "$hostile_fixture_lock" in
    symlink) printf 'preserve\n' > "$TEMP_DIR/fixture-lock-target"; ln -s "$TEMP_DIR/fixture-lock-target" "$fixture_lock" ;;
    hardlink) printf 'lock\n' > "$TEMP_DIR/fixture-lock-hardlink"; chmod 600 "$TEMP_DIR/fixture-lock-hardlink"; ln "$TEMP_DIR/fixture-lock-hardlink" "$fixture_lock" ;;
    mode) printf 'lock\n' > "$fixture_lock"; chmod 640 "$fixture_lock" ;;
    owner) printf 'lock\n' > "$fixture_lock"; chmod 600 "$fixture_lock" ;;
    type) mkdir -m 700 "$fixture_lock" ;;
  esac
  if [[ "$hostile_fixture_lock" == owner ]]; then
    cp "$FAKE_REMOTE_UID_MAP" "$TEMP_DIR/fixture-lock-uid-map"
    printf '%s\t99999\n' "$fixture_lock" >> "$FAKE_REMOTE_UID_MAP"
    if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-hostile-lock-$hostile_fixture_lock.log" 2>&1; then
      mv "$TEMP_DIR/fixture-lock-uid-map" "$FAKE_REMOTE_UID_MAP"
      fail "Catalog accepted hostile fixture install lock: $hostile_fixture_lock"
    fi
    mv "$TEMP_DIR/fixture-lock-uid-map" "$FAKE_REMOTE_UID_MAP"
  elif run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-hostile-lock-$hostile_fixture_lock.log" 2>&1; then
    fail "Catalog accepted hostile fixture install lock: $hostile_fixture_lock"
  fi
  test ! -e "$install_root/current" && test ! -e "$install_root/.fixture-pointer-transaction"
  [[ "$hostile_fixture_lock" != symlink ]] || test "$(<"$TEMP_DIR/fixture-lock-target")" = preserve
  rm -rf "$fixture_lock"; rm -f "$TEMP_DIR/fixture-lock-target" "$TEMP_DIR/fixture-lock-hardlink"
done
rm -rf "$install_root"

# Transaction temporaries are removed only when their complete locked recovery contract is authenticated.
for hostile_transaction_temporary in symlink hardlink mode unknown; do
  rm -f "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" "$TEMP_DIR/output/$deploy_case-state/catalog-manifest.json" \
    "$TEMP_DIR/output/$deploy_case-evidence/catalog.json"
  rm -rf "$install_root"; mkdir -m 700 "$install_root"
  transaction_temporary="$install_root/.fixture-candidate-transaction.tmp.123.456"
  case "$hostile_transaction_temporary" in
    symlink)
      printf 'preserve\n' > "$TEMP_DIR/fixture-transaction-temp-target"
      ln -s "$TEMP_DIR/fixture-transaction-temp-target" "$transaction_temporary"
      ;;
    hardlink)
      printf 'preserve\n' > "$TEMP_DIR/fixture-transaction-temp-hardlink"
      chmod 600 "$TEMP_DIR/fixture-transaction-temp-hardlink"
      ln "$TEMP_DIR/fixture-transaction-temp-hardlink" "$transaction_temporary"
      ;;
    mode)
      printf 'preserve\n' > "$transaction_temporary"
      chmod 640 "$transaction_temporary"
      ;;
    unknown)
      transaction_temporary="$install_root/.fixture-candidate-transaction.tmp.unexpected"
      printf 'preserve\n' > "$transaction_temporary"
      chmod 600 "$transaction_temporary"
      ;;
  esac
  if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-hostile-transaction-temp-$hostile_transaction_temporary.log" 2>&1; then
    fail "Catalog accepted hostile fixture transaction temporary: $hostile_transaction_temporary"
  fi
  test -e "$transaction_temporary" || test -L "$transaction_temporary" ||
    fail "Catalog removed hostile fixture transaction temporary: $hostile_transaction_temporary"
  [[ "$hostile_transaction_temporary" != symlink ]] ||
    test "$(<"$TEMP_DIR/fixture-transaction-temp-target")" = preserve
  rm -f "$transaction_temporary" "$TEMP_DIR/fixture-transaction-temp-target" \
    "$TEMP_DIR/fixture-transaction-temp-hardlink"
done
rm -rf "$install_root"

# Orphan current-pointer temporaries are removed only when their complete locked symlink contract is authenticated.
for hostile_current_temporary in regular hardlink owner malformed unsafe-target; do
  rm -f "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" "$TEMP_DIR/output/$deploy_case-state/catalog-manifest.json" \
    "$TEMP_DIR/output/$deploy_case-evidence/catalog.json"
  rm -rf "$install_root"; mkdir -m 700 "$install_root" "$install_root/versions"
  mkdir -m 555 "$install_root/versions/fixture-v1"
  current_temporary="$install_root/.current.tmp.123.456"
  case "$hostile_current_temporary" in
    regular) printf 'preserve\n' > "$current_temporary" ;;
    hardlink)
      printf 'preserve\n' > "$TEMP_DIR/fixture-current-temp-hardlink"
      ln "$TEMP_DIR/fixture-current-temp-hardlink" "$current_temporary"
      ;;
    owner)
      ln -s versions/fixture-v1 "$current_temporary"
      cp "$FAKE_REMOTE_UID_MAP" "$TEMP_DIR/fixture-current-temp-uid-map"
      printf '%s\t99999\n' "$current_temporary" >> "$FAKE_REMOTE_UID_MAP"
      ;;
    malformed)
      current_temporary="$install_root/.current.tmp.unexpected"
      ln -s versions/fixture-v1 "$current_temporary"
      ;;
    unsafe-target) ln -s ../outside "$current_temporary" ;;
  esac
  if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-hostile-current-temp-$hostile_current_temporary.log" 2>&1; then
    [[ "$hostile_current_temporary" != owner ]] || mv "$TEMP_DIR/fixture-current-temp-uid-map" "$FAKE_REMOTE_UID_MAP"
    fail "Catalog accepted hostile fixture current-pointer temporary: $hostile_current_temporary"
  fi
  [[ "$hostile_current_temporary" != owner ]] || mv "$TEMP_DIR/fixture-current-temp-uid-map" "$FAKE_REMOTE_UID_MAP"
  test -e "$current_temporary" || test -L "$current_temporary" ||
    fail "Catalog removed hostile fixture current-pointer temporary: $hostile_current_temporary"
  rm -f "$current_temporary" "$TEMP_DIR/fixture-current-temp-hardlink"
done
rm -rf "$install_root"

# Unauthenticated or structurally hostile candidate stages are retained for inspection and never adopted or deleted.
for hostile_candidate in no-intent hostile-intent; do
  rm -f "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" "$TEMP_DIR/output/$deploy_case-state/catalog-manifest.json" \
    "$TEMP_DIR/output/$deploy_case-evidence/catalog.json"
  rm -rf "$install_root"; mkdir -m 700 "$install_root" "$install_root/versions" "$install_root/versions/.fixture-candidate-fixture-v1.stage"
  printf 'preserve\n' > "$install_root/versions/.fixture-candidate-fixture-v1.stage/unexpected"
  if [[ "$hostile_candidate" == hostile-intent ]]; then
    printf 'versions/fixture-v1\ntest-fixture\n%s\n%s\n' \
      "$(sha256sum "$bundle/manifest.json" | cut -d' ' -f1)" "$catalog_sha" > "$install_root/.fixture-candidate-transaction"
    chmod 600 "$install_root/.fixture-candidate-transaction"
  fi
  if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-hostile-candidate-$hostile_candidate.log" 2>&1; then
    fail "Catalog accepted hostile fixture candidate state: $hostile_candidate"
  fi
  test "$(<"$install_root/versions/.fixture-candidate-fixture-v1.stage/unexpected")" = preserve
  test ! -e "$install_root/current"
done
rm -rf "$install_root"

install_fixture_test_version() {
  local target_root="$1" package_version="$2" destination
  destination="$target_root/versions/$package_version"
  make_private_tree "$destination"
  cp "$bundle/hyg_v42.sqlite" "$destination/hyg_v42.sqlite"
  jq --arg version "$package_version" '.package.version=$version' "$TEMP_DIR/fixture-manifest.valid" > "$destination/manifest.json"
  chmod 444 "$destination/manifest.json" "$destination/hyg_v42.sqlite"
  chmod 555 "$destination"
}

# A runtime-owned 0755 catalog parent is safe; activation preserves the old pointer before intent and reconciles on restart.
test "$(stat -c %a "$catalog_parent")" = 755
for fixture_boundary in after-candidate-transaction-temp-fsync during-candidate-copy before-candidate-rename \
  after-candidate-publication after-pointer-transaction-temp-fsync after-pointer-transaction \
  after-previous-pointer-temporary after-previous-pointer after-current-pointer-temporary after-current-pointer; do
  rm -f "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" "$TEMP_DIR/output/$deploy_case-state/catalog-manifest.json" \
    "$TEMP_DIR/output/$deploy_case-evidence/catalog.json"
  chmod -R u+w "$install_root" 2>/dev/null || true
  rm -rf "$install_root"; mkdir -m 700 "$install_root" "$install_root/versions"
  install_fixture_test_version "$install_root" prior
  ln -s versions/prior "$install_root/current"
  if HVO_FIXTURE_CATALOG_TEST_FAIL_AT="$fixture_boundary" run_deploy_mode catalog "$deploy_case" isolated \
    > "$TEMP_DIR/catalog-$fixture_boundary.log" 2>&1; then
    fail "Fixture activation failpoint $fixture_boundary passed."
  fi
  if [[ "$fixture_boundary" == after-current-pointer ]]; then
    test "$(readlink "$install_root/current")" = versions/fixture-v1
  else
    test "$(readlink "$install_root/current")" = versions/prior
  fi
  if [[ "$fixture_boundary" == after-current-pointer-temporary ]]; then
    test "$(find "$install_root" -maxdepth 1 -name '.current.tmp.*' -print | wc -l)" = 1
    if HVO_FIXTURE_CATALOG_TEST_FAIL_AT="$fixture_boundary" run_deploy_mode catalog "$deploy_case" isolated \
      > "$TEMP_DIR/catalog-$fixture_boundary-repeat.log" 2>&1; then
      fail "Repeated fixture activation failpoint $fixture_boundary passed."
    fi
    test "$(find "$install_root" -maxdepth 1 -name '.current.tmp.*' -print | wc -l)" = 1
    test "$(readlink "$install_root/current")" = versions/prior
  fi
  run_deploy_mode catalog "$deploy_case" isolated >/dev/null
  test "$(readlink "$install_root/current")" = versions/fixture-v1
  test "$(readlink "$install_root/previous")" = versions/prior
  test ! -e "$install_root/.fixture-pointer-transaction"; test ! -e "$install_root/.fixture-candidate-transaction"
  test -z "$(find "$install_root/versions" -maxdepth 1 -name '.fixture-candidate-*.stage' -print -quit)"
  test -z "$(find "$install_root" -maxdepth 1 \( -name '.fixture-candidate-transaction.tmp.*' -o -name '.fixture-pointer-transaction.tmp.*' \) -print -quit)"
  test -z "$(find "$install_root" -maxdepth 1 -name '.current.tmp.*' -print -quit)"
done

# Conditional fixture recovery propagates failed pointer publication and intent removal commands.
for fixture_command_failure in fixture-current-pointer-mv fixture-pointer-transaction-rm; do
  rm -f "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" "$TEMP_DIR/output/$deploy_case-state/catalog-manifest.json" \
    "$TEMP_DIR/output/$deploy_case-evidence/catalog.json"
  chmod -R u+w "$install_root" 2>/dev/null || true
  rm -rf "$install_root"; mkdir -m 700 "$install_root" "$install_root/versions"
  install_fixture_test_version "$install_root" prior
  ln -s versions/prior "$install_root/current"
  if HVO_CATALOG_TEST_MODE=true HVO_CATALOG_TEST_FAIL_COMMAND_AT="$fixture_command_failure" \
    run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-$fixture_command_failure.log" 2>&1; then
    fail "Fixture recovery accepted failed durable command: $fixture_command_failure"
  fi
  test -e "$install_root/.fixture-pointer-transaction"
  if [[ "$fixture_command_failure" == fixture-current-pointer-mv ]]; then
    test "$(readlink "$install_root/current")" = versions/prior
  else
    test "$(readlink "$install_root/current")" = versions/fixture-v1
  fi
  run_deploy_mode catalog "$deploy_case" isolated >/dev/null
  test "$(readlink "$install_root/current")" = versions/fixture-v1
  test ! -e "$install_root/.fixture-pointer-transaction"
done

make_fixture_transport_stage() {
  local stage_root="$1" package_version="$2"
  make_private_tree "$stage_root/bundle" "$stage_root/scripts/catalog"
  cp "$bundle/hyg_v42.sqlite" "$stage_root/bundle/hyg_v42.sqlite"
  jq --arg version "$package_version" '.package.version=$version' "$TEMP_DIR/fixture-manifest.valid" > "$stage_root/bundle/manifest.json"
  cp "$REPO_ROOT/scripts/catalog/catalog-common.sh" "$stage_root/scripts/catalog/catalog-common.sh"
}

run_fixture_transport_install() {
  local stage_root="$1" target_root="$2" package_version="$3" output="$4"
  local expected_sha="${5:-$catalog_sha}" expected_length="${6:-$catalog_length}" expected_rows="${7:-9}"
  (
    source "$REPO_ROOT/scripts/deploy/transport.sh"
    PATH="$BIN:$PATH" deploy_transport_catalog_install logic@example "$stage_root" "$target_root" test-fixture fixture \
      "$package_version" 2 3 "$expected_sha" "$expected_length" "$expected_rows" > "$output"
  )
}

# Authenticated remote stage cleanup rejects a same-UID directory swap after its final inode map.
transport_cleanup_parent="$TEMP_DIR/remote/catalog-cleanup-parent"
transport_cleanup_stage="$transport_cleanup_parent/.catalog-stage-cleanup-test-test-fixture.123.456.789"
transport_cleanup_saved="$transport_cleanup_stage.saved"
transport_cleanup_external="$TEMP_DIR/catalog-cleanup-external"
transport_cleanup_marker="$TEMP_DIR/catalog-cleanup-directory-swap.marker"
mkdir -m 700 "$transport_cleanup_parent"
make_private_tree "$transport_cleanup_stage/nested" "$transport_cleanup_external/nested"
printf 'stage\n' > "$transport_cleanup_stage/nested/payload"
printf 'external-preserved\n' > "$transport_cleanup_external/nested/payload"
chmod 0750 "$transport_cleanup_external" "$transport_cleanup_external/nested"
chmod 0440 "$transport_cleanup_external/nested/payload"
transport_cleanup_before="$(stat -c '%d:%i:%u:%g:%h:%a:%s' "$transport_cleanup_external" \
  "$transport_cleanup_external/nested" "$transport_cleanup_external/nested/payload")|$(sha256sum "$transport_cleanup_external/nested/payload" | cut -d' ' -f1)"
(
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  HVO_CATALOG_TEST_MODE=true HVO_CATALOG_TEST_LOCK_BARRIER=catalog-stage-cleanup-final \
    HVO_CATALOG_TEST_LOCK_MARKER="$transport_cleanup_marker" \
    PATH="$BIN:$PATH" deploy_transport_catalog_cleanup_stage logic@example "$transport_cleanup_stage" cleanup-test test-fixture
) >/dev/null 2>&1 &
FIXTURE_REPLACEMENT_PID=$!
while [[ ! -e "$transport_cleanup_marker" ]]; do sleep 0.01; kill -0 "$FIXTURE_REPLACEMENT_PID"; done
FIXTURE_REPLACEMENT_STOPPED_PID="$(<"$transport_cleanup_marker")"
mv -T -- "$transport_cleanup_stage" "$transport_cleanup_saved"
mv -T -- "$transport_cleanup_external" "$transport_cleanup_stage"
kill -CONT "$FIXTURE_REPLACEMENT_STOPPED_PID"
if wait "$FIXTURE_REPLACEMENT_PID"; then
  fail 'Remote catalog-stage cleanup accepted a same-UID directory swap.'
fi
FIXTURE_REPLACEMENT_PID=""; FIXTURE_REPLACEMENT_STOPPED_PID=""
transport_cleanup_after="$(stat -c '%d:%i:%u:%g:%h:%a:%s' "$transport_cleanup_stage" \
  "$transport_cleanup_stage/nested" "$transport_cleanup_stage/nested/payload")|$(sha256sum "$transport_cleanup_stage/nested/payload" | cut -d' ' -f1)"
test "$transport_cleanup_after" = "$transport_cleanup_before" || fail 'Remote catalog-stage cleanup mutated a swapped external tree.'
mv -T -- "$transport_cleanup_stage" "$transport_cleanup_external"
mv -T -- "$transport_cleanup_saved" "$transport_cleanup_stage"
(
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  PATH="$BIN:$PATH" deploy_transport_catalog_cleanup_stage logic@example "$transport_cleanup_stage" cleanup-test test-fixture
)
test ! -e "$transport_cleanup_stage"

# Crash-remnant cleanup also binds deletion to the mapped root and retains a substituted tree.
transport_remnant_parent="$TEMP_DIR/remote/catalog-remnant-parent"
transport_remnant="$transport_remnant_parent/.catalog-transaction-remnant-test-test-fixture.123.456.789"
transport_remnant_saved="$transport_remnant.saved"
transport_remnant_external="$TEMP_DIR/catalog-remnant-external"
transport_remnant_marker="$TEMP_DIR/catalog-remnant-directory-swap.marker"
mkdir -m 700 "$transport_remnant_parent"
make_private_tree "$transport_remnant/nested" "$transport_remnant_external/nested"
printf 'remnant\n' > "$transport_remnant/nested/payload"
printf 'remnant-external-preserved\n' > "$transport_remnant_external/nested/payload"
chmod 0750 "$transport_remnant_external" "$transport_remnant_external/nested"
chmod 0440 "$transport_remnant_external/nested/payload"
transport_remnant_before="$(stat -c '%d:%i:%u:%g:%h:%a:%s' "$transport_remnant_external" \
  "$transport_remnant_external/nested" "$transport_remnant_external/nested/payload")|$(sha256sum "$transport_remnant_external/nested/payload" | cut -d' ' -f1)"
(
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  HVO_CATALOG_TEST_MODE=true HVO_CATALOG_TEST_LOCK_BARRIER=catalog-remnant-cleanup-final \
    HVO_CATALOG_TEST_LOCK_MARKER="$transport_remnant_marker" \
    PATH="$BIN:$PATH" deploy_transport_catalog_create_stage logic@example "$transport_remnant_parent" remnant-test test-fixture
) > "$TEMP_DIR/catalog-remnant-created-stage.out" 2>/dev/null &
FIXTURE_REPLACEMENT_PID=$!
while [[ ! -e "$transport_remnant_marker" ]]; do sleep 0.01; kill -0 "$FIXTURE_REPLACEMENT_PID"; done
FIXTURE_REPLACEMENT_STOPPED_PID="$(<"$transport_remnant_marker")"
mv -T -- "$transport_remnant" "$transport_remnant_saved"
mv -T -- "$transport_remnant_external" "$transport_remnant"
kill -CONT "$FIXTURE_REPLACEMENT_STOPPED_PID"
wait "$FIXTURE_REPLACEMENT_PID"
FIXTURE_REPLACEMENT_PID=""; FIXTURE_REPLACEMENT_STOPPED_PID=""
test "$(stat -c '%d:%i:%u:%g:%h:%a:%s' "$transport_remnant" \
  "$transport_remnant/nested" "$transport_remnant/nested/payload")|$(sha256sum "$transport_remnant/nested/payload" | cut -d' ' -f1)" = \
  "$transport_remnant_before" || fail 'Remote catalog-remnant cleanup mutated a swapped external tree.'
mv -T -- "$transport_remnant" "$transport_remnant_external"
mv -T -- "$transport_remnant_saved" "$transport_remnant"
transport_created_stage="$(<"$TEMP_DIR/catalog-remnant-created-stage.out")"
(
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  PATH="$BIN:$PATH" deploy_transport_catalog_cleanup_stage logic@example "$transport_created_stage" remnant-test test-fixture
)
rm -rf -- "$transport_remnant"

# Fixture publication fails before candidate rename when the shared lock pathname stops naming the held descriptor.
replacement_fixture_root="$TEMP_DIR/remote/replaced-fixture-lock"
replacement_fixture_stage="$TEMP_DIR/replaced-fixture-stage"
replacement_fixture_marker="$TEMP_DIR/replaced-fixture-lock.marker"
make_fixture_transport_stage "$replacement_fixture_stage" fixture-replaced
HVO_CATALOG_TEST_LOCK_BARRIER=fixture-candidate-publication HVO_CATALOG_TEST_LOCK_MARKER="$replacement_fixture_marker" \
  run_fixture_transport_install "$replacement_fixture_stage" "$replacement_fixture_root" fixture-replaced \
  "$TEMP_DIR/replaced-fixture.out" &
FIXTURE_REPLACEMENT_PID=$!
while [[ ! -e "$replacement_fixture_marker" ]]; do sleep 0.01; kill -0 "$FIXTURE_REPLACEMENT_PID"; done
FIXTURE_REPLACEMENT_STOPPED_PID="$(<"$replacement_fixture_marker")"
mv -T -- "$replacement_fixture_root/.catalog.lock" "$replacement_fixture_root/.catalog.lock.original"
: > "$replacement_fixture_root/.catalog.lock"; chmod 600 "$replacement_fixture_root/.catalog.lock"
kill -CONT "$FIXTURE_REPLACEMENT_STOPPED_PID"
if wait "$FIXTURE_REPLACEMENT_PID"; then
  fail 'Fixture publisher accepted a replaced shared catalog lock.'
fi
FIXTURE_REPLACEMENT_PID=""; FIXTURE_REPLACEMENT_STOPPED_PID=""
test ! -e "$replacement_fixture_root/versions/fixture-replaced"
test -d "$replacement_fixture_root/versions/.fixture-candidate-fixture-replaced.stage"
test -f "$replacement_fixture_root/.fixture-candidate-transaction"
rm -f -- "$replacement_fixture_root/.catalog.lock"
mv -T -- "$replacement_fixture_root/.catalog.lock.original" "$replacement_fixture_root/.catalog.lock"
replacement_fixture_cleanup_marker="$TEMP_DIR/replaced-fixture-cleanup.marker"
HVO_CATALOG_TEST_LOCK_BARRIER=fixture-candidate-cleanup HVO_CATALOG_TEST_LOCK_MARKER="$replacement_fixture_cleanup_marker" \
  run_fixture_transport_install "$replacement_fixture_stage" "$replacement_fixture_root" fixture-replaced \
  "$TEMP_DIR/replaced-fixture-cleanup.out" &
FIXTURE_REPLACEMENT_PID=$!
while [[ ! -e "$replacement_fixture_cleanup_marker" ]]; do sleep 0.01; kill -0 "$FIXTURE_REPLACEMENT_PID"; done
FIXTURE_REPLACEMENT_STOPPED_PID="$(<"$replacement_fixture_cleanup_marker")"
mv -T -- "$replacement_fixture_root/.catalog.lock" "$replacement_fixture_root/.catalog.lock.original"
: > "$replacement_fixture_root/.catalog.lock"; chmod 600 "$replacement_fixture_root/.catalog.lock"
kill -CONT "$FIXTURE_REPLACEMENT_STOPPED_PID"
if wait "$FIXTURE_REPLACEMENT_PID"; then
  fail 'Conditional fixture candidate cleanup accepted a replaced shared catalog lock.'
fi
FIXTURE_REPLACEMENT_PID=""; FIXTURE_REPLACEMENT_STOPPED_PID=""
test -d "$replacement_fixture_root/versions/.fixture-candidate-fixture-replaced.stage"
test -f "$replacement_fixture_root/.fixture-candidate-transaction"
rm -f -- "$replacement_fixture_root/.catalog.lock"
mv -T -- "$replacement_fixture_root/.catalog.lock.original" "$replacement_fixture_root/.catalog.lock"
run_fixture_transport_install "$replacement_fixture_stage" "$replacement_fixture_root" fixture-replaced \
  "$TEMP_DIR/replaced-fixture-recovered.out"
test "$(readlink "$replacement_fixture_root/current")" = versions/fixture-replaced
test ! -e "$replacement_fixture_root/.fixture-candidate-transaction"

# Candidate substitution at the cleanup barrier is retained without touching the external target, then recovers.
substitution_fixture_root="$TEMP_DIR/remote/substituted-fixture-candidate"
substitution_fixture_stage="$TEMP_DIR/substituted-fixture-stage"
substitution_fixture_marker="$TEMP_DIR/substituted-fixture-candidate.marker"
substitution_fixture_external="$TEMP_DIR/substituted-fixture-external"
make_fixture_transport_stage "$substitution_fixture_stage" fixture-substituted
if HVO_FIXTURE_CATALOG_TEST_FAIL_AT=during-candidate-copy \
  run_fixture_transport_install "$substitution_fixture_stage" "$substitution_fixture_root" fixture-substituted \
  "$TEMP_DIR/substituted-fixture-initial.out"; then
  fail 'Fixture candidate setup failpoint unexpectedly completed.'
fi
substitution_fixture_candidate="$substitution_fixture_root/versions/.fixture-candidate-fixture-substituted.stage"
substitution_fixture_saved="$substitution_fixture_candidate.saved"
mkdir -m 0700 "$substitution_fixture_external"
printf 'external-manifest\n' > "$substitution_fixture_external/manifest.json"
printf 'external-database\n' > "$substitution_fixture_external/hyg_v42.sqlite"
chmod 0440 "$substitution_fixture_external/manifest.json" "$substitution_fixture_external/hyg_v42.sqlite"
chmod 0550 "$substitution_fixture_external"
substitution_fixture_before="$(stat -c '%d:%i:%u:%g:%h:%a:%s' "$substitution_fixture_external" \
  "$substitution_fixture_external/manifest.json" "$substitution_fixture_external/hyg_v42.sqlite")|$(sha256sum \
  "$substitution_fixture_external/manifest.json" "$substitution_fixture_external/hyg_v42.sqlite")"
HVO_CATALOG_TEST_LOCK_BARRIER=fixture-candidate-cleanup HVO_CATALOG_TEST_LOCK_MARKER="$substitution_fixture_marker" \
  run_fixture_transport_install "$substitution_fixture_stage" "$substitution_fixture_root" fixture-substituted \
  "$TEMP_DIR/substituted-fixture-cleanup.out" &
FIXTURE_REPLACEMENT_PID=$!
while [[ ! -e "$substitution_fixture_marker" ]]; do sleep 0.01; kill -0 "$FIXTURE_REPLACEMENT_PID"; done
FIXTURE_REPLACEMENT_STOPPED_PID="$(<"$substitution_fixture_marker")"
mv -T -- "$substitution_fixture_candidate" "$substitution_fixture_saved"
ln -s -- "$substitution_fixture_external" "$substitution_fixture_candidate"
kill -CONT "$FIXTURE_REPLACEMENT_STOPPED_PID"
if wait "$FIXTURE_REPLACEMENT_PID"; then
  fail 'Fixture cleanup accepted a substituted candidate directory.'
fi
FIXTURE_REPLACEMENT_PID=""; FIXTURE_REPLACEMENT_STOPPED_PID=""
test "$(stat -c '%d:%i:%u:%g:%h:%a:%s' "$substitution_fixture_external" \
  "$substitution_fixture_external/manifest.json" "$substitution_fixture_external/hyg_v42.sqlite")|$(sha256sum \
  "$substitution_fixture_external/manifest.json" "$substitution_fixture_external/hyg_v42.sqlite")" = "$substitution_fixture_before" ||
  fail 'Fixture cleanup mutated a substituted external candidate target.'
test -f "$substitution_fixture_root/.fixture-candidate-transaction"
rm -f -- "$substitution_fixture_candidate"
mv -T -- "$substitution_fixture_saved" "$substitution_fixture_candidate"
run_fixture_transport_install "$substitution_fixture_stage" "$substitution_fixture_root" fixture-substituted \
  "$TEMP_DIR/substituted-fixture-recovered.out"
test "$(readlink "$substitution_fixture_root/current")" = versions/fixture-substituted
test ! -e "$substitution_fixture_root/.fixture-candidate-transaction"

wait_for_fixture_lock() {
  local lock_path="$1" process_id="$2" attempt
  for ((attempt=0; attempt<100; attempt++)); do
    kill -0 "$process_id" 2>/dev/null || return 1
    if [[ -f "$lock_path" ]] && ! flock -n "$lock_path" true; then return 0; fi
    sleep 0.05
  done
  return 1
}

require_catalog_lock_blocked() {
  local root="$1"
  if timeout 0.2 bash -c 'source "$1"; hyg_catalog_acquire_root_lock "$2"' _ \
    "$REPO_ROOT/scripts/catalog/catalog-common.sh" "$root"; then
    fail 'A second package-kind publisher acquired the shared catalog root lock.'
  else
    [[ $? -eq 124 ]] || fail 'Cross-kind catalog lock probe failed for an unexpected reason.'
  fi
}

concurrent_root="$TEMP_DIR/remote/concurrent-fixture-catalog"
same_stage="$TEMP_DIR/concurrent-stage-same"
make_fixture_transport_stage "$same_stage" fixture-v1
rm -rf "$concurrent_root"
HVO_CATALOG_TEST_MODE=true HVO_CATALOG_TEST_LOCK_HOLD_SECONDS=1 \
  run_fixture_transport_install "$same_stage" "$concurrent_root" fixture-v1 "$TEMP_DIR/concurrent-same-first.out" &
same_first_pid=$!
run_fixture_transport_install "$same_stage" "$concurrent_root" fixture-v1 "$TEMP_DIR/concurrent-same-second.out" &
same_second_pid=$!
wait "$same_first_pid"; wait "$same_second_pid"
test "$(<"$TEMP_DIR/concurrent-same-first.out")" = $'installed\tversions/fixture-v1\t'"$catalog_sha"
test "$(<"$TEMP_DIR/concurrent-same-second.out")" = $'installed\tversions/fixture-v1\t'"$catalog_sha"
test "$(readlink "$concurrent_root/current")" = versions/fixture-v1
test ! -e "$concurrent_root/.fixture-pointer-transaction"

first_stage="$TEMP_DIR/concurrent-stage-v1"
second_stage="$TEMP_DIR/concurrent-stage-v2"
make_fixture_transport_stage "$first_stage" fixture-v1
make_fixture_transport_stage "$second_stage" fixture-v2
chmod -R u+w "$concurrent_root"; rm -rf "$concurrent_root"
HVO_CATALOG_TEST_MODE=true HVO_CATALOG_TEST_LOCK_HOLD_SECONDS=2 \
  run_fixture_transport_install "$first_stage" "$concurrent_root" fixture-v1 "$TEMP_DIR/concurrent-v1.out" &
first_publisher_pid=$!
wait_for_fixture_lock "$concurrent_root/.catalog.lock" "$first_publisher_pid" || fail 'First different-version fixture publisher did not acquire its catalog root lock.'
require_catalog_lock_blocked "$concurrent_root"
run_fixture_transport_install "$second_stage" "$concurrent_root" fixture-v2 "$TEMP_DIR/concurrent-v2.out" &
second_publisher_pid=$!
wait "$first_publisher_pid"; wait "$second_publisher_pid"
test "$(<"$TEMP_DIR/concurrent-v1.out")" = $'installed\tversions/fixture-v1\t'"$catalog_sha"
test "$(<"$TEMP_DIR/concurrent-v2.out")" = $'installed\tversions/fixture-v2\t'"$catalog_sha"
test "$(readlink "$concurrent_root/current")" = versions/fixture-v2
test "$(readlink "$concurrent_root/previous")" = versions/fixture-v1
test -d "$concurrent_root/versions/fixture-v1" && test -d "$concurrent_root/versions/fixture-v2"
test ! -e "$concurrent_root/.fixture-pointer-transaction"

# A committed v1 pointer move is completed before a newly requested v2 publication starts.
cross_version_root="$TEMP_DIR/remote/cross-version-fixture-catalog"
rm -rf "$cross_version_root"; mkdir -m 700 "$cross_version_root"
if HVO_FIXTURE_CATALOG_TEST_FAIL_AT=after-pointer-transaction \
  run_fixture_transport_install "$first_stage" "$cross_version_root" fixture-v1 "$TEMP_DIR/cross-version-v1.out"; then
  fail 'Cross-version fixture pointer failpoint passed.'
fi
test -f "$cross_version_root/.fixture-pointer-transaction"
run_fixture_transport_install "$second_stage" "$cross_version_root" fixture-v2 "$TEMP_DIR/cross-version-v2.out"
test "$(readlink "$cross_version_root/current")" = versions/fixture-v2
test "$(readlink "$cross_version_root/previous")" = versions/fixture-v1
test ! -e "$cross_version_root/.fixture-pointer-transaction"

# A bound root cannot be repurposed to another package kind or catalog ID.
(
  source "$REPO_ROOT/scripts/catalog/catalog-common.sh"
  hyg_catalog_acquire_root_lock "$cross_version_root"
  if hyg_catalog_require_lineage "$cross_version_root" test-fixture production 2 3 >/dev/null 2>&1; then exit 91; fi
  if hyg_catalog_require_lineage "$cross_version_root" different-fixture fixture 2 3 >/dev/null 2>&1; then exit 92; fi
)

# Reactivating the same version preserves previous; activating v1 again performs a validated rollback and swaps previous to v2.
run_fixture_transport_install "$second_stage" "$cross_version_root" fixture-v2 "$TEMP_DIR/cross-version-v2-repeat.out"
test "$(readlink "$cross_version_root/previous")" = versions/fixture-v1
run_fixture_transport_install "$first_stage" "$cross_version_root" fixture-v1 "$TEMP_DIR/cross-version-rollback.out"
test "$(readlink "$cross_version_root/current")" = versions/fixture-v1
test "$(readlink "$cross_version_root/previous")" = versions/fixture-v2

# A schema-drifted database remains internally valid and self-consistent but cannot be published.
schema_drift_stage="$TEMP_DIR/schema-drift-stage"
make_fixture_transport_stage "$schema_drift_stage" fixture-schema-drift
sqlite3 "$schema_drift_stage/bundle/hyg_v42.sqlite" 'CREATE TABLE unexpected_schema(value TEXT);'
schema_drift_sha="$(sha256sum "$schema_drift_stage/bundle/hyg_v42.sqlite" | cut -d' ' -f1)"
schema_drift_length="$(wc -c < "$schema_drift_stage/bundle/hyg_v42.sqlite")"
jq --arg sha "$schema_drift_sha" --argjson length "$schema_drift_length" '.database.sha256=$sha | .database.length=$length' \
  "$schema_drift_stage/bundle/manifest.json" > "$schema_drift_stage/bundle/manifest.json.changed"
mv "$schema_drift_stage/bundle/manifest.json.changed" "$schema_drift_stage/bundle/manifest.json"
if run_fixture_transport_install "$schema_drift_stage" "$TEMP_DIR/remote/schema-drift-catalog" fixture-schema-drift \
  "$TEMP_DIR/schema-drift.out" "$schema_drift_sha" "$schema_drift_length" 9; then
  fail 'Fixture publication accepted a self-consistent schema-drifted database.'
fi
test ! -e "$TEMP_DIR/remote/schema-drift-catalog/current"

rm -f "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" "$TEMP_DIR/output/$deploy_case-state/catalog-manifest.json" \
  "$TEMP_DIR/output/$deploy_case-evidence/catalog.json"
chmod -R u+w "$install_root" 2>/dev/null || true
rm -rf "$install_root"
mkdir -m 700 "$install_root" "$install_root/versions"
mkdir -p "$install_root/versions/prior" "$install_root/versions/fixture-v1"
printf 'prior\n' > "$install_root/versions/prior/marker"
ln -s versions/prior "$install_root/current"
cp "$bundle/hyg_v42.sqlite" "$install_root/versions/fixture-v1/hyg_v42.sqlite"
jq '.catalog.id="hostile-existing-fixture"' "$TEMP_DIR/fixture-manifest.valid" > "$install_root/versions/fixture-v1/manifest.json"
chmod 444 "$install_root/versions/fixture-v1/manifest.json" "$install_root/versions/fixture-v1/hyg_v42.sqlite"
chmod 555 "$install_root/versions/fixture-v1"
if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/hostile-existing-catalog.log" 2>&1; then
  fail 'Catalog accepted a hostile preexisting fixture version.'
fi
test "$(readlink "$install_root/current")" = versions/prior || fail 'Catalog replaced current before validating the existing fixture version.'
chmod 755 "$install_root/versions/fixture-v1"
rm -rf "$install_root/versions/fixture-v1"
rm "$install_root/current"; chmod 755 "$install_root/versions/prior"; rm -rf "$install_root/versions/prior"
run_deploy_mode catalog "$deploy_case" isolated >/dev/null
installed_catalog_manifest="$install_root/versions/fixture-v1/manifest.json"
test "$(stat -c %a "$install_root/versions/fixture-v1")" = 555
test "$(stat -c %a "$installed_catalog_manifest")" = 444
test "$(stat -c %a "$install_root/versions/fixture-v1/hyg_v42.sqlite")" = 444
cp "$installed_catalog_manifest" "$TEMP_DIR/installed-catalog.valid"
for installed_drift in kind version id schema preprocessing database-sha database-length database-rows; do
  chmod 644 "$installed_catalog_manifest"
  case "$installed_drift" in
    kind) jq '.package.kind="production"' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    version) jq '.package.version="other"' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    id) jq '.catalog.id="other"' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    schema) jq '.schemaVersion="drifted"' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    preprocessing) jq '.preprocessingVersion="drifted"' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    database-sha) jq '.database.sha256=("0"*64)' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    database-length) jq '.database.length += 1' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    database-rows) jq '.database.rowCount += 1' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
  esac
  if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/installed-catalog-$installed_drift.log" 2>&1; then fail "Catalog accepted installed $installed_drift lineage drift."; fi
  cp "$TEMP_DIR/installed-catalog.valid" "$installed_catalog_manifest"
  chmod 444 "$installed_catalog_manifest"
done
installed_database="$install_root/versions/fixture-v1/hyg_v42.sqlite"; cp "$installed_database" "$TEMP_DIR/installed-database.valid"
chmod 644 "$installed_database"
printf 'drift\n' >> "$installed_database"
if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/installed-catalog-bytes.log" 2>&1; then fail 'Catalog accepted installed database byte drift.'; fi
cp "$TEMP_DIR/installed-database.valid" "$installed_database"
chmod 444 "$installed_database"
ln -sfn versions/missing "$install_root/current"
if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/installed-catalog-current.log" 2>&1; then fail 'Catalog accepted conflicting current selection.'; fi
ln -sfn versions/fixture-v1 "$install_root/current"
run_deploy_mode catalog "$deploy_case" isolated >/dev/null
test "$(stat -c %a "$install_root/versions/fixture-v1")" = 555
test "$(stat -c %a "$installed_catalog_manifest")" = 444
test "$(stat -c %a "$installed_database")" = 444

expect_fixture_resume_failure() {
  local label="$1"
  if run_deploy_mode catalog "$deploy_case" isolated > "$TEMP_DIR/catalog-resume-$label.log" 2>&1; then
    fail "Catalog resume accepted fixture installation drift: $label"
  fi
}

# Resume revalidates the complete fixture installation boundary before returning verified.
rm "$install_root/.catalog-lineage.json"
run_deploy_mode catalog "$deploy_case" isolated >/dev/null
test "$(jq -r '.catalogId' "$install_root/.catalog-lineage.json")" = test-fixture
cp "$install_root/.catalog-lineage.json" "$TEMP_DIR/catalog-lineage.valid"
for lineage_drift in id kind mode hardlink; do
  case "$lineage_drift" in
    id) jq '.catalogId="other-fixture"' "$TEMP_DIR/catalog-lineage.valid" > "$install_root/.catalog-lineage.json"; chmod 600 "$install_root/.catalog-lineage.json" ;;
    kind) jq '.packageKind="production" | .packageLineage="hyg-v42-production-p3-s2"' "$TEMP_DIR/catalog-lineage.valid" > "$install_root/.catalog-lineage.json"; chmod 600 "$install_root/.catalog-lineage.json" ;;
    mode) chmod 640 "$install_root/.catalog-lineage.json" ;;
    hardlink) ln "$install_root/.catalog-lineage.json" "$TEMP_DIR/catalog-lineage.hardlink" ;;
  esac
  expect_fixture_resume_failure "lineage-$lineage_drift"
  rm -f "$TEMP_DIR/catalog-lineage.hardlink"
  cp "$TEMP_DIR/catalog-lineage.valid" "$install_root/.catalog-lineage.json"
  chmod 600 "$install_root/.catalog-lineage.json"
done

resume_catalog_parent="$TEMP_DIR/catalog-resume-parent"
mv "$catalog_parent" "$resume_catalog_parent"; ln -s "$resume_catalog_parent" "$catalog_parent"
expect_fixture_resume_failure symlinked-ancestor
rm "$catalog_parent"; mv "$resume_catalog_parent" "$catalog_parent"

resume_versions="$TEMP_DIR/catalog-resume-versions"
mv "$install_root/versions" "$resume_versions"; ln -s "$resume_versions" "$install_root/versions"
expect_fixture_resume_failure symlinked-versions
rm "$install_root/versions"; mv "$resume_versions" "$install_root/versions"

for writable_fixture_path in "$install_root" "$install_root/versions" "$install_root/versions/fixture-v1"; do
  original_mode="$(stat -c %a "$writable_fixture_path")"
  chmod g+w "$writable_fixture_path"
  expect_fixture_resume_failure "writable-$(basename "$writable_fixture_path")-$original_mode"
  chmod "$original_mode" "$writable_fixture_path"
done
for writable_payload in "$installed_catalog_manifest" "$installed_database"; do
  chmod 644 "$writable_payload"
  expect_fixture_resume_failure "writable-$(basename "$writable_payload")"
  chmod 444 "$writable_payload"
done

for hardlinked_payload in "$installed_catalog_manifest" "$installed_database"; do
  payload_hardlink="$TEMP_DIR/$(basename "$hardlinked_payload").hardlink"
  ln "$hardlinked_payload" "$payload_hardlink"
  expect_fixture_resume_failure "hardlinked-$(basename "$hardlinked_payload")"
  rm "$payload_hardlink"
done

installed_manifest_compact="$(jq -c . "$TEMP_DIR/installed-catalog.valid")"
for manifest_schema_drift in unknown-top unknown-package unknown-catalog unknown-database duplicate-top duplicate-package malformed oversized; do
  chmod 644 "$installed_catalog_manifest"
  case "$manifest_schema_drift" in
    unknown-top) jq '.unexpected=true' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    unknown-package) jq '.package.unexpected=true' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    unknown-catalog) jq '.catalog.unexpected=true' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    unknown-database) jq '.database.unexpected=true' "$TEMP_DIR/installed-catalog.valid" > "$installed_catalog_manifest" ;;
    duplicate-top) printf '{"manifestVersion":2,%s\n' "${installed_manifest_compact#\{}" > "$installed_catalog_manifest" ;;
    duplicate-package) printf '%s\n' "${installed_manifest_compact/\"package\":\{\"kind\":\"fixture\"/\"package\":\{\"kind\":\"fixture\",\"kind\":\"fixture\"}" > "$installed_catalog_manifest" ;;
    malformed) printf '{"manifestVersion":2\n' > "$installed_catalog_manifest" ;;
    oversized) cp "$TEMP_DIR/installed-catalog.valid" "$installed_catalog_manifest"; printf '%*s' 65537 '' >> "$installed_catalog_manifest" ;;
  esac
  chmod 444 "$installed_catalog_manifest"
  expect_fixture_resume_failure "manifest-$manifest_schema_drift"
  chmod 644 "$installed_catalog_manifest"
  cp "$TEMP_DIR/installed-catalog.valid" "$installed_catalog_manifest"
  chmod 444 "$installed_catalog_manifest"
done

chmod 755 "$install_root/versions/fixture-v1"
printf 'unexpected\n' > "$install_root/versions/fixture-v1/unexpected"
chmod 555 "$install_root/versions/fixture-v1"
expect_fixture_resume_failure extra-payload
chmod 755 "$install_root/versions/fixture-v1"; rm "$install_root/versions/fixture-v1/unexpected"; chmod 555 "$install_root/versions/fixture-v1"

rm "$install_root/current"; printf 'hostile\n' > "$install_root/current"
expect_fixture_resume_failure hostile-current
rm "$install_root/current"; ln -s ../outside "$install_root/current"
expect_fixture_resume_failure hostile-current-target
rm "$install_root/current"; ln -s versions/fixture-v1 "$install_root/current"
ln -s versions/missing "$install_root/previous"
expect_fixture_resume_failure hostile-previous-target
rm "$install_root/previous"; ln -s versions/fixture-v1 "$install_root/previous"
expect_fixture_resume_failure previous-equals-current
rm "$install_root/previous"
printf 'versions/fixture-v1\n' > "$install_root/.fixture-pointer-transaction"; chmod 600 "$install_root/.fixture-pointer-transaction"
expect_fixture_resume_failure leftover-pointer-transaction
rm "$install_root/.fixture-pointer-transaction"
run_deploy_mode catalog "$deploy_case" isolated >/dev/null
canonical_east_root="$(jq -r '.cameraAgents[0].runtimeRoot' "$INVENTORY")"
jq -n --arg instance "$(jq -r '.cameraAgents[0].instanceId' "$INVENTORY")" \
  '{schemaVersion:1,product:"HVO.SkyMonitor",component:"cameraAgent",instanceId:$instance,creationProvenance:{migration:"singular-layout-v1"},applicationIdentityFile:"application-identity.json"}' \
  > "$canonical_east_root/instance-manifest.json"
jq -cn '{schemaVersion:1,state:"pre-provisioning",configuredIdentity:"east-agent",boundIdentity:null}' > "$canonical_east_root/application-identity.json"
chmod 600 "$canonical_east_root/instance-manifest.json" "$canonical_east_root/application-identity.json"
compose_before_retired_provenance="$(grep -c $'\tcompose\t' "$LOG" || true)"
if run_deploy_mode up "$deploy_case" isolated > "$TEMP_DIR/retired-provenance.log" 2>&1; then
  fail 'Up accepted retired singular-layout provenance.'
fi
test "$(grep -c $'\tcompose\t' "$LOG" || true)" = "$compose_before_retired_provenance" || \
  fail 'Retired singular-layout provenance reached Compose mutation.'
jq -n --arg instance "$(jq -r '.cameraAgents[0].instanceId' "$INVENTORY")" \
  '{schemaVersion:1,product:"HVO.SkyMonitor",component:"cameraAgent",instanceId:$instance,creationProvenance:{installationId:"test-installation"},applicationIdentityFile:"application-identity.json"}' \
  > "$canonical_east_root/instance-manifest.json"
if DEPLOY_TEST_FAILPOINT=abrupt-after-private-scp-upload run_deploy_mode up "$deploy_case" isolated > "$TEMP_DIR/up-private-upload-crash.log" 2>&1; then
  fail 'Private SCP interruption failpoint passed.'
fi
jq -e 'length == 1 and (.[0].path | test("/[.]hvo-deploy/uploads/hvo-upload-up-[0-9]+-[0-9a-f]{32}[.]tmp$")) and .[0].target.name == "logic"' \
  "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
upload_path="$(jq -r '.[0].path' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json")"
test -f "$upload_path"
if FAKE_SSH_UNREACHABLE=logic run_deploy_mode up "$deploy_case" isolated > "$TEMP_DIR/up-private-upload-cleanup-failure.log" 2>&1; then
  fail 'Up passed while a registered private upload could not be reconciled.'
fi
test -f "$upload_path"
jq -e 'length == 1' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
run_deploy_mode up "$deploy_case" isolated >/dev/null
test ! -e "$upload_path"
jq -e 'length == 0' "$TEMP_DIR/output/$deploy_case-state/private-upload-registry.json" >/dev/null
run_deploy_mode up "$deploy_case" isolated >/dev/null
jq -e '.phaseStatus == "passed" and (.targets | length) == 3 and ([.targets[].target] | unique | length) == 3 and
  ([.resources[].kind] | sort) == ["existing-services","logic-initializer"]' "$TEMP_DIR/output/$deploy_case-state/up-ledger.json" >/dev/null
jq -e --slurpfile preflight "$TEMP_DIR/output/$deploy_case-state/manifest.json" '
  . as $catalog |
  ($preflight[0].targets | map(select(.architecture == "arm64") | .name)) as $arm64 |
  .phaseStatus == "passed" and (.targets | length) == 3 and all(.targets[]; .status == "installed") and
  ($arm64 | length) > 0 and all($arm64[]; . as $name | any($catalog.targets[]; .target == $name and .status == "installed"))' \
  "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" >/dev/null
logic_root="$(jq -r '.logicHost.runtimeRoot' "$INVENTORY")"; logic_config="$logic_root/config"
test -f "$logic_config/initializer-secrets/ConnectionStrings__skymonitordb-migrations"
test ! -e "$logic_config/runtime-secrets/ConnectionStrings__skymonitordb-migrations"
test -f "$logic_config/runtime-secrets/ConnectionStrings__skymonitordb"
test ! -e "$logic_config/initializer-secrets/ConnectionStrings__skymonitordb"
test "$(<"$logic_config/initializer-secrets/Catalog__Root")" = /app/catalog
test "$(<"$logic_config/runtime-secrets/Catalog__Root")" = /app/catalog
test "$(<"$logic_config/initializer-secrets/Catalog__RequiredCatalogId")" = test-fixture
test "$(<"$logic_config/runtime-secrets/Catalog__RequiredCatalogId")" = test-fixture
test "$(<"$logic_config/initializer-secrets/Deployment__Mode")" = isolated
test "$(<"$logic_config/runtime-secrets/Deployment__Mode")" = isolated
test "$(<"$logic_config/initializer-secrets/Catalog__RequiredPackageKind")" = Fixture
test "$(<"$logic_config/initializer-secrets/DatabaseSeed__ApiKeys__0__UserEmail")" = split-host-owner@hvo.local
test "$(<"$logic_config/initializer-secrets/DatabaseSeed__ApiKeys__0__AccessLevel")" = ReadWrite
test -f "$logic_config/initializer-secrets/DatabaseSeed__ApiKeys__0__RawKey"
test ! -e "$logic_config/runtime-secrets/DatabaseSeed__ApiKeys__0__RawKey"
grep -Fq "HVO_CATALOG_ROOT=$install_root" "$TEMP_DIR/output/$deploy_case-state/up-rendered/logic.env"
east_root="$(jq -r '.cameraAgents[0].runtimeRoot' "$INVENTORY")"
test ! -e "$east_root/config/secrets/ConnectionStrings__skymonitordb"
east_config="$east_root/config"
test "$(<"$east_config/secrets/LocalIdentity__AdminPasswordFile")" = /run/hvo-private/owner-password
test "$(<"$east_config/secrets/CameraAgent__ConfigFilePath")" = /app/cameraagent.deploy.json
test "$(<"$east_config/secrets/SkyMonitor__BaseUrl")" = "https://public.example.test:$HTTP_PORT"
test "$(<"$east_config/secrets/Catalog__Root")" = /app/catalog
test "$(<"$east_config/secrets/Catalog__RequiredCatalogId")" = test-fixture
test "$(<"$east_config/secrets/Catalog__RequiredPackageKind")" = Fixture
cmp -s "$TEMP_DIR/east-module.json" "$east_config/camera-module.json"
test -s "$east_config/private/owner-password"
test ! -e "$east_config/cameraagent.json"
# Resume an already-provisioned canonical instance without restoring bootstrap defaults.
if [[ "$SELECTED_SHARD" == existing-catalog-up ]]; then
jq -n '{schemaVersion:1,state:"bound",configuredIdentity:"east-agent",boundIdentity:"device-east"}' > "$east_root/application-identity.json"
printf 'device-east\n' > "$east_config/secrets/CameraAgent__AgentId"
printf 'false\n' > "$east_config/secrets/CameraAgent__ProvisioningStartupGate__Enabled"
printf 'true\n' > "$east_config/secrets/CameraAgent__CaptureDistribution__UploadEnabled"
jq '.agentId="device-east"' "$east_config/camera-module.json" > "$east_config/camera-module.json.changed" && mv "$east_config/camera-module.json.changed" "$east_config/camera-module.json"
chmod 600 "$east_root/application-identity.json" "$east_config/camera-module.json"
cp "$east_root/application-identity.json" "$TEMP_DIR/east-application-identity.valid"
for invalid_binding in unknown-key malformed-configured malformed-bound; do
  case "$invalid_binding" in
    unknown-key) jq '.unexpected=true' "$TEMP_DIR/east-application-identity.valid" > "$east_root/application-identity.json" ;;
    malformed-configured) jq '.configuredIdentity="invalid identity"' "$TEMP_DIR/east-application-identity.valid" > "$east_root/application-identity.json" ;;
    malformed-bound) jq '.boundIdentity="invalid identity"' "$TEMP_DIR/east-application-identity.valid" > "$east_root/application-identity.json" ;;
  esac
  chmod 600 "$east_root/application-identity.json"
  compose_before_identity_rejection="$(grep -c $'\tcompose\t' "$LOG" || true)"
  if run_deploy_mode up "$deploy_case" isolated > "$TEMP_DIR/binding-$invalid_binding.log" 2>&1; then
    fail "Up accepted malformed application identity: $invalid_binding"
  fi
  test "$(grep -c $'\tcompose\t' "$LOG" || true)" = "$compose_before_identity_rejection" ||
    fail "Malformed application identity reached Compose mutation: $invalid_binding"
  cp "$TEMP_DIR/east-application-identity.valid" "$east_root/application-identity.json"; chmod 600 "$east_root/application-identity.json"
done
migrated_module_sha="$(sha256sum "$east_config/camera-module.json")"; migrated_password_sha="$(sha256sum "$east_config/private/owner-password")"
run_deploy_mode up "$deploy_case" isolated >/dev/null
test "$(sha256sum "$east_config/camera-module.json")" = "$migrated_module_sha"
test "$(sha256sum "$east_config/private/owner-password")" = "$migrated_password_sha"
test "$(<"$east_config/secrets/CameraAgent__ProvisioningStartupGate__Enabled")" = false
test "$(<"$east_config/secrets/CameraAgent__CaptureDistribution__UploadEnabled")" = true
jq -e '(.targets[] | select(.target == "east") | .provisioningGate == false and .uploadEnabled == true) and
  (.targets[] | select(.target == "west") | .provisioningGate == true and .uploadEnabled == false)' "$TEMP_DIR/output/$deploy_case-state/up-ledger.json" >/dev/null
compose_before_bound_rejection="$(grep -c $'\tcompose\t' "$LOG" || true)"
rm "$east_config/secrets/CameraAgent__ProvisioningStartupGate__Enabled"
if run_deploy_mode up "$deploy_case" isolated > "$TEMP_DIR/bound-gate-missing.log" 2>&1; then fail 'Up accepted a bound instance with missing provisioning gate state.'; fi
test "$(grep -c $'\tcompose\t' "$LOG" || true)" = "$compose_before_bound_rejection" || fail 'Missing bound gate reached Compose mutation.'
jq -e '.phaseStatus != "passed"' "$TEMP_DIR/output/$deploy_case-state/up-ledger.json" >/dev/null
printf 'false\n' > "$east_config/secrets/CameraAgent__ProvisioningStartupGate__Enabled"
run_deploy_mode up "$deploy_case" isolated >/dev/null
compose_before_bound_rejection="$(grep -c $'\tcompose\t' "$LOG" || true)"
printf 'false\n' > "$east_config/secrets/CameraAgent__CaptureDistribution__UploadEnabled"
if run_deploy_mode up "$deploy_case" isolated > "$TEMP_DIR/bound-upload-wrong.log" 2>&1; then fail 'Up accepted a bound instance with disabled upload state.'; fi
test "$(grep -c $'\tcompose\t' "$LOG" || true)" = "$compose_before_bound_rejection" || fail 'Wrong bound upload state reached Compose mutation.'
jq -e '.phaseStatus != "passed"' "$TEMP_DIR/output/$deploy_case-state/up-ledger.json" >/dev/null
printf 'true\n' > "$east_config/secrets/CameraAgent__CaptureDistribution__UploadEnabled"
run_deploy_mode up "$deploy_case" isolated >/dev/null
jq -e '.phaseStatus == "passed" and (.targets[] | select(.target == "east") | .provisioningGate == false and .uploadEnabled == true)' "$TEMP_DIR/output/$deploy_case-state/up-ledger.json" >/dev/null
# Restore the canonical pre-provisioning fixture for unsharded continuation into bootstrap tests.
jq -cn '{schemaVersion:1,state:"pre-provisioning",configuredIdentity:"east-agent",boundIdentity:null}' > "$east_root/application-identity.json"
rm -f "$east_config/secrets/CameraAgent__AgentId"
cp "$TEMP_DIR/east-module.json" "$east_config/camera-module.json"
printf 'true\n' > "$east_config/secrets/CameraAgent__ProvisioningStartupGate__Enabled"
printf 'false\n' > "$east_config/secrets/CameraAgent__CaptureDistribution__UploadEnabled"
chmod 600 "$east_root/application-identity.json" "$east_config/camera-module.json"
run_deploy_mode up "$deploy_case" isolated >/dev/null
cp "$east_config/camera-module.json" "$FAKE_HTTP_STATE/east.active-profile.json"
jq -S -c . "$east_config/camera-module.json" | sha256sum | cut -d' ' -f1 > "$FAKE_HTTP_STATE/east.active-config-sha"
fi

# An operator-supplied immutable v2 production catalog is accepted by catalog resume and up.
if [[ "$SELECTED_SHARD" == existing-catalog-up && -n "${HVO_PRODUCTION_CATALOG_BUNDLE:-}" ]]; then
legacy_path_inventory="$TEMP_DIR/legacy-v1-path.inventory"; cp "$INVENTORY" "$legacy_path_inventory"
legacy_path_case=existing-catalog; legacy_path_product="$TEMP_DIR/remote/$legacy_path_case-skymonitor"
legacy_path_source="$TEMP_DIR/existing-production-source-v2"
legacy_path_version="$legacy_path_product/catalogs/hyg-v42-production/versions/hyg-v4.2-p3-s2-r1"
cp -a "$HVO_PRODUCTION_CATALOG_BUNDLE" "$legacy_path_source"
legacy_path_sha="$(sha256sum "$legacy_path_source/hyg_v42.sqlite" | cut -d' ' -f1)"; legacy_path_length="$(wc -c < "$legacy_path_source/hyg_v42.sqlite")"
jq --arg product "$legacy_path_product" --arg bundle "$legacy_path_source" --arg sha "$legacy_path_sha" --argjson length "$legacy_path_length" \
  --arg run "$legacy_path_case" '
  .productRoot=$product |
  .catalogs=[{catalogId:"hyg-v42-production",displayName:"HYG 4.2 production catalog",kind:"production",version:"hyg-v4.2-p3-s2-r1",schemaVersion:"2",preprocessingVersion:"3",sha256:$sha,length:$length,rowCount:119625,bundlePath:$bundle,installRoot:($product+"/catalogs/hyg-v42-production"),allowFixture:false}] |
  .logicHost.catalogId="hyg-v42-production" | (.cameraAgents[].catalogId)="hyg-v42-production" |
  .logicHost.runtimeRoot=($product+"/logichosts/"+.logicHost.instanceId) |
  .cameraAgents |= map(.runtimeRoot=($product+"/cameraagents/"+.instanceId)) |
  .deployment.resources.project=$run | .deployment.resources.sqlDatabase=$run | .deployment.services.sql.database=$run |
  .deployment.resources.redisPrefix=($run+":") | .deployment.services.redis.prefix=($run+":") |
  .deployment.resources.artifactBucket=($run+"-artifacts") | .deployment.services.minio.artifactBucket=($run+"-artifacts") |
  .deployment.resources.diagnosticsBucket=($run+"-diagnostics") | .deployment.services.minio.diagnosticsBucket=($run+"-diagnostics")' \
  "$INVENTORY" > "$INVENTORY.changed" && mv "$INVENTORY.changed" "$INVENTORY"
deploy_case="$legacy_path_case"
mkdir -p "$legacy_path_product/logichosts" "$legacy_path_product/cameraagents" "$legacy_path_product/catalogs"
chmod 700 "$legacy_path_product" "$legacy_path_product/logichosts" "$legacy_path_product/cameraagents" "$legacy_path_product/catalogs"
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode preflight "$deploy_case" isolated >/dev/null
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare "$deploy_case" isolated >/dev/null
rm -f "$FAKE_IMAGE_STATE"/*.pushed "$FAKE_IMAGE_STATE"/*.platforms "$FAKE_IMAGE_STATE"/*.labels "$FAKE_IMAGE_STATE"/*.reference "$FAKE_IMAGE_STATE"/*.config
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode images "$deploy_case" isolated >/dev/null
mkdir -p "$(dirname "$legacy_path_version")"
cp -a "$HVO_PRODUCTION_CATALOG_BUNDLE" "$legacy_path_version"
ln -s versions/hyg-v4.2-p3-s2-r1 "$legacy_path_product/catalogs/hyg-v42-production/current"
printf '' > "$legacy_path_product/catalogs/hyg-v42-production/.catalog.lock"
chmod 600 "$legacy_path_product/catalogs/hyg-v42-production/.catalog.lock"
legacy_manifest_sha_before="$(sha256sum "$legacy_path_version/manifest.json" | cut -d' ' -f1)"
legacy_database_sha_before="$(sha256sum "$legacy_path_version/hyg_v42.sqlite" | cut -d' ' -f1)"
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode catalog "$deploy_case" isolated >/dev/null
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode catalog "$deploy_case" isolated >/dev/null
legacy_installed="$legacy_path_product/catalogs/hyg-v42-production/versions/hyg-v4.2-p3-s2-r1"
legacy_recovery_version="$legacy_path_product/catalogs/hyg-v42-production/versions/hyg-v4.2-p3-s2-r2"
cp -a "$legacy_path_source" "$legacy_recovery_version"
chmod u+w "$legacy_recovery_version" "$legacy_recovery_version/manifest.json"
jq '.package.version="hyg-v4.2-p3-s2-r2"' "$legacy_recovery_version/manifest.json" > "$legacy_recovery_version/manifest.json.changed"
mv "$legacy_recovery_version/manifest.json.changed" "$legacy_recovery_version/manifest.json"
chmod 444 "$legacy_recovery_version"/*; chmod 555 "$legacy_recovery_version"
legacy_transaction="$legacy_path_product/catalogs/hyg-v42-production/.pointer-transaction"
printf 'versions/hyg-v4.2-p3-s2-r1\nversions/hyg-v4.2-p3-s2-r2\n' > "$legacy_transaction"; chmod 600 "$legacy_transaction"
legacy_pointer_temporary="$legacy_path_product/catalogs/hyg-v42-production/.previous.tmp.123.456"
ln -s versions/hyg-v4.2-p3-s2-r2 "$legacy_pointer_temporary"
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode catalog "$deploy_case" isolated >/dev/null
test ! -e "$legacy_transaction"; test ! -L "$legacy_transaction"
test ! -e "$legacy_pointer_temporary"; test ! -L "$legacy_pointer_temporary"
test "$(readlink "$legacy_path_product/catalogs/hyg-v42-production/current")" = versions/hyg-v4.2-p3-s2-r1
test "$(readlink "$legacy_path_product/catalogs/hyg-v42-production/previous")" = versions/hyg-v4.2-p3-s2-r2
for hostile_production_transaction in symlink hardlink mode owner type malformed unsafe-target; do
  legacy_transaction_outside="$TEMP_DIR/legacy-transaction-$hostile_production_transaction"
  printf 'versions/hyg-v4.2-p3-s2-r1\nversions/hyg-v4.2-p3-s2-r2\n' > "$legacy_transaction_outside"
  chmod 600 "$legacy_transaction_outside"
  case "$hostile_production_transaction" in
    symlink) ln -s "$legacy_transaction_outside" "$legacy_transaction" ;;
    hardlink) ln "$legacy_transaction_outside" "$legacy_transaction" ;;
    mode) cp "$legacy_transaction_outside" "$legacy_transaction"; chmod 640 "$legacy_transaction" ;;
    owner)
      cp "$legacy_transaction_outside" "$legacy_transaction"; chmod 600 "$legacy_transaction"
      cp "$FAKE_REMOTE_UID_MAP" "$TEMP_DIR/legacy-transaction-uid-map"
      printf '%s\t99999\n' "$legacy_transaction" >> "$FAKE_REMOTE_UID_MAP"
      ;;
    type) mkdir -m 700 "$legacy_transaction" ;;
    malformed) printf 'versions/hyg-v4.2-p3-s2-r1\n' > "$legacy_transaction"; chmod 600 "$legacy_transaction" ;;
    unsafe-target) printf 'versions/missing\n-\n' > "$legacy_transaction"; chmod 600 "$legacy_transaction" ;;
  esac
  legacy_transaction_before="$(stat -c '%d:%i:%u:%h:%a:%s' "$legacy_transaction_outside")|$(sha256sum "$legacy_transaction_outside")"
  if FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode catalog "$deploy_case" isolated \
    > "$TEMP_DIR/legacy-transaction-$hostile_production_transaction.log" 2>&1; then
    [[ "$hostile_production_transaction" != owner ]] || mv "$TEMP_DIR/legacy-transaction-uid-map" "$FAKE_REMOTE_UID_MAP"
    fail "Production transport verification accepted hostile pointer transaction: $hostile_production_transaction"
  fi
  [[ "$hostile_production_transaction" != owner ]] || mv "$TEMP_DIR/legacy-transaction-uid-map" "$FAKE_REMOTE_UID_MAP"
  test -e "$legacy_transaction" || test -L "$legacy_transaction" ||
    fail "Production transport verification removed hostile pointer transaction: $hostile_production_transaction"
  test "$(stat -c '%d:%i:%u:%h:%a:%s' "$legacy_transaction_outside")|$(sha256sum "$legacy_transaction_outside")" = "$legacy_transaction_before" ||
    fail "Production transport verification mutated external transaction state: $hostile_production_transaction"
  test "$(readlink "$legacy_path_product/catalogs/hyg-v42-production/current")" = versions/hyg-v4.2-p3-s2-r1
  test "$(readlink "$legacy_path_product/catalogs/hyg-v42-production/previous")" = versions/hyg-v4.2-p3-s2-r2
  rm -rf -- "$legacy_transaction"; rm -f -- "$legacy_transaction_outside"
  FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode catalog "$deploy_case" isolated >/dev/null
done
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode up "$deploy_case" isolated >/dev/null
test "$(sha256sum "$legacy_installed/manifest.json" | cut -d' ' -f1)" = "$legacy_manifest_sha_before"
test "$(sha256sum "$legacy_installed/hyg_v42.sqlite" | cut -d' ' -f1)" = "$legacy_database_sha_before"
jq -e '.manifestVersion == 2 and .catalog.id == "hyg-v42-production"' "$legacy_installed/manifest.json" >/dev/null
jq -e '.phaseStatus == "passed" and (.targets | length) == 3' "$TEMP_DIR/output/$deploy_case-state/catalog-ledger.json" >/dev/null
jq -e '.phaseStatus == "passed" and (.targets | length) == 3' "$TEMP_DIR/output/$deploy_case-state/up-ledger.json" >/dev/null
cp "$legacy_path_inventory" "$INVENTORY"; deploy_case=deploy-up; set_lifecycle_paths
fi
if grep -R -Fq 'fixture-secret-never-print' "$TEMP_DIR/output/$deploy_case-evidence"; then fail 'Deployment evidence exposed a secret.'; fi
grep -Fq $'compose\t--project-name\thvo-deploy-up-11111111111141118111111111111111' "$LOG"
grep -Fq $'east@example\tbash\t-s\t--\thttps://public.example.test:' "$LOG"
grep -Fq $'west@example\tbash\t-s\t--\thttps://public.example.test:' "$LOG"

# Carry a correlated same-host topology through the mutable lifecycle and preserve unrelated siblings.
if [[ "$SELECTED_SHARD" == existing-catalog-up ]]; then
cp "$INVENTORY" "$TEMP_DIR/deploy-up.inventory"
cp -a "$FAKE_HTTP_STATE" "$TEMP_DIR/deploy-up-http-state"
same_host_case=same-host-lifecycle; same_host_product="$TEMP_DIR/remote/$same_host_case-skymonitor"
jq --arg run "$same_host_case" --arg product "$same_host_product" --argjson eastPort "$EAST_PORT" --argjson westPort "$WEST_PORT" '
  .productRoot=$product | .logicHost.runtimeRoot=($product+"/logichosts/"+.logicHost.instanceId) |
  .cameraAgents |= map(.runtimeRoot=($product+"/cameraagents/"+.instanceId)) |
  .catalogs |= map(.installRoot=($product+"/catalogs/"+.catalogId)) |
  .logicHost as $logic | .cameraAgents |= map(.sshHost=$logic.sshHost | .dockerContext=$logic.dockerContext |
    .expectedArchitecture=$logic.expectedArchitecture | .expectedHostName=$logic.expectedHostName |
    .expectedHostIdentity=$logic.expectedHostIdentity | .expectedDockerDaemonIdentity=$logic.expectedDockerDaemonIdentity) |
  .cameraAgents[0].ports=[$eastPort] | .cameraAgents[0].internalEndpoint=("http://127.0.0.1:"+($eastPort|tostring)) |
  .cameraAgents[1].ports=[$westPort] | .cameraAgents[1].internalEndpoint=("http://127.0.0.1:"+($westPort|tostring)) |
  .deployment.resources.project=$run | .deployment.resources.sqlDatabase=$run | .deployment.services.sql.database=$run |
  .deployment.resources.redisPrefix=($run+":") | .deployment.services.redis.prefix=($run+":") |
  .deployment.resources.artifactBucket=($run+"-artifacts") | .deployment.services.minio.artifactBucket=($run+"-artifacts") |
  .deployment.resources.diagnosticsBucket=($run+"-diagnostics") | .deployment.services.minio.diagnosticsBucket=($run+"-diagnostics")' \
  "$INVENTORY" > "$INVENTORY.changed" && mv "$INVENTORY.changed" "$INVENTORY"
mkdir -p "$same_host_product/logichosts" "$same_host_product/cameraagents" "$same_host_product/catalogs" "$same_host_product/cameraagents/sibling-preserved"
chmod 700 "$same_host_product" "$same_host_product/logichosts" "$same_host_product/cameraagents" "$same_host_product/catalogs" "$same_host_product/cameraagents/sibling-preserved"
printf 'preserve\n' > "$same_host_product/cameraagents/sibling-preserved/value"
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode preflight "$same_host_case" isolated >/dev/null
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode prepare "$same_host_case" isolated >/dev/null
rm -f "$FAKE_IMAGE_STATE"/*.pushed "$FAKE_IMAGE_STATE"/*.platforms "$FAKE_IMAGE_STATE"/*.labels "$FAKE_IMAGE_STATE"/*.reference "$FAKE_IMAGE_STATE"/*.config
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode images "$same_host_case" isolated >/dev/null
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode catalog "$same_host_case" isolated >/dev/null
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode up "$same_host_case" isolated >/dev/null
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode bootstrap "$same_host_case" isolated >/dev/null
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode up "$same_host_case" isolated >/dev/null
FAKE_CURL_ALWAYS_SUCCESS=true run_deploy_mode down "$same_host_case" isolated --preserve-state >/dev/null
test "$(<"$same_host_product/cameraagents/sibling-preserved/value")" = preserve
jq -e 'all(.targets[] | select(.component == "cameraAgent"); .provisioningGate == false and .uploadEnabled == true)' "$TEMP_DIR/output/$same_host_case-state/up-ledger.json" >/dev/null
cp "$TEMP_DIR/deploy-up.inventory" "$INVENTORY"
rm -rf "$FAKE_HTTP_STATE"; cp -a "$TEMP_DIR/deploy-up-http-state" "$FAKE_HTTP_STATE"
set_lifecycle_paths
fi
set_lifecycle_paths
