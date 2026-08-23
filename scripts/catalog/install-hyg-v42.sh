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

hyg_require_commands flock id mv readlink realpath sha256sum sqlite3 stat sync wc
hyg_check_sqlite_version

INSTALL_STAGING=""

acquire_application_state_lock() {
    local requested_root="$1"
    local canonical_root
    local operation_runtime_root

    canonical_root="$(realpath -ms "$requested_root")"
    if [[ -n "${HVO_RUNTIME_DATA_ROOT:-}" ]]; then
        operation_runtime_root="$(realpath -ms "$HVO_RUNTIME_DATA_ROOT")"
        [[ "$canonical_root" == "$operation_runtime_root/catalog" ]] || \
            hyg_fail "catalog install root must equal HVO_RUNTIME_DATA_ROOT/catalog"
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
    local path
    local -a staging_paths
    shopt -s nullglob dotglob
    staging_paths=("$INSTALL_ROOT"/.staging.* "$INSTALL_ROOT/versions"/.staging.*)
    shopt -u nullglob dotglob
    for path in "${staging_paths[@]}"; do
        chmod -R u+w "$path" 2>/dev/null || true
        rm -rf -- "$path"
    done
}

read_pointer() {
    local pointer="$1"
    local target
    [[ -L "$INSTALL_ROOT/$pointer" ]] || hyg_fail "$pointer pointer is missing or is not a symbolic link"
    target="$(readlink "$INSTALL_ROOT/$pointer")"
    [[ "$target" =~ ^versions/hyg-v4\.2-p3-s2-r[1-9][0-9]*$ ]] || hyg_fail "$pointer pointer has an unsafe or incompatible target"
    [[ -d "$INSTALL_ROOT/$target" && ! -L "$INSTALL_ROOT/$target" ]] || hyg_fail "$pointer pointer target is missing or unsafe"
    printf '%s\n' "$target"
}

replace_pointer() {
    local pointer="$1"
    local target="$2"
    local temporary="$INSTALL_ROOT/.${pointer}.tmp.$$.$RANDOM"
    ln -s -- "$target" "$temporary"
    if ! mv -Tf -- "$temporary" "$INSTALL_ROOT/$pointer"; then
        rm -f -- "$temporary"
        return 1
    fi
    sync -f "$INSTALL_ROOT"
}

validate_pointer_target() {
    local target="$1"
    [[ "$target" =~ ^versions/hyg-v4\.2-p3-s2-r[1-9][0-9]*$ ]] || hyg_fail "pointer transaction contains an unsafe target"
    validate_installed_target "$target"
}

commit_pointer_state() {
    local current_target="$1"
    local previous_target="${2:--}"
    local temporary="$INSTALL_ROOT/.pointer-transaction.tmp.$$.$RANDOM"

    validate_pointer_target "$current_target"
    if [[ "$previous_target" != "-" ]]; then
        validate_pointer_target "$previous_target"
    fi
    printf '%s\n%s\n' "$current_target" "$previous_target" > "$temporary"
    chmod 0600 "$temporary"
    sync -f "$temporary"
    mv -Tf -- "$temporary" "$INSTALL_ROOT/.pointer-transaction"
    sync -f "$INSTALL_ROOT"
    test_fail_at after-transaction

    if [[ "$previous_target" == "-" ]]; then
        rm -f -- "$INSTALL_ROOT/previous"
        sync -f "$INSTALL_ROOT"
    else
        replace_pointer previous "$previous_target"
    fi
    test_fail_at after-previous-pointer
    replace_pointer current "$current_target"
    test_fail_at after-current-pointer
    rm -f -- "$INSTALL_ROOT/.pointer-transaction"
    sync -f "$INSTALL_ROOT"
}

reconcile_pointer_transaction() {
    local transaction="$INSTALL_ROOT/.pointer-transaction"
    local -a targets
    [[ -e "$transaction" || -L "$transaction" ]] || return 0
    [[ -f "$transaction" && ! -L "$transaction" ]] || hyg_fail "pointer transaction is not a safe regular file"
    mapfile -t targets < "$transaction"
    [[ ${#targets[@]} -eq 2 ]] || hyg_fail "pointer transaction is malformed"
    commit_pointer_state "${targets[0]}" "${targets[1]}"
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
    [[ -n "$INSTALL_ROOT" && "$INSTALL_ROOT" != "/" ]] || hyg_fail "refusing unsafe install root: $INSTALL_ROOT"
    IFS='/' read -r -a components <<< "${INSTALL_ROOT#/}"
    for component in "${components[@]}"; do
        current="$current$component"
        [[ ! -L "$current" ]] || hyg_fail "install root cannot traverse a symbolic link: $current"
        if [[ -e "$current" ]]; then
            read -r owner mode < <(stat -c '%u %a' "$current")
            [[ "$owner" == "$current_uid" || "$owner" == "0" ]] || \
                hyg_fail "install root ancestor is not trusted-owned: $current"
            permissions=$((10#$mode))
            if (( ((permissions / 10) % 10) & 2 || (permissions % 10) & 2 )); then
                (( owner == 0 && (permissions / 1000) & 1 )) || \
                    hyg_fail "install root ancestor is writable by untrusted users: $current"
            fi
        fi
        current="$current/"
    done
    mkdir -p "$INSTALL_ROOT"
    [[ -d "$INSTALL_ROOT" && ! -L "$INSTALL_ROOT" ]] || hyg_fail "install root is not a safe directory: $INSTALL_ROOT"
    [[ "$(stat -c '%u' "$INSTALL_ROOT")" == "$current_uid" ]] || hyg_fail "install root must be owned by the installing user"
    chmod 0700 "$INSTALL_ROOT"
    mkdir -p "$INSTALL_ROOT/versions"
    [[ -d "$INSTALL_ROOT/versions" && ! -L "$INSTALL_ROOT/versions" ]] || hyg_fail "versions path is not a safe directory"
    chmod 0700 "$INSTALL_ROOT/versions"
    hyg_catalog_acquire_root_lock "$INSTALL_ROOT" || hyg_fail "catalog root lock is unsafe or unavailable"
    hyg_catalog_reconcile_lineage_temporaries "$INSTALL_ROOT" || hyg_fail "catalog lineage temporary is unsafe"
    if [[ -e "$INSTALL_ROOT/.catalog-lineage.json" || -L "$INSTALL_ROOT/.catalog-lineage.json" ]]; then
        hyg_catalog_require_lineage "$INSTALL_ROOT" "$expected_catalog_id" "$expected_kind" \
            "$HYG_SCHEMA_VERSION" "$HYG_PREPROCESSING_VERSION" || hyg_fail "catalog root lineage is missing or incompatible"
    fi
    reconcile_pointer_transaction
    cleanup_orphan_staging
    hyg_catalog_require_lineage "$INSTALL_ROOT" "$expected_catalog_id" "$expected_kind" \
        "$HYG_SCHEMA_VERSION" "$HYG_PREPROCESSING_VERSION" || hyg_fail "catalog root lineage is missing or incompatible"
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
        [[ -L "$INSTALL_ROOT/current" ]] || hyg_fail "current pointer is not a symbolic link"
        if old_target="$(read_pointer current 2>/dev/null)" && validate_installed_target "$old_target" >/dev/null 2>&1; then
            :
        else
            printf 'Warning: active catalog is invalid and will not be retained as previous.\n' >&2
            old_target=""
        fi
    fi

    if [[ -e "$target_path" || -L "$target_path" ]]; then
        [[ -d "$target_path" && ! -L "$target_path" ]] || hyg_fail "immutable version path is not a safe directory: $target_path"
        validate_installed_target "$target"
        if [[ "$(hyg_json_value "$target_path/$HYG_MANIFEST_FILE" '$.manifestVersion')" == 2 ]]; then
            [[ "$(hyg_sha256 "$target_path/$HYG_MANIFEST_FILE")" == "$bundle_manifest_sha256" ]] || \
                hyg_fail "installed package version has different immutable bundle contents"
        fi
    else
        INSTALL_STAGING="$(mktemp -d "$INSTALL_ROOT/versions/.staging.XXXXXX")"
        cleanup_candidate() {
            local status=$?
            trap - EXIT
            if [[ -n "${INSTALL_STAGING:-}" ]]; then
                chmod -R u+w "$INSTALL_STAGING" 2>/dev/null || true
                rm -rf -- "$INSTALL_STAGING"
            fi
            rm -f -- "$INSTALL_ROOT"/.current.tmp.$$.* "$INSTALL_ROOT"/.previous.tmp.$$.*
            exit "$status"
        }
        trap cleanup_candidate EXIT

        for file in "$HYG_MANIFEST_FILE" "$HYG_DATABASE_FILE" "$HYG_LICENSE_FILE" "$HYG_ATTRIBUTION_FILE"; do
            cp -- "$bundle/$file" "$INSTALL_STAGING/$file"
        done
        test_fail_at after-copy
        hyg_validate_bundle "$INSTALL_STAGING"
        test_fail_at after-candidate-validation
        chmod 0444 "$INSTALL_STAGING"/*
        chmod 0555 "$INSTALL_STAGING"
        sync -f "$INSTALL_STAGING/$HYG_MANIFEST_FILE"
        sync -f "$INSTALL_STAGING/$HYG_DATABASE_FILE"
        sync -f "$INSTALL_STAGING/$HYG_LICENSE_FILE"
        sync -f "$INSTALL_STAGING/$HYG_ATTRIBUTION_FILE"
        sync -f "$INSTALL_STAGING"
        test_fail_at after-staging-fsync
        mv -T -- "$INSTALL_STAGING" "$target_path"
        INSTALL_STAGING=""
        sync -f "$INSTALL_ROOT/versions"
        validate_installed_target "$target"
        test_fail_at after-publish
    fi

    if [[ "$old_target" == "$target" ]]; then
        trap - EXIT
        printf 'Catalog version is already active: %s\n' "$target_path"
        return 0
    fi
    test_fail_at before-activation
    commit_pointer_state "$target" "${old_target:--}"
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
    [[ "$previous_target" != "$current_target" ]] || hyg_fail "previous catalog is already active"

    commit_pointer_state "$previous_target" "${current_target:--}"

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
