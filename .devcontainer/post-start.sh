#!/usr/bin/env bash
# Automatically start the local infrastructure stack whenever the devcontainer boots

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

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
