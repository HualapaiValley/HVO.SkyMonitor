# Dockerfile for HVO.SkyMonitor.LogicHost
# Deployment platform: linux/amd64
# Build from repo root with a source-revision tag; deployment rejects mutable tags.

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src

# Copy solution and project files
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
COPY src/HVO.SkyMonitor.TestSupport/HVO.SkyMonitor.TestSupport.csproj src/HVO.SkyMonitor.TestSupport/
COPY src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj src/HVO.SkyMonitor.LogicHost/

# Restore dependencies
RUN dotnet restore src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj

# Copy source code
COPY src/ src/

# Build and publish
WORKDIR /src/src/HVO.SkyMonitor.LogicHost
RUN dotnet publish HVO.SkyMonitor.LogicHost.csproj -c Release -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .

# Set environment
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080

# Expose port
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Run application
ENTRYPOINT ["dotnet", "HVO.SkyMonitor.LogicHost.dll"]
