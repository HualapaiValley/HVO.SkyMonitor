# Agent Execution Protocol

This protocol applies to every approved issue in `docs/roadmap.md` and its owning
roadmap epic or milestone.
It is designed for continuous roadmap execution: work may stop, hand off, and
resume without losing decisions, validation state, or the exact next action,
but a completed issue does not require an operator prompt before the next ready
issue starts.

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

## 2. Starting an Issue

Before changing files:

1. Confirm the issue is open and mapped to the expected roadmap initiative,
   owning epic, and milestone when one is assigned.
2. Confirm every required dependency is merged or explicitly coordinated.
3. Inspect `git status`, recent commits, current branch, and related PRs.
4. Preserve unrelated worktree changes.
5. Create one issue-focused branch from current `main`.
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

### Coordination

- The active initiative's owning roadmap epic names one coordinator for the
  execution session. Only that coordinator selects or claims the next issue;
  implementing agents return completion/blocker state to it rather than
  independently consuming the queue.
- Implementation slots are shared capacity, not entitlements for different
  components or initiatives. Fill them from the highest-priority Current
  initiative before selecting a lower roadmap horizon. A lower-horizon issue
  may move ahead only when the active epic records it as a real blocking
  dependency.
- When an active epic defines parallel lanes, those lanes coordinate work only
  within that initiative. An empty lane takes another non-overlapping ready
  issue from the same initiative or remains empty; it does not automatically
  advance to Next, Future, Research, or Deferred work.
- A required compatibility check may block a merge, but an unrelated failure
  from another component does not make that component's backlog a dependency.
  First determine whether the active change caused the failure. Correct a real
  cross-boundary regression in the active issue; otherwise correct the CI
  classification or leave the independently owned defect in its roadmap order.
- Keep one implementing agent per issue branch. Use research, review, failure
  analysis, and evidence agents concurrently when their work does not overlap.
- The roadmap coordinator may maintain at most two active implementation issues
  globally by default, including issues waiting on CI or review. Raise that limit
  only when machine and Docker capacity are known to support it.
- Before creating a worktree, claim the issue in the owning roadmap epic with its
  branch, worktree owner, and dependency base. Exclude every claimed or active
  issue from subsequent selection and clear the claim after merge or explicit
  release.
- Concurrent issues require stable merged dependencies and separate branches
  and isolated git worktrees. Agents must never edit the same worktree.
- Do not start a downstream issue from an unmerged contract or migration unless
  the issues explicitly define a stacked PR sequence.
- Assign each expensive test, benchmark, or evidence run one owner. Other agents
  consume its recorded result instead of launching the same run.
- Avoid running multiple Testcontainers or full-resolution performance suites
  concurrently against shared Docker resources unless their isolation and
  capacity have been verified.
- While CI runs, use available agents for independent review, next-ready-issue
  discovery, synopsis preparation, or non-overlapping work rather than polling
  as the only activity.

### Progress reporting

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
  the coordinator a structured `STARTED` message before substantive work, then
  milestone, blocker, at-least-thirty-minute, and completion messages. Review
  messages also identify the exact range and actual provider/model/effort. A
  long review posts an interim note after thirty minutes. Each agent also posts
  the same milestones on the owning issue or PR ledger. If its harness cannot,
  the coordinator posts a clearly attributed proxy entry before relaying the
  event to the main conversation.
- The coordinator immediately relays delegated starts, milestones, blockers,
  and completions to the main conversation, then arms exactly one persistent
  status monitor on a five-minute cadence while delegated agents, review
  acquisition, long gates, or CI runs are active. Use the harness's scheduled
  task, background loop, transcript tail, session status, or equivalent
  capability. Start the monitor when work becomes active, restart it whenever
  the active agent or PR set changes, and stop it when nothing is active.
  Harness UI activity or repository comments without a main-conversation relay
  do not satisfy operator-visible reporting.
- The monitor collects state; the coordinator owns delivery. Do not delegate
  the delivery obligation to an observer that cannot inspect sibling work or
  signal the coordinator. A timer or background shell loop whose output remains
  buffered until someone manually polls it is only a collector and does not
  satisfy the monitor requirement. When the harness lacks a native scheduled
  wake, use one delegated observer that sends the coordinator a message every
  five minutes, and keep the coordinator waiting on the mailbox or equivalent
  event path between active work. If no signaling observer is available, poll
  directly until one is available rather than claiming a buffered loop is a
  working monitor.
- Prove the signaling path after every start or active-set restart: require an
  immediate baseline message and relay it to the main conversation. Treat a
  late scheduled signal as a monitor failure, report the gap, repair or replace
  the monitor, and have the coordinator poll directly until the replacement
  proves its signaling path with a new immediate baseline.
- On every wake, the monitor records, per issue or PR: the agent's last activity
  timestamp and current step; the PR head SHA, draft state, and merge state; the
  first line and timestamp of the latest ledger comment; and the state of shared
  locks such as the Docker or finalization window. While review is pending,
  include its provider, range, request age, acknowledgement deadline, start and
  fallback state, and correction-rereview count. Use the best available harness
  signal for the current step, such as the transcript's latest tool description.
  Attach a one-line CI result watcher to each PR that emits only on success,
  failure, or cancellation.
- The coordinator relays a short note to the main conversation on every wake,
  even when nothing changed. It uses the literal status `still running, no
  change` when applicable, converts every reported time to MST (fixed UTC-7,
  without daylight-saving adjustment), and labels it `MST`. When nothing
  changed, use one compact line per active item that still names the current
  step, next step, and blocker. Do not repeat a milestone already relayed
  immediately unless its state changed.
- For each item, the coordinator states what just finished, what is running now,
  the next step, and any blocker. It reads milestone reports and summarizes
  their substance, such as the root cause, accepted findings, or gate result,
  rather than only repeating a label.
- If an implementing agent goes more than thirty minutes without an issue or
  ledger comment, the coordinator instructs it to post one before continuing
  and reports that intervention in the next operator note.
- A progress comment never replaces the handoff in section 11; a blocked agent
  still leaves the full handoff, and every agent still provides its final
  completion report.

#### Cross-provider coordinator channel

When two active coordinators cannot send direct harness messages, the owning
roadmap epic may act as a bounded control plane. It does not replace the issue
and PR ledgers.

1. Create or nominate exactly two fixed mutable comments, one owned by each
   coordinator. Record both comment IDs, owners, format version, cadence, and
   activation time once in an append-only epic protocol comment. A coordinator
   writes only its own slot.
2. Update each slot every five minutes while either side has active work. Send
   `no work available` or request `report status` when there is no richer
   instruction; unchanged work still reports `still running, no change`. Each
   record carries a monotonically increasing sequence and acknowledges the last
   peer sequence observed so lost or duplicated delivery is visible.
3. Use a compact delta record for routine liveness. The complete body should be
   at most 500 UTF-8 bytes and must preserve: format version, sequence and
   acknowledgement, authoritative UTC plus operator-facing fixed MST time,
   current step, next step, blocker, shared-resource owner, any request, and the
   next due time. Reference a durable comment ID instead of repeating a grant,
   review report, or long rationale.
4. Keep claims, authority grants, review findings, milestones, blockers, and
   handoffs append-only on the owning issue, PR, or epic. Mutable slots contain
   only the latest control state and may be overwritten. Relay material changes
   immediately; do not repeat the same full milestone in the next heartbeat.
5. Retain the exact slot IDs and last `updated_at` values. Poll only that
   metadata first, fetch the body only after it changes, and fetch durable
   comments newer than the last processed comment ID or timestamp. Reread an
   epic body only after an intentional revision. Never rescan the full epic on a
   routine wake. During a trial, total per active hour the metadata polls,
   changed-body fetches, slot bytes read and written, durable comments, estimated
   transcript tokens, and coordinator service time.
6. Treat a missed delivery window as a signaling gap, not proof that work
   failed. Report the gap, withhold new shared-resource authority when state is
   stale, poll directly, and ask the peer to repair or replace its monitor. Long
   work must be detached from any harness primitive that suppresses scheduled
   wakes and polled on each tick.
7. Negotiate a format or transport change through the existing channel, require
   an explicit acceptance or counterproposal, and keep the previous format as
   fallback until the new one completes a bounded trial. Record payload size,
   latency, missed wakes, repair time, ambiguity, resource/collision outcomes,
   and coordinator effort in
   [the coordination experiment log](coordination-experiments.md).

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
   Tier A/B uses focused and affected local gates and lets protected CI provide
   the complete matrix. Correct a failed gate and rerun every failed or
   invalidated portion; preserve unaffected evidence only when its recorded
   boundary did not change.
4. **Review correction:** run the reproducer, focused regression, and full
   affected gate. Review the correction delta before final CI. Let protected CI
   provide the complete classifier-selected plan unless the
   correction changes shared contracts, migrations, test infrastructure,
   category/coverage logic, or another cross-cutting boundary with uncertain
   blast radius. For those exceptions, rerun the complete local candidate gate
   or explicitly enumerate and run every affected local gate before push.
5. **Performance correction:** rerun a long performance harness only when code,
   configuration, fixtures, workload parameters, or measurement logic on the
   measured path changed. Documentation-only and unrelated test changes do not
   invalidate it.
6. **Current-head CI:** every tier requires one successful classifier-selected
   protected CI run for the latest PR head after review convergence and the final
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
and relies on the same complete protected CI before merge. Follow the correction
rules in section 4 after review feedback.

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
`.agents/skills/pr-lifecycle/SKILL.md`. That skill is the canonical detailed
procedure and defines review requests, provider timeouts, correction-rereview
limits, the finalization lock, target-branch synchronization, protected CI,
merge, and cleanup.

The invariant sequence is:

```text
local candidate evidence -> draft PR -> review convergence or waiver
  -> final target-branch synchronization and base-sync review
  -> ready -> classifier-selected protected CI -> merge and cleanup
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
   and repeat final target synchronization and base-sync review before marking
   the PR ready for new protected CI. If no repository content changed, rerun
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

After a successful merge, the roadmap coordinator must:

1. Confirm issue closure, update the owning roadmap epic, and synchronize `main`.
2. Recompute the unclaimed candidate-ready queue defined in section 12.
3. Fill only available implementation slots, up to the global maximum. Select by
   explicit epic priority first, then dependency critical-path unlocks, roadmap
   phase order, and finally oldest issue number.
4. For each selected issue, post its plain-language synopsis and `READY` signal,
   record its claim in the owning roadmap epic, create its issue branch/worktree,
   and begin the lifecycle without asking the operator to say `continue`.
5. Start a second independent issue only when a slot is available and doing so
   will not compete for the same contracts, migrations, or Docker-heavy gates.

Pause automatic continuation only when the operator explicitly asks, no issue is
candidate-ready, a product/architecture decision requires operator input, or
safe execution capacity is exhausted. Context exhaustion requires a handoff to a
successor, not an operator prompt merely to continue.

## 12. Ready Signal

An issue is candidate-ready for coordinator selection when:

- Its dependencies are merged or explicitly coordinated.
- Its scope and exclusions are unambiguous.
- Required contracts and migrations are identified.
- Acceptance tests and performance evidence are specified.
- Its owning requirement groups and specifications are identified through the
  requirements crosswalk.
- No unresolved decision would invalidate implementation.
- The issue links this protocol and the relevant prompt section.
- It is not already claimed or active in its owning roadmap epic.

After the coordinator selects a candidate-ready issue, post its plain-language
synopsis and update the issue or epic with:

```text
READY: <issue number> - dependencies green, acceptance defined, no unresolved blocker.
```

The issue becomes ready for an implementation agent only after that update. The
ready update must include or link the synopsis. Detailed acceptance criteria
remain authoritative; the synopsis explains why the work is worth doing and
what it unlocks.
