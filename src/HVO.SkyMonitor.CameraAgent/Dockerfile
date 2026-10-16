# Dockerfile for HVO.SkyMonitor.CameraAgent
# Deployment platforms: linux/amd64 and linux/arm64
# Build from repo root with a source-revision tag; deployment rejects mutable tags.

# Stage 1: Build
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c
# Build identity. Development builds keep the declared 0.0.0-dev version; the release script passes the release
# version. A build without a source revision stays marked "unknown", which the installer rejects.
ARG HVO_VERSION=0.0.0-dev
ARG HVO_SOURCE_REVISION=unknown
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

# Build and publish. Every published assembly carries the informational version <version>+<revision>; .dockerignore
# excludes .git, so the SDK cannot derive the revision itself. The identity is declared after restore and the source
# copy, so a change of revision alone reuses those layers but republishes, because the stamped assemblies are the
# payload.
WORKDIR /src/src/HVO.SkyMonitor.CameraAgent
ARG HVO_VERSION
ARG HVO_SOURCE_REVISION
RUN case "${HVO_SOURCE_REVISION}" in \
        unknown) ;; \
        *[!0-9a-f]*) false ;; \
        *) [ "${#HVO_SOURCE_REVISION}" -eq 40 ] ;; \
    esac \
    || { echo "HVO_SOURCE_REVISION must be a 40-character lowercase object ID or unknown." >&2; exit 64; } \
    && case "${HVO_VERSION}" in \
        ""|[!0-9A-Za-z]*|*[!0-9A-Za-z]|*..*|*[!0-9A-Za-z.-]*) false ;; \
        *) [ "${#HVO_VERSION}" -le 128 ] ;; \
    esac \
    || { echo "HVO_VERSION must be 1-128 ASCII letters, digits, periods, and hyphens." >&2; exit 64; } \
    && dotnet publish HVO.SkyMonitor.CameraAgent.csproj --no-restore -c Release -o /app/publish \
        "-p:InformationalVersion=${HVO_VERSION}" "-p:SourceRevisionId=${HVO_SOURCE_REVISION}" \
    && test "$(wc -c < /app/publish/wwwroot/_framework/blazor.web.js)" -gt 10000 \
    && grep -q 'window.Blazor' /app/publish/wwwroot/_framework/blazor.web.js
ARG TARGETARCH
RUN case "${TARGETARCH}" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 64 ;; esac \
    && dotnet publish ../HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj \
        --no-restore -c Release -r "${rid}" -o /app/replay-runner \
        "-p:InformationalVersion=${HVO_VERSION}" "-p:SourceRevisionId=${HVO_SOURCE_REVISION}"

# Apply the runtime permissions here so the runtime stage copies them in. A recursive chmod after the copy forces
# an overlay copy-up of the entire payload, which rewrites every published file a second time.
RUN chmod -R a+rX /app/publish /app/replay-runner

# Stage 2: Runtime
FROM ${DOTNET_ASPNET_IMAGE}
WORKDIR /app

# Install health-check and ASI SDK runtime dependencies. The proprietary SDK is mounted separately.
RUN apt-get update && apt-get install -y curl libusb-1.0-0 && rm -rf /var/lib/apt/lists/*

# Copy published application. The build stage already applied the runtime permissions.
COPY --from=build /app/publish .
COPY --from=build /app/replay-runner /app/replay-runner

# Declared after the payload so that the package install stays cached across revisions. The version label is set
# explicitly so it never inherits the base image's operating-system version.
#
# The state-compatibility label names the durable CameraAgent state contract this image
# reads and writes. It is not a generic backward-compatibility promise: revisions before
# minimum-compatible-revision produced catalog manifest v1, Identity migration
# 20251125021552_CreateLocalIdentity, and raw-ingress schema 11, which this image cannot
# adopt in place. Deployment preflight compares the three declared boundaries below against
# the persisted state before the container starts.
ARG HVO_VERSION
ARG HVO_SOURCE_REVISION
LABEL io.hvo.skymonitor.component="CameraAgent" \
      io.hvo.skymonitor.state-compatibility="cameraagent-state-v2" \
      io.hvo.skymonitor.minimum-compatible-revision="70ecdd3a0d02a5288aaa6438e3a5cfc8e395545f" \
      io.hvo.skymonitor.identity-migration="20260827053715_InitialIdentity" \
      io.hvo.skymonitor.raw-ingress-schema="15" \
      io.hvo.skymonitor.raw-ingress-migration="canonical-v13-v14-to-v15" \
      io.hvo.skymonitor.catalog-manifest-version="2" \
      io.hvo.skymonitor.configuration-contract="cameraagent-install-v1" \
      io.hvo.skymonitor.catalog-contract="hvo-approved-catalogs-v1" \
      io.hvo.skymonitor.replay-runner-contract="local-replay-runner-v1" \
      org.opencontainers.image.version="${HVO_VERSION}" \
      org.opencontainers.image.revision="${HVO_SOURCE_REVISION}"

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
