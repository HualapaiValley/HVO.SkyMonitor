# Secrets & Configuration Guide

This guide replaces `SECRETS_MANAGEMENT.md`, `SECRETS_QUICKSTART.md`,
`SECRETS_SUMMARY.md`, and `identity/secrets-reference.md`. It captures
how we handle configuration from local development through production and
summarizes every sensitive value the platform requires.

> Use the Docker/Testcontainers toolchain (`./scripts/infra:*`,
> `docker-compose.dev.yml`, and direct project runs under `src/`) when
> applying the steps below. Aspire/AppHost flows are no longer supported.

## 1. Layered Configuration Model

| Layer | Usage | Notes |
| --- | --- | --- |
| `appsettings*.json` | Non-sensitive defaults | Keep checked into git; document defaults only. |
| `.env` / `.env.development` | Developer overrides (non-secret) | Copy `.env.template` and keep it git-ignored. |
| .NET User Secrets | Local secrets for any project (`dotnet user-secrets`) | Preferred for developers and Testcontainers. |
| Dev Container env files | `.devcontainer/devcontainer.local.env` | Git-ignored opt-in for contributors who prefer env files. |
| Environment variables | Runtime overrides (containers, CI, production) | Use double underscores for nested config (`MINIO__ACCESSKEY`). |
| Azure Key Vault | Production/staging secrets | Add via `builder.Configuration.AddAzureKeyVault(...)`. |
| GitHub Secrets | CI/CD pipelines | Documented in `.github/workflows/README.md`. |

Configuration sources later in the list override earlier ones. Use
User Secrets or env vars for anything sensitive; never commit secrets
into git.

## 2. Quick Start (Local Development)

```bash
cp .env.template .env                # Non-sensitive defaults (ports, contexts)
./scripts/infra:start postgres minio redis smtp
cd src/HVO.SkyMonitor.LogicHost

# Optional: initialize user secrets and provide credentials
 dotnet user-secrets init
 dotnet user-secrets set "MinIO:AccessKey" "dev-minio"
 dotnet user-secrets set "MinIO:SecretKey" "dev-minio-secret"
 dotnet user-secrets set "PostgreSQL:Username" "postgres"
 dotnet user-secrets set "PostgreSQL:Password" "postgres"
 dotnet user-secrets set "SignedTicket:Secret" "$(openssl rand -base64 32)"

# Run the host
cd ../..
dotnet run --project src/HVO.SkyMonitor.LogicHost --configuration Debug
```

Default development credentials (when you skip secrets):
- MinIO: `minioadmin` / `minioadmin`
- PostgreSQL: `postgres` / `postgres`

## 3. Secrets Catalog

### 3.1 Non-Sensitive (stay in git)

```
USE_CONTAINERS=true
ASPNETCORE_ENVIRONMENT=Development
DOTNET_ENVIRONMENT=Development
DOCKER_HOST_ADDRESS=0.0.0.0
REDIS_PORT=6379
POSTGRES_PORT=5432
MINIO_API_PORT=9000
SKYMONITOR_HTTP_PORT=5174
```

### 3.2 Sensitive (never in git)

| Key | Description | Min Entropy | Recommended Store |
| --- | --- | --- | --- |
| `MinIO:AccessKey`, `MinIO:SecretKey` | Object storage admin credentials | strong password | User Secrets (dev) / Key Vault (prod) |
| `PostgreSQL:Username`, `PostgreSQL:Password` | Database login | strong password | User Secrets / Key Vault |
| `SignedTicket:Secret` | HMAC key for signed URLs | 256-bit random | User Secrets / Key Vault |
| `ApiKey:HashingSalt` | Optional salt for API key hashing | 256-bit random | Key Vault |
| `OpenIddict:*Certificate:*` | Signing/encryption certificates | RSA 4096 | Key Vault / secure file mount |
| `DeviceBootstrap:CentralIdentity:ClientCredentials:*` | Scoped client for camera agents | depends | Key Vault |
| `Kestrel:Certificates:Default:*` | HTTPS certificate | n/a | Key Vault / file mount |
| `DataProtection:KeyVaultUri` | URI used for key persistence | n/a | appsettings / env |

Generate random material with `openssl rand -base64 32` (Linux/macOS)
or the PowerShell equivalent shown in the legacy docs.

## 4. Environment-Specific Guidance

### Development + Dev Container
- `.env.template` → `.env` for ports, Docker contexts, and other non-secret
  overrides.
- User Secrets are automatically mounted inside the Dev Container
  (`~/.microsoft/usersecrets`).
- Optional `.devcontainer/devcontainer.local.env` holds extra env vars and
  stays git-ignored.

### CI/CD (GitHub Actions)
- Add required secrets under **Settings → Secrets and variables → Actions**.
- Minimum set: `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`,
  `POSTGRES_PASSWORD`, image-registry credentials, and any deployment
  secrets called out in `.github/workflows/README.md`.
- Reference them via `${{ secrets.NAME }}` inside workflow yaml.

### Production / Staging
- Store certificates, database credentials, signed-ticket secrets, and
  Central Identity client secrets in Azure Key Vault.
- Configure the app in `Program.cs`:
  ```csharp
  builder.Configuration.AddAzureKeyVault(
      new Uri(builder.Configuration["KeyVaultUri"]!),
      new DefaultAzureCredential());
  ```
- Persist ASP.NET Data Protection keys to Azure Storage/Key Vault so
  multiple instances share encryption material.
- Use environment variables to point Kestrel at the mounted HTTPS
  certificate:
  ```bash
  export KESTREL__CERTIFICATES__DEFAULT__PATH=/certs/skymonitor.pfx
  export KESTREL__CERTIFICATES__DEFAULT__PASSWORD="<secret>"
  ```

## 5. Identity-Specific Secrets

### 5.1 OpenIddict Certificates
- Development: `AddDevelopmentEncryptionCertificate()` and
  `AddDevelopmentSigningCertificate()` already generate ephemeral keys.
- Production: supply `.pfx` files + passwords (store in Key Vault).
- Rotate at least every 12 months; load both old and new certs during the
  cutover window to avoid downtime.

### 5.2 Signed URL HMAC
- `SignedTicket:Secret` must be a 32-byte (256-bit) key.
- Configure TTL, skew, and allowed path list via appsettings (non-secret).
- Rotate every 90 days. During rotation, accept both old and new secrets
  until the previous TTL expires.

### 5.3 API Key Hashing Salt
- Optional enhancement that mixes a random salt into API key hashes.
- Rotation requires issuing new API keys because plaintext values are
  never stored.

### 5.4 Camera Agent Bootstrap Bundle
- `DeviceBootstrap:CentralIdentity:*` supplies the scoped confidential
  client we hand to camera agents during registration.
- Store `ClientSecret` in Key Vault and scope the granted API permissions
  to only what agents need (`api.camera`, `api.frames`, `api.images`).

### 5.5 Data Protection & TLS
- Persist keys to Key Vault/Azure Storage for multi-instance
  deployments:
  ```csharp
  builder.Services.AddDataProtection()
      .PersistKeysToAzureBlobStorage(new Uri(builder.Configuration["DataProtection:BlobUri"]!))
      .ProtectKeysWithAzureKeyVault(new Uri(builder.Configuration["DataProtection:KeyVaultKeyUri"]!), new DefaultAzureCredential())
      .SetApplicationName("HVO.SkyMonitor");
  ```
- Configure production HTTPS endpoints via `Kestrel:Certificates` or a
  fronting ingress (Application Gateway, Front Door, etc.).

## 6. Secret Rotation Checklist

| Secret | Frequency | Notes |
| --- | --- | --- |
| Signed URL HMAC | 90 days | Accept both old/new secrets during overlap window. |
| MinIO / PostgreSQL credentials | 90 days (shared env) | Update secrets store first, then recycle containers. |
| OpenIddict certificates | 12 months | Load new cert alongside old before revoking. |
| Camera agent confidential client | With any suspected compromise | Update envelope service and restart LogicHost. |
| TLS certificates | Per CA lifetime | Automate with Let's Encrypt or Key Vault rotation. |

## 7. Troubleshooting

| Symptom | Checks |
| --- | --- |
| Secret not loading locally | `dotnet user-secrets list --project <csproj>` and confirm `UserSecretsId` exists in the `.csproj`. |
| Container cannot see secrets | Ensure `.env` or `--env-file` is passed, or exec into the container and run `env` to confirm values. |
| CI build missing secret | Verify GitHub repo/organization secrets scope and workflow name matches. |
| Token/signature errors after rotation | Confirm both old and new certs/secrets were deployed during the overlap period before revoking the old value. |

## 8. References

- `.env.template` – list of non-sensitive environment variables.
- `.github/workflows/README.md` – CI secrets inventory.
- `docs/runbooks/local-dev.md` – end-to-end local workflow that links back
  to this guide.
- `docs/identity/operations-runbook.md` – operational procedures (key
  rotation, incident response) for Central Identity.
