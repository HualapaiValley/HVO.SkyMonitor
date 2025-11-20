# Secrets and Environment Variables Management

> [!IMPORTANT]
> This guide reflects the current Docker Compose + Testcontainers workflow. Use the scripts under `./scripts` (for example `./scripts/infra:start`) together with direct project runs (`src/HVO.SkyMonitor.LogicHost`, camera agents) when applying the steps below.

## Overview

HVO.SkyMonitor uses a layered approach to manage configuration and secrets:

1. **Non-sensitive configuration** → Environment variables or `appsettings.json`
2. **Development secrets** → .NET User Secrets
3. **Production secrets** → Azure Key Vault or environment variables
4. **CI/CD secrets** → GitHub Secrets

**Identity Hardening Reference:** For comprehensive identity-specific secrets documentation (OpenIddict keys, signed URL HMAC, rate limiting, etc.), see [identity/secrets-reference.md](identity/secrets-reference.md).

## Security Principles

- **Never commit secrets to source control**
- Use User Secrets for local development
- Use Azure Key Vault or managed secrets in production
- Use GitHub Secrets for CI/CD pipelines
- Rotate secrets regularly (every 90 days for Identity Hardening auth secrets)
- Use different secrets for each environment
- Use minimum key lengths: 256 bits for HMAC, 4096 bits for RSA

## Environment Variables

### Non-Sensitive Configuration

Located in `.env.template` (copy to `.env` for local development):

```bash
# Application Mode
USE_CONTAINERS=true

# ASP.NET Core Environment
ASPNETCORE_ENVIRONMENT=Development
DOTNET_ENVIRONMENT=Development

# Docker Configuration
DOCKER_HOST_ADDRESS=0.0.0.0

# Service Ports
REDIS_PORT=6379
POSTGRES_PORT=5432
MINIO_API_PORT=9000
MINIO_CONSOLE_PORT=9001
```

### Development Defaults

`.env.development` contains non-sensitive defaults for local development:

```bash
MINIO_ROOT_USER=minioadmin
MINIO_ROOT_PASSWORD=minioadmin
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
```

**Note:** These are development-only defaults. Override with User Secrets for custom values.

## .NET User Secrets

User Secrets provide secure local storage for development secrets outside the project directory.

### Setup User Secrets

Initialize User Secrets for the primary web app or any agent you need to run locally:

```bash
cd src/HVO.SkyMonitor.LogicHost
dotnet user-secrets init

# Optional: initialize secrets for a camera agent
cd ../HVO.SkyMonitor.CameraAgent
dotnet user-secrets init
```

This adds/updates the `UserSecretsId` property inside the corresponding `.csproj` file.

### Setting Secrets

```bash
# MinIO Credentials
dotnet user-secrets set "MinIO:AccessKey" "your-access-key" \
  --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj
dotnet user-secrets set "MinIO:SecretKey" "your-secret-key" \
  --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj

# PostgreSQL Credentials  
dotnet user-secrets set "PostgreSQL:Username" "your-db-user" \
  --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj
dotnet user-secrets set "PostgreSQL:Password" "your-db-password" \
  --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj

# Other Secrets (example)
dotnet user-secrets set "JwtSettings:SecretKey" "your-jwt-secret" \
  --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj
```

### Listing Secrets

```bash
dotnet user-secrets list
```

### Removing Secrets

```bash
# Remove specific secret
dotnet user-secrets remove "MinIO:AccessKey"

# Remove all secrets
dotnet user-secrets clear
```

### Where Secrets are Stored

**Windows:** `%APPDATA%\Microsoft\UserSecrets\<user_secrets_id>\secrets.json`

**Linux/macOS:** `~/.microsoft/usersecrets/<user_secrets_id>/secrets.json`

Secrets are stored per-user and never committed to source control.

## Development Container Environment

### DevContainer Configuration

The `.devcontainer/devcontainer.json` includes non-sensitive environment variables:

```jsonc
"containerEnv": {
  "ASPNETCORE_ENVIRONMENT": "Development",
  "DOTNET_ENVIRONMENT": "Development",
  "USE_CONTAINERS": "true"
}
```

### Loading Secrets in DevContainer

User secrets are automatically available in the dev container because they're stored in the user's home directory, which is mounted.

To use `.env` files in the devcontainer:

1. Copy `.env.template` to `.env`
2. Add to `.devcontainer/devcontainer.json`:

```jsonc
"runArgs": ["--env-file", "${localWorkspaceFolder}/.env"]
```

## Production Secrets

### Azure Key Vault (Recommended)

For production deployments, use Azure Key Vault:

```bash
# Install Azure Key Vault configuration provider
dotnet add package Azure.Extensions.AspNetCore.Configuration.Secrets
```

**Configure in `Program.cs`:**

```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri($"https://{builder.Configuration["KeyVaultName"]}.vault.azure.net/"),
    new DefaultAzureCredential());
```

**Set secrets in Azure Key Vault:**

```bash
az keyvault secret set --vault-name <your-vault> --name "MinIO--AccessKey" --value "your-key"
az keyvault secret set --vault-name <your-vault> --name "MinIO--SecretKey" --value "your-secret"
```

### Environment Variables (Alternative)

Set secrets as environment variables in your production environment:

```bash
export MINIO__ACCESSKEY="your-access-key"
export MINIO__SECRETKEY="your-secret-key"
export POSTGRESQL__PASSWORD="your-db-password"
```

## GitHub Secrets (CI/CD)

### Setting Repository Secrets

1. Navigate to your repository on GitHub
2. Go to **Settings** → **Secrets and variables** → **Actions**
3. Click **New repository secret**

### Required Secrets for CI/CD

```
MINIO_ROOT_USER          # MinIO admin username
MINIO_ROOT_PASSWORD      # MinIO admin password
POSTGRES_PASSWORD        # PostgreSQL password
DOCKER_USERNAME          # Docker registry username (if pushing images)
DOCKER_PASSWORD          # Docker registry password (if pushing images)
AZURE_CREDENTIALS        # Azure service principal (if deploying to Azure)
```

### Using Secrets in GitHub Actions

```yaml
name: Build and Deploy

on:
  push:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Build with secrets
        env:
          MINIO_ROOT_USER: ${{ secrets.MINIO_ROOT_USER }}
          MINIO_ROOT_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD }}
        run: |
          dotnet build
```

## Configuration Priority

.NET configuration sources are loaded in this order (later sources override earlier):

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. User Secrets (Development environment only)
4. Environment Variables
5. Command-line arguments

**Example:** An environment variable `MINIO__ACCESSKEY=prod-key` overrides the same key from User Secrets.

## Secrets in Container Mode

When running in container mode, secrets must be passed as environment variables:

```bash
# Pass secrets to containers (simplified example)
docker run -e MINIO__ACCESSKEY="your-key" \
           -e MINIO__SECRETKEY="your-secret" \
           hvo-skymonitor:latest
```

The `scripts/infra:*` helpers wrap the split compose stacks (`docker compose -f docker-compose.infrastructure.yml ...` and `docker compose -f docker-compose.apps.yml ...`) and forward values from `.env`, `.devcontainer/devcontainer.local.env`, and your shell session so you don't have to specify them manually for local development.

## Secret Rotation

### Best Practices

1. **Rotate secrets regularly** (every 90 days minimum)
2. **Use different secrets per environment**
3. **Audit secret access** in production
4. **Remove unused secrets** immediately
5. **Never log secrets** or include in error messages

### Rotating MinIO Credentials

```bash
# 1. Update in User Secrets (Development)
dotnet user-secrets set "MinIO:AccessKey" "new-access-key"
dotnet user-secrets set "MinIO:SecretKey" "new-secret-key"

# 2. Update in Azure Key Vault (Production)
az keyvault secret set --vault-name <vault> --name "MinIO--AccessKey" --value "new-key"

# 3. Restart applications to pick up new secrets
```

### Rotating Database Passwords

```bash
# 1. Update password in database
psql -U postgres -c "ALTER USER skymonitor WITH PASSWORD 'new-password';"

# 2. Update in secrets store
dotnet user-secrets set "PostgreSQL:Password" "new-password"

# 3. Restart applications
```

## Troubleshooting

### Secret Not Found

**Error:** Configuration value is null or empty

**Solution:**
1. Verify secret is set: `dotnet user-secrets list`
2. Check configuration key matches exactly (case-sensitive)
3. Ensure User Secrets ID matches between `.csproj` and storage location

### Secret Not Available in Container

**Error:** Application can't connect to services

**Solution:**
1. Verify secrets are passed via environment variables
2. Check `WithEnvironment` calls in `Program.cs`
3. Use `docker exec <container> env` to verify environment variables

### Secrets Committed to Git

**If you accidentally commit secrets:**

1. **Rotate the exposed secrets immediately**
2. Remove from Git history:
   ```bash
   git filter-branch --force --index-filter \
     "git rm --cached --ignore-unmatch path/to/file" \
     --prune-empty --tag-name-filter cat -- --all
   ```
3. Force push (if safe): `git push origin --force --all`
4. Consider the secrets compromised and generate new ones

## Additional Resources

- [.NET User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets)
- [Azure Key Vault](https://learn.microsoft.com/azure/key-vault/)
- [GitHub Secrets](https://docs.github.com/actions/security-guides/encrypted-secrets)
- [ASP.NET Core Configuration](https://learn.microsoft.com/aspnet/core/fundamentals/configuration/)
