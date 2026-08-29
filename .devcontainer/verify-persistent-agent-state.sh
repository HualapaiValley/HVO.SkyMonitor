#!/usr/bin/env bash
# Refuse to run agents when resumable state would land on the container layer.

set -euo pipefail

readonly WORKTREE_ROOT="${HVO_AGENT_WORKTREE_ROOT:-/tmp/opencode}"
readonly AGENT_STATE_ROOT="${HVO_AGENT_STATE_ROOT:-/var/lib/hvo-agent-state}"
readonly OPENCODE_CONFIG_ROOT="${OPENCODE_CONFIG_DIR:-$HOME/.config/opencode}"
readonly OPENCODE_DATA_ROOT="${OPENCODE_DATA_DIR:-$HOME/.local/share/opencode}"
readonly PERSISTENT_STATE_SOURCE="${HVO_DEVCONTAINER_STATE_SOURCE:?HVO_DEVCONTAINER_STATE_SOURCE must identify the current checkout .devcontainer/state directory}"
EXPECTED_OWNER="$(id -u):$(id -g)"
readonly EXPECTED_OWNER

verify_dedicated_mount() {
    local path="$1"
    local label="$2"
    local expected_source="$3"
    local filesystem_type
    local mount_target
    local mount_source
    local root_mode
    local root_owner

    if [[ -L "$path" || ! -d "$path" ]]; then
        echo "$label is unavailable or is not a non-symlink directory: $path" >&2
        return 1
    fi
    root_owner="$(stat -c '%u:%g' -- "$path")"
    root_mode="$(stat -c '%a' -- "$path")"
    if [[ "$root_owner" != "$EXPECTED_OWNER" ]]; then
        echo "$label must be owned by $EXPECTED_OWNER, not $root_owner: $path" >&2
        return 1
    fi
    if [[ "$root_mode" != 700 ]]; then
        echo "$label must have mode 0700, not $root_mode: $path" >&2
        return 1
    fi
    if [[ ! -w "$path" ]]; then
        echo "$label is not writable: $path" >&2
        return 1
    fi
    if ! mount_target="$(findmnt --noheadings --raw --output TARGET --target "$path" 2>/dev/null)"; then
        echo "$label mount metadata is unavailable: $path" >&2
        return 1
    fi
    if [[ "$mount_target" != "$path" ]]; then
        echo "$label is not a dedicated persistent mount: $path (resolved mount: $mount_target)" >&2
        return 1
    fi
    if ! mount_source="$(findmnt --noheadings --raw --output SOURCE --target "$path" 2>/dev/null)" \
        || ! filesystem_type="$(findmnt --noheadings --raw --output FSTYPE --target "$path" 2>/dev/null)"; then
        echo "$label mount metadata is incomplete: $path" >&2
        return 1
    fi
    if [[ "$filesystem_type" == "tmpfs" || "$filesystem_type" == "overlay" ]]; then
        echo "$label uses ephemeral filesystem type '$filesystem_type': $path" >&2
        return 1
    fi
    case "$mount_source" in
        "$expected_source" | *"[$expected_source]") ;;
        *)
            echo "$label has unexpected mount source '$mount_source'; expected $expected_source" >&2
            return 1
            ;;
    esac
}

verify_dedicated_mount "$WORKTREE_ROOT" "Agent worktree root" "$PERSISTENT_STATE_SOURCE/opencode-worktrees"
verify_dedicated_mount "$AGENT_STATE_ROOT" "Agent state root" "$PERSISTENT_STATE_SOURCE/agent-scratch"
verify_dedicated_mount "$OPENCODE_CONFIG_ROOT" "OpenCode configuration root" "$PERSISTENT_STATE_SOURCE/opencode-config"
verify_dedicated_mount "$OPENCODE_DATA_ROOT" "OpenCode data root" "$PERSISTENT_STATE_SOURCE/opencode-data"

echo "Persistent agent state verified: worktrees=$WORKTREE_ROOT state=$AGENT_STATE_ROOT opencode=$OPENCODE_DATA_ROOT"
