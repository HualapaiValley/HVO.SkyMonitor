# Infrastructure Operations Runbook

This runbook covers maintenance tasks for the local Docker-based development environment as well as operational resets that mirror staging/production procedures.

## Service Layout

`docker-compose.infrastructure.yml` defines the shared backing services:

- `postgres` — metadata database (volume: `skymonitor-postgres`).
- `minio` — object storage (volume: `skymonitor-minio`).
- `redis` — caching (volume: `skymonitor-redis`).
- `smtp` — Mailpit relay for email testing.

`docker-compose.apps.yml` houses the ASP.NET/Blazor applications (`logichost`, `cameraagent`).

## Common Operations

### Start or restart services

```bash
./scripts/infra:start [service...]
```
- Omit arguments to start everything.
- Services may be listed to start a subset (e.g., `./scripts/infra:start postgres redis`).

### Reset data and start fresh

```bash
./scripts/infra:start --reset postgres minio
```
- `--reset all` wipes every volume before start-up.
- Resetting Postgres drops and recreates the database; re-running the host will apply EF migrations automatically.

### Status checks

```bash
./scripts/infra:status
```
Shows `docker compose ps` output per service using each service’s configured Docker context.

### Data-only reset

```bash
./scripts/infra:reset minio
```
Use when volumes need to be wiped but services should remain stopped afterward.

## Log Collection

- Tail a specific service:
  ```bash
  docker compose -f docker-compose.infrastructure.yml logs -f postgres
  ```
- Capture bundle for support:
  ```bash
  ./scripts/infra:status > /tmp/infra-status.txt
  docker compose -f docker-compose.infrastructure.yml logs > /tmp/infra-logs.txt
  ```

## Secrets & Credentials

- Base credentials live in `.env.template`; copy to `.env.development` or devcontainer env file.
- Rotations: update `.env.template`, rerun `./scripts/infra:start --reset service` for affected services, and commit documentation updates.

## Disaster Recovery Scenarios

| Scenario | Steps |
| --- | --- |
| Postgres volume corruption | `./scripts/infra:start --reset postgres`, rerun host migrations, reseed via integration fixture if needed. |
| MinIO object mismatch | `./scripts/infra:start --reset minio`, rerun unit/integration tests that populate fixtures. |
| Redis memory leak | `./scripts/infra:start redis` (script stops + starts the container). |
| SMTP never receives mail | `./scripts/infra:status smtp` then `docker compose -f docker-compose.infrastructure.yml logs smtp`; restart if necessary. |

## Change Management

1. Update `docker-compose.infrastructure.yml` and/or `docker-compose.apps.yml` for topology changes.
2. Reflect new environment variables in `.env.template` and devcontainer configuration.
3. Document operational differences here and announce via commit/PR.

## Related Docs
## Docker Context Reference

The stack is intentionally split so infrastructure can live on `proxmox-home` while applications run locally.

| Stack | Default Docker context | Start | Stop | Status | Logs |
| --- | --- | --- | --- | --- | --- |
| Infrastructure (`postgres`, `redis`, `minio`, `smtp`) | `proxmox-home` | <code>docker --context proxmox-home compose -f docker-compose.infrastructure.yml up -d postgres redis minio smtp</code> | <code>docker --context proxmox-home compose -f docker-compose.infrastructure.yml stop postgres redis minio smtp</code> | <code>docker --context proxmox-home compose -f docker-compose.infrastructure.yml ps</code> | <code>docker --context proxmox-home compose -f docker-compose.infrastructure.yml logs -f redis</code> |
| Application (`logichost`, `cameraagent`) | `default` (local) | <code>docker compose -f docker-compose.apps.yml up -d logichost cameraagent</code> | <code>docker compose -f docker-compose.apps.yml stop logichost cameraagent</code> | <code>docker compose -f docker-compose.apps.yml ps</code> | <code>docker compose -f docker-compose.apps.yml logs -f logichost</code> |

Script equivalents automatically pick the correct context for each service:

```bash
# Start remote infra stack and ensure bind directories exist locally
./scripts/infra:start postgres redis minio smtp

# Start or rebuild just the app containers locally
./scripts/infra:start --rebuild logichost cameraagent

# Stop everything (including remote services)
./scripts/infra:stop all

# Status overview across contexts
./scripts/infra:status

# Tail logs
docker --context proxmox-home compose -f docker-compose.infrastructure.yml logs -f postgres
docker compose -f docker-compose.apps.yml logs -f cameraagent
```

> **Data directories:** When an infrastructure service runs in the local `default` context, the helper scripts automatically create the directories referenced by `POSTGRES_DATA_DIR`, `REDIS_DATA_DIR`, and `MINIO_DATA_DIR` before starting containers or after wiping volumes. Remote contexts continue to rely on their Docker volumes.


- `docs/runbooks/local-dev.md` — developer workflow.
- `docs/runbooks/ci-pipeline.md` — automated pipeline behavior.
- `docs/SECRETS_MANAGEMENT.md` — storing and rotating secrets.
