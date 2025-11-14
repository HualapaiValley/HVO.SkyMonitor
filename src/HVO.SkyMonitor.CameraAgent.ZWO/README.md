# HVO.SkyMonitor.CameraAgent.ZWO

ZWO camera hardware agent for controlling ZWO astronomical cameras.

## Features

- **Authentication**: ASP.NET Core Identity with cookie authentication
- **Database**: SQLite (local file: `cameraagent_zwo.db`)
- **API Versioning**: URL-based versioning (v1.0)
- **Documentation**: OpenAPI + Scalar UI
- **Observability**: OpenTelemetry, Prometheus metrics, structured JSON logging
- **Health Checks**: Database connectivity

## Running with Aspire AppHost

By default, the AppHost runs this project as a .NET project for full debugging and telemetry integration.

## Docker Container Support

The project includes a `Dockerfile` for containerized deployment. To run as a Docker container in the AppHost, add the following to `HVO.SkyMonitor.AppHost/Program.cs`:

### Replace the ZWO Agent project definition with:

```csharp
// Camera Agent: ZWO
builder.AddDockerfile("zwo-agent", "../../", "src/HVO.SkyMonitor.CameraAgent.ZWO/Dockerfile")
    .WithHttpEndpoint(port: 5232, targetPort: 8080, name: "http")
    .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
    .WithLifetime(ContainerLifetime.Session)
    .WaitFor(skymonitor)
    .WithExternalHttpEndpoints();
```

**Note**: Make sure the `skymonitor` variable references the correct resource (project or container).

## Endpoints

- **Web UI**: Dynamic port (when running as project) or `http://localhost:5232` (when running as container)
- **API**: `/api/v1.0/status`
- **Health Check**: `/health`
- **Metrics**: `/metrics`
- **API Documentation**: `/scalar/v1`
- **OpenAPI Spec**: `/openapi/v1.json`

## Configuration

See `appsettings.json` and `appsettings.Development.json` for configuration options.

The `SkyMonitor__BaseUrl` environment variable is set by the AppHost to connect to the main SkyMonitor application.

## Database

The SQLite database file (`cameraagent_zwo.db`) is created automatically on first run.

Apply migrations:

```bash
dotnet ef database update
```

Create new migration:

```bash
dotnet ef migrations add MigrationName
```

## ZWO SDK

This project is designed to integrate with the ZWO ASI camera SDK. SDK integration code will be added in future development.
