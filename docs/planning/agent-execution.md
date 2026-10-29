# Agent Execution Protocol

This protocol applies to every approved issue in `docs/roadmap.md` and its owning
roadmap epic or milestone.
It is designed for continuous roadmap execution: work may stop, hand off, and
resume without losing decisions, validation state, or the exact next action,
and the assigned issue continues without routine operator prompts. Starting or
selecting another issue requires explicit assignment or opt-in queue authorization.

## 1. Authority Order

Read these sources in order before implementation:

1. `AGENTS.md`
2. `docs/roadmap.md`
3. `docs/project-plan.md`
4. The active GitHub issue, owning roadmap epic, and dependencies
5. The owning subsystem specification or runbook
6. `docs/planning/requirements-crosswalk.md`
7. `docs/planning/performance-validation.md`
8. `.agents/skills/pr-lifecycle/SKILL.md` when the work creates, reviews,
   updates, finalizes, or merges a PR
9. `docs/planning/agent-prompts.md`
10. Current code and tests

If sources conflict, stop implementation long enough to resolve the conflict in
the issue or authoritative plan. Do not silently choose a convenient behavior.

For PRs targeting `development/v1`, the protected-CI profile in
[Development v1](../development-v1.md) takes precedence over generic
full-matrix wording in this protocol. Run its Preflight and Build and Unit
checks after review convergence. The main/release classifier remains useful
for local scope and full qualification; `complete=true`, including from a
base-sync delta, does not authorize dispatching `ci.yml` for an ordinary v1 PR.
Additional qualification must be explicitly required by issue acceptance or
the operator. Keep risk-tier local validation and independent review.

## 2. Starting an Issue

### Technical Readiness

Use `blocked_by` for a named contract, implementation, qualification result, or
decision needed by the consumer. Priority and milestone order are not technical
dependencies. Only explicit maintainer/operator authorization may remove an
existing scheduling hold; record its reason on both owning issues/epics.

Separate start, integration, and release readiness. Split broad stories into
independently verifiable outcomes with scope, exclusions, risk tier, accepting
and rejecting cases, and a named validation gate. Discovery can settle a missing
contract without enabling a partial production feature. Keep atomic schema,
transaction, security, and publication changes together. Do not split solely to
increase the number of developers. Contract owners coordinate shared-file and
migration changes without taking over repository-wide task dispatch.

### SkyMonitor Recovery Ordering Gate

Owner clarification in [#1606](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1606#issuecomment-6370275612):
when selecting affected work, check the applicable bounded keep/change outcome
from #1607 (CameraAgent) or #1608 (LogicHost). It must be recorded before work
commits to an overlapping persistent-state layout, recovery protocol or
substantial forward upgrade path. Require each affected story to cite that
decision and its supported forward inputs. An evidence-backed keep-current
result clears the gate; neither a new backend/framework nor an implementation
PR is required. Studies may be scheduled now or later, retain their 60–90 minute
initial stop rule and credit delivered evidence.

The owner-requested [#1615 recurring-cost reassessment](../validation/upgrade-recovery-cost-1615.md)
adds the current disposition: simplify the pending-upgrade protocol on existing
primitives first, retain a controlled snapshot alternative if integrated
complexity/pause evidence reverses that choice. Overlapping stories cite this
comparison alongside #1606 and their #1607/#1608 inputs. Its recorded outcome
clears the reassessment ordering gate; it does not waive implementation
acceptance or adopt the proposed test-policy reductions.

The affected overlaps include CameraAgent #1059/#1060/#1300 and LogicHost
#1244/#1245/#589/#591. Scope dependencies to those overlaps. Unrelated features,
clean-install preparation, release verification and strict existing-behavior
#893 CLI parity stay independent; this is not a portfolio-wide scheduling hold.

Larger DB/file/schema conversions may be explicitly **forward-only**. Do not
add reverse migrations, previous-binary readability or rollback implementation
solely because a conversion is substantial. Declare supported source/target
inputs, validation, interruption/resume and operator recovery/data-loss
expectations before conversion. Preserve ordinary protected upgrade checkpoint
recovery: restoring a paired pre-upgrade point does not reverse a migration.
This exception neither removes protection universally nor permits silent
destructive conversion. The [install contract](../hvo-install-contract.md#skymonitor-qualification-and-adoption)
and [implementation map](skymonitor-upgrade-recovery.md#ordering-and-forward-only-conversions)
own the detailed boundary; existing required gates remain in force.

### Issue Start Checklist

Before changing files:

1. Confirm the issue is open and mapped to the expected roadmap initiative,
   owning epic, and milestone when one is assigned.
2. Confirm every required dependency is merged or explicitly coordinated.
3. Inspect `git status`, recent commits, current branch, and related PRs.
4. Preserve unrelated worktree changes.
5. Create one issue-focused branch from the freshly fetched target
   (`development/v1` for ordinary feature work).
6. Map existing implementation, tests, migrations, configuration, and docs.
7. Post the plain-language synopsis defined below before implementation.
8. Record unresolved product or architecture decisions in the issue.
9. Capture a behavior and performance baseline where the issue is
   performance-sensitive.
10. Define acceptance tests before selecting an implementation.

The synopsis is a short issue comment or issue-body section that a reader can
understand without knowing the codebase. Keep it under 120 words and include:

```markdown
## Plain-Language Synopsis
- Why now: dependency or problem that makes this the next useful step.
- Outcome: what will work when the issue is complete.
- Benefit: who or what becomes safer, faster, or easier to operate.
- Unlocks: the next roadmap capability enabled by this issue.
- Not included: the most likely scope misconception.
```

Do not begin a downstream schema, worker, UI, or detector issue against an
unmerged speculative contract unless the parent issues explicitly coordinate a
stacked PR series.

## 3. Implementation Rules

- Implement the smallest coherent issue slice that satisfies its acceptance
  gate.
- Keep CameraAgent and LogicHost independent.
- Keep AgentCore transport-neutral.
- Put pure astronomy, imaging, and recipe behavior in their shared owners.
- Keep Common limited to reusable web/security/identity/API/observability
  infrastructure; it does not own capture, processing, or persistence workflows.
- Treat SQL, SQLite, Redis, MinIO, filesystem journals, HTTP, and hosted services
  as host infrastructure.
- Before first release, update all current producers, consumers, fixtures,
  documentation, and tests together when changing a durable contract.
- Replace each EF context's canonical initial migration when its model changes;
  do not add upgrade, downgrade, backfill, or convergence behavior for an
  unreleased EF schema.
- Retain compatibility only for a shipped state, external consumer or protocol,
  concurrently supported current producer and consumer, immutable evidence or
  provenance need, or current lifecycle safety. Repository history alone is
  insufficient.
- Retain version identities needed for current validation, hashing, provenance,
  reconstruction, or reproducibility; they do not imply support for earlier
  unreleased versions.
- Assign identity before optional work.
- Preserve immutable raw evidence and complete source lineage.
- Keep queues bounded and durable work discoverable after missed wake-ups.
- Make skip, retry, quarantine, and terminal behavior explicit.
- Add comments only where the code would otherwise hide a non-obvious invariant.

## 4. Throughput and Validation Economy

Quality gates are unchanged, but expensive work must not be repeated without a
reason.

### Independent Issue Ownership

The assigned person or agent owns implementation, validation, independent review,
corrections, green CI, authorized merge, and cleanup. Delegation does not transfer
ownership.

Before editing, inspect assignees, `workflow:in-progress`, claim comments, and
linked PRs. Claim an approved unowned issue using an assignee where possible,
`workflow:in-progress`, and an append-only comment:

```text
CLAIM
Owner: <person or unique agent/session; distinguish shared GitHub accounts>
Branch/worktree: <branch and isolated path>
Scope: <assigned outcome and exclusions>
Dependencies/resources: <required inputs and contested resources>
Next checkpoint: <next verifiable milestone>
```

Re-read after claiming and before editing. Assignments and labels are visible
coordination, not atomic locks; resolve competing claims explicitly. Never take
over an apparently stale claim without release or authorized transfer/handoff.
Keep `workflow:in-progress` through review and CI. Add `workflow:blocked` with a
reason/next action when waiting on a dependency or decision; remove it on resume.
On completion or release, post the outcome/handoff and remove status labels.
Clear or replace the assignee when releasing/transferring an open issue.
`workflow:finalizing` is a PR merge reservation, not an issue claim.

Independent issues use separate worktrees. There is no global two-issue limit;
reserve contested contracts and heavy tests per host, daemon, or hardware.
Complete the assigned lifecycle without routine prompts. Another issue requires
explicit assignment or opt-in queue authorization.

### Task Dispatch

Work is dispatched over SSH to T3 Code on the dev hosts `home-dev-01`,
`home-dev-02`, `hvo-dev-02`, and `hvo-dev-03`. Each task runs in its own
git worktree off `development/v1`, so the main checkout is never modified.

- A required compatibility check may block a merge, but an unrelated failure
  from another component does not make that component's backlog a dependency.
  First determine whether the active change caused the failure. Correct a real
  cross-boundary regression in the active issue; otherwise correct the CI
  classification or leave the independently owned defect in its roadmap order.
- Keep one implementing agent per issue branch. Use research, review, failure
  analysis, and evidence agents concurrently when their work does not overlap.
- Concurrent issues require stable merged dependencies and separate branches
  and isolated git worktrees. Agents must never edit the same worktree.
- Do not start a downstream issue from an unmerged contract or migration unless
  the issues explicitly define a stacked PR sequence.
- Assign each expensive test, benchmark, or evidence run one owner. Other agents
  consume its recorded result instead of launching the same run.
- Avoid running multiple Testcontainers or full-resolution performance suites
  concurrently against shared Docker resources unless their isolation and
  capacity have been verified.

### Progress reporting

Owners report milestones, blockers, long-running gates, and completion
to their user and issue/PR ledger. Human reviewers use their agreed availability
window.

Silence reads as a stall. Every agent reports progress while it works, not only
when it finishes:

- Implementing agents post a short progress comment at every milestone
  (inventory complete, first slice committed, a gate started or finished, reviews
  launched, corrections pushed, PR marked ready) and at least every thirty
  minutes of active work. Post on the owning issue until the draft PR exists,
  then append to the PR's append-only review ledger. Each comment has a UTC
  timestamp and states what finished, what is running now, the next step, and
  any blocker. Long gates and reviews get an interim note rather than silence.
- Every delegated implementation, review, research, and evidence agent sends
  the issue/PR owner a structured `STARTED` message before substantive work,
  then milestone, blocker, at-least-thirty-minute, and completion messages.
  Review messages identify the exact range and actual provider/model/effort.
  Each agent also posts the same milestones on the owning issue or PR ledger;
  if its harness cannot, the owner posts a clearly attributed proxy entry and
  relays the event to the user.
- A progress comment never replaces the handoff in section 11; blocked work
  still leaves the full handoff, and every agent provides its final report.

### Validation ladder

Record commands and the source commit or worktree fingerprint they validate.
Invalidate evidence only when a later change can affect that boundary.

Select one validation tier before implementation and record it in the issue:

| Tier | Change risk | Required local evidence before push |
| --- | --- | --- |
| `A` | Documentation, labels, styling, or isolated non-behavioral cleanup | Focused validation and affected build/format; no complete local matrix or benchmark |
| `B` | Ordinary contract, algorithm, UI, configuration, or isolated defect | Focused tests and affected project/boundary tests; no complete local matrix or canonical benchmark unless promoted by wider risk |
| `C` | Durable boundary, concurrency, recovery, migration, full-frame algorithm, or measured hot path | Complete local candidate gate, affected integration/fault gates, and the smallest representative measurement |
| `M` | Named operational milestone | Complete local candidate gate, canonical composition/fault evidence, runtime review, and milestone benchmark suite |

The issue scope and changed boundary determine the tier, not project size or the
mere presence of image data. Escalate when evidence reveals wider risk. Do not
downgrade a durable or measured-path change to avoid its affected gate.

1. **Inner loop:** build the changed project when needed and run the smallest
   faithful focused test without coverage. Do not run the solution-wide matrix
   after every edit.
2. **Boundary checkpoint:** after a coherent contract, persistence, host, or UI
   slice, run the affected project tests and affected integration boundary.
   Independent affected gates may run concurrently when resources permit.
3. **Candidate gate:** once the implementation is stable, run the selected tier's
   local candidate evidence once per unchanged candidate before the first push.
   Tier C/M includes the complete standard local validation block, canonical
   coverage, output/observability review, and applicable performance harness.
   Tier A/B uses focused and affected local gates plus the target branch's
   protected CI profile. Correct a failed gate and rerun every failed or
   invalidated portion; preserve unaffected evidence only when its recorded
   boundary did not change.
   Choose the gate set with `scripts/ci:classify` on the review range, not by
   reading the diff, whenever it classifies the range successfully, and record
   which selector produced the set alongside the results. `complete` is not a
   condition on using it; it says whether the complete solution matrix is
   required, and `complete=false` with authoritative lane flags is the ordinary
   result for a component-scoped change. A ledger that lists gate
   results without naming the selector cannot distinguish a gate that passed
   from one that was never chosen, so a missing gate reads as silence rather
   than as an absence. This matters most exactly where it is least visible: the
   `changes` job is gated on `draft == false`, so through the entire draft
   convergence phase the classifier is the only selector available and every
   other view -- the implementer's and the reviewer's lenses alike -- is bounded
   by the diff.
4. **Review correction:** run the reproducer, focused regression, and full
   affected gate, with the affected set taken from the classifier whenever it
   classifies the delta successfully. Review the correction delta before final CI. Let protected CI
   provide the target branch's selected profile unless the
   correction changes shared contracts, migrations, test infrastructure,
   category/coverage logic, or another cross-cutting boundary with uncertain
   blast radius. For those exceptions, rerun the complete local candidate gate
   or explicitly enumerate and run every affected local gate before push.
5. **Performance correction:** rerun a long performance harness only when code,
   configuration, fixtures, workload parameters, or measurement logic on the
   measured path changed. Documentation-only and unrelated test changes do not
   invalidate it.
6. **Current-head CI:** every tier requires the target branch's successful
   protected CI profile for the latest PR head after review convergence and the final
   head-changing update. A stale green run never satisfies the merge gate; an
   unchanged successful latest-head run must not be repeated merely because a
   review or administrative step completed later.

If a supposedly focused correction exposes a cross-boundary failure, expand to
the affected integration gate immediately. Optimization means avoiding duplicate
evidence, not weakening failure investigation or the final merge bar.

## 5. Performance Protocol

Performance is a first-class design objective. More complexity is acceptable
when measured benefit is meaningful, bounded, and maintainable.

Every tier C/M implementation issue records each applicable field below and
marks a field `N/A` with a reason when it has no meaningful value. A tier C
measurement is reproducible even when the issue is not labeled `performance`;
the label identifies a comparative budget, canonical hot path, or milestone
that requires baseline/regression disposition. Do not add the label solely
because a phase once had a performance gate. Tier A/B work records correctness
and any cheap resource invariant needed by the changed behavior but does not
create comparative benchmark evidence. Umbrella issues link child evidence
rather than duplicating it.

| Field | Required evidence |
| --- | --- |
| Workload | Dimensions, formats, frame count, object count, recipe, concurrency, and data size |
| Environment | OS, architecture, SDK/runtime, build configuration, container/native mode, and service topology |
| I/O | Bytes read/written, operation count, batching, fsync/transaction behavior, and object requests |
| CPU | Total and per-operation time, sampled hot paths when useful, and algorithmic complexity |
| Memory | Allocations, large-object heap where applicable, working set, retained buffers, and queue/window size |
| Latency | Median and useful percentiles or bounded worst case for acquisition, ingress, processing, upload, and jobs |
| Throughput | Captures, artifacts, jobs, or bytes per unit time under an equivalent workload |
| Backlog | Queue depth, bytes, oldest age, and recovery time |
| Result | Baseline, candidate result, percentage change, interpretation, and residual risk |

Rules:

- Compare equivalent workloads and environments.
- Do not improve one metric by silently moving work outside the measurement.
- Prefer streaming, ownership-safe or pooled buffers, durable references,
  batching, and indexed queries over repeated full-frame copies and scans.
- Profile before introducing specialized caches, SIMD, parallelism, or complex
  persistence layouts.
- Verify that optimizations preserve checksums, numerical tolerances, lineage,
  ordering, failure semantics, and cancellation.
- Do not invent a universal threshold without evidence.
- Explain every material regression. An unexplained regression blocks merge.
- Record hardware-specific evidence separately; it is not required by this
  virtual-first milestone.

Use the canonical workloads, measurement boundaries, phase-specific evidence,
and complexity decision rule in `docs/planning/performance-validation.md`.

## 6. Test and Output Validation

Select tests according to the changed boundary:

- Unit tests for pure contracts, algorithms, validation, state transitions, and
  deterministic fixtures.
- SQL Server integration tests for EF migrations, uniqueness, row versions,
  leases, windows, and transactional behavior.
- SQLite integration tests for WAL journal atomicity, recovery, locking, and
  indexes.
- MinIO integration tests for streaming, checksum, range, orphan, and
  compensation behavior.
- CameraAgent integration tests for configured acquisition, ingress, lanes,
  local processing, storage, and outbox.
- Two-host tests for real outbox drain, authentication, ingest, central workers,
  retrieval, and fault recovery.
- bUnit and browser tests for durable read models, authorization, and audited
  operations.
- EF migration tests from an empty database through the canonical initial
  migration, plus repeated current-layout initialization, idempotent SQL,
  current constraints and indexes, locking, principal separation, and runtime
  behavior. Test prior schemas only for a released or explicitly documented
  current compatibility need; hand-written operational SQLite stores retain
  their owning contract's separate migration and recovery policy.

Data-producing behavior must validate outputs, not only status codes:

- Verify checksums and byte lengths.
- Verify image dimensions, stride, format, and media type.
- Verify numerical tolerances and deterministic fixture values.
- Verify recipe identity, parameter hash, profile identity, and source lineage.
- Verify database state, object state, journal state, and retention holds.
- Verify duplicate, retry, restart, and partial-failure convergence.

## 7. Runtime Observability Review

When a change runs a host, worker, queue, or service, inspect:

- Application errors and warnings.
- Structured log fields and exception preservation.
- Metrics for count, duration, bytes, backlog, age, retry, quarantine, and
  terminal failure.
- Traces across capture, ingress, upload, ingest, job, and retrieval boundaries.
- Health checks under healthy and degraded dependencies.
- Unexpected retry loops, duplicate work, orphaned files/objects, or stale
  leases.
- Metric-label cardinality.
- Secret, token, payload, path, or personal-data leakage.

A green process exit is insufficient when logs, metrics, traces, or durable
state show incorrect behavior.

Before an issue is marked ready, define its runtime signal manifest: expected
event IDs/log fields, metric names/units/allowed bounded labels, span boundaries,
health transitions, collection/assertion commands, and retained artifact path.
Use `N/A` with a reason for boundaries the issue does not operate.

## 8. Standard Local Validation

Use the SDK pinned by `global.json` and the solution
`HVO.SkyMonitor.v9.slnx`.

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
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release \
  --filter "TestCategory=Unit" \
  --settings tests/coverage.runsettings \
  --collect:"XPlat Code Coverage"
dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release \
  --filter "TestCategory=Integration" \
  --settings tests/coverage.runsettings \
  --collect:"XPlat Code Coverage"
```

The positive Unit filter must pass with an invalid Docker endpoint. Integration
is a separate required gate and requires Docker for the Testcontainers
assemblies. This complete block is the tier C/M local candidate gate, not the
default inner loop. Tier A/B uses its recorded focused/affected local evidence
and relies on the target branch's protected CI before merge. Follow the correction
rules in section 4 after review feedback.

The four script lines are the CI-control guards plus the shell syntax check.
They are Docker-free, need no build, and complete in under a minute, and they
belong here because the alternative is protected CI, which no draft reaches. On
PR #740 `scripts/test:ci-classification` failed on the first protected run after
a deep initial review, two correction rounds, a base sync and a base-sync review
had all passed; the defect was present throughout and none of those rounds could
have seen it. Run them again on the base-synced head before requesting the
base-sync review, since a review that verifies the merge preserved both sides'
behaviour cannot see a classifier lane that only their union violates.

Run this block under Bash 5.1 or newer. The macOS system Bash is 3.2, which
lacks `declare -A`; `scripts/ci:shell-syntax` refuses to run under it rather
than reporting a pass it did not perform, so invoke the Homebrew Bash there.

Additional issue-specific gates may include:

- Debug build.
- Warnings as errors.
- `dotnet format` verification.
- Package vulnerability/deprecation audit.
- EF pending-model and migration checks.
- SQLite recovery and lock-contention tests.
- Performance harnesses.
- Browser automation.
- Accelerated soak or external Stellarium workflow.

## 9. Required PR Lifecycle

Before changing PR or GitHub state, read and follow
`.agents/skills/pr-lifecycle/SKILL.md`. This execution protocol is the canonical
operating procedure; the independently loaded skill retains the non-negotiable
review request, selection, timeout, correction-cap, finalization-lock, CI,
merge, and cleanup contracts plus links back here.

The PR owner requests and monitors independent review using the lifecycle skill.
Human and one-shot agent routes record the immutable range, acceptance, evidence,
findings, and dispositions directly on the PR. Human
model/effort fields are N/A; agent values must reflect actual execution.

Tier A/B gets one full initial exact-range review and only finding-driven
correction rereviews. `standard` is the default for Tier A and ordinary Tier B;
concurrency, durability, security, CI-control, or cross-boundary Tier B risk
uses `deep`. Tier C/M remains deep. Before a correction launch, produce the
bounded evidence pack defined by the PR lifecycle skill. Always retain the
prior full report, complete carried-finding checklist, exact delta,
changed-symbol call sites, relevant tests, and applicable instructions; limit
unrelated preloaded context, not code reachable from the change. A token budget
is a checkpoint: exhaustion reports `INCOMPLETE` and escalates, never `CLEAN`.
Record tokens/time/diff/mode/profile/findings and compare three post-change
rounds before proposing a model default change.

After review convergence and lock acquisition, freshly fetch the target. If its
SHA equals the target-base SHA already covered by review, record `base unchanged
at <sha>; no merge and no base-sync review required`. If it advanced at all,
merge it without rebasing and obtain a base-sync review: standard only when
there was no conflict, shared file/contract, or material changed interaction;
deep otherwise. The lock, final-CI bar, and merge guard are unchanged.

The invariant sequence is:

```text
local candidate evidence -> draft PR -> review convergence or waiver
  -> final target-branch synchronization and base-sync review when it advanced
  -> ready -> target-branch protected CI -> merge and cleanup
```

Draft pushes intentionally do not run protected CI. Do not mark a PR ready while
review work or target-branch synchronization remains outstanding. A successful
protected run is valid only for the current reviewed head and current target
base; head-changing corrections and later target-branch changes follow the
skill's invalidation and recovery rules.

## 10. Non-Green Recovery

If any build, test, runtime, review, or deployment check is not green:

1. Capture the exact failing command, run URL, test, logs, and environment.
2. Determine whether the failure is deterministic, flaky, environmental, or a
   product defect.
3. Reproduce locally or with the smallest faithful integration harness.
4. Correct product code, tests, configuration, migration, or CI rather than
   suppressing the symptom.
5. Add regression coverage when the failure represents a product defect.
6. Rerun the focused failure and the full affected gate; do not rerun unrelated
   long suites locally when protected CI will cover them.
7. For a code correction, return the PR to draft and release the finalization
   lock before pushing. Review the CI-correction delta, then reacquire the lock
   and repeat final target synchronization, obtaining the required base-sync review
   if the target advanced or recording unchanged-base proof otherwise, before
   marking the PR ready for new protected CI. If no repository content changed, rerun
   the same SHA instead of creating a no-op commit under the bounded
   infrastructure-only exception in the lifecycle skill.
8. Keep the PR open and the issue active until green.

Do not merge around a failure, weaken a test without evidence, skip hooks, hide
warnings, or treat a canceled/timed-out check as success.

## 11. Handoff and Continuous Execution

An agent that stops, reaches a blocker, or exhausts context must leave this
handoff in the issue or epic:

```markdown
## Objective
- Current requirement and acceptance gate.

## Decisions
- Durable, architecture, compatibility, and performance decisions already made.

## Completed
- Commits, tests, migrations, measurements, reviews, and issue updates.

## Active
- Branch, PR, current files, current failing/pending check, and uncommitted work.

## Blocked
- Exact blocker, reproduction, logs, and required decision or dependency.

## Validation
- Commands run, results, coverage, output checksums/numeric evidence, and telemetry observations.

## Performance
- Baseline, current result, environment, interpretation, and open risks.

## Next Action
- One exact next command or implementation step.

## Relevant Files
- Paths needed to resume without repeating exploration.
```

The overall epic tracks current phase, completed PRs, active branch/PR, blockers,
validation state, performance observations, and next exact action.

After a successful merge, the issue owner confirms closure, updates the owning
epic's progress, synchronizes `main`, safely cleans up their branch/worktree,
records completion, and releases the issue claim. Taking another issue requires
explicit assignment or opt-in queue authorization. A context or resource limit
requires a resumable handoff, not silent abandonment of the assigned lifecycle.

## 12. Ready Signal

An approved issue is candidate-ready for an authorized owner when:

- Its dependencies are merged or explicitly coordinated.
- Its scope and exclusions are unambiguous.
- Required contracts and migrations are identified.
- Acceptance tests and performance evidence are specified.
- Its owning requirement groups and specifications are identified through the
  requirements crosswalk.
- No unresolved decision would invalidate implementation.
- The issue links this protocol and the relevant prompt section.
- It has no competing assignee, active claim/status label, or implementation PR.

The assigned owner verifies these conditions, follows the claim procedure in
section 4, posts the synopsis, and may record on the issue:

```text
READY: <issue number> - dependencies green, acceptance defined, no unresolved blocker.
```

This is an owner-recorded readiness check.
Detailed acceptance criteria and formal dependencies remain authoritative.
