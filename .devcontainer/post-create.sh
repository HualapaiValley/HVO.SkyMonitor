#!/bin/bash
set -e

LOG_ROOT="${POST_CREATE_LOG_ROOT:-/workspaces/HVO.SkyMonitor/.devcontainer/logs}"
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
if git -C /workspaces/HVO.SkyMonitor rev-parse HEAD >/dev/null 2>&1; then
	echo "Git HEAD: $(git -C /workspaces/HVO.SkyMonitor rev-parse HEAD)"
	git -C /workspaces/HVO.SkyMonitor status --short
fi
echo

log_section "Tool versions (pre-setup)"
log_tool_version "dotnet" dotnet --info
log_tool_version "Docker" docker --version
log_tool_version "Python" python --version
log_tool_version "ripgrep" rg --version

echo "Running post-create setup..."

# Fix .dotnet directory ownership
echo "Fixing .dotnet directory ownership..."
sudo chown -R vscode:vscode /home/vscode/.dotnet || true

# Display .NET version and runtime details
echo "Checking .NET installation..."
dotnet --info

# Ensure handy CLI tools are available (ripgrep and python alias)
echo "Installing development CLI utilities..."
sudo apt-get update -y
sudo apt-get install -y ripgrep python-is-python3 xxd

log_section "Tool versions (post CLI install)"
log_tool_version "Python" python --version
log_tool_version "ripgrep" rg --version

# Install EF Core CLI matching the repo packages
EF_TOOLS_VERSION="10.0.0"
echo "Installing dotnet-ef $EF_TOOLS_VERSION..."
dotnet tool update --global dotnet-ef --version "$EF_TOOLS_VERSION" 2>/dev/null \
	|| dotnet tool install --global dotnet-ef --version "$EF_TOOLS_VERSION"

# Add vscode user to docker group
echo "Adding vscode user to docker group..."
sudo usermod -aG docker vscode

# Set docker socket permissions
echo "Setting docker socket permissions..."
sudo chmod 666 /var/run/docker.sock

# Verify docker is working
echo "Verifying Docker installation..."
docker --version

log_section "SSH agent setup"
SSH_AGENT_DIR="/tmp/devcontainer-ssh-agent"
mkdir -p "$SSH_AGENT_DIR"
eval "$(ssh-agent -s)"
ln -sf "$SSH_AUTH_SOCK" "$SSH_AGENT_DIR/agent.sock"

if compgen -G "/home/vscode/.ssh/id_*" >/dev/null 2>&1; then
	for key in /home/vscode/.ssh/id_*; do
		if [[ -f "$key" && "$key" != *.pub ]]; then
			if ssh-add "$key" >/dev/null 2>&1; then
				echo "Loaded SSH key: $key"
			else
				echo "Warning: Failed to load key $key"
			fi
		fi
	done
else
	echo "No default SSH keys found under /home/vscode/.ssh. Add keys manually with ssh-add if needed."
fi

log_section "Configuring Docker contexts"
ensure_docker_context() {
	local name="$1"
	local description="$2"
	local host="$3"
	if docker context inspect "$name" >/dev/null 2>&1; then
		echo "Context '$name' already present."
	else
		echo "Creating docker context '$name' (${description})"
		docker context create "$name" --description "$description" --docker "host=$host"
	fi
}

ensure_docker_context "proxmox-home" "Remote engine on Home Proxmox" "ssh://roys@192.168.2.104"
ensure_docker_context "rpi-home" "Remote engine on Home Raspberry Pi" "ssh://roys@192.168.2.21"

echo

# Generate HTTPS developer certificate
echo "Generating HTTPS developer certificate..."
dotnet dev-certs https --clean
dotnet dev-certs https

log_section "Post-create summary"
echo "Logs captured at: $LOG_FILE"
echo "Latest log symlink: $LOG_ROOT/latest.log"
log_tool_version "dotnet-ef" dotnet-ef --version
log_tool_version "Docker" docker --version
log_tool_version "dotnet" dotnet --version

echo "Post-create setup completed successfully!"
