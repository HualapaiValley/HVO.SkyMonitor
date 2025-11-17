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

### Direct execution only

```bash
dotnet run --project src/HVO.SkyMonitor/HVO.SkyMonitor.csproj
```

## Endpoints

- **Web UI**: `http://localhost:5174` (when running as container) or dynamic port (when running as project)
- **API**: `/api/v1.0/status`
- **Health Check**: `/health`
- **Metrics**: `/metrics`
- **API Documentation**: `/scalar/v1`
- **OpenAPI Spec**: `/openapi/v1.json`

## Configuration

See `appsettings.json` and `appsettings.Development.json` for configuration options.

Use User Secrets for sensitive configuration in development:

```bash
dotnet user-secrets set "MinIO:Username" "your-username"
dotnet user-secrets set "MinIO:Password" "your-password"
```

## Database Migrations

Apply migrations:

```bash
dotnet ef database update
```

Create new migration:

```bash
dotnet ef migrations add MigrationName
```
