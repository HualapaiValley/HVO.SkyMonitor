# HVO.SkyMonitor

Main sky monitoring application with full authentication, API versioning, and observability.

## Features

- **Authentication**: ASP.NET Core Identity with cookie authentication + API Key authentication
- **Database**: PostgreSQL (managed via Docker Compose or Testcontainers)
- **API Versioning**: URL-based versioning (v1.0)
- **Documentation**: OpenAPI + Scalar UI
- **Observability**: OpenTelemetry, Prometheus metrics, structured JSON logging
- **Health Checks**: Database connectivity

> [!NOTE]
> The legacy Aspire AppHost has been removed. Use the Docker Compose scripts under `../../scripts` or run the project directly with `dotnet run` for debugging.

## Running Locally

### Docker Compose (recommended)

```bash
./scripts/infra:start postgres redis minio
dotnet run --project src/HVO.SkyMonitor/HVO.SkyMonitor.csproj
```
Stop the containers when you're done via `./scripts/infra:stop` (use `--clear-cache` to wipe Redis/MinIO/Postgres volumes during shutdown).

### Direct execution only

```bash
dotnet run --project src/HVO.SkyMonitor/HVO.SkyMonitor.csproj
```

## Endpoints

- **Web UI**: `http://localhost:5174` (when running as container) or dynamic port (when running as project)
- **API**: `/api/v1.0/status`
- **Health Check**: `/health` (no auth required; reports database, Redis, MinIO, SMTP status)
- **Metrics**: `/metrics`
- **API Documentation**: `/scalar/v1`
- **OpenAPI Spec**: `/openapi/v1.json`

> Quick probe: `curl http://localhost:5174/health` returns a JSON payload with each dependency's state plus the overall status. `/alive` remains a lightweight liveness check used by container orchestrators.

## Configuration

See `appsettings.json` and `appsettings.Development.json` for configuration options.

Use User Secrets for sensitive configuration in development:

```bash
dotnet user-secrets set "MinIO:Username" "your-username"
dotnet user-secrets set "MinIO:Password" "your-password"
```

## Development Accounts & Credentials

During startup the `DatabaseSeeder` populates standard test identities, API keys, and OAuth/OIDC clients from the shared `HVO.SkyMonitor.TestSupport` project. These are safe for local development only—change them before deploying anywhere else.

### Interactive Users (ASP.NET Core Identity)

| Role | Email | Username | Password | Notes |
| --- | --- | --- | --- | --- |
| Administrator | `admin@skymonitor.local` | `admin` | `Admin123!@#` | Full system access (Administrator/Operator/Viewer roles) |
| Operator | `operator@skymonitor.local` | `operator` | `Operator123!@#` | Operations + Viewer permissions |
| Viewer | `viewer@skymonitor.local` | `viewer` | `Viewer123!@#` | Read-only access |
| Regular | `user@skymonitor.local` | `user` | `User123!@#` | No elevated roles |
| System Service | `system@skymonitor.local` | `system-service` | _No password_ | Used for API keys/client credentials only |

### API Keys (sent via `x-api-key` header)

| Purpose | Raw Key | Scopes |
| --- | --- | --- |
| Camera Agent | `test-camera-agent-key-12345678901234567890123456789012` | `api.camera`, `api.frames` |
| Internal Service | `test-internal-service-key-12345678901234567890123456789012` | `api.admin`, `api.camera`, `api.frames`, `api.images` |
| Webhook | `test-webhook-key-12345678901234567890123456789012` | `api.webhooks` |
| Read Only | `test-readonly-key-12345678901234567890123456789012` | `api.viewer` |

### OAuth / OIDC Clients

| Client | Type | Client ID | Secret | Scopes | Notes |
| --- | --- | --- | --- | --- | --- |
| Camera Agent | Confidential | `system-camera-agent` | `test-camera-agent-secret-do-not-use-in-production` | `api.camera`, `api.frames`, `api.images` | Client Credentials flow |
| Internal Service | Confidential | `system-internal` | `test-internal-secret-do-not-use-in-production` | `api.admin`, `api.camera`, `api.frames`, `api.images` | Client Credentials flow |
| Web UI | Public (PKCE) | `web-ui` | _(none)_ | `openid`, `profile`, `email`, `api.viewer` | Redirect URIs `http://localhost:5000/signin-oidc`, `https://localhost:5001/signin-oidc` |
| Mobile App | Public (PKCE) | `mobile-app` | _(none)_ | `openid`, `profile`, `email`, `api.viewer`, `offline_access` | Redirect `com.skymonitor.mobile://auth-callback` |

To reset the data back to these defaults, stop the app, run `./scripts/infra:reset postgres`, then start `./scripts/infra:start postgres minio redis smtp` and relaunch the Logic Host.

## Database Migrations

Apply migrations:

```bash
dotnet ef database update
```

Create new migration:

```bash
dotnet ef migrations add MigrationName
```
