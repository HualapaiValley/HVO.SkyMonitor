#!/bin/bash
# Build container images locally
# This script creates a buildx builder and builds images for the current platform
# or multiple architectures (stored in buildx cache)
#
# Usage:
#   ./build-images.sh              # Build for current platform and load to docker
#   ./build-images.sh --multi-arch # Build for amd64, arm64, arm/v7 (buildx cache only)
#   PLATFORM=linux/arm64 ./build-images.sh  # Build specific platform

set -e

# Check if multi-arch mode is requested
MULTI_ARCH=false
if [[ "$1" == "--multi-arch" ]]; then
    MULTI_ARCH=true
    shift
fi

echo "Setting up Docker Buildx..."

# Create buildx builder if it doesn't exist
if ! docker buildx inspect hvo-builder &>/dev/null; then
    echo "Creating buildx builder 'hvo-builder'..."
    docker buildx create --use --name hvo-builder --driver docker-container
else
    echo "Using existing buildx builder 'hvo-builder'..."
    docker buildx use hvo-builder
fi

# Determine platforms and load strategy
if [ "$MULTI_ARCH" = true ]; then
    PLATFORMS="linux/amd64,linux/arm64,linux/arm/v7"
    LOAD_FLAG=""
    echo ""
    echo "⚙️  Building for multiple platforms: $PLATFORMS"
    echo "⚠️  Images will be stored in buildx cache (not loaded to docker)"
    echo "    To load a specific platform: PLATFORM=linux/arm64 ./build-images.sh"
else
    # Detect current platform or use specified platform
    if [ -z "$PLATFORM" ]; then
        ARCH=$(uname -m)
        case "$ARCH" in
            x86_64)
                PLATFORM="linux/amd64"
                ;;
            aarch64|arm64)
                PLATFORM="linux/arm64"
                ;;
            armv7l)
                PLATFORM="linux/arm/v7"
                ;;
            *)
                echo "⚠️  Unknown architecture: $ARCH, defaulting to linux/amd64"
                PLATFORM="linux/amd64"
                ;;
        esac
    fi
    PLATFORMS="$PLATFORM"
    LOAD_FLAG="--load"
    echo ""
    echo "⚙️  Building for platform: $PLATFORMS"
    echo "✅ Images will be loaded to local docker"
fi

# Build HVO.SkyMonitor
echo ""
echo "Building HVO.SkyMonitor..."
docker buildx build \
    --platform "$PLATFORMS" \
    $LOAD_FLAG \
    -t hvo-skymonitor:latest \
    -f src/HVO.SkyMonitor/Dockerfile \
    .

# Build Camera Agent Simulator
echo ""
echo "Building Camera Agent Simulator..."
docker buildx build \
    --platform "$PLATFORMS" \
    $LOAD_FLAG \
    -t hvo-cameraagent-simulator:latest \
    -f src/HVO.SkyMonitor.CameraAgent.Simulator/Dockerfile \
    .

# Build Camera Agent ZWO
echo ""
echo "Building Camera Agent ZWO..."
docker buildx build \
    --platform "$PLATFORMS" \
    $LOAD_FLAG \
    -t hvo-cameraagent-zwo:latest \
    -f src/HVO.SkyMonitor.CameraAgent.ZWO/Dockerfile \
    .

echo ""
echo "✅ All images built successfully!"

if [ "$MULTI_ARCH" = false ]; then
    echo ""
    echo "Built images:"
    docker images | grep -E "(hvo-skymonitor|hvo-cameraagent)" | head -3
fi

echo ""
echo "To run with containers, set USE_CONTAINERS=true:"
echo "  USE_CONTAINERS=true dotnet run --project src/HVO.SkyMonitor.AppHost"
echo ""
if [ "$MULTI_ARCH" = true ]; then
    echo "💡 Multi-arch images are in buildx cache. To load a specific platform:"
    echo "   PLATFORM=linux/arm64 ./build-images.sh"
    echo "   PLATFORM=linux/amd64 ./build-images.sh"
fi
