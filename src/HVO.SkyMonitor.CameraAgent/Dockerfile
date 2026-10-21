# Dockerfile for HVO.SkyMonitor.CameraAgent
# Deployment platforms: linux/amd64 and linux/arm64
# Build from repo root with a source-revision tag; deployment rejects mutable tags.

# Base images. The digests are pinned on the dotnet-sdk and dotnet-aspnet FROM lines because Dependabot updates
# only FROM lines (#1321). The stages below select those aliases by default; the ARM64 advisory build passes its
# resolved native image references through these arguments instead.
ARG DOTNET_SDK_IMAGE=dotnet-sdk
ARG DOTNET_ASPNET_IMAGE=dotnet-aspnet
# Build identity. The release script passes the release's version, which Directory.Build.targets requires to be the
# product version or a pre-release of it (#1310); a build given none is the product's development build, X.Y.Z-dev, and
# carries an empty version label. A build without a source revision stays marked "unknown", which the installer rejects.
ARG HVO_VERSION=""
ARG HVO_SOURCE_REVISION=unknown
FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS dotnet-sdk
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS dotnet-aspnet

# Stage 1: Build
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
# Explicit release-image opt-in; Compose, advisory and developer builds publish IL by default.
ARG HVO_READY_TO_RUN=false
ARG HVO_CREATED
ARG TARGETARCH
COPY scripts/release:cameraagent-publish scripts/release:cameraagent-publish
COPY scripts/cameraagent-release.targets scripts/cameraagent-release.targets

# Copy solution and project files needed for restore
COPY Directory.Build.props ./
COPY Directory.Build.targets ./
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
RUN dotnet restore src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj
RUN ./scripts/release:cameraagent-publish restore

# Copy source code
COPY src/ src/

# Restore again after Razor sources are present so implicit framework assets are discovered.
RUN dotnet restore src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj
RUN ./scripts/release:cameraagent-publish restore

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
        "") ;; \
        [!0-9A-Za-z]*|*[!0-9A-Za-z]|*..*|*[!0-9A-Za-z.-]*) false ;; \
        *) [ "${#HVO_VERSION}" -le 128 ] ;; \
    esac \
    || { echo "HVO_VERSION must be empty or 1-128 ASCII letters, digits, periods, and hyphens." >&2; exit 64; } \
    && /src/scripts/release:cameraagent-publish publish -c Release -o /app/publish \
        ${HVO_VERSION:+"-p:Version=${HVO_VERSION}"} "-p:SourceRevisionId=${HVO_SOURCE_REVISION}" \
    && test "$(wc -c < /app/publish/wwwroot/_framework/blazor.web.js)" -gt 10000 \
    && grep -q 'window.Blazor' /app/publish/wwwroot/_framework/blazor.web.js
RUN case "${TARGETARCH}" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 64 ;; esac \
    && dotnet publish ../HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj \
        --no-restore -c Release -r "${rid}" -o /app/replay-runner \
        ${HVO_VERSION:+"-p:Version=${HVO_VERSION}"} "-p:SourceRevisionId=${HVO_SOURCE_REVISION}"

# Apply the runtime permissions here so the runtime stage copies them in. A recursive chmod after the copy forces
# an overlay copy-up of the entire payload, which rewrites every published file a second time.
RUN chmod -R a+rX /app/publish /app/replay-runner

# Export exactly the image's CameraAgent payload for clean native repeatability checks.
FROM scratch AS cameraagent-publish
COPY --from=build /app/publish /

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
#
# The state-contracts label is the image's state contract (install contract section 6), which
# release.json's stateContracts.cameraAgent repeats: for each SQLite store an instance keeps,
# the schema this image writes and the schemas it reads. CameraAgentStateContractsLabelTests
# keeps it equal to the schema each store records when this image creates it. The created
# label is the release's created time, which only a release build passes (scripts/release:assets).
ARG HVO_VERSION
ARG HVO_SOURCE_REVISION
ARG HVO_CREATED
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
      io.hvo.skymonitor.state-contracts="{\"identity\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1}},\"rawIngress\":{\"writes\":15,\"reads\":{\"min\":13,\"max\":15}},\"artifactOutbox\":{\"writes\":2,\"reads\":{\"min\":2,\"max\":2}},\"fleetStatus\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1}},\"environmentalOutbox\":{\"writes\":3,\"reads\":{\"min\":3,\"max\":3}},\"executionEvidenceOutbox\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1}},\"timeLapses\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1}},\"nightlyProducts\":{\"writes\":2,\"reads\":{\"min\":2,\"max\":2}},\"localAutomations\":{\"writes\":3,\"reads\":{\"min\":3,\"max\":3}},\"orbitalElements\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1}},\"spaceWeather\":{\"writes\":1,\"reads\":{\"min\":1,\"max\":1}}}" \
      org.opencontainers.image.version="${HVO_VERSION}" \
      org.opencontainers.image.revision="${HVO_SOURCE_REVISION}" \
      org.opencontainers.image.created="${HVO_CREATED}"

# URLS owns the HTTP binding; clear the runtime image's inherited port default.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_URLS=http://+:8080

# Expose port
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Run application
ENTRYPOINT ["dotnet", "HVO.SkyMonitor.CameraAgent.dll"]
