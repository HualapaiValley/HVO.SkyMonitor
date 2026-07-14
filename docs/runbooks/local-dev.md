# Local Development Runbook

This runbook describes the day-to-day workflow for developing and validating HVO.SkyMonitor on a developer workstation or Codespace.

## Prerequisites

- Docker Engine with Compose plugin (the devcontainer already has both).
- .NET SDK 10.x as pinned in `global.json`.
- Access to repository secrets (see `docs/security/secrets.md`).
- Devcontainer recommended; if running locally ensure environment variables from `.env.template` are populated.

## Environment Setup

1. **Configure shared infrastructure.** Copy `.env.template` to the ignored `.env` and provide the `hvo-docker.hvo.lan` endpoints and credentials. SQL Server, Redis, MinIO, and Mailpit are persistent services managed outside this repository.

2. **Start application containers** when needed:
    ```bash
    ./scripts/infra:start
    ```

3. **Stop local application containers** using the matching helper:
   ```bash
   ./scripts/infra:stop
   ```
    This does not stop or clear shared-service data.

## Application Workflows

### Running the main host

1. Ensure `.env` is configured and invoke `dotnet run` through `./scripts/with-env` so the shared-service settings are loaded.
2. From `/workspaces/HVO.SkyMonitor` execute:
    ```bash
    ./scripts/with-env dotnet run --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj
    ```
3. The HTTPS direct-run profile exposes LogicHost at `https://localhost:7096`; the container profile exposes it at `http://localhost:5174`.

### Running camera agents

- **Camera Agent**:
  ```bash
   dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
  ```


Each agent reads central identity and LogicHost endpoints from configuration or environment variables. When running side-by-side with the direct-run host, use `https://localhost:7096`; the container profile uses `http://localhost:5174`.

## Testing Workflow

1. **Unit tests**
   ```bash
   DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
     dotnet test HVO.SkyMonitor.v9.slnx --filter "TestCategory=Unit"
   ```

2. **Integration tests only**
   ```bash
   dotnet test HVO.SkyMonitor.v9.slnx --filter "TestCategory=Integration"
   ```

3. **Manual diagnostic and accelerated soak tests**
   ```bash
   dotnet test HVO.SkyMonitor.v9.slnx --filter "TestCategory=Manual"
   dotnet test HVO.SkyMonitor.v9.slnx --filter "TestCategory=Soak"
   ```

### Hardware Tests

Hardware suites are opt-in when added and use the reserved `Hardware` category. There are currently no Hardware MSTest cases, so no successful Hardware check is published. External Stellarium validation remains in `.github/workflows/stellarium.yml` rather than being represented by an empty MSTest category.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Database migration failures | Verify the `SQLSERVER_*` values in `.env`, then apply migrations against the shared SQL Server. |
| MinIO credential errors | Verify `MINIO_ROOT_USER` and `MINIO_ROOT_PASSWORD` in `.env` match the shared MinIO service. |
| Redis connection timeouts | Verify the `REDIS_*` values in `.env` and the availability of `hvo-docker.hvo.lan:6379`. |
| SMTP emails missing | Verify the `SMTP_*` values in `.env`, then inspect Mailpit on `hvo-docker`. |

## Additional References

- `docs/security/secrets.md` for onboarding and rotation guidance.
- `docs/identity/overview.md` for authentication deep dives.
- `docs/runbooks/infra-operations.md` for production-like reset flows.
