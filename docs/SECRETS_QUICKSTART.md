# Quick Start: Secrets Setup

> [!IMPORTANT]
> HVO.SkyMonitor now relies on Docker Compose/Testcontainers infrastructure plus direct project runs (no Aspire AppHost). Use the scripts under `./scripts` to provision dependencies and run the application from `src/HVO.SkyMonitor.LogicHost`.

## Initial Development Setup

### 1. Copy Environment Template

```bash
cp .env.template .env
```

`.env` stays git-ignored and holds non-sensitive overrides (ports, Docker contexts, etc.). Secrets belong in User Secrets or a private env file such as `.devcontainer/devcontainer.local.env`.

### 2. Provide Secrets

Pick one (or mix) of the following options:

**Option A – `.devcontainer/devcontainer.local.env`**

```env
# Example – never commit this file
MINIO_ROOT_USER=dev-minio
MINIO_ROOT_PASSWORD=super-secret
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres-pass
SIGNED_TICKET_SECRET="$(openssl rand -base64 32)"
```

The file is already git-ignored; VS Code loads it automatically through `devcontainer.json`.

**Option B – .NET User Secrets for `HVO.SkyMonitor.LogicHost`**

```bash
cd src/HVO.SkyMonitor.LogicHost
dotnet user-secrets init          # creates/updates UserSecretsId
dotnet user-secrets set "MinIO:AccessKey" "your-username"
dotnet user-secrets set "MinIO:SecretKey" "your-password"
dotnet user-secrets set "PostgreSQL:Username" "postgres"
dotnet user-secrets set "PostgreSQL:Password" "strong-password"
```

Use `dotnet user-secrets list --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj` to confirm values.

### 3. Start Infrastructure

```bash
./scripts/infra:start postgres minio redis smtp
# or ./scripts/infra:start          # starts everything, including camera agents
```

The script loads `.env`, provisions bind-mount directories, and targets `docker-compose.infrastructure.yml` for the shared services (plus `docker-compose.apps.yml` whenever you include LogicHost or the Camera Agent).

> The helpers automatically create local folders referenced by `POSTGRES_DATA_DIR`, `MINIO_DATA_DIR`, and `REDIS_DATA_DIR` whenever those services run in the `default` Docker context, so you no longer need to pre-create `./data/*` directories.

### 4. Run the Application

```bash
dotnet run --project src/HVO.SkyMonitor.LogicHost --configuration Debug
# Optional: dotnet watch --project src/HVO.SkyMonitor.LogicHost run
```

The runtime pulls secrets from User Secrets → environment variables (`.env`, devcontainer) → configuration files.

### 5. Access Services

- **Main App:** https://localhost:7096 (from `LOGIC_HOST_HTTPS_PORT`)
- **MinIO Console:** `http://<MINIO_HOST>:<MINIO_CONSOLE_PORT>` (defaults to `http://192.168.2.104:9001` when using `proxmox-home`)
- **Mailpit (SMTP):** `http://<SMTP_HOST>:<SMTP_WEB_PORT>` (defaults to `http://192.168.2.104:8025` in the remote stack)
- **Camera Agent:** http://localhost:5130 (when started)
- **Identity (public):** https://localhost:7096 (used for interactive redirects)
 

## Default Credentials

When no secrets are supplied, local defaults apply:

- **MinIO:** `minioadmin` / `minioadmin`
- **PostgreSQL:** `postgres` / `postgres`

Change these for any shared or remote environment.

## Troubleshooting

### Secrets Not Loading

```bash
cd src/HVO.SkyMonitor.LogicHost
dotnet user-secrets list

grep UserSecretsId HVO.SkyMonitor.LogicHost.csproj   # ensures the project is linked
```

If you rely on `.devcontainer/devcontainer.local.env`, confirm the file exists and VS Code prompted you to reload the container.

### Infrastructure Issues

```bash
./scripts/infra:status                     # quick health summary
docker compose -f docker-compose.infrastructure.yml logs -f minio
./scripts/infra:start --reset postgres     # recreate a failing service
```

### Dev Container Cannot See Secrets

The dev container binds your host user-secrets directory automatically. If secrets are missing:

1. Rebuild the container (`Dev Containers: Rebuild and Reopen`)
2. Verify the mount entry in `.devcontainer/devcontainer.json`:
   ```jsonc
   {
     "type": "bind",
     "source": "${localEnv:HOME}${localEnv:USERPROFILE}/.microsoft/usersecrets",
     "target": "/home/vscode/.microsoft/usersecrets"
   }
   ```

## Next Steps

- Review [SECRETS_MANAGEMENT.md](./SECRETS_MANAGEMENT.md) for policy details
- Configure GitHub Secrets for CI/CD as described in `.github/workflows/README.md`
- Follow [identity/secrets-reference.md](identity/secrets-reference.md) when generating OpenIddict keys, API keys, and signed URL secrets
