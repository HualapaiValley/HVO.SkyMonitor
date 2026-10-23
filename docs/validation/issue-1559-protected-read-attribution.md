# Protected installation reads: current-source attribution (#1559)

This is the attribution slice of #1041, whose remaining-scope checkpoint is
[recorded here](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1041#issuecomment-6298546933).
It adds diagnostic spans, reproducible disposable fixtures, and regressions. It
introduces no cache, identity authority, validation shortcut, or installer change.
#1041 remains open; executable optimization belongs to a later measured slice.
The historical 35,049 ms observation is not a measurement of this candidate.

## Scope and method

The harness calls the real token-authenticated
`/api/internal/owner-bootstrap/installation-verification` endpoint over loopback
Kestrel. It uses the real configuration loader, Data Protection location store,
local Identity database and catalog fixture. Autonomous hosted services are removed
only in this fixture, so capture, retention, and reconciliation cannot mutate the
measured inputs. It contacts no installed system, camera, central host, or Docker
service. The client helper verifies that its address is the disposable host.

The two workload schedules are seven all-day fixed windows and seven
astronomical-dusk-to-next-dawn windows. Both use the browser fixture's small
160 x 120 Mono16 rig and Phoenix time zone. Each retains 5,000 valid v2 manifest
files plus 5,000 valid location-bearing manifests in the real v15 raw journal.
These are metadata-only records with eight-byte synthetic payload identities;
no acquisition, image processing, payload throughput or production-catalog claim
is made. Deterministic IDs, sequence reservation, schema initialization and
manifest validation are in
[`InstallationReadAttributionFixture`](../../tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/Infrastructure/InstallationReadAttributionFixture.cs).
The known capture time is 2026-01-02, and the initial retained location begins
2026-01-01, so a later real location change preserves the earlier evidence.

`cold-host-empty` is the first protected read on each newly created host, before
loader/retained-fixture setup. The first fixed workload also encounters cold
loader JIT; the solar workload shares that process. `first-http-no-listener` is
the first protected request with the retained corpus after setup, with loader JIT
and filesystem caches warm. These are distinct samples. There is no OS-cache
eviction, machine reboot, disk-cold claim, or ARM/Pi equivalence.

After five reads without the attribution listener, the harness attaches it,
records the first instrumented read, performs five warm-ups and ten sequential
measured reads, then two batches of four concurrent requests. It finally measures
a genuine configuration edit and a genuine audited manual-location edit.
The configuration edit atomically changes AgentId and schedule gain while keeping
file length and last-write time unchanged. The location edit is recorded through
`ApplyManualAsync`, and the next real loader reconciliation returns its new version.

The new diagnostic sources are not registered with the host's operational trace
provider. Normal host tracing is unchanged. The existing
`deployment-location.initialize` span supplies overall location initialization,
including semaphore wait. New spans distinguish deserialization, full config
validation, schedule compatibility and its 366-day expansion, retained-file
validation, and retained-journal validation. The existing validation order and both
annual expansions remain intact. Parent/child span IDs are retained in the JSON.

Allocation counters are process-wide, inclusive deltas from
`GC.GetTotalAllocatedBytes(true)`. They include the in-process client, server,
observer and any unrelated runtime work. Nested stages overlap and must not be
summed; concurrent stage allocation intervals also overlap across requests.
CPU and Linux `/proc/self/io` counters are process-wide deltas. `rchar` is logical
read bytes, `read_bytes` is physical storage I/O, and syscall counts include the
observer. Warm-cache physical reads can be zero while logical validation work
repeats. RSS is sampled after each request/batch; the peak counter is the process
lifetime high-water mark, including fixture setup. No per-stage CPU, allocation
stack, transient-memory attribution or cache-independent disk throughput is claimed.

## Reproduction

Use SDK 10.0.402 and an isolated issue worktree. Set `evidence_root` to a directory
registered with `hvo scratch HVO.SkyMonitor 1559 attribution`; retain the output.
Use a **short absolute path**: this directory also hosts the disposable recovery
socket at `iv-<32hex>/identity/owner.sock`. A normal deep worktree/TestResults
path can exceed the platform's Unix socket limit, which applies to encoded bytes,
not just characters. The harness preflights that prospective path before creating
host state and refuses an unsuitable root with a sanitized message directing you
to short registered scratch (#1578). Existing caller-owned report/TRX files stay
intact. Successful disposal removes the generated `iv-*` host directory and socket,
while retaining the output directory and its report/TRX. Copy retained outputs to
a deeper evidence location after disposal if needed.

The focused `InstallationReadEvidenceRootTests` cover long absolute roots,
multibyte path limits, relative-root guidance, and a real short-root host's cleanup
without rerunning the measured workload.
Build before applying the runtime constraints:

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 600 \
  dotnet build tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj \
  --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false

flock -o /tmp/hvo-heavy-gate.lock timeout 180 \
  env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  HVO_INSTALLATION_READ_EVIDENCE_DIR="$evidence_root" \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj \
  --no-build --no-restore --configuration Release \
  --filter 'FullyQualifiedName~InstallationVerificationReadTests.RepeatedDelayedAndConcurrentReadsObserveActualEditsAndRejectInvalidState' \
  --logger 'trx;LogFileName=regression.trx' --results-directory "$evidence_root/regression" \
  -nodeReuse:false -p:UseSharedCompilation=false

# Use an allowed CPU from taskset -pc $$; CPU 0 was allowed in this run.
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  DOTNET_PROCESSOR_COUNT=1 DOTNET_GCHeapHardLimit=0x18000000 \
  HVO_INSTALLATION_READ_EVIDENCE_DIR="$evidence_root" \
  HVO_ATTRIBUTION_REVISION="$(git rev-parse HEAD)" \
  taskset -c 0 dotnet test \
  tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj \
  --no-build --no-restore --configuration Release \
  --filter 'FullyQualifiedName~InstallationVerificationReadTests.ConstrainedDisposableReadsRecordStageAttribution' \
  --logger 'trx;LogFileName=attribution.trx' --results-directory "$evidence_root/measurement" \
  -nodeReuse:false -p:UseSharedCompilation=false
```

The kernel CPU affinity is the CPU bound; the managed heap limit is 384 MiB,
not an RSS limit. The test records affinity, runtime, processor count, effective
heap availability, per-sample timings/counters and fixture identities. There is
no portable latency assertion. The small Integration regression runs separately
from the explicitly selected Manual measurement; it exercises missing/wrong
tokens, current owner readiness, repeated reads, a controlled delayed read across
an edit, four concurrent reads, the same-length/timestamp edit, actual retained
location changes, fresh file/journal corruption, unsupported configuration and
unsupported protected-history schema. Each corruption is repaired and a successful
read is required before the next fault. Existing CLI timeout/cancellation and
identity comparison tests remain unchanged.

## Measurement and follow-up

The measured executable source is `2f2736ab3ed69a5706536957b0142243f4ccb725`,
based on `3e118ee8a69a05794bbe6904368e33fc37b6f132`. The following report-only commit
retains this evidence without changing the measured executable. The
[raw 44-sample JSON](issue-1559-protected-read-attribution.json),
[environment record](issue-1559-protected-read-environment.json), and
[source/fixture checksums](issue-1559-protected-read-inputs.sha256) are retained.
Raw JSON SHA-256:
`a1c4f0245a90c30218f50fcbc20cd91d297dce3cb7d12813b8afcd0c73f0c253`.

Measurements ran at 2026-10-22 23:46 UTC on Ubuntu 24.04.4 LTS, x64,
Intel Core Ultra 9 285H, .NET 10.0.13 / SDK 10.0.402, ext4, native test process.
The host has about 31 GiB RAM; the test's kernel affinity was CPU 0,
`Environment.ProcessorCount=1`, workstation GC, and effective managed heap
availability was exactly 402,653,184 bytes. This bounds CPU scheduling and the
managed heap, while OS caches and native allocations remain unconstrained.
The fresh-host samples include first-request work; the fixed sample includes
initial JIT and the solar sample benefits from prior work in the same process.
Neither is evidence of disk-cold retained-state latency.

Each workload contains 24,228,894 serialized manifest bytes. The main journal
file is 26,124,288 bytes, excluding WAL/SHM. The ordered generated-manifest
SHA-256 is identical for both workloads:
`8285C4E1B9B1435C2A42A0DF6CBC311B43F74966040D3B245637ECF4FCD9E81B`.
The raw JSON records each configuration-file hash and effective location hash.
The configuration file includes its disposable absolute storage root, so its
byte hash varies across fresh runs; the generated manifest corpus is independent
of that root. Pre/post checksums cover all retained files, protected history,
identity marker and journal main/WAL files, excluding SQLite SHM; they match
within each workload after the sequential and concurrent read phases.

All times below are milliseconds. Repeated samples have n=10; p95 uses nearest
rank and therefore equals the largest sample. Concurrent batches have only n=2,
with four requests each; no percentile inference is made for them.

| Protected-read sample | Fixed schedule | Solar schedule |
| --- | ---: | ---: |
| First fresh host, empty retained corpus | 428.66 | 342.74 |
| First HTTP read with retained corpus, no attribution listener | 200.08 | 239.11 |
| Five repeats without attribution listener, median | 192.52 | 242.45 |
| First instrumented retained read | 189.85 | 242.75 |
| Ten instrumented repeats, median / p95 | 196.81 / 293.24 | 245.15 / 407.77 |
| Four concurrent requests, median batch completion | 811.06 | 822.05 |
| First read after real configuration edit | 203.25 | 221.85 |
| First reconciliation read after real location edit | 386.71 | 408.19 |

The last two rows measure reads after the edits, excluding the explicit file
replacement and manual command itself. The location read includes reconciliation
and durable history/marker publication. It performs **two** retained-validation
passes, before reconciliation and against the updated history, preserving the
pre-mutation comparison semantics. Its allocation is approximately double the
unchanged-location read. The loader still performs two annual expansions.

| Repeated-read stage, median inclusive elapsed time | Fixed schedule | Solar schedule |
| --- | ---: | ---: |
| Whole configuration load | 194.44 | 243.39 |
| Configuration deserialization | 0.18 | 0.14 |
| Both 366-day expansions, summed within each sequential read | 4.75 | 60.93 |
| Retained location initialization, including gate wait | 189.19 | 181.97 |
| Retained validation, files plus journal | 185.12 | 166.20 |
| Retained manifest files | 107.01 | 91.66 |
| Retained journal manifests | 78.95 | 75.29 |

These rows are nested, so they cannot be added. Medians are computed per row and
also do not form an additive decomposition. Retained validation dominates these
10,000-record reads (roughly 94% of the fixed endpoint median and 68% of the solar
median); the annual expansions account for roughly 2% and 25%. The source performs
full manifest parsing/validation for every retained location-bearing record on
every call. This is stage attribution, not an allocation-stack profile proving
which parser internals allocate. The later solar workload's faster retained stage
must not be interpreted as a schedule effect: phase order, tiered JIT and GC can
change costs in this single-process run.

| Repeated-read resource, median unless stated | Fixed schedule | Solar schedule |
| --- | ---: | ---: |
| Process CPU, ms | 193.70 | 230.20 |
| Process allocated bytes / read | 128,273,636 | 141,071,676 |
| Retained-validation inclusive allocated bytes / read | 125,463,016 | 124,818,448 |
| Both annual expansions, inclusive allocated bytes / read | 2,506,632 | 15,955,372 |
| Logical read bytes / read (`rchar`) | 32,733,089 | 32,736,455 |
| Physical storage read bytes / read | 0 | 0 |
| Maximum sampled RSS during ten repeats, bytes | 313,057,280 | 397,365,248 |

No durable input changes occurred during the read-only phases. Four-request
batches allocated about 513 MB and 561 MB per batch, with median process CPU
772 ms and 816 ms, and about 131 MB logical reads each. The batches drain four
caller requests; they introduce no durable worker backlog. Their aggregate
throughput is about 4.9 requests/second under this single-CPU workload, with no
sustained-load or admission-budget claim. Concurrent stage durations/allocations
overlap and must not be added across requests. The process lifetime RSS peak
reached 438,771,712 bytes after the solar edits, including both fixtures and setup;
a managed heap cap is not an RSS cap.

The listener-control phases have similar medians (about +2.2% fixed and +1.1%
solar with attribution). Their sample counts and ordering do not isolate observer
overhead from JIT/GC/noise. These are diagnostic controls, not an optimization
before/after result. The whole command took 96 seconds, including setup; journal
seeding uses real durable transactions and writes substantially more than the
retained main-file size. Setup cost is excluded from the per-read numbers.

The concurrent requests serialize retained scans under
`ProtectedDeploymentLocationStore._gate`; manual-location commands also use that
gate. Within each four-request batch, initialization spans grow in steps from
about 185–268 ms for the first caller to 725–821 ms for the last, while each
retained scan takes about 165–256 ms. Initialization includes both gate wait and
the scan. These observations expose queued access to retained validation; they
do not establish parallel retained-scan throughput on additional cores.

The solar expansion series has two phases. Repeats 1–6 mostly take about 30 ms
per expansion, with 49–52 ms outliers, and allocate about 7.98 MB each. Repeat 7
transitions between phases; repeats 8–10 take 15.24–18.88 ms and allocate about
7.59 MB each, with whole-read times of 212.38, 208.70 and 215.29 ms. The allocation
and timing shift is consistent with tiered JIT, but these counters do not prove
its cause. The ten-repeat medians above span both phases.

## Smallest bounded follow-up

The smallest candidate supported here is **reuse of the first annual expansion's
validation within one `LoadAsync` call when the effective observer is exactly
unchanged after real retained-location initialization**. It would preserve both
configured/effective-location compatibility semantics, the current file load and
all retained-state checks. The same schedule, rig envelope/control inputs and
complete resolved observer (latitude, longitude, elevation and time zone) must
match. Changed observers must still expand again. This needs no cross-request
cache, invalidation generation or new identity authority. It is a proposal for
the next measured slice, not implemented or demonstrated as an improvement here.

One of the two identical expansions represents only about 2.4 ms for fixed
schedules, but roughly 30 ms for this solar schedule: an estimated upper saving
near 12% of the solar endpoint median before implementation overhead in the
earlier phase. The later phase instead suggests about 16 ms, or roughly 7–8%
of a 212 ms read. Neither estimate is a demonstrated optimization or a claim to
solve the dominant retained scan or historical live observation. Expansion reuse
would not shorten the retained scan's time holding the location-store gate.
The next slice should use interleaved before/after trials after tiering has
settled, report phase stability so warm-up is not credited as an optimization,
validate real observer changes, and retain latency and allocation evidence.
As a measured runtime change it requires the appropriate higher local risk tier
and independent protection/retained-state review.

Most cost here is retained parsing, which must continue detecting fresh retained
file/journal corruption and preserving both pre-publication checks. A later
parser/allocation investigation may be more valuable at this metadata scale than
an annual-expansion optimization. This slice provides no justification for
skipping retained validation or introducing a retained-state index/cache.

If a later issue proposes cross-request memoization, its contract must include:

- Identity from the actual current configuration bytes and supported schema,
  schedule definition, rig compatibility inputs, expander/solar algorithm
  versions, time-zone rule identity, and the **current validated effective**
  location snapshot/version. Length, timestamp, startup configuration or an
  installer manifest alone cannot be the version key.
- Real token authentication, owner readiness and retained location initialization
  on every request, including staged activation, manual/configuration changes,
  protected-history/schema validation and pre-mutation comparisons. Current
  identity must not become a previously cached installer identity.
- Independent retained-evidence generations if retained validation itself is ever
  proposed for reuse: location hash alone does not identify newly appended,
  edited, replaced or corrupted manifest/journal evidence. No such generation
  contract is established here.
- A bounded entry count, single-flight ownership per exact version, no publication
  of failed/canceled/partially validated entries, and safe retirement when a newer
  version arrives. In-flight old work must not overwrite or authorize a newer
  version.
- One consistent file snapshot for the returned configuration, schedule/rig hashes
  and configuration hash, or a content/version fence with bounded retry/failure.
  Today the endpoint reopens the file after `LoadAsync`; this report does not
  establish snapshot isolation for edits between those two reads. The deterministic
  delayed test edits **before** the real load proceeds, while the concurrent tests
  perform concurrent reads of stable inputs. A future optimization must explicitly
  cover edits during validation and between file opens, rather than claiming these
  tests settle that race.

## Validation and handoff

Risk tier remains B: runtime validation, authority, call order and persistence
behavior are unchanged; only opt-in diagnostics and tests were added. There is
no operator-facing behavior or changelog entry. The measurement is explicit
#1559 acceptance, not full milestone qualification. Required independent review
is deep because it covers protected installation reads and retained state.

The gate selector was `scripts/ci:classify pull_request` on immutable range
`3e118ee8a69a05794bbe6904368e33fc37b6f132..2f2736ab3ed69a5706536957b0142243f4ccb725`.
Target profile is **development-v1**. Exact selector output:

```text
mode=full
deployment=false
deployment_catalog=false
deployment_shards=false
deployment_installer=false
complete=false
shared=false
cameraagent=true
logichost=false
combined=true
delivery=false
secret_scan_contract=false
release_scripts=false
```

`complete=false` is authoritative for the selected component boundaries. The
local B gate uses affected warning-clean Release builds and these passing tests:
221 focused CameraAgent configuration/location/schedule Unit cases, 69 combined
Unit contracts, 29 CLI owner-bootstrap protection regressions, 27 acceptance Unit
cases, two real endpoint/owner-restart Integration cases and the explicitly
selected Manual measurement. Category count discovery and affected formatting
also passed: discovered acceptance counts are Unit=27, Integration=18, Manual=87,
and affected `dotnet format --no-restore --verify-no-changes` is clean. Four
Docker-free CI-control guards and the documentation inventory audit passed. The added Integration and Manual cases
update the owning inventory and runbook totals together. The CameraAgent Unit
selection was:

```text
TestCategory=Unit&(FullyQualifiedName~.Configuration.|FullyQualifiedName~ProtectedDeploymentLocationStoreTests|FullyQualifiedName~ManualDeploymentLocationStoreTests|FullyQualifiedName~CaptureScheduleRuntimeCoordinatorTests|FullyQualifiedName~CaptureScheduleEngineTests)
```

Combined contracts and acceptance Unit cases used `TestCategory=Unit`; CLI cases
used `FullyQualifiedName~OwnerBootstrapClientTests`. Builds and tests used Release,
`-nodeReuse:false -p:UseSharedCompilation=false`; Unit runs unset
`HVO_PRODUCTION_CATALOG_BUNDLE` and used the invalid Docker endpoint above. Final results are
recorded in the issue's handoff ledger, including a classifier rerun on the final
report-bearing head.

The measured JSON and source/fixture hashes above are committed evidence. Full
TRX/build/format/guard logs remain in registered scratch
`/tmp/hvo-skymonitor-1559-attribution.vsD506`; retain them through lifecycle handoff.
No protected workflow was dispatched. Parent owns deep review, final branch
synchronization, `Development v1 / Preflight`, `Development v1 / Build and Unit`,
and subsequent PR lifecycle. This evidence does not qualify production, close
#1041, or authorize a live deployment, promotion, release, or publication.
