# Dockerfile for HVO.SkyMonitor.CameraAgent
# Deployment platforms: linux/amd64 and linux/arm64
# Build from repo root with a source-revision tag; deployment rejects mutable tags.

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build
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
COPY src/HVO.SkyMonitor.CameraAgent.Modules.Zwo/HVO.SkyMonitor.CameraAgent.Modules.Zwo.csproj src/HVO.SkyMonitor.CameraAgent.Modules.Zwo/
COPY src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj src/HVO.SkyMonitor.CameraAgent/

# Restore dependencies
RUN dotnet restore src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj

# Copy source code
COPY src/ src/

# Build and publish
WORKDIR /src/src/HVO.SkyMonitor.CameraAgent
RUN dotnet publish HVO.SkyMonitor.CameraAgent.csproj --no-restore -c Release -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Install health-check and ASI SDK runtime dependencies. The proprietary SDK is mounted separately.
RUN apt-get update && apt-get install -y curl libusb-1.0-0 && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .
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
