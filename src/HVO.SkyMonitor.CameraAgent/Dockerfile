# Dockerfile for HVO.SkyMonitor.CameraAgent
# Deployment platforms: linux/amd64 and linux/arm64
# Build from repo root with a source-revision tag; deployment rejects mutable tags.

# Stage 1: Build
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

# Copy solution and project files needed for restore
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY .editorconfig ./
COPY global.json ./
COPY HVO.SkyMonitor.v9.slnx ./
COPY THIRD-PARTY-NOTICES.md ./
COPY src/HVO.SkyMonitor.AgentCore/HVO.SkyMonitor.AgentCore.csproj src/HVO.SkyMonitor.AgentCore/
COPY src/HVO.SkyMonitor.Astronomy/HVO.SkyMonitor.Astronomy.csproj src/HVO.SkyMonitor.Astronomy/
COPY src/HVO.SkyMonitor.Catalog.Sqlite/HVO.SkyMonitor.Catalog.Sqlite.csproj src/HVO.SkyMonitor.Catalog.Sqlite/
COPY src/HVO.SkyMonitor.Fleet.Contracts/HVO.SkyMonitor.Fleet.Contracts.csproj src/HVO.SkyMonitor.Fleet.Contracts/
COPY src/HVO.SkyMonitor.Imaging/HVO.SkyMonitor.Imaging.csproj src/HVO.SkyMonitor.Imaging/
COPY src/HVO.SkyMonitor.Processing/HVO.SkyMonitor.Processing.csproj src/HVO.SkyMonitor.Processing/
COPY src/HVO.SkyMonitor.Common/HVO.SkyMonitor.Common.csproj src/HVO.SkyMonitor.Common/
COPY src/HVO.SkyMonitor.CameraAgent.Common/HVO.SkyMonitor.CameraAgent.Common.csproj src/HVO.SkyMonitor.CameraAgent.Common/
COPY src/HVO.SkyMonitor.CameraAgent.Replay/HVO.SkyMonitor.CameraAgent.Replay.csproj src/HVO.SkyMonitor.CameraAgent.Replay/
COPY src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj src/HVO.SkyMonitor.CameraAgent.ReplayRunner/
COPY src/HVO.SkyMonitor.CameraAgent.Modules.Zwo/HVO.SkyMonitor.CameraAgent.Modules.Zwo.csproj src/HVO.SkyMonitor.CameraAgent.Modules.Zwo/
COPY src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj src/HVO.SkyMonitor.CameraAgent/

# Restore project dependencies into a source-independent cache layer.
RUN dotnet restore src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
RUN dotnet restore src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj

# Copy source code
COPY src/ src/

# Restore again after Razor sources are present so implicit framework assets are discovered.
RUN dotnet restore src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
RUN dotnet restore src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj

# Build and publish
WORKDIR /src/src/HVO.SkyMonitor.CameraAgent
RUN dotnet publish HVO.SkyMonitor.CameraAgent.csproj --no-restore -c Release -o /app/publish \
    && test "$(wc -c < /app/publish/wwwroot/_framework/blazor.web.js)" -gt 10000 \
    && grep -q 'window.Blazor' /app/publish/wwwroot/_framework/blazor.web.js
ARG TARGETARCH
RUN case "${TARGETARCH}" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 64 ;; esac \
    && dotnet publish ../HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj \
        --no-restore -c Release -r "${rid}" -o /app/replay-runner

# Stage 2: Runtime
FROM ${DOTNET_ASPNET_IMAGE}
ARG HVO_SOURCE_REVISION=unknown
WORKDIR /app

LABEL io.hvo.skymonitor.component="CameraAgent" \
      io.hvo.skymonitor.state-compatibility="backward-compatible" \
      io.hvo.skymonitor.configuration-contract="cameraagent-install-v1" \
      io.hvo.skymonitor.catalog-contract="hyg-v42-production-p3-s2" \
      io.hvo.skymonitor.replay-runner-contract="local-replay-runner-v1" \
      org.opencontainers.image.revision="${HVO_SOURCE_REVISION}"

# Install health-check and ASI SDK runtime dependencies. The proprietary SDK is mounted separately.
RUN apt-get update && apt-get install -y curl libusb-1.0-0 && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .
COPY --from=build /app/replay-runner /app/replay-runner
RUN chmod -R a+rX /app

# Set environment
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080

# Expose port
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Run application
ENTRYPOINT ["dotnet", "HVO.SkyMonitor.CameraAgent.dll"]
