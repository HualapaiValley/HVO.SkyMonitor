# HVO.SkyMonitor.CameraAgent.ZWO

Blazor Server application that emulates a SkyMonitor camera agent, mirroring the SkyMonitor V6 SampleApp layout, API pipeline, and security model. It is powered by ASP.NET Core Identity, API key policies, OpenAPI/Scalar, and a locally hosted copy of the `hvo-dark` theme.

## Highlights

- **Modern UI**: Main layout, reconnect modal, scoped CSS/JS, and shared components copied from the V6 SampleApp while loading the theme from `wwwroot/css/themes/hvo-dark.css`.
- **Identity + API Keys**: Full ASP.NET Core Identity scaffolding with passkey support, API key management, and reusable authorization policies (`ApiKeyOrCookie`, `ApiKeyRead`, `ApiKeyReadWrite`).
- **Sample APIs**: Versioned `/api/v1.0/sample/*` endpoints backed by `Result<T>` services for deterministic ZWO agent responses.
- **Diagnostics**: Structured JSON logging, custom correlation-id middleware, ProblemDetails enrichment, OpenTelemetry metrics/traces, Scalar UI, Prometheus scraping, and health checks.
- **SQLite Storage**: Identity + API key tables managed through EF Core migrations stored under `Data/Migrations`.

## Run It

```bash
cd /workspaces/HVO.SkyMonitor/src/HVO.SkyMonitor.CameraAgent.ZWO
dotnet run
```

- UI: `http://localhost:5130/`
- API: `http://localhost:5130/api/v1.0/sample/status`
- Health: `http://localhost:5130/health`
- Scalar UI: `http://localhost:5130/scalar/v1`
- Prometheus: `http://localhost:5130/metrics`

## Database & Migrations

SQLite lives under `Data/cameraagentzwo.db`. Apply or create migrations with:

```bash
dotnet ef database update --project src/HVO.SkyMonitor.CameraAgent.ZWO

dotnet ef migrations add <MigrationName> --project src/HVO.SkyMonitor.CameraAgent.ZWO
```

The app applies pending migrations automatically on startup.

## Configuration

- `appsettings.json` contains the `DefaultConnection` string pointing at `Data/cameraagentzwo.db` plus standard logging configuration.
- Data-protection keys persist under `DataProtection-Keys/` so browser sessions survive restarts.

## Theme Usage

`Components/App.razor` references the local copy of the HVO Dark theme. Scoped CSS files under `Components/**/*.razor.css` build on that palette to keep parity with SkyMonitor V6.

## Docker

The existing `Dockerfile` still works for container builds. Run `docker build -t hvo-cameraagent-zwo -f src/HVO.SkyMonitor.CameraAgent.ZWO/Dockerfile .` from the repo root when you need an image.
