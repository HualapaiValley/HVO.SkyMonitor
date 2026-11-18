# #1041 round-4 trust corrections

Refs #1041. This corrects Kepler's r4 findings on
`9fc7d1eb8e5ce10692fc95df16827b7160ca80a9`. The original issue body/discussion,
R1–R9 crosswalk and attribution reports remain governing, with Roy's Option A,
t388u/t389u and t410u directions. The [correction design](issue-1041-run7-design.md)
was written before product edits. Kepler owns subsequent Sol/Opus HIGH review;
this is an owner evidence handoff, not a review or PR-readiness verdict.

The [local ledger](issue-1041-run7-local-gate.json) binds the validated implementation, exact commands,
TRX fingerprints, classifiers and source/test/build tree objects. The final
handoff commit adds evidence only. No PR, issue comment, reviewer-branch change,
production request/deploy, networking change or CLIProxy operation occurred.

Validated implementation/normal merge: `4bc5f789abcb04331d6ef258ef3a5162739c2d7a`.
Its fetched development parent is `dd49fccfd8e0b2e61c187636fbb5fed94cd55d79`.
The merge preserved both changelog entries and mechanically re-summed category
inventories. A second fresh fetch/normal-merge check after validation was already
up to date. Source/test/build tree equivalence binds the evidence-only delivery
commit to this validated implementation. The original issue/discussion snapshot
is retained in [the run-5 ledger](issue-1041-run5-local-gate.json); the R1–R9
source crosswalk in [the Option A report](issue-1041-run2-ledger.md) and subsequent
approved directions remain the scope, with r4's two newly exposed gaps corrected
here.

## R4 findings and failing-before evidence

The new regressions first ran with **unchanged 9fc7d1eb product source**.
Five blocking cases failed and two existing refusal cases passed. The proof-count
regression separately failed on nine total rows versus three parsed contents.
The original failing observations are retained, not relabeled as passes.

| Finding | Fix | Failing-before regression | Observed current result |
| --- | --- | --- | --- |
| F1 High — recent failure cleared by incremental work | Failed recent admission revokes ledger qualification and startup incremental eligibility. Incremental work additionally requires a completed prior recent phase. Recent readiness can recover only after a successful full pass or fresh recent check. | `StartupDetectedRecentTamperCannotBeClearedByIncrementalQualification`, file and journal rows: cold admission found the tamper, but background work changed recent readiness back to true. | PASS, 2/2. Both refuse background/lifecycle qualification for same-length tampering; restoring authentic bytes followed by a full pass recovers. |
| F2 Medium — staged archive inherits clean trust | After authenticating the archive, staging removes and durably syncs only its clean checkpoint **before** publishing the ledger. Protected history, keys, expected rows and captured files are preserved. DB-only restore keeps its generation advance. | `CompleteArchiveStagesItsAuthenticatedLedgerWithoutPermittingDatabaseOnlyAdoption`: preserved-timestamp clean archive read zero of two manifests; preserved-file and same-length journal tampering incorrectly qualified. Journal exposure is outside the recent window, isolating F2 from F1. | PASS, 5/5. Clean archive reads both sidecar and v15 journal bytes through ordinary `LoadCurrentAsync`/`ScrubCurrentAsync`; both tamper cases refuse `VerifyInstallationAsync`. Existing malformed/missing-key refusals remain intact. |
| Sol Low — proof counter includes metadata hints | Count the authenticated parse-proof format separately from file/journal hints. Preserve the historical baseline build guard. | `SmallScaleProofCountExcludesAuthenticatedMetadataHints`: three sidecars produced nine total rows; old counter returned nine. | PASS, 1/1. Nine total rows, three parse proofs; no large campaign required. |
| Docs accuracy | Correct run-5/run-6 design/report assertions and annotate the run-6 gate ledger's uncovered cases; update the current runbook and changelog. | Direct inspection of each named r4 source, with historical results retained. | No claim that run-6 archive/recent cases had been proved. |

## Original issue completeness map

| Requirement | Behavior and evidence retained/current |
| --- | --- |
| R1 — repeated cost, scale and bounded protected reads | Qualified reads still inspect fresh bounded authority/configuration without a retained scan. Healthy clean restart metadata reuse remains covered by the affected store tests. Run-5 100,001/300,000-sidecar and production-fan-out measurements remain historical, not new r4 timings. Tiny scale proof counting is corrected. |
| R2 — tamper, schema, corruption and provenance | F1/F2 regressions now exercise ordinary qualification and actual protected refusal. Full scrubs hash bytes and validate schema/provenance; existing store and negative startup/Acceptance cases remain in the affected selections. Known recent failure cannot be erased by metadata reuse. |
| R3 — authoritative current identity and concurrent edits | Fresh configuration snapshots, protected identity/history, owner readiness and edit/reconciliation semantics remain unchanged. Affected store/loader and installation-verification tests cover current, changed and concurrent state. No configuration becomes immutable cached installer identity. |
| R4 — #1042 and lifecycle ordering | Same finite shared 120-second budget, timeout/caller-cancellation distinction, sanitized diagnostics and auth/proxy/redirect protections. Exact comparisons precede mutation; plans remain dry runs. Focused owner-client and lifecycle recovery selections retain these contracts. |
| R5 — failure/cancellation and optional progress | Invalidation and staged checkpoint revocation fail toward full verification. Revocation precedes ledger publication, including interruption. Existing optional-cache failure, prefix cancellation, incomplete restore and source authentication cases run in the affected selections. |
| R6 — writers and concurrency | Managed writer intents/expectations, nested writer barriers, retention and reconciliation hooks are unchanged; their affected store regressions run. Removing the staged certificate does not delete retained evidence or reset identity. |
| R7 — crash, archive, DB restore and rollback | Archive restore explicitly revokes its clean certificate; DB-only restore advances generation. Ordinary startup/coordinator and protected verification are used for the archive matrix. Existing dirty/hot/pending, cancellation, restore and failed-first-upgrade/ledger-boundary rollback tests are retained in current selections. |
| R8 — version compatibility | Successful wire identity remains compatible. Updated installer is required for archive checkpoint revocation; an older installer cannot supply this guarantee. Pre-mutation reads always execute on the currently running agent; a fixed image cannot accelerate an old running server's slow scan. No wire/private-ledger schema migration is introduced here. |
| R9 — HIGH evidence and selected gates | Pinned SDK 10.0.402, short private TMPDIR, disabled node reuse and heavy lock; fresh normal development merge; exact classifier output; warning-clean Release, affected selections, category audit, operations audit, format and lightweight guards. Results/skip counts are in the local ledger. No full burn-in or coverage campaign is claimed. |

The current core selection includes the named existing contracts
`QualifiedProtectedReadNeverTraversesRetainedEvidenceAndConcurrentReadsAgree`,
`StaleBackgroundConfigurationCannotCommitNewLocationAuthority`,
`MidScanCancellationPersistsOnlyCompletedProofsAndStillReadsCurrentBytes`,
`OptionalIndexWriteFailureKeepsDurableAndInMemoryLocationConsistent`,
`NestedManagedDeleteCompletesWhileAnotherFlowStartsAWrite`,
`WarmVerificationRefusesDeletedLedgerEvenWhenItsSqliteHandleIsOpen`,
`HotRollbackJournalRecoversBeforeLedgerLayoutValidation`, and the paired
restore/legacy-adoption matrices. The CLI selection includes client transport,
timeout/cancellation and snapshot contracts, plus failed-first-upgrade and
failed-old-image rollback recovery. Current host/Acceptance selections preserve
the full-graph deferred initialization/health retry and protected endpoint cases.

Normal, negative, failure, recovery, durable and interaction cases are addressed
above. No acquisition/hardware/production claim is made: all new inputs are local
disposable fixtures. Cancellation and budget evidence comes from the affected
existing client/store/restore cases; no unrelated process-probe campaign repeats.

## Commands and observed results

All five test commands use `dotnet test tests/<project> --no-build --no-restore
-c Release --filter <selection>`. Exact filters, TRX paths/hashes and complete
argument arrays are in the ledger. These results bind to the validated merge
above; the eight blocker/Low regression rows are included, not added again.

| Command / selection | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| CameraAgent.Tests — store, identity loader, scrub service and deferred-service tests | 175 | 0 | 0 |
| CameraAgent.AcceptanceTests — `FullyQualifiedName~InstallationVerification&TestCategory=Integration` | 15 | 0 | 0 |
| Deployment.Cli.Tests — owner client, snapshots and the two ledger-boundary lifecycle recovery cases | 62 | 0 | 0 |
| CameraAgent.IntegrationTests — positive Integration selection excluding Manual/Soak/External/Hardware | 61 | 0 | 0 |
| LogicHost.Tests — `FullyQualifiedName~DeviceRigProfileServiceTests` (incoming base change) | 29 | 0 | 0 |
| **Total** | **342** | **0** | **0** |

| Other command | Observed result |
| --- | --- |
| `scripts/ci:classify pull_request <previous-reviewed-head> <validated-head>` and branch range from `origin/development/v1` | Both pass. Correction selects CameraAgent, installer, LogicHost/base interaction and combined lanes; `complete=false`. Whole branch selects `complete=true`; development/v1's Preflight + Build and Unit profile remains governing. Roy t410u selects affected local tests, not another complete campaign. |
| `dotnet build HVO.SkyMonitor.v9.slnx --no-restore -c Release -warnaserror -p:SourceRevisionId=<validated-head>` | PASS; zero warnings/errors. |
| `dotnet run --project scripts/test-categories/HVO.SkyMonitor.TestCategoryAudit.csproj --disable-build-servers -p:UseSharedCompilation=false` | PASS, full discovery: Unit 16,295; Integration 1,340; Manual 307; Soak 1; External 0; Hardware 1. CameraAgent Unit 8,374; Acceptance Integration 41. |
| `dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes --include <changed-C#-files>` | PASS; exact correction/base-merge file list in ledger. |
| `scripts/ci:shell-syntax`, `scripts/test:ci-classification`, `scripts/test:coordination-guard`, `scripts/test:pr-review-tools` | All four pass. |
| `scripts/docs:audit-operations`, `git diff --check` | Both pass. |

Every build/test used the private short mode-0700 TMPDIR, disabled node reuse,
cached catalog and invalid Docker endpoint. Heavy validation held
`/tmp/hvo-heavy-gate.lock`. Per t410u, full Unit/Integration execution, coverage,
architecture/publish repeats, large-scale/startup/lifecycle burn-ins and unrelated
process probes were **not run this round**. Historical results retain their
original heads. The category audit discovers all cases; it does not execute them.

## Limits and Low dispositions

- The explicit `hvo-retained-validation-contract-v3` bump remains manual; there
  is no automatic semantic-change guard. Maintainers must bump it when retained
  validation/proof semantics change. This remains the recorded Opus Low.
- Incremental qualification is **O(N)**: it authenticates metadata, enumerates
  history and copies/rewrites the complete entries table on every restart. It
  avoids unchanged retained-byte reads; it is not constant-time qualification.
  No new large-corpus latency, allocation or write-amplification result is claimed.
- Roy's Option A remains the sole accepted detection timing tradeoff. Unobserved
  out-of-band metadata-preserving edits are detected at the next full scrub.
  An already observed recent failure is now an invalidation, not reusable trust.
- The fixed installer revokes the certificate on supported complete-archive
  staging. Device/inode binding is not added. Manual filesystem copies outside
  that staging path retain the existing out-of-band detection limits; use the
  updated installer for complete backup restore.
- Genuinely unqualified initial/dirty/restored state can require more than the
  120-second client budget. It refuses within that budget; no comparison, identity
  check or mutation ordering is bypassed, and no timeout is increased.
- The 400k recent-entry cap/headroom disclosure, persistent-fault latch, bounded
  initializer retries and prior registered Lows remain as recorded. No follow-up
  issues are filed by this coding thread. LogicHost reuse still needs bounded
  admission/discovery across agent count and retained volume, with background
  indexed qualification rather than a synchronous startup corpus walk.

Owner disposition: r4 corrections implemented; final gate results and immutable
bindings are recorded in the evidence ledger. Stop after authorized normal push
for Kepler's next independent HIGH reviews. The parent issue remains open.
