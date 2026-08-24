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
| Agent prompts | `docs/planning/agent-prompts.md` | Reusable GPT-5.6 Sol, Terra, Luna, research, review, implementation, and handoff prompts. |
| Performance validation | `docs/planning/performance-validation.md` | Canonical workloads, measurement record, phase gates, and evidence rule for justified complexity. |
| Requirements crosswalk | `docs/planning/requirements-crosswalk.md` | Maps retained normative requirement groups to owning specifications, phases, issues, and deferred evidence. |
| Standalone CameraAgent course correction | `docs/planning/standalone-cameraagent-course-correction.md` | Detailed phase-12A product outcome, work packages, dependency order, and tiered validation policy for issues #205-#211. |
| Document migration | `docs/planning/document-migration.md` | Requirement destinations and safe retirement record for superseded plans. |
| Project implementation prompt | `.github/prompts/implement-project-phase.prompt.md` | Executes one ready issue from epic #89 through the complete PR lifecycle. |
| Pull request evidence template | `.github/pull_request_template.md` | Required scope, validation, output, performance, runtime, correction, and current-head green evidence. |
| Virtual camera and validation | `docs/virtual-camera.md` | Mono/color sensor modes, fisheye/rectilinear optics, fixtures, and planetarium comparison strategy. |
| Capture manifest v2 | `docs/contracts/capture-manifest-v2.md` | Reconstructable identity, timing, layout, profile, recipe, lineage, compatibility, validation, and zero-copy semantics. |
| Processing recipes v1 | `docs/contracts/processing-recipes-v1.md` | Canonical recipe identity, selectors, variants, products, outcomes, algorithms, memory ownership, and host-adapter boundaries. |
| Transient contracts v1 | `docs/contracts/transient-contracts-v1.md` | Versioned event evidence, source locators, assessments, canonical JSON, and validated linear detector-input ownership. |
| Transient extraction and assessment v1 | `docs/contracts/transient-extraction-assessment-v1.md` | Deterministic residual components, mask and saturation semantics, geometry, receipts, observation promotion, assessment, and bounded execution. |
| Fireball/transient architecture | `docs/projects/fireball-transient-detection.md` | Subsystem constraints and design decisions; roadmap status and promotion remain in `docs/project-plan.md` and issues #61-#65. |
| Legacy implementation references | `docs/reference-code.md` | Commit-pinned V5/V6 source map, behavior notes, caveats, and checkout instructions. |
| Secrets & configuration | `docs/security/secrets.md` | Consolidates `SECRETS_MANAGEMENT.md`, `SECRETS_QUICKSTART.md`, `SECRETS_SUMMARY.md`, and identity-specific `secrets-reference.md`. |
| Identity architecture | `docs/identity/overview.md` | Registration/bootstrap and local identity boundaries; live status remains in the project plan/issues. |
| Identity runbooks | `docs/identity/operations-runbook.md` | Current SQL Server/Compose routes, onboarding, rotation, revocation, incident, backup, monitoring, and troubleshooting procedures. |
| Shared SQL Server operations | `docs/runbooks/sql-server-operations.md` | SQL Server 2022 Query Store, blocking/deadlock, capacity, backup/restore, maintenance observation, permission, and cleanup procedures. |
| Runbooks (daily ops) | `docs/runbooks/*.md` | `local-dev.md`, `infra-operations.md`, and `ci-pipeline.md` remain the authoritative workflow docs. |
| Smoke-test environment | `docs/runbooks/smoke-test.md` | Owner-only smoke inputs, initialization, fail-closed preflight, generated-state boundary, and data-policy guard. |
| Split-host deployment and smoke | `docs/runbooks/split-host-preflight.md` | Versioned inventory, preparation, immutable images, catalogs, services, application-assisted bootstrap, continuity refusal, W0 smoke, explicit teardown, resumable ledgers, and sanitized evidence. |
| Local CameraAgent installer | `docs/runbooks/deployment-installer.md` | Self-contained Linux CLI, verified local/offline inputs, UUID-scoped layout, owner bootstrap, dry run, resume, and structured installation evidence. |
| Signed distribution releases | `docs/runbooks/release-distribution.md` | Independent installer/catalog trains, Key Vault signing, immutable publication, public verification, retention, mirrors, and recovery. |
| CameraAgent retention recovery | `docs/runbooks/cameraagent-retention.md` | Pending-upload retention invariant and outage recovery procedure. |

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
