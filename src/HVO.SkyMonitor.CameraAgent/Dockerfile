# Dockerfile for HVO.SkyMonitor.CameraAgent
# Multi-arch support: linux/amd64, linux/arm64, linux/arm/v7
# Build from repo root: docker build -t hvo-cameraagent:latest -f src/HVO.SkyMonitor.CameraAgent/Dockerfile .

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files needed for restore
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY .editorconfig ./
COPY HVO.SkyMonitor.v9.slnx ./
COPY THIRD-PARTY-NOTICES.md ./
COPY src/HVO.SkyMonitor.AgentCore/HVO.SkyMonitor.AgentCore.csproj src/HVO.SkyMonitor.AgentCore/
COPY src/HVO.SkyMonitor.Astronomy/HVO.SkyMonitor.Astronomy.csproj src/HVO.SkyMonitor.Astronomy/
COPY src/HVO.SkyMonitor.Catalog.Sqlite/HVO.SkyMonitor.Catalog.Sqlite.csproj src/HVO.SkyMonitor.Catalog.Sqlite/
COPY src/HVO.SkyMonitor.Imaging/HVO.SkyMonitor.Imaging.csproj src/HVO.SkyMonitor.Imaging/
COPY src/HVO.SkyMonitor.Common/HVO.SkyMonitor.Common.csproj src/HVO.SkyMonitor.Common/
COPY src/HVO.SkyMonitor.CameraAgent.Common/HVO.SkyMonitor.CameraAgent.Common.csproj src/HVO.SkyMonitor.CameraAgent.Common/
COPY src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj src/HVO.SkyMonitor.CameraAgent/

# Restore dependencies
RUN dotnet restore src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj

# Copy source code
COPY src/ src/

# Build and publish
WORKDIR /src/src/HVO.SkyMonitor.CameraAgent
RUN dotnet publish HVO.SkyMonitor.CameraAgent.csproj -c Release -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

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
