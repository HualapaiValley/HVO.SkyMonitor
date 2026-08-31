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
five-operation warm-up, 30 measured operations, and concurrency. The phase
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
| `RM-016`: S3 replacement | `W1`, `W2`, `W3M`, `W3P`, `W4`; one non-seekable 100 MiB conformance upload | Put/stat/conditional-get/copy/delete/list requests and bytes, application/provider CPU and RSS, managed allocations, median/p95 latency, throughput, active streams, backlog count/bytes/age/drain, restart time, and restore time | Exact lengths/SHA-256 and strong visibility pass; memory follows stream buffers and concurrency rather than object size; listings have no gaps or duplicates; no unexplained material regression |

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

ReportGenerator is the single authoritative merger. CI collects 14 explicit
Unit, Integration, and architecture reports, creates one canonical Cobertura
report, and enforces and publishes that same result. Pull requests cannot remove
risk paths, lower thresholds, or widen tolerance relative to the target branch.
Approved generated/platform exclusions remain path-specific; ordinary coverage
regressions are corrected rather than accepted through a generic disposition.
