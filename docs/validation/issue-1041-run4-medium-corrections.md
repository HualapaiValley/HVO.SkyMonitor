# #1041 correction after b3423622

Refs #1041. The three returned Mediums are corrected and locally evidenced. Kepler supplied Sol
7393d8b2 (COMPLETE, Lows only) and Opus 27a3f0ec (COMPLETE, CHANGES REQUESTED,
three Medium findings). The frozen implementation candidate is
`83422a0234f752bab08838e9aa4fbac3accd5dcf`. No local CLEAN claim is made. Kepler owns re-review and
PR creation; this coding thread makes no GitHub writes except its authorized
normal branch push.

The original [R1–R9 crosswalk](issue-1041-run3-high-corrections.md), original issue
and discussion, Roy's Option A approval and #1559/#1591/#1618 attribution remain
governing. The only approved retained-file detection tradeoff remains out-of-band
tampering at the next full scrub. Current identity/readiness, managed mutation
proofs, schema/location checks, pre-mutation comparisons and #1042's shared finite
120-second verification budget remain required.

| Finding | Behavior | Baseline proof / candidate evidence |
| --- | --- | --- |
| M-1 | Ordinary configuration loads wait cancellably for the store gate and managed-write barrier. Protected verification retains its finite local waits and refusal semantics. | Both >1s contention cases fail on unchanged b3423622. Fixed contention/caller-cancellation cases and real continuity HTTP regression pass; full CameraAgent Unit and Acceptance Integration pass. |
| M-2 | Unchanged ordinary loads validate fresh protected state without certifying a dirty ledger. They request the host-owned coalesced scrub. Cooldown starts at completion, including a failed scrub longer than 30s; a cold startup is not immediately repeated. Startup/actual identity changes still fully reconcile. | Four b3423622 cases repeat scans for invalid JSON, drift, missing and damaged ledger. Fixed cases do zero inline scans, still refuse protected verification; host queue/completion-clock regressions and full CameraAgent Unit pass. |
| M-3 | Only an active rollback operation may recover its own authenticated S1 ledger after verifying its paired pre-ledger S0 and known protected-file transition. Generic missing-ledger restore still refuses. Current unchecked bytes never become expected proofs. New runtime must fully scrub. | Two committed-upgrade/failed-old-image cases fail on b3423622. Recovery, interrupted recovery/resume and unexpected-protected-edit refusal pass in the 77-case CLI focus and complete 3,199-case CLI Unit selection. |
| L-b/L-c/L-d | Test sealed/non-object/unauthenticated/missing-key legacy refusal; document protected-history rewind; bounded config snapshot re-read on concurrent edit. | Negative legacy and bounded retry tests pass. L-a registration lease race and L4 retention continuation remain Kepler follow-ups. |

M-3 restores the stopped S1 expectations, not a newly adopted corpus. Protected
files must hash to their recorded S1 or S0 values, allowing interrupted paired
writes but refusing unrelated edits. Copy stages on the state filesystem,
verifies the immutable snapshot checksum, removes stale SQLite recovery files,
and syncs the rename. A crash before completion remains an active operation;
resume restores S1 before restarting it. Real retained changes remain subject to
full scrub; no restored checkpoint grants cross-process qualification.

Normal configuration availability does not mean installer readiness. The ordinary
path still freshly parses config, protected history/marker/manual state and checks
the current snapshot. A dirty ledger keeps installation verification refused.
Busy ordinary reads can wait as the baseline did, with caller cancellation;
the finite protected-read deadline is not raised or reused as an ordinary-load
exception. Continuously changing config has a bounded three-attempt re-read and
cannot become immutable installer identity.

Baseline regressions were built warning-clean against b3423622 with no product
source diff: six CameraAgent cases and two lifecycle cases failed as expected.
Their TRX/source hashes and commands are retained in local run-4 evidence.
Initial test setup compile errors were fixed before those behavioral runs.

## Original requirement crosswalk

This table carries the original scope forward; the correction checklist does not
replace it. Results name the frozen implementation candidate above. The final
delivery commit also contains the evidence and updated standalone probe harness;
its product/test/build trees must match the recorded candidate trees.

| Requirement / original source | Required behavior | Local behavioral evidence | Result |
| --- | --- | --- | --- |
| R1 — #1041 repeated work / realistic scale | Qualified protected reads inspect current config, history and a bounded authenticated checkpoint without enumerating retained files or journal rows. Count sidecars and captures separately. | Real authenticated owner-settings endpoint: 100,001 sidecars/captures; 300,000 sidecars/captures; 300,000 sidecars for 100,000 captures/three products. Warm and fixture-page-evicted samples, scan spans, CPU, allocations, I/O, checksums and persisted proof counts. | PASS: all three cohorts; every qualified sample has zero retained scans/parses, unchanged identity and elapsed time below 590ms. Historical linear baseline stays explicitly historical. |
| R2 — integrity / Roy's Option A | All in-band publications and full scrubs validate bytes/schema/location. Out-of-band tampering is detected at the next full scrub, the only approved timing change. Missing/torn/drifted required authority never qualifies. Startup/recovery tolerates invalid/quarantined sidecars without deleting evidence. | CameraAgent retained-integrity/startup/production-layout tests; InstallationVerification acceptance; explicit repaired-data requalification; restore refusal cases. | PASS in fresh complete CameraAgent/CLI Unit, 548 storage Integration and 34 Acceptance Integration cases. |
| R3 — current installation identity / concurrent edits | Fresh config/schedule, owner readiness and protected history/marker/manual state on each protected read. Ordinary availability cannot grant installer identity. Retry a changed config snapshot at most three times. | Current-config process probe; actual manual/config edits and concurrent endpoint reads; bounded retry tests; strict refusal during unqualified ordinary loads. | PASS: 8 endpoint focus cases, full affected suites and real-file process probe. |
| R4 — #1042 / lifecycle ordering | Keep one finite shared 120s client budget, timeout versus caller-cancel distinction, sanitized authentication/proxy/redirect handling and exact comparisons before mutation. Plans remain dry runs. | Complete CLI Unit plus focused OwnerBootstrapClient and pre-mutation upgrade/rollback/backup mismatch, failure, timeout, cancellation and plan contracts. | PASS: CLI 3,198 passed / one existing skip; 77/77 focus. Client implementation unchanged by this correction. |
| R5 — failure / cancellation / optional index | Fully validated parse-index progress survives interrupted scans; optional writes remain best-effort. Ordinary gate waits honor caller cancellation. Committed authority updates memory before late cancellation. | CameraAgent optional-index/cancel/durable-state cases and new ordinary gate/root-barrier cancellation tests; endpoint host-scrub survival after caller cancellation. | PASS in full CameraAgent Unit and Acceptance Integration. |
| R6 — all writers / invalidation / concurrency | Publish, retain, reconcile, restore and quarantine preserve authenticated expected state; nested managed operations avoid lock inversion. Ordinary loads coalesce dirty work with completion-based cooldown. | Deterministic A-nested-delete/B-write and maximumRecords=1 ManualFocus regressions, journal/staging/retention/registration tests; blocked scrub/managed writer; four persistent-unqualified cases; fake-clock host coalescing and continuity HTTP. | PASS: 166/166 focused CameraAgent cases, full affected Unit/Integration. Six regression rows failed on b3423622 as expected. |
| R7 — durable crash / rollback / restore | Each restart fully verifies; authenticated pending outcomes and paired snapshots never grant unchecked trust. Generic deleted-ledger restore refuses. Only the active operation can recover its stopped authenticated S1 after its known pre-ledger S0 transition. | SIGKILL before/after/torn publication; pending/restore/full-archive/legacy cases; failed-first-upgrade; new committed-upgrade → failed-old-image rollback and recovery interruption/resume; unrelated protected edit refuses. | PASS: eight real-process crash/recovery states and full affected suites. Two new lifecycle rows failed on b3423622 as expected. |
| R8 — compatibility | Same success wire/auth/field comparisons; new paired restore needs the updated installer. Pre-mutation verification always executes on the running agent. | Endpoint/client contracts, first-upgrade and failed-rollback lifecycle tests; compatibility matrix below. | PASS in documented boundary; no old slow-runtime bypass. |
| R9 — HIGH / validation / integration | Normal merge current development/v1, exact-range classifier, warning-clean Release, requested affected suites/coverage and controls, immutable evidence, Kepler HIGH re-reviews. | Normal merge 2897df1d contains fetched 5f5d253d; conflict inventories summed as ours + theirs − base. Classifier selects CameraAgent/Combined/Delivery, complete=false. v1 Preflight + Build and Unit target profile; no workflow dispatch. | Local gates PASS, including 15,944-case v1 selection, all 27 per-file floors and three selected component coverage gates. Sol/Opus re-reviews remain with Kepler. |

## Compatibility and integrity boundary

| Installer | Running agent | Behavior |
| --- | --- | --- |
| Old | New | Same qualified success fields, authentication and comparisons. Old client has no explicit pending retry and may need another invocation. Paired ledger restore requires the updated installer. |
| New | Old | Same finite 120s budget. The running old server still performs its linear scan and can time out. |
| New | New | Qualified read cost is independent of retained corpus size. Explicit pending responses retry only within the original shared budget. Dirty/restarted/restored state must fully scrub. |
| Updated installer recovering failed old-image rollback | Paired pre-ledger S0/current S1 | Recover only the active operation's authenticated stopped S1 expectations, then restore its protected files/stores before restarting S1. Generic absent authority still refuses. |

A server-only fix cannot accelerate pre-upgrade verification on an already-slow
old image: that read runs against the **currently running agent**, before any
mutation. The empty-instance adoption window does not change that constraint.

The only accepted weakening remains out-of-band tamper detection at the next
full scrub. Startup, relevant identity/config changes, dirty/expired state and
daily maintenance trigger full scrubs; new processes never inherit qualification.
Unchanged ordinary reads request the same host-owned scrub, with a 30s cooldown
after completion. Scrubs remain linear and can exceed 120s on slower storage;
client cancellation cannot restart their work. Ordinary reads wait cancellably
for in-progress work as before, so continuity is not guaranteed a sub-30s response
during a full scrub. This correction prevents the introduced one-second error
and repetitive inline scrubs; it does not claim asynchronous ordinary loading.

Database-only paired rollback intentionally rewinds protected location history,
manual state and marker along with databases. The runbook now states that effect.
Retained files remain current and must match authenticated expectations before
the new process can qualify. The previously excluded config-file reopen race was
already touched by the ledger design; this round adds bounded snapshot re-read
on ordinary config edits without weakening that check.

## Remaining Low follow-ups

Kepler owns issue tracking under the no-GitHub-writes boundary. L-a's process-wide
registration lease check/increment race and L4's drifted-file retention sweep
continuation are unchanged and deferred as instructed. L-b negative legacy
restore cases, L-c history-rewind documentation and L-d changed-config retry are
addressed in this round. Prior diagnostic ambiguity (drift versus scrub-pending),
write amplification/flash endurance and strict refusal for invalid quarantine
evidence remain the documented follow-ups/acknowledgement items. Startup/recovery
availability does not promise that invalid evidence can pass lifecycle verification.

## Evidence identity and gate results

The [sanitized command/test ledger](issue-1041-run4-local-gate.json) records exact
commands, heads, durations, log/TRX digests, individual focused outcomes, baseline
failures, source/build tree IDs, category sums and coverage bindings. The issue
body and all 37 comments were refreshed read-only at
2026-11-11T18:18:56.086122+00:00; fingerprints matched the prior complete read.
Roy's Option A approval (Nov 10, 14:04 PST, t356s2122) and the design at
[issuecomment-6779992529](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1041#issuecomment-6779992529)
remain authoritative. Attribution reports
[#1559](issue-1559-protected-read-attribution.md),
[#1591](issue-1591-schedule-validation-reuse.md) and
[#1618](issue-1618-retained-read-attribution.md) are reused without relabeling their
old timings as current results.

The branch is `kepler/1041-bounded-installation-verification`. Normal merge
`2897df1d` contains fetched development/v1 `5f5d253d`, newer than requested
369ef3df. Only CHANGELOG, ci-pipeline.md and CameraAgent's category inventory
conflicted. Incoming entries and our #1041 entry were retained; category deltas
were combined and all repository inventory totals re-summed. Actual discovery
confirms Unit=15,944 / Integration=1,276 / Manual=306 / Soak=1 / External=0 /
Hardware=1. CameraAgent has 8,211 Unit and 548 Integration cases; CLI has 3,199 Unit.

SDK 10.0.402 and a short private TMPDIR were used for every build/test. Heavy
work held `/tmp/hvo-heavy-gate.lock` with `flock -o`. Unit uses an invalid Docker
endpoint, CLI uses the cached catalog bundle, and only combined Integration uses
disposable fixtures on the local Docker socket. No live service or installation
token was used. The classifier on b3423622..83422a02 selects CameraAgent, Combined
and Delivery, `complete=false`; the target profile is development/v1 Preflight
and Build and Unit. No full-qualification workflow was dispatched.

| Check | Observed result |
| --- | --- |
| Full Release `-warnaserror` | PASS; zero warnings/errors |
| Full `dotnet format --verify-no-changes` | PASS |
| Four CI-control guards; both v1 controls | PASS |
| Category audit; docs:audit-operations; package audit; sandbox prerequisites | PASS |
| Focused CameraAgent / CLI / endpoint | 166 / 77 / 8 passed; 251 total, zero failures |
| Canonical selected Unit | 11,652 passed / two existing skips; 11,654 total |
| Selected Integration | 548 storage + 34 Acceptance + 61 host + 24 combined = 667 passed |
| Full fresh v1 Unit collection | 15,941 passed / three existing skips; 15,944 total, 21 assemblies |
| v1 canonical per-file coverage | All 27 floors PASS |
| CameraAgent / Combined / Delivery canonical component coverage | PASS; line/branch 90.71%/77.61%, 34.50%/20.05%, 92.23%/78.26%, respectively, against their existing component floors |
| CameraAgent / Delivery publish contracts | PASS |
| Real SIGKILL before/after/torn publication | Eight crash/recovery/restart states PASS; torn state refuses qualification, startup remains available |
| Real file-edit/snapshot probe | PASS; mid-load edit rejected, current snapshot hash matches, immutable location unchanged |
| Real endpoint scale | All three cohorts PASS; see samples below |
| `git diff --check` | PASS |

The new M3 tests execute the actual lifecycle manager with real SQLite and Data
Protection snapshot/ledger state; image, owner and lifecycle clients are simulated.
This round does not relabel the earlier native installer campaign as a fresh run.
The eight required baseline failures were observed against unchanged b3423622
product sources; test setup compile errors were corrected before those behavioral
baseline runs.

Two harness assumptions were corrected during validation: the endpoint test's
out-of-band manifest/journal repair, and the standalone config-edit probe's
interrupted scrub, both assumed an ordinary load requalified the ledger. They now
request explicit scrubs; the standalone probe first asserts strict refusal. All
stale-config and tamper assertions remain. Failed attempts are retained as failed
in the ledger. Runtime sources did not change for the probe correction; its exact
source hash is recorded and checked alongside the final delivery head. The first
endpoint attempt at 2897df1d failed 1/8; all gates were rerun at 83422a02 after the
acceptance correction. The later probe-only correction resumed the same-head
runner without repeating unchanged successful suites.

## Current endpoint scale

[Sanitized samples](issue-1041-run4-scale.json) record the real authenticated
loopback Kestrel endpoint with installer-generated owner configuration and a
completed synthetic owner. Autonomous workers are removed. `SeedScaleAsync`
constructs synthetic metadata sidecars and one raw-journal row per capture in a
disposable corpus; pixel payloads are not materialized. Three-product fan-out is
Raw/Preview/AnnotatedPreview, a subset of the default graph. The
[pipeline inventory](issue-1041-run2-ledger.md#production-multiplicity)
records approximately twelve sidecars/twenty JSON files per complete successful
default capture, and explains why the old artifact-proof cap was reached sooner
than the sidecar count suggested.

Three warm and three file-page-evicted samples per cohort use one affined CPU and
a 1GiB managed-heap limit. Fixture-only fsync/POSIX_FADV_DONTNEED plus mincore
proves zero resident retained/main-database/checkpoint pages before evicted reads;
transient SQLite SHM is excluded. Inode/controller caches and a completely cold
host are uncontrolled. Construction, checksums, eviction and qualification are
outside the endpoint timers. No global cache drop is used.

| Captures | Sidecars | Products/capture | Protected warm median ms | Evicted median ms | Ordinary load median ms | Continuity HTTP median ms |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100,001 | 100,001 | 1 | 85.19 | 78.32 | 61.96 | 96.80 |
| 300,000 | 300,000 | 1 | 26.57 | 70.30 | 41.27 | 46.26 |
| 100,000 | 300,000 | 3 | 33.71 | 60.44 | 36.43 | 45.43 |

Every first/warm/evicted qualified endpoint sample has zero retained scan spans
and zero retained parses, and all compared identity hashes match. The slowest
sample, including initial JIT, is 589.69ms. Ordinary/continuity samples also have
zero retained scans. Persisted restart reuses every old proof and parses only the
one newly published auxiliary state file, including beyond the former capacity
cliff. Manifest checksums/lengths and journal lengths match the prior three
baseline corpora; retained-input checksums remain unchanged across reads.

| Captures / sidecars | Warm allocation MiB | Warm CPU median ms | Warm logical bytes read | Initial adoption/qualification s | Restart qualification s | Maximum sampled process peak MiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 100,001 / 100,001 | 7.92 | 72.20 | 39,309 | 144.03 | 80.64 | 328.72 |
| 300,000 / 300,000 | 7.77 | 24.66 | 40,395 | 220.73 | 147.30 | 473.59 |
| 100,000 / 300,000 | 7.53 | 31.96 | 41,448 | 153.05 | 109.66 | 560.37 |

Qualification timers include fixture restart and explicit loading. Sampled memory
is below the 4GiB product limit, but this is not an absolute whole-run memory or
sustained capture/flash qualification. The endpoint performs fixed checkpoint work;
configuration/history size still controls their own fresh validation cost.

The prior linear endpoint observations at 0dc79eb5 remain historical: warm/evicted
medians were 3,203.62/6,146.69ms, 9,337.26/18,922.44ms and
6,824.69/15,257.04ms for the same corpora. All 13 retained/config/endpoint/read-client
source blobs remain identical on fetched 5f5d253d; that proof does not turn old
timings into a new controlled baseline. Neither 35,049ms nor a small-fixture
~180ms median is used as today's result. Host/storage conditions differ between
runs; no same-host causal ratio is claimed.

**Startup/lifecycle qualification risk:** full scrubs remain linear and initial
adoption at 300,000 sidecars measured 220.73s here, above the unchanged default
three-minute installer health wait. Persisted restart measured 147.30s for that
cohort. The fixture removes autonomous startup workers, so these observations do
not qualify whole-installer startup at production fan-out or on slower storage.
The new readings also exceed prior run-3 qualification observations; this round
does not establish a cause for that timing difference or promise unchanged full
scrub cost. Kepler must assess this operational risk in HIGH re-review/adoption
planning. No timeout is raised or integrity bypass added to hide it. Client
cancellation still cannot restart the host-owned scrub or manufacture trust.

Owner disposition: the three returned Mediums and original bounded-read software
behavior are locally evidenced. PR/adoption readiness remains blocked on Kepler's
new Sol/Opus HIGH reviews and disposition of the stated startup qualification
risk. No live instance, production token/data, home-docker, network change,
CLIProxy restart, PR or deployment is authorized or performed.
