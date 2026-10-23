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
