# HVO.SkyMonitor

[![CI](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/ci.yml)
[![Line Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740/raw/coverage-line.json)](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/ci.yml)
[![Branch Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740/raw/coverage-branch.json)](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

All-sky imaging system built with .NET 10. Standalone CameraAgent instances
acquire and process images near each camera, retain bounded local history, and
serve an authenticated operator UI. The installer also supports the stock Silo
storage layout for new instances.

The portfolio roadmap and stable initiative IDs are in
[`docs/roadmap.md`](docs/roadmap.md). The authoritative architecture,
virtual-first requirements, and detailed implementation plan remain in
[`docs/project-plan.md`](docs/project-plan.md).

For a repository-independent local VirtualSky CameraAgent installation from
verified local/offline assets, see the
[`hvo-sky-install` installer runbook](docs/runbooks/deployment-installer.md).
Installation and release are being brought to the shared
[HVO product installation and release contract](docs/hvo-install-contract.md)
under epic [#1318](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1318).

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

Ordinary builds and tests default to the Linux host architecture, with framework-dependent
output under `bin/<Configuration>/net10.0/`. Pass `--runtime` to select another
architecture. Publishing without a runtime follows the Linux SDK host;
self-contained release and ReadyToRun commands select `linux-x64` or `linux-arm64` explicitly.
The IL image publish (`scripts/release:cameraagent-publish` without ReadyToRun), used
by Compose/dev images and local release-asset builds, follows the architecture of
the per-platform SDK stage from its pinned multi-architecture image manifest. The runners,
installer, and terminal client remain self-contained when published. Clean existing
`bin` and `obj` directories when adopting these defaults, switching architectures,
or switching between default and explicit RID builds, since incremental
builds do not remove obsolete native files. The former Mac advisory workflow is retired;
it provides no product qualification. Linux x64 and ARM64 remain the supported targets.

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

### Running CameraAgent

Install a verified [production catalog](docs/catalog/production-install.md)
under the configured runtime-data root before starting CameraAgent. Copy
`.env.template` to an ignored `.env` and set `CAMERA_AGENT_ADMIN_PASSWORD`
for a Compose deployment.

```bash
./scripts/infra:start
./scripts/infra:status
./scripts/infra:stop
```

The development container listens at `http://localhost:5130`. Use
`./scripts/infra:start --rebuild` to refresh its image. See the
[development runbook](docs/runbooks/local-dev.md) for reset behavior, owner
setup and direct execution.

```bash
./scripts/with-env dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
```

VS Code uses an attach profile so debugging follows the same launch and
environment-loading path as an ordinary direct run.

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
- **Shared diagnostics and security** — CameraAgent uses middleware and authentication helpers from `HVO.SkyMonitor.Common`.
- **Local operation** — CameraAgent owns acquisition, processing, retained state, and its operator UI.

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

The devcontainer forwards port 5130 for CameraAgent.

### Environment Variables & Secrets

- Copy `.env.template` to `.env` for Docker Compose. Only non-secret defaults live in version control.
- Place per-developer command overrides in `.devcontainer/devcontainer.local.env`
  (gitignored). `scripts/with-env` loads them after the root `.env` inside or
  outside the devcontainer.
- Use [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets?view=aspnetcore-10.0&tabs=linux) for direct development. A local devcontainer mounts the host user-secrets folder.
- See `docs/security/secrets.md` for local development and installed-instance credentials.

## Container Support

The CameraAgent Dockerfile builds for Linux amd64 and arm64 and exposes port 8080
inside the container. Compose maps it to the local development port. Use the
[installer](docs/runbooks/deployment-installer.md) for installed instances and the
[Silo runbook](docs/runbooks/cameraagent-silo-runtime.md) for the Silo layout.

## Identity

CameraAgent owns its local Identity database and Data Protection keys. The
[identity overview](docs/identity/overview.md) and
[operations runbook](docs/identity/operations-runbook.md) cover first sign-in and
owner recovery.
