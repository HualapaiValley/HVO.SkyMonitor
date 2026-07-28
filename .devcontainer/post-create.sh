#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

LOG_ROOT="${POST_CREATE_LOG_ROOT:-$SCRIPT_DIR/logs}"
TIMESTAMP="$(date -u +"%Y%m%dT%H%M%SZ")"
mkdir -p "$LOG_ROOT"
LOG_FILE="$LOG_ROOT/post-create-$TIMESTAMP.log"
touch "$LOG_FILE"
chmod 664 "$LOG_FILE" 2>/dev/null || true
ln -sf "$LOG_FILE" "$LOG_ROOT/latest.log" 2>/dev/null || true

exec > >(tee -a "$LOG_FILE")
exec 2>&1

echo "Recording post-create output to $LOG_FILE"
echo

# Make ignored repository secrets available to this setup process.
# .devcontainer/devcontainer.local.env overrides the repository .env.
# shellcheck source=.devcontainer/load-repo-env.sh
source "$SCRIPT_DIR/load-repo-env.sh"
unset TAILSCALE_AUTHKEY

command_exists() {
	command -v "$1" >/dev/null 2>&1
}

log_section() {
	echo "================ $1 ================"
}

log_tool_version() {
	local label="$1"
	shift
	if command -v "$1" >/dev/null 2>&1; then
		echo "$label version:"
		"$@"
	else
		echo "Skipping $label version capture because '$1' is not available."
	fi
	echo
}

log_section "Environment snapshot"
echo "UTC Timestamp: $TIMESTAMP"
echo "Current user: $(whoami) (UID $(id -u))"
echo "Working directory: $(pwd)"
uname -a
if [ -f /etc/os-release ]; then
	cat /etc/os-release
fi
if git -C "$REPO_ROOT" rev-parse HEAD >/dev/null 2>&1; then
	echo "Git HEAD: $(git -C "$REPO_ROOT" rev-parse HEAD)"
	git -C "$REPO_ROOT" status --short
fi
echo

log_section "Tool versions (pre-setup)"
log_tool_version "dotnet" dotnet --info
log_tool_version "Docker" docker --version
log_tool_version "ripgrep" rg --version
log_tool_version "ShellCheck" shellcheck --version

echo "Running post-create setup..."

for required_command in jq rg shellcheck sqlite3; do
	if ! command_exists "$required_command"; then
		echo "Required development command '$required_command' is not installed." >&2
		exit 1
	fi
done

# Fix .dotnet directory ownership
echo "Fixing .dotnet directory ownership..."
sudo chown -R vscode:vscode /home/vscode/.dotnet || true
sudo chown -R vscode:vscode /home/vscode/.cache
sudo chown -R vscode:vscode /home/vscode/.config/opencode /home/vscode/.local/share/opencode
sudo chown -R vscode:vscode /tmp/opencode /var/lib/hvo-agent-state
chmod 700 /tmp/opencode /var/lib/hvo-agent-state
bash "$SCRIPT_DIR/verify-persistent-agent-state.sh"
bash "$SCRIPT_DIR/configure-opencode.sh"

# Display .NET version and runtime details
echo "Checking .NET installation..."
dotnet --info
echo "Installed SDKs:"
dotnet --list-sdks || true
echo "Installed runtimes:"
dotnet --list-runtimes || true

# Restore the exact EF Core and ReportGenerator versions pinned by the repository.
echo "Restoring pinned .NET tools..."
dotnet tool restore

echo "Restoring solution dependencies..."
dotnet restore HVO.SkyMonitor.v9.slnx

echo "Installing the repository-pinned Playwright Chromium revision..."
bash "$SCRIPT_DIR/install-playwright.sh"

# The Docker devcontainer feature owns socket permissions and group membership.
echo "Verifying Docker daemon access..."
if command_exists docker; then
	docker --version
	docker compose version
	docker info >/dev/null
else
	echo "Docker CLI not found on PATH." >&2
	exit 1
fi

log_section "SSH agent setup"
echo "Setting up SSH agent..."
if [ -z "${SSH_AUTH_SOCK:-}" ]; then
	echo "Starting new SSH agent..."
	eval "$(ssh-agent -s)"
else
	echo "Using existing SSH agent at $SSH_AUTH_SOCK"
fi

# SSH_PRIVATE_KEY is optional and must be injected from the host or ignored local env.
bash "$SCRIPT_DIR/setup-ssh-key.sh"

# Match the Website devcontainer behavior when these optional values are present.
if [[ -n "${GIT_AUTHOR_NAME:-}" && -n "${GIT_AUTHOR_EMAIL:-}" ]]; then
	git config --global user.name "$GIT_AUTHOR_NAME"
	git config --global user.email "$GIT_AUTHOR_EMAIL"
elif [[ -n "${GIT_AUTHOR_NAME:-}" || -n "${GIT_AUTHOR_EMAIL:-}" ]]; then
	echo "GIT_AUTHOR_NAME and GIT_AUTHOR_EMAIL must be provided together." >&2
	exit 1
else
	echo "Git author identity is not configured; commits will remain unavailable until it is supplied."
fi

github_token="${GH_TOKEN:-${GITHUB_TOKEN:-${GH_PAT:-}}}"
if [[ -n "$github_token" ]]; then
	if ! command_exists gh; then
		echo "A GitHub token was supplied, but gh is not installed." >&2
		exit 1
	fi
	(unset GITHUB_TOKEN GH_TOKEN; printf '%s' "$github_token" | gh auth login --hostname github.com --git-protocol https --with-token)
	gh auth setup-git
elif command_exists gh && gh auth status >/dev/null 2>&1; then
	gh auth setup-git
else
	echo "GitHub authentication is not configured; GitHub operations will remain unavailable until a token is supplied."
fi
unset github_token

# Generate HTTPS developer certificate
echo "Generating HTTPS developer certificate..."
dotnet dev-certs https --clean
dotnet dev-certs https

log_section "Post-create summary"
echo "Logs captured at: $LOG_FILE"
echo "Latest log symlink: $LOG_ROOT/latest.log"
echo "Pinned .NET tools:"
dotnet tool list --local
echo
log_tool_version "dotnet-ef" dotnet ef --version
log_tool_version "OpenCode" opencode --version
log_tool_version "Tailscale" tailscale version
log_tool_version "Docker" docker --version
log_tool_version "ShellCheck" shellcheck --version
log_tool_version "dotnet" dotnet --version

echo "Post-create setup completed successfully!"
