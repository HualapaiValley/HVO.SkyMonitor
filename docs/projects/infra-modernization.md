# Infra, Testing, and Auth Modernization

This document supersedes `plan-infraAndTestingVNext.prompt.md` and
`future-infra-todos.md`. It tracks the multi-phase migration toward the
Docker/Testcontainers workflow, PostgreSQL-backed dev stack, and
end-to-end integration coverage.

## 1. Scope

- Replace Aspire/AppHost flows with direct project execution plus
  Docker/Testcontainers infrastructure.
- Centralize identity fixtures (shared test identities, tokens, API
  keys) in `HVO.SkyMonitor.TestSupport`.
- Move the host and camera agents to PostgreSQL, MinIO, Redis, and SMTP
  containers for both development and CI.
- Stand up Testcontainers-based integration suites for the host and
  camera agent (including auth coverage, MinIO IO, Redis caching, SMTP).
- Keep documentation and runbooks aligned with the new workflow.

## 2. Phase Summary

| Phase | Description | Status |
| --- | --- | --- |
| 0 | Planning + doc skeleton | ✅ Complete |
| 1 | TestSupport library + shared identities | ✅ Complete |
| 2 | Docker Compose dev stack + infra scripts | ✅ Complete |
| 3 | Replace Aspire + SQLite, clean docs | ✅ Complete |
| 4 | Host integration tests (HTTP + Testcontainers) | ✅ Complete |
| 5 | Camera agent integration + hardware test scaffolding | ✅ Complete |
| Identity Hardening | CI integration + runbooks | ✅ Complete |
| 7 | HTTPS/certificates + PostgreSQL migration validation | ✅ Complete (moved to future TODOs) |

## 3. Highlights by Phase

### Phase 0 – Planning
- Authored this plan, codified naming conventions (`*.Tests`,
  `*.IntegrationTests`, etc.), and ensured new projects land under `src/`.

### Phase 1 – TestSupport
- Added `src/HVO.SkyMonitor.TestSupport` with shared constants for test
  hosts, users, clients, API keys, and HMAC secrets.
- Introduced helpers that fetch tokens and provide common serialization /
  assertion utilities. Consumers: host + agent integration suites.

### Phase 2 – Docker Compose & Scripts
- Added `docker-compose.infrastructure.yml` and `docker-compose.apps.yml`
  with Postgres, MinIO, Redis, SMTP, LogicHost, and CameraAgent services.
- All services run on the local Docker daemon inside the devcontainer.
- Created `scripts/infra:start|status|reset|stop`, including `--reset`
  support per service and binder directories for data volumes.
- Documented workflows in `docs/runbooks/infra-operations.md` and
  `docs/runbooks/local-dev.md`.

### Shared Services Topology Update
- Retired repository-local infrastructure containers. SQL Server, Redis,
  MinIO, and Mailpit now persist on `hvo-docker` and applications consume
  their explicit `.env` endpoints and credentials.
- `docker-compose.apps.yml` and `scripts/infra:*` now manage only LogicHost
  and CameraAgent containers. Shared services are provisioned from
  `deploy/hvo-docker/docker-compose.shared-services.yml`.

### Phase 3 – Replace Aspire + SQLite
- Removed `HVO.SkyMonitor.AppHost` and Aspire dependencies.
- Swapped SQLite providers for PostgreSQL across host projects and
  ensured migrations run against Npgsql.
- Cleaned docs/README references to Aspire; updated config to point at
  Docker/Testcontainers.

### Phase 4 – Host Integration Tests
- Added `tests/HVO.SkyMonitor.IntegrationTests` with Testcontainers for
  Postgres, MinIO, Redis, and SMTP.
- Shared fixture spins up containers, builds configuration, and seeds
  auth data using TestSupport.
- Covered token issuance, protected APIs, MinIO read/write, Redis cache,
  and SMTP pipelines.

### Phase 5 – Camera Agent Integration & Hardware Tests
- Created `tests/HVO.SkyMonitor.CameraAgent.IntegrationTests` plus a
  shared hosting model for host+agent scenarios.
- Defined structure for future `*.HardwareTests` suites with
  `[TestCategory("Hardware")]` gating.
- Ensured CI excludes hardware suites by default but allows opt-in flag.

### Identity Hardening / CI Alignment
- Updated GitHub Actions workflows to run both unit and integration
  tests. Hardware suites stay opt-in via env flag.
- Authored runbooks: `docs/runbooks/local-dev.md`,
  `docs/runbooks/ci-pipeline.md`, `docs/runbooks/infra-operations.md`.
- Cleaned old Aspire/SQLite docs that conflicted with the new workflow.

### Phase 7 – HTTPS & Advanced Scenarios (Deferred work)
- Moved certificate automation and post-EF Core 10 PostgreSQL validation
  into the "Future Work" section below.

## 4. Future Work

| Item | Description |
| --- | --- |
| Development certificates & HTTPS | Provide `scripts/get-certs.sh`, generate wildcard certs for `*.skymonitor.local`, mount into containers, and extend smoke tests to cover HTTPS flows. |
| Automated cert renewals | Track Let's Encrypt/Key Vault automation for production along with monitoring hooks. |
| Additional Testcontainers coverage | Expand to camera-agent hardware simulators or specialized services as they become available. |

## 5. Related Files

- `deploy/hvo-docker/docker-compose.shared-services.yml`, `docker-compose.apps.yml`, `.env.template`, `scripts/infra:*`
- `src/HVO.SkyMonitor.TestSupport/`
- `tests/HVO.SkyMonitor.IntegrationTests/`
- `tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/`
- `docs/security/secrets.md`
- `docs/runbooks/*.md`

Keep this document up to date when new infra/test scope lands so the
legacy prompt files remain deleted.
