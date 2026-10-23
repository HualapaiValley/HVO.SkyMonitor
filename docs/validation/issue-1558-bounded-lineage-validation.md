# Bounded environmental lineage validation (#1558)

Refs #252. The service now resolves lineage with two EF scalar projections inside its existing ingest transaction and shared retention lock: distinct source hashes to source IDs, then exact `(SourceRecordId, ObservationId)` pairs to observation record IDs. It reconstructs the original input ordinals before persistence. No schema, indexes, retention policy, temporal correlation, deployment, or retry infrastructure changed.

The original [#1557 baseline](issue-1557-maximum-lineage-ingest-baseline.md) and its evidence are preserved. This report compares fresh runs on this host. Full W3M/W4 qualification budgets remain unchanged; these are the explicitly scoped metadata-only 5+30 arms.

## Source and reproduction

- Before: `267c8974586a6822f7d39e048d8e3162d0041ad2` (test/harness changes; production service bytes equal `e20f945d8f7108cb7a15e272dc42a6289107b2ed`).
- After: `924b34b8d60d878de5ca206ee88ea3ef3ba4cb30`. Both measurements are clean, rebuilt Release runs with requested revisions and recorded source/assembly SHA-256 values.
- Environment: Ubuntu 24.04.5 x64, Intel i9-14900K, 12 effective processors, SDK 10.0.402, .NET 10.0.13, server GC; disposable SQL Server 2022 CU26, version 16.0.4265.3. The complete environment objects are identical. Environment/source/assembly hashes and every retained file checksum are in [sha256.json](evidence/issue-1558/sha256.json).
- Fixture: 256 production-ingested observations across eight sources; ordered permutation `i*73%256`. Each arm primes one derived source per lane, then performs five untimed warmups and exactly 30 attempts at concurrency 1/4/8. Generated database/site/record GUIDs differ between isolated runs; the fixture definition, observation IDs and operation counts are equivalent.

Use separate clean checkouts of the recorded revisions, build before setting the evidence revision, then run the matching manual method. Exact executed arguments and environment overrides are retained in [commands.json](evidence/issue-1558/commands.json).

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 900 dotnet build \
  tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false

# Before: HVO_ISSUE_1557_EVIDENCE=1 and Issue1557_MaximumLineage_RecordsProductionBaseline
# After:
flock -o /tmp/hvo-heavy-gate.lock timeout 1800 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOTNET_gcServer=1 HVO_ISSUE_1558_EVIDENCE=1 HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
  dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter FullyQualifiedName~Issue1558_MaximumLineage_RecordsBoundedValidation \
  -nodeReuse:false -p:UseSharedCompilation=false
```

The shared harness retains the original per-reference baseline assertions and has a separately named after method/schema. It captures exact typed-command replays and actual plans at all four lengths. It now records SQL1205/51006/51007/command-timeout failures, including failed application-lock acquisition duration. Ordinary tests keep `HVO_EVIDENCE_REVISION` unset.

The first unchanged-source attempt at `e20f945d` stopped after seven arms on SQL51006; its [partial progress](evidence/issue-1558/original-aborted-progress.json.gz) and [failure disposition](evidence/issue-1558/original-aborted-run.json) are retained. The original harness did not retain individual outcomes for its aborted arm. It is excluded from this comparison. The completed comparison records every one of its 720 attempts; no failed attempt is retried or replaced.

## SQL and parameter bounds

| References | Before / after lineage SELECTs | Before / after total commands | EF write batches | Before / after logical reads | Before / after aggregate operator rows read |
| --- | --- | --- | --- | --- | --- |
| 1 | 2 / 2 | 10 / 10 | 1 | 54 / 51 | 13 / 11 |
| 32 | 64 / 2 | 72 / 10 | 1 | 729 / 479 | 753 / 1192 |
| 128 | 256 / 2 | 267 / 13 | 4 | 2829 / 1815 | 2376 / 2477 |
| 256 | 512 / 2 | 526 / 16 | 7 | 5628 / 3586 | 4793 / 4365 |

Lineage validation command count is independent of length. Total commands still include two own-identity reads, three historical target-binding reads, two application-lock calls and the length-dependent EF insert batches. At 256 references, validation reads fall from 512 to two and total commands from 526 to 16. The write plans still insert one observation and exactly 256 lineage rows.

The eight-source after capture uses ten `nvarchar(64)` hash parameters (eight distinct values, two padded values). The scalar qualified-pair predicate uses exactly 2/64/256/512 `uniqueidentifier` parameters at lengths 1/32/128/256. The 256-distinct-source SQL regression captures **300** `nvarchar(64)` parameters for 256 values (44 padded), and **512** scalar GUID parameters for pairs. Its repeated observation GUIDs occur under different source IDs. The maximum complete command budget is 512 + three RPC overhead parameters = **515**, below 2,100. Scalar pairs avoid multiplying collection-padding buckets across source groups. No TVP/procedure is warranted by this workload.

At lengths 32/128/256 the after observation projection scans the existing covering `(SourceRecordId, ObservationId)` index: 598 physical index rows read, then exactly 32/128/256 rows returned. The source projection reads 16 index rows and returns eight. This explains the higher aggregate rows-read counter at 32 and 128 despite lower logical I/O: lineage validation costs 11 logical reads after versus 256/1,024/2,048 before, excluding the two own-identity SELECTs. It does not fetch a cross product into EF or track referenced entities. The singleton uses an index seek. These plans describe the retained ~600-observation fixture, not a new large-history sizing qualification.

Detailed per-class counts are in [plan-attribution.json](evidence/issue-1558/plan-attribution.json); complete commands, parameter values/types/sizes and actual plans are retained as `before/sql-replay-{1,32,128,256}.json.gz` and `after/sql-replay-{1,32,128,256}.json.gz` under [the evidence directory](evidence/issue-1558/sha256.json). Every diagnostic replay rolls back its writes.

## Latency and throughput

Before: 226 accepted / 134 SQL1205 victims. After: 230 accepted / 130 victims. Neither completed run had another SQL failure. All-attempt latency includes victims; throughput counts accepted requests only.

| Refs | C | Accepted / victims before → after | p50 ms before → after | p95 ms before → after | Accepted/s before → after |
| --- | --- | --- | --- | --- | --- |
| 1 | 1 | 30/0 → 30/0 | 6.653 → 6.330 | 9.722 → 8.111 | 137.124 → 145.738 |
| 1 | 4 | 30/0 → 30/0 | 8.254 → 7.131 | 23.549 → 16.569 | 324.426 → 453.780 |
| 1 | 8 | 30/0 → 30/0 | 9.229 → 9.012 | 36.931 → 18.757 | 597.599 → 682.667 |
| 32 | 1 | 30/0 → 30/0 | 51.886 → 11.230 | 59.182 → 19.215 | 19.938 → 79.099 |
| 32 | 4 | 18/12 → 20/10 | 205.364 → 204.855 | 9321.059 → 8755.929 | 1.644 → 1.473 |
| 32 | 8 | 5/25 → 7/23 | 638.185 → 529.669 | 2100.189 → 1431.744 | 1.609 → 2.802 |
| 128 | 1 | 30/0 → 30/0 | 57.375 → 11.258 | 66.969 → 15.517 | 17.404 → 83.727 |
| 128 | 4 | 7/23 → 7/23 | 323.545 → 313.089 | 6017.552 → 1617.087 | 0.807 → 1.944 |
| 128 | 8 | 4/26 → 3/27 | 792.844 → 523.215 | 5229.298 → 2003.403 | 0.653 → 1.045 |
| 256 | 1 | 30/0 → 30/0 | 106.298 → 18.749 | 118.080 → 25.182 | 9.310 → 50.626 |
| 256 | 4 | 8/22 → 9/21 | 413.454 → 308.000 | 9709.325 → 3222.792 | 0.590 → 1.671 |
| 256 | 8 | 4/26 → 4/26 | 739.436 → 478.034 | 3748.111 → 2134.119 | 0.676 → 1.263 |

At 256/C1, p50 falls 82.4%, p95 falls 78.7%, and accepted throughput rises from 9.31/s to 50.63/s. The 32/C4 arm is slower in accepted throughput (1.644 → 1.473/s) and its p99/max grows 9,323 → 12,142 ms. It has 20 accepted requests versus 18 before; its slowest source-lock acquisition grows 3,756 → 4,995 ms. The per-attempt arrays retain these key-lock/deadlock scheduling tails. Concurrent arms remain failing production attempts and do not establish healthy throughput or a portable speed guarantee.

## Resource and lock evidence

| 256 refs / C | Transaction p95 ms before → after | Retention interval p95 ms before → after | Client CPU ms before → after | Allocated MiB before → after | Peak RSS MiB before → after | Physical log bytes before → after |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 116.328 → 23.565 | 116.043 → 23.163 | 3710.8 → 352.1 | 898.64 → 302.17 | 304.74 → 345.17 | 5,554,176 → 5,484,544 |
| 4 | 9706.542 → 3220.696 | 8937.033 → 3071.940 | 3817.4 → 591.2 | 1321.26 → 310.10 | 326.49 → 349.96 | 2,367,488 → 3,166,208 |
| 8 | 3745.997 → 2128.116 | 3686.799 → 1685.268 | 3541.9 → 429.0 | 1280.18 → 294.36 | 340.29 → 362.03 | 1,945,600 → 1,712,128 |

All sizes retain raw transaction, shared-retention hold, source-lock acquisition, retention-lock acquisition, CPU/allocations/RSS and storage samples in the [before JSON](evidence/issue-1558/before/maximum-lineage-baseline.json) and [after JSON](evidence/issue-1558/after/maximum-lineage-bounded.json). Both runs grow the log file by 64 MiB in 32/C1 and have no further file growth, including maximum lineage. Negative used-log deltas reflect log reuse.

Cost differences are retained rather than normalized away. At 32/C8 CPU grows 784 → 865 ms while accepted work grows five → seven requests; allocations fall 195 → 63 MiB. At 256/C4 physical log bytes grow 2,367,488 → 3,166,208: accepted requests grow eight → nine and attempted EF write batches grow 79 → 92. Eight victims reach a second write batch after versus one before; their work rolls back. The leaf-insert proxy grows 9,246 → 10,780. At 256/C1, with all 30 accepted in both runs, leaf inserts/updates are identical (23,322/2), and physical log bytes fall 5,554,176 → 5,484,544. At one reference, log-byte differences reach +40,960 (C1) / +45,056 (C8) with unchanged leaf-operation counts and unchanged insert shapes; physical log/block and random-key index-maintenance variation remains in these proxies. No reduction in logical write volume is claimed.

Timing uses client timestamps: successful transactions end at commit callback; failed intervals end at error return after disposal and are upper bounds on server lock ownership. CPU/allocations/RSS are client process-wide (10 ms RSS sampling), with recurring host workers suppressed. SQL server CPU is available only as millisecond-resolution diagnostic plan CPU, not natural-concurrency process CPU. Operator row reads sum physical operator counters and are not distinct records. SQL has no STATISTICS IO logical-write counter; actual insert rows and leaf counters are named proxies, including aborted work. Physical log bytes depend on buffering/index layout and log reuse. Backlog/object payload I/O are N/A for direct closed-loop metadata calls.

## Deadlock, rejection, retention and restart proof

The retained write plans attribute the natural contention to foreign-key parent validation in the multi-row lineage INSERT. The derived-observation FK uses seeks; the source-observation FK uses a merge join and clustered scan of `EnvironmentalObservations`. Each concurrent ingest owns an exclusive lock on its newly inserted, random-GUID observation key. A parent scan can request a shared lock on another ingest’s uncommitted key. Retained graphs show shared-key requests cycling against exclusive owners on that clustered PK; the active first write batch has the captured parameter signature and lineage INSERT on line 4. Lookup SELECTs use seeks in the before plans, and captured mutations contain inserts with no referenced-entity UPDATEs. Reference tracking adds client work; it is not established as the cause of this insert-plan cycle.

The two projections do not eliminate these optimizer-selected FK scans. Before/after graphs are retained as `before/deadlocks.xml.gz` and `after/deadlocks.xml.gz`; host/login/PID attributes are redacted. System-health retention is asynchronous and bounded (before/after report counts are in the measurement JSON), so graph count can be lower than victim count. Every measured victim leaves no observation or cascading lineage and receives no receipt. No automatic production retry was added.

The [regression evidence](evidence/issue-1558/sql-regressions.json) records five expected failing-before amplification/tracking cases and five passing durability/rejection cases; all ten pass after. The maximum fixture additionally reuses observation GUIDs across different sources.

| #252 / #1558 criterion | Retained proof and disposition |
| --- | --- |
| Amplification before correction | `BoundedLineagePreservesQualifiedOrdinalsWithoutTrackingInputs` at 1/32/128/256 and `BoundedLineageMaximumDistinctSourcesStaysBelowSqlParameterLimit` fail on original production. At 256, source lookups are 257 instead of two; singleton tracks an extra referenced observation. Full before capture proves 512 lineage SELECTs. |
| Exact 256 ordinals / qualified identity / hashes / existence | All accepted measured and warm/priming rows are checked in fresh contexts (338 before / 342 after), including canonical payload hashes. SQL regression fixture uses repeated IDs under different source records and permutation order. After tracking contains only the new observation, with no referenced observation entities. |
| Missing, duplicate, mismatched references reject atomically | `BoundedLineageRejectsCrossProductPairsAndMissingOrDuplicateInputsAtomically`: missing source, missing observation, absent qualified pair whose source and ID each exist, duplicate reference. Observation/source/lineage counts remain unchanged, including after a subsequent SaveChanges. |
| Retention race and pins | `BoundedLineageRetentionRaceIsAtomicInEitherLockOrder`: DMV proves waiting APPLICATION locks. Ingest-first pins all 256 old inputs before sweep; sweep-first deletes them and ingest rejects without output. Each manual production sweep retains all 256 pinned inputs and deletes its unpinned old control. |
| SQL deadlock / retry atomicity | `BoundedLineageSqlDeadlockRollsBackPartialWritesAndFreshContextReplayConverges`: after the first real EF observation/lineage batch, a test-only competing lock cycle forces this request to be SQL1205 victim. Root observation, new source and partial lineage roll back. Disposed request context returns no receipt. Fresh-context replay accepts once, then returns Duplicate with exactly 256 lineage rows. |
| Restart / receipt-loss replay | `BoundedLineageProviderRestartReplaysAfterCommittedReceiptLoss`: interceptor loses the receipt after SQL commit; entire ingest DI provider/context is disposed and recreated. Replay returns Duplicate with the same durable ID/hash/receipt time and 256 rows. This is service/provider restart, not SQL-server/power-loss qualification. |
| Two bounded EF reads / parameters / plans | Accepted validation has two queries at every length; typed plans at all four sizes, worst 256-source bucket and 515-parameter complete-command budget retained. No TVP/procedure, schema/index or correlation change. |
| Resource / lock / log acceptance | Equivalent 12-arm data retained, including every failed attempt and the cost differences above. Natural deadlocks remain; measured throughput includes their failures. W3M/W4 and production/shared-server qualification remain separate. |
| Tier C local evidence | See selector and gate section below. |
| Independent review / current-head protected CI / merge | Parent-owned and pending in this delegate report. Target profile is development-v1; no remote full ci.yml dispatch, PR mutation, push or merge is performed by this delegate. Do not treat this report as issue closure or production qualification. |

## Selector, local gate and signals

Selector: `scripts/ci:classify pull_request e20f945d8f7108cb7a15e272dc42a6289107b2ed 924b34b8d60d878de5ca206ee88ea3ef3ba4cb30`. [Output](evidence/issue-1558/classifier.txt): `mode=full complete=false deployment=false logichost=true combined=true`; shared/CameraAgent/delivery lanes are false. Profile **development-v1**, risk **C**. Tier C requires the complete local candidate gate; lane flags additionally select LogicHost and combined canonical coverage. `complete=false` is authoritative and does not authorize full remote qualification.

All required local gates below passed on the validated code head `924b34b8`. The Unit selection used `DOCKER_HOST=unix:///tmp/hvo-no-docker.sock` with the production catalog variable unset: **13,330 selected, 13,206 passed, 124 skipped/inconclusive, zero failed**. The separate real-Docker Integration selection passed **949/949**, including all ten new SQL cases and the existing architecture/publish cases. Category discovery and the updated inventories retain the complete selections.

The canonical LogicHost and combined coverage gates use the exact coverage attachments from those solution selections. The initial local wrapper stopped after passing Integration because the TRX collector href omitted its GUID directory. The repaired mapping binds the unique per-project attachment in the matching phase console log to its canonical slot, with source/TRX/coverage hashes in [coverage-mapping.json](evidence/issue-1558/coverage-mapping.json). No passing suite was rerun; only the remaining gate suffix resumed. [Wrapper disposition](evidence/issue-1558/local-wrapper-disposition.json).

| Gate | Result |
| --- | --- |
| `dotnet tool restore` | PASS |
| `dotnet restore` | PASS |
| `dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Debug -warnaserror -nodeReuse:false -p:UseSharedCompilation=false` | PASS |
| `dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false` | PASS |
| `dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes` | PASS |
| `./scripts/package:audit` | PASS |
| `./scripts/ci:shell-syntax` | PASS |
| `./scripts/test:ci-classification` | PASS |
| `./scripts/test:coordination-guard` | PASS |
| `./scripts/test:pr-review-tools` | PASS |
| `dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter TestCategory=Unit --settings tests/coverage.runsettings '--collect:XPlat Code Coverage' --logger 'trx;LogFileName=candidate-unit.trx' -nodeReuse:false -p:UseSharedCompilation=false` | PASS |
| `dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter TestCategory=Integration --settings tests/coverage.runsettings '--collect:XPlat Code Coverage' --logger 'trx;LogFileName=candidate-integration.trx' -nodeReuse:false -p:UseSharedCompilation=false` | PASS |
| `./scripts/coverage:policy pull_request development/v1` | PASS |
| `./scripts/docs:audit-operations` | PASS |
| `./scripts/ci:canonical-migration logichost` | PASS |
| `./scripts/ci:canonical-migration cameraagent` | PASS |
| `./scripts/coverage:component logichost` | PASS |
| `./scripts/coverage:component combined` | PASS |
| `dotnet run --project scripts/test-categories/HVO.SkyMonitor.TestCategoryAudit.csproj --configuration Release` | PASS |
| `dotnet ef migrations has-pending-model-changes --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj --startup-project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj --context HVO.SkyMonitor.LogicHost.Data.ApplicationDbContext --configuration Release --no-build` | PASS |
| `dotnet ef migrations has-pending-model-changes --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj --startup-project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj --context HVO.SkyMonitor.CameraAgent.Data.ApplicationDbContext --configuration Release --no-build` | PASS |
| `./scripts/ci:component-publish logichost` | PASS |

All heavy commands ran under `flock -o /tmp/hvo-heavy-gate.lock timeout ...`; build/test arguments disable node reuse/shared compilation. The exact command ledger, timestamps, configuration and environment overrides are in [candidate-gates.json](evidence/issue-1558/candidate-gates.json), with retained sanitized command logs under `gate-logs/` in the [checksum manifest](evidence/issue-1558/sha256.json). The standard candidate gate ran once; unchanged passing tests and measurements were preserved.

Architecture Integration retains four host/runner publish outputs under `TestResults/publish/issue1558/`; the selected LogicHost component output and checksums remain under `TestResults/publish/component/`. Its checksum manifest is [retained here](evidence/issue-1558/publish-logichost.sha256). No publish artifacts were deleted. This local evidence does not replace parent-owned final synchronization, independent review or protected development-v1 CI.

The unchanged runtime signal contract remains `environment.ingest` / `environment.retention` spans; ingest events 2500, conflict 2502, retention 2510/2511; bounded `skymonitor.environment.*` counts/durations/payload/clock/conflict/retention instruments. No ID/provider/payload label is introduced. Existing environmental telemetry, ingest, retention and health tests run in the solution selections. Expected fault exceptions are asserted by the SQL fault tests; no internal retry loop, new queue, or new health/configuration surface is added.

Evidence limitations remain explicit: disposable SQL and synthetic metadata, one paired host run, SQL deadlock scheduling variance, covering scans at the retained cardinality, direct service/provider restart, no HTTP payload/object-store/hardware/deployed capacity or W3M/W4 replacement claim. Parent review, final synchronization and protected v1 CI remain required before closure.
