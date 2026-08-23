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
    [[ -z "${replacement_publisher_pid:-}" ]] || kill -CONT "$replacement_publisher_pid" 2>/dev/null || true
    [[ -z "${replacement_publisher_pid:-}" ]] || kill "$replacement_publisher_pid" 2>/dev/null || true
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

# A lineage publisher invoked in a conditional cannot continue after its held lock descriptor is replaced.
conditional_lineage_root="$TEMPORARY_DIRECTORY/conditional-lineage"
mkdir -m 700 "$conditional_lineage_root" "$conditional_lineage_root/versions"
(
    hyg_catalog_acquire_root_lock "$conditional_lineage_root"
    mv -T -- "$conditional_lineage_root/.catalog.lock" "$conditional_lineage_root/.catalog.lock.original"
    : > "$conditional_lineage_root/.catalog.lock"; chmod 600 "$conditional_lineage_root/.catalog.lock"
    if hyg_catalog_require_lineage "$conditional_lineage_root" "$HYG_CATALOG_ID" production \
        "$HYG_SCHEMA_VERSION" "$HYG_PREPROCESSING_VERSION"; then
        exit 91
    fi
    [[ ! -e "$conditional_lineage_root/.catalog-lineage.json" ]]
)

# A failed durable command remains a failure when the shared publisher is invoked conditionally.
conditional_command_root="$TEMPORARY_DIRECTORY/conditional-command-lineage"
mkdir -m 700 "$conditional_command_root" "$conditional_command_root/versions"
(
    hyg_catalog_acquire_root_lock "$conditional_command_root"
    if HVO_CATALOG_TEST_MODE=true HVO_CATALOG_TEST_FAIL_COMMAND_AT=lineage-mv \
        hyg_catalog_require_lineage "$conditional_command_root" "$HYG_CATALOG_ID" production \
        "$HYG_SCHEMA_VERSION" "$HYG_PREPROCESSING_VERSION"; then
        exit 92
    fi
    [[ ! -e "$conditional_command_root/.catalog-lineage.json" ]]
    [[ -n "$(find "$conditional_command_root" -maxdepth 1 -name '.catalog-lineage.tmp.*' -print -quit)" ]]
)

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

for boundary in after-copy after-candidate-validation after-staging-fsync after-publish before-activation \
    after-pointer-transaction-temp-fsync after-transaction after-previous-pointer-temporary \
    after-previous-pointer after-current-pointer-temporary after-current-pointer; do
    case "$boundary" in
        after-copy) revision=2 ;;
        after-candidate-validation) revision=3 ;;
        after-staging-fsync) revision=4 ;;
        after-publish) revision=5 ;;
        before-activation) revision=6 ;;
        after-pointer-transaction-temp-fsync) revision=10 ;;
        after-transaction) revision=7 ;;
        after-previous-pointer-temporary) revision=17 ;;
        after-previous-pointer) revision=8 ;;
        after-current-pointer-temporary) revision=18 ;;
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
    case "$boundary" in
        after-previous-pointer-temporary) [[ -n "$(find "$boundary_root" -maxdepth 1 -name '.previous.tmp.*' -print -quit)" ]] ;;
        after-current-pointer-temporary) [[ -n "$(find "$boundary_root" -maxdepth 1 -name '.current.tmp.*' -print -quit)" ]] ;;
    esac
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$candidate" "$boundary_root" >/dev/null
    assert_current "$boundary_root" "$revision"
    [[ "$(readlink "$boundary_root/previous")" == "versions/hyg-v4.2-p3-s2-r1" ]]
    [[ -z "$(find "$boundary_root" -maxdepth 1 -name '.pointer-transaction.tmp.*' -print -quit)" ]]
    [[ -z "$(find "$boundary_root" -maxdepth 1 \( -name '.current.tmp.*' -o -name '.previous.tmp.*' \) -print -quit)" ]]
done

for boundary in after-transaction after-previous-pointer-temporary after-previous-pointer \
    after-current-pointer-temporary after-current-pointer; do
    rollback_root="$TEMPORARY_DIRECTORY/rollback-$boundary"
    case "$boundary" in
        after-transaction) rollback_revision=11 ;;
        after-previous-pointer-temporary) rollback_revision=19 ;;
        after-previous-pointer) rollback_revision=12 ;;
        after-current-pointer-temporary) rollback_revision=20 ;;
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
    case "$boundary" in
        after-previous-pointer-temporary) [[ -n "$(find "$rollback_root" -maxdepth 1 -name '.previous.tmp.*' -print -quit)" ]] ;;
        after-current-pointer-temporary) [[ -n "$(find "$rollback_root" -maxdepth 1 -name '.current.tmp.*' -print -quit)" ]] ;;
    esac
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$rollback_root" >/dev/null
    assert_current "$rollback_root" 1
    [[ "$(readlink "$rollback_root/previous")" == "versions/hyg-v4.2-p3-s2-r$rollback_revision" ]]
    [[ -z "$(find "$rollback_root" -maxdepth 1 \( -name '.current.tmp.*' -o -name '.previous.tmp.*' \) -print -quit)" ]]
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

# Replacing the shared lock at version publication retains the candidate and publishes nothing until the original inode is restored.
replacement_root="$TEMPORARY_DIRECTORY/replaced-production-lock"
replacement_bundle="$(make_revision 16)"
replacement_marker="$TEMPORARY_DIRECTORY/replaced-production-lock.marker"
"$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$replacement_root" >/dev/null
HVO_CATALOG_TEST_LOCK_BARRIER=production-version-publication HVO_CATALOG_TEST_LOCK_MARKER="$replacement_marker" \
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$replacement_bundle" "$replacement_root" >/dev/null 2>&1 &
replacement_publisher_pid=$!
while [[ ! -e "$replacement_marker" ]]; do sleep 0.01; kill -0 "$replacement_publisher_pid"; done
replacement_stopped_pid="$(<"$replacement_marker")"
mv -T -- "$replacement_root/.catalog.lock" "$replacement_root/.catalog.lock.original"
: > "$replacement_root/.catalog.lock"; chmod 600 "$replacement_root/.catalog.lock"
kill -CONT "$replacement_stopped_pid"
if wait "$replacement_publisher_pid"; then
    printf 'production publisher accepted a replaced shared catalog lock\n' >&2
    exit 1
fi
replacement_publisher_pid=
assert_current "$replacement_root" 1
[[ ! -e "$replacement_root/versions/hyg-v4.2-p3-s2-r16" ]]
[[ -n "$(find "$replacement_root/versions" -maxdepth 1 -name '.staging.*' -print -quit)" ]]
rm -f -- "$replacement_root/.catalog.lock"
mv -T -- "$replacement_root/.catalog.lock.original" "$replacement_root/.catalog.lock"
replacement_cleanup_marker="$TEMPORARY_DIRECTORY/replaced-production-cleanup.marker"
HVO_CATALOG_TEST_LOCK_BARRIER=production-staging-cleanup HVO_CATALOG_TEST_LOCK_MARKER="$replacement_cleanup_marker" \
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$replacement_bundle" "$replacement_root" >/dev/null 2>&1 &
replacement_publisher_pid=$!
while [[ ! -e "$replacement_cleanup_marker" ]]; do sleep 0.01; kill -0 "$replacement_publisher_pid"; done
replacement_stopped_pid="$(<"$replacement_cleanup_marker")"
mv -T -- "$replacement_root/.catalog.lock" "$replacement_root/.catalog.lock.original"
: > "$replacement_root/.catalog.lock"; chmod 600 "$replacement_root/.catalog.lock"
kill -CONT "$replacement_stopped_pid"
if wait "$replacement_publisher_pid"; then
    printf 'conditional production staging cleanup accepted a replaced shared catalog lock\n' >&2
    exit 1
fi
replacement_publisher_pid=
[[ ! -e "$replacement_root/versions/hyg-v4.2-p3-s2-r16" ]]
[[ -n "$(find "$replacement_root/versions" -maxdepth 1 -name '.staging.*' -print -quit)" ]]
rm -f -- "$replacement_root/.catalog.lock"
mv -T -- "$replacement_root/.catalog.lock.original" "$replacement_root/.catalog.lock"
"$SCRIPT_DIR/install-hyg-v42.sh" install "$replacement_bundle" "$replacement_root" >/dev/null
assert_current "$replacement_root" 16

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

# Every immutable production payload must remain privately linked; rejection must not mutate the outside link.
for hardlinked_file in "$HYG_MANIFEST_FILE" "$HYG_DATABASE_FILE" "$HYG_LICENSE_FILE" "$HYG_ATTRIBUTION_FILE"; do
    installed_file="$INSTALL_ROOT/versions/$HYG_PACKAGE_VERSION/$hardlinked_file"
    outside_link="$TEMPORARY_DIRECTORY/production-$(basename "$hardlinked_file").hardlink"
    ln "$installed_file" "$outside_link"
    outside_before="$(stat -c '%d:%i:%u:%h:%a:%s' "$outside_link")|$(hyg_sha256 "$outside_link")"
    if "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null 2>&1; then
        printf 'production installer accepted hardlinked payload: %s\n' "$hardlinked_file" >&2
        exit 1
    fi
    [[ "$(stat -c '%d:%i:%u:%h:%a:%s' "$outside_link")|$(hyg_sha256 "$outside_link")" == "$outside_before" ]] || {
        printf 'production installer mutated outside hardlink: %s\n' "$hardlinked_file" >&2
        exit 1
    }
    assert_current "$INSTALL_ROOT" 1
    rm "$outside_link"
done

# Recovery never changes external inodes while inspecting an untrusted orphan staging tree.
for hostile_staging in "$HYG_MANIFEST_FILE" "$HYG_DATABASE_FILE" "$HYG_LICENSE_FILE" "$HYG_ATTRIBUTION_FILE" \
    nested-hardlink nested-symlink nested-fifo; do
    staging="$INSTALL_ROOT/versions/.staging.hostile-$hostile_staging"
    mkdir -m 700 "$staging"
    mkdir -m 700 "$staging/nested"
    outside="$TEMPORARY_DIRECTORY/staging-outside-$hostile_staging"
    printf 'outside-preserved\n' > "$outside"
    chmod 0400 "$outside"
    case "$hostile_staging" in
        nested-hardlink) ln "$outside" "$staging/nested/hostile" ;;
        nested-symlink) ln -s "$outside" "$staging/nested/hostile" ;;
        nested-fifo) mkfifo "$staging/nested/hostile" ;;
        *) ln "$outside" "$staging/$hostile_staging" ;;
    esac
    outside_before="$(stat -c '%d:%i:%u:%h:%a:%s' "$outside")|$(hyg_sha256 "$outside")"
    if "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null 2>&1; then
        printf 'production installer accepted hostile orphan staging: %s\n' "$hostile_staging" >&2
        exit 1
    fi
    [[ -d "$staging" ]] || { printf 'production installer removed hostile orphan staging: %s\n' "$hostile_staging" >&2; exit 1; }
    [[ "$(stat -c '%d:%i:%u:%h:%a:%s' "$outside")|$(hyg_sha256 "$outside")" == "$outside_before" ]] || {
        printf 'production installer mutated an outside staging inode: %s\n' "$hostile_staging" >&2
        exit 1
    }
    assert_current "$INSTALL_ROOT" 1
    rm -rf -- "$staging"
    rm -f -- "$outside"
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

# A complete crash temporary is adopted under the root lock before the next requested operation.
valid_pointer_temporary="$INSTALL_ROOT/.pointer-transaction.tmp.123.456"
printf 'versions/hyg-v4.2-p3-s2-r1\nversions/hyg-v4.2-p3-s2-r14\n' > "$valid_pointer_temporary"
chmod 600 "$valid_pointer_temporary"
"$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null
assert_current "$INSTALL_ROOT" 1
[[ "$(readlink "$INSTALL_ROOT/previous")" == "versions/hyg-v4.2-p3-s2-r14" ]]
[[ ! -e "$valid_pointer_temporary" && ! -e "$INSTALL_ROOT/.pointer-transaction" ]]
"$SCRIPT_DIR/install-hyg-v42.sh" install "$upgrade" "$INSTALL_ROOT" >/dev/null
assert_current "$INSTALL_ROOT" 14

# Hostile or ambiguous crash temporaries are retained without changing external inodes or pointer state.
for hostile_temporary in symlink hardlink mode type malformed unsafe-target ambiguous; do
    temporary="$INSTALL_ROOT/.pointer-transaction.tmp.123.456"
    outside="$TEMPORARY_DIRECTORY/pointer-temporary-$hostile_temporary.outside"
    printf 'outside-preserved\n' > "$outside"
    chmod 600 "$outside"
    case "$hostile_temporary" in
        symlink) ln -s "$outside" "$temporary" ;;
        hardlink) ln "$outside" "$temporary" ;;
        mode) printf 'preserve\n' > "$temporary"; chmod 640 "$temporary" ;;
        type) mkdir -m 700 "$temporary" ;;
        malformed) printf 'versions/hyg-v4.2-p3-s2-r1\n' > "$temporary"; chmod 600 "$temporary" ;;
        unsafe-target) printf 'versions/missing\n-\n' > "$temporary"; chmod 600 "$temporary" ;;
        ambiguous)
            printf 'versions/hyg-v4.2-p3-s2-r1\nversions/hyg-v4.2-p3-s2-r14\n' > "$temporary"
            printf 'versions/hyg-v4.2-p3-s2-r14\nversions/hyg-v4.2-p3-s2-r1\n' > "$INSTALL_ROOT/.pointer-transaction.tmp.124.457"
            chmod 600 "$temporary" "$INSTALL_ROOT/.pointer-transaction.tmp.124.457"
            ;;
    esac
    outside_before="$(stat -c '%d:%i:%u:%h:%a:%s' "$outside")|$(hyg_sha256 "$outside")"
    if "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null 2>&1; then
        printf 'production installer accepted hostile pointer temporary: %s\n' "$hostile_temporary" >&2
        exit 1
    fi
    [[ -e "$temporary" || -L "$temporary" ]] || { printf 'hostile pointer temporary was removed: %s\n' "$hostile_temporary" >&2; exit 1; }
    [[ "$(stat -c '%d:%i:%u:%h:%a:%s' "$outside")|$(hyg_sha256 "$outside")" == "$outside_before" ]] || {
        printf 'production installer mutated external pointer temporary state: %s\n' "$hostile_temporary" >&2
        exit 1
    }
    assert_current "$INSTALL_ROOT" 14
    rm -rf -- "$temporary" "$INSTALL_ROOT/.pointer-transaction.tmp.124.457"
    rm -f -- "$outside"
done

# Pointer-symlink crash remnants are authenticated before mutation and hostile entries are retained.
for hostile_pointer in symlink hardlink type mode target; do
    transaction="$INSTALL_ROOT/.pointer-transaction"
    temporary="$INSTALL_ROOT/.current.tmp.123.456"
    printf 'versions/hyg-v4.2-p3-s2-r1\nversions/hyg-v4.2-p3-s2-r14\n' > "$transaction"
    chmod 600 "$transaction"
    case "$hostile_pointer" in
        symlink) ln -s ../outside "$temporary" ;;
        hardlink)
            ln -s versions/hyg-v4.2-p3-s2-r1 "$INSTALL_ROOT/.current.tmp.124.457"
            ln -P "$INSTALL_ROOT/.current.tmp.124.457" "$temporary"
            ;;
        type) mkdir -m 700 "$temporary" ;;
        mode) printf 'preserve\n' > "$temporary"; chmod 640 "$temporary" ;;
        target) ln -s versions/hyg-v4.2-p3-s2-r14 "$temporary" ;;
    esac
    if "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null 2>&1; then
        printf 'production installer accepted hostile pointer symlink temporary: %s\n' "$hostile_pointer" >&2
        exit 1
    fi
    [[ -e "$temporary" || -L "$temporary" ]] || exit 1
    assert_current "$INSTALL_ROOT" 14
    rm -rf -- "$temporary" "$INSTALL_ROOT/.current.tmp.124.457" "$transaction"
done

for hostile_transaction in symlink hardlink mode type malformed unsafe-target; do
    transaction="$INSTALL_ROOT/.pointer-transaction"
    outside="$TEMPORARY_DIRECTORY/production-transaction-$hostile_transaction"
    printf 'versions/hyg-v4.2-p3-s2-r1\nversions/hyg-v4.2-p3-s2-r14\n' > "$outside"
    chmod 0600 "$outside"
    case "$hostile_transaction" in
        symlink) ln -s "$outside" "$transaction" ;;
        hardlink) ln "$outside" "$transaction" ;;
        mode) cp "$outside" "$transaction"; chmod 0640 "$transaction" ;;
        type) mkdir -m 700 "$transaction" ;;
        malformed) printf 'versions/hyg-v4.2-p3-s2-r1\n' > "$transaction"; chmod 0600 "$transaction" ;;
        unsafe-target) printf 'versions/missing\n-\n' > "$transaction"; chmod 0600 "$transaction" ;;
    esac
    outside_before="$(stat -c '%d:%i:%u:%h:%a:%s' "$outside")|$(hyg_sha256 "$outside")"
    if "$SCRIPT_DIR/install-hyg-v42.sh" install "$SOURCE_BUNDLE" "$INSTALL_ROOT" >/dev/null 2>&1; then
        printf 'production installer accepted hostile pointer transaction: %s\n' "$hostile_transaction" >&2
        exit 1
    fi
    [[ -e "$transaction" || -L "$transaction" ]] || { printf 'hostile pointer transaction was removed: %s\n' "$hostile_transaction" >&2; exit 1; }
    [[ "$(stat -c '%d:%i:%u:%h:%a:%s' "$outside")|$(hyg_sha256 "$outside")" == "$outside_before" ]] || {
        printf 'production installer mutated external pointer transaction state: %s\n' "$hostile_transaction" >&2
        exit 1
    }
    assert_current "$INSTALL_ROOT" 14
    [[ "$(readlink "$INSTALL_ROOT/previous")" == "versions/hyg-v4.2-p3-s2-r1" ]]
    rm -rf -- "$transaction"; rm -f -- "$outside"
done

rollback_database="$INSTALL_ROOT/versions/$HYG_PACKAGE_VERSION/$HYG_DATABASE_FILE"
rollback_outside_link="$TEMPORARY_DIRECTORY/production-rollback-database.hardlink"
ln "$rollback_database" "$rollback_outside_link"
rollback_outside_before="$(stat -c '%d:%i:%u:%h:%a:%s' "$rollback_outside_link")|$(hyg_sha256 "$rollback_outside_link")"
if "$SCRIPT_DIR/rollback-hyg-v42.sh" "$INSTALL_ROOT" >/dev/null 2>&1; then
    printf 'production rollback accepted a hardlinked previous database\n' >&2
    exit 1
fi
[[ "$(stat -c '%d:%i:%u:%h:%a:%s' "$rollback_outside_link")|$(hyg_sha256 "$rollback_outside_link")" == "$rollback_outside_before" ]] || {
    printf 'production rollback mutated an outside database hardlink\n' >&2
    exit 1
}
assert_current "$INSTALL_ROOT" 14
[[ "$(readlink "$INSTALL_ROOT/previous")" == "versions/hyg-v4.2-p3-s2-r1" ]]
rm "$rollback_outside_link"

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
