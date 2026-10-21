# Performance Validation

This document defines the repeatable performance evidence required by the
[virtual-first completion plan](../project-plan.md). It does not impose one
portable latency or throughput target on every machine. It makes performance
decisions comparable and prevents an implementation from improving one stage by
silently moving cost, buffering, or backlog elsewhere.

## 1. Decision Rule

Choose the simplest correct design unless a more sophisticated design provides
one of these benefits under a named workload:

- A repeatable improvement larger than measurement noise in relevant I/O, CPU,
  allocation, working-set, throughput, latency, or backlog behavior.
- An explicit correctness property, such as atomic commit, bounded memory,
  indexed recovery, or ownership-safe buffer reuse, that the simpler design
  cannot provide.
- A measured path to an issue-approved budget that the simpler design misses.

The candidate must preserve checksums, numerical tolerances, ordering,
provenance, lineage, cancellation, retry, and crash-recovery behavior. Its new
ownership, invalidation, migration, and operational burden must be documented.
An unexplained material regression blocks merge.

SQLite WAL for indexed CameraAgent work state, reference-based fan-out,
streaming object-store transfers, and normalized SQL dependency rows are architecture
decisions justified first by durability, bounded ownership, or query
correctness. They still require implementation measurements. Ownership-safe or
pooled full-frame buffers are justified only where profiling confirms the LOH
pressure observed in `docs/validation/cameraagent-arm64.md` or an equivalent
named workload.

SIMD, GPU processing, broad pixel-loop parallelism, memory-mapped frame files,
Redis-authoritative queues, distributed brokers, generic plugin/ML platforms,
and specialized caches require issue-specific evidence before adoption.

## 2. Canonical Workloads

Use the smallest workload that exposes the changed behavior. A new or changed
full-frame algorithm gets a realistic output/resource check, but comparative
canonical benchmarking belongs only to a tier C measured-path issue or tier M
milestone. Do not rerun a phase benchmark for an unrelated tier A/B change.

| ID | Workload | Reference input and default scale | Use |
| --- | --- | --- | --- |
| `W0` | 64 x 48 deterministic Mono16, one object | `deploy/split-host/workloads/w0-virtual-mono16.json`; seed 2025, maximum 10 results, shot noise disabled, fixed profile/config hash | Fast fault injection and state transitions only |
| `W1` | ASI174 1936 x 1216 Mono16, 4,708,352 raw bytes | `virtual-asi174.full.json` and `hualapai-asi174-conformance-v1.json`; 5 warm-up plus 30 measured operations for an in-process algorithm | Canonical monochrome full-frame path |
| `W2` | ASI178 3096 x 2080 RGGB16, 12,879,360 raw bytes; 19,319,040-byte RGB24 preview | `virtual-asi178mc.full.json` with fixed fixture time/options/seed; 5 warm-up plus 30 measured operations for an in-process algorithm | Large color/CFA full-frame and memory path |
| `W3M` | 10,000 metadata-only durable records | Manifests, lane references, jobs, dependencies, or history rows with no payload duplication | Scan, index, claim, recovery, and pagination behavior |
| `W3P` | 100 `W2` payload-bearing captures | 1,287,936,000 raw bytes before derivatives, with metadata from `W3M` | Sustained file/object I/O, retention, and recovery without an unbounded 10,000-frame payload set |
| `W4` | Concurrency 1, 4, and 8 | 20 warm-up plus 200 measured operations per level using the same `W1` or `W2` payload and service topology | Upload, ingest, SQL claim, retrieval, and worker contention |
| `W5` | Existing 289-capture accelerated virtual day | `AcceleratedTwentyFourHours_ArtifactsAndBoundedOwnersRemainConsistent`; 64 x 48 correctness/retention workload | Sustained state, retention, cadence, and bounded-growth correctness, not a full-frame throughput claim |
| `W6` | ASI676MC 3552 x 3552 Bayer12-in-16, 25,233,408 raw bytes | Full production catalog, provisional 2.5 mm fisheye, five-second logical light exposure, separately declared cadence, and the standalone CameraAgent graph | Tier M standalone composition, storage, recovery, rendered output, and resource evidence |

Before `W6` is runnable, #211's readiness manifest pins the deployment location,
scene UTCs, rig/profile hashes, expected catalog row IDs and coordinate
tolerances, magnitude/result limits, cadence, operation count/duration,
retention limits, storage headroom, each fault/outage duration, initial backlog,
and drain budget. The full production catalog means the verified 119,625-row
snapshot is installed and identified; scene queries remain bounded and do not
render every catalog row.

The existing reference commands are:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~VirtualSkyCameraModuleTests.CaptureAsyncWithFullAsi174ProfileProducesCanonicalMono16Frame"

dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~AcceleratedCameraAgentSoakTests.AcceleratedTwentyFourHours_ArtifactsAndBoundedOwnersRemainConsistent"
```

These commands establish fixture correctness, not a general benchmark. Before
an issue is marked ready, its issue comment must provide an executable workload
manifest naming the command/harness, exact config/fixture/seed, operation count
or duration, arrival rate, initial backlog, outage/fault duration, concurrency,
warm-up, and measured stages. Terms such as configured generation rate,
representative DAG, recovery margin, and material latency must be replaced by
values in that manifest.

The split-host `measure` phase is an executable deployment harness for W1/W2.
`deploy/split-host/workloads/canonical-workloads.json`, not inventory, pins each
profile path, source/config/options identities, dimensions, format, seed, exact
five-operation warm-up, 30 measured operations, and concurrency. Both W1 and W2
set `"stellarCandidateSelection": "Exhaustive"`, so their measured star depth is
the profile's own 32,768-candidate query and never the target's operator scene
object limit; that opt-out is part of the pinned options hash. The phase
activates the selected profile, restarts the target, and uses authenticated
pause/resume capture control to delimit both windows. After each pause it rejects
in-flight overshoot, proves the exact ordered sequence/capture-ID set, and waits
for local queue drain plus central frame, artifact, checksum, derivative, and
provenance convergence. Raw proof binds each local raw artifact ID, checksum, and
byte length to the central Raw artifact for the same sequence and capture ID. It
resets collectors and captures an empty baseline only
after warm-up convergence; the final snapshot is taken only after measured-set
convergence. Warm-up and measured correctness/drain identities are retained
separately. Boundary-specific command attempts and idempotency keys are durable;
recovery reconciles live capture-control state and recorded capture boundaries
without replaying a completed workload. Failure handling attempts a fresh safety
pause for every possibly resumed target and records `capture-may-be-running`
while retaining cleanup credentials when that pause cannot be verified. Only the measured window contributes telemetry, timing,
queue/backlog, and Docker resource snapshots. The deployment contract runs this same fixed representative hook;
Tier M candidate evidence applies the trial/reporting rules below.

An issue may add a named workload when these do not represent its access
pattern. The PR must state why it was added and how another agent can reproduce
it.

## 3. Evidence Record

Every tier C/M implementation issue records each applicable field. Use `N/A`
with a reason when a field has no meaningful value. The `performance` label
identifies work requiring a comparative baseline/regression disposition, not
whether a tier C measurement is reproducible. Tier A/B issues do not produce
this record unless their scope is explicitly promoted. Coordination epics link
child evidence rather than repeating it.

| Field | Required content |
| --- | --- |
| Revision | Baseline commit, candidate commit, branch, and dirty-state disposition |
| Environment | OS, architecture, CPU, memory, storage type, SDK/runtime, Release configuration, container/native mode, and service versions |
| Workload | Workload ID, dimensions, formats, frame/object/job count, recipe, concurrency, total bytes, and fixture seed/version |
| Method | Harness/command, warm-up, repetitions, sampling interval, and counters/tools |
| I/O | Bytes and operations by filesystem, SQLite, SQL, object storage, and network; transaction, checkpoint, fsync, batching, and object-request behavior where relevant |
| CPU | Process and operation time, hot paths when useful, and algorithmic complexity |
| Memory | Allocated bytes, allocation rate, LOH where applicable, working set, peak temporary memory, retained buffers, and queue/window size |
| Latency | Median and p95, plus p99 or bounded worst case when tail behavior matters |
| Throughput | Frames, artifacts, jobs, queries, or bytes per unit time |
| Backlog | Count, bytes, oldest age, drain rate, and recovery duration |
| Correctness | Checksums, numerical tolerances, identities, lineage, state convergence, and failure behavior checked during the run |
| Result | Baseline, candidate, absolute/percentage change, noise or confidence interpretation, accepted regressions, and residual risk |

For five independent end-to-end trials, report median, minimum, and maximum; do
not infer p95 from five samples. Report p95 only from at least 30 independent
measured operations after warm-up, or from a benchmark harness with a documented
statistical method. Before measuring, declare the metric, sample unit, and
regression method. A result is material when it exceeds the predeclared
budget/tolerance or a documented confidence/noise interval; overlapping noisy
ranges do not prove improvement.

For a net-new path with no equivalent baseline, compare the nearest existing
path and the first simple correct implementation when feasible. Otherwise mark
the before value `N/A`, explain why, and record an absolute candidate baseline
for later non-regression.

Store transient raw results under `TestResults/` or another ignored output
directory. Put the reproducible command and reviewed summary in the PR. Commit a
stable evidence document only when the issue or external validation workflow
requires a durable baseline.

## 4. Measurement Boundaries

- Measure end-to-end cost and the changed stage separately when practical.
- Include waits introduced by bounded channels, locks, leases, storage, and
  backpressure; do not stop timing before the new work begins.
- Sample process CPU and working set for host or soak changes. Allocation-only
  evidence is insufficient for native buffers, SkiaSharp, SQLite, and object storage.
- Count full-frame copies and maximum simultaneously retained full-frame
  buffers for image paths.
- Use durable queue state for backlog metrics, never only in-memory channel
  depth.
- Record database commands, rows read, query plans/index use, object requests,
  and bytes where persistence design is under review.
- Separate fixture startup from steady state unless startup is the behavior
  under test.
- Keep process-wide allocation measurements out of parallel tests unless the
  harness isolates unrelated allocations.

## 5. Phase Gates

These are minimum evidence categories. The active issue may add a budget after
recording a baseline.

| Phase | Workloads | Required measurements | Performance acceptance |
| --- | --- | --- | --- |
| 0: boundaries and quality | Solution build/test plus publish output | Restore/build/test/coverage time, test counts by category, warnings, package and publish footprint | Required gates are reproducible; production output contains no test support; no unexplained build/test regression |
| 1: reconstructable contracts | `W1`, `W2` descriptors | Serialization size, validation/hash latency, allocations, operations/s, payload-copy count | Descriptor plus bytes round-trips; serialization/checksum does not copy payloads; added metadata cost is explained |
| 2: shared processing | `W1`, `W2` per recipe | CPU, allocations, temporary/retained memory, full-frame count, output bytes, frames/s | Local and central adapters match; each algorithm documents complexity and maximum live buffers |
| 3: durable ingress | `W2`, `W3M`, `W3P` recovery | Commit latency percentiles, payload/journal I/O, SQLite statements/transactions, WAL/checkpoint, lock wait, CPU, memory, recovery time | No acknowledged loss at fault boundaries; steady-state memory plateaus; ingress sustains the issue-manifest arrival rate and recovery margin |
| 4: durable lanes | `W2`, `W3M`, blocked lane | Per-lane claim/ack latency, throughput, retry, pending count/bytes/age, RSS | Fan-out adds references, not payload copies; blocked optional work stays within the issue-manifest acquisition/standard-lane tolerance |
| 5: local graph | `W1`, `W2`, representative DAG | Validation and per-step latency, CPU, I/O, output size, temporary and retained memory, restart recovery | Sustained cadence meets the issue budget; restart creates no duplicate logical output; full-resolution memory is bounded |
| 6: cadence/fleet | `W1`, `W2`, `W5` | Start jitter, readout/ingress gaps, metering bytes/CPU/allocation, heartbeat bytes/retry/latency, segment histograms | Disabled control does no scan; sparse metering does not scan a whole frame; optional work does not control next-start timing |
| 7: outbox | `W2`, `W3M`, `W3P`, outage/recovery | List/claim/ack I/O and latency, requests/bytes per capture, retry recovery, drain rate, backlog age | Indexed claims replace repeated directory scans; default upload is one raw payload; finite outage backlog drains faster than the issue-manifest arrival rate |
| 8: ingest/retrieval | `W1`, `W2`, `W4` | Upload/read latency, network and object-store requests/bytes, SQL commands, CPU, allocations/RSS, time to first byte | Memory is bounded by stream buffers and concurrency, not object size; exact bytes/ranges and checksums pass |
| 9: central worker | `W2`, `W3M`, `W4` | Claim latency/statements/collisions, object I/O, recipe cost, jobs/s, queue age, crash recovery | One logical output under retry/crash; queue recovery exceeds the issue-manifest arrival rate where required |
| 10: windows | 1K/10K/100K history, `N-2..N+2` | Resolution latency, SQL statements/rows/plans, pinned bytes, object reads, wait age | Selection cost follows window size, not history size; restart/out-of-order work remains bounded and idempotent |
| 11: weather/cloud | `W1`, `W2`, temporal fixtures | Render/assessment CPU, allocations, peak memory, mask/output bytes, frames/s | Edge/central outputs match; deterministic checksums/ranges pass; more complex mask storage requires measured benefit |
| 12: transients | `W1`, `W2`, scenario matrix | CPU/frame, allocations/RSS, source bytes, window state, candidate rate, latency, confusion matrix | `Off` allocates no detector state; edge lane does not materially regress acquisition; virtual sensitivity/cost is reported without physical claims |
| 12A: standalone CameraAgent | `W6`, ASI174 Mono8 ROI/bin conformance, schedule/environment/calibration/transient faults | Per-stage/end-to-end CPU, RSS/LOH, allocations, filesystem/SQLite I/O, latency, throughput, backlog/drain, storage growth, rendered outputs, central-attempt count | Complete local flow meets declared cadence/retention budgets, recovers without loss/duplicates, and makes zero central attempts |
| 13: UI | 1K/10K history, 1/10/50 clients | SQL statements/rows, API latency/bytes, encoding CPU, render latency, per-session memory | Paging is bounded; unchanged images are not repeatedly re-encoded when evidence justifies caching; no unbounded polling/query behavior |
| 14: E2E/readiness | Normal, outage, each fault, recovery | Stage and end-to-end latency, exact bytes/counts, per-host CPU/RSS/LOH, backlog age/drain, trace coverage, metric cardinality | Every fault converges; no acknowledged loss/duplicate logical output; recovery drains finite backlog; no unexplained regression |
| `RM-016`: local object-store replacement | `W1`, `W2`, `W3M`, `W3P`, `W4`; one non-seekable 100 MiB conformance upload; repeated overwrite/copy/delete and open-reader races | Put/stat/conditional-read/copy/delete/list operations and logical bytes; filesystem reads/writes/fsyncs/renames/unlinks; application/provider CPU and RSS; managed allocations; median/p95 latency; throughput; active streams; orphan, corrupt-state, and retired-generation backlog count/bytes/age/drain; capacity transitions; restart, backup, and restore time | Exact lengths/SHA-256 and strong visibility pass; memory follows stream buffers and concurrency rather than object size; listings have no gaps or duplicates; no partial generation is visible; retired generations are reclaimed without breaking open readers or unbounded growth; durability boundaries are evidenced; no unexplained material regression against the retained S3 path |
| `RM-019`: remote object-store profiles | Relevant `W1`, `W2`, `W3M`, `W3P`, `W4` set and the universal 100 MiB boundary, executed against each exact provider/topology | Universal object operations plus provider/network requests and bytes, retry/throttle behavior, time to first byte, WAN/LAN latency, egress where applicable, CPU/RSS/allocations, backlog/drain, outage recovery, backup/restore, capacity, and cost inputs | Provider-neutral correctness remains identical; retry and outage behavior is bounded; no unexplained material regression; each result certifies only the measured provider, region/network, and storage topology |

Phase 10 harness note (#553): the `CentralDerivativeWindowPerformanceTests`
Manual harnesses seed frames without going through ingest, so they now seed the
resolved capture-location provenance that reconstruction requires, and the P4
lease/retention trials give each of the five window sources its own object
because retention treats every non-expired artifact sharing a storage
reference as an active owner. Phase 10 and issue-116 evidence generated before
that change is not comparable for the P4 `RetentionReleased` field and must be
regenerated from a clean candidate head; all such evidence lives under the
ignored `TestResults/` tree, so no committed document changes.
Processing runner evidence (#428): `ProcessingRunnerPerformanceEvidenceTests`
(Manual, LogicHost integration project) records cold and warm start stages,
registration and claim latency, input/product transfer bytes, and W1
`encoded-preview` throughput in process versus through `processing-runner-v1`,
plus the runner-placed backlog observed with no runner. Output is
revision-scoped under `TestResults/processing-runner/<revision>/` and the
runtime signals are pinned in
`docs/validation/central-runner-runtime-signals.json`.

Fair scheduling evidence (#429): `ProcessingFairnessPerformanceEvidenceTests`
(Manual, LogicHost integration project) drains deterministic 5-, 10-, and
100-camera arrival streams under a per-observatory entitlement with concurrent
claim loops and records Jain's fairness index, claim and queue latency,
throughput, CPU, memory, and lease-loss recovery under
`TestResults/processing-fairness/<revision>/`; signals are pinned in
`docs/validation/central-fairness-runtime-signals.json`.

Execution-evidence export evidence (#537):
`ExecutionEvidenceExportPerformanceTests` (Manual, CameraAgent test project)
records the `W6` capture stage twice on separate roots — once with the export
lane absent and once with an export cycle running concurrently with each
capture against a sink that negotiates and then refuses every submission —
plus a drain stage that enlists a bounded backlog and drains it against the
conformance sink. It reports per-capture median/min/max latency, process CPU,
working set, allocated bytes, raw-ingress bytes, completed executions, export
backlog depth and durable bytes, enlistment throughput, submission
median/p95/maximum latency over at least thirty measured operations, drain
rate, remaining backlog, gap count, and the maximum concurrent export request.
Output is written to `TestResults/issue-537/w6-execution-evidence-export.json`
(override with `HVO_ISSUE537_EVIDENCE_ROOT`).

Reproduce with:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~ExecutionEvidenceExportPerformanceTests"
```

Orbital-elements acquisition evidence (#528):
`OrbitalElementsRefreshPerformanceTests` (Manual, CameraAgent test project)
generates a deterministic synthetic CelesTrak-GP-shaped payload at the #527
active-group size (16,754 records, 826 six-digit catalog numbers, 64 objects
with two element sets, two future epochs and 25 epochs older than seven days)
and serves it from an in-process handler, so no provider bytes are used and
nothing reaches the network. Every measured repeat uses a fresh root and the
real client, parser, normalizer, store and reader. It reports wall, process-CPU
and allocated-byte distributions for these stages: initialize, admit, fetch
(identity and gzip), parse, candidate, publish and activate, cold current and
exact materialization, warm current reads, an identical-body refresh (the
unchanged-payload path) and a changed refresh followed by retention. The
retention pass lowers the bound to one snapshot and zero minimum age so that it
retires one snapshot. The test also reports end-to-end first-activation latency,
process CPU, allocation, collections and working set, raw, wire, payload and
manifest sizes against the contract bounds, and the store's on-disk growth.

For the scheduler it measures:

- adapter validation and initial-epoch latency;
- the automation runner's sweep cost with and without the reserved orbital
  definition saved, while that definition is not yet due (zero provider
  requests).

A companion test moves the manifest and payload files out of the store after
one cold read and proves that 1,000 warm current and exact reads return the
cached instance without touching the disk.

There is no prior implementation, so the first recorded run is a baseline
rather than a comparison. On the recording host (12-core x64, ext4, Release),
per refresh:

| Stage | Median wall | Median allocation |
| --- | --- | --- |
| Candidate | about 2.3 s | about 5.1 GB |
| Publish and activate | about 1.2 s | about 2.55 GB |
| Cold materialization | about 0.95 s | about 2.1 GB |

Parse alone is about 29 ms and 5.8 MB, and fetch is about 6 ms. These costs sit
in the shared contract's per-record canonical-form and identity verification
(`OrbitalContract.ValidateRecord`, `CreateRecord` and `SerializeCanonical`
through a JSON DOM) rather than in the CameraAgent adapter. They are paid once
per activation, at most every two hours, and once per reader cache miss. Warm
reads perform no I/O.

The process peak working set across the measured loop was about 2.45 GB. That
is the main edge-device risk, and any reduction belongs to the Astronomy
contract implementation. Output is written to
`TestResults/issue-528/orbital-refresh-performance.json` (override the directory
with `HVO_ISSUE528_EVIDENCE_ROOT`). CPU, allocation and working-set figures are
process-scoped, so run the filtered invocation alone.

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~OrbitalElementsRefreshPerformanceTests"
```

### Orbital consumer and sweep interference (Refs #1425)

`OrbitalConsumerAndSweepInterferencePerformanceTests` extends the same in-process synthetic workload above. It
keeps all 39 phase-1 series and adds three job-occurrence series, seven real terminal-completion series and an
after-only preparation interval. The **first measurement** uses exactly the same harness (`bdc9da83`) for both
sources, one warmup and ten fresh-root measured repetitions. Production baseline is `939b46d6`; after production
is `65fd790e`. The phase-1
contended timings are excluded from acceptance. These are x64 integration measurements, without edge-hardware
or production qualification. Page cache is retained; restart means a new store/reader, not a new process.

On 2026-10-20, baseline ran 13:07:25–13:12:59 UTC and after ran 13:28:42–13:34:32 UTC on the same 12-CPU host.
Build servers were shut down before each run, entry load was below 2.0 and available memory exceeded 8 GiB.
Conditions were sampled every 20 seconds. Maximum sampled one-minute load was 1.83 baseline and 1.63 after;
minimum available memory was 11,415,960 / 11,275,004 KiB. Each run used three test processes and no Testcontainers.
Both runs are **quiet** under the issue protocol. Implementation and an eight-minute wait for other host work
separated the runs; that other work did not overlap either measurement.

Every latency cell below is **p50 / p95 in milliseconds** (nearest-rank p95 is the maximum with ten samples).

| Series | Baseline ms | After ms |
| --- | ---: | ---: |
| First activation read | 928.8911 / 1076.3635 | 0.0019 / 0.0078 |
| Changed activation read | 937.0607 / 1155.4517 | 0.0014 / 0.0124 |
| Unchanged-body reacquisition read | 934.5405 / 1280.8775 | 0.0014 / 0.0110 |
| Persisted-state startup read | 908.7179 / 952.4833 | 0.0031 / 0.0077 |
| Read after overlap publication | 900.9581 / 1015.9806 | 0.0018 / 0.0153 |
| Due full-body delay to next definition | 3320.4806 / 3689.1237 | 7.1587 / 24.3897 |
| Precision-wait delay to next definition | 3533.6141 / 3758.4985 | 9.4132 / 12.6353 |
| Run-now delay to next definition | 3452.8981 / 3715.7510 | 7.9492 / 9.6380 |
| Full-body occurrence completion | 3323.0792 / 3692.6884 | 4061.7996 / 4869.5317 |
| Precision-wait occurrence completion | 3537.4269 / 3762.2454 | 4054.5772 / 5802.5075 |
| Timeout occurrence completion | 19.1307 / 23.4515 | 17.0710 / 20.0055 |
| First job occurrence including handoff | 3526.1731 / 3669.2057 | 4002.5126 / 5204.4365 |

All shared-reader first reads after preparation allocate **32 B**, compared with about **2.10 GB** before.
The job-side preparation interval is measured directly between reader events 7722/7723, after activation and
retention and outside the following-definition delay: p50/p95 **857.24 / 1014.30 ms**, **2,098,377,616 /
2,165,454,400 B** allocated and **866.94 / 1018.24 ms** CPU. It has 40 observations across ten repetitions
(four activations each). Physical reads are 0/0 B; writes 32,768/32,768 B; read syscalls 446/464 and write
syscalls 8/8, including instrumentation. CPU, allocation and `/proc/self/io` are process-scoped. Preparation
transfers verified materialization to the worker; it does not remove that work. Completion timings include the
real committed journal outcome. The precision/deadline cases simulate 120/30 seconds on the injected clock, so
reported wall time excludes the simulated passage of time. The `.full-sweep` series also includes controller
work to advance those timers; `.delay-to-other` is the precise following-definition interval.

The chosen policy **always prepares**, even without a consumer since startup, using the existing pinned and
verified reader path rather than reusing an unproved candidate. This ensures the first future consumer is warm
after preparation and keeps the cache at two snapshots. The measured process peak rises from **2,175,938,560 B**
baseline to **2,596,007,936 B** after (about 19.3%). Candidate/prepared-copy overlap and their GC lifetime are
part of this cost. The measurement is a process lifetime high water, not isolated retained-cache memory; zero
stage increases do not mean zero memory use. This policy spends memory and job CPU to avoid the first-consumer
cold load; smaller devices still need their separate qualification.

Ten unavailable reads now make **one** initialization attempt, compared with ten before. After releasing the
root lock, recovery takes two observed reads and **one virtual second**, compared with one immediate baseline
read. The recovery-read distribution therefore has 20 after samples versus ten baseline samples. Subsequent
failure delays are covered deterministically at 1, 2, 4, 8, 16, 30 and 30 seconds.

A strict prepared-only Astronomy read remains a possible coordinated follow-up. The residual activation-to-ready
window includes retention and the measured preparation interval (p50/p95 857/1014 ms); neither it nor the load
lease has a hard overall deadline. A consumer in that window can still perform the cold load or wait for the
preparer. An independently constructed cold reader still pays materialization, and storage recovery observed
before the initializer prepares persisted state can also be cold. No Astronomy contract or unavailable enum
changed in this issue.

The external issue-1425 phase-2 evidence bundle contains `baseline-01/` and `after-01/`, each with the JSON below,
exact `command.txt`, host before/during/after logs, verdict and full metric distributions. `comparison.md` contains
every series with p50/p95 wall, allocation, CPU, peak and peak increase, physical I/O and syscall counts.

| Evidence JSON (`orbital-consumer-and-sweep-performance.json`) | SHA-256 |
| --- | --- |
| `p2/baseline-01/` (first measurement) | `2dabf7c7b872958c9031413b35c44971b20208c82c785baa75b6abb83b19f26b` |
| `p2/after-01/` (first measurement) | `ffb59c7c5dcfd952ad7395dc6c86d2773aa9e177651b93d5eda3acde406b7c3e` |

**Completion-tail correction evidence.** The first measurement did not explain the CPU outside preparation
requested by R0-F1. Six further fresh-process runs on 2026-10-20 retain the same workload, one warmup and ten
measured repetitions. Runs 02/03 are interleaved baseline/after with identical test-only harness `d4307776`;
04 is an additional interleaved pair with harness `0c7d8e6b`, adding fixed-work integer and SHA controls at
repetition boundaries. Baseline is `bdc9da83` plus the named harness overlay; all after production is byte-identical
to reviewed `547c6145`. Builds/tests use `-m:4`; build servers are shut down before each measurement. Entry
load is below 2, memory exceeds 8 GiB and no other `dotnet test` is running. Host samples are taken every 20 s.
All six pass the quiet rule (sampled load below 4). No provider or Docker is used. CPU-frequency files are
unavailable in this virtualized environment. Idle MSBuild nodes appeared briefly during baseline-04; its maximum
load remained 1.46 and no competing test was present.

| Evidence JSON directory | UTC start–end | Max load1 | SHA-256 |
| --- | --- | ---: | --- |
| `c1/baseline-02/` | 14:49:58–14:55:21 | 1.64 | `d426f5a8bc26ba029411a6c405685587ba0ac501a810c3c89ee0c028b7ec3327` |
| `c1/after-02/` | 14:55:21–15:02:32 | 1.46 | `09d86ecda11b4d741eb447e6b2063a4f5bb6773810f912e25473e39620ce3ddc` |
| `c1/baseline-03/` | 15:02:33–15:07:57 | 1.26 | `7b4d6602e575fa126853572703c47ee5e2a9af20f16a639c275d1e82ddac92cf` |
| `c1/after-03/` | 15:07:57–15:15:02 | 1.23 | `c343a7991f2bbb7b2df26f7229f2a5b19ef399087fdb5404790a93badb8ae5a9` |
| `c1/baseline-04/` | 15:20:31–15:26:57 | 1.46 | `922de8c0d6d5f42181e9ae411171512a8a726486e49c31432c76c2dea97b8400` |
| `c1/after-04/` | 15:26:58–15:32:53 | 1.88 | `55fb6e185dcaf05a7bce260b5af28eba0bb5585f2bfc25fb12728eea16230f2d` |

| Run | Changed job p50 / p95 ms | Run-now completion p50 / p95 ms | First consumer p50 / p95 ms | Process peak B |
| --- | ---: | ---: | ---: | ---: |
| baseline-02 | 3206.29 / 3440.60 | 3304.24 / 3618.68 | 855.47 / 948.90 | 2,532,016,128 |
| after-02 | 4990.69 / 6977.67 | 4922.04 / 6482.60 | 0.00 / 0.01 | 2,532,745,216 |
| baseline-03 | 3267.77 / 3531.97 | 3336.92 / 3572.31 | 871.51 / 982.90 | 2,201,436,160 |
| after-03 | 4974.29 / 7275.52 | 5065.41 / 7076.70 | 0.00 / 0.01 | 2,574,749,696 |
| baseline-04 | 3877.13 / 5618.64 | 3720.85 / 4579.26 | 975.10 / 1623.62 | 2,270,142,464 |
| after-04 | 4239.88 / 4669.52 | 4222.11 / 4302.99 | 0.00 / 0.01 | 2,413,654,016 |

The large tails recur in after-02/03, without confinement to the last three repetitions. They also occur in
baseline-04 (full-body completion max 6,282.98 ms), while after-04 run-now completion max is 4,302.99 ms.
Existing log events and a test provider wrapper locate most extra time in normalize/publish before preparation.
For changed-job means, pair 02 adds 2,158.45 ms: 1,100.54 ms preparation, 159.85 ms additional outside-preparation
GC pause, and 898.06 ms remaining execution time. Pair 03 adds 1,939.13 ms: 1,062.38, 142.47 and 734.28 ms
respectively. These are accounting differences, not a causal attribution of the remainder. Outside-preparation
allocation stays about 7.67 GB and collection counts remain close to baseline.

After-04 changed-job mean preparation is 911.88 ms and mean outside preparation is 3,349.10 ms, versus
3,319.74 ms in baseline-03. Its run-now mean preparation is 898.29 ms. Preparation still allocates about 2.10 GB
and adds about 212 G0 collections per activation. The 04 fixed-work controls record no collections or pauses,
with 0–168 B of process diagnostic overhead; their timing does not explain baseline-04’s orbital variation.
They do not measure large-heap allocation or physical-host memory bandwidth.

No collection is forced at repetition boundaries. Final full blocking collections find all 99 tracked disposed
readers collectible in every run; final process live-heap estimates are 301–395 MB. Heap/LOH/POH, committed
bytes and fragmentation fluctuate rather than growing every repetition, and baseline fragmentation can exceed
after fragmentation. Remaining process object types are not attributed. No production retention defect is
established. H3’s preparation cost is measured; H2’s heap-shape effects and H4’s host/runtime conditions remain
possible explanations of the additional variable CPU. Frequency and thermal attribution are unavailable.

**R0-F1 remains blocking:** the unexplained remainder is isolated but not fully attributed. This evidence does
not clear the performance acceptance gate. No production behavior is changed by the correction. The external
`c1/analysis.md`, `per-repetition.tsv`, `boundaries.tsv`, `cpu-probes.tsv`, `all-metrics.tsv` and runtime-thread
samples preserve the observations and limits. The owner needs CPU/allocation profiling and, where available,
physical-host telemetry before accepting a causal explanation or selecting a mitigation.

**Per-thread CPU and settle-control correction (c2).** Eight fresh-process runs on 2026-10-20 use
identical test-only harness `edf50864`, one warmup, ten repetitions, Release and `-m:4`. Baseline is
`bdc9da83` plus the byte-identical two-file overlay; after production source still matches reviewed
`547c6145`. Each run has its own measurement marker, build-server shutdown, entry load below 2, more
than 8 GiB available and no competing test; before/during/after samples are 20 seconds apart. All eight
are quiet on their first measurement attempt. An initial Razor shutdown failed before measurement,
then succeeded on retry. No profiler, package/tool installation, Docker or provider was used.

Changed-job snapshots read `/proc/self/task/<tid>/schedstat` and `comm` at job start/end, fetch return,
7741, 7749 and 7722/7723. Stat utime/stime with the process clock tick is the fallback; every actual
sample used schedstat. GC-background, finalizer, JIT and mutator CPU are separated, with the executing
thread's read CPU bracket charged to instrumentation. The 480 measured boundaries average
0.526 ms wall / 0.300 ms CPU per read
(maximum wall 6.718 ms). Sequential reads have that sampling skew;
small leading/trailing bookkeeping is outside the self-CPU bracket. Foreground GC remains on its mutator.

The default-off `HVO_ORBITAL_PERF_SETTLE=1` control (`s` rows) runs only before each repetition,
outside all timed windows: LOH CompactOnce, full blocking compacting collection, finalizer drain,
second collection, then less than 5 ms GC-background CPU over a 500 ms window, bounded at 10 seconds.
Each settled run records eleven successful controls, including warmup. This settles a repetition boundary;
the first activation/consumer still precedes the changed job inside the repetition.

| Run | UTC start–end (2026-10-20) | Verdict | Max load1 | JSON SHA-256 |
| --- | --- | --- | --- | --- |
| baseline-05 | 16:17:04–16:22:25 | quiet; exit 0 | 1.92 | `31c9501609e3231dc5c000536a972d675f359effbb4417cf557c90128c725d5c` |
| after-05 | 16:22:25–16:28:40 | quiet; exit 0 | 1.32 | `4296bcff67d7e0622f6581309ec900c30ab9a722850765e7689ad3d631b1844f` |
| baseline-05s | 16:28:40–16:34:28 | quiet; exit 0 | 1.3 | `53b08d62a63eba4b806231acf93b1a135ff3a7ce53038d1804a8b7f2d07f195a` |
| after-05s | 16:34:28–16:41:36 | quiet; exit 0 | 1.56 | `854ef0937bef777b88c69c5609769963fedc5b962c7f66e9198cc69cf37a615b` |
| after-06s | 16:41:36–16:47:53 | quiet; exit 0 | 1.24 | `43a7b212d9f0f70cd515e4efe2c19a5c6e36cab018991ce492c62b06f007627b` |
| baseline-06s | 16:47:53–16:54:14 | quiet; exit 0 | 1.25 | `3441a629a2d67a3ed7f27f2d809fd8bbd62a754a29d0cc1c1b80cc39de213700` |
| after-06 | 16:54:14–17:00:41 | quiet; exit 0 | 1.43 | `2ec3090f84eb81b9e92fe0ec13f8358114733e5c528c9a50fa7fe816d49561c1` |
| baseline-06 | 17:00:41–17:06:05 | quiet; exit 0 | 1.56 | `a92e73aa56ed81db04b2ed14a69bd9514b01ed637473e98ea7c74619cfca5fdf` |

Outside-preparation changed-job mean differences (after minus baseline), milliseconds:

| Pair | Outside wall Δ | Mutator CPU Δ | GC-background CPU Δ | Other CPU Δ | Instrumentation CPU Δ | Pause Δ | Wall residual | GC+pause / wall Δ |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 05 | 202.49 | 214.78 | -12.63 | -1.84 | 2.08 | 51.75 | -51.65 | 19.32% |
| 05s | 563.00 | 540.21 | 39.29 | -3.08 | -0.04 | 95.49 | -108.87 | 23.94% |
| 06s | -555.24 | -549.23 | -5.04 | -2.30 | -0.22 | -57.14 | 58.68 | 11.20% |
| 06 | 375.66 | 370.21 | 10.50 | -3.13 | 1.14 | 67.99 | -71.06 | 20.89% |

CPU classes overlap wall and pause. The residual is the requested arithmetic difference after subtracting
all listed CPU and pause deltas, not an independent causal wall-time partition. A negative value reflects
overlap and counter/sampling differences. Per-thread counters, phase/count/pause deltas, per-repetition
tables and process-vs-thread counter disagreement are preserved in external `c2/analysis.md`, `phases.tsv`,
`per-repetition.tsv`, `paired-deltas.tsv` and the eight JSONs.

**E3: inconclusive; R0-F1 remains blocking.** E1 fails: unsettled pair 05 GC-background CPU plus pause
accounts arithmetically for 19.32% of its +202.49 ms wall delta.
Settled pair 05s adds 563.0017 ms, compared with the settled baseline mean spread
of 560.2847 ms (the excess is 2.7171 ms; it is not rounded into a pass).
Pair 06s reverses to -555.24 ms wall / -549.23 ms mutator CPU.
E2 also fails: that is not a positive mutator regression beyond the 543.28 ms settled
baseline mutator spread. Pair 05s mutator delta is 540.21 ms, also within that band.
Unsettled mutator deltas (214.78/370.21 ms) do exceed their native
14.95 ms baseline band; that increase does not persist under both settled comparisons.
The first pair's excess is principally normalize/publish; retention is negligible. The c2 unsettled
baseline mean spread is 23.10 ms wall / 14.95 ms mutator CPU;
including c1 baselines 02/03/04 gives a 939.08 ms conventional wall-mean range.
These observed spreads are not confidence intervals. c1 has no phase-aligned historical mutator counters.

The 20 settled changed-job preparations average 1035.88 ms wall,
1048.89 ms process CPU and 2,103,975,788 B allocation.
Settled changed first-consumer mean is 1009.2852 ms baseline versus
0.0029 ms after. This quantifies preparation and consumer transfer;
it does not establish a steady production bound or attribute the historical tail. No production defect
or safe production remedy is established. Missing evidence is a causal split of mutator CPU (including
foreground GC/allocator/cache/memory effects), and an activation-boundary or genuinely spaced control
under representative production GC/heap conditions. Guest load and fixed CPU probes do not observe
physical-host frequency or memory bandwidth.

The periodic definition minimum is 7,200 s (`src/HVO.SkyMonitor.CameraAgent.Common/Orbital/OrbitalElementsAutomationAdapter.cs:21`, validation at `:73`). The host supplies the two-hour `MinimumProviderAttemptInterval` as `MinimumAttemptInterval` (`src/HVO.SkyMonitor.CameraAgent.Common/DependencyInjection/OrbitalElementsServiceCollectionExtensions.cs:79`; `src/HVO.SkyMonitor.CameraAgent.Common/Orbital/SqliteOrbitalElementsStore.cs:30`). The durable last-attempt floor is checked for every trigger before admission and committed before fetching (`SqliteOrbitalElementsStore.cs:164`, `:171`, `:187`, `:192`); smaller configured intervals are rejected at `:780`. Run-now maps to Manual at `OrbitalElementsAutomationAdapter.cs:106`, then uses the same job/admission; precision wait can wait up to 120 s and rechecks admission (`OrbitalElementsRefreshJob.cs:239`, `:245`, `:256`). These are two-hour attempt-start intervals, not a guarantee of two-hour activation-completion gaps or a quiet heap. Startup preparation also does not represent a newly admitted provider attempt.

All eight effective test hosts report `IsServerGC=false`, `LatencyMode=Interactive`, and 12 processors. Both AppContext switches `System.GC.Concurrent` and `System.GC.Server` are absent (TryGetSwitch=false, data=null); absent does not mean an explicit false configuration. The command-line-selected runtimeconfig is `HVO.SkyMonitor.CameraAgent.Tests.runtimeconfig.json`, with neither GC property; `.NET BGC` CPU is observed. The production CameraAgent uses the Web SDK (`src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj:1`); its generated Release runtimeconfig has `System.GC.Server=true`, concurrent GC left at the runtime default. The Dockerfile publishes and runs that host (`src/HVO.SkyMonitor.CameraAgent/Dockerfile:73`, `:132`, `:143`), with no GC override in the Dockerfile or supplied Compose environment. `deploy/split-host/compose.cameraagent.yml:5` supplies the production environment and `:30` defaults to one CPU. Thus these workstation-GC, 12-processor tests do not establish production server-GC behavior; effective production mode also depends on deployed CPU/resource limits. No live production process was inspected. The generated host config is preserved as `c2/production-cameraagent.runtimeconfig.json`; each run JSON embeds the effective test config and all adjacent configs.

**Within-process settle and kernel-counter correction (c3).**

All twelve fresh processes use identical test-only harness d69c280c, one excluded warmup, ten measured repetitions (five S and five U), Release, -m:4, and the unchanged synthetic workload. Alternate settling never runs in warmup. S runs immediately before M1.job.changed, after M1.job.first and its first consumer, outside all timed windows: LOH CompactOnce, full blocking compacting collection, finalizer drain, second collection, then a GC-thread-CPU window below 5 ms per 500 ms, bounded at 10 s. SETTLE_FIRST chooses the first condition. SETTLE=1 keeps c2's original before-whole-repetition behavior; default remains off.

Each run has its own MEASURING marker, removed on exit, with build-server shutdown, bounded 30 s entry polls (at most 3 h), entry load1 <2, MemAvailable >8 GiB and no competing dotnet test. Before/during/after host samples are 20 s apart; quiet requires every load1 <4. DOTNET_gcServer=1 is set only in g runs, whose JSON must prove IsServerGC=true. Workstation JSON must prove false. All use the same 12-processor test environment; this server arm checks mode sensitivity, not the one-CPU deployed resource envelope. No profiler, tool/package installation, Docker or provider/network request is used.

Outside preparation is defined as in c2: sum adjacent changed-job boundary deltas from job-start to job-end, subtracting only event-7722→event-7723 in after. Boundaries also cover fetch-return, 7741, 7749. Summing adjacent deltas retains observations before a transient thread exits. Source identity holds for executed source operations: the only after-only operation is preparation and its constant call/result plumbing; backoff lies beyond an initialized early return. The optional reader is constructed outside measurement; runner and worker are bypassed. This is not byte identity of JIT output or async layouts/lifetimes. See external code-identity.md for full call inventory, exact 547c6145 locations and hunks.

| Run | GC / first | UTC start–end | Verdict | Max load1 | JSON SHA-256 |
| --- | --- | --- | --- | --- | --- |
| baseline-w1 | workstation / U | 2026-10-20T17:45:35.104703+00:00–2026-10-20T17:51:32.618481+00:00 | quiet; exit 0 | 1.79 | `b5d7dc8071321de7fb7c4d8e9757d1c4457b26a477325a5197ad0bb4320854ab` |
| after-w1 | workstation / S | 2026-10-20T17:51:32.910742+00:00–2026-10-20T17:57:36.194067+00:00 | quiet; exit 0 | 1.27 | `69503153c580751b5f862926e3fb750d61933fd08c7a4c898262030add994edc` |
| after-w2 | workstation / U | 2026-10-20T18:03:14.323415+00:00–2026-10-20T18:08:53.048618+00:00 | quiet; exit 0 | 1.33 | `3463052493f0b8b04bbd342ba5ac6dc1964335e167315e837f081522bacef720` |
| baseline-w2 | workstation / S | 2026-10-20T18:08:53.312588+00:00–2026-10-20T18:14:02.561609+00:00 | quiet; exit 0 | 1.51 | `92540422dd2facc08d3ce726b618b0dce8d62f4b0ce62bd5f2bb52d13214c377` |
| baseline-w3 | workstation / U | 2026-10-20T18:14:02.841488+00:00–2026-10-20T18:19:10.448202+00:00 | quiet; exit 0 | 1.39 | `6c78b95914865240c93c1a5553799aff7ff7469dd29522db32ab47d0ff8100a2` |
| after-w3 | workstation / S | 2026-10-20T18:19:10.745619+00:00–2026-10-20T18:25:17.619928+00:00 | quiet; exit 0 | 1.2 | `de36106dde0c1f175387c852655f5ed8c1fe2618f2c725a7c8eadaafec440783` |
| after-w4 | workstation / U | 2026-10-20T18:25:17.878222+00:00–2026-10-20T18:31:03.781664+00:00 | quiet; exit 0 | 1.18 | `c98c60c6bf846353ca90ad35cd8b6e7c7dd8ee40c375d7f048c2404498b90886` |
| baseline-w4 | workstation / S | 2026-10-20T18:31:04.067268+00:00–2026-10-20T18:36:33.516878+00:00 | quiet; exit 0 | 1.66 | `6afbb566b07f4e479f8d37a99ba899416136d0732754becbaa0b22d3abfed0f3` |
| baseline-g1 | server / U | 2026-10-20T18:36:33.872149+00:00–2026-10-20T18:42:58.687080+00:00 | quiet; exit 0 | 3.26 | `67a0123054632ba32e869d84e50e1d020ba9ef996e9d13c25416781aee24e1bb` |
| after-g1 | server / S | 2026-10-20T18:42:59.024462+00:00–2026-10-20T18:50:54.079468+00:00 | quiet; exit 0 | 2.52 | `d9cf6e566d09f4bf602f4bcb25c835ec0c9b6e6de329d617bdc263b25f84ec8a` |
| after-g2 | server / U | 2026-10-20T18:50:54.343157+00:00–2026-10-20T18:57:28.246349+00:00 | quiet; exit 0 | 2.5 | `93f96a40c173e803ed7cf623f3701beed772896269f4c49dcea0041454a6a113` |
| baseline-g2 | server / S | 2026-10-20T18:57:28.518461+00:00–2026-10-20T19:03:21.416234+00:00 | quiet; exit 0 | 2.73 | `087a2e2a78f3893a7e2a80e753a498cc514ca55d1877a1d37d90c6c48ebfcd4a` |

Excluded contaminated attempts: after-w2 attempt 1, max load1 4.69, UTC 2026-10-20T17:57:36.469437+00:00–2026-10-20T18:03:14.067005+00:00, SHA-256 `931122294a03247360408d23dc830f88fbae8e8531747feaaaace4f2dc210b60`. Accepted retries retain their -retry directories; the twelve-run table lists only accepted runs.

Code-identity result: **PASS** for executed production source/work; full call inventory/hunks are in external `c3/code-identity.md`. Production remains byte-identical to reviewed `547c6145`.

**G1** under the pre-registered definitions.

| GC | ΔU ms | ΔS ms | DiD ms | BandS ms | BandU ms | B ms |
| --- | --- | --- | --- | --- | --- | --- |
| w | 17.00712674999977 | -25.413576999999805 | 42.4207037499998 | 609.1379670000001 | 241.414667 | 3307.2551715 |
| g | 167.96281199999976 | 221.99916750000057 | -54.0363554999999 | 91.071684 | 1185.735959 | 3551.6154109999998 |

- G1(i): PASS.
- G1(ii): PASS.
- G1(iii): PASS.
- G1(iv): PASS.

G1(ii): |ΔS| = 25.413576999999805; BandS = 609.1379670000001; 0.10 × B = 330.72551715000003 ms.
G1(iii): ΔU = 17.00712674999977; BandU = 241.414667; DiD = 42.4207037499998; 0.5 × ΔU = 8.503563374999885 ms.
G1(iv): server ΔS = 221.99916750000057; server BandS = 91.071684; 0.10 × B(server) = 355.1615411 ms.
G2 wall criterion = False; mutator ΔS = -33.259116749999976, BandS(cpu) = 606.3989889999998, 0.10 × B(cpu) = 327.40952172500005 ms. For the G2 CPU conjunct, B and BandS are applied analogously to per-process mutator medians. G2 = False.

Mechanism: None distinguishable as a causal carryover mechanism. The workstation unsettled after excess is only 17.00712675 ms versus BandU 241.414667 ms; settled after is 25.413577 ms faster. U after-minus-baseline counters are +25 ms mutator user, -2.5 ms mutator system, +5,091.75 minor faults, +12.81075 ms pause and +5.40714325 ms GC-thread CPU; S differences are -30 ms user, 0 ms system, +10,244.5 faults, -6.768 ms pause and -4.87487325 ms GC CPU. Settling increases system CPU/faults in both arms rather than removing a unique after-arm fault cost. The small differences do not isolate a heap/GC mechanism. Server settled after increases 221.9991675 ms (6.25065335% of B), above its 91.071684 ms band but below 355.1615411 ms (10% of B), so G1(iv) passes. That server difference is predominantly normalize/publish (+221.0986405 ms), tracks mutator user CPU (+225 ms overall; +220 ms in the phase), and has no increased minor-fault or system-CPU cost; retention adds only 0.5631645 ms. Mutator user CPU still includes foreground GC and does not identify application instructions versus allocator/cache/memory effects.

Allocation limitation: Server allocation cost is unavailable. All four server processes contain negative process-wide precise-allocation sample deltas (baseline-g1: 12; after-g1: 29; after-g2: 28; baseline-g2: 17). Twenty of 140 recorded after preparations are negative; positive jumps are also implausible. Raw preparation allocations range from -208,931,411,232 to +162,317,372,776 B. The raw signed server preparation mean is -6,626,729,609.542857 B, which is not an allocation cost. No samples are discarded to manufacture a positive mean. Non-precise cumulative heap counters remain monotonic but sometimes flat, so they cannot repair precise deltas. Raw JSONs and hashes are preserved, with allocation-counter-anomaly.json providing counts/ranges. Cause is not established; no production defect is inferred. Workstation preparation deltas are nonnegative and average 2,101,818,967.6285715 B over 280 recorded preparations. Wall, per-thread CPU, faults, pauses and every pre-registered G1/G2/G3 input are separate from allocation deltas; the outcome remains G1. Reliable server/combined allocation means require further evidence.

w: all 160 measured M1 preparations average 888.623394 ms wall, 900.255744 ms process CPU and 2,102,719,619.000 B allocation. The 40 changed-job preparations average 884.990413 ms phase wall, 895.263975 ms phase CPU and 2,102,325,619.200 B. Changed first-consumer mean is 902.792058 ms baseline versus 0.003295 ms after (saving 902.788763 ms). All 280 recorded M1/M3 preparations average 893.168229 ms wall, 905.917711 ms process CPU and 2,101,818,967.629 B.

g: all 80 measured M1 preparations average 1107.042791 ms wall, 1638.608712 ms process CPU and unavailable (invalid server counter) allocation. The 20 changed-job preparations average 1157.901413 ms phase wall, 1723.158850 ms phase CPU and unavailable (invalid server counter). Changed first-consumer mean is 1017.415555 ms baseline versus 0.005225 ms after (saving 1017.410330 ms). All 140 recorded M1/M3 preparations average 1106.588004 ms wall, 1625.638979 ms process CPU and unavailable (invalid server counter).

Across GC modes, all 420 recorded preparations average 964.308154 ms wall / 1145.824800 ms process CPU / unavailable (invalid server counter) allocation; the per-mode values above preserve the GC distinction.

CPU uses schedstat plus separate stat user/system ticks (AT_CLKTCK=100); process minflt/majflt, statm RSS and last-GC heap/committed/fragmented/promoted counters are captured at each boundary. User/system resolution is 10 ms. Foreground GC remains on mutators; process faults include all threads. The self CPU bracket is charged to instrumentation; CPU/pause overlap wall. Heap values describe the last completed collection. Full boundary overhead and additional-counter sub-brackets are recorded in external `c3/instrumentation.tsv`; all per-process medians, K values, arm means and phase/heap evidence are in `c3/analysis.md` and adjacent TSV/JSON. No test inventory changed.

Workload hashes: raw `9F75308EAFF68ED921A3CBBFCFFF771A67E4041E272A17E8E6FD868BF213718E`;
changed `6601B2C2227405C0273C9613059F0E88FD6E041C60640E2DFA33C6B567292FB9` (16,754 records, 7,147,269 raw bytes).

```bash
HVO_ISSUE1425_SOURCE_COMMIT="$SOURCE_COMMIT" HVO_ISSUE1425_EVIDENCE_ROOT="$EVIDENCE_ROOT" \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --no-restore --configuration Release \
  --filter "FullyQualifiedName~OrbitalConsumerAndSweepInterferencePerformanceTests" \
  --logger "trx;LogFileName=issue-1425.trx" --results-directory "$EVIDENCE_ROOT/test-results"
```

Live derived-window resolution evidence (#608):
`W6RollingWindowCaptureTimingTests` (Manual, CameraAgent test project) runs the
`W6` frame dimensions through the real raw ingress and standard lane with a
`Calibration` to `RollingCombination` graph and live durable executions, and
splits each capture into the raw-ingress acceptance critical section and the
lane. It reports minimum/median/maximum/mean for both and their sum, process CPU,
allocated bytes, working set, retained filesystem bytes, and the resulting stack
count, so moving derived-window resolution out of acceptance and into the
consuming node can be compared on an equivalent workload. Run it on the same
binary harness once per source, repeating for a spread. The harness is strictly
serial - one acceptance followed by one lane run - so it measures each phase in
isolation and cannot show contention between raw acceptance and the lane over
the shared SQLite writer; the single-agent production smoke covers that. Its
calibration step is pass-through, so the resolved stack count is five on both
sources and the harness compares equivalent work rather than reproducing the
short-window defect. Process CPU, allocation, and working-set figures are
process-scoped, so run the filtered invocation below rather than a wider
selection. Output is written to
`TestResults/issue-608/w6-rolling-window-capture-timing-<label>.json` (override
the directory with `HVO_ISSUE608_EVIDENCE_ROOT`).

```bash
HVO_ISSUE608_LABEL=after \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~W6RollingWindowCaptureTimingTests"
```

## 6. Output and Runtime Correlation

Performance evidence is valid only when the same run or fixture also checks the
relevant output and durable state:

- Payload length and SHA-256.
- Pixel layout, dimensions, byte order, media type, and deterministic image or
  assessment values.
- Capture, artifact, profile, recipe, parameter, and algorithm identity.
- Ordered source lineage and retention pins.
- Filesystem, SQLite, SQL, object-storage, lease, retry, quarantine, and completion state.
- Expected logs, bounded metric dimensions, connected traces, and health
  transitions without secret, payload, unsafe path, or personal-data leakage.

A run that is fast but produces the wrong bytes, loses lineage, accumulates
undrained work, or reports misleading telemetry is a failed result.

## 7. Coverage Baseline

The executable gate is `./scripts/coverage:enforce` with the reviewed baseline
and exact source-path risk mapping in `scripts/coverage/baseline.json`. The
aggregate baseline is 84.3690 percent line and 66.3253 percent branch with a
maximum 0.01 percentage-point regression. High-risk managed paths retain 95 percent
line/90 percent branch floors, and renderer/catalog paths retain 90/85 floors. The
native JPEG wrapper retains 95/80 because defensive null/failure branches inside
successful Skia factory and codec calls cannot be induced deterministically.

ReportGenerator is the single authoritative merger. CI collects 23 explicit
Unit, Integration, and architecture reports, creates one canonical Cobertura
report, and enforces and publishes that same result. Pull requests cannot remove
risk paths, lower thresholds, or widen tolerance relative to the target branch.
Approved generated/platform exclusions remain path-specific; ordinary coverage
regressions are corrected rather than accepted through a generic disposition.

## Elastic provider evidence (#430)

`ElasticProviderPerformanceEvidenceTests` (Manual) drives the `local-process`
adapter against a Kestrel-hosted LogicHost with the real runner binary and
records cold and warm start, drain throughput with 1, 2, and 4 instances,
scale-to-zero and cleanup timing, and host CPU/memory to
`TestResults/elastic-providers/<revision>/elastic-provider-evidence.json`.

## Space-weather refresh composition (Refs #1398)

Round-2 Manual measurements remain the before baseline. Round 4 runs each offline
Manual method once, separately, on the final production code, including the
publication lock. No live probe runs and no provisional ceiling changes.
Static regex calls thrashed the 15-entry process cache with 22 patterns per alert;
retaining the same NonBacktracking engines removes repeated construction. The
accepted source audit bounds the literal-derived cache to at most 38 patterns.

| Field | Recorded evidence |
| --- | --- |
| Revision | Baseline `f088b833a18503410d6a4df1131c3a69f1f66a33`; measured production/test head `c9fab1e9a82b3002e27ccd33a77d17a37385fe39`, branch `feature/1398-s4b-space-weather-refresh`, clean before measurement. This record is the following documentation commit. |
| Workload | Committed `swpc-v1` core captures, alerts padded to 1 MiB; zero-latency decoded-body stub and real SQLite. One warmup and five full-tick/restart/sweep samples; 720 compressed 120-second ticks. Retention seeds 35 daily ticks then advances another 35 days. Parser stress bodies have 2,000 accepted and 2,001 rejected records. Largest committed scenario body per core product is classified. Capture uses the real VirtualSky module, 1280×960 Mono16, fixed empty catalog, seed 2025, disabled sensor noise and equal-length baseline/concurrent series. |
| Environment | 12-vCPU x64 Linux VM, Intel i9-14900K reported, 14.98 GiB memory, ext4, native Release, SDK 10.0.402 and .NET 10.0.13. Load1 below 6 and shared measurement marker absent before every build/test; start/end loads retained externally. No network or Docker. |
| I/O | Zero network requests; SQLite WAL/FULL publication, reads, retention and restart. Physical database plus sidecar growth 1,744,896 B/day; footprint, not block-device write amplification. |
| CPU | Tick p95 process CPU 454.584 ms; compressed-day CPU 42,677.667 ms. Process counters during capture include concurrent refresh. |
| Memory | Tick p95 allocation 114.816 MiB, 113.733× decoded bytes; working-set delta 32.680 MiB. Generation counts and pauses are retained; working-set delta is not peak footprint. |
| Latency | Tick p95 5,537.415 → 369.593 ms; stages below. The unchanged harness uses nearest-rank ceil(0.95×n); with five samples p95 equals max. Singleton stages are one observation. These are descriptive comparisons, not population-tail qualification. |
| Throughput | 1098 provider attempts in 720 ticks, 14.768 ticks/s in compressed time; provider cadence and permission are unchanged. |
| Backlog | Each benchmark awaits one serialized job; no queued-backlog or full 50,000-attempt/10,000-receipt cap qualification. Production retains the bounded automation worker and acquires no capture/processing slot. |
| Correctness | All 125 core-product capture/scenario facts remain byte-identical (2,227,451 B; SHA-256 `131EC92D13A881293ED1DB7E9939783208D77D2FEAC917B49C0972CADBE53E7B`). All 128 ordered frame checksums match between capture series. Product admission bounds and restart hash assertions remain unchanged. |
| Result | tick p95: Over; tick allocation / decoded bytes: Over; tick working-set delta p95: Over; database growth / simulated day: Within. Capture p95 change -16.51%; sequential A/B, without a hardware or statistical qualification claim. No further parser/store/capture optimization or threshold change. Owner disposition of the provisional ceilings: pending. |

| Stage | Round-2 samples | Round-2 wall p95 ms | Round-4 samples | Round-4 wall p95 ms | Round-2 → round-4 allocated MiB p95 | CPU ms p95 before → after | WS MiB p95 before → after | GC 0/1/2 before → after |
| --- | ---: | ---: | ---: | ---: | --- | --- | --- | --- |
| fullTick1MiB | 5 | 5,537.415 | 5 | 369.593 | 3,353.752 → 114.816 | 6,504.016 → 454.584 | 12.582 → 32.680 | 1273/848/193 → 31/11/3 |
| sweepWithoutDefinition | 5 | 0.657 | 5 | 0.327 | 0.012 → 0.012 | 1.672 → 0.333 | 0.000 → 0.016 | 0/0/0 → 0/0/0 |
| sweepWithNotDueDefinition | 5 | 1.220 | 5 | 0.972 | 0.027 → 0.027 | 1.652 → 1.725 | 0.000 → 0.008 | 0/0/0 → 0/0/0 |
| restartRebuild | 5 | 13.367 | 5 | 12.318 | 5.088 → 5.088 | 16.273 → 20.896 | 0.016 → 2.395 | 2/0/0 → 1/0/0 |
| simulatedDay120SecondTicks | 1 | 3,012,438.126 | 1 | 48,753.465 | 2,355,943.291 → 29,600.351 | 3,233,207.523 → 42,677.667 | -21.270 → -108.676 | 224030/195456/69659 → 3322/1225/228 |
| retention35DayHistory | 1 | 4.642 | 1 | 6.321 | 0.044 → 0.045 | 2.449 → 3.799 | 0.004 → 0.004 | 0/0/0 → 0/0/0 |
| alerts2000Records | 1 | 45,350.071 | 1 | 20.520 | 40,406.981 → 18.724 | 48,417.567 → 20.554 | -14.543 → 0.000 | 3934/3694/1291 → 2/2/0 |
| alerts2001RecordsRejected | 1 | 0.776 | 1 | 0.498 | 0.000 → 0.000 | 0.772 → 0.504 | 0.000 → 0.008 | 0/0/0 → 0/0/0 |
| largestCommittedScenarioBodies | 1 | 106.707 | 1 | 1.046 | 90.961 → 0.273 | 114.969 → 1.057 | -3.488 → 0.074 | 9/6/3 → 0/0/0 |
| captureBaseline | 20 | 24.015 | 128 | 35.301 | 11.776 → 11.777 | 46.842 → 50.458 | 12.164 → 11.809 | 8/8/8 → 70/68/68 |
| captureWithMaximumRateRefresh | 20 | 38.620 | 128 | 29.472 | 30.375 → 29.267 | 70.418 → 68.026 | 12.891 → 18.848 | 39/35/19 → 107/60/31 |

| Provisional comparison | Round 2 | Round 4 | Ceiling | Round-4 result |
| --- | ---: | ---: | ---: | --- |
| tick p95 | 5,537.415 ms | 369.593 ms | 50 ms | Over |
| tick allocation / decoded bytes | 3,322.129 × | 113.733 × | 8 × | Over |
| tick working-set delta p95 | 12.582 MiB | 32.680 MiB | 32 MiB | Over |
| database growth / simulated day | 1.664 MiB/day | 1.664 MiB/day | 5 MiB/day | Within |

| Round-4 stage (samples) | Wall ms med/p95/max | CPU ms med/p95/max | Alloc MiB med/p95/max | WS delta MiB med/p95/max | GC 0/1/2 | GC pause ms |
| --- | --- | --- | --- | --- | --- | ---: |
| fullTick1MiB (5) | 343.884 / 369.593 / 369.593 | 371.320 / 454.584 / 454.584 | 114.772 / 114.816 / 114.816 | 22.871 / 32.680 / 32.680 | 31/11/3 | 48.083 |
| sweepWithoutDefinition (5) | 0.317 / 0.327 / 0.327 | 0.325 / 0.333 / 0.333 | 0.012 / 0.012 / 0.012 | 0.000 / 0.016 / 0.016 | 0/0/0 | 0.000 |
| sweepWithNotDueDefinition (5) | 0.617 / 0.972 / 0.972 | 0.764 / 1.725 / 1.725 | 0.027 / 0.027 / 0.027 | 0.000 / 0.008 / 0.008 | 0/0/0 | 0.000 |
| restartRebuild (5) | 10.485 / 12.318 / 12.318 | 12.409 / 20.896 / 20.896 | 5.088 / 5.088 / 5.088 | 2.199 / 2.395 / 2.395 | 1/0/0 | 1.080 |
| simulatedDay120SecondTicks (1) | 48753.465 / 48753.465 / 48753.465 | 42677.667 / 42677.667 / 42677.667 | 29600.351 / 29600.351 / 29600.351 | -108.676 / -108.676 / -108.676 | 3322/1225/228 | 5670.647 |
| retention35DayHistory (1) | 6.321 / 6.321 / 6.321 | 3.799 / 3.799 / 3.799 | 0.045 / 0.045 / 0.045 | 0.004 / 0.004 / 0.004 | 0/0/0 | 0.000 |
| alerts2000Records (1) | 20.520 / 20.520 / 20.520 | 20.554 / 20.554 / 20.554 | 18.724 / 18.724 / 18.724 | 0.000 / 0.000 / 0.000 | 2/2/0 | 3.593 |
| alerts2001RecordsRejected (1) | 0.498 / 0.498 / 0.498 | 0.504 / 0.504 / 0.504 | 0.000 / 0.000 / 0.000 | 0.008 / 0.008 / 0.008 | 0/0/0 | 0.000 |
| largestCommittedScenarioBodies (1) | 1.046 / 1.046 / 1.046 | 1.057 / 1.057 / 1.057 | 0.273 / 0.273 / 0.273 | 0.074 / 0.074 / 0.074 | 0/0/0 | 0.000 |
| captureBaseline (128) | 23.188 / 35.301 / 42.613 | 27.759 / 50.458 / 85.881 | 11.768 / 11.777 / 11.810 | 9.398 / 11.809 / 12.207 | 70/68/68 | 58.247 |
| captureWithMaximumRateRefresh (128) | 22.438 / 29.472 / 39.874 | 48.335 / 68.026 / 79.665 | 23.645 / 29.267 / 37.752 | 1.262 / 18.848 / 27.500 | 107/60/31 | 195.654 |

| Sized stage | Decoded bytes | Round-2 → round-4 allocation / decoded | Round-4 8× comparison |
| --- | ---: | ---: | --- |
| fullTick1MiB | 1,058,557 | 3322.129× → 113.733× | Over |
| restartRebuild | 1,058,557 | 5.040× → 5.040× | Within |
| alerts2000Records | 398,001 | 106456.492× → 49.331× | Over |
| alerts2001RecordsRejected | 398,200 | 0.001× → 0.001× | Within |
| largestCommittedScenarioBodies | 40,021 | 2383.233× → 7.155× | Within |

The 50 ms comparison applies to the full tick with the 1 MiB alerts input.
Parser maximum-admission stress allocations are reported factually; no further
optimization is authorized. Sweep/retention have no new decoded-input denominator,
and the compressed day does not record cumulative decoded bytes. Capture includes
pixel production, so its allocations are not divided by weather bytes.
Restart opens a new store in the same process with OS page cache retained.

Cold instrumented single sample (round 3, not the warm budget instrument): store
calls 545.012 ms / 155,343,792 B; completion/merge/purge 395.449 ms /
114,600,752 B. Store calls were 88.85% of time and 91.56% of allocations;
nested Processing scopes are included. Storage, SQL, schema and durable formats
remain unchanged. The scratch profiler is external and is not committed.

Capture overlap is proved on `Stopwatch.GetTimestamp()` for every measured frame
and refresh tick. The selected series has 128 frames per arm;
9 ticks lie completely within the first-frame-start to
last-frame-end window (3.008 s). Every counted tick
attempts all four configured products. Achieved workload is
43 provider attempts during the window,
2.992 complete ticks/s and 1,058,557 decoded bytes/tick.
Both arms double equally from 128 frames up to 2,048 if needed; fewer than five
complete ticks at the bound fails. `started` is signalled inside the first
production runner fetch, after durable admission. Checksums remain identical.

Capture p95 is 35.301 → 29.472 ms
(-16.51%). The round-2 short series was 24.015 → 38.620 ms (+60.81%),
with only one lifetime attempt and no qualified overlap proof. Frame counts differ
between rounds; compare each round's equal-length A/B arms, not absolute
inter-round capture latency as a controlled experiment.

| Capture attribution | Baseline | Concurrent |
| --- | ---: | ---: |
| Window wall ms | 3204.148 | 3008.214 |
| Process CPU ms | 4079.714 | 6391.953 |
| Average process CPU cores | 1.273 | 2.125 |
| GC pause ms | 58.247 | 195.654 |
| Monitor contention count | 0.000 | 0.000 |
| GC 0/1/2 | 70 / 68 / 68 | 107 / 60 / 31 |

CPU and managed GC are shared process resources; generation counts and measured
pause time quantify GC interference. Average CPU cores above one indicate
concurrent work; they do not prove saturation of the 12-vCPU host. Capture owns
its VirtualSky module, empty catalog and ProjectedSceneStore and never uses the
SWPC command/publication locks or SQLite connection. This source topology excludes
those particular resources as cross-path shared locks. Monitor contention is a
process-wide count, not a named-lock wait profile; these counters cannot apportion
every tail-latency increase. Any remaining capture regression is for owner
disposition; no capture code or existing threshold changes.

The external round-2 JSON artifacts retain `-r2` names; round-4 artifacts retain
`-r4` names and the canonical `space-weather-refresh-performance.json` and
`space-weather-capture-isolation.json` names under `HVO_ISSUE1398_EVIDENCE_ROOT`
(default `TestResults/issue-1398/`). Raw samples retain monotonic timestamps, GC
pauses/counts, CPU, allocations, working-set deltas, tick products and attempts.
The 1 MiB alerts SHA-256 is
`9F0008C04D9833E9E809235F2BC2203F79BA1948FA4317354E6CEFD143999EF8`.

Run separately after the focused gate and load/marker wait, with TMPDIR unset
and `DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build -c Release -m:4 --filter "TestCategory=Manual&FullyQualifiedName~SpaceWeatherRefreshPerformanceTests.FullTickRetentionRestartSimulatedDayAndRunnerSweepsAreMeasured"
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build -c Release -m:4 --filter "TestCategory=Manual&FullyQualifiedName~SpaceWeatherRefreshPerformanceTests.CapturePathWithAndWithoutConcurrentMaximumRateRefreshIsMeasured"
```
