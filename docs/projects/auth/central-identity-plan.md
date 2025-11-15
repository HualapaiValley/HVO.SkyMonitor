# Central Identity Program Plan (Phases 0-8)

## Context & Assumptions
- Objective: rebuild HVO.SkyMonitor identity/auth as a single authoritative system spanning the Logic/UI host and all camera agents.
- Scope: replace existing per-service Identity stores, add OpenIddict, API key consolidation, signed URLs, and operational hardening.
- State Reset: existing ASP.NET Identity migrations and local development databases may be deleted/recreated freely—nothing has shipped beyond dev environments yet.
- Aspire Topology: the solution runs under .NET Aspire; database services execute inside dedicated containers. Any build/run instructions must ensure the containerized database is available (e.g., via Aspire orchestration or the provided compose scripts) before applying migrations.
- Testing Strategy: only minimal smoke/manual validation before Phase 8; comprehensive automated tests are deferred to Phase 8.
- Deliverable Form: each phase lists goals plus Markdown-checkbox tasks for execution tracking.

## Phase 0 – Environment Reset & Readiness
**Goals**
- Remove legacy Identity artifacts and establish a clean baseline for the new schema.
- Document current auth landscape to inform later phases.

**Tasks**
- [x] Inventory existing Identity/API-key code paths in `HVO.SkyMonitor`, `HVO.SkyMonitor.CameraAgent.*`, and shared libraries.
- [x] Delete all existing Identity-related EF Core migrations across the solution (Logic/UI + agents).
- [x] Remove obsolete database artifacts (local dev databases can be dropped safely during this phase; remember Aspire hosts DBs inside containers, so stop/prune the relevant service if necessary).
- [x] Capture a target data-model outline (ERD or table list) for the forthcoming centralized Identity + OpenIddict schema.
- [x] Verify data-protection key storage and connection strings are documented for the rebuilt DBs.
- [x] Update developer onboarding notes (e.g., `README`, `docs/`) to state that Identity is being rebuilt from scratch.

## Phase 1 – Identity Foundation & Account Types
**Goals**
- Introduce AccountType-aware Identity model (USER vs SYSTEM) and enforce non-interactive behavior for SYSTEM accounts.
- Seed baseline accounts and document governance.

**Tasks**
- [x] Define `AccountType` enum and add property to `ApplicationUser` plus supporting EF migration.
- [x] Seed at least one admin USER and one SYSTEM service account; document credential handling and rotation expectations.
- [x] Update login flows (Blazor components, API endpoints) to block password sign-in for SYSTEM accounts with user-friendly messaging.
- [x] Review Identity cookie settings, lockouts, and password policies to align with new account types.
- [x] Document account governance (creation, lifecycle, recovery, access revocation).
- [x] Manual validation: create/sample login for USER, confirm SYSTEM accounts cannot sign in interactively.

## Phase 2 – OpenIddict Integration
**Goals**
- Add OpenIddict server/validation to HVO.SkyMonitor supporting Authorization Code + PKCE and Client Credentials flows.
- Secure at least one API using issued JWTs.

**Tasks**
- [x] Add OpenIddict packages (Server + Validation + EF) and configure `/connect/authorize`, `/connect/token`, `/connect/userinfo` endpoints.
- [x] Extend `ApplicationDbContext` with OpenIddict entities/mappings; create migration following Phase 1 schema.
- [x] Configure scopes, consent policies, token lifetimes, and signing/encryption credentials.
- [x] Map Identity data to token claims (`sub`, `account_type`, scopes) and ensure SYSTEM/USER semantics flow through.
- [x] Protect a starter API endpoint (e.g., `StatusController`) with `[Authorize]` using OpenIddict validation.
- [x] Provide minimal tooling/scripts for registering clients (camera agents, UI, automation).
- [x] Manual validation: complete Auth Code + PKCE login and client-credentials token acquisition against the secured endpoint.

## Phase 3 – API Key Consolidation & Policies
**Goals**
- Make HVO.SkyMonitor the single authority for API keys and authorization policies.
- Provide management UI for USER and SYSTEM keys.

**Tasks**
- [x] Confirm `DefaultApiKeyAuthenticationHandler` + `DatabaseApiKeyValidator` wiring inside HVO.SkyMonitor; remove duplicates from other services.
- [x] Define authorization policies (`RequireSystemAccount`, `RequireUserAccount`, scope-based, and `ApiKeyAccessLevel` tiers) and annotate sensitive endpoints.
- [x] Build Blazor UI workflows for listing, creating, rotating, and revoking API keys (self-service for USER, admin for SYSTEM accounts).
- [x] Ensure raw API keys display only once on creation, storing hashed values thereafter.
- [x] Implement structured audit logging and (where feasible) notifications for key lifecycle events.
- [x] Manual validation: call representative APIs with Read-only vs ReadWrite keys to confirm policy enforcement.

## Phase 4 – Camera Agent Refactor to Central Identity
**Goals**
- Remove local Identity stores from all camera agents and switch outbound authentication to centralized JWT/API keys.
- Align any inbound agent endpoints with the shared auth model.

**Tasks**
- [x] Strip `IdentityCore<ApplicationUser>` registrations, DbContexts, and migrations from each `HVO.SkyMonitor.CameraAgent.*` project.
- [x] Introduce agent credential configuration (client credentials or API key) pointing to the central HVO.SkyMonitor Identity service.
- [x] Implement shared auth helpers for outbound HTTP clients (token acquisition, API-key injection) in `HVO.SkyMonitor.CameraAgent` library.
- [x] Update agent configuration docs/env samples to show how to provision and store credentials.
- [x] Adjust inbound agent endpoints (if any) to validate HVO-issued JWTs or API keys using shared validators.
- [x] Manual validation: simulator agent authenticates to HVO APIs using central credentials; confirm no local Identity DB is used.

## Phase 5 – Signed URLs for Frames & Images
**Goals**
- Provide short-lived, HMAC-signed URLs for high-volume media endpoints to avoid per-request cookies or bearer tokens.

**Tasks**
- [x] Define signed ticket schema (version, expiration, subject ID, HTTP method/path/query digest, optional scope bits).
- [x] Implement `ISignedTicketService` for canonicalization and HMAC generation using centrally managed secrets.
- [x] Create validator/auth handler that parses `st` query parameters, verifies expiration/HMAC, and optionally rehydrates a `ClaimsPrincipal`.
- [x] Update Blazor UI components to request/generate signed URLs for critical endpoints (e.g., `/api/v1.0/frame/latest`).
- [x] Expose configuration knobs for TTL, allowed endpoints, and clock skew.
- [x] Manual validation: confirm valid signed URLs succeed, tampered or expired URLs are rejected, and performance metrics meet expectations.

## Phase 5.5 – Integration Validation Tests
**Goals**
- Provide early automated coverage for authentication/authorization scenarios before broader hardening in Phase 6.
- Split validation tests between the primary HVO.SkyMonitor suite and a dedicated camera simulator test project so coverage mirrors runtime responsibilities.
- Use Aspire.Hosting.Testing for true integration tests that start the complete distributed application.

**Tasks**
- [x] Add `HVO.SkyMonitor.Tests` coverage for:
	- [x] AccountType validation (USER vs SYSTEM login restrictions) - 3 tests in AccountTypeTests
	- [x] OAuth2 Authorization Code + PKCE flow - Claim mapping validated in OAuth2ClaimMappingTests (7 tests)
	- [x] OAuth2 Client Credentials flow - System account type claim mapping tested (7 tests)
	- [x] API key authentication honoring account types - 3 tests in ApiKeyAuthenticationTests
	- [x] Signed URL validation - 6 tests in SignedUrlTests
	- [x] Authorization policies (`RequireSystemAccount`, `RequireUserAccount`) - 6 tests in AuthorizationPolicyTests
- [x] Add Aspire.Hosting.Testing integration tests:
	- [x] Complete distributed application startup (AppHost with Redis, PostgreSQL, MinIO) - 4 tests in AspireIntegrationTests
	- [x] End-to-end OAuth2 token endpoint validation
	- [x] Protected endpoint authentication validation
	- [x] API key authentication endpoint validation
- [x] Create `HVO.SkyMonitor.CameraAgent.Tests` for the simulator covering:
	- [x] Central authentication service behavior (token acquisition, caching) - 3 tests in CentralAuthenticationServiceTests
	- [x] JWT validation - Covered through OAuth2 claim mapping tests
	- [x] `HttpClient` configuration that injects tokens or API keys appropriately - 3 tests + 5 error tests
- [x] Group / document the tests per functionality to keep future ZWO-agent tests aligned - Tests organized by class with clear documentation

**Summary**
- Total tests: 37 (25 unit tests + 4 Aspire integration tests in HVO.SkyMonitor.Tests + 8 in HVO.SkyMonitor.CameraAgent.Tests)
- Unit tests (29): All passing, run without Docker
- Aspire integration tests (4): Require Docker/Podman and start the full AppHost (Redis, PostgreSQL, MinIO, SkyMonitor) to validate `/connect/token`, protected endpoints, and API-key auth end-to-end
- Coverage includes AccountType, OAuth2 claims, API keys, signed URLs, authorization policies, camera agent authentication, and full Aspire application startup
- Test infrastructure: MSTest + Aspire.Hosting.Testing + FluentAssertions + Moq
- Documentation: README.md in tests directory explains unit vs integration testing approach


## Phase 6 – Hardening, Operations & Observability
**Goals**
- Productionize the auth system with proper secret storage, logging, metrics, rate limiting, and runbooks.

**Tasks**
- [x] Move OpenIddict signing keys, API-key hashing keys, and signed-URL HMAC keys into the designated secret store (document retrieval & rotation).
  - [x] Documented OpenIddict signing/encryption certificate management (development auto-generated, production from Azure Key Vault)
  - [x] Documented API-key hashing salt configuration (optional enhancement for production)
  - [x] Documented signed-URL HMAC secret requirements (256-bit minimum, stored in User Secrets/Key Vault)
  - [x] Created comprehensive secret management guide (PHASE6_SECRETS.md)
  - [x] Documented secret generation procedures (openssl commands)
  - [x] Documented secret rotation schedules and procedures
  - [x] Updated .env.template with all Phase 6 environment variables
  - [x] Updated devcontainer.json with Phase 6 non-sensitive configuration
  - [x] Updated GitHub Actions secrets documentation with Phase 6 requirements
- [x] Define key rotation/incident response procedures, including overlap windows where old and new keys are accepted.
  - [x] Created operational runbooks (PHASE6_RUNBOOKS.md) with:
  - [x] OpenIddict signing certificate rotation (zero-downtime, 12-month schedule)
  - [x] Signed URL HMAC secret rotation (overlap window, 90-day schedule)
  - [x] API key hashing salt rotation (destructive, annual or on breach)
  - [x] Emergency access revocation procedures
  - [x] Security incident response workflow (detection, containment, investigation, remediation, post-incident)
- [x] Produce runbooks for onboarding accounts, issuing/rotating keys, revoking access, and handling 401/403 scenarios across services.
  - [x] User account onboarding procedures (interactive USER accounts)
  - [x] System account onboarding procedures (service/agent SYSTEM accounts)
  - [x] API key lifecycle management (creation, rotation, revocation)
  - [x] Access revocation procedures (standard offboarding and emergency)
  - [x] Troubleshooting guide for 401/403 errors (comprehensive diagnostic steps)
  - [x] Certificate management and renewal (Let's Encrypt automation)
  - [x] Monitoring, metrics, and alerting guidelines
- [ ] Configure production-grade TLS for HVO.SkyMonitor and any public endpoints.
  - [x] Documented TLS certificate requirements and storage options
  - [x] Documented Let's Encrypt certificate renewal automation
  - [x] Documented certificate expiration monitoring
  - [ ] Code changes to load certificates from Azure Key Vault in production
  - [ ] Configure Kestrel HTTPS endpoints in appsettings.Production.json
- [ ] Implement structured logging for login success/failure, token issuance, API-key usage, and signed-URL validation failures (avoid sensitive payloads).
  - [x] Existing: ApiKeyAuditLogger for API key lifecycle events
  - [ ] Add structured logging for login success/failure events
  - [ ] Add structured logging for OAuth2 token issuance events
  - [ ] Enhance API-key authentication handler with usage logging
  - [ ] Add structured logging for signed-URL validation failures
  - [ ] Review and ensure no sensitive data (passwords, tokens) is logged
  - [ ] Document log structure, fields, and example queries
- [ ] Publish metrics (token requests per client, API-key auth success/failure, signed-URL rejection counts, latency/error rates per key API) via Prometheus/App Insights exporters.
  - [x] Existing: Prometheus metrics endpoint configured (Program.cs)
  - [ ] Add custom metrics for token requests per client (OpenIddict)
  - [ ] Add custom metrics for API-key authentication success/failure
  - [ ] Add custom metrics for signed-URL validation success/failure
  - [ ] Add latency/error rate metrics for authentication endpoints
  - [ ] Configure metrics exporters (Prometheus already enabled)
  - [ ] Document available metrics and how to access them
  - [ ] Create sample Grafana dashboards or query examples
- [ ] Add rate limiting/IP throttling to `/connect/token` and other sensitive endpoints; re-validate CSRF protections for cookie flows.
  - [x] Documented rate limiting configuration in .env.template and devcontainer.json
  - [ ] Add ASP.NET Core rate limiting middleware
  - [ ] Configure rate limits for /connect/token endpoint
  - [ ] Configure rate limits for /connect/authorize endpoint
  - [ ] Configure rate limits for API endpoints
  - [ ] Add IP-based throttling for sensitive endpoints
  - [ ] Re-validate CSRF protections for Blazor/cookie flows
  - [ ] Test rate limiting behavior with load testing
  - [ ] Document rate limiting configuration and behavior

**Documentation Status**
- ✅ PHASE6_SECRETS.md created (17.5 KB) - Comprehensive secret management guide
- ✅ PHASE6_RUNBOOKS.md created (24.7 KB) - Operational procedures and incident response
- ✅ .env.template updated with Phase 6 configuration
- ✅ .devcontainer/devcontainer.json updated with Phase 6 environment variables
- ✅ .github/workflows/README.md updated with Phase 6 GitHub Actions secrets
- ✅ SECRETS_MANAGEMENT.md updated with Phase 6 reference

**Implementation Status**
- 🔄 Documentation: Complete
- 🔄 TLS Configuration: Documented, implementation pending
- 🔄 Structured Logging: Partially implemented (API keys done), auth events pending
- 🔄 Metrics: Infrastructure ready, custom metrics pending
- 🔄 Rate Limiting: Documented, implementation pending

## Phase 7 – Adaptive Enhancements & Discoveries
**Goals**
- Provide a buffer for requirements discovered midstream without derailing earlier phases.

**Tasks**
- [ ] Maintain a living backlog of emergent items tied to observations or stakeholder requests.
- [ ] Triage and prioritize each item; decide whether it fits Phase 7 or needs rescheduling.
- [ ] Document decisions/deviations from the original plan, including rationale and owners.
- [ ] Ensure Phase 7 deliverables include at least manual validation before acceptance.
- [ ] Close Phase 7 only after backlog items are resolved or explicitly deferred.

## Phase 8 – Comprehensive Testing & Certification
**Goals**
- Build the full automated test suite (unit, integration, E2E) to certify the centralized identity system.

**Tasks**
- [ ] Implement MSTest-based coverage for Identity (AccountType rules), OpenIddict flows, API-key policies, signed-URL services, and agent auth integrations.
- [ ] Add Aspire integration test that boots the simulator camera agent with default SYSTEM credentials, acquires a real token/API key from SkyMonitor, and calls a protected endpoint (serves as the template for ZWO once mirrored).
- [ ] Add end-to-end workflows (user login → token issuance → signed URL consumption, agent credential flows, etc.).
- [ ] Wire tests into CI (`dotnet test` with coverage) and enforce pass/fail gates.
- [ ] Update documentation to reference the new automated tests and how to run them locally.
- [ ] Execute final `dotnet build` + `dotnet test` runs on the full solution, record results, and obtain sign-off.
