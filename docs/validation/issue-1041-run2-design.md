# #1041 correction design — Option A approved

Roy approved Option A at 2:04 PM PST / 22:04 UTC on Nov 10, 2026, widget
t356s2122, relayed by Kepler in this coding thread. Only out-of-band retained-file
tamper detection moves to the next full scrub. The writer-maintained ledger,
including all writer/recovery coverage, is now required implementation work.
The initial decision note below is retained as the record Roy approved. Its
pre-approval/pending wording is historical and superseded by this paragraph and
the implementation refinement. Current behavior/evidence is recorded in
[the run-2 ledger report](issue-1041-run2-ledger.md).

2026-11-10, correction to reviewed head `c4069b8c`. Sol and Opus returned
INCOMPLETE/FINDINGS. Their supplied results supersede the earlier owner
completeness verdict: **IMPLEMENTATION REQUIRED**, and the endpoint's bounded-cost
goal remains **unresolved**. Kepler supplied the combined findings; the Sol report
on hvo-dev-04 is unavailable here and is not claimed as personally inspected.
No PR, deployment or review dispatch is authorized.

## Integrity boundary for Roy's decision

The existing policy reads every retained manifest's current bytes on each
protected request. Reusing a previous parse result after hashing those bytes
preserves that policy, but cannot make filesystem traversal, reads or hashing
independent of retained history. The review's evicted-cache results (100,001:
11,210→9,535 ms; 300,000: 34,595→29,513 ms) demonstrate the remaining problem.
Those are reviewer measurements, not this correction's measurements. Persisting
validated parse progress helps repeated cancellation, but still cannot fix a
request whose mandatory byte reads alone exceed 120 seconds.

**Proposed bounded endpoint, not yet approved or implemented:** maintain an
integrity ledger during every managed manifest publication, journal transaction,
retention deletion, reconciliation/quarantine and restore. Record pending changes
before publication, atomically commit validated digests/provenance afterwards,
and expose a verified generation/root plus dirty/recovery state. The endpoint
freshly reads current configuration, schedule, protected location/history,
installation identity and owner readiness, and reads a bounded ledger checkpoint.
Pending, inconsistent, corrupt or unqualified state refuses verification.

**Guarantee changed:** an out-of-band edit to a retained file after its last
verified checkpoint would be detected by a subsequent full scrub, rather than
necessarily by the next protected read. A file watcher/mtime is not equivalent
to immediate byte verification; watcher overflow or loss must dirty the ledger.
A persisted root alone cannot prove that mutable external files still match it.
Roy must explicitly accept this detection delay/trust in managed writers, choose
a scrub/freshness policy and supported adoption inputs, or retain full current-byte
verification and its linear I/O. No such weakening is inferred from this task.

Bootstrap/new validator builds and unclean recovery require a resumable full
qualification before a ledger generation is usable. Rollback binds proofs to
its own validator/runtime and protected-history identity; an incompatible ledger
rebuilds. Backup/restore must pair retained stores and ledger generation, mark
unpaired/incomplete restores dirty and requalify before lifecycle mutation.
No corruption, missing checkpoint, cancellation or incomplete write permits a
successful identity response. The pre-mutation read still executes on the
currently running agent: a new server cannot accelerate an already-slow old one.
The existing finite 120-second client policy and transport protections stay.

## Corrections authorized now, without changing byte-verification policy

| Finding/requirement | Planned behavior | Behavioral evidence |
| --- | --- | --- |
| HIGH-1: repeated canceled work | Persist individually complete, authenticated content proofs in small transactions, including the completed prefix on cancellation/failure. A prefix is never a whole-corpus checkpoint; every request still reads/hashes all current bytes. | Repeated short-deadline requests accumulate proofs across restart; edited prefix bytes are rejected. Report remaining linear cost, never mark the incident loop solved. |
| HIGH-1: proof overflow | Replace the fixed whole-file set with an advisory SQLite proof index: authenticated identity/digest/type per row, bounded batches/memory, no 262,144-entry cliff. No authoritative journal migration. | More than 300k distinct digests, repeat/restart with no overflow parsing; missing/corrupt index rebuild; current corruption still rejected. |
| HIGH-1: auxiliary JSON | Classify valid non-artifact JSON once, cache only that classification under exact bytes and validation identity, and never use it to satisfy a required artifact contract. | Auxiliary models, replacing a frame with auxiliary bytes, and cache hits show correct classification without double parsing. |
| HIGH-2: startup recovery | Separate startup load from strict installation verification. Startup restores the baseline tolerance for malformed/schema-invalid sidecars so reconcilers and recovery UI can run; valid retained provenance and protected authority checks remain. Verification still refuses current invalid retained evidence. | Torn quarantine JSON, invalid v2 quarantine JSON, torn production `frames/.../Raw` sidecar, reconciler recovery and real startup/UI/verification interaction. |
| MEDIUM-1 and stale in-memory state | Optional proof writes/cleanup are best-effort, with sanitized bounded logging. Publish successfully durable location authority before optional cache work; never let cache failure leave memory at v1 while disk is v2. | Disk-full/write failure on unchanged load and real location change, startup succeeds; memory/disk agree; later verification still hashes current bytes. |
| Lows: abandoned state/docs/layout | Bounded cleanup of advisory temporary files; use path segments rather than a hard-coded separator. Correct report/changelog/runbook and fixture counts. Linux remains the only product platform. | Cleanup confinement/failure/cancellation, production paths and updated operator documentation. |

The SQLite index is disposable acceleration, not installation identity. Each
row authenticates proof kind, current validator/runtime/history identity and the
exact digest; invalid/corrupt rows are misses. Commit only fully validated rows;
a failed or canceled scan cannot certify the corpus. Index write failures log
without blocking authoritative load or suppressing verification errors. Existing
atomic authority writes and their durability gates are unchanged. An interrupted
index transaction rolls back, a committed prefix survives, and lost index/key
state causes revalidation. Retained evidence is never deleted/reset for recovery.

## Measurement and handoff

Verify actual pipeline product multiplicity and report captures, sidecars and
journal rows separately. The previous fixture had one sidecar per capture plus
one duplicate journal manifest, not production fan-out. Measure the authenticated
real endpoint at 100,001 and 300,000 distinct manifest sidecars, warm and
fixture-only cache-evicted, first/repeat/cancellation/restart. Include a production
layout/fan-out cohort. Use a private short TMPDIR and the heavy lock, never
system-wide cache dropping or production data. Run focused correction and
classifier-selected affected gates, canonical coverage and all four CI guards.

Handoff will retain HIGH-1 as unresolved pending Roy's integrity-policy decision;
these safe corrections do not claim capture-count-independent verification.
Kepler owns further HIGH reviews and PR lifecycle. Refs #1041.


## Approved implementation refinement (before ledger code)

Use an authenticated expected-entry ledger and a small protected checkpoint.
The checkpoint binds a fresh process epoch, exact validator/runtime and current
protected-history identity, mutation generation, count and authenticated set commitment.
**A new process never trusts a persisted checkpoint without a full scrub.** This
conservatively covers crash, rollback and offline restore, even if a backup copied
an interrupted ledger. The independent parse index remains optional acceleration.

Managed file operations use root-scoped mutation guards before publication,
replacement, deletion, tree move/quarantine and retention; raw journal insertion/
recovery and schema migration have matching guards. Persist authenticated pending
keys/prefixes before changing evidence. On completion, validate affected current
bytes and atomically update expected entries and the checkpoint. Unknown/failed
updates leave verification unqualified. Only changed files/rows are processed;
no capture-count scan is hidden in a publication or protected-read fast path.

Full scrubs validate current file/journal bytes, schema/provenance and the
expected-entry ledger; unauthorized drift is detected. Authenticated incomplete
writer intents are recovered by full validation. A scrub records observed update
generations and merges/revalidates concurrent managed changes before committing,
with only short per-entry/finalization mutation barriers. No half-scrub is a
checkpoint. Cancellation retains individually completed parse proofs but cannot
qualify integrity state. Protected reads refuse unqualified state promptly and
coalesce independent revalidation work, so caller timeouts do not restart it.

Scrubs run at startup before trusting the new process, after dirty/unknown state,
crash/rollback/restore, and every 24 hours while running. The periodic job and
recovery job are cancelable on host shutdown. A periodic scrub's failure keeps
verification unqualified; the recovery UI/startup tolerance is preserved. A
fresh process may adopt a missing/incompatible ledger only by complete original
contract/history verification, never by trusting cached metadata. Authenticated
expected digests distinguish drift from registered managed updates.

Current configuration, owner readiness, rig/schedule/catalog comparisons and
protected history remain fresh on every protected read. Background revalidation
requires a config-publication freshness check, and the protected response uses
the same JSON snapshot as the loader. This deliberately addresses the previously
excluded config-file reopen race, because asynchronous requalification must not
freeze a stale configuration as installer identity. No other integrity or
lifecycle guarantee is relaxed.
