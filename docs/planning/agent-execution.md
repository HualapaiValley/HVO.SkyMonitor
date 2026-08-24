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
8. `docs/planning/agent-prompts.md`
9. Current code and tests

If sources conflict, stop implementation long enough to resolve the conflict in
the issue or authoritative plan. Do not silently choose a convenient behavior.

## 2. Starting an Issue

Before changing files:

1. Confirm the issue is open and assigned to the expected milestone.
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
- Preserve manifest v1, old sidecars, and existing central history when the
  issue changes durable formats.
- Use additive migrations before destructive cleanup.
- Assign identity before optional work.
- Preserve immutable raw evidence and complete source lineage.
- Keep queues bounded and durable work discoverable after missed wake-ups.
- Make skip, retry, quarantine, and terminal behavior explicit.
- Avoid compatibility code unless a shipped durable format or external consumer
  requires it.
- Add comments only where the code would otherwise hide a non-obvious invariant.

## 4. Throughput and Validation Economy

Quality gates are unchanged, but expensive work must not be repeated without a
reason.

### Coordination

- The active initiative's owning roadmap epic names one coordinator for the
  execution session. Only that coordinator selects or claims the next issue;
  implementing agents return completion/blocker state to it rather than
  independently consuming the queue.
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
   affected gate. Let replacement CI provide the complete matrix unless the
   correction changes shared contracts, migrations, test infrastructure,
   category/coverage logic, or another cross-cutting boundary with uncertain
   blast radius. For those exceptions, rerun the complete local candidate gate
   or explicitly enumerate and run every affected local gate before push.
5. **Performance correction:** rerun a long performance harness only when code,
   configuration, fixtures, workload parameters, or measurement logic on the
   measured path changed. Documentation-only and unrelated test changes do not
   invalidate it.
6. **Current-head CI:** every tier requires the complete protected CI matrix after the
   final pushed correction. A stale green run never satisfies the merge gate.

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
- Migration tests from clean, current, and legacy schemas.

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

Every PR follows this sequence:

1. Inspect worktree, diff, recent log, issue, and dependencies.
2. Use the validation ladder: focused tests while developing, then the selected
   tier's candidate evidence with applicable output/performance review before
   push. Tier C/M runs the complete local candidate gate.
3. Commit only intended files.
4. Push the issue branch.
5. Open a PR linked to the issue and its owning roadmap epic.
6. Include implementation, migrations/compatibility, tests, output evidence,
   performance evidence, logs/telemetry review, and residual risks.
7. Wait for automatic CI and automatic review.
8. Investigate every non-green check.
9. Correct every actionable review finding.
10. Push correction commits; do not hide corrections through an unrequested
    amend or force push.
11. Run focused and affected local correction gates, then wait for the complete
    replacement CI matrix on the corrected head.
12. Reply to review threads with the correction commit and evidence.
13. Resolve threads only after the correction exists.
14. Merge only when the current head is mergeable, every required current-head
    check is green, and every actionable thread is resolved.
15. Confirm the issue closes, update the owning roadmap epic, synchronize local
    `main`, and preserve unrelated worktree changes.

A pre-correction green run is stale and does not satisfy the gate.

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
   long suites locally when replacement CI will cover them.
7. Push a new correction and require replacement CI.
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
