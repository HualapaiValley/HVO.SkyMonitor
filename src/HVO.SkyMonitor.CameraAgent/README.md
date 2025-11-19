# HVO.SkyMonitor.CameraAgent

Blazor Server application that emulates a SkyMonitor camera agent, mirroring the SkyMonitor V6 SampleApp layout, API pipeline, and security model. It is powered by ASP.NET Core Identity, API key policies, OpenAPI/Scalar, and a locally hosted copy of the `hvo-dark` theme.

## Highlights

- **Modern UI**: Main layout, reconnect modal, scoped CSS/JS, and shared components copied from the V6 SampleApp while loading the theme from `wwwroot/css/themes/hvo-dark.css`.
- **Live Telemetry Dashboard**: The landing page polls `ICaptureTelemetryProvider` every two seconds to display capture cadence, exposure, duty cycle, and a rolling history of recent frames.
- **Identity + API Keys**: Full ASP.NET Core Identity scaffolding with passkey support, API key management, and reusable authorization policies (`ApiKeyOrCookie`, `ApiKeyRead`, `ApiKeyReadWrite`).
- **Sample APIs**: Versioned `/api/v1.0/sample/*` endpoints backed by `Result<T>` services for deterministic sample responses.
- **Diagnostics**: Structured JSON logging, custom correlation-id middleware, ProblemDetails enrichment, OpenTelemetry metrics/traces, Scalar UI, Prometheus scraping, and health checks.
- **SQLite Storage**: Identity + API key tables managed through EF Core migrations stored under `Data/Migrations`.

## Preview Endpoint & Dashboard Pipeline

- `/api/v1.0/frames/latest` returns the most recent Mono8 frame as `image/jpeg`. The controller uses `Result<byte[]>`-based helpers in `SkiaPreviewEncoder` to keep SkiaSharp encoding logic centralized while still surfacing failures through structured logging and ProblemDetails.
- Frames originate from `CameraCaptureService`, which persists telemetry, stores payloads, and updates `ILatestFrameAccessor`. The accessor exposes a thread-safe snapshot the API can read without locking the capture loop.
- The dashboard consumes that endpoint via a cache-busted URL built from the latest telemetry timestamp. Polling cadence is configurable through `CapturePreview:PollingIntervalSeconds` (default 2s) in `appsettings*.json` and binds with data-annotation validation in `Program.cs`.
- Keep the preview endpoint unauthenticated only during local development. Production deployments should enforce the existing `ApiKeyOrCookie` policy or sit behind the authenticated UI to prevent arbitrary JPEG scraping.

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

SQLite lives under `Data/cameraagentsimulator.db`. Apply or create migrations with:

```bash
dotnet ef database update --project src/HVO.SkyMonitor.CameraAgent

dotnet ef migrations add <MigrationName> --project src/HVO.SkyMonitor.CameraAgent
```

The app applies pending migrations automatically on startup.

## Configuration

- `appsettings.json` contains the `DefaultConnection` string pointing at `Data/cameraagentsimulator.db` plus standard logging configuration.
- Data-protection keys persist under `DataProtection-Keys/` so browser sessions survive restarts.
- `CameraAgent` options default to `cameraagent.sample.json`. Copy or override this path (via `CameraAgent:ConfigFilePath`) when running in Docker to place the configuration under a mounted volume such as `/data/agent/config.json`.
- `CapturePreview` options drive dashboard cadence and will be expanded as we add RGB/bayer preview sources.

## SkiaSharp Native Assets

- The camera agent references `SkiaSharp` plus the `Linux.NoDependencies`, `macOS`, and `Win32` native-asset packages. This keeps preview encoding portable across devcontainer (Linux), local macOS workstations, and future Windows deployments without requiring manual `libSkiaSharp` placement.
- When targeting additional architectures (e.g., Windows Arm64), add the corresponding asset package in `Directory.Packages.props` and the project file so the native libraries flow into publish outputs and container images.
- The dev container image already ships glibc-compatible dependencies for Skia. If you install extra system fonts or GPU drivers, document those in `.devcontainer/post-create.sh` so other contributors inherit the same runtime behavior.

## Theme Usage

`Components/App.razor` references the local copy of the HVO Dark theme. Scoped CSS files under `Components/**/*.razor.css` build on that palette to keep parity with SkyMonitor V6.

## Docker

The existing `Dockerfile` still works for container builds. Run `docker build -t hvo-cameraagent -f src/HVO.SkyMonitor.CameraAgent/Dockerfile .` from the repo root when you need an image.
