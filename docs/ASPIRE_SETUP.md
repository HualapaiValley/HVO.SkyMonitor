# .NET Aspire Setup for HVO.SkyMonitor

## Overview

HVO.SkyMonitor uses **.NET Aspire** for orchestrating distributed services in both development and production environments. This document details the setup, configuration, and troubleshooting for running Aspire in a dev container environment.

## Architecture

### Service Topology

```
┌─────────────────────────────────────────────────────────────┐
│                    Aspire AppHost                           │
│                  (HVO.SkyMonitor.AppHost)                   │
└─────────────────────────────────────────────────────────────┘
                            │
        ┌───────────────────┼───────────────────┐
        │                   │                   │
┌───────▼─────┐    ┌────────▼────────┐   ┌─────▼──────┐
│   Redis     │    │   PostgreSQL    │   │   MinIO    │
│   (Cache)   │    │   (Database)    │   │  (Storage) │
└─────────────┘    └─────────────────┘   └────────────┘
                            │
                    ┌───────▼───────┐
                    │ HVO.SkyMonitor│
                    │ (Main App)    │
                    └───────┬───────┘
                            │
            ┌───────────────┴───────────────┐
            │                               │
    ┌───────▼────────┐            ┌────────▼────────┐
    │ Simulator Agent│            │   ZWO Agent     │
    │   (Camera)     │            │   (Camera)      │
    └────────────────┘            └─────────────────┘
```

### Startup Order

Services use `.WaitFor()` to establish dependency order:

1. **Infrastructure** (parallel): Redis, PostgreSQL, MinIO
2. **Main Application**: HVO.SkyMonitor (waits for infrastructure)
3. **Camera Agents**: Simulator and ZWO agents (wait for main app)

## Docker-in-Docker Configuration

### Why Docker-in-Docker?

Running Aspire inside a dev container requires the ability to spawn and manage additional Docker containers. The **Docker-in-Docker** feature provides an isolated Docker daemon inside the dev container.

### DevContainer Feature Configuration

In `.devcontainer/devcontainer.json`:

```jsonc
"features": {
  "ghcr.io/devcontainers/features/docker-in-docker:2": {
    "version": "latest",
    "moby": true,
    "dockerDashComposeVersion": "v2"
  }
}
```

**Parameters:**
- `version: "latest"` - Uses the latest Docker engine
- `moby: true` - Uses Moby engine (Docker's open-source components)
- `dockerDashComposeVersion: "v2"` - Installs Docker Compose V2

### Post-Create Setup

The `.devcontainer/post-create.sh` script runs after container creation:

```bash
# Add vscode user to docker group
sudo usermod -aG docker vscode

# Set docker socket permissions
sudo chmod 666 /var/run/docker.sock

# Install Aspire project templates
dotnet new install Aspire.ProjectTemplates

# Generate HTTPS developer certificate
dotnet dev-certs https --clean
dotnet dev-certs https
```

**What This Does:**

1. **Docker Group Access** - Adds `vscode` user to the `docker` group for container management
2. **Docker Socket Permissions** - Allows non-root access to Docker daemon (dev container only - never do this in production)
3. **Aspire Templates** - Installs .NET Aspire project templates for `dotnet new`
4. **HTTPS Developer Certificate** - Generates a self-signed certificate for local HTTPS development

**Important:** The docker socket permissions (`chmod 666`) allow the `vscode` user to access Docker without `sudo`. This is safe in a dev container environment but should **never** be done on a production system.

### HTTPS Certificates in Dev Containers

The post-create script automatically generates an HTTPS developer certificate using `dotnet dev-certs https`. However, **we recommend using the HTTP launch profile** because:

1. **Certificate Trust Issues** - Linux containers (including dev containers) don't automatically trust self-signed certificates
2. **Browser Warnings** - You'll see SSL warnings in browsers even with generated certificates
3. **Simplified Setup** - HTTP works immediately without additional configuration

**If you need HTTPS for testing:**

```bash
# Regenerate certificate
dotnet dev-certs https --clean
dotnet dev-certs https

# Try to trust (may not work in Linux containers)
dotnet dev-certs https --trust

# Use the HTTPS launch profile
dotnet run --launch-profile https
```

**Note:** Certificate trust (`--trust`) only works on Windows and macOS. In Linux dev containers, you'll need to manually add the certificate to your host browser's trust store or accept browser security warnings.

## AppHost Configuration

### Container Network Binding

For containers to be accessible from the host machine and between services, we bind to `0.0.0.0` using `WithContainerRuntimeArgs`:

```csharp
// Infrastructure containers use Session lifetime - stop when AppHost stops
var redis = builder.AddRedis("redis")
    .WithLifetime(ContainerLifetime.Session)
    .WithContainerRuntimeArgs("--publish", "0.0.0.0:6379:6379");

var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Session)
    .WithContainerRuntimeArgs("--publish", "0.0.0.0:5432:5432")
    .AddDatabase("skymonitordb");

var minio = builder.AddContainer("minio", "minio/minio", "latest")
    .WithHttpEndpoint(targetPort: 9000, name: "api")
    .WithHttpEndpoint(targetPort: 9001, name: "console")
    .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
    .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
    .WithVolume("minio-data", "/data")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithLifetime(ContainerLifetime.Session)
    .WithContainerRuntimeArgs("--publish", "0.0.0.0:9000:9000", "--publish", "0.0.0.0:9001:9001");
```

**Key Points:**

1. **`WithContainerRuntimeArgs("--publish", "0.0.0.0:PORT:PORT")`**
   - Binds container port to all interfaces (`0.0.0.0`)
   - Makes containers accessible from host machine through port forwarding
   - Essential for dev container environments where containers run inside another container

2. **`WithLifetime(ContainerLifetime.Session)`**
   - Containers stop and are removed when AppHost stops
   - Clean state on each run
   - Infrastructure data persists via Docker volumes (minio-data)
   - Changed from `Persistent` to ensure clean shutdown

3. **`WithVolume("minio-data", "/data")`**
   - Creates a named Docker volume for persistent storage
   - Data persists even with Session lifetime
   - Compatible with Docker-in-Docker (unlike bind mounts which can have path issues)

### Application Container Support

Applications can run as either projects (default) or containers (opt-in) controlled by the `USE_CONTAINERS` environment variable:

```csharp
// Check environment variable (defaults to true - containers by default)
var useContainersEnv = Environment.GetEnvironmentVariable("USE_CONTAINERS");
var useContainers = useContainersEnv?.ToLowerInvariant() != "false";

if (useContainers)
{
    // Run as containers - Aspire builds from Dockerfiles automatically
    var skymonitorContainer = builder.AddDockerfile("skymonitor", "../../", "src/HVO.SkyMonitor/Dockerfile")
        .WithHttpEndpoint(targetPort: 8080, name: "http")
        .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
        .WithEnvironment("ConnectionStrings__skymonitordb", postgres.Resource.ConnectionStringExpression)
        .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
        .WithEnvironment("MinIO__AccessKey", "minioadmin")
        .WithEnvironment("MinIO__SecretKey", "minioadmin")
        .WithLifetime(ContainerLifetime.Session)
        .WaitFor(redis)
        .WaitFor(postgres)
        .WaitFor(minio)
        .WithContainerRuntimeArgs("--publish", "0.0.0.0:5174:8080");
}
else
{
    // Run as projects - hot reload enabled, easier debugging
    var skymonitorProject = builder.AddProject<Projects.HVO_SkyMonitor>("skymonitor")
        .WithReference(redis)
        .WithReference(postgres)
        .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
        .WithEnvironment("MinIO__AccessKey", "minioadmin")
        .WithEnvironment("MinIO__SecretKey", "minioadmin")
        .WaitFor(redis)
        .WaitFor(postgres)
        .WaitFor(minio)
        .WithExternalHttpEndpoints();
}
```

**Key Points:**

1. **`AddDockerfile(name, context, dockerfile)`**
   - Aspire automatically builds the Docker image from the Dockerfile
   - Context path is relative to AppHost directory (../../ = repository root)
   - Rebuilds on code changes when AppHost restarts
   - No need for manual `docker build` commands during development

2. **`WithHttpEndpoint(targetPort: 8080, name: "http")`**
   - Do NOT include `port:` parameter - causes duplicate binding conflicts
   - Port mapping handled by `WithContainerRuntimeArgs`
   - Aspire needs endpoint definition for internal routing

3. **Container vs Project Mode:**
   - **Containers (default)**: Production-like environment, tests deployment
   - **Projects (`USE_CONTAINERS=false`)**: Hot reload, easier debugging
   - Toggle with environment variable for flexible development workflow

### Service Dependencies

Services reference infrastructure and wait for dependencies:

```csharp
var skymonitor = builder.AddProject<Projects.HVO_SkyMonitor>("skymonitor")
    .WithReference(redis)              // Injects Redis connection string
    .WithReference(postgres)           // Injects PostgreSQL connection string
    .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
    .WithEnvironment("MinIO__AccessKey", "minioadmin")
    .WithEnvironment("MinIO__SecretKey", "minioadmin")
    .WaitFor(redis)                    // Wait for Redis to be ready
    .WaitFor(postgres)                 // Wait for PostgreSQL to be ready
    .WaitFor(minio)                    // Wait for MinIO to be ready
    .WithExternalHttpEndpoints();      // Makes HTTP endpoints accessible

var simulatorAgent = builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_Simulator>("simulator-agent")
    .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
    .WaitFor(skymonitor)               // Wait for main app to be ready
    .WithExternalHttpEndpoints();
```

**`WithReference` vs `WithEnvironment`:**
- `WithReference`: Automatically injects connection strings for Aspire-managed resources (Redis, PostgreSQL)
- `WithEnvironment`: Manually set environment variables for custom resources (MinIO) or non-standard configurations

**`WaitFor` Behavior:**
- Ensures dependency is in "Running" state before starting dependent service
- Prevents connection errors from services starting too early
- Can be disabled in `appsettings.Development.json` for faster iteration (not recommended for production)

### Application Settings

#### `appsettings.Development.json`

```json
{
  "Dashboard": {
    "Frontend": {
      "AuthMode": "Unsecured"
    }
  },
  "Aspire": {
    "WaitForDependencies": {
      "Enabled": false
    },
    "Hosting": {
      "ContainerHostAddress": "0.0.0.0"
    }
  }
}
```

**Configuration Options:**

- **`Dashboard.Frontend.AuthMode: "Unsecured"`**
  - Disables authentication for Aspire Dashboard in development
  - Allows direct access to http://localhost:15201 without token
  - **IMPORTANT:** Should be removed or changed to `"BrowserToken"` in production

- **`Aspire.WaitForDependencies.Enabled: false`** (optional)
  - Disables all `WaitFor` constraints globally
  - Speeds up development by starting all services in parallel
  - May cause connection errors if services start before dependencies are ready
  - **Recommendation:** Keep this `false` only if you're comfortable handling startup race conditions

- **`Aspire.Hosting.ContainerHostAddress: "0.0.0.0"`**
  - Sets the default host address for all container bindings
  - Works in conjunction with `WithContainerRuntimeArgs` bindings
  - Ensures containers are accessible from outside the dev container

#### `appsettings.json`

```json
{
  "Dashboard": {
    "Frontend": {
      "EndpointUrls": "http://localhost:15201"
    },
    "Otlp": {
      "EndpointUrl": "http://localhost:18889"
    }
  }
}
```

Base configuration for the Aspire Dashboard endpoints.

### Launch Profiles

The `launchSettings.json` defines two profiles:

#### HTTP Profile (Recommended for Dev Containers)

```json
"http": {
  "commandName": "Project",
  "dotnetRunMessages": true,
  "launchBrowser": true,
  "applicationUrl": "http://localhost:15201",
  "environmentVariables": {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "DOTNET_ENVIRONMENT": "Development",
    "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true",
    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:19219",
    "ASPIRE_DASHBOARD_MCP_ENDPOINT_URL": "http://localhost:18270",
    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:20014",
    "DOCKER_HOST_ADDRESS": "0.0.0.0"
  }
}
```

**Usage:** `dotnet run --launch-profile http`

**Why HTTP?**
- Avoids HTTPS certificate issues in dev containers
- Linux containers don't trust Windows/macOS developer certificates by default
- `ASPIRE_ALLOW_UNSECURED_TRANSPORT: "true"` permits HTTP communication

#### HTTPS Profile

```json
"https": {
  "commandName": "Project",
  "dotnetRunMessages": true,
  "launchBrowser": true,
  "applicationUrl": "https://localhost:17068;http://localhost:15201",
  "environmentVariables": {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "DOTNET_ENVIRONMENT": "Development",
    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:21133",
    "ASPIRE_DASHBOARD_MCP_ENDPOINT_URL": "https://localhost:23217",
    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:22037"
  }
}
```

**Usage:** `dotnet run --launch-profile https`

Requires valid HTTPS developer certificate. Use only if certificates are properly configured in the dev container.

## Running the Application

### Starting the AppHost

**Container Mode (Default):**
```bash
cd src/HVO.SkyMonitor.AppHost
dotnet run --launch-profile http
```

Aspire automatically builds Docker images from Dockerfiles and runs applications in containers.

**Project Mode (for debugging):**
```bash
USE_CONTAINERS=false dotnet run --launch-profile http
```

Runs applications as .NET projects with hot reload enabled.

### Accessing Services

Once running, you can access:

- **Aspire Dashboard:** http://localhost:15201
- **MinIO Console:** http://localhost:9001 (minioadmin/minioadmin)
- **HVO.SkyMonitor:** http://localhost:5174 (port may vary - check dashboard)
- **Simulator Agent:** http://localhost:5130 (port may vary)
- **ZWO Agent:** http://localhost:5232 (port may vary)

### Stopping Services

Press `Ctrl+C` in the terminal running the AppHost. This will:
1. Stop the AppHost process
2. Stop all managed .NET projects (SkyMonitor, agents)
3. **Leave infrastructure containers running** (due to `ContainerLifetime.Persistent`)

To completely stop and remove all containers:

```bash
docker ps  # List running containers
docker stop <container-id>
docker rm <container-id>

# Or stop all:
docker stop $(docker ps -q)
```

## Troubleshooting

### HTTPS Certificate Errors

**Error:**
```
Unable to configure HTTPS endpoint. No server certificate was specified, and the default developer certificate could not be found or is out of date.
```

**Solution:** The post-create script automatically generates HTTPS certificates, but the **recommended approach is to use the HTTP launch profile**:

```bash
dotnet run --launch-profile http
```

This avoids certificate trust issues in dev containers.

**If you must use HTTPS**, regenerate the certificate:
```bash
dotnet dev-certs https --clean
dotnet dev-certs https
dotnet run --launch-profile https
```

**Note:** Certificate trust (`dotnet dev-certs https --trust`) only works on Windows and macOS, not in Linux dev containers. You'll need to accept browser security warnings or manually import the certificate into your host OS trust store.

### Docker Socket Permission Denied

**Error:**
```
permission denied while trying to connect to the Docker daemon socket
```

**Solution:** Ensure the post-create script ran successfully:
```bash
sudo chmod 666 /var/run/docker.sock
sudo usermod -aG docker vscode
```

Then reload the shell or restart the dev container.

### Containers Not Accessible from Host

**Symptom:** Services show as "Running" in the dashboard but are not accessible from the host machine.

**Solution:** Verify `WithContainerRuntimeArgs` binds to `0.0.0.0`:
```csharp
.WithContainerRuntimeArgs("--publish", "0.0.0.0:PORT:PORT")
```

Also check that ports are forwarded in `.devcontainer/devcontainer.json`:
```jsonc
"forwardPorts": [5000, 5001, 6379, 5432, 9000, 9001, 15201]
```

### Service Starts Before Dependencies

**Symptom:** Application logs show connection errors to Redis/PostgreSQL/MinIO on startup.

**Solution:** Ensure `.WaitFor()` is configured:
```csharp
var skymonitor = builder.AddProject<Projects.HVO_SkyMonitor>("skymonitor")
    .WaitFor(redis)
    .WaitFor(postgres)
    .WaitFor(minio);
```

If `Aspire.WaitForDependencies.Enabled: false` is set, remove it or set to `true`.

### Port Already in Use

**Error:**
```
Failed to bind to address http://0.0.0.0:15201: address already in use
```

**Solution:** A previous instance may still be running. Kill the process:
```bash
pkill -f "dotnet.*HVO.SkyMonitor.AppHost"
# or
lsof -ti:15201 | xargs kill -9
```

### MinIO Volume Permissions

**Symptom:** MinIO container fails to start with permission errors.

**Solution:** Named volumes should work automatically with Docker-in-Docker. If issues persist:
```bash
docker volume rm minio-data
docker volume create minio-data
```

Then restart the AppHost.

## Best Practices

### Development Workflow

1. **Start AppHost once per session:**
   ```bash
   dotnet run --launch-profile http
   ```
   Infrastructure containers persist between restarts.

2. **Use hot reload for application changes:**
   Aspire watches for changes and automatically restarts affected services.

3. **Monitor services in the Dashboard:**
   Open http://localhost:15201 to view logs, metrics, and traces.

4. **Clean up periodically:**
   ```bash
   docker system prune -af --volumes
   ```
   Removes stopped containers, unused images, and volumes.

### Resource Management

- **Container Lifetime:** Use `Persistent` for infrastructure, `Session` for applications
- **Named Volumes:** Use Docker volumes instead of bind mounts in dev containers
- **Port Allocation:** Use dynamic port allocation when possible (let Aspire assign ports)
- **Memory Limits:** Consider adding memory limits for containers in resource-constrained environments

### Security Considerations

- **Development Only:** The `0.0.0.0` bindings and unsecured dashboard are **for development only**
- **Production:** Use proper network isolation, secure authentication, and TLS
- **Secrets:** Never commit credentials; use Azure Key Vault or environment-specific configuration

## Additional Resources

- [.NET Aspire Documentation](https://learn.microsoft.com/dotnet/aspire/)
- [Docker-in-Docker Dev Container Feature](https://github.com/devcontainers/features/tree/main/src/docker-in-docker)
- [Dev Containers Specification](https://containers.dev/)
- [Aspire Dashboard Overview](https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard)
