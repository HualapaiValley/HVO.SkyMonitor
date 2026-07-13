# Agent Execution Protocol

This protocol applies to every issue in the
[Virtual-First Platform Completion milestone](https://github.com/RoySalisbury/HVO.SkyMonitor/milestone/1)
and [epic #89](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/89).
It is designed so work can stop, hand off, and resume without losing decisions,
validation state, or the exact next action.

## 1. Authority Order

Read these sources in order before implementation:

1. `AGENTS.md`
2. `docs/project-plan.md`
3. The active GitHub issue and its dependencies
4. The owning subsystem specification or runbook
5. `docs/planning/requirements-crosswalk.md`
6. `docs/planning/performance-validation.md`
7. `docs/planning/agent-prompts.md`
8. Current code and tests

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
7. Record unresolved product or architecture decisions in the issue.
8. Capture a behavior and performance baseline where the issue is
   performance-sensitive.
9. Define acceptance tests before selecting an implementation.

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

## 4. Performance Protocol

Performance is a first-class design objective. More complexity is acceptable
when measured benefit is meaningful, bounded, and maintainable.

Every implementation issue labeled `performance` records each applicable field
and marks a field `N/A` with a reason when it has no meaningful value. Umbrella
issues link their child evidence rather than duplicating it.

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

## 5. Test and Output Validation

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

## 6. Runtime Observability Review

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

## 7. Standard Local Validation

Use the SDK pinned by `global.json` and the solution
`HVO.SkyMonitor.v9.slnx`.

```bash
dotnet restore
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release
dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release \
  --filter "TestCategory!=Integration&TestCategory!=Manual" \
  --settings tests/coverage.runsettings \
  --collect:"XPlat Code Coverage"
./scripts/coverage:enforce
```

Until issue #110 adds real categories, the filter still
runs Testcontainers and requires Docker. Run focused tests during development,
then run the issue's full required gate before push.

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

## 8. Required PR Lifecycle

Every PR follows this sequence:

1. Inspect worktree, diff, recent log, issue, and dependencies.
2. Run local build, focused tests, full required tests, output validation, and
   performance checks.
3. Commit only intended files.
4. Push the issue branch.
5. Open a PR linked to the issue and epic #89.
6. Include implementation, migrations/compatibility, tests, output evidence,
   performance evidence, logs/telemetry review, and residual risks.
7. Wait for automatic CI and automatic review.
8. Investigate every non-green check.
9. Correct every actionable review finding.
10. Push correction commits; do not hide corrections through an unrequested
    amend or force push.
11. Wait for replacement CI on the corrected head.
12. Reply to review threads with the correction commit and evidence.
13. Resolve threads only after the correction exists.
14. Merge only when the current head is mergeable, every required current-head
    check is green, and every actionable thread is resolved.
15. Confirm the issue closes, update epic #89, synchronize local `main`, and
    preserve unrelated worktree changes.

A pre-correction green run is stale and does not satisfy the gate.

## 9. Non-Green Recovery

If any build, test, runtime, review, or deployment check is not green:

1. Capture the exact failing command, run URL, test, logs, and environment.
2. Determine whether the failure is deterministic, flaky, environmental, or a
   product defect.
3. Reproduce locally or with the smallest faithful integration harness.
4. Correct product code, tests, configuration, migration, or CI rather than
   suppressing the symptom.
5. Add regression coverage when the failure represents a product defect.
6. Rerun the focused failure and the full affected gate.
7. Push a new correction and require replacement CI.
8. Keep the PR open and the issue active until green.

Do not merge around a failure, weaken a test without evidence, skip hooks, hide
warnings, or treat a canceled/timed-out check as success.

## 10. Handoff and Continuation

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

## 11. Ready Signal

An issue is ready for an implementation agent only when:

- Its dependencies are merged or explicitly coordinated.
- Its scope and exclusions are unambiguous.
- Required contracts and migrations are identified.
- Acceptance tests and performance evidence are specified.
- Its owning requirement groups and specifications are identified through the
  requirements crosswalk.
- No unresolved decision would invalidate implementation.
- The issue links this protocol and the relevant prompt section.

When those conditions hold, update the issue or epic with:

```text
READY: <issue number> - dependencies green, acceptance defined, no unresolved blocker.
```
