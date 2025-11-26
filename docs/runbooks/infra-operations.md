# Infrastructure Operations Runbook

This runbook covers maintenance tasks for the local Docker-based development environment as well as operational resets that mirror staging/production procedures.

## Service Layout

`docker-compose.dev.yml` defines the following services:

- `postgres` — metadata database (volume: `skymonitor-postgres`).
- `minio` — object storage (volume: `skymonitor-minio`).
- `redis` — caching (volume: `skymonitor-redis`).
- `smtp` — Mailpit relay for email testing.
- `skymonitor`, `cameraagent` — application containers built from `src/`.

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
  docker compose -f docker-compose.dev.yml logs -f postgres
  ```
- Capture bundle for support:
  ```bash
  ./scripts/infra:status > /tmp/infra-status.txt
  docker compose -f docker-compose.dev.yml logs > /tmp/infra-logs.txt
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
| SMTP never receives mail | `./scripts/infra:status smtp` then `docker compose logs smtp`; restart if necessary. |

## Change Management

1. Update `docker-compose.dev.yml` for topology changes.
2. Reflect new environment variables in `.env.template` and devcontainer configuration.
3. Document operational differences here and announce via commit/PR.

## Related Docs

- `docs/runbooks/local-dev.md` — developer workflow.
- `docs/runbooks/ci-pipeline.md` — automated pipeline behavior.
- `docs/security/secrets.md` — storing and rotating secrets.
