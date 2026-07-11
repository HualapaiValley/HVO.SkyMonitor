#!/usr/bin/env bash
# Automatically start the local infrastructure stack whenever the devcontainer boots

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

LOCAL_ENV_FILE="$SCRIPT_DIR/devcontainer.local.env"
if [[ -f "$LOCAL_ENV_FILE" ]]; then
    echo "[post-start] Loading developer overrides from $LOCAL_ENV_FILE"
    set -a
    # shellcheck disable=SC1090
    source "$LOCAL_ENV_FILE"
    set +a
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
    else
        echo "[post-start] Failed to start the Tailscale daemon." >&2
    fi
fi

if [[ "${SKYMONITOR_SKIP_AUTO_INFRA:-}" == "true" ]]; then
    echo "[post-start] SKYMONITOR_SKIP_AUTO_INFRA=true -> skipping infrastructure startup"
    exit 0
fi

echo "[post-start] Starting local infrastructure (postgres, redis, minio, smtp)..."
if ./scripts/infra:start postgres redis minio smtp >/tmp/skymonitor-post-start.log 2>&1; then
    echo "[post-start] Infrastructure ready"
else
    echo "[post-start] Failed to start infrastructure automatically. See /tmp/skymonitor-post-start.log for details." >&2
fi
