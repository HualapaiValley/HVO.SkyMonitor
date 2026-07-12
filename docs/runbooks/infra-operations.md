# Infrastructure Operations Runbook

This runbook covers the persistent shared services on `hvo-docker.hvo.lan` and the local application containers that consume them.

## Service Layout

`deploy/hvo-docker/docker-compose.shared-services.yml` defines the persistent shared services; `docker-compose.apps.yml` defines only local application containers.

- SQL Server — metadata database, managed separately on `hvo-docker`.
- MinIO — persistent object storage on `hvo-docker`.
- Redis — persistent cache on `hvo-docker`.
- Mailpit — email capture service on `hvo-docker`.
- `logichost`, `cameraagent` — local application containers built from `src/`.

## Resource Ownership

- `SkyMonitor` is the only SQL Server database this repository migrates or seeds. Shared identity databases are owned by their defining repositories and must never be used as this host's connection string.
- Redis keys are prefixed with `skymonitor:`.
- SkyMonitor creates and uses only the `skymonitor-diagnostics` and `skymonitor-artifacts` MinIO buckets. Do not configure diagnostics requests to access another repository's bucket.

## Common Operations

### Start or restart services

```bash
./scripts/infra:start [service...]
```
- Omit arguments to start both application containers, or specify `logichost` or `cameraagent`.

### Reset data and start fresh

```bash
./scripts/infra:start --reset cameraagent
```
- `--reset` removes and rebuilds both local application containers; specify one or both service names to limit the reset.
- Shared-service data is never reset from this repository.

### Status checks

```bash
./scripts/infra:status
```

After a rebuild, verify both packaged catalogs and health endpoints:

```bash
docker exec skymonitor-logichost sha256sum /app/catalog/hyg_v42.sqlite
docker exec skymonitor-cameraagent sha256sum /app/catalog/hyg_v42.sqlite
curl --fail http://localhost:${LOGIC_HOST_HTTP_PORT:-5174}/health
curl --fail http://localhost:${CAMERA_AGENT_HTTP_PORT:-5130}/health
```

The expected catalog digest is
`f80689217769a6b13c1b9bfb9711485d3cb1ad8de009d3d6b0f0b0a4f1fa9840`.
Use `./scripts/test:infra` to validate service-selection semantics without
starting containers.
Shows `docker compose ps` output for local application containers.

### Data-only reset

```bash
./scripts/infra:reset cameraagent
```
Use when the CameraAgent identity and data-protection runtime state must be cleared.

## Log Collection

- Tail a specific service:
  ```bash
./scripts/infra:logs logichost
  ```
- Capture bundle for support:
  ```bash
  ./scripts/infra:status > /tmp/infra-status.txt
  docker --context hvo-docker compose --env-file .env -f deploy/hvo-docker/docker-compose.shared-services.yml logs > /tmp/infra-logs.txt
  ```

## Secrets & Credentials

- Application connection details live in the ignored root `.env`, based on `.env.template`.
- Shared-service credentials are supplied from the ignored root `.env`. Rotate them on `hvo-docker`, then update the root `.env` used by applications and provisioning commands.

## Disaster Recovery Scenarios

| Scenario | Steps |
| --- | --- |
| Shared service failure | Use the `hvo-docker` Docker context and `deploy/hvo-docker/docker-compose.shared-services.yml` to inspect or restart the affected service. |

## Change Management

1. Update `deploy/hvo-docker/docker-compose.shared-services.yml` / `docker-compose.apps.yml` for topology changes.
2. Reflect new environment variables in `.env.template` and devcontainer configuration.
3. Document operational differences here and announce via commit/PR.

## Related Docs

- `docs/runbooks/local-dev.md` — developer workflow.
- `docs/runbooks/ci-pipeline.md` — automated pipeline behavior.
- `docs/security/secrets.md` — storing and rotating secrets.
