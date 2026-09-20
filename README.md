# HVO.SkyMonitor

[![CI](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/ci.yml)
[![Line Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740/raw/coverage-line.json)](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/ci.yml)
[![Branch Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740/raw/coverage-branch.json)](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

Distributed all-sky imaging system built with .NET 10. Self-contained CameraAgent
instances acquire and process images near each camera, retain bounded local history,
and send selected artifacts to a central LogicHost for durable storage and further
processing.

The portfolio roadmap and stable initiative IDs are in
[`docs/roadmap.md`](docs/roadmap.md). The authoritative architecture,
virtual-first requirements, and detailed implementation plan remain in
[`docs/project-plan.md`](docs/project-plan.md).

For a repository-independent local VirtualSky CameraAgent installation from
verified local/offline assets, see the
[`hvo-skymonitor` installer runbook](docs/runbooks/deployment-installer.md).

## Development Environment

The primary development workflow runs directly on a Linux host, often through
SSH or VS Code Remote. The repository does not require or configure a particular
coding agent. A standard devcontainer is also available for cloud workspaces or
when an isolated environment is useful; it mirrors the same SDK and command-line
tooling used on the host.

### Prerequisites

- Exact .NET SDK from [`global.json`](global.json)
- Docker Engine with Compose
- Git and the Linux/GNU command-line tools used by repository scripts
- `jq`, `rg`, `shellcheck`, and `sqlite3`

On Windows, use WSL2 and keep the checkout on its Linux filesystem. Repository
scripts assume Linux filesystem semantics and GNU tools.

### Getting Started

#### Native host or SSH

1. Clone the repository on the Linux host.
2. Install the SDK pinned by `global.json` and the prerequisites above.
3. Copy `.env.template` to the ignored `.env` and supply local credentials.
4. Run `dotnet tool restore` and `dotnet restore HVO.SkyMonitor.v9.slnx`.
5. Open the checkout locally, over SSH, or with VS Code Remote and use the
   repository commands below.

#### Devcontainer or cloud workspace

For a local devcontainer, install
[Visual Studio Code](https://code.visualstudio.com/), the
[Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers),
and Docker Desktop or Docker Engine.

1. Clone this repository
2. Open the folder in Visual Studio Code
3. When prompted, click "Reopen in Container" (or use Command Palette: `Dev Containers: Reopen in Container`)
4. Wait for the container to build and start
5. The development environment will be ready with .NET 10 SDK and all necessary tools

For GitHub Codespaces:

1. Navigate to the repository on GitHub
2. Click the "Code" button and select "Codespaces"
3. Click "Create codespace on main" (or your desired branch)
4. Wait for the Codespace to initialize
5. Your development environment is ready to use

### Running the Application

The application uses persistent SQL Server, Redis, and Mailpit services on `hvo-docker.hvo.lan`, plus a LogicHost-local filesystem object store. Configure their endpoints and credentials in the ignored `.env` file using `.env.template`. Docker Compose runs only application containers.

#### Option 1: Docker Compose (Recommended)

Use the infrastructure management scripts to start services:

**Start application containers:**
```bash
./scripts/infra:start
```

**Start one application container:**
```bash
./scripts/infra:start logichost
```

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

`./scripts/infra:stop` and `./scripts/infra:reset` affect only local application containers. They never modify the shared services or their data.

**Shared services:**
- **SQL Server** - configured by `SQLSERVER_*` in `.env`
- **Redis** - configured by `REDIS_*` in `.env`
- **Object store** - a LogicHost-local filesystem root configured by `HVO_OBJECT_STORE_ROOT` in `.env`; not a shared service
- **SMTP (Mailpit)** - configured by `SMTP_*` in `.env`
- **Logic Host** - Main application: http://localhost:5174
- **Camera Agent** - http://localhost:5130

#### Option 2: Direct execution

```bash
# First install the verified HYG bundle as documented in docs/catalog/production-install.md.

# Main site
./scripts/with-env dotnet run --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj

# Camera agent
./scripts/with-env dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
```

Configure the CameraAgent owner password with the protected prompt in
[`docs/runbooks/local-dev.md`](docs/runbooks/local-dev.md) before its first run.
Both hosts require the same verified production catalog installed under the
configured runtime-data root; production builds do not package the test fixture.

This mode keeps hot reload and a faster edit/run cycle while still talking to the same SQL Server and Redis containers and the same object-store root.

VS Code launch configuration is intentionally attach-only. Start a host with
`./scripts/with-env` as shown above, then select the matching `Attach` profile so
debugging uses the same supported environment-loading path as ordinary direct
execution.

### Devcontainer and cloud setup

The optional `.devcontainer/devcontainer.json` uses the
Docker-outside-of-Docker feature so containers run through the workspace Docker
daemon without granting the development container privileged mode. It does not
install, configure, proxy, or persist any coding-agent runtime.

**Post-create script** (`.devcontainer/post-create.sh`):

- Restores the pinned `dotnet-ef` and ReportGenerator tools from `dotnet-tools.json`
- Restores `HVO.SkyMonitor.v9.slnx` so a fresh container can build immediately
- Generates HTTPS developer certificate (`dotnet dev-certs https`)
- Uses Git and GitHub authentication supplied by the host or cloud workspace
- Verifies Docker and Compose daemon access

**Note:** We default to HTTP endpoints inside the devcontainer to avoid
certificate trust issues.

### What's Included

The devcontainer configuration includes:

- **.NET 10 SDK** - Repository-pinned SDK for building and running applications
- **Docker CLI** - Manage host and remote Docker contexts from the dev container
- **.NET local tools** - Pinned Entity Framework Core and ReportGenerator tooling restored automatically
- **Command-line tools** - `jq`, `rg`, `shellcheck`, and `sqlite3` are installed in the container image
- **C# Dev Kit** - Complete C# development experience with IntelliSense, debugging, and more
- **GitHub Copilot** - AI-powered code completion and chat
- **Git & GitHub CLI** - Version control and GitHub integration
- **Zsh with Oh My Zsh** - Enhanced terminal experience
- **Docker extension** - Container and Compose integration in VS Code
- **Secret management plumbing** - `.env.template`, optional `.devcontainer/devcontainer.local.env`, and .NET user secrets support keep credentials out of git
- **Identity & API infrastructure parity** - LogicHost and CameraAgent use shared middleware and helpers from `HVO.SkyMonitor.Common` while retaining separate identity stores.
- **Shared diagnostics/security library** - Cross-cutting middleware (correlation IDs, exception handling, antiforgery helpers) and API-key primitives live in `src/HVO.SkyMonitor.Common`, consumed by the main site and reusable by future services.
- **Camera-agent independence** - Projects under `HVO.SkyMonitor.CameraAgent.*` keep acquisition and local processing independent from LogicHost while registering their own diagnostics and security components.

### Extensions

The following VS Code extensions are automatically installed:

- C# Dev Kit (`ms-dotnettools.csdevkit`)
- C# (`ms-dotnettools.csharp`)
- .NET Runtime (`ms-dotnettools.vscode-dotnet-runtime`)
- Docker (`ms-azuretools.vscode-docker`)
- GitHub Copilot (`GitHub.copilot`)
- GitHub Copilot Chat (`GitHub.copilot-chat`)
- OpenAI ChatGPT (`openai.chatgpt`)

### Port Forwarding

The following ports are automatically forwarded and accessible from your host machine:

- **7096** - LogicHost HTTPS direct-run profile
- **5174** - LogicHost HTTP container and direct-run profile
- **5130** - Camera Agent container profile

### Environment Variables & Secrets

- Copy `.env.template` to `.env` for Docker Compose. Only non-secret defaults live in version control.
- Place per-developer command overrides in `.devcontainer/devcontainer.local.env`
  (gitignored). `scripts/with-env` loads them after the root `.env` inside or
  outside the devcontainer.
- Use [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets?view=aspnetcore-10.0&tabs=linux) for direct development. A local devcontainer mounts the host user-secrets folder.
- See `docs/security/secrets.md` for detailed workflows covering Testcontainers, Docker Compose, and production deployments.

## Container Support

The Docker Compose workflow (via `scripts/infra:start`) runs only the ASP.NET/Blazor application containers. Docker caches previously built images, so use the script's `--rebuild` flag whenever you need to force fresh LogicHost or Camera Agent binaries.

### Automatic Container Building

`./scripts/infra:start --rebuild [logichost|cameraagent]` invokes `docker compose -f docker-compose.apps.yml build` for the selected application services before issuing `up -d`. Resetting those services (`--reset logichost cameraagent`) also triggers a rebuild automatically. This keeps each container aligned with the working tree without requiring manual `docker build` commands.

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

## Identity Boundaries

CameraAgent retains its local authenticated administration and monitoring UI so it
can be operated while disconnected. Device registration and agent-to-LogicHost
authentication are separate central concerns. See
[`docs/identity/overview.md`](docs/identity/overview.md) for the current workflow and
[`docs/identity/operations-runbook.md`](docs/identity/operations-runbook.md) for
operations.
