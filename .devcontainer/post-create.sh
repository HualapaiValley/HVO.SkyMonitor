#!/bin/bash
set -e

echo "Running post-create setup..."

# Fix .dotnet directory ownership
echo "Fixing .dotnet directory ownership..."
sudo chown -R vscode:vscode /home/vscode/.dotnet || true

# Display .NET version
echo "Checking .NET version..."
dotnet --version

# Install Aspire project templates
echo "Installing Aspire project templates..."
dotnet new install Aspire.ProjectTemplates

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
