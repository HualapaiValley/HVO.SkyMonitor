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

The application runs using **Docker Compose** for infrastructure and optionally via direct `dotnet run`/`dotnet watch` for faster inner-loop development.

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
Only the services you list are started now, so `./scripts/infra:start postgres` leaves MinIO, Redis, etc. untouched. If you request an application service (for example `logichost`), the script automatically starts all supporting infrastructure if you didn't list them explicitly.

**Reset data and start:**
```bash
./scripts/infra:start --reset all
```

**Force application containers to rebuild before start:**
```bash
./scripts/infra:start --rebuild
```

You can pass specific services to `--rebuild` (for example `--rebuild logichost`) and it will rebuild only those application images. Pair it with `--reset` to clear container state and refresh the image in a single command.

**Check service status:**
```bash
./scripts/infra:status
```

**Stop services:**
```bash
./scripts/infra:stop
```

**Stop and clear cached data (volumes/directories) for specific services:**
```bash
./scripts/infra:stop --clear-cache redis minio
```
When `--clear-cache` is supplied without explicit service names the script clears caches for everything you stop, wiping the corresponding Docker volumes (Postgres/Redis/MinIO) and removing the application containers so the next start is clean.

**Services Started:**
- **PostgreSQL** - tcp://localhost:5432 with `skymonitordb` database
- **Redis** - tcp://localhost:6379
- **MinIO** - API: http://localhost:9000, Console: http://localhost:9001 (minioadmin/minioadmin)
- **SMTP (Mailpit)** - SMTP: tcp://localhost:1025, Web UI: http://localhost:8025
- **Logic Host** - HTTPS (dotnet run): https://localhost:7096, HTTP (container profile): http://localhost:5174
 - **Camera Agent** - http://localhost:5130

```bash
# Main site
dotnet run --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj

# Camera agent
dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
```

This mode keeps hot reload and a faster edit/run cycle while still talking to the same Postgres, Redis, and MinIO containers.

### Docker-in-Docker Architecture

This project uses **Docker-in-Docker** to run container orchestration inside the dev container.

### Architecture & Design Patterns

We expect new services, features, and pipelines to lean on established industry patterns instead of ad-hoc custom wiring. When you need interchangeable behaviors (for example, alternate dependency-priming or diagnostics flows), model them with the Strategy pattern (an `IThingStrategy` interface and DI-registered implementations) or another well-known pattern that fits. Using recognizable patterns keeps the codebase self-documenting, simplifies reviews, and lets us swap implementations without risky refactors. If a scenario does not map to a standard pattern, call it out in the PR description and document the reasoning.

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
- Installs the pinned `dotnet-ef` CLI tool for Entity Framework migrations
- Generates HTTPS developer certificate (`dotnet dev-certs https`)
- Verifies Docker installation

**Note:** We default to HTTP endpoints inside the dev container to avoid certificate trust issues. HTTPS runbooks live under `docs/projects/infra/` if you need certificates locally.


### What's Included

The devcontainer configuration includes:

- **.NET 10 SDK** - Latest .NET SDK for building and running applications
- **Docker-in-Docker** - Run and manage Docker containers inside the dev container
- **dotnet-ef CLI** - Pinned Entity Framework Core tooling installed automatically
- **ripgrep & python alias** - `rg` and `python` commands available via ripgrep and python-is-python3 packages
- **C# Dev Kit** - Complete C# development experience with IntelliSense, debugging, and more
- **GitHub Copilot** - AI-powered code completion and chat
- **Git & GitHub CLI** - Version control and GitHub integration
- **Zsh with Oh My Zsh** - Enhanced terminal experience
- **IntelliCode** - AI-assisted development with usage examples
- **Secret management plumbing** - `.env.template`, `.devcontainer/devcontainer.local.env`, and .NET user secrets support keep credentials out of git
- **Identity & API infrastructure parity** - The main `HVO.SkyMonitor` site runs the same Identity, passkey, and API key pipeline used by the camera agent, backed by shared middleware and helpers in `HVO.SkyMonitor.Common`.
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

- **5000-5001** - Logic Host application (HTTP/HTTPS when running via `dotnet run`)
- **7096** - Logic Host HTTPS (dotnet run default secure port)
- **5174** - SkyMonitor container profile (Docker Compose build, HTTP)
- **5130** - Camera Agent container profile
  
- **6379** - Redis
- **5432** - PostgreSQL
- **9000** - MinIO API
- **9001** - MinIO Console

### Environment Variables & Secrets

- Copy `.env.template` to `.env` for Docker Compose. Only non-secret defaults live in version control.
- Place per-developer overrides in `.devcontainer/devcontainer.local.env` (gitignored) and map them via the `remoteEnv` block in `.devcontainer/devcontainer.json`.
- Use `.NET` [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets?view=aspnetcore-8.0&tabs=linux) for local debugging outside containers. The devcontainer mounts your host secrets folder automatically.
- See `docs/SECRETS_MANAGEMENT.md` and `docs/SECRETS_QUICKSTART.md` for detailed workflows covering Testcontainers, Docker Compose, and production deployments.

#### Central Identity issuer

OpenIddict now reads a single canonical issuer from `IdentityServer__Issuer`. Set it to whichever base address every part of the flow can reach:

- **Docker Compose** (default): `.env` already exports `IDENTITYSERVER_ISSUER=http://logichost:8080/`, so all containers agree on that host.
- **Running outside containers:** override before starting LogicHost so cookies and authorization codes are issued with the local URL you expose to browsers:

```bash
export IdentityServer__Issuer=http://localhost:5174/
dotnet run --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj
```

When the camera agent runs outside Docker, set `CentralIdentity__ServiceUrl` and `CAMERA_AGENT_IDENTITY_PUBLIC_URL` (or the corresponding config values) to the same origin so both the browser redirect and the server-side token exchange agree on the issuer.

#### Shared Data Protection keys

LogicHost and the Camera Agent now persist ASP.NET Core Data Protection keys in the PostgreSQL database (`security.dataprotectionkeys`). This lets antiforgery tokens and cookies survive container restarts and allows cross-host logout flows to function. **Keys are currently stored in plaintext for development convenience.** Before production, switch the repository to an encrypted backing store (Key Vault, envelope encryption, etc.) so the database never contains raw key material.

## Container Support

The Docker Compose workflow (via `scripts/infra:start`) now drives two stacks:

- `docker-compose.infrastructure.yml` for PostgreSQL, Redis, MinIO, and Mailpit (typically via the `proxmox-home` context)
- `docker-compose.apps.yml` for LogicHost and the Camera Agent (usually running locally)

Infrastructure services default to the remote `proxmox-home` Docker context (`ssh://roys@192.168.2.104`). Override `POSTGRES_HOST`, `REDIS_HOST`, `MINIO_HOST`, and `SMTP_HOST` in `.env` if your environment uses different addresses.

The helper scripts automatically create any bind-mount directories referenced by `POSTGRES_DATA_DIR`, `MINIO_DATA_DIR`, and `REDIS_DATA_DIR` whenever the associated service runs in the local (`default`) context.

Docker caches previously built images, so use the script's `--rebuild` flag whenever you need to force fresh LogicHost or Camera Agent binaries.

### Automatic Container Building

`./scripts/infra:start --rebuild [logichost|cameraagent]` invokes `docker compose -f docker-compose.apps.yml build` for the selected application services before issuing `up -d`. Resetting those services (`--reset logichost cameraagent`) also triggers a rebuild automatically. This keeps each container aligned with the working tree without requiring manual `docker build` commands.

### Stack commands by context

Use the scripts for day-to-day work, or call the compose files directly when you need to target a specific Docker context.

| Stack | Default context | Start | Stop | Status | Logs |
| --- | --- | --- | --- | --- | --- |
| Infrastructure (`postgres`, `redis`, `minio`, `smtp`) | `proxmox-home` | <code>docker --context proxmox-home compose -f docker-compose.infrastructure.yml up -d postgres minio redis smtp</code> | <code>docker --context proxmox-home compose -f docker-compose.infrastructure.yml stop postgres minio redis smtp</code> | <code>docker --context proxmox-home compose -f docker-compose.infrastructure.yml ps</code> | <code>docker --context proxmox-home compose -f docker-compose.infrastructure.yml logs -f postgres</code> |
| Application (`logichost`, `cameraagent`) | `default` (local) | <code>docker compose -f docker-compose.apps.yml up -d logichost cameraagent</code> | <code>docker compose -f docker-compose.apps.yml stop logichost cameraagent</code> | <code>docker compose -f docker-compose.apps.yml ps</code> | <code>docker compose -f docker-compose.apps.yml logs -f logichost</code> |

Equivalent script commands (respecting the per-service context variables declared in `.env`):

```bash
# Start remote infra stack
./scripts/infra:start postgres minio redis smtp

# Start only application containers locally
./scripts/infra:start logichost cameraagent

# Stop everything (clears remote contexts too)
./scripts/infra:stop all

# Status overview (per service context)
./scripts/infra:status

# Tail logs for a stack
docker --context proxmox-home compose -f docker-compose.infrastructure.yml logs -f redis
docker compose -f docker-compose.apps.yml logs -f cameraagent
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
- `hvo-cameraagent:latest`

### Deployment to Raspberry Pi

For deploying the camera agent to Raspberry Pi (if desired), build an ARM image locally and push to a registry or load via tarball, using the `hvo-cameraagent` image.

### Dockerfiles

Each project has a multi-stage Dockerfile:
- `src/HVO.SkyMonitor.LogicHost/Dockerfile`
- `src/HVO.SkyMonitor.CameraAgent/Dockerfile`

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
- Identity Operations Index: `docs/identity/operations-index.md`

**For Developers:**
- Database migrations have been reset - the database schema will be rebuilt during Phase 1
- If you encounter authentication errors, this is expected during the transition
- Camera agents will retain local Identity temporarily until Phase 4
- See `docs/projects/auth/central-identity-plan.md` for the full 8-phase implementation plan

## Recent Identity & Infrastructure Work (Pre-Rebuild)

- Migrated diagnostics middleware, correlation ID plumbing, and API-key primitives into `src/HVO.SkyMonitor.Common` so the main site and future microservices share a single implementation.
- Cloned the camera agent's complete Identity experience (Blazor pages, passkey WebAuthn flows, external login + email management, scoped CSS/JS) into `src/HVO.SkyMonitor.LogicHost/Components/Account`, ensuring parity with the hardened agent stack.
- Added minimal API endpoints in `Program.cs` via `MapAdditionalIdentityEndpoints()` to support passkey creation/request, external login linking, and personal-data download routes.
- Updated dependency wiring so only the main site references `HVO.SkyMonitor.Common`; camera-agent projects remain standalone and continue using their own infrastructure packages, preventing circular dependencies.