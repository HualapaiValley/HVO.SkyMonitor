#!/usr/bin/env bash
# Configure developer access to shared services whenever the devcontainer boots.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

source "$SCRIPT_DIR/load-repo-env.sh"

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

if command -v tailscaled >/dev/null 2>&1; then
    if pgrep -x tailscaled >/dev/null 2>&1; then
        echo "[post-start] Tailscale daemon is already running"
    else
        echo "[post-start] Starting Tailscale daemon..."
        sudo mkdir -p /var/lib/tailscale /var/run/tailscale
        sudo nohup tailscaled \
            --state=/var/lib/tailscale/tailscaled.state \
            --socket=/var/run/tailscale/tailscaled.sock \
            --tun=userspace-networking \
            >/tmp/tailscaled.log 2>&1 &
        sleep 1
    fi

    if pgrep -x tailscaled >/dev/null 2>&1; then
        echo "[post-start] Tailscale daemon ready"

        tailscale_state="$(tailscale status --json 2>/dev/null | jq -r '.BackendState' 2>/dev/null || true)"
        if [[ "$tailscale_state" != "Running" ]]; then
            if [[ -n "${TAILSCALE_AUTHKEY:-}" ]]; then
                echo "[post-start] Authenticating Tailscale with accepted routes..."
                if sudo tailscale up \
                    --auth-key="$TAILSCALE_AUTHKEY" \
                    --accept-routes \
                    --hostname=dev-host-skymonitor; then
                    echo "[post-start] Tailscale is connected as dev-host-skymonitor"
                else
                    echo "[post-start] Tailscale authentication failed; verify TAILSCALE_AUTHKEY." >&2
                fi
            else
                echo "[post-start] Tailscale needs login; set TAILSCALE_AUTHKEY in .env or devcontainer.local.env." >&2
            fi
        else
            echo "[post-start] Tailscale is already connected"
        fi
    else
        echo "[post-start] Failed to start the Tailscale daemon." >&2
    fi
fi
