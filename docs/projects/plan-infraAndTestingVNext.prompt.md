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

_Phase status: ✅ Complete_

1. Create master plan document  
   - [x] Add `docs/projects/infra-and-testing-vNext.md` (this file).  
   - [x] Capture goals, assumptions (`.NET 10`, Blazor Server, Testcontainers, eventual PostgreSQL), and scope (HVO.Common, HVO.SkyMonitor, CameraAgents).  
   - [x] Add a short “How to use this plan” section describing phases and checkboxes.

2. Establish naming and structure conventions  
   - [x] Confirm all new project folders are under `src/` (e.g., `src/HVO.SkyMonitor.TestSupport`).  
   - [x] Document test project naming: `*.Tests` (unit), `*.IntegrationTests` (infra-backed), `*.HardwareTests` (device-dependent), `*.UI.PlaywrightTests` (UI).

---

### Phase 1 – TestSupport Library and Shared Identities

_Phase status: ✅ Complete_

3. Create `HVO.SkyMonitor.TestSupport` project under `src/`  
   - [x] Add `src/HVO.SkyMonitor.TestSupport/HVO.SkyMonitor.TestSupport.csproj`.  
   - [x] Reference from test projects that need shared test configuration.  
   - [x] Optionally reference from `HVO.SkyMonitor` only for test-mode helpers.

4. Define shared test constants  
   - [x] `TestHosts`: base URLs (HTTP dev, logical domain `skymonitor.local`, etc.).  
   - [x] `TestEmail`: `From` address and common recipients (e.g., `no-reply@skymonitor.local`).  
   - [x] `TestUsers`: admin/operator/viewer identities (email, username, password, roles).  
   - [x] `TestClients`: system and UI clients (e.g., `system-camera-agent` with client ID, secret, scopes).  
   - [x] `TestApiKeys`: API keys for system clients (camera agents, internal services).  
   - [x] `TestHmacSecrets`: HMAC secrets for signed URLs/webhooks/presigned requests.

5. Add shared test utilities  
   - [x] HTTP helpers to obtain tokens using `TestClients`/`TestUsers`.  
   - [x] Utility extensions (serialization, assertion helpers) to reduce duplication.  
   - [x] Optional small README in `HVO.SkyMonitor.TestSupport` describing available helpers.

---

### Phase 2 – Docker Compose Dev Stack & Infra Scripts

_Phase status: ✅ Complete_

6. Add `docker-compose.dev.yml` at repository root  
   - [x] Define infra services: `postgres`, `minio`, `redis`, `smtp`.  
   - [x] Define app services: `skymonitor`, `cameraagent-sim`, `cameraagent-zwo` (built from `src/`).  
   - [x] Use named volumes for Postgres, MinIO, Redis, SMTP data.  
   - [x] Add bind mounts to host data directories that are accessible from the dev container (e.g., `~/skymonitor-data/postgres`, `~/skymonitor-data/minio`, etc.).  
   - [x] Ensure ports and environment variables match application config expectations (connection strings, MinIO endpoint, Redis connection, SMTP host/port).

7. Configure per-service Docker contexts via environment variables  
   - [x] In `.env.template` (and devcontainer env mapping), add:  
     - `POSTGRES_DOCKER_CONTEXT`, `MINIO_DOCKER_CONTEXT`, `REDIS_DOCKER_CONTEXT`, `SMTP_DOCKER_CONTEXT`.  
     - `SKYMONITOR_DOCKER_CONTEXT`, `CAMERAAGENT_SIM_DOCKER_CONTEXT`, `CAMERAAGENT_ZWO_DOCKER_CONTEXT`.  
   - [x] Document defaults as `default`, and how to override to target remote Docker contexts.

8. Implement infra scripts in `scripts/`  
   - [x] `scripts/infra:start`  
     - Starts requested services or all (`postgres`, `minio`, `redis`, `smtp`, `skymonitor`, `cameraagent-*`).  
     - Supports `--reset all|postgres|minio|redis|smtp` to reset data then start.  
     - Uses per-service Docker context env vars to run `docker --context <ctx> compose ...`.  
   - [x] `scripts/infra:status`  
     - Shows status for each service via `docker --context <ctx> compose ps <service>`.  
    - [x] `scripts/infra:reset`  
       - Performs data reset only (no start), following the same per-service semantics.

9. Document dev infra workflow  
    - [x] In this plan and a runbook, add examples:  
       - Start all deps: `./scripts/infra:start`.  
       - Reset Postgres + MinIO: `./scripts/infra:start --reset postgres minio`.  
       - Check status: `./scripts/infra:status`.

---

### Phase 3 – Replace Aspire, Move from SQLite to PostgreSQL, and Clean Docs

_Phase status: ⚙️ In progress (only item 13 outstanding)_

10. Replace SQLite with PostgreSQL for the main host (where appropriate)  
   - [x] Audit `HVO.SkyMonitor` and related projects for SQLite usage (connection strings, EF providers).  
   - [x] Update configuration to use PostgreSQL in dev/test and docker environments (e.g., `ConnectionStrings__skymonitordb` pointing to Postgres).  
   - [x] Update EF provider packages/config (e.g., switch from SQLite provider to Npgsql provider where the main host should use PostgreSQL).  
   - [x] Ensure existing migrations are compatible or add new migrations for PostgreSQL schema where needed.

   _Notes_: Originally deferred pending EF Core 10-compatible Npgsql, but the work was completed alongside the integration test bring-up that now runs fully against PostgreSQL.

11. Identify and remove Aspire dependencies  
   - [x] Remove `src/HVO.SkyMonitor.AppHost` and associated Aspire SDK references.  
   - [x] In `src/HVO.SkyMonitor/HVO.SkyMonitor.csproj`, remove Aspire/CommunityToolkit Aspire packages and replace them with direct equivalents (e.g., Npgsql, MinIO client, StackExchange.Redis).  
   - [x] In `tests/HVO.SkyMonitor.Tests/HVO.SkyMonitor.Tests.csproj`, remove `Aspire.Hosting.Testing` and related dependencies.  
   - [x] Delete `HVO.SkyMonitor.ServiceDefaults` and move observability wiring into shared helpers.

12. Update solution and launch configs  
   - [x] Remove or clearly mark `HVO.SkyMonitor.AppHost` as legacy-only in `HVO.SkyMonitor.v9.slnx`.  
   - [x] Update `.vscode/launch.json` and tasks to reference direct project runs (`HVO.SkyMonitor`, camera agents) instead of the AppHost.

13. Update or remove Aspire-related documentation  
   - [x] Review `docs/ASPIRE_SETUP.md` and other Aspire references.  
   - [x] Remove or move to an archive, replacing guidance with Docker/Testcontainers-based workflow (add prominent archival notes where legacy content remains).  
   - [x] Ensure `README.md` files no longer describe Aspire-based startup; point to new infra scripts and runbooks.  
   - [ ] Re-scan remaining docs (auth guides, secrets quickstart/summary) and replace legacy instructions entirely with Docker/Testcontainers equivalents. _This is the final outstanding action for Phase 3._

---

### Phase 4 – HVO.SkyMonitor Integration Tests (HTTP-only, Testcontainers)

_Phase status: ✅ Complete_

14. Create `HVO.SkyMonitor.IntegrationTests` project  
   - [x] Add project under `tests/` or `src/` (consistent with repo conventions).  
   - [x] Reference `HVO.SkyMonitor` and `HVO.SkyMonitor.TestSupport`.  
   - [x] Add Testcontainers packages for Postgres, MinIO, Redis, SMTP.

15. Implement shared integration test fixture  
   - [x] Fixture starts Testcontainers for Postgres, MinIO, Redis, SMTP with random ports.  
   - [x] Builds configuration for `HVO.SkyMonitor` using those endpoints (connection strings, MinIO config, etc.).  
   - [x] Uses `WebApplicationFactory<Program>` to host `HVO.SkyMonitor` in-process over HTTP (no HTTPS in phase 1).  
   - [x] After startup and migrations, runs a test seeding routine using `HVO.SkyMonitor.TestSupport` to insert test users, clients, API keys, and HMAC secrets.

16. Implement initial integration test suites  
    - [x] Token issuance tests (`/connect/token`) for:  
       - System clients (e.g., `system-camera-agent`).  
       - User principals (`admin`, `operator`, `viewer`) where appropriate.  
    - [x] Protected API tests:  
       - Successful access using valid bearer tokens.  
       - Authorization failure for incorrect roles/scopes or missing tokens.  
    - [x] MinIO tests: verify an endpoint can write/read objects via MinIO.  
    - [x] Redis tests: verify that expected caching behavior occurs via Redis.  
    - [x] SMTP tests: verify that emails sent by the app appear in the SMTP container.

---

### Phase 5 – Camera Agent Integration & Hardware Tests

_Phase status: 🔜 Planned_

17. Define camera-agent integration tests  
   - [ ] Create `HVO.SkyMonitor.CameraAgent.*.IntegrationTests` projects for Simulator/ZWO agents.  
   - [ ] Decide hosting model for host + agent (multiple `WebApplicationFactory` instances vs. Testcontainers-based app + agent containers).  
   - [ ] Use `HVO.SkyMonitor.TestSupport` identities and hosts for agent auth tests (bearer, API key, HMAC when introduced).

18. Define hardware test structure  
   - [ ] Design `*.HardwareTests` projects and/or `[TestCategory("Hardware")]` usage for GPIO/I2C/ZWO scenarios.  
   - [ ] Ensure CI excludes hardware tests by default and provides an opt-in path.

---

### Phase 6 – CI Integration and Runbooks

_Phase status: ⚙️ Partially complete_

19. Update GitHub Actions workflows  
   - [x] Ensure CI jobs build the solution and run unit + integration tests, including Testcontainers-based tests.  
   - [ ] Refine CI to explicitly exclude hardware tests (when added) by default and allow opt-in.

20. Create/update runbooks under `docs/`  
   - [ ] `docs/runbooks/local-dev.md`: local dev workflow with `scripts/infra:*`, project runs, and tests.  
   - [ ] `docs/runbooks/ci-pipeline.md`: CI stages, Testcontainers, and GitHub Secrets usage.  
   - [ ] `docs/runbooks/infra-operations.md`: reset flows, data locations, and troubleshooting.

21. Clean outdated docs at completion  
   - [ ] Re-scan `docs/` for Aspire or SQLite-specific instructions that are no longer valid.  
   - [ ] Update or remove them, linking to the new runbooks and PostgreSQL-based workflow.

---

### Phase 7 – HTTPS, Certificates, and Advanced Scenarios (Later)

_Phase status: 🔜 Deferred by design_

22. Cert management and HTTPS wiring  
   - [ ] Add `scripts/get-certs.sh` to generate self-signed wildcard `*.skymonitor.local` certs for dev.  
   - [ ] Mount certs into app containers via `docker-compose.dev.yml` and configure Kestrel/OpenIddict via env vars.  
   - [ ] Optionally extend integration tests to cover HTTPS flows once core system is stable.

23. PostgreSQL migration (deferred from Phase 3)  
   - [ ] Once EF Core 10-compatible Npgsql is available, update `HVO.SkyMonitor` to use PostgreSQL in dev/test and docker environments.  
   - [ ] Run and/or regenerate migrations for PostgreSQL schema and validate end-to-end behavior.  
   - [ ] Update docs and runbooks to reflect PostgreSQL as the primary store where applicable.

