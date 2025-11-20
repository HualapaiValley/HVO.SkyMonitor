# Secrets and Environment Configuration Summary

> [!IMPORTANT]
> HVO.SkyMonitor now runs using Docker Compose/Testcontainers plus direct project executions (no Aspire AppHost). The summaries below reference `./scripts/infra:*`, `.env`, and `src/HVO.SkyMonitor.LogicHost` as the authoritative entry points.

## ✅ What Was Implemented

### 1. Configuration Files Created

- **`.env.template`** - Template with all non-sensitive environment variables
- **`.env.development`** - Development defaults (git-tracked, non-sensitive)
- **`.gitignore`** - Updated to exclude `.env`, secrets, and credential files

### 2. Documentation Created

- **`docs/SECRETS_MANAGEMENT.md`** - Complete secrets management guide
- **`docs/SECRETS_QUICKSTART.md`** - Quick setup instructions
- **`.github/workflows/README.md`** - GitHub Actions secrets configuration

### 3. Code Changes

**`src/HVO.SkyMonitor.LogicHost/Program.cs`:**
- Loads configuration through the standard ASP.NET Core builder stack
- Pulls secrets from User Secrets, `.env`, devcontainer env, or Azure Key Vault depending on environment
- Default credentials (minioadmin/postgres) apply only when no overrides are provided

### 4. DevContainer Updates

**`.devcontainer/devcontainer.json`:**
- Added `containerEnv` with non-sensitive environment variables
- Mounted user secrets directory from host
- Added container mode ports (5174, 5130, 5232) plus Logic Host HTTPS forwarding (7096)
- Camera agent receives public identity authority env vars for interactive flows

### 5. Project Configuration

**`src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj`:**
- Configure `UserSecretsId` via `dotnet user-secrets init` if you need per-developer secrets
- Ready to use with `dotnet user-secrets` commands or environment variables supplied by Compose/Testcontainers

## 🔐 Secrets Classification

### Non-Sensitive (Tracked in Git)

These can be in `.env.development` or `appsettings.json`:

```bash
USE_CONTAINERS=true
ASPNETCORE_ENVIRONMENT=Development
DOCKER_HOST_ADDRESS=0.0.0.0
REDIS_PORT=6379
POSTGRES_PORT=5432
MINIO_API_PORT=9000
LOGIC_HOST_HTTPS_PORT=7096
CAMERA_AGENT_IDENTITY_PUBLIC_URL=https://localhost:7096
```

### Sensitive (Never Commit)

These go in User Secrets or environment variables:

```bash
# MinIO credentials
MINIO_ROOT_USER=<username>
MINIO_ROOT_PASSWORD=<password>

# PostgreSQL credentials
POSTGRES_USER=<username>
POSTGRES_PASSWORD=<password>

# Future secrets
JWT_SECRET_KEY=<secret>
ENCRYPTION_KEY=<key>
```

## 🚀 Quick Start

### For New Developers

1. **Clone repository**
   ```bash
   git clone <repo-url>
   cd HVO.SkyMonitor
   ```

2. **Open in Dev Container** (VS Code)
   - Press `F1` → "Dev Containers: Reopen in Container"
   - Wait for container to build and install dependencies

3. **Optional: Configure secrets**
   ```bash
   cd src/HVO.SkyMonitor.LogicHost
   dotnet user-secrets init
   dotnet user-secrets set "MinIO:AccessKey" "dev-minio"
   dotnet user-secrets set "MinIO:SecretKey" "dev-minio-secret"
   ```
   _or_ create `.devcontainer/devcontainer.local.env` with the same values.

4. **Start infrastructure and run the app**
   ```bash
   ./scripts/infra:start postgres minio redis smtp
   dotnet run --project src/HVO.SkyMonitor.LogicHost --configuration Debug
   ```

### Default Credentials

If you don't set user secrets, development defaults are used:

- **MinIO:** `minioadmin` / `minioadmin`
- **PostgreSQL:** `postgres` / `postgres`

Access MinIO Console at http://localhost:9001 with these credentials.

## 📋 Configuration Priority

Secrets are loaded in this order (later overrides earlier):

1. **Development defaults** (`.env.development`)
2. **User Secrets** (`dotnet user-secrets`)
3. **Environment Variables** (`export MINIO_ROOT_USER=...`)
4. **Command-line** (if explicitly passed)

## 🏗️ Environment Setup

### Local Development

```bash
# Option 1: Use defaults (Docker Compose stack + direct run)
./scripts/infra:start
dotnet run --project src/HVO.SkyMonitor.LogicHost --configuration Debug

# Option 2: Use custom user secrets
cd src/HVO.SkyMonitor.LogicHost
dotnet user-secrets set "MinIO:AccessKey" "custom-user"
dotnet user-secrets set "MinIO:SecretKey" "custom-pass"
dotnet run --configuration Debug

# Option 3: Use environment variables/.env overrides
export MINIO_ROOT_USER=env-user
export MINIO_ROOT_PASSWORD=env-pass
./scripts/infra:start
dotnet run --project src/HVO.SkyMonitor.LogicHost
```

### CI/CD (GitHub Actions)

Configure repository secrets:

1. Go to **Settings → Secrets and variables → Actions**
2. Add secrets:
   - `MINIO_ROOT_USER_DEV`
   - `MINIO_ROOT_PASSWORD_DEV`
   - `POSTGRES_PASSWORD_DEV`
   - (repeat for `_PROD` variants)

### Production Deployment

Use Azure Key Vault:

```bash
# Store secrets in Key Vault
az keyvault secret set --vault-name <vault-name> \
  --name "MinIO--Username" --value "prod-user"
az keyvault secret set --vault-name <vault-name> \
  --name "MinIO--Password" --value "prod-password"
```

## ✅ Security Checklist

- [x] Hardcoded credentials removed from source code
- [x] `.gitignore` excludes all secret files
- [x] User Secrets enabled for local development
- [x] DevContainer mounts user secrets directory
- [x] Environment variable fallbacks configured
- [x] Development defaults are non-sensitive
- [x] Documentation for all secret management approaches
- [x] GitHub Actions secrets documented
- [x] Azure Key Vault integration documented

## 📚 Documentation Links

- **Quick Start:** `docs/SECRETS_QUICKSTART.md`
- **Complete Guide:** `docs/SECRETS_MANAGEMENT.md`
- **GitHub Actions:** `.github/workflows/README.md`
- **Environment Template:** `.env.template`

## 🔄 Next Steps

1. **Review and test** - Run the application to verify secrets load correctly
2. **Set production secrets** - Configure Azure Key Vault or environment variables
3. **Update CI/CD** - Add GitHub Secrets for automated deployments
4. **Rotate credentials** - Change default development credentials for shared environments
5. **Document custom secrets** - Add any project-specific secrets to templates

## ⚠️ Important Notes

- **Never commit `.env` file** - It's in `.gitignore` but be careful
- **User Secrets are per-developer** - Each team member sets their own
- **Rotate production secrets** - Change credentials every 90 days
- **Use different credentials per environment** - Dev, Staging, Production should all differ
- **Audit secret access** - Review logs and access patterns regularly
