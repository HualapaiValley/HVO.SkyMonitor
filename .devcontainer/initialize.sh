#!/usr/bin/env bash
# Prepare ignored bind-mount sources before the devcontainer is created.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -L)"
physical_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
state_root="$repo_root/.devcontainer/state"
initialization_root="$repo_root/.devcontainer/.state-initialization-v1"
expected_owner="$(id -u):$(id -g)"

umask 077

if [[ "$(id -u)" == 0 \
    && "${HVO_DEVCONTAINER_TEST_ALLOW_ROOT_INITIALIZATION:-}" != true ]]; then
    echo "Devcontainer initialization must run as the non-root host user who will own persistent developer state." >&2
    exit 1
fi
if [[ "$repo_root" != "$physical_repo_root" ]]; then
    echo "Open the physical checkout path instead of a symlink before creating the devcontainer: $repo_root -> $physical_repo_root" >&2
    exit 1
fi

stat_owner() {
    if stat -c '%u:%g' -- "$1" >/dev/null 2>&1; then
        stat -c '%u:%g' -- "$1"
    else
        stat -f '%u:%g' -- "$1"
    fi
}

stat_mode() {
    if stat -c '%a' -- "$1" >/dev/null 2>&1; then
        stat -c '%a' -- "$1"
    else
        stat -f '%Lp' -- "$1"
    fi
}

verify_secure_state_directory() {
    local path="$1"
    local actual_mode
    local actual_owner

    if [[ -L "$path" || ! -d "$path" ]]; then
        echo "Persistent developer-state path must be a non-symlink directory: $path" >&2
        return 1
    fi
    actual_owner="$(stat_owner "$path")"
    actual_mode="$(stat_mode "$path")"
    if [[ "$actual_owner" != "$expected_owner" || "$actual_mode" != 700 ]]; then
        echo "Persistent developer-state path must be owned by $expected_owner with mode 0700; found $actual_owner mode $actual_mode: $path. Use a POSIX-permission filesystem; Windows hosts must clone under WSL2 rather than NTFS." >&2
        return 1
    fi
}

directory_is_empty() (
    local -a entries
    shopt -s dotglob nullglob
    entries=("$1"/*)
    ((${#entries[@]} == 0))
)

run_initialization_failpoint() {
    if [[ "${HVO_DEVCONTAINER_INIT_FAILPOINT:-}" == "$1" ]]; then
        kill -KILL "$$"
    fi
}

state_directories=(
    "$state_root/agent-scratch"
    "$state_root/opencode-config"
    "$state_root/opencode-data"
    "$state_root/opencode-worktrees"
)

discard_empty_initialization_root() {
    local staged_root="$1"
    local entry
    local -a entries

    for _ in {1..100}; do
        [[ -e "$staged_root" || -L "$staged_root" ]] || return 0
        if ! verify_secure_state_directory "$staged_root"; then
            [[ ! -e "$staged_root" && ! -L "$staged_root" ]] && return 0
            return 1
        fi
        shopt -s dotglob nullglob
        entries=("$staged_root"/*)
        shopt -u dotglob nullglob

        # Validate the complete staged tree before removing any part of it.
        for entry in "${entries[@]}"; do
            case "${entry##*/}" in
                agent-scratch | opencode-config | opencode-data | opencode-worktrees) ;;
                *)
                    echo "Concurrent developer-state initialization contains an unexpected entry: $entry" >&2
                    return 1
                    ;;
            esac
            if [[ -e "$entry" || -L "$entry" ]]; then
                if ! verify_secure_state_directory "$entry"; then
                    [[ ! -e "$entry" && ! -L "$entry" ]] && continue
                    return 1
                fi
                if ! directory_is_empty "$entry"; then
                    echo "Concurrent developer-state initialization directory must be empty: $entry" >&2
                    return 1
                fi
            fi
        done

        for entry in "${entries[@]}"; do
            rmdir -- "$entry" 2>/dev/null || true
        done
        rmdir -- "$staged_root" 2>/dev/null || true
        [[ ! -e "$staged_root" && ! -L "$staged_root" ]] && return 0
        sleep 0.01
    done
    echo "Concurrent developer-state initialization did not quiesce: $staged_root" >&2
    return 1
}

verify_installed_state() {
    local entry
    local nested_initialization_root="$state_root/.state-initialization-v1"
    local -a entries

    if [[ -e "$initialization_root" || -L "$initialization_root" ]]; then
        discard_empty_initialization_root "$initialization_root"
    fi
    verify_secure_state_directory "$state_root"
    if [[ -e "$nested_initialization_root" || -L "$nested_initialization_root" ]]; then
        discard_empty_initialization_root "$nested_initialization_root"
    fi
    shopt -s dotglob nullglob
    entries=("$state_root"/*)
    shopt -u dotglob nullglob
    for entry in "${entries[@]}"; do
        case "${entry##*/}" in
            agent-scratch | opencode-config | opencode-data | opencode-worktrees) ;;
            *)
                echo "Persistent developer state contains an unexpected entry: $entry" >&2
                return 1
                ;;
        esac
    done
    for state_directory in "${state_directories[@]}"; do
        if [[ ! -e "$state_directory" && ! -L "$state_directory" ]]; then
            echo "Existing persistent developer state is incomplete; missing directory: $state_directory" >&2
            return 1
        fi
        verify_secure_state_directory "$state_directory"
    done
}

if [[ -e "$state_root" || -L "$state_root" ]]; then
    verify_installed_state
else
    if [[ ! -e "$initialization_root" && ! -L "$initialization_root" ]]; then
        if ! mkdir -m 0700 -- "$initialization_root" 2>/dev/null \
            && [[ ! -d "$initialization_root" ]]; then
            if [[ -d "$state_root" ]]; then
                verify_installed_state
            else
                echo "Persistent developer-state initialization root could not be created: $initialization_root" >&2
                exit 1
            fi
        fi
    fi
    if [[ ! -d "$state_root" ]]; then
        if ! verify_secure_state_directory "$initialization_root"; then
            if [[ -d "$state_root" ]]; then
                verify_installed_state
            else
                exit 1
            fi
        fi
    fi
    if [[ ! -d "$state_root" ]]; then
        run_initialization_failpoint after-root

        shopt -s dotglob nullglob
        initialization_entries=("$initialization_root"/*)
        shopt -u dotglob nullglob
        for initialization_entry in "${initialization_entries[@]}"; do
            case "${initialization_entry##*/}" in
                agent-scratch | opencode-config | opencode-data | opencode-worktrees) ;;
                *)
                    echo "Persistent developer-state initialization contains an unexpected entry: $initialization_entry" >&2
                    exit 1
                    ;;
            esac
        done

        peer_installed=false
        for state_directory in "${state_directories[@]}"; do
            if [[ -d "$state_root" ]]; then
                peer_installed=true
                break
            fi
            initialization_directory="$initialization_root/${state_directory##*/}"
            if [[ ! -e "$initialization_directory" && ! -L "$initialization_directory" ]]; then
                if ! mkdir -m 0700 -- "$initialization_directory" 2>/dev/null \
                    && [[ ! -d "$initialization_directory" ]]; then
                    if [[ -d "$state_root" ]]; then
                        peer_installed=true
                        break
                    fi
                    echo "Persistent developer-state initialization directory could not be created: $initialization_directory" >&2
                    exit 1
                fi
            fi
            if [[ -d "$state_root" ]]; then
                peer_installed=true
                break
            fi
            if ! verify_secure_state_directory "$initialization_directory"; then
                if [[ -d "$state_root" ]]; then
                    peer_installed=true
                    break
                fi
                exit 1
            fi
            if ! directory_is_empty "$initialization_directory"; then
                echo "Persistent developer-state initialization directory must be empty: $initialization_directory" >&2
                exit 1
            fi
            run_initialization_failpoint "after-${state_directory##*/}"
        done

        if [[ "$peer_installed" == false ]]; then
            run_initialization_failpoint before-install
            if ! mv -- "$initialization_root" "$state_root" 2>/dev/null; then
                if [[ ! -d "$state_root" || -e "$initialization_root" || -L "$initialization_root" ]]; then
                    echo "Persistent developer-state initialization could not install the staged tree." >&2
                    exit 1
                fi
            fi
        fi
    fi
    verify_installed_state
fi

mkdir -p "$HOME/.microsoft/usersecrets"

for secret_file in \
    "$repo_root/.env" \
    "$repo_root/.devcontainer/devcontainer.local.env"; do
    if [[ -f "$secret_file" ]]; then
        chmod 0600 "$secret_file"
    fi
done
