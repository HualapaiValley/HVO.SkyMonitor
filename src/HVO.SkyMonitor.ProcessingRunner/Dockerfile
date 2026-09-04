# Dockerfile for HVO.SkyMonitor.ProcessingRunner (processing-runner-v1 self-hosted runner)
# Deployment platform: linux/amd64 and linux/arm64 (pass --platform; the publish RID follows TARGETARCH).
# Build from repo root with a source-revision tag; deployment rejects mutable tags.

# Stage 1: Build
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY .editorconfig ./
COPY global.json ./
COPY HVO.SkyMonitor.v9.slnx ./
COPY THIRD-PARTY-NOTICES.md ./
COPY src/HVO.SkyMonitor.AgentCore/HVO.SkyMonitor.AgentCore.csproj src/HVO.SkyMonitor.AgentCore/
COPY src/HVO.SkyMonitor.Astronomy/HVO.SkyMonitor.Astronomy.csproj src/HVO.SkyMonitor.Astronomy/
COPY src/HVO.SkyMonitor.Imaging/HVO.SkyMonitor.Imaging.csproj src/HVO.SkyMonitor.Imaging/
COPY src/HVO.SkyMonitor.Processing/HVO.SkyMonitor.Processing.csproj src/HVO.SkyMonitor.Processing/
COPY src/HVO.SkyMonitor.ProcessingRunner.Contracts/HVO.SkyMonitor.ProcessingRunner.Contracts.csproj src/HVO.SkyMonitor.ProcessingRunner.Contracts/
COPY src/HVO.SkyMonitor.ProcessingRunner/HVO.SkyMonitor.ProcessingRunner.csproj src/HVO.SkyMonitor.ProcessingRunner/

RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported TARGETARCH $TARGETARCH" >&2; exit 1 ;; esac \
    && dotnet restore src/HVO.SkyMonitor.ProcessingRunner/HVO.SkyMonitor.ProcessingRunner.csproj --runtime "$rid"

COPY src/ src/

RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; esac \
    && dotnet publish src/HVO.SkyMonitor.ProcessingRunner/HVO.SkyMonitor.ProcessingRunner.csproj \
       -c Release --no-restore --runtime "$rid" --self-contained true -o /app/publish

# Stage 2: Runtime (self-contained publish; runtime-deps only)
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0@sha256:9b37bbaf06fc653cb0e757215081139fb493658e1f864a738f6a478620c9196f
WORKDIR /app
COPY --from=build /app/publish .

# The runner holds no database, object-store, or identity credentials beyond its own client secret.
# Provide HVO_RUNNER_LOGICHOST_URL, HVO_RUNNER_CLIENT_ID, and HVO_RUNNER_CLIENT_SECRET_FILE (or _SECRET).
ENV DOTNET_EnableDiagnostics=0 \
    HVO_RUNNER_MAX_CONCURRENCY=1

LABEL io.hvo.skymonitor.component="processing-runner" \
      io.hvo.skymonitor.processing-runner-contract="processing-runner-v1"

USER $APP_UID
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["/app/HVO.SkyMonitor.ProcessingRunner", "--probe"]

ENTRYPOINT ["/app/HVO.SkyMonitor.ProcessingRunner"]
