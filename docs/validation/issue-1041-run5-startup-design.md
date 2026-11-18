# #1041 bounded startup and background qualification

Refs #1041. Design before product changes, based on Kepler's combined HIGH
findings at e7b3784328f66d7d8d11678796193a028a838b72 and Roy's Nov 11 decisions
t388u / t389u. The original R1–R9 maps, Option A approval and attribution reports
remain governing. This is implementation planning, not a completion/review claim.

## Verification boundaries

Startup reads fresh configuration, schedule, protected location authority and
small ledger metadata. It checks the production date partitions intersecting the
last 48 hours and an indexed, limited query of recent raw-journal rows. It never
recurses through older partitions or arbitrary retained directories. The UTC date
partition boundary can include the older part of the first day (at most 72 hours).
Explicit enumeration/row/byte limits bound malformed or excessive recent input;
exhaustion cannot grant readiness. Unknown layouts and older auxiliary/quarantine
evidence belong to background verification. Bad recent evidence must keep
recovery/UI available while refusing lifecycle identity. The later run-6
incremental implementation violated that requirement by clearing a detected recent
failure (round-4 F1). The [round-4 correction](issue-1041-run7-corrections.md)
revokes incremental eligibility on recent failure and restricts readiness recovery
to a full pass or a successful fresh recent check.

The ordinary cold-load path must not fall back to a full inline scrub. Schedule/capture initialization can invoke raw-ingress reconciliation; it is deferred until ApplicationStarted as well. Capture configuration remains unpublished until that recovery completes; pending dependencies report degraded health. Fresh
identity reconciliation is deferred when it needs old evidence; old protected
authority remains authoritative until the background operation validates the
current config snapshot and commits. A changed input never becomes immutable
history from a stale snapshot.

A host-owned coordinator starts background qualification after startup and owns
periodic/operator/dirty requests. It coalesces requests, runs without holding the
configuration/store gate through the corpus walk, and does not tie work to an
HTTP caller's cancellation. Permanent invalid/drift/missing-authority outcomes
remain recovery-required; ordinary polling cannot repeatedly launch full scans.
An explicit operator scrub, actual managed generation/config change or scheduled
maintenance can request another attempt. An already qualified, unexpired process
keeps its prior readiness during a periodic refresh; observed drift invalidates
immediately and expiration cannot be extended by writes.

Every new process remains unqualified until its background pass authenticates
all expected entries and verifies current retained bytes. Authenticated unchanged
content proofs with the same history/validator identity skip reparsing and repeated
Data Protection operations, not the full byte-integrity check. No persisted
checkpoint alone grants lifecycle readiness, including clean restart. Dirty/crash,
restore/rollback, pending writes, changed validators, missing authority and drift
continue to require full actual-byte/schema/location verification. This is the
conservative implementation of Roy's incremental design: file reads outside the
recent window move off startup; per-entry proof work and commits are batched.

While background qualification is genuinely pending, the protected endpoint
returns declared pending/refusal, never a partial identity. The installer can
retry only the existing explicit pending response and only inside the original
shared finite 120-second budget. Exhaustion/failure/caller cancellation keeps
the original distinctions and pre-mutation no-change behavior. Healthy recent
startup does not itself authorize upgrade, rollback or backup mutation.

## Per-entry work and durable format

Replace repeated Data Protection operations with row MACs keyed by random secret
material protected in ledger metadata. Preserve nonce/key/digest/kind/generation
binding and the authenticated set commitment. Read legacy protected rows during
migration; new/rewritten rows use the authenticated MAC format. The offline
installer restore reader/writer must understand both formats and must re-sign
copied rows under the destination metadata key. This changes private ledger
compatibility and will be recorded in the image reader boundary and matrix.

Bulk-read/authenticate expected entries once per background pass; avoid a point
query/unprotect per file and repeated crypto passes over unchanged rows. Reuse
prepared stage inserts and much larger transaction batches. Required authority
remains FULL-synchronous; advisory parse-index failures remain best-effort. A
crash cannot promote incomplete staging into qualified authority. Reconcile
concurrent managed changes under the root barrier before publication, then check
fresh protected/config identity under the short store gate. Serialize full scrubs
separately from ordinary loads and preserve lock ordering/re-entrant managed writes.

Warm verification must open/authenticate the current ledger pathname, so deletion
or replacement cannot be hidden by the existing SQLite handle. Nested managed
deletion must recognize the already declared in-flight write it is pruning;
unexplained bytes must still refuse deletion/replacement.

## Requirement to behavior to planned evidence

| Requirement | Behavior | Evidence to collect |
| --- | --- | --- |
| M-4 / M-A / R1 | Bounded recent startup, background/incremental older qualification | Failing cold-load nonrecent-read regression on e7; baseline/candidate startup at production fan-out, fixed recent cohort with growing old history; real time-to-healthy; lifecycle health-wait campaign above old threshold. |
| M-4c / R6 | Bulk expected reads, row MACs, batched stage writes | Crypto/query/commit counters and timing; restart proof reuse; legacy migration; concurrent writers and no cap cliff. |
| M-5 / R2 | Current ledger pathname authenticated on each warm verification | Delete/replace/damage database after qualification; warm read refuses before mutation. |
| M-6 / R6 | Self-pruning nested write/delete respects authenticated intent | Saving an older focus record with maximumRecords=1 self-prunes and remains qualified; tampered in-flight bytes still refuse. |
| R2 / R3 | Fresh schema/location/config and bounded recent tamper check | Recent/old tamper, invalid/quarantine, config race, invalidation, no stale history publication; auth/token non-leakage. |
| R4 / R5 | Existing finite budget and cancellation/ordering | #1042 transport/timeouts; pending background survives caller cancel; upgrade/rollback/backup mismatch/failure/plan cases. |
| R7 | Clean proof reuse distinguished from crash/restore/pending | SIGKILL, dirty checkpoint, incomplete writes, key/row corruption, restart and real restore/rollback regressions; no silent adoption. |
| R8 | Explicit private-ledger version boundary | Old/new installer-agent matrix; pre-mutation read always executes on currently running agent. |
| L1 / L2 | One coordinator, no permanent-fault polling loop, no premature daily invalidation | Concurrent scheduled/request scrubs coalesce; persistent fault needs explicit trigger; lifecycle reads during unexpired refresh remain safe. |
| L3 | Operator-facing startup disclosure, Refs wording | Changelog/runbook and operations audit. |
| R9 | Exact candidate evidence and fresh normal merge | Short private TMPDIR, no node reuse, heavy lock, classifier-selected correction gates, warning-clean Release, affected suites/coverage/guards; fresh normal development/v1 merge immediately before final validation/push. |

LogicHost follow-up (notes only): use the same recent-window selection,
authenticated generation/checkpoint and host-owned coalescing pattern with a
LogicHost-specific indexed storage adapter. Do not couple either host to the
other or move SQLite/domain state into Storage.FileSystem. Unlimited retention
must affect background maintenance capacity, not host/agent startup scans.

Authorization remains branch implementation/validation/normal push and thread
handoff only. No PR, GitHub write, deployment, live agent/token/data, networking
change or CLIProxy restart. Kepler owns the new Sol/Opus HIGH reviews.
