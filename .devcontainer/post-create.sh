#!/bin/bash
set -e

echo "Running post-create setup..."

# Fix .dotnet directory ownership
echo "Fixing .dotnet directory ownership..."
sudo chown -R vscode:vscode /home/vscode/.dotnet || true

# Display .NET version
echo "Checking .NET version..."
dotnet --version

# Ensure handy CLI tools are available (ripgrep and python alias)
echo "Installing development CLI utilities..."
sudo apt-get update -y
sudo apt-get install -y ripgrep python-is-python3

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

# Generate HTTPS developer certificate
echo "Generating HTTPS developer certificate..."
dotnet dev-certs https --clean
dotnet dev-certs https

echo "Post-create setup completed successfully!"
