#!/usr/bin/env bash
# Prepare ignored bind-mount sources before the devcontainer is created.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
state_root="$repo_root/.devcontainer/state"

mkdir -p \
    "$HOME/.microsoft/usersecrets" \
    "$state_root/agent-scratch" \
    "$state_root/opencode-config" \
    "$state_root/opencode-data" \
    "$state_root/opencode-worktrees"

chmod 0700 \
    "$state_root" \
    "$state_root/agent-scratch" \
    "$state_root/opencode-config" \
    "$state_root/opencode-data" \
    "$state_root/opencode-worktrees"

for secret_file in \
    "$repo_root/.env" \
    "$repo_root/.devcontainer/devcontainer.local.env"; do
    if [[ -f "$secret_file" ]]; then
        chmod 0600 "$secret_file"
    fi
done
