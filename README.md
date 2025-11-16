# HVO.SkyMonitor

Sky monitoring application built with .NET 10, featuring distributed architecture for camera control, data processing, and real-time visualization.

## Development Environment

This repository is configured to work with Visual Studio Code Dev Containers and GitHub Codespaces, with **Docker-in-Docker** support for running containers and infrastructure services.

### Prerequisites

- [Visual Studio Code](https://code.visualstudio.com/)
- [Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)
- [Docker Desktop](https://www.docker.com/products/docker-desktop) (for local development)

### Getting Started

#### Using Dev Containers (Local)

1. Clone this repository
2. Open the folder in Visual Studio Code
3. When prompted, click "Reopen in Container" (or use Command Palette: `Dev Containers: Reopen in Container`)
4. Wait for the container to build and start
5. The development environment will be ready with .NET 10 SDK and all necessary tools

#### Using GitHub Codespaces

1. Navigate to the repository on GitHub
2. Click the "Code" button and select "Codespaces"
3. Click "Create codespace on main" (or your desired branch)
4. Wait for the Codespace to initialize
5. Your development environment is ready to use

### Running the Application

The application can be run using **Docker Compose** (recommended) or **.NET Aspire** (legacy).

#### Option 1: Docker Compose (Recommended)

Use the infrastructure management scripts to start services:

**Start all infrastructure and application services:**
```bash
./scripts/infra:start
```

**Start only infrastructure services (for running app in IDE):**
```bash
./scripts/infra:start postgres redis minio smtp
```

**Reset data and start:**
```bash
./scripts/infra:start --reset all
```

**Check service status:**
```bash
./scripts/infra:status
```

**Services Started:**
- **PostgreSQL** - tcp://localhost:5432 with `skymonitordb` database
- **Redis** - tcp://localhost:6379
- **MinIO** - API: http://localhost:9000, Console: http://localhost:9001 (minioadmin/minioadmin)
- **SMTP (MailHog)** - SMTP: tcp://localhost:1025, Web UI: http://localhost:8025
- **HVO.SkyMonitor** - Main application: http://localhost:5174
- **Camera Agents** - Simulator: http://localhost:5130, ZWO: http://localhost:5232

See `docs/projects/infra-and-testing-vNext.md` for detailed Docker Compose workflow documentation.

#### Option 2: .NET Aspire (Legacy)

The application was originally built with **.NET Aspire** for orchestration. This approach is being phased out in favor of Docker Compose.

**Default - Container Mode:**
```bash
dotnet run --project src/HVO.SkyMonitor.AppHost --launch-profile http
```

Applications run in containers built automatically by Aspire from Dockerfiles.

**Project Mode - For Debugging:**
```bash
USE_CONTAINERS=false dotnet run --project src/HVO.SkyMonitor.AppHost --launch-profile http
```

Applications run as .NET projects with hot reload enabled for faster development iteration.

**Services Started:**
- **Aspire Dashboard** - http://localhost:15201 (no authentication required in development)
- **Redis** - tcp://localhost:6379
- **PostgreSQL** - tcp://localhost:5432 with `skymonitordb` database
- **MinIO** - API: http://localhost:9000, Console: http://localhost:9001 (minioadmin/minioadmin)
- **HVO.SkyMonitor** - Main application (port 5174 in container mode)
- **Camera Agents** - Simulator (5130) and ZWO (5232) agents

Services start in dependency order using `WaitFor` constraints to ensure proper initialization. All containers stop automatically when you press Ctrl+C.

### Docker-in-Docker Architecture

This project uses **Docker-in-Docker** to run container orchestration inside the dev container.

#### DevContainer Configuration

The `.devcontainer/devcontainer.json` includes:

```jsonc
"features": {
  "ghcr.io/devcontainers/features/docker-in-docker:2": {
    "version": "latest",
    "moby": true,
    "dockerDashComposeVersion": "v2"
  }
}
```

**Post-create script** (`.devcontainer/post-create.sh`):
- Adds `vscode` user to the `docker` group
- Sets Docker socket permissions (`chmod 666 /var/run/docker.sock`)
- Installs Aspire project templates
- Generates HTTPS developer certificate (`dotnet dev-certs https`)
- Verifies Docker installation

**Note:** We use the HTTP launch profile by default to avoid certificate trust issues in Linux containers. See `docs/ASPIRE_SETUP.md` for HTTPS configuration details.

#### AppHost Container Accessibility

To make containers accessible from the host and between services in a dev container environment:

**`WithContainerRuntimeArgs`** binds container ports to `0.0.0.0`:

```csharp
// Infrastructure containers with Session lifetime
var redis = builder.AddRedis("redis")
    .WithLifetime(ContainerLifetime.Session)
    .WithContainerRuntimeArgs("--publish", "0.0.0.0:6379:6379");

var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Session)
    .WithContainerRuntimeArgs("--publish", "0.0.0.0:5432:5432");

var minio = builder.AddContainer("minio", "minio/minio", "latest")
    .WithLifetime(ContainerLifetime.Session)
    .WithContainerRuntimeArgs("--publish", "0.0.0.0:9000:9000", "--publish", "0.0.0.0:9001:9001");

// Application containers built from Dockerfiles
var skymonitor = builder.AddDockerfile("skymonitor", "../../", "src/HVO.SkyMonitor/Dockerfile")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithContainerRuntimeArgs("--publish", "0.0.0.0:5174:8080");
```

This ensures:
- Containers are accessible from the host machine through forwarded ports
- Services can communicate with each other using localhost within the dev container
- The Aspire dashboard can properly monitor and manage all resources
- All containers stop automatically when AppHost stops (Session lifetime)

**Configuration in `appsettings.Development.json`**:

```json
{
  "Aspire": {
    "Hosting": {
      "ContainerHostAddress": "0.0.0.0"
    }
  },
  "Dashboard": {
    "Frontend": {
      "AuthMode": "Unsecured"
    }
  }
}
```

#### Service Dependencies with WaitFor

The AppHost uses `.WaitFor()` to ensure proper startup order:

```csharp
var skymonitor = builder.AddProject<Projects.HVO_SkyMonitor>("skymonitor")
    .WaitFor(redis)
    .WaitFor(postgres)
    .WaitFor(minio);

var simulatorAgent = builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_Simulator>("simulator-agent")
    .WaitFor(skymonitor);
```

This prevents services from starting before their dependencies are ready.

### What's Included

The devcontainer configuration includes:

- **.NET 10 SDK** - Latest .NET SDK for building and running applications
- **Docker-in-Docker** - Run and manage Docker containers inside the dev container
- **Aspire Project Templates** - Automatically installed during container creation
- **C# Dev Kit** - Complete C# development experience with IntelliSense, debugging, and more
- **GitHub Copilot** - AI-powered code completion and chat
- **Git & GitHub CLI** - Version control and GitHub integration
- **Zsh with Oh My Zsh** - Enhanced terminal experience
- **IntelliCode** - AI-assisted development with usage examples
- **Identity & API infrastructure parity** - The main `HVO.SkyMonitor` site now runs the same Identity, passkey, and API key pipeline previously used by the camera-agent simulator, backed by shared middleware and helpers in `HVO.SkyMonitor.Common`.
- **Shared diagnostics/security library** - Cross-cutting middleware (correlation IDs, exception handling, antiforgery helpers) and API-key primitives live in `src/HVO.SkyMonitor.Common`, consumed by the main site and reusable by future services.
- **Camera-agent independence** - Projects under `HVO.SkyMonitor.CameraAgent.*` only reference `HVO.Common`, keeping edge agents lightweight while still registering their own diagnostics/security components.

### Extensions

The following VS Code extensions are automatically installed:

- C# Dev Kit (`ms-dotnettools.csdevkit`)
- C# (`ms-dotnettools.csharp`)
- .NET Runtime (`ms-dotnettools.vscode-dotnet-runtime`)
- GitHub Copilot (`GitHub.copilot`)
- GitHub Copilot Chat (`GitHub.copilot-chat`)
- IntelliCode (`visualstudioexptteam.vscodeintellicode`)
- IntelliCode API Usage Examples (`visualstudioexptteam.intellicode-api-usage-examples`)
- JavaScript Profiler (`ms-vscode.vscode-js-profile-flame`)

### Port Forwarding

The following ports are automatically forwarded and accessible from your host machine:

- **5000-5001** - HVO.SkyMonitor main application (HTTP/HTTPS)
- **5010-5011** - Simulator Camera Agent (HTTP/HTTPS)
- **5020-5021** - ZWO Camera Agent (HTTP/HTTPS)
- **6379** - Redis
- **5432** - PostgreSQL
- **9000** - MinIO API
- **9001** - MinIO Console
- **15201** - Aspire Dashboard (opens automatically)

## Container Support

The application runs in containers by default, with automatic Docker image building by Aspire. You can switch to project mode for easier debugging.

### Automatic Container Building

Aspire automatically builds Docker images from Dockerfiles when starting in container mode (default):
- No manual `docker build` commands needed
- Images rebuild automatically when you restart the AppHost
- Built from Dockerfiles in each project directory

**To run:**
```bash
dotnet run --project src/HVO.SkyMonitor.AppHost --launch-profile http
```

### Manual Multi-Architecture Builds

For deploying to Raspberry Pi or other platforms, build multi-arch images manually:

```bash
# Build all images for all platforms (amd64, arm64, arm/v7)
./build-images.sh

# Build for specific platform only
PLATFORMS=linux/arm64 ./build-images.sh

# Or use docker-compose
docker-compose -f docker-compose.build.yml build
```

This creates local images:
- `hvo-skymonitor:latest`
- `hvo-cameraagent-simulator:latest`
- `hvo-cameraagent-zwo:latest`

### Development vs Container Mode

**Container Mode (Default):**
- Runs applications in Docker containers
- Aspire builds images automatically from Dockerfiles
- Matches deployment environment
- Tests containerized behavior
- Multi-arch support (ARM/x64)
- Recommended for normal development and deployment testing

**Project Mode (USE_CONTAINERS=false):**
- Runs projects directly with `dotnet run`
- Hot reload enabled
- Faster iteration for code changes
- Easier debugging with breakpoints
- Use only when debugging specific issues

```bash
# Switch to project mode
USE_CONTAINERS=false dotnet run --project src/HVO.SkyMonitor.AppHost --launch-profile http
```

### Deployment to Raspberry Pi

For deploying camera agents to Raspberry Pi:

1. **Build ARM images locally:**
   ```bash
   PLATFORMS=linux/arm64 ./build-images.sh
   # or for older Pi: PLATFORMS=linux/arm/v7 ./build-images.sh
   ```

2. **Save and transfer images:**
   ```bash
   docker save hvo-cameraagent-zwo:latest | gzip > zwo-agent.tar.gz
   scp zwo-agent.tar.gz pi@raspberrypi:/tmp/
   ```

3. **Load and run on Pi:**
   ```bash
   ssh pi@raspberrypi
   docker load < /tmp/zwo-agent.tar.gz
   docker run -d --name zwo-agent \
     -e SkyMonitor__BaseUrl=http://your-server:5174 \
     -e CentralIdentity__ServiceUrl=http://your-server:5174 \
     -p 8080:8080 \
     --restart unless-stopped \
     hvo-cameraagent-zwo:latest
   ```

4. **For ZWO cameras with USB access:**
   ```bash
   docker run -d --name zwo-agent \
     --device /dev/bus/usb:/dev/bus/usb \
     --privileged \
     -e SkyMonitor__BaseUrl=http://your-server:5174 \
     -e CentralIdentity__ServiceUrl=http://your-server:5174 \
     -p 8080:8080 \
     --restart unless-stopped \
     hvo-cameraagent-zwo:latest
   ```

### Dockerfiles

Each project has a multi-stage Dockerfile:
- `src/HVO.SkyMonitor/Dockerfile`
- `src/HVO.SkyMonitor.CameraAgent.Simulator/Dockerfile`
- `src/HVO.SkyMonitor.CameraAgent.ZWO/Dockerfile`

All Dockerfiles:
- Use .NET 10 SDK for build
- Use .NET 10 ASP.NET runtime for final image
- Support multi-arch builds (amd64, arm64, arm/v7)
- Include health checks
- Expose port 8080

## Identity System Rebuild (In Progress)

**⚠️ IMPORTANT: The identity and authentication system is being rebuilt from scratch.**

The application is undergoing a major refactor to implement a centralized identity and authorization system. This affects:

- **Identity Storage:** Transitioning from per-service databases to a single centralized PostgreSQL database
- **Authentication:** Adding OpenIddict for OAuth2/OIDC flows (Authorization Code + PKCE, Client Credentials)
- **Account Types:** Introducing USER vs SYSTEM account distinction
- **Camera Agents:** Will authenticate to central HVO.SkyMonitor service (no local Identity)
- **API Keys:** Centralized management with enhanced policies and audit logging
- **Signed URLs:** New capability for high-volume media endpoints

**Current Status:** Phase 0 (Environment Reset) - All existing Identity migrations have been removed. New schema is being implemented.

**Documentation:**
- Implementation Plan: `docs/projects/auth/central-identity-plan.md`
- Current State Inventory: `docs/projects/auth/phase0-inventory.md`
- Target Schema: `docs/projects/auth/target-schema.md`
- Connection Strings & Keys: `docs/projects/auth/data-protection-and-connections.md`

**For Developers:**
- Database migrations have been reset - the database schema will be rebuilt during Phase 1
- If you encounter authentication errors, this is expected during the transition
- Camera agents will retain local Identity temporarily until Phase 4
- See `docs/projects/auth/central-identity-plan.md` for the full 8-phase implementation plan

## Recent Identity & Infrastructure Work (Pre-Rebuild)

- Migrated diagnostics middleware, correlation ID plumbing, and API-key primitives into `src/HVO.SkyMonitor.Common` so the main site and future microservices share a single implementation.
- Cloned the camera-agent simulator's complete Identity experience (Blazor pages, passkey WebAuthn flows, external login + email management, scoped CSS/JS) into `src/HVO.SkyMonitor/Components/Account`, ensuring parity with the hardened simulator stack.
- Added minimal API endpoints in `Program.cs` via `MapAdditionalIdentityEndpoints()` to support passkey creation/request, external login linking, and personal-data download routes.
- Updated dependency wiring so only the main site references `HVO.SkyMonitor.Common`; camera-agent projects remain standalone and continue using their own infrastructure packages, preventing circular dependencies.