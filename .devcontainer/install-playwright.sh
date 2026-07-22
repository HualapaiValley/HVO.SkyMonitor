#!/usr/bin/env bash
# Install the pinned browser used for local UI review and browser tests.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
readonly TOOL_ROOT="$SCRIPT_DIR/playwright"
readonly PLAYWRIGHT="$TOOL_ROOT/node_modules/.bin/playwright"

for required_command in node npm; do
	if ! command -v "$required_command" >/dev/null 2>&1; then
		echo "Playwright setup requires '$required_command'. Rebuild the devcontainer to install the Node.js feature." >&2
		exit 1
	fi
done

echo "Restoring package-lock-pinned Playwright tooling..."
npm ci --prefix "$TOOL_ROOT" --ignore-scripts --no-audit --no-fund

echo "Installing Playwright Chromium and native dependencies..."
"$PLAYWRIGHT" install --with-deps chromium
"$PLAYWRIGHT" --version
