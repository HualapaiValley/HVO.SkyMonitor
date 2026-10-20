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
