# Local Development Runbook

This runbook describes the day-to-day workflow for developing and validating HVO.SkyMonitor on a developer workstation or Codespace.

## Prerequisites

- Docker Engine with Compose plugin (the devcontainer already has both).
- .NET SDK 10.x as pinned in `global.json`.
- Access to repository secrets (see `docs/SECRETS_MANAGEMENT.md`).
- Devcontainer recommended; if running locally ensure environment variables from `.env.template` are populated.

## Environment Setup

1. **Start the shared infrastructure** using the helper script. From the repo root:
   ```bash
   ./scripts/infra:start
   ```
   - Use `./scripts/infra:start postgres minio redis smtp` to start a subset.
   - Add `--reset` before the service list to wipe data (e.g., `./scripts/infra:start --reset postgres`).

2. **Check status** whenever you need to confirm container health:
   ```bash
   ./scripts/infra:status
   ```

3. **Stop everything** (if needed) with Docker directly:
   ```bash
   docker compose -f docker-compose.dev.yml down
   ```
   This mirrors what the scripts do and ensures networks/volumes remain intact.

## Application Workflows

### Running the main host

1. Ensure infra is running.
2. From `/workspaces/HVO.SkyMonitor` execute:
   ```bash
   dotnet run --project src/HVO.SkyMonitor
   ```
3. The host listens on the standard HTTP ports defined in `appsettings.Development.json` (defaults: 5000/5001). Update `.env.development` for overrides.

### Running camera agents

- **Simulator**:
  ```bash
  dotnet run --project src/HVO.SkyMonitor.CameraAgent.Simulator
  ```
- **ZWO** (requires hardware / SDK drivers):
  ```bash
  dotnet run --project src/HVO.SkyMonitor.CameraAgent.ZWO
  ```

Each agent reads central identity + MinIO endpoints from `appsettings.Development.json` or environment variables. When running side-by-side with the host, use the Docker-provided service names (e.g., `http://host.docker.internal:5000`).

## Testing Workflow

1. **Unit tests**
   ```bash
   dotnet test HVO.SkyMonitor.v9.slnx --filter TestCategory!=Hardware
   ```

2. **Integration tests only**
   ```bash
   dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj
   dotnet test tests/HVO.SkyMonitor.CameraAgent.Simulator.IntegrationTests/HVO.SkyMonitor.CameraAgent.Simulator.IntegrationTests.csproj
   ```

3. **Full solution**
   ```bash
   dotnet test HVO.SkyMonitor.v9.slnx
   ```

### Hardware Tests

Hardware suites are opt-in. They are tagged with `TestCategory("Hardware")`—omit them via `--filter TestCategory!=Hardware`. Future CI jobs will keep them disabled by default.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Database migration failures | Run `./scripts/infra:start --reset postgres` to recreate the database, then restart the host. |
| MinIO credential errors | Verify `Minio:AccessKey`/`SecretKey` in `.env.development` match `docker-compose.dev.yml`. |
| Redis connection timeouts | Ensure port `6379` is free; restart via `./scripts/infra:start redis`. |
| SMTP emails missing | Use `./scripts/infra:status smtp` and check logs: `docker compose -f docker-compose.dev.yml logs smtp`. |

## Additional References

- `docs/SECRETS_QUICKSTART.md` for onboarding secrets.
- `docs/identity` for authentication deep dives.
- `docs/runbooks/infra-operations.md` for production-like reset flows.
