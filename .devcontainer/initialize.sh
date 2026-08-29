#!/usr/bin/env bash
# Prepare ignored bind-mount sources before the devcontainer is created.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
state_root="$repo_root/.devcontainer/state"
initialization_root="$repo_root/.devcontainer/.state-initialization-v1"
expected_owner="$(id -u):$(id -g)"

umask 077

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
        echo "Persistent developer-state path must be owned by $expected_owner with mode 0700; found $actual_owner mode $actual_mode: $path" >&2
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

if [[ -e "$state_root" || -L "$state_root" ]]; then
    if [[ -e "$initialization_root" || -L "$initialization_root" ]]; then
        echo "Persistent developer state has both current and initialization roots; inspect without merging: $initialization_root" >&2
        exit 1
    fi
    verify_secure_state_directory "$state_root"
    for state_directory in "${state_directories[@]}"; do
        if [[ ! -e "$state_directory" && ! -L "$state_directory" ]]; then
            echo "Existing persistent developer state is incomplete; missing directory: $state_directory" >&2
            exit 1
        fi
        verify_secure_state_directory "$state_directory"
    done
else
    if [[ ! -e "$initialization_root" && ! -L "$initialization_root" ]]; then
        mkdir -m 0700 -- "$initialization_root"
    fi
    verify_secure_state_directory "$initialization_root"
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

    for state_directory in "${state_directories[@]}"; do
        initialization_directory="$initialization_root/${state_directory##*/}"
        if [[ ! -e "$initialization_directory" && ! -L "$initialization_directory" ]]; then
            mkdir -m 0700 -- "$initialization_directory"
        fi
        verify_secure_state_directory "$initialization_directory"
        if ! directory_is_empty "$initialization_directory"; then
            echo "Persistent developer-state initialization directory must be empty: $initialization_directory" >&2
            exit 1
        fi
        run_initialization_failpoint "after-${state_directory##*/}"
    done
    run_initialization_failpoint before-install
    mv -- "$initialization_root" "$state_root"
fi

mkdir -p "$HOME/.microsoft/usersecrets"

for secret_file in \
    "$repo_root/.env" \
    "$repo_root/.devcontainer/devcontainer.local.env"; do
    if [[ -f "$secret_file" ]]; then
        chmod 0600 "$secret_file"
    fi
done
