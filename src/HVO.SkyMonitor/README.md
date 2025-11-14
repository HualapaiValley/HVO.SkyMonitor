# HVO.SkyMonitor

Main sky monitoring application with full authentication, API versioning, and observability.

## Features

- **Authentication**: ASP.NET Core Identity with cookie authentication + API Key authentication
- **Database**: PostgreSQL (managed by Aspire)
- **API Versioning**: URL-based versioning (v1.0)
- **Documentation**: OpenAPI + Scalar UI
- **Observability**: OpenTelemetry, Prometheus metrics, structured JSON logging
- **Health Checks**: Database connectivity

## Running with Aspire AppHost

By default, the AppHost runs this project as a .NET project for full debugging and telemetry integration.

## Docker Container Support

The project includes a `Dockerfile` for containerized deployment. To run as a Docker container in the AppHost, add the following to `HVO.SkyMonitor.AppHost/Program.cs`:

### Replace the SkyMonitor project definition with:

```csharp
// Build and run from Dockerfile (context is repo root, dockerfile path is relative)
// Note: OpenTelemetry export from Docker containers to Aspire dashboard is challenging
// in dev container environments because the OTLP endpoint binds to localhost only.
// For production, configure OTLP to export to an external collector.
// For development, structured logs are visible in container console output.

var skymonitor = builder.AddDockerfile("skymonitor", "../../", "src/HVO.SkyMonitor/Dockerfile")
    .WithHttpEndpoint(port: 5174, targetPort: 8080, name: "http")
    .WithReference(redis)
    .WithReference(postgressDatabase)
    .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
    .WithEnvironment("MinIO__AccessKey", minioUsername)
    .WithEnvironment("MinIO__SecretKey", minioPassword)
    .WithLifetime(ContainerLifetime.Session)
    .WaitFor(redis)
    .WaitFor(postgres)
    .WaitFor(minio)
    .WithExternalHttpEndpoints();
```

### Update Camera Agent references:

Change the Camera Agent `WaitFor` calls to use the containerized skymonitor resource.

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
