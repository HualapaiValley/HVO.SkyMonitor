# Dockerfile for HVO.SkyMonitor
# Multi-arch support: linux/amd64, linux/arm64, linux/arm/v7
# Build from repo root: docker build -t hvo-skymonitor:latest -f src/HVO.SkyMonitor/Dockerfile .

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY HVO.SkyMonitor.v9.slnx .
COPY src/HVO.Common/HVO.Common.csproj src/HVO.Common/
COPY src/HVO.SkyMonitor.Common/HVO.SkyMonitor.Common.csproj src/HVO.SkyMonitor.Common/
COPY src/HVO.SkyMonitor/HVO.SkyMonitor.csproj src/HVO.SkyMonitor/
COPY src/HVO.SkyMonitor.ServiceDefaults/HVO.SkyMonitor.ServiceDefaults.csproj src/HVO.SkyMonitor.ServiceDefaults/

# Restore dependencies
RUN dotnet restore src/HVO.SkyMonitor/HVO.SkyMonitor.csproj

# Copy source code
COPY src/ src/

# Build and publish
WORKDIR /src/src/HVO.SkyMonitor
RUN dotnet publish HVO.SkyMonitor.csproj -c Release -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

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
ENTRYPOINT ["dotnet", "HVO.SkyMonitor.dll"]
