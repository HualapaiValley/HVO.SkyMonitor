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

- **Development v1 / Preflight** on a GitHub-hosted runner: exact-range whitespace validation, pinned workflow linting, shell syntax, the inexpensive CI-control regression checks, the full-history [secret scan](runbooks/ci-pipeline.md#secret-scan), and, when the classifier selects them, the [release script tests](runbooks/ci-pipeline.md#release-script-tests).
- **Development v1 / Build and Unit** on a self-hosted x64 runner: pinned SDK setup, solution restore, warning-clean Release build, and the complete Docker-disabled Unit selection.

Draft pull requests run hosted Preflight only. Marking a reviewed pull request ready starts the self-hosted Build and Unit check. Pushes to `development/v1` run both checks.

### Workflow contract YAML parser

`scripts/test:development-v1-ci` requires PyYAML and fails closed if it is
unavailable. Job IDs, names and reusable-workflow `uses` values come from parsed
YAML nodes; the canonical source view must agree on job boundaries, locations and
field values. Quoted or flow syntax cannot hide a producer from the parse.

The local Ubuntu 24.04 host was checked with Python 3 and PyYAML 6.0.1. Install
`python3-yaml` for local system Python if needed. Hosted `ubuntu-24.04` images
[include yamllint](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md),
[installed with pipx](https://github.com/actions/runner-images/blob/main/images/ubuntu/scripts/build/install-pipx-packages.sh);
[PyYAML is a yamllint dependency](https://github.com/adrienverge/yamllint/blob/master/pyproject.toml).
The image's [Python setup](https://github.com/actions/runner-images/blob/main/images/ubuntu/scripts/build/install-python.sh)
sets `PIPX_HOME=/opt/pipx`. The contract first probes `python3`, then the Python in
`${PIPX_HOME:-/opt/pipx}/venvs/yamllint`. It reports the selected interpreter and
parser version. Tests cover the hosted fallback and refusal when neither parser
is available. This availability check uses the published hosted-image build;
no hosted CI run was dispatched for this correction.

### Pending merge queue rollout (not yet live)

The checked-in queue support is preparation, not evidence that the queue or its
rules are enabled. Roy must approve the cutover separately, after this code is on
`development/v1`. It requires **ALLGREEN** (every grouped PR passes required
checks) and **MERGE** (merge commits). AGENTS.md and lifecycle documentation are
reserved for the post-cutover documentation PR.

Both v1 checks are wired for `merge_group`, which tests the combined changes of
the queued PR and entries ahead of it. Preflight has six minutes for queue events
because their classifier selects full mode; PR/push Preflight retains four
minutes. This changes the timeout, not the qualification profile.

The queue's temporary commit has no operator review status. The
`merge-queue-reviewed-head.yml` job checks out the **fresh `development/v1` tip**
and runs that trusted copy of `scripts/ci:queue-reviewed-head`. A missing checkout
or script fails the job; it never falls back to the queued tree. The first queue
run must therefore wait until the script has landed on the trusted branch.
The gate walks first parents from the group SHA to the checked-out tip, requiring
exactly two parents per entry (at most 100). Every second parent must equal an
open PR's current head targeting this repository's `development/v1`; fork PRs are
supported using statuses in the base repository. Each head's numeric newest
`hvo/reviewed-head` status, across all API pages, must be `success` from exactly
`RoySalisbury`. Unsupported topology, ambiguous PR association, a changed PR head,
an unreadable page, or a base that advanced beyond the group fails closed.
On PRs, `hvo/reviewed-head` remains the operator's commit status.

The [published webhook schema](https://github.com/octokit/webhooks/blob/main/payload-schemas/api.github.com/merge_group/checks_requested.schema.json)
describes `base_sha` only as “the SHA of the merge group's parent commit”; it does
not establish that a grouped parent excludes earlier queued entries. GitHub's
[queue documentation](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-a-merge-queue)
explicitly includes changes ahead of a PR. We therefore do **not** treat event
`base_sha` as a trust root. The supported topology assumes GitHub's MERGE mode
forms a two-parent chain ending at the actual destination tip, and verifies that
chain at runtime. Offline two- and three-entry fixtures cover this assumption;
actual GitHub queue topology still needs cutover validation. A stale group must
be rebuilt against the new destination tip.

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

Supported deployment operations use `HVO.SkyMonitor.Deployment.Cli`. Development v1 CI does not perform deployment, release packaging, complete integration qualification, coverage aggregation, or production mutation.

An annotated `vX.Y.Z-dev.<n>` tag that the owner pushes on a green `development/v1` commit publishes an integration pre-release for smoke testing; see [Integration Pre-releases](runbooks/prereleases.md).
