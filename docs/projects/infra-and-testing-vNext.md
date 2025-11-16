# Infrastructure and Testing Modernization Plan (vNext)

## Overview

This document outlines the phased migration from .NET Aspire to a Docker Compose + Testcontainers workflow, with shared test identities and comprehensive authentication coverage. This modernization effort will improve developer experience, simplify deployment, and establish robust testing infrastructure.

## How to Use This Plan

This plan is organized into sequential phases, each with specific checkboxes to track progress. Complete each phase before moving to the next:

1. **Phase 0**: Establish documentation and conventions
2. **Phase 1**: Build shared test support library
3. **Phase 2**: Implement Docker Compose infrastructure
4. **Phase 3**: Remove Aspire dependencies and migrate to PostgreSQL
5. **Phase 4**: Create comprehensive integration tests

Check off items as they are completed. Dependencies between phases are clearly marked.

## Goals

- **Modernize Infrastructure**: Replace Aspire with Docker Compose for simpler orchestration
- **Improve Testing**: Introduce Testcontainers for reliable, isolated integration tests
- **Consolidate Auth**: Implement comprehensive authentication coverage (cookie, bearer, API-key, HMAC)
- **Enhance Developer Experience**: Provide clear scripts and documentation for infrastructure management
- **PostgreSQL Migration**: Move from SQLite to PostgreSQL for production-ready data storage

## Assumptions

- **.NET 10**: All projects target .NET 10 LTS
- **Blazor Server**: UI framework for interactive components
- **Testcontainers**: Container-based testing infrastructure
- **PostgreSQL**: Primary database for production and integration tests
- **Docker**: Available in development environment (via DevContainer with Docker-in-Docker)

## Scope

This plan covers the following projects:
- `HVO.Common` - Core shared library
- `HVO.SkyMonitor` - Main application
- `HVO.SkyMonitor.CameraAgent.*` - Camera agent projects
- New: `HVO.SkyMonitor.TestSupport` - Shared test infrastructure
- New: `HVO.SkyMonitor.IntegrationTests` - Integration test suite

---

## Phase 0 – Planning & Documentation Skeleton

### 1. Create master plan document
- [x] Add `docs/projects/infra-and-testing-vNext.md` (this file)
- [x] Capture goals, assumptions (.NET 10, Blazor Server, Testcontainers, eventual PostgreSQL)
- [x] Capture scope (HVO.Common, HVO.SkyMonitor, CameraAgents)
- [x] Add "How to use this plan" section describing phases and checkboxes

### 2. Establish naming and structure conventions
- [x] Confirm all new project folders are under `src/` (e.g., `src/HVO.SkyMonitor.TestSupport`)
- [x] Document test project naming conventions

#### Test Project Naming Conventions

All test projects follow these naming patterns:

- **`*.Tests`**: Unit tests that don't require external infrastructure
  - Example: `HVO.Common.Tests`, `HVO.SkyMonitor.Tests`
  - Location: `tests/` or `src/` directory
  - Dependencies: Test framework only (MSTest), no infrastructure

- **`*.IntegrationTests`**: Integration tests requiring infrastructure (databases, message queues, etc.)
  - Example: `HVO.SkyMonitor.IntegrationTests`
  - Location: `tests/` directory
  - Dependencies: Testcontainers, infrastructure client libraries
  - Uses: Shared test fixtures, in-memory or containerized infrastructure

- **`*.HardwareTests`**: Tests requiring physical hardware or devices
  - Example: `HVO.SkyMonitor.CameraAgent.ZWO.HardwareTests`
  - Location: `tests/` directory
  - Dependencies: Device-specific libraries, may require special test attributes
  - Note: Should be excluded from standard CI/CD pipelines

- **`*.UI.PlaywrightTests`**: End-to-end UI tests using Playwright
  - Example: `HVO.SkyMonitor.UI.PlaywrightTests`
  - Location: `tests/` directory
  - Dependencies: Playwright, may use Testcontainers for backend
  - Uses: Page object patterns, accessibility testing

#### Project Structure Conventions

- **Source Projects**: All application code lives under `src/`
- **Test Projects**: All test code lives under `tests/` (except when tests are co-located with source for specific reasons)
- **TestSupport Library**: `src/HVO.SkyMonitor.TestSupport` provides shared test utilities, constants, and helpers

---

## Phase 1 – TestSupport Library and Shared Identities

### 3. Create `HVO.SkyMonitor.TestSupport` project under `src/`
- [ ] Add `src/HVO.SkyMonitor.TestSupport/HVO.SkyMonitor.TestSupport.csproj`
- [ ] Reference from test projects that need shared test configuration
- [ ] Optionally reference from `HVO.SkyMonitor` only for test-mode helpers

### 4. Define shared test constants
- [ ] `TestHosts`: base URLs (HTTP dev, logical domain `skymonitor.local`, etc.)
- [ ] `TestEmail`: `From` address and common recipients (e.g., `no-reply@skymonitor.local`)
- [ ] `TestUsers`: admin/operator/viewer identities (email, username, password, roles)
- [ ] `TestClients`: system and UI clients (e.g., `system-camera-agent` with client ID, secret, scopes)
- [ ] `TestApiKeys`: API keys for system clients (camera agents, internal services)
- [ ] `TestHmacSecrets`: HMAC secrets for signed URLs/webhooks/presigned requests

### 5. Add shared test utilities
- [ ] HTTP helpers to obtain tokens using `TestClients`/`TestUsers`
- [ ] Utility extensions (serialization, assertion helpers) to reduce duplication
- [ ] Optional small README in `HVO.SkyMonitor.TestSupport` describing available helpers

---

## Phase 2 – Docker Compose Dev Stack & Infra Scripts

### 6. Add `docker-compose.dev.yml` at repository root
- [x] Define infra services: `postgres`, `minio`, `redis`, `smtp`
- [x] Define app services: `skymonitor`, `cameraagent-sim`, `cameraagent-zwo` (built from `src/`)
- [x] Use named volumes for Postgres, MinIO, Redis, SMTP data
- [x] Add bind mounts to host data directories accessible from dev container
- [x] Ensure ports and environment variables match application config expectations

### 7. Configure per-service Docker contexts via environment variables
- [x] In `.env.template` (and devcontainer env mapping), add:
  - `POSTGRES_DOCKER_CONTEXT`, `MINIO_DOCKER_CONTEXT`, `REDIS_DOCKER_CONTEXT`, `SMTP_DOCKER_CONTEXT`
  - `SKYMONITOR_DOCKER_CONTEXT`, `CAMERAAGENT_SIM_DOCKER_CONTEXT`, `CAMERAAGENT_ZWO_DOCKER_CONTEXT`
- [x] Document defaults as `default`, and how to override to target remote Docker contexts

### 8. Implement infra scripts in `scripts/`
- [x] `scripts/infra:start`
  - Starts requested services or all (`postgres`, `minio`, `redis`, `smtp`, `skymonitor`, `cameraagent-*`)
  - Supports `--reset all|postgres|minio|redis|smtp` to reset data then start
  - Uses per-service Docker context env vars to run `docker --context <ctx> compose ...`
- [x] `scripts/infra:status`
  - Shows status for each service via `docker --context <ctx> compose ps <service>`
- [x] `scripts/infra:reset`
  - Performs data reset only (no start), following the same per-service semantics

### 9. Document dev infra workflow
- [x] In this plan and a runbook, add examples:
  - Start all deps: `./scripts/infra:start`
  - Reset Postgres + MinIO: `./scripts/infra:start --reset postgres minio`
  - Check status: `./scripts/infra:status`

#### Dev Infrastructure Workflow Examples

**Start all services:**
```bash
./scripts/infra:start
```

**Start only infrastructure services:**
```bash
./scripts/infra:start postgres minio redis smtp
```

**Reset all data and start:**
```bash
./scripts/infra:start --reset all
```

**Reset specific services and start all:**
```bash
./scripts/infra:start --reset postgres minio
```

**Check service status:**
```bash
./scripts/infra:status
```

**Reset services without starting:**
```bash
./scripts/infra:reset postgres minio
```

**View logs:**
```bash
docker compose -f docker-compose.dev.yml logs -f
docker compose -f docker-compose.dev.yml logs -f skymonitor
```

**Stop all services:**
```bash
docker compose -f docker-compose.dev.yml down
```

**Stop and remove volumes (full cleanup):**
```bash
docker compose -f docker-compose.dev.yml down -v
```

---

## Phase 3 – Replace Aspire, Move from SQLite to PostgreSQL, and Clean Docs

### 10. Replace SQLite with PostgreSQL for the main host (where appropriate)
- [x] Audit `HVO.SkyMonitor` and related projects for SQLite usage
- [ ] Update configuration to use PostgreSQL in dev/test and docker environments *(Deferred: waiting for EF 10-compatible Npgsql release)*
- [ ] Update EF provider packages/config (switch from SQLite to Npgsql) *(Deferred: waiting for EF 10-compatible Npgsql release)*
- [ ] Ensure existing migrations are compatible or add new migrations for PostgreSQL

**Note**: PostgreSQL migration is deferred until Npgsql.EntityFrameworkCore.PostgreSQL releases an EF 10-compatible version. Currently using SQLite with direct Microsoft packages (removed Aspire wrappers).

### 11. Identify and remove Aspire dependencies
- [x] In `src/HVO.SkyMonitor/HVO.SkyMonitor.csproj`, remove Aspire packages, replace with direct equivalents
  - Removed: `CommunityToolkit.Aspire.Microsoft.Data.Sqlite`, `CommunityToolkit.Aspire.Microsoft.EntityFrameworkCore.Sqlite`
  - Removed: `Aspire.StackExchange.Redis` → Replaced with `StackExchange.Redis`
  - Removed: `CommunityToolkit.Aspire.Minio.Client` → Replaced with `Minio`
  - Added: `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` for health checks
- [x] In `tests/HVO.SkyMonitor.Tests/HVO.SkyMonitor.Tests.csproj`, remove `Aspire.Hosting.Testing`
- [ ] In `src/HVO.SkyMonitor.AppHost/HVO.SkyMonitor.AppHost.csproj`, remove Aspire packages (or remove project)

### 12. Update solution and launch configs
- [ ] Remove `HVO.SkyMonitor.AppHost` from `HVO.SkyMonitor.v9.slnx` if no longer needed
- [ ] Update `.vscode/launch.json` and tasks to reference direct project runs instead of AppHost

### 13. Update or remove Aspire-related documentation
- [x] Review `docs/ASPIRE_SETUP.md` and other Aspire references
- [x] Update `README.md` to describe Docker Compose workflow as recommended approach
- [ ] Archive `docs/ASPIRE_SETUP.md` (kept for reference, marked as legacy)

---

## Phase 4 – HVO.SkyMonitor Integration Tests (HTTP-only, Testcontainers)

### 14. Create `HVO.SkyMonitor.IntegrationTests` project
- [x] Add project under `tests/` directory
- [x] Reference `HVO.SkyMonitor` and `HVO.SkyMonitor.TestSupport`
- [x] Add Testcontainers packages for Postgres, MinIO, Redis, SMTP

### 15. Implement shared integration test fixture
- [x] Fixture starts Testcontainers for Postgres, MinIO, Redis, SMTP with random ports
- [x] Builds configuration for `HVO.SkyMonitor` using those endpoints
- [x] Uses `WebApplicationFactory<Program>` to host `HVO.SkyMonitor` in-process over HTTP
- [ ] After startup and migrations, runs test seeding routine to insert test users, clients, API keys

### 16. Implement initial integration test suites
- [x] Basic health check tests to verify infrastructure
- [ ] Token issuance tests (`/connect/token`) for system clients and user principals
- [ ] Protected API tests: successful access and authorization failures
- [ ] MinIO tests: verify endpoint can write/read objects
- [ ] Redis tests: verify caching functionality
- [ ] PostgreSQL tests: verify data persistence and retrieval

---

## Success Criteria

Phase completion is measured by:

1. **Phase 0**: Documentation exists, conventions are clear
2. **Phase 1**: TestSupport library builds, can be referenced by test projects
3. **Phase 2**: Docker Compose stack can be started/stopped/reset via scripts
4. **Phase 3**: Aspire removed, PostgreSQL working, documentation updated
5. **Phase 4**: Integration tests pass, provide value in CI/CD pipeline

## Notes

- Each phase should be completed and tested before moving to the next
- Breaking changes should be clearly documented
- Migration should be reversible where possible (maintain backward compatibility during transition)
- All infrastructure scripts should work in both local dev containers and CI/CD environments
