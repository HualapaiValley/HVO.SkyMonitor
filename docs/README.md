# Documentation Catalog

The documents below have distinct ownership. Product direction, architecture,
phase order, and aggregate status live in the project plan. GitHub issues own
live issue/PR execution state and evidence. Runbooks and focused subsystem
documents link back without maintaining a competing roadmap.

| Topic | Target Doc | Notes |
| --- | --- | --- |
| Product architecture and implementation plan | `docs/project-plan.md` | Authoritative virtual-first architecture, requirements, current status, phases, issues, and acceptance criteria. |
| Agent execution and handoff | `docs/planning/agent-execution.md` | Required issue lifecycle, performance evidence, validation, review correction, green merge, and resumable handoff protocol. |
| Agent prompts | `docs/planning/agent-prompts.md` | Reusable GPT-5.6 Sol, Terra, Luna, research, review, implementation, and handoff prompts. |
| Performance validation | `docs/planning/performance-validation.md` | Canonical workloads, measurement record, phase gates, and evidence rule for justified complexity. |
| Requirements crosswalk | `docs/planning/requirements-crosswalk.md` | Maps retained normative requirement groups to owning specifications, phases, issues, and deferred evidence. |
| Document migration | `docs/planning/document-migration.md` | Requirement destinations and safe retirement record for superseded plans. |
| Project implementation prompt | `.github/prompts/implement-project-phase.prompt.md` | Executes one ready issue from epic #89 through the complete PR lifecycle. |
| Pull request evidence template | `.github/pull_request_template.md` | Required scope, validation, output, performance, runtime, correction, and current-head green evidence. |
| Virtual camera and validation | `docs/virtual-camera.md` | Mono/color sensor modes, fisheye/rectilinear optics, fixtures, and planetarium comparison strategy. |
| Fireball/transient architecture | `docs/projects/fireball-transient-detection.md` | Subsystem constraints and design decisions; roadmap status and promotion remain in `docs/project-plan.md` and issues #61-#65. |
| Legacy implementation references | `docs/reference-code.md` | Commit-pinned V5/V6 source map, behavior notes, caveats, and checkout instructions. |
| Secrets & configuration | `docs/security/secrets.md` | Consolidates `SECRETS_MANAGEMENT.md`, `SECRETS_QUICKSTART.md`, `SECRETS_SUMMARY.md`, and identity-specific `secrets-reference.md`. |
| Identity architecture | `docs/identity/overview.md` | Registration/bootstrap and local identity boundaries; live status remains in the project plan/issues. |
| Identity runbooks | `docs/identity/operations-runbook.md` | Current SQL Server/Compose routes, onboarding, rotation, revocation, incident, backup, monitoring, and troubleshooting procedures. |
| Runbooks (daily ops) | `docs/runbooks/*.md` | `local-dev.md`, `infra-operations.md`, and `ci-pipeline.md` remain the authoritative workflow docs. |
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
