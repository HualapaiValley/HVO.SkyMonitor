# Documentation Catalog

This catalog trims the legacy `/docs` tree into a handful of core references. The table
below shows the destination for each major topic so we can consolidate the content
without losing context.

| Topic | Target Doc | Notes |
| --- | --- | --- |
| Project vision & AllSky phases | `docs/overview.md` | Replaces `AllSky_Agent_POC_Summary.md` and `architecture/phase-plan.md` with an up-to-date summary tied to AllSky Phase 1.0. |
| Secrets & configuration | `docs/security/secrets.md` | Consolidates `SECRETS_MANAGEMENT.md`, `SECRETS_QUICKSTART.md`, `SECRETS_SUMMARY.md`, and identity-specific `secrets-reference.md`. |
| Identity program status | `docs/identity/overview.md` | Rolls up `identity/hardening-summary.md`, `operations-index.md`, `agent-registration-plan.md`, and `non-azure-delta.md`. |
| Identity runbooks | `docs/identity/operations-runbook.md` | Existing step-by-step guide (rename only if needed). Referenced from `identity/overview.md`. |
| Infra & testing modernization | `docs/projects/infra-modernization.md` | Supersedes `plan-infraAndTestingVNext.prompt.md` and merges next-step items from `future-infra-todos.md`. |
| Analyzer clean-up plan | `docs/projects/AnalyzerRemediationPlan.prompt.md` | Stays separate because it is an active engineering plan. |
| Runbooks (daily ops) | `docs/runbooks/*.md` | `local-dev.md`, `infra-operations.md`, and `ci-pipeline.md` remain the authoritative workflow docs. |

## Cleanup Rules

1. Delete any superseded files after their content lands in the destination doc.
2. Keep prompts (`*.prompt.md`) only when they describe ongoing engineering scope; archive
   them once the scope is complete.
3. Runbooks live under `docs/runbooks/` and should reference the new core docs instead of
   duplicating content.

## Work Sequencing

1. Draft `overview.md`, `security/secrets.md`, and `identity/overview.md` so the old files can be removed.
2. Merge the modernization plans into `projects/infra-modernization.md`.
3. Update runbooks to point at the consolidated docs (no procedural changes expected).
4. Delete the redundant markdown files once their replacements are committed.
