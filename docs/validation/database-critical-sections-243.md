# Database Critical Sections And Access Plans: Issue 243

This document is the historical-baseline and current-head inventory and disposition record for issue
[#243](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/243). It records
facts before tuning. Accepted production changes remain in focused child issues
with equivalent baseline/after evidence.

This is an interim structural inventory, not the `E2E-007` exit record. Issue
#243 remains open pending clean attributable replacement runs, exact plans where
listed, scaled I/O/WAL evidence, shared-instance attribution, fault evidence and
the named operational-policy dispositions.

## 1. Baseline

- Production source: `6b00fc8fc0727e03fbf9f87cf790720acff92425`
  (`origin/main`, PR #245 merge). New probe output also records the actual HEAD,
  branch, complete dirty-tree fingerprint and executed test/production assembly
  identities. Output from an uncommitted harness is explicitly
  `dirty-development-not-claimable`. A clean run remains
  `clean-source-attributed-review-required`: same-HEAD dirty builds cannot be
  cryptographically bound after the fact, so no helper output is automatically
  claimable. Final issue evidence requires reviewer confirmation or a separate
  build-time source/binary attestation.
- Environment: Linux x64, 8 logical CPUs, 15 GiB RAM, no swap, .NET SDK
  `10.0.100`, Docker `29.6.2`.
- SQL fixture: SQL Server 2022 CU14 and MinIO
  `RELEASE.2025-09-07T16-13-09Z`, both pinned by digest in
  `IntegrationTestFixture`.
- Existing evidence reused: issue #107's current-head 1K/10K central read plans
  and resource evidence. Its unchanged canonical browser suite was not rerun.
- New raw evidence is ignored under
  `TestResults/issue-243/<head>[-dirty-<fingerprint>]/<run-or-trial>/`. Writers
  use create-new files so independent runs cannot silently overwrite each other.
  The executable manifest is retained in the issue readiness comment; a reviewed
  aggregate and artifact checksums are still required for multi-trial claims.

Focused current-head commands:

```bash
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release -warnaserror

HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~Issue243SqliteCriticalSectionEvidenceTests.CandidateReservation_AllowsUnrelatedWriterWhilePhysicalEvidenceReadIsPaused"

HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~Issue243SqlCriticalSectionEvidenceTests.RetentionDelete_HoldsSerializableTransactionAndSessionLockAcrossMinioIo"
```

## 2. Schema Ownership

| Store | Schema and migration owner | Runtime consumers | Disposition |
| --- | --- | --- | --- |
| LogicHost SQL Server | `ApplicationDbContext`; EF migrations in `src/HVO.SkyMonitor.LogicHost/Data/Migrations` | Identity, OpenIddict, API keys, observatories, fleet, ingest, artifacts, derivatives, environment, transients, network operations and audit | Preserve EF for ordinary CRUD/projections and named parameterized SQL for provider-specific locking |
| `raw-ingress.db` | `SqliteRawCaptureJournal`, currently `PRAGMA user_version=10`; processing also records schema v3 | Raw ingress, lanes, controls, schedules, calibration, processing, transient candidate/runtime and gallery reads | Preserve direct SQLite; one initialization/migration policy is required before adding more feature migrations |
| `artifact-outbox.db` | `SqliteArtifactOutbox`, schema v2 | Artifact delivery and audit | Preserve direct SQLite; classify acknowledged/abandoned lifetime |
| `environmental-observation-outbox.db` | `SqliteEnvironmentalObservationOutbox`, user version 3 | Local observations, association, acquisition, projection and delivery | Preserve direct SQLite; retain independent durability and retention policy |
| `fleet-status.db` | `SqliteFleetStatusOutbox`, metadata schema v1 | Fleet delivery | Preserve direct SQLite; align physical-path/integrity policy where evidence justifies it |
| `cameraagent_identity.db` | `CameraAgentIdentityDbContext` EF migration | Local Identity only | Keep isolated and low-throughput; review WAL/busy/integrity policy separately |
| Catalog SQLite snapshots | Build-time immutable schema 2/preprocessing 3 | Read-only astronomy queries in both hosts | Reject runtime migration or mutable sharing; preserve `Mode=ReadOnly`, private cache and `immutable=1` |

SQL Server objects currently use the default schema. No migration-owned user
stored procedure, view, TVF or indexed view exists. Future database objects must
remain migration-owned by `ApplicationDbContext`; feature-owned SQLite SQL and
transitions remain in their feature stores.

## 3. Lock Hierarchy And Cross-Store Boundaries

Observed ordering, not yet a complete normative hierarchy:

1. LogicHost object operations acquire the session-owned
   `CentralObjectApplicationLock`, then a SQL transaction, then row/range locks.
2. Derivative and transient paths acquire job/event application locks before
   artifact locks; multiple artifact IDs are normally ordered.
3. Environmental ingest acquires source identity and retention application
   locks inside its transaction.
4. CameraAgent raw and candidate paths acquire `RawIngressLifecycleLock`, then
   one `raw-ingress.db` immediate transaction.
5. Raw retention acquires `RawIngressLifecycleLock`, then
   `StorageLifecycleLock`; publication puts immutable files before discoverable
   database work.

The required normative hierarchy must cover session/transaction application
locks, serializable ranges, row locks, leases, process lifecycle locks,
filesystem publication, MinIO fences and conditional finalization. No caller
may move external I/O outside a transaction unless durable intermediate state,
fencing and crash reconciliation are defined.

Confirmed cross-store transaction boundaries:

| Path | SQL/SQLite boundary held during external work | Current disposition |
| --- | --- | --- |
| `ArtifactIngestService.ReconcileExistingUnderObjectLockAsync` and `PersistAsync` | Serializable SQL transaction while MinIO object bytes are streamed and SHA-256 verified | Evidence required; probable separate ingest child |
| `CentralArtifactRetentionService.ReleaseAsync` | Object session lock, serializable SQL transaction and incompatible artifact lock while MinIO DELETE runs | Confirmed structural coupling; candidate blocker pending #246 baseline |
| `CentralTransientPayloadReleaseService.ReleaseItemAsync` | Object session lock and serializable transaction while MinIO DELETE runs | Evidence required; separate transient release child if accepted |
| `CentralTransientSubmissionService.SubmitAsync` | Serializable transaction/application/artifact locks while MinIO generation HEAD checks run | Evidence required; no correction accepted yet |
| `SqliteTransientCandidateJournal.ReserveAsync` | Raw lifecycle lock across a deferred durable snapshot, transaction-free physical validation and a short exactly revalidated immediate transaction | Corrected by #247; historical blocking and corrected writer-freedom observations are distinguished below |
| `SqliteCaptureLaneStore.ClaimAsync` | Raw lifecycle lock and immediate writer reservation while manifest/context parsing and filesystem existence checks run | Measure separately; no hashing occurs, so it is not combined with candidate reservation by default |

## 4. Current-Head Critical-Section Evidence

### SQL retention and MinIO

The production retention service was run with MinIO DELETE paused after its SQL
mutation. SQL Server reported:

- two sessions attributed to one unique issue-243 application name;
- one session with an open database transaction;
- one session holding at least one granted session-owned application lock;
- a conflicting `CentralArtifacts` update timed out after one second;
- barrier-probe wall time exceeded the injected one-second competing-writer
  timeout and included observation overhead;
- after release, SQL converged to `Expired` / `retention.expired`, the MinIO
  object was absent, and the competing update made no mutation.

This proves external object latency directly extends transaction, incompatible
SQL-lock and dedicated-session occupancy. It is not a natural production latency
baseline. A correction candidate must use durable deletion intent, short fenced
transitions, conditional finalization and restart reconciliation; simply moving
DELETE after commit is rejected.

### SQL duplicate ingest and MinIO verification

The production multipart-duplicate path was measured with one W2
`3096x2080` Bayer16 object (`12,879,360` bytes). While its streamed MinIO GET
was paused, SQL Server reported two attributed sessions, one open transaction
session and one granted session-owned application lock. A conflicting artifact
update reached its injected one-second timeout. The recorded operation duration
is barrier-probe wall time including that timeout and observation overhead, not
natural ingest latency. The duplicate acknowledgement, exact length, SHA-256 and
final Available state remained correct.

This confirms full-object verification directly extends SQL transaction,
incompatible SQL-lock and dedicated-session occupancy. #249 owns a candidate
durable verification-intent protocol, external streaming, fenced finalization
and restart reconciliation. It is not combined with deletion protocol #246.

### SQLite transient reservation

#### Historical issue #243 baseline

The original production candidate reservation validated five immutable sources. Its last
sidecar was replaced by a Linux FIFO carrying the exact committed bytes, which
paused physical validation for two seconds after the immediate transaction had
started. An unrelated `raw_captures` writer:

- remained blocked at 250 ms;
- completed only after the two-second barrier released;
- applied an observable sentinel row mutation after release;
- followed one successful candidate reservation with five ordered source rows.

This preserved v1 deterministic mechanism probe proves that historical physical evidence latency occupied the
only WAL writer. It is not the declared W2/W3P, four-writer or multi-trial
performance workload, so its timings are descriptive and no percentile or
capacity claim is made. It remains evidence about the pre-#247 implementation;
it is not relabeled as a current-head result.

#### Corrected current-head v2 probe and #247 baseline

The renamed v2 probe
`CandidateReservation_AllowsUnrelatedWriterWhilePhysicalEvidenceReadIsPaused`
uses the same five-source FIFO boundary but asserts corrected current behavior:
the unrelated production-shaped writer updates exactly one row within 250 ms
while physical validation remains blocked, the reservation then commits exactly
one candidate and five ordered sources, and the sentinel state is durable. Its
evidence schema is `hvo-issue-243-sqlite-critical-section-v2`; it supplements
rather than overwrites the historical v1 observation.

Issue [#247](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/247#issuecomment-5150429093)
has a reviewed replacement baseline for the scaled W2/W3M/W3P, four-writer and
separate FIFO-barrier workload. The replacement checkpoint is rooted at harness
source `68b61f838eb180ad2b4d911425329ec5c7231248`.
The reviewed baseline directory is
`TestResults/issue-247/68b61f838eb180ad2b4d911425329ec5c7231248/aggregate-baseline/`;
its summary SHA-256 is
`73407FEB40C8C1DFCA8A1A3EFA9F06992779D56A026373DEB547B7243643973D`
and manifest SHA-256 is
`114559BC45B308D536ADBC22E1E11A128F0C77DA0615C9A6A6C1776CD2E36EB0`.
The baseline is complete; only corrected-head after comparison remains evidence
work for #247.

### Deployment-location reconciliation

The production owner-resolution path was exercised once with one deployment
owning 10,000 captures and 10,000 eligible artifacts. It converged all bindings
and one audit correctly. The probe intercepts reader, scalar and non-query
commands only while `ResolveAsync` executes, but deliberately replaces the real
derivative scheduler with a counting no-op. It therefore establishes the deep
cardinality and 10,000 in-transaction scheduler-interface invocations; its
timing, allocation, growth and SQL-command values are descriptive structural
observations that exclude scheduler persistence cost, not a production
performance baseline.

The captured production reconciliation command used four typed parameters:
registration `uniqueidentifier`, location ID `nvarchar(128)`, version `bigint`
and effective-from `datetimeoffset`. No index change is proposed from this run;
an actual-plan comparison, real scheduler, blocked-writer probe and the declared
multi-trial workload remain mandatory before #248 changes production. The
structural finding accepts #248's investigation scope, not a performance target
or implementation disposition.

### Central transient payload release

The production event payload-release path was paused at its first MinIO DELETE.
SQL Server showed at least two attributed sessions, one open transaction and one
granted session-owned application lock. A conflicting update over the bounded
release artifact set timed out after one second. The probe publishes every seeded
payload before starting and verifies every object is absent afterward; the parent
release completed and eligible artifacts became Expired. Existing release
contract tests separately cover independently held evidence; this barrier probe
does not claim to re-prove that behavior.

#250 owns durable ordered item deletion, external I/O outside SQL transactions,
fenced finalization and processor restart. Event/review/release identity and
late retention holds make this distinct from ordinary retention #246.

### Hybrid source-generation checks

The production hybrid submission path was paused at the first of five
post-verification object-generation checks. SQL Server showed an attributed open
transaction, granted transaction-owned application locks and a conflicting
source-artifact update timing out after one second. After release, all five
prior verifications and five generation checks completed and one submission was
accepted.

The generation check closes a real verification-to-commit race, so removing it
without an object fence or durable generation token is rejected. #251 owns a
deterministically ordered, bounded fence or durable verification/finalization
protocol with no MinIO I/O inside SQL transactions.

The current SQL barrier probes count attributed sessions, open transactions and
sessions holding granted application locks, then observe a one-second competing
update timeout. They do not yet capture the competing request's
`blocking_session_id`, subject isolation level or exact key/range lock resource.
Each child baseline must add that mapping before making lock-granularity or
contention-severity claims.

## 5. Query And Growth Inventory

Verified amplification or unbounded work that still requires exact production
plans and scaled evidence:

- Environmental derived ingest can issue two sequential lookups for each of
  256 lineage references, or 512 lookups before lineage persistence.
- Environmental correlation loops over source priority, target specificity,
  freshness, stale fallback, overlap and graph loading.
- Successful API-key validation performs one key query and one user query;
  missing/inactive keys perform one query.
- Legacy frame/artifact history materializes wider entities than its DTOs;
  current network operations uses bounded keyset SQL projections.
- Derivative claim repeats eligibility in lock-hinted selection and fenced
  update, inserts an attempt, reloads a split graph, and permits high bounded
  collision/deadlock retry counts.
- Deployment-location reconciliation materializes every matching historical
  location/frame/artifact and invokes scheduling per eligible artifact inside
  its caller's transaction. Existing 10K evidence is distributed as 1,000
  deployments by 10 frames and does not test one deep deployment.
- Raw startup reconciliation and several migrations scan all retained rows,
  including manifest BLOB parsing/hashing.
- Artifact outbox snapshots scan complete acknowledged/abandoned history;
  several page/detail surfaces perform bounded N+1 reads.

The current-head environmental W3M/W4 developmental control observed indexed
temporal selection at 100,000 rows with 11 fresh, 24 stale and 26 history logical
reads; each measured correlation used three SQL commands. The raw evidence is
not yet linked here by clean source fingerprint, trial manifest and artifact
checksum, so these are single-run descriptive observations. No
correlation/index/ranked-query change is accepted. #252 separately owns the
unmeasured 256-reference derived-lineage boundary and defaults to two bounded EF
queries if its baseline confirms the structural 512-lookup amplification.

The smaller central derivative claim/window developmental control observed:

- W3M contained 10,000 candidate artifacts, 100 scheduled jobs and 80 measured
  claims;
- claim median/p95/maximum were 6.1917/10.7887/13.0852 ms;
- the claim plan used
  `IX_CentralDerivativeJobs_Status_AvailableAtUtc_CreatedAtUtc_Id` with seven
  logical reads;
- W4 concurrency 1/4/8 converged with zero deadlocks and zero unique
  collisions.

These values also remain single-run descriptive observations until linked to a
clean source fingerprint, trial manifest and artifact checksum. No claim
procedure, `UPDATE ... OUTPUT`, retry-policy or claim index change is accepted.
The full derivative worker/fault harness is not green: after stale
fixture reconstruction and recipe-count assumptions were corrected, a later
fault case retained its staging object. #253 owns focused failure analysis and
blocks claiming that unchanged long suite as current-head evidence.

Candidate indexes remain hypotheses until the exact production statement,
typed parameters, W3M/W3P distribution, actual plan, rows estimated/read,
logical reads, sort/spill/temp-B-tree behavior and write cost are captured. A
simplified equality query is not evidence for optional-filter production SQL.

## 6. Retention Classification

| Class | Examples | Required property |
| --- | --- | --- |
| Permanent evidence | Capture/artifact identity and provenance, immutable review/assessment history, audit decisions | Never deleted merely to reduce table size; archive only with preserved lineage and retrieval semantics |
| Compact idempotency/tombstone | Released object identity, acknowledged delivery identity, terminal operation key | Preserve duplicate/retry protection while permitting payload/detail compaction |
| Archived history | Completed jobs/attempts, old notifications, superseded projections | Keyset archival with explicit retrieval and restore policy |
| Bounded operational state | Leases, pending/retry queues, fleet receipts, environmental delivery and active transient work | Explicit age/count/byte bounds, oldest age and drain telemetry |
| Disposable telemetry | Diagnostic samples without durable workflow meaning | Time/count bounded and safe to lose |

No central or edge deletion policy is accepted by this classification alone.
Each owner must prove that provenance, idempotency, recovery, legal/operator
history and backup/restore requirements survive the proposed compaction.

## 7. SQL Server And SQLite Operations Gaps

Before rollout, SQL Server requires an operator-owned policy for stable
`Application Name`, measured pool/timeout values, Query Store, blocked-process
and deadlock Extended Events, waits/grants/spills/tempdb/log/file latency,
autogrowth/headroom, backup/restore and statistics/index maintenance. Startup
currently migrates, seeds and backfills with the runtime principal; production
must separate controlled migration from least-privilege runtime operation while
preserving development startup.

The rollout work is split by ownership:

- #254 owns stable runtime/migration session attribution and evidence-gated
  connection pool/timeout disposition.
- #256 owns the controlled migration/seed/backfill command and separate
  migration/runtime grants.
- #255 owns Query Store, blocked/deadlock Extended Events, shared capacity,
  backup/restore and statistics/index-maintenance operations evidence.

Pool size, timeout, RCSI and maintenance values remain deferred until those
children collect comparable evidence; issue #243 does not set defaults.

`raw-ingress.db` currently establishes WAL, `synchronous=FULL`, foreign keys,
busy timeout, autocheckpoint, physical DB/WAL/SHM checks, integrity and foreign
key checks. Pooling/cache choices and migration ownership vary by feature. The
shared policy must standardize connection roles, initialization ordering and
checkpoint result telemetry without moving feature SQL into a generic
repository. Identity SQLite and immutable catalog policy remain separate.

The lane harness is stale: its historical acceptance baseline belongs to profile
SHA-256 `4CDF8496...49308`, while the current profile is
`227FB3C0...0F499`, and its expected raw-ingress schema is 7 rather than the
current 10. Updating only those identities would incorrectly compare
non-equivalent profiles and retain invalid acceptance gates. No current-head lane
performance conclusion is claimed; its owner must either collect an equivalent
current-profile baseline or explicitly mark the historical comparison `N/A`.
[#257](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/257) owns that
evidence-only correction.

`SqliteCaptureLaneStore.ClaimAsync` parses bounded manifest/context bytes
and checks file existence under `BEGIN IMMEDIATE`, but it does not hash payload
bytes. No separate correction is accepted from current evidence. Reconsider only
after a faithful current-profile barrier workload measures production
busy/locked duration, cadence blocking and context size; do not combine it with
#247 by assumption.

## 8. Option Decision Record

| Option | Decision | Evidence/trigger |
| --- | --- | --- |
| EF Core ordinary CRUD/projections | Accept | Existing ownership and composability remain appropriate |
| Named parameterized provider SQL | Accept | Required for lock hints, application locks, queue claims and plan probes |
| Dapper or generic repository | Reject | No measured mapping or ownership benefit; adds a dormant access layer |
| Blanket `NOLOCK` | Reject | Breaks correctness and does not repair critical sections |
| Blanket retries | Reject | External side effects and transaction protocols require operation-specific idempotency |
| Speculative indexes | Reject | Exact production plans and write cost are not yet captured |
| Stored procedures | Defer | Consider only measured atomic claim/attempt, bounded deployment update or TVP lineage protocols |
| Non-indexed retention view | Defer | Consider only if multiple consumers need one reviewed relational definition |
| TVP / `SqlBulkCopy` | Defer | Trigger is measured bounded composite-set or import/backfill pressure |
| Compiled queries | Defer | Trigger is material query-compilation CPU under measured auth/read traffic |
| RCSI | Defer | Requires lock-wait benefit and shared-tempdb/version-store evidence |
| Connection pool/timeout limits | Defer | Must be measured under W4 and delayed-object saturation; no value is invented |
| Retention changes/counters | Defer | Require W3M scan/growth evidence and preserved durable semantics |
| Runtime catalog migration | Reject | Catalog snapshots remain immutable verified artifacts |

## 9. Ranked Disposition

1. Candidate rollout blocker: [#246](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/246)
   must baseline a durable-state/crash-reconciliation design that shortens the
   artifact-retention SQL/MinIO DELETE critical section.
2. Candidate rollout blocker correction: [#247](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/247)
   has the reviewed declared W2/W3M/W3P baseline and implements the shorter
   transient-candidate reservation; corrected-head after comparison remains the
   outstanding performance proof.
3. Rollout-blocking investigation: [#248](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/248)
   must collect the real-scheduler multi-trial baseline and actual plan before it
   may commit authority separately from bounded, durable, restart-safe capture
   reconciliation and derivative scheduling.
4. Candidate rollout blocker: [#249](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/249)
   must baseline durable intent and fenced finalization for duplicate/status
   existing-object verification outside SQL transactions.
5. Candidate rollout blocker: [#250](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/250)
   must baseline ordered central transient payload deletion outside SQL
   transactions while preserving item outcomes and event evidence holds.
6. Candidate rollout blocker: [#251](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/251)
   must baseline a hybrid source-generation fence outside SQL transactions
   without reopening the object-generation race.
7. Near-term: [#252](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/252)
   measures and, if confirmed, batches maximum environmental lineage. Temporal
   correlation, API-key validation and legacy history changes remain deferred.
   Derivative claim procedure/index/retry changes are rejected on current
   evidence; [#253](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/253)
   separately owns the non-green full worker staging-cleanup fault evidence.
8. Operational prerequisite to #151: attribution, migration/runtime privilege,
   Query Store/XE, capacity, backup/restore and maintenance policy through
   [#254](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/254),
    [#256](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/256) and
    [#255](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/255).
9. Evidence correction: [#257](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/257)
   rebaselines current-profile lane evidence without changing production.
10. Deferred/rejected: every option in section 8 without its stated trigger.

The structural child scopes in positions 1 through 6 require separate issues and
branches, but are not accepted performance conclusions. Their baseline and after
evidence must use the same barriers, distribution and correctness state checks
recorded here; each child must satisfy its scaled workload before production
changes.
