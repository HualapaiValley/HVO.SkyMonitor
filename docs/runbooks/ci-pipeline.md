# CI Pipeline Runbook

This runbook describes the required current-head checks in `.github/workflows/ci.yml` and their local equivalents.

This is the **full qualification** profile for main/release and explicitly
requested qualification. Ordinary PRs targeting `development/v1` use the
[short v1 profile](../development-v1.md), including after a base sync. Classifier
output does not by itself authorize an extra full-workflow dispatch for a v1 PR.
Whole-repository ShellCheck remains in Quality; exhaustive category discovery
remains in Build. Both are intentionally absent from the short workflow.

## Development v1 Preflight Budget

**Development v1 / Preflight** has a four-minute (240-second) job timeout,
per the owner decision in #1365. This supersedes the three-minute (180-second)
budget retained by #1333 when the secret-scanner contract became conditional.
PR #1364 took 173 seconds with the contract selected; the new budget provides
67 seconds of headroom for that measured run. Ordinary PRs remain near one minute.
All checks, their order and selection rules, job names and required-check names
remain unchanged.

## Promotion ancestry prerequisite

Before **every** promotion from `development/v1` to `main` becomes ready, including promotions without release
tags, follow [Before every promotion](releases.md#before-every-promotion). A merge-commit promotion leaves a commit
on `main` that development lacks. The workflow's behind-check tolerates verified promotion merges, but that does
not satisfy `main`'s strict up-to-date protection; `Required CI` and branch protection stay unchanged.

The operator/coordinator opens the sync PR with authenticated Git/`gh`, proves its normal merge tree equals the
**pre-sync development tree**, and takes it through review, both Development v1 required checks, and a merge-commit
merge. A conflict or changed tree stops the sync. Workflow `GITHUB_TOKEN` pushes do not start downstream push CI,
and PR workflow runs may require approval; the operator step avoids relying on that token or adding secrets or
permissions. Wait for the green Development v1 **push run on the merged development head**, then verify ancestry:

<!-- promotion-sync-ready -->
```bash
(
  set -euo pipefail
  git fetch origin main development/v1
  git merge-base --is-ancestor origin/main origin/development/v1 || {
    echo "Keep the promotion draft: sync main into development/v1 first." >&2
    exit 1
  }
  git rev-parse origin/main origin/development/v1
)
```
<!-- /promotion-sync-ready -->

Record these source/target SHAs and the sync tree proof with the promotion's updated-source review. If `main` was
already an ancestor, record the no-sync result instead. Mark the promotion ready only after this prerequisite;
then full `ci.yml` qualification supplies `Required CI` on the reviewed head. Recheck when either branch moves.
A green sync PR is integration evidence and does not replace promotion qualification.

## Required Checks

| Check | Enforced behavior |
| --- | --- |
| **Change Classification** | Fail-closed selection of the full matrix for pushes and behavior-affecting pull requests or reduced mode for explicitly allowlisted documentation/developer-environment pull requests. |
| **Catalog Contracts** | Full-mode hosted build and smoke validation of the exact HYG v42 production catalog contracts, retained as a one-day workflow artifact. Skipped in classified reduced mode. |
| **Quality** | The full-history [secret scan](#secret-scan), workflow lint, syntax and documentation audits, the Unit-selection completeness control, lightweight environment/classification contracts, and Compose validation. Full mode also enforces formatting, package vulnerability/deprecation policy, and pinned .NET tools; manual dispatch additionally validates the historical Phase 14 acceptance inventory. Reduced mode does not restore or audit application packages it cannot affect. |
| **Deployment Contracts** | Deployment-relevant pull requests run the coordinator watchdog/failure contracts and current campaign-shape contracts, plus only the affected exhaustive catalog, split-host, or installer suite selected by the classifier. Main/release/manual runs execute every exhaustive suite. Otherwise its planned `skipped` result is required. |
| **Build** | Warning-clean solution Debug and Release builds plus complete, disjoint behavioral category discovery. Never component-scoped, so no component plan can hide a warning or a category-count drift. Skipped only in classified reduced mode. |
| **Unit Tests** | 13816 Unit cases with an intentionally invalid Docker endpoint and per-project TRX/Cobertura paths, plus four TRX-only supplemental runs (see [Supplemental Unit selections](#supplemental-unit-selections)) so every project that owns Unit cases is executed. Runs only for the complete solution plan; component plans run their own per-project commands inside their selected lanes including each lane’s owned supplemental Unit selection (#1261). |
