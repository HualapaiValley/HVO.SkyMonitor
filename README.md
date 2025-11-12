# HVO.SkyMonitor

## Development Environment

This repository is configured to work with Visual Studio Code Dev Containers and GitHub Codespaces.

### Prerequisites

- [Visual Studio Code](https://code.visualstudio.com/)
- [Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)

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

### What's Included

The devcontainer configuration includes:

- **.NET 10 SDK** - Latest .NET SDK for building and running applications
- **C# Dev Kit** - Complete C# development experience with IntelliSense, debugging, and more
- **GitHub Copilot** - AI-powered code completion and chat
- **Git & GitHub CLI** - Version control and GitHub integration
- **Zsh with Oh My Zsh** - Enhanced terminal experience
- **IntelliCode** - AI-assisted development with usage examples

### Extensions

The following VS Code extensions are automatically installed:

- C# Dev Kit (`ms-dotnettools.csdevkit`)
- C# (`ms-dotnettools.csharp`)
- .NET Runtime (`ms-dotnettools.vscode-dotnet-runtime`)
- GitHub Copilot (`GitHub.copilot`)
- GitHub Copilot Chat (`GitHub.copilot-chat`)
- IntelliCode (`visualstudioexptteam.vscodeintellicode`)
- IntelliCode API Usage Examples (`visualstudioexptteam.intellicode-api-usage-examples`)

### Port Forwarding

The following ports are automatically forwarded for web application development:

- `5000` - HTTP
- `5001` - HTTPS