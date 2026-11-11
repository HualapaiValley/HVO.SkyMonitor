# #1041 HIGH correction after da8fbbf7

Refs #1041. Status: implementation and local validation in progress. Kepler's
Opus HIGH review returned CHANGES REQUESTED. Sol reported the deleted-ledger
finding before provider disconnects; there is no Sol final verdict. The prior
owner completeness verdict is withdrawn. Kepler owns the new HIGH reviews;
this report does not grant PR readiness.

The original issue/discussion, Roy's Nov 10 Option A approval and the
[run-2 requirement map](issue-1041-run2-ledger.md) remain governing sources. This
round follows Kepler's supplied blocking checklist. The issue body and all 37
comments were refreshed read-only. Latest authorization permits implementation,
local tests and a normal branch push, with a handoff in this thread. It prohibits
GitHub comments, PR/review writes, deployment and production/beta requests.

The branch retains published history from `da8fbbf7a448f8a66a0f44d78d1afe0f1caec4f6`.
Upstream `558608be453c84382f4a2461c4149e65e0451b72` caused actual conflicts, so it
was merged in `58c885b49ca345a5dfd17f5b9d4cd6228e8dcff1`. Changelog entries are
preserved, category counts combine both deltas, and the new development-v1
canonical per-file coverage profile is retained. No history is rewritten.

## Blocking findings and behavior

| Finding | Correction | Required local evidence | Status |
| --- | --- | --- | --- |
| H1: registration gate/root barrier inversion | Keep the ambient, re-entrant registration lease until all managed mutation scopes finish. All writers acquire registration before root barriers. Nested operations find an existing root through the ambient parent chain; composite disposal releases all leases even on failure. | Independent flow A in a write scope deletes another tracked file while B starts a write; real ManualFocusSessionStore pruning at maximumRecords=1 with a competing writer; existing publication/staging/journal and registration tests. | Focused cases passed; final candidate gates pending |
| M1: first pre-ledger upgrade cannot recover | A paired snapshot explicitly recording ledger/checkpoint absence and authenticated unsealed history may restore that original absence. Only candidate-created ledger state is removed; retained files stay. A later new agent must fully validate/adopt the legacy corpus. Snapshots lacking paired evidence still refuse. | Failed-candidate lifecycle recovery before original restart; real legacy-authority rollback with and without pending candidate state; retained checksums and no qualified response before full adoption. | Focused store cases passed; CLI/final gates pending |
| M1: pending state prevents recovery | Authenticate both complete entry and pending-intent sets. Carry current file entries/intents plus snapshot journal entries/intents, preserving signatures. The derived overlay binds the file-entry and file-intent commitments. Pending outcomes remain unqualified until a restarted agent validates current bytes/schema. | Pending current, target and both; tampered pending set; torn declared output; cancellation/idempotent overlay tests. | Focused cases passed; final gates pending |
| Sol: deleted-ledger restore bypass | A target that contains an established ledger requires the current authenticated source to exist. Missing current ledger refuses before restore mutation, including when a prior overlay exists. It cannot silently copy/adopt the older snapshot. | Deleted source with and without overlay; protected history and retained sentinel unchanged. The old opaque missing-store positive fixture is corrected to a negative regression. | Focused cases passed; final gates pending |
| M2: every normal load full-scrubs | Ordinary loads freshly parse configuration and validate current protected history/checkpoint. Unchanged qualified loads do not scrub. Initial, changed, dirty or expired state still requires full validation. Daily/queued maintenance uses an explicit scrub API. | Ordinary configuration loads, authenticated continuity HTTP polls and protected reads without retained scans; explicit scrub still scans; actual config/location changes; renewed large-corpus endpoint/ordinary-load measurements. | Focused store case passed; endpoint/scale pending |

The H1 change serializes managed write scopes across registered roots in one
process. Writes within one root were already serialized. This deliberately
preserves registration-before-mutation ordering and initial-registration drain;
parallel throughput across distinct roots is not qualified. Required journal/
file state remains authoritative; no unverified bytes become completed proofs.

Legacy rollback is a version boundary: a pre-ledger runtime cannot maintain
candidate-era file proofs. The permitted fallback restores exactly the verified
pre-ledger authority/absence, preserves retained evidence, and requires full
adoption before a new runtime can qualify. Established ledger targets never use
that fallback. A complete paired backup remains the recovery route when a
snapshot lacks the necessary protected-state inventory.

## Original requirement crosswalk

| Requirement | Preserved behavior / interaction | Evidence for this candidate |
| --- | --- | --- |
| R1 bounded cost and current attribution | Qualified protected reads do not traverse retained history. Normal unchanged loads also avoid the scrub. Counts distinguish sidecars/captures/journal rows. | Renewed 100,001 and both 300,000-sidecar real-endpoint cohorts, warm/evicted, plus ordinary loader/continuity observations; pending |
| R2 integrity/schema/provenance | Managed writes validate outcomes; full scrubs detect out-of-band drift. No missing ledger or partial pending result grants trust. Startup/recovery tolerance remains. | Protected store negative cases, deleted-ledger/pending/torn restore cases, startup and full Acceptance suites; pending final gate |
| R3 current identity/readiness/concurrent edits | Exact current config snapshot and freshness callback; fresh protected authority/readiness, finite gate waits. No cross-request configuration identity cache. | Existing delayed/edit/concurrent endpoint cases plus ordinary-load regression; pending final gate |
| R4 transport/pre-mutation/#1042 budget | Unchanged finite 120s client budget, auth/proxy/redirect/field comparisons; verify before mutation, dry-run remains dry-run. | Full owner-client/CLI selection; upgrade/rollback/backup mismatch/cancel/timeout/plan cases and new first-upgrade failure; pending |
| R5 failure/cancellation/progress | Optional index remains best-effort, completed proof prefixes persist. Late committed location state updates memory before caller cancellation is observed. | Existing index fault/cancel tests plus L1 durable-marker cancellation/restart; pending final gate |
| R6 writers/concurrency/invalidation | Publication, retain/reconcile/quarantine/journal integration remains, with re-entrant global/root ordering. Daily expiry is not extended by writes. | New deterministic nested-write and real focus-prune cases; existing staging/registration/retention/concurrency cases; pending final gate |
| R7 durable/crash/restore/rollback | New process never inherits readiness. Restore authenticates source commitments, carries pending expectations and refuses deleted established state. | Real store restore matrix and lifecycle recovery; process-crash probe/current-image campaign as selected; pending |
| R8 compatibility | Success wire contract unchanged. Old installer/new agent retains qualified success; updated installer required for paired recovery. New installer/old agent retains the old server cost. Pre-mutation always calls the running agent. | Existing contract tests and compatibility discussion below; pending final gate |
| R9 HIGH evidence/profile | development-v1 Preflight + warning-clean Release/complete Unit/canonical per-file floors; classifier-selected affected local Integration/coverage and guards. | Exact range, commands, source fingerprints and counts in final local gate artifact; pending |

The only ordinary-runtime policy change remains Roy's accepted detection of
out-of-band retained-file changes at the next full scrub. Scrubs run at startup,
on relevant configuration/location or dirty/expired state, and daily. This round
removes the unintended scrub on every normal configuration load. Full scrubs and
startup remain linear; qualified reads are bounded with respect to retained
history. A server change cannot accelerate pre-upgrade verification on an
already-slow old running image.

## Low findings

| Finding | Disposition |
| --- | --- |
| L1 committed disk state / stale memory after cancellation | Fixed: update current in-memory authority after durable writes before observing late cancellation. New restart/marker-boundary regression; final result pending. |
| L2 drift and pending scrub share 503/7308 | Follow-up through Kepler: distinguish recovery-required corruption/drift from active qualification with sanitized diagnostics. Current finite client may exhaust its budget without that distinction; no bypass is added. |
| L3 publication amplification / flash wear | Follow-up through Kepler: sustained capture/flash qualification remains unavailable. Prior measured physical publication writes were 86–233 KiB versus roughly 4 KiB. New writer timings do not establish flash endurance or sustained throughput. |
| L4 drifted delete aborts retention sweep | Follow-up through Kepler: preserve refusal to delete drifted evidence, but continue other safe per-file retention work with bounded diagnostics. No evidence deletion/reset is used to recover. |
| L5 invalid quarantine blocks lifecycle verification | Kepler/Roy acknowledgement remains required: recovery/startup stays available, while malformed retained/quarantine evidence refuses strict lifecycle verification. A repair tool/policy is not silently introduced. |
| L6 cooldown uses system UTC instead of injected clock | Fixed: loader uses injected TimeProvider; fake-clock test checks the 29s/30s retry boundary and explicit background scrub path. Focused test passed; final gate pending. |

No Low follow-up issues or other GitHub objects were created in this round.
Kepler owns tracking and acknowledgement under the no-GitHub-writes boundary.
