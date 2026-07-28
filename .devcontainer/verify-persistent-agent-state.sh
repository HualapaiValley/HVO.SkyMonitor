#!/usr/bin/env bash
# Refuse to run agents when resumable state would land on the container layer.

set -euo pipefail

readonly WORKTREE_ROOT="${HVO_AGENT_WORKTREE_ROOT:-/tmp/opencode}"
readonly AGENT_STATE_ROOT="${HVO_AGENT_STATE_ROOT:-/var/lib/hvo-agent-state}"
readonly OPENCODE_CONFIG_ROOT="${OPENCODE_CONFIG_DIR:-$HOME/.config/opencode}"
readonly OPENCODE_DATA_ROOT="${OPENCODE_DATA_DIR:-$HOME/.local/share/opencode}"

verify_dedicated_mount() {
    local path="$1"
    local label="$2"
    local expected_source_suffix="$3"
    local filesystem_type
    local mount_target
    local mount_source

    if [[ ! -d "$path" || ! -w "$path" ]]; then
        echo "$label is unavailable or not writable: $path" >&2
        return 1
    fi
    mount_target="$(findmnt --noheadings --raw --output TARGET --target "$path")"
    if [[ "$mount_target" != "$path" ]]; then
        echo "$label is not a dedicated persistent mount: $path (resolved mount: $mount_target)" >&2
        return 1
    fi
    mount_source="$(findmnt --noheadings --raw --output SOURCE --target "$path")"
    filesystem_type="$(findmnt --noheadings --raw --output FSTYPE --target "$path")"
    if [[ "$filesystem_type" == "tmpfs" || "$filesystem_type" == "overlay" ]]; then
        echo "$label uses ephemeral filesystem type '$filesystem_type': $path" >&2
        return 1
    fi
    if [[ "$mount_source" != *"$expected_source_suffix"* ]]; then
        echo "$label has unexpected mount source '$mount_source'; expected $expected_source_suffix" >&2
        return 1
    fi
}

verify_dedicated_mount "$WORKTREE_ROOT" "Agent worktree root" "/.devcontainer/state/opencode-worktrees"
verify_dedicated_mount "$AGENT_STATE_ROOT" "Agent state root" "/.devcontainer/state/agent-scratch"
verify_dedicated_mount "$OPENCODE_CONFIG_ROOT" "OpenCode configuration root" "/.devcontainer/state/opencode-config"
verify_dedicated_mount "$OPENCODE_DATA_ROOT" "OpenCode data root" "/.devcontainer/state/opencode-data"

echo "Persistent agent state verified: worktrees=$WORKTREE_ROOT state=$AGENT_STATE_ROOT opencode=$OPENCODE_DATA_ROOT"
