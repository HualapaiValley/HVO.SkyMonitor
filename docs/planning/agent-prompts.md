# Agent Prompts

These prompts support work under
[epic #89](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/89).
They supplement, but never override, `AGENTS.md`, `docs/project-plan.md`, the
active issue, or [the execution protocol](agent-execution.md).

The named models are recommended coordination roles. If a named model is not
available in an execution environment, use an equivalent primary or reviewer
while preserving the required independent review focus.

## 1. Model Roles

| Model | Recommended role |
| --- | --- |
| GPT-5.6 Sol | Primary cross-host architecture, contracts, shared processing, integration, and complex corrective work |
| Terra | Primary or independent reviewer for SQLite/SQL/MinIO/filesystem persistence, concurrency, I/O, CPU, memory, and performance evidence |
| Luna | Primary or independent reviewer for UI, test design, documentation traceability, operational workflows, and output validation |

One model may implement an issue, but performance-sensitive, migration-heavy, or
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
stable-candidate local gate, affected correction gates, and complete current-head
replacement CI. Do not repeat long suites when the validated boundary did not
change. Follow the complete push/review/correct/resolve/merge workflow. After a
merge, the roadmap coordinator automatically selects, claims, and begins the
next candidate-ready issue after posting its synopsis and `READY` signal, unless
the operator asked to pause or a real decision/blocker prevents continuation.
Non-coordinator implementing agents return completion state to the coordinator
instead of selecting from the queue. If blocked, leave the required handoff in
the issue and epic #89.
```

## 3. Foundation and Contracts Prompt

Recommended primary: GPT-5.6 Sol
Recommended reviewers: Terra and Luna

Use for #91 and #92.

```text
Map every existing serialized and persisted consumer before changing public
contracts. Prefer additive versioned records. Preserve manifest v1 and legacy
sidecars without inventing missing facts. Add golden fixtures and architecture
tests. Treat canonical serialization, identity, hash, timing, layout, profile,
and lineage semantics as security and reproducibility boundaries. Measure
serialization, validation, checksum, encoded size, allocation, and throughput.
```

## 4. Shared Processing Prompt

Recommended primary: GPT-5.6 Sol
Recommended reviewers: Terra for performance and Luna for conformance tests

Use for #93, #96, #105, and #62.

```text
Extract behavior, not host infrastructure. Define explicit recipe inputs,
outputs, format requirements, canonical options, implementation identity, and
outcomes. Keep algorithms deterministic where configured. Avoid full-frame
copies and disposable host types in public APIs. Build cross-host conformance
tests proving equivalent bytes, numerical outputs, provenance, and failures.
Record complexity and temporary-memory behavior for each full-frame algorithm.
```

## 5. Edge Persistence and Lanes Prompt

Recommended primary: Terra
Recommended reviewers: GPT-5.6 Sol for integration and Luna for fault tests

Use for #94, #95, #97, and the edge parts of #59.

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

Recommended primary: GPT-5.6 Sol
Recommended reviewers: Terra for timing/performance and Luna for telemetry tests

Use for #58 and #102.

```text
Separate acquisition-critical timing from optional processing. Use active
setpoints, monotonic deadlines, explicit day/twilight/night policy, sparse
stride-aware metering, and reason-coded decisions. Disabled controls must do no
metering work. Persist truthful timing and heartbeat state. Use deterministic
VirtualSky and fake timing modules; do not require physical hardware. Measure
CPU, allocations, scan bytes, cadence, control latency, and backlog effects.
```

## 7. Central Persistence and Worker Prompt

Recommended primary: GPT-5.6 Sol
Recommended reviewer: Terra for SQL/MinIO concurrency and Luna for API/output tests

Use for #98, #99, #100, #101, and the central parts of #60.

```text
Use additive SQL migrations and preserve legacy history. Bind capture-time
profiles, never current registration state. Stream MinIO payloads and verify
length/checksum. Keep SQL authoritative for jobs and lineage. Make claim,
renewal, output persistence, completion, retry, quarantine, and reprocessing
idempotent under concurrent workers and crashes. Resolve windows by capture
sequence, not ingest order. Measure SQL statements, object requests, bytes,
worker CPU/memory, queue age, claim latency, and processing throughput.
```

## 8. Weather and Cloud Prompt

Recommended primary: GPT-5.6 Sol
Recommended reviewers: Luna for fixtures/provenance and Terra for image performance

Use for #103, #104, #105, #157, and #209.

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

Recommended primary: GPT-5.6 Sol
Recommended reviewers: Terra for window/performance behavior and Luna for scenario/review evidence

Use for #61-#65.

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

Recommended primary: Luna
Recommended reviewers: GPT-5.6 Sol for backend contracts and Terra for query/content performance

Use for #106 and #107.

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

Recommended primary: GPT-5.6 Sol
Recommended reviewers: Terra for infrastructure/performance and Luna for user-visible/output validation

Use for #90, #108, and #109.

```text
Exercise actual production adapters where possible: real outbox drain, auth,
multipart ingest, SQL, Redis, MinIO, worker, retrieval, and durable read models.
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
implementing model remains responsible for verifying findings and correcting
every actionable result.

```text
Research only; do not edit. Audit issue <NUMBER> against current code, tests,
durable formats, architecture boundaries, performance paths, logs/telemetry,
and dependent issues. Return findings ordered by severity with exact paths and
minimal fixes. Identify missing acceptance tests, migration/compatibility risks,
I/O/CPU/memory hot paths, and any plan/issue contradiction.
```

## 13. Handoff Prompt

```text
Stop implementation and write the resumable handoff required by
docs/planning/agent-execution.md. Include objective, decisions, completed work,
active branch/PR/files, exact blocker, validation and coverage, output evidence,
performance evidence, logs/telemetry observations, one exact next action, and
relevant paths. Update epic #89 if the issue cannot continue in this session.
```
