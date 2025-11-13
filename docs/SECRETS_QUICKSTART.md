# Quick Start: Secrets Setup

## Initial Development Setup

### 1. Copy Environment Template

```bash
cp .env.template .env
```

The `.env` file is git-ignored and can contain your local overrides.

### 2. Set User Secrets (Recommended for Custom Credentials)

```bash
cd src/HVO.SkyMonitor.AppHost

# Initialize user secrets (already done - UserSecretsId in .csproj)
dotnet user-secrets init

# Set MinIO credentials (optional - defaults to minioadmin)
dotnet user-secrets set "MinIO:Username" "your-username"
dotnet user-secrets set "MinIO:Password" "your-password"

# Set PostgreSQL credentials (optional - defaults to postgres)
# IMPORTANT: Use Parameters:postgres-password for Aspire's parameter system
dotnet user-secrets set "Parameters:postgres-password" "your-secure-password"
```

### 3. Verify Secrets

```bash
# List all secrets
dotnet user-secrets list

# Should show:
# MinIO:Username = your-username
# MinIO:Password = [hidden]
# Parameters:postgres-password = [hidden]
```

### 4. Run the Application

```bash
# From repository root
dotnet run --project src/HVO.SkyMonitor.AppHost --launch-profile http
```

The application will use:
1. Your user secrets (if set)
2. Or environment variables from `.env` (if present)
3. Or fallback to development defaults (minioadmin/minioadmin)

## Default Credentials

If you don't set custom secrets, the following defaults are used:

- **MinIO:** `minioadmin` / `minioadmin`
- **PostgreSQL:** `postgres` / `postgres`

These are safe for local development but **should be changed for any shared or production environment**.

## Access Services

Once running:

- **Aspire Dashboard:** http://localhost:15201
- **MinIO Console:** http://localhost:9001 (use MinIO credentials to log in)
- **Main App:** http://localhost:5174 (container mode) or dynamic port (project mode)

## Troubleshooting

### Secrets Not Loading

If your custom secrets aren't being used:

```bash
# Verify user secrets are set
cd src/HVO.SkyMonitor.AppHost
dotnet user-secrets list

# Check that UserSecretsId exists in .csproj
grep UserSecretsId HVO.SkyMonitor.AppHost.csproj
```

### Can't Access MinIO Console

If you see authentication errors in MinIO console:

1. Check MinIO container logs in Aspire Dashboard
2. Verify credentials match between:
   - Container environment variables
   - Your user secrets or .env file
   - MinIO console login attempt

### Dev Container Can't Find Secrets

User secrets should be mounted into the dev container. If not working:

1. Rebuild the dev container
2. Verify mount in `.devcontainer/devcontainer.json`:
   ```jsonc
   "mounts": [
     "source=${localEnv:HOME}${localEnv:USERPROFILE}/.microsoft/usersecrets,target=/home/vscode/.microsoft/usersecrets,type=bind,consistency=cached"
   ]
   ```

## Next Steps

- Read [SECRETS_MANAGEMENT.md](./SECRETS_MANAGEMENT.md) for complete documentation
- Set up GitHub Secrets for CI/CD (see workflow below)
- Configure Azure Key Vault for production deployments
