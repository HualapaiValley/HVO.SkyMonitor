#!/usr/bin/env bash
# Prepare host-side paths used by the standard devcontainer mounts.

set -euo pipefail

workspace_path="${1:-$(dirname "${BASH_SOURCE[0]}")/..}"
repo_root="$(cd "$workspace_path" && pwd)"

mkdir -p "$HOME/.microsoft/usersecrets"

for secret_file in \
    "$repo_root/.env" \
    "$repo_root/.devcontainer/devcontainer.local.env"; do
    if [[ -f "$secret_file" ]]; then
        chmod 0600 "$secret_file"
    fi
done
