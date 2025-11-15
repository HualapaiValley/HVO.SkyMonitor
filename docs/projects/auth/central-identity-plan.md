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

**Tasks**
- [ ] Add `HVO.SkyMonitor.Tests` coverage for:
	- AccountType validation (USER vs SYSTEM login restrictions).
	- OAuth2 Authorization Code + PKCE flow.
	- OAuth2 Client Credentials flow.
	- API key authentication honoring account types.
	- Signed URL validation.
	- Authorization policies (`RequireSystemAccount`, `RequireUserAccount`).
- [ ] Create `HVO.SkyMonitor.CameraAgent.Tests` for the simulator covering:
	- Central authentication service behavior (token acquisition, caching).
	- JWT validation.
	- `HttpClient` configuration that injects tokens or API keys appropriately.
- [ ] Group / document the tests per functionality to keep future ZWO-agent tests aligned.

## Phase 6 – Hardening, Operations & Observability
**Goals**
- Productionize the auth system with proper secret storage, logging, metrics, rate limiting, and runbooks.

**Tasks**
- [ ] Move OpenIddict signing keys, API-key hashing keys, and signed-URL HMAC keys into the designated secret store (document retrieval & rotation).
- [ ] Configure production-grade TLS for HVO.SkyMonitor and any public endpoints.
- [ ] Implement structured logging for login success/failure, token issuance, API-key usage, and signed-URL validation failures (avoid sensitive payloads).
- [ ] Publish metrics (token requests per client, API-key auth success/failure, signed-URL rejection counts, latency/error rates per key API) via Prometheus/App Insights exporters.
- [ ] Add rate limiting/IP throttling to `/connect/token` and other sensitive endpoints; re-validate CSRF protections for cookie flows.
- [ ] Define key rotation/incident response procedures, including overlap windows where old and new keys are accepted.
- [ ] Produce runbooks for onboarding accounts, issuing/rotating keys, revoking access, and handling 401/403 scenarios across services.

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
- [ ] Add end-to-end workflows (user login → token issuance → signed URL consumption, agent credential flows, etc.).
- [ ] Wire tests into CI (`dotnet test` with coverage) and enforce pass/fail gates.
- [ ] Update documentation to reference the new automated tests and how to run them locally.
- [ ] Execute final `dotnet build` + `dotnet test` runs on the full solution, record results, and obtain sign-off.
