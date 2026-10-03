# Development v1

[![Development v1 CI](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/development-v1.yml/badge.svg?branch=development%2Fv1)](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/development-v1.yml?query=branch%3Adevelopment%2Fv1)

`development/v1` is the daily integration branch for the next development line. It is independent from `main` and does not imply promotion to the stable line.

## Daily Flow

```text
development/v1
  -> feature/<issue>-<short-name>
  -> draft pull request targeting development/v1
  -> independent review and finding-thread resolution
  -> ready pull request
  -> required Development v1 CI
  -> merge into development/v1
```

Create work from the current `development/v1` head. Ordinary issue branches and pull requests target `development/v1`, not `main`.

## Issue And Pull Request Forms

- [Development v1 issue form](../.github/ISSUE_TEMPLATE/development-v1.yml)
- [Development v1 pull request template](../.github/PULL_REQUEST_TEMPLATE/development-v1.md)
- [Development v1 review process](runbooks/development-v1-review.md)
- [Pull request walkthrough](runbooks/pull-request-walkthrough.md): the step-by-step procedure with commands, using a real PR as the example
- [Repository setup](runbooks/repository-setup.md): how to bring another repository into this model

Issues merged into `development/v1` are closed explicitly with the pull request and merge SHA. Completion comments state that the work is not promoted to `main`.

## Required CI

The branch currently requires:

- **Development v1 / Preflight** on a GitHub-hosted runner: exact-range whitespace validation, pinned workflow linting, shell syntax and the inexpensive CI-control regression checks.
- **Development v1 / Build and Unit** on a self-hosted x64 runner: pinned SDK setup, solution restore, warning-clean Release build, and the complete Docker-disabled Unit selection.

Draft pull requests run hosted Preflight only. Marking a reviewed pull request ready starts the self-hosted Build and Unit check. Pushes to `development/v1` run both checks.

The target controlling path is under ten minutes, with a 720-second cumulative
workload budget and a 15-minute hard job timeout. Each self-hosted run retains
restore/build/Unit timing, largest-process RSS, and Unit TRX evidence in the
`hvo-development-v1-ci-v4` manifest. Failed runs retain their evidence too.

### Short checks and full qualification

The operator-approved #1142 split keeps these checks off the short path:

| Check | Required full-qualification owner in `ci.yml` |
| --- | --- |
| Whole-repository ShellCheck and its contract tests | Quality |
| Exhaustive test discovery, category disjointness and inventory counts | Build |
| Integration, coverage, architecture/publish and migrations | Existing full-matrix jobs |
| Deployment qualification | Existing selected Deployment Contracts job |

The short run still builds the entire solution warning-clean and executes the
complete positive Unit selection with an invalid Docker endpoint. It does not
claim exhaustive category or full qualification evidence. Changes to shell or
category behavior require focused local checks; an inventory/category defect
not exercised by Unit remains a promotion blocker when full CI discovers it.

For ordinary v1 PRs, agents must use these two branch-required checks. Record
`profile=development-v1` with local validation and review evidence.
`scripts/ci:classify` describes affected local/full-qualification scope;
`mode=full` or `complete=true` is **not** a request to manually dispatch
`ci.yml`. This remains true after syncing a broad set of upstream changes.
Risk-tier local gates and explicit issue acceptance still apply. Run extra full
qualification only when the issue or operator explicitly requires it, recording
that reason separately. Passing v1 is not promotion to main or production
qualification.

## Review

Mechanical, Standard, and Deep review levels use one parent review with one resolvable child thread per finding. Author resolutions and independent reviewer verification remain in the finding thread. A pull request is marked ready only after the current head is reviewed, every finding has a verified terminal disposition, and every review thread is resolved.

## Promotion To main

`main` moves only by promotion from `development/v1`. Every night at 02:00
America/Phoenix, `.github/workflows/promote-main.yml` compares the two branches
and, when `development/v1` is ahead, opens or refreshes one draft promotion pull
request as `hvo-agentcontrol[bot]`. It refuses to promote a head whose own
Development v1 push run is not green, and it halts if `main` has commits that
`development/v1` lacks, because that means the branch model was bypassed.

New promotions remain draft while feature work continues. A title/body refresh
does not mark an existing draft ready. A reviewed promotion is marked ready
only for a planned qualification/merge window; return it to draft if that
window is deferred or its reviewed source changes. This prevents a moving,
unreviewed promotion from launching full CI for every development merge.

The full pipeline on `main` (`Required CI`) is the qualification gate for the
ready promotion pull request. Merging is the operator's decision, taken with a merge
commit so every `development/v1` commit keeps its SHA on `main`. The workflow
never pushes to `main` and never merges; its token is pull-requests write only.
It can be run on demand with `gh workflow run promote-main.yml`.

## Deployment

Supported deployment operations use `HVO.SkyMonitor.Deployment.Cli`. Development v1 CI does not perform deployment, release packaging, complete integration qualification, coverage aggregation, or production mutation.
