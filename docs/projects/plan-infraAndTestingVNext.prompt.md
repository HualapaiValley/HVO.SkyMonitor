> Guidance
> - Implementation should follow the coding and architectural guidelines defined in the repo’s Copilot instructions (e.g., under `.github/copilot-instructions.md`) for language, structure, and patterns.
> - When this plan specifies something more concrete or narrow (for example, exact project names, test names, or file locations), those instructions take precedence over the more general guidelines.
> - Ensure that all new code is well-documented, with comments explaining the purpose and functionality of complex sections.
> - Follow best practices for security, performance, and maintainability throughout the implementation.
> - Use clear and consistent naming conventions for all new files, classes, methods, and variables.
> - Ensure all unused classes and documents are removed if not needed.

## Infra, Testing, and Auth Modernization Plan

Phased migration to a Docker Compose + Testcontainers workflow, shared test identities, and comprehensive auth coverage (cookie, bearer, API-key, HMAC), while removing Aspire and introducing new runbooks. All new projects live under `src/`.

### Phase 0 – Planning & Documentation Skeleton

1. Create master plan document  
   - [ ] Add `docs/projects/infra-and-testing-vNext.md` (this file).  
   - [ ] Capture goals, assumptions (`.NET 10`, Blazor Server, Testcontainers, eventual PostgreSQL), and scope (HVO.Common, HVO.SkyMonitor, CameraAgents).  
   - [ ] Add a short “How to use this plan” section describing phases and checkboxes.

2. Establish naming and structure conventions  
   - [ ] Confirm all new project folders are under `src/` (e.g., `src/HVO.SkyMonitor.TestSupport`).  
   - [ ] Document test project naming: `*.Tests` (unit), `*.IntegrationTests` (infra-backed), `*.HardwareTests` (device-dependent), `*.UI.PlaywrightTests` (UI).

---

### Phase 1 – TestSupport Library and Shared Identities

3. Create `HVO.SkyMonitor.TestSupport` project under `src/`  
   - [ ] Add `src/HVO.SkyMonitor.TestSupport/HVO.SkyMonitor.TestSupport.csproj`.  
   - [ ] Reference from test projects that need shared test configuration.  
   - [ ] Optionally reference from `HVO.SkyMonitor` only for test-mode helpers.

4. Define shared test constants  
   - [ ] `TestHosts`: base URLs (HTTP dev, logical domain `skymonitor.local`, etc.).  
   - [ ] `TestEmail`: `From` address and common recipients (e.g., `no-reply@skymonitor.local`).  
   - [ ] `TestUsers`: admin/operator/viewer identities (email, username, password, roles).  
   - [ ] `TestClients`: system and UI clients (e.g., `system-camera-agent` with client ID, secret, scopes).  
   - [ ] `TestApiKeys`: API keys for system clients (camera agents, internal services).  
   - [ ] `TestHmacSecrets`: HMAC secrets for signed URLs/webhooks/presigned requests.

5. Add shared test utilities  
   - [ ] HTTP helpers to obtain tokens using `TestClients`/`TestUsers`.  
   - [ ] Utility extensions (serialization, assertion helpers) to reduce duplication.  
   - [ ] Optional small README in `HVO.SkyMonitor.TestSupport` describing available helpers.

---

### Phase 2 – Docker Compose Dev Stack & Infra Scripts

6. Add `docker-compose.dev.yml` at repository root  
   - [ ] Define infra services: `postgres`, `minio`, `redis`, `smtp`.  
   - [ ] Define app services: `skymonitor`, `cameraagent-sim`, `cameraagent-zwo` (built from `src/`).  
   - [ ] Use named volumes for Postgres, MinIO, Redis, SMTP data.  
   - [ ] Add bind mounts to host data directories that are accessible from the dev container (e.g., `~/skymonitor-data/postgres`, `~/skymonitor-data/minio`, etc.).  
   - [ ] Ensure ports and environment variables match application config expectations (connection strings, MinIO endpoint, Redis connection, SMTP host/port).

7. Configure per-service Docker contexts via environment variables  
   - [ ] In `.env.template` (and devcontainer env mapping), add:  
     - `POSTGRES_DOCKER_CONTEXT`, `MINIO_DOCKER_CONTEXT`, `REDIS_DOCKER_CONTEXT`, `SMTP_DOCKER_CONTEXT`.  
     - `SKYMONITOR_DOCKER_CONTEXT`, `CAMERAAGENT_SIM_DOCKER_CONTEXT`, `CAMERAAGENT_ZWO_DOCKER_CONTEXT`.  
   - [ ] Document defaults as `default`, and how to override to target remote Docker contexts.

8. Implement infra scripts in `scripts/`  
   - [ ] `scripts/infra:start`  
     - Starts requested services or all (`postgres`, `minio`, `redis`, `smtp`, `skymonitor`, `cameraagent-*`).  
     - Supports `--reset all|postgres|minio|redis|smtp` to reset data then start.  
     - Uses per-service Docker context env vars to run `docker --context <ctx> compose ...`.  
   - [ ] `scripts/infra:status`  
     - Shows status for each service via `docker --context <ctx> compose ps <service>`.  
   - [ ] `scripts/infra:reset`  
     - Performs data reset only (no start), following the same per-service semantics.

9. Document dev infra workflow  
   - [ ] In this plan and a runbook, add examples:  
     - Start all deps: `./scripts/infra:start`.  
     - Reset Postgres + MinIO: `./scripts/infra:start --reset postgres minio`.  
     - Check status: `./scripts/infra:status`.

---

### Phase 3 – Replace Aspire, Move from SQLite to PostgreSQL, and Clean Docs

10. Replace SQLite with PostgreSQL for the main host (where appropriate)  
   - [ ] Audit `HVO.SkyMonitor` and related projects for SQLite usage (connection strings, EF providers).  
   - [ ] Update configuration to use PostgreSQL in dev/test and docker environments (e.g., `ConnectionStrings__skymonitordb` pointing to Postgres).  
   - [ ] Update EF provider packages/config (e.g., switch from SQLite provider to Npgsql provider where the main host should use PostgreSQL).  
   - [ ] Ensure existing migrations are compatible or add new migrations for PostgreSQL schema where needed.

11. Identify and remove Aspire dependencies  
   - [ ] In `src/HVO.SkyMonitor.AppHost/HVO.SkyMonitor.AppHost.csproj`, remove Aspire SDK and packages (or remove the project entirely if obsolete).  
   - [ ] In `src/HVO.SkyMonitor/HVO.SkyMonitor.csproj`, remove Aspire/CommunityToolkit Aspire packages and replace them with direct equivalents (e.g., Npgsql, MinIO client, StackExchange.Redis).  
   - [ ] In `tests/HVO.SkyMonitor.Tests/HVO.SkyMonitor.Tests.csproj`, remove `Aspire.Hosting.Testing` and related dependencies.

12. Update solution and launch configs  
   - [ ] Remove `HVO.SkyMonitor.AppHost` from `HVO.SkyMonitor.v9.slnx` if no longer needed.  
   - [ ] Update `.vscode/launch.json` and tasks to reference direct project runs (`HVO.SkyMonitor`, camera agents) instead of the AppHost.

13. Update or remove Aspire-related documentation  
   - [ ] Review `docs/ASPIRE_SETUP.md` and other Aspire references.  
   - [ ] Remove or move to an archive, replacing guidance with Docker/Testcontainers-based workflow.  
   - [ ] Ensure `README.md` no longer describes Aspire-based startup; point to new infra scripts and runbooks.

---

### Phase 4 – HVO.SkyMonitor Integration Tests (HTTP-only, Testcontainers)

14. Create `HVO.SkyMonitor.IntegrationTests` project  
   - [ ] Add project under `tests/` or `src/` (consistent with repo conventions).  
   - [ ] Reference `HVO.SkyMonitor` and `HVO.SkyMonitor.TestSupport`.  
   - [ ] Add Testcontainers packages for Postgres, MinIO, Redis, SMTP.

15. Implement shared integration test fixture  
   - [ ] Fixture starts Testcontainers for Postgres, MinIO, Redis, SMTP with random ports.  
   - [ ] Builds configuration for `HVO.SkyMonitor` using those endpoints (connection strings, MinIO config, etc.).  
   - [ ] Uses `WebApplicationFactory<Program>` to host `HVO.SkyMonitor` in-process over HTTP (no HTTPS in phase 1).  
   - [ ] After startup and migrations, runs a test seeding routine using `HVO.SkyMonitor.TestSupport` to insert test users, clients, API keys, and HMAC secrets.

16. Implement initial integration test suites  
   - [ ] Token issuance tests (`/connect/token`) for:  
     - System clients (e.g., `system-camera-agent`).  
     - User principals (`admin`, `operator`, `viewer`) where appropriate.  
   - [ ] Protected API tests:  
     - Successful access using valid bearer tokens.  
     - Authorization failure for incorrect roles/scopes or missing tokens.  
   - [ ] MinIO tests: verify an endpoint can write/aghị
