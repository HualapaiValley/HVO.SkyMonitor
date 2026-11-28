# Development v1

See the [unified policy and SkyMonitor profile](development/policy/skymonitor.md)
for policy scope and retained procedures.

[![Development v1 CI](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/development-v1.yml/badge.svg?branch=development%2Fv1)](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/workflows/development-v1.yml?query=branch%3Adevelopment%2Fv1)

`development/v1` is the daily integration branch for the next development line. It is independent from `main` and does not imply promotion to the stable line.

## Daily Flow

```text
development/v1
  -> feature/<issue>-<short-name>
  -> draft pull request targeting development/v1
  -> passing draft Preflight
  -> independent formal review and review-thread resolution
  -> ready pull request
  -> required Development v1 CI
  -> merge into development/v1
```

Create work from the current `development/v1` head. Ordinary issue branches and pull requests target `development/v1`, not `main`.

A pull request that changes what operators or users notice adds one
[changelog fragment](../changelog.d/README.md). The release PR assembles the
pending fragments into [`CHANGELOG.md`](../CHANGELOG.md).

## Issue And Pull Request Forms

- [Development v1 issue form](../.github/ISSUE_TEMPLATE/development-v1.yml)
- [Development v1 pull request template](../.github/PULL_REQUEST_TEMPLATE/development-v1.md)
- [Development v1 review process](runbooks/development-v1-review.md): current review limits and CI guidance alongside labeled historical mechanics
- [Pull request walkthrough](runbooks/pull-request-walkthrough.md): historical example and retired commands
- [Repository setup](runbooks/repository-setup.md): historical setup checklist and retired commands

Issues merged into `development/v1` are closed explicitly with the pull request and merge SHA. Completion comments state that the work is not promoted to `main`.

## Required CI

The repository pins .NET SDK 10.0.402 in global.json with rollForward: disable;
local builds, the devcontainer, CI, and SDK container images use that version.
The CameraAgent runtime image uses ASP.NET Core 10.0.15 (#2053).
ProcessingRunner's `runtime-deps` base moves to 10.0.14 with #1668 as an OS-layer update only;
its self-contained .NET runtime and the SDK-bundled runtime remain at 10.0.13 because SDK 10.0.402 is unchanged.
Microsoft NuGet package pins use 10.0.14. The .NET 10.0.14 runtime security fixes reach ProcessingRunner only with a coordinated SDK update.

The branch currently requires:

- **Development v1 / Preflight** on a GitHub-hosted runner: exact-range whitespace and changelog fragment validation, pinned workflow linting, shell syntax, the mandatory documentation fast-path certificate check, the inexpensive CI-control regression checks, coverage baseline policy, the full-history [secret scan](runbooks/ci-pipeline.md#secret-scan), and, when the classifier selects them, the [release script tests](runbooks/ci-pipeline.md#release-script-tests).
- **Development v1 / Build and Unit** runs its full workload on a self-hosted x64 runner with pinned SDK setup, solution restore, a warning-clean Release build, the [Docker-disabled Unit selection for active test projects](runbooks/ci-pipeline.md#active-test-selection), and [canonical per-file coverage floors](runbooks/ci-pipeline.md#development-v1-per-file-coverage). On `pull_request` runs, Build and Unit uses the base-controlled `scripts/ci:classify` rules (`complete`, `delivery`, and `deployment`) to path-filter `HVO.SkyMonitor.Deployment.Cli.Tests` out only when a PR touches no deployment, delivery, shared, linked-input (including CameraAgent files linked by `Deployment.Cli` or `Deployment.Cli.Tests`, such as `hvo-dark.css`, and `docs/runbooks/cameraagent-silo-runtime.md`), or complete-matrix files and no active canonical coverage floor depends on `Deployment.Cli.Tests`; `merge_group` and full `push` runs always execute `HVO.SkyMonitor.Deployment.Cli.Tests` unconditionally (#2071). Collection targets the assemblies owning those floors. Catalog-dependent ReleaseTool and full-suite aggregate/component coverage retain their documented local/full-qualification scope.

The [Pi 5 performance workflow](runbooks/ci-pipeline.md#raspberry-pi-5-performance) runs independently of these checks. Build and Unit excludes the Performance category.

Ready PRs can use a documentation fast path after Preflight succeeds. Hosted Preflight and full `CI / Quality` run the SDK-free `./scripts/ci:classify --check-docs-inventory HEAD` gate, which validates file modes, audit rules, source inventory bytes, certificate schema, `sourceSha256`, the committed `global.json` SDK version pin, and the committed restored project set (solution projects plus the coverage-gate project when present) — failing closed whenever the source inventory or restore-import certificate is stale, drifted, missing, or unsafe. SDK-backed full restore replay (`./scripts/ci:classify --verify-docs-restore`) validates the observed Linux x64 SDK binary digest, package sources and archive/payload digests, and each project's resolved graph and generated `.nuget.g.props`/`.nuget.g.targets` imports; full-mode `CI / Quality` runs both the offline `./scripts/test:ci-docs-restore-import` proof and `./scripts/ci:classify --verify-docs-restore HEAD` under the pinned `.NET` SDK. Build and Unit verifies the checked-out merge parents and runs the first parent's classifier against both exact endpoints, the PR head and the merge tree. Only regular, non-executable added or modified UTF-8 text without control characters under `docs/` with `.md`, `.markdown` or `.txt` extensions, plus root `README.md`, `AGENTS.md` and `CLAUDE.md`, can qualify. Catalog, contracts, validation, generated evidence, merge-queue drill artifacts, the test-executed Silo replacement runbook (`docs/runbooks/cameraagent-silo-runtime.md`), images and upgrade notes are excluded. Both endpoint inventories must pass the existing project and import audit and match the base inventory. A tree with SDK or package restore inputs also requires a successful replay against the base's reviewed restore-import certificate. The fast path skips the ordinary workload, including the solution build, Unit, coverage and terminal screenshots. A missing, stale or unverifiable certificate or any uncertain classification still selects full validation and never produces a skipped-green required result. Build and Unit records the merge, base and PR head, decision, reason and Preflight dependency. See the [restore-import runbook](runbooks/development-v1-restore-imports.md) for capture, review and diagnosis. Refs #1989, #1995 and #1996.

Draft pull requests run hosted Preflight only. Marking a reviewed pull request ready starts the self-hosted Build and Unit check. A merge-queue group (`merge_group`) runs both checks with the full workload on the group's temporary commit, together with the [queue's reviewed-head gate](#merge-queue). Pushes to `development/v1` also run both checks. After a queue-generated push, Build and Unit uses a short hosted job when Preflight verifies that the exact pushed SHA already passed the Development v1 merge-group workflow. Direct pushes and inconclusive lookups select the full workload. The full workload runs two measured `CameraAgent.Tests` shards alongside the remaining Unit projects and enforces the canonical floors across their combined coverage. See [post-merge push validation](runbooks/ci-pipeline.md#development-v1-post-merge-pushes).

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
the queued PR and entries ahead of it. Preflight has six minutes for all events; push/queue events select full classifier mode. This changes the timeout, not the qualification profile.

The queue's temporary commit has no operator review status. The
`merge-queue-reviewed-head.yml` job checks out the **fresh `development/v1` tip**
and runs that trusted copy of `scripts/ci:queue-reviewed-head`. It fetches the
GitHub queue ref as objects and verifies that it still points to the event SHA.
The gate refreshes the destination tip through the read-only ref API and walks
the group's first parents to that tip. It requires exactly two parents per entry,
with a limit of 100 entries. Each second parent must equal an open PR's current
head targeting this repository's `development/v1`. Earlier entries require a
unique matching PR association because the diff approval binds the PR number.
If another open PR targeting `development/v1` (including a fork PR in this
public repository) shares an earlier queued PR A's head, the association cannot
uniquely identify PR A when verifying a later queued PR B (`found 2`); the gate
rejects PR B's merge group (so GitHub removes PR B from the queue and rebuilds
the following merge group with the remaining queued PRs, which can fail
independently if the same ambiguity persists), not PR A itself. Use a
single-entry group so GitHub's queue ref identifies the PR directly.
An earlier entry landing during traversal can become the trusted stopping point.
Fork PRs use commit statuses in the base repository.

### Diff-bound review approval

Kepler publishes a versioned `hvo/reviewed-head` commit status after the
[required review and current-head checks](runbooks/development-v1-merge-queue.md#publish-review-approval).
Its description is `hvo-diff-v1:<reviewed-head>:<reviewed-merge-base>:<digest>`.
The digest is SHA-256 encoded as unpadded base64url. It covers the repository,
destination ref, PR number and the NUL-delimited raw Git tree diff from the
unique reviewed merge base to the reviewed head. Raw records include paths,
modes and full object IDs. A rename appears as a removal and an addition.
Whitespace, binaries, executable-bit changes and empty files all affect the
digest.

- **Base advance with the PR untouched.** The approval carries through when
  Git reproduces the queue entry's merge tree cleanly.
- **Clean merge-only sync.** Every intervening first-parent merge must have
  merged a forward commit on the protected branch's first-parent history.
  The verifier recreates each sync with Git's standard three-way merge and
  compares the entire tree. This also covers non-overlapping changes to the
  same file, where base edits may move a reviewed hunk or change its blob ID.
  The verifier recomputes the original diff at the recorded merge base.
- **Changed PR content or uncertain history.** Extra changes anywhere in a
  merge tree, conflict resolution, rewritten PR history, ambiguous merge
  bases, missing history or objects, and unreadable API data fail closed.
  The verifier uses Git 2.43 or newer and disables repository merge
  attributes when reproducing merges so an automatic conflict driver
  cannot attest a conflict as clean.
- **Approval authority.** The verifier selects the numeric newest matching
  status across all API pages and requires `success` from GitHub account
  `RoySalisbury` (ID `12501943`) on the exact queued PR head. A carried status
  points to the original reviewed commit, whose latest status must still be
  valid. PR comments provide no gate input. Duplicate status IDs, malformed or
  unknown `hvo-diff` versions, and digests for another repository, PR or diff
  fail closed.

Existing unversioned success statuses from the authorized account work on the
exact PR head, including after a clean base advance with the PR untouched. A
head-changing sync requires a verified versioned status on the reviewed
ancestor and a matching carry on the exact queued head. GitHub's branch rules
require a passing check from any source on the current PR head for admission.
The queue verifier checks that the commit status itself is authorized. Kepler
publishes carries and supersedes earlier approvals with the
[stamp command](runbooks/development-v1-merge-queue.md#publish-review-approval).

The [published webhook schema](https://github.com/octokit/webhooks/blob/main/payload-schemas/api.github.com/merge_group/checks_requested.schema.json)
describes `base_sha` only as “the SHA of the merge group's parent commit”; it does
not establish that a grouped parent excludes earlier queued entries. GitHub's
[queue documentation](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-a-merge-queue)
explicitly includes changes ahead of a PR. We therefore do **not** treat event
`base_sha` as a trust root. The supported topology assumes GitHub's MERGE mode
forms a two-parent chain ending at the actual destination tip, and verifies that
chain at runtime, on every live group. Offline real-Git two- and three-entry
fixtures cover this assumption, and a group of any other shape fails closed.
A group whose chain does not reach the refreshed destination tip must be rebuilt.
A main-to-development/v1 back-merge cannot be an earlier non-head entry unless
GitHub associates its exact head with an open PR on this destination; it fails
closed when that association is absent. Use a single-entry group or rebuild the group.

Changes to the workflow/script environment interface require two phases: first
land a trusted-base script that accepts both the old and new inputs without
weakening validation; then change the queued workflow caller. Retire the old
interface only after groups using it have drained. A caller-only change cannot
assume its matching script has reached the trusted branch. The diff verifier
uses the existing inputs and can fetch complete history for older callers
with depth-one checkouts.

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
coverage evidence in the `hvo-development-v1-ci-v6` manifest. Failed runs retain
their evidence too.

### Short checks and full qualification

The operator-approved #1142 split keeps these checks off the short path:

| Check | Required full-qualification owner in `ci.yml` |
| --- | --- |
| Whole-repository ShellCheck and its contract tests | Quality |
| Exhaustive test discovery, category disjointness and generated totals | Build |
| Integration, full-suite aggregate/component coverage, architecture/publish and migrations | Existing full-matrix jobs; measurable canonical per-file floors run in Build and Unit |
| Deployment qualification | Existing selected Deployment Contracts job |

The catalog reproducibility regressions are full-qualification only as well
(Refs #1774): Catalog Contracts rebuilds HYG 4.4 and Catalog Reproducibility
rebuilds HYG 4.4, OpenNGC and the catalog test fixtures, each under both SQLite
`secure_delete` defaults. Neither required v1 check runs them or reads the
classifier's `catalog_recipe` output; see
[Catalog Reproducibility](runbooks/ci-pipeline.md#catalog-reproducibility).

The short run still builds the entire solution warning-clean and executes the
positive Unit selection for active assemblies with an invalid Docker endpoint. It does not
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

The [SkyMonitor profile](development/policy/skymonitor.md#repository-and-integration)
records the observed integration settings and their observation date; live settings
remain authoritative. Agents do not change repository settings. Changes to
protections or rulesets require Roy's decision and the applicable policy checks.
The install contract's settings requirements remain in [section 2](hvo-install-contract.md).

## Review

Mechanical, Standard, and Deep set coverage and round limits under the
[review process](runbooks/development-v1-review.md#review-levels-and-limits).
Kepler alone publishes review and verification records before Ready. Kepler
posts reviewer output verbatim, links the reviewer session, and faithfully
relays verified finding dispositions under the
[common policy](development/policy/common.md#authority-and-precedence).

A pull request is marked ready only after the current head is reviewed, every finding has a verified terminal disposition, and every review thread is resolved.

After Ready, build or test breaks, High or Medium correctness findings, and
missed acceptance criteria require correction. Kepler tracks post-Ready Low
findings in [rolling issues labeled `follow-up` and `low-batch`](planning/agent-execution.md#rolling-low-batches).
Sync a v1 PR with
`git merge --no-ff origin/development/v1` only to resolve an actual conflict.
The merge queue validates the combined tree.

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
