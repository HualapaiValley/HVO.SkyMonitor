# HVO.SkyMonitor Agent Guide

This is the canonical repository-wide instruction source for every coding-agent
harness. Agent-specific instruction files must load or point to this file and
must not redefine its policy inconsistently.

## Coding Threads and Kepler

Coding threads own implementation, original-requirement evidence, local tests,
independent local review, and corrections. They stop at the candidate/evidence/
review handoff to Kepler, and push only when instructed. They **never create a
PR, including a draft, mark a PR ready, publish `hvo/reviewed-head`, or enqueue**.
This boundary is unconditional; being the issue owner or having write access
does not authorize those actions. Kepler alone creates PRs, controls draft/
readiness and formal review, publishes the reviewed-head gate, and enqueues.
Kepler also operates any authorized direct/auto-merge fallback and PR ledger.
Older owner/finalization instructions in other guides do not transfer these
actions to a coding thread.

## Review Tiers and Findings

| Tier | Scope | Required independent reviews on the same candidate SHA |
| --- | --- | --- |
| LOW | Docs/tests-only, non-gating CI configuration, dependency bump, or small isolated non-safety fix, with no HIGH concern | One Sol **CLEAN** |
| HIGH | Camera, roof, safety, interlock, durable-state, lifecycle, concurrency, recovery, security, merge-gating, data-migration, or cross-component work; when unsure, HIGH | Sol **and** Opus **CLEAN**, both on that candidate |

HIGH concerns take precedence over LOW categories. These requirements apply at
each required local/formal review stage; local review does not waive formal
review. The existing A/B/C/M validation ladder and review-depth profiles still
select checks and depth, but cannot replace the named reviewers or their count.
Humans, bots, other models, a focused interaction review, an availability
waiver, elapsed time, and a correction cap cannot substitute for required Sol/
Opus CLEAN reports on the candidate. Missing required review means BLOCKED.

Only **High and Medium review findings block**; classify a Critical finding as
High. Lows become linked follow-up issues through Kepler and do not force
another correction round. Incompleteness or missing required evidence always
blocks, is at least Medium, and is **never Low** or deferrable. Required tests
and CI checks remain gates independently of finding severity. CLEAN requires
complete scope and no unresolved High/Medium findings; any remaining Lows must
have follow-up tracking.

## Toolchain and Validation

- Select the protected CI profile by the PR's **target branch** before applying
  the risk-tier ladder. For `development/v1`, the merge checks are
  `Development v1 / Preflight` and `Development v1 / Build and Unit`: lightweight
  control checks, one Release build, and the complete Docker-disabled Unit
  selection. Full qualification in `ci.yml` belongs to `main`/`release/**`
  and explicitly requested qualification. Do not dispatch it for an ordinary
  v1 PR merely because `scripts/ci:classify` reports `complete=true`, including
  on a base-sync delta. See `docs/development-v1.md` for this branch-specific
  contract, which takes precedence over generic protected-CI wording below.
- Risk-tier local validation and explicit issue acceptance still apply.
  Record the target profile, classifier output, local evidence and any separately
  authorized qualification. A v1 pass is integration evidence, not production
  qualification. Whole-repository ShellCheck and exhaustive category discovery
  run in full CI; changes to shell/category behavior still need focused local
  validation of that behavior.
- Use the SDK pinned in `global.json` (`10.0.402`, stable releases only) and the solution `HVO.SkyMonitor.v9.slnx`.
- Package versions are centralized in `Directory.Packages.props`; do not put `Version` attributes on individual `PackageReference` items.
- The complete Tier C/M local candidate gate is:
  ```bash
  dotnet tool restore
  dotnet restore
  dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Debug -warnaserror
  dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release -warnaserror
  dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes
  ./scripts/package:audit
  ./scripts/ci:shell-syntax
  ./scripts/test:ci-classification
  ./scripts/test:coordination-guard
  ./scripts/test:pr-review-tools
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory=Unit" --settings tests/coverage.runsettings --collect:"XPlat Code Coverage"
  dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory=Integration" --settings tests/coverage.runsettings --collect:"XPlat Code Coverage"
  ```
- `scripts/test:ci-classification`, `scripts/test:coordination-guard` and
  `scripts/test:pr-review-tools` are the CI-control guards, and
  `scripts/ci:shell-syntax` is grouped with them because it has the same
  property. All four are Docker-free, need no build, and complete in well under
  a minute, and until now all four spoke only in protected CI. PR #740 reached
  protected CI through a deep initial review, two correction rounds, a base sync
  and a base-sync review before `scripts/test:ci-classification` reported that a
  combined test assembly had taken a dependency the classifier routes away from
  its lane. None of those rounds could have found it, because none of them ran
  it. A check that cannot speak until after the finalization lock is taken is a
  check bought at the highest available price.
- Run them under Bash 5.1 or newer. `scripts/ci:require` uses `declare -A`,
  which the macOS system Bash 3.2 does not have, and `scripts/ci:shell-syntax`
  refuses to run under 3.2 rather than reporting a clean pass it did not
  perform. On macOS invoke the Homebrew Bash explicitly.
- `scripts/test:pr-review-tools` exercises `scripts/pr:dispatch-review`, which
  requires `flock` to serialize review dispatch. macOS does not ship `flock`, so
  a stock Mac cannot complete this now-mandatory gate: it exits 1 saying `flock
  is required for serialized review dispatch`, which reads as a guard failure
  rather than as a missing tool. Install it with `brew install flock`, alongside
  `brew install bash` for the floor above.
- Run the guards again on the base-synced head, in the local candidate gate,
  before requesting the base-sync review. This settles the question PR #740's
  base-sync reviewer recorded rather than answered: a base-sync review verifies
  that the merge preserved both sides' behaviour, and a classifier lane that
  both sides always satisfied separately can still be violated by their union,
  so the review is the wrong instrument and the gate is the right one.
- Reproduce the exact category/project evidence, architecture/publish, migration, and canonical coverage gates with `docs/runbooks/ci-pipeline.md` and `.github/workflows/ci.yml`.
- Unit selection is positive and passes with an invalid Docker endpoint. Integration selection is a separate required gate and requires Docker for the Testcontainers assemblies.
- Run a focused MSTest with `dotnet test <project> --filter "FullyQualifiedName~Namespace.Class.Method"`.
- `tests/coverage.runsettings` excludes test assemblies, `TestSupport`, migrations, and build output; keep coverage configuration aligned when adding projects.
- Use the risk-tiered validation ladder in `docs/planning/agent-execution.md`:
  focused tests in the inner loop, tier-appropriate local candidate evidence
  before the first push, affected gates for corrections, and classifier-selected
  target-branch protected CI on the final reviewed head. Tier C/M work runs the complete local
  candidate gate; Tier A/B work relies on focused/affected local evidence plus
  protected CI. Do not repeatedly run unchanged long suites or performance
  harnesses.
- Select the local gate set with `scripts/ci:classify`, not by reading the diff,
  whenever it classifies the range successfully. Run it on the review range and
  record its output alongside the gate results; a ledger that lists gates
  without naming the selector that chose them is incomplete evidence, because a
  gate that was never selected then shows up as an absence rather than as a
  silence. `complete` is not a condition on using the classifier. It governs one
  thing, whether the full-qualification profile selects the complete solution
  matrix, and `complete=false`
  is the ordinary result for a change confined to one component's paths, where
  the lane flags the classifier sets are exactly the selection being asked for.
  Requiring `complete=true` before trusting it would send agents back to reading
  the diff in the case the classifier serves best. The
  classifier supplies the affected local/full-qualification plan; it does not
  override the v1 profile or authorize a full workflow dispatch. In full CI the
  `changes` job is gated on `draft == false`, so through the whole convergence
  phase every gate decision would otherwise be made from the diff by parties
  whose view is the diff. On PR #756 the classifier returned `mode=full
  complete=true` on the first head and would have named Unit Tests, which owns
  the test-category audit that had been failing since round 4.

## Architecture Boundaries

- Read `docs/roadmap.md` for portfolio initiatives, stable `RM-###` IDs, and future direction. `docs/project-plan.md` remains authoritative for architecture, virtual-first requirements, phase order, and completion; it forbids references between `CameraAgent` and `LogicHost`.
- `AgentCore` contains stable transport-neutral camera, rig, frame, and artifact contracts only; do not add ASP.NET, EF Core, object-storage SDK, SkiaSharp, or camera-SDK dependencies.
- `HVO.SkyMonitor.Processing` (introduced under issue #93) owns host-neutral recipe definitions and execution contracts, and host-neutral, source-neutral fact models; it may reference AgentCore, Astronomy, and Imaging but no host or persistence infrastructure.
- `HVO.SkyMonitor.Common` contains reusable ASP.NET security, identity, API, middleware, and observability infrastructure only; do not move capture, recipe, image, edge-workflow, or central-workflow ownership into it.
- `HVO.SkyMonitor.Storage.FileSystem` (introduced under issue #592) owns only host-neutral root confinement, symlink/reparse protection, atomic publication, durable file/directory synchronization, and bounded cleanup/error primitives. It must not own CameraAgent or LogicHost storage contracts, capture/object identity, manifests, persistence policy, ASP.NET, EF Core, SQLite, or provider SDKs. Register it explicitly in the solution graph, architecture rules, coverage policy, and CI component classifier.
- `CameraAgent.Common` owns edge capture orchestration: module registration/factory, SQLite/file durable ingress, durable lanes, local storage, outbox, retention, telemetry, and configuration loading. The `CameraAgent` host registers this through `AddCameraAgentInfrastructure` and hosts the local UI/API/identity.
- `HVO.SkyMonitor.CameraAgent.Replay` owns the authenticated bounded local transport and immutable projection for explicitly requested archived replay; `HVO.SkyMonitor.CameraAgent.ReplayRunner` is its self-contained local executable. CameraAgent retains durable job, lease, and publication authority, and newly acquired/live work never dispatches to the replay runner.
- `LogicHost` is the central ASP.NET host; it owns SQL Server/Redis/provider-neutral object-storage services and provider adapters and runs EF migrations plus seed data at startup. Its `IObjectStore` contract never becomes the CameraAgent storage contract.
- New reusable astronomy/projection behavior belongs in `HVO.SkyMonitor.Astronomy`, and reusable image algorithms in `HVO.SkyMonitor.Imaging`; do not create host-specific projection math. Astronomy catalogs are versioned read-only SQLite snapshots deployed locally to LogicHost and every CameraAgent, never part of the shared SQL Server schema.
- Concrete read-only catalog persistence belongs in the optional `HVO.SkyMonitor.Catalog.Sqlite` infrastructure adapter shared by both hosts. It may reference Astronomy contracts; Astronomy must remain storage-neutral and must not reference the adapter.

## Runtime and Infrastructure

- SQL Server, Redis, and Mailpit are persistent shared services on `hvo-docker`; configure their endpoints and credentials in the ignored `.env` using `.env.template`. LogicHost object storage is a LogicHost-local filesystem root at `HVO_OBJECT_STORE_ROOT`, not a shared service.
- Use `./scripts/infra:start [logichost|cameraagent]` rather than raw Compose for application containers. `--reset` only deletes application container state and rebuilding refreshes the image.
- Run hosts directly through `./scripts/with-env dotnet run --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj` and `./scripts/with-env dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj`. Compose exposes them at ports `5174` and `5130` respectively.
- LogicHost Data Protection keys and CameraAgent local Identity, Data Protection, and provisioning files are runtime state under `data/`, `App_Data/`, `DataProtection-Keys/`, and the configured `DeviceProvisioning:StateDirectory`; do not add them to commits.
- Keep local secrets in user secrets, `.env`, or `.devcontainer/devcontainer.local.env`. These are intentionally ignored; `.env.template` contains the supported local defaults.

## Tests and UI

- LogicHost integration tests start SQL Server, Redis, and Mailpit through Testcontainers and use the real filesystem object-store provider; CameraAgent integration tests reuse that central-host fixture and wire in-process HTTP handlers.
- CameraAgent module/pipeline configuration comes from `cameraagent.sample.json` and the `CameraAgent` configuration section. Preserve configuration-driven module discovery and explicit pipeline graphs that use stable step aliases rather than adding host-specific branches.
- For Blazor components with logic, keep markup, code-behind, scoped CSS, and optional scoped JS in sibling `.razor`, `.razor.cs`, `.razor.css`, and `.razor.js` files. Root/layout components must render `data-theme="hvo-dark"` on `<html>`.

## Changelog

- A pull request that changes what an operator or user notices adds a line under `## [Unreleased]` in `CHANGELOG.md`.
  This covers the installer, UI, API, configuration, deployment, images, and the logs and metrics operators act on.
- Put the line under the Keep a Changelog heading that fits: `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed` or
  `Security`. Write it for an operator, and end it with the issue number.
- Internal refactoring, tests, CI and agent-process changes add no entry.
- Cutting a release renames `[Unreleased]` and adds `docs/upgrade-notes/X.Y.Z.md`. That is the release procedure of the
  install contract (section 3), not part of an ordinary pull request.

## Roadmap Execution

### Developer Ownership

- The assigned coding owner owns implementation, validation, independent local
  review, corrections, and the resumable candidate handoff. Kepler owns the PR
  lifecycle and returns findings to that owner when implementation is needed.
  No coordinator enrollment is required for ordinary coding work; it does not
  change the unconditional Kepler boundary.
- Before editing, inspect the issue assignees, `workflow:in-progress` label,
  claim comments, and linked open PRs. An existing claim means do not take it,
  including while its owner waits for review or CI.
- Claim on the issue using a GitHub assignee where available, the
  `workflow:in-progress` label, and an append-only comment naming the actual
  person or unique agent/session, branch/worktree, scope, and next checkpoint.
  Agents sharing a GitHub account must have distinct claim identities.
- Re-read the issue after claiming and before editing. Labels and assignments
  are visible coordination, not atomic locks: competing claims require explicit
  resolution before either implementation proceeds. Never steal an apparently
  stale claim; request release or an authorized transfer with a handoff.
- Keep the claim through review, CI, and merge. On completion or explicit
  release, record the outcome/handoff and remove `workflow:in-progress`.
  Use `workflow:blocked` for a retained claim awaiting a dependency or decision;
  remove it when unblocked. Do not use `workflow:finalizing` as an issue claim.
- Independent owners may work concurrently in isolated worktrees. There is no
  repository-wide two-issue limit; reserve only contested resources and shared
  contract work. Technical dependencies and approved product scope still apply.
- Complete the assigned issue without routine continuation prompts. Taking
  another issue requires explicit assignment or opt-in queue authorization.
  An open issue labelled `agent:ready` is opt-in queue authorization: a queue
  worker may claim it under the claim rules above. When several workers claim
  at once, the earliest claim comment keeps it and the others withdraw in a
  comment.

### Agent Workspaces

- Run agents from the native host, an SSH session, or remote-agent tooling.
  Keep concurrent work isolated in Git worktrees at durable, host-owned paths
  appropriate to the selected tool. Do not rely on a devcontainer's writable
  layer for authoritative work or resumable state.

- Use each issue as the authoritative claim and handoff record; use its owning
  epic as a progress index, not an authorization service. Epic #89 and the
  `Virtual-First Platform Completion` milestone retain
  the completed virtual-first delivery history.
- Use `<harness>:<provider>:<host>:<session-short-id>` for a unique agent
  identity without credentials or full session tokens.
- Existing tooling retains the status literal `UNREGISTERED/WAIT`.
- The review dispatch scripts (`scripts/pr:dispatch-review`,
  `scripts/pr:review-request`) and the coordination guard
  (`scripts/coordination:guard`) are still present, with their tests. Revisit
  them later and remove them if no longer needed.
- Work is dispatched over SSH to T3 Code on the dev hosts `home-dev-01`,
  `home-dev-02`, `home-dev-03`, `hvo-dev-02`, `hvo-dev-03`, and `hvo-dev-04`.
  Each task runs in its own git worktree off `development/v1`, so the main
  checkout is never modified.
- A session coming online on a new machine, or a machine being pre-staged before
  any agent runs on it, follows `docs/runbooks/agent-host-onboarding.md`. It is
  the operational checklist under this policy and the execution protocol, and it
  covers toolchain, environment, permissions, and task setup.
- Before implementing a roadmap issue, follow `docs/planning/agent-execution.md` and the relevant section of `docs/planning/agent-prompts.md`.
- Before opening, reviewing, updating, finalizing, or merging any PR, read and
  follow `.agents/skills/pr-lifecycle/SKILL.md`. This instruction is mandatory
  even when the current harness does not discover repository skills natively.
- Use `docs/planning/requirements-crosswalk.md` for the owning detailed specification and `docs/planning/performance-validation.md` for canonical workloads and evidence.
- Keep one implementation issue per branch/PR unless dependencies explicitly coordinate stacked PRs.
- Before implementation, post the protocol's plain-language synopsis explaining
  why the issue is next, its practical outcome and benefit, what it unlocks, and
  the main exclusion.
- The issue owner may delegate non-overlapping research, review, and evidence.
  Delegation does not transfer accountability. Never share an editing worktree.
  Bound heavy tests by host/daemon capacity, not the number of contributors.
- Roadmap priority guides selection, not ownership. This ownership change does
  not authorize deferred product work. Use formal dependencies for named
  technical deliverables, not portfolio ordering. Record maintainer-approved
  removal of scheduling-only holds in the issue and roadmap before claiming.
  Distinguish start, integration, and release gates; preparation can precede
  qualification, but supported adoption cannot.
- Reaching a PR, including a draft, means the declared issue scope is completed,
  tested locally, and independently vetted locally at the required complexity.
  Apply the Issue Completeness contract below before PR creation; draft-first
  convergence is the subsequent Kepler/PR lifecycle, not permission to offload
  unfinished implementation or acceptance discovery onto formal review or CI.
- Kepler creates a draft PR only after completeness evidence and the required
  tier-specific local CLEAN reviews pass. Protected CI must not advance until
  formal review converges on the current PR head and real merge/product
  conflicts are resolved, tested, and independently reviewed. Target-tip
  movement alone requires no synchronization, re-testing, or new review.
- Initial review covers the full merge-base-to-candidate diff and original
  requirements. Correction review covers the prior-reviewed-head delta and
  concrete interactions, verifying every prior finding individually. Every
  required Sol/Opus report must identify the same candidate SHA and include a
  completeness verdict. Provider-side bots and human reviews are supplemental.
  The legacy three-correction budget triggers escalation to Kepler, not an
  approval, waiver, or deferral of High/Medium findings or incompleteness.
- A/B/C/M validation tiers and standard/deep profiles retain their validation
  and depth roles. Use deep for HIGH concerns and material blocking findings;
  these profiles never reduce LOW's one Sol or HIGH's Sol-and-Opus requirement.
- For the operated `development/v1` merge queue, Kepler revalidates the
  **CURRENT PR HEAD SHA**, complete requirement evidence, required local/formal
  CLEAN reviews, passing required CI, and resolved conversations. Kepler alone
  publishes `hvo/reviewed-head` on that exact head and then enqueues with
  GraphQL `enqueuePullRequest`, checking the expected head. Under the queue,
  `gh pr merge --merge` warns and does **not** enqueue. Coding threads stop at
  handoff; they do not perform any of these PR/status/queue operations.
- Direct merge and auto-merge are separate fallback routes, used only by Kepler
  when the queue is unavailable or an operator explicitly selects the fallback.
  They retain the same completeness, exact-candidate reviews, checks, and
  conversation requirements. They are not substitutes for queue admission.
  Kepler controls fallback finalization locks and disables auto-merge/returns
  the PR to draft before authorizing a correction push to a finalized PR.
- Queue eligibility belongs to the **CURRENT PR HEAD SHA**, not the target tip
  or temporary group commit. Tip-behind or age alone does not invalidate head
  reviews, require automatic base-only synchronization or latest-development/v1
  re-testing, or block submission/resubmission. Roy deferred stricter latest-base
  pre-submit policy: monitor queue and CI first; adopt stricter policy only if
  evidence shows a need and Roy authorizes it. Queue CI validates integration
  separately; this documentation changes no executable gate.
- Actual CONFLICTING merges and real product conflicts still need resolution.
  Kepler keeps/returns the PR to draft and returns the work to its coding owner.
  Preserve existing conflict resolutions and reviewed history. Any changed
  candidate, including an integration merge, needs applicable local evidence
  and the required tier's reviewers CLEAN on that SHA before Kepler advances it.
  An unchanged-diff/base proof can inform review but cannot copy old reviews or
  a reviewed-head status to a new candidate. Target-tip movement with an
  unchanged PR head does not trigger this loop.
- Performance-sensitive work requires reproducible baseline/after evidence for relevant I/O, CPU, allocations/working set, throughput, latency, and backlog. Unexplained regression blocks merge.
- Validate produced outputs through checksums, numerical invariants, provenance, lineage, and durable state where applicable. Inspect logs, metrics, traces, and health behavior for host/worker changes.
- If work stops or blocks, leave the resumable handoff required by the execution protocol and update the owning roadmap epic with the exact next action.
- After merge, report completion and release the claim. Taking another issue
  requires explicit assignment or opt-in queue authorization, not a routine
  prompt between steps of the already assigned issue.

### Issue Completeness

- Before claiming done, the owner reads the original GitHub issue body,
  **all discussion and acceptance criteria**, linked governing requirements,
  and approved scope clarifications. Roy's latest instructions win conflicts;
  record the source and resulting scope explicitly. A PR description, code diff,
  generated test list, or previous review summary cannot replace those sources.
- Before requesting independent local review, map every in-scope requirement to
  implemented behavior and local behavioral evidence at the immutable candidate
  SHA. Include normal, negative, failure, cancellation, recovery, durable-state,
  and interaction cases as relevant; give N/A reasons for inapplicable cases.
  Tests and acceptance criteria derived only from the implemented behavior do
  not prove the original issue is solved.
  For docs-only or mechanical work, evidence checks the required policy or
  invariant directly, without inventing runtime results.
- Missing behavior or required evidence returns the owner to **IMPLEMENTATION**.
  Do not claim completion, silently downgrade it to Low, or defer it through a
  review-round cap. Keep the assigned HIGH/LOW risk tier and existing validation
  and review-depth rules; every tier must satisfy issue completeness.
- A partial slice must name its delivered scope, exclusions, remaining parent
  requirements, and tracking. The parent issue stays **OPEN** while any of its
  scope remains incomplete; do not use an automatic closing reference for it.
- The independent reviewer reads the original sources as well as the code,
  checks the owner's requirement/evidence mapping independently, and records
  an explicit completeness verdict, supporting evidence, and every uncovered
  requirement. Independently check normal, negative, failure, cancellation,
  recovery, durable-state, and interaction evidence, with N/A reasons where
  inapplicable. A focused interaction review alone cannot establish full issue
  resolution. Use the compact owner and reviewer evidence templates in
  `.agents/skills/pr-lifecycle/SKILL.md`.
- Blocking incompleteness means **CHANGES REQUESTED**, owner implementation,
  and no PR creation or CI/queue advancement. Keep an existing PR draft or
  have Kepler return it to draft. Green CI, coverage, and a code-only CLEAN
  verdict cannot substitute for issue-resolution evidence.
- Classify findings as an existing bug, test gap, fix-introduced regression, or
  mechanical-only change, citing expected versus observed behavior. Missing
  hardware or state means evidence is unavailable: report the limitation and
  exact remaining validation to Kepler/Roy, never assert a pass. Required
  acceptance remains blocked until evidenced or explicitly re-scoped by Roy.
- The rejection loop is **reject -> IMPLEMENTATION -> local tests/evidence ->
  independent local review -> Kepler PR lifecycle**. Prove resolution before
  the local review; obtain the required tier-specific CLEAN reports before
  handoff. Only Kepler may create a PR/draft. Unavailability waives no required
  local or formal review.
- These are agent/Kepler process requirements. GitHub may disallow a PR author
  from submitting native `request_changes` on their own PR, just as it forbids
  self-approval. Record the honest process verdict and enforce the state/handoff;
  never fabricate a native review or claim branch protection enforces
  completeness. PR/readiness/reviewed-head/queue writes belong only to Kepler.

### Progress Reporting

Progress reporting is mandatory while delegated work, review acquisition, long
gates, or CI is active. Section 4 of `docs/planning/agent-execution.md` is the
single canonical source for progress reporting. Coding owners report milestones,
blockers, long-running work, and completion directly to the user and retain
local evidence; Kepler maintains the PR ledger. Long gates and reviews get an
interim note, and blocked work leaves a resumable handoff.
