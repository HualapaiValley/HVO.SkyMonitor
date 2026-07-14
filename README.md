# HVO.SkyMonitor

Distributed all-sky imaging system built with .NET 10. Self-contained CameraAgent
instances acquire and process images near each camera, retain bounded local history,
and send selected artifacts to a central LogicHost for durable storage and further
processing.

The authoritative architecture and implementation roadmap is
[`docs/project-plan.md`](docs/project-plan.md).

## Development Environment

This repository is configured to work with Visual Studio Code Dev Containers and GitHub Codespaces. The devcontainer provides a Docker CLI for local and remote Docker contexts.

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

The application uses persistent SQL Server, Redis, MinIO, and Mailpit services on `hvo-docker.hvo.lan`. Configure their endpoints and credentials in the ignored `.env` file using `.env.template`. Docker Compose runs only application containers.

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
- **MinIO** - configured by `MINIO_*` in `.env`
- **SMTP (Mailpit)** - configured by `SMTP_*` in `.env`
- **Logic Host** - Main application: http://localhost:5174
 - **Camera Agent** - http://localhost:5130

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

This mode keeps hot reload and a faster edit/run cycle while still talking to the same SQL Server, Redis, and MinIO containers.

### Devcontainer Setup

The `.devcontainer/devcontainer.json` uses the Docker-outside-of-Docker feature so containers run through the host Docker daemon without granting the devcontainer privileged mode.

**Post-create script** (`.devcontainer/post-create.sh`):
- Adds `vscode` user to the `docker` group
- Sets Docker socket permissions (`chmod 666 /var/run/docker.sock`)
- Installs the pinned `dotnet-ef` CLI tool for Entity Framework migrations
- Restores `HVO.SkyMonitor.v9.slnx` so a fresh container can build immediately
- Generates HTTPS developer certificate (`dotnet dev-certs https`)
- Verifies Docker installation

**Note:** We default to HTTP endpoints inside the dev container to avoid certificate trust issues.


### What's Included

The devcontainer configuration includes:

- **.NET 10 SDK** - Latest .NET SDK for building and running applications
- **Docker CLI** - Manage host and remote Docker contexts from the dev container
- **dotnet-ef CLI** - Pinned Entity Framework Core tooling installed automatically
- **OpenCode CLI** - Checksum-verified, pinned CLI started automatically on loopback
- **Tailscale CLI** - Signature-verified, pinned client started with accepted tailnet routes
- **Command-line tools** - `jq`, `rg`, and `sqlite3` are installed during container setup
- **C# Dev Kit** - Complete C# development experience with IntelliSense, debugging, and more
- **GitHub Copilot** - AI-powered code completion and chat
- **Git & GitHub CLI** - Version control and GitHub integration
- **Zsh with Oh My Zsh** - Enhanced terminal experience
- **Docker extension** - Container and Compose integration in VS Code
- **Secret management plumbing** - `.env.template`, `.devcontainer/devcontainer.local.env`, and .NET user secrets support keep credentials out of git
- **Identity & API infrastructure parity** - LogicHost and CameraAgent use shared middleware and helpers from `HVO.SkyMonitor.Common` while retaining separate identity stores.
- **Shared diagnostics/security library** - Cross-cutting middleware (correlation IDs, exception handling, antiforgery helpers) and API-key primitives live in `src/HVO.SkyMonitor.Common`, consumed by the main site and reusable by future services.
- **Camera-agent independence** - Projects under `HVO.SkyMonitor.CameraAgent.*` keep acquisition and local processing independent from LogicHost while registering their own diagnostics and security components.

### OpenCode over Tailscale

OpenCode and Tailscale are installed and started automatically. Their credentials and runtime identity are retained under the ignored `.devcontainer/state/` directory, so rebuilding the same workspace does not require authentication again.

For first-time enrollment in a new clone, set a one-time `TAILSCALE_AUTHKEY` in `.env` or `.devcontainer/devcontainer.local.env` and keep that file mode `0600`. Remove the key after successful enrollment; the persisted node state replaces it. The startup script connects as `dev-host-skymonitor`, accepts advertised routes, starts OpenCode on loopback, and exposes it through tailnet-only HTTPS. Set `TAILSCALE_ACCEPT_ROUTES=false` only when advertised routes would conflict with another local network. Authenticate OpenCode providers once with `opencode auth login`; provider credentials then persist across rebuilds in the ignored state directory.

The HTTPS endpoint also requires OpenCode Basic Auth. Its username is `opencode`; a random password is generated at `.devcontainer/state/opencode-data/server-password` and retained across rebuilds.

To start or repair the services manually:

```bash
./scripts/opencode:enable
```

The script starts OpenCode only on loopback and uses `tailscale serve` to provide a tailnet-only HTTPS endpoint. Set `OPENCODE_PORT` to use a different local port. To remove the tailnet endpoint and stop the OpenCode process started by the script:

```bash
./scripts/opencode:disable
```

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
- **5174** - LogicHost container profile
- **5130** - Camera Agent container profile

### Environment Variables & Secrets

- Copy `.env.template` to `.env` for Docker Compose. Only non-secret defaults live in version control.
- Place per-developer overrides in `.devcontainer/devcontainer.local.env` (gitignored) and map them via the `remoteEnv` block in `.devcontainer/devcontainer.json`.
- Treat `.devcontainer/state/` as secret local data. It survives rebuilds but not a fresh clone; restore it securely or re-enroll Tailscale and OpenCode after cloning.
- Use `.NET` [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets?view=aspnetcore-8.0&tabs=linux) for local debugging outside containers. The devcontainer mounts your host secrets folder automatically.
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
