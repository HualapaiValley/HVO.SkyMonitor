# Phase Execution Plan

> Use this plan to track delivery across the modernization phases. Each task includes a checkbox (`[ ]` / `[x]`) so we can mark progress directly in the doc. Unless noted, lightweight smoke tests are sufficient until we have end-to-end flows; comprehensive testing hardening happens once Phase&nbsp;02 wiring is available.

## Phase 01 – Architecture, Auth & Contracts (In Progress)
**Outcomes**
- Shared understanding of the camera-agent platform, multi-tenant auth approach, and RAW-first contracts.
- Azure Entra External ID stands up as the central identity broker, with the LogicHost-style local Identity fallback (SQLite + cookie auth) documented.

**Work Items**
- [x] Capture platform overview (`docs/architecture/camera-agent-platform.md`).
- [x] Document centralized auth + fallback strategy (`docs/architecture/auth-strategy.md`, diagram updates).
- [x] Document capture/storage contracts and telemetry payloads (`docs/architecture/contracts.md`).
- [x] Harden `CentralIdentityOptions` validation for External ID metadata (via `CentralIdentityOptionsValidator` + unit tests).
- [x] Implement External ID user flows + confidential client wiring in LogicHost (including UI toggle between External ID redirect and the local Identity area).
- [x] Implement External ID user flows + confidential client wiring in CameraAgent (including UI toggle between External ID redirect and the local Identity area).
- [x] Implement API-key/local admin fallback wiring with seeded Identity user + rotation guidance (`LocalIdentity` options + `docs/identity/secrets-reference.md` / runbook updates).
- [x] Update runbooks (`docs/identity/operations-runbook.md`, `operations-index.md`) with External ID onboarding steps.
- [x] Document CameraAgent registration + secret escrow architecture (`docs/identity/agent-registration-plan.md`).
- [x] Scaffold LogicHost `DeviceRegistration` entity/service and `/api/internal/devices/verify` endpoint for agent pairing.
- [x] Implement LogicHost envelope issuance API + backend services for agent registration (`DeviceRegistrationEnvelopeService`, `/api/internal/devices/envelope`).
- [x] Build LogicHost portal UI to surface pending registrations + downloadable envelopes (`/devices`).
- [x] Implement LogicHost `/api/device/bootstrap` endpoint + services to activate registrations and deliver encrypted secrets.
- [x] Implement CameraAgent bootstrap UI + secure storage integration for LogicHost-issued secrets (`/devices/bootstrap`).

**Testing Focus**
- Smoke test token acquisition + API calls via unit/integration tests (`CentralAuthenticationService`, LogicHost auth handlers).
- Manual validation of the local Identity (SQLite) admin fallback during setup drills.

## Phase 02 – Multi-Tenant Ingest Foundation
**Outcomes**
- Agents can push RAW frames + metadata to LogicHost through standardized envelopes.
- LogicHost persists frames/metadata and exposes freshest frame views without per-agent polling.

**Work Items**
- [ ] Define `FrameEnvelope` contract in `HVO.SkyMonitor.Common` + update `contracts.md`.
- [ ] Extend capture pipeline with upload step that emits envelopes + object references.
- [ ] Implement LogicHost `/api/v1.0/frames` endpoint validated by External ID scopes.
- [ ] Integrate MinIO (or cloud blob storage) for tenant-isolated RAW storage namespaces.
- [ ] Replace `ILatestFrameAccessor` with distributed cache keyed by `observatoryId` for UI previews.
- [ ] Document ingest dataflow in diagrams (`diagrams/end-to-end-flow.mmd`).

**Testing Focus**
- Unit tests for envelope serialization + validation.
- Integration test covering agent upload → LogicHost ingest → storage persistence (can leverage Testcontainers MinIO/Postgres later in phase).

## Phase 03 – Observability & Derivatives
**Outcomes**
- Derivative planners and telemetry APIs provide insight into capture health.
- Azure Monitor dashboards visualize camera-agent and LogicHost KPIs.

**Work Items**
- [ ] Implement derivative planner module + configurable steps (stacking, overlays).
- [ ] Expose capture telemetry APIs on agent + LogicHost (versioned endpoints).
- [ ] Wire `CaptureTelemetryMetricsRecorder` into Azure Monitor dashboards/alerts.
- [ ] Expand `docs/architecture/camera-agent-platform.md` with observability sections + diagrams.

**Testing Focus**
- Unit tests for planner pipelines and telemetry aggregations.
- Begin automated regression tests for telemetry endpoints once ingestion path (Phase 02) is stable.

## Phase 04 – Tenant Provisioning Automation
**Outcomes**
- Self-service onboarding for observatories, including identity, storage, and config bundle generation.

**Work Items**
- [ ] Build LogicHost admin UI/API flow for tenant onboarding, including External ID allow-list updates.
- [ ] Automate storage/queue provisioning per tenant (Bicep/Azure CLI/Terraform).
- [ ] Implement device enrollment or enrollment tokens for camera agents.
- [ ] Update runbooks and diagrams to reflect automated provisioning lifecycle.

**Testing Focus**
- Scenario tests for onboarding workflow (can be manual or scripted) ensuring tenants receive working config packages.
- Security validation around provisioning secrets + audit logging.

## How to Update This Plan
- Mark tasks completed by switching `[ ]` to `[x]` and optionally add date/PR link in parentheses.
- When new scope emerges, add rows to the relevant phase and cross-link to the owning doc or issue.
- Keep this plan, `future-work.md`, and the diagrams in sync whenever architecture decisions change.
