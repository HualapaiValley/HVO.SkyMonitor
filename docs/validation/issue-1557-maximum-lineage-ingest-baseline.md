# Maximum-lineage LogicHost ingest baseline (#1557)

Refs #252. This is slice A's measurement and fixture handoff to #1558. The
production `EnvironmentalObservationIngestService.IngestAsync` implementation,
schema, indexes, retention policy, and temporal-correlation path are unchanged.
The existing W3M/W4 budgets remain authoritative; this narrower 5+30 metadata
workload does not replace their qualification evidence. #252 remains open for
slice B's complete acceptance, including its wider fault matrix.

## Reproduce

Use the pinned SDK from `global.json`, Docker, and a clean checkout of the
measurement revision recorded in the retained evidence. Build the current
Release integration-test project before setting `HVO_EVIDENCE_REVISION`. The
source identity helper verifies Release configuration, assembly revision and
freshness, and records assembly/source hashes. Dirty or unrequested runs are
explicitly marked unclaimable.

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  dotnet build tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false

flock -o /tmp/hvo-heavy-gate.lock timeout 1800 \
  env -u HVO_PRODUCTION_CATALOG_BUNDLE DOTNET_gcServer=1 \
  HVO_ISSUE_1557_EVIDENCE=1 HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
  dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter FullyQualifiedName~Issue1557_MaximumLineage_RecordsProductionBaseline \
  -nodeReuse:false -p:UseSharedCompilation=false
```

The fixture starts pinned disposable SQL Server/Redis/Mailpit containers and a
filesystem-backed test host. Recurring host workers are suppressed for this
command. The measured service uses a separate migrated database, deleted in
`finally`; no shared service database or deployed state is used. Retain evidence
before removing disposable build/test state.

The output is under `TestResults/issue-1557/<source>/<run>/`:

- `maximum-lineage-baseline.json`: source/environment identities, all 12 arms,
  raw latency/transaction/lock samples, process resource measurements, per-arm
  SQL counts and storage deltas, correctness and negative-control results.
- `sql-replay.json.gz`: one complete maximum-lineage command sequence, exact
  parameter names/types/sizes/precision/scale/values, actual execution plans,
  per-operator estimated/actual/read rows, result rows and `STATISTICS IO`.

All input values are synthetic. Connection strings and credentials are omitted.
The test checks rollback of diagnostic writes before deleting its database.

## Measurement boundaries

The fixture ingests 256 raw observations across eight source identities through
production ingest. Ordered derived lineage uses permutation `i*73%256`, making
an accidental reorder visible. Each arm primes its derived sources, performs
five untimed warm-ups and 30 timed ingest attempts. Concurrency is 1, 4 or 8; derived
source identities are assigned round-robin across that many lanes. The setup
avoids imposing one exclusive source lock on every concurrent request. SQL 1205
deadlock victims are recorded as failed attempts with their observed latency
and command/resource cost; they are never retried or counted as accepted
throughput. Accepted counts and all 30 outcomes accompany each arm. Other
exceptions still abort. This measurement does not qualify a failing production
path; slice B must resolve its deadlock/retry acceptance. Each completed arm is
flushed to `progress.json` so a later failure cannot erase it. Sanitized
`deadlocks.xml` retains available system-health deadlock graphs.

Wall time includes context construction/disposal; successful transaction time runs from
EF's transaction-start callback through its commit callback. Retention hold time
runs from successful shared `sp_getapplock` completion through that commit
callback. For a victim, the interval ends when the call reports failure after
disposal; it is an upper bound on server lock ownership, not a successful commit.
Those are client timestamps and include return-path overhead. Lock
acquisition duration includes wait and round trip. RSS is sampled every 10 ms;
CPU/allocations are process-wide and include instrumentation. SQL Server runs
in a separate process and is excluded from those client resource figures.

Plans and logical reads come from a separate untimed exact-command replay in a
rolled-back transaction. They describe the mechanism, not simultaneous timing
or resource behavior of the natural concurrency runs. SQL Server has no
`STATISTICS IO` logical-write counter: database-specific leaf insert/update/
delete deltas and actual-plan write rows are named proxies. Log evidence reports
physical bytes written, file growth, and used-space delta; buffering and log
reuse can affect these values.

Every accepted observation, including warm-ups, is verified for count, ordinal,
qualified source/observation identity and canonical payload hash outside timing.
A new-context replay must preserve record ID, hash and receipt time. Missing
source, missing observation, mismatched source/observation pair and duplicate
reference controls must leave all observation/source/lineage counts unchanged.
The production retention sweep must retain all 256 old pinned inputs and delete
one old unpinned control. These are baseline controls; slice B retains #252's
separate retention-race, deadlock/retry and restart/fault acceptance obligations.

## Retained current-source result

Measured source: `d7fa7d8fd7568d4da90afc73f7596f825b4416d6` (clean Release assemblies; SDK 10.0.402, .NET 10.0.13, server GC, Ubuntu 24.04.4 x64, Intel Core Ultra 9 285H, eight effective processors; pinned SQL Server 2022 CU26). Recorded 2026-10-23T00:39:51.559517+00:00.

The [baseline JSON](evidence/issue-1557/maximum-lineage-baseline.json), [typed commands and actual plans](evidence/issue-1557/sql-replay.json.gz), [compressed deadlock graphs](evidence/issue-1557/deadlocks.xml.gz), and [SHA-256 manifest](evidence/issue-1557/sha256.json) are retained together. `gzip -dc deadlocks.xml.gz` restores the XML named by the baseline JSON; its decompressed checksum is included. No credentials, connection strings, private network addresses, or physical client identities are retained.

The workload completed 360 measured attempts: **240 accepted and 120 SQL1205 victims**. Five warm-ups per arm plus lane priming contributed 112 further accepted derived observations. All 352 accepted derived rows passed durable ordinal/composite-identity/hash verification. No victim was retried or replaced; the concurrency arms expose a production failure and do not qualify healthy ingest.

| References | Concurrency | Accepted / victims | Commands accepted / victim | All-attempt p50 ms | p95 ms | p99 ms | Accepted/s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 30 / 0 | 10 / — | 14.299 | 27.815 | 33.782 | 61.334 |
| 1 | 4 | 30 / 0 | 10 / — | 12.614 | 32.851 | 67.476 | 230.516 |
| 1 | 8 | 30 / 0 | 10 / — | 15.959 | 42.856 | 57.611 | 307.666 |
| 32 | 1 | 30 / 0 | 72 / — | 89.624 | 133.970 | 152.158 | 10.659 |
| 32 | 4 | 22 / 8 | 72 / 72 | 49.538 | 8755.266 | 9737.557 | 2.010 |
| 32 | 8 | 6 / 24 | 72 / 72 | 857.569 | 2361.132 | 2363.227 | 1.531 |
| 128 | 1 | 30 / 0 | 267 / — | 102.175 | 161.596 | 166.968 | 9.534 |
| 128 | 4 | 10 / 20 | 267 / 264 | 513.848 | 7510.024 | 8765.969 | 0.737 |
| 128 | 8 | 3 / 27 | 267 / 264 | 865.901 | 7521.764 | 9364.052 | 0.296 |
| 256 | 1 | 30 / 0 | 526 / — | 637.781 | 732.336 | 813.025 | 1.786 |
| 256 | 4 | 13 / 17 | 526 / 520 | 713.496 | 9346.361 | 11570.570 | 0.797 |
| 256 | 8 | 6 / 24 | 526 / 520 | 953.918 | 12540.046 | 14035.070 | 0.400 |

At 256 references each attempt performs **257 source and 257 observation lookups**: 512 lineage lookups plus two own-identity lookups. The 526-command accepted sequence comprises those 514 SELECTs, three target-binding reads, two application-lock commands and seven EF write batches. The replay contains 525 actual plans, 5,640 logical reads and 4,886 aggregate operator rows read. Actual write operators insert one observation and 256 lineage rows; there are no source inserts in this source-primed diagnostic. Each source SELECT returns a full 15-column entity; observation SELECTs materialize 27 columns. The repeated source-reference queries each use `nvarchar(64)`, while observation lookups use two `uniqueidentifier` parameters. The largest emitted write batch has 150 typed parameters.

| 256-reference concurrency | Transaction p95 ms | Retention interval p95 ms | Client CPU ms | Allocations MiB | Peak sampled RSS MiB | Physical log bytes written | Log file growth bytes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 726.239 | 724.322 | 13385.568 | 909.00 | 354.55 | 5,472,256 | 0 |
| 4 | 9342.515 | 9341.665 | 8533.445 | 1329.90 | 349.36 | 3,092,480 | 0 |
| 8 | 12536.153 | 12535.218 | 5436.276 | 1319.94 | 390.53 | 2,174,976 | 0 |

The concurrent transaction/retention distributions include victims, whose intervals end at client error return. Those are upper bounds on server lock ownership. Source-lock acquisition p95 at C1/C4/C8 was 1.657/965.729/5,000.997 ms; shared-retention acquisition p95 was 2.183/1.624/0.880 ms. Allocations include process-wide instrumentation and aborted work. Index leaf counters also include attempted work that may later roll back; they are not durable inserted-row counts. Log used-space deltas can be negative after log reuse (the 32-reference/C1 arm grew its log file by 64 MiB); there was no further file growth in the maximum-lineage arms. The separate replay reports 3 ms summed, millisecond-resolution SQL CPU and cannot substitute for natural-concurrency server CPU or client CPU.

All four negative controls preserved source/observation/lineage counts. Replay preserved the original record ID, receipt time and content hash. Diagnostic SQL replay rolled back its writes. The production retention sweep kept all 256 expired pinned inputs and deleted the one unpinned control.

The disposable server retained 106 graphs for the 120 victims. Their captured resources are key locks on `EnvironmentalObservations` / `PK_EnvironmentalObservations`, with shared-lock requests cycling against exclusive owners in read-committed user transactions. Truncated parameter-list input buffers and unknown frames prevent attributing the exact causal statement. This supports a concrete deadlock/retry investigation in #1558; it does not establish a new index or procedure requirement.

Before/after optimization comparison is N/A in slice A: production code is unchanged and this is its current-source baseline. Latency varies with this host/topology; no portable throughput or budget threshold is inferred. Slice B must compare equivalent workloads, report every failed attempt, and preserve the existing W3M/W4 qualification boundaries.

## Slice B handoff

Use the retained command sequence to distinguish own-source/own-observation
resolution, lineage lookups, lock commands and EF write batches. The current
lineage loop materializes full source and observation entities for each input;
a repeated source identity still causes another SQL lookup.

The smallest approved candidate is two bounded EF projections: distinct source
identity hash to source record ID, then qualified source record ID and
observation ID to observation record ID. Reconstruct references in their exact
original ordinals, reject unresolved composite pairs atomically, and preserve
transaction/retention-lock coverage and canonical hashes. A set of observation
IDs alone is insufficient to establish qualified identity.

There are at most 256 source hashes and 256 qualified pairs. An explicitly
expanded predicate needs at most 256 `nvarchar(64)` parameters for the first
query and 512 `uniqueidentifier` parameters for the second, before any fixed
parameters. Deduplicating source IDs can reduce that bound. Verify the actual
EF translation and parameter metadata on the pinned provider: a collection
translation or padded parameter bucket may differ from that arithmetic. These
budgets leave ample room beneath SQL Server's documented
[2,100 procedure-parameter boundary](https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server?view=sql-server-ver16);
include provider/RPC overhead when validating the complete emitted command. A
TVP/procedure remains disallowed without measured justification after the
bounded EF candidate. No correlation/index or retention redesign follows from
lookup amplification alone.
