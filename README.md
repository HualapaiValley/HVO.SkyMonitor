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
- Restores the pinned `dotnet-ef` and ReportGenerator tools from `dotnet-tools.json`
- Restores `HVO.SkyMonitor.v9.slnx` so a fresh container can build immediately
- Generates HTTPS developer certificate (`dotnet dev-certs https`)
- Configures optional Git, GitHub, and remote-host SSH access from ignored settings
- Verifies Docker and Compose daemon access

**Note:** We default to HTTP endpoints inside the dev container to avoid certificate trust issues.


### What's Included

The devcontainer configuration includes:

- **.NET 10 SDK** - Latest .NET SDK for building and running applications
- **Docker CLI** - Manage host and remote Docker contexts from the dev container
- **.NET local tools** - Pinned Entity Framework Core and ReportGenerator tooling restored automatically
- **OpenCode CLI** - Checksum-verified, pinned CLI serving persistent in-container sessions on port 4096
- **Tailscale CLI** - Signature-verified, pinned client available in the container image
- **Command-line tools** - `jq`, `rg`, `shellcheck`, and `sqlite3` are installed in the container image
- **C# Dev Kit** - Complete C# development experience with IntelliSense, debugging, and more
- **GitHub Copilot** - AI-powered code completion and chat
- **Git & GitHub CLI** - Version control and GitHub integration
- **Zsh with Oh My Zsh** - Enhanced terminal experience
- **Docker extension** - Container and Compose integration in VS Code
- **Secret management plumbing** - `.env.template`, `.devcontainer/devcontainer.local.env`, and .NET user secrets support keep credentials out of git
- **Identity & API infrastructure parity** - LogicHost and CameraAgent use shared middleware and helpers from `HVO.SkyMonitor.Common` while retaining separate identity stores.
- **Shared diagnostics/security library** - Cross-cutting middleware (correlation IDs, exception handling, antiforgery helpers) and API-key primitives live in `src/HVO.SkyMonitor.Common`, consumed by the main site and reusable by future services.
- **Camera-agent independence** - Projects under `HVO.SkyMonitor.CameraAgent.*` keep acquisition and local processing independent from LogicHost while registering their own diagnostics and security components.

### OpenCode from the devcontainer

OpenCode runs as a persistent in-container server supervised by an internal `tmux` session. The server survives terminal, SSH, browser, and desktop-client disconnects; each client is independent and `/exit` closes only that client. The server starts automatically during devcontainer startup and can be managed from a devcontainer terminal:

```bash
./scripts/opencode:enable
./scripts/opencode:disable
```

The server listens on container port `4096`; Docker publishes it to every Docker-host interface at port `4097`. Browser and desktop clients can use `http://<docker-host>:4097`. A terminal client must set `OPENCODE_SERVER_PASSWORD` from the protected password file and run `opencode attach --username opencode http://<docker-host>:4097`; the host-side `./scripts/opencode:remote-connect --continue` helper does this automatically through `http://127.0.0.1:4097`. Inside the devcontainer, `./scripts/opencode:connect --continue` attaches a new TUI client to `http://127.0.0.1:4096`. Use `--session <session-id>` to attach to a specific session.

The endpoint requires OpenCode Basic Auth. Its username is `opencode`; post-create setup generates a random password at `.devcontainer/state/opencode-data/server-password` and retains it across rebuilds. The HTTP endpoint does not use TLS. Host firewall rules must restrict port `4097` to an encrypted private overlay such as Tailscale, or the endpoint must sit behind a TLS reverse proxy; never expose it to an untrusted LAN or the public Internet. Only one client should actively control a particular session at a time.

The non-secret [`.devcontainer/opencode-host.conf`](.devcontainer/opencode-host.conf) keeps the internal tmux identity, container port, and Docker-host port consistent with [`.devcontainer/devcontainer.json`](.devcontainer/devcontainer.json).

Another repository can use the same scripts without colliding by choosing its own host port and tmux session while retaining OpenCode's container port:

```bash
OPENCODE_TMUX_SESSION=hvo-website-opencode
OPENCODE_CONTAINER_PORT=4096
OPENCODE_HOST_PORT=4098
```

Its devcontainer would publish `0.0.0.0:4098:4096`. Host ports and tmux session names must be unique for concurrently running workspaces. Stop the repository's managed session before changing these values and recreate the devcontainer for the Docker port change.

The OpenCode executable is pinned, checksum-verified, and installed root-owned in the container image. Provider credentials, configuration, sessions, agent worktrees, and resumable scratch state remain in ignored host bind mounts beneath `.devcontainer/state/` across container rebuilds and Docker daemon restarts. Specifically, `opencode-worktrees` is mounted at `/tmp/opencode`, while `agent-scratch` is mounted at `/var/lib/hvo-agent-state`. Normal `/tmp` remains disposable and must contain only caches, sockets, locks, and other reproducible files.

Post-create and post-start checks refuse to start OpenCode unless both agent-state paths are dedicated writable mounts. This avoids the dangerous fallback where a missing mount appears to work but stores in-progress files on the disposable container layer. The bind-mounted state still belongs to the local checkout: back up `.devcontainer/state/` separately before deleting the clone, running an ignored-file cleanup such as `git clean -xfd`, or replacing the host disk.

Before the first rebuild that introduces these mounts, stop OpenCode from a separate terminal with `./scripts/opencode:disable`, then run `./scripts/opencode:prepare-rebuild` inside the old container. It resolves the primary checkout, copies any legacy `/tmp/opencode` worktrees into its host bind source, and verifies the copy. Do not resume agents between migration and rebuild. Once the persistence-enabled container is running, the command verifies the expected bind source and reports that migration is no longer needed.

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
- Treat `.devcontainer/state/` as secret local data. OpenCode credentials survive rebuilds but not a fresh clone; restore them securely or reauthenticate OpenCode after cloning. Legacy Tailscale state is intentionally not mounted into the container.
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
