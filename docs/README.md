# Documentation Catalog

The documents below have distinct ownership. Portfolio direction and stable
roadmap initiative IDs live in the roadmap. Architecture, virtual-first phase
order, requirements, and completion live in the project plan. GitHub issues own
live issue/PR execution state and evidence. Runbooks and focused subsystem
documents link back without maintaining competing status.

| Topic | Target Doc | Notes |
| --- | --- | --- |
| Product roadmap | `docs/roadmap.md` | Portfolio initiatives, stable `RM-###` IDs, planning horizons, top-level epic mappings, and high-level dependencies. |
| Product architecture and implementation plan | `docs/project-plan.md` | Authoritative virtual-first architecture, requirements, phase status, issues, and acceptance criteria. |
| Agent execution and handoff | `docs/planning/agent-execution.md` | Required issue lifecycle, performance evidence, validation, review correction, green merge, and resumable handoff protocol. |
| PR lifecycle skill | `.agents/skills/pr-lifecycle/SKILL.md` | Canonical draft, review acquisition, correction cap, finalization lock, protected CI, merge, and cleanup procedure. |
| Agent prompts | `docs/planning/agent-prompts.md` | Capability-first research, review, implementation, and handoff prompts. |
| Performance validation | `docs/planning/performance-validation.md` | Canonical workloads, measurement record, phase gates, and evidence rule for justified complexity. |
| Requirements crosswalk | `docs/planning/requirements-crosswalk.md` | Maps retained normative requirement groups to owning specifications, phases, issues, and deferred evidence. |
| Standalone CameraAgent course correction | `docs/planning/standalone-cameraagent-course-correction.md` | Detailed phase-12A product outcome, work packages, dependency order, and tiered validation policy for issues #205-#211. |
| Document migration | `docs/planning/document-migration.md` | Requirement destinations and safe retirement record for superseded plans. |
| Project implementation prompt | `.github/prompts/implement-project-phase.prompt.md` | Executes one ready issue from its owning roadmap epic through the complete PR lifecycle. |
| Pull request evidence template | `.github/pull_request_template.md` | Required scope, validation, output, performance, runtime, correction, and current-head green evidence. |
| Virtual camera and validation | `docs/virtual-camera.md` | Mono/color sensor modes, fisheye/rectilinear optics, fixtures, and planetarium comparison strategy. |
| Capture manifest v2 | `docs/contracts/capture-manifest-v2.md` | Reconstructable identity, timing, layout, profile, recipe, lineage, compatibility, validation, and zero-copy semantics. |
| Processing recipes v1 | `docs/contracts/processing-recipes-v1.md` | Canonical recipe identity, selectors, variants, products, outcomes, algorithms, memory ownership, and host-adapter boundaries. |
| CameraAgent execution evidence v1 | `docs/contracts/cameraagent-execution-evidence-v1.md` | Transport-neutral immutable graph revision and execution evidence: content-addressed bodies, origin sequencing, canonical payload hashing, receipt/validation/acceptance/acknowledgement facts, gap detection and bounded resynchronization, append-only corrections, limits, reason codes, and redaction. |
| CameraAgent operator settings and API keys v1 | `docs/contracts/cameraagent-operator-settings-v1.md` | The settings catalogue, values with their sources, version-checked group writes, and owner-created scoped API keys for the operations API. |
| Transient contracts v1 | `docs/contracts/transient-contracts-v1.md` | Versioned event evidence, source locators, assessments, canonical JSON, and validated linear detector-input ownership. |
| Transient extraction and assessment v1 | `docs/contracts/transient-extraction-assessment-v1.md` | Deterministic residual components, mask and saturation semantics, geometry, receipts, observation promotion, assessment, and bounded execution. |
| Fireball/transient architecture | `docs/projects/fireball-transient-detection.md` | Subsystem constraints and design decisions; portfolio status lives in `docs/roadmap.md`, virtual-first phase history in `docs/project-plan.md`, and execution state in issues #61-#65. |
| Legacy implementation references | `docs/reference-code.md` | Commit-pinned V5/V6 source map, behavior notes, caveats, and checkout instructions. |
| Secrets & configuration | `docs/security/secrets.md` | Consolidates `SECRETS_MANAGEMENT.md`, `SECRETS_QUICKSTART.md`, `SECRETS_SUMMARY.md`, and identity-specific `secrets-reference.md`. |
| Identity architecture | `docs/identity/overview.md` | Registration/bootstrap and local identity boundaries; portfolio order lives in the roadmap, completion history in the project plan, and live state in linked issues. |
| Identity runbooks | `docs/identity/operations-runbook.md` | Current SQL Server/Compose routes, onboarding, rotation, revocation, incident, backup, monitoring, and troubleshooting procedures. |
| Shared SQL Server operations | `docs/runbooks/sql-server-operations.md` | SQL Server 2022 Query Store, blocking/deadlock, capacity, backup/restore, maintenance observation, permission, and cleanup procedures. |
| Runbooks (daily ops) | `docs/runbooks/*.md` | `local-dev.md`, `infra-operations.md`, and `ci-pipeline.md` remain the authoritative workflow docs. |
| Smoke-test environment | `docs/runbooks/smoke-test.md` | Owner-only smoke inputs, initialization, fail-closed preflight, generated-state boundary, and data-policy guard. |
| Split-host deployment and smoke | `docs/runbooks/split-host-preflight.md` | Versioned inventory, preparation, immutable images, catalogs, services, application-assisted bootstrap, continuity refusal, W0 smoke, explicit teardown, resumable ledgers, and sanitized evidence. |
| HVO install and release contract | `docs/hvo-install-contract.md` | Installation and release contract shared with HVO.RoofController: versions, release flow, assets and trust, `install.sh`, installer commands, upgrade/rollback, container resources, runtime client, settings catalogue, naming, and the owner's decisions. Conformance is tracked in epic #1318; the installer and release runbooks list the current differences. |
| CameraAgent command line (`hvo-sky`) | `docs/runbooks/hvo-sky.md` | The runtime client: installing it from a release, owner API keys and the credentials file, status, info, settings from the catalogue, restart, rig and schedule, `--json`, exit codes, and the operations routes it uses. |
| Local CameraAgent installer | `docs/runbooks/deployment-installer.md` | Self-contained Linux CLI, verified local/offline inputs, UUID-scoped layout, owner bootstrap, dry run, resume, and structured installation evidence. |
| Self-hosted processing runner | `docs/runbooks/processing-runner.md` | `processing-runner-v1` placement configuration, runner environment, container image and compose example, health, and lease-loss/staleness operations. |
| Processing entitlements and fair scheduling | `docs/runbooks/processing-entitlements.md` | Per-observatory entitlements, weighted fair share and starvation ordering, resource classes and budgets, runner pools, usage records, signals, backpressure, and capacity guidance. |
| Elastic provider adapters | `docs/runbooks/elastic-providers.md` | `elastic-provider-v1` boundaries, the `local-process` proof adapter, autoscaling bounds and placement policy, orphan cleanup, signals, and evidence. |
| Integration pre-releases | `docs/runbooks/prereleases.md` | Cutting a `vX.Y.Z-dev.<n>` pre-release from `development/v1`, what the workflow checks and publishes, and installing or upgrading a host from it. |
| Releases | `docs/runbooks/releases.md` | The product version, cutting a final `vX.Y.Z` release or `vX.Y.Z-rc.<n>` candidate from `main`, the CI qualification, the install tests on both platforms, the owner's approval, when `latest` moves, and the owner settings it relies on. |
| CameraAgent retention recovery | `docs/runbooks/cameraagent-retention.md` | Pending-upload retention invariant and outage recovery procedure. |
| CameraAgent local automation | `docs/runbooks/cameraagent-local-automation.md` | Versioned local automation definitions, the closed task and trigger registry, storage and bounded retention, runner behaviour, restart recovery, and owner-only endpoints. |
| CameraAgent orbital-elements refresh | `docs/runbooks/orbital-elements-refresh.md` | CelesTrak GP active-group provider policy, enabling and the reserved automation definition, status/refresh/resume endpoints and reason codes, health and metrics, storage, retention and quarantine, crash and disk-full recovery, rollback, and the snapshot-reader handoff for consumers. |

## Documentation Rules

1. Delete superseded files after their still-valid content lands in the destination doc.
2. Keep prompts (`*.prompt.md`) only when they describe ongoing engineering scope; delete
   them once the scope is complete.
3. Runbooks live under `docs/runbooks/` and should reference the new core docs instead of
   duplicating content.
4. Mark a phase complete only when its acceptance checks pass in working code.
5. Dated implementation inventories belong in issues or pull requests, not permanent
    roadmap documents.
6. Never delete machine-readable evidence, unique dated measurement narratives,
   checksums, catalog provenance, or
   calibration manifests as planning cleanup.
