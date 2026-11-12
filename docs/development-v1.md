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

A pull request that changes what operators or users notice adds a line under `## [Unreleased]` in [`CHANGELOG.md`](../CHANGELOG.md). The rule is in [`AGENTS.md`](../AGENTS.md#changelog).

## Issue And Pull Request Forms

- [Development v1 issue form](../.github/ISSUE_TEMPLATE/development-v1.yml)
- [Development v1 pull request template](../.github/PULL_REQUEST_TEMPLATE/development-v1.md)
- [Development v1 review process](runbooks/development-v1-review.md)
- [Pull request walkthrough](runbooks/pull-request-walkthrough.md): the step-by-step procedure with commands, using a real PR as the example
- [Repository setup](runbooks/repository-setup.md): how to bring another repository into this model

Issues merged into `development/v1` are closed explicitly with the pull request and merge SHA. Completion comments state that the work is not promoted to `main`.

## Required CI

The repository pins .NET SDK 10.0.402 in global.json with rollForward: disable;
local builds, the devcontainer, CI, and SDK container images use that version.
CameraAgent and LogicHost runtime images use ASP.NET Core 10.0.14 (#1667).
ProcessingRunner's `runtime-deps` base moves to 10.0.14 with #1668 as an OS-layer update only;
its self-contained .NET runtime and the SDK-bundled runtime remain at 10.0.13 because SDK 10.0.402 is unchanged.
Microsoft NuGet package pins use 10.0.14. The .NET 10.0.14 runtime security fixes reach ProcessingRunner only with a coordinated SDK update.

The branch currently requires:

- **Development v1 / Preflight** on a GitHub-hosted runner: exact-range whitespace validation, pinned workflow linting, shell syntax, the inexpensive CI-control regression checks, coverage baseline policy, the full-history [secret scan](runbooks/ci-pipeline.md#secret-scan), and, when the classifier selects them, the [release script tests](runbooks/ci-pipeline.md#release-script-tests).
- **Development v1 / Build and Unit** on a self-hosted x64 runner: pinned SDK setup, solution restore, warning-clean Release build, the complete Docker-disabled Unit selection, and [canonical per-file coverage floors](runbooks/ci-pipeline.md#development-v1-per-file-coverage). Collection targets the assemblies owning those floors. Catalog-dependent ReleaseTool and full-suite aggregate/component coverage retain their documented local/full-qualification scope.

Draft pull requests run hosted Preflight only. Marking a reviewed pull request ready starts the self-hosted Build and Unit check. A merge-queue group (`merge_group`) runs both checks on the group's temporary commit, together with the [queue's reviewed-head gate](#merge-queue). Pushes to `development/v1` run both checks.

### Workflow contract YAML parser

`scripts/test:development-v1-ci` requires PyYAML and fails closed if it is
unavailable. Job IDs, names and reusable-workflow `uses` values come from parsed
YAML nodes; the canonical source view must agree on job boundaries, locations and
field values. Quoted or flow syntax cannot hide a producer from the parse.

Hosted Preflight installs PyYAML 6.0.2 from the hash-pinned CPython 3.12 Linux
x64 wheel in `scripts/ci/pyyaml-requirements.txt`, using `--require-hashes`,
`--only-binary` and `--no-deps`. Its private target directory is exported on
`PYTHONPATH` before the contract runs. Local developers may use `python3-yaml`
or the yamllint pipx fallback; the contract reports its interpreter and parser
version and tests refusal when neither is available.

The workflow contract compares the complete queue-gate source with its approved
fixture, including workflow/job defaults, environment and container settings.
All workflows and job overrides reject `checks: write`, `statuses: write` and
`write-all`; workflow tokens cannot publish an alternate required check/status.
Job properties must use known lowercase keys. Workflow discovery includes
hidden files and case-insensitive `.yml`/`.yaml` extensions. Both lint steps enumerate with NUL delimiters, pass
an explicit file array to actionlint and reject an empty list;
actionlint's default discovery omits uppercase extensions. The contract exercises
invalid hidden and uppercase workflow files against pinned actionlint 1.7.12.
Local runs can set `HVO_ACTIONLINT_BIN` to that binary; CI uses the binary already
installed by the lint step. Missing or mismatched versions reject the contract
rather than skip its behavioral regressions.

### Merge queue

`development/v1` merges through a GitHub merge queue. The `development/v1 merge
queue` ruleset is active on the branch. It groups with **ALLGREEN** (every
grouped PR passes required checks), merges with **MERGE** (merge commits), and
requires `Development v1 / Preflight`, `Development v1 / Build and Unit` and
`hvo/reviewed-head`. [AGENTS.md](../AGENTS.md) says who publishes that status and
who enqueues.

Both v1 checks are wired for `merge_group`, which tests the combined changes of
the queued PR and entries ahead of it. Preflight has four minutes for `pull_request` events and six minutes for
push/queue events, whose classifier selects full mode. This changes the timeout, not the qualification profile.

The queue's temporary commit has no operator review status. The
`merge-queue-reviewed-head.yml` job checks out the **fresh `development/v1` tip**
and runs that trusted copy of `scripts/ci:queue-reviewed-head`. A missing checkout
or script fails the job; it never falls back to the queued tree. The gate
therefore always runs the script as it stands on the trusted branch.
The gate refreshes the destination tip through the read-only ref API on each
iteration and walks first parents from the group SHA to that tip, requiring
exactly two parents per entry (at most 100). Every second parent must equal an
open PR's current head targeting this repository's `development/v1`; fork PRs are
supported using statuses in the base repository. One or more matching PR
associations are accepted for earlier entries with a shared head. An earlier
entry merging during traversal stops the walk at the refreshed trusted tip. Each head's numeric newest
`hvo/reviewed-head` status, across all API pages, must be `success` from exactly
`RoySalisbury`. Unsupported topology, no matching PR association, a changed PR head,
an unreadable page, or a base that advanced beyond the group fails closed.
On PRs, `hvo/reviewed-head` remains the operator's commit status.

The [published webhook schema](https://github.com/octokit/webhooks/blob/main/payload-schemas/api.github.com/merge_group/checks_requested.schema.json)
describes `base_sha` only as “the SHA of the merge group's parent commit”; it does
not establish that a grouped parent excludes earlier queued entries. GitHub's
[queue documentation](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-a-merge-queue)
explicitly includes changes ahead of a PR. We therefore do **not** treat event
`base_sha` as a trust root. The supported topology assumes GitHub's MERGE mode
forms a two-parent chain ending at the actual destination tip, and verifies that
chain at runtime, on every live group. Offline two- and three-entry fixtures
cover this assumption, and a group of any other shape fails closed. A group whose chain does not reach the refreshed destination tip must be rebuilt.
A main-to-development/v1 back-merge cannot be an earlier non-head entry unless
GitHub associates its exact head with an open PR on this destination; it fails
closed when that association is absent. Use a single-entry group or rebuild the group.

Changes to the workflow/script environment interface require two phases: first
land a trusted-base script that accepts both the old and new inputs without
weakening validation; then change the queued workflow caller. Retire the old
interface only after groups using it have drained. A caller-only change cannot
assume its matching script has reached the trusted branch.

The [settings snapshot and cutover/rollback checklist](runbooks/development-v1-merge-queue.md)
record the operated policy. The required `hvo/reviewed-head` source stays **any
source** while the operator status and GitHub Actions queue job share that name;
only a separately approved external App migration can pin an integration id.

**Remaining trust boundary:** the workflow file itself still comes from the
queued commit. Trusted script checkout and the all-workflow unique-name contract
reduce accidental or direct script self-approval, but a PR can still change the
workflow or its tests. They do not create an independently enforced review
authority. The durable fix is an external GitHub App producer whose required
check pins its integration id. That is a separate decision for Roy and is not
implemented by this change.

A successful queue check is a point-in-time API observation. To revoke review
after that check succeeds, **dequeue the PR** as well as changing its review
status; re-entry must create a fresh group and rerun the checks. Merely publishing
a failure does not retract an already successful queue check.

The target controlling path is under ten minutes, with a 1080-second cumulative
workload budget and a 22-minute hard job timeout. Each self-hosted run retains
restore/build/Unit/coverage timing, largest-process RSS, Unit TRX and raw/merged
coverage evidence in the `hvo-development-v1-ci-v5` manifest. Failed runs retain
their evidence too.

### Short checks and full qualification

The operator-approved #1142 split keeps these checks off the short path:

| Check | Required full-qualification owner in `ci.yml` |
| --- | --- |
| Whole-repository ShellCheck and its contract tests | Quality |
| Exhaustive test discovery, category disjointness and inventory counts | Build |
| Integration, full-suite aggregate/component coverage, architecture/publish and migrations | Existing full-matrix jobs; measurable canonical per-file floors run in Build and Unit |
| Deployment qualification | Existing selected Deployment Contracts job |

The catalog reproducibility regressions are full-qualification only as well
(Refs #1774): Catalog Contracts rebuilds HYG 4.2 and Catalog Reproducibility
rebuilds HYG 4.4, OpenNGC and the catalog test fixtures, each under both SQLite
`secure_delete` defaults. Neither required v1 check runs them or reads the
classifier's `catalog_recipe` output; see
[Catalog Reproducibility](runbooks/ci-pipeline.md#catalog-reproducibility).

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

## Repository Settings

Section 2 of the [install contract](hvo-install-contract.md) also needs repository
settings. Agents do not change repository settings; the owner applies these after
approving them (#1321). The tag ruleset is applied; the others are pending that
approval. The current values were read on 2026-10-16, and the tag ruleset on
2026-10-17.

| Setting | Current | Recommended |
| --- | --- | --- |
| Merge methods | Merge commits, squash and rebase all allowed | Merge commits only, so every commit keeps its SHA; `.gitleaks.toml` exceptions name commits |
| Delete branch on merge | Off | On |
| `v*` release tags | Ruleset `release tags` (id 25302808), active on `refs/tags/v*`: creation, update, deletion and non-fast-forward rules, with repository administrators as the only bypass, as RoofController's `release tags` ruleset does | Unchanged |
| Required checks | `development/v1`: Development v1 / Preflight and Build and Unit. `main`: Required CI | Unchanged. The secret scan runs inside Preflight and inside Quality, which Required CI aggregates |
| GitHub secret scanning and push protection | Off | Optional: on. Both are free for public repositories, and push protection rejects a recognised provider token before it is pushed, which a CI history scan cannot do |

The owner can apply them with:

```bash
gh api -X PATCH repos/HualapaiValley/HVO.SkyMonitor \
  -F allow_merge_commit=true -F allow_squash_merge=false -F allow_rebase_merge=false \
  -F delete_branch_on_merge=true
# Optional:
gh api -X PATCH repos/HualapaiValley/HVO.SkyMonitor \
  -f 'security_and_analysis[secret_scanning][status]=enabled' \
  -f 'security_and_analysis[secret_scanning_push_protection][status]=enabled'
```

The [integration pre-release workflow](runbooks/prereleases.md) creates no tags:
the owner pushes them, so it needs no bypass. If a workflow later creates `v*`
tags, add its actor as a ruleset bypass at that time.

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

Supported deployment operations use `HVO.SkyMonitor.Deployment.Cli`. Development v1 CI does not perform deployment, release packaging, complete integration qualification, full-suite aggregate coverage qualification, or production mutation.

An annotated `vX.Y.Z-dev.<n>` tag that the owner pushes on a green `development/v1` commit publishes an integration pre-release for smoke testing; see [Integration Pre-releases](runbooks/prereleases.md).
