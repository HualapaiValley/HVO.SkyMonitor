# #1041 run-6 HIGH corrections

Refs #1041. This corrects Kepler's r3 findings on
`711fa7b9cdb53023951e38fd0fe171252ed53069`. The original R1–R9 crosswalk,
[Option A approval/design](issue-1041-run2-ledger.md), prior attribution reports,
and Roy's t388u/t389u/t410u directions remain governing. The
[run-6 design](issue-1041-run6-design.md) was written before product edits.
Kepler owns the r4 Sol/Opus HIGH reviews. No PR or issue comment is authorized.

Validated implementation head: `3a13549096b7abf001f17116dcabb6190fad55ac`. The
[local evidence ledger](issue-1041-run6-local-gate.json) records exact results. The delivery evidence commit preserves its source/test/
build trees. Normal merge `8ee489c3863141167c2a615dd5ca25b8d2f89d38` incorporates target
`41ff38accb3086e8bcd265f33016e9744cdc5361`. A second fetch/normal-merge check
immediately before handoff was already up to date. The merge brought in only CI queue
policy/guard changes; no reviewer branch was touched.

## Requirement, behavior and evidence

| Requirement | Implemented behavior | Behavioral evidence | Result |
| --- | --- | --- | --- |
| 1 — M-4/M-A(c), trusted incremental qualification | A drained clean shutdown seals a protected checkpoint. A new process authenticates the full row commitment and current authority, compares MAC-bound file size/mtime/digest/kind/generation hints and journal checksum/length metadata, and reads only changed/new/unaccounted content. Cache misses or bad MACs fall back to bytes. The explicit validation-contract version replaces build MVIDs. | `SuccessiveCleanRestartsReadZeroOlderFiles`: two restarts over 12 older files each read zero. `CleanRestartsSkipUnchangedProductionJournalManifestBytes`: two restarts fetch zero older sidecars/journal BLOBs. `ManagedNewEvidenceReadsOnlyTheNewFileOnNextCleanRestart`: one new file read, then zero on the next restart. `ChangedOlderFileRefusesRestartAndFullScrub`: changed metadata refuses restart qualification; same-size/time-preserving tamper is rejected by the full scrub. | PASS in the affected core selection. The unchanged-file regression failed against the preserved pre-fix product assembly. |
| 1 — dirty/crash/restore and scrub expiry | Running checkpoints remain unclean. Incomplete work, recovered hot journals, invalid/expired authority, dirty state and paired restore force full byte qualification. Incremental restart preserves the last full-scrub deadline; maintenance uses that deadline. No saved marker alone grants lifecycle identity. | Five `UntrustedRestartFallsBackToReadingActualOlderBytes` cases; hot-journal startup reads all 12 older files; the real paired restore matrix now uses ordinary startup/coordinator qualification and asserts actual reads. `IncrementalRestartCannotExtendTheOriginalFullScrubDeadline` and `ScheduledFullScrubUsesThePreservedDeadlineAfterIncrementalRestart`. | PASS. Restore/cancellation/source authentication cases are included in the affected store selection. |
| 2 — M-7, hot rollback journal | The confined required ledger is opened writable so SQLite completes rollback before the unchanged read-only layout/authentication checks and schema use. This recovery forces a full pass. A hot optional hint cache is discarded and rebuilt, never promoted to authority. | `HotRollbackJournalRecoversBeforeLedgerLayoutValidation`, ledger and optional-hint variants. Both fixtures first prove that a read-only open needs writable rollback recovery. Next startup recovers, reads actual bytes, and passes a fresh full scrub and protected verification. Existing corrupt/layout/entry/authentication refusals remain in the same selection. | PASS. The authoritative-journal case failed against the preserved pre-fix assembly. |
| 3 — initializer M1 | Initialization failure uses the shared sanitized event 7312 and capture Unhealthy readiness. Five maximum attempts use delays 1/2/4/8 seconds; success clears only this service's failure. Persistent failure remains unhealthy. Event 7310 no longer falsely says existing qualification was lost. | `DeferredConfigurationFailureMakesRealHostUnhealthyThenRecoversOnRetry` keeps the full production hosted-service graph, fails graph initialization once, observes HTTP health 503 and then 200 after retry. `PermanentInitializationFailureHasFiniteBackoffAndShutdownCancelsPendingRetry` checks exactly five attempts or cancellation after one, exact delays, unhealthy readiness and sanitized messages. | PASS in the final affected Acceptance/host selections. The full-graph regression failed against the pre-fix assembly. |

## Integrity and compatibility

The only detection timing tradeoff remains Roy's approved Option A. Ordinary
file metadata changes are detected on restart. Out-of-band changes preserving
size/mtime, or journal bytes changed without changing declared checksum/length,
are detected by the next periodic or explicit full actual-byte scrub. The
metadata hints are authenticated optional acceleration, never a replacement
for the authoritative expected entries. Full scrubs still hash current bytes,
validate schema/provenance under the explicit semantic contract, and refuse
drift. Changing retained validation/proof semantics requires bumping that
contract version; unrelated build/runtime MVID changes do not invalidate it.

Clean-shutdown is an additive protected checkpoint field. Older checkpoints
without it, or without the new semantic binding, get one full qualification;
future unchanged builds can reuse clean proofs. Ledger row/private restore
formats and successful HTTP fields are unchanged. Snapshot restore advances
ledger generation, so even valid old hints cannot grant restored readiness.
Authenticated interrupted writes may satisfy recent admission only when actual
bytes equal expected or declared content; pending work still cannot qualify the
whole installation. Fresh configuration, location, owner readiness, field
comparisons, auth/proxy/redirect refusal and #1042's shared finite 120-second
budget and timeout/caller-cancellation distinction remain unchanged.

| Installer / currently running agent | Behavior |
| --- | --- |
| Older installer / new qualified agent | Successful wire fields and authentication remain compatible; pending handling has the existing older-client limits. Matching updated installer is still needed for paired ledger restore. |
| New installer / old agent | The existing 120-second budget applies to the old server's scan. A new server image cannot accelerate pre-mutation verification executed by the currently running old agent. |
| New installer / new agent | Clean restart performs incremental qualification; initial/unclean/expired/restored state performs full qualification. Pending work is bounded refusal, recovery-required is terminal, and comparisons precede mutation. |

## Lows and operator notes

- **L1 fixed:** the recovery latch uses corpus/authority identity, not changing
  capture generation. `RealRecoveryLatchSurvivesOngoingManagedCaptureWrites`
  uses a real store, three managed writes and 15 minutes of clock advancement:
  generations advance, the failure latch remains terminal and no extra scan runs.
  The existing endpoint therefore reports `recovery-required` promptly.
- **L2 fixed:** `RecentAdmissionChecksAnAuthenticatedInterruptedPublicationIntent`
  checks matching and mismatching crash-publication bytes. Matching intent keeps
  recent readiness; both states remain lifecycle-unqualified until full checking.
  Normal recent files retain the existing bulk expected-row comparison; only a
  mismatch needs an intent lookup.
- **L3:** clean startup no longer performs older actual-byte qualification.
  Metadata discovery and raw recovery still run before capture configuration is
  safely published; no constant capture-admission latency is claimed.
- **L4 annotated:** the run-5 ledger's storage Integration exit 1 at 03987c5b was
  two stale UI-wording assertions for cancelled standalone drains. Upstream had
  moved cancellation to structured actor/time/reason fields. The receipt and
  paused/explicit-resume checks passed; 587f111b2 corrected the expectations and
  the 19-case focus passed at cc0771f29. Its observed failure is retained and now
  has an explicit disposition; it is not relabeled a pass.
- **Recent cap headroom:** nearly 72 hours of date-partition enumeration at one
  frame/20 seconds and 24 files/frame is 311,040 files plus directories, about
  78% of the 400,000 cap. Roughly 22%, or 88,900 entries, remains. Extra fan-out,
  faster cadence or unexpected files consumes that margin. The runbook states it.
- Prior registration-lease and retention-continuation Lows remain with Kepler;
  this thread files no follow-up issues. LogicHost should reuse indexed metadata
  qualification and host-owned maintenance without making retained volume or
  registered agent count a synchronous startup loop.

## Validation scope and limits

Roy's t410u affected-only direction applies. All work uses SDK 10.0.402, a new
short private TMPDIR, disabled MSBuild node reuse, an invalid Docker endpoint,
and the host heavy lock. No live agent, production/beta data, tokens, Docker
services or networking changes are involved.

| Final check | Result |
| --- | --- |
| Affected core Unit selection | 171/171 passed |
| InstallationVerification Acceptance Integration selection | 14/14 passed |
| CameraAgent host Integration selection | 61/61 passed |
| Affected Release builds | 3 passed, zero warnings/errors |
| Changed category inventories | CameraAgent Unit 8,370; Acceptance Integration 40, both discovered exactly |
| Formatting (changed C# files), four CI-control guards, operations audit, diff check | All passed |

The final local ledger records the exact affected core/Acceptance/host filters,
Release warning results, changed-project category discovery, formatting scope,
CI-control guards, operations audit and classifier ranges. Earlier full suites
remain attributed to their original heads. No full Unit/Integration campaign,
coverage sweep, broad process-probe campaign or release burn-in is repeated.

This round measures actual retained-read counts on disposable fixtures. It does
not relabel the old 246-second/10.5-GB startup qualification, run-5 large-corpus
latencies or memory figures as run-6 results. Complete metadata discovery and
authentication still scale with corpus size in the background. Qualified endpoint
work and the bounded recent startup front remain separate from that work. No
new 129,600-frame end-to-end latency or allocation result is claimed.

Owner disposition: the run-6 blocking corrections are implemented with targeted
negative, recovery and interaction evidence. Final results and immutable source
bindings are in the evidence ledger. Stop after normal push for Kepler's r4
Sol/Opus HIGH completeness/review verdicts; no PR-readiness verdict is claimed.
