# HVO.SkyMonitor.CameraAgent

Blazor Server application that emulates a SkyMonitor camera agent, mirroring the SkyMonitor V6 SampleApp layout, API pipeline, and security model. It is powered by ASP.NET Core Identity, passkey support, OpenAPI/Scalar, and a locally hosted copy of the `hvo-dark` theme.

## Highlights

- **Modern UI**: Main layout, reconnect modal, scoped CSS/JS, and shared components copied from the V6 SampleApp while loading the theme from `wwwroot/css/themes/hvo-dark.css`.
- **Identity + Passkeys**: Full ASP.NET Core Identity scaffolding with passkey support, profile management, and reusable cookie-based authorization.
- **Sample APIs**: Versioned `/api/v1.0/sample/*` endpoints backed by `Result<T>` services for deterministic sample responses.
- **Diagnostics**: Structured JSON logging, custom correlation-id middleware, ProblemDetails enrichment, OpenTelemetry metrics/traces, Scalar UI, Prometheus scraping, and health checks.
- **SQLite Storage**: Identity tables managed through EF Core migrations stored under `Data/Migrations`.

## Run It

```bash
cd /workspaces/HVO.SkyMonitor/src/HVO.SkyMonitor.CameraAgent
dotnet run
```

- UI: `http://localhost:5130/`
- API: `http://localhost:5130/api/v1.0/sample/status`
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
