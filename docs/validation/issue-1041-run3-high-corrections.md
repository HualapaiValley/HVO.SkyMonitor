# #1041 HIGH correction after da8fbbf7

> Historical evidence at the named run-3 heads remains valid as observations.
> Kepler's re-review found three blocking Medium interactions (ordinary load
> contention, repeated unqualified scrubs, and failed rollback recovery across the
> ledger boundary). The [run-4 correction report](issue-1041-run4-medium-corrections.md)
> supersedes the implementation-blocker disposition below. Sol and Opus both
> assessed original scope completeness; neither result waives those findings.

Refs #1041. Status: blocking corrections implemented and locally evidenced at `12e73a7a804e2d423d739919748e78dfcc57ced7`. New HIGH re-reviews remain with Kepler. Kepler's
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
| H1: registration gate/root barrier inversion | Keep the ambient, re-entrant registration lease until all managed mutation scopes finish. All writers acquire registration before root barriers. Nested operations find an existing root through the ambient parent chain; composite disposal releases all leases even on failure. | Independent flow A in a write scope deletes another tracked file while B starts a write; real ManualFocusSessionStore pruning at maximumRecords=1 with a competing writer; existing publication/staging/journal and registration tests. | PASS: fresh affected suites and focused regressions at the candidate; see local evidence below |
| M1: first pre-ledger upgrade cannot recover | A paired snapshot explicitly recording ledger/checkpoint absence and authenticated unsealed history may restore that original absence. Only candidate-created ledger state is removed; retained files stay. A later new agent must fully validate/adopt the legacy corpus. Snapshots lacking paired evidence still refuse. | Failed-candidate lifecycle recovery before original restart; real legacy-authority rollback with and without pending candidate state; retained checksums and no qualified response before full adoption. | PASS: real protected-state cases, full CLI suite and native lifecycle campaign |
| M1: pending state prevents recovery | Authenticate both complete entry and pending-intent sets. Carry current file entries/intents plus snapshot journal entries/intents, preserving signatures. The derived overlay binds the file-entry and file-intent commitments. Pending outcomes remain unqualified until a restarted agent validates current bytes/schema. | Pending current, target and both; tampered pending set; torn declared output; cancellation/idempotent overlay tests. | PASS: fresh focused and full affected suites; authenticated negative/recovery cases passed |
| Sol: deleted-ledger restore bypass | Database-only restore of an established target requires the current authenticated source to exist. Missing current ledger refuses before restore mutation, including when a prior overlay exists. It cannot silently copy/adopt the older snapshot. | Deleted source with and without overlay; protected history and retained sentinel unchanged. The old opaque missing-store positive fixture is corrected to a negative regression. | PASS: fresh focused and full affected suites; authenticated negative/recovery cases passed |
| M2: every normal load full-scrubs | Ordinary loads freshly parse configuration and validate current protected history/checkpoint. Unchanged qualified loads do not scrub. Initial, changed, dirty or expired state still requires full validation. Daily/queued maintenance uses an explicit scrub API. | Ordinary configuration loads, authenticated continuity HTTP polls and protected reads without retained scans; explicit scrub still scans; actual config/location changes; renewed large-corpus endpoint/ordinary-load measurements. | PASS: endpoint/ordinary-load tests plus all three scale cohorts; runtime source equivalence recorded |

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

## Complete-archive interaction found by the native campaign

The first current-image campaign exposed a second interaction of the deleted-ledger
refusal: full-backup extraction excludes SQLite files because they are carried
in its verified database snapshot. Snapshot-file verification must work before
those files exist in the replacement instance. Pure `VerifyAsync` therefore checks
the snapshot itself; database-only `PlanRestoreAsync` and `RestoreAsync` still
check the current authority before any database replacement.

Only the complete-archive staging path authenticates the snapshot ledger using
the archive's extracted keys and copies it, checksum-verified and synced, into
the disposable staged instance alongside the archive's retained files. The full
archive has already been verified twice before its staged tree is swapped in.
There is no missing-ledger adoption flag on database restore. Missing archive
keys refuse, and no staging step grants runtime qualification. A restarted agent
still performs a full scrub, and rejects retained bytes that differ from the
archived ledger. Interrupted staging is discarded/re-extracted; interrupted
swaps retain the durable staged ledger and resume through the usual restore plan.

The first-upgrade lifecycle case uses fake process control with real SQLite
snapshot/restore, supported by real-protector legacy cases. The native Docker
campaign exercises current-image backup/restore and migrating rollback; it is not
a live/pre-ledger image upgrade campaign.

New real-protector cases cover valid staging, absent archive keys, changed retained
bytes, and continued database-only refusal after deleting its current ledger.
The native campaign passed at the candidate after its first failure. The failed
attempt remains evidence and is not counted as a pass.

## Original requirement crosswalk

| Requirement and governing source | Behavior | Local evidence / result |
| --- | --- | --- |
| R1 — #1041 bounded verification; #1559/#1591/#1618 attribution | Qualified reads validate current configuration/history/checkpoint without visiting retained files or journal rows. Ordinary qualified loads also avoid scrubs. Sidecars and captures are counted separately. | Real authenticated endpoint: 100,001 and both 300,000-sidecar cohorts, warm/evicted, zero retained scans; numeric table below. Source-equivalent runtime at candidate; PASS. |
| R2 — integrity/schema/provenance; Roy's approved Option A | Every managed mutation maintains expectations. Full scrubs reject out-of-band drift/schema/corruption; missing authority never qualifies. Startup/recovery tolerates malformed/quarantined sidecars. | Full CameraAgent suite, explicit scrub/corruption/startup/production-layout cases; authenticated restore/pending/torn/missing-key cases; 33 Acceptance cases; PASS. OOB detection timing is the explicitly accepted tradeoff. |
| R3 — fresh identity/readiness; concurrent edits | Fresh config parse/schedule and snapshot digest, fresh authority/readiness and finite gate waits. No cross-request config identity cache. | 7 endpoint cases, current-config race probe (actual mid-load edit rejected; snapshot hash current), location-edit/late-cancel/concurrent-reader tests; PASS. |
| R4 — #1042 transport/budget; pre-mutation comparison | Same finite shared 120s budget, timeout vs caller cancellation, sanitized auth/proxy/redirect refusals, exact field comparison before mutation. Plans remain dry runs. | Full CLI 3,192 cases plus 70 focused cases, upgrade/rollback/backup mismatch/timeout/cancel/plan cases, real native installer campaign; PASS. |
| R5 — cancellation, failure, recovery | Completed optional proof prefixes survive cancellation; optional index writes remain best-effort; committed in-memory location updates precede late cancellation. | Index fault/mid-scan cancellation tests, new durable-marker cancellation/restart case, dirty endpoint/background retry cases; PASS. |
| R6 — every writer, invalidation, concurrency | Publication, retention, reconciliation, quarantine, journals and restore preserve ledger expectations. Registration/root locks have consistent order and nested re-entry. Writes cannot extend daily expiry. | Deterministic A-nested-delete/B-write test, real ManualFocus maximumRecords=1 pruning, existing staging/registration/retention/read/write/expiry tests; fresh 8,169-case CameraAgent selection PASS (one existing platform skip). |
| R7 — crash, rollback and restore fail safe | Restart never inherits qualification. Authenticate complete/pending sets, preserve current file and snapshot journal expectations. Explicit legacy absence permits first-upgrade rollback; deleted established authority refuses database-only restore. Only verified complete archives stage their own authenticated ledger. | 14-case real restore matrix, two real legacy cases, failed-first-upgrade lifecycle test, three real-protector full-archive cases; SIGKILL before/after/torn publication probe; native full-backup restore/upgrade/rollback campaign; PASS. |
| R8 — compatibility | Success wire fields/auth unchanged. Pre-mutation verification always calls the running agent. Updated installer is needed for paired ledger restore. | Current endpoint/client/lifecycle contracts and matrix below; PASS within the documented version boundary. No old slow-runtime bypass. |
| R9 — HIGH evidence, canonical coverage, CI controls | development-v1 target profile; exact classifiers; warning-clean Release, full Unit, affected canonical coverage, guards and required local lifecycle/scale evidence. | 15,895-case fresh v1 selection; canonical source-equivalent rollup, 1,262 Integration cases, 232 focus cases; all coverage/guards/publishes/native campaign PASS. New Sol/Opus HIGH reviews pending with Kepler. |

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
| L1 committed disk state / stale memory after cancellation | Fixed: update current in-memory authority after durable writes before observing late cancellation. New restart/marker-boundary regression; PASS in fresh focused and full CameraAgent suites. |
| L2 drift and pending scrub share 503/7308 | Follow-up through Kepler: distinguish recovery-required corruption/drift from active qualification with sanitized diagnostics. Current finite client may exhaust its budget without that distinction; no bypass is added. |
| L3 publication amplification / flash wear | Follow-up through Kepler: sustained capture/flash qualification remains unavailable. Prior measured physical publication writes were 86–233 KiB versus roughly 4 KiB. New writer timings do not establish flash endurance or sustained throughput. |
| L4 drifted delete aborts retention sweep | Follow-up through Kepler: preserve refusal to delete drifted evidence, but continue other safe per-file retention work with bounded diagnostics. No evidence deletion/reset is used to recover. |
| L5 invalid quarantine blocks lifecycle verification | Kepler/Roy acknowledgement remains required: recovery/startup stays available, while malformed retained/quarantine evidence refuses strict lifecycle verification. A repair tool/policy is not silently introduced. |
| L6 cooldown uses system UTC instead of injected clock | Fixed: loader uses injected TimeProvider; fake-clock test checks the 29s/30s retry boundary and explicit background scrub path. PASS in fresh focused and full CameraAgent suites. |

No Low follow-up issues or other GitHub objects were created in this round.
Kepler owns tracking and acknowledgement under the no-GitHub-writes boundary.

## Evidence identity, sources and validation

The delivery head may include an evidence-only commit after the implementation
candidate below. Compare its `src`, `tests`, `scripts`, `tools`, workflow and root
build-input tree/blob IDs with the ledger; source equality preserves the recorded
behavioral evidence. Final exact-range classification and lightweight controls are
rerun on that delivery head before push.

The implementation candidate is `12e73a7a804e2d423d739919748e78dfcc57ced7`. The full initial evidence was collected
at `dafbb60fb4a6f768241f77c7ccae3470e5248739`. The archive correction's classifier selects
CameraAgent and Delivery (`complete=false`); those canonical Unit selections,
full v1 Unit collection, focused suites, controls, Release, formatting, coverage,
publishes and native campaign were rerun. The [local ledger](issue-1041-run3-local-gate.json)
records both attempts, exact commands, TRX counts, focused method outcomes,
coverage bindings, source trees and log hashes. Successful prior scopes are
reused only with recorded source equivalence. The 32 runtime/harness/build trees
or blobs for endpoint scale and real process probes are unchanged; measured
revision fields remain the original values. Rebuild informational revisions and
process epochs differ, so startup still has to qualify each new process.

The original issue body and all 37 comments were refreshed read-only at
2026-11-11T12:58:20.702050+00:00; all fingerprints
matched the earlier complete source read. Governing attribution reports remain
[#1559](issue-1559-protected-read-attribution.md),
[#1591](issue-1591-schedule-validation-reuse.md) and
[#1618](issue-1618-retained-read-attribution.md). Roy's Nov 10 Option A approval
and Kepler's supplied da8fbbf7 correction checklist govern this round. The signed
incident figures are context, not measurements from a live agent.

Fresh candidate Release build: zero warnings/errors. Full format, the four
CI-control guards, both new v1 coverage controls, documentation/category audits,
secret-scan contract and sandbox prerequisites pass. Fresh v1 Unit: **15,892
passed, three existing skips, zero failed**. Fresh affected canonical Unit:
CameraAgent **8,168 passed / one skip**, CLI **3,191 passed / one skip**. Current
canonical rollup combines those with unchanged source-equivalent slots. Initial
all-application/Architecture Integration: **1,262/1,262 passed**; candidate focus:
**232/232 passed**. All five component coverage gates, the canonical aggregate,
and 27 v1 per-file floors pass without changing floors. Canonical aggregate is
**89.84% line / 75.91% branch**. Host/component and x64/ARM64 publish contracts,
migration/model checks and native installer campaign pass in their recorded
scopes. No full CI workflow was dispatched; this is local v1 integration evidence.

The initial canonical-as-v1 collection failed the new gate's exact assembly
inventory; generated v1 settings and a fresh 21-project collection corrected the
evidence setup. The first native campaign exposed the complete-archive regression
above, which was fixed and rerun. Earlier inner-loop failures were fixture clock
and owner-readiness setup issues, plus narrow analyzer fixes; the logs/attempts
are retained. None of these failed attempts is counted as a pass.

## Current endpoint and ordinary-read scale

[Sanitized samples](issue-1041-run3-scale.json) retain runtime, CPU, allocations,
logical/physical I/O, checksums, proof counts and sampled peak memory. Each cohort
uses synthetic metadata sidecars and raw journal rows constructed by
`SeedScaleAsync`, with installer-generated owner configuration and a completed
synthetic owner. Pixel payloads are not fabricated/read for this metadata path.
The real authenticated endpoint runs through the host TestServer. Three warm and
three evicted samples per cohort use one affined CPU and a 1 GiB managed-heap
limit. Eviction is fixture-only fsync/POSIX_FADV_DONTNEED with mincore proving zero
resident file pages; transient SQLite SHM is excluded. Inode/controller caches and
full-machine cold start are not controlled. Construction, all-file checksum checks,
cache eviction and full qualification/restart are outside the endpoint timers.

| Captures | Sidecars | Products/capture | Protected warm median ms | Evicted median ms | Normal load median ms | Continuity HTTP median ms |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100,001 | 100,001 | 1 | 37.82 | 43.29 | 20.57 | 26.12 |
| 300,000 | 300,000 | 1 | 19.42 | 26.32 | 19.87 | 27.48 |
| 100,000 | 300,000 | 3 | 18.43 | 20.86 | 18.91 | 21.65 |

All qualified endpoint samples and all ordinary/continuity observations have
**zero retained-scan spans**; warm retained parsing is zero beyond the former
262,144-proof cap. Retained checksums and compared identity hashes stay unchanged.
Ordinary loader/continuity work avoids the undisclosed repeated full scrub from
da8fbbf7; an explicit scrub still scans. Current configuration/history size still
controls its own fresh validation work, independently of retained capture count.

| Captures / sidecars | Warm allocation MiB | Warm CPU median ms | Warm logical read bytes | Initial full scrub s | Restart qualification s | Maximum sampled process peak MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 100,001 / 100,001 | 7.92 | 33.49 | 38,986 | 33.78 | 26.93 | 325.04 |
| 300,000 / 300,000 | 7.53 | 17.81 | 40,072 | 102.48 | 87.17 | 440.29 |
| 100,000 / 300,000 | 7.76 | 15.99 | 41,122 | 84.61 | 93.47 | 534.35 |

Full scrubs remain linear and can exceed 120 seconds on slower storage. A client
deadline does not cancel the host-owned refresh; incomplete work cannot qualify.
The maximum sampled peak is 534.35 MiB, below the fixture heap/product 4 GiB
limits; this is not a production working-set or sustained-capture qualification.

The named prior linear baseline (`0dc79eb5`) has warm/evicted medians of
3,203.62/6,146.69 ms, 9,337.26/18,922.44 ms and 6,824.69/15,257.04 ms for the
same three corpora. These remain **prior-run observations**, not a new baseline
run. Manifest checksums and journal lengths match; the 13 retained/configuration/
endpoint/read-client baseline source blobs are unchanged at fetched development
`558608be`. Neither the historical 35,049 ms nor small-fixture ~180 ms is used
as a current result.

## Compatibility and remaining limits

| Installer | Running agent | Behavior |
| --- | --- | --- |
| Old | New | Same qualified success fields/auth/comparisons. Old clients do not use the new explicit pending retry; they may need another invocation. Paired ledger rollback/restore needs the updated installer. |
| New | Old | Same finite 120s budget and field comparisons. The old server still performs its old full scan and may time out. |
| New | New | Qualified reads are bounded by current config/history/checkpoint work. Pending responses retry only within the same original finite budget. No identity or mutation bypass. |
| Updated installer recovering a failed first ledger upgrade | Pre-ledger target | Explicit paired absence and authenticated unsealed history allow removal of candidate-only ledger state, with retained files preserved. A later new runtime fully verifies/adopts before qualifying. |

Pre-mutation verification always executes on the **currently running agent**.
A server-only change cannot unblock pre-upgrade verification on an already-slow
old image. The new empty instance is an adoption window; no live deployment was
performed. Upgrade incompatibility is disclosed under the dev policy.

The only accepted detection weakening is Roy's out-of-band tamper timing: next
full scrub rather than next qualified read. Startup, relevant config/location
changes, dirty/expired state and daily maintenance trigger full verification;
normal unchanged qualified config/rig-sync/continuity loads do not. Managed
writer validation, schema/location proofs, identity freshness and client ordering
remain intact. The design already addressed configuration snapshot and ledger
pathname-replacement races as recorded in run 2; this round preserves that
behavior and reruns the actual config-edit probe.

Cross-root write scopes now serialize through one registration lease. Sustained
multi-root throughput/flash endurance remains unqualified. The five auxiliary
publication samples/cohort show roughly 84 KiB physical writes and 10–13 ms median
latency; this small burst does not supersede the review's 86–233 KiB product-write
range or qualify sustained capture. L2/L3/L4/L5 follow-up text above goes to
Kepler for tracking/acknowledgement. No evidence deletion/reset is used as a
recovery procedure. Invalid quarantine evidence can still refuse lifecycle
verification while startup/recovery remains available.

Owner implementation-blocker disposition: **resolved in the declared local
software scope**. New exact-head Sol and Opus HIGH completeness/review verdicts
remain with Kepler. No PR, issue comment, review/status, enqueue, merge, deployment
or live-instance action was performed; the final delivery commit adds documentation/evidence only, with product/test/build tree equality checked before the authorized branch push.
