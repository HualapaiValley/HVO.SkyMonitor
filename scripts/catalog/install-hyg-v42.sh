#!/usr/bin/env bash
set -euo pipefail
umask 022

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/catalog/catalog-common.sh
source "$SCRIPT_DIR/catalog-common.sh"
# shellcheck source=scripts/infra:operation-lock
source "$SCRIPT_DIR/../infra:operation-lock"

usage() {
    cat >&2 <<USAGE
Usage:
  $0 install BUNDLE_DIRECTORY INSTALL_ROOT
  $0 rollback INSTALL_ROOT
USAGE
}

hyg_require_commands cmp find flock id mv readlink realpath sha256sum sqlite3 stat sync wc
hyg_check_sqlite_version

INSTALL_STAGING=""

catalog_lock_barrier() {
    hyg_catalog_revalidate_root_lock "$INSTALL_ROOT" "${1:-}" ||
        hyg_fail "catalog root lock descriptor no longer matches its pathname"
}

acquire_application_state_lock() {
    local requested_root="$1"
    local canonical_root
    local operation_runtime_root

    canonical_root="$(realpath -ms "$requested_root")"
    if [[ -n "${HVO_RUNTIME_DATA_ROOT:-}" ]]; then
        operation_runtime_root="$(realpath -ms "$HVO_RUNTIME_DATA_ROOT")"
        [[ "$canonical_root" == "$operation_runtime_root/catalog" ]] || \
            { hyg_fail "catalog install root must equal HVO_RUNTIME_DATA_ROOT/catalog"; return 1; }
    elif [[ "$(basename "$canonical_root")" == catalog ]]; then
        operation_runtime_root="$(dirname "$canonical_root")"
    else
        operation_runtime_root="$canonical_root/.catalog-operation-boundary"
    fi
    hvo_acquire_application_state_lock "$operation_runtime_root" || return 1
    hvo_require_restore_marker_access catalog || return 1
}

test_fail_at() {
    if [[ "${HVO_CATALOG_TEST_FAIL_AT:-}" == "$1" ]]; then
        hyg_fail "injected installer failure at boundary: $1"
    fi
}

cleanup_orphan_staging() {
    local path entry root_device mode identity paths_file scan_file
    local -A identities=()
    local -a entries directories
    local -a staging_paths
    shopt -s nullglob dotglob
    staging_paths=("$INSTALL_ROOT"/.staging.* "$INSTALL_ROOT/versions"/.staging.*)
    shopt -u nullglob dotglob
    for path in "${staging_paths[@]}"; do
        [[ -d "$path" && ! -L "$path" && "$(stat -c %u -- "$path")" == "$(id -u)" ]] || return 1
        root_device="$(stat -c %d -- "$path")"
        identity="$(stat -c '%d:%i:%f:%u:%g:%h:%a' -- "$path")" || return 1
        identities=(["$path"]="$identity")
        entries=("$path")
        directories=("$path")
        paths_file="$(mktemp)" || return 1
        scan_file="$(mktemp)" || { rm -f -- "$paths_file"; return 1; }
        find -P "$path" -xdev -mindepth 1 -print0 > "$paths_file" || {
            rm -f -- "$paths_file" "$scan_file"
            return 1
        }
        while IFS= read -r -d '' entry; do
            [[ "$(stat -c %d -- "$entry")" == "$root_device" ]] || return 1
            if [[ -d "$entry" && ! -L "$entry" ]]; then
                [[ "$(stat -c %u -- "$entry")" == "$(id -u)" ]] || return 1
                mode="$(stat -c %a -- "$entry")"
                (( (8#$mode & 0022) == 0 )) || return 1
                directories+=("$entry")
            elif [[ -f "$entry" && ! -L "$entry" ]]; then
                [[ "$(stat -c '%u:%h' -- "$entry")" == "$(id -u):1" ]] || return 1
            else
                return 1
            fi
            identity="$(stat -c '%d:%i:%f:%u:%g:%h:%a' -- "$entry")" || return 1
            identities["$entry"]="$identity"
            entries+=("$entry")
        done < "$paths_file"
        for entry in "${directories[@]}"; do
            catalog_lock_barrier production-staging-cleanup || return 1
            [[ "$(stat -c '%d:%i:%f:%u:%g:%h:%a' -- "$path")" == "${identities[$path]}" &&
               "$(stat -c '%d:%i:%f:%u:%g:%h:%a' -- "$entry")" == "${identities[$entry]}" ]] || return 1
            chmod u+w -- "$entry" || return 1
            identities["$entry"]="$(stat -c '%d:%i:%f:%u:%g:%h:%a' -- "$entry")" || return 1
            catalog_lock_barrier || return 1
        done
        catalog_lock_barrier || return 1
        find -P "$path" -xdev -print0 > "$scan_file" || return 1
        local -A seen=()
        while IFS= read -r -d '' entry; do
            [[ -n "${identities[$entry]+present}" &&
               "$(stat -c '%d:%i:%f:%u:%g:%h:%a' -- "$entry")" == "${identities[$entry]}" ]] || return 1
            seen["$entry"]=1
        done < "$scan_file"
        [[ ${#seen[@]} -eq ${#entries[@]} ]] || return 1
        for entry in "${entries[@]}"; do [[ -n "${seen[$entry]+present}" ]] || return 1; done
        rm -rf -- "$path" || return 1
        catalog_lock_barrier || return 1
        [[ ! -e "$path" && ! -L "$path" ]] || return 1
        rm -f -- "$paths_file" "$scan_file" || return 1
    done
}

read_pointer() {
    local pointer="$1"
    local target
    [[ -L "$INSTALL_ROOT/$pointer" ]] || { hyg_fail "$pointer pointer is missing or is not a symbolic link"; return 1; }
    target="$(readlink "$INSTALL_ROOT/$pointer")" || return 1
    [[ "$target" =~ ^versions/hyg-v4\.2-p3-s2-r[1-9][0-9]*$ ]] || { hyg_fail "$pointer pointer has an unsafe or incompatible target"; return 1; }
    [[ -d "$INSTALL_ROOT/$target" && ! -L "$INSTALL_ROOT/$target" ]] || { hyg_fail "$pointer pointer target is missing or unsafe"; return 1; }
    printf '%s\n' "$target"
}

replace_pointer() {
    local pointer="$1"
    local target="$2"
    local temporary="$INSTALL_ROOT/.${pointer}.tmp.$$.$RANDOM"
    catalog_lock_barrier || return 1
    ln -s -- "$target" "$temporary" || return 1
    catalog_lock_barrier || return 1
    test_fail_at "after-$pointer-pointer-temporary" || return 1
    if ! mv -Tf -- "$temporary" "$INSTALL_ROOT/$pointer"; then
        catalog_lock_barrier || return 1
        rm -f -- "$temporary" || return 1
        catalog_lock_barrier || return 1
        return 1
    fi
    sync -f "$INSTALL_ROOT" || return 1
    catalog_lock_barrier || return 1
}

validate_pointer_target() {
    local target="$1"
    [[ "$target" =~ ^versions/hyg-v4\.2-p3-s2-r[1-9][0-9]*$ ]] || { hyg_fail "pointer transaction contains an unsafe target"; return 1; }
    validate_installed_target "$target"
}

commit_pointer_state() {
    local current_target="$1"
    local previous_target="${2:--}"
    local temporary='' transaction_fd attempt

    validate_pointer_target "$current_target"
    if [[ "$previous_target" != "-" ]]; then
        validate_pointer_target "$previous_target"
    fi
    catalog_lock_barrier || return 1
    for attempt in {1..16}; do
        temporary="$INSTALL_ROOT/.pointer-transaction.tmp.$$.$RANDOM"
        if (set -o noclobber; umask 077; printf '%s\n%s\n' "$current_target" "$previous_target" > "$temporary") 2>/dev/null; then
            break
        fi
        temporary=''
    done
    [[ -n "$temporary" ]] || { hyg_fail "could not allocate an exclusive pointer transaction temporary"; return 1; }
    catalog_lock_barrier || return 1
    [[ -f "$temporary" && ! -L "$temporary" &&
       "$(stat -c '%u:%h:%a' -- "$temporary")" == "$(id -u):1:600" ]] ||
        { hyg_fail "pointer transaction temporary is unsafe"; return 1; }
    exec {transaction_fd}<>"$temporary" || { hyg_fail "pointer transaction temporary could not be pinned"; return 1; }
    [[ "$(stat -Lc '%d:%i:%u:%h:%a' -- "$temporary")" == \
       "$(stat -Lc '%d:%i:%u:%h:%a' -- "/proc/$BASHPID/fd/$transaction_fd")" ]] ||
        { hyg_fail "pointer transaction temporary changed while being pinned"; return 1; }
    sync -f "$temporary" || return 1
    [[ "$(stat -Lc '%d:%i:%u:%h:%a' -- "$temporary")" == \
       "$(stat -Lc '%d:%i:%u:%h:%a' -- "/proc/$BASHPID/fd/$transaction_fd")" ]] ||
        { hyg_fail "pointer transaction temporary changed before publication"; return 1; }
    exec {transaction_fd}>&-
    catalog_lock_barrier || return 1
    test_fail_at after-pointer-transaction-temp-fsync || return 1
    [[ ! -e "$INSTALL_ROOT/.pointer-transaction" && ! -L "$INSTALL_ROOT/.pointer-transaction" ]] ||
        { hyg_fail "pointer transaction already exists"; return 1; }
    catalog_lock_barrier || return 1
    mv -Tf -- "$temporary" "$INSTALL_ROOT/.pointer-transaction" || return 1
    sync -f "$INSTALL_ROOT" || return 1
    catalog_lock_barrier || return 1
    test_fail_at after-transaction || return 1

    if [[ "$previous_target" == "-" ]]; then
        catalog_lock_barrier || return 1
        rm -f -- "$INSTALL_ROOT/previous" || return 1
        sync -f "$INSTALL_ROOT" || return 1
        catalog_lock_barrier || return 1
    else
        replace_pointer previous "$previous_target" || return 1
    fi
    test_fail_at after-previous-pointer || return 1
    replace_pointer current "$current_target" || return 1
    test_fail_at after-current-pointer || return 1
    catalog_lock_barrier || return 1
    rm -f -- "$INSTALL_ROOT/.pointer-transaction" || return 1
    sync -f "$INSTALL_ROOT" || return 1
    catalog_lock_barrier || return 1
}

reconcile_pointer_transaction() {
    hyg_production_reconcile_pointer_temporaries "$INSTALL_ROOT" ||
        { hyg_fail "pointer transaction temporary is unsafe, malformed, or ambiguous"; return 1; }
    hyg_production_reconcile_pointer_symlink_temporaries "$INSTALL_ROOT" ||
        { hyg_fail "pointer symlink temporary is unsafe, malformed, or incompatible"; return 1; }
    hyg_production_reconcile_pointer_transaction "$INSTALL_ROOT" ||
        { hyg_fail "pointer transaction is unsafe, malformed, or incompatible"; return 1; }
}

validate_installed_target() {
    local target="$1"
    hyg_catalog_validate_installed_target "$INSTALL_ROOT" "$target" "$HYG_CATALOG_ID" production ||
        hyg_fail "installed production catalog target is unsafe or incompatible: $target"
}

prepare_root() {
    local requested_root="$1"
    local expected_catalog_id="$2"
    local expected_kind="$3"
    local current="/"
    local component
    local owner
    local mode
    local permissions
    local current_uid="$(id -u)"
    INSTALL_ROOT="$(realpath -ms "$requested_root")"
    [[ -n "$INSTALL_ROOT" && "$INSTALL_ROOT" != "/" ]] || { hyg_fail "refusing unsafe install root: $INSTALL_ROOT"; return 1; }
    IFS='/' read -r -a components <<< "${INSTALL_ROOT#/}"
    for component in "${components[@]}"; do
        current="$current$component"
        [[ ! -L "$current" ]] || { hyg_fail "install root cannot traverse a symbolic link: $current"; return 1; }
        if [[ -e "$current" ]]; then
            read -r owner mode < <(stat -c '%u %a' "$current")
            [[ "$owner" == "$current_uid" || "$owner" == "0" ]] || \
                { hyg_fail "install root ancestor is not trusted-owned: $current"; return 1; }
            permissions=$((10#$mode))
            if (( ((permissions / 10) % 10) & 2 || (permissions % 10) & 2 )); then
                (( owner == 0 && (permissions / 1000) & 1 )) || \
                    { hyg_fail "install root ancestor is writable by untrusted users: $current"; return 1; }
            fi
        fi
        current="$current/"
    done
    mkdir -p "$INSTALL_ROOT" || return 1
    [[ -d "$INSTALL_ROOT" && ! -L "$INSTALL_ROOT" ]] || { hyg_fail "install root is not a safe directory: $INSTALL_ROOT"; return 1; }
    [[ "$(stat -c '%u' "$INSTALL_ROOT")" == "$current_uid" ]] || { hyg_fail "install root must be owned by the installing user"; return 1; }
    chmod 0700 "$INSTALL_ROOT" || return 1
    hyg_catalog_acquire_root_lock "$INSTALL_ROOT" || { hyg_fail "catalog root lock is unsafe or unavailable"; return 1; }
    catalog_lock_barrier || return 1
    mkdir -p "$INSTALL_ROOT/versions" || return 1
    catalog_lock_barrier || return 1
    [[ -d "$INSTALL_ROOT/versions" && ! -L "$INSTALL_ROOT/versions" ]] || { hyg_fail "versions path is not a safe directory"; return 1; }
    catalog_lock_barrier || return 1
    chmod 0700 "$INSTALL_ROOT/versions" || return 1
    catalog_lock_barrier || return 1
    hyg_catalog_reconcile_lineage_temporaries "$INSTALL_ROOT" || { hyg_fail "catalog lineage temporary is unsafe"; return 1; }
    if [[ -e "$INSTALL_ROOT/.catalog-lineage.json" || -L "$INSTALL_ROOT/.catalog-lineage.json" ]]; then
        hyg_catalog_require_lineage "$INSTALL_ROOT" "$expected_catalog_id" "$expected_kind" \
            "$HYG_SCHEMA_VERSION" "$HYG_PREPROCESSING_VERSION" || { hyg_fail "catalog root lineage is missing or incompatible"; return 1; }
    fi
    reconcile_pointer_transaction
    cleanup_orphan_staging || { hyg_fail "orphan catalog staging is unsafe and was retained"; return 1; }
    hyg_catalog_require_lineage "$INSTALL_ROOT" "$expected_catalog_id" "$expected_kind" \
        "$HYG_SCHEMA_VERSION" "$HYG_PREPROCESSING_VERSION" || { hyg_fail "catalog root lineage is missing or incompatible"; return 1; }
}

install_bundle() {
    local bundle
    local package_version
    local requested_package_version
    local target
    local target_path
    local bundle_manifest_sha256
    local old_target=""
    local file

    acquire_application_state_lock "$2"
    bundle="$(realpath "$1")"
    hyg_validate_bundle "$bundle"
    requested_package_version="$HYG_MANIFEST_PACKAGE_VERSION"
    prepare_root "$2" "$HYG_CATALOG_ID" production

    # Recovery validates existing targets and must not replace the requested package identity.
    bundle_manifest_sha256="$(hyg_sha256 "$bundle/$HYG_MANIFEST_FILE")"
    package_version="$requested_package_version"
    target="versions/$package_version"
    target_path="$INSTALL_ROOT/$target"

    if [[ -e "$INSTALL_ROOT/current" || -L "$INSTALL_ROOT/current" ]]; then
        [[ -L "$INSTALL_ROOT/current" ]] || { hyg_fail "current pointer is not a symbolic link"; return 1; }
        if old_target="$(read_pointer current 2>/dev/null)" && validate_installed_target "$old_target" >/dev/null 2>&1; then
            :
        else
            printf 'Warning: active catalog is invalid and will not be retained as previous.\n' >&2
            old_target=""
        fi
    fi

    if [[ -e "$target_path" || -L "$target_path" ]]; then
        [[ -d "$target_path" && ! -L "$target_path" ]] || { hyg_fail "immutable version path is not a safe directory: $target_path"; return 1; }
        validate_installed_target "$target"
        if [[ "$(hyg_json_value "$target_path/$HYG_MANIFEST_FILE" '$.manifestVersion')" == 2 ]]; then
            [[ "$(hyg_sha256 "$target_path/$HYG_MANIFEST_FILE")" == "$bundle_manifest_sha256" ]] || \
                { hyg_fail "installed package version has different immutable bundle contents"; return 1; }
        fi
    else
        catalog_lock_barrier || return 1
        INSTALL_STAGING="$(mktemp -d "$INSTALL_ROOT/versions/.staging.XXXXXX")"
        catalog_lock_barrier || return 1
        cleanup_candidate() {
            local status=$?
            trap - EXIT
            if [[ -n "${INSTALL_STAGING:-}" ]] && hyg_catalog_revalidate_root_lock "$INSTALL_ROOT"; then
                chmod -R u+w "$INSTALL_STAGING" 2>/dev/null || true
                if hyg_catalog_revalidate_root_lock "$INSTALL_ROOT"; then
                    rm -rf -- "$INSTALL_STAGING"
                    hyg_catalog_revalidate_root_lock "$INSTALL_ROOT" || true
                fi
            fi
            exit "$status"
        }
        trap cleanup_candidate EXIT

        catalog_lock_barrier || return 1
        for file in "$HYG_MANIFEST_FILE" "$HYG_DATABASE_FILE" "$HYG_LICENSE_FILE" "$HYG_ATTRIBUTION_FILE"; do
            catalog_lock_barrier || return 1
            cp -- "$bundle/$file" "$INSTALL_STAGING/$file" || return 1
            catalog_lock_barrier || return 1
        done
        catalog_lock_barrier || return 1
        test_fail_at after-copy || return 1
        hyg_validate_bundle "$INSTALL_STAGING"
        test_fail_at after-candidate-validation || return 1
        catalog_lock_barrier || return 1
        chmod 0444 "$INSTALL_STAGING"/* || return 1
        catalog_lock_barrier || return 1
        chmod 0555 "$INSTALL_STAGING" || return 1
        sync -f "$INSTALL_STAGING/$HYG_MANIFEST_FILE" || return 1
        sync -f "$INSTALL_STAGING/$HYG_DATABASE_FILE" || return 1
        sync -f "$INSTALL_STAGING/$HYG_LICENSE_FILE" || return 1
        sync -f "$INSTALL_STAGING/$HYG_ATTRIBUTION_FILE" || return 1
        sync -f "$INSTALL_STAGING" || return 1
        catalog_lock_barrier || return 1
        test_fail_at after-staging-fsync || return 1
        catalog_lock_barrier production-version-publication || return 1
        mv -T -- "$INSTALL_STAGING" "$target_path" || return 1
        INSTALL_STAGING=""
        sync -f "$INSTALL_ROOT/versions" || return 1
        catalog_lock_barrier || return 1
        validate_installed_target "$target"
        test_fail_at after-publish || return 1
    fi

    if [[ "$old_target" == "$target" ]]; then
        catalog_lock_barrier || return 1
        trap - EXIT
        printf 'Catalog version is already active: %s\n' "$target_path"
        return 0
    fi
    test_fail_at before-activation || return 1
    commit_pointer_state "$target" "${old_target:--}"
    catalog_lock_barrier || return 1
    trap - EXIT

    printf 'Installed HYG production catalog: %s\n' "$target_path"
    printf 'Active database: %s/current/%s\n' "$INSTALL_ROOT" "$HYG_DATABASE_FILE"
    if [[ -n "$old_target" ]]; then
        printf 'Previous catalog retained: %s/%s\n' "$INSTALL_ROOT" "$old_target"
    fi
}

rollback_catalog() {
    local previous_target
    local current_target=""

    acquire_application_state_lock "$1"
    prepare_root "$1" "$HYG_CATALOG_ID" production
    previous_target="$(read_pointer previous)"
    validate_installed_target "$previous_target"

    if [[ -e "$INSTALL_ROOT/current" || -L "$INSTALL_ROOT/current" ]]; then
        current_target="$(read_pointer current)"
        validate_installed_target "$current_target"
    fi
    [[ "$previous_target" != "$current_target" ]] || { hyg_fail "previous catalog is already active"; return 1; }

    commit_pointer_state "$previous_target" "${current_target:--}"
    catalog_lock_barrier || return 1

    printf 'Rolled back active catalog to: %s/%s\n' "$INSTALL_ROOT" "$previous_target"
}

case "${1:-}" in
    install)
        [[ $# -eq 3 ]] || { usage; exit 2; }
        install_bundle "$2" "$3"
        ;;
    rollback)
        [[ $# -eq 2 ]] || { usage; exit 2; }
        rollback_catalog "$2"
        ;;
    --help|-h)
        usage
        ;;
    *)
        usage
        exit 2
        ;;
esac
