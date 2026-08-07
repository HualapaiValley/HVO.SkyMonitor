#!/usr/bin/env bash

deploy_fail() {
    local stage="$1" check="$2" reason="$3"
    printf 'stage=%s check=%s status=failed reason=%s\n' "$stage" "$check" "$reason" >&2
    return 1
}

deploy_status() {
    printf 'stage=%s check=%s status=%s reason=%s\n' "$1" "$2" "$3" "$4"
}

deploy_require_commands() {
    local name
    for name in "$@"; do
        command -v "$name" >/dev/null || deploy_fail validate tools "required-tool-unavailable" || return 1
    done
}

deploy_is_safe_name() {
    [[ "$1" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]]
}

deploy_normalize_docker_architecture() {
    case "$1" in
        amd64|x86_64) printf 'amd64\n' ;;
        arm64|aarch64) printf 'arm64\n' ;;
        *) return 1 ;;
    esac
}

deploy_is_safe_absolute_path() {
    local path="$1"
    [[ "$path" == /* && "$path" != / && "$path" != *//* && "$path" != */./* &&
       "$path" != */../* && "$path" != */. && "$path" != */.. ]]
}

deploy_validate_private_file() {
    local path="$1" mode owner links
    [[ -f "$path" && ! -L "$path" ]] || deploy_fail validate secret-source "unsafe-secret-source" || return 1
    if ! owner="$(stat -c '%u' -- "$path" 2>/dev/null)" ||
       ! mode="$(stat -c '%a' -- "$path" 2>/dev/null)" ||
       ! links="$(stat -c '%h' -- "$path" 2>/dev/null)"; then
        deploy_fail validate secret-source "secret-source-stat-failed"
        return 1
    fi
    [[ "$owner" == "$(id -u)" && "$links" == 1 && ( "$mode" == 600 || "$mode" == 400 ) ]] ||
        deploy_fail validate secret-source "secret-source-must-be-owner-only" || return 1
}

deploy_validate_output_root() {
    local path="$1" label="$2" current mode
    deploy_is_safe_absolute_path "$path" || deploy_fail validate "$label" "unsafe-output-root" || return 1
    [[ "$(realpath -m -- "$path" 2>/dev/null)" == "$path" ]] || deploy_fail validate "$label" "noncanonical-output-root" || return 1
    current="$path"
    while [[ ! -e "$current" && ! -L "$current" ]]; do current="$(dirname -- "$current")"; done
    [[ -d "$current" && ! -L "$current" && "$(stat -c '%u' -- "$current" 2>/dev/null)" == "$(id -u)" ]] ||
        deploy_fail validate "$label" "unsafe-output-ancestor" || return 1
    mode="$(stat -c '%a' -- "$current" 2>/dev/null)" || { deploy_fail validate "$label" "output-ancestor-stat-failed"; return 1; }
    (( (8#$mode & 0022) == 0 )) || deploy_fail validate "$label" "writable-output-ancestor" || return 1
    if [[ -e "$path" || -L "$path" ]]; then
        [[ -d "$path" && ! -L "$path" && "$(stat -c '%u' -- "$path" 2>/dev/null)" == "$(id -u)" &&
           "$(stat -c '%a' -- "$path" 2>/dev/null)" == 700 ]] || deploy_fail validate "$label" "output-root-must-be-owner-only" || return 1
    fi
}
