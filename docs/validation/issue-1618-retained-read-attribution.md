# Retained installation-read attribution (#1618)

This narrows the remaining work in #1041 after
[#1559](issue-1559-protected-read-attribution.md), #1578, and
[#1591](issue-1591-schedule-validation-reuse.md). The current target already
contains same-load annual schedule-validation reuse. This study does not count
that gain again, close #1041, change persistent authority or recovery policy,
introduce a cache, or fix the excluded endpoint file-reopen race.

## Boundary and diagnostics

The real authenticated disposable Kestrel endpoint still loads actual current
configuration, initializes the real protected deployment-location store, and
checks installation/owner identity. Retained-file traversal, symlink protection,
file reads, full manifest parsing/contract validation, provenance checks,
read-only journal SQL and pre-mutation ordering are unchanged.

Diagnostics use the existing unregistered `.Attribution` source. File and journal
spans carry numeric aggregate leaf counts, elapsed time, materialized bytes and
synchronous thread allocation. Protected-history and manual-state read spans
separate security checks, file read, unprotection, deserialization and validation.
Each scan retains a fixed number of spans and counters, independent of corpus
size. No payload, token, actual operator location or private runtime path is
added to tags. No operational trace registration, logging/metric/health contract,
worker, retry or admission policy changes.

File enumeration includes enumerator creation and the terminal `MoveNext`;
5,000 files therefore have 5,002 enumeration attempts. File reads number 5,000.
Journal query counts include the schema-table probe and reader creation; the
5,001 read attempts include the terminal `Read`. SQLite stepping, filtering and
blob materialization fall within query/read, rather than a separately inferred
SQL execution time. Manifest counts/bytes are recorded before parsing. Failed
leaves count as attempts, while byte totals count successful materialization.

Leaf timers do not nest within a scan, but exclude wrapper/accounting overhead
and disposal. Inclusive parent spans overlap and must not be added to their
children. Existing span allocation is process-wide and includes the client,
server and listener. Leaf allocation is thread-local for synchronous intervals;
async protected reads report elapsed time/bytes without a thread-allocation
claim. Neither is an allocation-stack or per-stage CPU profile. Concurrent
request/span allocation intervals overlap and cannot be summed into per-request
allocation. Full endpoint CPU, allocation, logical I/O and RSS remain separately
measured process counters. The derived concurrent summaries retain the valid
batch process-allocation counter and omit aggregate process-allocation values
for spans, whose overlapping intervals repeatedly count the same allocations.
Individual span deltas remain in the raw archive; sequential summaries are
unchanged. This correction uses existing evidence and reruns no measurement.

## Workload and method

The workload is the same W3M-shaped fixed/solar corpus as #1559/#1591: seven
all-day fixed windows or seven astronomical-dusk-to-next-dawn windows, the
160 x 120 Mono16 browser rig, fixture catalog, and Phoenix time zone. Each host
has 5,000 manifest files and 5,000 location-bearing journal manifests in the
real v15 SQLite journal. The synthetic manifests identify 2 x 2 Mono16,
eight-byte payloads; no payload processing or throughput is measured. The corpus
contains 24,228,894 serialized manifest bytes; the main journal is 26,124,288
bytes excluding WAL/SHM. The ordered manifest SHA-256 is
`8285C4E1B9B1435C2A42A0DF6CBC311B43F74966040D3B245637ECF4FCD9E81B`.

Baseline production is `dffab19529c55769e25fbd162fe06b248386eed0` and measured
diagnostic production/harness is `26ac697fdc71eb7b822e8138cc1c26691fecbf97`.
Both are committed sources; native test checkouts are clean. A report-only
commit follows measurement without changing executable inputs. The source
manifest records file hashes and full commands.

Five listener trials and five settled no-listener controls use the same candidate
commit, interleaved I,C,C,I,I,C,C,I,I,C. Two current-target baseline listener
trials bracket that cohort for context; two trials do not support a production
non-regression or improvement guarantee. Each trial retains fresh-host empty,
first populated no-listener, five early no-listener, first instrumented/control,
120 settling, 30 measured, two four-request batches, actual configuration edit
and actual audited manual-location edit phases. Controls use
`HVO_INSTALLATION_READ_MODE=no-listener`, and run the same settling and measured
phases. Five early listener-free reads are not used as the settled control.

The primary sample unit is a trial's median of 30 measured sequential endpoint
reads, with the five-trial median and min/max reported. There is no pooled
p95/p99 inference from five trials. Early/late settling and measurement remain
separate to expose phase drift. A consistent change beyond cohort ranges,
accompanied by corresponding unchanged-stage behavior, would merit a runtime
regression investigation; overlapping ranges leave the direction uncertain.
Listener/control differences assess diagnostic observer cost, not an
optimization. There is no new portable latency budget or Pi qualification.

Measurements run native Release on Ubuntu 24.04.5 x64, Intel Core i9-14900K
virtual CPU, 12 host CPUs and about 15 GiB host RAM, SDK 10.0.402 / .NET 10.0.13.
Each test process has one kernel-affined CPU, `ProcessorCount=1`, workstation GC
and exactly 402,653,184 bytes of available managed heap. This is not an RSS cap;
OS caches, native memory and host services remain outside it. Host load/process
snapshots are retained per trial, and the host heavy-gate lock serializes tests.
Warm logical reads are not physical disk throughput. Sampled RSS and
process-lifetime peak RSS include different phases; peak includes fixture setup.
Optional GC snapshots are outside measured counters; forced collection occurs
only after both fixtures and all timed phases are disposed.

Pre/post checksums include retained files, protected history, marker and journal
main/WAL, excluding SQLite SHM. Actual reads must leave them unchanged. Whole
configuration hashes vary because the disposable root is serialized into
configuration; semantic identities and deterministic corpus hashes are compared
instead. Four-request batches describe bounded caller drain, not durable worker
backlog or sustained parallel scan capacity. There is no worker backlog in this
fixture, no outage/recovery workload, and no image I/O or LOH-specific analysis.

## Reproduction

Use a short absolute directory registered with `hvo scratch HVO.SkyMonitor 1618`;
the #1578 socket preflight applies. Build each pinned source before constraining
the runtime, then use a fresh output directory per trial:

```bash
project=tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  dotnet build "$project" --configuration Release -warnaserror \
  -nodeReuse:false -p:UseSharedCompilation=false

# mode=listener or no-listener; cpu is an allowed CPU from taskset -pc $$.
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  DOTNET_PROCESSOR_COUNT=1 DOTNET_GCHeapHardLimit=0x18000000 \
  HVO_INSTALLATION_READ_MODE="$mode" HVO_INSTALLATION_READ_SETTLING_READS=120 \
  HVO_INSTALLATION_READ_GC_DIAGNOSTICS=1 \
  HVO_INSTALLATION_READ_EVIDENCE_DIR="$evidence_root" \
  HVO_ATTRIBUTION_REVISION="$(git rev-parse HEAD)" \
  taskset -c "$cpu" dotnet test "$project" --no-build --no-restore \
  --configuration Release \
  --filter 'FullyQualifiedName~InstallationVerificationReadTests.ConstrainedDisposableReadsRecordStageAttribution' \
  --logger 'trx;LogFileName=attribution.trx' --results-directory "$evidence_root/trx" \
  -nodeReuse:false -p:UseSharedCompilation=false
```

The baseline ignores the new mode variable and retains its original listener
behavior. Recompute a trial's values by selecting `samples[kind="repeat"]`,
taking each field's median, then taking the median/min/max of the five trial
medians. Stage values use matching span names and numeric tags within each
sample. Retain phase boundaries, source pins and checksum comparisons. Generated
`iv-*` host/socket state is removed on disposal; reports and TRX are retained.

## Results and observer cost

Each table cell is the median of five trial medians, with min–max. Times are ms;
allocated/RSS values are MiB. All outliers remain included.

| Endpoint metric | Fixed listener | Fixed control | Solar listener | Solar control |
| --- | ---: | ---: | ---: | ---: |
| Elapsed | 167.62 (166.69–184.76) | 174.16 (164.31–239.90) | 185.33 (175.21–381.20) | 182.17 (175.00–185.66) |
| Process CPU | 164.82 (159.51–175.67) | 172.79 (158.90–235.23) | 177.75 (172.11–366.46) | 177.67 (161.56–182.20) |
| Process allocation | 120.54 (120.54–121.16) | 121.12 (120.51–121.13) | 126.58 (126.58–126.58) | 126.55 (126.55–126.55) |
| Maximum sampled steady RSS | 309.78 (304.63–340.99) | 314.68 (303.94–348.07) | 395.80 (387.89–426.92) | 416.44 (378.58–449.52) |

| Diagnostic stage | Fixed time | Solar time | Fixed synchronous leaf allocation |
| --- | ---: | ---: | ---: |
| Retained files, inclusive | 90.12 (87.58–94.86) | 89.96 (86.65–191.87) | N/A: inclusive process allocation is a different counter |
| File enumeration | 1.41 (1.37–1.47) | 1.38 (1.33–3.98) | 1.26 MiB |
| File symlink/security checks | 17.12 (16.76–17.52) | 17.77 (16.63–32.73) | 3.32 MiB |
| File read/materialization | 14.88 (14.53–15.24) | 15.08 (14.46–34.95) | 11.98 MiB |
| File full manifest parse/validation | 54.59 (53.15–57.41) | 54.89 (53.17–117.65) | 44.83 MiB |
| File provenance/history comparison | 0.41 (0.28–1.22) | 0.37 (0.27–0.74) | 0.46 MiB (0.46–0.76) |
| Retained journal, inclusive | 70.82 (68.24–75.13) | 70.69 (68.00–156.45) | N/A: inclusive process allocation is a different counter |
| Journal open | 0.054 (0.052–0.058) | 0.051 (0.051–0.079) | 272 bytes |
| Journal schema probe/reader creation | 0.512 (0.505–0.522) | 0.517 (0.502–0.815) | 1,152 bytes |
| Journal stepping/filtering/blob read | 14.17 (14.11–15.35) | 14.41 (13.93–36.91) | 11.67 MiB |
| Journal full manifest parse/validation | 54.01 (52.59–56.69) | 54.56 (52.66–116.02) | 44.83 MiB |
| Journal provenance/history comparison | 0.41 (0.27–1.22) | 0.31 (0.27–0.74) | 0.46 MiB (0.46–0.76) |
| Protected-history read, inclusive | 0.178 (0.173–0.246) | 0.173 (0.138–0.257) | N/A: async read allocation is not thread-local |

The protected history is 1,076 protected bytes and 991 plaintext bytes. Fixed
median leaf times are 0.022 ms file read, 0.074 ms unprotection, 0.032 ms
deserialization and 0.034 ms validation; solar equivalents are 0.039, 0.052,
0.033 and 0.033 ms. Manual-state protection is separately visible after the
real location edit: inclusive times are 1.16–1.37 ms fixed and 0.13–0.27 ms solar
(one edit per workload/trial, not a steady-state manual-history benchmark).
Marker checks, lock wait, reconciliation and other initialization work are not
mislabelled as cryptographic cost.

The combined parse leaves occupy roughly two-thirds of retained-validation time
and allocate about 89.65 MiB, versus whole-read allocation around 120–127 MiB.
These leaves include `CaptureContractJson.ParseManifest`: schema DOM inspection,
typed deserialization and the full current manifest contract validation. The
record does not isolate those internal subcosts. Journal access is more than its
small `ExecuteReader` timer: SQLite filtering/stepping and BLOB materialization
are in the approximately 14 ms read stage. Eliminating the measured schema probe
alone cannot eliminate that work.

All 300 candidate instrumented steady reads have exactly 5,000 file and 5,000
journal parses, one protected-history read, one retained scan and one annual
expansion. The corresponding 300 no-listener controls emit no attribution spans.
All 24 workload pre/post retained checksums match; semantic installation, rig,
schedule, location and catalog identities match equivalent trials. All bounded
concurrent batches keep four annual expansions and 40,000 materialized manifests
per four-call batch. Real configuration and audited location changes return
changed identities. No retained validation or annual expansion was bypassed.

Observer disposition: solar process allocation differs by about 30,000 bytes per
read (0.023%), while fixed allocation has an approximately 0.62 MiB phase shift
whose ranges overlap between modes. Synchronous parse allocation is stable;
fixed provenance leaf allocation differs between trials, so the whole fixed
allocation shift cannot be attributed solely to the listener. No-listener mode
creates no attribution state/timers/tags. This establishes the disabled control
route and bounded diagnostic state, but does **not** establish zero CPU overhead
from inactive call sites or a precise listener latency penalty.

The ranges overlap, and the large timing changes occur in both modes. Fixed c4
is 239.90 ms; solar i4 is 381.20 ms. Solar i4's file parse, journal parse and
file/security/read stages all slow together; its annual stage also slows without
changing its count. Bracketing baseline medians move 176.31→194.17 ms fixed and
184.22→201.64 ms solar. Within baseline b2 fixed, early→late measured medians
move 179.2→361.4 ms; c3 fixed moves 157.2→255.1 ms. Neither the bracketing
baseline nor the mode comparison establishes a production latency regression or
improvement. No outlier is silently removed or credited to diagnostics.

Host snapshots are taken before acquiring the heavy lock, not continuously.
They show 12 CPUs, load roughly 2.7–3.4 around the anomalous candidate trials,
background orchestration/test-supervisor processes, and a transient GitHub CLI
process at c4 start. The b1 snapshot includes this study's own build, which
released the same lock before measurement began. They do not identify the cause
of every in-trial slowdown or prove exclusive host/hypervisor capacity. This
limits precise timing-overhead inference. Repeating a whole cohort without
addressing that limitation is not warranted by these data.

Warm logical I/O is about 32.7 MB/read, around 10,100 read syscalls, with zero
median physical storage reads; diagnostic and control routes both retain full
input reads. Median four-request completion is roughly 690/690 ms fixed
listener/control and 756/738 ms solar, about 5.8/5.8 and 5.3/5.4 caller requests/s.
Solar listener batches span 713–1,258 ms and control batches 714–809 ms across
trial batch medians; this is caller drain with timing uncertainty. RSS ranges
overlap. GC snapshots remain in the raw record; these observations do not claim
an exact live-heap, retention, LOH or hard-RSS regression diagnosis.

## Next decision

**No bounded executable optimization is supported yet.** The measured target is
full manifest parsing/validation across both sources, rather than protected
history or enumeration. Every corpus manifest is a distinct retained record;
there is no demonstrated same-load duplicate parse to reuse. The aggregate parse
stage does not identify a redundant validation or prove that a cheaper parser
preserves the accepted/rejected contract. A narrower parser-internal experiment
could distinguish schema inspection, deserialization and validation before
selecting a change; it is not a queued implementation or an estimated gain.

Any future parser change must retain current schema, tamper/provenance and
exception behavior over the same bytes and protected history. Pure per-call
scratch/serializer metadata needs no retained-result invalidation and must not
publish mutable state between concurrent loads. A future proposal that reuses
validation results would additionally need exact byte/content identity for every
file and a consistent journal read boundary, the current protected history and
installation identity, actual configuration/location edits and cancellation/
failure isolation. File length/timestamp, path, location version or prior success
alone is insufficient. No such cross-request validity authority, snapshot,
single-flight or cache is selected here. #1041 remains open.

The [twelve-trial raw archive](issue-1618-retained-attribution.json.gz) has SHA-256
`5c61454e01604e573e7bb86693367898c6e321bef940365ac1f8d9d332faedd2`.
The [per-trial summary](issue-1618-retained-summary.json) retains early/late
phases and stage ranges; the [input manifest](issue-1618-retained-inputs.json)
pins sources and artifact checksums. Only disposable synthetic fixture paths
appear in endpoint identities; no installed system was read.

## Correctness and local validation

Tier C, target profile **development-v1**. The selector is
`scripts/ci:classify pull_request dffab19529c55769e25fbd162fe06b248386eed0 26ac697fdc71eb7b822e8138cc1c26691fecbf97`:
`mode=full complete=false cameraagent=true combined=true`; other component,
deployment, release-script and secret-scan-contract flags are false. The complete
local candidate gate uses canonical per-project positive Unit and separate
Integration selections, including the supplemental Unit assemblies, with TRX
and canonical coverage slots. This is local evidence; it does not dispatch full
qualification or replace required Development v1 checks.

Warning-clean Debug/Release solution builds, tool/solution restore, full format,
package audit, all four CI-control guards, selected component coverage,
architecture/publish, canonical migration shape/model checks, operations docs
audit and focused CameraAgent category discovery pass. Unit selects 13,677 cases:
13,553 passed and 124 explicitly skipped, with no failures. The 124 skips are
122 approved-catalog prerequisites, one OSX-only case and one provider whose
ambiguous-delete state is unreachable. Integration passes 948 cases in six
assemblies; seven separate architecture Integration cases pass. The 32
architecture Unit cases also pass (a redundant separately retained selection,
already included in the Unit total). CameraAgent enforced line/branch coverage
is 90.0423% / 76.6528%; combined is 35.0832% / 20.6958%. No new test method or
category inventory was introduced.

The [local gate ledger](issue-1618-local-gate.json) retains per-assembly outcomes,
commands, durations, selector, artifact hashes, original failures and corrections.
The original wrapper had 47/49 successful steps: coverage policy was incorrectly
given a SHA where it expects a branch ref, and LogicHost model checking omitted
CI's design-time connection environment. The first correction setup attempt
started no checks. Only those two checks were rerun, with a verified scratch
remote-tracking alias at the immutable baseline and the canonical CI design-time
environment; both passed. No product fix or acceptance waiver resulted.

The focused endpoint Integration regression passes on macOS and Linux. It covers
count/byte accuracy, bounded span volume, denied tokens, owner readiness, actual
same-length/timestamp edits, delayed/concurrent reads, audited location edits,
fresh file/journal provenance corruption, unsupported configuration/history,
unprotection failure, successful repair, absent-listener sequential/concurrent
helpers and retained pre-mutation checksums. Existing complete Unit selections
retain CLI timeout/caller-cancellation and installation field-comparison coverage.
No operator-visible behavior changed, so no changelog entry is required.

The first target synchronization merges `5c2f0f9860660578b00f225aadb45e0244b13432`
without conflicts before initial review. It changes unrelated calibration/cloud
option UI/descriptors and typed deployment teardown. The measured loader, store,
parser, harness and fixtures remain byte-identical to the measurement pins.
The initial review therefore covers this union; fresh selector/control and local
validation evidence for the synchronized head is recorded in the PR ledger.
