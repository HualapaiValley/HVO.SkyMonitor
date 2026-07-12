# HVO.SkyMonitor.CameraAgent

Blazor Server host for a self-contained SkyMonitor camera agent. It provides local identity, operational UI, capture APIs, and configuration-driven camera/pipeline hosting while remaining independent of LogicHost.

## Highlights

- **Modern UI**: Main layout, reconnect modal, scoped CSS/JS, and shared components using the local `hvo-dark` theme.
- **Identity**: Local-only ASP.NET Core Identity with confirmation email flow, cookie auth, and profile/email/password management pages.
- **Frame APIs**: Versioned `/api/v1.0/frames` endpoints that stream the most recent exposure from the simulated capture pipeline.
- **Diagnostics**: Structured JSON logging, custom correlation-id middleware, ProblemDetails enrichment, OpenTelemetry metrics/traces, Scalar UI, Prometheus scraping, and health checks.
- **SQLite Storage**: Identity tables managed through EF Core migrations stored under `Data/Migrations`.

## Run It

```bash
cd /workspaces/HVO.SkyMonitor/src/HVO.SkyMonitor.CameraAgent
dotnet run
```

- UI: `http://localhost:5130/`
- API: `http://localhost:5130/api/v1.0/frames/latest`
- Health: `http://localhost:5130/health`
- Scalar UI: `http://localhost:5130/scalar/v1`
- Prometheus: `http://localhost:5130/metrics`

## Database & Migrations

SQLite lives under `App_Data/cameraagent_identity.db` by default (override via `LocalIdentity:DatabasePath`). Apply or create migrations with:

```bash
dotnet ef database update --project src/HVO.SkyMonitor.CameraAgent

dotnet ef migrations add <MigrationName> --project src/HVO.SkyMonitor.CameraAgent
```

The app applies pending migrations automatically on startup.

## Configuration

- `appsettings.json` contains the `LocalIdentity` section (admin email/password/username) and optional `DatabasePath` override used during seeding.
- Data-protection keys persist under `DataProtection-Keys/` (or the path you mount in Docker) so browser sessions survive restarts.

## Theme Usage

`Components/App.razor` references the local copy of the HVO Dark theme. Scoped CSS files under `Components/**/*.razor.css` build on that palette to keep parity with SkyMonitor V6.

## Docker

The existing `Dockerfile` still works for container builds. Run `docker build -t hvo-cameraagent -f src/HVO.SkyMonitor.CameraAgent/Dockerfile .` from the repo root when you need an image.
