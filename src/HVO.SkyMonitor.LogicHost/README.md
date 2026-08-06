# HVO.SkyMonitor

Main sky monitoring application with full authentication, API versioning, and observability.

## Features

- **Authentication**: ASP.NET Core Identity with cookie authentication + API Key authentication
- **Database**: SQL Server (persistent shared instance or Testcontainers)
- **API Versioning**: URL-based versioning (v1.0)
- **Documentation**: OpenAPI + Scalar UI
- **Observability**: OpenTelemetry, Prometheus metrics, structured JSON logging
- **Health Checks**: Database connectivity

> [!NOTE]
> The legacy Aspire AppHost has been removed. Use the Docker Compose scripts under `../../scripts` or run the project directly with `dotnet run` for debugging.

## Running Locally

### Docker Compose (recommended)

```bash
./scripts/infra:start logichost
```
Stop local application containers when you're done via `./scripts/infra:stop`. Shared SQL Server, Redis, MinIO, and Mailpit remain running on `hvo-docker`.

### Direct execution only

```bash
./scripts/with-env dotnet run --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj
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

## Bootstrap Identities

Development and Testing startup create the non-interactive system account,
standard OAuth scopes, and the confidential client described by the effective
device-bootstrap identity configuration. Production performs this work only
through `--host-mode=database-initialize`; ordinary runtime validates the
completed initialization state without mutating it. Additional interactive
users, API keys, and OAuth/OIDC clients are seeded only when supplied through
the `DatabaseSeed` configuration section. Keep passwords, raw API keys, and
client secrets in user secrets or environment variables, not checked-in
settings. Integration tests inject their fixture credentials from
`HVO.SkyMonitor.TestSupport`; production does not reference that assembly.

Shared database resets are an operational action on `hvo-docker`, not a repository script. Reapply migrations and reseed as appropriate after an approved reset.

## Database Migrations

Production migrations, role grants, reviewed SQL evidence, and recovery follow
[`docs/runbooks/logichost-database-initialization.md`](../../docs/runbooks/logichost-database-initialization.md).
For local development, apply migrations directly when needed:

```bash
dotnet ef database update
```

Create new migration:

```bash
dotnet ef migrations add MigrationName
```
