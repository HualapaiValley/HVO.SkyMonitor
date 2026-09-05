# Agent Prompts

These prompts support approved work under the owning epics mapped in
`docs/roadmap.md`.
They supplement, but never override, `AGENTS.md`, `docs/roadmap.md`,
`docs/project-plan.md`, the active issue,
[the execution protocol](agent-execution.md), or the
[PR lifecycle skill](../../.agents/skills/pr-lifecycle/SKILL.md).

Choose providers and models by the capability required for the current brief.
Record both the requested role and the actual provider/model in the PR review
ledger; do not rely on a model alias remaining available.

## 1. Capability Roles

| Role | Required capability |
| --- | --- |
| Architecture and integration | Cross-host boundaries, contracts, migrations, dependency interactions, and complex corrective work |
| Persistence and performance | SQLite, SQL, object storage, filesystems, concurrency, I/O, CPU, memory, and performance evidence |
| Verification and experience | Test design, UI, documentation traceability, operational workflows, provenance, and output validation |

One provider may implement an issue, but performance-sensitive, migration-heavy, or
cross-host issues should use independent review perspectives before merge.
The coordinator should launch non-overlapping research, review, failure-analysis,
and evidence roles concurrently. Independent implementation issues may run in
parallel only in isolated worktrees with stable merged dependencies; default to
at most two active implementation issues.

## 2. Universal Implementation Prompt

```text
Implement GitHub issue <NUMBER> in HVO.SkyMonitor.

Read AGENTS.md, docs/project-plan.md, docs/planning/agent-execution.md, this
issue, all dependencies, and the owning subsystem specification before editing.
Use docs/planning/requirements-crosswalk.md to find retained requirements and
docs/planning/performance-validation.md for canonical workloads and evidence.
Inspect the current branch, worktree, recent commits, current tests, durable
formats, and open PR state. Preserve unrelated changes.

Before implementation, post a plain-language synopsis under 120 words covering
why this issue is next, the outcome, the practical benefit, what it unlocks, and
what is explicitly not included. Avoid architecture jargon where ordinary
language is sufficient.

Implement the smallest coherent issue slice. Keep CameraAgent and LogicHost
independent. Put transport-neutral contracts in AgentCore, astronomy in
Astronomy, pure image algorithms in Imaging, host-neutral recipe execution in
Processing, edge orchestration in CameraAgent.Common, and central persistence
and workers in LogicHost.

For durable changes, define compatibility, migration, restart, retry,
idempotency, quarantine, and retention behavior. For performance-sensitive
changes, record equivalent baseline and after evidence for relevant I/O, CPU,
memory/allocations, throughput, latency, and backlog. Do not accept unexplained
regression.

Add focused, integration, migration, fault, output, and UI tests as applicable.
Validate produced bytes, checksums, numerical results, provenance, lineage, and
durable state. Inspect logs, metrics, traces, and health behavior.

Use the execution protocol's validation ladder: focused inner-loop tests, one
stable-candidate local gate, affected correction gates, and classifier-selected
protected CI on the final reviewed head. Before changing PR state, read and
follow `.agents/skills/pr-lifecycle/SKILL.md`. Open a draft PR, review the full
initial diff, and limit rereviews to each correction delta plus verification of
prior findings. Require each correction report to identify the exact range and
mark every prior finding `verified corrected`, `verified deferred` with a linked
issue, or `unresolved`; an omitted or otherwise undispositioned finding remains
actionable. Use the skill's Copilot/Codex acquisition timeouts, three-round
correction cap, finalization lock, base-sync review, and final-CI rules. Do not
repeat long suites when the validated boundary did not change. After a
merge, the roadmap coordinator automatically selects, claims, and begins the
next candidate-ready issue after posting its synopsis and `READY` signal, unless
the operator asked to pause or a real decision/blocker prevents continuation.
Non-coordinator implementing agents return completion state to the coordinator
instead of selecting from the queue. While working, post a short progress
comment at every milestone and at least every thirty minutes: on the issue until
the draft PR exists, then in the PR's append-only review ledger. Each comment
states, with a UTC timestamp, what finished, what is running now, the next step,
and any blocker. Long gate or review runs get an interim note rather than
silence. This does not replace the final completion report. If blocked, leave
the required handoff in the issue and its owning roadmap epic.
```

## 3. Foundation and Contracts Prompt

Primary capability: architecture and integration
Review capabilities: persistence/performance and verification/experience

Use for shared public contracts, durable formats, and cross-host boundaries.

```text
Map every current serialized and persisted producer and consumer before changing
public contracts. Prefer one canonical current record and retain compatibility
only for a concrete current need; repository history alone is insufficient.
Preserve schema, recipe, algorithm, identity, hash, timing, layout, profile, and
lineage versions where they provide current validation, provenance,
reconstruction, or reproducibility. Add golden fixtures and architecture tests.
Measure serialization, validation, checksum, encoded size, allocation, and
throughput.
```

## 4. Shared Processing Prompt

Primary capability: architecture and integration
Review capabilities: persistence/performance and verification/experience

Use for host-neutral processing contracts, algorithms, and conformance.

```text
Extract behavior, not host infrastructure. Define explicit recipe inputs,
outputs, format requirements, canonical options, implementation identity, and
outcomes. Keep algorithms deterministic where configured. Avoid full-frame
copies and disposable host types in public APIs. Build cross-host conformance
tests proving equivalent bytes, numerical outputs, provenance, and failures.
Record complexity and temporary-memory behavior for each full-frame algorithm.
```

## 5. Edge Persistence and Lanes Prompt

Primary capability: persistence and performance
Review capabilities: architecture/integration and verification/experience

Use for edge files, SQLite, durable queues, retention, and recovery.

```text
Treat files as immutable payloads and SQLite WAL as transactional work state.
Define commit order, fsync/transaction expectations, indexes, lock behavior,
reconciliation, quarantine, disk pressure, required-consumer holds, and bounded
shutdown. Wake-up channels are accelerators only. Fault every boundary and
prove restart convergence. Measure file operations, bytes, transactions,
contention, allocations, queue age, throughput, and recovery time using
full-resolution virtual frames.
```

## 6. Cadence and Fleet Prompt

Primary capability: architecture and integration
Review capabilities: persistence/performance and verification/experience

Use for acquisition cadence, control loops, fleet state, and telemetry.

```text
Separate acquisition-critical timing from optional processing. Use active
setpoints, monotonic deadlines, explicit day/twilight/night policy, sparse
stride-aware metering, and reason-coded decisions. Disabled controls must do no
metering work. Persist truthful timing and heartbeat state. Use deterministic
VirtualSky and fake timing modules; do not require physical hardware. Measure
CPU, allocations, scan bytes, cadence, control latency, and backlog effects.
```

## 7. Central Persistence and Worker Prompt

Primary capability: architecture and integration
Review capabilities: persistence/performance and verification/experience

Use for central SQL/object storage, workers, ingest, and durable read models.

```text
Before first release, generate one canonical initial SQL migration from the
current model and initialize an empty database; do not preserve unreleased EF
history or add upgrade, downgrade, backfill, or convergence tests for it. Bind
capture-time profiles, never current registration state. Stream object-store payloads
and verify length/checksum. Keep SQL authoritative for jobs and lineage. Make
claim, renewal, output persistence, completion, retry, quarantine, and
reprocessing idempotent under concurrent workers and crashes. Resolve windows
by capture sequence, not ingest order. Measure SQL statements, object requests,
bytes, worker CPU/memory, queue age, claim latency, and processing throughput.
```

## 8. Weather and Cloud Prompt

Primary capability: architecture and integration
Review capabilities: verification/experience and persistence/performance

Use for environmental observations, virtual weather, and cloud assessment.

```text
Keep environmental observations distinct from image-derived cloud assessments.
Represent source, UTC validity, units, quality, and staleness. Make VirtualSky
cloud fields seeded and deterministic, applying attenuation before sensor
response. Keep cloud scoring/masks host-neutral and versioned. Test clear,
partial, overcast, stale, and missing-data cases. Validate raw checksums,
assessment ranges, masks, provenance, and cross-host conformance. Measure
full-frame CPU, allocations, memory, and throughput.
```

## 9. Virtual Transient Prompt

Primary capability: architecture and integration
Review capabilities: persistence/performance and verification/experience

Use for virtual events, extraction, assessment, and transient evidence.

```text
Use ordinary VirtualSky raw acquisition and durable lanes. Keep expected labels
outside detector inputs. Integrate sky events before sensor response and sensor
artifacts after optics. Keep events separate from one-capture artifact sets.
Persist structured geometry, features, assessments, reason codes, versions, and
source lineage. Exercise one-frame, boundary-crossing, multi-frame, clouded,
satellite, aircraft, cosmic-ray, hot-pixel, and no-event cases. Prove edge and
central restart/retry/reprocessing convergence. Report virtual sensitivity and
cost without making physical false-positive or performance claims.
```

## 10. UI Prompt

Primary capability: verification and experience
Review capabilities: architecture/integration and persistence/performance

Use for authenticated UI, APIs, state presentation, and browser behavior.

```text
Build UI only over stable authenticated APIs or server-side read services.
Display durable state, not in-memory inference. Preserve the repository's
Blazor component/code-behind/scoped-CSS conventions and dark theme. Cover
loading, empty, stale, degraded, unauthorized, and failed states. Keep worker
leases and storage credentials private. Audit mutations. Add bUnit and browser
tests. Measure paginated query cost, image/content transfer, render latency, and
memory for realistic gallery sizes.
```

## 11. End-to-End and Quality Prompt

Primary capability: architecture and integration
Review capabilities: persistence/performance and verification/experience

Use for production-adapter integration, fault injection, CI, and acceptance.

```text
Exercise actual production adapters where possible: real outbox drain, auth,
multipart ingest, SQL, Redis, the configured S3 service, worker, retrieval, and durable read models.
Inject failures at every commit boundary. Verify checksums, numerical outputs,
lineage, journal/database/object state, logs, metrics, traces, and health. Keep
external, soak, Stellarium, and future hardware workflows separately labeled.
Strengthen CI without hiding failures or weakening coverage. Produce a handoff
that names the exact next issue and command. If acting as the roadmap coordinator
and the current issue merges, execute the next claimed handoff automatically
rather than waiting for an operator `continue` message. Other agents return the
handoff to the coordinator.
```

## 12. Research and Review Subagent Prompt

Use independent research/review subagents proactively for cross-host,
persistence-heavy, performance-sensitive, or migration-heavy issues. The
implementing model remains responsible for verifying and explicitly dispositioning
every finding under the bounded review protocol.

```text
Research only; do not edit. Follow `.agents/skills/pr-lifecycle/SKILL.md`.
Review mode: <initial|correction|base-sync>. Base reviewed SHA: <SHA or merge
base>. Head SHA: <SHA>. In initial mode, audit the complete PR diff
against current code, tests, durable formats, architecture boundaries,
performance paths, logs/telemetry, and dependent issues. In correction mode,
review only Base reviewed SHA..Head SHA and verify disposition of the preceding
findings. Begin the report with the exact range examined and an item-by-item
table marking every prior finding `verified corrected`, `verified deferred` with
a linked issue and rationale, or `unresolved`. An omitted finding remains
unresolved. Do not substitute a generic whole-PR review or reopen unchanged
portions of the earlier diff without concrete evidence that the correction
created a new interaction. Return new findings ordered by severity with exact
paths and minimal fixes. Identify missing acceptance tests,
migration/compatibility risks, I/O/CPU/memory hot paths, and any plan/issue
contradiction. In base-sync mode, inspect conflict resolutions and new
interactions against the updated target base without rereviewing unchanged
upstream code. Acknowledge the start on the PR within fifteen minutes and report
progress at least every thirty minutes.
```

## 13. Handoff Prompt

```text
Stop implementation and write the resumable handoff required by
docs/planning/agent-execution.md. Include objective, decisions, completed work,
active branch/PR/files, exact blocker, validation and coverage, output evidence,
performance evidence, logs/telemetry observations, one exact next action, and
relevant paths. Update the owning roadmap epic if the issue cannot continue in
this session.
```
