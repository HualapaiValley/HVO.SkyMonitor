# #1041 run-6 correction design

Refs #1041. Governing sources are the original R1–R9 crosswalk and attribution
reports, Roy's Option A decision, t388u/t389u and t410u, and Kepler's r3 findings
at 711fa7b9. No PR, issue comment or reviewer-branch edit is authorized.

Round-4 erratum: full restore qualification was a requirement, but run 6 enforced
it only for DB-only restore. Complete-archive staging preserved a clean seal (F2).
Also, its incremental pass could clear an observed recent failure (F1). The
[round-4 design](issue-1041-run7-design.md) specifies the missing invalidations;
the original plan below is not evidence those cases were implemented.

| Finding | Planned behavior | Affected evidence |
| --- | --- | --- |
| M-4/M-A(c) | Authenticate the clean, unexpired prior checkpoint and the complete expected-row commitment before incremental qualification. Compare authenticated per-file size/mtime/generation/content identity; read actual bytes only for missing/changed/unaccounted metadata. Journal projection compares row checksum/length to authenticated expectations before fetching changed manifests. Periodic/explicit or dirty/crash qualification reads all actual bytes. DB-only restore invalidates generation; complete-archive invalidation was missing in run 6 and is corrected in round 4. Keep the original full-scrub expiry across incremental starts. | Two successive unchanged restarts read zero older files/journal manifests; changed older file refuses incremental qualification; full scrub detects same-size/time-preserving tamper; explicit proof-version invalidation. |
| M-7 | After the existing confinement and required-authority checks, let SQLite recover the existing hot journal using a read-write connection, then perform unchanged layout and authentication checks before schema use. | Real hot rollback-journal fixture refuses a read-only open, recovers on next agent load, then passes a fresh full scrub; malformed layouts remain refused. |
| Initializer M1 | Treat deferred configuration initialization separately from scrub failure: unhealthy capture readiness and shared sanitized event 7312, then at most five attempts with 1/2/4/8-second delays; success clears only this service's failure. Persistent failure stays unhealthy. | Fail-once initializer exposes unhealthy HTTP health and recovers after the bounded retry; cancellation and permanent-failure cases. |
| Low L1 | Keep the recovery latch stable across normal capture writes; authority/config edits and explicit/scheduled maintenance remain retry triggers. Surface recovery-required before scheduling a pending response. | Real store with ongoing managed writes does not rescan or change terminal refusal. |
| Low L2 | Recent admission may recognize an authenticated pending intent only when its declared digest matches the bytes; unresolved/denied intent never grants whole-corpus trust. | Matching crash-publication intent preserves recent readiness; different bytes refuse. |
| Low L3/L4/headroom | Incremental qualification removes old byte reads while retaining pending lifecycle semantics; annotate the earlier two stale UI-wording failures; document 400k recent-entry capacity (~22% spare at 24 files/frame). | Run-6 handoff and existing startup/timeout evidence. |

Use the existing optional authenticated validation index for file-stat hints; its
proof binds the protected history/explicit validation-contract version, expected
entry digest/kind/generation and relative key. An absent, invalid or unwritable
hint only causes actual-byte verification. It never replaces the authoritative
ledger. Full rollback/restore qualification is required; run 6 enforced it on
DB-only restore, while round 4 adds the missing archive checkpoint revocation.
A new process
marks its checkpoint unqualified before background work, so an interrupted pass
cannot confer trust on the following restart. No installer wire/private-ledger
format change is needed. Replace build MVIDs with an explicit semantic validation
version; bump that version when validation semantics/proof format change.

The accepted Option A detection timing remains explicit: metadata-preserving
out-of-band file edits and journal edits that preserve declared checksum/length
are detected by the next mandatory periodic/explicit full scrub. Ordinary changed
file metadata is checked on restart. Fresh config/location/owner identity,
authentication, comparisons, finite 120-second budget and cancellation distinctions
remain unchanged. Incremental metadata discovery is still background O(history)
work; it avoids retained-byte reads/parses and does not extend scrub expiry.

Only focused affected selections run, under a new private short TMPDIR and the
heavy lock. Retain earlier unrelated successes. Merge development/v1 normally
before push; Kepler alone starts the r4 Sol/Opus HIGH reviews.

A qualified checkpoint is incrementally reusable only after an orderly, drained
shutdown seals it clean. Running checkpoints remain marked unclean. A detected
SQLite rollback/WAL journal also forces full verification after recovery, even
if rollback restores matching authority. DB-only snapshot restore advances the
ledger generation. Complete-archive staging did not revoke the clean checkpoint
in run 6; round 4 discards that checkpoint before ledger publication. Both paths
now require full byte qualification, checked through ordinary startup/coordinator
and protected verification calls.
