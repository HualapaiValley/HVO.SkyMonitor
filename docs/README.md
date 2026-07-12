# Documentation Catalog

The documents below have distinct ownership. Product direction and implementation
status live only in the project plan; runbooks and focused subsystem documents link
back to it rather than restating roadmap status.

| Topic | Target Doc | Notes |
| --- | --- | --- |
| Product architecture and implementation plan | `docs/project-plan.md` | Authoritative product description, current status, phases, and acceptance criteria. |
| Project implementation handoff | `.github/prompts/implement-project-phase.prompt.md` | Continuous phase-ordered TDD workflow with coverage, documentation, and zero-warning gates. |
| Virtual camera and validation | `docs/virtual-camera.md` | Mono/color sensor modes, fisheye/rectilinear optics, fixtures, and planetarium comparison strategy. |
| Virtual planetarium implementation | `docs/projects/virtual-planetarium-implementation.md` | Fixed Hualapai/ASI174 fisheye fixtures, work streams, Stellarium automation, test gates, and ASI174MC follow-on. |
| Fireball/transient architecture | `docs/projects/fireball-transient-detection.md` | Deferred subsystem constraints and internal dependency graph; roadmap status and promotion remain in `docs/project-plan.md`. |
| Legacy implementation references | `docs/reference-code.md` | Commit-pinned V5/V6 source map, behavior notes, caveats, and checkout instructions. |
| Secrets & configuration | `docs/security/secrets.md` | Consolidates `SECRETS_MANAGEMENT.md`, `SECRETS_QUICKSTART.md`, `SECRETS_SUMMARY.md`, and identity-specific `secrets-reference.md`. |
| Identity program status | `docs/identity/overview.md` | Rolls up `identity/hardening-summary.md`, `operations-index.md`, `agent-registration-plan.md`, and `non-azure-delta.md`. |
| Identity runbooks | `docs/identity/operations-runbook.md` | Existing step-by-step guide (rename only if needed). Referenced from `identity/overview.md`. |
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
