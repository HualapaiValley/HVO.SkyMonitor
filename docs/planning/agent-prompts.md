# Agent Prompts

These prompts support approved work under the owning epics mapped in
`docs/roadmap.md`.
They supplement, but never override, `AGENTS.md`, `docs/roadmap.md`,
`docs/project-plan.md`, the active issue,
[the execution protocol](agent-execution.md), or the
[PR lifecycle skill](../../.agents/skills/pr-lifecycle/SKILL.md).

Choose the capability profile before choosing a provider. Use `fast` for
deterministic inventory and isolated low-risk work, `standard` for ordinary
implementation and review, and `deep` for Tier C/M, security, data-loss,
concurrency, migration, architecture, CI-control, conflict-resolution, or
material-correctness work. Reviews use at least medium effort; initial reviews
normally use high effort. The PR lifecycle skill contains the full selection
matrix and current harness mappings. Record requested profile and actual
provider/model/effort in the PR review ledger; do not infer an alias or claim a
provider-managed setting was enforced.

## 1. Capability Roles

| Role | Required capability |
| --- | --- |
| Architecture and integration | Cross-host boundaries, contracts, migrations, dependency interactions, and complex corrective work |
| Persistence and performance | SQLite, SQL, object storage, filesystems, concurrency, I/O, CPU, memory, and performance evidence |
| Verification and experience | Test design, UI, documentation traceability, operational workflows, provenance, and output validation |

One provider may implement an issue, but performance-sensitive, migration-heavy, or
cross-host issues should use independent review perspectives before merge.
The issue owner may request non-overlapping research, review, failure-analysis,
and evidence work concurrently. Independent implementation issues may run in
parallel in isolated worktrees with stable merged dependencies. Capacity is
assessed per shared resource (host, Docker, storage, or external service), not
through a global two-issue slot limit.

## 2. Universal Implementation Prompt

The default owner is an authorized human or an operator-directed agent, not a
coordinator-assigned worker. No enrollment, `READY` signal, command receipt, or
fleet heartbeat is required for the default workflow. `JOIN REQUEST`,
participant-bound `JOIN ACK`, `JOINED ACK`, targeted commands, slot leases, and
coordinator heartbeat/relay mechanics apply only to optional legacy managed
sessions explicitly enrolled by the operator. Their guarded resumed-session
tools retain the execution protocol's identity and command checks; do not use
them as an unguarded substitute for a one-shot review.

```text
Implement GitHub issue <NUMBER> in HVO.SkyMonitor.

Read AGENTS.md, docs/project-plan.md, docs/planning/agent-execution.md, this
issue, all dependencies, and the owning subsystem specification before editing.
Use docs/planning/requirements-crosswalk.md to find retained requirements and
docs/planning/performance-validation.md for canonical workloads and evidence.
Inspect the current branch, worktree, recent commits, current tests, durable
formats, and open PR state. Preserve unrelated changes.

Claim the assigned issue before implementation: inspect existing claims, assign
the GitHub issue to yourself if possible, apply workflow:in-progress, and post a
claim comment with a unique person/agent identity, branch/worktree, scope, and
next checkpoint. For agents, use <harness>:<provider>:<host>:<session-short-id>
without credentials or full session tokens. An assignee alone is insufficient
when agents share an account. Re-read the issue, labels, assignees, and claim
comments after posting: these writes are not atomic. Stop on a competing claim
and resolve ownership explicitly before proceeding. If permissions prevent the
claim, report the blocker rather than treating it as acquired.
Keep the claim through review and CI. Release it explicitly with a resumable
handoff on completion or transfer; age or silence never permits automatic
takeover. Assess capacity per shared resource, not global implementation slots.

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
actionable. The PR owner requests independent human or one-shot agent review,
binding the immutable base/head SHAs and recording reviewer identity, findings,
and dispositions in the PR ledger. For agent reviews record requested capability
and actual provider/model/effort; use the skill's acquisition timeouts and
fallback rules for applicable agent routes. Human reviews record identity and
use N/A for model/effort. A provider-side current-head audit alone does not
replace exact-range review. Preserve the three-round correction cap,
finalization lock, base-sync review or unchanged-base proof, validation, and
final-CI rules. Do not repeat long suites when the validated boundary did not
change. Sandbox and permission denials remain binding; never route a denied
action through another session.

Own this issue through implementation, review corrections, green required CI,
authorized merge, issue closure verification, local synchronization, and branch/
worktree cleanup without deleting unrelated work. If merge is reserved or not
authorized, report that boundary and leave a handoff rather than bypassing it.
Report milestones and blockers directly to the operator and on the issue until
the draft PR exists, then in its append-only ledger, using UTC at least every
thirty minutes during active work. Long gates and reviews get interim notes.
On completion or a stop, record completed/current/next/blocker state and an
explicit claim release or retention; update the owning epic for roadmap work.
Do not select or start another issue unless the user authorized that scope.
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
Strengthen CI without hiding failures or weakening coverage. Complete the
assigned issue through independent review, green required CI, authorized merge,
and cleanup. Produce a handoff naming the exact next action and claim state.
Do not take another issue without user authorization.
```

## 12. Research and Review Subagent Prompt

Use independent research/review subagents proactively for cross-host,
persistence-heavy, performance-sensitive, or migration-heavy issues. The
implementing model remains responsible for verifying and explicitly dispositioning
every finding under the bounded review protocol.

```text
Research only; do not edit. Follow `.agents/skills/pr-lifecycle/SKILL.md`.
Before substantive work, notify the requesting issue/PR owner with the requested
capability profile, actual provider/model/effort, current step, next step, and
blocker. For a review, include the exact immutable range and append the same
acknowledgement to the PR ledger. For issue-only research, identify the issue
and base commit or worktree fingerprint instead, and post on the issue. If the
harness cannot post, ask the owner to add an attributed proxy entry. Use
the model and effort pinned by the dispatch. If the actual values do not match,
or the selected capability cannot be verified, stop and report the mismatch
rather than silently inheriting defaults. Report an intentionally fixed
provider model as `provider-managed`, as required by the skill.
Task mode: <issue-research|initial|correction|base-sync>.

In issue-research mode, identify the issue plus base commit or worktree
fingerprint, investigate only the assigned question, and return conclusions,
evidence, uncertainty, and the recommended next action. Acknowledge on the issue
and report each milestone to the owner and issue ledger. A PR number,
review range, and PR acknowledgement are not required.

For every review mode, provide Base reviewed SHA: <resolved immutable SHA> and Head
SHA: <SHA>. In initial mode, audit the complete PR diff against current code,
tests, durable formats, architecture boundaries, performance paths,
logs/telemetry, and dependent issues. In correction mode, review only Base
reviewed SHA..Head SHA and verify disposition of the preceding findings. Begin
the report with the exact range examined and an item-by-item table marking every
prior finding `verified corrected`, `verified deferred` with a linked issue and
rationale, or `unresolved`. An omitted finding remains unresolved. Do not
substitute a generic whole-PR review or reopen unchanged portions of the earlier
diff without concrete evidence that the correction created a new interaction.
Return new findings ordered by severity with exact paths and minimal fixes.
Identify missing acceptance tests, migration/compatibility risks,
I/O/CPU/memory hot paths, and any plan/issue contradiction. In base-sync mode,
inspect conflict resolutions and new interactions against the updated target
base without rereviewing unchanged upstream code. For every review mode,
acknowledge the start on the PR within fifteen minutes and report each milestone
to the owner and PR ledger, with an interim report at least every thirty
minutes.
```

## 13. Handoff Prompt

```text
Stop implementation and write the resumable handoff required by
docs/planning/agent-execution.md. Include objective, decisions, completed work,
active branch/PR/files, exact blocker, validation and coverage, output evidence,
performance evidence, logs/telemetry observations, one exact next action, and
relevant paths. Record whether the issue claim remains with this owner or is
explicitly released for transfer; silence or age is not a release. Update the
owning roadmap epic for roadmap work if the issue cannot continue in this
session. Do not claim another issue without user authorization.
```
