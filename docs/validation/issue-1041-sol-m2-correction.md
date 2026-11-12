# #1041 missing-authority restore correction

Refs #1041. This addresses only SOL-M2 from the
[formal Sol review at 51e7810d](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1803#pullrequestreview-5872678049).
The complete review was read before implementation. The original issue,
[R1–R9 crosswalk](issue-1041-run2-ledger.md), subsequent corrections and Roy's
approved scope remain governing. Kepler owns subsequent formal review and PR
state. No recorded Opus Low is implemented by this correction.

## Required behavior and implementation

Historical database-only snapshots have neither paired protected files nor a
retained-integrity database inventory entry. Missing current database/checkpoint
paths do not establish that an installation is pre-ledger: authenticated location
history may still require the deleted authority.

`VerifyProtectedFilesAsync` now authenticates any surviving current history with
the existing Data Protection keys, application name and purpose before accepting
that historical snapshot shape. Key generation is disabled. A nonce-bearing,
unreadable, unauthenticated or non-object history refuses with a sanitized
installer diagnostic. It creates no history, key, ledger or qualification proof.
Genuinely unsealed legacy history remains compatible. This check runs within
`VerifyAsync`, before `PlanRestoreAsync` returns a plan and before `RestoreAsync`
can replace a store, including when the caller supplies an earlier valid plan.
Paired pre-ledger rollback/recovery and complete-archive staging are unchanged.

## Failing-before and passing-after evidence

The new `HistoricalDatabaseOnlyRestoreRequiresAuthenticatedLegacyAuthority`
matrix uses real installer APIs and agent-generated Data-Protection-authenticated
history. It reconstructs the historical manifest shape using the existing
snapshot fixture convention. The direct-restore rows use a legitimate plan
created before authority is lost, then remove only the disposable fixture's
ledger and checkpoint. The genuine legacy controls remove the nonce from the
real history and re-protect it with the same fixture keys, as existing adoption
tests do.

| Case | Unchanged 51e7810d product source | After correction |
| --- | --- | --- |
| Sealed history, planning | FAIL: no refusal | PASS: installer refusal; two rows and all state-file bytes preserved |
| Sealed history, direct restore with prior plan | FAIL: database rewound from two rows to one | PASS: installer refusal; two rows and all state-file bytes preserved |
| Authenticated unsealed legacy, planning | PASS | PASS: plan remains accepted; current database unchanged |
| Authenticated unsealed legacy, direct restore | PASS | PASS: snapshot's one row restored; protected history preserved |

Before source changes, `git diff --quiet HEAD -- src` confirmed the product tree
was unchanged at `51e7810d18826b7a6ce3ea5dfa283f9ca2a3d01f`. The old run produced
two expected failures and two control passes; the corrected focused run passed
all four with no skips. Its real category discovery found 8,384 CameraAgent Unit
cases. Inventory/runbook totals are recomputed from the project inventories and
checked again with complete category discovery after base integration.

Local raw evidence is retained under ignored `TestResults/issue-1041-sol-m2/`:
`before-binding.json`, before/after TRX, exact command/result ledgers, the final
affected selections and full category-audit output. The initial build found a
missing assets file and stopped before testing; restoring dependencies corrected
that setup prerequisite. That attempt is not counted as a behavioral result.
The owner thread records the immutable pushed head and final gate counts.

## Requirement mapping

| Original requirement | Correction behavior and evidence |
| --- | --- |
| R1 — bounded protected reads/startup and scale | Agent endpoint, startup, qualification and writer code are unchanged. Existing scale evidence remains attributed to its original heads; no new scale result is claimed. |
| R2 — tamper/schema/provenance | Existing authenticated authority must survive recovery intact; absence cannot downgrade sealed history to legacy. Real sealed-history plan/restore refusals prove the new boundary. Existing tamper/archive/dirty-state matrices remain in the affected store selection. |
| R3 — fresh authoritative identity and edits | No configuration, installation identity or owner-readiness cache is added. Current history is read afresh with existing keys before historical restore acceptance. |
| R4 — deadlines, auth and pre-mutation ordering | Client budget/transport/comparisons are unchanged. Affected owner-client and lifecycle contracts retain those checks; the new regression asserts refusal before any current database replacement. |
| R5 — failure/cancellation and progress | The new read honors cancellation and does not write authority. Existing restore cancellation, corruption and recovery cases remain selected. No optional proof is promoted to authority. |
| R6 — managed writers/concurrency | Writer and ledger synchronization code is unchanged. Restore continues to require the existing stopped-instance contract; the file-reopen race is not changed. |
| R7 — missing authority, crash, restore and rollback | Both missing-authority entry points refuse; a prior valid plan cannot bypass the restore check. Genuine legacy controls and existing paired/failed-first-upgrade/ledger-boundary recovery cases preserve supported recovery. |
| R8 — compatibility | Successful wire fields, reader floor and private formats are unchanged. Historical database-only restore is retained for genuine unsealed legacy state. A fixed image still cannot accelerate pre-mutation verification executed on an already-slow old running agent. |
| R9 — evidence/gates | SDK 10.0.402, short private mode-0700 TMPDIR, isolated HOME/XDG, disabled build servers/shared compilation and Docker, heavy lock, exact classifiers, Release warning checks, affected tests, category audit, formatting, operations/control guards and required documentation secret scans. Final results are in the owner handoff. |

## Scope and operator limits

30 days is the shipped default; longer settings are allowed. Shipped raw/archive
defaults remain 7/30 days; this correction changes no retention range or policy.

Roy's Option A tamper-detection timing remains unchanged. Qualified endpoint
reads remain bounded; clean incremental background qualification still performs
O(N) metadata/table work. No new large-corpus startup, allocation or throughput
campaign is run for this offline restore guard, per t410u.

Recorded Opus L1 (dispose/in-flight coordination), L2 (a long initial qualification
can exhaust the existing 120-second lifecycle budget and safely refuse/roll back)
and L3 (PR-body refresh) remain with Kepler. No timeout, repair/reset path,
production action, PR-body edit or formal review action is added here.
