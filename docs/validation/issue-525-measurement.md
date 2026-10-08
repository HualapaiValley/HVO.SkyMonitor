# Issue 525 measurement contract

This describes the corrected W3-CAT-DSO harness. It is a prospective contract,
not a completed M1/AC8 result. F7 smoke and the original rc=1 checksum manifest
with its external superseding sidecar remain historical evidence. Issue434
Manual/W6 Docker, UI screenshots, deployment Actions identity and all other
explicit acceptance remain separate pending items.

Run only after a coordinator allocation, on the pinned SDK and a clean exact
head. `scripts/evidence:issue-525 smoke` runs A B. `measure` runs ABBA three
times in one quiet session, using a fresh test process per trial and one Release
binary. A is HYG 4.4; B is its approved HYG 4.4 + OpenNGC composition. The host,
lock holder, disk, Linux CPU clock ticks, output assembly inventory, vmstat,
MemAvailable/Cached, terminal GNU time maximum RSS, and package digests are
retained. No cache drop, service restart, build-server shutdown, or concurrent
copy is part of measurement.

## CPU and output contract

`hvo-catalog-performance-v2` / `hvo-issue525-measurement-v2` keeps operation
wall time, allocations and process CPU separate from result verification. The
operation CPU counter ends before checksum generation, dictionaries and
assertions. `verificationCpuMillisecondsPerOperation` records that work during
measured repetitions; first-operation and warm-up verification are outside both
reported CPU averages. These are process counters, so GC and other process
threads can contribute. Counter reads bracket wall/allocation instrumentation;
this overhead is not subtracted. Linux `CLK_TCK` is recorded: short operations
may produce zero or quantized CPU deltas. Such values do not imply zero work,
and ratios below counter resolution are not acceptance evidence.

`issue-525-cpu-proof.cs` calls the actual compiled private helper by reflection
in a fresh process for each old/corrected assembly. Thirty fixed-result
operations are run with cheap verification, then with a 40 ms verification-only
busy loop. The old helper must increase reported operation CPU by at least
20 ms/operation. The corrected helper must keep operation CPU within 5 ms of
its cheap-verification result and below 5 ms, while reporting at least 20 ms in
verification CPU. The fixed checksum must remain the same. These broad bounds
include Linux quantization and instrumentation, and a failed proof stops the
gate; it never changes the tolerance after observing results.

## Required queries and candidate baselines

The approved composed database SHA-256 is
`eea1181ffae1dca2935aeed2c7790305b28045061f38ca6dda5eaf19694a8f1f`.
`issue-525-query-pins.py` reads it in SQLite read-only mode and creates a new
pin file. The committed `issue-525-query-pins.json` hash is
`acb6f7600850307aac42a5b5fe021885f4b26c4507f1f4dfed6f333e806c30f6`.
Both the compiled harness and driver validator check that hash.
The identity/coordinate criteria derive from OpenNGC under CC BY-SA 4.0; the
[catalogue source and attribution](../catalog/hyg-v44-openngc.md) and repository
third-party notices retain that provenance.

| Family | Fixed workloads | Pinned output |
| --- | --- | --- |
| Spatial | 20-degree J2000 caps at NGC0224, Mel022 and NGC1976; bound 64 | Full match count and first ordinal 64 IDs, including extent/outline reach |
| Type | G, OCl and PN, separately; bound 64 | Full match count and returned IDs |
| Brightness | V <= 8 and B <= 10, each excluding and including unknowns; bound 64 | Band and unknown policy, match count and returned IDs |
| Alias | M31, M45, M102 | Alias/ID/kind rows; M102 retains disputed identity |
| Outline | Mel022 and NGC1976, all available levels | Level count, point count and exact IEEE-754 coordinate-bit checksum |

All 15 rows have nonempty expected results from approved data. Every first,
warm-up and measured result is checked against these pins. The original
50-point Fibonacci spatial sweep remains as additional coverage. The validator
rejects a missing family/row, altered criteria/checksum, vacuous result, missing
CPU/memory fields or an invented HYG DSO ratio. Its negative checks mutate copies
of actual newly produced evidence, preserving original files.

Each DSO operation also measures a full linear snapshot baseline in the same
candidate and checks equivalent pinned output. Spatial baselines scan all
precomputed directions/reaches; type/brightness baselines scan all objects;
alias baselines scan all 65,822 aliases; outline baselines scan all catalogue
outlines. Baseline setup is separate from recorded startup and operation timing.
Alias rows are read once from the approved database in read-only mode; retained
baseline arrays and their setup allocations/I/O are explicitly recorded. They
contribute to later process memory, so later memory is not compared as a paired
startup ratio. These baselines characterize the same candidate. They do not
fabricate an HYG-only operation or certify regression against a past DSO release.

Star all-sky/cap and star-scene checksums must agree across A/B. Default/stress
scene identities must agree within each arm across all six trials. The campaign
validator enforces both. Every stage retains latency p50/p95/maximum, throughput,
operation CPU, verification CPU, allocations and output checksums.

## Full-resolution resources

W6 is 3552 x 3552 RGB24: 37,850,112 bytes. Each composite borrows one immutable
base and returns one distinct owned output of that length. First and measured
helper results are explicitly cleared before another operation so the async
state machine does not intentionally retain previous full-frame outputs. The
base checksum is checked after all compositions. This is an in-process buffer
ownership statement; unreachable arrays awaiting GC can still increase RSS.

The semantic compositor owns one reusable RGBA tile of at most 1024 x 1024 x 4
= 4,194,304 bytes and no full-frame layer raster. Input + current output + tile
is 79,894,528 pixel-buffer bytes. This is a source-derived live pixel-buffer
inventory, not an observed total native-memory peak. Recorded native geometry,
font caches, managed objects, catalogue/baseline arrays, GC retention and runtime
memory are covered by observed process working-set/high-water evidence. No claim
of exact native geometry bytes is made without a native allocator trace.

Every stage records working set, process-lifetime peak working set and managed
live bytes before/after its measured loop (after warm-up). The peak is cumulative
and cannot be attributed exclusively to that stage. GNU time independently
records terminal maximum RSS for the trial process tree. The process high-water
limit is 2 GiB. Completed composites also record layer primitives, text characters
and geometry work, enforcing 4,096 primitives and 4,194,304 work units. Default
and maximum-count/one-pixel stress selections run at all three M31/M45/M42
instants. These bounds must pass before a measured campaign is spent.

## Proposed M1 predeclared interpretation

The allocation pre-start freezes these limits before any M1 result. They are
reviewable proposed criteria, and require the coordinator's measurement ruling:

| Metric | Proposed stop/review criterion |
| --- | --- |
| Correctness/identity | Any missing row, changed pinned identity, unequal comparable output, package change or failed TRX stops |
| Full-resolution memory | Process peak > 2 GiB, wrong ownership/length, or primitive/work limit violation stops |
| Common star work | > 10% paired median/p95 regression and > 1 ms absolute delta requires explanation; unresolved material regression blocks |
| Startup | Report paired latency/CPU/allocation/working-set changes and normalize I/O by authenticated database bytes; > 2x plus > 100 ms startup latency requires explanation |
| DSO query cost | Report every candidate/linear baseline with equal output; no HYG DSO ratio; > baseline p95 plus 1 ms requires explanation |
| Default W6 stages | Sum of stage p95 <= 5 s per declared logical exposure; do not interpret the five-second exposure as a sustained capture cadence |
| Stress W6 stages | Same hard correctness/memory/primitive/work limits; report latency without silently promoting stress to a production SLA |
| CPU reliability | Passing actual-helper contamination proof; counter-resolution-limited values explicitly labelled, no CPU ratio from zero/one-tick deltas |
| Cache/I/O | Fresh process proves empty application cache only. Zero/low read_bytes is warm page-cache evidence, never cold storage |

A campaign with warm startup remains a warm-page-cache startup result. The
current procedure cannot establish a controlled cold-storage comparison without
a separately approved isolation/cache protocol; it does not substitute fresh
processes for that proof. Compare physical `read_bytes`, logical `rchar` and read
calls separately. Record database/package lengths, retained catalog counts,
input/output digests, stage identities and the host/service versions. If either
host is co-resident with active work, or vmstat shows contention/steal, stop and
report the affected trial rather than rerunning within the same reservation.

M1 uses one session on an explicitly allocated quiet host with its co-resident
pair also reserved quiet, ABBA x 3, a one-hour wall cap, nice 19/idle I/O and the
required nonblocking `flock -n -o`. A later independent correction review carries
all F1–F8/N1–N3, the preserved r1 INCOMPLETE report, locally readable original F5
records, and every remaining acceptance item. The PR stays draft until convergence
and explicit acceptance; development/v1 CI is Preflight plus Build and Unit.
