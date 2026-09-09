#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj"
OUTPUT="$REPO_ROOT/tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/bin/Release/net10.0"
# PLAYWRIGHT_BROWSERS_PATH is honoured because the devcontainer sets it
# (.devcontainer/devcontainer.json:81) and mounts that exact path (:142), so the browser
# cache survives rebuilds. The unset case must fall back to what the Playwright tooling
# itself uses, $HOME/.cache/ms-playwright, not to one container's layout: a hardcoded
# /home/vscode path is silently wrong on any host whose user is not `vscode`, installing
# browsers where nothing will look for them (issue #778).
CACHE_ROOT="${PLAYWRIGHT_BROWSERS_PATH:-${HOME}/.cache/ms-playwright}"

sudo mkdir -p "$CACHE_ROOT"
sudo chown "$(id -u):$(id -g)" "$CACHE_ROOT"

dotnet build "$PROJECT" --no-restore --configuration Release -warnaserror
PLAYWRIGHT_BROWSERS_PATH="$CACHE_ROOT" PLAYWRIGHT_DRIVER_SEARCH_PATH="$OUTPUT" dotnet exec \
    --runtimeconfig "$OUTPUT/HVO.SkyMonitor.CameraAgent.AcceptanceTests.runtimeconfig.json" \
    --depsfile "$OUTPUT/HVO.SkyMonitor.CameraAgent.AcceptanceTests.deps.json" \
    "$OUTPUT/Microsoft.Playwright.dll" install --with-deps chromium
