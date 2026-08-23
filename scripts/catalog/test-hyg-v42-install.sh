#!/usr/bin/env bash
set -euo pipefail
umask 077

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/catalog/catalog-common.sh
source "$SCRIPT_DIR/catalog-common.sh"
readonly TEMPORARY_DIRECTORY="$(mktemp -d "${TMPDIR:-/tmp}/hvo-catalog-install-test.XXXXXX")"

cleanup() {
    local status=$?
    trap - EXIT
    chmod -R u+w "$TEMPORARY_DIRECTORY" 2>/dev/null || true
    rm -rf -- "$TEMPORARY_DIRECTORY"
    exit "$status"
}
trap cleanup EXIT

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s VERIFIED_PRODUCTION_BUNDLE\n' "$0" >&2
    exit 2
fi

readonly SOURCE_BUNDLE="$(realpath "$1")"
readonly INSTALL_ROOT="$TEMPORARY_DIRECTORY/install"

mkdir -p "$TEMPORARY_DIRECTORY/real-root"
ln -s "$TEMPORARY_DIRECTORY/real-root" "$TEMPORARY_DIRECTORY/symlink-root"
if "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$TEMPORARY_DIRECTORY/symlink-root" >/dev/null 2>&1; then
    printf 'installer unexpectedly accepted a symlink install root\n' >&2
    exit 1
fi

for hostile_lock in symlink hardlink mode type; do
    hostile_root="$TEMPORARY_DIRECTORY/hostile-lock-$hostile_lock"
    mkdir -m 700 "$hostile_root"
    lock="$hostile_root/.catalog.lock"
    case "$hostile_lock" in
        symlink) printf 'preserve\n' > "$TEMPORARY_DIRECTORY/lock-target"; ln -s "$TEMPORARY_DIRECTORY/lock-target" "$lock" ;;
        hardlink) printf 'preserve\n' > "$TEMPORARY_DIRECTORY/lock-target"; chmod 600 "$TEMPORARY_DIRECTORY/lock-target"; ln "$TEMPORARY_DIRECTORY/lock-target" "$lock" ;;
        mode) printf 'preserve\n' > "$lock"; chmod 640 "$lock" ;;
        type) mkdir -m 700 "$lock" ;;
    esac
    before_mode="$(stat -c %a "$lock")"
    if "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$hostile_root" >/dev/null 2>&1; then
        printf 'production installer accepted hostile catalog root lock: %s\n' "$hostile_lock" >&2
        exit 1
    fi
    [[ "$(stat -c %a "$lock")" == "$before_mode" ]] || {
        printf 'production installer mutated hostile catalog root lock: %s\n' "$hostile_lock" >&2
        exit 1
    }
    [[ "$hostile_lock" == type || "$(<"${lock}")" == preserve ]] || exit 1
    rm -rf "$lock"; rm -f "$TEMPORARY_DIRECTORY/lock-target"
done

make_revision() {
    local revision="$1"
    local output="$TEMPORARY_DIRECTORY/r$revision.bundle"
    cp -R -- "$SOURCE_BUNDLE" "$output"
    chmod -R u+w "$output"
    sqlite3 ':memory:' \
        "SELECT writefile('$output/manifest.json', CAST(json(json_set(CAST(readfile('$output/manifest.json') AS TEXT), '$.package.version', 'hyg-v4.2-p3-s2-r$revision')) AS BLOB));" \
        >/dev/null
    chmod 0444 "$output"/*
    chmod 0555 "$output"
    printf '%s\n' "$output"
}

assert_current() {
    local root="$1"
    local expected="$2"
    [[ "$(readlink "$root/current")" == "versions/hyg-v4.2-p3-s2-r$expected" ]] || {
        printf 'unexpected active package after installer test boundary\n' >&2
        exit 1
    }
}

for boundary in after-copy after-candidate-validation after-staging-fsync after-publish before-activation after-transaction after-previous-pointer after-current-pointer; do
    case "$boundary" in
        after-copy) revision=2 ;;
        after-candidate-validation) revision=3 ;;
        after-staging-fsync) revision=4 ;;
        after-publish) revision=5 ;;
        before-activation) revision=6 ;;
        after-transaction) revision=7 ;;
        after-previous-pointer) revision=8 ;;
        after-current-pointer) revision=9 ;;
    esac
    boundary_root="$TEMPORARY_DIRECTORY/install-$boundary"
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$boundary_root" >/dev/null
    candidate="$(make_revision "$revision")"
    if HVO_CATALOG_TEST_FAIL_AT="$boundary" \
        "$SCRIPT_DIR/install-hyg-v42.sh" install "$candidate" "$boundary_root" >/dev/null 2>&1; then
        printf 'installer fault injection unexpectedly succeeded at %s\n' "$boundary" >&2
        exit 1
    fi
    if [[ "$boundary" == after-current-pointer ]]; then
        assert_current "$boundary_root" "$revision"
    else
        assert_current "$boundary_root" 1
    fi
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$candidate" "$boundary_root" >/dev/null
    assert_current "$boundary_root" "$revision"
    [[ "$(readlink "$boundary_root/previous")" == "versions/hyg-v4.2-p3-s2-r1" ]]
done

for boundary in after-transaction after-previous-pointer after-current-pointer; do
    rollback_root="$TEMPORARY_DIRECTORY/rollback-$boundary"
    case "$boundary" in
        after-transaction) rollback_revision=11 ;;
        after-previous-pointer) rollback_revision=12 ;;
        after-current-pointer) rollback_revision=13 ;;
    esac
    rollback_upgrade="$(make_revision "$rollback_revision")"
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$rollback_root" >/dev/null
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$rollback_upgrade" "$rollback_root" >/dev/null
    if HVO_CATALOG_TEST_FAIL_AT="$boundary" \
        "$SCRIPT_DIR/rollback-hyg-v42.sh" "$rollback_root" >/dev/null 2>&1; then
        printf 'rollback fault injection unexpectedly succeeded at %s\n' "$boundary" >&2
        exit 1
    fi
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$rollback_root" >/dev/null
    assert_current "$rollback_root" 1
    [[ "$(readlink "$rollback_root/previous")" == "versions/hyg-v4.2-p3-s2-r$rollback_revision" ]]
done

maximum_revision="$(make_revision 2147483647)"
"$SCRIPT_DIR/install-hyg-v42.sh" install "$maximum_revision" "$TEMPORARY_DIRECTORY/maximum-revision" >/dev/null
assert_current "$TEMPORARY_DIRECTORY/maximum-revision" 2147483647
for invalid_revision in 01 2147483648 99999999999999999999; do
    invalid_bundle="$(make_revision "$invalid_revision")"
    if "$SCRIPT_DIR/install-hyg-v42.sh" install "$invalid_bundle" "$TEMPORARY_DIRECTORY/invalid-$invalid_revision" >/dev/null 2>&1; then
        printf 'installer unexpectedly accepted invalid package revision %s\n' "$invalid_revision" >&2
        exit 1
    fi
done

"$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null
assert_current "$INSTALL_ROOT" 1
[[ "$(stat -c '%u:%h:%a' "$INSTALL_ROOT/.catalog.lock")" == "$(id -u):1:600" ]]
[[ "$(stat -c '%u:%h:%a' "$INSTALL_ROOT/.catalog-lineage.json")" == "$(id -u):1:600" ]]
[[ "$(hyg_json_value "$INSTALL_ROOT/.catalog-lineage.json" '$.catalogId')" == "$HYG_CATALOG_ID" ]]
[[ "$(hyg_json_value "$INSTALL_ROOT/.catalog-lineage.json" '$.packageKind')" == production ]]

# A fixture-side holder and the production installer contend on the same root lock.
HVO_CATALOG_TEST_MODE=true HVO_CATALOG_TEST_LOCK_HOLD_SECONDS=2 bash -c \
    'source "$1"; hyg_catalog_acquire_root_lock "$2"' _ "$SCRIPT_DIR/catalog-common.sh" "$INSTALL_ROOT" &
cross_kind_holder=$!
for ((attempt=0; attempt<100; attempt++)); do
    kill -0 "$cross_kind_holder" 2>/dev/null || exit 1
    if ! flock -n "$INSTALL_ROOT/.catalog.lock" true; then break; fi
    sleep 0.05
done
set +e
timeout 0.2 "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null 2>&1
blocked_status=$?
set -e
[[ "$blocked_status" == 124 ]] || { printf 'production publisher bypassed the shared catalog root lock\n' >&2; exit 1; }
wait "$cross_kind_holder"
"$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null

# An exact pre-binding installation is adopted once; conflicting identity or kind is rejected thereafter.
rm "$INSTALL_ROOT/.catalog-lineage.json"
"$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null
[[ -f "$INSTALL_ROOT/.catalog-lineage.json" ]]
cp "$INSTALL_ROOT/.catalog-lineage.json" "$TEMPORARY_DIRECTORY/lineage.valid"
for conflict in id kind; do
    case "$conflict" in
        id) jq '.catalogId="different-catalog"' "$TEMPORARY_DIRECTORY/lineage.valid" > "$INSTALL_ROOT/.catalog-lineage.json" ;;
        kind) jq '.packageKind="fixture" | .packageLineage="hyg-v42-fixture-p3-s2"' "$TEMPORARY_DIRECTORY/lineage.valid" > "$INSTALL_ROOT/.catalog-lineage.json" ;;
    esac
    chmod 600 "$INSTALL_ROOT/.catalog-lineage.json"
    if "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null 2>&1; then
        printf 'production installer accepted conflicting root lineage: %s\n' "$conflict" >&2
        exit 1
    fi
    assert_current "$INSTALL_ROOT" 1
    cp "$TEMPORARY_DIRECTORY/lineage.valid" "$INSTALL_ROOT/.catalog-lineage.json"
    chmod 600 "$INSTALL_ROOT/.catalog-lineage.json"
done

collision="$TEMPORARY_DIRECTORY/collision.bundle"
cp -R -- "$SOURCE_BUNDLE" "$collision"
chmod -R u+w "$collision"
printf '\n' >> "$collision/manifest.json"
if "$SCRIPT_DIR/install-hyg-v42.sh" install "$collision" "$INSTALL_ROOT" >/dev/null 2>&1; then
    printf 'same-version bundle collision unexpectedly succeeded\n' >&2
    exit 1
fi
assert_current "$INSTALL_ROOT" 1

upgrade="$(make_revision 14)"
"$SCRIPT_DIR/install-hyg-v42.sh" install "$upgrade" "$INSTALL_ROOT" >/dev/null
assert_current "$INSTALL_ROOT" 14
[[ "$(readlink "$INSTALL_ROOT/previous")" == "versions/hyg-v4.2-p3-s2-r1" ]]

corrupt="$(make_revision 15)"
chmod u+w "$corrupt/hyg_v42.sqlite"
printf 'corrupt\n' >> "$corrupt/hyg_v42.sqlite"
if "$SCRIPT_DIR/install-hyg-v42.sh" install "$corrupt" "$INSTALL_ROOT" >/dev/null 2>&1; then
    printf 'corrupt upgrade unexpectedly succeeded\n' >&2
    exit 1
fi
assert_current "$INSTALL_ROOT" 14

mkdir -p "$INSTALL_ROOT/versions/.staging.interrupted"
printf 'interrupted\n' > "$INSTALL_ROOT/versions/.staging.interrupted/payload"
chmod 0444 "$INSTALL_ROOT/versions/.staging.interrupted/payload"
chmod 0555 "$INSTALL_ROOT/versions/.staging.interrupted"
"$SCRIPT_DIR/rollback-hyg-v42.sh" "$INSTALL_ROOT" >/dev/null
assert_current "$INSTALL_ROOT" 1
[[ "$(readlink "$INSTALL_ROOT/previous")" == "versions/hyg-v4.2-p3-s2-r14" ]]
[[ ! -e "$INSTALL_ROOT/versions/.staging.interrupted" ]]

# Reinstalling the active local bundle simulates an offline restart/revalidation.
"$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null
assert_current "$INSTALL_ROOT" 1

if [[ -n "${HVO_LEGACY_CATALOG_BUNDLE:-}" ]]; then
    legacy_root="$TEMPORARY_DIRECTORY/legacy-v1-install"
    legacy_target="$legacy_root/versions/hyg-v4.2-p3-s2-r1"
    mkdir -p "$legacy_root/versions"
    cp -a "$HVO_LEGACY_CATALOG_BUNDLE" "$legacy_target"
    ln -s versions/hyg-v4.2-p3-s2-r1 "$legacy_root/current"
    legacy_manifest_sha="$(hyg_sha256 "$legacy_target/manifest.json")"
    legacy_database_sha="$(hyg_sha256 "$legacy_target/hyg_v42.sqlite")"
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$legacy_root" >/dev/null
    assert_current "$legacy_root" 1
    [[ "$(hyg_sha256 "$legacy_target/manifest.json")" == "$legacy_manifest_sha" ]]
    [[ "$(hyg_sha256 "$legacy_target/hyg_v42.sqlite")" == "$legacy_database_sha" ]]
    [[ "$(hyg_json_value "$legacy_target/manifest.json" '$.manifestVersion')" == 1 ]]
    [[ "$(hyg_json_type "$legacy_target/manifest.json" '$.catalog.id')" == "" ]]
fi

printf 'Catalog production install, fault, restart, upgrade, and rollback checks passed.\n'
