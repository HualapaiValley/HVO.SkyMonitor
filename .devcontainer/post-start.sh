#!/usr/bin/env bash
# Configure developer access to shared services whenever the devcontainer boots.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

# shellcheck source=.devcontainer/load-repo-env.sh
source "$SCRIPT_DIR/load-repo-env.sh"
unset TAILSCALE_AUTHKEY

bash "$SCRIPT_DIR/verify-persistent-agent-state.sh"

# Refresh the persisted key after local developer secrets have been loaded.
if ! bash "$SCRIPT_DIR/setup-ssh-key.sh"; then
    echo "[post-start] SSH key setup failed; continuing without a configured SSH key." >&2
fi

if command -v docker >/dev/null 2>&1; then
    declare -a docker_contexts=(
        "hvo-docker|HVO shared-services Docker host|ssh://roys@hvo-docker"
        "devpi5|HVO development Raspberry Pi Docker host|ssh://roys@devpi5"
    )

    for docker_context in "${docker_contexts[@]}"; do
        IFS='|' read -r context_name context_description context_endpoint <<< "$docker_context"
        if ! docker context inspect "$context_name" >/dev/null 2>&1; then
            echo "[post-start] Creating Docker context $context_name"
            docker context create "$context_name" \
                --description "$context_description" \
                --docker "host=$context_endpoint"
        fi
    done
fi

if ! "$REPO_ROOT/scripts/opencode:enable"; then
    echo "[post-start] OpenCode was not started; run scripts/opencode:enable for diagnostics." >&2
fi
